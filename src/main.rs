// 禁用命令行窗口（Windows 窗口应用程序，免安装版直接双击无黑窗）
#![windows_subsystem = "windows"]

use eframe::egui;
use egui::{FontData, FontDefinitions, FontFamily};

/// 控制台运行中的后台刷新节流间隔，避免主界面长期满帧重绘导致 CPU 占用过高。
const ACTIVE_CONSOLE_REPAINT_INTERVAL_MS: u64 = 120;
/// 其他后台任务的通用刷新节流间隔，兼顾进度可见性与空闲功耗。
const BACKGROUND_TASK_REPAINT_INTERVAL_MS: u64 = 150;

fn main() -> eframe::Result {
    let settings = pages::settings::SettingsState::load();

    // 窗口图标
    let icon = eframe::icon_data::from_png_bytes(include_bytes!("../icons/icon_eframe.png"))
        .unwrap_or_else(|_| egui::IconData::default());

    let mut viewport = egui::ViewportBuilder::default()
        .with_inner_size([1280.0, 720.0])
        .with_min_inner_size([800.0, 600.0])
        .with_max_inner_size([1280.0, 720.0])
        .with_app_id("cn.astrabrew.launcher")
        .with_maximize_button(false)
        .with_icon(icon);

    let mut is_centered = true;
    if settings.remember_window_pos {
        if let Some(pos) = settings.window_position {
            viewport = viewport.with_position(egui::pos2(pos[0], pos[1]));
            is_centered = false;
        }
    }

    let options = eframe::NativeOptions {
        viewport,
        centered: is_centered,
        ..Default::default()
    };

    let result = eframe::run_native(
        "星酿启动器 - AstraBrew Launcher",
        options,
        Box::new(|cc| {
            setup_fonts(&cc.egui_ctx);
            // 安装图像加载器，支持 PNG/JPEG/GIF 等格式
            egui_extras::install_image_loaders(&cc.egui_ctx);
            Ok(Box::new(MyApp::new(settings)))
        }),
    );
    if result.is_ok() {
        // eframe has already run on_exit and dropped the app state. Detached environment and
        // network workers can still race DLL detach and make ExitProcess fail with 0xc000041d.
        unsafe {
            let _ = windows::Win32::System::Threading::TerminateProcess(
                windows::Win32::System::Threading::GetCurrentProcess(),
                0,
            );
        }
        unreachable!("TerminateProcess returned after a successful shutdown");
    }
    result
}

fn setup_fonts(ctx: &egui::Context) {
    let mut fonts = FontDefinitions::default();
    fonts.font_data.insert(
        "MiSans".to_owned(),
        FontData::from_static(include_bytes!("../assets/fonts/MiSans-Regular.ttf")).into(),
    );

    egui_phosphor::add_to_fonts(&mut fonts, egui_phosphor::Variant::Regular);

    fonts
        .families
        .entry(FontFamily::Proportional)
        .or_default()
        .insert(0, "MiSans".to_owned());

    fonts
        .families
        .entry(FontFamily::Monospace)
        .or_default()
        .insert(0, "MiSans".to_owned());

    ctx.set_fonts(fonts);
}

#[derive(PartialEq)]
enum Page {
    OneClickStart,
    TavernConfig,
    VersionManage,
    ExtensionManage,
    ResourceManage,
    Console,
    Settings,
}

mod core;
#[path = "lang/lang.rs"]
mod lang;
mod pages;
mod ui;
mod utils;

use pages::console::ConsoleState;
use pages::settings::{SettingsState, SettingsTab, StartMode, Theme, GitNodeSelectState, NodejsNodeSelectState, CaddyNodeSelectState};
use pages::resource_manage::ResourceManageState;
use pages::tavern_config::TavernConfigUI;

/// 环境安装进度消息（Git/Node.js 下载安装）
#[derive(Debug, Clone)]
pub enum EnvInstallProgress {
    /// 状态消息
    Status(String),
    /// 进度 0.0-1.0
    Progress(f32),
    /// 下载速度（字节/秒）
    Speed(f32),
    /// 安装完成后的版本号（验证安装成功）
    Version(String),
    /// 错误消息
    Error(String),
    /// 安装完成
    Finished,
}

#[derive(Clone, Copy, PartialEq, Eq, Default)]
enum OneClickStage {
    #[default]
    Idle,
    CheckingEnvironment,
    SelectingGitMirror,
    InstallingGit,
    SelectingNodeMirror,
    InstallingNode,
    CheckingTavern,
    FetchingTavern,
    InstallingTavern,
    StartingTavern,
    WaitingForTavern,
}

#[derive(Default)]
struct OneClickFlow {
    stage: OneClickStage,
    cancel_requested: bool,
    cancel_dispatched: bool,
}

struct DependencyRepairTask {
    package: String,
    receiver: std::sync::mpsc::Receiver<Result<(), String>>,
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum UpdaterOperation {
    AutomaticCheck,
    ManualCheck,
    Install,
}

impl UpdaterOperation {
    fn reports_non_actionable_result(self) -> bool {
        self != Self::AutomaticCheck
    }
}

impl OneClickFlow {
    fn is_active(&self) -> bool {
        self.stage != OneClickStage::Idle
    }
}

fn system_environment_error(
    git_ready: bool,
    node_ready: bool,
    npm_ready: bool,
) -> Option<&'static str> {
    if !git_ready {
        Some("one_click_system_git_missing")
    } else if !node_ready || !npm_ready {
        Some("one_click_system_node_missing")
    } else {
        None
    }
}

fn next_environment_stage(git_ready: bool, node_ready: bool) -> OneClickStage {
    if !git_ready {
        OneClickStage::SelectingGitMirror
    } else if !node_ready {
        OneClickStage::SelectingNodeMirror
    } else {
        OneClickStage::CheckingTavern
    }
}

fn should_use_first_launch_automation(
    first_launch_available: bool,
    selected_environment_ready: bool,
) -> bool {
    first_launch_available && !selected_environment_ready
}

fn theme_preference(theme: Theme) -> egui::ThemePreference {
    match theme {
        Theme::System => egui::ThemePreference::System,
        Theme::Light => egui::ThemePreference::Light,
        Theme::Dark => egui::ThemePreference::Dark,
    }
}

fn tavern_installation_is_ready(path: &std::path::Path) -> bool {
    let package = std::fs::read_to_string(path.join("package.json"))
        .ok()
        .and_then(|content| serde_json::from_str::<serde_json::Value>(&content).ok());
    let Some(dependencies) = package
        .as_ref()
        .and_then(|json| json.get("dependencies"))
        .and_then(|dependencies| dependencies.as_object())
    else {
        return false;
    };
    !dependencies.is_empty()
        && dependencies
            .keys()
            .all(|dependency| path.join("node_modules").join(dependency).exists())
}

struct MyApp {
    current_page: Page,
    last_monitor_size: Option<egui::Vec2>,
    settings_tab: SettingsTab,
    settings_state: SettingsState,
    toast_stack: ui::toast::ToastStack,
    notification_stack: ui::notification::NotificationStack,

    // 版本管理状态
    version_manage_state: pages::version_manage::VersionManageState,
    // 扩展管理状态
    extension_manage_state: pages::extensions::ExtensionManageState,
    // 酒馆配置 UI 状态
    tavern_config_ui: TavernConfigUI,
    // 控制台状态
    console_state: ConsoleState,
    // 资源管理状态
    resource_manage_state: ResourceManageState,
    // 桌面模式 WebView 句柄
    desktop_webview: Option<crate::core::desktop_webview::DesktopWebView>,
    // 桌面模式关闭事件通道
    desktop_webview_close_rx: Option<std::sync::mpsc::Receiver<()>>,
    // 桌面模式 IPC 事件通道
    desktop_webview_ipc_rx: Option<std::sync::mpsc::Receiver<String>>,
    // 标记当前关闭是否由主程序主动发起，避免误触发自动停服
    desktop_webview_internal_close: bool,
    // 标记当前启动器窗口是否由桌面模式自动隐藏，避免错误恢复用户自己的最小化状态
    launcher_hidden_by_webview: bool,
    // 安装任务状态（Git/Node.js/Caddy/PM2/WebView2 安装弹窗）
    #[allow(dead_code)]
    git_install_state: pages::settings::InstallTaskState,
    #[allow(dead_code)]
    nodejs_install_state: pages::settings::InstallTaskState,
    #[allow(dead_code)]
    caddy_install_state: pages::settings::InstallTaskState,
    #[allow(dead_code)]
    pm2_install_state: pages::settings::InstallTaskState,
    #[allow(dead_code)]
    webview2_install_state: pages::settings::InstallTaskState,

    // Git 节点选择弹窗状态
    git_node_select: GitNodeSelectState,

    // Node.js 节点选择弹窗状态
    nodejs_node_select: NodejsNodeSelectState,

    // Caddy 节点选择弹窗状态
    caddy_node_select: CaddyNodeSelectState,

    // Github 节点状态
    github_node_rx: Option<
        std::sync::mpsc::Receiver<crate::core::settings::github_proxy::NodeLoadMsg>,
    >,
    github_node_state: crate::core::settings::github_proxy::NodeLoadState,
    on_refresh_nodes: bool,
    folder_picker_rx: Option<std::sync::mpsc::Receiver<Option<std::path::PathBuf>>>,
    export_path_picker_rx: Option<std::sync::mpsc::Receiver<Option<std::path::PathBuf>>>,
    // 异步路径检查
    path_check_rx: Option<std::sync::mpsc::Receiver<PathCheckResult>>,
    last_path_check: Option<std::time::Instant>,
    // 自动更新检测通道
    updater_rx: Option<std::sync::mpsc::Receiver<crate::core::updater::UpdateStatus>>,
    updater_operation: Option<UpdaterOperation>,
    // 启动时缺少所选 Node.js 环境的引导弹窗。
    missing_env_dialog: Option<pages::settings::EnvSource>,
    // 从环境缺失弹窗进入设置后，将环境依赖区域滚动到视口中央。
    focus_env_dependencies: bool,
    // 小白模式的一键环境准备、酒馆安装与启动流水线。
    one_click_flow: OneClickFlow,
    // 仅配置文件首次创建的当前进程可消费一次。
    first_launch_auto_setup_available: bool,
    // ERR_MODULE_NOT_FOUND 缺失依赖确认与异步修复状态。
    missing_dependency_dialog: Option<String>,
    dependency_repair_task: Option<DependencyRepairTask>,
    dependency_repair_waiting: Option<String>,
    dependency_repair_error: Option<(String, String)>,
}

