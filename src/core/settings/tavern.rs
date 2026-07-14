//! 酒馆配置 (SillyTavern config.yaml) 数据结构与 YAML 读写
//!
//! 路径规则：
//! - Current 模式：酒馆实例根目录 / config.yaml
//! - Global 模式：data/sillytavern/data/config.yaml
//! - 模板：data/sillytavern/default/config.yaml

use serde::{Deserialize, Serialize};
use std::fs;
use std::io::Read;
use std::path::{Path, PathBuf};
use std::sync::mpsc::Sender;
use std::time::Duration;

use crate::utils;

// ============================================================================
// 配置下载消息
// ============================================================================

/// 模板配置下载进度消息
pub enum GenConfigMsg {
    /// 下载进度 (已下载字节, 总字节)
    Progress(u64, u64),
    /// 下载完成
    Done,
    /// 下载出错
    Error(String),
    /// 正在尝试回退到直连
    FallingBack,
}

// ============================================================================
// 子结构体
// ============================================================================

#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct ListenAddress {
    pub ipv4: String,
    pub ipv6: String,
}

impl Default for ListenAddress {
    fn default() -> Self {
        Self { ipv4: "0.0.0.0".into(), ipv6: "[::]".into() }
    }
}

#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct Protocol {
    pub ipv4: bool,
    pub ipv6: bool,
}

impl Default for Protocol {
    fn default() -> Self {
        Self { ipv4: true, ipv6: false }
    }
}

#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct BasicAuthUser {
    pub username: String,
    pub password: String,
}

impl Default for BasicAuthUser {
    fn default() -> Self {
        Self { username: "user".into(), password: "password".into() }
    }
}

#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct CorsConfig {
    pub enabled: bool,
    pub origin: Vec<String>,
    pub methods: Vec<String>,
    pub allowed_headers: Vec<String>,
    pub exposed_headers: Vec<String>,
    pub credentials: bool,
    pub max_age: Option<i64>,
}

impl Default for CorsConfig {
    fn default() -> Self {
        Self {
            enabled: true,
            origin: vec!["null".into()],
            methods: vec!["OPTIONS".into()],
            allowed_headers: vec![],
            exposed_headers: vec![],
            credentials: false,
            max_age: None,
        }
    }
}

#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct RequestProxy {
    pub enabled: bool,
    pub url: String,
    pub bypass: Vec<String>,
}

impl Default for RequestProxy {
    fn default() -> Self {
        Self { enabled: false, url: String::new(), bypass: vec![] }
    }
}

#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct BackupCommon {
    pub number_of_backups: i64,
}

impl Default for BackupCommon {
    fn default() -> Self {
        Self { number_of_backups: 50 }
    }
}

#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct ChatBackup {
    pub enabled: bool,
    pub check_integrity: bool,
    pub max_total_backups: i64,
    pub throttle_interval: i64,
}

impl Default for ChatBackup {
    fn default() -> Self {
        Self { enabled: true, check_integrity: true, max_total_backups: -1, throttle_interval: 10000 }
    }
}

#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct BackupConfig {
    pub common: BackupCommon,
    pub chat: ChatBackup,
}

impl Default for BackupConfig {
    fn default() -> Self {
        Self { common: BackupCommon::default(), chat: ChatBackup::default() }
    }
}

#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct ThumbnailDimensions {
    pub bg: Vec<u32>,
    pub avatar: Vec<u32>,
    pub persona: Vec<u32>,
}

impl Default for ThumbnailDimensions {
    fn default() -> Self {
        Self { bg: vec![160, 90], avatar: vec![96, 144], persona: vec![96, 144] }
    }
}

#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct ThumbnailConfig {
    pub enabled: bool,
    pub format: String,
    pub quality: i64,
    pub dimensions: ThumbnailDimensions,
}

impl Default for ThumbnailConfig {
    fn default() -> Self {
        Self { enabled: true, format: "jpg".into(), quality: 95, dimensions: ThumbnailDimensions::default() }
    }
}

#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct SslConfig {
    pub enabled: bool,
    pub cert_path: String,
    pub key_path: String,
    pub key_passphrase: String,
}

impl Default for SslConfig {
    fn default() -> Self {
        Self { enabled: false, cert_path: "./certs/cert.pem".into(), key_path: "./certs/privkey.pem".into(), key_passphrase: String::new() }
    }
}

#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct HostWhitelist {
    pub enabled: bool,
    pub scan: bool,
    pub hosts: Vec<String>,
}

impl Default for HostWhitelist {
    fn default() -> Self {
        Self { enabled: false, scan: true, hosts: vec![] }
    }
}

#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct LoggingConfig {
    pub enable_access_log: bool,
    pub min_log_level: i64,
}

impl Default for LoggingConfig {
    fn default() -> Self {
        Self { enable_access_log: true, min_log_level: 0 }
    }
}

#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct PerformanceConfig {
    pub lazy_load_characters: bool,
    pub memory_cache_capacity: String,
    pub use_disk_cache: bool,
}

