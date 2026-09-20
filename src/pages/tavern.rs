//! 酒馆配置页面：提供面向新手的常用配置，以及与旧版一致的完整高级配置。

use std::fmt;

pub(crate) mod sync;
use crate::core::tavern_config::{ConfigError, ErrorKind, Values};

use crate::lang::{raw, t, text};
use iced::widget::{button, column, container, pick_list, row, scrollable, space, text_input};
use iced::{Alignment, Background, Border, Color, Element, Fill, Length, Theme};
use lucide_icons::Icon;

use crate::theme::{button_style, pick_list_menu_style, pick_list_style, text_input_style};
use astra_ui::{
    BLUE_600, ButtonVariant, CYAN_500, DANGER, INK_MUTED, SUCCESS, WARNING, WHITE, icons,
    pick_list_handle,
};

const CONTROL_WIDTH: f32 = 270.0;
const LIST_WIDTH: f32 = 330.0;
// 仅限制配置表单的阅读宽度，页头不受此限制。
const FORM_WIDTH: f32 = 840.0;

macro_rules! enum_text {
    ($ty:ident, $([$variant:ident, $label:literal]),+ $(,)?) => {
        impl fmt::Display for $ty {
            fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
                f.write_str(&crate::lang::t(match self { $(Self::$variant => $label,)+ }))
            }
        }
    };
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, serde::Serialize, serde::Deserialize)]
pub(crate) enum BrowserType {
    Unknown,
    #[default]
    System,
    Chrome,
    Firefox,
    Edge,
}
impl BrowserType {
    pub(crate) const ALL: [Self; 4] = [Self::System, Self::Chrome, Self::Firefox, Self::Edge];

    pub(crate) const fn label_key(self) -> &'static str {
        match self {
            Self::Unknown => "tavern.browser_type.unknown",
            Self::System => "tavern.browser_type.system",
            Self::Chrome => "Chrome",
            Self::Firefox => "Firefox",
            Self::Edge => "Edge",
        }
    }
}
enum_text!(
    BrowserType,
    [Unknown, "tavern.browser_type.unknown"],
    [System, "tavern.browser_type.system"],
    [Chrome, "Chrome"],
    [Firefox, "Firefox"],
    [Edge, "Edge"]
);

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, serde::Serialize, serde::Deserialize)]
pub(crate) enum ThumbnailFormat {
    Unknown,
    #[default]
    Jpeg,
    Png,
    Webp,
}
impl ThumbnailFormat {
    const ALL: [Self; 3] = [Self::Jpeg, Self::Png, Self::Webp];
}
enum_text!(
    ThumbnailFormat,
    [Unknown, "tavern.browser_type.unknown"],
    [Jpeg, "tavern.thumbnail_format.jpeg"],
    [Png, "PNG"],
    [Webp, "tavern.thumbnail_format.webp"]
);

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, serde::Serialize, serde::Deserialize)]
pub(crate) enum LogLevel {
    Unknown,
    #[default]
    Debug,
    Info,
    Warn,
    Error,
}
impl LogLevel {
    const ALL: [Self; 4] = [Self::Debug, Self::Info, Self::Warn, Self::Error];
}
enum_text!(
    LogLevel,
    [Unknown, "tavern.browser_type.unknown"],
    [Debug, "tavern.log_level.debug"],
    [Info, "1 - Info"],
    [Warn, "2 - Warn"],
    [Error, "3 - Error"]
);

/// 所有布尔配置的稳定标识，供通用开关消息使用。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum BoolField {
    Listen,
    ProtocolIpv4,
    ProtocolIpv6,
    DnsPreferIpv6,
    BrowserLaunchEnabled,
    BasicAuthMode,
    EnableUserAccounts,
    EnableDiscreetLogin,
    PerUserBasicAuth,
    WhitelistMode,
    HostWhitelistEnabled,
    HostWhitelistScan,
    SslEnabled,
    CorsEnabled,
    CorsCredentials,
    RequestProxyEnabled,
    ChatBackupsEnabled,
    ChatBackupsCheckIntegrity,
    ThumbnailsEnabled,
    LazyLoadCharacters,
    UseDiskCache,
    EnableAccessLog,
    DisableCsrfProtection,
    SecurityOverride,
    AllowKeysExposure,
    SkipContentCheck,
    ExtensionsEnabled,
    ExtensionsAutoUpdate,
    EnableServerPlugins,
    EnableServerPluginsAutoUpdate,
    AutheliaAuth,
    AuthentikAuth,
    CacheBusterEnabled,
    EnableCorsProxy,
    EnableDownloadableTokenizers,
}

/// 所有文本及数字输入项的稳定标识。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum TextField {
    Port,
    ListenIpv4,
    ListenIpv6,
    HeartbeatInterval,
    BasicAuthUsername,
    BasicAuthPassword,
    SslCertPath,
    SslKeyPath,
    SslKeyPassphrase,
    CorsMaxAge,
    RequestProxyUrl,
    CommonBackups,
    ChatMaxBackups,
    ChatThrottleInterval,
    ThumbnailQuality,
    BackgroundWidth,
    BackgroundHeight,
    AvatarWidth,
    AvatarHeight,
    PersonaWidth,
    PersonaHeight,
    MemoryCacheCapacity,
    PromptPlaceholder,
    SessionTimeout,
    CacheBusterPattern,
}

/// 可增删列表配置的稳定标识。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum ListField {
    Whitelist,
    HostWhitelist,
    ImportDomains,
    CorsOrigins,
    CorsMethods,
    CorsAllowedHeaders,
    CorsExposedHeaders,
    ProxyBypass,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum TavernAction {
    OpenConfigFile,
    ImportConfig,
}

/// 酒馆配置页的交互消息。
#[derive(Debug, Clone)]
pub(crate) enum TavernMessage {
    ToggleAdvancedSection(usize),
    Toggle(BoolField, bool),
    Edit(TextField, String),
    EditList(ListField, usize, String),
    AddListItem(ListField),
    RemoveListItem(ListField, usize),
    SelectBrowser(BrowserType),
    SelectThumbnailFormat(ThumbnailFormat),
    SelectLogLevel(LogLevel),
    Action(TavernAction),
    GenerateConfig,
    RetryConfig,
    ConfirmImport,
    CancelImport,
    UseDiskValues,
    KeepDraftValues,
    GoToVersions,
    ConfigOverlayInteract,
    ContinueEditing,
    DiscardAndClose,
    /// 恢复默认入口将在配置持久化服务接入后启用。
    #[allow(dead_code)]
    RestoreDefaults,
}

/// 与旧版字段对应的界面草稿；数字输入仍为字符串，以保留编辑中间态。
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
struct TavernConfig {
    port: String,
    listen: bool,
    listen_ipv4: String,
    listen_ipv6: String,
    protocol_ipv4: bool,
    protocol_ipv6: bool,
    basic_auth_mode: bool,
    enable_user_accounts: bool,
    enable_discreet_login: bool,
    per_user_basic_auth: bool,
    basic_auth_username: String,
    basic_auth_password: String,
    whitelist_mode: bool,
    whitelist: Vec<String>,
    cors_enabled: bool,
    cors_origins: Vec<String>,
    cors_methods: Vec<String>,
    cors_allowed_headers: Vec<String>,
    cors_exposed_headers: Vec<String>,
    cors_credentials: bool,
    cors_max_age: String,
    request_proxy_enabled: bool,
    request_proxy_url: String,
    proxy_bypass: Vec<String>,
    common_backups: String,
    chat_backups_enabled: bool,
    chat_backups_check_integrity: bool,
    chat_max_backups: String,
    chat_throttle_interval: String,
    thumbnails_enabled: bool,
    thumbnail_format: ThumbnailFormat,
    thumbnail_quality: String,
    background_width: String,
    background_height: String,
    avatar_width: String,
    avatar_height: String,
    persona_width: String,
    persona_height: String,
    browser_launch_enabled: bool,
    browser_type: BrowserType,
    ssl_enabled: bool,
    ssl_cert_path: String,
    ssl_key_path: String,
    ssl_key_passphrase: String,
    dns_prefer_ipv6: bool,
    heartbeat_interval: String,
    host_whitelist_enabled: bool,
    host_whitelist_scan: bool,
    host_whitelist: Vec<String>,
    import_domains: Vec<String>,
    session_timeout: String,
    disable_csrf_protection: bool,
    security_override: bool,
    allow_keys_exposure: bool,
    skip_content_check: bool,
    enable_access_log: bool,
    min_log_level: LogLevel,
    lazy_load_characters: bool,
    memory_cache_capacity: String,
    use_disk_cache: bool,
    cache_buster_enabled: bool,
    cache_buster_pattern: String,
    authelia_auth: bool,
    authentik_auth: bool,
    extensions_enabled: bool,
    extensions_auto_update: bool,
    enable_server_plugins: bool,
    enable_server_plugins_auto_update: bool,
    enable_cors_proxy: bool,
    prompt_placeholder: String,
    enable_downloadable_tokenizers: bool,
}

