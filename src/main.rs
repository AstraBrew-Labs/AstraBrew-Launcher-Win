use eframe::egui;
use egui::{FontData, FontDefinitions, FontFamily};

fn main() -> eframe::Result {
    // 提前加载设置状态以获取窗口位置
    let settings = pages::settings::SettingsState::load();
    
    // 根据规范配置软件窗口
    let mut viewport = egui::ViewportBuilder::default()
        // 默认尺寸 1280x720 (16:9 初始默认)
        .with_inner_size([1280.0, 720.0])
        // 最小尺寸 800x600
        .with_min_inner_size([800.0, 600.0])
        // 最大尺寸不超过 1280x720 (16:9 最大限制)
        .with_max_inner_size([1280.0, 720.0])
        // 禁用最大化按钮
        .with_maximize_button(false);
        
    // 恢复窗口位置或居中
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

            Ok(Box::new(MyApp::new(settings)))
        }),
    )
}

fn setup_fonts(ctx: &egui::Context) {
    let mut fonts = FontDefinitions::default();
    // 加载中文字体
    fonts.font_data.insert(
        "MiSans".to_owned(),
        FontData::from_static(include_bytes!("../assets/fonts/MiSans-Regular.ttf")).into(),
    );

    egui_phosphor::add_to_fonts(&mut fonts, egui_phosphor::Variant::Regular);

    // 放到最前面
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
    Settings,
}

mod core;
#[path = "lang/lang.rs"]
mod lang;
mod pages;
mod ui;
use pages::settings::{SettingsState, SettingsTab, Theme};

pub enum EnvInstallProgress {
    Status(String),
    Progress(f32),
    Finished,
    Error(String),
}

use core::settings::github_proxy::{NodeLoadMsg, NodeLoadState};

struct MyApp {
    current_page: Page,
    // 记录上一次检测到的显示器大小，用于多屏切换适配
    last_monitor_size: Option<egui::Vec2>,
    settings_tab: SettingsTab,
    settings_state: SettingsState,
    last_save_time: Option<std::time::Instant>,
    
    // 环境状态缓存 (version, path)
    git_info: Option<(String, String)>,
    nodejs_info: Option<(String, String)>,
    npm_info: Option<(String, String)>,
    
    // 自定义提示 (提示内容, 触发时间)
    toast_message: Option<(String, std::time::Instant)>,
    
    // 环境下载状态
    show_env_download_prompt: bool,
    env_download_tasks: Vec<String>, // e.g., ["git", "nodejs"]
    env_downloading: bool,
    env_download_status: String,
    env_download_progress: f32,
    env_download_progress_receiver: Option<std::sync::mpsc::Receiver<EnvInstallProgress>>,

    // GitHub 代理节点
    github_node_state: NodeLoadState,
    github_node_receiver: Option<std::sync::mpsc::Receiver<NodeLoadMsg>>,
    github_node_entries: Vec<core::settings::github_proxy::NodeEntry>,
}

impl MyApp {
    fn new(settings_state: SettingsState) -> Self {
        let mut app = Self {
            current_page: Page::OneClickStart,
            last_monitor_size: None,
            settings_tab: SettingsTab::default(),
            settings_state,
            last_save_time: None,
            git_info: None,
            nodejs_info: None,
            npm_info: None,
            toast_message: None,
            show_env_download_prompt: false,
            env_download_tasks: Vec::new(),
            env_downloading: false,
            env_download_status: String::new(),
            env_download_progress: 0.0,
            env_download_progress_receiver: None,
            github_node_state: NodeLoadState::Idle,
            github_node_receiver: None,
            github_node_entries: Vec::new(),
        };
        
        // 初始化时检测并刷新环境信息
        app.refresh_env_info();

        // 如果 GitHub 代理已开启，自动加载节点列表（读缓存）
        if app.settings_state.github_proxy_enabled {
            app.start_github_node_fetch(false);
        }
        
        app
    }

    fn start_github_node_fetch(&mut self, force_refresh: bool) {
        self.github_node_state = NodeLoadState::Loading;
        let (tx, rx) = std::sync::mpsc::channel();
        self.github_node_receiver = Some(rx);
        core::settings::github_proxy::start_fetch_and_test(tx, force_refresh);
    }

