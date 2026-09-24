//! 启动器偏好设置的持久化。
//!
//! 本模块只维护当前版本已经接入的界面偏好，同时保留旧版配置文件中的
//! 其他字段，避免新旧版本交替使用时丢失尚未迁移的设置。

pub(crate) mod env_detect;
pub(crate) mod install;
pub(crate) mod webview2;

use std::fmt;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

/// 全局酒馆数据目录遵循启动器统一的 AppData 目录规范。
pub(crate) const DEFAULT_GLOBAL_DATA_PATH: &str =
    "%APPDATA%/AstraBrew Launcher/data/sillytavern/data";

/// 历史版本使用过的错误默认目录；只迁移这些精确值，不覆盖用户自定义路径。
///
/// 前两条是 macOS 版本的旧值，保留在此是为了让从 macOS 版本迁移过来的用户
/// 配置能被自动纠正到规范路径（属于历史数据兼容，不属于平台代码）。
const LEGACY_GLOBAL_DATA_PATHS: &[&str] = &[
    "~/Library/Application Support/AstraBrew/data",
    "~/Library/Application Support/AstraBrew Launcher/data/sillytavern",
    "%APPDATA%/AstraBrew Launcher/data/sillytavern",
];

/// 启动器显示语言。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
pub enum DisplayLanguage {
    #[serde(rename = "Chinese", alias = "SimplifiedChinese")]
    SimplifiedChinese,
    English,
    #[default]
    System,
}

impl DisplayLanguage {
    /// 设置页下拉项使用的文案键。
    ///
    /// 与持久化取值（serde 的 `Chinese` / `English` / `System`）无关，避免把
    /// 展示文本写进存储层。
    pub const fn label_key(self) -> &'static str {
        match self {
            Self::SimplifiedChinese => "settings.language.simplified_chinese",
            Self::English => "settings.language.english",
            Self::System => "settings.language.system",
        }
    }
}

impl fmt::Display for DisplayLanguage {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(crate::lang::t(self.label_key()))
    }
}

/// 启动器界面主题。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
pub enum ThemeMode {
    Light,
    Dark,
    #[default]
    System,
}

impl ThemeMode {
    /// 设置页主题选项使用的文案键。
    pub const fn label_key(self) -> &'static str {
        match self {
            Self::Light => "settings.theme_mode.light",
            Self::Dark => "settings.theme_mode.dark",
            Self::System => "settings.theme_mode.system",
        }
    }
}

impl fmt::Display for ThemeMode {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(crate::lang::t(self.label_key()))
    }
}

/// 环境来源：使用系统 PATH 中的工具，还是软件内置的 `lib/` 环境。
///
/// 定义在核心层而非界面层，因为进程启动、依赖检查、PM2 管理等核心模块
/// 都需要按来源解析可执行文件路径；界面层只负责展示与切换。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
pub enum EnvSource {
    /// 使用软件内置的 `lib/` 环境（默认，开箱即用）。
    #[default]
    Builtin,
    /// 使用系统 PATH 中已安装的工具。
    System,
}

impl EnvSource {
    /// 从配置文件中的字符串还原；不认识的取值回落到默认（内置）。
    ///
    /// 中文取值属于历史数据兼容别名（旧版本曾直接持久化展示文案），
    /// 属于数据不是文案，必须保留。
    pub fn from_key(value: &str) -> Self {
        match value.trim().to_ascii_lowercase().as_str() {
            "system" | "env_mode_system" | "系统" | "系统环境" => Self::System,
            _ => Self::Builtin,
        }
    }

    /// 持久化到配置文件时使用的字符串。
    pub const fn storage_key(self) -> &'static str {
        match self {
            Self::Builtin => "builtin",
            Self::System => "system",
        }
    }
}

/// 后台任务允许占用的 CPU 核心数。
///
/// 定义在核心层而非界面层：全盘扫描、依赖安装等核心模块都要按它折算线程预算，
/// 界面层只负责展示与切换。默认 `Auto` 由核心层自行留出一个核心给界面线程。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
pub enum CpuCores {
    /// 自动：留出一个核心给界面，其余全部用于后台任务。
    #[default]
    Auto,
    /// 只使用一半核心。
    Half,
    /// 使用全部核心。
    All,
}