impl Default for TavernConfig {
    fn default() -> Self {
        Self {
            port: "8000".into(),
            listen: false,
            listen_ipv4: "0.0.0.0".into(),
            listen_ipv6: "[::]".into(),
            protocol_ipv4: true,
            protocol_ipv6: false,
            basic_auth_mode: false,
            enable_user_accounts: false,
            enable_discreet_login: false,
            per_user_basic_auth: false,
            basic_auth_username: "user".into(),
            basic_auth_password: "password".into(),
            whitelist_mode: true,
            whitelist: vec!["::1".into(), "127.0.0.1".into()],
            cors_enabled: true,
            cors_origins: vec!["null".into()],
            cors_methods: vec!["OPTIONS".into()],
            cors_allowed_headers: Vec::new(),
            cors_exposed_headers: Vec::new(),
            cors_credentials: false,
            cors_max_age: String::new(),
            request_proxy_enabled: false,
            request_proxy_url: String::new(),
            proxy_bypass: Vec::new(),
            common_backups: "50".into(),
            chat_backups_enabled: true,
            chat_backups_check_integrity: true,
            chat_max_backups: "-1".into(),
            chat_throttle_interval: "10000".into(),
            thumbnails_enabled: true,
            thumbnail_format: ThumbnailFormat::Jpeg,
            thumbnail_quality: "95".into(),
            background_width: "160".into(),
            background_height: "90".into(),
            avatar_width: "96".into(),
            avatar_height: "144".into(),
            persona_width: "96".into(),
            persona_height: "144".into(),
            browser_launch_enabled: true,
            browser_type: BrowserType::System,
            ssl_enabled: false,
            ssl_cert_path: "./certs/cert.pem".into(),
            ssl_key_path: "./certs/privkey.pem".into(),
            ssl_key_passphrase: String::new(),
            dns_prefer_ipv6: false,
            heartbeat_interval: "0".into(),
            host_whitelist_enabled: false,
            host_whitelist_scan: true,
            host_whitelist: Vec::new(),
            import_domains: Vec::new(),
            session_timeout: "-1".into(),
            disable_csrf_protection: false,
            security_override: false,
            allow_keys_exposure: false,
            skip_content_check: false,
            enable_access_log: true,
            min_log_level: LogLevel::Debug,
            lazy_load_characters: false,
            memory_cache_capacity: "100".into(),
            use_disk_cache: true,
            cache_buster_enabled: false,
            cache_buster_pattern: String::new(),
            authelia_auth: false,
            authentik_auth: false,
            extensions_enabled: true,
            extensions_auto_update: true,
            enable_server_plugins: false,
            enable_server_plugins_auto_update: true,
            enable_cors_proxy: false,
            prompt_placeholder: "[Start a new chat]".into(),
            enable_downloadable_tokenizers: true,
        }
    }
}

/// 酒馆配置页草稿与同步展示状态；持久化及目标隔离由应用服务协调。
#[derive(Debug, Clone)]
pub(crate) struct TavernState {
    config: TavernConfig,
    advanced_expanded: [bool; 9],
    pub(crate) sync: sync::SyncView,
}

impl Default for TavernState {
    fn default() -> Self {
        Self {
            config: TavernConfig::default(),
            // 与旧版一致：首次进入页面只展开网络基础配置。
            advanced_expanded: [true, false, false, false, false, false, false, false, false],
            sync: sync::SyncView::default(),
        }
    }
}

impl TavernState {
    pub(crate) fn values(&self) -> Values {
        [
            (
                "port".to_owned(),
                serde_json::Value::String(self.config.port.clone()),
            ),
            (
                "listen".to_owned(),
                serde_json::Value::Bool(self.config.listen),
            ),
            (
                "listen_ipv4".to_owned(),
                serde_json::Value::String(self.config.listen_ipv4.clone()),
            ),
            (
                "listen_ipv6".to_owned(),
                serde_json::Value::String(self.config.listen_ipv6.clone()),
            ),
            (
                "protocol_ipv4".to_owned(),
                serde_json::Value::Bool(self.config.protocol_ipv4),
            ),
            (
                "protocol_ipv6".to_owned(),
                serde_json::Value::Bool(self.config.protocol_ipv6),
            ),
            (
                "basic_auth_mode".to_owned(),
                serde_json::Value::Bool(self.config.basic_auth_mode),
            ),
            (
                "enable_user_accounts".to_owned(),
                serde_json::Value::Bool(self.config.enable_user_accounts),
            ),
            (
                "enable_discreet_login".to_owned(),
                serde_json::Value::Bool(self.config.enable_discreet_login),
            ),
            (
                "per_user_basic_auth".to_owned(),
                serde_json::Value::Bool(self.config.per_user_basic_auth),
            ),
            (
                "basic_auth_username".to_owned(),
                serde_json::Value::String(self.config.basic_auth_username.clone()),
            ),
            (
                "basic_auth_password".to_owned(),
                serde_json::Value::String(self.config.basic_auth_password.clone()),
            ),
            (
                "whitelist_mode".to_owned(),
                serde_json::Value::Bool(self.config.whitelist_mode),
            ),
            (
                "whitelist".to_owned(),
                serde_json::Value::Array(
                    self.config
                        .whitelist
                        .iter()
                        .cloned()
                        .map(serde_json::Value::String)
                        .collect(),
                ),
            ),
            (
                "cors_enabled".to_owned(),
                serde_json::Value::Bool(self.config.cors_enabled),
            ),
            (
                "cors_origins".to_owned(),
                serde_json::Value::Array(
                    self.config
                        .cors_origins
                        .iter()
                        .cloned()
                        .map(serde_json::Value::String)
                        .collect(),
                ),
            ),
            (
                "cors_methods".to_owned(),
                serde_json::Value::Array(
                    self.config
                        .cors_methods
                        .iter()
                        .cloned()
                        .map(serde_json::Value::String)
                        .collect(),
                ),
            ),
            (
                "cors_allowed_headers".to_owned(),
                serde_json::Value::Array(
                    self.config
                        .cors_allowed_headers
                        .iter()
                        .cloned()
                        .map(serde_json::Value::String)
                        .collect(),
                ),
            ),
            (
                "cors_exposed_headers".to_owned(),
                serde_json::Value::Array(
                    self.config
                        .cors_exposed_headers
                        .iter()
                        .cloned()
                        .map(serde_json::Value::String)
                        .collect(),
                ),
            ),
            (
                "cors_credentials".to_owned(),
                serde_json::Value::Bool(self.config.cors_credentials),
            ),
            (
                "cors_max_age".to_owned(),
                serde_json::Value::String(self.config.cors_max_age.clone()),
            ),
            (
                "request_proxy_enabled".to_owned(),
                serde_json::Value::Bool(self.config.request_proxy_enabled),
            ),
            (
                "request_proxy_url".to_owned(),
                serde_json::Value::String(self.config.request_proxy_url.clone()),
            ),
            (
                "proxy_bypass".to_owned(),
                serde_json::Value::Array(
                    self.config
                        .proxy_bypass
                        .iter()
                        .cloned()
                        .map(serde_json::Value::String)
                        .collect(),
                ),
            ),
            (
                "common_backups".to_owned(),
                serde_json::Value::String(self.config.common_backups.clone()),
            ),
            (
                "chat_backups_enabled".to_owned(),
                serde_json::Value::Bool(self.config.chat_backups_enabled),
            ),
            (
                "chat_backups_check_integrity".to_owned(),
                serde_json::Value::Bool(self.config.chat_backups_check_integrity),
            ),
            (
                "chat_max_backups".to_owned(),
                serde_json::Value::String(self.config.chat_max_backups.clone()),
            ),
            (
                "chat_throttle_interval".to_owned(),
                serde_json::Value::String(self.config.chat_throttle_interval.clone()),
            ),
            (
                "thumbnails_enabled".to_owned(),
                serde_json::Value::Bool(self.config.thumbnails_enabled),
            ),
            (
                "thumbnail_format".to_owned(),
                serde_json::Value::String(format!("{:?}", self.config.thumbnail_format)),
            ),
            (
                "thumbnail_quality".to_owned(),
                serde_json::Value::String(self.config.thumbnail_quality.clone()),
            ),
            (
                "background_width".to_owned(),
                serde_json::Value::String(self.config.background_width.clone()),
            ),
            (
                "background_height".to_owned(),
                serde_json::Value::String(self.config.background_height.clone()),
            ),
            (
                "avatar_width".to_owned(),
                serde_json::Value::String(self.config.avatar_width.clone()),
            ),
            (
                "avatar_height".to_owned(),
                serde_json::Value::String(self.config.avatar_height.clone()),
            ),
            (
                "persona_width".to_owned(),
                serde_json::Value::String(self.config.persona_width.clone()),
            ),
            (
                "persona_height".to_owned(),
                serde_json::Value::String(self.config.persona_height.clone()),
            ),
            (
                "browser_launch_enabled".to_owned(),
                serde_json::Value::Bool(self.config.browser_launch_enabled),
            ),
            (
                "browser_type".to_owned(),
                serde_json::Value::String(format!("{:?}", self.config.browser_type)),
            ),
            (
                "ssl_enabled".to_owned(),
                serde_json::Value::Bool(self.config.ssl_enabled),
            ),
            (
                "ssl_cert_path".to_owned(),
                serde_json::Value::String(self.config.ssl_cert_path.clone()),
            ),
            (
                "ssl_key_path".to_owned(),
                serde_json::Value::String(self.config.ssl_key_path.clone()),
            ),
            (
                "ssl_key_passphrase".to_owned(),
                serde_json::Value::String(self.config.ssl_key_passphrase.clone()),
            ),
            (
                "dns_prefer_ipv6".to_owned(),
                serde_json::Value::Bool(self.config.dns_prefer_ipv6),
            ),
            (
                "heartbeat_interval".to_owned(),
                serde_json::Value::String(self.config.heartbeat_interval.clone()),
            ),
            (
                "host_whitelist_enabled".to_owned(),
                serde_json::Value::Bool(self.config.host_whitelist_enabled),
            ),
            (
                "host_whitelist_scan".to_owned(),
                serde_json::Value::Bool(self.config.host_whitelist_scan),
            ),
            (
                "host_whitelist".to_owned(),
                serde_json::Value::Array(
                    self.config
                        .host_whitelist
                        .iter()
                        .cloned()
                        .map(serde_json::Value::String)
                        .collect(),
                ),
            ),
            (
                "import_domains".to_owned(),
                serde_json::Value::Array(
                    self.config
                        .import_domains
                        .iter()
                        .cloned()
                        .map(serde_json::Value::String)
                        .collect(),
                ),
            ),
            (
                "session_timeout".to_owned(),
                serde_json::Value::String(self.config.session_timeout.clone()),
            ),
            (
                "disable_csrf_protection".to_owned(),
                serde_json::Value::Bool(self.config.disable_csrf_protection),
            ),
            (
                "security_override".to_owned(),
                serde_json::Value::Bool(self.config.security_override),
            ),
            (
                "allow_keys_exposure".to_owned(),
                serde_json::Value::Bool(self.config.allow_keys_exposure),
            ),
            (
                "skip_content_check".to_owned(),
                serde_json::Value::Bool(self.config.skip_content_check),
            ),
            (
                "enable_access_log".to_owned(),
                serde_json::Value::Bool(self.config.enable_access_log),
            ),
            (
                "min_log_level".to_owned(),
                serde_json::Value::String(format!("{:?}", self.config.min_log_level)),
            ),
            (
                "lazy_load_characters".to_owned(),
                serde_json::Value::Bool(self.config.lazy_load_characters),
            ),
            (
                "memory_cache_capacity".to_owned(),
                serde_json::Value::String(self.config.memory_cache_capacity.clone()),
            ),
            (
                "use_disk_cache".to_owned(),
                serde_json::Value::Bool(self.config.use_disk_cache),
            ),
            (
                "cache_buster_enabled".to_owned(),
                serde_json::Value::Bool(self.config.cache_buster_enabled),
            ),
            (
                "cache_buster_pattern".to_owned(),
                serde_json::Value::String(self.config.cache_buster_pattern.clone()),
            ),
            (
                "authelia_auth".to_owned(),
                serde_json::Value::Bool(self.config.authelia_auth),
            ),
            (
                "authentik_auth".to_owned(),
                serde_json::Value::Bool(self.config.authentik_auth),
            ),
            (
                "extensions_enabled".to_owned(),
                serde_json::Value::Bool(self.config.extensions_enabled),
            ),
            (
                "extensions_auto_update".to_owned(),
                serde_json::Value::Bool(self.config.extensions_auto_update),
            ),
            (
                "enable_server_plugins".to_owned(),
                serde_json::Value::Bool(self.config.enable_server_plugins),
            ),
            (
                "enable_server_plugins_auto_update".to_owned(),
                serde_json::Value::Bool(self.config.enable_server_plugins_auto_update),
            ),
            (
                "enable_cors_proxy".to_owned(),
                serde_json::Value::Bool(self.config.enable_cors_proxy),
            ),
            (
                "prompt_placeholder".to_owned(),
                serde_json::Value::String(self.config.prompt_placeholder.clone()),
            ),
            (
                "enable_downloadable_tokenizers".to_owned(),
                serde_json::Value::Bool(self.config.enable_downloadable_tokenizers),
            ),
        ]
        .into_iter()
        .collect()
    }
    pub(crate) fn apply_values(&mut self, values: Values) -> Result<(), ConfigError> {
        self.config =
            serde_json::from_value(serde_json::Value::Object(values.into_iter().collect()))
                .map_err(|_| ConfigError::new(ErrorKind::Invalid, "tavern.error.sync_to_ui_failed", ""))?;
        Ok(())
    }