impl Default for PerformanceConfig {
    fn default() -> Self {
        Self { lazy_load_characters: false, memory_cache_capacity: "100mb".into(), use_disk_cache: true }
    }
}

#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct CacheBusterConfig {
    pub enabled: bool,
    pub user_agent_pattern: String,
}

impl Default for CacheBusterConfig {
    fn default() -> Self {
        Self { enabled: false, user_agent_pattern: String::new() }
    }
}

#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct SsoConfig {
    pub authelia_auth: bool,
    pub authentik_auth: bool,
}

impl Default for SsoConfig {
    fn default() -> Self {
        Self { authelia_auth: false, authentik_auth: false }
    }
}

#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct ExtensionsConfig {
    pub enabled: bool,
    pub auto_update: bool,
}

impl Default for ExtensionsConfig {
    fn default() -> Self {
        Self { enabled: true, auto_update: true }
    }
}

// ============================================================================
// 根配置结构体
// ============================================================================

#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct TavernConfig {
    pub port: u16,
    pub listen: bool,
    pub listen_address: ListenAddress,
    pub protocol: Protocol,

    pub basic_auth_mode: bool,
    pub enable_user_accounts: bool,
    pub enable_discreet_login: bool,
    pub per_user_basic_auth: bool,
    pub basic_auth_user: BasicAuthUser,
    pub whitelist_mode: bool,
    pub whitelist: Vec<String>,

    pub browser_launch_enabled: bool,
    pub browser_type: String,
    pub dns_prefer_ipv6: bool,
    pub heartbeat_interval: u64,
    pub host_whitelist: HostWhitelist,
    pub whitelist_import_domains: Vec<String>,

    pub ssl: SslConfig,
    pub cors: CorsConfig,
    pub request_proxy: RequestProxy,
    pub backups: BackupConfig,
    pub thumbnails: ThumbnailConfig,
    pub logging: LoggingConfig,
    pub performance: PerformanceConfig,
    pub cache_buster: CacheBusterConfig,
    pub sso: SsoConfig,
    pub extensions: ExtensionsConfig,

    pub enable_server_plugins: bool,
    pub enable_server_plugins_auto_update: bool,
    pub enable_cors_proxy: bool,
    pub prompt_placeholder: String,
    pub enable_downloadable_tokenizers: bool,
    pub session_timeout: i64,
    pub disable_csrf_protection: bool,
    pub security_override: bool,
    pub allow_keys_exposure: bool,
    pub skip_content_check: bool,
}

impl Default for TavernConfig {
    fn default() -> Self {
        Self {
            port: 8000,
            listen: false,
            listen_address: ListenAddress::default(),
            protocol: Protocol::default(),
            basic_auth_mode: false,
            enable_user_accounts: false,
            enable_discreet_login: false,
            per_user_basic_auth: false,
            basic_auth_user: BasicAuthUser::default(),
            whitelist_mode: true,
            whitelist: vec![],
            browser_launch_enabled: true,
            browser_type: "default".into(),
            dns_prefer_ipv6: false,
            heartbeat_interval: 0,
            host_whitelist: HostWhitelist::default(),
            whitelist_import_domains: vec![],
            ssl: SslConfig::default(),
            cors: CorsConfig::default(),
            request_proxy: RequestProxy::default(),
            backups: BackupConfig::default(),
            thumbnails: ThumbnailConfig::default(),
            logging: LoggingConfig::default(),
            performance: PerformanceConfig::default(),
            cache_buster: CacheBusterConfig::default(),
            sso: SsoConfig::default(),
            extensions: ExtensionsConfig::default(),
            enable_server_plugins: false,
            enable_server_plugins_auto_update: true,
            enable_cors_proxy: false,
            prompt_placeholder: "[Start a new chat]".into(),
            enable_downloadable_tokenizers: true,
            session_timeout: -1,
            disable_csrf_protection: false,
            security_override: false,
            allow_keys_exposure: false,
            skip_content_check: false,
        }
    }
}

// ============================================================================
// YAML 辅助宏 & 函数
// ============================================================================

type YamlMap = serde_yaml::Mapping;

fn yk(s: &str) -> serde_yaml::Value {
    serde_yaml::Value::String(s.to_string())
}

fn get_bool(m: &YamlMap, k: &str, d: bool) -> bool {
    m.get(&yk(k)).and_then(|v| v.as_bool()).unwrap_or(d)
}

fn get_str(m: &YamlMap, k: &str, d: &str) -> String {
    m.get(&yk(k)).and_then(|v| v.as_str()).unwrap_or(d).to_string()
}

fn get_u16(m: &YamlMap, k: &str, d: u16) -> u16 {
    m.get(&yk(k)).and_then(|v| v.as_u64()).map(|n| n as u16).unwrap_or(d)
}

fn get_u64(m: &YamlMap, k: &str, d: u64) -> u64 {
    m.get(&yk(k)).and_then(|v| v.as_u64()).unwrap_or(d)
}

