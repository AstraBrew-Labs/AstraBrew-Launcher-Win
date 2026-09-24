//! 单页设置视图：沿用旧版设置清单，使用 Astra UI 重新排版。

use std::fmt;

use iced::widget::{
    button, column, combo_box, container, mouse_area, row, rule, scrollable, slider, space, stack,
    text_input,
};
use iced::{Alignment, Background, Border, Color, Element, Fill, Font, Length, Theme};
use lucide_icons::Icon;

use astra_ui::{
    AlertKind, BLUE_600, ButtonVariant, INK_MUTED, ProgressBar, ProgressBarColor,
    ProgressBarSize, ProgressCircle, ProgressCircleColor, ProgressCircleSize,
    ToggleButtonGroupItem, icons,
};

use super::{themed_segmented_group, themed_segmented_group_enabled};
use crate::app::Message;
use crate::core::network::{DownloadChannel, DownloadChannelTestResult};
use crate::core::updater::UpdateSource;
use crate::core::typography::{
    DEFAULT_UI_SCALE, FontChoice, MAX_UI_SCALE, MIN_UI_SCALE, normalize_ui_scale,
};
pub use crate::core::settings::{DisplayLanguage, ThemeMode};
use crate::lang::lang::current_language;
use crate::lang::{raw, t, t_in, text, tf};
use crate::theme::{button_style, pick_list_menu_style, slider_style, text_input_style};

const INPUT_WIDTH: f32 = 250.0;

/// 设置页主滚动区域使用稳定标识，便于从全局安装引导定位到环境依赖区域。
pub(crate) fn settings_scroll_id() -> iced::widget::Id {
    iced::widget::Id::new("settings-main-scroll")
}

macro_rules! enum_text {
    ($ty:ident, $([$variant:ident, $label:literal]),+ $(,)?) => {
        impl fmt::Display for $ty {
            fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
                f.write_str(&crate::lang::t(match self { $(Self::$variant => $label,)+ }))
            }
        }
    };
}

// 后台任务核心数定义在核心层（扫描、依赖安装都要按它折算线程预算），
// 这里只做再导出，界面层继续用 `pages::settings::CpuCores` 这一条路径。
pub use crate::core::settings::CpuCores;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum StartMode {
    #[default]
    Normal,
    Desktop,
}
enum_text!(StartMode, [Normal, "settings.start_mode.normal"], [Desktop, "settings.start_mode.desktop"]);

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum QuickStartMode {
    #[default]
    Normal,
    Desktop,
    Server,
}
impl QuickStartMode {
    pub const fn label_key(self) -> &'static str {
        match self {
            Self::Normal => "settings.quick_start.normal",
            Self::Desktop => "settings.start_mode.desktop",
            Self::Server => "settings.quick_start.server",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ServerServiceMode {
    #[default]
    Lan,
    Internet,
}
impl ServerServiceMode {
    pub const fn key(self) -> &'static str {
        match self {
            Self::Lan => "lan",
            Self::Internet => "internet",
        }
    }

    pub fn from_key(value: &str) -> Self {
        if value.eq_ignore_ascii_case("internet") {
            Self::Internet
        } else {
            Self::Lan
        }
    }
}
enum_text!(ServerServiceMode, [Lan, "settings.service_mode.lan"], [Internet, "settings.service_mode.internet"]);

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum TavernDataMode {
    Global,
    #[default]
    Current,
}
enum_text!(TavernDataMode, [Global, "settings.data_mode.global"], [Current, "settings.data_mode.current"]);

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum NpmRegistry {
    Official,
    #[default]
    Npmmirror,
    Tencent,
    HuaweiCloud,
}
impl NpmRegistry {
    pub fn from_url(url: &str) -> Self {
        match url.trim_end_matches('/') {
            "https://registry.npmjs.org" => Self::Official,
            "https://registry.npmmirror.com" => Self::Npmmirror,
            "https://mirrors.cloud.tencent.com/npm" => Self::Tencent,
            "https://repo.huaweicloud.com/repository/npm" => Self::HuaweiCloud,
            _ => Self::Npmmirror,
        }
    }

    pub const fn url(self) -> &'static str {
        match self {
            Self::Official => "https://registry.npmjs.org/",
            Self::Npmmirror => "https://registry.npmmirror.com/",
            Self::Tencent => "https://mirrors.cloud.tencent.com/npm/",
            Self::HuaweiCloud => "https://repo.huaweicloud.com/repository/npm/",
        }
    }
}
enum_text!(
    NpmRegistry,
    [Official, "settings.npm_registry.official"],
    [Npmmirror, "npmmirror"],
    [Tencent, "settings.npm_registry.tencent"],
    [HuaweiCloud, "settings.npm_registry.huawei_cloud"]
);

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ProxyMode {
    #[default]
    None,
    System,
    Custom,
}
impl fmt::Display for ProxyMode {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let label = match self {
            Self::None => "settings.proxy_mode.none",
            Self::System => "settings.language.system",
            Self::Custom => "settings.proxy_mode.custom",
        };
        f.write_str(&crate::lang::t(label))
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SettingsAction {
    OpenLoginItemSettings,
    ChooseExportPath,
    ChooseGlobalDataPath,
    RefreshDownloadChannel,
    TestGithub,
    CheckUpdate,
}
impl SettingsAction {
    pub const fn feedback(self) -> &'static str {
        match self {
            Self::OpenLoginItemSettings => "settings.action.login_item_opened",
            Self::ChooseExportPath => "settings.action.export_path_updated",
            Self::ChooseGlobalDataPath => "settings.action.global_data_path_updated",
            Self::RefreshDownloadChannel => "settings.action.download_channel_refreshed",
            Self::TestGithub => "settings.action.github_pending",
            // 更新检查由应用层直接驱动后台任务并自行提示，不再走通用 feedback 通道。
            Self::CheckUpdate => "settings.check_update.hint",
        }
    }
}

/// 待用户确认的更新信息。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PendingUpdate {
    /// 新版本号。
    pub version: String,
    /// 发行说明，发布方未填写时为 `None`。
    pub notes: Option<String>,
    /// 检测阶段命中的更新源，确认安装时在该源上重新解析地址。
    pub source: UpdateSource,
}

/// 启动器更新模块的界面状态。
#[derive(Debug, Clone, Default)]
pub struct UpdateState {
    /// 正在检查更新：按钮显示「正在检查更新…」并禁用。
    pub checking: bool,
    /// 正在下载安装：按钮显示「正在下载安装…」并禁用。
    pub downloading: bool,
    /// 已发现且等待确认的更新；为 `Some` 时展示确认弹窗。
    pub pending: Option<PendingUpdate>,
}

impl UpdateState {
    /// 是否处于「检查或下载中」，用于禁用重复触发。
    pub fn busy(&self) -> bool {
        self.checking || self.downloading
    }

    /// 当前应展示在按钮上的文案键。
    pub fn button_label(&self) -> &'static str {
        if self.downloading {
            "settings.update.downloading"
        } else if self.checking {
            "settings.update.checking"
        } else {
            "settings.check_update"
        }
    }
}

/// 环境依赖条目。
///
/// 与旧版 macOS 版本相比去掉了 Homebrew（Windows 不需要包管理器），
/// 新增 WebView2（桌面模式渲染酒馆界面所需）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EnvironmentDependency {
    Git,
    NodeJs,
    Caddy,
    Pm2,
    WebView2,
}

impl EnvironmentDependency {
    /// 依赖名称。
    ///
    /// 这些是产品名（Git、Node.js、Caddy、PM2、WebView2），属于专有名词，
    /// 不属于需要翻译的界面文案，因此直接使用字面量。
    pub const fn name(self) -> &'static str {
        match self {
            Self::Git => "Git",
            Self::NodeJs => "Node.js",
            Self::Caddy => "Caddy",
            Self::Pm2 => "PM2",
            Self::WebView2 => "WebView2",
        }
    }
}

/// 某个环境来源下各依赖的已装版本。
///
/// `None` 表示未安装；WebView2 在系统来源下由注册表提供版本号，
/// 在内置来源下由 `lib/webview2/version.txt` 提供。
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct EnvironmentVersions {
    pub git: Option<String>,
    pub nodejs: Option<String>,
    pub caddy: Option<String>,
    pub pm2: Option<String>,
    pub webview2: Option<String>,
}

impl EnvironmentVersions {
    /// 按指定来源探测全部依赖版本。
    ///
    /// 探测会拉起多个子进程（`git --version`、`node --version` 等），
    /// 单次耗时可达数百毫秒，必须放在后台线程执行，避免阻塞 iced 主线程。
    pub fn detect(source: crate::core::settings::EnvSource) -> Self {
        use crate::core::settings::env_detect;
        Self {
            git: env_detect::detect_git(source),
            nodejs: env_detect::detect_nodejs(source),
            caddy: env_detect::detect_caddy(source),
            pm2: env_detect::detect_pm2(source),
            webview2: env_detect::detect_version("webview2", source),
        }
    }

    /// 读取指定依赖的版本。
    pub fn get(&self, dependency: EnvironmentDependency) -> Option<&String> {
        match dependency {
            EnvironmentDependency::Git => self.git.as_ref(),
            EnvironmentDependency::NodeJs => self.nodejs.as_ref(),
            EnvironmentDependency::Caddy => self.caddy.as_ref(),
            EnvironmentDependency::Pm2 => self.pm2.as_ref(),
            EnvironmentDependency::WebView2 => self.webview2.as_ref(),
        }
    }

    pub fn set(&mut self, dependency: EnvironmentDependency, version: String) {
        let slot = match dependency {
            EnvironmentDependency::Git => &mut self.git,
            EnvironmentDependency::NodeJs => &mut self.nodejs,
            EnvironmentDependency::Caddy => &mut self.caddy,
            EnvironmentDependency::Pm2 => &mut self.pm2,
            EnvironmentDependency::WebView2 => &mut self.webview2,
        };
        *slot = Some(version);
    }
}

/// 双来源环境探测结果。
///
/// 界面需要同时知道两套环境的安装情况：用户切换环境模式时要立即显示
/// 新来源的状态，而不是等下一次后台探测回来才刷新。
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct EnvironmentSnapshot {
    /// 内置 `lib/` 环境。
    pub builtin: EnvironmentVersions,
    /// 系统 PATH 环境。
    pub system: EnvironmentVersions,
    /// 是否已至少完成一次探测（未完成前界面显示「检测中」）。
    pub detected: bool,
}

impl EnvironmentSnapshot {
    /// 探测两套环境。
    pub fn detect_all() -> Self {
        use crate::core::settings::EnvSource;
        Self {
            builtin: EnvironmentVersions::detect(EnvSource::Builtin),
            system: EnvironmentVersions::detect(EnvSource::System),
            detected: true,
        }
    }

    /// 取出指定来源的探测结果。
    pub fn for_source(&self, source: crate::core::settings::EnvSource) -> &EnvironmentVersions {
        match source {
            crate::core::settings::EnvSource::Builtin => &self.builtin,
            crate::core::settings::EnvSource::System => &self.system,
        }
    }

    /// 可变取出指定来源的探测结果。
    pub fn for_source_mut(
        &mut self,
        source: crate::core::settings::EnvSource,
    ) -> &mut EnvironmentVersions {
        match source {
            crate::core::settings::EnvSource::Builtin => &mut self.builtin,
            crate::core::settings::EnvSource::System => &mut self.system,
        }
    }