    pub(crate) fn browser_type(&self) -> BrowserType {
        self.config.browser_type
    }

    pub(crate) fn whitelist(&self) -> &[String] {
        &self.config.whitelist
    }

    pub(crate) fn update(&mut self, message: TavernMessage) {
        match message {
            TavernMessage::ToggleAdvancedSection(index) => {
                if let Some(expanded) = self.advanced_expanded.get_mut(index) {
                    *expanded = !*expanded;
                }
            }
            TavernMessage::Toggle(field, value) => self.set_bool(field, value),
            TavernMessage::Edit(field, value) => self.set_text(field, value),
            TavernMessage::EditList(field, index, value) => {
                if let Some(item) = self.list_mut(field).get_mut(index) {
                    *item = value;
                }
            }
            TavernMessage::AddListItem(field) => self.list_mut(field).push(String::new()),
            TavernMessage::RemoveListItem(field, index) => {
                let list = self.list_mut(field);
                if index < list.len() {
                    list.remove(index);
                }
            }
            TavernMessage::SelectBrowser(value) => self.config.browser_type = value,
            TavernMessage::SelectThumbnailFormat(value) => self.config.thumbnail_format = value,
            TavernMessage::SelectLogLevel(value) => self.config.min_log_level = value,
            TavernMessage::Action(_)
            | TavernMessage::GenerateConfig
            | TavernMessage::RetryConfig
            | TavernMessage::ConfirmImport
            | TavernMessage::CancelImport
            | TavernMessage::UseDiskValues
            | TavernMessage::KeepDraftValues
            | TavernMessage::GoToVersions
            | TavernMessage::ConfigOverlayInteract
            | TavernMessage::ContinueEditing
            | TavernMessage::DiscardAndClose => {}
            TavernMessage::RestoreDefaults => *self = Self::default(),
        }
    }

    fn set_bool(&mut self, field: BoolField, value: bool) {
        let config = &mut self.config;
        match field {
            BoolField::Listen => config.listen = value,
            BoolField::ProtocolIpv4 => config.protocol_ipv4 = value,
            BoolField::ProtocolIpv6 => config.protocol_ipv6 = value,
            BoolField::DnsPreferIpv6 => config.dns_prefer_ipv6 = value,
            BoolField::BrowserLaunchEnabled => config.browser_launch_enabled = value,
            BoolField::BasicAuthMode => config.basic_auth_mode = value,
            BoolField::EnableUserAccounts => config.enable_user_accounts = value,
            BoolField::EnableDiscreetLogin => config.enable_discreet_login = value,
            BoolField::PerUserBasicAuth => config.per_user_basic_auth = value,
            BoolField::WhitelistMode => config.whitelist_mode = value,
            BoolField::HostWhitelistEnabled => config.host_whitelist_enabled = value,
            BoolField::HostWhitelistScan => config.host_whitelist_scan = value,
            BoolField::SslEnabled => config.ssl_enabled = value,
            BoolField::CorsEnabled => config.cors_enabled = value,
            BoolField::CorsCredentials => config.cors_credentials = value,
            BoolField::RequestProxyEnabled => config.request_proxy_enabled = value,
            BoolField::ChatBackupsEnabled => config.chat_backups_enabled = value,
            BoolField::ChatBackupsCheckIntegrity => config.chat_backups_check_integrity = value,
            BoolField::ThumbnailsEnabled => config.thumbnails_enabled = value,
            BoolField::LazyLoadCharacters => config.lazy_load_characters = value,
            BoolField::UseDiskCache => config.use_disk_cache = value,
            BoolField::EnableAccessLog => config.enable_access_log = value,
            BoolField::DisableCsrfProtection => config.disable_csrf_protection = value,
            BoolField::SecurityOverride => config.security_override = value,
            BoolField::AllowKeysExposure => config.allow_keys_exposure = value,
            BoolField::SkipContentCheck => config.skip_content_check = value,
            BoolField::ExtensionsEnabled => config.extensions_enabled = value,
            BoolField::ExtensionsAutoUpdate => config.extensions_auto_update = value,
            BoolField::EnableServerPlugins => config.enable_server_plugins = value,
            BoolField::EnableServerPluginsAutoUpdate => {
                config.enable_server_plugins_auto_update = value
            }
            BoolField::AutheliaAuth => config.authelia_auth = value,
            BoolField::AuthentikAuth => config.authentik_auth = value,
            BoolField::CacheBusterEnabled => config.cache_buster_enabled = value,
            BoolField::EnableCorsProxy => config.enable_cors_proxy = value,
            BoolField::EnableDownloadableTokenizers => {
                config.enable_downloadable_tokenizers = value
            }
        }
    }