impl CpuCores {
    /// 设置页选项使用的文案键。
    pub const fn label_key(self) -> &'static str {
        match self {
            Self::Auto => "settings.cpu_cores.auto",
            Self::Half => "settings.cpu_cores.half",
            Self::All => "settings.cpu_cores.all",
        }
    }

    /// 从配置文件的字符串还原；不认识的取值回落到默认（自动）。
    ///
    /// 中文取值属于历史数据兼容别名（旧版本曾直接持久化展示文案），
    /// 属于数据不是文案，必须保留。
    pub fn from_key(value: &str) -> Self {
        match value.trim().to_ascii_lowercase().as_str() {
            "all" | "全部核心" => Self::All,
            "half" | "一半核心" => Self::Half,
            _ => Self::Auto,
        }
    }

    /// 持久化到配置文件时使用的字符串。
    pub const fn storage_key(self) -> &'static str {
        match self {
            Self::Auto => "auto",
            Self::Half => "half",
            Self::All => "all",
        }
    }

    /// 折算成可用的工作线程数。
    ///
    /// `available` 为系统报告的逻辑核心数。结果至少为 1，保证单核机器也能扫描。
    pub fn thread_budget(self, available: usize) -> usize {
        let available = available.max(1);
        match self {
            // 留出一个核心给界面渲染与事件循环，避免全盘扫描把 UI 拖到卡顿。
            Self::Auto => available.saturating_sub(1).max(1),
            Self::Half => (available / 2).max(1),
            Self::All => available,
        }
    }
}

/// 已接入持久化的用户偏好。
#[derive(Debug, Clone, PartialEq)]
pub struct PersistentPreferences {
    pub language: DisplayLanguage,
    pub theme: ThemeMode,
    /// 普通界面与布局的用户缩放比例。
    pub ui_scale: f32,
    /// 显示字体族；default 表示内置 HarmonyOS Sans。
    pub font_family: String,
    pub remember_window_position: bool,
    pub window_position: Option<[f32; 2]>,
    /// 网络代理模式：none、system 或 custom。
    pub proxy_mode: String,
    /// 自定义代理地址，即使当前未选择自定义模式也保留。
    pub custom_proxy: String,
    /// 旧版 githubProxy 配置。
    pub github_proxy_enabled: bool,
    pub github_proxy_url: String,
    /// 旧版 npmRegistry 配置。
    pub npm_registry: String,
    /// 酒馆下载渠道：auto、mirror1、mirror2、official。
    pub download_channel: String,
    /// 是否启用软件自启动。
    pub auto_start: bool,
    /// 酒馆数据模式：global 或 current。
    pub data_mode: String,
    /// 全局数据存放位置。
    pub global_data_path: String,
    /// 导出保存目录。
    pub tavern_export_path: String,
    /// 酒馆启动模式：normal 或 desktop。
    pub start_mode: String,
    /// 是否启用服务器模式。
    pub server_mode_enabled: bool,
    /// 服务器对外服务范围：lan 或 internet。
    pub server_service_mode: String,
    /// 桌面 WebView 关闭时是否自动停止酒馆。
    pub auto_stop_tavern_on_window_close: bool,
    /// 是否允许服务器模式通过 PM2 在后台继续运行。
    pub allow_tavern_background: bool,
    /// 是否在控制台展示完整启动命令。
    pub show_startup_command: bool,
    /// 环境来源：内置 `lib/` 环境或系统 PATH 环境。
    pub env_mode: EnvSource,
    /// 后台任务允许占用的 CPU 核心数。
    pub cpu_cores: CpuCores,
    /// 用户是否已经确认过 staging 开发版风险提示。
    pub staging_risk_confirmed: bool,
}