    /// 指定来源下某个依赖是否已安装。
    ///
    /// 用于「该依赖当前是否可用」这类布尔判断，避免调用方层层展开字段。
    pub fn has(
        &self,
        source: crate::core::settings::EnvSource,
        dependency: EnvironmentDependency,
    ) -> bool {
        self.for_source(source).get(dependency).is_some()
    }

    /// 两套环境中任一来源装有该依赖即可用。
    ///
    /// 适用于「能不能用」而非「装在哪」的判断：例如酒馆既可以用内置 PM2，
    /// 也可以用用户自己装好的系统 PM2，只要有一套可用就不该拦截操作。
    pub fn has_any(&self, dependency: EnvironmentDependency) -> bool {
        self.has(crate::core::settings::EnvSource::Builtin, dependency)
            || self.has(crate::core::settings::EnvSource::System, dependency)
    }

    /// 更新某个来源下某个依赖的版本号。
    pub fn set(
        &mut self,
        source: crate::core::settings::EnvSource,
        dependency: EnvironmentDependency,
        version: String,
    ) {
        self.for_source_mut(source).set(dependency, version);
    }
}

#[derive(Debug, Clone, Default)]
pub struct EnvironmentTaskState {
    pub dependency: Option<EnvironmentDependency>,
    /// 本次安装写入的目标环境来源。
    pub source: crate::core::settings::EnvSource,
    pub show: bool,
    pub log: String,
    pub running: bool,
    pub done_at: Option<std::time::Instant>,
    pub started_at: Option<std::time::Instant>,
    pub timed_out: bool,
    pub failed: bool,
    /// 安装日志默认收起，用户需要时再展开查看完整详情。
    pub show_details: bool,
    /// 确定进度百分比（0-100）；下载类安装会持续更新。
    pub progress: Option<f32>,
    /// 当前阶段说明（已解析的文案，进入通道时固化）。
    pub stage: Option<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum SystemProxyStatus {
    Enabled,
    Disabled,
    #[default]
    Unknown,
}

#[derive(Debug, Clone, Default)]
pub struct GithubTestState {
    pub show: bool,
    pub running: bool,
    pub timed_out: bool,
    pub results: Option<Vec<crate::core::network::GithubMultiTestItem>>,
    pub error: Option<String>,
    pub mode_label: String,
    pub proxy_address: Option<String>,
    pub accelerate_url: Option<String>,
    pub started_at: Option<std::time::Instant>,
    pub current_key: Option<String>,
    pub current_name: Option<String>,
    pub clone_stage: Option<String>,
    pub clone_current: Option<u64>,
    pub clone_total: Option<u64>,
    pub clone_percentage: Option<f32>,
    pub download_total_bytes: Option<u64>,
    pub download_downloaded_bytes: u64,
    pub download_bytes_per_second: u64,
    pub download_percentage: Option<f32>,
    pub live_items: Vec<GithubLiveItem>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum GithubLiveItemStatus {
    Running,
    Finished,
}

#[derive(Debug, Clone)]
pub struct GithubLiveItem {
    pub key: String,
    pub name: String,
    pub status: GithubLiveItemStatus,
    pub result: Option<crate::core::network::GithubMultiTestItem>,
}

#[derive(Debug, Clone, Default)]
pub struct DownloadChannelTestState {
    pub show: bool,
    pub running: bool,
    pub timed_out: bool,
    pub all_failed: bool,
    pub started_at: Option<std::time::Instant>,
    pub done_at: Option<std::time::Instant>,
    pub current_channel: Option<DownloadChannel>,
    pub clone_stage: Option<String>,
    pub clone_current: Option<u64>,
    pub clone_total: Option<u64>,
    pub clone_percentage: Option<f32>,
    pub results: Vec<DownloadChannelTestResult>,
}

#[derive(Debug, Clone)]
pub struct SettingsState {
    pub language: DisplayLanguage,
    pub theme: ThemeMode,
    /// 普通界面文字、控件和间距的缩放比例。
    pub ui_scale: f32,
    /// 当前已经成功应用的字体设置键。
    pub font_family: String,
    /// 可搜索字体选择器的本地状态。
    pub font_choices: combo_box::State<FontChoice>,
    /// 字体选择器当前展示的选项。
    pub selected_font: FontChoice,
    /// 新字体文件是否正在交给渲染器加载。
    pub font_loading: bool,
    /// 字体发现或动态加载失败时展示的错误。
    pub appearance_error: Option<String>,
    pub remember_window_position: bool,
    pub auto_start: bool,
    pub cpu_cores: CpuCores,
    pub start_mode: StartMode,
    pub auto_stop_tavern_on_window_close: bool,
    pub tavern_export_path: String,
    pub server_mode_enabled: bool,
    pub server_service_mode: ServerServiceMode,
    pub allow_tavern_background: bool,
    pub data_mode: TavernDataMode,
    pub global_data_path: String,
    pub show_startup_command: bool,
    pub npm_registry: NpmRegistry,
    /// 兼容旧版配置的隐藏字段，新版下载设置不再展示它们。
    pub github_proxy_enabled: bool,
    pub github_proxy_url: String,
    pub download_channel: DownloadChannel,
    pub download_resolved_channel: Option<DownloadChannel>,
    pub download_channel_last_tested: Option<u64>,
    pub download_channel_test: DownloadChannelTestState,
    pub proxy_mode: ProxyMode,
    pub custom_proxy: String,
    pub system_proxy_status: SystemProxyStatus,
    /// 当前选中的环境来源（内置 `lib/` 或系统 PATH）。
    pub env_mode: crate::core::settings::EnvSource,
    /// 双来源环境探测结果。
    pub environment: EnvironmentSnapshot,
    pub environment_task: EnvironmentTaskState,
    pub github_test: GithubTestState,
    /// 启动器更新模块的界面状态。
    pub update: UpdateState,
    pub last_action: Option<SettingsAction>,
    /// 最近一次偏好设置保存失败的错误信息。
    pub save_error: Option<String>,
}

impl Default for SettingsState {
    fn default() -> Self {
        Self {
            language: DisplayLanguage::System,
            theme: ThemeMode::System,
            ui_scale: DEFAULT_UI_SCALE,
            font_family: crate::core::typography::DEFAULT_FONT_KEY.to_owned(),
            font_choices: combo_box::State::with_selection(
                vec![FontChoice::default_choice()],
                Some(&FontChoice::default_choice()),
            ),
            selected_font: FontChoice::default_choice(),
            font_loading: false,
            appearance_error: None,
            remember_window_position: true,
            auto_start: false,
            cpu_cores: CpuCores::Auto,
            start_mode: StartMode::Normal,
            auto_stop_tavern_on_window_close: true,
            tavern_export_path: "~/Downloads".into(),
            server_mode_enabled: false,
            server_service_mode: ServerServiceMode::Lan,
            allow_tavern_background: false,
            data_mode: TavernDataMode::Current,
            global_data_path: crate::core::settings::DEFAULT_GLOBAL_DATA_PATH.into(),
            show_startup_command: false,
            npm_registry: NpmRegistry::Npmmirror,
            github_proxy_enabled: false,
            github_proxy_url: "https://gh-proxy.org/".into(),
            download_channel: DownloadChannel::Auto,
            download_resolved_channel: None,
            download_channel_last_tested: None,
            download_channel_test: DownloadChannelTestState::default(),
            proxy_mode: ProxyMode::System,
            custom_proxy: String::new(),
            system_proxy_status: SystemProxyStatus::Unknown,
            env_mode: crate::core::settings::EnvSource::Builtin,
            environment: EnvironmentSnapshot::default(),
            environment_task: EnvironmentTaskState::default(),
            github_test: GithubTestState::default(),
            update: UpdateState::default(),
            last_action: None,
            save_error: None,
        }
    }
}

impl SettingsState {
    /// 使用当前机器字体目录配置可搜索选项并解析保存值。
    pub(crate) fn configure_fonts(
        &mut self,
        catalog: &crate::core::typography::SystemFontCatalog,
    ) -> FontChoice {
        let selected = catalog.resolve(&self.font_family);
        self.font_family = selected.key().to_owned();
        self.selected_font = selected;
        self.font_choices = combo_box::State::with_selection(
            catalog.choices(),
            Some(&selected),
        );
        selected
    }

    /// 让字体选择器回到已经生效的字体。
    pub(crate) fn select_font(&mut self, selected: FontChoice) {
        self.selected_font = selected;
        self.font_choices = combo_box::State::with_selection(
            self.font_choices.options().to_vec(),
            Some(&selected),
        );
    }

    /// 判断自动下载渠道缓存是否仍在七天有效期内。
    pub fn download_channel_cache_valid(&self) -> bool {
        let Some(resolved) = self.download_resolved_channel else {
            return false;
        };
        if resolved == DownloadChannel::Auto {
            return false;
        }
        let Some(tested_at) = self.download_channel_last_tested else {
            return false;
        };
        let Some(now) = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .ok()
            .map(|duration| duration.as_secs())
        else {
            return false;
        };
        now.saturating_sub(tested_at) < 7 * 24 * 60 * 60
    }

    /// 将磁盘偏好应用到设置页状态.
    pub fn apply_persistent_preferences(
        &mut self,
        preferences: &crate::core::settings::PersistentPreferences,
    ) {
        self.language = preferences.language;
        self.theme = preferences.theme;
        self.ui_scale = normalize_ui_scale(preferences.ui_scale);
        self.font_family = preferences.font_family.clone();
        self.remember_window_position = preferences.remember_window_position;
        self.data_mode = match preferences.data_mode.as_str() {
            "global" => TavernDataMode::Global,
            _ => TavernDataMode::Current,
        };
        self.global_data_path = preferences.global_data_path.clone();
        self.tavern_export_path = preferences.tavern_export_path.clone();
        self.start_mode = match preferences.start_mode.as_str() {
            "desktop" => StartMode::Desktop,
            _ => StartMode::Normal,
        };
        self.server_mode_enabled = preferences.server_mode_enabled;
        self.server_service_mode = ServerServiceMode::from_key(&preferences.server_service_mode);
        self.auto_stop_tavern_on_window_close = preferences.auto_stop_tavern_on_window_close;
        self.allow_tavern_background = preferences.allow_tavern_background;
        self.show_startup_command = preferences.show_startup_command;
        if self.server_mode_enabled {
            self.start_mode = StartMode::Normal;
        }
        self.proxy_mode = match preferences.proxy_mode.as_str() {
            "none" => ProxyMode::None,
            "custom" => ProxyMode::Custom,
            _ => ProxyMode::System,
        };
        self.custom_proxy = preferences.custom_proxy.clone();
        self.download_channel = DownloadChannel::from_key(&preferences.download_channel);
        self.npm_registry = NpmRegistry::from_url(&preferences.npm_registry);
        self.github_proxy_enabled = preferences.github_proxy_enabled;
        self.github_proxy_url = preferences.github_proxy_url.clone();
        self.cpu_cores = preferences.cpu_cores;
    }
}

pub fn settings_view(state: &SettingsState, mode_controls_locked: bool) -> Element<'_, Message> {
    let header = row![
        column![
            text("settings.title").size(24).font(crate::core::typography::medium()),
            text("settings.subtitle")
                .size(12)
                .font(crate::core::typography::regular())
                .style(crate::theme::muted_text_style)
        ]
        .spacing(4),
        space::horizontal(),
        button(
            row![
                crate::theme::muted_icon(Icon::RotateCcw, 15),
                text("settings.restore_defaults").size(12).font(crate::core::typography::medium())
            ]
            .spacing(7)
            .align_y(Alignment::Center)
        )
        .on_press(Message::SettingsRestoreDefaults)
        .height(36)
        .padding([8, 13])
        .style(button_style(ButtonVariant::Outline)),
    ]
    .align_y(Alignment::Center)
    .width(Fill);

