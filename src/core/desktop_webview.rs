//! 桌面模式 WebView 管理器（Windows / WebView2）
//!
//! 当启动模式为"桌面模式"时，酒馆启动成功后自动创建原生 WebView 窗口，
//! 以类似桌面应用的方式展示酒馆页面。
//!
//! ## 架构
//! **不能用 winit 事件循环**：winit 用进程级全局静态量 `EVENT_LOOP_CREATED`
//! 限制事件循环在进程内只能创建一次，而 iced 启动时已经消费掉这个配额。
//! 在工作线程里第二次 `EventLoop::new()` 必定返回
//! `EventLoopError::RecreationAttempt`（文案 "EventLoop can't be recreated"）；
//! 该标志在 Windows 上没有任何复位路径（复位函数被 `#[cfg(web_platform)]` 门控，
//! 仅 WASM 可用），因此这不是"偶发第二次失败"，而是必然失败。
//!
//! 改为**原生 Win32 窗口 + 手写消息循环**：`wry` 在 Windows 上只要求
//! `HasWindowHandle`（不依赖 winit/tao），自建 HWND 完全可以承载 WebView2。
//!
//! ```text
//!   iced 主线程                        WebView 专属线程
//!   ─────────────                      ─────────────────
//!   DesktopWebView::open()  ──启动──►  CreateWindowExW + GetMessageW 消息循环
//!      │  ▲                                │
//!      │  │ 事件通道 (WebViewEvent)         │ 原生 HWND + wry WebView2
//!      │  └────Loading / Ready / Failed────┤
//!      │                                   │
//!      └──PostMessageW(WM_APP+1)──────────►│ Reload / BringToFront / Close
//!                                          │
//!   drain_events() 每帧拉取            窗口关闭 → PostQuitMessage → 线程结束
//! ```
//!
//! ## 关键设计
//! - **`PostMessageW` 唤醒**：`GetMessageW` 阻塞期间无法轮询 `mpsc`，
//!   因此主线程下发命令时向窗口投递 `WM_APP + 1`，由窗口过程处理。
//! - **延迟析构**：`WM_CLOSE` 只投递退出消息，WebView 必须在消息循环退出后、
//!   且在窗口过程调用链之外释放；否则 wry 在自己的 subclass 回调仍在执行时
//!   移除 subclass，Windows 会以 `0xc000041d` 终止进程。
//! - **`is_closed()` 轮询**：窗口关闭时置位 `closed`，iced 侧定时轮询即可感知。
//! - **脚本注入**：与旧版行为对齐——`blob_patch_js` 把 blob/大文件下载交给原生保存，
//!   `file_input_filter_js` 还原 `<input type="file">` 的 accept 过滤，
//!   两者都在文档开始时注入主框架。
//! - **下载**：使用 wry 的下载回调，保存目录由 `EXPORT_PATH` 决定；
//!   完成后把 `WebViewDownloadEvent` 推入全局队列，由启动器根界面呈现。
//! - **DPI 换算**：`CreateWindowExW` 收**物理**像素，而 wry 的
//!   `with_bounds` / `set_bounds` 收**逻辑**像素（内部再乘缩放比）。
//!   两者必须分别处理，否则非 100% 缩放下会出现二次放大、内容被裁切。
//! - **可缩放／可最大化**：承载酒馆页面的窗口按浏览器语义开放
//!   `WS_SIZEBOX | WS_MAXIMIZEBOX`，`WM_SIZE` 负责把新客户区同步给 WebView
//!   （`AGENTS.md` 的「不能最大化」约束的是启动器主界面，非本窗口）。

use crate::lang::t;
use crate::lang::tf;
use std::ffi::c_void;
use std::num::NonZeroIsize;
use std::path::{Path, PathBuf};
use std::sync::LazyLock;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, Receiver, Sender, TryRecvError};
use std::sync::{Arc, Mutex, OnceLock};
use std::time::Duration;

use raw_window_handle::{
    HandleError, HasWindowHandle, RawWindowHandle, Win32WindowHandle, WindowHandle,
};
use wry::dpi::{LogicalPosition, LogicalSize};
use wry::{PageLoadEvent, Rect, WebViewBuilder};
use windows_sys::Win32::Foundation::{HINSTANCE, HWND, LPARAM, LRESULT, WPARAM};
use windows_sys::Win32::Graphics::Gdi::HBRUSH;
use windows_sys::Win32::System::Com::{COINIT_APARTMENTTHREADED, CoInitializeEx, CoUninitialize};
use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
use windows_sys::Win32::UI::HiDpi::{GetDpiForSystem, GetDpiForWindow};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    CREATESTRUCTW, CS_HREDRAW, CS_VREDRAW, CW_USEDEFAULT, CreateWindowExW, DefWindowProcW,
    DestroyWindow, DispatchMessageW, GWLP_USERDATA, GetMessageW, GetWindowLongPtrW, IDC_ARROW,
    LoadCursorW, MSG, PostMessageW, PostQuitMessage, RegisterClassW, SW_SHOW, SetForegroundWindow,
    SetWindowLongPtrW, ShowWindow, TranslateMessage, WM_APP, WM_CLOSE, WM_DESTROY, WM_NCCREATE,
    WM_NCDESTROY, WM_SIZE, WNDCLASSW, WS_CAPTION, WS_CLIPCHILDREN, WS_CLIPSIBLINGS,
    WS_EX_APPWINDOW, WS_MAXIMIZEBOX, WS_MINIMIZEBOX, WS_OVERLAPPED, WS_SIZEBOX, WS_SYSMENU,
};

/// 桌面窗口初始尺寸：与主界面 16:9 默认尺寸保持一致。
const WINDOW_WIDTH: i32 = 1280;
const WINDOW_HEIGHT: i32 = 720;
/// WebView2 环境初始化的等待上限。
const STARTUP_TIMEOUT: Duration = Duration::from_secs(15);

/// 主线程 → 窗口线程的自定义唤醒消息。
const WM_DESKTOP_WEBVIEW_COMMAND: u32 = WM_APP + 1;

