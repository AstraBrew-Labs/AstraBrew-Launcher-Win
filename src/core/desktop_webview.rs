//! 桌面模式 WebView 管理器（Windows / WebView2）
//!
//! 这里把旧的 macOS 原生 WebView 思路统一替换成 Windows 下的 WebView2。
//! 为了便于后续扩展，这里采用 Builder 风格封装，并保留窗口控制句柄。

use std::ffi::c_void;
use std::io::Cursor;
use std::num::NonZeroIsize;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, LazyLock, Mutex, OnceLock, mpsc};
use std::thread;

use webview2_com::{
    CoreWebView2EnvironmentOptions, CreateCoreWebView2EnvironmentCompletedHandler,
    Microsoft::Web::WebView2::Win32::{
        CreateCoreWebView2EnvironmentWithOptions, ICoreWebView2Environment,
        ICoreWebView2EnvironmentOptions,
    },
};
use ico::IconDir;
use windows::Win32::Foundation::{E_POINTER, HINSTANCE, HWND, LPARAM, LRESULT, WPARAM};
use windows::Win32::Graphics::Gdi::{
    GetMonitorInfoW, HBRUSH, MONITOR_DEFAULTTONEAREST, MONITORINFO, MonitorFromWindow,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Com::{COINIT_APARTMENTTHREADED, CoInitializeEx, CoUninitialize};
use windows::Win32::UI::WindowsAndMessaging::{
    CREATESTRUCTW, CS_HREDRAW, CS_VREDRAW, CW_USEDEFAULT, CreateIconFromResourceEx,
    CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW, GWLP_USERDATA, GetMessageW,
    GetWindowLongPtrW, HICON, ICON_BIG, ICON_SMALL, IDC_ARROW, LoadCursorW, MSG, PostMessageW,
    PostQuitMessage, RegisterClassW, SW_MAXIMIZE, SW_RESTORE, SW_SHOW, SWP_FRAMECHANGED,
    SWP_NOACTIVATE, SendMessageW, SetForegroundWindow, SetWindowLongPtrW, SetWindowPos,
    ShowWindow, TranslateMessage, WM_APP, WM_CLOSE, WM_DESTROY, WM_MOVE, WM_NCCREATE,
    WM_NCDESTROY, WM_SETICON, WM_SIZE, WNDCLASSW,
    WS_CAPTION, WS_CLIPCHILDREN, WS_CLIPSIBLINGS, WS_EX_APPWINDOW, WS_MAXIMIZEBOX,
    WS_MINIMIZEBOX, WS_OVERLAPPED, WS_POPUP, WS_SIZEBOX, WS_SYSMENU, WINDOW_EX_STYLE,
    WINDOW_STYLE,
};
use windows::core::{Error as WindowsError, HSTRING, PCWSTR};
use wry::WebView;
use wry::WebViewBuilder;
use wry::WebViewBuilderExtWindows;
use wry::raw_window_handle::{
    HandleError, HasWindowHandle, RawWindowHandle, Win32WindowHandle, WindowHandle,
};

use crate::pages::settings::EnvSource;

/// 默认桌面模式窗口宽度。
const DEFAULT_WIDTH: u32 = 1280;
/// 默认桌面模式窗口高度。
const DEFAULT_HEIGHT: u32 = 720;
/// WebView 用户数据目录名称。
const WEBVIEW_USER_DATA_DIR: &str = "desktop-webview";
/// WebView2 默认附加启动参数。
///
/// 这里优先关闭桌面模式不需要的浏览器后台能力，尽量压低额外进程数和常驻内存占用。
const DEFAULT_WEBVIEW_BROWSER_ARGS: &str = concat!(
    "--disable-extensions ",
    "--disable-component-extensions-with-background-pages ",
    "--disable-background-networking ",
    "--disable-component-update ",
    "--disable-sync ",
    "--disable-features=Translate,OptimizationHints,MediaRouter ",
    "--no-default-browser-check ",
    "--no-first-run ",
    "--renderer-process-limit=2"
);
/// 桌面模式窗口图标 ICO 资源。
const SILLYTAVERN_WINDOW_ICON: &[u8] = include_bytes!("../../icons/logo_st.ico");

/// 导出路径，下载处理器会优先把文件保存到这里。
static EXPORT_PATH: LazyLock<Mutex<PathBuf>> = LazyLock::new(|| {
    Mutex::new(crate::utils::app_paths().data.join("exports"))
});

/// blob 下载结果通知队列，主线程可以按需轮询展示。
pub static DOWNLOAD_NOTIFICATIONS: LazyLock<Mutex<Vec<String>>> =
    LazyLock::new(|| Mutex::new(Vec::new()));

/// 关闭回调类型，便于桌面窗口关闭后触发宿主逻辑。
type CloseHandler = Arc<dyn Fn() + Send + Sync>;
/// IPC 回调类型，网页可以把消息投递回 Rust。
type IpcHandler = Arc<dyn Fn(String) + Send + Sync>;

/// WebView2 运行时来源。
#[derive(Clone, Debug, Default)]
pub enum WebViewRuntime {
    /// 使用系统已安装的 WebView2 Runtime。
    System,
    /// 使用内置 Fixed Runtime。
    #[default]
    Builtin,
}

impl WebViewRuntime {
    /// 根据设置页的统一环境模式转换为 WebView 运行时来源。
    pub fn from_env_source(env_source: EnvSource) -> Self {
        match env_source {
            EnvSource::System => Self::System,
            EnvSource::Builtin => Self::Builtin,
        }
    }

    /// 获取内置运行时根目录。
    fn builtin_runtime_dir() -> PathBuf {
        crate::utils::app_paths().lib.join("webview2")
    }

    /// 解析本次 WebView2 应使用的浏览器运行时目录。
    fn browser_executable_dir(&self) -> Result<Option<PathBuf>, String> {
        match self {
            Self::System => Ok(None),
            Self::Builtin => {
                let runtime_dir = Self::builtin_runtime_dir();
                if runtime_dir.join("msedgewebview2.exe").exists() {
                    Ok(Some(runtime_dir))
                } else {
                    Err("未找到内置 WebView2 运行时，请先在设置中安装内置 WebView2".into())
                }
            }
        }
    }

    /// 为不同运行时隔离用户数据目录，避免环境混用导致的会话污染。
    fn user_data_dir_name(&self) -> &'static str {
        match self {
            Self::System => "system",
            Self::Builtin => "builtin",
        }
    }
}