/// 后台路径检查结果
struct PathCheckResult {
    should_clear_current: bool,
    dead_instance_indices: Vec<usize>,
    /// 在线下载的 builtin 实例是否被删除
    builtin_deleted: bool,
}

impl MyApp {
    fn new(mut settings_state: SettingsState) -> Self {
        let first_launch_auto_setup_available =
            settings_state.first_launch_auto_setup_available;
        settings_state.first_launch_auto_setup_available = false;
        // 检测环境依赖版本
        settings_state.detect_all_env();

        // 同步自启动状态：以系统实际注册状态为准（用户可能在系统设置中手动关闭）
        settings_state.auto_start = crate::core::auto_launch::is_auto_launch_enabled();
        settings_state.update_checking = true;
        settings_state.save();

        let global_data_path = settings_state.global_data_path.clone();

        Self {
            current_page: Page::OneClickStart,
            last_monitor_size: None,
            settings_tab: SettingsTab::default(),
            settings_state,
            toast_stack: ui::toast::ToastStack::new(),
            notification_stack: ui::notification::NotificationStack::new(),
            version_manage_state: {
                let mut state = pages::version_manage::VersionManageState::new();
                state.local_instances = pages::version_manage::load_local_instances();
                state
            },
            extension_manage_state: pages::extensions::ExtensionManageState::new(),
            tavern_config_ui: TavernConfigUI::new(
                crate::core::settings::tavern::ConfigMode::Current,
                None,
                global_data_path,
            ),
            console_state: ConsoleState::new(),
            resource_manage_state: ResourceManageState::new(),
            desktop_webview: None,
            desktop_webview_close_rx: None,
            desktop_webview_ipc_rx: None,
            desktop_webview_internal_close: false,
            launcher_hidden_by_webview: false,
            git_install_state: pages::settings::InstallTaskState::new(),
            nodejs_install_state: pages::settings::InstallTaskState::new(),
            caddy_install_state: pages::settings::InstallTaskState::new(),
            pm2_install_state: pages::settings::InstallTaskState::new(),
            webview2_install_state: pages::settings::InstallTaskState::new(),
            git_node_select: GitNodeSelectState::new(),
            nodejs_node_select: NodejsNodeSelectState::new(),
            caddy_node_select: CaddyNodeSelectState::new(),
            github_node_rx: None,
            github_node_state: crate::core::settings::github_proxy::NodeLoadState::Done(vec![]),
            on_refresh_nodes: false,
            folder_picker_rx: None,
            export_path_picker_rx: None,
            path_check_rx: None,
            last_path_check: None,
            updater_rx: Some(crate::core::updater::start_check()),
            updater_operation: Some(UpdaterOperation::AutomaticCheck),
            missing_env_dialog: None,
            focus_env_dependencies: false,
            one_click_flow: OneClickFlow::default(),
            first_launch_auto_setup_available,
            missing_dependency_dialog: None,
            dependency_repair_task: None,
            dependency_repair_waiting: None,
            dependency_repair_error: None,
        }
    }
}

impl MyApp {
    /// 按桌面模式设置最小化或恢复主启动器窗口。
    fn set_launcher_hidden_by_webview(&mut self, ctx: &egui::Context, hidden: bool) {
        if hidden {
            if self.launcher_hidden_by_webview {
                return;
            }
            ctx.send_viewport_cmd(egui::ViewportCommand::Minimized(true));
            self.launcher_hidden_by_webview = true;
            return;
        }

        if !self.launcher_hidden_by_webview {
            return;
        }
        ctx.send_viewport_cmd(egui::ViewportCommand::Minimized(false));
        self.launcher_hidden_by_webview = false;
    }

    /// 主动关闭当前桌面模式窗口。
    fn close_desktop_webview(&mut self, ctx: &egui::Context) {
        self.set_launcher_hidden_by_webview(ctx, false);
        if let Some(mut webview) = self.desktop_webview.take() {
            self.desktop_webview_internal_close = true;
            webview.close();
        }
        self.desktop_webview_close_rx = None;
        self.desktop_webview_ipc_rx = None;
    }

    /// 打开桌面模式窗口，并把关闭/IPC 事件接回主线程。
    fn open_desktop_webview(&mut self, ctx: &egui::Context, url: String) {
        let (close_tx, close_rx) = std::sync::mpsc::channel();
        let (ipc_tx, ipc_rx) = std::sync::mpsc::channel();

        let export_path = self.settings_state.tavern_export_path.clone();
        let runtime =
            crate::core::desktop_webview::WebViewRuntime::from_env_source(self.settings_state.env_mode);
        let env_mode_name = match self.settings_state.env_mode {
            pages::settings::EnvSource::System => "System",
            pages::settings::EnvSource::Builtin => "Builtin",
        };
        let user_agent = format!(
            "AstraBrew Launcher/{} ({})",
            env!("CARGO_PKG_VERSION"),
            env_mode_name
        );
        let webview_memory_limit_mb = self.settings_state.effective_webview_memory_limit_mb();

        let webview = crate::core::desktop_webview::WebViewWindow::new(url.clone())
            .title("SillyTavern")
            .size(1280, 720)
            .resizable(true)
            .maximized(self.settings_state.auto_maximize_webview_on_start)
            .decorations(true)
            .devtools(self.settings_state.auto_open_devtools_on_webview_start)
            .additional_browser_args(format!(
                "--js-flags=--max-old-space-size={webview_memory_limit_mb}"
            ))
            .runtime(runtime)
            .user_agent(user_agent)
            .export_path(export_path)
            .init_script(
                r#"
                    console.log("AstraBrew Desktop Mode Ready");
                "#,
            )
            .on_close(move || {
                let _ = close_tx.send(());
            })
            .on_ipc(move |msg| {
                let _ = ipc_tx.send(msg);
            })
            .run();

        match webview {
            Ok(webview) => {
                self.console_state.webview_auto_opened = true;
                self.desktop_webview = Some(webview);
                self.desktop_webview_close_rx = Some(close_rx);
                self.desktop_webview_ipc_rx = Some(ipc_rx);
                if self.settings_state.auto_hide_launcher_when_webview_opens {
                    self.set_launcher_hidden_by_webview(ctx, true);
                }
                self.toast_stack.push("桌面模式已打开".into(), ctx);
            }
            Err(err) => {
                self.console_state.webview_auto_opened = true;
                self.console_state
                    .add_log(&format!("[桌面模式] 打开 WebView 失败: {err}"));
                self.notification_stack.push(
                    "桌面模式".into(),
                    format!("打开 WebView 失败，已回退到系统浏览器。\n{err}"),
                    ctx,
                );
                let _ = crate::core::shell::open_target(&url);
            }
        }
    }

    /// 处理桌面模式网页通过 `window.ipc.postMessage(...)` 发回来的消息。
    fn handle_desktop_ipc_message(&mut self, ctx: &egui::Context, message: String) {
        let trimmed = message.trim();
        match trimmed {
            "restart-server" => {
                self.console_state
                    .add_log("[桌面模式] 收到 IPC: restart-server");
                self.console_state.restart(&self.settings_state.language);
            }
            "stop-server" => {
                self.console_state
                    .add_log("[桌面模式] 收到 IPC: stop-server");
                self.console_state.stop(&self.settings_state.language);
            }
            "focus-webview" => {
                if let Some(webview) = &self.desktop_webview {
                    webview.bring_to_front();
                }
            }
            _ => {
                self.console_state
                    .add_log(&format!("[桌面模式] IPC: {trimmed}"));
                self.toast_stack
                    .push(format!("桌面 IPC: {trimmed}"), ctx);
            }
        }
    }

    /// 轮询桌面模式的关闭、IPC 和下载通知事件。
    fn drain_desktop_webview_events(&mut self, ctx: &egui::Context) {
        if let Some(rx) = &self.desktop_webview_close_rx {
            let mut closed = false;
            while rx.try_recv().is_ok() {
                closed = true;
            }

            if closed {
                self.set_launcher_hidden_by_webview(ctx, false);
                self.desktop_webview = None;
                self.desktop_webview_close_rx = None;
                self.desktop_webview_ipc_rx = None;

                if self.desktop_webview_internal_close {
                    self.desktop_webview_internal_close = false;
                } else if self.settings_state.auto_stop_tavern_on_webview_close {
                    self.console_state
                        .stop(&self.settings_state.language);
                    self.notification_stack.push(
                        "桌面模式".into(),
                        "桌面窗口已关闭，已自动停止酒馆服务。".into(),
                        ctx,
                    );
                } else {
                    self.toast_stack.push("桌面窗口已关闭".into(), ctx);
                }
            }
        }

        if let Some(rx) = &self.desktop_webview_ipc_rx {
            let mut messages = Vec::new();
            while let Ok(message) = rx.try_recv() {
                messages.push(message);
            }
            for message in messages {
                self.handle_desktop_ipc_message(ctx, message);
            }
        }

        if let Ok(mut notifications) =
            crate::core::desktop_webview::DOWNLOAD_NOTIFICATIONS.lock()
        {
            let messages: Vec<String> = notifications.drain(..).collect();
            drop(notifications);
            for message in messages {
                self.toast_stack.push(message, ctx);
            }
        }
    }