/// WebView 导出文件的规范保存目录。
static EXPORT_PATH: LazyLock<Mutex<PathBuf>> =
    LazyLock::new(|| Mutex::new(default_download_directory()));

/// WebView 下载结果，由启动器根界面显示全局消息。
#[derive(Debug, Clone)]
pub enum WebViewDownloadEvent {
    Saved(PathBuf),
    Failed(String),
}

static DOWNLOAD_NOTIFICATIONS: LazyLock<Mutex<Vec<WebViewDownloadEvent>>> =
    LazyLock::new(|| Mutex::new(Vec::new()));

/// 一次性取出所有下载结果，避免重复显示全局通知。
pub fn drain_download_notifications() -> Vec<WebViewDownloadEvent> {
    match DOWNLOAD_NOTIFICATIONS.lock() {
        Ok(mut guard) => std::mem::take(&mut *guard),
        Err(_) => Vec::new(),
    }
}

/// 原生 WebView 导航状态，由 iced 主线程定时消费。
#[derive(Debug, Clone)]
pub enum WebViewEvent {
    /// 导航已开始。
    Loading,
    /// 导航已成功完成，附带当前地址。
    Ready(String),
    /// 导航失败，附带已翻译的错误文案。
    Failed(String),
    /// WebView2 浏览器进程异常退出（崩溃）。
    ContentProcessTerminated,
}

/// 主线程 → WebView 线程的命令。
enum Command {
    /// 重新加载页面，可指定是否把 localhost 换成 IPv4 回环地址。
    Reload { use_loopback_fallback: bool },
    /// 把窗口唤回前台。
    BringToFront,
    /// 主动关闭窗口并结束事件循环。
    Close,
}

/// WebView 线程的启动结果。
///
/// 原生窗口句柄以 `isize` 传输：`HWND` 是裸指针，不实现 `Send`，
/// 但 Win32 约定允许跨线程投递消息，因此在这里做一次显式转换。
enum StartupOutcome {
    /// 窗口与 WebView 创建成功，附带原生窗口句柄地址。
    Ready(isize),
    /// 创建失败，附带已翻译的错误文案。
    Failed(String),
}

/// JS 侧投递导出数据使用的 IPC 频道名（`window.chrome.webview.postMessage` 的 `channel` 字段）。
const DOWNLOAD_CHANNEL: &str = "fileDownloader";

// ============================================================================
// 路径工具
// ============================================================================

/// 默认下载目录，实现见 [`crate::utils::user_downloads_dir`]。
fn default_download_directory() -> PathBuf {
    crate::utils::user_downloads_dir()
}

/// 当前用户的主目录。
fn user_profile_dir() -> PathBuf {
    std::env::var_os("USERPROFILE")
        .map(PathBuf::from)
        .filter(|path| path.is_absolute())
        .unwrap_or_else(|| PathBuf::from(r"C:\"))
}

/// 将设置中的导出目录解析为真实绝对路径。
///
/// 支持的写法：
/// - 空值或 `~` → 系统「下载」目录
/// - `~/xxx` 或 `~\xxx` → 用户主目录下的相对路径
/// - 绝对路径 → 原样使用
/// - 其他相对路径 → 相对用户主目录
fn resolve_download_directory(path: &str) -> PathBuf {
    let path = path.trim();
    if path.is_empty() || path == "~" {
        return default_download_directory();
    }
    if let Some(rest) = path
        .strip_prefix("~/")
        .or_else(|| path.strip_prefix("~\\"))
    {
        return user_profile_dir().join(rest);
    }
    let candidate = PathBuf::from(path);
    if candidate.is_absolute() {
        candidate
    } else {
        user_profile_dir().join(candidate)
    }
}

/// 在目标目录中为下载文件挑选一个不冲突的路径（`name.ext` → `name_1.ext`）。
fn available_download_path(directory: &Path, filename: &str) -> PathBuf {
    let requested = directory.join(filename);
    if !requested.exists() {
        return requested;
    }
    let path = Path::new(filename);
    let stem = path
        .file_stem()
        .and_then(|value| value.to_str())
        .unwrap_or("download");
    let extension = path.extension().and_then(|value| value.to_str());
    for counter in 1_u32.. {
        let candidate = match extension {
            Some(extension) if !extension.is_empty() => {
                directory.join(format!("{stem}_{counter}.{extension}"))
            }
            _ => directory.join(format!("{stem}_{counter}")),
        };
        if !candidate.exists() {
            return candidate;
        }
    }
    unreachable!("文件名递增查找必定能够找到可用路径")
}

/// 把下载结果推入全局队列，等待 iced 主线程消费。
fn push_download_event(event: WebViewDownloadEvent) {
    if let Ok(mut guard) = DOWNLOAD_NOTIFICATIONS.lock() {
        guard.push(event);
    }
}

// ============================================================================
// 注入脚本
// ============================================================================

/// 拦截 blob / 大体积 data URL 下载，改为把内容经 IPC 交给原生层保存。
///
/// 页面内触发 `<a download href="blob:...">` 或 `window.open(blobUrl)` 时，
/// WebView2 的默认行为可能是静默失败或另存为无意义文件名；本脚本统一改写为
/// `fileDownloader` 频道消息，由 Rust 侧写出到配置的导出目录。
fn blob_patch_js() -> String {
    // 超过该体积的 blob 不做 base64 传输，避免一次性占用过多内存。
    const MAX_BYTES: usize = 64 * 1024 * 1024;

    format!(
        r#"(function(){{
var MAX={max};
function isDl(u){{
if(!u){{return false}}
u=String(u);
if(u.indexOf('blob:')===0){{return true}}
if(u.indexOf('data:')===0&&u.length>2000){{return true}}
return false;
}}
function post(name,data){{
try{{window.chrome.webview.postMessage(JSON.stringify({{channel:'{channel}',name:name,data:data}}))}}catch(e){{}}
}}
function save(url,name){{
try{{
fetch(url).then(function(r){{return r.blob()}}).then(function(b){{
if(b.size>MAX){{throw new Error('too-large')}}
var fr=new FileReader();
fr.onload=function(){{
var s=String(fr.result);
var comma=s.indexOf(',');
post(name||'download',comma>=0?s.slice(comma+1):s);
}};
fr.readAsDataURL(b);
}}).catch(function(e){{post('__error__',String(e))}});
}}catch(e){{post('__error__',String(e))}}
}}
document.addEventListener('click',function(e){{
var t=e.target;
var a=(t&&t.closest)?t.closest('a[download]'):null;
if(!a){{return}}
var href=a.getAttribute('href')||'';
if(!isDl(href)){{return}}
e.preventDefault();
e.stopPropagation();
save(href,a.getAttribute('download')||'download');
}},true);
var oo=window.open;
window.open=function(u){{
if(u&&isDl(u)){{save(u,'download');return null}}
return oo.apply(window,arguments)
}};
}})()"#,
        max = MAX_BYTES,
        channel = DOWNLOAD_CHANNEL,
    )
}