/// 可扩展的 WebView2 窗口 Builder。
pub struct WebViewWindow {
    url: String,
    title: String,
    width: u32,
    height: u32,
    fullscreen: bool,
    devtools: bool,
    resizable: bool,
    maximized: bool,
    decorations: bool,
    user_agent: Option<String>,
    init_scripts: Vec<String>,
    additional_browser_args: Option<String>,
    export_path: Option<PathBuf>,
    user_data_dir: Option<PathBuf>,
    runtime: WebViewRuntime,
    on_close: Option<CloseHandler>,
    on_ipc: Option<IpcHandler>,
}

#[allow(dead_code)]
impl WebViewWindow {
    /// 创建一个新的 WebView 窗口配置。
    pub fn new(url: impl Into<String>) -> Self {
        Self {
            url: url.into(),
            title: "WebView".into(),
            width: DEFAULT_WIDTH,
            height: DEFAULT_HEIGHT,
            fullscreen: false,
            devtools: false,
            resizable: true,
            maximized: false,
            decorations: true,
            user_agent: None,
            init_scripts: Vec::new(),
            additional_browser_args: None,
            export_path: None,
            user_data_dir: None,
            runtime: WebViewRuntime::default(),
            on_close: None,
            on_ipc: None,
        }
    }

    /// 设置窗口标题。
    pub fn title(mut self, title: impl Into<String>) -> Self {
        self.title = title.into();
        self
    }

    /// 设置窗口尺寸。
    pub fn size(mut self, width: u32, height: u32) -> Self {
        self.width = width;
        self.height = height;
        self
    }

    /// 设置是否启动即全屏。
    pub fn fullscreen(mut self, value: bool) -> Self {
        self.fullscreen = value;
        self
    }

    /// 设置是否开启开发者工具。
    pub fn devtools(mut self, value: bool) -> Self {
        self.devtools = value;
        self
    }

    /// 设置窗口是否可调整大小。
    pub fn resizable(mut self, value: bool) -> Self {
        self.resizable = value;
        self
    }

    /// 设置窗口是否启动即最大化。
    pub fn maximized(mut self, value: bool) -> Self {
        self.maximized = value;
        self
    }

    /// 设置窗口是否保留系统边框。
    pub fn decorations(mut self, value: bool) -> Self {
        self.decorations = value;
        self
    }

    /// 设置自定义 User-Agent。
    pub fn user_agent(mut self, ua: impl Into<String>) -> Self {
        self.user_agent = Some(ua.into());
        self
    }

