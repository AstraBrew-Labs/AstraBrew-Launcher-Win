//! 桌面模式 WebView 管理器
//!
//! 当启动模式为"桌面模式"时，酒馆启动成功后自动创建原生 WebView 窗口，
//! 以类似桌面应用的方式展示酒馆页面。
//!
//! 设计要点：
//! - macOS 要求 UI 必须在主线程创建。eframe 的 NSApp 已在主线程运行，
//!   所以直接用 objc2 创建 NSWindow + WKWebView，参与现有运行循环。
//! - 通过 `isVisible` 轮询检测窗口关闭（每帧在 egui update 中调用，主线程安全）。
//! - Drop 时自动关闭窗口。
//!
//! ## Delegate 实现
//! - **WKNavigationDelegate**：拦截外部链接在默认浏览器打开；检测不可显示的 MIME 类型触发下载
//! - **WKUIDelegate**：处理 `<input type="file">` 文件选择对话框
//! - **WKScriptMessageHandler**：接收 JS 发送的 blob 导出数据（`window.webkit.messageHandlers.fileDownloader`），
//!   解码 base64 后自动保存到配置的导出目录

use std::ptr;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::sync::LazyLock;

use block2::DynBlock;
use objc2::define_class;
use objc2::rc::Retained;
use objc2::runtime::{AnyObject, ProtocolObject};
use objc2::{AnyThread, MainThreadOnly};
use objc2_app_kit::{
    NSBackingStoreType, NSModalResponseOK, NSOpenPanel, NSWindow, NSWindowStyleMask, NSWorkspace,
};
use objc2_foundation::{
    MainThreadMarker, NSArray, NSData, NSDataBase64DecodingOptions, NSDictionary, NSObject,
    NSObjectProtocol, NSPoint, NSRect, NSSize, NSString, NSURL, NSURLRequest,
};
use objc2_uniform_type_identifiers::UTType;
use objc2_web_kit::{
    WKFrameInfo, WKNavigationAction, WKNavigationActionPolicy, WKNavigationDelegate,
    WKNavigationResponse, WKNavigationResponsePolicy, WKNavigationType, WKOpenPanelParameters,
    WKScriptMessage, WKScriptMessageHandler, WKUIDelegate, WKUserContentController, WKUserScript,
    WKUserScriptInjectionTime, WKWebView, WKWebViewConfiguration,
};

/// blob: URL 下载的目标目录，由 DesktopWebView::open 设置
static EXPORT_PATH: LazyLock<Mutex<String>> = LazyLock::new(|| {
    Mutex::new(
        std::env::var("HOME")
            .map(|h| format!("{}/Downloads", h))
            .unwrap_or_default(),
    )
});

/// 最近一次点击的 `<input type="file">` 的 accept 属性，由 JS 注入脚本通过
/// `fileInputTracker` messageHandler 同步发送，供 `run_open_panel` 设置 NSOpenPanel.allowedFileTypes。
///
/// 时序保证：JS click 事件 capture 阶段调用 postMessage → WebKit dispatch_async(主线程)
/// → WebKit 在 click 事件结束后 dispatch_async(主线程) 调用 runOpenPanel。
/// 两次 dispatch_async 按入队顺序执行，故 accept 先于 runOpenPanel 写入。
static LAST_FILE_ACCEPT: LazyLock<Mutex<String>> = LazyLock::new(|| Mutex::new(String::new()));

/// blob 下载结果通知队列，由 main.rs 每帧轮询并弹出 Toast
pub static DOWNLOAD_NOTIFICATIONS: LazyLock<Mutex<Vec<String>>> =
    LazyLock::new(|| Mutex::new(Vec::new()));

// ============================================================================
// WKNavigationDelegate — 外部链接 & 下载处理
// ============================================================================