    let mut sections = column![
        interface_settings(state),
        basic_settings(state, mode_controls_locked),
        console_settings(state),
        environment_settings(state),
        download_settings(state),
        network_settings(state),
        software_settings(state),
    ]
    .spacing(22)
    .width(Fill);
    if let Some(error) = &state.appearance_error {
        sections = sections.push(crate::theme::alert(
            t_in("settings.interface.font.error_title", current_language()),
            error,
            AlertKind::Danger,
        ));
    }
    if let Some(error) = &state.save_error {
        sections = sections.push(crate::theme::alert(
            "settings.save_failed",
            error,
            AlertKind::Danger,
        ));
    }

    let mut page: Element<'_, Message> = container(
        column![
            header,
            scrollable(container(sections).padding([0, 24]))
                .id(settings_scroll_id())
                .width(Fill)
                .height(Fill)
        ]
        .spacing(20),
    )
    .width(Fill)
    .height(Fill)
    .padding([24, 28])
    .style(crate::theme::canvas_style)
    .into();

    if state.environment_task.show {
        page = stack![page, environment_task_modal(&state.environment_task)]
            .width(Fill)
            .height(Fill)
            .into();
    }
    if state.github_test.show {
        page = stack![page, github_test_modal(&state.github_test)]
            .width(Fill)
            .height(Fill)
            .into();
    }
    if state.download_channel_test.show {
        page = stack![page, download_channel_test_modal(state)]
            .width(Fill)
            .height(Fill)
            .into();
    }
    page
}

/// 本地实例缺少 Node.js 时显示的全局安装引导。
///
/// 安装按钮由应用根层处理，以便同时完成页面导航和复用设置页的安装动作。
pub(crate) fn nodejs_required_modal() -> Element<'static, Message> {
    let language = current_language();
    let panel = mouse_area(
        container(
            column![
                row![
                    container(icons::icon(
                        Icon::CodeXml,
                        22,
                        iced::Color::from_rgb8(88, 80, 236),
                    ))
                    .width(42)
                    .height(42)
                    .align_x(Alignment::Center)
                    .align_y(Alignment::Center)
                    .style(environment_log_style),
                    column![
                        raw(t_in("environment.nodejs_required.title", language))
                            .size(18)
                            .font(crate::core::typography::medium()),
                        raw(t_in("environment.nodejs_required.description", language))
                            .size(12)
                            .font(crate::core::typography::regular())
                            .style(crate::theme::muted_text_style),
                    ]
                    .spacing(5)
                    .width(Fill),
                ]
                .spacing(14)
                .align_y(Alignment::Center),
                crate::theme::alert(
                    t_in("environment.nodejs_required.title", language),
                    t_in("environment.nodejs_required.action_hint", language),
                    AlertKind::Info,
                ),
                row![
                    space::horizontal(),
                    button(
                        raw(t_in("environment.nodejs_required.later", language))
                            .size(12)
                            .font(crate::core::typography::medium())
                    )
                    .on_press(Message::DismissNodeJsRequired)
                    .height(36)
                    .padding([8, 14])
                    .style(button_style(ButtonVariant::Secondary)),
                    button(
                        row![
                            icons::icon(Icon::Download, 15, iced::Color::WHITE),
                            raw(t_in("environment.nodejs_required.install", language))
                                .size(12)
                                .font(crate::core::typography::medium())
                                .color(iced::Color::WHITE),
                        ]
                        .spacing(7)
                        .align_y(Alignment::Center)
                    )
                    .on_press(Message::InstallRequiredNodeJs)
                    .height(36)
                    .padding([8, 15])
                    .style(button_style(ButtonVariant::Primary)),
                ]
                .spacing(10)
                .align_y(Alignment::Center)
                .width(Fill),
            ]
            .spacing(18),
        )
        .width(Fill).max_width(500)
        .padding(22)
        .style(environment_modal_style),
    )
    .on_press(Message::NodeJsRequiredInteract);

    stack![
        button(space::Space::new())
            .on_press(Message::NodeJsRequiredInteract)
            .width(Fill)
            .height(Fill)
            .padding(0)
            .style(environment_backdrop_style),
        container(panel)
            .width(Fill)
            .height(Fill)
            .align_x(Alignment::Center)
            .align_y(Alignment::Center)
            .padding(24),
    ]
    .width(Fill)
    .height(Fill)
    .into()
}

fn download_channel_test_modal(state: &SettingsState) -> Element<'_, Message> {
    let test = &state.download_channel_test;
    let phase = test
        .started_at
        .map(|started| (started.elapsed().as_secs_f32() * 0.9).fract())
        .unwrap_or(0.0);
    let channels = DownloadChannel::fixed_channels();
    let rows = channels
        .into_iter()
        .map(|channel| {
            let result = test.results.iter().find(|result| result.channel == channel);
            let is_current = test.current_channel == Some(channel) && result.is_none();
            let (icon, detail, indicator): (
                Element<'static, Message>,
                Element<'static, Message>,
                Element<'static, Message>,
            ) = if let Some(result) = result {
                let detail = if result.success {
                    result
                        .latency_ms
                        .map(|latency| tf("settings.channel_test.speed_ok_latency", &[("latency", &latency)]))
                        .unwrap_or_else(|| t("settings.channel_test.speed_ok").to_owned())
                } else {
                    result
                        .error
                        .clone()
                        .unwrap_or_else(|| t("settings.channel_test.speed_failed").to_owned())
                };
                let icon = if result.success {
                    icons::icon(Icon::CircleCheck, 16, iced::Color::from_rgb8(23, 201, 100))
                } else {
                    icons::icon(Icon::CircleX, 16, iced::Color::from_rgb8(255, 56, 60))
                };
                (
                    icon,
                    raw(detail)
                        .size(10)
                        .font(crate::core::typography::regular())
                        .style(crate::theme::muted_text_style)
                        .into(),
                    text("").into(),
                )
            } else if is_current {
                let stage = test
                    .clone_stage
                    .as_deref()
                    .map(crate::lang::github_clone_stage_label)
                    .unwrap_or_else(|| t("git.clone.preparing").to_owned());
                let percent = test
                    .clone_percentage
                    .map(|value| format!("{value:.0}%"))
                    .unwrap_or_else(|| t("git.clone.in_progress").to_owned());
                let detail = format!("{stage} {percent}");
                let counts = match (test.clone_current, test.clone_total) {
                    (Some(current), Some(total)) => Some(tf("git.clone.objects", &[("current", &current), ("total", &total)])),
                    _ => None,
                };
                let detail = if let Some(counts) = counts {
                    format!("{detail} · {counts}")
                } else {
                    detail
                };
                let indicator: Element<'static, Message> = match test.clone_percentage {
                    Some(value) => ProgressCircle::new(value)
                        .color(ProgressCircleColor::Accent)
                        .into(),
                    None => ProgressCircle::new(0.0)
                        .is_indeterminate(true)
                        .animation_phase(phase)
                        .color(ProgressCircleColor::Accent)
                        .into(),
                };
                (
                    crate::theme::subtle_icon(Icon::Circle, 13),
                    raw(detail)
                        .size(10)
                        .font(crate::core::typography::regular())
                        .style(crate::theme::muted_text_style)
                        .into(),
                    indicator,
                )
            } else {
                (
                    crate::theme::subtle_icon(Icon::Circle, 13),
                    text("settings.channel_test.waiting").into(),
                    text("").into(),
                )
            };
            row![
                icon,
                column![text(channel.label_key()).size(12).font(crate::core::typography::medium()), detail]
                    .spacing(3)
                    .width(Fill),
                indicator,
            ]
            .spacing(10)
            .padding([9, 10])
            .align_y(Alignment::Center)
            .into()
        })
        .collect::<Vec<_>>();

    let status: Element<'_, Message> = if test.running {
        row![
            ProgressCircle::new(0.0)
                .is_indeterminate(true)
                .animation_phase(phase)
                .color(ProgressCircleColor::Accent),
            text("settings.channel_test.running").size(12).font(crate::core::typography::medium()),
        ]
        .spacing(8)
        .align_y(Alignment::Center)
        .into()
    } else if test.timed_out {
        row![
            icons::icon(Icon::ClockAlert, 16, iced::Color::from_rgb8(245, 165, 36)),
            text("settings.channel_test.slow_hint")
                .size(12)
                .font(crate::core::typography::medium()),
        ]
        .spacing(8)
        .align_y(Alignment::Center)
        .into()
    } else if test.all_failed {
        row![
            icons::icon(Icon::CircleX, 16, iced::Color::from_rgb8(255, 56, 60)),
            text("settings.channel_test.all_failed")
                .size(12)
                .font(crate::core::typography::medium()),
        ]
        .spacing(8)
        .align_y(Alignment::Center)
        .into()
    } else {
        row![
            icons::icon(Icon::CircleCheck, 16, iced::Color::from_rgb8(23, 201, 100)),
            raw(tf(
                "settings.channel_test.fastest",
                &[(
                    "channel",
                    &t(state
                        .download_resolved_channel
                        .or(test.current_channel)
                        .unwrap_or(DownloadChannel::Official)
                        .label_key()),
                )],
            ))
            .size(12)
            .font(crate::core::typography::medium()),
        ]
        .spacing(8)
        .align_y(Alignment::Center)
        .into()
    };

    let footer = row![
        status,
        space::horizontal(),
        button(
            text(if test.running { "common.cancel" } else { "common.close" })
                .size(12)
                .font(crate::core::typography::medium())
        )
        .on_press(Message::DownloadChannelTestClose)
        .height(34)
        .padding([7, 14])
        .style(button_style(ButtonVariant::Secondary)),
    ]
    .spacing(12)
    .align_y(Alignment::Center)
    .width(Fill);

    let panel = mouse_area(
        container(
            column![
                text("settings.channel_test.title").size(18).font(crate::core::typography::medium()),
                rule::horizontal(1.0).style(crate::theme::separator_style),
                scrollable(
                    container(column(rows).spacing(8))
                        .padding(12)
                        .style(environment_log_style)
                )
                .height(if crate::core::typography::current_ui_scale() >= 1.35 {
                    210
                } else {
                    300
                }),
                rule::horizontal(1.0).style(crate::theme::separator_style),
                footer,
            ]
            .spacing(16),
        )
        .width(Fill).max_width(620)
        .padding(20)
        .style(environment_modal_style),
    )
    .on_press(Message::DownloadChannelTestInteract);

    stack![
        button(space::Space::new())
            .on_press(Message::DownloadChannelTestInteract)
            .width(Fill)
            .height(Fill)
            .padding(0)
            .style(environment_backdrop_style),
        container(panel)
            .width(Fill)
            .height(Fill)
            .align_x(Alignment::Center)
            .align_y(Alignment::Center)
            .padding(24),
    ]
    .width(Fill)
    .height(Fill)
    .into()
}

