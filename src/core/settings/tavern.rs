//! 酒馆配置 (SillyTavern config.yaml) 数据结构与持久化
//! 
//! 对应原 Vue 项目中 TavernConfigPayload 的数据结构。
//! 配置保存为 JSON 格式到 data/tavern_config.json。

use serde::{Deserialize, Serialize};
use std::fs;
use std::path::PathBuf;

/// 监听地址
#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct ListenAddress {
    pub ipv4: String,
    pub ipv6: String,
}

impl Default for ListenAddress {
    fn default() -> Self {
        Self {
            ipv4: "0.0.0.0".to_string(),
            ipv6: "[::]".to_string(),
        }
    }
}

/// 协议开关
#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct Protocol {
    #[serde(default = "default_true")]
    pub ipv4: bool,
    #[serde(default)]
    pub ipv6: bool,
}

impl Default for Protocol {
    fn default() -> Self {
        Self { ipv4: true, ipv6: false }
    }
}

fn default_true() -> bool { true }

/// 基础认证用户
#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct BasicAuthUser {
    pub username: String,
    pub password: String,
}

impl Default for BasicAuthUser {
    fn default() -> Self {
        Self {
            username: "user".to_string(),
            password: "password".to_string(),
        }
    }
}

/// CORS 跨域配置
#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct CorsConfig {
    #[serde(default = "default_true")]
    pub enabled: bool,
    #[serde(default = "default_null_origin")]
    pub origin: Vec<String>,
    #[serde(default = "default_options_method")]
    pub methods: Vec<String>,
    #[serde(default)]
    pub allowed_headers: Vec<String>,
    #[serde(default)]
    pub exposed_headers: Vec<String>,
    #[serde(default)]
    pub credentials: bool,
    #[serde(default)]
    pub max_age: Option<u32>,
}

fn default_null_origin() -> Vec<String> { vec!["null".to_string()] }
fn default_options_method() -> Vec<String> { vec!["OPTIONS".to_string()] }

impl Default for CorsConfig {
    fn default() -> Self {
        Self {
            enabled: true,
            origin: default_null_origin(),
            methods: default_options_method(),
            allowed_headers: vec![],
            exposed_headers: vec![],
            credentials: false,
            max_age: None,
        }
    }
}

/// 请求代理配置
#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct RequestProxy {
    #[serde(default)]
    pub enabled: bool,
    #[serde(default)]
    pub url: String,
    #[serde(default)]
    pub bypass: Vec<String>,
}

impl Default for RequestProxy {
    fn default() -> Self {
        Self { enabled: false, url: String::new(), bypass: vec![] }
    }
}

/// 通用备份设置
#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct BackupCommon {
    #[serde(default = "default_backups_count")]
    pub number_of_backups: u32,
}

fn default_backups_count() -> u32 { 50 }

impl Default for BackupCommon {
    fn default() -> Self {
        Self { number_of_backups: default_backups_count() }
    }
}

/// 聊天备份设置
#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct ChatBackup {
    #[serde(default = "default_true")]
    pub enabled: bool,
    #[serde(default = "default_true")]
    pub check_integrity: bool,
    #[serde(default = "default_backups_unlimited")]
    pub max_total_backups: i32,
    #[serde(default = "default_throttle_interval")]
    pub throttle_interval: u64,
}

fn default_backups_unlimited() -> i32 { -1 }
fn default_throttle_interval() -> u64 { 10000 }

impl Default for ChatBackup {
    fn default() -> Self {
        Self {
            enabled: true,
            check_integrity: true,
            max_total_backups: default_backups_unlimited(),
            throttle_interval: default_throttle_interval(),
        }
    }
}

/// 备份总配置
#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct BackupConfig {
    pub common: BackupCommon,
    pub chat: ChatBackup,
}

impl Default for BackupConfig {
    fn default() -> Self {
        Self {
            common: BackupCommon::default(),
            chat: ChatBackup::default(),
        }
    }
}

/// 缩略图尺寸
#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct ThumbnailDimensions {
    #[serde(default = "default_bg_dims")]
    pub bg: Vec<u32>,
    #[serde(default = "default_avatar_dims")]
    pub avatar: Vec<u32>,
    #[serde(default = "default_persona_dims")]
    pub persona: Vec<u32>,
}

fn default_bg_dims() -> Vec<u32> { vec![160, 90] }
fn default_avatar_dims() -> Vec<u32> { vec![96, 144] }
fn default_persona_dims() -> Vec<u32> { vec![96, 144] }