#[allow(dead_code)]
fn get_u8(m: &YamlMap, k: &str, d: u8) -> u8 {
    m.get(&yk(k)).and_then(|v| v.as_u64()).map(|n| n as u8).unwrap_or(d)
}

fn get_i64(m: &YamlMap, k: &str, d: i64) -> i64 {
    m.get(&yk(k)).and_then(|v| v.as_i64()).unwrap_or(d)
}

fn get_seq_str(m: &YamlMap, k: &str) -> Vec<String> {
    m.get(&yk(k))
        .and_then(|v| v.as_sequence())
        .map(|s| s.iter().filter_map(|v| v.as_str()).map(|s| s.to_string()).collect())
        .unwrap_or_default()
}

fn get_seq_u32(m: &YamlMap, k: &str) -> Vec<u32> {
    m.get(&yk(k))
        .and_then(|v| v.as_sequence())
        .map(|s| s.iter().filter_map(|v| v.as_u64()).map(|n| n as u32).collect())
        .unwrap_or_default()
}

fn child<'a>(m: &'a YamlMap, k: &str) -> Option<&'a YamlMap> {
    m.get(&yk(k)).and_then(|v| v.as_mapping())
}

fn child_mut<'a>(m: &'a mut YamlMap, k: &str) -> &'a mut YamlMap {
    let key = yk(k);
    if !m.contains_key(&key) {
        m.insert(key.clone(), serde_yaml::Value::Mapping(YamlMap::new()));
    }
    m.get_mut(&key).unwrap().as_mapping_mut().unwrap()
}

fn upsert(m: &mut YamlMap, k: &str, v: serde_yaml::Value) {
    m.insert(yk(k), v);
}

fn upsert_bool(m: &mut YamlMap, k: &str, v: bool) {
    upsert(m, k, serde_yaml::Value::Bool(v));
}

fn upsert_str(m: &mut YamlMap, k: &str, v: &str) {
    upsert(m, k, yk(v));
}

#[allow(dead_code)]
fn upsert_u16(m: &mut YamlMap, k: &str, v: u16) {
    upsert(m, k, serde_yaml::Value::Number((v as i64).into()));
}

fn upsert_u64(m: &mut YamlMap, k: &str, v: u64) {
    upsert(m, k, serde_yaml::Value::Number((v as i64).into()));
}

#[allow(dead_code)]
fn upsert_u8(m: &mut YamlMap, k: &str, v: u8) {
    upsert(m, k, serde_yaml::Value::Number((v as i64).into()));
}

fn upsert_i64(m: &mut YamlMap, k: &str, v: i64) {
    upsert(m, k, serde_yaml::Value::Number(v.into()));
}

fn upsert_seq_str(m: &mut YamlMap, k: &str, v: &[String]) {
    let seq: Vec<serde_yaml::Value> = v.iter().map(|s| yk(s)).collect();
    upsert(m, k, serde_yaml::Value::Sequence(seq));
}

fn upsert_seq_u32(m: &mut YamlMap, k: &str, v: &[u32]) {
    let seq: Vec<serde_yaml::Value> = v.iter().map(|&n| serde_yaml::Value::Number((n as i64).into())).collect();
    upsert(m, k, serde_yaml::Value::Sequence(seq));
}

fn upsert_nullable_i64(m: &mut YamlMap, k: &str, v: Option<i64>) {
    match v {
        Some(n) => upsert(m, k, serde_yaml::Value::Number(n.into())),
        None => upsert(m, k, serde_yaml::Value::Null),
    }
}

// ============================================================================
// YAML 解析
// ============================================================================

fn parse_listen_address(m: &YamlMap) -> ListenAddress {
    child(m, "listenAddress")
        .map(|la| ListenAddress {
            ipv4: get_str(la, "ipv4", "0.0.0.0"),
            ipv6: get_str(la, "ipv6", "[::]"),
        })
        .unwrap_or_default()
}

fn parse_protocol(m: &YamlMap) -> Protocol {
    child(m, "protocol")
        .map(|p| Protocol {
            ipv4: get_bool(p, "ipv4", true),
            ipv6: get_bool(p, "ipv6", false),
        })
        .unwrap_or_default()
}

fn parse_basic_auth_user(m: &YamlMap) -> BasicAuthUser {
    child(m, "basicAuthUser")
        .map(|bau| BasicAuthUser {
            username: get_str(bau, "username", "user"),
            password: get_str(bau, "password", "password"),
        })
        .unwrap_or_default()
}

fn parse_cors(m: &YamlMap) -> CorsConfig {
    child(m, "cors")
        .map(|c| CorsConfig {
            enabled: get_bool(c, "enabled", true),
            origin: get_seq_str(c, "origin"),
            methods: get_seq_str(c, "methods"),
            allowed_headers: get_seq_str(c, "allowedHeaders"),
            exposed_headers: get_seq_str(c, "exposedHeaders"),
            credentials: get_bool(c, "credentials", false),
            max_age: c.get(&yk("maxAge")).and_then(|v| v.as_i64()),
        })
        .unwrap_or_default()
}