/// 还原 `<input type="file" accept="...">` 的文件类型过滤。
///
/// 手动指定类型规则（不依赖酒馆 DOM 结构）：
///   - 角色卡导入：accept 含 png / image → 强制 `.png,.json`
///   - 世界书/预设导入：accept 含 json → 强制 `.json`
///   - 其他：沿用原 accept
fn file_input_filter_js() -> String {
    let rejected_prefix = crate::lang::t("webview.file_type_rejected_prefix");
    let allowed_prefix = crate::lang::t("webview.file_type_allowed_prefix");
    format!(
        r#"(function(){{
function pickType(input){{
var acc=(input.getAttribute('accept')||'').toLowerCase();
if(acc.indexOf('png')>=0||acc.indexOf('image/')>=0){{return '.png,.json'}}
if(acc.indexOf('json')>=0){{return '.json'}}
return acc
}}
document.addEventListener('change',function(e){{
var t=e.target;
if(!t||t.tagName!=='INPUT'||(t.type||'').toLowerCase()!=='file'){{return}}
if(!t.files||!t.files.length){{return}}
var acc=pickType(t);
if(!acc){{return}}
var exts=[],any=false;
acc.split(',').forEach(function(p){{
p=p.trim().toLowerCase();
if(!p){{return}}
if(p.charAt(0)==='.'){{exts.push(p.slice(1))}}
else if(p==='*/*'||p==='*'||p.indexOf('/*')>=0){{any=true}}
}});
if(any){{return}}
if(!exts.length){{return}}
var bad=[];
for(var i=0;i<t.files.length;i++){{
var f=t.files[i];
var n=(f.name||'').toLowerCase();
var ok=exts.some(function(x){{return n.lastIndexOf('.'+x)===n.length-x.length-1}});
if(!ok){{bad.push(f.name)}}
}}
if(bad.length){{
t.value='';
alert('{rejected}'+bad.join('\n')+'\n\n{allowed}'+acc);
}}
}},true)
}})()"#,
        rejected = escape_js_string(&rejected_prefix),
        allowed = escape_js_string(&allowed_prefix),
    )
}

/// 转义注入 JS 字符串字面量中的特殊字符。
fn escape_js_string(value: &str) -> String {
    let mut escaped = String::with_capacity(value.len());
    for character in value.chars() {
        match character {
            '\\' => escaped.push_str("\\\\"),
            '\'' => escaped.push_str("\\'"),
            '\n' => escaped.push_str("\\n"),
            '\r' => escaped.push_str("\\r"),
            _ => escaped.push(character),
        }
    }
    escaped
}

// ============================================================================
// DesktopWebView
// ============================================================================

/// 桌面模式的 WebView 窗口句柄。
///
/// 自身只持有跨线程句柄；真正的原生窗口与 WebView2 实例都存在于
/// [`DesktopWebView::open`] 启动的专属线程上。
pub struct DesktopWebView {
    /// 主线程 → WebView 线程的命令发送端。
    commands: Sender<Command>,
    /// WebView 线程 → 主线程的事件接收端。
    events: Receiver<WebViewEvent>,
    /// 原生窗口句柄，用于投递唤醒消息。
    hwnd: HWND,
    /// 窗口是否已关闭（用户关闭或程序主动关闭）。
    closed: Arc<AtomicBool>,
    /// 线程句柄，`close()` 时用于等待线程退出。
    thread: Option<std::thread::JoinHandle<()>>,
}

// 原生窗口句柄是裸指针，但按 Win32 约定可跨线程用于 PostMessageW 等操作。
unsafe impl Send for DesktopWebView {}
unsafe impl Sync for DesktopWebView {}

impl DesktopWebView {
    /// 更新导出文件保存目录。
    ///
    /// 设置页修改 `tavern_export_path` 后每帧调用此方法同步到 WebView，
    /// 这样无需重新打开 WebView 即可让新路径生效。
    pub fn set_export_path(path: &str) {
        if let Ok(mut guard) = EXPORT_PATH.lock() {
            *guard = resolve_download_directory(path);
        }
    }

    /// 创建桌面 WebView 窗口。
    ///
    /// - `url`：酒馆访问地址（如 `http://127.0.0.1:8000`）
    /// - `title`：窗口标题（用于启动阶段日志定位；wry 的窗口标题最终由页面
    ///   `<title>` 决定，此处仅作为初始标题）
    /// - `export_path`：酒馆页面导出文件的保存目录
    ///
    /// 本方法**同步等待**窗口创建结果：失败时立即返回 `Err`，
    /// 调用方可以据此显示错误并安排重试。
    pub fn open(url: &str, title: &str, export_path: String) -> Result<Self, String> {
        validate_webview_url(url)?;
        Self::set_export_path(&export_path);

        let (command_tx, command_rx) = mpsc::channel::<Command>();
        let (event_tx, event_rx) = mpsc::channel::<WebViewEvent>();
        let (startup_tx, startup_rx) = mpsc::channel::<StartupOutcome>();
        let closed = Arc::new(AtomicBool::new(false));

        let thread_closed = Arc::clone(&closed);
        let thread_url = url.to_owned();
        let thread_title = title.to_owned();

        let thread = std::thread::Builder::new()
            .name("astrabrew-webview".to_owned())
            .spawn(move || {
                run_webview_thread(
                    thread_url,
                    thread_title,
                    command_rx,
                    event_tx,
                    startup_tx,
                    thread_closed,
                );
            })
            .map_err(|error| {
                tf("webview.thread_spawn_failed", &[("error", &error.to_string())])
            })?;

        // 等待窗口创建结果：WebView2 环境初始化通常在一秒内完成。
        match startup_rx.recv_timeout(STARTUP_TIMEOUT) {
            Ok(StartupOutcome::Ready(hwnd)) => Ok(Self {
                commands: command_tx,
                events: event_rx,
                hwnd: hwnd as HWND,
                closed,
                thread: Some(thread),
            }),
            Ok(StartupOutcome::Failed(error)) => {
                let _ = thread.join();
                Err(error)
            }
            Err(_) => {
                // 超时：通知线程尽快退出，避免留下孤儿窗口。
                closed.store(true, Ordering::SeqCst);
                let _ = command_tx.send(Command::Close);
                let _ = thread.join();
                Err(t("webview.create_timeout").to_owned())
            }
        }
    }