    /// 追加初始化脚本，支持多次调用累积注入。
    pub fn init_script(mut self, script: impl Into<String>) -> Self {
        self.init_scripts.push(script.into());
        self
    }

    /// 设置 WebView2 的额外浏览器参数。
    pub fn additional_browser_args(mut self, args: impl Into<String>) -> Self {
        self.additional_browser_args = Some(args.into());
        self
    }

    /// 指定下载导出目录。
    pub fn export_path(mut self, path: impl Into<PathBuf>) -> Self {
        self.export_path = Some(path.into());
        self
    }

    /// 自定义 WebView 用户数据目录。
    pub fn user_data_dir(mut self, path: impl Into<PathBuf>) -> Self {
        self.user_data_dir = Some(path.into());
        self
    }

    /// 选择系统或内置 WebView2 运行时。
    pub fn runtime(mut self, runtime: WebViewRuntime) -> Self {
        self.runtime = runtime;
        self
    }

    /// 注册关闭回调。
    pub fn on_close<F>(mut self, handler: F) -> Self
    where
        F: Fn() + Send + Sync + 'static,
    {
        self.on_close = Some(Arc::new(handler));
        self
    }

    /// 注册 IPC 回调。
    pub fn on_ipc<F>(mut self, handler: F) -> Self
    where
        F: Fn(String) + Send + Sync + 'static,
    {
        self.on_ipc = Some(Arc::new(handler));
        self
    }

    /// 启动窗口并返回控制句柄。
    pub fn run(self) -> Result<DesktopWebView, String> {
        DesktopWebView::spawn(self)
    }

    /// 生成默认注入脚本，统一挂载 AstraBrew 能力入口。
    fn merged_init_script(&self) -> String {
        let mut scripts = vec![format!(
            r#"
                window.AstraBrew = window.AstraBrew || {{}};
                window.AstraBrew.version = "{}";
                window.AstraBrew.ipc = {{
                    postMessage(message) {{
                        if (window.ipc && typeof window.ipc.postMessage === "function") {{
                            window.ipc.postMessage(String(message));
                        }}
                    }}
                }};
            "#,
            env!("CARGO_PKG_VERSION")
        )];
        scripts.extend(self.init_scripts.iter().cloned());
        scripts.join("\n")
    }

    /// 解析本次实例使用的用户数据目录。
    fn resolved_user_data_dir(&self) -> PathBuf {
        if let Some(dir) = &self.user_data_dir {
            return dir.clone();
        }
        crate::utils::app_paths()
            .temp
            .join(WEBVIEW_USER_DATA_DIR)
            .join(self.runtime.user_data_dir_name())
    }

    /// 合并默认浏览器参数与外部追加参数。
    fn effective_browser_args(&self) -> String {
        match &self.additional_browser_args {
            Some(args) if !args.trim().is_empty() => {
                format!("{DEFAULT_WEBVIEW_BROWSER_ARGS} {}", args.trim())
            }
            _ => DEFAULT_WEBVIEW_BROWSER_ARGS.to_string(),
        }
    }
}

/// 运行中的桌面模式窗口句柄。
pub struct DesktopWebView {
    command_tx: mpsc::Sender<WebViewCommand>,
    hwnd: HWND,
    closed: Arc<AtomicBool>,
    /// 保留标题字段，便于后续扩展窗口切换、调试和诊断。
    title: String,
    /// 保留 URL 字段，便于后续扩展重定向检测与状态同步。
    url: String,
    _thread: thread::JoinHandle<()>,
}

#[allow(dead_code)]
impl DesktopWebView {
    /// 兼容旧调用方式，直接按默认配置打开一个桌面窗口。
    pub fn open(
        url: &str,
        title: &str,
        export_path: String,
        env_mode: EnvSource,
    ) -> Result<Self, String> {
        WebViewWindow::new(url)
            .title(title)
            .export_path(export_path)
            .runtime(WebViewRuntime::from_env_source(env_mode))
            .run()
    }

    /// 更新导出目录，后续下载会优先落到该路径。
    pub fn set_export_path(path: &str) {
        if let Ok(mut export_path) = EXPORT_PATH.lock() {
            *export_path = PathBuf::from(path);
        }
    }

    /// 将窗口关闭。
    pub fn close(&mut self) {
        self.send_command(WebViewCommand::Close);
    }

    /// 将窗口唤回前台。
    pub fn bring_to_front(&self) {
        self.send_command(WebViewCommand::BringToFront);
    }

    /// 动态切换全屏状态。
    pub fn set_fullscreen(&self, value: bool) {
        self.send_command(WebViewCommand::SetFullscreen(value));
    }