fn environment_task_modal(task: &EnvironmentTaskState) -> Element<'_, Message> {
    let dependency = task.dependency.unwrap_or(EnvironmentDependency::Git);
    let language = current_language();
    let elapsed = task
        .started_at
        .map(|started| started.elapsed().as_secs())
        .unwrap_or_default();
    let phase = task
        .started_at
        .map(|started| (started.elapsed().as_secs_f32() * 0.9).fract())
        .unwrap_or(0.0);

    let (status_key, description_key, progress, progress_color) = if task.running {
        (
            "environment.install.running",
            "environment.install.executing",
            0.0,
            ProgressBarColor::Accent,
        )
    } else if task.timed_out {
        (
            "environment.install.timed_out",
            "environment.install.timeout_description",
            100.0,
            ProgressBarColor::Warning,
        )
    } else if task.failed {
        (
            "environment.install.failed",
            "environment.install.not_completed",
            100.0,
            ProgressBarColor::Danger,
        )
    } else {
        (
            "environment.install.success",
            "environment.install.ready",
            100.0,
            ProgressBarColor::Success,
        )
    };

    let status_icon: Element<'_, Message> = if task.running {
        container(
            ProgressCircle::new(0.0)
                .is_indeterminate(true)
                .animation_phase(phase)
                .size(ProgressCircleSize::Small)
                .color(ProgressCircleColor::Accent),
        )
        .width(28)
        .height(28)
        .align_x(Alignment::Center)
        .align_y(Alignment::Center)
        .style(environment_task_icon_style)
        .into()
    } else {
        let (icon, color) = if task.timed_out {
            (Icon::ClockAlert, iced::Color::from_rgb8(245, 165, 36))
        } else if task.failed {
            (Icon::CircleX, iced::Color::from_rgb8(255, 56, 60))
        } else {
            (Icon::CircleCheck, iced::Color::from_rgb8(23, 201, 100))
        };
        container(icons::icon(icon, 17, color))
            .width(28)
            .height(28)
            .align_x(Alignment::Center)
            .align_y(Alignment::Center)
            .style(environment_task_icon_style)
            .into()
    };

    let status_title = format!("{} {}", t_in(status_key, language), dependency.name());
    let elapsed_label = format!(
        "{} {}s",
        t_in("environment.install.elapsed", language),
        elapsed
    );
    let latest_line = task
        .log
        .lines()
        .rev()
        .find(|line| !line.trim().is_empty())
        .map(|line| truncate_environment_log(line, 78))
        .unwrap_or_else(|| t_in("environment.install.waiting", language).to_owned());

    let action_label = if task.running {
        t_in("environment.install.cancel", language)
    } else {
        t_in("environment.install.close", language)
    };
    let details_label = if task.show_details {
        t_in("environment.install.hide_details", language)
    } else {
        t_in("environment.install.show_details", language)
    };

    let header = row![
        status_icon,
        column![
            raw(status_title).size(14).font(crate::core::typography::medium()),
            raw(format!(
                "{}  ·  {}",
                t_in(description_key, language),
                elapsed_label
            ))
            .size(10)
            .font(crate::core::typography::regular())
            .style(crate::theme::muted_text_style),
        ]
        .spacing(2)
        .width(Fill),
        button(text(action_label).size(10).font(crate::core::typography::medium()))
            .on_press(Message::EnvironmentTaskClose)
            .height(28)
            .padding([5, 10])
            .style(button_style(ButtonVariant::Secondary)),
    ]
    .spacing(10)
    .align_y(Alignment::Center)
    .width(Fill);

    let summary = row![
        icons::icon(
            if task.failed || task.timed_out {
                Icon::CircleAlert
            } else {
                Icon::CircleCheck
            },
            13,
            if task.failed {
                iced::Color::from_rgb8(255, 56, 60)
            } else if task.timed_out {
                iced::Color::from_rgb8(245, 165, 36)
            } else {
                INK_MUTED
            },
        ),
        raw(latest_line)
            .size(10)
            .font(Font::MONOSPACE)
            .style(crate::theme::muted_text_style),
    ]
    .spacing(6)
    .align_y(Alignment::Center);

    let details_toggle = button(
        row![
            crate::theme::muted_icon(
                if task.show_details {
                    Icon::ChevronUp
                } else {
                    Icon::ChevronDown
                },
                14,
            ),
            text(details_label).size(10).font(crate::core::typography::medium()),
            space::horizontal(),
        ]
        .spacing(7)
        .align_y(Alignment::Center)
        .width(Fill),
    )
    .on_press(Message::EnvironmentTaskToggleDetails)
    .width(Fill)
    .padding([6, 0])
    .style(button_style(ButtonVariant::Ghost));

    let mut content = column![
        header,
        ProgressBar::new(progress)
            .show_value(false)
            // brew/npm 不提供可信的整体百分比，安装期间统一使用不确定进度。
            .is_indeterminate(task.running)
            .animation_phase(phase)
            .size(ProgressBarSize::Small)
            .color(progress_color),
    ]
    .spacing(8)
    .width(Fill);

    if task.running {
        content = content.push(
            raw(t_in("environment.install.progress_unknown", language))
                .size(9)
                .font(crate::core::typography::regular())
                .style(crate::theme::muted_text_style),
        );
    }
    content = content.push(summary).push(details_toggle);

    if task.show_details {
        let log = if task.log.is_empty() {
            t_in("environment.install.waiting", language)
        } else {
            task.log.as_str()
        };
        content = content.push(
            scrollable(
                container(
                    raw(log)
                        .size(10)
                        .font(Font::MONOSPACE)
                        .style(crate::theme::text_style),
                )
                .width(Fill)
                .padding(12)
                .style(environment_log_style),
            )
            .height(if crate::core::typography::current_ui_scale() >= 1.35 {
                120
            } else {
                180
            }),
        );
    }

    let panel = mouse_area(
        container(content)
            .width(Fill).max_width(500)
            .padding(12)
            .style(environment_modal_style),
    )
    .on_press(Message::EnvironmentModalInteract);

    stack![
        button(space::Space::new())
            .on_press(Message::EnvironmentModalInteract)
            .width(Fill)
            .height(Fill)
            .padding(0)
            .style(environment_backdrop_style),
        container(panel)
            .width(Fill)
            .height(Fill)
            .align_x(Alignment::Center)
            .align_y(Alignment::Center)
            .padding(24),
    ]
    .width(Fill)
    .height(Fill)
    .into()
}

/// 安装摘要只展示最近一行，并在固定宽度内保留省略号。
fn truncate_environment_log(line: &str, max_chars: usize) -> String {
    let clean = line.trim();
    if clean.chars().count() <= max_chars {
        return clean.to_owned();
    }
    let mut result: String = clean.chars().take(max_chars.saturating_sub(1)).collect();
    result.push('…');
    result
}

fn interface_settings(state: &SettingsState) -> Element<'_, Message> {
    section(
        Icon::Palette,
        "settings.section.interface",
        "settings.section.interface.hint",
        section_rows(vec![
            setting_row(
                Icon::Languages,
                "settings.field.language",
                "settings.field.language.hint",
                segmented_control(
                    &[
                        (DisplayLanguage::System, "settings.language.system", Icon::Monitor),
                        (
                            DisplayLanguage::SimplifiedChinese,
                            "settings.language.simplified_chinese",
                            Icon::Languages,
                        ),
                        (DisplayLanguage::English, "English", Icon::Languages),
                    ],
                    state.language,
                    Message::SettingsLanguageSelected,
                ),
            ),
            setting_row(
                Icon::SunMoon,
                "settings.field.theme",
                "settings.field.theme.hint",
                segmented_control(
                    &[
                        (ThemeMode::System, "settings.theme_mode.system", Icon::Monitor),
                        (ThemeMode::Light, "settings.theme_mode.light", Icon::Sun),
                        (ThemeMode::Dark, "settings.theme_mode.dark", Icon::Moon),
                    ],
                    state.theme,
                    Message::SettingsThemeSelected,
                ),
            ),
            setting_row(
                Icon::Scaling,
                "settings.interface.scale.title",
                "settings.interface.scale.description",
                ui_scale_control(state.ui_scale),
            ),
            setting_row(
                Icon::Type,
                "settings.interface.font.title",
                "settings.interface.font.description",
                font_control(state),
            ),
            setting_row(
                Icon::PanelsTopLeft,
                "settings.field.remember_window",
                "settings.field.remember_window.hint",
                toggle_control(
                    state.remember_window_position,
                    Message::SettingsRememberWindowPosition,
                ),
            ),
        ]),
    )
}

fn basic_settings(state: &SettingsState, mode_controls_locked: bool) -> Element<'_, Message> {
    let launch_mode_control: Element<'_, Message> = if state.server_mode_enabled {
        readonly_text("settings.quick_start.server")
    } else {
        start_mode_control(state.start_mode, !mode_controls_locked)
    };

    let mut rows = vec![
        setting_row(
            Icon::Power,
            "settings.field.auto_launch",
            "settings.field.auto_launch.hint",
            row![
                toggle_control(state.auto_start, Message::SettingsAutoStart),
                action_button(
                    "settings.auto_launch.open_login_items",
                    Icon::ExternalLink,
                    SettingsAction::OpenLoginItemSettings
                )
            ]
            .spacing(10)
            .align_y(Alignment::Center)
            .into(),
        ),
        setting_row(
            Icon::Cpu,
            "settings.field.scan_cores",
            "settings.field.scan_cores.hint",
            segmented_control(
                // 文案键由枚举自己提供，避免界面与核心层各写一份导致不同步。
                &[
                    (CpuCores::Auto, CpuCores::Auto.label_key(), Icon::Gauge),
                    (CpuCores::Half, CpuCores::Half.label_key(), Icon::CircleGauge),
                    (CpuCores::All, CpuCores::All.label_key(), Icon::Cpu),
                ],
                state.cpu_cores,
                Message::SettingsCpuCoresSelected,
            ),
        ),
        setting_row(
            Icon::Play,
            "settings.field.tavern_start_mode",
            "settings.field.tavern_start_mode.hint",
            launch_mode_control,
        ),
    ];

    // WebView 相关设置仅在桌面模式下显示。
    if !state.server_mode_enabled && state.start_mode == StartMode::Desktop {
        rows.extend([
            setting_row(
                Icon::CircleStop,
                "settings.field.stop_on_window_close",
                "settings.field.stop_on_window_close.hint",
                toggle_control(
                    state.auto_stop_tavern_on_window_close,
                    Message::SettingsAutoStopTavern,
                ),
            ),
            setting_row(
                Icon::FolderOpen,
                "settings.field.resource_save_path",
                "settings.field.resource_save_path.hint",
                path_control(&state.tavern_export_path, SettingsAction::ChooseExportPath),
            ),
        ]);
    }

    rows.push(setting_row(
        Icon::Server,
        "settings.field.server_mode",
        "settings.field.server_mode.hint",
        if mode_controls_locked {
            readonly_text(if state.server_mode_enabled { "workbench.field.enabled" } else { "settings.value.not_enabled" })
        } else {
            toggle_control(state.server_mode_enabled, Message::SettingsServerMode)
        },
    ));

    // 酒馆服务模式仅在服务器模式启用时显示。
    if state.server_mode_enabled {
        rows.push(setting_row(
            Icon::Globe,
            "settings.field.tavern_service_mode",
            "settings.field.tavern_service_mode.hint",
            segmented_control_enabled(
                &[
                    (ServerServiceMode::Lan, "settings.service_mode.lan", Icon::Wifi),
                    (ServerServiceMode::Internet, "settings.service_mode.internet", Icon::Earth),
                ],
                state.server_service_mode,
                !mode_controls_locked,
                Message::SettingsServerServiceModeSelected,
            ),
        ));
    }

    if state.server_mode_enabled {
        // 后台常驻依赖 PM2；内置与系统两套环境中任一可用即可放开开关。
        let pm2_available = state
            .environment
            .has_any(EnvironmentDependency::Pm2);
        rows.push(setting_row(
            Icon::CloudCog,
            "settings.field.allow_background",
            "settings.field.allow_background.hint",
            if mode_controls_locked || !pm2_available {
                readonly_text(if state.allow_tavern_background { "workbench.field.enabled" } else { "settings.value.not_enabled" })
            } else {
                toggle_control(
                    state.allow_tavern_background,
                    Message::SettingsAllowTavernBackground,
                )
            },
        ));
        if state.server_service_mode == ServerServiceMode::Internet {
            rows.push(setting_row(
                Icon::Waypoints,
                "settings.field.reverse_proxy",
                "settings.field.reverse_proxy.hint",
                readonly_text("settings.value.pending_development"),
            ));
        }
    }

    rows.extend([
        setting_row(
            Icon::Database,
            "settings.field.tavern_data_mode",
            "settings.field.tavern_data_mode.hint",
            segmented_control(
                &[
                    (TavernDataMode::Global, "settings.data_mode.global", Icon::Database),
                    (TavernDataMode::Current, "settings.data_mode.current", Icon::HardDrive),
                ],
                state.data_mode,
                Message::SettingsDataModeSelected,
            ),
        ),
        setting_row(
            Icon::FolderCog,
            "settings.field.global_data_path",
            "settings.field.global_data_path.hint",
            path_control(
                &state.global_data_path,
                SettingsAction::ChooseGlobalDataPath,
            ),
        ),
    ]);

    section(
        Icon::SlidersHorizontal,
        "settings.section.basic",
        "settings.section.basic.hint",
        section_rows(rows),
    )
}