    fn refresh_env_info(&mut self) {
        use pages::settings::EnvSource;
        use core::env::{get_builtin_git_path, get_system_cmd_path, get_cmd_version, get_builtin_node_path, get_builtin_npm_path};
        
        let mut missing_tasks = Vec::new();

        // 1. 刷新 Git 环境
        let git_path_opt = match self.settings_state.git_env {
            EnvSource::Builtin => get_builtin_git_path(),
            EnvSource::System => get_system_cmd_path("git"),
        };

        if self.settings_state.git_env == EnvSource::Builtin && git_path_opt.is_none() && !self.env_downloading {
            missing_tasks.push("git".to_string());
        }

        self.git_info = git_path_opt.map(|p| {
            let ver = get_cmd_version(&p).unwrap_or_else(|| "Unknown".to_string());
            (ver, p.to_string_lossy().to_string())
        });

        // 2. 刷新 Node.js 环境
        let node_path_opt = match self.settings_state.nodejs_env {
            EnvSource::Builtin => get_builtin_node_path(),
            EnvSource::System => get_system_cmd_path("node"),
        };

        if self.settings_state.nodejs_env == EnvSource::Builtin && node_path_opt.is_none() && !self.env_downloading {
            missing_tasks.push("node".to_string());
        }

        self.nodejs_info = node_path_opt.map(|p| {
            let ver = get_cmd_version(&p).unwrap_or_else(|| "Unknown".to_string());
            (ver, p.to_string_lossy().to_string())
        });

        // 3. 刷新 NPM 环境 (NPM 依赖于 Node.js 环境来源)
        let npm_path_opt = match self.settings_state.nodejs_env {
            EnvSource::Builtin => get_builtin_npm_path(),
            EnvSource::System => get_system_cmd_path("npm"),
        };

        self.npm_info = npm_path_opt.map(|p| {
            let ver = get_cmd_version(&p).unwrap_or_else(|| "Unknown".to_string());
            (ver, p.to_string_lossy().to_string())
        });

        if !missing_tasks.is_empty() {
            self.env_download_tasks = missing_tasks;
            self.show_env_download_prompt = true;
        }
    }
}

impl eframe::App for MyApp {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        // 应用主题
        let visuals = match self.settings_state.theme {
            Theme::Light => egui::Visuals::light(),
            Theme::Dark => egui::Visuals::dark(),
        };
        ctx.set_visuals(visuals);

        // 动态适配屏幕比例 (16:9 或 4:3) 以及多屏切换
        if let Some(monitor_size) = ctx.input(|i| i.viewport().monitor_size) {
            // 当检测到显示器大小发生变化（比如切换到了另一个屏幕），重新计算窗口限制
            if self.last_monitor_size != Some(monitor_size) {
                let aspect_ratio = monitor_size.x / monitor_size.y;

                // 判断是否接近 4:3 比例 (约等于 1.33)
                if (aspect_ratio - 4.0 / 3.0).abs() < 0.1 {
                    // 如果是第一次初始化，设置默认大小
                    if self.last_monitor_size.is_none() {
                        ctx.send_viewport_cmd(egui::ViewportCommand::InnerSize(egui::vec2(
                            800.0, 600.0,
                        )));
                    }
                    ctx.send_viewport_cmd(egui::ViewportCommand::MinInnerSize(egui::vec2(
                        800.0, 600.0,
                    )));
                    ctx.send_viewport_cmd(egui::ViewportCommand::MaxInnerSize(egui::vec2(
                        1200.0, 800.0,
                    )));
                } else {
                    // 默认当作 16:9 处理
                    if self.last_monitor_size.is_none() {
                        ctx.send_viewport_cmd(egui::ViewportCommand::InnerSize(egui::vec2(
                            1280.0, 720.0,
                        )));
                    }
                    ctx.send_viewport_cmd(egui::ViewportCommand::MinInnerSize(egui::vec2(
                        800.0, 600.0,
                    )));
                    ctx.send_viewport_cmd(egui::ViewportCommand::MaxInnerSize(egui::vec2(
                        1280.0, 720.0,
                    )));
                }

                self.last_monitor_size = Some(monitor_size);
            }
        }