define_class!(
    /// 自定义 NavigationDelegate：
    /// - 外部链接 / target="_blank" → 在默认浏览器中打开
    /// - 不可显示的 MIME 类型 → 在默认浏览器中下载
    #[unsafe(super(NSObject))]
    #[thread_kind = MainThreadOnly]
    struct WebViewNavDelegate;

    impl WebViewNavDelegate {
        /// 决策导航动作：区分内部/外部链接
        #[unsafe(method(webView:decidePolicyForNavigationAction:decisionHandler:))]
        fn decide_policy_for_navigation_action(
            &self,
            web_view: &WKWebView,
            navigation_action: &WKNavigationAction,
            decision_handler: &DynBlock<dyn Fn(WKNavigationActionPolicy)>,
        ) {
            unsafe {
                let nav_type = navigation_action.navigationType();
                let request = navigation_action.request();
                let target_frame = navigation_action.targetFrame();

                // 判断是否需要在默认浏览器打开
                let should_open_externally = if nav_type == WKNavigationType::LinkActivated {
                    // targetFrame 为 nil 表示 target="_blank" / 新窗口
                    if target_frame.is_none() {
                        true
                    } else {
                        // 比较当前页面 host 与目标 URL host，不同则视为外部链接
                        let request_url = request.URL();
                        match (web_view.URL(), &request_url) {
                            (Some(cur), Some(req)) => {
                                let cur_host = cur.host();
                                let req_host = req.host();
                                cur_host != req_host
                                    || cur_host.is_none()
                                    || req_host.is_none()
                            }
                            _ => false,
                        }
                    }
                } else {
                    false
                };

                if should_open_externally {
                    if let Some(url) = request.URL() {
                        let workspace = NSWorkspace::sharedWorkspace();
                        workspace.openURL(&url);
                    }
                    decision_handler.call((WKNavigationActionPolicy::Cancel,));
                } else {
                    decision_handler.call((WKNavigationActionPolicy::Allow,));
                }
            }
        }

        /// 决策导航响应：检测不可显示的 MIME 类型 → 触发下载
        #[unsafe(method(webView:decidePolicyForNavigationResponse:decisionHandler:))]
        fn decide_policy_for_navigation_response(
            &self,
            _web_view: &WKWebView,
            navigation_response: &WKNavigationResponse,
            decision_handler: &DynBlock<dyn Fn(WKNavigationResponsePolicy)>,
        ) {
            unsafe {
                if navigation_response.canShowMIMEType() {
                    decision_handler.call((WKNavigationResponsePolicy::Allow,));
                } else {
                    // WKWebView 无法显示此 MIME 类型 → 在默认浏览器中打开以下载
                    let response = navigation_response.response();
                    if let Some(url) = response.URL() {
                        let workspace = NSWorkspace::sharedWorkspace();
                        workspace.openURL(&url);
                    }
                    decision_handler.call((WKNavigationResponsePolicy::Cancel,));
                }
            }
        }
    }

    unsafe impl NSObjectProtocol for WebViewNavDelegate {}
    unsafe impl WKNavigationDelegate for WebViewNavDelegate {}
);

impl WebViewNavDelegate {
    fn new(mtm: MainThreadMarker) -> Retained<Self> {
        let this = Self::alloc(mtm);
        // SAFETY: alloc returns +1 retain count. For a delegate without
        // custom ivars, NSObject::init is a no-op. We skip calling it
        // to avoid an objc2 0.5/0.6 version conflict in msg_send!.
        // Both Allocated<T> and Retained<T> are #[repr(transparent)]
        // over a single pointer, so transmute is safe here.
        unsafe { core::mem::transmute::<objc2::rc::Allocated<Self>, Retained<Self>>(this) }
    }
}

// ============================================================================
// WKUIDelegate — 文件上传对话框
// ============================================================================

