use crate::core::process::ConsoleCommand;
use crate::lang;
use crate::pages::settings::Language;
use egui::{Color32, RichText, Vec2};

#[derive(PartialEq, Clone)]
pub enum ConsoleStatus {
    Stopped,
    Starting,
    Running,
    Stopping,
}

pub struct ConsoleState {
    pub status: ConsoleStatus,
    pub logs: Vec<String>,
    /// 用户点了重启 → 停止完成后自动启动
    pub pending_restart: bool,
}

impl ConsoleState {
    pub fn new() -> Self {
        Self {
            status: ConsoleStatus::Stopped,
            logs: vec![String::from("[系统] 控制台已就绪")],
            pending_restart: false,
        }
    }

    pub fn add_log(&mut self, msg: &str) {
        let timestamp = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| {
                let secs = d.as_secs();
                let h = (secs / 3600) % 24 + 8; // UTC+8
                let m = (secs / 60) % 60;
                let s = secs % 60;
                format!("{:02}:{:02}:{:02}", h, m, s)
            })
            .unwrap_or_else(|_| String::from("--:--:--"));
        self.logs.push(format!("[{}] {}", timestamp, msg));
    }

    /// 将所有日志拼接为纯文本（移除 ANSI 转义码）
    pub fn plain_text(&self) -> String {
        let mut out = String::new();
        for line in &self.logs {
            // 去掉 ANSI 转义序列
            let clean = strip_ansi(line);
            out.push_str(&clean);
            out.push('\n');
        }
        out
    }
}

/// 去掉字符串中的 ANSI 转义序列
fn strip_ansi(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    let bytes = s.as_bytes();
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == 0x1b && i + 1 < bytes.len() && bytes[i + 1] == b'[' {
            i += 2;
            while i < bytes.len() && bytes[i] != b'm' {
                i += 1;
            }
            if i < bytes.len() {
                i += 1; // skip 'm'
            }
        } else {
            out.push(bytes[i] as char);
            i += 1;
        }
    }
    out
}

