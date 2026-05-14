use eframe::egui;
use serde::{Deserialize, Serialize};
use std::fs;
use std::path::PathBuf;

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
    Custom,
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
            cpu_cores: CpuCores::default(),
            start_mode: StartMode::default(),
            git_env: EnvSource::default(),
            nodejs_env: EnvSource::default(),
            npm_registry: NpmRegistry::default(),
            github_proxy_enabled: false,
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
                                        EnvSource::Custom => lang::t("custom_env", &state.language),
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
                                        ui.selectable_value(
                                            &mut state.git_env,
                                            EnvSource::Custom,
                                            lang::t("custom_env", &state.language),
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
                                        EnvSource::Custom => lang::t("custom_env", &state.language),
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
                                        ui.selectable_value(
                                            &mut state.nodejs_env,
                                            EnvSource::Custom,
                                            lang::t("custom_env", &state.language),
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
                            setting_row(
                                ui,
                                egui_phosphor::regular::LIST,
                                lang::t("github_nodes", &state.language),
                                lang::t("github_nodes_desc", &state.language),
                                |ui| {
                                    if state.github_proxy_enabled {
                                        ui.label(lang::t("loading", &state.language));
                                    } else {
                                        ui.label(
                                            egui::RichText::new(lang::t("enable_proxy_first", &state.language))
                                                .color(egui::Color32::GRAY),
                                        );
                                    }
                                },
                            );
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
}