impl Default for PersistentPreferences {
    fn default() -> Self {
        Self {
            language: DisplayLanguage::System,
            theme: ThemeMode::System,
            ui_scale: crate::core::typography::DEFAULT_UI_SCALE,
            font_family: crate::core::typography::DEFAULT_FONT_KEY.to_owned(),
            remember_window_position: true,
            window_position: None,
            proxy_mode: "system".to_owned(),
            custom_proxy: String::new(),
            github_proxy_enabled: false,
            github_proxy_url: "https://ghfast.top/".to_owned(),
            npm_registry: "https://registry.npmmirror.com/".to_owned(),
            download_channel: "auto".to_owned(),
            auto_start: false,
            data_mode: "current".to_owned(),
            global_data_path: DEFAULT_GLOBAL_DATA_PATH.to_owned(),
            tavern_export_path: "~/Downloads".to_owned(),
            start_mode: "normal".to_owned(),
            server_mode_enabled: false,
            server_service_mode: "lan".to_owned(),
            auto_stop_tavern_on_window_close: true,
            allow_tavern_background: false,
            show_startup_command: false,
            env_mode: EnvSource::Builtin,
            cpu_cores: CpuCores::Auto,
            staging_risk_confirmed: false,
        }
    }
}

/// 保留原始 JSON 的设置存储器。
#[derive(Debug, Clone)]
pub struct SettingsStore {
    path: PathBuf,
    document: Map<String, Value>,
}

impl SettingsStore {
    /// 从默认的 `%AppData%/AstraBrew Launcher/config.json` 加载配置。
    pub fn load_default() -> (Self, PersistentPreferences) {
        let path = default_settings_path();
        migrate_legacy_settings_file(&path);
        Self::load(path)
    }

    /// 从指定路径加载配置，便于测试时隔离真实用户数据。
    pub fn load(path: impl Into<PathBuf>) -> (Self, PersistentPreferences) {
        let path = path.into();
        let document = fs::read_to_string(&path)
            .ok()
            .and_then(|content| serde_json::from_str::<Value>(&content).ok())
            .and_then(|value| value.as_object().cloned())
            .unwrap_or_default();
        let preferences = preferences_from_document(&document);

        (Self { path, document }, preferences)
    }

    /// 将偏好合并回旧版配置并进行原子替换。
    pub fn save(&mut self, preferences: PersistentPreferences) -> io::Result<()> {
        // 保持旧版 settings.json 的字段结构，方便老用户直接升级。
        self.document.insert(
            "language".into(),
            serde_json::to_value(preferences.language).map_err(io::Error::other)?,
        );
        self.document.insert(
            "theme".into(),
            serde_json::to_value(preferences.theme).map_err(io::Error::other)?,
        );
        self.document.insert(
            "ui_scale".into(),
            Value::from(crate::core::typography::normalize_ui_scale(preferences.ui_scale) as f64),
        );
        self.document
            .insert("font_family".into(), Value::String(preferences.font_family));
        self.document.insert(
            "remember_window_pos".into(),
            Value::Bool(preferences.remember_window_position),
        );
        self.document.remove("remember_window_position");
        self.document.insert(
            "window_position".into(),
            serde_json::to_value(preferences.window_position).map_err(io::Error::other)?,
        );
        self.document.insert(
            "npm_registry".into(),
            Value::String(preferences.npm_registry),
        );
        self.document.insert(
            "download_channel".into(),
            Value::String(preferences.download_channel),
        );
        self.document
            .insert("auto_start".into(), Value::Bool(preferences.auto_start));
        self.document.insert(
            "data_mode".into(),
            Value::String(normalize_data_mode(&preferences.data_mode).to_owned()),
        );
        self.document.insert(
            "global_data_path".into(),
            Value::String(normalize_global_data_path(&preferences.global_data_path)),
        );
        self.document.insert(
            "tavern_export_path".into(),
            Value::String(preferences.tavern_export_path),
        );
        self.document.insert(
            "start_mode".into(),
            Value::String(legacy_start_mode(&preferences.start_mode).to_owned()),
        );
        self.document.insert(
            "server_mode_enabled".into(),
            Value::Bool(preferences.server_mode_enabled),
        );
        self.document.insert(
            "server_service_mode".into(),
            Value::String(
                normalize_server_service_mode(&preferences.server_service_mode).to_owned(),
            ),
        );
        self.document.insert(
            "auto_stop_tavern_on_window_close".into(),
            Value::Bool(preferences.auto_stop_tavern_on_window_close),
        );
        self.document.insert(
            "allow_tavern_background".into(),
            Value::Bool(preferences.allow_tavern_background),
        );
        self.document.insert(
            "show_startup_command".into(),
            Value::Bool(preferences.show_startup_command),
        );
        // 存稳定的英文键而不是 serde 派生名，避免枚举变体重命名后旧配置失效。
        self.document.insert(
            "cpu_cores".into(),
            Value::String(preferences.cpu_cores.storage_key().to_owned()),
        );
        self.document.insert(
            "env_mode".into(),
            Value::String(preferences.env_mode.storage_key().to_owned()),
        );
        self.document.insert(
            "staging_risk_confirmed".into(),
            Value::Bool(preferences.staging_risk_confirmed),
        );
        self.document.insert(
            "proxy_type".into(),
            Value::String(legacy_proxy_type(&preferences.proxy_mode).to_owned()),
        );
        self.document.insert(
            "custom_proxy".into(),
            Value::String(preferences.custom_proxy),
        );

        // 清理过渡期写入的其他键，避免与旧版结构混杂。
        for key in [
            "lang",
            "rememberWindowPosition",
            "windowPosition",
            "githubProxy",
            "npmRegistry",
            "networkProxy",
            "proxy_mode",
            "proxyMode",
            "customProxy",
            "dataMode",
            "globalDataPath",
            "tavernExportPath",
            "downloadChannel",
            "downloadResolvedChannel",
            "downloadChannelLastTested",
            "startMode",
            "serverModeEnabled",
            "serverServiceMode",
            "stagingRiskConfirmed",
            "envMode",
            "env_source",
        ] {
            self.document.remove(key);
        }

        if let Some(parent) = self.path.parent() {
            fs::create_dir_all(parent)?;
        }

        let contents = serde_json::to_vec_pretty(&self.document).map_err(io::Error::other)?;
        let temporary = temporary_path(&self.path);
        fs::write(&temporary, contents)?;
        fs::rename(&temporary, &self.path)?;
        Ok(())
    }
}