pub fn render(
    ui: &mut egui::Ui,
    state: &mut ConsoleState,
    lang: &Language,
    command: &mut Option<ConsoleCommand>,
) {
    let available = ui.available_size();

    // ---- 状态栏区域（固定高度）----
    let status_bar_height = 72.0;
    let log_area_height = (available.y - status_bar_height - 8.0).max(100.0);

    // 根据状态选择颜色和图标
    let (status_color, status_icon) = match state.status {
        ConsoleStatus::Stopped => (Color32::from_rgb(150, 150, 150), egui_phosphor::regular::STOP_CIRCLE),
        ConsoleStatus::Starting => (Color32::from_rgb(255, 200, 50), egui_phosphor::regular::ARROW_CLOCKWISE),
        ConsoleStatus::Running => (Color32::from_rgb(80, 220, 80), egui_phosphor::regular::PLAY_CIRCLE),
        ConsoleStatus::Stopping => (Color32::from_rgb(255, 150, 50), egui_phosphor::regular::STOP_CIRCLE),
    };

    let (status_title, status_subtitle) = match state.status {
        ConsoleStatus::Stopped => (
            lang::t("console_status_stopped", lang),
            lang::t("console_subtitle_stopped", lang),
        ),
        ConsoleStatus::Starting => (
            lang::t("console_status_starting", lang),
            lang::t("console_subtitle_starting", lang),
        ),
        ConsoleStatus::Running => (
            lang::t("console_status_running", lang),
            lang::t("console_subtitle_running", lang),
        ),
        ConsoleStatus::Stopping => (
            lang::t("console_status_stopping", lang),
            lang::t("console_subtitle_stopping", lang),
        ),
    };

    // 绘制状态栏
    egui::Frame::NONE
        .fill(ui.style().visuals.extreme_bg_color)
        .corner_radius(egui::CornerRadius::same(6))
        .inner_margin(egui::Margin::symmetric(12, 8))
        .show(ui, |ui| {
            ui.horizontal(|ui| {
                // 左侧：状态图标 + 文本
                ui.horizontal(|ui| {
                    ui.add(
                        egui::Label::new(
                            RichText::new(status_icon).size(28.0).color(status_color),
                        )
                        .selectable(false),
                    );
                    ui.add_space(10.0);
                    ui.vertical(|ui| {
                        ui.add(
                            egui::Label::new(
                                RichText::new(status_title).size(18.0).strong(),
                            )
                            .selectable(false),
                        );
                        ui.add(
                            egui::Label::new(
                                RichText::new(status_subtitle).size(12.0).color(Color32::GRAY),
                            )
                            .selectable(false),
                        );
                    });
                });

                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    // 右侧：按钮组
                    let is_stopped = state.status == ConsoleStatus::Stopped;
                    let is_running = state.status == ConsoleStatus::Running;
                    let is_transitioning =
                        state.status == ConsoleStatus::Starting || state.status == ConsoleStatus::Stopping;

                    let btn_height = 30.0;

                    // 强行停止
                    let kill_enabled = is_running || is_transitioning;
                    let kill_btn = egui::Button::new(
                        RichText::new(lang::t("console_btn_kill", lang)).size(13.0),
                    )
                    .min_size(Vec2::new(90.0, btn_height))
                    .fill(if kill_enabled {
                        Color32::from_rgb(200, 50, 50)
                    } else {
                        Color32::from_rgb(80, 30, 30)
                    });
                    if ui
                        .add_enabled(kill_enabled, kill_btn)
                        .clicked()
                    {
                        *command = Some(ConsoleCommand::ForceStop);
                    }

                    ui.add_space(6.0);

                    // 停止
                    let stop_enabled = is_running;
                    let stop_btn = egui::Button::new(
                        RichText::new(lang::t("console_btn_stop", lang)).size(13.0),
                    )
                    .min_size(Vec2::new(70.0, btn_height))
                    .fill(if stop_enabled {
                        Color32::from_rgb(200, 120, 30)
                    } else {
                        Color32::from_rgb(60, 40, 20)
                    });
                    if ui
                        .add_enabled(stop_enabled, stop_btn)
                        .clicked()
                    {
                        *command = Some(ConsoleCommand::Stop);
                    }

                    ui.add_space(6.0);

                    // 重启
                    let restart_enabled = is_running;
                    let restart_btn = egui::Button::new(
                        RichText::new(lang::t("console_btn_restart", lang)).size(13.0),
                    )
                    .min_size(Vec2::new(70.0, btn_height))
                    .fill(if restart_enabled {
                        Color32::from_rgb(60, 120, 200)
                    } else {
                        Color32::from_rgb(30, 50, 80)
                    });
                    if ui
                        .add_enabled(restart_enabled, restart_btn)
                        .clicked()
                    {
                        *command = Some(ConsoleCommand::Stop);
                        state.pending_restart = true;
                    }

                    ui.add_space(6.0);

                    // 启动
                    let start_enabled = is_stopped;
                    let start_btn = egui::Button::new(
                        RichText::new(lang::t("console_btn_start", lang)).size(13.0),
                    )
                    .min_size(Vec2::new(70.0, btn_height))
                    .fill(if start_enabled {
                        Color32::from_rgb(50, 180, 80)
                    } else {
                        Color32::from_rgb(30, 60, 35)
                    });
                    if ui
                        .add_enabled(start_enabled, start_btn)
                        .clicked()
                    {
                        *command = Some(ConsoleCommand::Start);
                    }
                });
            });
        });

    ui.add_space(8.0);

    // ---- 日志输出区域 ----
    egui::Frame::NONE
        .fill(ui.style().visuals.extreme_bg_color)
        .corner_radius(egui::CornerRadius::same(6))
        .inner_margin(egui::Margin::symmetric(12, 8))
        .show(ui, |ui| {
            // 标题栏 + 操作按钮
            ui.horizontal(|ui| {
                ui.add(
                    egui::Label::new(
                        RichText::new(lang::t("console_log_area", lang)).size(13.0).strong(),
                    )
                    .selectable(false),
                );
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    // 导出按钮
                    let export_btn = egui::Button::new(
                        RichText::new(lang::t("console_btn_export", lang)).size(11.0),
                    );
                    if ui.add_sized([52.0, 22.0], export_btn).clicked() {
                        let txt = state.plain_text();
                        let path = crate::core::env::get_data_dir()
                            .join("console_export.log");
                        if let Err(e) = std::fs::write(&path, &txt) {
                            state.add_log(&format!("[错误] 导出日志失败: {}", e));
                        } else {
                            state.add_log(&format!("[系统] 日志已导出到: {}", path.display()));
                            // 尝试在资源管理器中打开
                            let _ = std::process::Command::new("explorer")
                                .arg("/select,")
                                .arg(path.to_string_lossy().as_ref())
                                .spawn();
                        }
                    }

                    ui.add_space(4.0);

                    // 复制按钮
                    let copy_btn = egui::Button::new(
                        RichText::new(lang::t("console_btn_copy", lang)).size(11.0),
                    );
                    if ui.add_sized([52.0, 22.0], copy_btn).clicked() {
                        let txt = state.plain_text();
                        ui.ctx().copy_text(txt);
                        state.add_log(lang::t("console_log_copied", lang));
                    }

                    ui.add_space(4.0);

                    // 清空按钮
                    let clear_btn = egui::Button::new(
                        RichText::new(lang::t("console_btn_clear", lang)).size(11.0),
                    );
                    if ui.add_sized([52.0, 22.0], clear_btn).clicked() {
                        state.logs.clear();
                        state.add_log(lang::t("console_log_cleared", lang));
                    }
                });
            });
            ui.separator();

            let log_text_height = log_area_height - 36.0;
            egui::ScrollArea::vertical()
                .max_height(log_text_height)
                .auto_shrink([false; 2])
                .stick_to_bottom(true)
                .show(ui, |ui| {
                    let monospace = egui::FontId::monospace(12.0);
                    for line in state.logs.iter() {
                        let segments = parse_ansi_line(line);
                        if segments.len() == 1 && segments[0].1.is_none() {
                            // 无 ANSI 码 → 用前缀判定颜色
                            let color = if line.contains("[错误]") || line.contains("[ERROR]") {
                                Color32::from_rgb(255, 100, 100)
                            } else if line.contains("[警告]") || line.contains("[WARN]") {
                                Color32::from_rgb(255, 200, 80)
                            } else {
                                Color32::from_rgb(180, 200, 220)
                            };
                            ui.add(
                                egui::Label::new(
                                    RichText::new(line).font(monospace.clone()).color(color),
                                )
                                .selectable(false),
                            );
                        } else {
                            // 有 ANSI 码 → 按颜色段渲染
                            ui.horizontal(|ui| {
                                for (text, opt_color) in &segments {
                                    let color = opt_color
                                        .unwrap_or(Color32::from_rgb(180, 200, 220));
                                    ui.add(
                                        egui::Label::new(
                                            RichText::new(text).font(monospace.clone()).color(color),
                                        )
                                        .selectable(false),
                                    );
                                }
                            });
                        }
                    }
                });
        });
}