    /// 拉取加载状态事件，不阻塞 iced 主线程。
    pub fn drain_events(&self) -> Vec<WebViewEvent> {
        let mut events = Vec::new();
        loop {
            match self.events.try_recv() {
                Ok(event) => events.push(event),
                Err(TryRecvError::Empty | TryRecvError::Disconnected) => break,
            }
        }
        events
    }

    /// 重新加载页面；需要时把 localhost 回退为 IPv4 回环地址。
    pub fn reload(&mut self, use_loopback_fallback: bool) -> Result<(), String> {
        if self.is_closed() {
            return Err(t("app.webview.window_missing").to_owned());
        }
        self.commands
            .send(Command::Reload {
                use_loopback_fallback,
            })
            .map_err(|_| t("app.webview.window_missing").to_owned())?;
        // 消息循环阻塞在 `GetMessageW`，必须投递消息把它唤醒去处理命令。
        post_command_message(self.hwnd);
        Ok(())
    }

    /// 主动关闭 WebView 窗口并回收线程。
    pub fn close(&mut self) {
        if self.closed.swap(true, Ordering::SeqCst) {
            return;
        }
        // 窗口线程可能已自行退出，发送失败无需处理。
        let _ = self.commands.send(Command::Close);
        post_command_message(self.hwnd);
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }

    /// 将 WebView 窗口唤回前台（避免重复打开新窗口）。
    pub fn bring_to_front(&self) {
        let _ = self.commands.send(Command::BringToFront);
        post_command_message(self.hwnd);
    }

    /// 检查 WebView 窗口是否已被关闭。
    ///
    /// 由 iced 定时消息轮询，跨线程安全。
    pub fn is_closed(&self) -> bool {
        self.closed.load(Ordering::SeqCst)
    }

    /// WebView 是否仍在运行。
    #[allow(dead_code)]
    pub fn is_running(&self) -> bool {
        !self.is_closed()
    }
}

impl Drop for DesktopWebView {
    fn drop(&mut self) {
        self.close();
    }
}

// ============================================================================
// WebView 专属线程
// ============================================================================

/// 注册「内容进程异常退出」监听。
///
/// 组件进程崩溃（如渲染进程被系统内存回收）时，wry 不会给出任何信号，
/// 页面会停留在白屏。这里挂上 WebView2 的 `ProcessFailed` 事件，
/// 一旦浏览器进程退出就把 `ContentProcessTerminated` 交给 iced 侧触发重试。
fn register_process_failed_handler(webview: &wry::WebView, events: Sender<WebViewEvent>) {
    use webview2_com::Microsoft::Web::WebView2::Win32::{
        COREWEBVIEW2_PROCESS_FAILED_KIND_BROWSER_PROCESS_EXITED, ICoreWebView2,
        ICoreWebView2ProcessFailedEventArgs,
    };
    use webview2_com::ProcessFailedEventHandler;
    use wry::WebViewExtWindows;

    let core = webview.webview();
    // 事件回调只关心“浏览器进程退出”这一种致命情况；其他失败类型
    // （渲染进程、GPU 进程等）WebView2 会自行恢复，无需打扰用户。
    let handler = ProcessFailedEventHandler::create(Box::new(
        move |_sender: Option<ICoreWebView2>,
              args: Option<ICoreWebView2ProcessFailedEventArgs>| {
            let Some(args) = args else {
                return Ok(());
            };
            let mut kind = COREWEBVIEW2_PROCESS_FAILED_KIND_BROWSER_PROCESS_EXITED;
            if unsafe { args.ProcessFailedKind(&mut kind) }.is_ok()
                && kind == COREWEBVIEW2_PROCESS_FAILED_KIND_BROWSER_PROCESS_EXITED
            {
                let _ = events.send(WebViewEvent::ContentProcessTerminated);
            }
            Ok(())
        },
    ));

    let mut token = 0i64;
    let _ = unsafe { core.add_ProcessFailed(&handler, &mut token) };
}

