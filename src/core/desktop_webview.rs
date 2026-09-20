//! 桌面模式 WebView 管理器（Windows / WebView2）
//!
//! 当启动模式为"桌面模式"时，酒馆启动成功后自动创建原生 WebView 窗口，
//! 以类似桌面应用的方式展示酒馆页面。
//!
//! ## 架构
//! iced 已经占用了主线程的 winit 事件循环，而 WebView2 的窗口必须在
//! **创建它的那个线程**上持续接收消息。因此本模块在独立线程上跑一个专属的
//! winit 事件循环，承载酒馆 WebView 窗口：
//!
//! ```text
//!   iced 主线程                        WebView 专属线程
//!   ─────────────                      ─────────────────
//!   DesktopWebView::open()  ──启动──►  EventLoop::run_app()
//!      │  ▲                                │
//!      │  │ 事件通道 (WebViewEvent)         │ 创建 winit 窗口 + wry WebView
//!      │  └────Loading / Ready / Failed────┤
//!      │                                   │
//!      └──命令通道 (Command)──────────────►│ Reload / BringToFront / Close
//!                                          │
//!   drain_events() 每帧拉取            窗口关闭 → 退出事件循环 → 线程结束
//! ```
//!
//! ## 关键设计
//! - **`is_closed()` 轮询**：winit 的 `CloseRequested` 会置位 `closed` 并退出事件循环，
//!   iced 侧定时轮询即可感知窗口关闭，无需把回调跨线程送回 iced。
//! - **脚本注入**：与旧版行为对齐——`blob_patch_js` 把 blob/大文件下载交给原生保存，
//!   `file_input_filter_js` 还原 `<input type="file">` 的 accept 过滤，
//!   两者都在文档开始时注入主框架。
//! - **下载**：使用 wry 的下载回调，保存目录由 `EXPORT_PATH` 决定；
//!   完成后把 `WebViewDownloadEvent` 推入全局队列，由启动器根界面呈现。

use crate::lang::t;
use crate::lang::tf;
use std::path::{Path, PathBuf};
use std::sync::LazyLock;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, Receiver, Sender, TryRecvError};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use wry::dpi::{LogicalPosition, LogicalSize};
use wry::{PageLoadEvent, Rect, WebViewBuilder};