    /// 执行一段运行时脚本，方便后续扩展宿主控制。
    pub fn evaluate_script(&self, script: impl Into<String>) {
        self.send_command(WebViewCommand::EvaluateScript(script.into()));
    }

    /// 跳转到新地址。
    pub fn navigate(&self, url: impl Into<String>) {
        self.send_command(WebViewCommand::Navigate(url.into()));
    }

    /// 检查窗口是否已经关闭。
    pub fn is_closed(&self) -> bool {
        self.closed.load(Ordering::SeqCst)
    }

    /// WebView 是否仍在运行。
    pub fn is_running(&self) -> bool {
        !self.is_closed()
    }

    /// 当前句柄对应的初始 URL。
    pub fn url(&self) -> &str {
        &self.url
    }

    /// 当前句柄对应的初始标题。
    pub fn title(&self) -> &str {
        &self.title
    }

    /// 向窗口线程发送控制命令，并唤醒原生消息循环。
    fn send_command(&self, command: WebViewCommand) {
        if self.closed.load(Ordering::SeqCst) {
            return;
        }
        if self.command_tx.send(command).is_ok() {
            unsafe {
                let _ = PostMessageW(Some(self.hwnd), WM_DESKTOP_WEBVIEW_COMMAND, WPARAM(0), LPARAM(0));
            }
        }
    }

    /// 启动真正的 WebView2 窗口线程并返回控制句柄。
    fn spawn(config: WebViewWindow) -> Result<Self, String> {
        let closed = Arc::new(AtomicBool::new(false));
        let title = config.title.clone();
        let url = config.url.clone();
        let (startup_tx, startup_rx) = mpsc::sync_channel::<Result<isize, String>>(1);
        let (command_tx, command_rx) = mpsc::channel();
        let thread_closed = Arc::clone(&closed);

        let thread = thread::Builder::new()
            .name("desktop-webview".into())
            .spawn(move || {
                run_webview_window_thread(
                    config,
                    command_rx,
                    Arc::clone(&thread_closed),
                    startup_tx,
                );
            })
            .map_err(|err| format!("启动 WebView 线程失败: {err}"))?;

        let hwnd_value = startup_rx
            .recv()
            .map_err(|_| "WebView 启动结果通道已断开".to_string())??;
        let hwnd = HWND(hwnd_value as *mut c_void);

        Ok(Self {
            command_tx,
            hwnd,
            closed,
            title,
            url,
            _thread: thread,
        })
    }
}

/// 原生命令消息，用于唤醒窗口线程处理管道命令。
const WM_DESKTOP_WEBVIEW_COMMAND: u32 = WM_APP + 1;

/// 窗口线程内部使用的控制命令。
#[allow(dead_code)]
enum WebViewCommand {
    BringToFront,
    Close,
    SetFullscreen(bool),
    EvaluateScript(String),
    Navigate(String),
}

/// Win32 原生窗口包装，供 `wry` 读取窗口句柄。
struct NativeWindowHandle {
    hwnd: HWND,
    hinstance: HINSTANCE,
}

impl NativeWindowHandle {
    /// 根据原生句柄创建包装对象。
    fn new(hwnd: HWND) -> Result<Self, String> {
        let hinstance = unsafe {
            GetModuleHandleW(None)
                .map(HINSTANCE::from)
                .map_err(|err| format!("获取窗口模块句柄失败: {err}"))?
        };
        Ok(Self { hwnd, hinstance })
    }
}

impl HasWindowHandle for NativeWindowHandle {
    fn window_handle(&self) -> Result<WindowHandle<'_>, HandleError> {
        let hwnd =
            NonZeroIsize::new(self.hwnd.0 as isize).ok_or(HandleError::Unavailable)?;
        let mut handle = Win32WindowHandle::new(hwnd);
        handle.hinstance = NonZeroIsize::new(self.hinstance.0 as isize);
        unsafe { Ok(WindowHandle::borrow_raw(RawWindowHandle::Win32(handle))) }
    }
}

/// 窗口线程内部状态。
struct DesktopWebViewState {
    command_rx: mpsc::Receiver<WebViewCommand>,
    closed: Arc<AtomicBool>,
    on_close: Option<CloseHandler>,
    webview: Option<WebView>,
    fullscreen: bool,
    closed_callback_fired: bool,
}