fn console_settings(state: &SettingsState) -> Element<'_, Message> {
    section(
        Icon::SquareTerminal,
        "settings.section.console",
        "settings.section.console.hint",
        section_rows(vec![setting_row(
            Icon::Terminal,
            "settings.field.show_startup_command",
            "settings.field.show_startup_command.hint",
            toggle_control(
                state.show_startup_command,
                Message::SettingsShowStartupCommand,
            ),
        )]),
    )
}

fn environment_settings(state: &SettingsState) -> Element<'_, Message> {
    use crate::core::settings::{EnvSource, env_detect};

    let source = state.env_mode;
    let versions = state.environment.for_source(source);
    let is_system = source == EnvSource::System;

    let nodejs_installed = versions.nodejs.is_some();
    let nodejs_outdated = versions
        .nodejs
        .as_deref()
        .is_some_and(env_detect::is_nodejs_outdated);
    let nodejs_title = dependency_title("Node.js", nodejs_outdated);

    let caddy_description = if state.server_mode_enabled {
        "settings.dep.caddy.required"
    } else {
        "settings.dep.caddy.optional"
    };
    let pm2_description = if state.server_mode_enabled {
        "settings.dep.pm2.required"
    } else {
        "settings.dep.pm2.optional"
    };

    let rows = section_rows(vec![
        // 环境模式：决定后续每一行从哪套环境读取版本、往哪套环境安装。
        environment_mode_row(state),
        environment_dependency_row(
            Icon::GitBranch,
            "Git".to_owned(),
            "settings.dep.git",
            environment_version_or_action(
                source,
                EnvironmentDependency::Git,
                versions.git.clone(),
                false,
                // 系统环境由用户自行维护，启动器不提供安装入口。
                !is_system,
            ),
        ),
        environment_dependency_row(
            Icon::CodeXml,
            nodejs_title,
            "settings.dep.nodejs",
            environment_version_or_action(
                source,
                EnvironmentDependency::NodeJs,
                versions.nodejs.clone(),
                nodejs_outdated,
                !is_system,
            ),
        ),
        npm_registry_setting(state),
        environment_dependency_row(
            Icon::ShieldCheck,
            "Caddy".to_owned(),
            caddy_description,
            environment_version_or_action(
                source,
                EnvironmentDependency::Caddy,
                versions.caddy.clone(),
                false,
                !is_system,
            ),
        ),
        environment_dependency_row(
            Icon::CloudDownload,
            "PM2".to_owned(),
            pm2_description,
            environment_version_or_action(
                source,
                EnvironmentDependency::Pm2,
                versions.pm2.clone(),
                false,
                // PM2 是 npm 全局包，必须先有 Node.js。
                !is_system && nodejs_installed,
            ),
        ),
        environment_dependency_row(
            Icon::MonitorSmartphone,
            "WebView2".to_owned(),
            "settings.dep.webview2",
            environment_version_or_action(
                source,
                EnvironmentDependency::WebView2,
                versions.webview2.clone(),
                false,
                !is_system,
            ),
        ),
    ]);

    section(
        Icon::PackageOpen,
        "settings.section.environment",
        "settings.section.environment.hint",
        rows,
    )
}

/// 环境模式切换行。
///
/// 使用分段控件而非下拉框，使两个选项始终可见，避免用户误以为只有一种模式。
fn environment_mode_row(state: &SettingsState) -> Element<'_, Message> {
    use crate::core::settings::EnvSource;

    row![
        setting_icon(Icon::Wrench),
        column![
            text("settings.env_mode.title")
                .size(13)
                .font(crate::core::typography::medium())
                .style(crate::theme::text_style),
            text("settings.env_mode.hint")
                .size(11)
                .font(crate::core::typography::regular())
                .style(crate::theme::muted_text_style),
        ]
        .spacing(4)
        .width(Fill),
        segmented_control(
            &[
                (EnvSource::Builtin, "settings.env_mode.builtin", Icon::Package),
                (EnvSource::System, "settings.env_mode.system", Icon::Globe),
            ],
            state.env_mode,
            Message::SettingsEnvModeSelected,
        ),
    ]
    .spacing(12)
    .padding([13, 16])
    .align_y(Alignment::Center)
    .width(Fill)
    .into()
}

fn npm_registry_setting(state: &SettingsState) -> Element<'_, Message> {
    row![
        setting_icon(Icon::Globe),
        column![
            text("settings.npm.source_title")
                .size(13)
                .font(crate::core::typography::medium())
                .style(crate::theme::text_style),
            text("settings.npm.source_hint")
                .size(11)
                .font(crate::core::typography::regular())
                .style(crate::theme::muted_text_style),
            row![
                text("settings.npm.source_url")
                    .size(10)
                    .font(crate::core::typography::regular())
                    .style(crate::theme::muted_text_style),
                raw(state.npm_registry.url())
                    .size(10)
                    .font(crate::core::typography::regular())
                    .style(crate::theme::text_style),
            ]
            .spacing(3)
            .align_y(Alignment::Center),
        ]
        .spacing(4)
        .width(Fill),
        segmented_control(
            &[
                (NpmRegistry::Npmmirror, "npmmirror", Icon::Package),
                (NpmRegistry::HuaweiCloud, "settings.npm.huawei_cloud", Icon::Cloud),
                (NpmRegistry::Tencent, "settings.npm.tencent", Icon::CloudCog),
                (NpmRegistry::Official, "settings.npm.official", Icon::Boxes),
            ],
            state.npm_registry,
            Message::SettingsNpmRegistrySelected,
        ),
    ]
    .spacing(12)
    .padding([13, 16])
    .align_y(Alignment::Center)
    .width(Fill)
    .into()
}

fn dependency_title(name: &str, outdated: bool) -> String {
    if outdated {
        tf("settings.dep.version_outdated", &[("name", &name)])
    } else {
        name.to_owned()
    }
}

fn environment_version_or_action(
    source: crate::core::settings::EnvSource,
    dependency: EnvironmentDependency,
    version: Option<String>,
    outdated: bool,
    enabled: bool,
) -> Element<'static, Message> {
    match version {
        Some(_) if outdated => environment_action_button(
            "settings.dep.update",
            Icon::ArrowUp,
            dependency,
            source,
            enabled,
        ),
        Some(version) => raw(version)
            .size(13)
            .font(crate::core::typography::medium())
            .style(crate::theme::text_style)
            .into(),
        // 系统环境下只能提示「未安装」，安装入口交给用户自己处理。
        None if !enabled => text("settings.dep.not_installed")
            .size(13)
            .font(crate::core::typography::regular())
            .style(crate::theme::muted_text_style)
            .into(),
        None => environment_action_button(
            "versions.online.install",
            Icon::Download,
            dependency,
            source,
            true,
        ),
    }
}

/// 环境依赖行的操作按钮。
///
/// 图标由调用方显式传入，不再拿「显示文案」当判断条件——文案已经键化，
/// 用翻译结果做比较在切换语言后会失效。
fn environment_action_button(
    label: &'static str,
    icon: Icon,
    dependency: EnvironmentDependency,
    source: crate::core::settings::EnvSource,
    enabled: bool,
) -> Element<'static, Message> {
    let content = row![
        crate::theme::muted_icon(icon, 14),
        text(label).size(11).font(crate::core::typography::medium()),
    ]
    .spacing(6)
    .align_y(Alignment::Center);
    let mut control = button(content)
        .height(34)
        .padding([7, 11])
        .style(button_style(ButtonVariant::Secondary));
    if enabled {
        control = control.on_press(Message::EnvironmentInstall { dependency, source });
    }
    control.into()
}

/// 自动测速缓存状态文案。
///
/// 缓存是否可用与当前选中的渠道无关：只有“自动”依赖它解析实际渠道，
/// 但手动选渠道时用户同样需要确认缓存还在有效期内，否则会误以为缓存没有保存。
fn download_channel_cache_status(state: &SettingsState) -> &'static str {
    if state.download_channel_test.running {
        "settings.channel_cache.testing"
    } else if state.download_channel_cache_valid() {
        "settings.channel_cache.valid"
    } else if state.download_resolved_channel.is_some()
        || state.download_channel_last_tested.is_some()
    {
        "settings.channel_cache.expired"
    } else {
        "settings.channel_cache.untested"
    }
}

