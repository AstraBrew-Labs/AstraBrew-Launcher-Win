//! 桌面模式 WebView 管理器（Windows / WebView2）
//!
//! 这里把旧的 macOS 原生 WebView 思路统一替换成 Windows 下的 WebView2。
//! 为了便于后续扩展，这里采用 Builder 风格封装，并保留窗口控制句柄。

use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, LazyLock, Mutex, mpsc};
use std::thread;

use webview2_com::{
    CoreWebView2EnvironmentOptions, CreateCoreWebView2EnvironmentCompletedHandler,
    Microsoft::Web::WebView2::Win32::{
        CreateCoreWebView2EnvironmentWithOptions, ICoreWebView2Environment,
        ICoreWebView2EnvironmentOptions,
    },
};
use windows::Win32::Foundation::E_POINTER;
use windows::Win32::System::Com::{COINIT_APARTMENTTHREADED, CoInitializeEx, CoUninitialize};
use windows::core::{Error as WindowsError, HSTRING, PCWSTR};
use winit::application::ApplicationHandler;
use winit::dpi::LogicalSize;
use winit::event::WindowEvent;
use winit::event_loop::{ActiveEventLoop, EventLoop, EventLoopProxy};
use winit::platform::windows::EventLoopBuilderExtWindows;
use winit::window::{Fullscreen, Window};
use wry::WebView;
use wry::WebViewBuilder;
use wry::WebViewBuilderExtWindows;

use crate::pages::settings::EnvSource;

/// 默认桌面模式窗口宽度。
const DEFAULT_WIDTH: u32 = 1280;
/// 默认桌面模式窗口高度。
const DEFAULT_HEIGHT: u32 = 720;
/// WebView 用户数据目录名称。
const WEBVIEW_USER_DATA_DIR: &str = "desktop-webview";

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
}

/// 运行中的桌面模式窗口句柄。
pub struct DesktopWebView {
    proxy: EventLoopProxy<WebViewCommand>,
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
        let _ = self.proxy.send_event(WebViewCommand::Close);
    }

    /// 将窗口唤回前台。
    pub fn bring_to_front(&self) {
        let _ = self.proxy.send_event(WebViewCommand::BringToFront);
    }

    /// 动态切换全屏状态。
    pub fn set_fullscreen(&self, value: bool) {
        let _ = self.proxy.send_event(WebViewCommand::SetFullscreen(value));
    }

    /// 执行一段运行时脚本，方便后续扩展宿主控制。
    pub fn evaluate_script(&self, script: impl Into<String>) {
        let _ = self
            .proxy
            .send_event(WebViewCommand::EvaluateScript(script.into()));
    }

    /// 跳转到新地址。
    pub fn navigate(&self, url: impl Into<String>) {
        let _ = self.proxy.send_event(WebViewCommand::Navigate(url.into()));
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

    /// 启动真正的 WebView2 线程并返回控制句柄。
    fn spawn(config: WebViewWindow) -> Result<Self, String> {
        let closed = Arc::new(AtomicBool::new(false));
        let startup_closed = Arc::clone(&closed);
        let title = config.title.clone();
        let url = config.url.clone();

        let (proxy_tx, proxy_rx) = mpsc::sync_channel(1);
        let (startup_tx, startup_rx) = mpsc::sync_channel(1);

        let thread = thread::Builder::new()
            .name("desktop-webview".into())
            .spawn(move || {
                let _com_scope = match ComScope::new() {
                    Ok(scope) => scope,
                    Err(err) => {
                        let _ = startup_tx.send(Err(err));
                        startup_closed.store(true, Ordering::SeqCst);
                        return;
                    }
                };

                let mut event_loop_builder = EventLoop::<WebViewCommand>::with_user_event();
                // 桌面模式运行在独立线程中，Windows 下需要显式允许任意线程创建事件循环。
                event_loop_builder.with_any_thread(true);

                let event_loop = match event_loop_builder.build() {
                    Ok(loop_handle) => loop_handle,
                    Err(err) => {
                        let _ = startup_tx.send(Err(format!("创建 WebView 事件循环失败: {err}")));
                        startup_closed.store(true, Ordering::SeqCst);
                        return;
                    }
                };

                let proxy = event_loop.create_proxy();
                let _ = proxy_tx.send(proxy);

                let mut app = DesktopWebViewApp::new(config, startup_tx, startup_closed);
                if let Err(err) = event_loop.run_app(&mut app) {
                    let _ = app.send_startup_error_if_needed(format!("运行 WebView 事件循环失败: {err}"));
                    app.mark_closed();
                }
            })
            .map_err(|err| format!("启动 WebView 线程失败: {err}"))?;

        let proxy = proxy_rx
            .recv()
            .map_err(|_| "未能获取 WebView 事件代理".to_string())?;

        startup_rx
            .recv()
            .map_err(|_| "WebView 启动结果通道已断开".to_string())??;

        Ok(Self {
            proxy,
            closed,
            title,
            url,
            _thread: thread,
        })
    }
}