impl DesktopWebViewState {
    /// 初始化窗口线程状态。
    fn new(
        command_rx: mpsc::Receiver<WebViewCommand>,
        closed: Arc<AtomicBool>,
        on_close: Option<CloseHandler>,
        fullscreen: bool,
    ) -> Self {
        Self {
            command_rx,
            closed,
            on_close,
            webview: None,
            fullscreen,
            closed_callback_fired: false,
        }
    }

    /// 标记窗口已关闭。
    fn mark_closed(&self) {
        self.closed.store(true, Ordering::SeqCst);
    }

    /// 仅在第一次关闭时触发回调，避免重复执行停服逻辑。
    fn fire_close_callback_once(&mut self) {
        if self.closed_callback_fired {
            return;
        }
        self.closed_callback_fired = true;
        if let Some(handler) = &self.on_close {
            handler();
        }
    }

    /// 处理来自主线程的窗口命令。
    fn process_commands(&mut self, hwnd: HWND) {
        while let Ok(command) = self.command_rx.try_recv() {
            match command {
                WebViewCommand::BringToFront => unsafe {
                    let _ = ShowWindow(hwnd, SW_RESTORE);
                    let _ = SetForegroundWindow(hwnd);
                },
                WebViewCommand::Close => unsafe {
                    // 先主动释放 WebView，再销毁窗口，避免内存占用拖到消息循环退出后才回收。
                    self.webview.take();
                    let _ = DestroyWindow(hwnd);
                },
                WebViewCommand::SetFullscreen(enabled) => {
                    self.fullscreen = enabled;
                    apply_fullscreen(hwnd, enabled);
                }
                WebViewCommand::EvaluateScript(script) => {
                    if let Some(webview) = &self.webview {
                        let _ = webview.evaluate_script(&script);
                    }
                }
                WebViewCommand::Navigate(url) => {
                    if let Some(webview) = &self.webview {
                        let _ = webview.load_url(&url);
                    }
                }
            }
        }
    }
}

/// 窗口创建参数。
struct DesktopWebViewCreateParams {
    state: Box<DesktopWebViewState>,
}

/// 已注册的桌面模式窗口类 Atom。
static DESKTOP_WEBVIEW_WINDOW_CLASS: OnceLock<Result<u16, String>> = OnceLock::new();

/// 桌面模式大小图标句柄。
#[derive(Clone, Copy)]
struct DesktopWindowIcons {
    big: HICON,
    small: HICON,
}

/// 获取并注册桌面模式窗口类。
fn desktop_webview_window_class() -> Result<u16, String> {
    DESKTOP_WEBVIEW_WINDOW_CLASS
        .get_or_init(register_desktop_webview_window_class)
        .clone()
}

/// 获取桌面模式窗口图标。
fn desktop_window_icons() -> Result<DesktopWindowIcons, String> {
    load_desktop_window_icons()
}

/// 从内嵌 ICO 资源创建大小图标。
fn load_desktop_window_icons() -> Result<DesktopWindowIcons, String> {
    let icon_dir = IconDir::read(Cursor::new(SILLYTAVERN_WINDOW_ICON))
        .map_err(|err| format!("解析桌面模式图标失败: {err}"))?;
    let big = load_icon_from_icon_dir(&icon_dir, 48, "大图标")?;
    let small = load_icon_from_icon_dir(&icon_dir, 16, "小图标")?;
    Ok(DesktopWindowIcons { big, small })
}

/// 从 ICO 中挑选最合适尺寸的图标帧，并转换成 Win32 图标句柄。
fn load_icon_from_icon_dir(icon_dir: &IconDir, target_size: u32, label: &str) -> Result<HICON, String> {
    let entry = icon_dir
        .entries()
        .iter()
        .min_by_key(|entry| {
            let width = entry.width();
            let score = width.abs_diff(target_size);
            (score, width)
        })
        .ok_or_else(|| format!("桌面模式{label}不存在可用图标帧"))?;

    unsafe {
        CreateIconFromResourceEx(
            entry.data(),
            true,
            0x0003_0000,
            target_size as i32,
            target_size as i32,
            Default::default(),
        )
        .map_err(|err| format!("加载桌面模式{label}失败: {err}"))
    }
}

