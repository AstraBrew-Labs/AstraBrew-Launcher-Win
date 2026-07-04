use eframe::egui;
use std::sync::{LazyLock, Mutex};

use crate::lang;
use crate::pages::settings::{Language, SettingsState};

#[derive(PartialEq, Clone, Copy)]
pub enum ReverseProxyTab {
    BasicSettings,
    SslSettings,
}

impl Default for ReverseProxyTab {
    fn default() -> Self {
        Self::BasicSettings
    }
}

pub struct ReverseProxyPopupState {
    pub show: bool,
    pub tab: ReverseProxyTab,
}

pub static REVERSE_PROXY_POPUP: LazyLock<Mutex<ReverseProxyPopupState>> = LazyLock::new(|| {
    Mutex::new(ReverseProxyPopupState {
        show: false,
        tab: ReverseProxyTab::BasicSettings,
    })
});

pub fn render_reverse_proxy_popup(
    ctx: &egui::Context,
    state: &mut SettingsState,
    lang_state: &Language,
) {
    // 先获取弹窗状态（不持有锁进入渲染）
    let (should_show, mut tab) = {
        let popup = REVERSE_PROXY_POPUP.lock().unwrap();
        if !popup.show {
            return;
        }
        (true, popup.tab)
    };

    if !should_show {
        return;
    }

    let mut open = true;

    egui::Window::new(lang::t("rp_popup_title", lang_state))
        .collapsible(false)
        .resizable(true)
        .min_width(680.0)
        .min_height(500.0)
        .open(&mut open)
        .show(ctx, |ui| {
            // === 顶部：总开关 ===
            ui.horizontal(|ui| {
                ui.label(egui::RichText::new(lang::t("rp_master_switch", lang_state)).size(14.0).strong());
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    ui.add(crate::ui::switch::toggle(&mut state.reverse_proxy_enabled));
                });
            });
            ui.separator();

            // === Tab 切换 ===
            ui.horizontal(|ui| {
                ui.selectable_value(&mut tab, ReverseProxyTab::BasicSettings, lang::t("rp_tab_basic", lang_state));
                ui.selectable_value(&mut tab, ReverseProxyTab::SslSettings, lang::t("rp_tab_ssl", lang_state));
            });
            ui.separator();

            // 总开关关闭时，tab 内容灰色禁用
            let enabled = state.reverse_proxy_enabled;
            ui.add_enabled_ui(enabled, |ui| {
                match tab {
                    ReverseProxyTab::BasicSettings => {
                        render_basic_settings(ui, state, lang_state);
                    }
                    ReverseProxyTab::SslSettings => {
                        render_ssl_settings(ui, state, lang_state);
                    }
                }
            });
        });

    // 同步弹窗状态
    {
        let mut popup = REVERSE_PROXY_POPUP.lock().unwrap();
        popup.show = open;
        popup.tab = tab;
    }
}

fn render_basic_settings(ui: &mut egui::Ui, state: &mut SettingsState, lang_state: &Language) {
    ui.add_space(10.0);

    // 绑定域名
    ui.horizontal(|ui| {
        ui.vertical(|ui| {
            ui.label(egui::RichText::new(lang::t("rp_domain", lang_state)).size(13.0).strong());
            ui.label(
                egui::RichText::new(lang::t("rp_domain_desc", lang_state))
                    .color(egui::Color32::GRAY)
                    .size(11.0),
            );
        });
        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
            ui.add(
                egui::TextEdit::singleline(&mut state.reverse_proxy_domain)
                    .hint_text(lang::t("rp_domain_hint", lang_state))
                    .desired_width(300.0),
            );
        });
    });
    ui.add_space(14.0);

    // 代理端口
    ui.horizontal(|ui| {
        ui.vertical(|ui| {
            ui.label(egui::RichText::new(lang::t("rp_http_port", lang_state)).size(13.0).strong());
            ui.label(
                egui::RichText::new(lang::t("rp_http_port_desc", lang_state))
                    .color(egui::Color32::GRAY)
                    .size(11.0),
            );
        });
        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
            ui.horizontal(|ui| {
                ui.label(egui::RichText::new("HTTP").size(12.0));
                ui.add(
                    egui::TextEdit::singleline(&mut state.reverse_proxy_http_port)
                        .desired_width(80.0),
                );
                ui.add_space(16.0);
                ui.label(egui::RichText::new("HTTPS").size(12.0));
                ui.add(
                    egui::TextEdit::singleline(&mut state.reverse_proxy_https_port)
                        .desired_width(80.0),
                );
            });
        });
    });
}