    fn set_text(&mut self, field: TextField, value: String) {
        let config = &mut self.config;
        *match field {
            TextField::Port => &mut config.port,
            TextField::ListenIpv4 => &mut config.listen_ipv4,
            TextField::ListenIpv6 => &mut config.listen_ipv6,
            TextField::HeartbeatInterval => &mut config.heartbeat_interval,
            TextField::BasicAuthUsername => &mut config.basic_auth_username,
            TextField::BasicAuthPassword => &mut config.basic_auth_password,
            TextField::SslCertPath => &mut config.ssl_cert_path,
            TextField::SslKeyPath => &mut config.ssl_key_path,
            TextField::SslKeyPassphrase => &mut config.ssl_key_passphrase,
            TextField::CorsMaxAge => &mut config.cors_max_age,
            TextField::RequestProxyUrl => &mut config.request_proxy_url,
            TextField::CommonBackups => &mut config.common_backups,
            TextField::ChatMaxBackups => &mut config.chat_max_backups,
            TextField::ChatThrottleInterval => &mut config.chat_throttle_interval,
            TextField::ThumbnailQuality => &mut config.thumbnail_quality,
            TextField::BackgroundWidth => &mut config.background_width,
            TextField::BackgroundHeight => &mut config.background_height,
            TextField::AvatarWidth => &mut config.avatar_width,
            TextField::AvatarHeight => &mut config.avatar_height,
            TextField::PersonaWidth => &mut config.persona_width,
            TextField::PersonaHeight => &mut config.persona_height,
            TextField::MemoryCacheCapacity => &mut config.memory_cache_capacity,
            TextField::PromptPlaceholder => &mut config.prompt_placeholder,
            TextField::SessionTimeout => &mut config.session_timeout,
            TextField::CacheBusterPattern => &mut config.cache_buster_pattern,
        } = value;
    }

    fn list_mut(&mut self, field: ListField) -> &mut Vec<String> {
        match field {
            ListField::Whitelist => &mut self.config.whitelist,
            ListField::HostWhitelist => &mut self.config.host_whitelist,
            ListField::ImportDomains => &mut self.config.import_domains,
            ListField::CorsOrigins => &mut self.config.cors_origins,
            ListField::CorsMethods => &mut self.config.cors_methods,
            ListField::CorsAllowedHeaders => &mut self.config.cors_allowed_headers,
            ListField::CorsExposedHeaders => &mut self.config.cors_exposed_headers,
            ListField::ProxyBypass => &mut self.config.proxy_bypass,
        }
    }
}

/// 渲染酒馆配置页，结构与旧版 Tavern.vue 保持一致。
pub(crate) fn tavern_view(state: &TavernState) -> Element<'_, TavernMessage> {
    let header = row![
        container(icons::icon(Icon::List, 21, WHITE))
            .width(40)
            .height(40)
            .align_x(Alignment::Center)
            .align_y(Alignment::Center)
            .style(page_icon_style),
        column![
            text("tavern.title").size(21).font(crate::core::typography::medium()),
            row![
                crate::theme::muted_icon(Icon::Settings, 10),
                text("tavern.subtitle")
                    .size(12)
                    .font(crate::core::typography::regular())
                    .style(crate::theme::muted_text_style)
            ]
            .spacing(5)
            .align_y(Alignment::Center)
        ]
        .spacing(4),
        space::horizontal(),
        status_badge(&state.sync),
        header_button(
            "tavern.action.open_config_file",
            Icon::FolderOpen,
            TavernAction::OpenConfigFile
        ),
        header_button(
            "tavern.action.import_config_file",
            Icon::ArrowDownUp,
            TavernAction::ImportConfig
        ),
    ]
    .spacing(11)
    .align_y(Alignment::Center)
    .width(Fill);

    let header = column![header, crate::theme::separator()]
        .spacing(20)
        .width(Fill);

    let groups = config_groups(state);
    let groups = container(groups)
        .width(Fill)
        .max_width(FORM_WIDTH)
        .padding([4, 0]);
    let scroller = scrollable(container(groups).width(Fill).align_x(Alignment::Center))
        .width(Fill)
        .height(Fill);

    // 页头及分隔线铺满主内容区；下方表单仍保持限宽居中。
    let scroller = sync::with_overlay(scroller.into(), &state.sync);
    let content = column![header, scroller]
        .spacing(18)
        .width(Fill)
        .height(Fill);

    container(content)
        .width(Fill)
        .height(Fill)
        .padding([26, 32])
        .align_x(Alignment::Center)
        .style(crate::theme::canvas_style)
        .into()
}

fn config_groups(state: &TavernState) -> Element<'_, TavernMessage> {
    let config = &state.config;
    column![
        network_section(config, state.advanced_expanded[0]),
        security_section(
            config,
            state.advanced_expanded[1],
            &state.sync.fixed_whitelist,
        ),
        ssl_section(config, state.advanced_expanded[2]),
        cors_section(config, state.advanced_expanded[3]),
        proxy_backup_section(config, state.advanced_expanded[4]),
        thumbnail_section(config, state.advanced_expanded[5]),
        performance_section(config, state.advanced_expanded[6]),
        logging_section(config, state.advanced_expanded[7]),
        session_security_section(config, state.advanced_expanded[8]),
    ]
    .spacing(14)
    .width(Fill)
    .into()
}

fn network_section(config: &TavernConfig, expanded: bool) -> Element<'_, TavernMessage> {
    let port = row![
        container(stacked_field(
            "tavern.config.port",
            wide_text_control("8000", &config.port, TextField::Port, false),
            None,
        ))
        .width(Length::FillPortion(1)),
        space::horizontal().width(Length::FillPortion(1)),
    ]
    .spacing(20)
    .width(Fill);

    let listen_addresses = row![
        container(stacked_field(
            "tavern.config.listen_ipv4",
            wide_text_control("0.0.0.0", &config.listen_ipv4, TextField::ListenIpv4, false,),
            None,
        ))
        .width(Length::FillPortion(1)),
        container(stacked_field(
            "tavern.config.listen_ipv6",
            wide_text_control("[::]", &config.listen_ipv6, TextField::ListenIpv6, false,),
            None,
        ))
        .width(Length::FillPortion(1)),
    ]
    .spacing(20)
    .width(Fill);

    let protocol_options = row![
        pill_toggle("tavern.config.allow_lan", BoolField::Listen, config.listen),
        pill_toggle("tavern.config.enable_ipv4", BoolField::ProtocolIpv4, config.protocol_ipv4),
        pill_toggle("tavern.config.enable_ipv6", BoolField::ProtocolIpv6, config.protocol_ipv6),
        pill_toggle(
            "tavern.config.dns_ipv6_prefer",
            BoolField::DnsPreferIpv6,
            config.dns_prefer_ipv6,
        ),
    ]
    .spacing(10)
    .align_y(Alignment::Center);

    let launch_options = row![
        container(stacked_field(
            "tavern.config.heartbeat_interval",
            wide_text_control(
                "0",
                &config.heartbeat_interval,
                TextField::HeartbeatInterval,
                false,
            ),
            Some("tavern.config.heartbeat_interval.hint"),
        ))
        .width(Length::FillPortion(1)),
        container(stacked_field(
            "tavern.config.browser_type",
            wide_select_control(
                &BrowserType::ALL,
                config.browser_type,
                TavernMessage::SelectBrowser,
            ),
            None,
        ))
        .width(Length::FillPortion(1)),
    ]
    .spacing(20)
    .width(Fill);

    let content = column![
        port,
        crate::theme::separator(),
        listen_addresses,
        protocol_options,
        crate::theme::separator(),
        launch_options,
        pill_toggle(
            "tavern.config.browser_launch",
            BoolField::BrowserLaunchEnabled,
            config.browser_launch_enabled,
        ),
    ]
    .spacing(18)
    .padding(20)
    .width(Fill);

    advanced_group(
        0,
        expanded,
        Icon::Globe,
        BLUE_600,
        "tavern.section.network.title",
        "tavern.section.network.description",
        content.into(),
    )
}

fn security_section<'a>(
    config: &'a TavernConfig,
    expanded: bool,
    fixed_whitelist: &'a [String],
) -> Element<'a, TavernMessage> {
    let mut rows = vec![
        field_row(
            "tavern.config.basic_auth_mode",
            "tavern.config.basic_auth_mode.hint",
            toggle_control(BoolField::BasicAuthMode, config.basic_auth_mode),
        ),
        field_row(
            "tavern.config.enable_user_accounts",
            "tavern.config.enable_user_accounts.hint",
            toggle_control(BoolField::EnableUserAccounts, config.enable_user_accounts),
        ),
    ];
    if config.basic_auth_mode {
        rows.extend([
            field_row(
                "tavern.config.basic_auth_username",
                "tavern.config.basic_auth_username.hint",
                text_control(
                    "user",
                    &config.basic_auth_username,
                    TextField::BasicAuthUsername,
                    false,
                ),
            ),
            field_row(
                "tavern.config.basic_auth_password",
                "tavern.config.basic_auth_password.hint",
                text_control(
                    "password",
                    &config.basic_auth_password,
                    TextField::BasicAuthPassword,
                    true,
                ),
            ),
        ]);
    }
    rows.extend([
        field_row(
            "tavern.config.enable_discreet_login",
            "tavern.config.enable_discreet_login.hint",
            toggle_control(BoolField::EnableDiscreetLogin, config.enable_discreet_login),
        ),
        field_row(
            "tavern.config.per_user_basic_auth",
            "tavern.config.per_user_basic_auth.hint",
            toggle_control(BoolField::PerUserBasicAuth, config.per_user_basic_auth),
        ),
        field_row(
            "tavern.config.whitelist_mode",
            "tavern.config.whitelist_mode.hint",
            toggle_control(BoolField::WhitelistMode, config.whitelist_mode),
        ),
    ]);
    if config.whitelist_mode {
        rows.push(field_row(
            "tavern.config.whitelist",
            "tavern.config.whitelist.hint",
            whitelist_control(&config.whitelist, fixed_whitelist),
        ));
    }
    rows.extend([
        field_row(
            "tavern.config.host_whitelist_enabled",
            "tavern.config.host_whitelist_enabled.hint",
            toggle_control(
                BoolField::HostWhitelistEnabled,
                config.host_whitelist_enabled,
            ),
        ),
        field_row(
            "tavern.config.host_whitelist_scan",
            "tavern.config.host_whitelist_scan.hint",
            toggle_control(BoolField::HostWhitelistScan, config.host_whitelist_scan),
        ),
    ]);
    if config.host_whitelist_enabled {
        rows.push(field_row(
            "tavern.config.host_whitelist",
            "tavern.config.host_whitelist.hint",
            list_control(
                &config.host_whitelist,
                ListField::HostWhitelist,
                "tavern.config.host_whitelist.placeholder",
            ),
        ));
    }
    rows.push(field_row(
        "tavern.config.import_domains",
        "tavern.config.import_domains.hint",
        list_control(
            &config.import_domains,
            ListField::ImportDomains,
            "tavern.config.import_domains.placeholder",
        ),
    ));

    advanced_group(
        1,
        expanded,
        Icon::ShieldCheck,
        Color::from_rgb8(124, 58, 237),
        "tavern.section.security.title",
        "tavern.section.security.description",
        section_rows(rows),
    )
}