/// 注册桌面模式原生窗口类。
fn register_desktop_webview_window_class() -> Result<u16, String> {
    let module = unsafe {
        GetModuleHandleW(None)
            .map(HINSTANCE::from)
            .map_err(|err| format!("获取模块句柄失败: {err}"))?
    };
    let icons = desktop_window_icons()?;

    let class_name = HSTRING::from("AstraBrewDesktopWebViewWindow");
    let window_class = WNDCLASSW {
        style: CS_HREDRAW | CS_VREDRAW,
        hCursor: unsafe { LoadCursorW(None, IDC_ARROW).unwrap_or_default() },
        hInstance: module,
        hIcon: icons.big,
        lpszClassName: PCWSTR(class_name.as_ptr()),
        lpfnWndProc: Some(desktop_webview_wndproc),
        hbrBackground: HBRUSH(std::ptr::null_mut()),
        ..Default::default()
    };

    let atom = unsafe { RegisterClassW(&window_class) };
    if atom == 0 {
        Err("注册桌面模式窗口类失败".into())
    } else {
        Ok(atom)
    }
}

/// 原生窗口线程入口。
fn run_webview_window_thread(
    config: WebViewWindow,
    command_rx: mpsc::Receiver<WebViewCommand>,
    closed: Arc<AtomicBool>,
    startup_tx: mpsc::SyncSender<Result<isize, String>>,
) {
    let run_result = (|| -> Result<(), String> {
        let _com_scope = ComScope::new()?;
        let class_atom = desktop_webview_window_class()?;
        let module = unsafe {
            GetModuleHandleW(None)
                .map(HINSTANCE::from)
                .map_err(|err| format!("获取模块句柄失败: {err}"))?
        };
        let icons = desktop_window_icons()?;

        let state = Box::new(DesktopWebViewState::new(
            command_rx,
            Arc::clone(&closed),
            config.on_close.clone(),
            config.fullscreen,
        ));
        let create_params = Box::new(DesktopWebViewCreateParams { state });
        let title = HSTRING::from(config.title.clone());
        let window_style = desktop_window_style(&config);

        let hwnd = unsafe {
            CreateWindowExW(
                WINDOW_EX_STYLE(WS_EX_APPWINDOW.0),
                PCWSTR(class_atom as usize as _),
                PCWSTR(title.as_ptr()),
                window_style,
                CW_USEDEFAULT,
                CW_USEDEFAULT,
                config.width as i32,
                config.height as i32,
                None,
                None,
                Some(module),
                Some(Box::into_raw(create_params) as *const c_void),
            )
        }
        .map_err(|err| format!("创建桌面模式原生窗口失败: {err}"))?;

        if hwnd.0.is_null() {
            closed.store(true, Ordering::SeqCst);
            return Err("创建桌面模式原生窗口失败".into());
        }

        unsafe {
            let _ = SendMessageW(
                hwnd,
                WM_SETICON,
                Some(WPARAM(ICON_BIG as usize)),
                Some(LPARAM(icons.big.0 as isize)),
            );
            let _ = SendMessageW(
                hwnd,
                WM_SETICON,
                Some(WPARAM(ICON_SMALL as usize)),
                Some(LPARAM(icons.small.0 as isize)),
            );
        }

        let native_window = NativeWindowHandle::new(hwnd)?;
        let webview = build_webview(&native_window, &config)?;
        let state = unsafe { desktop_webview_state(hwnd) }
            .ok_or_else(|| "桌面模式窗口状态初始化失败".to_string())?;
        state.webview = Some(webview);

        if config.maximized {
            unsafe {
                let _ = ShowWindow(hwnd, SW_MAXIMIZE);
            }
        } else {
            unsafe {
                let _ = ShowWindow(hwnd, SW_SHOW);
            }
        }
        if config.fullscreen {
            apply_fullscreen(hwnd, true);
        }

        // 在进入消息循环前就回传窗口句柄，避免主线程一直阻塞到 WebView 关闭。
        startup_tx
            .send(Ok(hwnd.0 as isize))
            .map_err(|_| "WebView 启动结果通道已断开".to_string())?;

        let mut message = MSG::default();
        loop {
            let has_message = unsafe { GetMessageW(&mut message, None, 0, 0) };
            if has_message.0 == 0 {
                break;
            }
            unsafe {
                let _ = TranslateMessage(&message);
                DispatchMessageW(&message);
            }
        }

        Ok(())
    })();

    if let Err(err) = run_result {
        closed.store(true, Ordering::SeqCst);
        let _ = startup_tx.send(Err(err));
    }
}

/// 组装桌面模式顶层窗口样式。
fn desktop_window_style(config: &WebViewWindow) -> WINDOW_STYLE {
    let mut style = WINDOW_STYLE(WS_CLIPCHILDREN.0 | WS_CLIPSIBLINGS.0);
    if config.decorations {
        style |= WINDOW_STYLE(WS_OVERLAPPED.0 | WS_CAPTION.0 | WS_SYSMENU.0 | WS_MINIMIZEBOX.0);
        if config.resizable {
            style |= WINDOW_STYLE(WS_SIZEBOX.0 | WS_MAXIMIZEBOX.0);
        }
    } else {
        style |= WINDOW_STYLE(WS_POPUP.0);
    }
    style
}