define_class!(
    /// 自定义 UIDelegate：处理 `<input type="file">` 文件选择
    #[unsafe(super(NSObject))]
    #[thread_kind = MainThreadOnly]
    struct WebViewUIDelegate;

    impl WebViewUIDelegate {
        /// 显示文件选择面板（文件导入）
        ///
        /// WKOpenPanelParameters 不暴露 HTML `<input accept>` 属性（WebKit API 限制），
        /// 因此通过 JS 注入脚本在 input 点击时通过 `fileInputTracker` messageHandler
        /// 预先把 accept 发送给原生层，这里读取并设置 NSOpenPanel.allowedFileTypes。
        #[unsafe(method(webView:runOpenPanelWithParameters:initiatedByFrame:completionHandler:))]
        fn run_open_panel(
            &self,
            _web_view: &WKWebView,
            parameters: &WKOpenPanelParameters,
            _frame: &WKFrameInfo,
            completion_handler: &DynBlock<dyn Fn(*mut NSArray<NSURL>)>,
        ) {
            unsafe {
                let mtm = MainThreadMarker::new()
                    .expect("UIDelegate::runOpenPanel must be on main thread");

                let panel = NSOpenPanel::openPanel(mtm);

                // 根据网页表单参数配置面板
                panel.setCanChooseFiles(true);
                panel.setAllowsMultipleSelection(parameters.allowsMultipleSelection());
                panel.setCanChooseDirectories(parameters.allowsDirectories());

                // 读取 JS 预先发送的 accept，设置文件类型过滤
                // 仅取扩展名形式（如 .json .png），MIME 类型 / 通配符交给 JS change 校验处理
                let accept = LAST_FILE_ACCEPT.lock().unwrap().clone();
                if !accept.is_empty() {
                    let uttypes: Vec<Retained<UTType>> = accept
                        .split(',')
                        .map(|s| s.trim())
                        .filter(|s| s.starts_with('.') && s.len() > 1)
                        .filter_map(|s| {
                            UTType::typeWithFilenameExtension(&NSString::from_str(&s[1..]))
                        })
                        .collect();
                    if !uttypes.is_empty() {
                        let ns_types: Retained<NSArray<UTType>> = uttypes.into_iter().collect();
                        panel.setAllowedContentTypes(&ns_types);
                    }
                }

                let result = panel.runModal();

                if result == NSModalResponseOK {
                    let urls = panel.URLs();
                    // 将所有权转移给 WebKit
                    completion_handler.call((Retained::into_raw(urls),));
                } else {
                    completion_handler.call((ptr::null_mut(),));
                }
            }
        }
    }

    unsafe impl NSObjectProtocol for WebViewUIDelegate {}
    unsafe impl WKUIDelegate for WebViewUIDelegate {}
);

impl WebViewUIDelegate {
    fn new(mtm: MainThreadMarker) -> Retained<Self> {
        let this = Self::alloc(mtm);
        // 同 WebViewNavDelegate::new 的理由
        unsafe { core::mem::transmute::<objc2::rc::Allocated<Self>, Retained<Self>>(this) }
    }
}

// ============================================================================
// WKScriptMessageHandler — blob 导出文件下载
// ============================================================================

define_class!(
    /// 接收 JS 通过 `webkit.messageHandlers.*.postMessage(...)` 发送的消息
    ///
    /// 当前注册两个 name：
    /// - `fileDownloader`：接收 {filename, base64} 字典，base64 解码后写入导出目录
    /// - `fileInputTracker`：接收 accept 字符串，记录到 `LAST_FILE_ACCEPT` 供 NSOpenPanel 过滤
    #[unsafe(super(NSObject))]
    #[thread_kind = MainThreadOnly]
    struct FileDownloadHandler;

    // 注意：WKScriptMessageHandler 的 userContentController:didReceiveScriptMessage: 是
    // required 方法，必须定义在 `unsafe impl WKScriptMessageHandler` 块内，否则 objc2
    // define_class! 宏在 debug 构建下会 panic（协议必需方法未在协议块中注册）。
    unsafe impl WKScriptMessageHandler for FileDownloadHandler {
        #[allow(non_snake_case)]
        #[unsafe(method(userContentController:didReceiveScriptMessage:))]
        fn userContentController_didReceiveScriptMessage(
            &self,
            _user_content_controller: &WKUserContentController,
            message: &WKScriptMessage,
        ) {
            unsafe {
                let name = message.name().to_string();
                match name.as_str() {
                    "fileDownloader" => {
                        handle_file_download(message);
                    }
                    "fileInputTracker" => {
                        handle_file_input_accept(message);
                    }
                    _ => {}
                }
            }
        }
    }

    unsafe impl NSObjectProtocol for FileDownloadHandler {}
);