fn preferences_from_document(document: &Map<String, Value>) -> PersistentPreferences {
    let defaults = PersistentPreferences::default();
    let language = document
        .get("language")
        .or_else(|| document.get("lang"))
        .cloned()
        .and_then(|value| serde_json::from_value(value).ok())
        .unwrap_or(defaults.language);
    let theme = document
        .get("theme")
        .cloned()
        .and_then(|value| serde_json::from_value(value).ok())
        .unwrap_or(defaults.theme);
    let ui_scale = document
        .get("ui_scale")
        .and_then(Value::as_f64)
        .map(|value| crate::core::typography::normalize_ui_scale(value as f32))
        .unwrap_or(defaults.ui_scale);
    let font_family = document
        .get("font_family")
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .unwrap_or(&defaults.font_family)
        .to_owned();
    let remember_window_position = document
        .get("remember_window_pos")
        .or_else(|| document.get("remember_window_position"))
        .or_else(|| document.get("rememberWindowPosition"))
        .and_then(Value::as_bool)
        .unwrap_or(defaults.remember_window_position);
    let window_position = document
        .get("window_position")
        .or_else(|| document.get("windowPosition"))
        .and_then(parse_window_position)
        .filter(|[x, y]| x.is_finite() && y.is_finite());
    let proxy_mode = document
        .get("proxy_type")
        .or_else(|| document.get("proxy_mode"))
        .or_else(|| {
            document
                .get("networkProxy")
                .and_then(Value::as_object)
                .and_then(|obj| obj.get("mode"))
        })
        .and_then(Value::as_str)
        .map(normalize_proxy_type)
        .unwrap_or_else(|| defaults.proxy_mode.clone());
    let custom_proxy = document
        .get("custom_proxy")
        .or_else(|| document.get("customProxy"))
        .and_then(Value::as_str)
        .map(str::to_owned)
        .or_else(|| {
            document
                .get("networkProxy")
                .and_then(Value::as_object)
                .and_then(|obj| {
                    let host = obj.get("host").and_then(Value::as_str)?;
                    let port = obj.get("port").and_then(Value::as_u64).unwrap_or(7890);
                    Some(format!("{host}:{port}"))
                })
        })
        .unwrap_or_else(|| defaults.custom_proxy.clone());
    let github_proxy_enabled = document
        .get("github_proxy_enabled")
        .or_else(|| {
            document
                .get("githubProxy")
                .and_then(Value::as_object)
                .and_then(|obj| obj.get("enable"))
        })
        .and_then(Value::as_bool)
        .unwrap_or(defaults.github_proxy_enabled);
    let github_proxy_url = document
        .get("github_proxy_url")
        .or_else(|| {
            document
                .get("githubProxy")
                .and_then(Value::as_object)
                .and_then(|obj| obj.get("url"))
        })
        .and_then(Value::as_str)
        .filter(|url| !url.trim().is_empty())
        .unwrap_or(&defaults.github_proxy_url)
        .to_owned();
    let npm_registry = document
        .get("npm_registry")
        .or_else(|| document.get("npmRegistry"))
        .and_then(Value::as_str)
        .filter(|url| !url.trim().is_empty())
        .unwrap_or(&defaults.npm_registry)
        .to_owned();
    let download_channel = document
        .get("download_channel")
        .or_else(|| document.get("downloadChannel"))
        .and_then(Value::as_str)
        .map(normalize_download_channel)
        .unwrap_or_else(|| defaults.download_channel.clone());
    let auto_start = document
        .get("auto_start")
        .or_else(|| document.get("autoStart"))
        .and_then(Value::as_bool)
        .unwrap_or(defaults.auto_start);
    let data_mode = document
        .get("data_mode")
        .or_else(|| document.get("dataMode"))
        .and_then(Value::as_str)
        .map(normalize_data_mode)
        .unwrap_or_else(|| defaults.data_mode.clone());
    let stored_global_data_path = document
        .get("global_data_path")
        .or_else(|| document.get("globalDataPath"))
        .and_then(Value::as_str)
        .filter(|path| !path.trim().is_empty())
        .unwrap_or(&defaults.global_data_path);
    let global_data_path = normalize_global_data_path(stored_global_data_path);
    let tavern_export_path = document
        .get("tavern_export_path")
        .or_else(|| document.get("tavernExportPath"))
        .and_then(Value::as_str)
        .filter(|path| !path.trim().is_empty())
        .unwrap_or(&defaults.tavern_export_path)
        .to_owned();
    let start_mode = document
        .get("start_mode")
        .or_else(|| document.get("startMode"))
        .or_else(|| document.get("launchMode"))
        .and_then(Value::as_str)
        .map(normalize_start_mode)
        .unwrap_or_else(|| defaults.start_mode.clone());
    let server_mode_enabled = document
        .get("server_mode_enabled")
        .or_else(|| document.get("serverModeEnabled"))
        .and_then(Value::as_bool)
        .unwrap_or(defaults.server_mode_enabled);
    let server_service_mode = document
        .get("server_service_mode")
        .or_else(|| document.get("serverServiceMode"))
        .and_then(Value::as_str)
        .map(normalize_server_service_mode)
        .unwrap_or_else(|| defaults.server_service_mode.clone());
    let auto_stop_tavern_on_window_close = document
        .get("auto_stop_tavern_on_window_close")
        .or_else(|| document.get("auto_stop_tavern_on_webview_close"))
        .or_else(|| document.get("autoStopTavernOnWebviewClose"))
        .or_else(|| document.get("autoStopTavernOnWindowClose"))
        .and_then(Value::as_bool)
        .unwrap_or(defaults.auto_stop_tavern_on_window_close);
    let allow_tavern_background = document
        .get("allow_tavern_background")
        .or_else(|| document.get("allowTavernBackground"))
        .and_then(Value::as_bool)
        .unwrap_or(defaults.allow_tavern_background);
    let show_startup_command = document
        .get("show_startup_command")
        .or_else(|| document.get("showStartupCommand"))
        .and_then(Value::as_bool)
        .unwrap_or(defaults.show_startup_command);
    let env_mode = document
        .get("env_mode")
        .or_else(|| document.get("envMode"))
        .or_else(|| document.get("env_source"))
        .and_then(Value::as_str)
        .map(EnvSource::from_key)
        .unwrap_or(defaults.env_mode);
    let staging_risk_confirmed = document
        .get("staging_risk_confirmed")
        .or_else(|| document.get("stagingRiskConfirmed"))
        .and_then(Value::as_bool)
        .unwrap_or(defaults.staging_risk_confirmed);
    let cpu_cores = document
        .get("cpu_cores")
        .or_else(|| document.get("cpuCores"))
        .and_then(Value::as_str)
        .map(CpuCores::from_key)
        .unwrap_or(defaults.cpu_cores);

    PersistentPreferences {
        language,
        theme,
        ui_scale,
        font_family,
        remember_window_position,
        window_position,
        proxy_mode,
        custom_proxy,
        github_proxy_enabled,
        github_proxy_url,
        npm_registry,
        download_channel,
        auto_start,
        data_mode,
        global_data_path,
        tavern_export_path,
        start_mode,
        server_mode_enabled,
        server_service_mode,
        auto_stop_tavern_on_window_close,
        allow_tavern_background,
        show_startup_command,
        env_mode,
        cpu_cores,
        staging_risk_confirmed,
    }
}