/// WebView 线程的主函数：创建原生窗口并运行消息循环，直到窗口关闭。
fn run_webview_thread(
    url: String,
    title: String,
    commands: Receiver<Command>,
    events: Sender<WebViewEvent>,
    startup: Sender<StartupOutcome>,
    closed: Arc<AtomicBool>,
) {
    let result = (|| -> Result<(), String> {
        // WebView2 是 COM 组件，必须先在本线程初始化 STA 环境。
        let _com_scope = ComScope::new()?;
        let class_atom = desktop_window_class()?;
        let module = unsafe { GetModuleHandleW(std::ptr::null()) };
        if module.is_null() {
            return Err(t("webview.module_handle_failed").to_owned());
        }

        // 导航地址需要在两处使用（初始加载 + `Reload`），因此先留一份副本。
        let initial_url = url.clone();

        // 窗口状态挂到 `GWLP_USERDATA`，由窗口过程取用。
        let state = Box::new(DesktopWindowState {
            url,
            commands,
            events: events.clone(),
            webview: None,
        });
        let create_params = Box::new(DesktopWindowCreateParams { state });
        let title_wide = to_wide(&title);

        // 窗口创建前只能拿系统 DPI；创建后必须用 `GetDpiForWindow` 复核，
        // 因为窗口可能被系统放到另一块不同缩放的显示器上。
        let dpi = system_dpi();
        let width = scale_for_dpi(WINDOW_WIDTH, dpi);
        let height = scale_for_dpi(WINDOW_HEIGHT, dpi);

        let hwnd = unsafe {
            CreateWindowExW(
                WS_EX_APPWINDOW,
                class_atom as usize as *const u16,
                title_wide.as_ptr(),
                window_style(),
                CW_USEDEFAULT,
                CW_USEDEFAULT,
                width,
                height,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                module,
                Box::into_raw(create_params) as *const c_void,
            )
        };
        if hwnd.is_null() {
            return Err(t("webview.window_create_failed_raw").to_owned());
        }

        // 客户区尺寸可能因非客户区（标题栏/边框）而与请求值不同。
        let (client_width, client_height) = client_size(hwnd, width, height);

        // 窗口已存在，改用真实的窗口 DPI（可能与 `system_dpi()` 不同）。
        let dpi = window_dpi(hwnd, dpi);

        // **必须传逻辑像素**：wry 内部会用 `GetDpiForWindow` 取到的缩放比
        // 再乘一次（见 wry 的 `set_bounds`：`bounds.size.to_physical(scale_factor)`）。
        // 若这里直接给物理像素，在 150% 缩放屏上会被二次放大 1.5 倍，
        // 导致 WebView 远大于窗口客户区 —— 表现为内容被裁掉、布局错乱。
        let bounds = logical_bounds(client_width, client_height, dpi);

        let load_event_tx = events.clone();
        let builder = WebViewBuilder::new()
            .with_url(initial_url)
            .with_bounds(bounds)
            .with_initialization_script_for_main_only(blob_patch_js(), true)
            .with_initialization_script_for_main_only(file_input_filter_js(), true)
            .with_clipboard(true)
            .with_devtools(cfg!(debug_assertions))
            .with_download_started_handler(|_url, destination| {
                // 用配置的导出目录覆盖 WebView2 的默认保存位置。
                let directory = EXPORT_PATH
                    .lock()
                    .map(|guard| guard.clone())
                    .unwrap_or_else(|_| default_download_directory());
                let filename = destination
                    .file_name()
                    .and_then(|value| value.to_str())
                    .unwrap_or("download");
                if std::fs::create_dir_all(&directory).is_err() {
                    return false;
                }
                *destination = available_download_path(&directory, filename);
                true
            })
            .with_download_completed_handler(move |_url, path, success| match (success, path) {
                (true, Some(path)) => push_download_event(WebViewDownloadEvent::Saved(path)),
                (true, None) => push_download_event(WebViewDownloadEvent::Failed(
                    t("webview.download.write_failed").to_owned(),
                )),
                (false, _) => push_download_event(WebViewDownloadEvent::Failed(
                    t("webview.download.failed").to_owned(),
                )),
            })
            .with_on_page_load_handler(move |event, url| match event {
                PageLoadEvent::Started => {
                    let _ = load_event_tx.send(WebViewEvent::Loading);
                }
                PageLoadEvent::Finished => {
                    let _ = load_event_tx.send(WebViewEvent::Ready(url));
                }
            });

        let native_window = NativeWindowHandle::new(hwnd)?;
        // WebView 构建失败时窗口已经存在，必须销毁，否则会留下一个空白且无法关闭的窗口。
        let webview = match builder.build_as_child(&native_window) {
            Ok(webview) => webview,
            Err(error) => {
                unsafe {
                    let _ = DestroyWindow(hwnd);
                }
                return Err(tf("webview.create_failed", &[("error", &error.to_string())]));
            }
        };

        // WebView2 内容进程崩溃时通知主线程重试。
        register_process_failed_handler(&webview, events.clone());

        // 状态必须在窗口已经建立之后再写回：窗口过程此时才持有有效指针。
        if let Some(state) = unsafe { desktop_window_state(hwnd) } {
            state.webview = Some(webview);
        }

        // 窗口创建后按真实 DPI 校正一次尺寸与位置，并保证不会落在已断开的显示器上。
        adapt_to_monitors(hwnd, width, height);

        unsafe {
            let _ = ShowWindow(hwnd, SW_SHOW);
            let _ = SetForegroundWindow(hwnd);
        }

        // 在进入消息循环前回报成功，避免主线程一直阻塞到窗口关闭。
        let _ = startup.send(StartupOutcome::Ready(hwnd as isize));

        // 手写消息循环：替代 winit，线程内独立运行，不受进程级配额限制。
        let mut message = MSG::default();
        loop {
            let has_message = unsafe { GetMessageW(&mut message, std::ptr::null_mut(), 0, 0) };
            if has_message == 0 || has_message == -1 {
                break;
            }
            unsafe {
                let _ = TranslateMessage(&message);
                DispatchMessageW(&message);
            }
        }

        // 必须在窗口过程的调用链之外释放 WebView：wry 在自己安装的 subclass 回调
        // 仍在执行时移除 subclass，Windows 会以 0xc000041d 终止进程。
        if let Some(state) = unsafe { desktop_window_state(hwnd) } {
            state.webview.take();
        }
        unsafe {
            let _ = DestroyWindow(hwnd);
        }
        Ok(())
    })();

    if let Err(error) = result {
        let _ = startup.send(StartupOutcome::Failed(error));
    }
    // 无论从哪条路径退出，都确保主线程能观察到"已关闭"。
    closed.store(true, Ordering::SeqCst);
}

/// 窗口线程内部状态，挂在 `GWLP_USERDATA` 上。
struct DesktopWindowState {
    /// 初始导航地址，`Reload` 时使用。
    url: String,
    /// 主线程下发的命令。
    commands: Receiver<Command>,
    /// 上报给主线程的事件。
    events: Sender<WebViewEvent>,
    /// WebView2 实例，消息循环退出后被取走。
    webview: Option<wry::WebView>,
}