impl Default for ThumbnailDimensions {
    fn default() -> Self {
        Self {
            bg: default_bg_dims(),
            avatar: default_avatar_dims(),
            persona: default_persona_dims(),
        }
    }
}

/// 缩略图配置
#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct ThumbnailConfig {
    #[serde(default = "default_true")]
    pub enabled: bool,
    #[serde(default = "default_thumb_format")]
    pub format: String,
    #[serde(default = "default_thumb_quality")]
    pub quality: u8,
    pub dimensions: ThumbnailDimensions,
}

fn default_thumb_format() -> String { "jpg".to_string() }
fn default_thumb_quality() -> u8 { 95 }

impl Default for ThumbnailConfig {
    fn default() -> Self {
        Self {
            enabled: true,
            format: default_thumb_format(),
            quality: default_thumb_quality(),
            dimensions: ThumbnailDimensions::default(),
        }
    }
}

/// SSL/TLS 证书配置
#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct SslConfig {
    #[serde(default)]
    pub enabled: bool,
    #[serde(default = "default_cert_path")]
    pub cert_path: String,
    #[serde(default = "default_key_path")]
    pub key_path: String,
    #[serde(default)]
    pub key_passphrase: String,
}

fn default_cert_path() -> String { "./certs/cert.pem".to_string() }
fn default_key_path() -> String { "./certs/privkey.pem".to_string() }

impl Default for SslConfig {
    fn default() -> Self {
        Self {
            enabled: false,
            cert_path: default_cert_path(),
            key_path: default_key_path(),
            key_passphrase: String::new(),
        }
    }
}

/// 主机白名单
#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct HostWhitelist {
    #[serde(default)]
    pub enabled: bool,
    #[serde(default = "default_true")]
    pub scan: bool,
    #[serde(default)]
    pub hosts: Vec<String>,
}

impl Default for HostWhitelist {
    fn default() -> Self {
        Self { enabled: false, scan: true, hosts: vec![] }
    }
}

/// 日志配置
#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct LoggingConfig {
    #[serde(default = "default_true")]
    pub enable_access_log: bool,
    #[serde(default)]
    pub min_log_level: u8,
}

impl Default for LoggingConfig {
    fn default() -> Self {
        Self { enable_access_log: true, min_log_level: 0 }
    }
}

/// 性能优化配置
#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct PerformanceConfig {
    #[serde(default)]
    pub lazy_load_characters: bool,
    #[serde(default = "default_memory_cache")]
    pub memory_cache_capacity: String,
    #[serde(default = "default_true")]
    pub use_disk_cache: bool,
}

fn default_memory_cache() -> String { "100mb".to_string() }

impl Default for PerformanceConfig {
    fn default() -> Self {
        Self {
            lazy_load_characters: false,
            memory_cache_capacity: default_memory_cache(),
            use_disk_cache: true,
        }
    }
}

/// 缓存破坏器配置
#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct CacheBusterConfig {
    #[serde(default)]
    pub enabled: bool,
    #[serde(default)]
    pub user_agent_pattern: String,
}

impl Default for CacheBusterConfig {
    fn default() -> Self {
        Self { enabled: false, user_agent_pattern: String::new() }
    }
}

/// SSO 单点登录
#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct SsoConfig {
    #[serde(default)]
    pub authelia_auth: bool,
    #[serde(default)]
    pub authentik_auth: bool,
}

impl Default for SsoConfig {
    fn default() -> Self {
        Self { authelia_auth: false, authentik_auth: false }
    }
}

/// 扩展配置
#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct ExtensionsConfig {
    #[serde(default = "default_true")]
    pub enabled: bool,
    #[serde(default = "default_true")]
    pub auto_update: bool,
}

impl Default for ExtensionsConfig {
    fn default() -> Self {
        Self { enabled: true, auto_update: true }
    }
}