fn parse_window_position(value: &Value) -> Option<[f32; 2]> {
    if let Some(position) = value.as_object() {
        let x = position.get("x").and_then(Value::as_f64)? as f32;
        let y = position.get("y").and_then(Value::as_f64)? as f32;
        return Some([x, y]);
    }
    serde_json::from_value::<[f32; 2]>(value.clone()).ok()
}

fn normalize_proxy_type(value: &str) -> String {
    match value.trim().to_ascii_lowercase().as_str() {
        "none" | "direct" | "off" | "resources.import.close" | "直连" => "none".to_owned(),
        "custom" | "settings.proxy.custom" | "自定义代理" => "custom".to_owned(),
        _ => "system".to_owned(),
    }
}

fn normalize_download_channel(value: &str) -> String {
    match value.trim().to_ascii_lowercase().as_str() {
        "mirror1" | "mirror_1" | "镜像1" | "镜像 1" => "mirror1".to_owned(),
        "mirror2" | "mirror_2" | "镜像2" | "镜像 2" => "mirror2".to_owned(),
        // 已下线的 mirror3 不再映射到具体渠道，回落到“自动”。
        "official" | "官方" => "official".to_owned(),
        _ => "auto".to_owned(),
    }
}

fn normalize_data_mode(value: &str) -> String {
    match value.trim().to_ascii_lowercase().as_str() {
        "global" => "global".to_owned(),
        _ => "current".to_owned(),
    }
}

