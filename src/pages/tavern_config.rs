//! 酒馆配置页面 — 基础设置
//!
//! 布局规则：
//!   单项 → 图标/标题/副标题在左，控件在右
//!   多项 → 图标/标题/副标题在左，控件竖排在下方，每个控件带 label

use eframe::egui;
use egui::{Align, Color32, Frame, Layout, RichText, ScrollArea};

use crate::core::settings::tavern::{ConfigMode, GenConfigMsg, InstanceInfo, TavernConfig};
use crate::pages::settings::Language;
use crate::Page;

// ---------------------------------------------------------------------------
// 配置生成状态
// ---------------------------------------------------------------------------

pub enum GenConfigStatus {
    Idle,
    Downloading { progress: u64, total: u64 },
    Done,
    Error(String),
}

impl GenConfigStatus {
    pub fn is_downloading(&self) -> bool {
        matches!(self, GenConfigStatus::Downloading { .. })
    }
}

// ---------------------------------------------------------------------------
// UI 状态
// ---------------------------------------------------------------------------

pub struct TavernConfigUI {
    pub config: TavernConfig,
    pub just_saved: bool,
    /// 保存触发时间，用于 3 秒后自动恢复按钮状态
    pub save_time: Option<std::time::Instant>,
    /// 数据模式（当前 vs 全局）
    pub config_mode: ConfigMode,
    /// 全局数据自定义路径
    pub global_data_path: Option<String>,
    /// 当前酒馆实例信息
    pub instance: Option<InstanceInfo>,
    /// 配置文件是否已存在
    pub config_ready: bool,
    /// 生成配置是否成功（用于显示成功提示）
    pub gen_config_success: bool,
    /// 上一帧的完整配置标识 key，用于检测是否需要刷新
    pub last_config_key: String,
    /// IPv4 编辑开始前的快照（获得焦点时记录，失焦校验不通过则还原）
    ipv4_snapshot: String,
    /// IPv6 编辑开始前的快照
    ipv6_snapshot: String,

    // ── 下载相关 ──
    /// 配置生成/下载状态
    pub gen_config_status: GenConfigStatus,
    /// 下载进度接收端
    pub gen_config_rx: Option<std::sync::mpsc::Receiver<GenConfigMsg>>,
    /// GitHub 代理是否开启（由 main.rs 每帧同步）
    pub proxy_enabled: bool,
    /// GitHub 代理 URL（由 main.rs 每帧同步）
    pub proxy_url: String,

    // ── 恢复默认 ──
    /// 是否显示恢复默认二次确认弹窗
    pub show_reset_confirm: bool,

    // ── 服务器模式（用于白名单固定 IP 逻辑）──
    /// 服务器模式是否开启（由 main.rs 每帧同步）
    pub server_mode_enabled: bool,
    /// 服务模式："Lan" 或 "Internet"（由 main.rs 每帧同步）
    pub server_service_mode: String,
}

impl TavernConfigUI {
    pub fn new(config_mode: ConfigMode, instance: Option<InstanceInfo>, global_data_path: Option<String>) -> Self {
        let gdp = global_data_path.as_deref();
        let config_ready = TavernConfig::config_exists(config_mode, instance.as_ref(), gdp);
        let config = if config_ready {
            TavernConfig::load_from_yaml(config_mode, instance.as_ref(), gdp)
                .unwrap_or_default()
        } else {
            TavernConfig::default()
        };
        Self {
            config,
            just_saved: false,
            save_time: None,
            config_mode,
            global_data_path,
            instance,
            config_ready,
            gen_config_success: false,
            last_config_key: String::new(),
            ipv4_snapshot: String::new(),
            ipv6_snapshot: String::new(),
            gen_config_status: GenConfigStatus::Idle,
            gen_config_rx: None,
            proxy_enabled: false,
            proxy_url: String::new(),
            show_reset_confirm: false,
            server_mode_enabled: false,
            server_service_mode: String::new(),
        }
    }

    /// 构建当前配置的唯一标识 key（模式 + 实例 + 自定义路径）
    pub fn config_key(&self) -> String {
        let gdp = self.global_data_path.as_deref().unwrap_or("");
        match (self.config_mode, &self.instance) {
            (ConfigMode::Current, Some(inst)) => {
                format!("Current:{}:{}", 
                    inst.instance_type, 
                    inst.path.as_deref().unwrap_or("builtin"))
            }
            (ConfigMode::Current, None) => "Current:builtin:".to_string(),
            (ConfigMode::Global, _) => format!("Global:{}", gdp),
        }
    }