fn parse_request_proxy(m: &YamlMap) -> RequestProxy {
    child(m, "requestProxy")
        .map(|rp| RequestProxy {
            enabled: get_bool(rp, "enabled", false),
            url: get_str(rp, "url", ""),
            bypass: get_seq_str(rp, "bypass"),
        })
        .unwrap_or_default()
}

fn parse_backups(m: &YamlMap) -> BackupConfig {
    child(m, "backups")
        .map(|bk| BackupConfig {
            common: child(bk, "common")
                .map(|bc| BackupCommon {
                    number_of_backups: get_i64(bc, "numberOfBackups", 50),
                })
                .unwrap_or_default(),
            chat: child(bk, "chat")
                .map(|ch| ChatBackup {
                    enabled: get_bool(ch, "enabled", true),
                    check_integrity: get_bool(ch, "checkIntegrity", true),
                    max_total_backups: get_i64(ch, "maxTotalBackups", -1),
                    throttle_interval: get_i64(ch, "throttleInterval", 10000),
                })
                .unwrap_or_default(),
        })
        .unwrap_or_default()
}

fn parse_thumbnails(m: &YamlMap) -> ThumbnailConfig {
    child(m, "thumbnails")
        .map(|th| ThumbnailConfig {
            enabled: get_bool(th, "enabled", true),
            format: get_str(th, "format", "jpg"),
            quality: get_i64(th, "quality", 95),
            dimensions: child(th, "dimensions")
                .map(|dim| ThumbnailDimensions {
                    bg: get_seq_u32(dim, "bg"),
                    avatar: get_seq_u32(dim, "avatar"),
                    persona: get_seq_u32(dim, "persona"),
                })
                .unwrap_or_default(),
        })
        .unwrap_or_default()
}

fn parse_ssl(m: &YamlMap) -> SslConfig {
    child(m, "ssl")
        .map(|s| SslConfig {
            enabled: get_bool(s, "enabled", false),
            cert_path: get_str(s, "certPath", "./certs/cert.pem"),
            key_path: get_str(s, "keyPath", "./certs/privkey.pem"),
            key_passphrase: get_str(s, "keyPassphrase", ""),
        })
        .unwrap_or_default()
}

fn parse_host_whitelist(m: &YamlMap) -> HostWhitelist {
    child(m, "hostWhitelist")
        .map(|hw| HostWhitelist {
            enabled: get_bool(hw, "enabled", false),
            scan: get_bool(hw, "scan", true),
            hosts: get_seq_str(hw, "hosts"),
        })
        .unwrap_or_default()
}

fn parse_logging(m: &YamlMap) -> LoggingConfig {
    child(m, "logging")
        .map(|l| LoggingConfig {
            enable_access_log: get_bool(l, "enableAccessLog", true),
            min_log_level: get_i64(l, "minLogLevel", 0),
        })
        .unwrap_or_default()
}

fn parse_performance(m: &YamlMap) -> PerformanceConfig {
    child(m, "performance")
        .map(|perf| PerformanceConfig {
            lazy_load_characters: get_bool(perf, "lazyLoadCharacters", false),
            memory_cache_capacity: get_str(perf, "memoryCacheCapacity", "100mb"),
            use_disk_cache: get_bool(perf, "useDiskCache", true),
        })
        .unwrap_or_default()
}

fn parse_cache_buster(m: &YamlMap) -> CacheBusterConfig {
    child(m, "cacheBuster")
        .map(|cb| CacheBusterConfig {
            enabled: get_bool(cb, "enabled", false),
            user_agent_pattern: get_str(cb, "userAgentPattern", ""),
        })
        .unwrap_or_default()
}

fn parse_sso(m: &YamlMap) -> SsoConfig {
    child(m, "sso")
        .map(|s| SsoConfig {
            authelia_auth: get_bool(s, "autheliaAuth", false),
            authentik_auth: get_bool(s, "authentikAuth", false),
        })
        .unwrap_or_default()
}

fn parse_extensions(m: &YamlMap) -> ExtensionsConfig {
    child(m, "extensions")
        .map(|ext| ExtensionsConfig {
            enabled: get_bool(ext, "enabled", true),
            auto_update: get_bool(ext, "autoUpdate", true),
        })
        .unwrap_or_default()
}

fn parse_browser_launch(m: &YamlMap) -> (bool, String) {
    child(m, "browserLaunch")
        .map(|bl| {
            (get_bool(bl, "enabled", true), get_str(bl, "browser", "default"))
        })
        .unwrap_or((true, "default".into()))
}

impl TavernConfig {
    /// 所有可能被系统保留的白名单 IP（跨所有模式）
    /// 匹配这些 IP 的条目由系统管理，模式切换时自动移除旧模式专属条目
    pub fn all_reserved_whitelist_ips() -> Vec<String> {
        vec![
            "::1".into(),
            "127.0.0.1".into(),
            "10.0.0.0/8".into(),
            "172.16.0.0/12".into(),
            "192.168.0.0/16".into(),
            "0.0.0.0/0".into(),
            "::/0".into(),
        ]
    }