    /// 同步桌面模式窗口生命周期。
    fn sync_desktop_webview(&mut self, ctx: &egui::Context) {
        let is_desktop_mode = self.settings_state.start_mode == StartMode::Desktop;
        if !is_desktop_mode || self.console_state.status == pages::console::ConsoleStatus::Stopped {
            if self.desktop_webview.is_some() {
                self.close_desktop_webview(ctx);
            } else {
                self.set_launcher_hidden_by_webview(ctx, false);
            }
            self.console_state.reopen_webview_triggered = false;
            return;
        }

        let should_reopen = self.console_state.reopen_webview_triggered;
        self.console_state.reopen_webview_triggered = false;

        if should_reopen {
            if let Some(webview) = &self.desktop_webview {
                if webview.is_running() {
                    webview.bring_to_front();
                    return;
                }
            }
        }

        let Some(url) = self.console_state.tavern_url.clone() else {
            return;
        };

        let auto_open = !self.console_state.webview_auto_opened;
        let should_open = should_reopen || auto_open;
        if !should_open {
            return;
        }

        if self.desktop_webview.is_some() {
            self.close_desktop_webview(ctx);
        }

        self.open_desktop_webview(ctx, url);
    }

    fn begin_one_click_start(&mut self) {
        if self.one_click_flow.is_active() {
            return;
        }
        self.missing_env_dialog = None;
        self.one_click_flow = OneClickFlow {
            stage: OneClickStage::CheckingEnvironment,
            cancel_requested: false,
            cancel_dispatched: false,
        };
        self.current_page = Page::Settings;
        self.settings_tab = SettingsTab::General;
        self.focus_env_dependencies = true;
    }

    fn selected_environment_is_ready(&self) -> bool {
        match self.settings_state.env_mode {
            pages::settings::EnvSource::Builtin => {
                crate::core::settings::env_detect::detect_git_builtin().is_some()
                    && crate::core::settings::env_detect::detect_nodejs_builtin().is_some()
                    && crate::core::env::get_builtin_npm_path().is_some()
            }
            pages::settings::EnvSource::System => {
                crate::core::settings::env_detect::detect_git_system().is_some()
                    && crate::core::settings::env_detect::detect_nodejs_system().is_some()
                    && crate::core::env::get_system_cmd_path("npm").is_some()
            }
        }
    }

    fn handle_user_start_request(&mut self) {
        let use_automation = should_use_first_launch_automation(
            self.first_launch_auto_setup_available,
            self.selected_environment_is_ready(),
        );
        self.first_launch_auto_setup_available = false;

        if use_automation {
            self.begin_one_click_start();
        } else {
            self.current_page = Page::Console;
            self.console_state.start(&self.settings_state.language);
        }
    }

    fn finish_one_click_cancel(&mut self, ctx: &egui::Context) {
        self.git_install_state.show = false;
        self.nodejs_install_state.show = false;
        self.git_node_select.show = false;
        self.git_node_select.receiver = None;
        self.nodejs_node_select.show = false;
        self.nodejs_node_select.receiver = None;
        self.version_manage_state.release_receiver = None;
        self.version_manage_state.is_fetching_releases = false;
        self.version_manage_state.is_downloading = false;
        self.one_click_flow = OneClickFlow::default();
        self.notification_stack.push(
            lang::t("one_click_cancelled_title", &self.settings_state.language).to_string(),
            lang::t("one_click_cancelled_desc", &self.settings_state.language).to_string(),
            ctx,
        );
    }

    fn fail_one_click_start(&mut self, error: String, ctx: &egui::Context) {
        self.git_install_state.show = false;
        self.nodejs_install_state.show = false;
        self.version_manage_state.is_downloading = false;
        self.one_click_flow = OneClickFlow::default();
        self.notification_stack.push(
            lang::t("one_click_failed_title", &self.settings_state.language).to_string(),
            error,
            ctx,
        );
    }

    fn selected_tavern_is_ready(&mut self) -> bool {
        let selected_path = self.settings_state.sillytavern.as_ref().and_then(|instance| {
            match instance.instance_type.as_str() {
                "builtin" => Some(crate::utils::app_paths().sillytavern_dir()),
                "local" => instance.path.as_ref().map(std::path::PathBuf::from),
                _ => None,
            }
        });

        if let Some(path) = selected_path
            && tavern_installation_is_ready(&path)
        {
            return true;
        }

        let builtin_path = crate::utils::app_paths().sillytavern_dir();
        if !tavern_installation_is_ready(&builtin_path) {
            return false;
        }

        let version = std::fs::read_to_string(builtin_path.join("package.json"))
            .ok()
            .and_then(|content| serde_json::from_str::<serde_json::Value>(&content).ok())
            .and_then(|json| json.get("version")?.as_str().map(str::to_string))
            .unwrap_or_else(|| "Unknown".to_string());
        self.version_manage_state.online_installed_version = Some(version.clone());
        pages::version_manage::save_current_to_settings(
            "builtin",
            None,
            &version,
            &mut self.settings_state,
        );
        true
    }

    fn handle_one_click_cancel(&mut self, ctx: &egui::Context) -> bool {
        if !self.one_click_flow.cancel_requested {
            return false;
        }

        if !self.one_click_flow.cancel_dispatched {
            match self.one_click_flow.stage {
                OneClickStage::InstallingGit => self.git_install_state.cancel(),
                OneClickStage::InstallingNode => self.nodejs_install_state.cancel(),
                OneClickStage::InstallingTavern => {
                    self.version_manage_state.cancel_active_install()
                }
                OneClickStage::StartingTavern | OneClickStage::WaitingForTavern => {
                    self.console_state
                        .force_kill(&self.settings_state.language)
                }
                _ => {}
            }
            self.one_click_flow.cancel_dispatched = true;
        }

        let stopped = match self.one_click_flow.stage {
            OneClickStage::InstallingGit => {
                self.git_install_state.poll();
                !self.git_install_state.running
            }
            OneClickStage::InstallingNode => {
                self.nodejs_install_state.poll();
                !self.nodejs_install_state.running
            }
            OneClickStage::InstallingTavern => {
                let _ = self
                    .version_manage_state
                    .poll_install_messages(&mut self.settings_state);
                self.version_manage_state.download_receiver.is_none()
            }
            OneClickStage::StartingTavern | OneClickStage::WaitingForTavern => {
                self.console_state.status == pages::console::ConsoleStatus::Stopped
            }
            _ => true,
        };

        if stopped {
            self.finish_one_click_cancel(ctx);
        } else {
            ctx.request_repaint_after(std::time::Duration::from_millis(100));
        }
        true
    }