fn ssl_section(config: &TavernConfig, expanded: bool) -> Element<'_, TavernMessage> {
    let mut rows = vec![field_row(
        "tavern.config.ssl_enabled",
        "tavern.config.ssl_enabled.hint",
        toggle_control(BoolField::SslEnabled, config.ssl_enabled),
    )];
    if config.ssl_enabled {
        rows.extend([
            field_row(
                "tavern.config.ssl_cert_path",
                "tavern.config.ssl_cert_path.hint",
                text_control(
                    "./certs/cert.pem",
                    &config.ssl_cert_path,
                    TextField::SslCertPath,
                    false,
                ),
            ),
            field_row(
                "tavern.config.ssl_key_path",
                "tavern.config.ssl_key_path.hint",
                text_control(
                    "./certs/privkey.pem",
                    &config.ssl_key_path,
                    TextField::SslKeyPath,
                    false,
                ),
            ),
            field_row(
                "tavern.config.ssl_key_passphrase",
                "tavern.config.ssl_key_passphrase.hint",
                text_control(
                    "tavern.config.ssl_key_passphrase.placeholder",
                    &config.ssl_key_passphrase,
                    TextField::SslKeyPassphrase,
                    true,
                ),
            ),
        ]);
    }
    advanced_group(
        2,
        expanded,
        Icon::LockKeyhole,
        SUCCESS,
        "HTTPS / SSL",
        "tavern.section.ssl.description",
        section_rows(rows),
    )
}

fn cors_section(config: &TavernConfig, expanded: bool) -> Element<'_, TavernMessage> {
    let mut rows = vec![field_row(
        "tavern.config.cors_enabled",
        "tavern.config.cors_enabled.hint",
        toggle_control(BoolField::CorsEnabled, config.cors_enabled),
    )];
    if config.cors_enabled {
        rows.extend([
            field_row(
                "tavern.config.cors_origins",
                "tavern.config.cors_origins.hint",
                list_control(
                    &config.cors_origins,
                    ListField::CorsOrigins,
                    "tavern.config.cors_origins.placeholder",
                ),
            ),
            field_row(
                "tavern.config.cors_methods",
                "tavern.config.cors_methods.hint",
                list_control(
                    &config.cors_methods,
                    ListField::CorsMethods,
                    "tavern.config.cors_methods.placeholder",
                ),
            ),
            field_row(
                "tavern.config.cors_max_age",
                "tavern.config.cors_max_age.hint",
                text_control("tavern.config.cors_max_age.placeholder", &config.cors_max_age, TextField::CorsMaxAge, false),
            ),
            field_row(
                "tavern.config.cors_allowed_headers",
                "tavern.config.cors_allowed_headers.hint",
                list_control(
                    &config.cors_allowed_headers,
                    ListField::CorsAllowedHeaders,
                    "tavern.config.cors_allowed_headers.placeholder",
                ),
            ),
            field_row(
                "tavern.config.cors_exposed_headers",
                "tavern.config.cors_exposed_headers.hint",
                list_control(
                    &config.cors_exposed_headers,
                    ListField::CorsExposedHeaders,
                    "tavern.config.cors_exposed_headers.placeholder",
                ),
            ),
            field_row(
                "tavern.config.cors_credentials",
                "tavern.config.cors_credentials.hint",
                toggle_control(BoolField::CorsCredentials, config.cors_credentials),
            ),
        ]);
    }
    advanced_group(
        3,
        expanded,
        Icon::PlugZap,
        CYAN_500,
        "tavern.section.cors.title",
        "tavern.section.cors.description",
        section_rows(rows),
    )
}

fn proxy_backup_section(config: &TavernConfig, expanded: bool) -> Element<'_, TavernMessage> {
    let mut rows = vec![field_row(
        "tavern.config.request_proxy_enabled",
        "tavern.config.request_proxy_enabled.hint",
        toggle_control(BoolField::RequestProxyEnabled, config.request_proxy_enabled),
    )];
    if config.request_proxy_enabled {
        rows.extend([
            field_row(
                "tavern.config.request_proxy_url",
                "tavern.config.request_proxy_url.hint",
                text_control(
                    "http://proxy.example.com:8080",
                    &config.request_proxy_url,
                    TextField::RequestProxyUrl,
                    false,
                ),
            ),
            field_row(
                "tavern.config.proxy_bypass",
                "tavern.config.proxy_bypass.hint",
                list_control(
                    &config.proxy_bypass,
                    ListField::ProxyBypass,
                    "tavern.config.host_whitelist.placeholder",
                ),
            ),
        ]);
    }
    rows.extend([
        field_row(
            "tavern.config.common_backups",
            "tavern.config.common_backups.hint",
            text_control(
                "50",
                &config.common_backups,
                TextField::CommonBackups,
                false,
            ),
        ),
        field_row(
            "tavern.config.chat_backups_enabled",
            "tavern.config.chat_backups_enabled.hint",
            toggle_control(BoolField::ChatBackupsEnabled, config.chat_backups_enabled),
        ),
    ]);
    if config.chat_backups_enabled {
        rows.extend([
            field_row(
                "tavern.config.chat_backups_check_integrity",
                "tavern.config.chat_backups_check_integrity.hint",
                toggle_control(
                    BoolField::ChatBackupsCheckIntegrity,
                    config.chat_backups_check_integrity,
                ),
            ),
            field_row(
                "tavern.config.chat_max_backups",
                "tavern.config.chat_max_backups.hint",
                text_control(
                    "-1",
                    &config.chat_max_backups,
                    TextField::ChatMaxBackups,
                    false,
                ),
            ),
            field_row(
                "tavern.config.chat_throttle_interval",
                "tavern.config.chat_throttle_interval.hint",
                text_control(
                    "10000",
                    &config.chat_throttle_interval,
                    TextField::ChatThrottleInterval,
                    false,
                ),
            ),
        ]);
    }
    advanced_group(
        4,
        expanded,
        Icon::DatabaseBackup,
        Color::from_rgb8(234, 88, 12),
        "tavern.section.proxy_backup.title",
        "tavern.section.proxy_backup.description",
        section_rows(rows),
    )
}

fn thumbnail_section(config: &TavernConfig, expanded: bool) -> Element<'_, TavernMessage> {
    let mut rows = vec![field_row(
        "tavern.config.thumbnails_enabled",
        "tavern.config.thumbnails_enabled.hint",
        toggle_control(BoolField::ThumbnailsEnabled, config.thumbnails_enabled),
    )];
    if config.thumbnails_enabled {
        rows.extend([
            field_row(
                "tavern.config.thumbnail_format",
                "tavern.config.thumbnail_format.hint",
                select_control(
                    &ThumbnailFormat::ALL,
                    config.thumbnail_format,
                    TavernMessage::SelectThumbnailFormat,
                ),
            ),
            field_row(
                "tavern.config.thumbnail_quality",
                "tavern.config.thumbnail_quality.hint",
                text_control(
                    "95",
                    &config.thumbnail_quality,
                    TextField::ThumbnailQuality,
                    false,
                ),
            ),
            dimension_row(
                "tavern.config.background_size",
                &config.background_width,
                &config.background_height,
                TextField::BackgroundWidth,
                TextField::BackgroundHeight,
            ),
            dimension_row(
                "tavern.config.avatar_size",
                &config.avatar_width,
                &config.avatar_height,
                TextField::AvatarWidth,
                TextField::AvatarHeight,
            ),
            dimension_row(
                "tavern.config.persona_size",
                &config.persona_width,
                &config.persona_height,
                TextField::PersonaWidth,
                TextField::PersonaHeight,
            ),
        ]);
    }
    advanced_group(
        5,
        expanded,
        Icon::Image,
        Color::from_rgb8(219, 39, 119),
        "tavern.section.thumbnail.title",
        "tavern.section.thumbnail.description",
        section_rows(rows),
    )
}

fn performance_section(config: &TavernConfig, expanded: bool) -> Element<'_, TavernMessage> {
    advanced_group(
        6,
        expanded,
        Icon::Cpu,
        WARNING,
        "tavern.section.performance.title",
        "tavern.section.performance.description",
        section_rows(vec![
            field_row(
                "tavern.config.lazy_load_characters",
                "tavern.config.lazy_load_characters.hint",
                toggle_control(BoolField::LazyLoadCharacters, config.lazy_load_characters),
            ),
            field_row(
                "tavern.config.use_disk_cache",
                "tavern.config.use_disk_cache.hint",
                toggle_control(BoolField::UseDiskCache, config.use_disk_cache),
            ),
            field_row(
                "tavern.config.memory_cache_capacity",
                "tavern.config.memory_cache_capacity.hint",
                text_control(
                    "100",
                    &config.memory_cache_capacity,
                    TextField::MemoryCacheCapacity,
                    false,
                ),
            ),
        ]),
    )
}