fn render_ssl_settings(ui: &mut egui::Ui, state: &mut SettingsState, lang_state: &Language) {
    ui.add_space(10.0);

    // === 启用 SSL ===
    ui.horizontal(|ui| {
        ui.vertical(|ui| {
            ui.label(egui::RichText::new(lang::t("rp_ssl_enabled", lang_state)).size(13.0).strong());
        });
        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
            ui.add(crate::ui::switch::toggle(&mut state.reverse_proxy_ssl_enabled));
        });
    });
    ui.add_space(10.0);

    // === 强制 HTTPS ===
    ui.add_enabled_ui(state.reverse_proxy_ssl_enabled, |ui| {
        ui.horizontal(|ui| {
            ui.vertical(|ui| {
                ui.label(egui::RichText::new(lang::t("rp_ssl_force_https", lang_state)).size(13.0).strong());
                ui.label(
                    egui::RichText::new(lang::t("rp_ssl_force_https_desc", lang_state))
                        .color(egui::Color32::GRAY)
                        .size(11.0),
                );
            });
            ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                ui.add(crate::ui::switch::toggle(&mut state.reverse_proxy_ssl_force_https));
            });
        });
    });
    ui.add_space(16.0);

    // === 底部：左右两列，SSL 证书 和 私钥输入框 ===
    let ssl_enabled = state.reverse_proxy_ssl_enabled;
    let text_height = 200.0;
    let col_width = 280.0;
    ui.add_enabled_ui(ssl_enabled, |ui| {
        ui.horizontal(|ui| {
            // 左侧 — SSL 证书
            ui.vertical(|ui| {
                ui.set_width(col_width);
                ui.horizontal(|ui| {
                    ui.label(egui::RichText::new(lang::t("rp_ssl_cert", lang_state)).size(13.0).strong());
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        if ui.small_button(lang::t("rp_select_file", lang_state)).clicked() {
                            if let Some(path) = rfd::FileDialog::new()
                                .set_title(lang::t("rp_ssl_cert", lang_state))
                                .add_filter("证书文件", &["pem", "crt", "cert", "cer"])
                                .add_filter("所有文件", &["*"])
                                .pick_file()
                            {
                                if let Ok(content) = std::fs::read_to_string(&path) {
                                    state.reverse_proxy_ssl_cert = content;
                                }
                            }
                        }
                    });
                });
                ui.add_space(4.0);
                ui.add_sized(
                    [col_width, text_height],
                    egui::TextEdit::multiline(&mut state.reverse_proxy_ssl_cert)
                        .hint_text(lang::t("rp_ssl_cert_hint", lang_state))
                        .font(egui::TextStyle::Monospace),
                );
            });

            ui.add_space(12.0);

            // 右侧 — SSL 私钥
            ui.vertical(|ui| {
                ui.set_width(col_width);
                ui.horizontal(|ui| {
                    ui.label(egui::RichText::new(lang::t("rp_ssl_key", lang_state)).size(13.0).strong());
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        if ui.small_button(lang::t("rp_select_file", lang_state)).clicked() {
                            if let Some(path) = rfd::FileDialog::new()
                                .set_title(lang::t("rp_ssl_key", lang_state))
                                .add_filter("私钥文件", &["pem", "key"])
                                .add_filter("所有文件", &["*"])
                                .pick_file()
                            {
                                if let Ok(content) = std::fs::read_to_string(&path) {
                                    state.reverse_proxy_ssl_key = content;
                                }
                            }
                        }
                    });
                });
                ui.add_space(4.0);
                ui.add_sized(
                    [col_width, text_height],
                    egui::TextEdit::multiline(&mut state.reverse_proxy_ssl_key)
                        .hint_text(lang::t("rp_ssl_key_hint", lang_state))
                        .font(egui::TextStyle::Monospace),
                );
            });
        });
    });
}