        let panel_width = match self.settings_state.language {
            pages::settings::Language::Chinese => 150.0,
            pages::settings::Language::English => 180.0,
        };

        // 获取禁用交互的标志（当显示弹窗时禁用主界面交互）
        let is_modal_open = self.show_env_download_prompt || self.env_downloading;

        // 如果模态框打开，则为底层增加一个半透明的遮罩层
        if is_modal_open {
            // 使用 Area 将遮罩层放在 Foreground
            egui::Area::new(egui::Id::new("modal_mask"))
                .order(egui::Order::Foreground)
                .interactable(true)
                .show(ctx, |ui| {
                    let content_rect = ctx.content_rect();
                    ui.painter().rect_filled(content_rect, 0.0, egui::Color32::from_black_alpha(150));
                });
        }

        // 左侧导航栏
        egui::SidePanel::left("left_panel")
            .resizable(false)
            .exact_width(panel_width)
            .show(ctx, |ui| {
                ui.add_enabled_ui(!is_modal_open, |ui| {
                    ui.add_space(10.0);

                    // 设置导航栏标题
                    ui.vertical_centered(|ui| {
                        ui.heading(lang::t("app_title", &self.settings_state.language));
                        ui.heading(egui::RichText::new(lang::t("app_subtitle", &self.settings_state.language)).size(12.0));
                    });

                    ui.add_space(20.0);

                    // 导航按钮
                    let nav_button = |ui: &mut egui::Ui,
                                      current: &mut Page,
                                      target: Page,
                                      icon: &str,
                                      text: &str| {
                        let is_selected = *current == target;
                        
                        // 使用水平布局，图标和文本分离，确保文本左对齐且不被居中打乱
                        let response = ui.add_sized(
                            [ui.available_width(), 32.0],
                            egui::Button::selectable(is_selected, ""),
                        );
                        
                        // 覆盖在 Button 上的图标和文本
                        let rect = response.rect;
                        let text_color = ui.style().interact_selectable(&response, is_selected).text_color();
                        
                        let mut child_ui = ui.new_child(egui::UiBuilder::new().max_rect(rect).layout(*ui.layout()));
                        child_ui.horizontal(|ui| {
                                ui.add_space(8.0); // 左侧间距
                                
                                // 图标固定宽度
                                ui.add_sized(
                                    [20.0, rect.height()], 
                                    |ui: &mut egui::Ui| {
                                        ui.centered_and_justified(|ui| {
                                            ui.add(egui::Label::new(egui::RichText::new(icon).size(16.0).color(text_color)).selectable(false));
                                        }).response
                                    }
                                );
                                
                                ui.add_space(4.0); // 图标与文字间距
                                
                                // 文本部分
                                ui.with_layout(egui::Layout::left_to_right(egui::Align::Center), |ui| {
                                    ui.add(egui::Label::new(egui::RichText::new(text).size(16.0).color(text_color)).selectable(false));
                                });
                            });
                        
                        if response.clicked() {
                            *current = target;
                        }
                        response
                    };

                    nav_button(
                        ui,
                        &mut self.current_page,
                        Page::OneClickStart,
                        egui_phosphor::regular::ROCKET,
                        lang::t("one_click_start", &self.settings_state.language),
                    );
                    nav_button(
                        ui,
                        &mut self.current_page,
                        Page::TavernConfig,
                        egui_phosphor::regular::SLIDERS,
                        lang::t("tavern_config", &self.settings_state.language),
                    );
                    nav_button(
                        ui,
                        &mut self.current_page,
                        Page::VersionManage,
                        egui_phosphor::regular::GIT_BRANCH,
                        lang::t("version_manage", &self.settings_state.language),
                    );
                    nav_button(
                        ui,
                        &mut self.current_page,
                        Page::ExtensionManage,
                        egui_phosphor::regular::PUZZLE_PIECE,
                        lang::t("extension_manage", &self.settings_state.language),
                    );
                    nav_button(
                        ui,
                        &mut self.current_page,
                        Page::ResourceManage,
                        egui_phosphor::regular::FOLDER,
                        lang::t("resource_manage", &self.settings_state.language),
                    );

                    // 将设置按钮推到底部
                    ui.with_layout(egui::Layout::bottom_up(egui::Align::Min), |ui| {
                        ui.add_space(10.0);
                        // 为了防止在 bottom_up 布局下内部渲染坐标计算错乱，
                        // 我们可以在分配一块区域后，在这块区域内使用正常的 top_down 布局来绘制按钮。
                        let button_height = 32.0;
                        let (rect, _) = ui.allocate_exact_size(
                            egui::vec2(ui.available_width(), button_height),
                            egui::Sense::hover(),
                        );
                        
                        let mut child_ui = ui.new_child(egui::UiBuilder::new().max_rect(rect).layout(egui::Layout::top_down(egui::Align::Min)));
                        nav_button(
                            &mut child_ui,
                            &mut self.current_page,
                            Page::Settings,
                            egui_phosphor::regular::GEAR,
                            lang::t("software_settings", &self.settings_state.language),
                        );
                    });
                });
            });