// ---------------------------------------------------------------------------
// 根配置结构体
// ---------------------------------------------------------------------------
/// SillyTavern 酒馆全局配置
#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct TavernConfig {
    // 网络基础
    #[serde(default = "default_port")]
    pub port: u16,
    #[serde(default)]
    pub listen: bool,
    pub listen_address: ListenAddress,
    pub protocol: Protocol,

    // 安全与账户
    #[serde(default)]
    pub basic_auth_mode: bool,
    #[serde(default)]
    pub enable_user_accounts: bool,
    #[serde(default)]
    pub enable_discreet_login: bool,
    #[serde(default)]
    pub per_user_basic_auth: bool,
    pub basic_auth_user: BasicAuthUser,
    #[serde(default = "default_true")]
    pub whitelist_mode: bool,
    #[serde(default)]
    pub whitelist: Vec<String>,

    // 网络高级
    #[serde(default = "default_true")]
    pub browser_launch_enabled: bool,
    #[serde(default = "default_browser_type")]
    pub browser_type: String,
    #[serde(default)]
    pub dns_prefer_ipv6: bool,
    #[serde(default)]
    pub heartbeat_interval: u64,
    pub host_whitelist: HostWhitelist,
    #[serde(default)]
    pub whitelist_import_domains: Vec<String>,

    // SSL
    pub ssl: SslConfig,

    // CORS
    pub cors: CorsConfig,

    // 代理
    pub request_proxy: RequestProxy,

    // 备份
    pub backups: BackupConfig,

    // 缩略图
    pub thumbnails: ThumbnailConfig,

    // 日志
    pub logging: LoggingConfig,

    // 性能
    pub performance: PerformanceConfig,

    // 缓存破坏器
    pub cache_buster: CacheBusterConfig,

    // SSO
    pub sso: SsoConfig,

    // 扩展
    pub extensions: ExtensionsConfig,

    // 服务器插件
    #[serde(default)]
    pub enable_server_plugins: bool,
    #[serde(default = "default_true")]
    pub enable_server_plugins_auto_update: bool,

    // 其他
    #[serde(default)]
    pub enable_cors_proxy: bool,
    #[serde(default = "default_prompt_placeholder")]
    pub prompt_placeholder: String,
    #[serde(default = "default_true")]
    pub enable_downloadable_tokenizers: bool,

    // 会话与安全
    #[serde(default = "default_session_timeout")]
    pub session_timeout: i64,
    #[serde(default)]
    pub disable_csrf_protection: bool,
    #[serde(default)]
    pub security_override: bool,
    #[serde(default)]
    pub allow_keys_exposure: bool,
    #[serde(default)]
    pub skip_content_check: bool,
}

fn default_port() -> u16 { 8000 }
fn default_browser_type() -> String { "default".to_string() }
fn default_prompt_placeholder() -> String { "[Start a new chat]".to_string() }
fn default_session_timeout() -> i64 { -1 }

impl Default for TavernConfig {
    fn default() -> Self {
        Self {
            port: default_port(),
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
            browser_type: default_browser_type(),
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
            prompt_placeholder: default_prompt_placeholder(),
            enable_downloadable_tokenizers: true,
            session_timeout: default_session_timeout(),
            disable_csrf_protection: false,
            security_override: false,
            allow_keys_exposure: false,
            skip_content_check: false,
        }
    }
}

impl TavernConfig {
    /// 获取配置文件路径
    pub fn config_path() -> PathBuf {
        let mut path = if cfg!(debug_assertions) {
            // 开发模式: 优先从项目根目录 data/ 读取，fallback 到 target/debug/data/
            let project_root = std::env::current_dir().unwrap_or_default();
            let project_data = project_root.join("data/tavern_config.json");
            if project_data.exists() {
                return project_data;
            }
            // fallback: 从可执行文件目录查找
            std::env::current_exe()
                .unwrap_or_default()
                .parent()
                .unwrap_or(std::path::Path::new("."))
                .to_path_buf()
        } else {
            std::env::current_exe()
                .unwrap_or_default()
                .parent()
                .unwrap_or(std::path::Path::new("."))
                .to_path_buf()
        };
        path.push("data/tavern_config.json");
        path
    }

    /// 从文件加载配置，若不存在或解析失败则返回默认值
    pub fn load() -> Self {
        let path = Self::config_path();
        match fs::read_to_string(&path) {
            Ok(content) => {
                serde_json::from_str(&content).unwrap_or_else(|e| {
                    eprintln!("[tavern_config] 解析配置失败: {}，使用默认值", e);
                    Self::default()
                })
            }
            Err(_) => {
                eprintln!("[tavern_config] 配置文件不存在，使用默认值");
                Self::default()
            }
        }
    }

    /// 保存配置到文件
    pub fn save(&self) {
        let path = Self::config_path();
        if let Some(parent) = path.parent() {
            let _ = fs::create_dir_all(parent);
        }
        match serde_json::to_string_pretty(self) {
            Ok(json) => {
                if let Err(e) = fs::write(&path, json) {
                    eprintln!("[tavern_config] 保存失败: {}", e);
                }
            }
            Err(e) => {
                eprintln!("[tavern_config] 序列化失败: {}", e);
            }
        }
    }

    /// 返回所有配置的 JSON 字符串（用于传给后端）
    #[allow(dead_code)]
    pub fn to_json(&self) -> String {
        serde_json::to_string(self).unwrap_or_default()
    }
}