    /// 获取根据服务器模式/服务模式应写死的白名单 IP 列表
    /// - 服务器关闭 → 本机回环
    /// - 局域网模式 → 本机 + 内网段
    /// - 互联网模式 → 全网放通
    pub fn fixed_whitelist(server_mode_enabled: bool, service_mode: &str) -> Vec<String> {
        if !server_mode_enabled {
            // 服务器关闭：只锁定本机回环
            return vec!["::1".into(), "127.0.0.1".into()];
        }
        match service_mode {
            "Internet" => vec![
                "::1".into(),
                "127.0.0.1".into(),
                "0.0.0.0/0".into(),
                "::/0".into(),
            ],
            _ => vec![
                "::1".into(),
                "127.0.0.1".into(),
                "10.0.0.0/8".into(),
                "172.16.0.0/12".into(),
                "192.168.0.0/16".into(),
            ],
        }
    }

    /// 从 YAML 字符串解析
    pub fn from_yaml(yaml_str: &str) -> Option<Self> {
        // serde_yaml 0.9 不支持 !tag:yaml.org,2002:null 写法，预处理替换为 plain null
        let sanitized = yaml_str.replace("!tag:yaml.org,2002:null", "");
        let root: serde_yaml::Value = serde_yaml::from_str(&sanitized).ok()?;
        let m = root.as_mapping()?;

        let (browser_launch_enabled, browser_type) = parse_browser_launch(m);

        Some(Self {
            port: get_u16(m, "port", 8000),
            listen: get_bool(m, "listen", false),
            listen_address: parse_listen_address(m),
            protocol: parse_protocol(m),
            basic_auth_mode: get_bool(m, "basicAuthMode", false),
            enable_user_accounts: get_bool(m, "enableUserAccounts", false),
            enable_discreet_login: get_bool(m, "enableDiscreetLogin", false),
            per_user_basic_auth: get_bool(m, "perUserBasicAuth", false),
            basic_auth_user: parse_basic_auth_user(m),
            whitelist_mode: get_bool(m, "whitelistMode", true),
            whitelist: get_seq_str(m, "whitelist"),
            browser_launch_enabled,
            browser_type,
            dns_prefer_ipv6: get_bool(m, "dnsPreferIPv6", false),
            heartbeat_interval: get_u64(m, "heartbeatInterval", 0),
            host_whitelist: parse_host_whitelist(m),
            whitelist_import_domains: get_seq_str(m, "whitelistImportDomains"),
            ssl: parse_ssl(m),
            cors: parse_cors(m),
            request_proxy: parse_request_proxy(m),
            backups: parse_backups(m),
            thumbnails: parse_thumbnails(m),
            logging: parse_logging(m),
            performance: parse_performance(m),
            cache_buster: parse_cache_buster(m),
            sso: parse_sso(m),
            extensions: parse_extensions(m),
            enable_server_plugins: get_bool(m, "enableServerPlugins", false),
            enable_server_plugins_auto_update: get_bool(m, "enableServerPluginsAutoUpdate", true),
            enable_cors_proxy: get_bool(m, "enableCorsProxy", false),
            prompt_placeholder: get_str(m, "promptPlaceholder", "[Start a new chat]"),
            enable_downloadable_tokenizers: get_bool(m, "enableDownloadableTokenizers", true),
            session_timeout: get_i64(m, "sessionTimeout", -1),
            disable_csrf_protection: get_bool(m, "disableCsrfProtection", false),
            security_override: get_bool(m, "securityOverride", false),
            allow_keys_exposure: get_bool(m, "allowKeysExposure", false),
            skip_content_check: get_bool(m, "skipContentCheck", false),
        })
    }