    fn advance_one_click_start(&mut self, ctx: &egui::Context) {
        if !self.one_click_flow.is_active() || self.handle_one_click_cancel(ctx) {
            return;
        }

        match self.one_click_flow.stage {
            OneClickStage::Idle => {}
            OneClickStage::CheckingEnvironment => {
                if self.settings_state.env_mode == pages::settings::EnvSource::System {
                    let git_version = crate::core::settings::env_detect::detect_git_system();
                    let node_version = crate::core::settings::env_detect::detect_nodejs_system();
                    let npm_ready = crate::core::env::get_system_cmd_path("npm").is_some();
                    self.settings_state.git_version = git_version.clone();
                    self.settings_state.nodejs_version =
                        node_version.clone().unwrap_or_default();
                    if let Some(error_key) = system_environment_error(
                        git_version.is_some(),
                        node_version.is_some(),
                        npm_ready,
                    ) {
                        self.fail_one_click_start(
                            lang::t(error_key, &self.settings_state.language).to_string(),
                            ctx,
                        );
                    } else {
                        self.one_click_flow.stage = OneClickStage::CheckingTavern;
                    }
                    return;
                }

                let git_version = crate::core::settings::env_detect::detect_git_builtin();
                let builtin_git_ready = git_version.is_some();
                if let Some(version) = &git_version {
                    self.settings_state.git_version_builtin = Some(version.clone());
                }

                let node_version = crate::core::settings::env_detect::detect_nodejs_builtin();
                let node_ready = node_version.is_some()
                    && crate::core::env::get_builtin_npm_path().is_some();
                if let Some(version) = node_version {
                    self.settings_state.nodejs_version_builtin = version;
                }
                let next_stage = next_environment_stage(builtin_git_ready, node_ready);
                match next_stage {
                    OneClickStage::SelectingGitMirror => {
                        self.git_node_select.open();
                        self.git_node_select.show = false;
                    }
                    OneClickStage::SelectingNodeMirror => {
                        self.nodejs_node_select.open();
                        self.nodejs_node_select.show = false;
                    }
                    _ => {}
                }
                self.one_click_flow.stage = next_stage;
            }
            OneClickStage::SelectingGitMirror => {
                self.git_node_select.poll();
                if self.git_node_select.loading {
                    ctx.request_repaint_after(std::time::Duration::from_millis(100));
                    return;
                }
                let url = self
                    .git_node_select
                    .nodes
                    .iter()
                    .find(|node| {
                        node.latency_ms.is_some() && !node.blocked && !node.timed_out
                    })
                    .or_else(|| self.git_node_select.nodes.first())
                    .map(|node| node.url.clone());
                let Some(url) = url else {
                    self.fail_one_click_start(
                        lang::t("one_click_no_git_source", &self.settings_state.language)
                            .to_string(),
                        ctx,
                    );
                    return;
                };
                self.git_install_state.start_git_install(&url);
                self.git_install_state.show = false;
                self.one_click_flow.stage = OneClickStage::InstallingGit;
            }
            OneClickStage::InstallingGit => {
                self.git_install_state.poll();
                self.git_install_state.show = false;
                if self.git_install_state.running {
                    ctx.request_repaint_after(std::time::Duration::from_millis(100));
                    return;
                }
                if self.git_install_state.installed_version.is_some() {
                    self.settings_state.git_version_builtin =
                        crate::core::settings::env_detect::detect_git_builtin();
                    self.git_install_state.done_at = None;
                    self.one_click_flow.stage = OneClickStage::CheckingEnvironment;
                } else if self.git_install_state.receiver.is_none() {
                    self.fail_one_click_start(
                        lang::t("one_click_git_install_failed", &self.settings_state.language)
                            .to_string(),
                        ctx,
                    );
                }
            }
            OneClickStage::SelectingNodeMirror => {
                self.nodejs_node_select.poll();
                if self.nodejs_node_select.loading {
                    ctx.request_repaint_after(std::time::Duration::from_millis(100));
                    return;
                }
                let url = self
                    .nodejs_node_select
                    .nodes
                    .iter()
                    .find(|node| {
                        node.latency_ms.is_some() && !node.blocked && !node.timed_out
                    })
                    .or_else(|| self.nodejs_node_select.nodes.first())
                    .map(|node| node.url.clone());
                let Some(url) = url else {
                    self.fail_one_click_start(
                        lang::t("one_click_no_node_source", &self.settings_state.language)
                            .to_string(),
                        ctx,
                    );
                    return;
                };
                self.nodejs_install_state.start_nodejs_install(&url);
                self.nodejs_install_state.show = false;
                self.one_click_flow.stage = OneClickStage::InstallingNode;
            }
            OneClickStage::InstallingNode => {
                self.nodejs_install_state.poll();
                self.nodejs_install_state.show = false;
                if self.nodejs_install_state.running {
                    ctx.request_repaint_after(std::time::Duration::from_millis(100));
                    return;
                }
                if self.nodejs_install_state.installed_version.is_some() {
                    self.settings_state.nodejs_version_builtin =
                        crate::core::settings::env_detect::detect_nodejs_builtin()
                            .unwrap_or_default();
                    self.nodejs_install_state.done_at = None;
                    self.one_click_flow.stage = OneClickStage::CheckingEnvironment;
                } else if self.nodejs_install_state.receiver.is_none() {
                    self.fail_one_click_start(
                        lang::t("one_click_node_install_failed", &self.settings_state.language)
                            .to_string(),
                        ctx,
                    );
                }
            }
            OneClickStage::CheckingTavern => {
                if self.selected_tavern_is_ready() {
                    self.one_click_flow.stage = OneClickStage::StartingTavern;
                } else {
                    self.version_manage_state.fetch_error = None;
                    self.version_manage_state.fetch_forbidden = false;
                    self.version_manage_state
                        .fetch_releases(false, &self.settings_state);
                    self.one_click_flow.stage = OneClickStage::FetchingTavern;
                }
            }
            OneClickStage::FetchingTavern => {
                self.version_manage_state.poll_release_messages();
                if self.version_manage_state.is_fetching_releases {
                    ctx.request_repaint_after(std::time::Duration::from_millis(100));
                    return;
                }
                let api_unavailable = self.version_manage_state.fetch_error.take().is_some()
                    || self.version_manage_state.fetch_forbidden
                    || self.version_manage_state.latest_release.is_none();
                let install_result = if api_unavailable {
                    self.version_manage_state
                        .start_release_branch_install(&mut self.settings_state)
                } else {
                    self.version_manage_state
                        .start_latest_install(&mut self.settings_state)
                };
                if let Err(error) = install_result {
                    self.fail_one_click_start(error, ctx);
                    return;
                }
                self.one_click_flow.stage = OneClickStage::InstallingTavern;
            }
            OneClickStage::InstallingTavern => {
                if let Some(outcome) = self
                    .version_manage_state
                    .poll_install_messages(&mut self.settings_state)
                {
                    self.version_manage_state.is_downloading = false;
                    match outcome {
                        Ok(()) => self.one_click_flow.stage = OneClickStage::StartingTavern,
                        Err(error) => self.fail_one_click_start(error, ctx),
                    }
                } else {
                    ctx.request_repaint_after(std::time::Duration::from_millis(100));
                }
            }
            OneClickStage::StartingTavern => {
                self.current_page = Page::Console;
                self.console_state.start(&self.settings_state.language);
                self.one_click_flow.stage = OneClickStage::WaitingForTavern;
            }
            OneClickStage::WaitingForTavern => match self.console_state.status {
                pages::console::ConsoleStatus::Running => {
                    self.one_click_flow = OneClickFlow::default();
                    self.current_page = Page::Console;
                }
                pages::console::ConsoleStatus::Stopped => {
                    let error = self
                        .console_state
                        .logs
                        .back()
                        .cloned()
                        .unwrap_or_else(|| {
                            lang::t(
                                "one_click_tavern_start_failed",
                                &self.settings_state.language,
                            )
                            .to_string()
                        });
                    self.fail_one_click_start(error, ctx);
                }
                _ => ctx.request_repaint_after(std::time::Duration::from_millis(100)),
            },
        }
    }

    fn one_click_capsule_state(&self) -> (&'static str, f32) {
        let (stage_key, progress) = match self.one_click_flow.stage {
            OneClickStage::Idle => ("one_click_stage_checking", 0.0),
            OneClickStage::CheckingEnvironment => ("one_click_stage_checking_env", 0.05),
            OneClickStage::SelectingGitMirror => ("one_click_stage_selecting_git", 0.10),
            OneClickStage::InstallingGit => {
                ("one_click_stage_installing_git", 0.10 + self.git_install_state.progress * 0.23)
            }
            OneClickStage::SelectingNodeMirror => ("one_click_stage_selecting_node", 0.35),
            OneClickStage::InstallingNode => (
                "one_click_stage_installing_node",
                0.35 + self.nodejs_install_state.progress * 0.23,
            ),
            OneClickStage::CheckingTavern => ("one_click_stage_checking_tavern", 0.60),
            OneClickStage::FetchingTavern => ("one_click_stage_fetching_tavern", 0.64),
            OneClickStage::InstallingTavern => (
                "one_click_stage_installing_tavern",
                0.66 + self.version_manage_state.download_progress * 0.28,
            ),
            OneClickStage::StartingTavern => ("one_click_stage_starting_tavern", 0.96),
            OneClickStage::WaitingForTavern => ("one_click_stage_waiting_tavern", 0.98),
        };
        if self.one_click_flow.cancel_requested {
            ("one_click_stage_cancelling", progress)
        } else {
            (stage_key, progress)
        }
    }

    fn render_one_click_capsule(&mut self, ctx: &egui::Context) {
        if !self.one_click_flow.is_active() {
            return;
        }

        let screen_size = ctx.content_rect().size();
        egui::Area::new(egui::Id::new("one_click_interaction_lock"))
            .order(egui::Order::Foreground)
            .fixed_pos(egui::Pos2::ZERO)
            .show(ctx, |ui| {
                let _ = ui.allocate_exact_size(screen_size, egui::Sense::click_and_drag());
            });

        let (stage_key, progress) = self.one_click_capsule_state();
        let mut cancel_clicked = false;
        egui::Area::new(egui::Id::new("one_click_progress_capsule"))
            .order(egui::Order::Tooltip)
            .anchor(egui::Align2::RIGHT_BOTTOM, egui::vec2(-18.0, -18.0))
            .show(ctx, |ui| {
                egui::Frame::popup(ui.style())
                    .corner_radius(18.0)
                    .inner_margin(egui::Margin::symmetric(16, 12))
                    .show(ui, |ui| {
                        ui.set_width(390.0);
                        ui.horizontal(|ui| {
                            ui.spinner();
                            ui.vertical(|ui| {
                                ui.label(
                                    egui::RichText::new(lang::t(
                                        "one_click_running",
                                        &self.settings_state.language,
                                    ))
                                    .strong()
                                    .size(14.0),
                                );
                                ui.label(
                                    egui::RichText::new(lang::t(
                                        stage_key,
                                        &self.settings_state.language,
                                    ))
                                    .color(ui.visuals().weak_text_color())
                                    .size(12.0),
                                );
                            });
                            ui.with_layout(
                                egui::Layout::right_to_left(egui::Align::Center),
                                |ui| {
                                    let button = egui::Button::new(format!(
                                        "{}  {}",
                                        egui_phosphor::regular::X,
                                        lang::t("cancel", &self.settings_state.language)
                                    ));
                                    if ui
                                        .add_enabled(
                                            !self.one_click_flow.cancel_requested,
                                            button,
                                        )
                                        .clicked()
                                    {
                                        cancel_clicked = true;
                                    }
                                },
                            );
                        });
                        ui.add_space(8.0);
                        ui.add(
                            egui::ProgressBar::new(progress.clamp(0.0, 1.0))
                                .desired_width(ui.available_width()),
                        );
                    });
            });

        if cancel_clicked {
            self.one_click_flow.cancel_requested = true;
            self.one_click_flow.cancel_dispatched = false;
        }
        ctx.request_repaint_after(std::time::Duration::from_millis(100));
    }

    fn current_tavern_instance_path(&self) -> Option<std::path::PathBuf> {
        self.settings_state.sillytavern.as_ref().and_then(|instance| {
            match instance.instance_type.as_str() {
                "builtin" => Some(crate::utils::app_paths().sillytavern_dir()),
                "local" => instance.path.as_ref().map(std::path::PathBuf::from),
                _ => None,
            }
        })
    }

