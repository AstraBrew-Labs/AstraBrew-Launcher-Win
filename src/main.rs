// objc 0.2 宏内使用旧式 `cargo-clippy` cfg，Rust 2024 默认报 warning
#![allow(unexpected_cfgs)]

use eframe::egui;
use egui::{FontData, FontDefinitions, FontFamily};

/// 设置 macOS 进程显示名称（Dock 栏、菜单栏、Cmd+Tab 切换器）
///
/// 直接运行二进制时，macOS 默认显示进程名（即 `Cargo.toml` 中的 `package.name`）。
/// 通过 `NSProcessInfo.setProcessName` 覆盖为正确的中/英文显示名称。
#[cfg(target_os = "macos")]
fn set_macos_process_name(lang: &pages::settings::Language) {
    use objc::{class, msg_send, sel};
    #[allow(unused_imports)]
    use objc::sel_impl;

    let effective = lang::effective_language(lang);
    let name = match effective {
        pages::settings::Language::Chinese => "星酿启动器",
        pages::settings::Language::English => "AstraBrew Launcher",
        pages::settings::Language::System => "AstraBrew Launcher", // 不应到达，安全回退
    };

    let c_name = std::ffi::CString::new(name).expect("CString::new failed");
    unsafe {
        let ns_string: *mut objc::runtime::Object = msg_send![class!(NSString), alloc];
        let ns_string: *mut objc::runtime::Object =
            msg_send![ns_string, initWithUTF8String: c_name.as_ptr()];
        let process_info: *mut objc::runtime::Object = msg_send![class!(NSProcessInfo), processInfo];
        let _: () = msg_send![process_info, setProcessName: ns_string];
    }
}

#[cfg(not(target_os = "macos"))]
fn set_macos_process_name(_lang: &pages::settings::Language) {}

/// 检测是否运行在 .app bundle 内（即已打包的 macOS 应用）
#[cfg(target_os = "macos")]
fn is_running_in_bundle() -> bool {
    std::env::current_exe()
        .map(|p| {
            p.to_string_lossy()
                .contains(".app/Contents/MacOS/")
        })
        .unwrap_or(false)
}

#[cfg(not(target_os = "macos"))]
fn is_running_in_bundle() -> bool {
    false
}

fn main() -> eframe::Result {
    let settings = pages::settings::SettingsState::load();

    // 必须在创建窗口之前设置，否则 Dock/菜单栏会先显示进程名再切换
    set_macos_process_name(&settings.language);

    // eframe 0.33 在不提供图标时会用紫色 e 默认图标覆盖 Dock 图标。
    // - .app bundle：传 IconData::default() 让 eframe 跳过覆盖，系统使用 bundle 内 icon.icns
    // - cargo run：加载预处理的 icon_eframe.png（从 ICNS 提取 + macOS 标准圆角/留白）
    let icon = if is_running_in_bundle() {
        egui::IconData::default()
    } else {
        eframe::icon_data::from_png_bytes(include_bytes!("../icons/icon_eframe.png"))
            .expect("Failed to load icon_eframe.png")
    };

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

    eframe::run_native(
        "星酿启动器 - AstraBrew Launcher",
        options,
        Box::new(|cc| {
            setup_fonts(&cc.egui_ctx);
            // 安装图像加载器，支持 PNG/JPEG/GIF 等格式
            egui_extras::install_image_loaders(&cc.egui_ctx);
            Ok(Box::new(MyApp::new(settings)))
        }),
    )
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

use core::desktop_webview::DesktopWebView;
use pages::console::ConsoleState;
use pages::settings::{SettingsState, SettingsTab, StartMode, Theme};
use pages::resource_manage::ResourceManageState;
use pages::tavern_config::TavernConfigUI;

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
    // brew 任务状态
    git_install_state: pages::settings::BrewTaskState,
    nodejs_install_state: pages::settings::BrewTaskState,
    caddy_install_state: pages::settings::BrewTaskState,
    pm2_install_state: pages::settings::BrewTaskState,

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
    // 桌面模式 WebView
    desktop_webview: Option<DesktopWebView>,
    // 自动更新检测通道
    updater_rx: Option<std::sync::mpsc::Receiver<crate::core::updater::UpdateStatus>>,
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
        // 检测环境依赖版本
        settings_state.detect_all_env();

        // 同步自启动状态：以系统实际注册状态为准（用户可能在系统设置中手动关闭）
        settings_state.auto_start = crate::core::auto_launch::is_auto_launch_enabled();
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
            git_install_state: pages::settings::BrewTaskState::new(),
            nodejs_install_state: pages::settings::BrewTaskState::new(),
            caddy_install_state: pages::settings::BrewTaskState::new(),
            pm2_install_state: pages::settings::BrewTaskState::new(),
            github_node_rx: None,
            github_node_state: crate::core::settings::github_proxy::NodeLoadState::Done(vec![]),
            on_refresh_nodes: false,
            folder_picker_rx: None,
            export_path_picker_rx: None,
            path_check_rx: None,
            last_path_check: None,
            desktop_webview: None,
            updater_rx: None,
        }
    }
}