    /// 将配置 upsert 到 YAML Mapping 中（保留未知字段）
    fn upsert_to_yaml(&self, m: &mut YamlMap) {
        upsert_u16(m, "port", self.port);
        upsert_bool(m, "listen", self.listen);

        // listenAddress
        {
            let la = child_mut(m, "listenAddress");
            upsert_str(la, "ipv4", &self.listen_address.ipv4);
            upsert_str(la, "ipv6", &self.listen_address.ipv6);
        }

        // protocol
        {
            let p = child_mut(m, "protocol");
            upsert_bool(p, "ipv4", self.protocol.ipv4);
            upsert_bool(p, "ipv6", self.protocol.ipv6);
        }

        upsert_bool(m, "basicAuthMode", self.basic_auth_mode);
        upsert_bool(m, "enableUserAccounts", self.enable_user_accounts);
        upsert_bool(m, "enableDiscreetLogin", self.enable_discreet_login);
        upsert_bool(m, "perUserBasicAuth", self.per_user_basic_auth);

        // basicAuthUser
        {
            let bau = child_mut(m, "basicAuthUser");
            upsert_str(bau, "username", &self.basic_auth_user.username);
            upsert_str(bau, "password", &self.basic_auth_user.password);
        }

        upsert_bool(m, "whitelistMode", self.whitelist_mode);
        upsert_seq_str(m, "whitelist", &self.whitelist);

        // browserLaunch
        {
            let bl = child_mut(m, "browserLaunch");
            upsert_bool(bl, "enabled", self.browser_launch_enabled);
            upsert_str(bl, "browser", &self.browser_type);
        }

        upsert_bool(m, "dnsPreferIPv6", self.dns_prefer_ipv6);
        upsert_u64(m, "heartbeatInterval", self.heartbeat_interval);

        // hostWhitelist
        {
            let hw = child_mut(m, "hostWhitelist");
            upsert_bool(hw, "enabled", self.host_whitelist.enabled);
            upsert_bool(hw, "scan", self.host_whitelist.scan);
            upsert_seq_str(hw, "hosts", &self.host_whitelist.hosts);
        }

        upsert_seq_str(m, "whitelistImportDomains", &self.whitelist_import_domains);

        // ssl
        {
            let s = child_mut(m, "ssl");
            upsert_bool(s, "enabled", self.ssl.enabled);
            upsert_str(s, "certPath", &self.ssl.cert_path);
            upsert_str(s, "keyPath", &self.ssl.key_path);
            upsert_str(s, "keyPassphrase", &self.ssl.key_passphrase);
        }

        // cors
        {
            let c = child_mut(m, "cors");
            upsert_bool(c, "enabled", self.cors.enabled);
            upsert_seq_str(c, "origin", &self.cors.origin);
            upsert_seq_str(c, "methods", &self.cors.methods);
            upsert_seq_str(c, "allowedHeaders", &self.cors.allowed_headers);
            upsert_seq_str(c, "exposedHeaders", &self.cors.exposed_headers);
            upsert_bool(c, "credentials", self.cors.credentials);
            upsert_nullable_i64(c, "maxAge", self.cors.max_age);
        }

        // requestProxy
        {
            let rp = child_mut(m, "requestProxy");
            upsert_bool(rp, "enabled", self.request_proxy.enabled);
            upsert_str(rp, "url", &self.request_proxy.url);
            upsert_seq_str(rp, "bypass", &self.request_proxy.bypass);
        }

        // backups
        {
            let bk = child_mut(m, "backups");
            {
                let bc = child_mut(bk, "common");
                upsert_i64(bc, "numberOfBackups", self.backups.common.number_of_backups);
            }
            {
                let ch = child_mut(bk, "chat");
                upsert_bool(ch, "enabled", self.backups.chat.enabled);
                upsert_bool(ch, "checkIntegrity", self.backups.chat.check_integrity);
                upsert_i64(ch, "maxTotalBackups", self.backups.chat.max_total_backups);
                upsert_i64(ch, "throttleInterval", self.backups.chat.throttle_interval);
            }
        }

        // thumbnails
        {
            let th = child_mut(m, "thumbnails");
            upsert_bool(th, "enabled", self.thumbnails.enabled);
            upsert_str(th, "format", &self.thumbnails.format);
            upsert_i64(th, "quality", self.thumbnails.quality);
            {
                let dim = child_mut(th, "dimensions");
                upsert_seq_u32(dim, "bg", &self.thumbnails.dimensions.bg);
                upsert_seq_u32(dim, "avatar", &self.thumbnails.dimensions.avatar);
                upsert_seq_u32(dim, "persona", &self.thumbnails.dimensions.persona);
            }
        }

        // logging
        {
            let l = child_mut(m, "logging");
            upsert_bool(l, "enableAccessLog", self.logging.enable_access_log);
            upsert_i64(l, "minLogLevel", self.logging.min_log_level);
        }

        // performance
        {
            let perf = child_mut(m, "performance");
            upsert_bool(perf, "lazyLoadCharacters", self.performance.lazy_load_characters);
            upsert_str(perf, "memoryCacheCapacity", &self.performance.memory_cache_capacity);
            upsert_bool(perf, "useDiskCache", self.performance.use_disk_cache);
        }

        // cacheBuster
        {
            let cb = child_mut(m, "cacheBuster");
            upsert_bool(cb, "enabled", self.cache_buster.enabled);
            upsert_str(cb, "userAgentPattern", &self.cache_buster.user_agent_pattern);
        }

        // sso
        {
            let s = child_mut(m, "sso");
            upsert_bool(s, "autheliaAuth", self.sso.authelia_auth);
            upsert_bool(s, "authentikAuth", self.sso.authentik_auth);
        }

        // extensions
        {
            let ext = child_mut(m, "extensions");
            upsert_bool(ext, "enabled", self.extensions.enabled);
            upsert_bool(ext, "autoUpdate", self.extensions.auto_update);
        }

        upsert_bool(m, "enableServerPlugins", self.enable_server_plugins);
        upsert_bool(m, "enableServerPluginsAutoUpdate", self.enable_server_plugins_auto_update);
        upsert_bool(m, "enableCorsProxy", self.enable_cors_proxy);
        upsert_str(m, "promptPlaceholder", &self.prompt_placeholder);
        upsert_bool(m, "enableDownloadableTokenizers", self.enable_downloadable_tokenizers);
        upsert_i64(m, "sessionTimeout", self.session_timeout);
        upsert_bool(m, "disableCsrfProtection", self.disable_csrf_protection);
        upsert_bool(m, "securityOverride", self.security_override);
        upsert_bool(m, "allowKeysExposure", self.allow_keys_exposure);
        upsert_bool(m, "skipContentCheck", self.skip_content_check);
    }
}