fn logging_section(config: &TavernConfig, expanded: bool) -> Element<'_, TavernMessage> {
    advanced_group(
        7,
        expanded,
        Icon::Activity,
        INK_MUTED,
        "tavern.section.logging.title",
        "tavern.section.logging.description",
        section_rows(vec![
            field_row(
                "tavern.config.enable_access_log",
                "tavern.config.enable_access_log.hint",
                toggle_control(BoolField::EnableAccessLog, config.enable_access_log),
            ),
            field_row(
                "tavern.config.min_log_level",
                "tavern.config.min_log_level.hint",
                select_control(
                    &LogLevel::ALL,
                    config.min_log_level,
                    TavernMessage::SelectLogLevel,
                ),
            ),
        ]),
    )
}

fn session_security_section(config: &TavernConfig, expanded: bool) -> Element<'_, TavernMessage> {
    advanced_group(
        8,
        expanded,
        Icon::ListChecks,
        DANGER,
        "tavern.section.session_security.title",
        "tavern.section.session_security.description",
        section_rows(vec![
            field_row(
                "tavern.config.prompt_placeholder",
                "tavern.config.prompt_placeholder.hint",
                text_control(
                    "[Start a new chat]",
                    &config.prompt_placeholder,
                    TextField::PromptPlaceholder,
                    false,
                ),
            ),
            field_row(
                "tavern.config.session_timeout",
                "tavern.config.session_timeout.hint",
                text_control(
                    "-1",
                    &config.session_timeout,
                    TextField::SessionTimeout,
                    false,
                ),
            ),
            field_row(
                "tavern.config.disable_csrf_protection",
                "tavern.config.disable_csrf_protection.hint",
                toggle_control(
                    BoolField::DisableCsrfProtection,
                    config.disable_csrf_protection,
                ),
            ),
            field_row(
                "tavern.config.security_override",
                "tavern.config.security_override.hint",
                toggle_control(BoolField::SecurityOverride, config.security_override),
            ),
            field_row(
                "tavern.config.allow_keys_exposure",
                "tavern.config.allow_keys_exposure.hint",
                toggle_control(BoolField::AllowKeysExposure, config.allow_keys_exposure),
            ),
            field_row(
                "tavern.config.skip_content_check",
                "tavern.config.skip_content_check.hint",
                toggle_control(BoolField::SkipContentCheck, config.skip_content_check),
            ),
            field_row(
                "tavern.config.extensions_enabled",
                "tavern.config.extensions_enabled.hint",
                toggle_control(BoolField::ExtensionsEnabled, config.extensions_enabled),
            ),
            field_row(
                "tavern.config.extensions_auto_update",
                "tavern.config.extensions_auto_update.hint",
                toggle_control(
                    BoolField::ExtensionsAutoUpdate,
                    config.extensions_auto_update,
                ),
            ),
            field_row(
                "tavern.config.enable_server_plugins",
                "tavern.config.enable_server_plugins.hint",
                toggle_control(BoolField::EnableServerPlugins, config.enable_server_plugins),
            ),
            field_row(
                "tavern.config.enable_server_plugins_auto_update",
                "tavern.config.enable_server_plugins_auto_update.hint",
                toggle_control(
                    BoolField::EnableServerPluginsAutoUpdate,
                    config.enable_server_plugins_auto_update,
                ),
            ),
            field_row(
                "Authelia SSO",
                "tavern.config.authelia_auth.hint",
                toggle_control(BoolField::AutheliaAuth, config.authelia_auth),
            ),
            field_row(
                "Authentik SSO",
                "tavern.config.authentik_auth.hint",
                toggle_control(BoolField::AuthentikAuth, config.authentik_auth),
            ),
            field_row(
                "tavern.config.cache_buster_enabled",
                "tavern.config.cache_buster_enabled.hint",
                toggle_control(BoolField::CacheBusterEnabled, config.cache_buster_enabled),
            ),
            field_row(
                "tavern.config.cache_buster_pattern",
                "tavern.config.cache_buster_pattern.hint",
                text_control(
                    "tavern.config.cache_buster_pattern.placeholder",
                    &config.cache_buster_pattern,
                    TextField::CacheBusterPattern,
                    false,
                ),
            ),
            field_row(
                "tavern.config.enable_cors_proxy",
                "tavern.config.enable_cors_proxy.hint",
                toggle_control(BoolField::EnableCorsProxy, config.enable_cors_proxy),
            ),
            field_row(
                "tavern.config.enable_downloadable_tokenizers",
                "tavern.config.enable_downloadable_tokenizers.hint",
                toggle_control(
                    BoolField::EnableDownloadableTokenizers,
                    config.enable_downloadable_tokenizers,
                ),
            ),
        ]),
    )
}

fn status_badge(sync: &sync::SyncView) -> Element<'static, TavernMessage> {
    let (label, color) = (sync.status.label_key(), sync.status.color());
    let badge = container(
        row![
            icons::icon(
                if matches!(sync.status, sync::Status::Ready | sync::Status::Saved) {
                    Icon::CircleCheck
                } else {
                    Icon::Info
                },
                13,
                color
            ),
            text(label).size(12).font(crate::core::typography::medium()).color(color)
        ]
        .spacing(6)
        .align_y(Alignment::Center),
    )
    .padding([7, 9])
    .style(status_badge_style(color));
    let badge: Element<'static, TavernMessage> = if sync.status == sync::Status::SaveFailed {
        button(badge)
            .on_press(TavernMessage::RetryConfig)
            .padding(0)
            .style(button_style(ButtonVariant::Ghost))
            .into()
    } else {
        badge.into()
    };
    if let Some(error) = &sync.error {
        iced::widget::tooltip(
            badge,
            container(
                column![text(error.message).size(13), raw(&error.detail).size(12)].spacing(5),
            )
            .max_width(420)
            .padding(10)
            .style(config_card_style),
            iced::widget::tooltip::Position::Bottom,
        )
        .into()
    } else {
        badge
    }
}

fn header_button(
    label: &'static str,
    icon: Icon,
    action: TavernAction,
) -> Element<'static, TavernMessage> {
    button(
        row![
            crate::theme::muted_icon(icon, 14),
            text(label).size(13).font(crate::core::typography::medium())
        ]
        .spacing(6)
        .align_y(Alignment::Center),
    )
    .on_press(TavernMessage::Action(action))
    .height(36)
    .padding([7, 11])
    .style(button_style(ButtonVariant::Outline))
    .into()
}

/// 配置分组沿用旧版的独立折叠卡片交互。
fn advanced_group<'a>(
    index: usize,
    expanded: bool,
    icon: Icon,
    accent: Color,
    title: &'static str,
    description: &'static str,
    content: Element<'a, TavernMessage>,
) -> Element<'a, TavernMessage> {
    let header = button(
        row![
            container(icons::icon(icon, 18, accent))
                .width(40)
                .height(40)
                .align_x(Alignment::Center)
                .align_y(Alignment::Center)
                .style(section_icon_style(accent)),
            column![
                text(title)
                    .size(17)
                    .font(crate::core::typography::medium())
                    .style(crate::theme::text_style),
                text(description)
                    .size(13)
                    .font(crate::core::typography::regular())
                    .style(crate::theme::muted_text_style)
            ]
            .spacing(3)
            .width(Fill),
            icons::icon(
                if expanded {
                    Icon::ChevronUp
                } else {
                    Icon::ChevronDown
                },
                16,
                INK_MUTED,
            )
        ]
        .spacing(14)
        .align_y(Alignment::Center),
    )
    .on_press(TavernMessage::ToggleAdvancedSection(index))
    .width(Fill)
    .padding(16)
    .style(button_style(ButtonVariant::Ghost));

    let mut group = column![header].width(Fill);
    if expanded {
        group = group.push(crate::theme::separator()).push(content);
    }

    container(group).width(Fill).style(config_card_style).into()
}

fn section_rows<'a>(rows: Vec<Element<'a, TavernMessage>>) -> Element<'a, TavernMessage> {
    column(rows).spacing(10).padding(20).width(Fill).into()
}

fn field_row<'a>(
    title: &'static str,
    description: &'static str,
    control: Element<'a, TavernMessage>,
) -> Element<'a, TavernMessage> {
    container(
        row![
            column![
                text(title)
                    .size(15)
                    .font(crate::core::typography::medium())
                    .style(crate::theme::text_style),
                text(description)
                    .size(13)
                    .font(crate::core::typography::regular())
                    .style(crate::theme::muted_text_style)
            ]
            .spacing(3)
            .width(Fill),
            control
        ]
        .spacing(18)
        .align_y(Alignment::Center)
        .width(Fill),
    )
    .padding([13, 15])
    .style(setting_row_style)
    .width(Fill)
    .into()
}