/// 处理 blob 导出下载：JS postMessage({filename, base64}) → 写入文件
///
/// 注意：这是自由函数而非 FileDownloadHandler 的方法，因为 objc2 define_class! 的
/// `impl Type` 块内方法会被当作 ObjC 方法处理（需要 &self 参数）。
unsafe fn handle_file_download(message: &WKScriptMessage) {
    unsafe {
        let body = message.body();
        // JS postMessage({filename, base64}) → NSDictionary<NSString, NSString>
        let dict: &NSDictionary<NSString, NSString> =
            &*(&*body as *const AnyObject
                as *const NSDictionary<NSString, NSString>);

        let filename = dict
            .objectForKey(&NSString::from_str("filename"))
            .map(|s| s.to_string())
            .unwrap_or_else(|| "download".to_string());

        let b64_str = dict
            .objectForKey(&NSString::from_str("base64"))
            .map(|s| s.to_string())
            .unwrap_or_default();

        if b64_str.is_empty() {
            DOWNLOAD_NOTIFICATIONS
                .lock()
                .unwrap()
                .push("导出失败：数据为空".into());
            return;
        }

        // Base64 → NSData
        let b64_ns = NSString::from_str(&b64_str);
        let data = NSData::initWithBase64EncodedString_options(
            NSData::alloc(),
            &b64_ns,
            NSDataBase64DecodingOptions(0),
        );
        let data = match data {
            Some(d) => d,
            None => {
                DOWNLOAD_NOTIFICATIONS
                    .lock()
                    .unwrap()
                    .push("导出失败：文件数据损坏".into());
                return;
            }
        };

        // 保存到导出目录
        let downloads = EXPORT_PATH.lock().unwrap().clone();

        // 处理文件名冲突
        use std::path::Path;
        let mut save_path = format!("{}/{}", downloads, filename);
        if Path::new(&save_path).exists() {
            let stem = Path::new(&filename)
                .file_stem()
                .and_then(|s| s.to_str())
                .unwrap_or(&filename);
            let ext = Path::new(&filename)
                .extension()
                .and_then(|s| s.to_str())
                .unwrap_or("");
            let mut counter: u32 = 1;
            loop {
                let candidate = if ext.is_empty() {
                    format!("{}/{}_{}", downloads, stem, counter)
                } else {
                    format!("{}/{}_{}.{}", downloads, stem, counter, ext)
                };
                if !Path::new(&candidate).exists() {
                    save_path = candidate;
                    break;
                }
                counter += 1;
            }
        }

        let _ = std::fs::create_dir_all(&downloads);
        let path_ns = NSString::from_str(&save_path);
        if data.writeToFile_atomically(&path_ns, true) {
            let display_name = save_path
                .rsplit_once('/')
                .map(|(_, name)| name)
                .unwrap_or(&save_path);
            DOWNLOAD_NOTIFICATIONS
                .lock()
                .unwrap()
                .push(format!("已导出: {}", display_name));
        } else {
            DOWNLOAD_NOTIFICATIONS
                .lock()
                .unwrap()
                .push("导出失败：无法写入文件".into());
        }
    }
}

/// 处理 `<input type="file">` 的 accept 属性：JS postMessage(acceptString)
/// → 记录到 LAST_FILE_ACCEPT，供 run_open_panel 设置 NSOpenPanel.allowedFileTypes
unsafe fn handle_file_input_accept(message: &WKScriptMessage) {
    unsafe {
        let body = message.body();
        // JS postMessage(string) → NSString
        let ns_str: &NSString = &*(&*body as *const AnyObject as *const NSString);
        let accept = ns_str.to_string();
        *LAST_FILE_ACCEPT.lock().unwrap() = accept;
    }
}

impl FileDownloadHandler {
    fn new(mtm: MainThreadMarker) -> Retained<Self> {
        let this = Self::alloc(mtm);
        unsafe {
            core::mem::transmute::<objc2::rc::Allocated<Self>, Retained<Self>>(this)
        }
    }
}