    fn start_dependency_repair_task(&mut self, package: String) -> Result<(), String> {
        let working_dir = self
            .current_tavern_instance_path()
            .ok_or_else(|| "未选择酒馆实例".to_string())?;
        if !working_dir.join("package.json").exists() {
            return Err("酒馆实例缺少 package.json".to_string());
        }

        let npm_path = match self.settings_state.env_mode {
            pages::settings::EnvSource::Builtin => crate::core::env::get_builtin_npm_path(),
            pages::settings::EnvSource::System => crate::core::env::get_system_cmd_path("npm"),
        }
        .ok_or_else(|| "当前环境模式下未找到 npm".to_string())?;

        let package_declared = std::fs::read_to_string(working_dir.join("package.json"))
            .ok()
            .and_then(|content| serde_json::from_str::<serde_json::Value>(&content).ok())
            .is_some_and(|json| {
                ["dependencies", "optionalDependencies"]
                    .iter()
                    .filter_map(|key| json.get(*key).and_then(|value| value.as_object()))
                    .any(|dependencies| dependencies.contains_key(&package))
            });
        let registry = crate::core::settings::pm2::npm_registry_url(
            &self.settings_state.npm_registry,
        );
        let env_mode = self.settings_state.env_mode;
        let package_for_thread = package.clone();
        let (tx, rx) = std::sync::mpsc::channel();

        self.console_state.add_log(&format!(
            "[系统] 正在修复缺失依赖: {}",
            package
        ));
        std::thread::spawn(move || {
            let is_script = npm_path
                .extension()
                .and_then(|extension| extension.to_str())
                .is_some_and(|extension| {
                    extension.eq_ignore_ascii_case("cmd")
                        || extension.eq_ignore_ascii_case("bat")
                });
            let mut command = if is_script {
                let mut command = std::process::Command::new("cmd");
                command.arg("/d").arg("/c").arg(&npm_path);
                command
            } else {
                std::process::Command::new(&npm_path)
            };
            crate::core::env::apply_no_window_to_command(&mut command);
            command.arg("install");
            if !package_declared {
                command.arg(&package_for_thread);
            }
            command
                .arg("--no-save")
                .arg("--package-lock=false")
                .arg("--omit=dev")
                .arg("--no-audit")
                .arg("--no-fund")
                .env("NODE_ENV", "production")
                .current_dir(&working_dir);
            if let Some(registry) = registry {
                command.arg("--registry").arg(registry);
            }
            if env_mode == pages::settings::EnvSource::Builtin {
                crate::core::env::apply_builtin_path_to_command(&mut command);
            }

            let result = command.output().map_err(|error| error.to_string()).and_then(|output| {
                if output.status.success() {
                    Ok(())
                } else {
                    let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
                    let stdout = String::from_utf8_lossy(&output.stdout).trim().to_string();
                    let detail = if stderr.is_empty() { stdout } else { stderr };
                    let detail = detail.chars().rev().take(2000).collect::<Vec<_>>();
                    let detail: String = detail.into_iter().rev().collect();
                    Err(if detail.is_empty() {
                        format!("npm 退出码: {}", output.status)
                    } else {
                        detail
                    })
                }
            });
            let _ = tx.send(result);
        });

        self.dependency_repair_task = Some(DependencyRepairTask {
            package,
            receiver: rx,
        });
        Ok(())
    }

    fn poll_dependency_repair(&mut self, ctx: &egui::Context) {
        if self.console_state.status == pages::console::ConsoleStatus::Stopped {
            if let Some(package) = self.dependency_repair_waiting.take() {
                if let Err(error) = self.start_dependency_repair_task(package.clone()) {
                    self.dependency_repair_error = Some((package, error));
                }
            }
        }

        let outcome = self.dependency_repair_task.as_ref().and_then(|task| {
            match task.receiver.try_recv() {
                Ok(result) => Some(result),
                Err(std::sync::mpsc::TryRecvError::Empty) => None,
                Err(std::sync::mpsc::TryRecvError::Disconnected) => {
                    Some(Err("依赖修复任务意外中断".to_string()))
                }
            }
        });
        if let Some(outcome) = outcome {
            let Some(task) = self.dependency_repair_task.take() else {
                return;
            };
            match outcome {
                Ok(()) => {
                    self.console_state.add_log(&format!(
                        "[系统] 依赖 {} 修复完成，正在重新启动酒馆...",
                        task.package
                    ));
                    self.console_state.start(&self.settings_state.language);
                    self.current_page = Page::Console;
                }
                Err(error) => {
                    self.console_state.add_log(&format!(
                        "[错误] 依赖 {} 修复失败: {}",
                        task.package, error
                    ));
                    self.dependency_repair_error = Some((task.package, error));
                }
            }
        }

        if self.dependency_repair_task.is_some()
            || self.dependency_repair_waiting.is_some()
        {
            ctx.request_repaint_after(std::time::Duration::from_millis(100));
        }
    }

    fn render_dependency_repair_dialogs(&mut self, ctx: &egui::Context) {
        if let Some(package) = self.missing_dependency_dialog.clone() {
            let mut repair = false;
            let mut dismiss = false;
            egui::Modal::new(egui::Id::new("missing_dependency_repair_prompt")).show(ctx, |ui| {
                ui.set_width(430.0);
                ui.heading(lang::t(
                    "dependency_missing_title",
                    &self.settings_state.language,
                ));
                ui.add_space(8.0);
                ui.label(
                    lang::t("dependency_missing_desc", &self.settings_state.language)
                        .replace("{package}", &package),
                );
                ui.add_space(14.0);
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if ui
                        .button(lang::t(
                            "dependency_repair_now",
                            &self.settings_state.language,
                        ))
                        .clicked()
                    {
                        repair = true;
                    }
                    if ui
                        .button(lang::t(
                            "dependency_repair_later",
                            &self.settings_state.language,
                        ))
                        .clicked()
                    {
                        dismiss = true;
                    }
                });
            });
            if repair {
                self.missing_dependency_dialog = None;
                if self.console_state.status == pages::console::ConsoleStatus::Stopped {
                    if let Err(error) = self.start_dependency_repair_task(package.clone()) {
                        self.dependency_repair_error = Some((package, error));
                    }
                } else {
                    self.console_state
                        .force_kill(&self.settings_state.language);
                    self.dependency_repair_waiting = Some(package);
                }
            } else if dismiss {
                self.missing_dependency_dialog = None;
            }
        }

        if let Some(package) = self
            .dependency_repair_task
            .as_ref()
            .map(|task| task.package.clone())
            .or_else(|| self.dependency_repair_waiting.clone())
        {
            egui::Modal::new(egui::Id::new("dependency_repair_progress")).show(ctx, |ui| {
                ui.set_width(400.0);
                ui.horizontal(|ui| {
                    ui.spinner();
                    ui.label(
                        lang::t("dependency_repairing", &self.settings_state.language)
                            .replace("{package}", &package),
                    );
                });
            });
        }

        if let Some((package, error)) = self.dependency_repair_error.clone() {
            let mut close = false;
            egui::Modal::new(egui::Id::new("dependency_repair_failed")).show(ctx, |ui| {
                ui.set_width(460.0);
                ui.heading(lang::t(
                    "dependency_repair_failed_title",
                    &self.settings_state.language,
                ));
                ui.label(
                    lang::t("dependency_repair_failed_desc", &self.settings_state.language)
                        .replace("{package}", &package),
                );
                ui.add_space(8.0);
                egui::ScrollArea::vertical()
                    .max_height(180.0)
                    .show(ui, |ui| {
                        ui.label(egui::RichText::new(error).monospace().size(12.0));
                    });
                ui.add_space(10.0);
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if ui.button(lang::t("close", &self.settings_state.language)).clicked() {
                        close = true;
                    }
                });
            });
            if close {
                self.dependency_repair_error = None;
            }
        }
    }

    /// 显示 Node.js 环境缺失提示，并为内置环境提供直达安装区域的入口。
    fn render_missing_env_dialog(&mut self, ctx: &egui::Context) {
        let Some(source) = self.missing_env_dialog else {
            return;
        };

        let is_builtin = source == pages::settings::EnvSource::Builtin;
        let desc_key = if is_builtin {
            "env_missing_builtin_desc"
        } else {
            "env_missing_system_desc"
        };
        let language = self.settings_state.language;
        let mut close = false;
        let mut go_install = false;

        egui::Modal::new(egui::Id::new("missing_nodejs_environment_modal")).show(ctx, |ui| {
            ui.set_width(420.0);
            ui.horizontal(|ui| {
                ui.label(
                    egui::RichText::new(egui_phosphor::regular::WARNING_CIRCLE)
                        .size(24.0)
                        .color(egui::Color32::from_rgb(235, 165, 45)),
                );
                ui.heading(lang::t("env_missing_title", &language));
            });
            ui.add_space(10.0);
            ui.label(egui::RichText::new(lang::t(desc_key, &language)).size(14.0));
            ui.add_space(16.0);

            ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                if is_builtin
                    && ui
                        .button(lang::t("env_go_install", &language))
                        .clicked()
                {
                    go_install = true;
                }
                if ui.button(lang::t("close", &language)).clicked() {
                    close = true;
                }
            });
        });

        if go_install {
            self.missing_env_dialog = None;
            self.current_page = Page::Settings;
            self.settings_tab = SettingsTab::General;
            self.focus_env_dependencies = true;
        } else if close {
            self.missing_env_dialog = None;
        }
    }
}

impl eframe::App for MyApp {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        // 由 egui 保留独立的明暗样式，并响应 Windows 的主题变更事件。
        ctx.set_theme(theme_preference(self.settings_state.theme));

        // 动态适配屏幕比例
        if let Some(monitor_size) = ctx.input(|i| i.viewport().monitor_size) {
            if self.last_monitor_size != Some(monitor_size) {
                let aspect_ratio = monitor_size.x / monitor_size.y;

                if (aspect_ratio - 4.0 / 3.0).abs() < 0.1 {
                    if self.last_monitor_size.is_none() {
                        ctx.send_viewport_cmd(egui::ViewportCommand::InnerSize(egui::vec2(800.0, 600.0)));
                    }
                    ctx.send_viewport_cmd(egui::ViewportCommand::MinInnerSize(egui::vec2(800.0, 600.0)));
                    ctx.send_viewport_cmd(egui::ViewportCommand::MaxInnerSize(egui::vec2(1200.0, 800.0)));
                } else {
                    if self.last_monitor_size.is_none() {
                        ctx.send_viewport_cmd(egui::ViewportCommand::InnerSize(egui::vec2(1280.0, 720.0)));
                    }
                    ctx.send_viewport_cmd(egui::ViewportCommand::MinInnerSize(egui::vec2(800.0, 600.0)));
                    ctx.send_viewport_cmd(egui::ViewportCommand::MaxInnerSize(egui::vec2(1280.0, 720.0)));
                }

                self.last_monitor_size = Some(monitor_size);
            }
        }

        let panel_width = match lang::effective_language(&self.settings_state.language) {
            pages::settings::Language::Chinese => 150.0,
            pages::settings::Language::English => 180.0,
            pages::settings::Language::System => unreachable!("effective_language already resolved System"),
        };

