use eframe::egui;
use egui::{FontData, FontDefinitions, FontFamily};

fn main() -> eframe::Result {
    // 根据规范配置软件窗口
    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            // 默认尺寸 1280x720 (16:9 初始默认)
            .with_inner_size([1280.0, 720.0])
            // 最小尺寸 800x600
            .with_min_inner_size([800.0, 600.0])
            // 最大尺寸不超过 1280x720 (16:9 最大限制)
            .with_max_inner_size([1280.0, 720.0])
            // 禁用最大化按钮
            .with_maximize_button(false),
        ..Default::default()
    };

    eframe::run_native(
        "中文测试",
        options,
        Box::new(|cc| {
            setup_fonts(&cc.egui_ctx);

            Ok(Box::new(MyApp::default()))
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

mod pages;
use pages::settings::SettingsTab;

struct MyApp {
    current_page: Page,
    // 记录上一次检测到的显示器大小，用于多屏切换适配
    last_monitor_size: Option<egui::Vec2>,
    settings_tab: SettingsTab,
}

impl Default for MyApp {
    fn default() -> Self {
        Self {
            current_page: Page::OneClickStart,
            last_monitor_size: None,
            settings_tab: SettingsTab::default(),
        }
    }
}

impl eframe::App for MyApp {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        // 动态适配屏幕比例 (16:9 或 4:3) 以及多屏切换
        if let Some(monitor_size) = ctx.input(|i| i.viewport().monitor_size) {
            // 当检测到显示器大小发生变化（比如切换到了另一个屏幕），重新计算窗口限制
            if self.last_monitor_size != Some(monitor_size) {
                let aspect_ratio = monitor_size.x / monitor_size.y;
                
                // 判断是否接近 4:3 比例 (约等于 1.33)
                if (aspect_ratio - 4.0 / 3.0).abs() < 0.1 {
                    // 如果是第一次初始化，设置默认大小
                    if self.last_monitor_size.is_none() {
                        ctx.send_viewport_cmd(egui::ViewportCommand::InnerSize(egui::vec2(800.0, 600.0)));
                    }
                    ctx.send_viewport_cmd(egui::ViewportCommand::MinInnerSize(egui::vec2(800.0, 600.0)));
                    ctx.send_viewport_cmd(egui::ViewportCommand::MaxInnerSize(egui::vec2(1200.0, 800.0)));
                } else {
                    // 默认当作 16:9 处理
                    if self.last_monitor_size.is_none() {
                        ctx.send_viewport_cmd(egui::ViewportCommand::InnerSize(egui::vec2(1280.0, 720.0)));
                    }
                    ctx.send_viewport_cmd(egui::ViewportCommand::MinInnerSize(egui::vec2(800.0, 600.0)));
                    ctx.send_viewport_cmd(egui::ViewportCommand::MaxInnerSize(egui::vec2(1280.0, 720.0)));
                }
                
                self.last_monitor_size = Some(monitor_size);
            }
        }

        // 左侧导航栏
        egui::SidePanel::left("left_panel")
            .resizable(false)
            .exact_width(150.0)
            .show(ctx, |ui| {
                ui.add_space(10.0);
                
                // 设置导航栏标题
                ui.vertical_centered(|ui| {
                    ui.heading("星酿启动器");
                    ui.heading(egui::RichText::new("AstraBrew Launcher").size(12.0));
                });
                
                ui.add_space(20.0);

                // 导航按钮
                let nav_button = |ui: &mut egui::Ui, current: &mut Page, target: Page, icon: &str, text: &str| {
                    let is_selected = *current == target;
                    let text = egui::RichText::new(format!("{} {}", icon, text)).size(16.0);
                    // 使用 add_sized 和 available_width() 让按钮占满宽度，高度留 0.0 自适应
                    let response = ui.add_sized([ui.available_width(), 32.0], egui::Button::selectable(is_selected, text));
                    if response.clicked() {
                        *current = target;
                    }
                    response
                };

                nav_button(ui, &mut self.current_page, Page::OneClickStart, egui_phosphor::regular::ROCKET, "一键启动");
                nav_button(ui, &mut self.current_page, Page::TavernConfig, egui_phosphor::regular::SLIDERS, "酒馆配置");
                nav_button(ui, &mut self.current_page, Page::VersionManage, egui_phosphor::regular::GIT_BRANCH, "版本管理");
                nav_button(ui, &mut self.current_page, Page::ExtensionManage, egui_phosphor::regular::PUZZLE_PIECE, "扩展管理");
                nav_button(ui, &mut self.current_page, Page::ResourceManage, egui_phosphor::regular::FOLDER, "资源管理");
                
                // 将设置按钮推到底部
                ui.with_layout(egui::Layout::bottom_up(egui::Align::Min), |ui| {
                    ui.add_space(10.0);
                    nav_button(ui, &mut self.current_page, Page::Settings, egui_phosphor::regular::GEAR, "软件设置");
                });
            });

        // 右侧页面视口
        egui::CentralPanel::default().show(ctx, |ui| {
            match self.current_page {
                Page::OneClickStart => {
                    ui.heading("一键启动");
                    ui.separator();
                    ui.label("这里是一键启动页面的内容...");
                }
                Page::TavernConfig => {
                    ui.heading("酒馆配置");
                    ui.separator();
                    ui.label("这里是酒馆配置页面的内容...");
                }
                Page::VersionManage => {
                    ui.heading("版本管理");
                    ui.separator();
                    ui.label("这里是版本管理页面的内容...");
                }
                Page::ExtensionManage => {
                    ui.heading("扩展管理");
                    ui.separator();
                    ui.label("这里是扩展管理页面的内容...");
                }
                Page::ResourceManage => {
                    ui.heading("资源管理");
                    ui.separator();
                    ui.label("这里是资源管理页面的内容...");
                }
                Page::Settings => {
                    ui.heading("软件设置");
                    ui.separator();
                    pages::settings::render(ui, &mut self.settings_tab);
                }
            }
        });
    }
}
