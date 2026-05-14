use eframe::egui;

#[derive(PartialEq, Default)]
pub enum SettingsTab {
    #[default]
    General,
    About,
}

pub fn render(ui: &mut egui::Ui, tab: &mut SettingsTab) {
    ui.horizontal(|ui| {
        ui.selectable_value(tab, SettingsTab::General, "基本设置");
        ui.selectable_value(tab, SettingsTab::About, "关于软件");
    });
    ui.separator();

    match tab {
        SettingsTab::General => {
            ui.label("这里是基本设置的内容...");
        }
        SettingsTab::About => {
            ui.vertical_centered(|ui| {
                // 上部分：软件关于信息
                ui.heading("星酿启动器 (AstraBrew Launcher)");
                ui.label("版本: v0.1.0");
                ui.label("一款基于 Rust 和 egui 开发的启动器。");
                
                ui.add_space(20.0);
                ui.separator();
                ui.add_space(10.0);
                
                // 下部分：开发信息与技术栈表格
                ui.heading("开发信息与技术栈");
                ui.add_space(10.0);
                
                // 居中表格：利用 Layout 强制水平居中，并添加垂直滚动条
                ui.with_layout(egui::Layout::top_down(egui::Align::Center), |ui| {
                    egui::ScrollArea::vertical()
                        .auto_shrink([false, false])
                        .show(ui, |ui| {
                            // 设定固定列宽，确保文字不错位
                            egui::Grid::new("tech_stack_grid")
                                .striped(true)
                                .num_columns(4) // 修改为4列
                                .min_col_width(100.0) // 进一步调小以适应4列
                                .spacing(egui::vec2(30.0, 15.0)) // 调整间距
                                .show(ui, |ui| {
                                    // 表头
                                    ui.vertical_centered(|ui| ui.strong("技术/组件"));
                                    ui.vertical_centered(|ui| ui.strong("当前版本"));
                                    ui.vertical_centered(|ui| ui.strong("开源协议"));
                                    ui.vertical_centered(|ui| ui.strong("说明"));
                                    ui.end_row();
                                    
                                    // 数据行
                                    ui.vertical_centered(|ui| ui.label("Rust"));
                                    ui.vertical_centered(|ui| ui.label("2024"));
                                    ui.vertical_centered(|ui| {
                                        ui.hyperlink_to("MIT / Apache-2.0", "https://github.com/rust-lang/rust/blob/master/LICENSE-MIT");
                                    });
                                    ui.vertical_centered(|ui| ui.label("核心编程语言，提供内存安全和高性能"));
                                    ui.end_row();
                                    
                                    ui.vertical_centered(|ui| ui.label("egui"));
                                    ui.vertical_centered(|ui| ui.label("0.33"));
                                    ui.vertical_centered(|ui| {
                                        ui.hyperlink_to("MIT / Apache-2.0", "https://github.com/emilk/egui/blob/master/LICENSE-MIT");
                                    });
                                    ui.vertical_centered(|ui| ui.label("即时模式 GUI 框架"));
                                    ui.end_row();
                                    
                                    ui.vertical_centered(|ui| ui.label("eframe"));
                                    ui.vertical_centered(|ui| ui.label("0.33"));
                                    ui.vertical_centered(|ui| {
                                        ui.hyperlink_to("MIT / Apache-2.0", "https://github.com/emilk/egui/blob/master/LICENSE-MIT");
                                    });
                                    ui.vertical_centered(|ui| ui.label("egui 的官方集成框架"));
                                    ui.end_row();
                                    
                                    ui.vertical_centered(|ui| ui.label("egui_phosphor"));
                                    ui.vertical_centered(|ui| ui.label("0.11"));
                                    ui.vertical_centered(|ui| {
                                        ui.hyperlink_to("MIT / Apache-2.0", "https://github.com/amPerl/egui-phosphor/blob/main/LICENSE-MIT");
                                    });
                                    ui.vertical_centered(|ui| ui.label("图标库"));
                                    ui.end_row();
                                });
                        });
                });
            });
        }
    }
}