fn download_settings(state: &SettingsState) -> Element<'_, Message> {
    let selected_label = t(
        state
            .download_channel
            .resolved_label_key(state.download_resolved_channel),
    );
    let descriptions = column(
        DownloadChannel::fixed_channels()
            .into_iter()
            .map(|channel| {
                row![
                    raw(format!("{}：", t(channel.label_key())))
                        .size(10)
                        .font(crate::core::typography::medium())
                        .style(crate::theme::muted_text_style),
                    text(channel.hint_key())
                        .size(10)
                        .font(crate::core::typography::regular())
                        .style(crate::theme::muted_text_style),
                ]
                .spacing(3)
                .into()
            })
            .collect::<Vec<_>>(),
    )
    .spacing(3);

    let channel_values = [
        DownloadChannel::Auto,
        DownloadChannel::Mirror1,
        DownloadChannel::Mirror2,
        DownloadChannel::Official,
    ];
    let channel_icons = [Icon::Gauge, Icon::Cloud, Icon::CloudCog, Icon::GitBranch];
    let channel_items = channel_values
        .into_iter()
        .zip(channel_icons)
        .map(|(channel, icon)| {
            ToggleButtonGroupItem::new(
                Some(channel.resolved_label_key(state.download_resolved_channel)),
                Some(icon),
                channel == state.download_channel,
            )
        })
        .collect();
    let channel_control = themed_segmented_group(channel_items, move |index| {
        Message::SettingsDownloadChannelSelected(channel_values[index])
    });

    section(
        Icon::CloudDownload,
        "settings.section.download",
        "settings.section.download.hint",
        section_rows(vec![
            row![
                setting_icon(Icon::Download),
                column![
                    text("settings.field.download_channel")
                        .size(13)
                        .font(crate::core::typography::medium())
                        .style(crate::theme::text_style),
                    text(selected_label)
                        .size(11)
                        .font(crate::core::typography::medium())
                        .style(crate::theme::muted_text_style),
                    descriptions,
                ]
                .spacing(4)
                .width(Fill),
                channel_control,
            ]
            .spacing(12)
            .padding([13, 16])
            .align_y(Alignment::Center)
            .width(Fill)
            .into(),
            setting_row(
                Icon::RefreshCw,
                "settings.field.channel_cache",
                "settings.field.channel_cache.hint",
                row![
                    text(download_channel_cache_status(state))
                        .size(11)
                        .font(crate::core::typography::medium())
                        .style(crate::theme::muted_text_style),
                    action_button(
                        "settings.action.retest",
                        Icon::RefreshCw,
                        SettingsAction::RefreshDownloadChannel,
                    ),
                ]
                .spacing(10)
                .align_y(Alignment::Center)
                .into(),
            ),
        ]),
    )
}

fn network_settings(state: &SettingsState) -> Element<'_, Message> {
    let mut rows = vec![proxy_setting(state)];
    if state.proxy_mode == ProxyMode::Custom {
        rows.push(setting_row(
            Icon::Link,
            "settings.field.custom_proxy",
            "settings.field.custom_proxy.hint",
            input_control(
                "http://127.0.0.1:7890",
                &state.custom_proxy,
                Message::SettingsCustomProxyChanged,
            ),
        ));
    }
    rows.push(github_test_setting(state));

    section(
        Icon::Cable,
        "settings.section.network",
        "settings.section.network.hint",
        section_rows(rows),
    )
}

fn proxy_setting(state: &SettingsState) -> Element<'_, Message> {
    let mut description = column![
        text("settings.proxy.choice_hint")
            .size(11)
            .font(crate::core::typography::regular())
            .style(crate::theme::muted_text_style),
    ]
    .spacing(4)
    .width(Fill);

    if state.proxy_mode == ProxyMode::System {
        let status = match state.system_proxy_status {
            SystemProxyStatus::Enabled => "settings.system_proxy.enabled",
            SystemProxyStatus::Disabled => "settings.system_proxy.disabled",
            SystemProxyStatus::Unknown => "settings.system_proxy.unknown",
        };
        description = description.push(
            row![
                crate::theme::subtle_icon(Icon::Info, 13),
                text(status)
                    .size(10)
                    .font(crate::core::typography::regular())
                    .style(crate::theme::muted_text_style),
            ]
            .spacing(5)
            .align_y(Alignment::Center),
        );
    }

    row![
        setting_icon(Icon::Shield),
        description,
        segmented_control(
            &[
                (ProxyMode::None, "settings.proxy.direct", Icon::Cable),
                (ProxyMode::System, "settings.proxy_mode.system", Icon::Monitor),
                (ProxyMode::Custom, "settings.proxy.custom", Icon::Link),
            ],
            state.proxy_mode,
            Message::SettingsProxyModeSelected,
        ),
    ]
    .spacing(12)
    .padding([14, 16])
    .align_y(Alignment::Center)
    .width(Fill)
    .into()
}

fn github_test_setting(state: &SettingsState) -> Element<'_, Message> {
    let control: Element<'_, Message> = if state.github_test.running {
        let phase = state
            .github_test
            .started_at
            .map(|started| (started.elapsed().as_secs_f32() * 0.9).fract())
            .unwrap_or(0.0);
        button(
            row![
                ProgressCircle::new(0.0)
                    .is_indeterminate(true)
                    .animation_phase(phase)
                    .color(ProgressCircleColor::Accent),
                text("settings.github_test.testing").size(11).font(crate::core::typography::medium()),
            ]
            .spacing(6)
            .align_y(Alignment::Center),
        )
        .height(34)
        .padding([7, 11])
        .style(button_style(ButtonVariant::Secondary))
        .into()
    } else {
        action_button("settings.github_test.start", Icon::Activity, SettingsAction::TestGithub)
    };

    setting_row(
        Icon::Plug,
        "settings.github_test.title",
        "settings.github_test.hint",
        control,
    )
}

fn github_test_modal(state: &GithubTestState) -> Element<'_, Message> {
    let phase = state
        .started_at
        .map(|started| (started.elapsed().as_secs_f32() * 0.9).fract())
        .unwrap_or(0.0);
    let mode = if state.mode_label.is_empty() {
        t("settings.proxy.direct")
    } else {
        state.mode_label.as_str()
    };
    let mut details = column![
        row![
            text("settings.github_test.mode_label").size(11).font(crate::core::typography::medium()),
            space::horizontal(),
            raw(mode).size(11).font(crate::core::typography::regular()),
        ]
        .align_y(Alignment::Center),
    ]
    .spacing(7)
    .width(Fill);
    if let Some(proxy) = &state.proxy_address {
        details = details.push(
            row![
                text("tavern.config.request_proxy_url").size(11).font(crate::core::typography::medium()),
                space::horizontal(),
                raw(proxy).size(10).font(crate::core::typography::regular()),
            ]
            .align_y(Alignment::Center),
        );
    }
    if let Some(accelerate) = &state.accelerate_url {
        details = details.push(
            row![
                text("settings.github_test.accelerator_label").size(11).font(crate::core::typography::medium()),
                space::horizontal(),
                raw(accelerate).size(10).font(crate::core::typography::regular()),
            ]
            .align_y(Alignment::Center),
        );
    }

    let body: Element<'_, Message> = if state.running {
        let rows = state
            .live_items
            .iter()
            .map(|item| live_github_result_row(item, state, phase))
            .collect::<Vec<_>>();
        column(rows).spacing(8).into()
    } else if let Some(results) = &state.results {
        column(
            results
                .iter()
                .map(|item| github_result_row(item, Some(state)))
                .collect::<Vec<_>>(),
        )
        .spacing(8)
        .into()
    } else {
        crate::theme::alert(
            "settings.github_test.failed_title",
            state
                .error
                .as_deref()
                .unwrap_or(t("settings.github_test.incomplete")),
            AlertKind::Danger,
        )
    };

    let status: Element<'_, Message> = if state.running {
        row![
            ProgressCircle::new(0.0)
                .is_indeterminate(true)
                .animation_phase(phase)
                .color(ProgressCircleColor::Accent),
            text("settings.github_test.running").size(12).font(crate::core::typography::medium()),
        ]
        .spacing(8)
        .align_y(Alignment::Center)
        .into()
    } else if state.timed_out {
        row![
            icons::icon(Icon::ClockAlert, 16, iced::Color::from_rgb8(245, 165, 36)),
            text("settings.github_test.timeout")
                .size(12)
                .font(crate::core::typography::medium()),
        ]
        .spacing(8)
        .align_y(Alignment::Center)
        .into()
    } else if state.error.is_some() {
        row![
            icons::icon(Icon::CircleX, 16, iced::Color::from_rgb8(255, 56, 60)),
            text("settings.github_test.failed_hint")
                .size(12)
                .font(crate::core::typography::medium()),
        ]
        .spacing(8)
        .align_y(Alignment::Center)
        .into()
    } else {
        row![
            icons::icon(Icon::CircleCheck, 16, iced::Color::from_rgb8(23, 201, 100)),
            text("settings.github_test.completed").size(12).font(crate::core::typography::medium()),
        ]
        .spacing(8)
        .align_y(Alignment::Center)
        .into()
    };

    let footer = row![
        status,
        space::horizontal(),
        button(text("resources.import.close").size(12).font(crate::core::typography::medium()))
            .on_press(Message::GithubTestClose)
            .height(34)
            .padding([7, 14])
            .style(button_style(ButtonVariant::Secondary)),
    ]
    .spacing(12)
    .align_y(Alignment::Center)
    .width(Fill);

    let panel = mouse_area(
        container(
            column![
                text("settings.github_test.title").size(18).font(crate::core::typography::medium()),
                rule::horizontal(1.0).style(crate::theme::separator_style),
                details,
                rule::horizontal(1.0).style(crate::theme::separator_style),
                scrollable(
                    container(body)
                        .width(Fill)
                        .padding(12)
                        .style(environment_log_style)
                )
                .height(if crate::core::typography::current_ui_scale() >= 1.35 {
                    210
                } else {
                    300
                }),
                rule::horizontal(1.0).style(crate::theme::separator_style),
                footer,
            ]
            .spacing(16),
        )
        .width(Fill).max_width(620)
        .padding(20)
        .style(environment_modal_style),
    )
    .on_press(Message::GithubTestInteract);

    stack![
        button(space::Space::new())
            .on_press(Message::GithubTestInteract)
            .width(Fill)
            .height(Fill)
            .padding(0)
            .style(environment_backdrop_style),
        container(panel)
            .width(Fill)
            .height(Fill)
            .align_x(Alignment::Center)
            .align_y(Alignment::Center)
            .padding(24),
    ]
    .width(Fill)
    .height(Fill)
    .into()
}