fn dimension_row<'a>(
    title: &'static str,
    width: &'a str,
    height: &'a str,
    width_field: TextField,
    height_field: TextField,
) -> Element<'a, TavernMessage> {
    field_row(
        title,
        "tavern.config.dimension.hint",
        row![
            compact_text_control("tavern.config.dimension.width", width, width_field),
            text("×").size(14).style(crate::theme::muted_text_style),
            compact_text_control("tavern.config.dimension.height", height, height_field),
        ]
        .spacing(7)
        .align_y(Alignment::Center)
        .width(CONTROL_WIDTH)
        .into(),
    )
}

fn compact_text_control<'a>(
    placeholder: &'static str,
    value: &'a str,
    field: TextField,
) -> Element<'a, TavernMessage> {
    text_input(t(placeholder), value)
        .on_input(move |value| TavernMessage::Edit(field, value))
        .width(112)
        .padding([8, 11])
        .size(14)
        .font(crate::core::typography::regular())
        .style(text_input_style)
        .into()
}

fn toggle_control(field: BoolField, value: bool) -> Element<'static, TavernMessage> {
    crate::theme::switch("", value, move |enabled| {
        TavernMessage::Toggle(field, enabled)
    })
}

fn pill_toggle(
    label: &'static str,
    field: BoolField,
    value: bool,
) -> Element<'static, TavernMessage> {
    button(
        row![
            if value {
                icons::icon(Icon::CircleCheck, 13, WHITE)
            } else {
                crate::theme::muted_icon(Icon::Circle, 13)
            },
            text(label)
                .size(13)
                .font(crate::core::typography::medium())
                .style(move |theme| iced::widget::text::Style {
                    color: Some(if value {
                        WHITE
                    } else {
                        crate::theme::text_muted(theme)
                    }),
                }),
        ]
        .spacing(7)
        .align_y(Alignment::Center),
    )
    .on_press(TavernMessage::Toggle(field, !value))
    .height(38)
    .padding([8, 13])
    .style(pill_toggle_style(value))
    .into()
}

fn stacked_field<'a>(
    label: &'static str,
    control: Element<'a, TavernMessage>,
    help: Option<&'static str>,
) -> Element<'a, TavernMessage> {
    let mut content = column![
        text(label)
            .size(12)
            .font(crate::core::typography::medium())
            .style(crate::theme::muted_text_style),
        control,
    ]
    .spacing(7)
    .width(Fill);
    if let Some(help) = help {
        content = content.push(
            text(help)
                .size(12)
                .font(crate::core::typography::regular())
                .style(crate::theme::muted_text_style),
        );
    }
    content.into()
}

fn text_control<'a>(
    placeholder: &'static str,
    value: &'a str,
    field: TextField,
    secure: bool,
) -> Element<'a, TavernMessage> {
    let input = text_input(t(placeholder), value)
        .on_input(move |value| TavernMessage::Edit(field, value))
        .secure(secure)
        .width(CONTROL_WIDTH)
        .padding([8, 11])
        .size(14)
        .font(crate::core::typography::regular())
        .style(text_input_style)
        .into();
    sync::validated_input(
        input,
        field.key(),
        &serde_json::Value::String(value.to_owned()),
    )
}

fn wide_text_control<'a>(
    placeholder: &'static str,
    value: &'a str,
    field: TextField,
    secure: bool,
) -> Element<'a, TavernMessage> {
    let input = text_input(t(placeholder), value)
        .on_input(move |value| TavernMessage::Edit(field, value))
        .secure(secure)
        .width(Fill)
        .padding([10, 14])
        .size(15)
        .font(crate::core::typography::regular())
        .style(text_input_style)
        .into();
    sync::validated_input(
        input,
        field.key(),
        &serde_json::Value::String(value.to_owned()),
    )
}

fn select_control<'a, T: Copy + Eq + fmt::Display + 'a>(
    options: &'a [T],
    selected: T,
    on_selected: fn(T) -> TavernMessage,
) -> Element<'a, TavernMessage> {
    pick_list(options, Some(selected), on_selected)
        .width(CONTROL_WIDTH)
        .padding([8, 11])
        .text_size(12)
        .font(crate::core::typography::regular())
        .handle(pick_list_handle())
        .style(pick_list_style)
        .menu_style(pick_list_menu_style)
        .into()
}

fn wide_select_control<'a, T: Copy + Eq + fmt::Display + 'a>(
    options: &'a [T],
    selected: T,
    on_selected: fn(T) -> TavernMessage,
) -> Element<'a, TavernMessage> {
    pick_list(options, Some(selected), on_selected)
        .width(Fill)
        .padding([10, 14])
        .text_size(13)
        .font(crate::core::typography::regular())
        .handle(pick_list_handle())
        .style(pick_list_style)
        .menu_style(pick_list_menu_style)
        .into()
}

const LIST_ACTION_SIZE: f32 = 34.0;

/// 所有动态列表共用双向居中的图标按钮，避免文字图标基线导致视觉偏上。
fn list_icon_button(
    glyph: Icon,
    message: TavernMessage,
    variant: ButtonVariant,
) -> iced::widget::Button<'static, TavernMessage> {
    button(
        container(crate::theme::muted_icon(glyph, 14))
            .width(Fill)
            .height(Fill)
            .align_x(Alignment::Center)
            .align_y(Alignment::Center),
    )
    .on_press(message)
    .width(LIST_ACTION_SIZE)
    .height(LIST_ACTION_SIZE)
    .padding(0)
    .style(button_style(variant))
}

fn whitelist_is_locked(value: &str, fixed: &[String]) -> bool {
    fixed.iter().any(|item| item == value)
}

/// 系统保留地址只读且没有删除消息；用户地址继续使用当前输入框和删除样式。
fn whitelist_control<'a>(values: &'a [String], fixed: &'a [String]) -> Element<'a, TavernMessage> {
    let mut items = column![].spacing(7).width(LIST_WIDTH);
    for (index, value) in values.iter().enumerate() {
        let locked = whitelist_is_locked(value, fixed);
        let input: Element<'a, TavernMessage> = if locked {
            text_input("", value)
                .width(Fill)
                .padding([8, 11])
                .size(14)
                .font(crate::core::typography::regular())
                .style(text_input_style)
                .into()
        } else {
            text_input(t("tavern.field.ip_placeholder"), value)
                .on_input(move |value| TavernMessage::EditList(ListField::Whitelist, index, value))
                .width(Fill)
                .padding([8, 11])
                .size(14)
                .font(crate::core::typography::regular())
                .style(text_input_style)
                .into()
        };
        let trailing: Element<'a, TavernMessage> = if locked {
            iced::widget::tooltip(
                container(crate::theme::muted_icon(Icon::LockKeyhole, 14))
                    .width(LIST_ACTION_SIZE)
                    .height(LIST_ACTION_SIZE)
                    .align_x(Alignment::Center)
                    .align_y(Alignment::Center)
                    .style(fixed_whitelist_style),
                container(text("tavern.field.managed_by_service_mode").size(12))
                    .padding([6, 9])
                    .style(config_card_style),
                iced::widget::tooltip::Position::Bottom,
            )
            .into()
        } else {
            list_icon_button(
                Icon::Trash2,
                TavernMessage::RemoveListItem(ListField::Whitelist, index),
                ButtonVariant::DangerSoft,
            )
            .into()
        };
        items = items.push(row![input, trailing].spacing(7).align_y(Alignment::Center));
    }
    items = items.push(
        button(
            row![
                icons::icon(Icon::Plus, 14, BLUE_600),
                text("tavern.action.add").size(13).font(crate::core::typography::medium()).color(BLUE_600)
            ]
            .spacing(6)
            .align_y(Alignment::Center),
        )
        .on_press_maybe(
            (!values.iter().any(|value| value.trim().is_empty()))
                .then_some(TavernMessage::AddListItem(ListField::Whitelist)),
        )
        .height(LIST_ACTION_SIZE)
        .padding([7, 11])
        .style(button_style(ButtonVariant::Tertiary)),
    );
    sync::validated_input(
        items.into(),
        ListField::Whitelist.key(),
        &serde_json::Value::Array(
            values
                .iter()
                .cloned()
                .map(serde_json::Value::String)
                .collect(),
        ),
    )
}

fn list_control<'a>(
    values: &'a [String],
    field: ListField,
    placeholder: &'static str,
) -> Element<'a, TavernMessage> {
    let mut items = column![].spacing(7).width(LIST_WIDTH);
    for (index, value) in values.iter().enumerate() {
        items = items.push(
            row![
                text_input(t(placeholder), value)
                    .on_input(move |value| TavernMessage::EditList(field, index, value))
                    .width(Fill)
                    .padding([8, 11])
                    .size(14)
                    .font(crate::core::typography::regular())
                    .style(text_input_style),
                list_icon_button(
                    Icon::Trash2,
                    TavernMessage::RemoveListItem(field, index),
                    ButtonVariant::DangerSoft,
                ),
            ]
            .spacing(7)
            .align_y(Alignment::Center),
        );
    }
    items = items.push(
        button(
            row![
                icons::icon(Icon::Plus, 14, BLUE_600),
                text("tavern.action.add").size(13).font(crate::core::typography::medium()).color(BLUE_600)
            ]
            .spacing(6)
            .align_y(Alignment::Center),
        )
        .on_press(TavernMessage::AddListItem(field))
        .height(34)
        .padding([7, 11])
        .style(button_style(ButtonVariant::Tertiary)),
    );
    sync::validated_input(
        items.into(),
        field.key(),
        &serde_json::Value::Array(
            values
                .iter()
                .cloned()
                .map(serde_json::Value::String)
                .collect(),
        ),
    )
}