        // 右侧页面视口
        let old_state = self.settings_state.clone();
        egui::CentralPanel::default().show(ctx, |ui| {
            ui.add_enabled_ui(!is_modal_open, |ui| {
                match self.current_page {
                    Page::OneClickStart => {
                        ui.heading(lang::t("one_click_start", &self.settings_state.language));
                        ui.separator();
                        ui.label("这里是一键启动页面的内容...");
                    }
                    Page::TavernConfig => {
                        ui.heading(lang::t("tavern_config", &self.settings_state.language));
                        ui.separator();
                        ui.label("这里是酒馆配置页面的内容...");
                    }
                    Page::VersionManage => {
                        ui.heading(lang::t("version_manage", &self.settings_state.language));
                        ui.separator();
                        ui.label("这里是版本管理页面的内容...");
                    }
                    Page::ExtensionManage => {
                        ui.heading(lang::t("extension_manage", &self.settings_state.language));
                        ui.separator();
                        ui.label("这里是扩展管理页面的内容...");
                    }
                    Page::ResourceManage => {
                        ui.heading(lang::t("resource_manage", &self.settings_state.language));
                        ui.separator();
                        ui.label("这里是资源管理页面的内容...");
                    }
                    Page::Settings => {
                        ui.heading(lang::t("software_settings", &self.settings_state.language));
                        ui.separator();
                        let mut do_refresh = false;
                        pages::settings::render(
                            ui,
                            &mut self.settings_tab,
                            &mut self.settings_state,
                            &self.git_info,
                            &self.nodejs_info,
                            &self.npm_info,
                            &self.github_node_state,
                            &mut do_refresh,
                        );
                        if do_refresh {
                            self.start_github_node_fetch(true);
                        }
                    }
                }
            });
        });

        // 如果设置发生变化，触发保存，并检测是否需要刷新环境信息
        if old_state != self.settings_state {
            self.settings_state.save();
            self.last_save_time = Some(std::time::Instant::now());
            
            // 如果环境变量源被修改了，则刷新信息
            if old_state.git_env != self.settings_state.git_env ||
               old_state.nodejs_env != self.settings_state.nodejs_env {
                self.refresh_env_info();
            }

            // 代理开关打开时，若还没加载则自动触发一次（读缓存）
            if !old_state.github_proxy_enabled && self.settings_state.github_proxy_enabled {
                if self.github_node_state == NodeLoadState::Idle {
                    self.start_github_node_fetch(false);
                }
            }
        }