        // 左侧导航栏
        egui::SidePanel::left("left_panel")
            .resizable(false)
            .exact_width(panel_width)
            .show(ctx, |ui| {
                if self.one_click_flow.is_active() {
                    ui.disable();
                }
                ui.add_space(10.0);

                ui.vertical_centered(|ui| {
                    ui.heading(lang::t("app_title", &self.settings_state.language));
                    // BETA 角标（仅 beta 构建时渲染，放在中文标题和英文副标题之间，不遮挡 logo）
                    #[cfg(beta)]
                    {
                        egui::Frame::NONE
                            .fill(egui::Color32::from_rgb(255, 120, 50))
                            .corner_radius(3.0)
                            .inner_margin(egui::Margin::symmetric(6, 1))
                            .show(ui, |ui| {
                                ui.label(
                                    egui::RichText::new(lang::t("beta_tag", &self.settings_state.language))
                                        .size(10.0)
                                        .color(egui::Color32::WHITE)
                                        .strong(),
                                );
                            });
                    }
                    ui.heading(egui::RichText::new(lang::t("app_subtitle", &self.settings_state.language)).size(12.0));
                });

                // 当前版本信息
                let lang = &self.settings_state.language;
                if let Some(ref inst) = self.settings_state.sillytavern {
                    ui.add_space(8.0);
                    ui.vertical_centered(|ui| {
                        ui.label(
                            egui::RichText::new(lang::t("sidebar_current_version", lang))
                                .size(10.0)
                                .color(egui::Color32::GRAY),
                        );
                        ui.label(
                            egui::RichText::new(format!("{} {}", lang::t("sidebar_version_label", lang), &inst.version))
                                .size(13.0)
                                .strong(),
                        );
                        let is_online = inst.instance_type == "builtin";
                        let inst_type = if is_online {
                            lang::t("sidebar_instance_online", lang)
                        } else {
                            lang::t("sidebar_instance_local", lang)
                        };
                        ui.label(
                            egui::RichText::new(inst_type)
                                .size(10.0)
                                .color(if is_online { egui::Color32::from_rgb(100, 180, 255) } else { egui::Color32::from_rgb(100, 255, 150) }),
                        );
                    });
                    ui.add_space(14.0);
                }

                // 导航按钮
                let nav_button = |ui: &mut egui::Ui,
                                  current: &mut Page,
                                  target: Page,
                                  icon: &str,
                                  text: &str| {
                    let is_selected = *current == target;
                    let response = ui.add_sized(
                        [ui.available_width(), 32.0],
                        egui::Button::selectable(is_selected, ""),
                    );

                    let rect = response.rect;
                    let text_color = ui.style().interact_selectable(&response, is_selected).text_color();

                    let mut child_ui = ui.new_child(egui::UiBuilder::new().max_rect(rect).layout(*ui.layout()));
                    child_ui.horizontal(|ui| {
                        ui.add_space(8.0);
                        ui.add_sized(
                            [20.0, rect.height()],
                            |ui: &mut egui::Ui| {
                                ui.centered_and_justified(|ui| {
                                    ui.add(egui::Label::new(egui::RichText::new(icon).size(16.0).color(text_color)).selectable(false));
                                }).response
                            }
                        );
                        ui.add_space(4.0);
                        ui.with_layout(egui::Layout::left_to_right(egui::Align::Center), |ui| {
                            ui.add(egui::Label::new(egui::RichText::new(text).size(16.0).color(text_color)).selectable(false));
                        });
                    });

                    if response.clicked() {
                        *current = target;
                    }
                    response
                };

                nav_button(ui, &mut self.current_page, Page::OneClickStart, egui_phosphor::regular::ROCKET, lang::t("one_click_start", &self.settings_state.language));
                nav_button(ui, &mut self.current_page, Page::TavernConfig, egui_phosphor::regular::SLIDERS, lang::t("tavern_config", &self.settings_state.language));
                nav_button(ui, &mut self.current_page, Page::VersionManage, egui_phosphor::regular::GIT_BRANCH, lang::t("version_manage", &self.settings_state.language));
                nav_button(ui, &mut self.current_page, Page::ExtensionManage, egui_phosphor::regular::PUZZLE_PIECE, lang::t("extension_manage", &self.settings_state.language));
                nav_button(ui, &mut self.current_page, Page::ResourceManage, egui_phosphor::regular::FOLDER, lang::t("resource_manage", &self.settings_state.language));

                ui.with_layout(egui::Layout::bottom_up(egui::Align::Min), |ui| {
                    ui.add_space(10.0);
                    let button_height = 32.0;

                    // 设置按钮
                    let (rect, _) = ui.allocate_exact_size(
                        egui::vec2(ui.available_width(), button_height),
                        egui::Sense::hover(),
                    );
                    let mut child_ui = ui.new_child(egui::UiBuilder::new().max_rect(rect).layout(egui::Layout::top_down(egui::Align::Min)));
                    nav_button(&mut child_ui, &mut self.current_page, Page::Settings, egui_phosphor::regular::GEAR, lang::t("software_settings", &self.settings_state.language));

                    // 控制台按钮
                    ui.add_space(2.0);
                    let (rect2, _) = ui.allocate_exact_size(
                        egui::vec2(ui.available_width(), button_height),
                        egui::Sense::hover(),
                    );
                    let mut child_ui2 = ui.new_child(egui::UiBuilder::new().max_rect(rect2).layout(egui::Layout::top_down(egui::Align::Min)));
                    nav_button(&mut child_ui2, &mut self.current_page, Page::Console, egui_phosphor::regular::TERMINAL_WINDOW, lang::t("console", &self.settings_state.language));
                });
            });

        // 右侧页面区域
        let mut old_state = self.settings_state.clone();

        // 轮询 Github 节点加载消息
        {
            let mut clear_rx = false;
            if let Some(ref rx) = self.github_node_rx {
                while let Ok(msg) = rx.try_recv() {
                    use crate::core::settings::github_proxy::NodeLoadMsg;
                    match msg {
                        NodeLoadMsg::Nodes(entries) => {
                            // 自动选择首选节点（如果当前未选中）
                            if self.settings_state.github_proxy_url.is_empty()
                                || !entries
                                    .iter()
                                    .any(|e| e.url == self.settings_state.github_proxy_url)
                            {
                                if let Some(first) = entries.first() {
                                    self.settings_state.github_proxy_url = first.url.clone();
                                }
                            }
                            self.github_node_state =
                                crate::core::settings::github_proxy::NodeLoadState::Done(entries);
                        }
                        NodeLoadMsg::LatencyUpdate => {
                            ctx.request_repaint();
                        }
                        NodeLoadMsg::Done => {
                            clear_rx = true;
                        }
                    }
                }
            }
            if clear_rx {
                self.github_node_rx = None;
            }
        }

        // 处理更新检测触发
        if self.settings_state.check_update_trigger {
            self.settings_state.check_update_trigger = false;
            self.settings_state.update_checking = true;
            self.notification_stack.push(
                lang::t("check_update", &self.settings_state.language).to_string(),
                lang::t("checking_update", &self.settings_state.language).to_string(),
                ctx,
            );
            self.updater_rx = Some(crate::core::updater::check_update_manual());
            self.updater_operation = Some(UpdaterOperation::ManualCheck);
        }

        // 处理下载安装触发
        if let Some(endpoint) = self.settings_state.do_update_trigger.take() {
            self.notification_stack.push(
                lang::t("update_now", &self.settings_state.language).to_string(),
                lang::t("updating", &self.settings_state.language).to_string(),
                ctx,
            );
            self.updater_rx = Some(crate::core::updater::do_install(endpoint));
            self.updater_operation = Some(UpdaterOperation::Install);
        }

        // 轮询自动更新状态
        {
            let mut clear_rx = false;
            if let Some(ref rx) = self.updater_rx {
                while let Ok(status) = rx.try_recv() {
                    use crate::core::updater::UpdateStatus;
                    match status {
                        UpdateStatus::Checking => {}
                        UpdateStatus::UpToDate => {
                            if self
                                .updater_operation
                                .is_some_and(UpdaterOperation::reports_non_actionable_result)
                            {
                                self.notification_stack.push(
                                    lang::t("check_update", &self.settings_state.language).to_string(),
                                    lang::t("update_up_to_date", &self.settings_state.language).to_string(),
                                    ctx,
                                );
                            }
                            self.settings_state.update_checking = false;
                            self.settings_state.update_downloading = false;
                            clear_rx = true;
                        }
                        UpdateStatus::UpdateAvailable { version, notes, endpoint } => {
                            self.settings_state.update_confirm_version = version;
                            self.settings_state.update_confirm_notes = notes;
                            self.settings_state.update_confirm_endpoint = endpoint;
                            self.settings_state.update_confirm_open = true;
                            self.settings_state.update_checking = false;
                            self.settings_state.update_downloading = false;
                            clear_rx = true;
                        }
                        UpdateStatus::Downloading => {}
                        UpdateStatus::Installed => {
                            self.notification_stack.push(
                                lang::t("check_update", &self.settings_state.language).to_string(),
                                lang::t("update_installed", &self.settings_state.language).to_string(),
                                ctx,
                            );
                            self.settings_state.update_checking = false;
                            self.settings_state.update_downloading = false;
                            clear_rx = true;
                        }
                        UpdateStatus::Error(e) => {
                            if self
                                .updater_operation
                                .is_some_and(UpdaterOperation::reports_non_actionable_result)
                            {
                                self.notification_stack.push(
                                    lang::t("check_update", &self.settings_state.language).to_string(),
                                    lang::t("update_failed", &self.settings_state.language)
                                        .replace("{error}", &e),
                                    ctx,
                                );
                            }
                            self.settings_state.update_checking = false;
                            self.settings_state.update_downloading = false;
                            clear_rx = true;
                        }
                    }
                }
            }
            if clear_rx {
                self.updater_rx = None;
                self.updater_operation = None;
            }
        }

        if self.updater_rx.is_some() {
            ctx.request_repaint_after(std::time::Duration::from_millis(
                BACKGROUND_TASK_REPAINT_INTERVAL_MS,
            ));
        }

        // 处理刷新节点请求
        if self.on_refresh_nodes {
            self.on_refresh_nodes = false;
            let (tx, rx) = std::sync::mpsc::channel();
            self.github_node_rx = Some(rx);
            self.github_node_state =
                crate::core::settings::github_proxy::NodeLoadState::Loading;
            crate::core::settings::github_proxy::start_fetch_and_test(tx, false);
        }

