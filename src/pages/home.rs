use crate::lang;
use crate::pages::console::{ConsoleState, ConsoleStatus};
use crate::pages::settings::Language;
use crate::Page;
use egui::{Color32, CornerRadius, Frame, Margin, RichText, Stroke, Vec2};

/// 主页渲染函数
/// - current_page: 用于页面跳转（点击按钮时切到控制台）
/// - console_state: 用于触发启动/停止
/// - lang: 语言
/// - version_info: 当前版本字符串（可选）
/// - start_mode_label: 当前启动模式的翻译文本
pub fn render(
    ui: &mut egui::Ui,
    current_page: &mut Page,
    console_state: &mut ConsoleState,
    lang: &Language,
    version_info: Option<&str>,
    start_mode_label: &str,
) {
    let available = ui.available_size();
    let visuals = ui.style().visuals.clone();
    let is_dark = visuals.dark_mode;

    // ---- 配色 ----
    let bg_fill = if is_dark {
        Color32::from_rgb(24, 26, 32)
    } else {
        Color32::from_rgb(248, 249, 252)
    };
    let card_fill = if is_dark {
        Color32::from_rgb(32, 35, 42)
    } else {
        Color32::from_rgb(255, 255, 255)
    };
    let border_color = if is_dark {
        Color32::from_rgb(50, 54, 64)
    } else {
        Color32::from_rgb(220, 224, 232)
    };
    let text_primary = if is_dark {
        Color32::from_rgb(230, 232, 240)
    } else {
        Color32::from_rgb(30, 32, 40)
    };
    let text_secondary = if is_dark {
        Color32::from_rgb(140, 144, 158)
    } else {
        Color32::from_rgb(130, 134, 148)
    };
    let accent_green = Color32::from_rgb(60, 200, 100);
    let accent_red = Color32::from_rgb(240, 80, 70);
    let accent_blue = Color32::from_rgb(70, 140, 240);
    let accent_orange = Color32::from_rgb(240, 150, 50);
    let accent_purple = Color32::from_rgb(140, 100, 220);

    // 计算布局：垂直居中分为上中下三区
    let content_width = (available.x * 0.7).min(600.0).max(360.0);
    let _side_margin = ((available.x - content_width) / 2.0).max(0.0);

    // 服务状态
    let is_stopped = console_state.status == ConsoleStatus::Stopped;
    let is_running = console_state.status == ConsoleStatus::Running;
    let is_transitioning = console_state.status == ConsoleStatus::Starting
        || console_state.status == ConsoleStatus::Stopping;

    // ---- 整体布局 ----
    Frame::NONE
        .fill(bg_fill)
        .show(ui, |ui| {
            ui.centered_and_justified(|ui| {
                ui.set_min_size(Vec2::new(content_width, available.y));

                // 分配三区高度
                let top_section_h = 120.0;   // 欢迎区
                let hero_section_h = 240.0;  // 按钮区
                let bottom_section_h = (available.y - top_section_h - hero_section_h).max(140.0);

                // ========== 顶部：欢迎区 ==========
                Frame::NONE.show(ui, |ui| {
                    ui.set_min_size(Vec2::new(content_width, top_section_h));
                    ui.vertical_centered(|ui| {
                        ui.add_space(24.0);

                        // App 图标 + 标题
                        ui.add(
                            egui::Label::new(
                                RichText::new(egui_phosphor::regular::ROCKET)
                                    .size(36.0)
                                    .color(accent_blue),
                            )
                            .selectable(false),
                        );
                        ui.add_space(8.0);

                        ui.add(
                            egui::Label::new(
                                RichText::new(lang::t("home_welcome", lang))
                                    .size(28.0)
                                    .strong()
                                    .color(text_primary),
                            )
                            .selectable(false),
                        );
                        ui.add_space(6.0);

                        ui.add(
                            egui::Label::new(
                                RichText::new(lang::t("home_subtitle", lang))
                                    .size(14.0)
                                    .color(text_secondary),
                            )
                            .selectable(false),
                        );
                    });
                });

                ui.add_space(8.0);

                // ========== 中部：英雄按钮区 ==========
                Frame::NONE.show(ui, |ui| {
                    ui.set_min_size(Vec2::new(content_width, hero_section_h));
                    ui.vertical_centered(|ui| {
                        ui.add_space(20.0);

                        // 访问/打开酒馆按钮（运行中 + URL 已捕获时显示，位于状态指示上方）
                        if is_running {
                            if let Some(ref url) = console_state.tavern_url {
                                let (btn_key, icon, open_in_browser) = if console_state.is_desktop_mode && !console_state.desktop_auto_stop {
                                    ("console_btn_open", egui_phosphor::regular::ARROW_SQUARE_OUT, false)
                                } else if !console_state.is_desktop_mode {
                                    ("console_btn_visit", egui_phosphor::regular::GLOBE, true)
                                } else {
                                    ("", "", false) // desktop + auto_stop: no button
                                };

                                if !btn_key.is_empty() {
                                    let link_color = Color32::from_rgb(80, 180, 255);
                                    let link = RichText::new(
                                        format!("{} {}", icon, lang::t(btn_key, lang)),
                                    )
                                    .size(15.0)
                                    .color(link_color);
                                    let resp = ui.add(
                                        egui::Label::new(link).sense(egui::Sense::click()),
                                    );
                                    if resp.hovered() {
                                        ui.ctx().set_cursor_icon(egui::CursorIcon::PointingHand);
                                    }
                                    if resp.clicked() {
                                        if open_in_browser {
                                            let _ = std::process::Command::new("open")
                                                .arg(url)
                                                .spawn();
                                        } else {
                                            console_state.reopen_webview_triggered = true;
                                        }
                                    }
                                    ui.add_space(12.0);
                                }
                            }
                        }

                        // 状态标签
                        let (status_icon, status_text, status_color) = match console_state.status {
                            ConsoleStatus::Stopped => (
                                egui_phosphor::regular::STOP_CIRCLE,
                                lang::t("home_status_stopped", lang),
                                text_secondary,
                            ),
                            ConsoleStatus::Starting => (
                                egui_phosphor::regular::ARROW_CLOCKWISE,
                                lang::t("home_status_starting", lang),
                                accent_orange,
                            ),
                            ConsoleStatus::Running => (
                                egui_phosphor::regular::PLAY_CIRCLE,
                                lang::t("home_status_running", lang),
                                accent_green,
                            ),
                            ConsoleStatus::Stopping => (
                                egui_phosphor::regular::STOP_CIRCLE,
                                lang::t("home_status_stopping", lang),
                                accent_orange,
                            ),
                        };

                        ui.add(
                            egui::Label::new(
                                RichText::new(format!("{}  {}", status_icon, status_text))
                                    .size(15.0)
                                    .color(status_color),
                            )
                            .selectable(false),
                        );

                        ui.add_space(18.0);

                        // ---- 主按钮 ----
                        let btn_size = Vec2::new(220.0, 64.0);
                        let (btn_fill, btn_text, btn_icon) = if is_stopped {
                            (
                                accent_green,
                                lang::t("home_btn_start", lang),
                                egui_phosphor::regular::ROCKET_LAUNCH,
                            )
                        } else if is_running {
                            (
                                accent_red,
                                lang::t("home_btn_stop", lang),
                                egui_phosphor::regular::STOP_CIRCLE,
                            )
                        } else {
                            (
                                accent_orange,
                                lang::t("home_status_transitioning", lang),
                                egui_phosphor::regular::HOURGLASS,
                            )
                        };

                        let btn_response = ui.add_sized(
                            btn_size,
                            egui::Button::new(
                                RichText::new(format!("{}  {}", btn_icon, btn_text))
                                    .size(20.0)
                                    .strong()
                                    .color(Color32::WHITE),
                            )
                            .fill(btn_fill)
                            .corner_radius(CornerRadius::same(16))
                            .stroke(Stroke::NONE),
                        );

                        if btn_response.clicked() && !is_transitioning {
                            if is_stopped {
                                // 一键启动 → 调用控制台启动服务
                                console_state.start(lang);
                                *current_page = Page::Console;
                            } else if is_running {
                                // 立即停止 → 调用控制台正常关闭
                                console_state.stop(lang);
                                *current_page = Page::Console;
                            }
                        }

                        ui.add_space(10.0);

                        // 按钮下方提示文字
                        let hint_text = if is_stopped {
                            lang::t("home_hint_start", lang)
                        } else if is_running {
                            lang::t("home_hint_stop", lang)
                        } else {
                            lang::t("home_hint_transitioning", lang)
                        };
                        ui.add(
                            egui::Label::new(
                                RichText::new(hint_text)
                                    .size(12.0)
                                    .color(text_secondary)
                                    .italics(),
                            )
                            .selectable(false),
                        );
                    });
                });

                ui.add_space(8.0);

                // ========== 底部：信息卡片区 ==========
                Frame::NONE.show(ui, |ui| {
                    ui.set_min_size(Vec2::new(content_width, bottom_section_h));
                    ui.add_space(4.0);

                    // 三列卡片布局
                    let card_width = (content_width - 24.0) / 3.0;
                    let card_height = (bottom_section_h - 16.0).min(130.0);

                    ui.horizontal(|ui| {
                        // 卡片 1: 当前版本
                        card_widget(
                            ui,
                            card_width,
                            card_height,
                            card_fill,
                            border_color,
                            text_primary,
                            text_secondary,
                            accent_blue,
                            egui_phosphor::regular::PACKAGE,
                            lang::t("home_card_version", lang),
                            version_info.unwrap_or(lang::t("home_no_version", lang)),
                        );

                        ui.add_space(12.0);

                        // 卡片 2: 启动模式
                        card_widget(
                            ui,
                            card_width,
                            card_height,
                            card_fill,
                            border_color,
                            text_primary,
                            text_secondary,
                            accent_purple,
                            egui_phosphor::regular::GEAR,
                            lang::t("home_card_mode", lang),
                            start_mode_label,
                        );

                        ui.add_space(12.0);

                        // 卡片 3: 服务端口
                        card_widget(
                            ui,
                            card_width,
                            card_height,
                            card_fill,
                            border_color,
                            text_primary,
                            text_secondary,
                            accent_orange,
                            egui_phosphor::regular::GLOBE,
                            lang::t("home_card_port", lang),
                            "8000",
                        );
                    });
                });
            });
        });
}

/// 信息卡片组件
fn card_widget(
    ui: &mut egui::Ui,
    width: f32,
    height: f32,
    fill: Color32,
    border: Color32,
    text_primary: Color32,
    text_secondary: Color32,
    accent: Color32,
    icon: &str,
    label: &str,
    value: &str,
) {
    Frame::NONE
        .fill(fill)
        .corner_radius(CornerRadius::same(10))
        .stroke(Stroke::new(1.0, border))
        .inner_margin(Margin::symmetric(14, 12))
        .show(ui, |ui| {
            ui.set_min_size(Vec2::new(width, height));
            ui.vertical_centered(|ui| {
                ui.add_space(8.0);

                // 图标
                ui.add(
                    egui::Label::new(
                        RichText::new(icon).size(28.0).color(accent),
                    )
                    .selectable(false),
                );

                ui.add_space(6.0);

                // 标签
                ui.add(
                    egui::Label::new(
                        RichText::new(label).size(11.0).color(text_secondary),
                    )
                    .selectable(false),
                );

                ui.add_space(4.0);

                // 值
                ui.add(
                    egui::Label::new(
                        RichText::new(value)
                            .size(16.0)
                            .strong()
                            .color(text_primary),
                    )
                    .selectable(false),
                );
            });
        });
}