fn live_github_result_row(
    item: &GithubLiveItem,
    state: &GithubTestState,
    phase: f32,
) -> Element<'static, Message> {
    if let Some(result) = &item.result {
        return github_result_row(result, Some(state));
    }

    let detail_text = |value: String| {
        raw(value)
            .size(10)
            .font(crate::core::typography::regular())
            .style(crate::theme::muted_text_style)
    };

    let (progress, details): (Element<'static, Message>, Element<'static, Message>) =
        if item.key == "clone" {
            let stage = state
                .clone_stage
                .as_deref()
                .map(crate::lang::github_clone_stage_label)
                .unwrap_or_else(|| crate::lang::github_clone_preparing_label().to_owned());
            let percentage = state
                .clone_percentage
                .map(|value| format!("{value:.0}%"))
                .unwrap_or_else(|| crate::lang::github_clone_in_progress_label().to_owned());
            let counts = match (state.clone_current, state.clone_total) {
                (Some(current), Some(total)) => {
                    Some(crate::lang::github_clone_objects_label(current, total))
                }
                _ => None,
            };
            let detail_lines = if let Some(counts) = counts {
                column![
                    detail_text(format!("{stage} {percentage}")),
                    detail_text(counts)
                ]
            } else {
                column![detail_text(format!("{stage} {percentage}"))]
            };
            let indicator: Element<'static, Message> = match state.clone_percentage {
                Some(value) => ProgressCircle::new(value)
                    .color(ProgressCircleColor::Accent)
                    .into(),
                None => ProgressCircle::new(0.0)
                    .is_indeterminate(true)
                    .animation_phase(phase)
                    .color(ProgressCircleColor::Accent)
                    .into(),
            };
            (
                indicator,
                detail_lines.spacing(2).align_x(Alignment::End).into(),
            )
        } else if item.key == "speed" {
            let downloaded = format_bytes(state.download_downloaded_bytes);
            let total = state
                .download_total_bytes
                .map(format_bytes)
                .unwrap_or_else(|| t("settings.github_test.size_unknown").to_owned());
            let speed = format_speed(state.download_bytes_per_second);
            let percentage = state
                .download_percentage
                .map(|value| format!("{value:.1}%"))
                .unwrap_or_else(|| "—".to_owned());
            let indicator: Element<'static, Message> = match state.download_percentage {
                Some(value) => ProgressCircle::new(value)
                    .color(ProgressCircleColor::Accent)
                    .into(),
                None => ProgressCircle::new(0.0)
                    .is_indeterminate(true)
                    .animation_phase(phase)
                    .color(ProgressCircleColor::Accent)
                    .into(),
            };
            (
                indicator,
                column![
                    detail_text(format!("{downloaded} / {total}")),
                    detail_text(speed),
                    detail_text(percentage),
                ]
                .spacing(2)
                .align_x(Alignment::End)
                .into(),
            )
        } else {
            (
                ProgressCircle::new(0.0)
                    .is_indeterminate(true)
                    .animation_phase(phase)
                    .color(ProgressCircleColor::Accent)
                    .into(),
                detail_text(t("settings.github_test.testing").to_owned()).into(),
            )
        };

    row![
        crate::theme::subtle_icon(Icon::Circle, 13),
        raw(item.name.clone())
            .size(12)
            .font(crate::core::typography::medium())
            .width(Fill),
        row![progress, details]
            .spacing(8)
            .align_y(Alignment::Center)
            .width(Length::Shrink),
    ]
    .spacing(10)
    .padding([9, 10])
    .align_y(Alignment::Center)
    .into()
}

/// 容量展示统一走工具函数，避免设置页与版本页各写一份换算。
fn format_bytes(bytes: u64) -> String {
    crate::utils::format_bytes(bytes)
}

fn format_speed(bytes_per_second: u64) -> String {
    if bytes_per_second == 0 {
        t("settings.github_test.calculating_speed").to_owned()
    } else {
        format!("{}/s", format_bytes(bytes_per_second))
    }
}

fn github_result_row(
    item: &crate::core::network::GithubMultiTestItem,
    state: Option<&GithubTestState>,
) -> Element<'static, Message> {
    let (icon, color) = if item.success {
        (Icon::CircleCheck, iced::Color::from_rgb8(23, 201, 100))
    } else {
        (Icon::CircleX, iced::Color::from_rgb8(255, 56, 60))
    };
    let detail = if item.key == "speed" && item.success {
        if let Some(state) = state {
            let total = state
                .download_total_bytes
                .map(format_bytes)
                .unwrap_or_else(|| t("settings.github_test.size_unknown").to_owned());
            let summary = tf("settings.github_test.progress", &[("done", &format_bytes(state.download_downloaded_bytes)), ("total", &total), ("speed", &format_speed(state.download_bytes_per_second))]);
            item.warning
                .as_ref()
                .map(|warning| format!("{summary} · {warning}"))
                .unwrap_or(summary)
        } else {
            item.warning
                .clone()
                .unwrap_or_else(|| t("settings.github_test.download_done").to_owned())
        }
    } else {
        item.warning
            .clone()
            .or_else(|| item.error.clone())
            .unwrap_or_else(|| match (item.key.as_str(), state) {
                ("clone", Some(_)) if item.success => t("settings.github_test.clone_done").to_owned(),
                _ => t("settings.github_test.connected").to_owned(),
            })
    };
    let latency = item
        .latency_ms
        .map(|value| tf("settings.github_test.elapsed", &[("value", &value)]))
        .unwrap_or_default();

    row![
        icons::icon(icon, 16, color),
        column![
            raw(item.name.clone()).size(12).font(crate::core::typography::medium()),
            // detail 由 warning / error / 拼接文案构成，可能是键也可能是自由文本，统一解析一次。
            raw(crate::lang::resolve(&detail))
                .size(10)
                .font(crate::core::typography::regular())
                .style(crate::theme::muted_text_style),
        ]
        .spacing(3)
        .width(Fill),
        raw(latency)
            .size(11)
            .font(crate::core::typography::medium())
            .style(crate::theme::muted_text_style),
    ]
    .spacing(10)
    .padding([9, 10])
    .align_y(Alignment::Center)
    .into()
}

/// 「软件与更新」区块：展示当前版本并提供更新检查入口。
fn software_settings(state: &SettingsState) -> Element<'_, Message> {
    section(
        Icon::Info,
        "settings.section.updates",
        "settings.section.updates.hint",
        section_rows(vec![
            setting_row(
                Icon::AppWindow,
                "AstraBrew Launcher",
                "settings.current_version",
                // 版本号来自编译期常量，属于运行时数据，用不翻译的胶囊展示。
                crate::theme::flat_chip_raw(format!("v{}", env!("CARGO_PKG_VERSION")), BLUE_600),
            ),
            setting_row(
                Icon::Download,
                "settings.check_update",
                "settings.check_update.hint",
                update_button(state),
            ),
        ]),
    )
}

/// 「检查更新」按钮：检查或下载期间改写文案并禁用，避免重复触发。
fn update_button(state: &SettingsState) -> Element<'_, Message> {
    let control = button(
        row![
            crate::theme::muted_icon(Icon::RefreshCw, 14),
            text(state.update.button_label())
                .size(11)
                .font(crate::core::typography::medium())
        ]
        .spacing(6)
        .align_y(Alignment::Center),
    )
    .height(34)
    .padding([7, 11])
    .style(button_style(ButtonVariant::Secondary));

    if state.update.busy() {
        control.into()
    } else {
        control
            .on_press(Message::SettingsAction(SettingsAction::CheckUpdate))
            .into()
    }
}

/// 发现新版本后的确认弹窗。
///
/// 由应用根层通过 `overlay_layer` 常驻占位承载，弹窗开关不改变顶层控件结构，
/// 因此不会重置设置页的滚动位置。
pub(crate) fn update_confirm_modal(pending: &PendingUpdate) -> Element<'static, Message> {
    let language = current_language();

    let mut details = column![
        raw(tf(
            "settings.update.available_desc",
            &[("version", &pending.version)],
        ))
        .size(12)
        .font(crate::core::typography::regular())
        .style(crate::theme::muted_text_style),
    ]
    .spacing(12)
    .width(Fill);

    // 发行说明由发布方自由书写且可能很长，固定高度内滚动展示。
    if let Some(notes) = pending
        .notes
        .as_deref()
        .map(str::trim)
        .filter(|notes| !notes.is_empty())
    {
        details = details.push(
            container(
                scrollable(
                    raw(notes.to_owned())
                        .size(11)
                        .font(crate::core::typography::regular()),
                )
                .height(Length::Fixed(120.0)),
            )
            .width(Fill)
            .padding([10, 12])
            .style(environment_log_style),
        );
    }

    let panel = mouse_area(
        container(
            column![
                row![
                    container(icons::icon(Icon::Download, 22, BLUE_600))
                        .width(42)
                        .height(42)
                        .align_x(Alignment::Center)
                        .align_y(Alignment::Center)
                        .style(environment_task_icon_style),
                    raw(t_in("settings.update.available", language))
                        .size(18)
                        .font(crate::core::typography::medium()),
                ]
                .spacing(14)
                .align_y(Alignment::Center),
                details,
                row![
                    space::horizontal(),
                    button(
                        raw(t_in("settings.update.later", language))
                            .size(12)
                            .font(crate::core::typography::medium())
                    )
                    .on_press(Message::UpdateDismissed)
                    .height(36)
                    .padding([8, 14])
                    .style(button_style(ButtonVariant::Secondary)),
                    button(
                        row![
                            icons::icon(Icon::Download, 15, Color::WHITE),
                            raw(t_in("settings.update.install_now", language))
                                .size(12)
                                .font(crate::core::typography::medium())
                                .color(Color::WHITE),
                        ]
                        .spacing(7)
                        .align_y(Alignment::Center)
                    )
                    .on_press(Message::UpdateInstallConfirmed)
                    .height(36)
                    .padding([8, 15])
                    .style(button_style(ButtonVariant::Primary)),
                ]
                .spacing(10)
                .align_y(Alignment::Center)
                .width(Fill),
            ]
            .spacing(18),
        )
        .width(Fill)
        .max_width(500)
        .padding(22)
        .style(environment_modal_style),
    )
    .on_press(Message::UpdateDialogInteract);

    stack![
        button(space::Space::new())
            .on_press(Message::UpdateDismissed)
            .width(Fill)
            .height(Fill)
            .padding(0)
            .style(environment_backdrop_style),
        container(panel)
            .width(Fill)
            .height(Fill)
            .align_x(Alignment::Center)
            .align_y(Alignment::Center)
            .padding(24),
    ]
    .width(Fill)
    .height(Fill)
    .into()
}

fn section<'a>(
    icon: Icon,
    title: &'static str,
    description: &'static str,
    content: Element<'a, Message>,
) -> Element<'a, Message> {
    column![
        row![
            container(icons::icon(icon, 17, BLUE_600))
                .width(32)
                .height(32)
                .align_x(Alignment::Center)
                .align_y(Alignment::Center)
                .style(setting_icon_style),
            column![
                text(title).size(17).font(crate::core::typography::medium()),
                text(description)
                    .size(11)
                    .font(crate::core::typography::regular())
                    .style(crate::theme::muted_text_style)
            ]
            .spacing(3)
        ]
        .spacing(10)
        .align_y(Alignment::Center),
        crate::theme::card(content, Fill, 0)
    ]
    .spacing(11)
    .width(Fill)
    .into()
}

fn section_rows<'a>(rows: Vec<Element<'a, Message>>) -> Element<'a, Message> {
    let mut content = column![];
    let count = rows.len();
    for (index, item) in rows.into_iter().enumerate() {
        content = content.push(item);
        if index + 1 < count {
            content = content.push(rule::horizontal(1.0).style(crate::theme::separator_style));
        }
    }
    content.into()
}

fn setting_row<'a>(
    icon: Icon,
    title: &'static str,
    description: &'static str,
    control: Element<'a, Message>,
) -> Element<'a, Message> {
    let label = column![
        text(title)
            .size(13)
            .font(crate::core::typography::medium())
            .style(crate::theme::text_style),
        text(description)
            .size(11)
            .font(crate::core::typography::regular())
            .style(crate::theme::muted_text_style)
    ]
    .spacing(3)
    .width(Fill);

    if crate::core::typography::current_ui_scale() >= 1.35 {
        column![
            row![setting_icon(icon), label]
                .spacing(12)
                .align_y(Alignment::Center)
                .width(Fill),
            container(control).width(Fill).padding(iced::Padding {
                top: 0.0,
                right: 0.0,
                bottom: 0.0,
                left: 46.0,
            }),
        ]
        .spacing(10)
        .padding([14, 16])
        .width(Fill)
        .into()
    } else {
        row![setting_icon(icon), label, control]
            .spacing(12)
            .padding([14, 16])
            .align_y(Alignment::Center)
            .width(Fill)
            .into()
    }
}