/// 桌面窗口初始尺寸：与主界面 16:9 默认尺寸保持一致。
const WINDOW_WIDTH: f64 = 1280.0;
const WINDOW_HEIGHT: f64 = 720.0;
/// 桌面窗口最小尺寸：避免被拖成不可用的窄条。
const MIN_WINDOW_WIDTH: f64 = 800.0;
const MIN_WINDOW_HEIGHT: f64 = 500.0;
/// WebView2 环境初始化的等待上限。
const STARTUP_TIMEOUT: Duration = Duration::from_secs(15);
/// 命令轮询间隔：窗口关闭等低频事件无需高频检查。
const COMMAND_POLL_INTERVAL: Duration = Duration::from_millis(100);

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
enum StartupOutcome {
    /// 窗口与 WebView 创建成功。
    Ready,
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
/// 自身只持有跨线程句柄；真正的 winit 窗口与 WebView2 实例都存在于
/// [`DesktopWebView::open`] 启动的专属线程上。
pub struct DesktopWebView {
    /// 主线程 → WebView 线程的命令发送端。
    commands: Sender<Command>,
    /// WebView 线程 → 主线程的事件接收端。
    events: Receiver<WebViewEvent>,
    /// 窗口是否已关闭（用户关闭或程序主动关闭）。
    closed: Arc<AtomicBool>,
    /// 线程句柄，`close()` 时用于等待线程退出。
    thread: Option<std::thread::JoinHandle<()>>,
}

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
            Ok(StartupOutcome::Ready) => Ok(Self {
                commands: command_tx,
                events: event_rx,
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
            .map_err(|_| t("app.webview.window_missing").to_owned())
    }

    /// 主动关闭 WebView 窗口并回收线程。
    pub fn close(&mut self) {
        if self.closed.swap(true, Ordering::SeqCst) {
            return;
        }
        // 事件循环可能已自行退出，发送失败无需处理。
        let _ = self.commands.send(Command::Close);
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }

    /// 将 WebView 窗口唤回前台（避免重复打开新窗口）。
    pub fn bring_to_front(&self) {
        let _ = self.commands.send(Command::BringToFront);
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

/// WebView 线程的主函数：运行 winit 事件循环，直到窗口关闭。
fn run_webview_thread(
    url: String,
    title: String,
    commands: Receiver<Command>,
    events: Sender<WebViewEvent>,
    startup: Sender<StartupOutcome>,
    closed: Arc<AtomicBool>,
) {
    use winit::application::ApplicationHandler;
    use winit::event::WindowEvent;
    use winit::event_loop::{ActiveEventLoop, ControlFlow, EventLoop};
    use winit::window::{Window, WindowId};

    /// winit 事件循环的应用状态。
    struct App {
        /// 初始导航地址。
        url: String,
        /// 初始窗口标题。
        title: String,
        /// 主线程下发的命令。
        commands: Receiver<Command>,
        /// 上报给主线程的事件。
        events: Sender<WebViewEvent>,
        /// 启动结果发送端，仅在创建阶段存在。
        startup: Option<Sender<StartupOutcome>>,
        /// winit 窗口。
        window: Option<Window>,
        /// WebView2 实例。
        webview: Option<wry::WebView>,
        /// 全局关闭标志。
        closed: Arc<AtomicBool>,
    }

    impl App {
        /// 上报启动结果；只会生效一次。
        fn report_startup(&mut self, outcome: StartupOutcome) {
            if let Some(sender) = self.startup.take() {
                let _ = sender.send(outcome);
            }
        }

        /// 处理主线程命令；返回 `true` 表示需要退出事件循环。
        fn pump_commands(&mut self) -> bool {
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
                    Ok(Command::BringToFront) => {
                        if let Some(window) = &self.window {
                            window.set_visible(true);
                            window.focus_window();
                        }
                    }
                    Ok(Command::Close) => return true,
                    Err(TryRecvError::Empty) => return false,
                    Err(TryRecvError::Disconnected) => return true,
                }
            }
        }

        /// 退出事件循环并标记窗口已关闭。
        fn shutdown(&mut self, event_loop: &ActiveEventLoop) {
            self.webview = None;
            self.window = None;
            self.closed.store(true, Ordering::SeqCst);
            event_loop.exit();
        }

        /// 创建窗口失败时的统一收尾。
        fn fail(&mut self, event_loop: &ActiveEventLoop, error: String) {
            self.report_startup(StartupOutcome::Failed(error));
            self.closed.store(true, Ordering::SeqCst);
            event_loop.exit();
        }
    }

    impl ApplicationHandler for App {
        fn resumed(&mut self, event_loop: &ActiveEventLoop) {
            if self.window.is_some() {
                return;
            }

            let attributes = Window::default_attributes()
                .with_title(self.title.as_str())
                .with_inner_size(LogicalSize::new(WINDOW_WIDTH, WINDOW_HEIGHT))
                .with_min_inner_size(LogicalSize::new(MIN_WINDOW_WIDTH, MIN_WINDOW_HEIGHT));

            let window = match event_loop.create_window(attributes) {
                Ok(window) => window,
                Err(error) => {
                    self.fail(
                        event_loop,
                        tf(
                            "webview.window_create_failed",
                            &[("error", &error.to_string())],
                        ),
                    );
                    return;
                }
            };

            // WebView 铺满整个客户区。
            let size = window
                .inner_size()
                .to_logical::<f64>(window.scale_factor());
            let bounds = Rect {
                position: LogicalPosition::new(0.0, 0.0).into(),
                size: LogicalSize::new(size.width, size.height).into(),
            };

            let load_event_tx = self.events.clone();

            let builder = WebViewBuilder::new()
                .with_url(self.url.as_str())
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

            let webview = match builder.build_as_child(&window) {
                Ok(webview) => webview,
                Err(error) => {
                    self.fail(
                        event_loop,
                        tf("webview.create_failed", &[("error", &error.to_string())]),
                    );
                    return;
                }
            };

            // WebView2 内容进程崩溃时通知主线程重试。
            register_process_failed_handler(&webview, self.events.clone());

            window.set_visible(true);
            window.focus_window();

            self.window = Some(window);
            self.webview = Some(webview);
            self.report_startup(StartupOutcome::Ready);
        }

        fn window_event(
            &mut self,
            event_loop: &ActiveEventLoop,
            _window_id: WindowId,
            event: WindowEvent,
        ) {
            match event {
                WindowEvent::Resized(size) => {
                    if let (Some(window), Some(webview)) = (&self.window, &self.webview) {
                        let logical = size.to_logical::<f64>(window.scale_factor());
                        let _ = webview.set_bounds(Rect {
                            position: LogicalPosition::new(0.0, 0.0).into(),
                            size: LogicalSize::new(logical.width, logical.height).into(),
                        });
                    }
                }
                WindowEvent::CloseRequested => self.shutdown(event_loop),
                _ => {}
            }
        }

        fn about_to_wait(&mut self, event_loop: &ActiveEventLoop) {
            if self.pump_commands() {
                self.shutdown(event_loop);
                return;
            }
            // 低频轮询命令通道，兼顾响应速度与 CPU 占用。
            event_loop.set_control_flow(ControlFlow::WaitUntil(
                Instant::now() + COMMAND_POLL_INTERVAL,
            ));
        }
    }

    let event_loop = match EventLoop::new() {
        Ok(event_loop) => event_loop,
        Err(error) => {
            let _ = startup.send(StartupOutcome::Failed(tf(
                "webview.event_loop_failed",
                &[("error", &error.to_string())],
            )));
            closed.store(true, Ordering::SeqCst);
            return;
        }
    };

    let mut app = App {
        url,
        title,
        commands,
        events,
        startup: Some(startup),
        window: None,
        webview: None,
        closed: Arc::clone(&closed),
    };

    if let Err(error) = event_loop.run_app(&mut app) {
        app.report_startup(StartupOutcome::Failed(tf(
            "webview.event_loop_failed",
            &[("error", &error.to_string())],
        )));
    }
    // 无论从哪条路径退出，都确保主线程能观察到"已关闭"。
    closed.store(true, Ordering::SeqCst);
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