impl DesktopWindowState {
    /// 处理主线程下发的命令；返回 `true` 表示需要退出消息循环。
    fn process_commands(&mut self, hwnd: HWND) -> bool {
        loop {
            match self.commands.try_recv() {
                Ok(Command::Reload {
                    use_loopback_fallback,
                }) => {
                    if let Some(webview) = &self.webview {
                        let target = if use_loopback_fallback {
                            loopback_fallback_url(&self.url)
                        } else {
                            self.url.clone()
                        };
                        if webview.load_url(&target).is_ok() {
                            let _ = self.events.send(WebViewEvent::Loading);
                        } else {
                            let _ = self.events.send(WebViewEvent::Failed(tf(
                                "webview.navigation_request_failed",
                                &[("url", &target)],
                            )));
                        }
                    }
                }
                Ok(Command::BringToFront) => unsafe {
                    let _ = ShowWindow(hwnd, SW_SHOW);
                    let _ = SetForegroundWindow(hwnd);
                },
                // WebView2 在父窗口上安装了 subclass，不能在窗口回调链中直接析构；
                // 先退出消息循环，随后在循环之外完成清理。
                Ok(Command::Close) => {
                    unsafe { PostQuitMessage(0) };
                    return true;
                }
                Err(TryRecvError::Empty) => return false,
                Err(TryRecvError::Disconnected) => return true,
            }
        }
    }
}

/// 创建窗口时经由 `lpCreateParams` 传入的参数包。
struct DesktopWindowCreateParams {
    state: Box<DesktopWindowState>,
}

/// 已注册的窗口类 Atom，进程内只注册一次。
static DESKTOP_WINDOW_CLASS: OnceLock<Result<u16, String>> = OnceLock::new();

/// 获取（必要时注册）桌面模式窗口类。
fn desktop_window_class() -> Result<u16, String> {
    DESKTOP_WINDOW_CLASS
        .get_or_init(register_desktop_window_class)
        .clone()
}

/// 注册桌面模式原生窗口类。
fn register_desktop_window_class() -> Result<u16, String> {
    let module = unsafe { GetModuleHandleW(std::ptr::null()) };
    if module.is_null() {
        return Err(t("webview.module_handle_failed").to_owned());
    }
    let class_name = to_wide("AstraBrewDesktopWebViewWindow");
    let window_class = WNDCLASSW {
        style: CS_HREDRAW | CS_VREDRAW,
        lpfnWndProc: Some(desktop_window_proc),
        hInstance: module,
        hCursor: unsafe { LoadCursorW(std::ptr::null_mut(), IDC_ARROW) },
        lpszClassName: class_name.as_ptr(),
        hbrBackground: std::ptr::null_mut::<c_void>() as HBRUSH,
        ..Default::default()
    };
    let atom = unsafe { RegisterClassW(&window_class) };
    if atom == 0 {
        Err(t("webview.register_class_failed").to_owned())
    } else {
        Ok(atom)
    }
}

/// 顶层窗口样式：可缩放、**可最大化**。
///
/// 注意：`AGENTS.md` 里「不能最大化」约束的是**启动器主界面**（`iced` 主窗口）；
/// 桌面模式的 WebView 窗口承载的是酒馆页面，属于独立的内容浏览窗口，
/// 用户需要像浏览器一样缩放/最大化，故此处显式开放 `WS_SIZEBOX | WS_MAXIMIZEBOX`
/// （`WM_SIZE` 会把新客户区尺寸同步给 WebView，见该分支的处理）。
fn window_style() -> u32 {
    WS_OVERLAPPED
        | WS_CAPTION
        | WS_SYSMENU
        | WS_MINIMIZEBOX
        | WS_MAXIMIZEBOX
        | WS_SIZEBOX
        | WS_CLIPCHILDREN
        | WS_CLIPSIBLINGS
}

/// 按 DPI 缩放逻辑像素。
fn scale_for_dpi(logical: i32, dpi: u32) -> i32 {
    ((logical as i64 * dpi as i64) / 96) as i32
}

/// 由物理像素尺寸构造 WebView 的**逻辑**边界。
///
/// wry 的 `set_bounds` / `with_bounds` 接受逻辑像素（DIP），内部会用
/// `to_physical(scale_factor)` 自行换算成物理像素交给 WebView2。
/// 因此这里必须把窗口客户区的物理尺寸**除以**缩放比还原成逻辑值，
/// 否则在非 100% 缩放的屏幕上会被二次放大，WebView 超出客户区。
fn logical_bounds(client_width: i32, client_height: i32, dpi: u32) -> Rect {
    let dpi = if dpi == 0 { 96 } else { dpi };
    let scale = dpi as f64 / 96.0;
    Rect {
        position: LogicalPosition::new(0.0, 0.0).into(),
        size: LogicalSize::new(client_width as f64 / scale, client_height as f64 / scale).into(),
    }
}

/// 当前系统 DPI（96 = 100% 缩放）。
///
/// 用 `GetDpiForSystem` 而不是 `GetDpiForWindow`：窗口尚未创建时没有窗口句柄，
/// 而传入空句柄属于非法调用。窗口建立后会由 `WM_SIZE` 用真实客户区尺寸修正。
fn system_dpi() -> u32 {
    let dpi = unsafe { GetDpiForSystem() };
    if dpi == 0 { 96 } else { dpi }
}

/// 窗口所在显示器的 DPI；失败时回退到 `fallback`。
///
/// 进程声明了 `PER_MONITOR_AWARE_V2`，因此该值就是窗口当前所在显示器的真实 DPI。
fn window_dpi(hwnd: HWND, fallback: u32) -> u32 {
    let dpi = unsafe { GetDpiForWindow(hwnd) };
    if dpi == 0 { fallback } else { dpi }
}