        // 节点加载中或测试进行中时持续重绘
        if matches!(
            self.github_node_state,
            crate::core::settings::github_proxy::NodeLoadState::Loading
        ) || crate::core::network::is_github_multi_test_in_progress()
        {
            ctx.request_repaint_after(std::time::Duration::from_millis(
                BACKGROUND_TASK_REPAINT_INTERVAL_MS,
            ));
        }

        // 每帧同步酒馆配置页的数据模式 & 实例 & 全局路径 & 代理设置
        {
            use crate::core::settings::tavern::{ConfigMode, InstanceInfo};
            self.tavern_config_ui.config_mode = match self.settings_state.data_mode {
                crate::pages::settings::TavernDataMode::Current => ConfigMode::Current,
                crate::pages::settings::TavernDataMode::Global => ConfigMode::Global,
            };
            self.tavern_config_ui.global_data_path = self.settings_state.global_data_path.clone();
            self.tavern_config_ui.instance = self.settings_state.sillytavern.as_ref().map(|i| InstanceInfo {
                instance_type: i.instance_type.clone(),
                path: i.path.clone(),
            });
            self.tavern_config_ui.proxy_enabled = self.settings_state.github_proxy_enabled;
            self.tavern_config_ui.proxy_url = self.settings_state.github_proxy_url.clone();
            self.tavern_config_ui.server_mode_enabled = self.settings_state.server_mode_enabled;
            self.tavern_config_ui.server_service_mode = match self.settings_state.server_service_mode {
                crate::pages::settings::ServerServiceMode::Lan => "Lan".to_string(),
                crate::pages::settings::ServerServiceMode::Internet => "Internet".to_string(),
            };
        }

        // 同步控制台所需配置（实例路径 + 类型/版本 + 数据模式 + 代理）
        {
            let inst = self.settings_state.sillytavern.as_ref();
            let instance_path = inst.map(|i| {
                match i.instance_type.as_str() {
                    "builtin" => crate::utils::app_paths().sillytavern_dir().to_string_lossy().to_string(),
                    "local" => i.path.clone().unwrap_or_default(),
                    _ => String::new(),
                }
            }).unwrap_or_default();
            let instance_type = inst.map(|i| i.instance_type.clone()).unwrap_or_default();
            let instance_version = inst.map(|i| i.version.clone()).unwrap_or_default();

            let github_proxy_url = if self.settings_state.github_proxy_enabled
                && !self.settings_state.github_proxy_url.is_empty()
            {
                Some(self.settings_state.github_proxy_url.clone())
            } else {
                None
            };

            self.console_state.sync_with_settings(
                instance_path,
                instance_type,
                instance_version,
                &self.settings_state.data_mode,
                &self.settings_state.proxy_type,
                &self.settings_state.custom_proxy,
                github_proxy_url,
                self.settings_state.show_startup_command,
                self.settings_state.auto_stop_tavern_on_webview_close,
                self.settings_state.start_mode == StartMode::Desktop,
                self.settings_state.allow_tavern_background,
                self.settings_state.server_mode_enabled,
                self.settings_state.server_service_mode.clone(),
                self.settings_state.global_data_path.clone(),
                self.settings_state.env_mode,
            );
        }

        // 每帧轮询酒馆进程状态
        self.console_state.poll(&self.settings_state.language);
        self.advance_one_click_start(ctx);
        self.drain_desktop_webview_events(ctx);
        self.sync_desktop_webview(ctx);
        if self.console_state.status == pages::console::ConsoleStatus::Running
            || self.console_state.status == pages::console::ConsoleStatus::Starting
            || self.console_state.status == pages::console::ConsoleStatus::Stopping
        {
            ctx.request_repaint_after(std::time::Duration::from_millis(
                ACTIVE_CONSOLE_REPAINT_INTERVAL_MS,
            ));
        }

        // 在线下载中持续重绘
        if self.version_manage_state.is_downloading {
            ctx.request_repaint_after(std::time::Duration::from_millis(
                BACKGROUND_TASK_REPAINT_INTERVAL_MS,
            ));
        }

        // 酒馆配置下载中持续重绘
        if self.tavern_config_ui.gen_config_status.is_downloading() {
            ctx.request_repaint_after(std::time::Duration::from_millis(
                BACKGROUND_TASK_REPAINT_INTERVAL_MS,
            ));
        }

        // 异步全局检测：实例路径是否被手动删除（后台线程，每 5s 一次，不卡 UI）
        {
            // 轮询上次检查结果
            if let Some(rx) = &self.path_check_rx {
                if let Ok(result) = rx.try_recv() {
                    if result.should_clear_current {
                        self.settings_state.sillytavern = None;
                        self.settings_state.save();
                    }
                    if result.builtin_deleted {
                        self.version_manage_state.online_installed_version = None;
                    }
                    if !result.dead_instance_indices.is_empty() {
                        for idx in result.dead_instance_indices.iter().rev() {
                            self.version_manage_state.local_instances.remove(*idx);
                        }
                        crate::pages::version_manage::save_local_instances(&self.version_manage_state.local_instances);
                    }
                    self.path_check_rx = None;
                }
            }

            // 启动时检查一次实例路径（不重复检测，避免下载中误判）
            let should_check = self.last_path_check.is_none();
            if should_check && self.path_check_rx.is_none() {
                self.last_path_check = Some(std::time::Instant::now());
                let current = self.settings_state.sillytavern.clone();
                let instance_paths: Vec<String> = self.version_manage_state
                    .local_instances
                    .iter()
                    .map(|i| i.path.clone())
                    .collect();
                let (tx, rx) = std::sync::mpsc::channel();
                self.path_check_rx = Some(rx);
                std::thread::spawn(move || {
                    let mut should_clear_current = false;
                    let mut dead_indices = Vec::new();
                    let mut builtin_deleted = false;

                    // 检查 builtin 实例（使用写死的路径）
                    let builtin_path = crate::utils::app_paths().sillytavern_dir();
                    if !builtin_path.join("package.json").exists() {
                        builtin_deleted = true;
                    }

                    // 检查当前选中实例
                    if let Some(ref curr) = current {
                        let exists = match curr.instance_type.as_str() {
                            "builtin" => !builtin_deleted,
                            "local" => {
                                if let Some(ref p) = curr.path {
                                    !p.is_empty() && std::path::PathBuf::from(p).join("package.json").exists()
                                } else {
                                    false
                                }
                            }
                            _ => true,
                        };
                        if !exists {
                            should_clear_current = true;
                        }
                    }

                    // 检查本地实例列表
                    for (idx, path) in instance_paths.iter().enumerate() {
                        if !std::path::PathBuf::from(path).join("package.json").exists() {
                            dead_indices.push(idx);
                        }
                    }

                    let _ = tx.send(PathCheckResult {
                        should_clear_current,
                        dead_instance_indices: dead_indices,
                        builtin_deleted,
                    });
                });
            }
        }

        egui::CentralPanel::default().show(ctx, |ui| {
            if self.one_click_flow.is_active() {
                ui.disable();
            }
            match self.current_page {
                Page::OneClickStart => {
                    let version = self
                        .settings_state
                        .sillytavern
                        .as_ref()
                        .map(|inst| inst.version.as_str());
                    let start_mode_label = match self.settings_state.start_mode {
                        pages::settings::StartMode::Normal => lang::t("normal_mode", &self.settings_state.language),
                        pages::settings::StartMode::Desktop => lang::t("desktop_mode", &self.settings_state.language),
                    };
                    pages::home::render(
                        ui,
                        &mut self.current_page,
                        &mut self.console_state,
                        &self.settings_state.language,
                        version,
                        start_mode_label,
                    );
                }
                Page::TavernConfig => {
                    let current_key = self.tavern_config_ui.config_key();
                    if current_key != self.tavern_config_ui.last_config_key {
                        self.tavern_config_ui.refresh();
                    }
                    pages::tavern_config::render(ui, &mut self.tavern_config_ui, &self.settings_state.language, &mut self.current_page, self.settings_state.start_mode == StartMode::Desktop, self.settings_state.server_mode_enabled);
                }
                Page::VersionManage => {
                    ui.heading(lang::t("version_manage", &self.settings_state.language));
                    ui.separator();
                    pages::version_manage::render(ui, &mut self.version_manage_state, &mut self.settings_state);
                }
                Page::ExtensionManage => {
                    let inst = self.settings_state.sillytavern.as_ref();
                    let instance_path = inst.map(|i| {
                        match i.instance_type.as_str() {
                            "builtin" => crate::utils::app_paths().sillytavern_dir().to_string_lossy().to_string(),
                            "local" => i.path.clone().unwrap_or_default(),
                            _ => String::new(),
                        }
                    });

                    // 仅在有选中实例且尚未加载时触发加载
                    let has_instance = instance_path.as_ref().map_or(false, |p| !p.is_empty());
                    if has_instance
                        && !self.extension_manage_state.has_loaded
                        && !self.extension_manage_state.is_loading
                    {
                        self.extension_manage_state.load_extensions(instance_path.as_deref());
                    }

                    // 同步 GitHub 加速设置
                    self.extension_manage_state.github_proxy_enabled = self.settings_state.github_proxy_enabled;
                    self.extension_manage_state.github_proxy_url = self.settings_state.github_proxy_url.clone();

                    pages::extensions::render(ui, &mut self.extension_manage_state, &self.settings_state.language, instance_path.as_deref());
                }
                Page::ResourceManage => {
                    let inst = self.settings_state.sillytavern.as_ref();
                    let instance_path = inst.map(|i| {
                        match i.instance_type.as_str() {
                            "builtin" => crate::utils::app_paths().sillytavern_dir().to_string_lossy().to_string(),
                            "local" => i.path.clone().unwrap_or_default(),
                            _ => String::new(),
                        }
                    }).unwrap_or_default();

                    self.resource_manage_state.sync_context(
                        instance_path,
                        self.settings_state.data_mode.clone(),
                        self.settings_state.global_data_path.clone(),
                    );

                    // 如果角色卡列表为空且没在加载，触发自动加载
                    if self.resource_manage_state.characters.is_empty() && !self.resource_manage_state.is_loading {
                        self.resource_manage_state.characters_loaded = false;
                    }
                    // 如果世界书列表为空且没在加载，触发自动加载
                    if self.resource_manage_state.world_books.is_empty() && !self.resource_manage_state.is_loading_wb {
                        self.resource_manage_state.world_books_loaded = false;
                    }
                    // 如果聊天记录列表为空且没在加载，触发自动加载
                    if self.resource_manage_state.chat_groups.is_empty() && !self.resource_manage_state.is_loading_chats {
                        self.resource_manage_state.chats_loaded = false;
                    }
                    // 如果预设列表为空且没在加载，触发自动加载
                    if self.resource_manage_state.presets.is_empty() && !self.resource_manage_state.is_loading_presets {
                        self.resource_manage_state.presets_loaded = false;
                    }

                    pages::resource_manage::render(ui, &mut self.resource_manage_state, &self.settings_state.language);
                }
                Page::Console => {
                    pages::console::render(ui, &mut self.console_state, &self.settings_state.language);
                }
                Page::Settings => {
                    ui.heading(lang::t("software_settings", &self.settings_state.language));
                    ui.separator();

                    // 代理开关已开启 + 节点列表未加载 → 自动加载
                    if self.settings_state.github_proxy_enabled
                        && matches!(
                            self.github_node_state,
                            crate::core::settings::github_proxy::NodeLoadState::Done(ref entries) if entries.is_empty()
                        )
                    {
                        self.on_refresh_nodes = true;
                    }

                    pages::settings::render(
                        ui,
                        &mut self.settings_tab,
                        &mut self.settings_state,
                        &mut self.git_install_state,
                        &mut self.nodejs_install_state,
                        &mut self.caddy_install_state,
                        &mut self.pm2_install_state,
                        &mut self.webview2_install_state,
                        &self.github_node_state,
                        &mut self.on_refresh_nodes,
                        &mut self.git_node_select,
                        &mut self.nodejs_node_select,
                        &mut self.caddy_node_select,
                        &mut self.focus_env_dependencies,
                    );
                }
            }
        });