// ============================================================================
// 文件路径 & 持久化
// ============================================================================

/// 数据模式枚举（与 SettingsState 中保持一致）
#[derive(Clone, Copy, PartialEq)]
pub enum ConfigMode {
    Current,
    Global,
}

/// 实例信息（用于路径解析）
#[derive(Clone)]
pub struct InstanceInfo {
    pub instance_type: String, // "builtin" or "local"
    pub path: Option<String>,
}

impl TavernConfig {
    /// 获取 builtin 酒馆实例路径
    #[allow(dead_code)]
    pub fn builtin_instance_path() -> PathBuf {
        utils::app_paths().sillytavern_dir()
    }

    /// 解析全局数据根目录。
    ///
    /// 优先使用设置页中的自定义目录；如果未设置或值为空，则回退到程序默认目录。
    pub fn resolve_global_data_dir(global_data_path: Option<&str>) -> PathBuf {
        if let Some(custom) = global_data_path
            .map(str::trim)
            .filter(|path| !path.is_empty())
        {
            PathBuf::from(custom)
        } else {
            utils::app_paths().default_global_data_dir()
        }
    }

    /// 根据数据模式和实例信息解析 config.yaml 路径
    pub fn resolve_path(mode: ConfigMode, instance: Option<&InstanceInfo>, global_data_path: Option<&str>) -> PathBuf {
        let paths = utils::app_paths();
        match mode {
            ConfigMode::Current => {
                if let Some(inst) = instance {
                    match inst.instance_type.as_str() {
                        "local" => {
                            if let Some(ref p) = inst.path {
                                return PathBuf::from(p).join("config.yaml");
                            }
                        }
                        _ => {} // builtin → use default
                    }
                }
                paths.tavern_config_file()
            }
            ConfigMode::Global => Self::resolve_global_data_dir(global_data_path).join("config.yaml"),
        }
    }

    /// 默认模板文件路径（用于生成配置）
    pub fn template_path() -> PathBuf {
        utils::app_paths().tavern_template_file()
    }

    /// 检查配置文件是否存在
    pub fn config_exists(mode: ConfigMode, instance: Option<&InstanceInfo>, global_data_path: Option<&str>) -> bool {
        Self::resolve_path(mode, instance, global_data_path).exists()
    }

    /// 从 YAML 文件加载配置
    pub fn load_from_yaml(mode: ConfigMode, instance: Option<&InstanceInfo>, global_data_path: Option<&str>) -> Option<Self> {
        let path = Self::resolve_path(mode, instance, global_data_path);
        let content = fs::read_to_string(&path).ok()?;
        Self::from_yaml(&content)
    }

    /// 保存配置到 YAML 文件（保留未知字段）
    pub fn save_to_yaml(&self, mode: ConfigMode, instance: Option<&InstanceInfo>, global_data_path: Option<&str>) -> bool {
        let path = Self::resolve_path(mode, instance, global_data_path);
        if let Some(parent) = path.parent() {
            let _ = fs::create_dir_all(parent);
        }

        // 尝试读取现有文件以保留未知字段
        let mut root: serde_yaml::Value = match fs::read_to_string(&path) {
            Ok(content) => serde_yaml::from_str(&content).unwrap_or(serde_yaml::Value::Mapping(YamlMap::new())),
            Err(_) => serde_yaml::Value::Mapping(YamlMap::new()),
        };

        let m = root.as_mapping_mut().unwrap();
        self.upsert_to_yaml(m);

        match serde_yaml::to_string(&root) {
            Ok(yaml_str) => {
                if let Err(e) = fs::write(&path, yaml_str) {
                    eprintln!("[tavern_config] 写入 YAML 失败: {}", e);
                    false
                } else {
                    true
                }
            }
            Err(e) => {
                eprintln!("[tavern_config] 序列化 YAML 失败: {}", e);
                false
            }
        }
    }

    /// 从模板文件生成目标配置文件
    #[allow(dead_code)]
    pub fn generate_from_template(target_path: &Path) -> bool {
        let template = Self::template_path();
        Self::copy_template_to(&template, target_path)
    }

    /// 从指定模板路径复制配置文件到目标
    pub fn copy_template_to(template_path: &Path, target_path: &Path) -> bool {
        if !template_path.exists() {
            eprintln!("[tavern_config] 模板文件不存在: {:?}", template_path);
            return false;
        }
        if let Some(parent) = target_path.parent() {
            let _ = fs::create_dir_all(parent);
        }
        match fs::copy(template_path, target_path) {
            Ok(_) => true,
            Err(e) => {
                eprintln!("[tavern_config] 复制模板失败: {}", e);
                false
            }
        }
    }

