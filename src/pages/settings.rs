use eframe::egui;
use once_cell::sync::Lazy;
use serde::{Deserialize, Serialize};
use std::fs;
use std::path::PathBuf;
use std::sync::Mutex;

/// Github 测试弹窗状态
struct GithubTestPopupState {
    show: bool,
    results: Vec<crate::core::network::GithubMultiTestItem>,
}

static GITHUB_TEST_POPUP_STATE: Lazy<Mutex<GithubTestPopupState>> = Lazy::new(|| {
    Mutex::new(GithubTestPopupState {
        show: false,
        results: Vec::new(),
    })
});

#[derive(PartialEq, Default)]
pub enum SettingsTab {
    #[default]
    General,
    About,
}

#[derive(PartialEq, Default, Clone, Serialize, Deserialize)]
pub enum Language {
    #[default]
    Chinese,
    English,
}

#[derive(PartialEq, Default, Clone, Serialize, Deserialize)]
pub enum Theme {
    Light,
    #[default]
    Dark,
}

#[derive(PartialEq, Default, Clone, Serialize, Deserialize)]
pub enum CpuCores {
    #[default]
    Auto,
    Half,
    All,
}

#[derive(PartialEq, Default, Clone, Serialize, Deserialize)]
pub enum StartMode {
    #[default]
    Normal,
    Desktop,
    Lan,
    Public,
}

#[derive(PartialEq, Default, Clone, Serialize, Deserialize)]
pub enum EnvSource {
    System,
    #[default]
    Builtin,
}

#[derive(PartialEq, Default, Clone, Serialize, Deserialize)]
pub enum NpmRegistry {
    Official,
    #[default]
    Taobao,
    Tencent,
}

#[derive(PartialEq, Default, Clone, Serialize, Deserialize)]
pub enum ProxyType {
    #[default]
    None,
    System,
    Custom,
}

#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct SettingsState {
    // 界面设置
    pub language: Language,
    pub theme: Theme,
    pub remember_window_pos: bool,
    pub window_position: Option<[f32; 2]>,

    // 基本设置
    pub cpu_cores: CpuCores,
    pub start_mode: StartMode,

    // Git 设置
    pub git_env: EnvSource,

    // NodeJs 设置
    pub nodejs_env: EnvSource,
    pub npm_registry: NpmRegistry,

    // Github 设置
    pub github_proxy_enabled: bool,
    pub github_proxy_url: String,

    // 网络设置
    pub proxy_type: ProxyType,
    pub custom_proxy: String,
}

impl Default for SettingsState {
    fn default() -> Self {
        Self {
            language: Language::default(),
            theme: Theme::default(),
            remember_window_pos: true,
            window_position: None,
            cpu_cores: CpuCores::default(),
            start_mode: StartMode::default(),
            git_env: EnvSource::default(),
            nodejs_env: EnvSource::default(),
            npm_registry: NpmRegistry::default(),
            github_proxy_enabled: false,
            github_proxy_url: String::new(),
            proxy_type: ProxyType::default(),
            custom_proxy: String::new(),
        }
    }
}

impl SettingsState {
    fn config_path() -> PathBuf {
        // 回退到程序运行目录 (或开发时的项目根目录)
        let mut current_exe = std::env::current_exe().unwrap_or_else(|_| PathBuf::from("."));
        current_exe.pop(); // 去除执行文件名

        let path_str = current_exe.to_string_lossy();
        let mut root = if path_str.contains("target\\debug") || path_str.contains("target\\release") {
            // 回退到项目根目录
            let mut p = current_exe.clone();
            p.pop(); // pop debug/release
            p.pop(); // pop target
            p
        } else {
            current_exe
        };

        root.push("data");
        root.push("settings.json");
        root
    }

    pub fn load() -> Self {
        let path = Self::config_path();
        if path.exists() {
            if let Ok(content) = fs::read_to_string(&path) {
                if let Ok(state) = serde_json::from_str(&content) {
                    return state;
                }
            }
        }
        
        // 如果文件不存在或解析失败，生成默认配置并保存（自动创建目录和文件）
        let default_state = Self::default();
        default_state.save();
        default_state
    }

    pub fn save(&self) {
        let path = Self::config_path();
        if let Some(parent) = path.parent() {
            let _ = fs::create_dir_all(parent);
        }
        if let Ok(content) = serde_json::to_string_pretty(self) {
            let _ = fs::write(path, content);
        }
    }
}

use crate::lang;