    /// 重新扫描配置文件状态并加载；仅在 key 变化或强制刷新时调用
    pub fn refresh(&mut self) {
        let new_key = self.config_key();
        let gdp = self.global_data_path.as_deref();
        self.config_ready = TavernConfig::config_exists(self.config_mode, self.instance.as_ref(), gdp);
        if self.config_ready {
            self.config = TavernConfig::load_from_yaml(self.config_mode, self.instance.as_ref(), gdp)
                .unwrap_or_default();
        }
        self.last_config_key = new_key;
    }

    pub fn save(&mut self) {
        let gdp = self.global_data_path.as_deref();
        self.config.save_to_yaml(self.config_mode, self.instance.as_ref(), gdp);
        self.just_saved = true;
        self.save_time = Some(std::time::Instant::now());
    }
}

// ---------------------------------------------------------------------------
// 布局组件
// ---------------------------------------------------------------------------

/// 单项设置行：左侧图标 + 标题/副标题，右侧一个控件
fn single_row(
    ui: &mut egui::Ui,
    icon: &str,
    title: &str,
    desc: &str,
    add_content: impl FnOnce(&mut egui::Ui),
) {
    ui.add_space(8.0);
    ui.horizontal(|ui| {
        ui.add_sized([30.0, 30.0], egui::Label::new(RichText::new(icon).size(20.0)));
        ui.vertical(|ui| {
            ui.add_space(2.0);
            ui.label(RichText::new(title).size(14.0).strong());
            if !desc.is_empty() {
                ui.label(RichText::new(desc).color(Color32::GRAY).size(12.0));
            }
        });
        ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
            add_content(ui);
        });
    });
}

/// 多项设置行标题：左侧图标 (30px) + 标题/副标题
fn multi_row_title(ui: &mut egui::Ui, icon: &str, title: &str, desc: &str) {
    ui.add_space(8.0);
    ui.horizontal(|ui| {
        // 图标固定 30px 宽
        ui.add_sized([30.0, 30.0], egui::Label::new(RichText::new(icon).size(20.0)));
        ui.vertical(|ui| {
            ui.add_space(2.0);
            ui.label(RichText::new(title).size(14.0).strong());
            if !desc.is_empty() {
                ui.label(RichText::new(desc).color(Color32::GRAY).size(12.0));
            }
        });
    });
}

/// 多项设置的子控件——与标题行左对齐，内部自动换行
fn multi_row_controls(ui: &mut egui::Ui, add_content: impl FnOnce(&mut egui::Ui)) {
    ui.add_space(4.0);
    ui.horizontal_wrapped(|ui| {
        // 30px 占位列：确保与 multi_row_title 图标列对齐
        ui.add_sized([30.0, 0.0], egui::Label::new(""));
        add_content(ui);
    });
}

/// 带 label 的 toggle
fn toggle_labeled(ui: &mut egui::Ui, label: &str, value: &mut bool) {
    ui.horizontal(|ui| {
        ui.add(crate::ui::switch::toggle(value));
        ui.label(RichText::new(label).size(13.0));
    });
}

/// 带 label 的文本输入
fn text_labeled(ui: &mut egui::Ui, label: &str, value: &mut String, hint: &str) {
    ui.horizontal(|ui| {
        ui.label(RichText::new(label).size(12.0).color(Color32::GRAY));
        ui.add(egui::TextEdit::singleline(value).hint_text(hint).desired_width(160.0));
    });
}

/// IPv4 地址输入：获得焦点时快照，失焦校验不通过则还原
fn ipv4_input(ui: &mut egui::Ui, label: &str, value: &mut String, snapshot: &mut String) {
    use std::net::Ipv4Addr;
    ui.horizontal(|ui| {
        ui.label(RichText::new(label).size(12.0).color(Color32::GRAY));
        let resp = ui.add(egui::TextEdit::singleline(value).desired_width(160.0));
        if resp.gained_focus() {
            *snapshot = value.clone();
        }
        if resp.lost_focus() && value.parse::<Ipv4Addr>().is_err() {
            *value = snapshot.clone();
        }
    });
}

/// IPv6 地址输入：获得焦点时快照，失焦校验（自动忽略方括号），不通过则还原
fn ipv6_input(ui: &mut egui::Ui, label: &str, value: &mut String, snapshot: &mut String) {
    use std::net::Ipv6Addr;
    ui.horizontal(|ui| {
        ui.label(RichText::new(label).size(12.0).color(Color32::GRAY));
        let resp = ui.add(egui::TextEdit::singleline(value).desired_width(160.0));
        if resp.gained_focus() {
            *snapshot = value.clone();
        }
        if resp.lost_focus() {
            let stripped = value.trim().trim_start_matches('[').trim_end_matches(']');
            if stripped.parse::<Ipv6Addr>().is_err() {
                *value = snapshot.clone();
            }
        }
    });
}