fn page_icon_style(_theme: &Theme) -> container::Style {
    container::Style {
        background: Some(Background::Color(BLUE_600)),
        border: Border {
            radius: 10.0.into(),
            ..Border::default()
        },
        ..container::Style::default()
    }
}

fn section_icon_style(accent: Color) -> impl Fn(&Theme) -> container::Style {
    move |_theme| container::Style {
        background: Some(Background::Color(Color::from_rgba(
            accent.r, accent.g, accent.b, 0.10,
        ))),
        border: Border {
            radius: 10.0.into(),
            ..Border::default()
        },
        ..container::Style::default()
    }
}

fn config_card_style(theme: &Theme) -> container::Style {
    container::Style {
        background: Some(Background::Color(crate::theme::surface(theme))),
        border: Border {
            color: crate::theme::line(theme),
            width: 1.0,
            radius: 14.0.into(),
        },
        ..container::Style::default()
    }
}

fn setting_row_style(theme: &Theme) -> container::Style {
    let surface_alt = crate::theme::surface_alt(theme);
    let line = crate::theme::line(theme);
    container::Style {
        background: Some(Background::Color(Color::from_rgba(
            surface_alt.r,
            surface_alt.g,
            surface_alt.b,
            0.55,
        ))),
        border: Border {
            color: Color::from_rgba(line.r, line.g, line.b, 0.85),
            width: 1.0,
            radius: 10.0.into(),
        },
        ..container::Style::default()
    }
}

fn pill_toggle_style(active: bool) -> impl Fn(&Theme, button::Status) -> button::Style {
    move |theme, status| {
        let hovered = matches!(status, button::Status::Hovered);
        let surface_alt = crate::theme::surface_alt(theme);
        let line = crate::theme::line(theme);
        let background = if active {
            theme.palette().primary
        } else if hovered {
            Color::from_rgba(
                theme.palette().primary.r,
                theme.palette().primary.g,
                theme.palette().primary.b,
                0.16,
            )
        } else {
            surface_alt
        };
        button::Style {
            background: Some(Background::Color(background)),
            text_color: if active {
                WHITE
            } else {
                crate::theme::text_muted(theme)
            },
            border: Border {
                color: if active {
                    theme.palette().primary
                } else {
                    line
                },
                width: 1.0,
                radius: 10.0.into(),
            },
            ..button::Style::default()
        }
    }
}

fn fixed_whitelist_style(theme: &Theme) -> container::Style {
    container::Style {
        background: Some(Background::Color(crate::theme::surface_alt(theme))),
        border: Border {
            color: crate::theme::line(theme),
            width: 1.0,
            radius: 10.0.into(),
        },
        ..container::Style::default()
    }
}

fn status_badge_style(color: Color) -> impl Fn(&Theme) -> container::Style {
    move |_theme| container::Style {
        background: Some(Background::Color(Color::from_rgba(
            color.r, color.g, color.b, 0.08,
        ))),
        border: Border {
            color: Color::from_rgba(color.r, color.g, color.b, 0.22),
            width: 1.0,
            radius: 7.0.into(),
        },
        ..container::Style::default()
    }
}

#[cfg(test)]
mod tests {
    use super::{
        BoolField, LIST_ACTION_SIZE, ListField, TavernMessage, TavernState, TextField,
        whitelist_is_locked,
    };

    #[test]
    fn edits_update_the_shared_configuration_draft() {
        let mut state = TavernState::default();
        state.update(TavernMessage::Edit(TextField::Port, "9000".into()));

        assert_eq!(state.config.port, "9000");
    }

    #[test]
    fn list_editing_and_defaults_are_stable() {
        let mut state = TavernState::default();
        state.update(TavernMessage::AddListItem(ListField::Whitelist));
        state.update(TavernMessage::EditList(
            ListField::Whitelist,
            2,
            "192.168.1.20".into(),
        ));
        state.update(TavernMessage::Toggle(BoolField::Listen, true));

        assert_eq!(state.config.whitelist[2], "192.168.1.20");
        assert!(state.config.listen);

        state.update(TavernMessage::RestoreDefaults);
        assert_eq!(state.config.port, "8000");
        assert!(!state.config.listen);
        assert_eq!(state.config.whitelist.len(), 2);
    }

    #[test]
    fn advanced_groups_keep_the_old_initial_expansion() {
        let mut state = TavernState::default();
        assert_eq!(
            state.advanced_expanded,
            [true, false, false, false, false, false, false, false, false]
        );

        state.update(TavernMessage::ToggleAdvancedSection(1));
        assert!(state.advanced_expanded[0]);
        assert!(state.advanced_expanded[1]);
    }

    #[test]
    fn list_actions_are_square_and_system_whitelist_entries_are_locked() {
        assert_eq!(LIST_ACTION_SIZE, 34.0);
        let fixed = vec!["::1".to_owned(), "127.0.0.1".to_owned()];
        assert!(whitelist_is_locked("::1", &fixed));
        assert!(whitelist_is_locked("127.0.0.1", &fixed));
        assert!(!whitelist_is_locked("203.0.113.9", &fixed));
    }
}

impl BoolField {
    pub(crate) const fn key(self) -> &'static str {
        match self {
            Self::Listen => "listen",
            Self::ProtocolIpv4 => "protocol_ipv4",
            Self::ProtocolIpv6 => "protocol_ipv6",
            Self::DnsPreferIpv6 => "dns_prefer_ipv6",
            Self::BrowserLaunchEnabled => "browser_launch_enabled",
            Self::BasicAuthMode => "basic_auth_mode",
            Self::EnableUserAccounts => "enable_user_accounts",
            Self::EnableDiscreetLogin => "enable_discreet_login",
            Self::PerUserBasicAuth => "per_user_basic_auth",
            Self::WhitelistMode => "whitelist_mode",
            Self::HostWhitelistEnabled => "host_whitelist_enabled",
            Self::HostWhitelistScan => "host_whitelist_scan",
            Self::SslEnabled => "ssl_enabled",
            Self::CorsEnabled => "cors_enabled",
            Self::CorsCredentials => "cors_credentials",
            Self::RequestProxyEnabled => "request_proxy_enabled",
            Self::ChatBackupsEnabled => "chat_backups_enabled",
            Self::ChatBackupsCheckIntegrity => "chat_backups_check_integrity",
            Self::ThumbnailsEnabled => "thumbnails_enabled",
            Self::LazyLoadCharacters => "lazy_load_characters",
            Self::UseDiskCache => "use_disk_cache",
            Self::EnableAccessLog => "enable_access_log",
            Self::DisableCsrfProtection => "disable_csrf_protection",
            Self::SecurityOverride => "security_override",
            Self::AllowKeysExposure => "allow_keys_exposure",
            Self::SkipContentCheck => "skip_content_check",
            Self::ExtensionsEnabled => "extensions_enabled",
            Self::ExtensionsAutoUpdate => "extensions_auto_update",
            Self::EnableServerPlugins => "enable_server_plugins",
            Self::EnableServerPluginsAutoUpdate => "enable_server_plugins_auto_update",
            Self::AutheliaAuth => "authelia_auth",
            Self::AuthentikAuth => "authentik_auth",
            Self::CacheBusterEnabled => "cache_buster_enabled",
            Self::EnableCorsProxy => "enable_cors_proxy",
            Self::EnableDownloadableTokenizers => "enable_downloadable_tokenizers",
        }
    }
}

impl TextField {
    pub(crate) const fn key(self) -> &'static str {
        match self {
            Self::Port => "port",
            Self::ListenIpv4 => "listen_ipv4",
            Self::ListenIpv6 => "listen_ipv6",
            Self::HeartbeatInterval => "heartbeat_interval",
            Self::BasicAuthUsername => "basic_auth_username",
            Self::BasicAuthPassword => "basic_auth_password",
            Self::SslCertPath => "ssl_cert_path",
            Self::SslKeyPath => "ssl_key_path",
            Self::SslKeyPassphrase => "ssl_key_passphrase",
            Self::CorsMaxAge => "cors_max_age",
            Self::RequestProxyUrl => "request_proxy_url",
            Self::CommonBackups => "common_backups",
            Self::ChatMaxBackups => "chat_max_backups",
            Self::ChatThrottleInterval => "chat_throttle_interval",
            Self::ThumbnailQuality => "thumbnail_quality",
            Self::BackgroundWidth => "background_width",
            Self::BackgroundHeight => "background_height",
            Self::AvatarWidth => "avatar_width",
            Self::AvatarHeight => "avatar_height",
            Self::PersonaWidth => "persona_width",
            Self::PersonaHeight => "persona_height",
            Self::MemoryCacheCapacity => "memory_cache_capacity",
            Self::PromptPlaceholder => "prompt_placeholder",
            Self::SessionTimeout => "session_timeout",
            Self::CacheBusterPattern => "cache_buster_pattern",
        }
    }
}

impl ListField {
    pub(crate) const fn key(self) -> &'static str {
        match self {
            Self::Whitelist => "whitelist",
            Self::HostWhitelist => "host_whitelist",
            Self::ImportDomains => "import_domains",
            Self::CorsOrigins => "cors_origins",
            Self::CorsMethods => "cors_methods",
            Self::CorsAllowedHeaders => "cors_allowed_headers",
            Self::CorsExposedHeaders => "cors_exposed_headers",
            Self::ProxyBypass => "proxy_bypass",
        }
    }
}