impl eframe::App for MyApp {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        // 应用主题
        let visuals = match self.settings_state.theme {
            Theme::System => match ctx.system_theme() {
                Some(egui::Theme::Dark) => egui::Visuals::dark(),
                Some(egui::Theme::Light) => egui::Visuals::light(),
                None => egui::Visuals::dark(), // 无法检测时默认深色
            },
            Theme::Light => egui::Visuals::light(),
            Theme::Dark => egui::Visuals::dark(),
        };
        ctx.set_visuals(visuals);

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
                ui.add_space(10.0);

                ui.vertical_centered(|ui| {
                    ui.heading(lang::t("app_title", &self.settings_state.language));
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

        // 轮询 brew 任务日志
        if let Some(new_ver) = self.git_install_state.poll() {
            self.settings_state.git_version = Some(new_ver);
        }
        if let Some(new_ver) = self.nodejs_install_state.poll() {
            self.settings_state.nodejs_version = new_ver;
        }
        if let Some(new_ver) = self.caddy_install_state.poll() {
            self.settings_state.caddy_version = Some(new_ver);
        }
        if let Some(new_ver) = self.pm2_install_state.poll() {
            self.settings_state.pm2_version = Some(new_ver);
        }
        if self.git_install_state.running
            || self.nodejs_install_state.running
            || self.caddy_install_state.running
            || self.pm2_install_state.running
            || self.git_install_state.done_at.is_some()
            || self.nodejs_install_state.done_at.is_some()
            || self.caddy_install_state.done_at.is_some()
            || self.pm2_install_state.done_at.is_some()
        {
            ctx.request_repaint();
        }

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
        }

        // 处理下载安装触发
        if let Some(endpoint) = self.settings_state.do_update_trigger.take() {
            self.notification_stack.push(
                lang::t("update_now", &self.settings_state.language).to_string(),
                lang::t("updating", &self.settings_state.language).to_string(),
                ctx,
            );
            self.updater_rx = Some(crate::core::updater::do_install(endpoint));
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
                            self.notification_stack.push(
                                lang::t("check_update", &self.settings_state.language).to_string(),
                                lang::t("update_up_to_date", &self.settings_state.language).to_string(),
                                ctx,
                            );
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
                            self.notification_stack.push(
                                lang::t("check_update", &self.settings_state.language).to_string(),
                                lang::t("update_failed", &self.settings_state.language)
                                    .replace("{error}", &e),
                                ctx,
                            );
                            self.settings_state.update_checking = false;
                            self.settings_state.update_downloading = false;
                            clear_rx = true;
                        }
                    }
                }
            }
            if clear_rx {
                self.updater_rx = None;
            }
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
            ctx.request_repaint();
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
            );
        }

        // 每帧轮询酒馆进程状态
        self.console_state.poll(&self.settings_state.language);

        // ---- 桌面模式 WebView 管理 ----
        if self.settings_state.start_mode == StartMode::Desktop {
            // 每帧同步导出路径设置到 WebView（用户在设置页修改后即时生效）
            DesktopWebView::set_export_path(&self.settings_state.tavern_export_path);

            // 日志中出现 "Go to: http://..." → 首次自动打开 WebView
            if let Some(ref url) = self.console_state.tavern_url {
                if self.desktop_webview.is_none() && !self.console_state.webview_auto_opened {
                    let title = format!(
                        "SillyTavern - v{}",
                        self.console_state.instance_version
                    );
                    match DesktopWebView::open(url, &title, self.settings_state.tavern_export_path.clone()) {
                        Ok(wv) => {
                            self.console_state.add_log(&format!(
                                "[系统] 桌面模式 WebView 已打开: {}",
                                url
                            ));
                            self.desktop_webview = Some(wv);
                            self.console_state.webview_auto_opened = true;
                        }
                        Err(e) => {
                            self.console_state.add_log(&format!(
                                "[错误] 桌面模式 WebView 启动失败: {}",
                                e
                            ));
                        }
                    }
                }
            }

            // 用户关闭 WebView → 根据设置决定是否停止酒馆
            if let Some(ref mut wv) = self.desktop_webview {
                if wv.is_closed() {
                    self.desktop_webview = None;
                    if self.settings_state.auto_stop_tavern_on_webview_close {
                        self.console_state.add_log("[系统] 桌面模式 WebView 已关闭，正在停止酒馆...");
                        if self.console_state.status == pages::console::ConsoleStatus::Running {
                            self.console_state.stop(&self.settings_state.language);
                        }
                    } else {
                        self.console_state.add_log("[系统] 桌面模式 WebView 已关闭（服务继续运行）");
                    }
                }
            }

            // 重新打开 WebView（控制台"打开酒馆"按钮触发）
            if self.console_state.reopen_webview_triggered {
                self.console_state.reopen_webview_triggered = false;
                if let Some(ref mut wv) = self.desktop_webview {
                    // WebView 已存在 → 唤回前台，不重复打开
                    wv.bring_to_front();
                } else if let Some(ref url) = self.console_state.tavern_url {
                    // WebView 不存在 → 新建
                    let title = format!(
                        "SillyTavern - v{}",
                        self.console_state.instance_version
                    );
                    match DesktopWebView::open(url, &title, self.settings_state.tavern_export_path.clone()) {
                        Ok(wv) => {
                            self.console_state.add_log(&format!(
                                "[系统] 桌面模式 WebView 已打开: {}",
                                url
                            ));
                            self.desktop_webview = Some(wv);
                        }
                        Err(e) => {
                            self.console_state.add_log(&format!(
                                "[错误] 桌面模式 WebView 启动失败: {}",
                                e
                            ));
                        }
                    }
                }
            }

            // 酒馆停止/停止中 → 关闭 WebView（从控制台手动停止时）
            if self.console_state.status != pages::console::ConsoleStatus::Running
                && self.console_state.status != pages::console::ConsoleStatus::Starting
            {
                if let Some(ref mut wv) = self.desktop_webview {
                    wv.close();
                    self.desktop_webview = None;
                }
            }
        } else {
            // 非桌面模式 → 确保 WebView 已关闭
            if let Some(ref mut wv) = self.desktop_webview {
                wv.close();
                self.desktop_webview = None;
            }
        }

        // 酒馆进程运行中持续重绘（确保日志实时更新）
        if self.console_state.status == pages::console::ConsoleStatus::Running
            || self.console_state.status == pages::console::ConsoleStatus::Starting
            || self.console_state.status == pages::console::ConsoleStatus::Stopping
        {
            ctx.request_repaint();
        }

        // 在线下载中持续重绘
        if self.version_manage_state.is_downloading {
            ctx.request_repaint();
        }

        // 酒馆配置下载中持续重绘
        if self.tavern_config_ui.gen_config_status.is_downloading() {
            ctx.request_repaint();
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
                    
                    // 如果扩展列表为空且没有在加载，触发一次加载
                    if self.extension_manage_state.extensions.is_empty() && !self.extension_manage_state.is_loading {
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

                    self.resource_manage_state.instance_path = instance_path;
                    self.resource_manage_state.data_mode = self.settings_state.data_mode.clone();

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
                        &self.github_node_state,
                        &mut self.on_refresh_nodes,
                    );
                }
            }
        });

        // 同步 transient 更新字段，避免误触发"设置已保存"
        old_state.update_confirm_open = self.settings_state.update_confirm_open;
        old_state.update_confirm_version.clone_from(&self.settings_state.update_confirm_version);
        old_state.update_confirm_notes.clone_from(&self.settings_state.update_confirm_notes);
        old_state.update_confirm_endpoint.clone_from(&self.settings_state.update_confirm_endpoint);
        old_state.update_downloading = self.settings_state.update_downloading;
        old_state.update_checking = self.settings_state.update_checking;
        old_state.check_update_trigger = self.settings_state.check_update_trigger;
        old_state.do_update_trigger.clone_from(&self.settings_state.do_update_trigger);

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

        // blob 下载结果通知（来自桌面模式 WebView）
        {
            let mut notifications =
                crate::core::desktop_webview::DOWNLOAD_NOTIFICATIONS
                    .lock()
                    .unwrap();
            for msg in notifications.drain(..) {
                self.toast_stack.push(msg, ctx);
            }
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
            if self.settings_state.remember_window_pos {
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