/// 子标题
fn sub_label(ui: &mut egui::Ui, label: &str) {
    ui.add_space(6.0);
    ui.label(RichText::new(label).size(13.0).strong().color(Color32::GRAY));
    ui.add_space(2.0);
}

/// 动态列表
fn dynamic_list(ui: &mut egui::Ui, items: &mut Vec<String>, add_label: &str) {
    let mut to_remove: Option<usize> = None;
    for (i, item) in items.iter_mut().enumerate() {
        ui.horizontal(|ui| {
            ui.text_edit_singleline(item);
            if ui
                .add_sized([28.0, 28.0], egui::Button::new(
                    RichText::new("✕").size(14.0).color(Color32::from_rgb(239, 68, 68)),
                ))
                .clicked()
            {
                to_remove = Some(i);
            }
        });
    }
    if let Some(i) = to_remove {
        items.remove(i);
    }
    ui.add_space(2.0);
    if ui
        .add_sized([ui.available_width(), 32.0], egui::Button::new(
            RichText::new(format!("+  {}", add_label)).size(12.0).color(Color32::from_rgb(37, 99, 235)),
        ))
        .clicked()
    {
        items.push(String::new());
    }
}

// ---------------------------------------------------------------------------
// 主渲染
// ---------------------------------------------------------------------------

