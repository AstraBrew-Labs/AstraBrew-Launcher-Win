//! 酒馆配置页面 — 基础设置
//!
//! 布局规则：
//!   单项 → 图标/标题/副标题在左，控件在右
//!   多项 → 图标/标题/副标题在左，控件竖排在下方，每个控件带 label

use eframe::egui;
use egui::{Align, Color32, Frame, Layout, RichText, ScrollArea};

use crate::core::settings::tavern::TavernConfig;
use crate::pages::settings::Language;

// ---------------------------------------------------------------------------
// UI 状态
// ---------------------------------------------------------------------------

pub struct TavernConfigUI {
    pub config: TavernConfig,
    pub just_saved: bool,
    /// IPv4 编辑开始前的快照（获得焦点时记录，失焦校验不通过则还原）
    ipv4_snapshot: String,
    /// IPv6 编辑开始前的快照
    ipv6_snapshot: String,
}

impl TavernConfigUI {
    pub fn new() -> Self {
        Self {
            config: TavernConfig::load(),
            just_saved: false,
            ipv4_snapshot: String::new(),
            ipv6_snapshot: String::new(),
        }
    }

    pub fn save(&self) {
        self.config.save();
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

pub fn render(ui: &mut egui::Ui, state: &mut TavernConfigUI, lang: &Language) {
    // ---------- 标题栏 ----------
    ui.horizontal(|ui| {
        ui.label(RichText::new(egui_phosphor::regular::SLIDERS).size(22.0).color(Color32::from_rgb(37, 99, 235)));
        ui.heading(RichText::new(crate::lang::t("tavern_config", lang)).strong());
        ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
            if ui.button("打开配置文件").clicked() {
                let path = TavernConfig::config_path();
                let _ = std::process::Command::new("explorer")
                    .arg("/select,")
                    .arg(path.to_string_lossy().as_ref())
                    .spawn();
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
                state.just_saved = true;
            }
        });
    });
    ui.separator();
    ui.add_space(4.0);

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
                        toggle_labeled(ui, crate::lang::t("tc_auto_browser", lang), &mut state.config.browser_launch_enabled);
                        ui.add_space(16.0);
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
                                dynamic_list(ui, &mut state.config.whitelist, crate::lang::t("tc_add_ip", lang));
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