/// 把窗口摆放到主屏内，避免落在已断开的显示器上。
///
/// 规范要求「适配多屏切换；若界面在副屏而副屏断开，需自动切回主屏」。
/// 桌面窗口使用 `CW_USEDEFAULT` 创建，Windows 通常已放在有效显示器上；
/// 这里用虚拟桌面范围再校验一次，完全落在屏幕外时移回主屏（0, 0）。
fn adapt_to_monitors(hwnd: HWND, width: i32, height: i32) {
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        GetSystemMetrics, SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN, SM_XVIRTUALSCREEN,
        SM_YVIRTUALSCREEN, SWP_NOACTIVATE, SetWindowPos,
    };

    let virtual_left = unsafe { GetSystemMetrics(SM_XVIRTUALSCREEN) };
    let virtual_top = unsafe { GetSystemMetrics(SM_YVIRTUALSCREEN) };
    let virtual_width = unsafe { GetSystemMetrics(SM_CXVIRTUALSCREEN) };
    let virtual_height = unsafe { GetSystemMetrics(SM_CYVIRTUALSCREEN) };
    // 查询失败时（0）不做任何调整，交由系统默认布局。
    if virtual_width <= 0 || virtual_height <= 0 {
        return;
    }

    let mut rect = windows_sys::Win32::Foundation::RECT::default();
    let has_rect = unsafe {
        windows_sys::Win32::UI::WindowsAndMessaging::GetWindowRect(hwnd, &mut rect)
    };
    if has_rect == 0 {
        return;
    }

    let virtual_right = virtual_left + virtual_width;
    let virtual_bottom = virtual_top + virtual_height;
    // 只要窗口与虚拟桌面有交集，就认为用户能找到它。
    let intersects = rect.right > virtual_left
        && rect.left < virtual_right
        && rect.bottom > virtual_top
        && rect.top < virtual_bottom;
    if intersects {
        return;
    }

    // 完全落在屏幕外：移回主屏左上角并保持请求尺寸。
    unsafe {
        let _ = SetWindowPos(
            hwnd,
            std::ptr::null_mut(),
            0,
            0,
            width,
            height,
            SWP_NOACTIVATE,
        );
    }
}

/// 取窗口客户区尺寸；失败时回退到请求尺寸。
fn client_size(hwnd: HWND, fallback_width: i32, fallback_height: i32) -> (i32, i32) {
    let mut rect = windows_sys::Win32::Foundation::RECT::default();
    let ok = unsafe {
        windows_sys::Win32::UI::WindowsAndMessaging::GetClientRect(hwnd, &mut rect)
    };
    if ok != 0 && rect.right > rect.left && rect.bottom > rect.top {
        (rect.right - rect.left, rect.bottom - rect.top)
    } else {
        (fallback_width, fallback_height)
    }
}

/// 从 `GWLP_USERDATA` 取出窗口状态指针。
///
/// # Safety
/// 调用者必须保证 `hwnd` 由本模块创建，且已处理过 `WM_NCCREATE`。
unsafe fn desktop_window_state<'a>(hwnd: HWND) -> Option<&'a mut DesktopWindowState> {
    let pointer = unsafe { GetWindowLongPtrW(hwnd, GWLP_USERDATA) } as *mut DesktopWindowState;
    unsafe { pointer.as_mut() }
}

/// 桌面模式窗口过程。
extern "system" fn desktop_window_proc(
    hwnd: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    match message {
        WM_NCCREATE => {
            // 接管创建时传入的状态包，转存到窗口的 USERDATA 槽位。
            let create_struct = unsafe { &*(lparam as *const CREATESTRUCTW) };
            let params = unsafe {
                Box::from_raw(create_struct.lpCreateParams as *mut DesktopWindowCreateParams)
            };
            let DesktopWindowCreateParams { state } = *params;
            unsafe {
                SetWindowLongPtrW(hwnd, GWLP_USERDATA, Box::into_raw(state) as isize);
            }
            1
        }
        WM_DESKTOP_WEBVIEW_COMMAND => {
            let should_quit = unsafe { desktop_window_state(hwnd) }
                .is_some_and(|state| state.process_commands(hwnd));
            if should_quit {
                unsafe { PostQuitMessage(0) };
            }
            0
        }
        WM_SIZE => {
            // 窗口被缩放 / 最大化 / 还原，或发生 DPI 迁移时会触发，需要同步 WebView 边界。
            let width = (lparam & 0xffff) as i32;
            let height = ((lparam >> 16) & 0xffff) as i32;
            if width > 0
                && height > 0
                && let Some(state) = unsafe { desktop_window_state(hwnd) }
                && let Some(webview) = &state.webview
            {
                // `lparam` 是**物理**像素，必须还原成逻辑像素再交给 wry（同 `logical_bounds`）。
                let dpi = window_dpi(hwnd, system_dpi());
                let _ = webview.set_bounds(logical_bounds(width, height, dpi));
            }
            0
        }
        WM_CLOSE => {
            // 延迟到消息循环退出后再释放 WebView 并销毁窗口。
            unsafe { PostQuitMessage(0) };
            0
        }
        WM_DESTROY => {
            unsafe { PostQuitMessage(0) };
            0
        }
        WM_NCDESTROY => {
            let pointer = unsafe { SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0) };
            if pointer != 0 {
                drop(unsafe { Box::<DesktopWindowState>::from_raw(pointer as *mut _) });
            }
            unsafe { DefWindowProcW(hwnd, message, wparam, lparam) }
        }
        _ => unsafe { DefWindowProcW(hwnd, message, wparam, lparam) },
    }
}

/// Win32 原生窗口包装，供 wry 读取窗口句柄。
struct NativeWindowHandle {
    hwnd: HWND,
    hinstance: HINSTANCE,
}

impl NativeWindowHandle {
    /// 根据原生句柄创建包装对象。
    fn new(hwnd: HWND) -> Result<Self, String> {
        let hinstance = unsafe { GetModuleHandleW(std::ptr::null()) };
        if hinstance.is_null() {
            return Err(t("webview.module_handle_failed").to_owned());
        }
        Ok(Self { hwnd, hinstance })
    }
}