/// 将历史错误默认目录迁移到规范路径，同时保留用户主动选择的其他目录。
fn normalize_global_data_path(value: &str) -> String {
    let value = value.trim().trim_end_matches('/');
    let is_legacy = LEGACY_GLOBAL_DATA_PATHS.iter().any(|legacy| {
        if value == *legacy {
            return true;
        }
        // 旧值可能是已展开的绝对路径，这里按同一规则展开后比较。
        Path::new(value) == crate::utils::expand_user_path(legacy)
    });
    if is_legacy {
        DEFAULT_GLOBAL_DATA_PATH.to_owned()
    } else {
        value.to_owned()
    }
}

fn normalize_server_service_mode(value: &str) -> String {
    match value.trim().to_ascii_lowercase().as_str() {
        "internet" | "public" | "互联网" => "internet".to_owned(),
        _ => "lan".to_owned(),
    }
}

fn normalize_start_mode(value: &str) -> String {
    match value.trim().to_ascii_lowercase().as_str() {
        "desktop" => "desktop".to_owned(),
        _ => "normal".to_owned(),
    }
}

fn legacy_start_mode(value: &str) -> &'static str {
    match value {
        "desktop" => "Desktop",
        _ => "Normal",
    }
}

fn legacy_proxy_type(value: &str) -> &'static str {
    match value {
        "none" => "None",
        "custom" => "Custom",
        _ => "System",
    }
}

fn default_settings_path() -> PathBuf {
    crate::utils::app_paths().settings_file()
}