fn environment_dependency_row<'a>(
    icon: Icon,
    title: String,
    description: &'static str,
    control: Element<'a, Message>,
) -> Element<'a, Message> {
    row![
        setting_icon(icon),
        column![
            raw(title)
                .size(13)
                .font(crate::core::typography::medium())
                .style(crate::theme::text_style),
            text(description)
                .size(11)
                .font(crate::core::typography::regular())
                .style(crate::theme::muted_text_style)
        ]
        .spacing(4)
        .width(Fill),
        control,
    ]
    .spacing(12)
    .padding([13, 16])
    .align_y(Alignment::Center)
    .width(Fill)
    .into()
}

fn setting_icon(icon: Icon) -> Element<'static, Message> {
    container(icons::icon(icon, 17, BLUE_600))
        .width(34)
        .height(34)
        .align_x(Alignment::Center)
        .align_y(Alignment::Center)
        .style(setting_icon_style)
        .into()
}

fn start_mode_control(selected: StartMode, enabled: bool) -> Element<'static, Message> {
    let items = [
        (StartMode::Normal, "settings.start_mode.normal", Icon::Play),
        (StartMode::Desktop, "settings.start_mode.desktop", Icon::AppWindow),
    ]
    .into_iter()
    .map(|(mode, label, icon)| {
        ToggleButtonGroupItem::new(Some(label), Some(icon), mode == selected)
    })
    .collect();

    themed_segmented_group_enabled(items, enabled, |index| {
        Message::SettingsLaunchModeSelected(match index {
            1 => QuickStartMode::Desktop,
            _ => QuickStartMode::Normal,
        })
    })
}


fn segmented_control<T>(
    options: &[(T, &'static str, Icon)],
    selected: T,
    on_selected: fn(T) -> Message,
) -> Element<'static, Message>
where
    T: Copy + Eq + 'static,
{
    segmented_control_enabled(options, selected, true, on_selected)
}

fn segmented_control_enabled<T>(
    options: &[(T, &'static str, Icon)],
    selected: T,
    enabled: bool,
    on_selected: fn(T) -> Message,
) -> Element<'static, Message>
where
    T: Copy + Eq + 'static,
{
    let values = options
        .iter()
        .map(|(value, _, _)| *value)
        .collect::<Vec<_>>();
    let items = options
        .iter()
        .map(|(value, label, icon)| {
            ToggleButtonGroupItem::new(Some(*label), Some(*icon), *value == selected)
        })
        .collect();

    themed_segmented_group_enabled(items, enabled, move |index| on_selected(values[index]))
}

fn ui_scale_control(value: f32) -> Element<'static, Message> {
    let percent = (normalize_ui_scale(value) * 100.0).round();
    row![
        slider(
            MIN_UI_SCALE * 100.0..=MAX_UI_SCALE * 100.0,
            percent,
            |value| Message::SettingsUiScaleChanged(value / 100.0),
        )
        .step(5.0)
        .width(210)
        .style(slider_style),
        container(
            raw(format!("{percent:.0}%"))
                .size(12)
                .font(crate::core::typography::medium())
        )
        .width(48)
        .align_x(Alignment::End),
    ]
    .spacing(10)
    .align_y(Alignment::Center)
    .into()
}

fn font_control(state: &SettingsState) -> Element<'_, Message> {
    let picker = combo_box(
        &state.font_choices,
        t_in("settings.interface.font.placeholder", current_language()),
        Some(&state.selected_font),
        Message::SettingsFontSelected,
    )
    .width(280)
    .padding([8, 11])
    .size(12)
    .font(crate::core::typography::regular())
    .input_style(text_input_style)
    .menu_style(pick_list_menu_style);

    if state.font_loading {
        return column![
            picker,
            raw(t_in("settings.interface.font.loading", current_language()))
            .size(10)
            .font(crate::core::typography::regular())
            .style(crate::theme::muted_text_style),
        ]
        .spacing(4)
        .into();
    }

    // 选中不含中文字形的西文字体时提示用户：中文会退回系统字体渲染，
    // 与拉丁数字形成粗细差。这里不阻止选择，只把后果说明白。
    if !state.selected_font.covers_cjk() {
        return column![
            picker,
            raw(t_in("settings.interface.font.no_cjk_warning", current_language()))
                .size(10)
                .font(crate::core::typography::regular())
                .style(crate::theme::warning_text_style),
        ]
        .spacing(4)
        .into();
    }

    picker.into()
}

fn input_control<'a>(
    placeholder: &'static str,
    value: &'a str,
    on_input: fn(String) -> Message,
) -> Element<'a, Message> {
    text_input(t(placeholder), value)
        .on_input(on_input)
        .width(INPUT_WIDTH)
        .padding([8, 11])
        .size(12)
        .font(crate::core::typography::regular())
        .style(text_input_style)
        .into()
}

fn toggle_control(is_toggled: bool, on_toggle: fn(bool) -> Message) -> Element<'static, Message> {
    crate::theme::switch("", is_toggled, on_toggle)
}

fn readonly_text(label: &'static str) -> Element<'static, Message> {
    text(label)
        .size(11)
        .font(crate::core::typography::medium())
        .style(crate::theme::muted_text_style)
        .into()
}

fn path_control(path: &str, action: SettingsAction) -> Element<'_, Message> {
    row![
        container(
            raw(path)
                .size(10)
                .font(crate::core::typography::regular())
                .style(crate::theme::muted_text_style)
        )
        .width(190),
        action_button("settings.action.change", Icon::FolderOpen, action)
    ]
    .spacing(8)
    .align_y(Alignment::Center)
    .into()
}

fn action_button(
    label: &'static str,
    icon: Icon,
    action: SettingsAction,
) -> Element<'static, Message> {
    button(
        row![
            crate::theme::muted_icon(icon, 14),
            text(label).size(11).font(crate::core::typography::medium())
        ]
        .spacing(6)
        .align_y(Alignment::Center),
    )
    .on_press(Message::SettingsAction(action))
    .height(34)
    .padding([7, 11])
    .style(button_style(ButtonVariant::Secondary))
    .into()
}

fn setting_icon_style(theme: &Theme) -> container::Style {
    container::Style {
        background: Some(Background::Color(Color::from_rgba(
            theme.palette().primary.r,
            theme.palette().primary.g,
            theme.palette().primary.b,
            if crate::theme::is_dark(theme) {
                0.18
            } else {
                0.09
            },
        ))),
        border: Border {
            radius: 9.0.into(),
            ..Border::default()
        },
        ..container::Style::default()
    }
}

fn environment_modal_style(theme: &Theme) -> container::Style {
    container::Style {
        background: Some(Background::Color(crate::theme::surface(theme))),
        border: Border {
            color: crate::theme::line(theme),
            width: 1.0,
            radius: 12.0.into(),
        },
        ..container::Style::default()
    }
}

fn environment_task_icon_style(theme: &Theme) -> container::Style {
    container::Style {
        background: Some(Background::Color(Color::from_rgba(
            theme.palette().primary.r,
            theme.palette().primary.g,
            theme.palette().primary.b,
            if crate::theme::is_dark(theme) {
                0.24
            } else {
                0.12
            },
        ))),
        border: Border {
            radius: 14.0.into(),
            ..Border::default()
        },
        ..container::Style::default()
    }
}

fn environment_log_style(theme: &Theme) -> container::Style {
    container::Style {
        background: Some(Background::Color(if crate::theme::is_dark(theme) {
            Color::from_rgb8(20, 20, 23)
        } else {
            Color::from_rgb8(247, 247, 248)
        })),
        border: Border {
            color: crate::theme::line(theme),
            width: 1.0,
            radius: 8.0.into(),
        },
        ..container::Style::default()
    }
}

fn environment_backdrop_style(_theme: &Theme, _status: button::Status) -> button::Style {
    button::Style {
        background: Some(Background::Color(Color::from_rgba8(0, 0, 0, 0.48))),
        ..button::Style::default()
    }
}

#[cfg(test)]
mod tests {
    use super::{
        DEFAULT_UI_SCALE, DownloadChannelTestState, NpmRegistry, SettingsState, StartMode,
        TavernDataMode,
    };
    use crate::core::network::DownloadChannel;
    #[test]
    fn npm_registry_urls_match_the_old_launcher() {
        assert_eq!(NpmRegistry::Official.url(), "https://registry.npmjs.org/");
        assert_eq!(
            NpmRegistry::Npmmirror.url(),
            "https://registry.npmmirror.com/"
        );
        assert_eq!(
            NpmRegistry::Tencent.url(),
            "https://mirrors.cloud.tencent.com/npm/"
        );
        assert_eq!(
            NpmRegistry::HuaweiCloud.url(),
            "https://repo.huaweicloud.com/repository/npm/"
        );
    }

    #[test]
    fn automatic_channel_label_includes_resolved_channel() {
        // 「自动」解析出具体渠道后，标签键要带上实际渠道；文案本身由键值表提供。
        use crate::lang::{Language, t_in};

        assert_eq!(
            DownloadChannel::Auto.resolved_label_key(Some(DownloadChannel::Mirror1)),
            "channel.auto.resolved.mirror1"
        );
        assert_eq!(
            t_in("channel.auto.resolved.mirror1", Language::Chinese),
            "自动（镜像 1）"
        );
        assert_eq!(
            DownloadChannel::Auto.resolved_label_key(Some(DownloadChannel::Official)),
            "channel.auto.resolved.official"
        );
        assert_eq!(
            t_in("channel.auto.resolved.official", Language::English),
            "Automatic (Official)"
        );
        // 未解析出渠道时回落到「自动」本身。
        assert_eq!(
            DownloadChannel::Auto.resolved_label_key(None),
            DownloadChannel::Auto.label_key()
        );
    }

    #[test]
    fn automatic_channel_cache_requires_recent_result() {
        let mut settings = SettingsState::default();
        settings.download_resolved_channel = Some(DownloadChannel::Mirror2);
        settings.download_channel_last_tested = Some(
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .expect("system time")
                .as_secs(),
        );
        assert!(settings.download_channel_cache_valid());
        settings.download_channel_last_tested =
            Some(settings.download_channel_last_tested.unwrap() - 7 * 24 * 60 * 60);
        assert!(!settings.download_channel_cache_valid());
        let _ = DownloadChannelTestState::default();
    }

    #[test]
    fn defaults_match_old_launcher_preferences() {
        let settings = SettingsState::default();
        assert_eq!(settings.start_mode, StartMode::Normal);
        assert_eq!(settings.data_mode, TavernDataMode::Current);
        assert!(settings.remember_window_position);
        assert!(settings.auto_stop_tavern_on_window_close);
        assert!((settings.ui_scale - DEFAULT_UI_SCALE).abs() < f32::EPSILON);
        assert_eq!(
            settings.font_family,
            crate::core::typography::DEFAULT_FONT_KEY
        );
    }
}