/// 窗口线程内部使用的控制命令。
#[allow(dead_code)]
enum WebViewCommand {
    BringToFront,
    Close,
    SetFullscreen(bool),
    EvaluateScript(String),
    Navigate(String),
}

/// 事件循环内部的应用状态。
struct DesktopWebViewApp {
    config: Option<WebViewWindow>,
    startup_tx: Option<mpsc::SyncSender<Result<(), String>>>,
    closed: Arc<AtomicBool>,
    window: Option<Window>,
    webview: Option<WebView>,
    on_close: Option<CloseHandler>,
    closed_callback_fired: bool,
}

impl DesktopWebViewApp {
    /// 初始化应用状态。
    fn new(
        config: WebViewWindow,
        startup_tx: mpsc::SyncSender<Result<(), String>>,
        closed: Arc<AtomicBool>,
    ) -> Self {
        Self {
            config: Some(config),
            startup_tx: Some(startup_tx),
            closed,
            window: None,
            webview: None,
            on_close: None,
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

    /// 发送启动结果。
    fn send_startup_result(&mut self, result: Result<(), String>) {
        if let Some(tx) = self.startup_tx.take() {
            let _ = tx.send(result);
        }
    }

    /// 当事件循环异常退出但启动结果尚未发送时，补发错误。
    fn send_startup_error_if_needed(&mut self, message: String) -> Result<(), String> {
        if self.startup_tx.is_some() {
            self.send_startup_result(Err(message.clone()));
        }
        Err(message)
    }
}

impl ApplicationHandler<WebViewCommand> for DesktopWebViewApp {
    /// 事件循环恢复时创建窗口与 WebView。
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        if self.window.is_some() {
            return;
        }

        let Some(config) = self.config.take() else {
            return;
        };

        let window_attributes = {
            let mut attrs = Window::default_attributes()
                .with_title(config.title.clone())
                .with_resizable(config.resizable)
                .with_maximized(config.maximized)
                .with_decorations(config.decorations)
                .with_inner_size(LogicalSize::new(
                    f64::from(config.width),
                    f64::from(config.height),
                ));
            if config.fullscreen {
                attrs = attrs.with_fullscreen(Some(Fullscreen::Borderless(None)));
            }
            attrs
        };

        let window = match event_loop.create_window(window_attributes) {
            Ok(window) => window,
            Err(err) => {
                self.mark_closed();
                self.send_startup_result(Err(format!("创建 WebView 窗口失败: {err}")));
                event_loop.exit();
                return;
            }
        };

        match build_webview(&window, &config) {
            Ok(webview) => {
                self.on_close = config.on_close.clone();
                self.window = Some(window);
                self.webview = Some(webview);
                self.send_startup_result(Ok(()));
            }
            Err(err) => {
                self.mark_closed();
                self.send_startup_result(Err(err));
                event_loop.exit();
            }
        }
    }

    /// 处理来自宿主线程的控制命令。
    fn user_event(&mut self, event_loop: &ActiveEventLoop, event: WebViewCommand) {
        match event {
            WebViewCommand::BringToFront => {
                if let Some(window) = &self.window {
                    window.set_minimized(false);
                    window.set_visible(true);
                    window.focus_window();
                }
            }
            WebViewCommand::Close => {
                self.mark_closed();
                self.fire_close_callback_once();
                event_loop.exit();
            }
            WebViewCommand::SetFullscreen(enabled) => {
                if let Some(window) = &self.window {
                    let fullscreen = enabled.then_some(Fullscreen::Borderless(None));
                    window.set_fullscreen(fullscreen);
                }
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

    /// 处理原生窗口事件。
    fn window_event(
        &mut self,
        event_loop: &ActiveEventLoop,
        _window_id: winit::window::WindowId,
        event: WindowEvent,
    ) {
        if let WindowEvent::CloseRequested = event {
            self.mark_closed();
            self.fire_close_callback_once();
            event_loop.exit();
        }
    }

    /// 事件循环退出时兜底标记关闭状态。
    fn exiting(&mut self, _event_loop: &ActiveEventLoop) {
        self.mark_closed();
    }
}

/// 构建真正的 Wry WebView 实例。
fn build_webview(window: &Window, config: &WebViewWindow) -> Result<WebView, String> {
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

    if let Some(args) = &config.additional_browser_args {
        builder = builder.with_additional_browser_args(args);
    }

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