impl HasWindowHandle for NativeWindowHandle {
    fn window_handle(&self) -> Result<WindowHandle<'_>, HandleError> {
        let hwnd = NonZeroIsize::new(self.hwnd as isize).ok_or(HandleError::Unavailable)?;
        let mut handle = Win32WindowHandle::new(hwnd);
        handle.hinstance = NonZeroIsize::new(self.hinstance as isize);
        // SAFETY: 句柄在整个 WebView 生命周期内保持有效，且由本结构独占持有。
        unsafe { Ok(WindowHandle::borrow_raw(RawWindowHandle::Win32(handle))) }
    }
}

/// RAII 包装 COM 线程环境，线程退出时自动反初始化。
struct ComScope;

impl ComScope {
    /// 初始化当前线程的 COM 环境。
    fn new() -> Result<Self, String> {
        let result = unsafe { CoInitializeEx(std::ptr::null(), COINIT_APARTMENTTHREADED as u32) };
        // `RPC_E_CHANGED_MODE` 表示线程已用其他模式初始化过，可继续使用。
        if result < 0 && result != windows_sys::Win32::Foundation::RPC_E_CHANGED_MODE as i32 {
            return Err(tf(
                "webview.com_init_failed",
                &[("error", &format!("0x{result:08X}"))],
            ));
        }
        Ok(Self)
    }
}

impl Drop for ComScope {
    fn drop(&mut self) {
        unsafe { CoUninitialize() };
    }
}

/// 把 Rust 字符串转换成以 NUL 结尾的 UTF-16 缓冲区。
fn to_wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain(std::iter::once(0)).collect()
}

/// 向窗口线程投递 `WM_APP + 1`，唤醒阻塞中的消息循环处理命令。
fn post_command_message(hwnd: HWND) {
    if !hwnd.is_null() {
        unsafe {
            let _ = PostMessageW(hwnd, WM_DESKTOP_WEBVIEW_COMMAND, 0, 0);
        }
    }
}


// ============================================================================
// 工具函数
// ============================================================================

/// 校验 URL 是否可用作 WebView 导航目标。
fn validate_webview_url(url: &str) -> Result<(), String> {
    let url = url.trim();
    if !url.starts_with("http://") && !url.starts_with("https://") {
        return Err(tf("webview.url_scheme_invalid", &[("url", &url)]));
    }
    // 仅有协议头没有主机名的情况也视为非法。
    let rest = url
        .strip_prefix("https://")
        .or_else(|| url.strip_prefix("http://"))
        .unwrap_or_default();
    if rest.is_empty() || rest.starts_with('/') {
        return Err(tf("webview.url_invalid", &[("url", &url)]));
    }
    Ok(())
}

/// `localhost` → `127.0.0.1`：绕过部分环境下 IPv6 回环不可达的问题。
fn loopback_fallback_url(url: &str) -> String {
    url.replacen("://localhost", "://127.0.0.1", 1)
}

#[cfg(test)]
mod tests {
    use super::{
        DOWNLOAD_CHANNEL, available_download_path, default_download_directory, escape_js_string,
        loopback_fallback_url, resolve_download_directory, user_profile_dir, validate_webview_url,
    };
    use std::path::PathBuf;

    #[test]
    fn validates_http_urls_and_rejects_other_schemes() {
        assert!(validate_webview_url("http://localhost:8000/").is_ok());
        assert!(validate_webview_url("https://127.0.0.1:8000/").is_ok());
        assert!(validate_webview_url("file:///C:/index.html").is_err());
        assert!(validate_webview_url("").is_err());
        assert!(validate_webview_url("http://").is_err());
        assert!(validate_webview_url("http:///path").is_err());
    }

    #[test]
    fn default_download_setting_expands_to_user_downloads() {
        assert_eq!(
            resolve_download_directory("~/Downloads"),
            user_profile_dir().join("Downloads")
        );
        assert_eq!(resolve_download_directory(""), default_download_directory());
        assert_eq!(resolve_download_directory("~"), default_download_directory());
    }

    #[test]
    fn tilde_backslash_path_is_resolved_like_forward_slash() {
        assert_eq!(
            resolve_download_directory("~\\Exports"),
            resolve_download_directory("~/Exports")
        );
    }

    #[test]
    fn absolute_download_directory_is_used_as_is() {
        let absolute = r"C:\Exports";
        assert_eq!(resolve_download_directory(absolute), PathBuf::from(absolute));
    }

    #[test]
    fn localhost_retry_uses_ipv4_loopback() {
        assert_eq!(
            loopback_fallback_url("http://localhost:11451/"),
            "http://127.0.0.1:11451/"
        );
        assert_eq!(
            loopback_fallback_url("http://192.168.1.2:11451/"),
            "http://192.168.1.2:11451/"
        );
    }

    #[test]
    fn download_path_avoids_overwriting_existing_files() {
        let directory = std::env::temp_dir();
        let unique = format!("astrabrew-test-{}.txt", std::process::id());
        let first = available_download_path(&directory, &unique);
        assert_eq!(first, directory.join(&unique));

        std::fs::write(&first, b"x").expect("写入临时文件");
        let second = available_download_path(&directory, &unique);
        let stem = PathBuf::from(&unique)
            .file_stem()
            .and_then(|value| value.to_str())
            .expect("文件名含主干")
            .to_owned();
        assert_eq!(
            second.file_name().and_then(|value| value.to_str()),
            Some(format!("{stem}_1.txt").as_str())
        );
        let _ = std::fs::remove_file(&first);
    }

    #[test]
    fn js_string_escaping_handles_quotes_and_backslashes() {
        assert_eq!(escape_js_string(r"a'b\c"), r"a\'b\\c");
        assert_eq!(escape_js_string("line\nbreak"), "line\\nbreak");
    }

    #[test]
    fn injected_scripts_are_well_formed() {
        let blob = super::blob_patch_js();
        assert!(blob.contains(DOWNLOAD_CHANNEL));
        assert!(blob.starts_with("(function(){"));
        assert!(blob.trim_end().ends_with("})()"));

        let filter = super::file_input_filter_js();
        assert!(filter.starts_with("(function(){"));
        assert!(filter.contains("pickType"));
        assert!(filter.trim_end().ends_with("})()"));
    }
}