// ============================================================================
// DesktopWebView
// ============================================================================

pub struct DesktopWebView {
    window: Retained<NSWindow>,
    /// 保持强引用，因为 WKWebView 对 delegate 是 weak 引用
    _nav_delegate: Retained<WebViewNavDelegate>,
    _ui_delegate: Retained<WebViewUIDelegate>,
    _download_handler: Retained<FileDownloadHandler>,
    running: Arc<AtomicBool>,
}

impl DesktopWebView {
    /// 更新导出文件保存目录。
    ///
    /// 设置页修改 `tavern_export_path` 后每帧调用此方法同步到 WebView，
    /// 这样无需重新打开 WebView 即可让新路径生效。
    pub fn set_export_path(path: &str) {
        *EXPORT_PATH.lock().unwrap() = path.to_string();
    }

    /// 在主线程上创建 NSWindow + WKWebView
    ///
    /// - `url`: 酒馆访问地址（如 http://127.0.0.1:8000）
    /// - `title`: 窗口标题（如 "SillyTavern - v1.12.0"）
    /// - `export_path`: 酒馆页面导出文件的保存目录
    ///
    /// 调用者必须确保在主线程上调用此方法。
    pub fn open(url: &str, title: &str, export_path: String) -> Result<Self, String> {
        // 更新 blob 下载目标目录
        Self::set_export_path(&export_path);

        let mtm =
            MainThreadMarker::new().ok_or("桌面模式 WebView 必须在主线程创建")?;

        // ---- 创建 NSWindow ----
        let style = NSWindowStyleMask::Titled
            | NSWindowStyleMask::Closable
            | NSWindowStyleMask::Miniaturizable
            | NSWindowStyleMask::Resizable;

        let rect = NSRect::new(NSPoint::new(0.0, 0.0), NSSize::new(1280.0, 720.0));
        let min_size = NSSize::new(800.0, 500.0);

        let window = unsafe {
            NSWindow::initWithContentRect_styleMask_backing_defer(
                NSWindow::alloc(mtm),
                rect,
                style,
                NSBackingStoreType::Buffered,
                false,
            )
        };
        window.setTitle(&NSString::from_str(title));
        window.setContentMinSize(min_size);
        window.center();

        // 关键：用户关闭窗口时不自动释放，由我们的 Retained 管理生命周期
        unsafe { window.setReleasedWhenClosed(false) };

        // ---- 创建 Delegate 对象（保持强引用） ----
        let nav_delegate = WebViewNavDelegate::new(mtm);
        let ui_delegate = WebViewUIDelegate::new(mtm);
        let download_handler = FileDownloadHandler::new(mtm);

        // ---- 创建 WKWebView ----
        let config = unsafe { WKWebViewConfiguration::new(mtm) };

        // 注册 JS → Native 通信桥梁
        // - fileDownloader：接收 blob 导出的 {filename, base64}
        // - fileInputTracker：接收 <input type="file"> 的 accept 属性，供 NSOpenPanel 过滤
        unsafe {
            let controller = config.userContentController();
            controller.addScriptMessageHandler_name(
                &ProtocolObject::from_ref(&*download_handler),
                &NSString::from_str("fileDownloader"),
            );
            controller.addScriptMessageHandler_name(
                &ProtocolObject::from_ref(&*download_handler),
                &NSString::from_str("fileInputTracker"),
            );
        }

        // 注入脚本：全面拦截文件下载行为，覆盖 FileSaver.js / 程序触发 a.click() / window.open 等
        //
        // 背景：原方案只拦截用户真实点击 <a href="blob:">，但预设/世界书等导出走 FileSaver.js
        // 等库，通常是程序触发 a.click() 或使用 data: URL，导致拦截失败。本脚本覆写以下入口：
        //   1. 用户真实点击 <a> (capture 阶段)
        //   2. HTMLAnchorElement.prototype.click (FileSaver.js 等库入口)
        //   3. window.open(blob:|data:) (部分库的备选路径)
        // 同时支持 blob: 和 data: 两种 URL scheme。
        let blob_patch_js = concat!(
            "(function(){",
            "function dl(u,f){",
            "fetch(u).then(function(r){return r.blob()}).then(function(b){",
            "var rd=new FileReader();",
            "rd.onloadend=function(){",
            "window.webkit.messageHandlers.fileDownloader.postMessage({",
            "filename:f||'download',",
            "base64:rd.result.split(',')[1]",
            "})};",
            "rd.readAsDataURL(b)",
            "}).catch(function(e){console.error('export err:',e)})",
            "}",
            "function isDl(u){return u&&(u.indexOf('blob:')===0||u.indexOf('data:')===0)}",
            "window.addEventListener('click',function(e){",
            "var a=e.target.closest&&e.target.closest('a');",
            "if(a&&a.href&&isDl(a.href)){",
            "e.preventDefault();e.stopPropagation();",
            "dl(a.href,a.download||'download')",
            "}",
            "},true);",
            "var oc=HTMLAnchorElement.prototype.click;",
            "HTMLAnchorElement.prototype.click=function(){",
            "if(this.href&&isDl(this.href)){",
            "dl(this.href,this.download||'download');return",
            "}",
            "return oc.apply(this,arguments)",
            "};",
            "var oo=window.open;",
            "window.open=function(u){",
            "if(u&&isDl(u)){dl(u,'download');return null}",
            "return oo.apply(window,arguments)",
            "}",
            "})()"
        );
        let user_script = unsafe {
            WKUserScript::initWithSource_injectionTime_forMainFrameOnly(
                WKUserScript::alloc(mtm),
                &NSString::from_str(blob_patch_js),
                WKUserScriptInjectionTime::AtDocumentStart,
                true,
            )
        };
        unsafe {
            let controller = config.userContentController();
            controller.addUserScript(&user_script);
        }

        // 注入脚本：恢复 `<input type="file" accept="...">` 的文件类型过滤
        //
        // 背景：自定义 WKUIDelegate::runOpenPanel 创建新的 NSOpenPanel 时，WebKit 不会
        // 自动应用 HTML accept 属性（WKOpenPanelParameters 不暴露该信息）。本脚本：
        //   1. capture 阶段监听 input click，识别导入类型，通过 fileInputTracker
        //      messageHandler 同步发送给原生层（WebKit dispatch_async 保证先于 runOpenPanel）
        //   2. change 事件校验作为后备：若 NSOpenPanel 过滤失效，在文件选中后再次校验，
        //      不匹配则清空 input.value 并提示
        //
        // 手动指定类型规则（不依赖酒馆 DOM 结构）：
        //   - 角色卡导入：accept 含 png / image → 强制 .png,.json
        //   - 世界书/预设导入：accept 含 json → 强制 .json
        //   - 其他：用原 accept
        let file_input_filter_js = concat!(
            "(function(){",
            // 类型识别：根据 input 的 accept 属性归类
            "function pickType(input){",
            "var acc=(input.getAttribute('accept')||'').toLowerCase();",
            // 角色卡：通常 accept="image/png,.png,application/json,.json" 或 .json
            // 但有的角色卡 import 按钮 accept 只写 .json，需结合上下文判断
            // 这里用 accept 内容做硬规则
            "if(acc.indexOf('png')>=0||acc.indexOf('image/')>=0){return '.png,.json'}",
            "if(acc.indexOf('json')>=0){return '.json'}",
            // 兜底：用原 accept
            "return acc",
            "}",
            // 1. 点击 input[type=file] 时，把识别出的类型发送给原生层
            "document.addEventListener('click',function(e){",
            "var t=e.target;",
            "if(!t||t.tagName!=='INPUT'||(t.type||'').toLowerCase()!=='file')return;",
            "var acc=pickType(t);",
            "try{window.webkit.messageHandlers.fileInputTracker.postMessage(acc)}catch(err){}",
            "},true);",
            // 2. change 事件校验（后备，与 pickType 规则保持一致）
            "document.addEventListener('change',function(e){",
            "var t=e.target;",
            "if(!t||t.tagName!=='INPUT'||(t.type||'').toLowerCase()!=='file')return;",
            "if(!t.files||!t.files.length)return;",
            "var acc=pickType(t);",
            "if(!acc)return;",
            "var exts=[],any=false;",
            "acc.split(',').forEach(function(p){",
            "p=p.trim().toLowerCase();",
            "if(!p)return;",
            "if(p.charAt(0)==='.'){exts.push(p.slice(1))}",
            "else if(p==='*/*'||p==='*'||p.indexOf('/*')>=0){any=true}",
            "});",
            "if(any)return;",
            "if(!exts.length)return;",
            "var bad=[];",
            "for(var i=0;i<t.files.length;i++){",
            "var f=t.files[i];",
            "var n=(f.name||'').toLowerCase();",
            "var ok=exts.some(function(x){return n.lastIndexOf('.'+x)===n.length-x.length-1});",
            "if(!ok){bad.push(f.name)}",
            "}",
            "if(bad.length){",
            "t.value='';",
            "alert('以下文件类型不被允许：\\n'+bad.join('\\n')+'\\n\\n允许的类型：'+acc)",
            "}",
            "},true)",
            "})()"
        );
        let file_filter_script = unsafe {
            WKUserScript::initWithSource_injectionTime_forMainFrameOnly(
                WKUserScript::alloc(mtm),
                &NSString::from_str(file_input_filter_js),
                WKUserScriptInjectionTime::AtDocumentStart,
                true,
            )
        };
        unsafe {
            let controller = config.userContentController();
            controller.addUserScript(&file_filter_script);
        }

        let webview = unsafe {
            WKWebView::initWithFrame_configuration(
                WKWebView::alloc(mtm),
                NSRect::new(NSPoint::new(0.0, 0.0), NSSize::new(1280.0, 720.0)),
                &config,
            )
        };

        // 设置 delegates（从 Retained 创建 ProtocolObject 引用）
        unsafe {
            webview.setNavigationDelegate(Some(ProtocolObject::from_ref(&*nav_delegate)));
            webview.setUIDelegate(Some(ProtocolObject::from_ref(&*ui_delegate)));
        }

        // 加载 URL
        let ns_url = NSString::from_str(url);
        if let Some(nsurl) = NSURL::URLWithString(&ns_url) {
            let request = NSURLRequest::requestWithURL(&nsurl);
            unsafe { webview.loadRequest(&request) };
        }

        // WebView 填入窗口
        window.setContentView(Some(&webview));

        window.makeKeyAndOrderFront(None);

        Ok(Self {
            window,
            _nav_delegate: nav_delegate,
            _ui_delegate: ui_delegate,
            _download_handler: download_handler,
            running: Arc::new(AtomicBool::new(true)),
        })
    }

    /// 主动关闭 WebView 窗口（仅当窗口仍可见时）
    pub fn close(&mut self) {
        self.running.store(false, Ordering::SeqCst);
        // 窗口已被用户关闭时不再重复 close，否则 segfault
        if self.window.isVisible() {
            self.window.close();
        }
    }

    /// 将 WebView 窗口唤回前台（避免重复打开新窗口）
    pub fn bring_to_front(&self) {
        self.window.makeKeyAndOrderFront(None);
    }

    /// 检查 WebView 窗口是否已被关闭（用户点击关闭按钮 或 程序主动关闭）
    ///
    /// 每帧在 egui update 中调用，主线程安全。
    /// 返回 `true` 表示窗口已关闭。
    pub fn is_closed(&self) -> bool {
        // 主动关闭时 running=false，用户关闭时 isVisible=false
        !self.running.load(Ordering::SeqCst) || !self.window.isVisible()
    }

    /// WebView 是否仍在运行
    #[allow(dead_code)]
    pub fn is_running(&self) -> bool {
        self.running.load(Ordering::SeqCst)
    }
}

impl Drop for DesktopWebView {
    fn drop(&mut self) {
        self.close();
    }
}