        if self.console_state.take_one_click_start_request() {
            self.handle_user_start_request();
        }
        if let Some(source) = self.console_state.take_missing_env_prompt() {
            match source {
                pages::settings::EnvSource::Builtin => {
                    self.settings_state.nodejs_version_builtin.clear();
                }
                pages::settings::EnvSource::System => {
                    self.settings_state.nodejs_version.clear();
                }
            }
            self.missing_env_dialog = Some(source);
        }
        self.render_missing_env_dialog(ctx);
        if let Some(package) = self.console_state.take_missing_dependency_prompt() {
            if self.missing_dependency_dialog.is_none()
                && self.dependency_repair_task.is_none()
                && self.dependency_repair_waiting.is_none()
            {
                self.missing_dependency_dialog = Some(package);
            }
        }
        self.poll_dependency_repair(ctx);
        self.render_dependency_repair_dialogs(ctx);

        // 同步 transient 更新字段，避免误触发"设置已保存"
        old_state.update_confirm_open = self.settings_state.update_confirm_open;
        old_state.update_confirm_version.clone_from(&self.settings_state.update_confirm_version);
        old_state.update_confirm_notes.clone_from(&self.settings_state.update_confirm_notes);
        old_state.update_confirm_endpoint.clone_from(&self.settings_state.update_confirm_endpoint);
        old_state.update_downloading = self.settings_state.update_downloading;
        old_state.update_checking = self.settings_state.update_checking;
        old_state.check_update_trigger = self.settings_state.check_update_trigger;
        old_state.do_update_trigger.clone_from(&self.settings_state.do_update_trigger);
        old_state.has_seen_scan_warning = self.settings_state.has_seen_scan_warning;
        // 环境版本是运行时检测缓存，不属于用户设置；缺失检查刷新缓存时不应提示“设置已保存”。
        old_state.nodejs_version.clone_from(&self.settings_state.nodejs_version);
        old_state
            .nodejs_version_builtin
            .clone_from(&self.settings_state.nodejs_version_builtin);
        old_state.git_version.clone_from(&self.settings_state.git_version);
        old_state
            .git_version_builtin
            .clone_from(&self.settings_state.git_version_builtin);
        if self.one_click_flow.is_active() {
            old_state.env_mode = self.settings_state.env_mode;
        }

        // 设置变化时保存
        if old_state != self.settings_state {
            self.settings_state.save();
            let toast_key = if self.settings_state.restore_defaults_triggered {
                self.settings_state.restore_defaults_triggered = false;
                "restore_defaults_done"
            } else {
                "settings_saved"
            };
            let toast_text = lang::t(toast_key, &self.settings_state.language).to_string();
            self.toast_stack.push(toast_text, ctx);
        }

        // 渲染 toast 堆叠
        self.toast_stack.render(ctx);

        // 连接通知（新设备访问酒馆）：从 ConsoleState 取出待显示通知
        {
            let pending: Vec<String> =
                self.console_state.pending_connection_notifications.drain(..).collect();
            for msg in pending {
                self.notification_stack.push("新设备访问".into(), msg, ctx);
            }
        }
        // 渲染通知堆叠（右下角，从右到左滑入）
        self.notification_stack.render(ctx);

        // 访问酒馆弹窗（服务器模式）
        crate::pages::access_tavern_popup::render_access_tavern_popup(ctx, &self.settings_state.language);
        self.render_one_click_capsule(ctx);

        // 文件夹选择器处理
        if self.settings_state.trigger_folder_picker {
            self.settings_state.trigger_folder_picker = false;
            let lang = self.settings_state.language;
            let (tx, rx) = std::sync::mpsc::channel();
            self.folder_picker_rx = Some(rx);
            std::thread::spawn(move || {
                let title = lang::t("dialog_select_folder", &lang);
                let path = rfd::FileDialog::new().set_title(title).pick_folder();
                let _ = tx.send(path);
            });
        }
        if let Some(rx) = &self.folder_picker_rx {
            if let Ok(result) = rx.try_recv() {
                if let Some(path) = result {
                    self.settings_state.global_data_path =
                        Some(path.to_string_lossy().to_string());
                }
                self.folder_picker_rx = None;
            }
        }

        // 导出路径选择器处理
        if self.settings_state.trigger_export_path_picker {
            self.settings_state.trigger_export_path_picker = false;
            let lang = self.settings_state.language;
            let (tx, rx) = std::sync::mpsc::channel();
            self.export_path_picker_rx = Some(rx);
            std::thread::spawn(move || {
                let title = lang::t("dialog_select_export_folder", &lang);
                let path = rfd::FileDialog::new().set_title(title).pick_folder();
                let _ = tx.send(path);
            });
        }
        if let Some(rx) = &self.export_path_picker_rx {
            if let Ok(result) = rx.try_recv() {
                if let Some(path) = result {
                    self.settings_state.tavern_export_path =
                        path.to_string_lossy().to_string();
                }
                self.export_path_picker_rx = None;
            }
        }

        // 关闭时保存窗口位置
        if ctx.input(|i| i.viewport().close_requested()) {
            if self.one_click_flow.is_active() {
                ctx.send_viewport_cmd(egui::ViewportCommand::CancelClose);
                self.one_click_flow.cancel_requested = true;
                self.one_click_flow.cancel_dispatched = false;
            } else if self.settings_state.remember_window_pos {
                if let Some(pos) = ctx.input(|i| i.viewport().inner_rect).map(|r| r.min) {
                    let pos_array = [pos.x, pos.y];
                    if self.settings_state.window_position != Some(pos_array) {
                        self.settings_state.window_position = Some(pos_array);
                        self.settings_state.save();
                    }
                }
            }
        }
    }
}

#[cfg(test)]
mod one_click_flow_tests {
    use super::*;

    #[test]
    fn complete_system_environment_is_accepted() {
        assert!(system_environment_error(true, true, true).is_none());
    }

    #[test]
    fn theme_settings_map_to_egui_theme_preferences() {
        assert_eq!(
            theme_preference(Theme::System),
            egui::ThemePreference::System
        );
        assert_eq!(theme_preference(Theme::Light), egui::ThemePreference::Light);
        assert_eq!(theme_preference(Theme::Dark), egui::ThemePreference::Dark);
    }

    #[test]
    fn automatic_update_checks_only_report_actionable_results() {
        assert!(!UpdaterOperation::AutomaticCheck.reports_non_actionable_result());
        assert!(UpdaterOperation::ManualCheck.reports_non_actionable_result());
        assert!(UpdaterOperation::Install.reports_non_actionable_result());
    }

    #[test]
    fn automation_is_only_used_for_first_launch_with_missing_environment() {
        assert!(should_use_first_launch_automation(true, false));
        assert!(!should_use_first_launch_automation(true, true));
        assert!(!should_use_first_launch_automation(false, false));
    }

    #[test]
    fn first_launch_automation_eligibility_is_not_persisted() {
        let settings = pages::settings::SettingsState::default();
        let json = serde_json::to_value(settings).expect("settings should serialize");
        assert!(json.get("first_launch_auto_setup_available").is_none());
    }

    #[test]
    fn incomplete_system_environment_does_not_fall_back_to_builtin() {
        assert_eq!(
            system_environment_error(false, true, true),
            Some("one_click_system_git_missing")
        );
        assert_eq!(
            system_environment_error(true, false, false),
            Some("one_click_system_node_missing")
        );
    }

    #[test]
    fn missing_builtin_components_are_installed_in_dependency_order() {
        assert!(matches!(
            next_environment_stage(false, false),
            OneClickStage::SelectingGitMirror
        ));
        assert!(matches!(
            next_environment_stage(true, false),
            OneClickStage::SelectingNodeMirror
        ));
        assert!(matches!(
            next_environment_stage(true, true),
            OneClickStage::CheckingTavern
        ));
    }

    #[test]
    fn cancelling_an_environment_task_sets_its_shared_token() {
        let mut task = pages::settings::InstallTaskState::new();
        task.running = true;
        task.show = true;
        task.cancel();

        assert!(!task.show);
        assert!(task
            .cancel_flag
            .load(std::sync::atomic::Ordering::Relaxed));
    }
}