/// 获取窗口状态指针。
unsafe fn desktop_webview_state<'a>(hwnd: HWND) -> Option<&'a mut DesktopWebViewState> {
    let ptr = unsafe { GetWindowLongPtrW(hwnd, GWLP_USERDATA) as *mut DesktopWebViewState };
    unsafe { ptr.as_mut() }
}

/// 应用全屏或退出全屏。
fn apply_fullscreen(hwnd: HWND, enabled: bool) {
    if !enabled {
        unsafe {
            let _ = ShowWindow(hwnd, SW_RESTORE);
        }
        return;
    }

    let monitor = unsafe { MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) };
    if monitor.0.is_null() {
        unsafe {
            let _ = ShowWindow(hwnd, SW_MAXIMIZE);
        }
        return;
    }

    let mut monitor_info = MONITORINFO {
        cbSize: std::mem::size_of::<MONITORINFO>() as u32,
        ..Default::default()
    };
    if unsafe { GetMonitorInfoW(monitor, &mut monitor_info) }.as_bool() {
        let rect = monitor_info.rcMonitor;
        unsafe {
            let _ = SetWindowPos(
                hwnd,
                None,
                rect.left,
                rect.top,
                rect.right - rect.left,
                rect.bottom - rect.top,
                SWP_FRAMECHANGED | SWP_NOACTIVATE,
            );
        }
    } else {
        unsafe {
            let _ = ShowWindow(hwnd, SW_MAXIMIZE);
        }
    }
}

/// 桌面模式窗口过程。
extern "system" fn desktop_webview_wndproc(
    hwnd: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    match message {
        WM_NCCREATE => {
            let create_struct = unsafe { &*(lparam.0 as *const CREATESTRUCTW) };
            let params = unsafe {
                Box::from_raw(create_struct.lpCreateParams as *mut DesktopWebViewCreateParams)
            };
            let DesktopWebViewCreateParams { state } = *params;
            unsafe {
                SetWindowLongPtrW(hwnd, GWLP_USERDATA, Box::into_raw(state) as _);
            }
            unsafe { DefWindowProcW(hwnd, message, wparam, lparam) }
        }
        WM_DESKTOP_WEBVIEW_COMMAND => {
            if let Some(state) = unsafe { desktop_webview_state(hwnd) } {
                state.process_commands(hwnd);
            }
            LRESULT(0)
        }
        WM_SIZE | WM_MOVE => LRESULT(0),
        WM_CLOSE => {
            unsafe {
                let _ = DestroyWindow(hwnd);
            }
            LRESULT(0)
        }
        WM_DESTROY => {
            if let Some(state) = unsafe { desktop_webview_state(hwnd) } {
                // 关闭按钮直接销毁窗口时，也要确保 WebView 在退出消息循环前尽早释放。
                state.webview.take();
                state.mark_closed();
                state.fire_close_callback_once();
            }
            unsafe {
                PostQuitMessage(0);
            }
            LRESULT(0)
        }
        WM_NCDESTROY => {
            let ptr = unsafe { SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0) };
            if ptr != 0 {
                drop(unsafe { Box::<DesktopWebViewState>::from_raw(ptr as _) });
            }
            unsafe { DefWindowProcW(hwnd, message, wparam, lparam) }
        }
        _ => unsafe { DefWindowProcW(hwnd, message, wparam, lparam) },
    }
}