fn setting_section(
    ui: &mut egui::Ui,
    icon: &str,
    title: &str,
    add_content: impl FnOnce(&mut egui::Ui),
) {
    ui.add_space(10.0);
    ui.horizontal(|ui| {
        ui.label(
            egui::RichText::new(icon)
                .size(18.0)
                .color(ui.visuals().text_color()),
        );
        ui.heading(egui::RichText::new(title).strong());
    });
    ui.add_space(5.0);

    egui::Frame::NONE
        .fill(ui.visuals().faint_bg_color)
        .corner_radius(8.0)
        .inner_margin(15.0)
        .show(ui, |ui| {
            add_content(ui);
        });
}

fn setting_row(
    ui: &mut egui::Ui,
    icon: &str,
    title: &str,
    description: &str,
    add_content: impl FnOnce(&mut egui::Ui),
) {
    ui.horizontal(|ui| {
        // Icon
        ui.add_sized(
            [30.0, 30.0],
            egui::Label::new(egui::RichText::new(icon).size(20.0)),
        );

        // Title and Description
        ui.vertical(|ui| {
            ui.add_space(2.0); // Adjust vertical alignment
            ui.label(egui::RichText::new(title).size(14.0).strong());
            if !description.is_empty() {
                ui.label(
                    egui::RichText::new(description)
                        .color(egui::Color32::GRAY)
                        .size(12.0),
                );
            }
        });

        // Fill available space to push controls to the right
        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
            add_content(ui);
        });
    });
}

