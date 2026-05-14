use eframe::egui;

#[derive(PartialEq, Default)]
pub enum SettingsTab {
    #[default]
    General,
    About,
}

#[derive(PartialEq, Default, Clone)]
pub enum Language {
    #[default]
    Chinese,
    English,
}

#[derive(PartialEq, Default, Clone)]
pub enum Theme {
    Light,
    #[default]
    Dark,
}

#[derive(PartialEq, Default, Clone)]
pub enum CpuCores {
    #[default]
    Auto,
    Half,
    All,
}

#[derive(PartialEq, Default, Clone)]
pub enum StartMode {
    #[default]
    Normal,
    Desktop,
    Lan,
    Public,
}

#[derive(PartialEq, Default, Clone)]
pub enum EnvSource {
    System,
    #[default]
    Builtin,
    Custom,
}

#[derive(PartialEq, Default, Clone)]
pub enum NpmRegistry {
    Official,
    #[default]
    Taobao,
    Tencent,
}

#[derive(PartialEq, Default, Clone)]
pub enum ProxyType {
    #[default]
    None,
    System,
    Custom,
}

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

pub fn render(ui: &mut egui::Ui, tab: &mut SettingsTab, state: &mut SettingsState) {
    ui.horizontal(|ui| {
        ui.selectable_value(tab, SettingsTab::General, "基本设置");
        ui.selectable_value(tab, SettingsTab::About, "关于软件");
    });
    ui.separator();

    match tab {
        SettingsTab::General => {
            egui::ScrollArea::vertical()
                .auto_shrink([false, false])
                .show(ui, |ui| {
                    ui.add_space(10.0);

                    // 界面设置
                    setting_section(ui, egui_phosphor::regular::PAINT_BRUSH, "界面设置", |ui| {
                        setting_row(ui, egui_phosphor::regular::TRANSLATE, "语言", "选择应用程序显示的语言", |ui| {
                            egui::ComboBox::from_id_salt("lang_combo")
                                .selected_text(match state.language {
                                    Language::Chinese => "中文",
                                    Language::English => "English",
                                })
                                .show_ui(ui, |ui| {
                                    ui.selectable_value(&mut state.language, Language::Chinese, "中文");
                                    ui.selectable_value(&mut state.language, Language::English, "English");
                                });
                        });
                        ui.add_space(10.0);
                        setting_row(ui, egui_phosphor::regular::PALETTE, "主题", "切换明亮或夜晚模式", |ui| {
                            egui::ComboBox::from_id_salt("theme_combo")
                                .selected_text(match state.theme {
                                    Theme::Light => "明亮主题",
                                    Theme::Dark => "夜晚主题",
                                })
                                .show_ui(ui, |ui| {
                                    ui.selectable_value(&mut state.theme, Theme::Light, "明亮主题");
                                    ui.selectable_value(&mut state.theme, Theme::Dark, "夜晚主题");
                                });
                        });
                        ui.add_space(10.0);
                        setting_row(ui, egui_phosphor::regular::CORNERS_OUT, "记住上次窗口位置", "启动时恢复上次窗口的位置和大小", |ui| {
                            ui.radio_value(&mut state.remember_window_pos, false, "关闭");
                            ui.radio_value(&mut state.remember_window_pos, true, "开启");
                        });
                    });

                    // 基本设置
                    setting_section(ui, egui_phosphor::regular::SLIDERS, "基本设置", |ui| {
                        setting_row(ui, egui_phosphor::regular::CPU, "扫描占用核心数", "分配用于全盘扫描的 CPU 线程数", |ui| {
                            egui::ComboBox::from_id_salt("cpu_combo")
                                .selected_text(match state.cpu_cores {
                                    CpuCores::Auto => "Auto",
                                    CpuCores::Half => "1/2核心",
                                    CpuCores::All => "所有核心",
                                })
                                .show_ui(ui, |ui| {
                                    ui.selectable_value(&mut state.cpu_cores, CpuCores::Auto, "Auto");
                                    ui.selectable_value(&mut state.cpu_cores, CpuCores::Half, "1/2核心");
                                    ui.selectable_value(&mut state.cpu_cores, CpuCores::All, "所有核心");
                                });
                        });
                        ui.add_space(10.0);
                        setting_row(ui, egui_phosphor::regular::ROCKET, "酒馆启动模式", "设置酒馆的启动方式", |ui| {
                            ui.radio_value(&mut state.start_mode, StartMode::Public, "公网服务");
                            ui.radio_value(&mut state.start_mode, StartMode::Lan, "局域网服务");
                            ui.radio_value(&mut state.start_mode, StartMode::Desktop, "桌面程序");
                            ui.radio_value(&mut state.start_mode, StartMode::Normal, "正常模式");
                        });
                    });

                    // Git 设置
                    setting_section(ui, egui_phosphor::regular::GIT_BRANCH, "Git 设置", |ui| {
                        setting_row(ui, egui_phosphor::regular::INFO, "Git 环境信息", "查看当前使用的 Git 版本和路径", |ui| {
                            ui.vertical(|ui| {
                                ui.horizontal(|ui| {
                                    ui.label(egui::RichText::new("未知").color(egui::Color32::GRAY));
                                    ui.label("版本：");
                                });
                                ui.horizontal(|ui| {
                                    ui.label(egui::RichText::new("未知").color(egui::Color32::GRAY));
                                    ui.label("路径：");
                                });
                            });
                        });
                        ui.add_space(10.0);
                        setting_row(ui, egui_phosphor::regular::WRENCH, "Git 环境来源", "可切换使用系统 Git 或内置 Git", |ui| {
                            egui::ComboBox::from_id_salt("git_env_combo")
                                .selected_text(match state.git_env {
                                    EnvSource::System => "系统环境",
                                    EnvSource::Builtin => "内置环境（默认）",
                                    EnvSource::Custom => "自定义环境",
                                })
                                .show_ui(ui, |ui| {
                                    ui.selectable_value(&mut state.git_env, EnvSource::System, "系统环境");
                                    ui.selectable_value(&mut state.git_env, EnvSource::Builtin, "内置环境（默认）");
                                    ui.selectable_value(&mut state.git_env, EnvSource::Custom, "自定义环境");
                                });
                        });
                    });

                    // NodeJs 设置
                    setting_section(ui, egui_phosphor::regular::TERMINAL, "NodeJs 设置", |ui| {
                        setting_row(ui, egui_phosphor::regular::INFO, "NodeJs 环境信息", "查看当前使用的 NodeJs 版本和路径", |ui| {
                            ui.vertical(|ui| {
                                ui.horizontal(|ui| {
                                    ui.label(egui::RichText::new("未知").color(egui::Color32::GRAY));
                                    ui.label("版本：");
                                });
                                ui.horizontal(|ui| {
                                    ui.label(egui::RichText::new("未知").color(egui::Color32::GRAY));
                                    ui.label("路径：");
                                });
                            });
                        });
                        ui.add_space(10.0);
                        setting_row(ui, egui_phosphor::regular::WRENCH, "Node.js 环境来源", "可切换使用系统 NodeJs 或内置 NodeJs", |ui| {
                            egui::ComboBox::from_id_salt("nodejs_env_combo")
                                .selected_text(match state.nodejs_env {
                                    EnvSource::System => "系统环境",
                                    EnvSource::Builtin => "内置环境（默认）",
                                    EnvSource::Custom => "自定义环境",
                                })
                                .show_ui(ui, |ui| {
                                    ui.selectable_value(&mut state.nodejs_env, EnvSource::System, "系统环境");
                                    ui.selectable_value(&mut state.nodejs_env, EnvSource::Builtin, "内置环境（默认）");
                                    ui.selectable_value(&mut state.nodejs_env, EnvSource::Custom, "自定义环境");
                                });
                        });
                        ui.add_space(10.0);
                        setting_row(ui, egui_phosphor::regular::INFO, "NPM 环境信息", "查看当前使用的 NPM 版本和路径", |ui| {
                            ui.vertical(|ui| {
                                ui.horizontal(|ui| {
                                    ui.label(egui::RichText::new("未知").color(egui::Color32::GRAY));
                                    ui.label("版本：");
                                });
                                ui.horizontal(|ui| {
                                    ui.label(egui::RichText::new("未知").color(egui::Color32::GRAY));
                                    ui.label("路径：");
                                });
                            });
                        });
                        ui.add_space(10.0);
                        setting_row(ui, egui_phosphor::regular::GLOBE, "NPM 源设置", "设置 NPM 的镜像源", |ui| {
                            egui::ComboBox::from_id_salt("npm_registry_combo")
                                .selected_text(match state.npm_registry {
                                    NpmRegistry::Official => "官方源",
                                    NpmRegistry::Taobao => "淘宝源（默认）",
                                    NpmRegistry::Tencent => "腾讯源",
                                })
                                .show_ui(ui, |ui| {
                                    ui.selectable_value(&mut state.npm_registry, NpmRegistry::Official, "官方源");
                                    ui.selectable_value(&mut state.npm_registry, NpmRegistry::Taobao, "淘宝源（默认）");
                                    ui.selectable_value(&mut state.npm_registry, NpmRegistry::Tencent, "腾讯源");
                                });
                        });
                    });

                    // Github 设置
                    setting_section(ui, egui_phosphor::regular::GITHUB_LOGO, "Github 设置", |ui| {
                        setting_row(ui, egui_phosphor::regular::POWER, "替换总开关", "开启后将在源地址前面加上加速地址，实现加速 Github 资源下载", |ui| {
                            ui.radio_value(&mut state.github_proxy_enabled, false, "关闭");
                            ui.radio_value(&mut state.github_proxy_enabled, true, "开启");
                        });
                        ui.add_space(10.0);
                        setting_row(ui, egui_phosphor::regular::LIST, "替换节点列表", "通过接口获取可用的加速节点", |ui| {
                            if state.github_proxy_enabled {
                                ui.label("加载中...");
                            } else {
                                ui.label(egui::RichText::new("请先开启总开关").color(egui::Color32::GRAY));
                            }
                        });
                    });

                    // 网络设置
                    setting_section(ui, egui_phosphor::regular::WIFI_HIGH, "网络设置", |ui| {
                        setting_row(ui, egui_phosphor::regular::SHIELD, "代理设置", "设置应用程序的网络代理", |ui| {
                            egui::ComboBox::from_id_salt("proxy_type_combo")
                                .selected_text(match state.proxy_type {
                                    ProxyType::None => "关闭",
                                    ProxyType::System => "跟随系统",
                                    ProxyType::Custom => "自定义代理",
                                })
                                .show_ui(ui, |ui| {
                                    ui.selectable_value(&mut state.proxy_type, ProxyType::None, "关闭");
                                    ui.selectable_value(&mut state.proxy_type, ProxyType::System, "跟随系统");
                                    ui.selectable_value(&mut state.proxy_type, ProxyType::Custom, "自定义代理");
                                });
                        });
                        
                        if state.proxy_type == ProxyType::Custom {
                            ui.add_space(10.0);
                            setting_row(ui, egui_phosphor::regular::LINK, "代理地址", "输入自定义代理地址", |ui| {
                                ui.text_edit_singleline(&mut state.custom_proxy);
                            });
                        }
                        
                        ui.add_space(10.0);
                        setting_row(ui, egui_phosphor::regular::PLUG, "GitHub 连接测试", "不通过代理测试 GitHub 连接", |ui| {
                            if ui.button("开始测试").clicked() {
                                // 待实现
                            }
                        });
                    });

                    ui.add_space(20.0);
                });
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
                                    ui.vertical_centered(|ui| ui.strong("技术/组件/资源"));
                                    ui.vertical_centered(|ui| ui.strong("当前版本"));
                                    ui.vertical_centered(|ui| ui.strong("开源协议"));
                                    ui.vertical_centered(|ui| ui.strong("说明"));
                                    ui.end_row();

                                    // 资源
                                    ui.vertical_centered(|ui| ui.label("MiSans"));
                                    ui.vertical_centered(|ui| ui.label("2022"));
                                    ui.vertical_centered(|ui| {
                                        ui.hyperlink_to("免费商用", "https://hyperos.mi.com/font/zh/faq/");
                                    });
                                    ui.vertical_centered(|ui| ui.label("小米字体"));
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