/// 把旧版本遗留的 `settings.json` 迁移为当前的 `config.json`。
///
/// 仅在新文件不存在、旧文件存在时执行一次，避免覆盖用户已有配置。
/// 迁移失败时静默继续——最坏情况是用户重新配置，不会丢失或破坏数据。
fn migrate_legacy_settings_file(target: &Path) {
    const LEGACY_FILE_NAME: &str = "settings.json";
    if target.exists() {
        return;
    }
    let Some(parent) = target.parent() else {
        return;
    };
    let legacy = parent.join(LEGACY_FILE_NAME);
    if legacy.exists() {
        let _ = fs::rename(&legacy, target);
    }
}

fn temporary_path(path: &Path) -> PathBuf {
    let name = path
        .file_name()
        .and_then(|name| name.to_str())
        .unwrap_or("settings.json");
    path.with_file_name(format!(".{name}.tmp"))
}

#[cfg(test)]
mod tests {
    use super::{
        DEFAULT_GLOBAL_DATA_PATH, DisplayLanguage, PersistentPreferences, SettingsStore, ThemeMode,
    };
    use std::fs;

    fn test_path(name: &str) -> std::path::PathBuf {
        std::env::temp_dir().join(format!(
            "astrabrew-settings-{name}-{}.json",
            std::process::id()
        ))
    }

    #[test]
    fn reads_legacy_names_and_preserves_unknown_fields() {
        let path = test_path("legacy");
        fs::write(
            &path,
            r#"{"language":"Chinese","theme":"Dark","remember_window_pos":false,"window_position":[-120.0,48.0],"proxy_type":"Custom"}"#,
        )
        .expect("write fixture");

        let (mut store, preferences) = SettingsStore::load(&path);
        assert_eq!(preferences.language, DisplayLanguage::SimplifiedChinese);
        assert_eq!(preferences.theme, ThemeMode::Dark);
        assert!(!preferences.remember_window_position);

        store
            .save(PersistentPreferences {
                language: DisplayLanguage::English,
                ..preferences
            })
            .expect("save settings");
        let value: serde_json::Value =
            serde_json::from_slice(&fs::read(&path).expect("read settings"))
                .expect("parse settings");
        assert_eq!(value["proxy_type"], "Custom");
        assert_eq!(value["language"], "English");
        let _ = fs::remove_file(path);
    }

    #[test]
    fn interface_preferences_default_normalize_and_roundtrip() {
        let defaults = PersistentPreferences::default();
        assert!(
            (defaults.ui_scale - crate::core::typography::DEFAULT_UI_SCALE).abs() < f32::EPSILON
        );
        assert_eq!(
            defaults.font_family,
            crate::core::typography::DEFAULT_FONT_KEY
        );

        let path = test_path("interface-preferences");
        fs::write(
            &path,
            r#"{"ui_scale":1.13,"font_family":"PingFang SC","unknown":true}"#,
        )
        .expect("write interface preferences fixture");
        let (mut store, preferences) = SettingsStore::load(&path);
        assert!((preferences.ui_scale - 1.15).abs() < f32::EPSILON);
        assert_eq!(preferences.font_family, "PingFang SC");
        store.save(preferences).expect("save interface preferences");

        let value: serde_json::Value =
            serde_json::from_slice(&fs::read(&path).expect("read interface preferences"))
                .expect("parse interface preferences");
        assert_eq!(value["ui_scale"], 1.15);
        assert_eq!(value["font_family"], "PingFang SC");
        assert_eq!(value["unknown"], true);
        let _ = fs::remove_file(path);
    }

    #[test]
    fn persists_proxy_mode_and_custom_proxy() {
        let path = test_path("proxy");
        let (mut store, _) = SettingsStore::load(&path);
        store
            .save(PersistentPreferences {
                proxy_mode: "system".to_owned(),
                custom_proxy: "127.0.0.1:7890".to_owned(),
                ..PersistentPreferences::default()
            })
            .expect("save proxy settings");

        let (_, preferences) = SettingsStore::load(&path);
        assert_eq!(preferences.proxy_mode, "system");
        assert_eq!(preferences.custom_proxy, "127.0.0.1:7890");
        let value: serde_json::Value =
            serde_json::from_slice(&fs::read(&path).expect("read settings"))
                .expect("parse settings");
        assert_eq!(value["proxy_type"], "System");
        assert_eq!(value["custom_proxy"], "127.0.0.1:7890");
        let _ = fs::remove_file(path);
    }