    /// 生成配置后自动优化：打开局域网访问 / 启用IPv6 / 端口改为 11451
    /// 直接操作 YAML 文件，无需经过 TavernConfig 结构体
    pub fn optimize_after_generate(path: &Path) {
        let content = match fs::read_to_string(path) {
            Ok(c) => c,
            Err(e) => {
                eprintln!("[tavern_config] optimize: 读取文件失败: {}", e);
                return;
            }
        };

        let mut root: serde_yaml::Value = match serde_yaml::from_str(&content) {
            Ok(v) => v,
            Err(e) => {
                eprintln!("[tavern_config] optimize: 解析 YAML 失败: {}", e);
                return;
            }
        };

        let m = match root.as_mapping_mut() {
            Some(m) => m,
            None => return,
        };

        // 端口改为 11451
        upsert_u16(m, "port", 11451);
        // 允许局域网访问
        upsert_bool(m, "listen", true);

        // protocol.ipv6 = true
        {
            let p = child_mut(m, "protocol");
            upsert_bool(p, "ipv6", true);
        }

        let yaml_str = match serde_yaml::to_string(&root) {
            Ok(s) => s,
            Err(e) => {
                eprintln!("[tavern_config] optimize: 序列化 YAML 失败: {}", e);
                return;
            }
        };

        if let Err(e) = fs::write(path, yaml_str) {
            eprintln!("[tavern_config] optimize: 写入文件失败: {}", e);
        }
    }

    /// 在后台线程中下载默认模板配置文件
    /// target_path: 目标配置文件路径
    /// template_path: 默认模板缓存路径（下载后同时保存一份到此，供后续"恢复默认"使用）
    pub fn start_download_template(
        target_path: PathBuf,
        template_path: PathBuf,
        proxy_enabled: bool,
        proxy_url: String,
        tx: Sender<GenConfigMsg>,
    ) {
        std::thread::spawn(move || {
            let direct_url = "https://raw.githubusercontent.com/SillyTavern/SillyTavern/refs/heads/release/default/config.yaml";

            // 确保目标目录存在
            if let Some(parent) = target_path.parent() {
                let _ = fs::create_dir_all(parent);
            }
            // 确保模板目录存在
            if let Some(parent) = template_path.parent() {
                let _ = fs::create_dir_all(parent);
            }

            // 构建 URL 列表：代理在前，直连在后
            let urls: Vec<String> = if proxy_enabled && !proxy_url.is_empty() {
                let proxy_clean = proxy_url.trim_end_matches('/');
                vec![
                    format!("{}/{}", proxy_clean, direct_url),
                    direct_url.to_string(),
                ]
            } else {
                vec![direct_url.to_string()]
            };

            for (i, url) in urls.iter().enumerate() {
                if i > 0 {
                    let _ = tx.send(GenConfigMsg::FallingBack);
                }
                match Self::download_single(&url, &target_path, &tx) {
                    Ok(()) => {
                        // 生成后自动优化：打开局域网/IPv6，端口 11451
                        Self::optimize_after_generate(&target_path);
                        // 同时保存一份到模板目录，供后续"恢复默认"使用
                        let _ = fs::copy(&target_path, &template_path);
                        let _ = tx.send(GenConfigMsg::Done);
                        return;
                    }
                    Err(e) => {
                        eprintln!("[tavern_config] 下载失败 ({}): {}", url, e);
                    }
                }
            }

            let _ = tx.send(GenConfigMsg::Error("所有下载地址均失败".to_string()));
        });
    }

    /// 从单个 URL 下载配置文件，实时报告进度
    fn download_single(
        url: &str,
        target_path: &Path,
        tx: &Sender<GenConfigMsg>,
    ) -> Result<(), String> {
        let client = reqwest::blocking::Client::builder()
            .timeout(Duration::from_secs(15))
            .build()
            .map_err(|e| format!("创建 HTTP 客户端失败: {}", e))?;

        let mut resp = client
            .get(url)
            .header("User-Agent", "AstraBrew-Launcher/0.1.0")
            .send()
            .map_err(|e| format!("请求失败: {}", e))?;

        let status = resp.status();
        if !status.is_success() {
            return Err(format!("HTTP {}", status));
        }

        let total = resp.content_length().unwrap_or(0);
        let mut downloaded: u64 = 0;
        let mut data = Vec::new();
        let mut buf = [0u8; 8192];

        loop {
            let n = resp
                .read(&mut buf)
                .map_err(|e| format!("读取响应失败: {}", e))?;
            if n == 0 {
                break;
            }
            data.extend_from_slice(&buf[..n]);
            downloaded += n as u64;
            let _ = tx.send(GenConfigMsg::Progress(downloaded, total));
        }

        fs::write(target_path, &data)
            .map_err(|e| format!("写入文件失败: {}", e))?;

        Ok(())
    }
}