/// 构建真正的 Wry WebView 实例。
fn build_webview(window: &impl HasWindowHandle, config: &WebViewWindow) -> Result<WebView, String> {
    if let Some(path) = &config.export_path {
        DesktopWebView::set_export_path(&path.to_string_lossy());
    }

    let environment = create_webview2_environment(config)?;
    let user_agent = config.user_agent.clone();
    let merged_script = config.merged_init_script();
    let ipc_handler = config.on_ipc.clone();

    let mut builder = WebViewBuilder::new()
        .with_url(&config.url)
        .with_initialization_script(merged_script)
        .with_devtools(config.devtools)
        .with_environment(environment);

    if let Some(user_agent) = user_agent {
        builder = builder.with_user_agent(&user_agent);
    }

    let browser_args = config.effective_browser_args();
    builder = builder.with_additional_browser_args(&browser_args);

    builder = builder
        .with_ipc_handler(move |request| {
            if let Some(handler) = &ipc_handler {
                handler(request.body().clone());
            }
        })
        .with_download_started_handler(|url, path| {
            let export_dir = current_export_path();
            if ensure_directory(&export_dir).is_err() {
                push_download_notification(format!("下载目录不可用，已使用默认保存路径: {url}"));
                return true;
            }

            let file_name = path
                .file_name()
                .map(|value| value.to_owned())
                .unwrap_or_else(|| "download.bin".into());
            *path = export_dir.join(file_name);
            true
        })
        .with_download_completed_handler(|url, path, success| {
            if success {
                let target = path
                    .map(|value| value.display().to_string())
                    .unwrap_or_else(|| "未知路径".to_string());
                push_download_notification(format!("下载完成: {target} <- {url}"));
            } else {
                push_download_notification(format!("下载失败: {url}"));
            }
        });

    let webview = builder
        .build(window)
        .map_err(|err| format!("创建 WebView 失败: {err}"))?;

    if config.devtools {
        webview.open_devtools();
    }

    Ok(webview)
}

/// 创建 WebView2 环境，用于切换系统运行时和内置 Fixed Runtime。
fn create_webview2_environment(config: &WebViewWindow) -> Result<ICoreWebView2Environment, String> {
    let browser_executable_dir = config.runtime.browser_executable_dir()?;
    let user_data_dir = config.resolved_user_data_dir();
    ensure_directory(&user_data_dir)
        .map_err(|err| format!("创建 WebView 用户数据目录失败: {err}"))?;

    let browser_dir = browser_executable_dir
        .as_ref()
        .map(|path| path_to_hstring(path));
    let user_data = path_to_hstring(&user_data_dir);

    let options = {
        let options = CoreWebView2EnvironmentOptions::default();
        // 这里为不同实例隔离用户数据目录，避免不同运行时抢占同一份 profile。
        unsafe {
            options.set_exclusive_user_data_folder_access(true);
        }
        let options: ICoreWebView2EnvironmentOptions = options.into();
        options
    };

    let (tx, rx) = mpsc::channel();

    CreateCoreWebView2EnvironmentCompletedHandler::wait_for_async_operation(
        Box::new(move |handler| unsafe {
            let browser_dir_ptr = browser_dir
                .as_ref()
                .map(|value| PCWSTR(value.as_ptr()))
                .unwrap_or(PCWSTR::null());
            CreateCoreWebView2EnvironmentWithOptions(
                browser_dir_ptr,
                PCWSTR(user_data.as_ptr()),
                &options,
                &handler,
            )
            .map_err(webview2_com::Error::WindowsError)
        }),
        Box::new(move |error_code, environment| {
            error_code?;
            tx.send(environment.ok_or_else(|| WindowsError::from(E_POINTER)))
                .expect("send webview2 environment over mpsc channel");
            Ok(())
        }),
    )
    .map_err(|err| format!("初始化 WebView2 环境失败: {err}"))?;

    let environment = rx
        .recv()
        .map_err(|_| "WebView2 环境结果通道已断开".to_string())?
        .map_err(|err| format!("创建 WebView2 环境失败: {err}"))?;

    Ok(environment)
}

/// 读取当前下载导出目录。
fn current_export_path() -> PathBuf {
    EXPORT_PATH
        .lock()
        .map(|path| path.clone())
        .unwrap_or_else(|_| crate::utils::app_paths().data.join("exports"))
}

/// 推送下载通知。
fn push_download_notification(message: String) {
    if let Ok(mut notifications) = DOWNLOAD_NOTIFICATIONS.lock() {
        notifications.push(message);
    }
}

/// 确保目录存在。
fn ensure_directory(path: &Path) -> std::io::Result<()> {
    std::fs::create_dir_all(path)
}

/// 把路径转换为 `HSTRING`，供 WebView2 COM API 使用。
fn path_to_hstring(path: &Path) -> HSTRING {
    HSTRING::from(path.to_string_lossy().to_string())
}

/// 在 WebView 线程初始化 COM 单线程单元。
struct ComScope;

impl ComScope {
    /// 初始化当前线程的 COM 环境。
    fn new() -> Result<Self, String> {
        unsafe {
            CoInitializeEx(None, COINIT_APARTMENTTHREADED)
                .ok()
                .map_err(|err| format!("初始化 COM 线程环境失败: {err}"))?;
        }
        Ok(Self)
    }
}

impl Drop for ComScope {
    fn drop(&mut self) {
        unsafe {
            CoUninitialize();
        }
    }
}