pub fn render(
    ui: &mut egui::Ui,
    tab: &mut SettingsTab,
    state: &mut SettingsState,
    git_info: &Option<(String, String)>,
    nodejs_info: &Option<(String, String)>,
    npm_info: &Option<(String, String)>,
    github_node_state: &crate::core::settings::github_proxy::NodeLoadState,
    on_refresh_nodes: &mut bool,
) {
    ui.horizontal(|ui| {
        ui.selectable_value(tab, SettingsTab::General, lang::t("general_settings", &state.language));
        ui.selectable_value(tab, SettingsTab::About, lang::t("about_software", &state.language));
    });
    ui.separator();

    match tab {
        SettingsTab::General => {
            egui::ScrollArea::vertical()
                .auto_shrink([false, false])
                .show(ui, |ui| {
                    ui.add_space(10.0);

                    // 界面设置
                    setting_section(
                        ui,
                        egui_phosphor::regular::PAINT_BRUSH,
                        lang::t("interface_settings", &state.language),
                        |ui| {
                            setting_row(
                                ui,
                                egui_phosphor::regular::TRANSLATE,
                                lang::t("language", &state.language),
                                lang::t("language_desc", &state.language),
                                |ui| {
                                    egui::ComboBox::from_id_salt("lang_combo")
                                        .selected_text(match state.language {
                                            Language::Chinese => lang::t("zh_cn", &state.language),
                                            Language::English => lang::t("en_us", &state.language),
                                        })
                                        .show_ui(ui, |ui| {
                                            let text_zh = lang::t("zh_cn", &state.language);
                                            let text_en = lang::t("en_us", &state.language);
                                            ui.selectable_value(
                                                &mut state.language,
                                                Language::Chinese,
                                                text_zh,
                                            );
                                            ui.selectable_value(
                                                &mut state.language,
                                                Language::English,
                                                text_en,
                                            );
                                        });
                                },
                            );
                            ui.add_space(10.0);
                            setting_row(
                                ui,
                                egui_phosphor::regular::PALETTE,
                                lang::t("theme", &state.language),
                                lang::t("theme_desc", &state.language),
                                |ui| {
                                    egui::ComboBox::from_id_salt("theme_combo")
                                        .selected_text(match state.theme {
                                            Theme::Light => lang::t("light_theme", &state.language),
                                            Theme::Dark => lang::t("dark_theme", &state.language),
                                        })
                                        .show_ui(ui, |ui| {
                                            ui.selectable_value(
                                                &mut state.theme,
                                                Theme::Light,
                                                lang::t("light_theme", &state.language),
                                            );
                                            ui.selectable_value(
                                                &mut state.theme,
                                                Theme::Dark,
                                                lang::t("dark_theme", &state.language),
                                            );
                                        });
                                },
                            );
                            ui.add_space(10.0);
                            setting_row(
                                ui,
                                egui_phosphor::regular::CORNERS_OUT,
                                lang::t("remember_window_pos", &state.language),
                                lang::t("remember_window_pos_desc", &state.language),
                                |ui| {
                                    ui.radio_value(&mut state.remember_window_pos, false, lang::t("off", &state.language));
                                    ui.radio_value(&mut state.remember_window_pos, true, lang::t("on", &state.language));
                                },
                            );
                        },
                    );

                    // 基本设置
                    setting_section(ui, egui_phosphor::regular::SLIDERS, lang::t("basic_settings", &state.language), |ui| {
                        setting_row(
                            ui,
                            egui_phosphor::regular::CPU,
                            lang::t("cpu_cores", &state.language),
                            lang::t("cpu_cores_desc", &state.language),
                            |ui| {
                                egui::ComboBox::from_id_salt("cpu_combo")
                                    .selected_text(match state.cpu_cores {
                                        CpuCores::Auto => lang::t("auto", &state.language),
                                        CpuCores::Half => lang::t("half_cores", &state.language),
                                        CpuCores::All => lang::t("all_cores", &state.language),
                                    })
                                    .show_ui(ui, |ui| {
                                        ui.selectable_value(
                                            &mut state.cpu_cores,
                                            CpuCores::Auto,
                                            lang::t("auto", &state.language),
                                        );
                                        ui.selectable_value(
                                            &mut state.cpu_cores,
                                            CpuCores::Half,
                                            lang::t("half_cores", &state.language),
                                        );
                                        ui.selectable_value(
                                            &mut state.cpu_cores,
                                            CpuCores::All,
                                            lang::t("all_cores", &state.language),
                                        );
                                    });
                            },
                        );
                        ui.add_space(10.0);
                        setting_row(
                            ui,
                            egui_phosphor::regular::ROCKET,
                            lang::t("start_mode", &state.language),
                            lang::t("start_mode_desc", &state.language),
                            |ui| {
                                crate::ui::segmented::segmented_control(
                                    ui,
                                    &mut state.start_mode,
                                    &[
                                        (StartMode::Normal, lang::t("normal_mode", &state.language)),
                                        (StartMode::Desktop, lang::t("desktop_mode", &state.language)),
                                        (StartMode::Lan, lang::t("lan_mode", &state.language)),
                                        (StartMode::Public, lang::t("public_mode", &state.language)),
                                    ],
                                );
                            },
                        );
                    });

                    // Git 设置
                    setting_section(ui, egui_phosphor::regular::GIT_BRANCH, lang::t("git_settings", &state.language), |ui| {
                        let unknown = lang::t("unknown", &state.language);
                        let (git_ver, git_path) = git_info.as_ref().map(|(v, p)| (v.as_str(), p.as_str())).unwrap_or((unknown, unknown));
                        let git_info_desc = lang::t("git_env_info_desc", &state.language)
                            .replace("{version}", git_ver)
                            .replace("{path}", git_path);
                            
                        setting_row(
                            ui,
                            egui_phosphor::regular::INFO,
                            lang::t("git_env_info", &state.language),
                            &git_info_desc,
                            |_| {},
                        );
                        ui.add_space(10.0);
                        setting_row(
                                ui,
                                egui_phosphor::regular::WRENCH,
                                lang::t("git_env_source", &state.language),
                                lang::t("git_env_source_desc", &state.language),
                                |ui| {
                                    egui::ComboBox::from_id_salt("git_env_combo")
                                        .selected_text(match state.git_env {
                                            EnvSource::System => lang::t("system_env", &state.language),
                                            EnvSource::Builtin => lang::t("builtin_env", &state.language),
                                        })
                                        .show_ui(ui, |ui| {
                                            ui.selectable_value(
                                                &mut state.git_env,
                                                EnvSource::System,
                                                lang::t("system_env", &state.language),
                                            );
                                            ui.selectable_value(
                                                &mut state.git_env,
                                                EnvSource::Builtin,
                                                lang::t("builtin_env", &state.language),
                                            );
                                        });
                                },
                            );
                    });

                    // NodeJs 设置
                    setting_section(ui, egui_phosphor::regular::TERMINAL, lang::t("nodejs_settings", &state.language), |ui| {
                        let unknown = lang::t("unknown", &state.language);
                        let (node_ver, node_path) = nodejs_info.as_ref().map(|(v, p)| (v.as_str(), p.as_str())).unwrap_or((unknown, unknown));
                        let node_info_desc = lang::t("nodejs_env_info_desc", &state.language)
                            .replace("{version}", node_ver)
                            .replace("{path}", node_path);
                            
                        setting_row(
                            ui,
                            egui_phosphor::regular::INFO,
                            lang::t("nodejs_env_info", &state.language),
                            &node_info_desc,
                            |_| {},
                        );
                        ui.add_space(10.0);
                        setting_row(
                                ui,
                                egui_phosphor::regular::WRENCH,
                                lang::t("nodejs_env_source", &state.language),
                                lang::t("nodejs_env_source_desc", &state.language),
                                |ui| {
                                    egui::ComboBox::from_id_salt("nodejs_env_combo")
                                        .selected_text(match state.nodejs_env {
                                            EnvSource::System => lang::t("system_env", &state.language),
                                            EnvSource::Builtin => lang::t("builtin_env", &state.language),
                                        })
                                        .show_ui(ui, |ui| {
                                            ui.selectable_value(
                                                &mut state.nodejs_env,
                                                EnvSource::System,
                                                lang::t("system_env", &state.language),
                                            );
                                            ui.selectable_value(
                                                &mut state.nodejs_env,
                                                EnvSource::Builtin,
                                                lang::t("builtin_env", &state.language),
                                            );
                                        });
                                },
                            );
                        ui.add_space(10.0);
                        
                        let (npm_ver, npm_path) = npm_info.as_ref().map(|(v, p)| (v.as_str(), p.as_str())).unwrap_or((unknown, unknown));
                        let npm_info_desc = lang::t("npm_env_info_desc", &state.language)
                            .replace("{version}", npm_ver)
                            .replace("{path}", npm_path);
                            
                        setting_row(
                            ui,
                            egui_phosphor::regular::INFO,
                            lang::t("npm_env_info", &state.language),
                            &npm_info_desc,
                            |_| {},
                        );
                        ui.add_space(10.0);
                        setting_row(
                            ui,
                            egui_phosphor::regular::GLOBE,
                            lang::t("npm_registry", &state.language),
                            lang::t("npm_registry_desc", &state.language),
                            |ui| {
                                egui::ComboBox::from_id_salt("npm_registry_combo")
                                    .selected_text(match state.npm_registry {
                                        NpmRegistry::Official => lang::t("official_registry", &state.language),
                                        NpmRegistry::Taobao => lang::t("taobao_registry", &state.language),
                                        NpmRegistry::Tencent => lang::t("tencent_registry", &state.language),
                                    })
                                    .show_ui(ui, |ui| {
                                        ui.selectable_value(
                                            &mut state.npm_registry,
                                            NpmRegistry::Official,
                                            lang::t("official_registry", &state.language),
                                        );
                                        ui.selectable_value(
                                            &mut state.npm_registry,
                                            NpmRegistry::Taobao,
                                            lang::t("taobao_registry", &state.language),
                                        );
                                        ui.selectable_value(
                                            &mut state.npm_registry,
                                            NpmRegistry::Tencent,
                                            lang::t("tencent_registry", &state.language),
                                        );
                                    });
                            },
                        );
                    });

                    // Github 设置
                    setting_section(
                        ui,
                        egui_phosphor::regular::GITHUB_LOGO,
                        lang::t("github_settings", &state.language),
                        |ui| {
                            setting_row(
                                ui,
                                egui_phosphor::regular::POWER,
                                lang::t("github_proxy", &state.language),
                                lang::t("github_proxy_desc", &state.language),
                                |ui| {
                                    ui.radio_value(&mut state.github_proxy_enabled, false, lang::t("off", &state.language));
                                    ui.radio_value(&mut state.github_proxy_enabled, true, lang::t("on", &state.language));
                                },
                            );
                            ui.add_space(10.0);

                            // 节点列表标题行（带刷新按钮）
                            ui.horizontal(|ui| {
                                ui.add_sized(
                                    [30.0, 30.0],
                                    egui::Label::new(egui::RichText::new(egui_phosphor::regular::LIST).size(20.0)),
                                );
                                ui.vertical(|ui| {
                                    ui.add_space(2.0);
                                    ui.label(egui::RichText::new(lang::t("github_nodes", &state.language)).size(14.0).strong());
                                    ui.label(
                                        egui::RichText::new(lang::t("github_nodes_desc", &state.language))
                                            .color(egui::Color32::GRAY)
                                            .size(12.0),
                                    );
                                });
                                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                                    let is_loading = matches!(github_node_state, crate::core::settings::github_proxy::NodeLoadState::Loading);
                                    ui.add_enabled_ui(!is_loading, |ui| {
                                        if ui.button(lang::t("refresh_nodes", &state.language)).clicked() {
                                            *on_refresh_nodes = true;
                                        }
                                    });
                                    if is_loading {
                                        ui.spinner();
                                    }
                                });
                            });

                            ui.add_space(8.0);

                            if !state.github_proxy_enabled {
                                ui.label(
                                    egui::RichText::new(lang::t("enable_proxy_first", &state.language))
                                        .color(egui::Color32::GRAY),
                                );
                            } else {
                                match github_node_state {
                                    crate::core::settings::github_proxy::NodeLoadState::Idle => {
                                        ui.label(
                                            egui::RichText::new(lang::t("click_refresh_to_load", &state.language))
                                                .color(egui::Color32::GRAY),
                                        );
                                    }
                                    crate::core::settings::github_proxy::NodeLoadState::Loading => {
                                        ui.horizontal(|ui| {
                                            ui.spinner();
                                            ui.label(lang::t("loading_nodes", &state.language));
                                        });
                                    }
                                    crate::core::settings::github_proxy::NodeLoadState::Error(e) => {
                                        ui.label(
                                            egui::RichText::new(format!("{} {e}", lang::t("fetch_error", &state.language)))
                                                .color(egui::Color32::RED),
                                        );
                                    }
                                    crate::core::settings::github_proxy::NodeLoadState::Done(entries) => {
                                        // 按实测延迟排序（测试中排最后，超时排中间，有值按延迟升序）
                                        let mut sorted_entries = entries.clone();
                                        sorted_entries.sort_by(|a, b| {
                                            let a_ms = *a.measured_ms.lock().unwrap();
                                            let b_ms = *b.measured_ms.lock().unwrap();
                                            match (a_ms, b_ms) {
                                                (None, None) => std::cmp::Ordering::Equal,
                                                (None, _) => std::cmp::Ordering::Greater,
                                                (_, None) => std::cmp::Ordering::Less,
                                                (Some(None), Some(None)) => std::cmp::Ordering::Equal,
                                                (Some(None), Some(Some(_))) => std::cmp::Ordering::Greater,
                                                (Some(Some(_)), Some(None)) => std::cmp::Ordering::Less,
                                                (Some(Some(a)), Some(Some(b))) => a.cmp(&b),
                                            }
                                        });

                                        // 节点表格 — 9 列，支持横向滚动
                                        let avail_w = ui.available_width();
                                        let select_w: f32 = 50.0;
                                        let url_w: f32 = 260.0;
                                        let server_w: f32 = 120.0;
                                        let ip_w: f32 = 130.0;
                                        let loc_w: f32 = 90.0;
                                        let api_latency_w: f32 = 90.0;
                                        let latency_w: f32 = 90.0;
                                        let speed_w: f32 = 90.0;
                                        let tag_w: f32 = 100.0;
                                        let spacing: f32 = 16.0;
                                        let total_fixed: f32 = select_w + server_w + ip_w + loc_w + api_latency_w + latency_w + speed_w + tag_w + spacing * 8.0;
                                        let url_calc: f32 = (avail_w - total_fixed).max(url_w);

                                        egui::ScrollArea::new(egui::Vec2b::TRUE)
                                            .id_salt("github_nodes_scroll")
                                            .max_height(400.0)
                                            .min_scrolled_height(400.0)
                                            .show(ui, |ui| {
                                                egui::Grid::new("github_nodes_grid")
                                                    .striped(true)
                                                    .num_columns(9)
                                                    .spacing(egui::vec2(spacing, 6.0))
                                                    .show(ui, |ui| {
                                                        // 表头
                                                        ui.allocate_ui_with_layout(egui::vec2(select_w, 28.0), egui::Layout::top_down(egui::Align::Center), |ui| { ui.strong(lang::t("col_select", &state.language)); });
                                                        ui.allocate_ui_with_layout(egui::vec2(url_calc, 28.0), egui::Layout::left_to_right(egui::Align::Center), |ui| { ui.strong(lang::t("col_url", &state.language)); });
                                                        ui.allocate_ui_with_layout(egui::vec2(server_w, 28.0), egui::Layout::top_down(egui::Align::Center), |ui| { ui.strong("Server"); });
                                                        ui.allocate_ui_with_layout(egui::vec2(ip_w, 28.0), egui::Layout::top_down(egui::Align::Center), |ui| { ui.strong("IP"); });
                                                        ui.allocate_ui_with_layout(egui::vec2(loc_w, 28.0), egui::Layout::top_down(egui::Align::Center), |ui| { ui.strong(lang::t("col_location", &state.language)); });
                                                        ui.allocate_ui_with_layout(egui::vec2(api_latency_w, 28.0), egui::Layout::top_down(egui::Align::Center), |ui| { ui.strong("接口延迟"); });
                                                        ui.allocate_ui_with_layout(egui::vec2(latency_w, 28.0), egui::Layout::top_down(egui::Align::Center), |ui| { ui.strong(lang::t("col_latency", &state.language)); });
                                                        ui.allocate_ui_with_layout(egui::vec2(speed_w, 28.0), egui::Layout::top_down(egui::Align::Center), |ui| { ui.strong(lang::t("col_speed", &state.language)); });
                                                        ui.allocate_ui_with_layout(egui::vec2(tag_w, 28.0), egui::Layout::top_down(egui::Align::Center), |ui| { ui.strong("Tag"); });
                                                        ui.end_row();

                                                        for entry in sorted_entries.iter() {
                                                            let is_selected = state.github_proxy_url == entry.url;

                                                            // 选择列
                                                            ui.allocate_ui_with_layout(egui::vec2(select_w, 28.0), egui::Layout::top_down(egui::Align::Center), |ui| {
                                                                let mut sel = is_selected;
                                                                if ui.radio(sel, "").clicked() { sel = true; }
                                                                if sel && !is_selected { state.github_proxy_url = entry.url.clone(); }
                                                            });
                                                            // URL
                                                            let url_display = entry.url.trim_start_matches("https://").trim_start_matches("http://").trim_end_matches('/');
                                                            ui.allocate_ui_with_layout(egui::vec2(url_calc, 28.0), egui::Layout::left_to_right(egui::Align::Center), |ui| {
                                                                ui.label(egui::RichText::new(url_display).size(13.0).color(ui.visuals().text_color())).on_hover_text(entry.url.clone());
                                                            });
                                                            // Server
                                                            ui.allocate_ui_with_layout(egui::vec2(server_w, 28.0), egui::Layout::top_down(egui::Align::Center), |ui| {
                                                                ui.label(egui::RichText::new(&entry.server).size(13.0));
                                                            });
                                                            // IP
                                                            ui.allocate_ui_with_layout(egui::vec2(ip_w, 28.0), egui::Layout::top_down(egui::Align::Center), |ui| {
                                                                ui.label(egui::RichText::new(&entry.ip).size(13.0));
                                                            });
                                                            // 地区
                                                            let loc = if entry.location.is_empty() { "-".to_string() } else { entry.location.clone() };
                                                            ui.allocate_ui_with_layout(egui::vec2(loc_w, 28.0), egui::Layout::top_down(egui::Align::Center), |ui| {
                                                                ui.label(egui::RichText::new(&loc).size(13.0));
                                                            });
                                                            // 接口延迟
                                                            ui.allocate_ui_with_layout(egui::vec2(api_latency_w, 28.0), egui::Layout::top_down(egui::Align::Center), |ui| {
                                                                ui.label(egui::RichText::new(format!("{} ms", entry.api_latency)).size(13.0).color(egui::Color32::from_rgb(140, 140, 140)));
                                                            });
                                                            // 实测延迟
                                                            let latency_text = {
                                                                let guard = entry.measured_ms.lock().unwrap();
                                                                match &*guard {
                                                                    None => lang::t("testing", &state.language).to_string(),
                                                                    Some(None) => lang::t("timeout", &state.language).to_string(),
                                                                    Some(Some(ms)) => format!("{ms} ms"),
                                                                }
                                                            };
                                                            let latency_color = {
                                                                let guard = entry.measured_ms.lock().unwrap();
                                                                match &*guard {
                                                                    Some(Some(ms)) if *ms < 200 => egui::Color32::from_rgb(80, 200, 100),
                                                                    Some(Some(ms)) if *ms < 500 => egui::Color32::from_rgb(230, 180, 60),
                                                                    Some(Some(_)) => egui::Color32::from_rgb(220, 80, 60),
                                                                    _ => egui::Color32::GRAY,
                                                                }
                                                            };
                                                            ui.allocate_ui_with_layout(egui::vec2(latency_w, 28.0), egui::Layout::top_down(egui::Align::Center), |ui| {
                                                                ui.label(egui::RichText::new(&latency_text).size(13.0).color(latency_color));
                                                            });
                                                            // 速度
                                                            let speed_str = if entry.speed >= 1000.0 {
                                                                format!("{:.1} MB/s", entry.speed / 1024.0)
                                                            } else {
                                                                format!("{:.1} KB/s", entry.speed)
                                                            };
                                                            ui.allocate_ui_with_layout(egui::vec2(speed_w, 28.0), egui::Layout::top_down(egui::Align::Center), |ui| {
                                                                ui.label(egui::RichText::new(&speed_str).size(13.0));
                                                            });
                                                            // Tag
                                                            ui.allocate_ui_with_layout(egui::vec2(tag_w, 28.0), egui::Layout::top_down(egui::Align::Center), |ui| {
                                                                let tag_display = if entry.tag.is_empty() { "-".to_string() } else { entry.tag.clone() };
                                                                ui.label(egui::RichText::new(&tag_display).size(13.0));
                                                            });
                                                            ui.end_row();
                                                        }
                                                    });
                                            });

                                        // 当前选中节点提示
                                        if !state.github_proxy_url.is_empty() {
                                            ui.add_space(6.0);
                                            ui.horizontal(|ui| {
                                                ui.label(
                                                    egui::RichText::new(lang::t("selected_node", &state.language))
                                                        .size(12.0)
                                                        .color(egui::Color32::GRAY),
                                                );
                                                ui.label(
                                                    egui::RichText::new(&state.github_proxy_url)
                                                        .size(12.0)
                                                        .color(egui::Color32::from_rgb(100, 160, 240)),
                                                );
                                            });
                                        }
                                    }
                                }
                            }
                        },
                    );

                    // 网络设置
                    setting_section(ui, egui_phosphor::regular::WIFI_HIGH, lang::t("network_settings", &state.language), |ui| {
                        setting_row(
                            ui,
                            egui_phosphor::regular::SHIELD,
                            lang::t("proxy_settings", &state.language),
                            lang::t("proxy_settings_desc", &state.language),
                            |ui| {
                                egui::ComboBox::from_id_salt("proxy_type_combo")
                                    .selected_text(match state.proxy_type {
                                        ProxyType::None => lang::t("off", &state.language),
                                        ProxyType::System => lang::t("follow_system", &state.language),
                                        ProxyType::Custom => lang::t("custom_proxy", &state.language),
                                    })
                                    .show_ui(ui, |ui| {
                                        ui.selectable_value(
                                            &mut state.proxy_type,
                                            ProxyType::None,
                                            lang::t("off", &state.language),
                                        );
                                        ui.selectable_value(
                                            &mut state.proxy_type,
                                            ProxyType::System,
                                            lang::t("follow_system", &state.language),
                                        );
                                        ui.selectable_value(
                                            &mut state.proxy_type,
                                            ProxyType::Custom,
                                            lang::t("custom_proxy", &state.language),
                                        );
                                    });
                            },
                        );

                        if state.proxy_type == ProxyType::Custom {
                            ui.add_space(10.0);
                            setting_row(
                                ui,
                                egui_phosphor::regular::LINK,
                                lang::t("proxy_address", &state.language),
                                lang::t("proxy_address_desc", &state.language),
                                |ui| {
                                    ui.text_edit_singleline(&mut state.custom_proxy);
                                },
                            );
                        }

                        ui.add_space(10.0);
                        setting_row(
                            ui,
                            egui_phosphor::regular::PLUG,
                            lang::t("github_test", &state.language),
                            lang::t("github_test_desc", &state.language),
                            |ui| {
                                if ui.button(lang::t("start_test", &state.language)).clicked() {
                                    // 待实现
                                }
                            },
                        );
                    });

                    ui.add_space(20.0);
                });
        }
        SettingsTab::About => {
            ui.vertical_centered(|ui| {
                // 上部分：软件关于信息
                ui.heading(lang::t("about_title", &state.language));
                ui.label(lang::t("about_version", &state.language));
                ui.label(lang::t("about_desc", &state.language));
                
                ui.add_space(20.0);
                ui.separator();
                ui.add_space(10.0);
                
                // 下部分：开发信息与技术栈表格
                ui.heading(lang::t("tech_stack", &state.language));
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
                                    ui.vertical_centered(|ui| ui.strong(lang::t("tech_col_1", &state.language)));
                                    ui.vertical_centered(|ui| ui.strong(lang::t("tech_col_2", &state.language)));
                                    ui.vertical_centered(|ui| ui.strong(lang::t("tech_col_3", &state.language)));
                                    ui.vertical_centered(|ui| ui.strong(lang::t("tech_col_4", &state.language)));
                                    ui.end_row();

                                    // 资源
                                    ui.vertical_centered(|ui| ui.label("MiSans"));
                                    ui.vertical_centered(|ui| ui.label("2022"));
                                    ui.vertical_centered(|ui| {
                                        ui.hyperlink_to(lang::t("free_commercial", &state.language), "https://hyperos.mi.com/font/zh/faq/");
                                    });
                                    ui.vertical_centered(|ui| ui.label(lang::t("mi_font", &state.language)));
                                    ui.end_row();
                                    
                                    // 数据行
                                    ui.vertical_centered(|ui| ui.label("Rust"));
                                    ui.vertical_centered(|ui| ui.label("2024"));
                                    ui.vertical_centered(|ui| {
                                        ui.hyperlink_to("MIT / Apache-2.0", "https://github.com/rust-lang/rust/blob/master/LICENSE-MIT");
                                    });
                                    ui.vertical_centered(|ui| ui.label(lang::t("rust_desc", &state.language)));
                                    ui.end_row();
                                    
                                    ui.vertical_centered(|ui| ui.label("egui"));
                                    ui.vertical_centered(|ui| ui.label("0.33"));
                                    ui.vertical_centered(|ui| {
                                        ui.hyperlink_to("MIT / Apache-2.0", "https://github.com/emilk/egui/blob/master/LICENSE-MIT");
                                    });
                                    ui.vertical_centered(|ui| ui.label(lang::t("egui_desc", &state.language)));
                                    ui.end_row();
                                    
                                    ui.vertical_centered(|ui| ui.label("eframe"));
                                    ui.vertical_centered(|ui| ui.label("0.33"));
                                    ui.vertical_centered(|ui| {
                                        ui.hyperlink_to("MIT / Apache-2.0", "https://github.com/emilk/egui/blob/master/LICENSE-MIT");
                                    });
                                    ui.vertical_centered(|ui| ui.label(lang::t("eframe_desc", &state.language)));
                                    ui.end_row();
                                    
                                    ui.vertical_centered(|ui| ui.label("egui_phosphor"));
                                    ui.vertical_centered(|ui| ui.label("0.11"));
                                    ui.vertical_centered(|ui| {
                                        ui.hyperlink_to("MIT / Apache-2.0", "https://github.com/amPerl/egui-phosphor/blob/main/LICENSE-MIT");
                                    });
                                    ui.vertical_centered(|ui| ui.label(lang::t("phosphor_desc", &state.language)));
                                    ui.end_row();
                                });
                        });
                });
            });
        }
    }
    // === Github 测试弹窗 ===
    {
        // 获取弹窗状态（不持有锁）
        let (show, results) = {
            let state = GITHUB_TEST_POPUP_STATE.lock().unwrap();
            (state.show, state.results.clone())
        };
        
        if show {
            let mut open = true;
            let mut results = results;
            
            egui::Window::new(lang::t("github_test", &state.language))
                .open(&mut open)
                .resizable(true)
                .default_width(500.0)
                .show(ui.ctx(), |ui| {
                    // 检查测试是否正在进行
                    let testing = crate::core::network::is_github_multi_test_in_progress();
                    
                    // 开始测试按钮
                    if !testing {
                        if ui.button(lang::t("start_test", &state.language)).clicked() {
                            // 启动测试
                            let mode = if state.github_proxy_enabled {
                                "proxy"
                            } else {
                                "none"
                            };
                            let host = state.github_proxy_url.clone();
                            crate::core::network::start_github_multi_test(mode, &host, 0, true);
                        }
                    } else {
                        ui.horizontal(|ui| {
                            ui.spinner();
                            ui.label(lang::t("testing", &state.language));
                        });
                    }
                    
                    ui.separator();
                    
                    // 显示结果
                    if !results.is_empty() {
                        ui.heading(lang::t("test_results", &state.language));
                        egui::ScrollArea::vertical()
                            .max_height(300.0)
                            .show(ui, |ui| {
                                for item in &results {
                                    ui.horizontal(|ui| {
                                        ui.label(&item.key);
                                        ui.separator();
                                        ui.label(&item.name);
                                        ui.separator();
                                        if item.success {
                                            if let Some(latency) = item.latency_ms {
                                                ui.colored_label(egui::Color32::GREEN, format!("{} ms", latency));
                                            } else {
                                                ui.colored_label(egui::Color32::GREEN, lang::t("success", &state.language));
                                            }
                                        } else {
                                            ui.colored_label(egui::Color32::RED, lang::t("failed", &state.language));
                                        }
                                        if let Some(err) = &item.error {
                                            ui.label(err);
                                        }
                                        if let Some(warn) = &item.warning {
                                            ui.colored_label(egui::Color32::YELLOW, warn);
                                        }
                                    });
                                    ui.separator();
                                }
                            });
                    }
                });
            
            // 检查测试是否完成（不持有锁时调用）
            if let Some(test_results) = crate::core::network::get_github_multi_test_result() {
                results = test_results;
            }
            
            // 保存状态
            let mut state = GITHUB_TEST_POPUP_STATE.lock().unwrap();
            state.show = open;
            state.results = results;
        }
    }

}