        // 轮询 GitHub 节点 channel
        let mut need_repaint = false;
        let mut clear_receiver = false;
        if let Some(rx) = self.github_node_receiver.as_ref() {
            loop {
                match rx.try_recv() {
                    Ok(NodeLoadMsg::Nodes(entries)) => {
                        self.github_node_entries = entries.clone();
                        self.github_node_state = NodeLoadState::Done(entries);
                        need_repaint = true;
                    }
                    Ok(NodeLoadMsg::LatencyUpdate) => {
                        // 延迟数据通过 Arc<Mutex> 共享，直接请求重绘即可
                        need_repaint = true;
                    }
                    Ok(NodeLoadMsg::Done) => {
                        need_repaint = true;
                    }
                    Ok(NodeLoadMsg::Error(e)) => {
                        self.github_node_state = NodeLoadState::Error(e);
                        clear_receiver = true;
                        need_repaint = true;
                    }
                    Err(std::sync::mpsc::TryRecvError::Empty) => break,
                    Err(std::sync::mpsc::TryRecvError::Disconnected) => {
                        clear_receiver = true;
                        break;
                    }
                }
            }
        }
        if clear_receiver {
            self.github_node_receiver = None;
        }
        if need_repaint {
            ctx.request_repaint();
        }

        // 询问是否下载环境的弹窗
        if self.show_env_download_prompt {
            let tasks_str = self.env_download_tasks.join(" & ");
            let title = lang::t("download_env_title", &self.settings_state.language).replace("{env}", &tasks_str);
            let prompt = lang::t("download_env_prompt", &self.settings_state.language).replace("{env}", &tasks_str);

            egui::Window::new(title)
                .order(egui::Order::Tooltip)
                .collapsible(false)
                .resizable(false)
                .anchor(egui::Align2::CENTER_CENTER, [0.0, 0.0])
                .show(ctx, |ui| {
                    ui.label(prompt);
                    ui.add_space(10.0);
                    ui.horizontal(|ui| {
                        if ui.button(lang::t("confirm", &self.settings_state.language)).clicked() {
                            self.show_env_download_prompt = false;
                            self.env_downloading = true;
                            self.env_download_status = lang::t("loading", &self.settings_state.language).to_string();
                            self.env_download_progress = 0.0;
                            
                            let (tx, rx) = std::sync::mpsc::channel();
                            self.env_download_progress_receiver = Some(rx);
                            
                            let tasks = self.env_download_tasks.clone();
                            std::thread::spawn(move || {
                                for task in tasks {
                                    if task == "git" {
                                        let _ = tx.send(EnvInstallProgress::Status(format!("Start downloading Git...")));
                                        let _ = core::settings::git::download_and_install_git(Some(tx.clone()));
                                    } else if task == "node" {
                                        let _ = tx.send(EnvInstallProgress::Status(format!("Start downloading Node.js...")));
                                        let _ = core::settings::nodejs::download_and_install_nodejs(Some(tx.clone()));
                                    }
                                }
                                let _ = tx.send(EnvInstallProgress::Finished);
                            });
                        }
                        if ui.button(lang::t("cancel", &self.settings_state.language)).clicked() {
                            self.show_env_download_prompt = false;
                            // 无论系统是否存在，都强制退回到系统环境，防止弹窗死循环
                            for task in &self.env_download_tasks {
                                if task == "node" {
                                    self.settings_state.nodejs_env = pages::settings::EnvSource::System;
                                    if core::env::get_system_cmd_path("node").is_some() {
                                        self.toast_message = Some((
                                            lang::t("fallback_system_node", &self.settings_state.language).to_string(),
                                            std::time::Instant::now()
                                        ));
                                    }
                                } else if task == "git" {
                                    self.settings_state.git_env = pages::settings::EnvSource::System;
                                    if core::env::get_system_cmd_path("git").is_some() {
                                        self.toast_message = Some((
                                            lang::t("fallback_system_git", &self.settings_state.language).to_string(),
                                            std::time::Instant::now()
                                        ));
                                    }
                                }
                            }
                            self.settings_state.save();
                            self.env_download_tasks.clear();
                            self.refresh_env_info();
                        }
                    });
                });
        }