    #[test]
    fn missing_proxy_mode_defaults_to_follow_system() {
        let path = test_path("proxy-default");
        fs::write(&path, r#"{"language":"English"}"#).expect("write fixture");
        let (_, preferences) = SettingsStore::load(&path);
        assert_eq!(preferences.proxy_mode, "system");
        let _ = fs::remove_file(path);
    }

    #[test]
    fn global_data_path_uses_directory_spec_and_migrates_old_default() {
        assert_eq!(
            PersistentPreferences::default().global_data_path,
            DEFAULT_GLOBAL_DATA_PATH
        );

        let legacy = test_path("legacy-global-data");
        fs::write(
            &legacy,
            r#"{"global_data_path":"~/Library/Application Support/AstraBrew/data"}"#,
        )
        .expect("write legacy global data fixture");
        let (mut store, preferences) = SettingsStore::load(&legacy);
        assert_eq!(preferences.global_data_path, DEFAULT_GLOBAL_DATA_PATH);
        store
            .save(preferences)
            .expect("save migrated global data path");
        let saved: serde_json::Value =
            serde_json::from_slice(&fs::read(&legacy).expect("read migrated global data settings"))
                .expect("parse migrated global data settings");
        assert_eq!(saved["global_data_path"], DEFAULT_GLOBAL_DATA_PATH);
        let _ = fs::remove_file(legacy);

        let previous = test_path("previous-global-data");
        fs::write(
            &previous,
            r#"{"global_data_path":"~/Library/Application Support/AstraBrew Launcher/data/sillytavern"}"#,
        )
        .expect("write previous global data fixture");
        let (_, preferences) = SettingsStore::load(&previous);
        assert_eq!(preferences.global_data_path, DEFAULT_GLOBAL_DATA_PATH);
        let _ = fs::remove_file(previous);

        let custom = test_path("custom-global-data");
        fs::write(&custom, r#"{"global_data_path":"~/Custom/Tavern"}"#)
            .expect("write custom global data fixture");
        let (_, preferences) = SettingsStore::load(&custom);
        assert_eq!(preferences.global_data_path, "~/Custom/Tavern");
        let _ = fs::remove_file(custom);
    }

    #[test]
    fn accepts_current_aliases_and_recovers_from_invalid_json() {
        let alias_path = test_path("aliases");
        fs::write(
            &alias_path,
            r#"{"language":"SimplifiedChinese","remember_window_position":false}"#,
        )
        .expect("write fixture");
        let (_, preferences) = SettingsStore::load(&alias_path);
        assert_eq!(preferences.language, DisplayLanguage::SimplifiedChinese);
        assert!(!preferences.remember_window_position);
        let _ = fs::remove_file(alias_path);

        let invalid_path = test_path("invalid");
        fs::write(&invalid_path, "not json").expect("write fixture");
        let (_, invalid) = SettingsStore::load(&invalid_path);
        assert_eq!(invalid, PersistentPreferences::default());
        let _ = fs::remove_file(invalid_path);
    }
    #[test]
    fn persists_server_service_mode_and_reads_legacy_alias() {
        let legacy = test_path("service-mode-legacy");
        fs::write(
            &legacy,
            r#"{"serverModeEnabled":true,"serverServiceMode":"Internet"}"#,
        )
        .expect("write service mode fixture");
        let (mut store, preferences) = SettingsStore::load(&legacy);
        assert!(preferences.server_mode_enabled);
        assert_eq!(preferences.server_service_mode, "internet");
        store
            .save(preferences)
            .expect("save normalized service mode");
        let value: serde_json::Value =
            serde_json::from_slice(&fs::read(&legacy).expect("read normalized settings"))
                .expect("parse normalized settings");
        assert_eq!(value["server_service_mode"], "internet");
        assert!(value.get("serverServiceMode").is_none());
        let _ = fs::remove_file(legacy);

        let invalid = test_path("service-mode-invalid");
        fs::write(&invalid, r#"{"server_service_mode":"unsupported"}"#)
            .expect("write invalid mode fixture");
        let (_, preferences) = SettingsStore::load(&invalid);
        assert_eq!(preferences.server_service_mode, "lan");
        let _ = fs::remove_file(invalid);
    }
}