// ---- ANSI 颜色解析 ----

/// 解析一行中 ANSI 转义序列，返回 (文本, 可选颜色) 的片段列表。
/// 颜色为 None 表示使用默认色。
fn parse_ansi_line(line: &str) -> Vec<(String, Option<Color32>)> {
    let mut segments: Vec<(String, Option<Color32>)> = Vec::new();
    let mut current = String::new();
    let mut current_color: Option<Color32> = None;
    let bytes = line.as_bytes();
    let mut i: usize = 0;

    while i < bytes.len() {
        if bytes[i] == 0x1b && i + 1 < bytes.len() && bytes[i + 1] == b'[' {
            if !current.is_empty() {
                segments.push((std::mem::take(&mut current), current_color));
            }

            i += 2;
            let mut code = String::new();
            while i < bytes.len() && bytes[i] != b'm' {
                if bytes[i].is_ascii_digit() || bytes[i] == b';' {
                    code.push(bytes[i] as char);
                }
                i += 1;
            }
            if i < bytes.len() {
                i += 1;
            }

            current_color = resolve_ansi_code(&code, current_color);
        } else {
            current.push(bytes[i] as char);
            i += 1;
        }
    }

    if !current.is_empty() {
        segments.push((current, current_color));
    }

    segments
}

fn resolve_ansi_code(code: &str, prev: Option<Color32>) -> Option<Color32> {
    for part in code.split(';') {
        let n: u8 = match part.parse() {
            Ok(v) => v,
            Err(_) => continue,
        };
        match n {
            0 => return None,
            1 => {}
            30 => return Some(Color32::from_rgb(40, 40, 40)),
            31 => return Some(Color32::from_rgb(255, 80, 80)),
            32 => return Some(Color32::from_rgb(80, 220, 80)),
            33 => return Some(Color32::from_rgb(220, 200, 60)),
            34 => return Some(Color32::from_rgb(70, 140, 240)),
            35 => return Some(Color32::from_rgb(200, 80, 200)),
            36 => return Some(Color32::from_rgb(60, 200, 200)),
            37 => return Some(Color32::from_rgb(210, 210, 210)),
            90 => return Some(Color32::from_rgb(120, 120, 120)),
            91 => return Some(Color32::from_rgb(255, 120, 120)),
            92 => return Some(Color32::from_rgb(120, 255, 120)),
            93 => return Some(Color32::from_rgb(255, 255, 100)),
            94 => return Some(Color32::from_rgb(100, 160, 255)),
            95 => return Some(Color32::from_rgb(255, 120, 255)),
            96 => return Some(Color32::from_rgb(100, 255, 255)),
            97 => return Some(Color32::from_rgb(255, 255, 255)),
            _ => {}
        }
    }
    prev
}