        // 环境下载进度弹窗
        if self.env_downloading {
            let tasks_str = self.env_download_tasks.join(" & ");
            let title = lang::t("download_env_title", &self.settings_state.language).replace("{env}", &tasks_str);

            egui::Window::new(title)
                .order(egui::Order::Tooltip)
                .collapsible(false)
                .resizable(false)
                .anchor(egui::Align2::CENTER_CENTER, [0.0, 0.0])
                .show(ctx, |ui| {
                    ui.label(&self.env_download_status);
                    ui.add(egui::ProgressBar::new(self.env_download_progress).show_percentage());
                });

            // 接收进度
            let mut is_finished = false;
            if let Some(rx) = &self.env_download_progress_receiver {
                while let Ok(progress) = rx.try_recv() {
                    match progress {
                        EnvInstallProgress::Status(status) => {
                            self.env_download_status = status;
                        }
                        EnvInstallProgress::Progress(p) => {
                            self.env_download_progress = p;
                        }
                        EnvInstallProgress::Finished => {
                            self.env_downloading = false;
                            is_finished = true;
                            self.toast_message = Some((
                                lang::t("download_success", &self.settings_state.language).to_string(),
                                std::time::Instant::now()
                            ));
                        }
                        EnvInstallProgress::Error(err) => {
                            self.env_downloading = false;
                            is_finished = true;
                            self.toast_message = Some((
                                lang::t("download_failed", &self.settings_state.language).replace("{error}", &err),
                                std::time::Instant::now()
                            ));
                            // 退回到系统环境
                            for task in &self.env_download_tasks {
                                if task == "node" {
                                    self.settings_state.nodejs_env = pages::settings::EnvSource::System;
                                } else if task == "git" {
                                    self.settings_state.git_env = pages::settings::EnvSource::System;
                                }
                            }
                            self.settings_state.save();
                        }
                    }
                }
            }
            
            if is_finished {
                self.env_download_progress_receiver = None;
                self.env_download_tasks.clear();
                self.refresh_env_info();
            }
            
            // 请求持续重绘以刷新进度条
            ctx.request_repaint();
        }

        // 统一处理 Toast 提示逻辑
        let mut show_toast = None;
        
        // 检查是否有自定义的环境切换提示
        if let Some((msg, time)) = &self.toast_message {
            let elapsed = time.elapsed().as_secs_f32();
            if elapsed < 3.0 {
                show_toast = Some((msg.clone(), elapsed));
            } else {
                self.toast_message = None;
            }
        } 
        // 否则检查是否有保存提示
        else if let Some(save_time) = self.last_save_time {
            let elapsed = save_time.elapsed().as_secs_f32();
            if elapsed < 3.0 {
                show_toast = Some((lang::t("settings_saved", &self.settings_state.language).to_string(), elapsed));
            } else {
                self.last_save_time = None;
            }
        }

        if let Some((toast_text, elapsed)) = show_toast {
            // 计算透明度 (最后1秒淡出)
            let alpha = if elapsed > 2.0 {
                1.0 - (elapsed - 2.0)
            } else {
                1.0
            };
            
            // 绘制 Toast
            let visuals = ctx.style().visuals.clone();
            let text_color = visuals.text_color().linear_multiply(alpha);
            let bg_color = visuals.window_fill().linear_multiply(alpha);
            let stroke_color = visuals.window_stroke().color.linear_multiply(alpha);
            
            let painter = ctx.layer_painter(egui::LayerId::new(egui::Order::Tooltip, egui::Id::new("toast")));
            
            // 计算文本大小以居中
            let font_id = egui::FontId::proportional(16.0);
            let text_galley = painter.layout_no_wrap(toast_text, font_id, text_color);
            
            let screen_rect = ctx.content_rect();
            let center_x = screen_rect.center().x;
            let bottom_y = screen_rect.max.y - 50.0;
            
            let padding = egui::vec2(16.0, 10.0);
            let rect = egui::Rect::from_center_size(
                egui::pos2(center_x, bottom_y),
                text_galley.size() + padding * 2.0,
            );
            
            painter.rect(
                rect,
                8.0, // rounding
                bg_color,
                egui::Stroke::new(1.0, stroke_color),
                egui::StrokeKind::Middle,
            );
            
            let text_pos = egui::pos2(
                rect.center().x - text_galley.size().x / 2.0,
                rect.center().y - text_galley.size().y / 2.0,
            );
            painter.galley(text_pos, text_galley, text_color);
            
            // 请求重绘以更新淡出动画
            ctx.request_repaint();
        }

        // 如果软件准备关闭，保存当前窗口位置
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