pub fn render(ui: &mut egui::Ui, state: &mut TavernConfigUI, lang: &Language, current_page: &mut Page, is_desktop_mode: bool, is_server_mode: bool) {
    // 保存成功提示 3 秒后自动恢复按钮状态
    if state.just_saved {
        if let Some(t) = state.save_time {
            if t.elapsed().as_secs_f32() >= 3.0 {
                state.just_saved = false;
                state.save_time = None;
            } else {
                ui.ctx().request_repaint();
            }
        } else {
            state.just_saved = false;
        }
    }

    // 生成配置成功提示 3 秒后自动恢复
    if state.gen_config_success {
        if let Some(t) = state.save_time {
            if t.elapsed().as_secs_f32() >= 3.0 {
                state.gen_config_success = false;
                state.save_time = None;
            } else {
                ui.ctx().request_repaint();
            }
        } else {
            state.gen_config_success = false;
        }
    }

    // ── 轮询下载进度 ──
    {
        let mut clear_rx = false;
        let mut needs_refresh = false;
        if let Some(ref rx) = state.gen_config_rx {
            while let Ok(msg) = rx.try_recv() {
                match msg {
                    GenConfigMsg::Progress(downloaded, total) => {
                        state.gen_config_status = GenConfigStatus::Downloading { progress: downloaded, total };
                    }
                    GenConfigMsg::FallingBack => {
                        // 保持 downloading 状态，后续 progress 会更新
                    }
                    GenConfigMsg::Done => {
                        clear_rx = true;
                        needs_refresh = true;
                        state.gen_config_status = GenConfigStatus::Done;
                        state.gen_config_success = true;
                        state.save_time = Some(std::time::Instant::now());
                    }
                    GenConfigMsg::Error(e) => {
                        clear_rx = true;
                        state.gen_config_status = GenConfigStatus::Error(e);
                    }
                }
            }
            ui.ctx().request_repaint();
        }
        if clear_rx {
            state.gen_config_rx = None;
        }
        if needs_refresh {
            state.refresh();
        }
    }

    // ── 未选择酒馆实例遮罩（仅 Current 模式生效）──
    if state.instance.is_none() && state.config_mode == ConfigMode::Current {
        let content_rect = ui.max_rect();
        ui.painter().rect_filled(content_rect, 0.0, Color32::from_black_alpha(200));

        let center = content_rect.center();
        let overlay_rect = egui::Rect::from_center_size(
            center,
            egui::vec2(340.0, 170.0),
        );

        let mut child_ui = ui.new_child(egui::UiBuilder::new()
            .max_rect(overlay_rect)
            .layout(Layout::top_down(Align::Center)));

        child_ui.add_space(20.0);
        child_ui.label(
            RichText::new(crate::lang::t("tc_no_instance_title", lang))
                .size(18.0)
                .color(Color32::from_rgb(255, 200, 100)),
        );
        child_ui.add_space(8.0);
        child_ui.label(
            RichText::new(crate::lang::t("tc_no_instance_desc", lang))
                .size(13.0)
                .color(Color32::LIGHT_GRAY),
        );
        child_ui.add_space(16.0);

        if child_ui.add_sized(
            [200.0, 32.0],
            egui::Button::new(RichText::new(crate::lang::t("tc_goto_version_mgmt", lang)).size(15.0)),
        ).clicked() {
            *current_page = Page::VersionManage;
        }

        return;
    }

    // ── 未初始化遮罩 ──
    if !state.config_ready {
        // 半透明遮罩覆盖整个页面
        let content_rect = ui.max_rect();
        ui.painter().rect_filled(content_rect, 0.0, Color32::from_black_alpha(200));

        // 居中提示
        let center = content_rect.center();
        let overlay_rect = egui::Rect::from_center_size(
            center,
            egui::vec2(360.0, 200.0),
        );

        let mut child_ui = ui.new_child(egui::UiBuilder::new()
            .max_rect(overlay_rect)
            .layout(Layout::top_down(Align::Center)));

        child_ui.add_space(20.0);
        child_ui.label(
            RichText::new("⚠ 酒馆实例未初始化")
                .size(18.0)
                .color(Color32::from_rgb(255, 200, 100)),
        );
        child_ui.add_space(8.0);

        match &state.gen_config_status {
            GenConfigStatus::Idle => {
                // 默认按钮：生成配置
                child_ui.label(
                    RichText::new("目标配置文件不存在，点击下方按钮生成默认配置")
                        .size(13.0)
                        .color(Color32::LIGHT_GRAY),
                );
                child_ui.add_space(16.0);

                if child_ui.add_sized(
                    [200.0, 32.0],
                    egui::Button::new(RichText::new(format!("🔄 {}", crate::lang::t("tc_gen_config_btn", lang))).size(15.0)),
                ).clicked() {
                    let target = TavernConfig::resolve_path(state.config_mode, state.instance.as_ref(), state.global_data_path.as_deref());

                    // 确定模板路径
                    // 全局模式：data/default/sillytavern/config.yaml
                    // 独立模式：<instance>/default/config.yaml
                    let template = match (state.config_mode, state.instance.as_ref()) {
                        (ConfigMode::Current, Some(inst)) => {
                            let inst_path = inst.path.as_deref().unwrap_or("");
                            if inst_path.is_empty() {
                                std::path::PathBuf::new()
                            } else {
                                std::path::PathBuf::from(inst_path).join("default").join("config.yaml")
                            }
                        }
                        _ => TavernConfig::template_path(),
                    };

                    if template.exists() {
                        if TavernConfig::copy_template_to(&template, &target) {
                            TavernConfig::optimize_after_generate(&target);
                            state.gen_config_status = GenConfigStatus::Done;
                            state.refresh();
                            state.gen_config_success = true;
                            state.save_time = Some(std::time::Instant::now());
                        } else {
                            state.gen_config_status = GenConfigStatus::Error("复制模板失败".to_string());
                        }
                    } else {
                        // 模板不存在，启动网络下载
                        state.gen_config_status = GenConfigStatus::Downloading { progress: 0, total: 0 };
                        let (tx, rx) = std::sync::mpsc::channel();
                        state.gen_config_rx = Some(rx);
                        TavernConfig::start_download_template(
                            target,
                            template,
                            state.proxy_enabled,
                            state.proxy_url.clone(),
                            tx,
                        );
                    }
                }
            }
            GenConfigStatus::Downloading { progress, total } => {
                // 下载中：进度条
                child_ui.label(
                    RichText::new(crate::lang::t("tc_downloading", lang))
                        .size(13.0)
                        .color(Color32::LIGHT_GRAY),
                );
                child_ui.add_space(12.0);

                let bar_width = 240.0;
                let bar_height = 8.0;
                let (bar_rect, _) = child_ui.allocate_exact_size(
                    egui::vec2(bar_width, bar_height),
                    egui::Sense::hover(),
                );

                // 背景
                child_ui.painter().rect_filled(
                    bar_rect,
                    4.0,
                    Color32::from_gray(60),
                );

                // 进度
                if *total > 0 {
                    let fraction = (*progress as f32) / (*total as f32).min(1.0);
                    let fill_w = bar_width * fraction;
                    child_ui.painter().rect_filled(
                        egui::Rect::from_min_size(bar_rect.min, egui::vec2(fill_w, bar_height)),
                        4.0,
                        Color32::from_rgb(37, 99, 235),
                    );
                }

                child_ui.add_space(8.0);
                let info = if *total > 0 {
                    let pct = ((*progress as f64) / (*total as f64) * 100.0) as u32;
                    format!("{}% ({} / {} KB)", pct, progress / 1024, total / 1024)
                } else {
                    format!("{} KB", progress / 1024)
                };
                child_ui.label(
                    RichText::new(info)
                        .size(11.0)
                        .color(Color32::GRAY),
                );
            }
            GenConfigStatus::Done => {
                // 理论上不会走到这里（config_ready 变为 true 后遮罩消失）
                // 但留做防御
            }
            GenConfigStatus::Error(e) => {
                // 下载失败
                child_ui.label(
                    RichText::new(format!("{}: {}", crate::lang::t("tc_download_error", lang), e))
                        .size(13.0)
                        .color(Color32::from_rgb(255, 100, 100)),
                );
                child_ui.add_space(16.0);

                if child_ui.add_sized(
                    [200.0, 32.0],
                    egui::Button::new(RichText::new(format!("🔄 {}", crate::lang::t("tc_download_retry", lang))).size(15.0)),
                ).clicked() {
                    state.gen_config_status = GenConfigStatus::Idle;
                }
            }
        }

        return; // 后续 UI 不渲染
    }

    // ── 生成成功 Toast ──
    if state.gen_config_success {
        let painter = ui.ctx().layer_painter(egui::LayerId::new(egui::Order::Tooltip, egui::Id::new("gen_toast")));
        let screen_rect = ui.ctx().content_rect();
        let font_id = egui::FontId::proportional(15.0);
        let text = "✓ 默认配置已生成，请刷新页面查看";
        let text_galley = painter.layout_no_wrap(text.to_string(), font_id, Color32::from_rgb(16, 185, 129));
        let rect = egui::Rect::from_center_size(
            egui::pos2(screen_rect.center().x, screen_rect.max.y - 50.0),
            text_galley.size() + egui::vec2(24.0, 12.0),
        );
        painter.rect(rect, 8.0, Color32::from_black_alpha(220), egui::Stroke::new(1.0, Color32::from_rgb(16, 185, 129).linear_multiply(0.5)), egui::StrokeKind::Middle);
        painter.galley(egui::pos2(rect.center().x - text_galley.size().x / 2.0, rect.center().y - text_galley.size().y / 2.0), text_galley, Color32::from_rgb(16, 185, 129));
    }

    // ---------- 标题栏 ----------
    ui.horizontal(|ui| {
        ui.label(RichText::new(egui_phosphor::regular::SLIDERS).size(22.0).color(Color32::from_rgb(37, 99, 235)));
        ui.heading(RichText::new(crate::lang::t("tavern_config", lang)).strong());
        ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
            if ui.button(crate::lang::t("tc_open_config_file", lang)).clicked() {
                let path = TavernConfig::resolve_path(state.config_mode, state.instance.as_ref(), state.global_data_path.as_deref());
                // Windows: 在资源管理器中打开并选中文件
                let _ = std::process::Command::new("explorer")
                    .arg("/select,")
                    .arg(path.to_string_lossy().as_ref())
                    .spawn();
            }
            ui.add_space(8.0);
            if ui.button(crate::lang::t("tc_reset_default", lang)).clicked() {
                state.show_reset_confirm = true;
            }
            ui.add_space(8.0);
            let label = if state.just_saved { "✓ 已保存" } else { "保存配置" };
            let color = if state.just_saved {
                Color32::from_rgb(16, 185, 129)
            } else {
                Color32::WHITE
            };
            if ui.add_sized([120.0, 28.0], egui::Button::new(RichText::new(label).color(color))).clicked() {
                state.save();
            }
            ui.add_space(4.0);
            if ui.add_sized([28.0, 28.0], egui::Button::new(RichText::new(egui_phosphor::regular::ARROWS_CLOCKWISE).size(16.0)))
                .on_hover_text("从文件重新加载配置")
                .clicked()
            {
                state.refresh();
            }
        });
    });
    ui.separator();
    ui.add_space(4.0);

    // ---------- 恢复默认确认弹窗 ----------
    if state.show_reset_confirm {
        let ctx = ui.ctx().clone();
        egui::Window::new(crate::lang::t("tc_reset_confirm_title", lang))
            .collapsible(false)
            .resizable(false)
            .anchor(egui::Align2::CENTER_CENTER, egui::vec2(0.0, 0.0))
            .show(&ctx, |ui| {
                ui.set_min_width(300.0);
                ui.label(crate::lang::t("tc_reset_confirm_desc", lang));
                ui.add_space(16.0);
                ui.horizontal(|ui| {
                    ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                        if ui.button(crate::lang::t("tc_confirm", lang)).clicked() {
                            state.show_reset_confirm = false;
                            // 用模板覆盖当前配置
                            let target = TavernConfig::resolve_path(state.config_mode, state.instance.as_ref(), state.global_data_path.as_deref());
                            let template = match (state.config_mode, state.instance.as_ref()) {
                                (ConfigMode::Current, Some(inst)) => {
                                    let inst_path = inst.path.as_deref().unwrap_or("");
                                    if inst_path.is_empty() {
                                        std::path::PathBuf::new()
                                    } else {
                                        std::path::PathBuf::from(inst_path).join("default").join("config.yaml")
                                    }
                                }
                                _ => TavernConfig::template_path(),
                            };
                            if TavernConfig::copy_template_to(&template, &target) {
                                state.refresh();
                            }
                        }
                        if ui.button(crate::lang::t("tc_cancel", lang)).clicked() {
                            state.show_reset_confirm = false;
                        }
                    });
                });
            });
    }

    // ---------- 内容 ----------
    ScrollArea::vertical()
        .auto_shrink([false, false])
        .show(ui, |ui| {
            // ================================================================
            // 基础设置
            // ================================================================
            ui.horizontal(|ui| {
                ui.label(RichText::new(egui_phosphor::regular::GEAR).size(18.0));
                ui.heading(RichText::new("基础设置").strong());
            });
            ui.add_space(5.0);

            Frame::NONE
                .fill(ui.visuals().faint_bg_color)
                .corner_radius(8.0)
                .inner_margin(15.0)
                .show(ui, |ui| {
                    // ====== 服务端口 (单项) ======
                    single_row(
                        ui,
                        egui_phosphor::regular::HARD_DRIVES,
                        crate::lang::t("tc_server_port", lang),
                        "SillyTavern 服务监听端口 (1–65535)",
                        |ui| {
                            let mut port = state.config.port as i64;
                            ui.add_sized(
                                [100.0, 24.0],
                                egui::DragValue::new(&mut port).range(1..=65535).speed(1),
                            );
                            state.config.port = port.clamp(1, 65535) as u16;
                        },
                    );

                    // ------

                    // ====== 监听地址 (多项) ======
                    multi_row_title(
                        ui,
                        egui_phosphor::regular::GLOBE,
                        "监听地址",
                        "IPv4 与 IPv6 网络接口绑定地址",
                    );
                    multi_row_controls(ui, |ui| {
                        ipv4_input(ui, "IPv4", &mut state.config.listen_address.ipv4, &mut state.ipv4_snapshot);
                        ui.add_space(20.0);
                        ipv6_input(ui, "IPv6", &mut state.config.listen_address.ipv6, &mut state.ipv6_snapshot);
                    });

                    // ------

                    // ====== 网络选项 (多项) ======
                    multi_row_title(
                        ui,
                        egui_phosphor::regular::BROADCAST,
                        "网络选项",
                        "局域网访问、IPv4/IPv6 协议与 DNS 偏好",
                    );
                    multi_row_controls(ui, |ui| {
                        toggle_labeled(ui, crate::lang::t("tc_allow_lan", lang), &mut state.config.listen);
                        ui.add_space(16.0);
                        toggle_labeled(ui, crate::lang::t("tc_enable_ipv4", lang), &mut state.config.protocol.ipv4);
                        ui.add_space(16.0);
                        toggle_labeled(ui, crate::lang::t("tc_enable_ipv6", lang), &mut state.config.protocol.ipv6);
                        ui.add_space(16.0);
                        toggle_labeled(ui, crate::lang::t("tc_dns_prefer_ipv6", lang), &mut state.config.dns_prefer_ipv6);
                    });

                    // ------

                    // ====== 心跳与浏览器 (多项) ======
                    multi_row_title(
                        ui,
                        egui_phosphor::regular::HEARTBEAT,
                        "心跳与浏览器",
                        "心跳检测间隔，以及启动后是否自动打开浏览器",
                    );
                    multi_row_controls(ui, |ui| {
                        // 桌面模式/服务器模式下不显示（命令行 --browserLaunchEnabled false 强制关闭）
                        if !is_desktop_mode && !is_server_mode {
                            toggle_labeled(ui, crate::lang::t("tc_auto_browser", lang), &mut state.config.browser_launch_enabled);
                            ui.add_space(16.0);
                        }
                        ui.horizontal(|ui| {
                            ui.label(RichText::new("心跳间隔").size(12.0).color(Color32::GRAY));
                            let mut hb = state.config.heartbeat_interval as i64;
                            ui.add(egui::DragValue::new(&mut hb).range(0..=3600000).speed(100));
                            state.config.heartbeat_interval = hb.max(0) as u64;
                            ui.label(RichText::new("ms (0 = 禁用)").size(11.0).color(Color32::GRAY));
                        });
                        ui.add_space(16.0);
                        ui.horizontal(|ui| {
                            ui.label(RichText::new("浏览器类型").size(12.0).color(Color32::GRAY));
                            egui::ComboBox::from_id_salt("browser_type")
                                .selected_text(browser_label(&state.config.browser_type))
                                .width(120.0)
                                .show_ui(ui, |ui| {
                                    for opt in &["default", "chrome", "firefox", "edge"] {
                                        ui.selectable_value(
                                            &mut state.config.browser_type,
                                            opt.to_string(),
                                            browser_label(opt),
                                        );
                                    }
                                });
                        });
                    });

                    // ==================== 安全分割线 ====================
                    ui.add_space(8.0);
                    ui.separator();
                    sub_label(ui, "安全与账户");

                    // ====== 认证方式 (多项) ======
                    multi_row_title(
                        ui,
                        egui_phosphor::regular::SHIELD_CHECK,
                        "认证方式",
                        "基础认证或用户账户系统",
                    );
                    multi_row_controls(ui, |ui| {
                        toggle_labeled(ui, crate::lang::t("tc_basic_auth", lang), &mut state.config.basic_auth_mode);
                        ui.add_space(16.0);
                        toggle_labeled(ui, crate::lang::t("tc_user_accounts", lang), &mut state.config.enable_user_accounts);

                        if state.config.basic_auth_mode {
                            ui.add_space(8.0);
                            text_labeled(ui, "用户名", &mut state.config.basic_auth_user.username, "user");
                            ui.add_space(16.0);
                            text_labeled(ui, "密码", &mut state.config.basic_auth_user.password, "••••");
                        }
                    });

                    // ------

                    // ====== 其他安全选项 (多项) ======
                    multi_row_title(
                        ui,
                        egui_phosphor::regular::EYE_SLASH,
                        "其他安全选项",
                        "隐蔽登录、每用户认证、IP 白名单",
                    );
                    multi_row_controls(ui, |ui| {
                        toggle_labeled(ui, crate::lang::t("tc_discreet_login", lang), &mut state.config.enable_discreet_login);
                        ui.add_space(16.0);
                        toggle_labeled(ui, crate::lang::t("tc_per_user_auth", lang), &mut state.config.per_user_basic_auth);
                        ui.add_space(16.0);
                        toggle_labeled(ui, crate::lang::t("tc_whitelist_mode", lang), &mut state.config.whitelist_mode);
                    });

                    // 白名单 IP 列表：独立于 wrapped 流，始终垂直排列
                    if state.config.whitelist_mode {
                        ui.add_space(4.0);
                        ui.horizontal(|ui| {
                            ui.add_sized([30.0, 0.0], egui::Label::new(""));
                            ui.vertical(|ui| {
                                ui.label(RichText::new(crate::lang::t("tc_whitelist_ips", lang)).size(12.0).strong());
                                ui.add_space(2.0);

                                // 固定白名单 IP（根据服务器模式/服务模式）
                                let fixed = TavernConfig::fixed_whitelist(
                                    state.server_mode_enabled,
                                    &state.server_service_mode,
                                );
                                let fixed_set: std::collections::HashSet<String> =
                                    fixed.iter().cloned().collect();

                                // 所有可被系统保留的 IP（跨三种模式）
                                let all_reserved: std::collections::HashSet<String> =
                                    TavernConfig::all_reserved_whitelist_ips()
                                        .into_iter()
                                        .collect();

                                // 1) 移除旧模式专属的系统保留 IP（当前 fixed 里没有的 → 删除）
                                state.config.whitelist.retain(|ip| {
                                    if all_reserved.contains(ip) {
                                        fixed_set.contains(ip) // 仅保留当前模式需要的
                                    } else {
                                        true // 用户自添 IP 永远保留
                                    }
                                });

                                // 2) 去重（安全网：防止历史上出现重复条目）
                                {
                                    let mut seen = std::collections::HashSet::new();
                                    state.config.whitelist.retain(|ip| seen.insert(ip.clone()));
                                }

                                // 3) 确保当前 fixed IP 都在列表中（缺失则追加）
                                {
                                    let mut missing: Vec<String> = Vec::new();
                                    for ip in &fixed {
                                        if !state.config.whitelist.contains(ip) {
                                            missing.push(ip.clone());
                                        }
                                    }
                                    for ip in missing {
                                        state.config.whitelist.push(ip);
                                    }
                                }

                                if !fixed.is_empty() && state.server_mode_enabled {
                                    ui.label(
                                        RichText::new("🔒 固定白名单（由服务器模式自动管理）")
                                            .size(11.0)
                                            .color(Color32::from_rgb(100, 100, 255)),
                                    );
                                }

                                // 渲染白名单：固定条目只读，用户条目可编辑
                                let mut to_remove: Option<usize> = None;
                                for (idx, ip) in state.config.whitelist.iter_mut().enumerate() {
                                    let is_fixed = fixed_set.contains(ip.as_str());
                                    ui.horizontal(|ui| {
                                        if is_fixed {
                                            let mut display = ip.clone();
                                            ui.add_enabled(
                                                false,
                                                egui::TextEdit::singleline(&mut display)
                                                    .desired_width(220.0),
                                            );
                                            ui.label(
                                                RichText::new("固定")
                                                    .size(11.0)
                                                    .color(Color32::GRAY),
                                            );
                                        } else {
                                            ui.text_edit_singleline(ip);
                                            if ui
                                                .add_sized(
                                                    [28.0, 28.0],
                                                    egui::Button::new(
                                                        RichText::new("✕")
                                                            .size(14.0)
                                                            .color(Color32::from_rgb(239, 68, 68)),
                                                    ),
                                                )
                                                .clicked()
                                            {
                                                to_remove = Some(idx);
                                            }
                                        }
                                    });
                                }
                                if let Some(idx) = to_remove {
                                    state.config.whitelist.remove(idx);
                                }

                                // 添加按钮（防重复）
                                ui.add_space(2.0);
                                if ui
                                    .add_sized(
                                        [ui.available_width(), 32.0],
                                        egui::Button::new(
                                            RichText::new(format!(
                                                "+  {}",
                                                crate::lang::t("tc_add_ip", lang)
                                            ))
                                            .size(12.0)
                                            .color(Color32::from_rgb(37, 99, 235)),
                                        ),
                                    )
                                    .clicked()
                                {
                                    let exists = state.config.whitelist.contains(&String::new())
                                        || fixed_set.contains(&String::new());
                                    if !exists {
                                        state.config.whitelist.push(String::new());
                                    }
                                }
                            });
                        });
                    }

                    // ------

                    // ====== 主机白名单 (多项) ======
                    multi_row_title(
                        ui,
                        egui_phosphor::regular::LIST,
                        crate::lang::t("tc_host_whitelist", lang),
                        "主机级别白名单控制",
                    );
                    multi_row_controls(ui, |ui| {
                        toggle_labeled(ui, crate::lang::t("tc_host_wl_enabled", lang), &mut state.config.host_whitelist.enabled);
                        ui.add_space(16.0);
                        toggle_labeled(ui, crate::lang::t("tc_host_wl_scan", lang), &mut state.config.host_whitelist.scan);
                    });

                    // 主机列表：独立于 wrapped 流，始终垂直排列
                    if state.config.host_whitelist.enabled {
                        ui.add_space(4.0);
                        ui.horizontal(|ui| {
                            ui.add_sized([30.0, 0.0], egui::Label::new(""));
                            ui.vertical(|ui| {
                                dynamic_list(ui, &mut state.config.host_whitelist.hosts, crate::lang::t("tc_add_host", lang));
                            });
                        });
                    }

                    // ------

                    // ====== 导入域名白名单 (多项) ======
                    multi_row_title(
                        ui,
                        egui_phosphor::regular::GLOBE_SIMPLE,
                        crate::lang::t("tc_import_domains", lang),
                        "可从外部导入的白名单域名",
                    );
                    // 导入域名列表始终垂直排列
                    ui.add_space(4.0);
                    ui.horizontal(|ui| {
                        ui.add_sized([30.0, 0.0], egui::Label::new(""));
                        ui.vertical(|ui| {
                            dynamic_list(ui, &mut state.config.whitelist_import_domains, crate::lang::t("tc_add_domain", lang));
                        });
                    });
                });
        });

    ui.add_space(40.0);
}

// ---------------------------------------------------------------------------
// 辅助
// ---------------------------------------------------------------------------

fn browser_label(val: &str) -> &str {
    match val {
        "default" => "系统默认",
        "chrome" => "Chrome",
        "firefox" => "Firefox",
        "edge" => "Edge",
        _ => val,
    }
}
