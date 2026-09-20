//! 网络相关功能：系统代理读取、GitHub 多地址连通性与下载测试。

use crate::lang::t;
use crate::lang::tf;
use crate::lang::resolve;
use std::fs::{self, File};
use std::io::{self, Read, Write};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::{Arc, LazyLock};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, Receiver, Sender};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use crate::core::settings::EnvSource;

const GITHUB_CLONE_URL: &str = "https://github.com/SillyTavern/SillyTavern.git";
const GITHUB_DOWNLOAD_URL: &str =
    "https://github.com/SillyTavern/SillyTavern/archive/refs/tags/1.18.0.tar.gz";
const TEST_ROOT_DIR: &str = "AstraBrew Launcher";

/// 酒馆核心下载渠道。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum DownloadChannel {
    #[default]
    Auto,
    Mirror1,
    Mirror2,
    Official,
}

impl DownloadChannel {
    pub const fn key(self) -> &'static str {
        match self {
            Self::Auto => "auto",
            Self::Mirror1 => "mirror1",
            Self::Mirror2 => "mirror2",
            Self::Official => "official",
        }
    }

    pub const fn label_key(self) -> &'static str {
        match self {
            Self::Auto => "channel.auto",
            Self::Mirror1 => "channel.mirror1",
            Self::Mirror2 => "channel.mirror2",
            Self::Official => "channel.official",
        }
    }

    pub const fn hint_key(self) -> &'static str {
        match self {
            Self::Auto => "channel.auto.hint",
            Self::Mirror1 => "channel.mirror1.hint",
            Self::Mirror2 => "channel.mirror2.hint",
            Self::Official => "channel.official.hint",
        }
    }

    pub const fn repository_url(self) -> &'static str {
        match self {
            Self::Auto => "https://github.com/sillyTavern/SillyTavern",
            Self::Mirror1 => "https://gitee.com/AstraBrew-Labs/SillyTavern",
            Self::Mirror2 => "https://gitcode.com/GitHub_Trending/si/SillyTavern",
            Self::Official => "https://github.com/sillyTavern/SillyTavern",
        }
    }

    pub const fn clone_url(self) -> &'static str {
        match self {
            Self::Auto => Self::Official.clone_url(),
            Self::Mirror1 => "https://gitee.com/AstraBrew-Labs/SillyTavern.git",
            Self::Mirror2 => "https://gitcode.com/GitHub_Trending/si/SillyTavern.git",
            Self::Official => GITHUB_CLONE_URL,
        }
    }

    pub const fn fixed_channels() -> [Self; 3] {
        [Self::Mirror1, Self::Mirror2, Self::Official]
    }

    pub fn from_key(value: &str) -> Self {
        match value.trim().to_ascii_lowercase().as_str() {
            "mirror1" | "mirror_1" | "镜像1" | "镜像 1" => Self::Mirror1,
            "mirror2" | "mirror_2" | "镜像2" | "镜像 2" => Self::Mirror2,
            // 历史设置里的 mirror3 已下线，回落到“自动”重新测速，而不是钉死某个渠道。
            "official" | "官方" => Self::Official,
            _ => Self::Auto,
        }
    }

    /// 「自动」渠道解析出具体渠道后用于展示的键。
    ///
    /// 界面侧用 [`crate::lang::text`] 渲染该键，避免在非渲染路径上固化语言。
    pub const fn resolved_label_key(self, resolved: Option<Self>) -> &'static str {
        match (self, resolved) {
            (Self::Auto, Some(Self::Mirror1)) => "channel.auto.resolved.mirror1",
            (Self::Auto, Some(Self::Mirror2)) => "channel.auto.resolved.mirror2",
            (Self::Auto, Some(Self::Official)) => "channel.auto.resolved.official",
            _ => self.label_key(),
        }
    }
}

#[derive(Debug, Clone)]
pub struct DownloadChannelTestResult {
    pub channel: DownloadChannel,
    pub success: bool,
    pub latency_ms: Option<u64>,
    pub error: Option<String>,
}

#[derive(Debug, Clone)]
pub enum DownloadChannelTestEvent {
    ChannelStarted {
        channel: DownloadChannel,
    },
    CloneProgress {
        channel: DownloadChannel,
        stage: String,
        current: Option<u64>,
        total: Option<u64>,
        percentage: Option<f32>,
    },
    ChannelFinished(DownloadChannelTestResult),
    Completed {
        selected: DownloadChannel,
        results: Vec<DownloadChannelTestResult>,
        all_failed: bool,
    },
}

/// 自动下载渠道测速结果的缓存。
///
/// 实际落盘位置由 [`download_channel_cache_path`] 决定，是软件根目录而不是 Caches：
/// 测速结果决定「自动」渠道解析成哪个镜像，属于需要跨启动保留的数据。
#[derive(Debug, Clone)]
pub struct DownloadChannelCache {
    pub resolved_channel: DownloadChannel,
    pub tested_at: u64,
    pub results: Vec<DownloadChannelTestResult>,
}

impl DownloadChannelCache {
    pub fn is_valid_at(&self, now: u64) -> bool {
        self.resolved_channel != DownloadChannel::Auto
            && now.saturating_sub(self.tested_at) < 7 * 24 * 60 * 60
    }
}

/// 自动下载渠道缓存文件：`%AppData%/AstraBrew Launcher/download_channel_cache.json`。
///
/// 放在软件根目录而不是临时目录：测速结果决定「自动」渠道解析成哪个镜像，属于需要跨启动保留的数据；
/// 放进 `%Temp%` 会被系统或清理工具删除，表现为每次打开程序都要重新测速。
pub fn download_channel_cache_path() -> PathBuf {
    crate::utils::app_paths()
        .root
        .join("download_channel_cache.json")
}

/// 旧版缓存路径：`%Temp%/astrabrew-launcher/caches/download_channel_cache.json`。
///
/// 只用于升级后读取一次历史结果，避免用户白白重新测速一次。
fn legacy_download_channel_cache_path() -> PathBuf {
    crate::utils::app_paths()
        .caches
        .join("download_channel_cache.json")
}

fn unix_seconds() -> Option<u64> {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .ok()
        .map(|duration| duration.as_secs())
}

/// 读取自动下载渠道缓存；文件不存在、损坏或字段不完整时返回 None。
pub fn load_download_channel_cache() -> Option<DownloadChannelCache> {
    read_download_channel_cache(&download_channel_cache_path())
        .or_else(|| read_download_channel_cache(&legacy_download_channel_cache_path()))
}

fn read_download_channel_cache(path: &Path) -> Option<DownloadChannelCache> {
    let contents = fs::read_to_string(path).ok()?;
    let value = serde_json::from_str::<serde_json::Value>(&contents).ok()?;
    let object = value.as_object()?;
    let resolved_channel = object
        .get("resolved_channel")
        .and_then(serde_json::Value::as_str)
        .map(DownloadChannel::from_key)?;
    let tested_at = object
        .get("tested_at")
        .and_then(serde_json::Value::as_u64)?;
    let results = object
        .get("results")
        .and_then(serde_json::Value::as_array)
        .map(|items| {
            items
                .iter()
                .filter_map(|item| {
                    let item = item.as_object()?;
                    Some(DownloadChannelTestResult {
                        channel: DownloadChannel::from_key(item.get("channel")?.as_str()?),
                        success: item.get("success")?.as_bool()?,
                        latency_ms: item.get("latency_ms").and_then(serde_json::Value::as_u64),
                        error: item
                            .get("error")
                            .and_then(serde_json::Value::as_str)
                            .map(str::to_owned),
                    })
                })
                .collect()
        })
        .unwrap_or_default();
    Some(DownloadChannelCache {
        resolved_channel,
        tested_at,
        results,
    })
}

/// 保存自动下载渠道测速结果到 Caches 目录，不写入 settings.json。
pub fn save_download_channel_cache(
    selected: DownloadChannel,
    results: &[DownloadChannelTestResult],
) -> io::Result<DownloadChannelCache> {
    let tested_at = unix_seconds().unwrap_or_default();
    let value = serde_json::json!({
        "resolved_channel": selected.key(),
        "tested_at": tested_at,
        "results": results.iter().map(|result| serde_json::json!({
            "channel": result.channel.key(),
            "success": result.success,
            "latency_ms": result.latency_ms,
            "error": result.error,
        })).collect::<Vec<_>>(),
    });
    let path = download_channel_cache_path();
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }
    let temporary = path.with_extension("json.tmp");
    fs::write(
        &temporary,
        serde_json::to_vec_pretty(&value).map_err(io::Error::other)?,
    )?;
    fs::rename(&temporary, &path)?;
    Ok(DownloadChannelCache {
        resolved_channel: selected,
        tested_at,
        results: results.to_vec(),
    })
}

/// 用已经完成的测速结果写入自动渠道缓存。
///
/// 只有全部固定渠道都拿到结果、且至少有一个渠道成功时才会写入：
/// - 结果不完整说明测速被中断，按已完成渠道写入会把非最优渠道锁定 7 天；
/// - 全部失败通常是临时的网络问题，写入后“自动”会长期退化成官方直连。
///
/// 返回 `Ok(None)` 表示本次没有可写入的结果，调用方应保留原有缓存。
pub fn cache_download_channel_result(
    results: &[DownloadChannelTestResult],
) -> io::Result<Option<DownloadChannelCache>> {
    if results.len() < DownloadChannel::fixed_channels().len()
        || results.iter().all(|result| !result.success)
    {
        return Ok(None);
    }
    let Some(selected) = fastest_download_channel(results) else {
        return Ok(None);
    };
    save_download_channel_cache(selected, results).map(Some)
}

/// 读取 Windows 系统代理设置。
///
/// 返回 `Some((代理地址, 是否启用))`：代理地址可能是空串（表示「用户明确禁用了代理」），
/// 与 `None`（读取失败，调用方应自行兜底）语义不同。
///
/// 优先级从高到低：
/// 1. 环境变量 `HTTPS_PROXY` / `HTTP_PROXY`（用户手动覆盖，务必最高优先）；
/// 2. IE/WinINET 注册表代理 —— 用户在「设置 → 网络和 Internet → 代理」里配的就是这里；
/// 3. WinHTTP 代理（`netsh winhttp show proxy`），通常只服务系统组件，作为最后回退。
pub fn read_system_proxy() -> Option<(String, bool)> {
    if let Some(server) = environment_proxy() {
        return Some((server, true));
    }
    if let Some(result) = read_registry_proxy() {
        return Some(result);
    }
    read_winhttp_proxy()
}

/// 从 IE/WinINET 注册表读取代理设置。
///
/// 注册表路径：`HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings`
/// - `ProxyEnable`（REG_DWORD）：0 = 禁用，非 0 = 启用；
/// - `ProxyServer`（REG_SZ）：`127.0.0.1:7890` 或 `http=host:port;https=host:port`。
///
/// 读到 `ProxyEnable = 0` 时返回 `None` 而不是空串：此时用户是明确关了代理，
/// 后续的 WinHTTP 回退不该被跳过，仍要试一次。
fn read_registry_proxy() -> Option<(String, bool)> {
    let hkcu = winreg::RegKey::predef(winreg::enums::HKEY_CURRENT_USER);
    let subkey = hkcu
        .open_subkey(r"Software\Microsoft\Windows\CurrentVersion\Internet Settings")
        .ok()?;

    let enabled: u32 = subkey.get_value("ProxyEnable").unwrap_or(0);
    if enabled == 0 {
        return None;
    }

    let server: String = subkey.get_value("ProxyServer").ok()?;
    let address = resolve_proxy_server(&server);
    if address.is_empty() {
        return None;
    }
    Some((address, true))
}

/// 从 `ProxyServer` 值里解析出可用的代理地址。
///
/// 支持两种格式：
/// - `127.0.0.1:7890` —— 单地址，直接返回；
/// - `http=127.0.0.1:7890;https=127.0.0.1:7890` —— 按协议分离，按 `https` → `http` → `socks`
///   的顺序取第一个非空项（HTTPS 覆盖更广，优先级最高）。
fn resolve_proxy_server(server: &str) -> String {
    let server = server.trim();
    if server.contains('=') {
        for protocol in ["https=", "http=", "socks="] {
            if let Some(start) = server.find(protocol).map(|position| position + protocol.len()) {
                let end = server[start..]
                    .find(';')
                    .map(|offset| start + offset)
                    .unwrap_or(server.len());
                let address = server[start..end].trim();
                if !address.is_empty() {
                    return address.to_owned();
                }
            }
        }
    }
    server.to_owned()
}

/// 通过 `netsh winhttp show proxy` 读取 WinHTTP 代理。
///
/// 输出为非 UTF-8 之前的 OEM 编码，但「代理服务器/Proxy Server」与「直接访问/Direct access」
/// 这些关键词在中英文系统上都是当前代码页下的 ASCII 或常见汉字，按 UTF-8 宽松解码后仍能匹配。
fn read_winhttp_proxy() -> Option<(String, bool)> {
    let mut command = Command::new("netsh");
    command.args(["winhttp", "show", "proxy"]);
    crate::core::env::apply_no_window_to_command(&mut command);
    let output = command.output().ok()?;

    let text = String::from_utf8_lossy(&output.stdout);
    if text
        .lines()
        .any(|line| line.contains("直接访问") || line.contains("Direct access"))
    {
        return None;
    }

    for line in text.lines() {
        let trimmed = line.trim();
        for prefix in ["代理服务器:", "Proxy Server:"] {
            if let Some(rest) = trimmed.strip_prefix(prefix) {
                let server = rest.trim();
                if !server.is_empty() {
                    return Some((server.to_owned(), true));
                }
            }
        }
    }
    None
}

/// 获取局域网 IPv4 地址，排除回环和链路本地地址。
///
/// 解析 `ipconfig` 输出：中英文系统的行前缀分别是 `IPv4 地址` 与 `IPv4 Address`，
/// 冒号前后有大量用于对齐的点号，因此按 `split(':')` 取值而不是按固定列宽。
pub fn get_lan_ipv4() -> Option<String> {
    parse_lan_ipv4(&ipconfig_output()?)
}

/// 获取局域网全局 IPv6 地址，排除回环和链路本地地址。
pub fn get_lan_ipv6() -> Option<String> {
    parse_lan_ipv6(&ipconfig_output()?)
}

/// 运行 `ipconfig` 并取回标准输出。
///
/// 该命令会弹出控制台窗口，必须带上 `CREATE_NO_WINDOW`，否则界面会闪黑框。
fn ipconfig_output() -> Option<String> {
    let mut command = Command::new("ipconfig");
    // 强制 UTF-8 代码页，避免中文系统上的本地化字段名被解码成乱码而匹配不上。
    command.args(["/all"]);
    crate::core::env::apply_no_window_to_command(&mut command);
    let output = command.output().ok()?;
    output
        .status
        .success()
        .then(|| String::from_utf8_lossy(&output.stdout).into_owned())
}

fn parse_lan_ipv4(output: &str) -> Option<String> {
    output.lines().find_map(|line| {
        let trimmed = line.trim();
        // 中文是 `IPv4 地址 . . . : 192.168.1.100`，英文是 `IPv4 Address. . . . : ...`。
        // `IP Address` 是更老的英文系统写法，一并兼容。
        let matched = trimmed.starts_with("IPv4")
            || trimmed.starts_with("IP Address")
            || trimmed.starts_with("IPv4 Address");
        if !matched {
            return None;
        }
        let address = trimmed.rsplit(':').next()?.trim();
        let is_usable = !address.is_empty()
            && address.contains('.')
            && address != "127.0.0.1"
            && !address.starts_with("169.254.");
        is_usable.then(|| address.to_owned())
    })
}

fn parse_lan_ipv6(output: &str) -> Option<String> {
    output.lines().find_map(|line| {
        let trimmed = line.trim();
        if !trimmed.starts_with("IPv6") {
            return None;
        }
        // 行尾可能是 `2001:db8::1`，也可能是带 `%12` 接口号的临时地址，
        // 且 `IPv6 地址` 自身的冒号会干扰切分，因此从右侧逐段尝试。
        let raw = trimmed.rsplit(':').next()?.trim();
        let address = raw.split('%').next()?.trim();
        let lower = address.to_ascii_lowercase();
        let is_usable = !address.is_empty()
            && address != "::1"
            && !lower.starts_with("fe80:")
            && address.contains(':');
        is_usable.then(|| address.to_owned())
    })
}

/// 获取真实公网 IPv4 地址；禁用代理并强制使用 IPv4 socket。
pub fn get_public_ipv4() -> Option<String> {
    public_ip(
        std::net::IpAddr::V4(std::net::Ipv4Addr::UNSPECIFIED),
        &[
            "https://api-ipv4.ip.sb/ip",
            "https://api4.ipify.org",
            "https://v4.ident.me",
        ],
        |value| value.contains('.') && !value.contains(':'),
    )
}

/// 获取真实公网 IPv6 地址；禁用代理并强制使用 IPv6 socket。
pub fn get_public_ipv6() -> Option<String> {
    public_ip(
        std::net::IpAddr::V6(std::net::Ipv6Addr::UNSPECIFIED),
        &[
            "https://api-ipv6.ip.sb/ip",
            "https://api6.ipify.org",
            "https://v6.ident.me",
        ],
        |value| value.contains(':'),
    )
}

// ─── 酒馆连接日志解析 ─────────────────────────────────────────────────────────

/// 酒馆连接日志中提取出的客户端信息。
#[derive(Debug, Clone)]
pub struct ConnectionInfo {
    /// 客户端 IP 地址。
    pub ip: String,
    /// 从 User-Agent 推断出的操作系统。
    pub os: String,
    /// 从 User-Agent 推断出的设备型号。
    pub device: Option<String>,
    /// 原始 User-Agent，用于当前会话内去重。
    pub user_agent: String,
}

/// 去掉酒馆日志中的 ANSI 控制序列，避免彩色输出影响连接日志匹配。
fn strip_ansi_simple(line: &str) -> String {
    if !line.contains('\x1b') {
        return line.to_owned();
    }
    let mut result = String::with_capacity(line.len());
    let mut chars = line.chars().peekable();
    while let Some(ch) = chars.next() {
        if ch != '\x1b' {
            result.push(ch);
            continue;
        }
        match chars.next() {
            Some('[') => {
                while let Some(c) = chars.next() {
                    if c.is_ascii_alphabetic() || c == '~' {
                        break;
                    }
                }
            }
            Some(']') => {
                while let Some(c) = chars.next() {
                    if c == '\x07' {
                        break;
                    }
                    if c == '\x1b' && chars.peek() == Some(&'\\') {
                        chars.next();
                        break;
                    }
                }
            }
            Some(_) | None => {}
        }
    }
    result
}

/// 从 User-Agent 提取可读的操作系统名称。
fn parse_os_from_ua(ua: &str) -> String {
    if let Some(index) = ua.find("Mac OS X ") {
        let version: String = ua[index + 9..]
            .chars()
            .take_while(|c| c.is_ascii_alphanumeric() || matches!(c, '_' | '.'))
            .collect();
        if !version.is_empty() {
            return format!("macOS {}", version.replace('_', "."));
        }
    }
    if let Some(index) = ua.find("Windows NT ") {
        let version: String = ua[index + 11..]
            .chars()
            .take_while(|c| c.is_ascii_digit() || *c == '.')
            .collect();
        if !version.is_empty() {
            return format!("Windows {version}");
        }
    }
    if let Some(index) = ua.find("iPhone OS ") {
        let version: String = ua[index + 10..]
            .chars()
            .take_while(|c| c.is_ascii_alphanumeric() || matches!(c, '_' | '.'))
            .collect();
        if !version.is_empty() {
            return format!("iOS {}", version.replace('_', "."));
        }
    }
    if let Some(index) = ua.find("CPU OS ") {
        let version: String = ua[index + 7..]
            .chars()
            .take_while(|c| c.is_ascii_alphanumeric() || matches!(c, '_' | '.'))
            .collect();
        if !version.is_empty() {
            return format!("iPadOS {}", version.replace('_', "."));
        }
    }
    if let Some(index) = ua.find("Android ") {
        let version: String = ua[index + 8..]
            .chars()
            .take_while(|c| c.is_ascii_digit() || *c == '.')
            .collect();
        if !version.is_empty() {
            return format!("Android {version}");
        }
    }
    if ua.contains("Linux") {
        return "Linux".to_owned();
    }
    "Unknown".to_owned()
}

/// 从 User-Agent 提取移动设备名称；桌面浏览器返回 None。
fn parse_device_from_ua(ua: &str) -> Option<String> {
    if ua.contains("iPhone") {
        return Some("iPhone".to_owned());
    }
    if ua.contains("iPad") {
        return Some("iPad".to_owned());
    }
    let android = ua.find("Android")?;
    let segment = ua[android..].split(')').next()?;
    let model = segment.rsplit(';').next()?.trim();
    if model.is_empty() || model.eq_ignore_ascii_case("wv") || model.starts_with("Android ") {
        return None;
    }
    let upper = model.to_ascii_uppercase();
    if model.starts_with("Pixel") {
        return Some(format!("Google {model}"));
    }
    if upper.starts_with("SM-") || upper.starts_with("GT-") {
        return Some(format!("Samsung {model}"));
    }
    if model.starts_with("Redmi") || model.starts_with("POCO") {
        return Some(format!("Xiaomi {model}"));
    }
    if upper.starts_with("ONEPLUS") {
        return Some(format!("OnePlus {model}"));
    }
    if upper.starts_with("RMX") {
        return Some(format!("realme {model}"));
    }
    if upper.starts_with("CPH") {
        return Some(format!("OPPO {model}"));
    }
    Some(model.to_owned())
}

/// 解析 `New connection from <IP>; User Agent: <UA>` 日志行。
pub fn parse_connection_log(line: &str) -> Option<ConnectionInfo> {
    let plain = strip_ansi_simple(line);
    let rest = plain.split_once("New connection from ")?.1;
    let (ip, ua) = rest.split_once("; User Agent:")?;
    let ip = ip.trim();
    let user_agent = ua.trim();
    if ip.is_empty() || user_agent.is_empty() {
        return None;
    }
    Some(ConnectionInfo {
        ip: ip.to_owned(),
        os: parse_os_from_ua(user_agent),
        device: parse_device_from_ua(user_agent),
        user_agent: user_agent.to_owned(),
    })
}

/// 判断 IP 是否属于本机，回环地址和本机网卡地址均不提醒。
pub fn is_local_ip(ip: &str) -> bool {
    let ip = ip.trim();
    ip.is_empty()
        || matches!(ip, "localhost" | "::1")
        || ip.starts_with("127.")
        || LOCAL_IP_SET.contains(ip)
}

/// 启动时缓存本机网卡地址，避免每条日志都去跑一次 `ipconfig`。
///
/// 这里收集的是**全部**本机地址（含链路本地与回环以外的所有接口），
/// 用于把酒馆连接日志里的「自己连自己」过滤掉，与 [`get_lan_ipv4`] 只取一个地址的用途不同。
static LOCAL_IP_SET: LazyLock<std::collections::HashSet<String>> = LazyLock::new(|| {
    let mut addresses = std::collections::HashSet::new();
    let Some(output) = ipconfig_output() else {
        return addresses;
    };
    for line in output.lines() {
        let trimmed = line.trim();
        if let Some(address) = parse_address_column(trimmed, "IPv4") {
            addresses.insert(address);
        } else if let Some(address) = parse_address_column(trimmed, "IPv6") {
            addresses.insert(address);
        }
    }
    addresses
});

/// 从 `ipconfig` 的一行里取出地址列。
///
/// 中英文系统的行首分别是 `IPv4 地址` 与 `IPv4 Address`，共同点是都以 `IPv4`/`IPv6` 开头；
/// 字段名与值之间用点号对齐，所以取最后一个冒号之后的内容即为地址。
fn parse_address_column(line: &str, family: &str) -> Option<String> {
    if !line.starts_with(family) {
        return None;
    }
    let raw = line.rsplit(':').next()?.trim();
    let address = raw.split('%').next()?.trim();
    let lower = address.to_ascii_lowercase();
    let usable = !address.is_empty()
        && address != "::1"
        && address != "127.0.0.1"
        && !address.starts_with("169.254.")
        && !lower.starts_with("fe80:")
        && !lower.starts_with("::");
    usable.then(|| address.to_owned())
}

fn public_ip(
    local_address: std::net::IpAddr,
    endpoints: &[&str],
    valid: impl Fn(&str) -> bool,
) -> Option<String> {
    let client = reqwest::blocking::Client::builder()
        .local_address(local_address)
        .timeout(Duration::from_secs(8))
        .no_proxy()
        .build()
        .ok()?;
    endpoints.iter().find_map(|endpoint| {
        let response = client.get(*endpoint).send().ok()?;
        if !response.status().is_success() {
            return None;
        }
        let value = response.text().ok()?.trim().to_owned();
        (!value.is_empty() && valid(&value)).then_some(value)
    })
}

fn environment_proxy() -> Option<String> {
    ["HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy"]
        .into_iter()
        .find_map(|key| {
            let value = std::env::var(key).ok()?;
            (!value.trim().is_empty()).then_some(value)
        })
}

fn normalize_proxy_url(proxy: &str) -> Result<String, &'static str> {
    let proxy = proxy.trim();
    if proxy.is_empty() {
        return Err("network.proxy_empty");
    }
    if proxy.starts_with("http://")
        || proxy.starts_with("https://")
        || proxy.starts_with("socks5://")
    {
        Ok(proxy.to_owned())
    } else {
        Ok(format!("http://{proxy}"))
    }
}

fn normalize_system_proxy_url(proxy: &str) -> Option<String> {
    if proxy.contains('=') {
        let entries = proxy.split(';').filter_map(|part| {
            let (key, value) = part.split_once('=')?;
            Some((key, value))
        });
        let mut http = None;
        for (key, value) in entries {
            if key == "https" {
                return normalize_proxy_url(value).ok();
            }
            if key == "http" {
                http = Some(value);
            }
        }
        return normalize_proxy_url(http?).ok();
    }
    normalize_proxy_url(proxy).ok()
}

fn selected_proxy_url(proxy_mode: &str, proxy_host: &str) -> Result<Option<String>, String> {
    match proxy_mode {
        "custom" => normalize_proxy_url(proxy_host)
            .map(Some)
            .map_err(str::to_owned),
        "system" => Ok(read_system_proxy()
            .filter(|(_, enabled)| *enabled)
            .and_then(|(server, _)| normalize_system_proxy_url(&server))),
        _ => Ok(None),
    }
}

pub(crate) fn build_client(
    proxy_mode: &str,
    proxy_host: &str,
) -> Result<reqwest::blocking::Client, String> {
    let mut builder = reqwest::blocking::Client::builder()
        .no_proxy()
        .timeout(Duration::from_secs(10))
        .user_agent("AstraBrew-Launcher-macOS");
    if let Some(proxy_url) = selected_proxy_url(proxy_mode, proxy_host)? {
        let proxy = reqwest::Proxy::all(&proxy_url)
            .map_err(|error| tf("network.test.proxy_format_invalid", &[("error", &error)]))?;
        builder = builder.proxy(proxy);
    }
    builder
        .build()
        .map_err(|error| tf("network.test.client_build_failed", &[("error", &error)]))
}

/// 单个 GitHub 测试结果。
#[derive(Debug, Clone)]
pub struct GithubMultiTestItem {
    pub key: String,
    pub name: String,
    pub success: bool,
    pub latency_ms: Option<u64>,
    pub error: Option<String>,
    pub warning: Option<String>,
}

/// 测试线程发给 iced UI 的实时事件。
#[derive(Debug, Clone)]
pub enum GithubTestEvent {
    ItemStarted {
        key: String,
        name: String,
    },
    CloneProgress {
        stage: String,
        current: Option<u64>,
        total: Option<u64>,
        percentage: Option<f32>,
    },
    DownloadProgress {
        total_bytes: Option<u64>,
        downloaded_bytes: u64,
        bytes_per_second: u64,
        percentage: Option<f32>,
    },
    ItemFinished(GithubMultiTestItem),
    Completed(Vec<GithubMultiTestItem>),
}

fn emit(sender: &Option<Sender<GithubTestEvent>>, event: GithubTestEvent) {
    if let Some(sender) = sender {
        let _ = sender.send(event);
    }
}

fn test_urls(include_api: bool) -> Vec<(String, String, String)> {
    let mut urls = vec![
        (
            "raw".to_owned(),
            t("network.test.raw").to_owned(),
            "https://raw.githubusercontent.com/SillyTavern/SillyTavern/release/start.sh".to_owned(),
        ),
        (
            "repo".to_owned(),
            t("network.test.repo").to_owned(),
            "https://github.com/SillyTavern/SillyTavern".to_owned(),
        ),
        (
            "homepage".to_owned(),
            t("network.test.homepage").to_owned(),
            "https://www.github.com".to_owned(),
        ),
    ];
    if include_api {
        urls.push((
            "api".to_owned(),
            t("network.test.api").to_owned(),
            "https://api.github.com/repos/SillyTavern/SillyTavern/releases".to_owned(),
        ));
    }
    urls
}

fn accelerated_url(url: &str, accelerate_url: Option<&str>) -> String {
    accelerate_url
        .map(|accelerate| format!("{}/{}", accelerate.trim_end_matches('/'), url))
        .unwrap_or_else(|| url.to_owned())
}

fn channelize_url(url: &str, channel: DownloadChannel) -> String {
    let github_repo = "https://github.com/SillyTavern/SillyTavern";
    let lower = url.to_ascii_lowercase();
    let github_prefix = github_repo.to_ascii_lowercase();
    if lower.starts_with(&github_prefix) {
        let suffix = &url[github_repo.len()..];
        format!("{}{}", channel.repository_url(), suffix)
    } else {
        url.to_owned()
    }
}

fn download_url(channel: DownloadChannel) -> &'static str {
    match channel {
        DownloadChannel::Mirror1 => {
            "https://gitee.com/AstraBrew-Labs/SillyTavern/archive/refs/tags/1.18.0.tar.gz"
        }
        DownloadChannel::Mirror2 => {
            "https://gitcode.com/GitHub_Trending/si/SillyTavern/archive/refs/tags/1.18.0.tar.gz"
        }
        DownloadChannel::Auto | DownloadChannel::Official => GITHUB_DOWNLOAD_URL,
    }
}

fn failed_results(error: &str, include_api: bool) -> Vec<GithubMultiTestItem> {
    // error 是文案键（超时 / 未完成），解析一次后随每个测试项展示。
    let error = resolve(error);
    let mut items = test_urls(include_api)
        .into_iter()
        .map(|(key, name, _)| GithubMultiTestItem {
            key,
            name,
            success: false,
            latency_ms: None,
            error: Some(error.clone()),
            warning: None,
        })
        .collect::<Vec<_>>();
    items.push(GithubMultiTestItem {
        key: "clone".to_owned(),
        name: t("network.test.clone").to_owned(),
        success: false,
        latency_ms: None,
        error: Some(error.clone()),
        warning: None,
    });
    items.push(GithubMultiTestItem {
        key: "speed".to_owned(),
        name: t("network.test.download_speed").to_owned(),
        success: false,
        latency_ms: None,
        error: Some(error.clone()),
        warning: None,
    });
    items
}

/// 同步执行完整测试并返回最终结果。主要用于测试和非 UI 调用。
#[allow(dead_code)]
pub fn test_github_multi(
    proxy_mode: &str,
    proxy_host: &str,
    _proxy_port: u16,
    accelerate_url: Option<String>,
    include_api: bool,
) -> Vec<GithubMultiTestItem> {
    let (sender, receiver) = std::sync::mpsc::channel();
    run_github_test(
        proxy_mode,
        proxy_host,
        accelerate_url,
        include_api,
        Some(sender),
    );
    receiver
        .into_iter()
        .find_map(|event| match event {
            GithubTestEvent::Completed(results) => Some(results),
            _ => None,
        })
        .unwrap_or_else(|| failed_results("network.test.incomplete", include_api))
}

/// 在后台线程中执行完整 GitHub 测试，并通过事件发送实时进度。
pub fn run_github_test(
    proxy_mode: &str,
    proxy_host: &str,
    accelerate_url: Option<String>,
    include_api: bool,
    sender: Option<Sender<GithubTestEvent>>,
) {
    run_github_test_with_cancel_for_channel(
        proxy_mode,
        proxy_host,
        DownloadChannel::Official,
        accelerate_url,
        include_api,
        sender,
        Arc::new(AtomicBool::new(false)),
    );
}

/// 支持取消信号的 GitHub 测试入口。
#[allow(dead_code)]
pub fn run_github_test_with_cancel(
    proxy_mode: &str,
    proxy_host: &str,
    accelerate_url: Option<String>,
    include_api: bool,
    sender: Option<Sender<GithubTestEvent>>,
    cancel: Arc<AtomicBool>,
) {
    run_github_test_with_cancel_for_channel(
        proxy_mode,
        proxy_host,
        DownloadChannel::Official,
        accelerate_url,
        include_api,
        sender,
        cancel,
    );
}

/// 使用指定酒馆下载渠道执行完整 GitHub 连通性测试。
pub fn run_github_test_with_cancel_for_channel(
    proxy_mode: &str,
    proxy_host: &str,
    channel: DownloadChannel,
    accelerate_url: Option<String>,
    include_api: bool,
    sender: Option<Sender<GithubTestEvent>>,
    cancel: Arc<AtomicBool>,
) {
    let mut results = Vec::new();
    let accelerate = accelerate_url.as_deref();
    let client = match build_client(proxy_mode, proxy_host) {
        Ok(client) => client,
        Err(error) => {
            let results = failed_results(&error, include_api);
            for item in &results {
                emit(&sender, GithubTestEvent::ItemFinished(item.clone()));
            }
            emit(&sender, GithubTestEvent::Completed(results));
            return;
        }
    };

    for (key, name, url) in test_urls(include_api) {
        if cancel.load(Ordering::Relaxed) {
            break;
        }
        let item = test_http_endpoint(
            &client, &key, &name, &url, channel, accelerate, &sender, &cancel,
        );
        emit(&sender, GithubTestEvent::ItemFinished(item.clone()));
        results.push(item);
    }

    let clone_item = test_git_clone(
        proxy_mode, proxy_host, channel, accelerate, &sender, &cancel,
    );
    emit(&sender, GithubTestEvent::ItemFinished(clone_item.clone()));
    results.push(clone_item);

    let download_item = test_download(
        &client, proxy_mode, proxy_host, channel, accelerate, &sender, &cancel,
    );
    emit(
        &sender,
        GithubTestEvent::ItemFinished(download_item.clone()),
    );
    results.push(download_item);

    emit(&sender, GithubTestEvent::Completed(results));
}

fn test_http_endpoint(
    client: &reqwest::blocking::Client,
    key: &str,
    name: &str,
    url: &str,
    channel: DownloadChannel,
    accelerate_url: Option<&str>,
    sender: &Option<Sender<GithubTestEvent>>,
    cancel: &Arc<AtomicBool>,
) -> GithubMultiTestItem {
    emit(
        sender,
        GithubTestEvent::ItemStarted {
            key: key.to_owned(),
            name: name.to_owned(),
        },
    );
    if cancel.load(Ordering::Relaxed) {
        return cancelled_item(key, name);
    }
    let request_url = accelerated_url(&channelize_url(url, channel), accelerate_url);
    let start = Instant::now();
    match client.get(request_url).send() {
        Ok(mut response) => {
            let latency = start.elapsed().as_millis() as u64;
            let status = response.status();
            let mut success = status.is_success();
            let mut warning = None;
            let mut error = None;

            if !success {
                if let Some(accelerate_url) = accelerate_url {
                    let status_code = status.as_u16();
                    if status_code == 403 || status_code == 404 {
                        success = true;
                        warning = Some(tf("network.test.accelerator_unusable_code", &[("status_code", &status_code)]));
                    } else {
                        let mut body = String::new();
                        let _ = response.read_to_string(&mut body);
                        let lower = body.to_lowercase();
                        if lower.contains("invalid input") || lower.contains("无效输入") {
                            success = true;
                            warning = Some(t("network.test.accelerator_unusable").to_owned());
                        } else {
                            error = Some(tf("network.test.http_status_with_proxy", &[("status", &status), ("url", &accelerate_url)]));
                        }
                    }
                } else {
                    error = Some(tf("network.test.http_status", &[("status", &status)]));
                }
            }

            GithubMultiTestItem {
                key: key.to_owned(),
                name: name.to_owned(),
                success,
                latency_ms: Some(latency),
                error,
                warning,
            }
        }
        Err(error) => GithubMultiTestItem {
            key: key.to_owned(),
            name: name.to_owned(),
            success: false,
            latency_ms: None,
            error: Some(tf("network.test.connection_failed", &[("error", &error)])),
            warning: None,
        },
    }
}

/// 解析外部命令的可执行文件路径。
///
/// 用 `where` 而不是硬编码目录：Windows 上 NodeJS / Git 可能装在任意盘符，
/// 内置环境（`%AppData%/AstraBrew Launcher/lib/`）也通过 PATH 注入被 `where` 看到。
/// 全部找不到时原样返回命令名，由 `Command` 自己按 PATH 再试一次。
fn resolve_command(name: &str) -> String {
    crate::core::env::get_system_cmd_path(name)
        .map(|path| path.to_string_lossy().into_owned())
        .unwrap_or_else(|| name.to_owned())
}

/// 为 Git 命令配置代理。
///
/// **同时负责隐藏控制台窗口**：Git 在 Windows 上是控制台程序，从 GUI 进程直接拉起时
/// 系统会为它新建一个控制台，表现为黑框闪过；clone/fetch 这类长任务的黑框还会一直
/// 停在启动器前面。放在这里统一处理，是因为本模块所有 Git 调用都必然先经过它。
fn configure_git_proxy(command: &mut Command, proxy_mode: &str, proxy_host: &str) {
    crate::core::env::apply_no_window_to_command(command);
    match selected_proxy_url(proxy_mode, proxy_host).ok().flatten() {
        Some(proxy) => {
            command.arg("-c").arg(format!("http.proxy={proxy}"));
            command.arg("-c").arg(format!("https.proxy={proxy}"));
        }
        None => {
            command.arg("-c").arg("http.proxy=");
            command.arg("-c").arg("https.proxy=");
        }
    }
}

/// 为一次测速生成不会互相冲突的临时目录。
///
/// 用系统临时目录而不是硬编码路径：Windows 上 `%TEMP%` 可能被重定向到别的盘。
fn unique_test_root() -> std::path::PathBuf {
    let timestamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_nanos())
        .unwrap_or_default();
    let suffix = format!("{}-{timestamp}", std::process::id());
    std::env::temp_dir().join(TEST_ROOT_DIR).join(suffix)
}

fn test_git_clone(
    proxy_mode: &str,
    proxy_host: &str,
    channel: DownloadChannel,
    accelerate_url: Option<&str>,
    sender: &Option<Sender<GithubTestEvent>>,
    cancel: &Arc<AtomicBool>,
) -> GithubMultiTestItem {
    let key = "clone";
    // 项名会直接上屏（设置页的实时测试列表），构造时就按当前语言固化。
    let name = t("network.test.clone");
    emit(
        sender,
        GithubTestEvent::ItemStarted {
            key: key.to_owned(),
            name: name.to_owned(),
        },
    );

    if cancel.load(Ordering::Relaxed) {
        return cancelled_item(key, name);
    }
    let root = unique_test_root();
    let clone_path = root.join("SillyTavern");
    if let Err(error) = fs::create_dir_all(&root) {
        return GithubMultiTestItem {
            key: key.to_owned(),
            name: name.to_owned(),
            success: false,
            latency_ms: None,
            error: Some(tf("network.test.temp_dir_failed", &[("error", &error)])),
            warning: None,
        };
    }

    let url = accelerated_url(channel.clone_url(), accelerate_url);
    let start = Instant::now();
    let mut command = Command::new(resolve_command("git"));
    configure_git_proxy(&mut command, proxy_mode, proxy_host);
    command
        .args(["clone", "--progress"])
        .arg(url)
        .arg(&clone_path)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::piped());

    let mut child = match command.spawn() {
        Ok(child) => child,
        Err(error) => {
            let _ = fs::remove_dir_all(&root);
            return GithubMultiTestItem {
                key: key.to_owned(),
                name: name.to_owned(),
                success: false,
                latency_ms: None,
                error: Some(tf("network.test.git_start_failed", &[("error", &error)])),
                warning: None,
            };
        }
    };

    let mut last_message = None;
    let progress_receiver = child.stderr.take().map(spawn_progress_reader);
    loop {
        if cancel.load(Ordering::Relaxed) {
            let _ = child.kill();
            let _ = child.wait();
            let _ = fs::remove_dir_all(&root);
            return cancelled_item(key, name);
        }

        if let Some(receiver) = &progress_receiver {
            while let Ok(line) = receiver.try_recv() {
                if let Some(progress) = parse_git_progress(&line) {
                    emit(
                        sender,
                        GithubTestEvent::CloneProgress {
                            stage: progress.stage,
                            current: progress.current,
                            total: progress.total,
                            percentage: progress.percentage,
                        },
                    );
                } else if !line.starts_with("warning:") && !line.is_empty() {
                    last_message = Some(line);
                }
            }
        }

        match child.try_wait() {
            Ok(Some(_)) => break,
            Ok(None) => std::thread::sleep(Duration::from_millis(40)),
            Err(error) => {
                last_message = Some(tf("network.test.clone_wait_failed", &[("error", &error)]));
                break;
            }
        }
    }
    if let Some(receiver) = &progress_receiver {
        while let Ok(line) = receiver.try_recv() {
            if let Some(progress) = parse_git_progress(&line) {
                emit(
                    sender,
                    GithubTestEvent::CloneProgress {
                        stage: progress.stage,
                        current: progress.current,
                        total: progress.total,
                        percentage: progress.percentage,
                    },
                );
            } else if !line.starts_with("warning:") && !line.is_empty() {
                last_message = Some(line);
            }
        }
    }

    let status = child.wait();
    let elapsed = start.elapsed().as_millis() as u64;
    let result = match status {
        Ok(status) if status.success() => GithubMultiTestItem {
            key: key.to_owned(),
            name: name.to_owned(),
            success: true,
            latency_ms: Some(elapsed),
            error: None,
            warning: None,
        },
        Ok(status) => GithubMultiTestItem {
            key: key.to_owned(),
            name: name.to_owned(),
            success: false,
            latency_ms: Some(elapsed),
            error: Some(last_message.unwrap_or_else(|| {
                tf("network.test.clone_failed", &[("code", &status.code().unwrap_or(-1))])
            })),
            warning: None,
        },
        Err(error) => GithubMultiTestItem {
            key: key.to_owned(),
            name: name.to_owned(),
            success: false,
            latency_ms: Some(elapsed),
            error: Some(tf("network.test.clone_wait_failed", &[("error", &error)])),
            warning: None,
        },
    };
    let _ = fs::remove_dir_all(&root);
    result
}

struct GitProgress {
    stage: String,
    current: Option<u64>,
    total: Option<u64>,
    percentage: Option<f32>,
}

fn parse_git_progress(line: &str) -> Option<GitProgress> {
    let cleaned_line = strip_git_ansi(line);
    let line = cleaned_line.trim().trim_start_matches("remote: ").trim();
    let (stage, detail) = line.split_once(':')?;
    let stage = stage.trim();
    if !matches!(
        stage,
        "Enumerating objects"
            | "Counting objects"
            | "Compressing objects"
            | "Receiving objects"
            | "Resolving deltas"
    ) {
        return None;
    }

    let percentage = detail.split_once('%').and_then(|(value, _)| {
        value
            .split_whitespace()
            .last()
            .and_then(|value| value.parse::<f32>().ok())
    });
    let (current, total) = detail
        .split_once('(')
        .and_then(|(_, rest)| rest.split_once(')'))
        .and_then(|(counts, _)| counts.split_once('/'))
        .map(|(current, total)| {
            (
                current.trim().parse::<u64>().ok(),
                total.trim().parse::<u64>().ok(),
            )
        })
        .unwrap_or((None, None));
    let percentage = percentage.or_else(|| {
        current.zip(total).and_then(|(current, total)| {
            (total > 0).then_some(current as f32 / total as f32 * 100.0)
        })
    });

    Some(GitProgress {
        stage: stage.to_owned(),
        current,
        total,
        percentage,
    })
}

fn strip_git_ansi(input: &str) -> String {
    let mut output = String::with_capacity(input.len());
    let mut chars = input.chars().peekable();
    while let Some(ch) = chars.next() {
        if ch == '\x1b' && chars.peek() == Some(&'[') {
            chars.next();
            while let Some(next) = chars.next() {
                if next.is_ascii_alphabetic() {
                    break;
                }
            }
        } else {
            output.push(ch);
        }
    }
    output
}

fn spawn_progress_reader(mut stderr: impl Read + Send + 'static) -> Receiver<String> {
    let (sender, receiver) = mpsc::channel();
    std::thread::spawn(move || {
        let mut buffer = [0u8; 4096];
        let mut pending = String::new();
        loop {
            let read = match stderr.read(&mut buffer) {
                Ok(read) => read,
                Err(_) => break,
            };
            if read == 0 {
                break;
            }
            pending.push_str(&String::from_utf8_lossy(&buffer[..read]));
            while let Some(index) = pending.find(['\r', '\n']) {
                let line = pending[..index].to_owned();
                pending.drain(..=index);
                let _ = sender.send(line);
            }
        }
        if !pending.is_empty() {
            let _ = sender.send(pending);
        }
    });
    receiver
}

fn emit_download_channel(
    sender: &Option<Sender<DownloadChannelTestEvent>>,
    event: DownloadChannelTestEvent,
) {
    if let Some(sender) = sender {
        let _ = sender.send(event);
    }
}

/// 对全部固定酒馆下载渠道执行轻量 Git 克隆测速。
pub fn run_download_channel_test(
    proxy_mode: &str,
    proxy_host: &str,
    sender: Option<Sender<DownloadChannelTestEvent>>,
    cancel: Arc<AtomicBool>,
) {
    let mut results = Vec::new();
    for channel in DownloadChannel::fixed_channels() {
        if cancel.load(Ordering::Relaxed) {
            break;
        }
        emit_download_channel(
            &sender,
            DownloadChannelTestEvent::ChannelStarted { channel },
        );
        let result = test_download_channel_clone(channel, proxy_mode, proxy_host, &sender, &cancel);
        emit_download_channel(
            &sender,
            DownloadChannelTestEvent::ChannelFinished(result.clone()),
        );
        results.push(result);
    }

    if cancel.load(Ordering::Relaxed) {
        return;
    }
    let selected = fastest_download_channel(&results).unwrap_or(DownloadChannel::Official);
    let all_failed = results.iter().all(|result| !result.success);
    emit_download_channel(
        &sender,
        DownloadChannelTestEvent::Completed {
            selected,
            results,
            all_failed,
        },
    );
}

fn fastest_download_channel(results: &[DownloadChannelTestResult]) -> Option<DownloadChannel> {
    results
        .iter()
        .filter(|result| result.success)
        .filter_map(|result| result.latency_ms.map(|latency| (result.channel, latency)))
        .min_by_key(|(_, latency)| *latency)
        .map(|(channel, _)| channel)
}

fn test_download_channel_clone(
    channel: DownloadChannel,
    proxy_mode: &str,
    proxy_host: &str,
    sender: &Option<Sender<DownloadChannelTestEvent>>,
    cancel: &Arc<AtomicBool>,
) -> DownloadChannelTestResult {
    let root = unique_test_root();
    let clone_path = root.join(channel.key());
    if let Err(error) = fs::create_dir_all(&root) {
        return DownloadChannelTestResult {
            channel,
            success: false,
            latency_ms: None,
            error: Some(tf("network.test.temp_dir_failed", &[("error", &error)])),
        };
    }

    let start = Instant::now();
    let mut command = Command::new(resolve_command("git"));
    configure_git_proxy(&mut command, proxy_mode, proxy_host);
    command
        .args(["clone", "--progress", "--depth=1", "--no-tags"])
        .arg(channel.clone_url())
        .arg(&clone_path)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::piped());

    let mut child = match command.spawn() {
        Ok(child) => child,
        Err(error) => {
            let _ = fs::remove_dir_all(&root);
            return DownloadChannelTestResult {
                channel,
                success: false,
                latency_ms: None,
                error: Some(tf("network.test.git_start_failed", &[("error", &error)])),
            };
        }
    };

    let progress_receiver = child.stderr.take().map(spawn_progress_reader);
    let mut last_message = None;
    loop {
        if cancel.load(Ordering::Relaxed) {
            let _ = child.kill();
            let _ = child.wait();
            let _ = fs::remove_dir_all(&root);
            return DownloadChannelTestResult {
                channel,
                success: false,
                latency_ms: None,
                error: Some(t("network.test.channel_speed_cancelled").to_owned()),
            };
        }
        if let Some(receiver) = &progress_receiver {
            while let Ok(line) = receiver.try_recv() {
                if let Some(progress) = parse_git_progress(&line) {
                    emit_download_channel(
                        sender,
                        DownloadChannelTestEvent::CloneProgress {
                            channel,
                            stage: progress.stage,
                            current: progress.current,
                            total: progress.total,
                            percentage: progress.percentage,
                        },
                    );
                } else if !line.starts_with("warning:") && !line.trim().is_empty() {
                    last_message = Some(line);
                }
            }
        }
        match child.try_wait() {
            Ok(Some(_)) => break,
            Ok(None) => std::thread::sleep(Duration::from_millis(40)),
            Err(error) => {
                last_message = Some(tf("network.test.clone_wait_failed", &[("error", &error)]));
                break;
            }
        }
    }

    if let Some(receiver) = &progress_receiver {
        while let Ok(line) = receiver.try_recv() {
            if let Some(progress) = parse_git_progress(&line) {
                emit_download_channel(
                    sender,
                    DownloadChannelTestEvent::CloneProgress {
                        channel,
                        stage: progress.stage,
                        current: progress.current,
                        total: progress.total,
                        percentage: progress.percentage,
                    },
                );
            } else if !line.starts_with("warning:") && !line.trim().is_empty() {
                last_message = Some(line);
            }
        }
    }

    let status = child.wait();
    let elapsed = start.elapsed().as_millis() as u64;
    let result = match status {
        Ok(status) if status.success() => DownloadChannelTestResult {
            channel,
            success: true,
            latency_ms: Some(elapsed),
            error: None,
        },
        Ok(status) => DownloadChannelTestResult {
            channel,
            success: false,
            latency_ms: Some(elapsed),
            error: Some(last_message.unwrap_or_else(|| {
                tf("network.test.clone_failed", &[("code", &status.code().unwrap_or(-1))])
            })),
        },
        Err(error) => DownloadChannelTestResult {
            channel,
            success: false,
            latency_ms: Some(elapsed),
            error: Some(tf("network.test.clone_wait_failed", &[("error", &error)])),
        },
    };
    let _ = fs::remove_dir_all(&root);
    result
}

fn cancelled_item(key: &str, name: &str) -> GithubMultiTestItem {
    GithubMultiTestItem {
        key: key.to_owned(),
        name: name.to_owned(),
        success: false,
        latency_ms: None,
        error: Some(t("network.test.cancelled").to_owned()),
        warning: None,
    }
}

fn test_download(
    client: &reqwest::blocking::Client,
    _proxy_mode: &str,
    _proxy_host: &str,
    channel: DownloadChannel,
    accelerate_url: Option<&str>,
    sender: &Option<Sender<GithubTestEvent>>,
    cancel: &Arc<AtomicBool>,
) -> GithubMultiTestItem {
    let key = "speed";
    let name = t("network.test.download_speed");
    emit(
        sender,
        GithubTestEvent::ItemStarted {
            key: key.to_owned(),
            name: name.to_owned(),
        },
    );

    if cancel.load(Ordering::Relaxed) {
        return cancelled_item(key, name);
    }
    let root = unique_test_root();
    let path = root.join("SillyTavern-1.18.0.tar.gz");
    if let Err(error) = fs::create_dir_all(&root) {
        return GithubMultiTestItem {
            key: key.to_owned(),
            name: name.to_owned(),
            success: false,
            latency_ms: None,
            error: Some(tf("network.test.download_temp_dir_failed", &[("error", &error)])),
            warning: None,
        };
    }

    let url = accelerated_url(download_url(channel), accelerate_url);
    let start = Instant::now();
    let response = client.get(url).send();
    let result = match response {
        Ok(mut response) if response.status().is_success() => {
            let total_bytes = response.content_length();
            let mut file = match File::create(&path) {
                Ok(file) => file,
                Err(error) => {
                    let _ = fs::remove_dir_all(&root);
                    return GithubMultiTestItem {
                        key: key.to_owned(),
                        name: name.to_owned(),
                        success: false,
                        latency_ms: None,
                        error: Some(tf("network.test.download_file_create_failed", &[("error", &error)])),
                        warning: None,
                    };
                }
            };
            let mut downloaded_bytes = 0u64;
            let mut last_emit = Instant::now();
            let mut last_emit_bytes = 0u64;
            let mut buffer = [0u8; 32 * 1024];
            let mut read_error = None;

            emit(
                sender,
                GithubTestEvent::DownloadProgress {
                    total_bytes,
                    downloaded_bytes: 0,
                    bytes_per_second: 0,
                    percentage: Some(0.0).filter(|_| total_bytes.is_some()),
                },
            );

            loop {
                if cancel.load(Ordering::Relaxed) {
                    let _ = fs::remove_dir_all(&root);
                    return cancelled_item(key, name);
                }
                match response.read(&mut buffer) {
                    Ok(0) => break,
                    Ok(read) => {
                        if let Err(error) = file.write_all(&buffer[..read]) {
                            read_error = Some(tf("network.test.download_write_failed", &[("error", &error)]));
                            break;
                        }
                        downloaded_bytes += read as u64;
                        let elapsed = last_emit.elapsed();
                        if elapsed >= Duration::from_millis(100) {
                            let speed = ((downloaded_bytes - last_emit_bytes) as f64
                                / elapsed.as_secs_f64())
                                as u64;
                            emit(
                                sender,
                                GithubTestEvent::DownloadProgress {
                                    total_bytes,
                                    downloaded_bytes,
                                    bytes_per_second: speed,
                                    percentage: total_bytes.map(|total| {
                                        (downloaded_bytes as f32 / total.max(1) as f32 * 100.0)
                                            .min(100.0)
                                    }),
                                },
                            );
                            last_emit = Instant::now();
                            last_emit_bytes = downloaded_bytes;
                        }
                    }
                    Err(error) => {
                        read_error = Some(tf("network.test.download_failed", &[("error", &error)]));
                        break;
                    }
                }
            }

            let elapsed = start.elapsed();
            let average_speed = (downloaded_bytes as f64 / elapsed.as_secs_f64().max(0.001)) as u64;
            emit(
                sender,
                GithubTestEvent::DownloadProgress {
                    total_bytes,
                    downloaded_bytes,
                    bytes_per_second: average_speed,
                    percentage: total_bytes.map(|total| {
                        (downloaded_bytes as f32 / total.max(1) as f32 * 100.0).min(100.0)
                    }),
                },
            );

            if let Some(error) = read_error {
                GithubMultiTestItem {
                    key: key.to_owned(),
                    name: name.to_owned(),
                    success: false,
                    latency_ms: Some(elapsed.as_millis() as u64),
                    error: Some(error),
                    warning: None,
                }
            } else {
                GithubMultiTestItem {
                    key: key.to_owned(),
                    name: name.to_owned(),
                    success: true,
                    latency_ms: Some(elapsed.as_millis() as u64),
                    error: None,
                    warning: Some(speed_message(average_speed)),
                }
            }
        }
        Ok(mut response) => {
            let status = response.status();
            let mut success = false;
            let mut warning = None;
            let mut error = Some(tf("network.test.http_status", &[("status", &status)]));
            if accelerate_url.is_some() && (status.as_u16() == 403 || status.as_u16() == 404) {
                success = true;
                warning = Some(tf(
                    "network.test.accelerator_unusable_code",
                    &[("status_code", &status.as_u16())]
                ));
                error = None;
            } else {
                let mut body = String::new();
                let _ = response.read_to_string(&mut body);
            }
            GithubMultiTestItem {
                key: key.to_owned(),
                name: name.to_owned(),
                success,
                latency_ms: Some(start.elapsed().as_millis() as u64),
                error,
                warning,
            }
        }
        Err(error) => GithubMultiTestItem {
            key: key.to_owned(),
            name: name.to_owned(),
            success: false,
            latency_ms: None,
            error: Some(tf("network.test.speed_failed", &[("error", &error)])),
            warning: None,
        },
    };
    let _ = fs::remove_dir_all(&root);
    result
}

fn speed_message(bytes_per_second: u64) -> String {
    let mbps = bytes_per_second as f64 / 1_048_576.0;
    if mbps < 1.0 {
        tf("network.speed.slow", &[("speed", &format!("{:.1}", bytes_per_second as f64 / 1024.0))])
    } else if mbps < 4.0 {
        tf("network.speed.normal", &[("mbps", &format!("{mbps:.2}"))])
    } else if mbps < 10.0 {
        tf("network.speed.fast", &[("mbps", &format!("{mbps:.2}"))])
    } else {
        tf("network.speed.very_fast", &[("mbps", &format!("{mbps:.2}"))])
    }
}

/// 构造旧版风格的超时结果。
pub fn timeout_results() -> Vec<GithubMultiTestItem> {
    failed_results("network.test.timeout", true)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn protocol_tagged_proxy_entries_prefer_https() {
        // `ProxyServer` 写成 `http=...;https=...` 时，https 条目的覆盖面更广，应优先。
        assert_eq!(
            resolve_proxy_server("http=127.0.0.1:8080;https=127.0.0.1:8443"),
            "127.0.0.1:8443"
        );
        // 只有 http 条目时退回它。
        assert_eq!(
            resolve_proxy_server("http=127.0.0.1:8080;socks=127.0.0.1:1080"),
            "127.0.0.1:8080"
        );
        // socks 是最后的选择。
        assert_eq!(resolve_proxy_server("socks=127.0.0.1:1080"), "127.0.0.1:1080");
    }

    #[test]
    fn plain_proxy_server_is_used_verbatim() {
        // 没有 `=` 的单地址格式原样返回，前后空白要去掉。
        assert_eq!(resolve_proxy_server("  127.0.0.1:7890  "), "127.0.0.1:7890");
        assert_eq!(resolve_proxy_server(""), "");
    }

    #[test]
    fn ipconfig_ipv4_parsing_skips_loopback_and_link_local() {
        // 中文系统的真实格式：字段名与值之间用点号填充，末尾才是冒号。
        let fixture = "Windows IP 配置\n\n以太网适配器 以太网:\n\n   连接特定的 DNS 后缀 . . . . . . . : \n   IPv4 地址 . . . . . . . . . . . . : 192.168.8.20\n   子网掩码  . . . . . . . . . . . . : 255.255.255.0";
        assert_eq!(parse_lan_ipv4(fixture).as_deref(), Some("192.168.8.20"));
    }

    #[test]
    fn ipconfig_ipv4_parsing_handles_english_locale() {
        let fixture = "Windows IP Configuration\n\nEthernet adapter Ethernet:\n\n   IPv4 Address. . . . . . . . . . . : 10.0.0.7\n   Subnet Mask . . . . . . . . . . . : 255.0.0.0";
        assert_eq!(parse_lan_ipv4(fixture).as_deref(), Some("10.0.0.7"));
    }

    #[test]
    fn ipconfig_ipv4_parsing_ignores_unusable_addresses() {
        // 回环与自动私有地址（169.254.x.x）都不该被当成局域网地址返回。
        let fixture = "   IPv4 地址 . . . . . . . . . . . . : 127.0.0.1\n   IPv4 地址 . . . . . . . . . . . . : 169.254.10.20";
        assert_eq!(parse_lan_ipv4(fixture), None);
    }

    #[test]
    fn ipconfig_ipv6_parsing_skips_loopback_and_link_local() {
        let fixture = "   IPv6 地址 . . . . . . . . . . . . : 240a:42cc::1234\n   临时 IPv6 地址. . . . . . . . . . : 240a:42cc::5678";
        assert_eq!(parse_lan_ipv6(fixture).as_deref(), Some("240a:42cc::1234"));

        let link_local = "   IPv6 地址 . . . . . . . . . . . . : fe80::1234%12";
        assert_eq!(parse_lan_ipv6(link_local), None);

        let loopback = "   IPv6 地址 . . . . . . . . . . . . : ::1";
        assert_eq!(parse_lan_ipv6(loopback), None);
    }

    #[test]
    fn download_channels_have_expected_urls_and_keys() {
        assert_eq!(
            DownloadChannel::from_key("mirror_1"),
            DownloadChannel::Mirror1
        );
        assert_eq!(
            DownloadChannel::from_key("镜像 2"),
            DownloadChannel::Mirror2
        );
        assert_eq!(DownloadChannel::from_key("官方"), DownloadChannel::Official);
        assert_eq!(DownloadChannel::from_key("unknown"), DownloadChannel::Auto);
        // 已下线的 mirror3 设置回落到“自动”。
        assert_eq!(DownloadChannel::from_key("mirror3"), DownloadChannel::Auto);
        assert_eq!(
            DownloadChannel::Mirror1.clone_url(),
            "https://gitee.com/AstraBrew-Labs/SillyTavern.git"
        );
        assert_eq!(
            DownloadChannel::Mirror2.clone_url(),
            "https://gitcode.com/GitHub_Trending/si/SillyTavern.git"
        );
        assert_eq!(DownloadChannel::Official.clone_url(), GITHUB_CLONE_URL);
        assert_eq!(DownloadChannel::fixed_channels().len(), 3);
    }

    fn release_fixture(tag_name: &str, mirror: MirrorAvailability) -> SillyTavernRelease {
        SillyTavernRelease {
            version: tag_name.to_owned(),
            tag_name: tag_name.to_owned(),
            published_at: String::new(),
            created_at: String::new(),
            body: String::new(),
            mirror,
        }
    }

    /// 镜像站存在该 tag 时必须判定为已同步，探测失败必须判定为未知而不是未同步。
    #[test]
    fn mirror_state_distinguishes_absent_tags_from_failed_probes() {
        let tags = vec!["1.18.0".to_owned(), "v1.19.0".to_owned()];
        assert_eq!(
            mirror_state(DownloadChannel::Mirror1, Some(&tags), "1.19.0"),
            MirrorAvailability::Synced
        );
        assert_eq!(
            mirror_state(DownloadChannel::Mirror1, Some(&tags), "1.20.0"),
            MirrorAvailability::NotSynced
        );
        assert_eq!(
            mirror_state(DownloadChannel::Mirror1, None, "1.20.0"),
            MirrorAvailability::Unknown
        );
        assert_eq!(
            mirror_state(DownloadChannel::Official, None, "1.20.0"),
            MirrorAvailability::Official
        );
    }

    /// 重新判定会覆盖上一轮留下的旧标记，避免缓存里的旧状态长期生效。
    #[test]
    fn apply_mirror_availability_overwrites_stale_state() {
        let releases = vec![release_fixture("1.19.0", MirrorAvailability::NotSynced)];
        let releases = apply_mirror_availability(
            releases,
            DownloadChannel::Mirror1,
            Some(&["1.19.0".to_owned()]),
        );
        assert_eq!(releases[0].mirror, MirrorAvailability::Synced);

        let staging = SillyTavernStaging {
            branch: "staging".to_owned(),
            commit_sha: String::new(),
            committed_at: String::new(),
            message: String::new(),
            mirror: MirrorAvailability::Synced,
        };
        let staging = apply_staging_mirror_availability(staging, DownloadChannel::Mirror2, None);
        assert_eq!(staging.mirror, MirrorAvailability::Unknown);
    }

    /// 镜像 ref 快照只在同渠道且未过期时可复用。
    #[test]
    fn mirror_ref_snapshot_is_only_reused_for_same_channel() {
        let now = 1_000_000;
        let snapshot = MirrorRefsCache {
            channel: DownloadChannel::Mirror1,
            tags: vec!["1.18.0".to_owned()],
            tags_checked_at: now,
            branches: vec!["staging".to_owned()],
            branches_checked_at: now,
        };
        assert!(snapshot.tags_fresh_for(DownloadChannel::Mirror1, now + 60));
        assert!(!snapshot.tags_fresh_for(
            DownloadChannel::Mirror1,
            now + MIRROR_REFS_CACHE_TTL
        ));
        assert!(!snapshot.tags_fresh_for(DownloadChannel::Mirror2, now + 60));
        assert!(
            snapshot
                .for_channel(DownloadChannel::Mirror2)
                .tags
                .is_empty()
        );
        assert_eq!(
            snapshot.for_channel(DownloadChannel::Mirror1).tags,
            snapshot.tags
        );
    }

    #[test]
    fn fastest_channel_ignores_failed_results() {
        let results = vec![
            DownloadChannelTestResult {
                channel: DownloadChannel::Mirror1,
                success: false,
                latency_ms: Some(10),
                error: Some("failed".into()),
            },
            DownloadChannelTestResult {
                channel: DownloadChannel::Mirror2,
                success: true,
                latency_ms: Some(80),
                error: None,
            },
            DownloadChannelTestResult {
                channel: DownloadChannel::Official,
                success: true,
                latency_ms: Some(120),
                error: None,
            },
        ];
        assert_eq!(
            fastest_download_channel(&results),
            Some(DownloadChannel::Mirror2)
        );
    }

    #[test]
    fn all_failed_channel_selection_falls_back_to_official() {
        let results = vec![
            DownloadChannelTestResult {
                channel: DownloadChannel::Mirror1,
                success: false,
                latency_ms: Some(10),
                error: None,
            },
            DownloadChannelTestResult {
                channel: DownloadChannel::Mirror2,
                success: false,
                latency_ms: None,
                error: None,
            },
            DownloadChannelTestResult {
                channel: DownloadChannel::Official,
                success: false,
                latency_ms: Some(30),
                error: None,
            },
        ];
        assert_eq!(fastest_download_channel(&results), None);
    }

    #[test]
    fn test_urls_use_the_requested_clone_and_archive_endpoints() {
        assert_eq!(
            GITHUB_CLONE_URL,
            "https://github.com/SillyTavern/SillyTavern.git"
        );
        assert_eq!(
            GITHUB_DOWNLOAD_URL,
            "https://github.com/SillyTavern/SillyTavern/archive/refs/tags/1.18.0.tar.gz"
        );
    }

    #[test]
    fn parses_git_progress_stage_without_percentage() {
        let progress = parse_git_progress("Enumerating objects: 123, done.").expect("progress");
        assert_eq!(progress.stage, "Enumerating objects");
        assert_eq!(progress.percentage, None);
    }

    #[test]
    fn parses_git_progress_with_counts_and_percentage() {
        let progress = parse_git_progress("Receiving objects: 42% (42/100), 1.2 MiB | 2.3 MiB/s")
            .expect("progress");
        assert_eq!(progress.stage, "Receiving objects");
        assert_eq!(progress.current, Some(42));
        assert_eq!(progress.total, Some(100));
        assert_eq!(progress.percentage, Some(42.0));
    }

    #[test]
    fn normalizes_proxy_urls_and_prefers_https_in_proxy_lists() {
        assert_eq!(
            normalize_proxy_url("127.0.0.1:7890").unwrap(),
            "http://127.0.0.1:7890"
        );
        assert_eq!(
            normalize_system_proxy_url("http=plain.proxy:8080;https=secure.proxy:8443"),
            Some("http://secure.proxy:8443".to_owned())
        );
    }

    #[test]
    fn timeout_results_include_clone_and_download_checks() {
        let results = timeout_results();
        assert_eq!(results.len(), 6);
        assert_eq!(results[4].key, "clone");
        assert_eq!(results[5].key, "speed");
    }
}

/// 当前有效下载渠道对某个版本的镜像同步状态。
///
/// 必须区分「镜像确认未同步」与「本次无法确认」：把探测失败当成镜像缺失，
/// 会让界面显示错误的“镜像未同步”，所以这里用三态而不是布尔值。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum MirrorAvailability {
    /// 尚未判定或本次无法确认，下载时会重新确认一次。
    #[default]
    Unknown,
    /// 有效渠道就是官方直连，不涉及镜像同步。
    Official,
    /// 镜像站已存在对应的 tag 或分支。
    Synced,
    /// 镜像站确认不存在对应的 tag 或分支，安装时会回退官方直连。
    NotSynced,
}

impl MirrorAvailability {
    /// 该状态对应的中文文案，英文由语言层自动翻译。
    pub const fn label_key(self) -> &'static str {
        match self {
            Self::Official => "network.mirror_availability.official",
            Self::Synced => "network.mirror_availability.synced",
            Self::NotSynced => "network.mirror_availability.not_synced",
            Self::Unknown => "network.mirror_availability.unknown",
        }
    }
}

/// 在线酒馆稳定发行版本。
#[derive(Debug, Clone, PartialEq, Eq)]
#[allow(dead_code)]
pub struct SillyTavernRelease {
    /// 展示用的版本号，例如 `1.18.0`。
    pub version: String,
    /// GitHub 的原始 tag，用于 checkout。
    pub tag_name: String,
    /// GitHub Release 的发布时间。
    pub published_at: String,
    /// GitHub Release 的创建时间。
    pub created_at: String,
    /// GitHub Release body，保持 Markdown 原文。
    pub body: String,
    /// 当前有效下载渠道对该 tag 的镜像同步状态。
    pub mirror: MirrorAvailability,
}

/// staging 分支的最新状态。
#[derive(Debug, Clone, PartialEq, Eq)]
#[allow(dead_code)]
pub struct SillyTavernStaging {
    pub branch: String,
    pub commit_sha: String,
    pub committed_at: String,
    pub message: String,
    /// 当前有效下载渠道对该分支的镜像同步状态。
    pub mirror: MirrorAvailability,
}

/// 在线版本目录读取结果。
#[derive(Debug, Clone)]
#[allow(dead_code)]
pub struct SillyTavernCatalog {
    pub branch: String,
    pub releases: Vec<SillyTavernRelease>,
    pub staging: Option<SillyTavernStaging>,
    /// 本次用于判断镜像标签的实际下载渠道。
    pub resolved_channel: DownloadChannel,
    /// 版本数据实际写入缓存的时间戳，用于避免页面进入时显示“刚刚更新”。
    pub cached_at: u64,
    /// 是否因为网络请求失败而复用了过期缓存。
    pub used_stale_cache: bool,
}

/// 规范目录中当前安装的酒馆 Git 状态。
#[derive(Debug, Clone, PartialEq, Eq)]
#[allow(dead_code)]
pub struct InstalledSillyTavern {
    pub tag_name: Option<String>,
    pub branch: Option<String>,
    pub head: String,
}

/// 在线酒馆安装过程发送给界面的事件。
#[derive(Debug, Clone)]
#[allow(dead_code)]
pub enum SillyTavernInstallEvent {
    Log(String),
    DownloadComplete,
    InstallStarted,
    Cancelled,
    Completed(Result<(), String>),
}

/// 在线酒馆安装目标，可以是稳定版 tag 或 staging 分支。
#[derive(Debug, Clone, PartialEq, Eq)]
#[allow(dead_code)]
pub enum SillyTavernInstallTarget {
    Tag(String),
    Branch(String),
}

const SILLYTAVERN_API_MIRROR: &str =
    "https://gh-proxy.org/https://api.github.com/repos/SillyTavern/SillyTavern";
const SILLYTAVERN_API_DIRECT: &str = "https://api.github.com/repos/SillyTavern/SillyTavern";
const SILLYTAVERN_CACHE_NAME: &str = "sillytavern_versions_cache.json";
const SILLYTAVERN_CACHE_TTL: u64 = 7 * 24 * 60 * 60;
/// 镜像 ref 快照缓存文件名。
const MIRROR_REFS_CACHE_NAME: &str = "sillytavern_mirror_refs_cache.json";
/// 镜像 ref 快照有效期。镜像同步延迟以分钟计，10 分钟足够新，
/// 同时避免每次进入版本页都执行 `git ls-remote`。
const MIRROR_REFS_CACHE_TTL: u64 = 10 * 60;

/// 在线酒馆安装目录：`%AppData%/AstraBrew Launcher/sillytavern/`。
#[allow(dead_code)]
pub fn sillytavern_install_dir() -> PathBuf {
    crate::utils::app_paths().sillytavern_dir()
}

/// 在线版本缓存文件路径：`%Temp%/astrabrew-launcher/caches/`。
#[allow(dead_code)]
pub fn sillytavern_versions_cache_path() -> PathBuf {
    crate::utils::app_paths()
        .caches
        .join(SILLYTAVERN_CACHE_NAME)
}

/// 读取规范在线酒馆目录当前精确检出的 Git tag。
#[allow(dead_code)]
pub fn installed_sillytavern_tag() -> Option<String> {
    installed_sillytavern_state().and_then(|state| state.tag_name)
}

/// 读取规范在线酒馆目录的 tag、分支和 HEAD，供重启时恢复 UI 状态。
#[allow(dead_code)]
pub fn installed_sillytavern_state() -> Option<InstalledSillyTavern> {
    let target = sillytavern_install_dir();
    let target = target.to_str()?;
    if !sillytavern_install_dir().is_dir() {
        return None;
    }
    let head = git_output(&["-C", target, "rev-parse", "HEAD"])?;
    let tag_name = git_output(&["-C", target, "describe", "--tags", "--exact-match", "HEAD"]);
    let branch =
        git_output(&["-C", target, "branch", "--show-current"]).filter(|branch| !branch.is_empty());
    Some(InstalledSillyTavern {
        tag_name,
        branch,
        head,
    })
}

fn git_output(args: &[&str]) -> Option<String> {
    let mut command = Command::new(resolve_command("git"));
    crate::core::env::apply_no_window_to_command(&mut command);
    let output = command.args(args).output().ok()?;
    if !output.status.success() {
        return None;
    }
    let value = String::from_utf8(output.stdout).ok()?.trim().to_owned();
    (!value.is_empty()).then_some(value)
}

#[derive(Debug, Clone)]
struct SillyTavernVersionCache {
    release_cached_at: u64,
    staging_cached_at: u64,
    releases: Vec<SillyTavernRelease>,
    /// 旧版缓存可能没有 body，缺少 body 时必须重新请求一次 Release API。
    release_body_complete: bool,
    staging: Option<SillyTavernStaging>,
}

impl SillyTavernVersionCache {
    fn cached_at_for(&self, branch: &str) -> u64 {
        if branch == "staging" {
            self.staging_cached_at
        } else {
            self.release_cached_at
        }
    }

    fn is_fresh_at(&self, branch: &str, now: u64) -> bool {
        let cached_at = if branch == "staging" {
            self.staging_cached_at
        } else {
            self.release_cached_at
        };
        cached_at != 0
            && now.saturating_sub(cached_at) < SILLYTAVERN_CACHE_TTL
            && (branch == "staging" || self.release_body_complete)
    }
}

fn load_sillytavern_versions_cache() -> Option<SillyTavernVersionCache> {
    let contents = fs::read_to_string(sillytavern_versions_cache_path()).ok()?;
    let value = serde_json::from_str::<serde_json::Value>(&contents).ok()?;
    let object = value.as_object()?;
    let legacy_cached_at = object
        .get("cached_at")
        .and_then(serde_json::Value::as_u64)
        .unwrap_or(0);
    let release_cached_at = object
        .get("release_cached_at")
        .and_then(serde_json::Value::as_u64)
        .unwrap_or(legacy_cached_at);
    let staging_cached_at = object
        .get("staging_cached_at")
        .and_then(serde_json::Value::as_u64)
        .unwrap_or(0);
    let mut release_body_complete = true;
    let releases = object
        .get("releases")
        .and_then(serde_json::Value::as_array)
        .map(|items| {
            items
                .iter()
                .filter_map(|item| {
                    let item = item.as_object()?;
                    if !item.contains_key("body") {
                        release_body_complete = false;
                    }
                    Some(SillyTavernRelease {
                        version: item.get("version")?.as_str()?.to_owned(),
                        tag_name: item.get("tag_name")?.as_str()?.to_owned(),
                        published_at: item
                            .get("published_at")
                            .and_then(serde_json::Value::as_str)
                            .unwrap_or_default()
                            .to_owned(),
                        created_at: item
                            .get("created_at")
                            .and_then(serde_json::Value::as_str)
                            .unwrap_or_default()
                            .to_owned(),
                        body: item
                            .get("body")
                            .and_then(serde_json::Value::as_str)
                            .unwrap_or_default()
                            .to_owned(),
                        // 镜像同步状态不随版本元数据缓存，统一由目录返回前重新判定。
                        mirror: MirrorAvailability::Unknown,
                    })
                })
                .collect::<Vec<_>>()
        })
        .unwrap_or_default();
    let staging = object.get("staging").and_then(|value| {
        let item = value.as_object()?;
        Some(SillyTavernStaging {
            branch: item.get("branch")?.as_str()?.to_owned(),
            commit_sha: item.get("commit_sha")?.as_str()?.to_owned(),
            committed_at: item.get("committed_at")?.as_str()?.to_owned(),
            message: item.get("message")?.as_str()?.to_owned(),
            mirror: MirrorAvailability::Unknown,
        })
    });
    if releases.is_empty() && staging.is_none() {
        return None;
    }
    Some(SillyTavernVersionCache {
        release_cached_at,
        staging_cached_at,
        releases,
        release_body_complete,
        staging,
    })
}

fn save_sillytavern_versions_cache(
    branch: &str,
    channel: DownloadChannel,
    releases: &[SillyTavernRelease],
    staging: Option<&SillyTavernStaging>,
) -> io::Result<()> {
    let old = load_sillytavern_versions_cache();
    let now = unix_seconds().unwrap_or_default();
    let release_cached_at = if branch == "release" {
        now
    } else {
        old.as_ref()
            .map(|cache| cache.release_cached_at)
            .unwrap_or(0)
    };
    let staging_cached_at = if branch == "staging" {
        now
    } else {
        old.as_ref()
            .map(|cache| cache.staging_cached_at)
            .unwrap_or(0)
    };
    let cached_releases = if branch == "release" {
        releases.to_vec()
    } else {
        old.as_ref()
            .map(|cache| cache.releases.clone())
            .unwrap_or_default()
    };
    let cached_staging = if branch == "staging" {
        staging.cloned()
    } else {
        old.as_ref().and_then(|cache| cache.staging.clone())
    };
    let path = sillytavern_versions_cache_path();
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }
    let value = serde_json::json!({
        "release_cached_at": release_cached_at,
        "staging_cached_at": staging_cached_at,
        "mirror_channel": channel.key(),
        "releases": cached_releases.iter().map(|release| serde_json::json!({
            "version": release.version,
            "tag_name": release.tag_name,
            "published_at": release.published_at,
            "created_at": release.created_at,
            "body": release.body,
        })).collect::<Vec<_>>(),
        "staging": cached_staging.map(|item| serde_json::json!({
            "branch": item.branch,
            "commit_sha": item.commit_sha,
            "committed_at": item.committed_at,
            "message": item.message,
        })),
    });
    let temporary = path.with_extension("json.tmp");
    fs::write(
        &temporary,
        serde_json::to_vec_pretty(&value).map_err(io::Error::other)?,
    )?;
    fs::rename(temporary, path)
}

/// 按分支读取在线版本。有效缓存不会触发网络请求，过期后镜像失败则回退直连和旧缓存。
///
/// 版本元数据可以缓存，但镜像同步状态每次都会按当前下载渠道重新判定，
/// 避免渠道切换或镜像站新同步的 tag 被旧标记长期覆盖。
#[allow(dead_code)]
pub fn fetch_sillytavern_catalog(
    branch: &str,
    selected_channel: DownloadChannel,
    proxy_mode: &str,
    proxy_host: &str,
) -> Result<SillyTavernCatalog, String> {
    let now = unix_seconds().unwrap_or_default();
    let channel = resolve_download_channel(selected_channel);
    let cached = load_sillytavern_versions_cache();
    if let Some(cache) = cached.as_ref()
        && cache.is_fresh_at(branch, now)
    {
        let mut catalog = catalog_from_cache(cache, branch, channel);
        refresh_catalog_mirror_state(&mut catalog, channel, proxy_mode, proxy_host, now);
        return Ok(catalog);
    }

    let client = build_client(proxy_mode, proxy_host);
    let result = client.and_then(|client| {
        if branch == "staging" {
            fetch_sillytavern_staging(&client, SILLYTAVERN_API_MIRROR).or_else(|mirror_error| {
                fetch_sillytavern_staging(&client, SILLYTAVERN_API_DIRECT).map_err(|direct_error| {
                    tf("network.catalog.fetch_failed", &[("mirror_error", &mirror_error), ("direct_error", &direct_error)])
                })
            })
        } else {
            fetch_sillytavern_releases(&client, SILLYTAVERN_API_MIRROR)
                .map(FetchedCatalog::Release)
                .or_else(|mirror_error| {
                    fetch_sillytavern_releases(&client, SILLYTAVERN_API_DIRECT)
                        .map(FetchedCatalog::Release)
                        .map_err(|direct_error| {
                            tf("network.catalog.fetch_failed", &[("mirror_error", &mirror_error), ("direct_error", &direct_error)])
                        })
                })
        }
    });

    match result {
        Ok(FetchedCatalog::Release(mut releases)) => {
            releases.truncate(10);
            let mut catalog = SillyTavernCatalog {
                branch: "release".to_owned(),
                releases,
                staging: cached.and_then(|item| item.staging),
                resolved_channel: channel,
                cached_at: now,
                used_stale_cache: false,
            };
            refresh_catalog_mirror_state(&mut catalog, channel, proxy_mode, proxy_host, now);
            save_sillytavern_versions_cache(
                "release",
                channel,
                &catalog.releases,
                catalog.staging.as_ref(),
            )
            .map_err(|error| tf("network.cache.save_failed", &[("error", &error)]))?;
            catalog.cached_at = unix_seconds().unwrap_or_default();
            Ok(catalog)
        }
        Ok(FetchedCatalog::Staging(staging)) => {
            let mut catalog = SillyTavernCatalog {
                branch: "staging".to_owned(),
                releases: Vec::new(),
                staging: Some(staging),
                resolved_channel: channel,
                cached_at: now,
                used_stale_cache: false,
            };
            refresh_catalog_mirror_state(&mut catalog, channel, proxy_mode, proxy_host, now);
            save_sillytavern_versions_cache("staging", channel, &[], catalog.staging.as_ref())
                .map_err(|error| tf("network.cache.save_failed", &[("error", &error)]))?;
            catalog.cached_at = unix_seconds().unwrap_or_default();
            Ok(catalog)
        }
        Err(error) => match cached
            .as_ref()
            .map(|cache| catalog_from_cache(cache, branch, channel))
        {
            Some(mut catalog) => {
                catalog.used_stale_cache = true;
                // 旧缓存同样要重新判定镜像状态，镜像站通常是可访问的。
                refresh_catalog_mirror_state(&mut catalog, channel, proxy_mode, proxy_host, now);
                Ok(catalog)
            }
            None => Err(error),
        },
    }
}

enum FetchedCatalog {
    Release(Vec<SillyTavernRelease>),
    Staging(SillyTavernStaging),
}

fn catalog_from_cache(
    cache: &SillyTavernVersionCache,
    branch: &str,
    channel: DownloadChannel,
) -> SillyTavernCatalog {
    if branch == "staging" {
        SillyTavernCatalog {
            branch: "staging".to_owned(),
            releases: Vec::new(),
            staging: cache.staging.clone(),
            resolved_channel: channel,
            cached_at: cache.cached_at_for(branch),
            used_stale_cache: false,
        }
    } else {
        SillyTavernCatalog {
            branch: "release".to_owned(),
            releases: cache.releases.iter().take(10).cloned().collect(),
            staging: cache.staging.clone(),
            resolved_channel: channel,
            cached_at: cache.cached_at_for(branch),
            used_stale_cache: false,
        }
    }
}

fn fetch_sillytavern_releases(
    client: &reqwest::blocking::Client,
    api_base: &str,
) -> Result<Vec<SillyTavernRelease>, String> {
    let mut releases = Vec::new();
    let mut page = 1_u32;
    loop {
        let url = format!("{api_base}/releases?per_page=100&page={page}");
        let response = client
            .get(url)
            .header("Accept", "application/vnd.github+json")
            .header("User-Agent", "AstraBrew-Launcher")
            .send()
            .map_err(|error| error.to_string())?;
        if !response.status().is_success() {
            return Err(format!("HTTP {}", response.status()));
        }
        let body = response.text().map_err(|error| error.to_string())?;
        let items = serde_json::from_str::<serde_json::Value>(&body)
            .map_err(|error| tf("network.release.parse_failed", &[("error", &error)]))?;
        let Some(items) = items.as_array() else {
            return Err(t("network.release.parse_failed_bare").to_owned());
        };
        if items.is_empty() {
            break;
        }
        for item in items {
            let Some(item) = item.as_object() else {
                continue;
            };
            if item
                .get("draft")
                .and_then(serde_json::Value::as_bool)
                .unwrap_or(false)
                || item
                    .get("prerelease")
                    .and_then(serde_json::Value::as_bool)
                    .unwrap_or(false)
            {
                continue;
            }
            let Some(tag_name) = item.get("tag_name").and_then(serde_json::Value::as_str) else {
                continue;
            };
            releases.push(SillyTavernRelease {
                version: normalize_release_version(tag_name),
                tag_name: tag_name.to_owned(),
                published_at: item
                    .get("published_at")
                    .and_then(serde_json::Value::as_str)
                    .unwrap_or_default()
                    .to_owned(),
                created_at: item
                    .get("created_at")
                    .and_then(serde_json::Value::as_str)
                    .unwrap_or_default()
                    .to_owned(),
                body: item
                    .get("body")
                    .and_then(serde_json::Value::as_str)
                    .unwrap_or_default()
                    .to_owned(),
                // 具体渠道的同步状态在目录返回前统一判定。
                mirror: MirrorAvailability::Unknown,
            });
        }
        if items.len() < 100 {
            break;
        }
        page = page
            .checked_add(1)
            .ok_or_else(|| t("network.release.page_overflow").to_owned())?;
    }
    releases.sort_by(compare_releases_desc);
    releases.dedup_by(|left, right| left.tag_name == right.tag_name);
    if releases.is_empty() {
        return Err(t("network.release.no_stable").to_owned());
    }
    Ok(releases)
}

fn fetch_sillytavern_staging(
    client: &reqwest::blocking::Client,
    api_base: &str,
) -> Result<FetchedCatalog, String> {
    let response = client
        .get(format!("{api_base}/branches/staging"))
        .header("Accept", "application/vnd.github+json")
        .header("User-Agent", "AstraBrew-Launcher")
        .send()
        .map_err(|error| error.to_string())?;
    if !response.status().is_success() {
        return Err(format!("HTTP {}", response.status()));
    }
    let body = response.text().map_err(|error| error.to_string())?;
    let value = serde_json::from_str::<serde_json::Value>(&body)
        .map_err(|error| tf("network.branch.parse_failed", &[("error", &error)]))?;
    let object = value
        .as_object()
        .ok_or_else(|| t("network.branch.parse_failed_bare").to_owned())?;
    let commit = object
        .get("commit")
        .and_then(serde_json::Value::as_object)
        .ok_or_else(|| t("network.branch.missing_commit").to_owned())?;
    let commit_detail = commit
        .get("commit")
        .and_then(serde_json::Value::as_object)
        .ok_or_else(|| t("network.branch.missing_commit_detail").to_owned())?;
    let author = commit_detail
        .get("author")
        .and_then(serde_json::Value::as_object);
    Ok(FetchedCatalog::Staging(SillyTavernStaging {
        branch: "staging".to_owned(),
        commit_sha: commit
            .get("sha")
            .and_then(serde_json::Value::as_str)
            .unwrap_or_default()
            .to_owned(),
        committed_at: author
            .and_then(|item| item.get("date"))
            .and_then(serde_json::Value::as_str)
            .unwrap_or_default()
            .to_owned(),
        message: commit_detail
            .get("message")
            .and_then(serde_json::Value::as_str)
            .unwrap_or_default()
            .lines()
            .next()
            .unwrap_or_default()
            .to_owned(),
        // 具体渠道的同步状态在目录返回前统一判定。
        mirror: MirrorAvailability::Unknown,
    }))
}

fn normalize_release_version(tag_name: &str) -> String {
    tag_name.trim().trim_start_matches(['v', 'V']).to_owned()
}

fn compare_releases_desc(
    left: &SillyTavernRelease,
    right: &SillyTavernRelease,
) -> std::cmp::Ordering {
    right
        .published_at
        .cmp(&left.published_at)
        .then_with(|| {
            parse_release_version(&right.version).cmp(&parse_release_version(&left.version))
        })
        .then_with(|| right.tag_name.cmp(&left.tag_name))
}

fn parse_release_version(version: &str) -> Option<(u64, u64, u64)> {
    let mut parts = version.split('.');
    let major = parts.next()?.parse().ok()?;
    let minor = parts.next()?.parse().ok()?;
    let patch_text = parts.next()?;
    let patch_end = patch_text
        .find(|character: char| !character.is_ascii_digit())
        .unwrap_or(patch_text.len());
    let patch = patch_text[..patch_end].parse().ok()?;
    Some((major, minor, patch))
}

/// 镜像 ref 快照缓存路径：`%Temp%/astrabrew-launcher/caches/sillytavern_mirror_refs_cache.json`。
fn mirror_refs_cache_path() -> PathBuf {
    crate::utils::app_paths()
        .caches
        .join(MIRROR_REFS_CACHE_NAME)
}

/// 最近一次成功探测到的镜像 ref 快照。
///
/// 该快照只用于判断镜像是否已经同步某个 tag / 分支，与版本缓存分离，
/// 这样即使版本元数据命中缓存，也能按当前渠道重新判定同步状态。
#[derive(Debug, Clone)]
struct MirrorRefsCache {
    /// 快照所属的下载渠道；渠道变化后必须重新探测。
    channel: DownloadChannel,
    tags: Vec<String>,
    tags_checked_at: u64,
    branches: Vec<String>,
    branches_checked_at: u64,
}

impl MirrorRefsCache {
    fn empty_for(channel: DownloadChannel) -> Self {
        Self {
            channel,
            tags: Vec::new(),
            tags_checked_at: 0,
            branches: Vec::new(),
            branches_checked_at: 0,
        }
    }

    fn tags_fresh_for(&self, channel: DownloadChannel, now: u64) -> bool {
        self.channel == channel
            && self.tags_checked_at != 0
            && now.saturating_sub(self.tags_checked_at) < MIRROR_REFS_CACHE_TTL
    }

    fn branches_fresh_for(&self, channel: DownloadChannel, now: u64) -> bool {
        self.channel == channel
            && self.branches_checked_at != 0
            && now.saturating_sub(self.branches_checked_at) < MIRROR_REFS_CACHE_TTL
    }

    /// 返回同渠道的快照副本；渠道不一致时返回空快照，避免把别的渠道的 ref 当作本渠道。
    fn for_channel(&self, channel: DownloadChannel) -> Self {
        if self.channel == channel {
            self.clone()
        } else {
            Self::empty_for(channel)
        }
    }
}

fn load_mirror_refs_cache() -> Option<MirrorRefsCache> {
    let contents = fs::read_to_string(mirror_refs_cache_path()).ok()?;
    let value = serde_json::from_str::<serde_json::Value>(&contents).ok()?;
    let object = value.as_object()?;
    let channel = object
        .get("channel")
        .and_then(serde_json::Value::as_str)
        .map(DownloadChannel::from_key)?;
    let collect = |key: &str| {
        object
            .get(key)
            .and_then(serde_json::Value::as_array)
            .map(|items| {
                items
                    .iter()
                    .filter_map(|item| item.as_str().map(str::to_owned))
                    .collect::<Vec<_>>()
            })
            .unwrap_or_default()
    };
    Some(MirrorRefsCache {
        channel,
        tags: collect("tags"),
        tags_checked_at: object
            .get("tags_checked_at")
            .and_then(serde_json::Value::as_u64)
            .unwrap_or(0),
        branches: collect("branches"),
        branches_checked_at: object
            .get("branches_checked_at")
            .and_then(serde_json::Value::as_u64)
            .unwrap_or(0),
    })
}

fn save_mirror_refs_cache(cache: &MirrorRefsCache) -> io::Result<()> {
    let path = mirror_refs_cache_path();
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }
    let value = serde_json::json!({
        "channel": cache.channel.key(),
        "tags": cache.tags,
        "tags_checked_at": cache.tags_checked_at,
        "branches": cache.branches,
        "branches_checked_at": cache.branches_checked_at,
    });
    let temporary = path.with_extension("json.tmp");
    fs::write(
        &temporary,
        serde_json::to_vec_pretty(&value).map_err(io::Error::other)?,
    )?;
    fs::rename(temporary, path)
}

/// 镜像 tag 探测结果：`items` 为可用 tag，`snapshot` 表示需要写回的新快照。
struct MirrorTagProbe {
    items: Option<Vec<String>>,
    snapshot: Option<MirrorRefsCache>,
}

/// 镜像分支探测结果：`items` 为可用分支，`snapshot` 表示需要写回的新快照。
struct MirrorBranchProbe {
    items: Option<Vec<String>>,
    snapshot: Option<MirrorRefsCache>,
}

/// 读取镜像 tag 列表。
///
/// 同渠道的新鲜快照直接复用；过期或渠道变化时重新探测；探测失败时回退到
/// 同渠道的旧快照。`items` 为 `None` 表示确实无法确认，此时必须显示未知状态，
/// 不能当作「镜像未同步」。
fn probe_mirror_tags(
    channel: DownloadChannel,
    proxy_mode: &str,
    proxy_host: &str,
    cached: Option<&MirrorRefsCache>,
    now: u64,
) -> MirrorTagProbe {
    if channel == DownloadChannel::Official {
        return MirrorTagProbe {
            items: None,
            snapshot: None,
        };
    }
    if let Some(cache) = cached
        && cache.tags_fresh_for(channel, now)
    {
        return MirrorTagProbe {
            items: Some(cache.tags.clone()),
            snapshot: None,
        };
    }
    match list_remote_tags(channel, proxy_mode, proxy_host) {
        Ok(tags) => {
            let mut snapshot = cached
                .map(|cache| cache.for_channel(channel))
                .unwrap_or_else(|| MirrorRefsCache::empty_for(channel));
            snapshot.channel = channel;
            snapshot.tags = tags.clone();
            snapshot.tags_checked_at = now;
            MirrorTagProbe {
                items: Some(tags),
                snapshot: Some(snapshot),
            }
        }
        Err(_) => MirrorTagProbe {
            items: cached
                .filter(|cache| cache.channel == channel)
                .map(|cache| cache.tags.clone()),
            snapshot: None,
        },
    }
}

/// 读取镜像分支列表，规则与 `probe_mirror_tags` 一致。
fn probe_mirror_branches(
    channel: DownloadChannel,
    proxy_mode: &str,
    proxy_host: &str,
    cached: Option<&MirrorRefsCache>,
    now: u64,
) -> MirrorBranchProbe {
    if channel == DownloadChannel::Official {
        return MirrorBranchProbe {
            items: None,
            snapshot: None,
        };
    }
    if let Some(cache) = cached
        && cache.branches_fresh_for(channel, now)
    {
        return MirrorBranchProbe {
            items: Some(cache.branches.clone()),
            snapshot: None,
        };
    }
    match list_remote_branches(channel, proxy_mode, proxy_host) {
        Ok(branches) => {
            let mut snapshot = cached
                .map(|cache| cache.for_channel(channel))
                .unwrap_or_else(|| MirrorRefsCache::empty_for(channel));
            snapshot.channel = channel;
            snapshot.branches = branches.clone();
            snapshot.branches_checked_at = now;
            MirrorBranchProbe {
                items: Some(branches),
                snapshot: Some(snapshot),
            }
        }
        Err(_) => MirrorBranchProbe {
            items: cached
                .filter(|cache| cache.channel == channel)
                .map(|cache| cache.branches.clone()),
            snapshot: None,
        },
    }
}

/// 刷新目录中的镜像同步状态。
///
/// 版本缓存只保存版本元数据，镜像同步状态每次返回目录时按当前渠道重新判定：
/// 否则切换下载渠道、或镜像站刚刚同步的新 tag，都会被旧缓存里的标记长期覆盖。
/// 只有在探测成功时才写回镜像 ref 快照，避免把探测失败固化成「未同步」。
fn refresh_catalog_mirror_state(
    catalog: &mut SillyTavernCatalog,
    channel: DownloadChannel,
    proxy_mode: &str,
    proxy_host: &str,
    now: u64,
) {
    if channel == DownloadChannel::Official {
        for release in &mut catalog.releases {
            release.mirror = MirrorAvailability::Official;
        }
        if let Some(staging) = catalog.staging.as_mut() {
            staging.mirror = MirrorAvailability::Official;
        }
        return;
    }

    let mut cache = load_mirror_refs_cache();
    let mut dirty = false;
    if !catalog.releases.is_empty() {
        let probe = probe_mirror_tags(channel, proxy_mode, proxy_host, cache.as_ref(), now);
        if let Some(snapshot) = probe.snapshot {
            cache = Some(snapshot);
            dirty = true;
        }
        catalog.releases = apply_mirror_availability(
            std::mem::take(&mut catalog.releases),
            channel,
            probe.items.as_deref(),
        );
    }
    if let Some(staging) = catalog.staging.clone() {
        let probe = probe_mirror_branches(channel, proxy_mode, proxy_host, cache.as_ref(), now);
        if let Some(snapshot) = probe.snapshot {
            cache = Some(snapshot);
            dirty = true;
        }
        catalog.staging = Some(apply_staging_mirror_availability(
            staging,
            channel,
            probe.items.as_deref(),
        ));
    }
    if dirty && let Some(cache) = cache.as_ref() {
        let _ = save_mirror_refs_cache(cache);
    }
}

/// 依据镜像 tag 列表刷新每个版本的同步状态。
fn apply_mirror_availability(
    mut releases: Vec<SillyTavernRelease>,
    channel: DownloadChannel,
    tags: Option<&[String]>,
) -> Vec<SillyTavernRelease> {
    for release in &mut releases {
        release.mirror = mirror_state(channel, tags, &release.tag_name);
    }
    releases
}

/// 依据镜像分支列表刷新 staging 分支的同步状态。
fn apply_staging_mirror_availability(
    mut staging: SillyTavernStaging,
    channel: DownloadChannel,
    branches: Option<&[String]>,
) -> SillyTavernStaging {
    let reference = staging.branch.clone();
    staging.mirror = mirror_state(channel, branches, &reference);
    staging
}

/// 计算单个 ref 的镜像同步状态；`refs` 为 `None` 表示本次无法确认。
fn mirror_state(
    channel: DownloadChannel,
    refs: Option<&[String]>,
    reference: &str,
) -> MirrorAvailability {
    if channel == DownloadChannel::Official {
        return MirrorAvailability::Official;
    }
    match refs {
        Some(items) if items.iter().any(|item| tags_match(item, reference)) => {
            MirrorAvailability::Synced
        }
        Some(_) => MirrorAvailability::NotSynced,
        None => MirrorAvailability::Unknown,
    }
}

/// 比较镜像 ref 与官方 ref 是否指向同一版本。
///
/// 部分镜像站习惯给 tag 加 `v` 前缀，直接字符串相等会把存在的版本误判为未同步。
fn tags_match(left: &str, right: &str) -> bool {
    normalize_ref_for_match(left) == normalize_ref_for_match(right)
}

fn normalize_ref_for_match(value: &str) -> &str {
    value.trim().trim_start_matches(['v', 'V'])
}

/// Auto 优先使用现有测速缓存；没有有效测速结果时回退官方直连。
#[allow(dead_code)]
pub fn resolve_download_channel(channel: DownloadChannel) -> DownloadChannel {
    if channel != DownloadChannel::Auto {
        return channel;
    }
    let now = unix_seconds().unwrap_or_default();
    load_download_channel_cache()
        .filter(|cache| cache.is_valid_at(now))
        .map(|cache| cache.resolved_channel)
        .filter(|channel| *channel != DownloadChannel::Auto)
        .unwrap_or(DownloadChannel::Official)
}

#[allow(dead_code)]
pub fn list_sillytavern_remote_tags(
    selected_channel: DownloadChannel,
    proxy_mode: &str,
    proxy_host: &str,
) -> Result<Vec<String>, String> {
    list_remote_tags(
        resolve_download_channel(selected_channel),
        proxy_mode,
        proxy_host,
    )
}

fn list_remote_tags(
    channel: DownloadChannel,
    proxy_mode: &str,
    proxy_host: &str,
) -> Result<Vec<String>, String> {
    if channel == DownloadChannel::Official {
        return Ok(Vec::new());
    }
    let mut command = Command::new(resolve_command("git"));
    configure_git_proxy(&mut command, proxy_mode, proxy_host);
    let output = command
        .args(["ls-remote", "--tags", channel.clone_url()])
        .output()
        .map_err(|error| tf("network.mirror.tags_failed", &[("error", &error)]))?;
    if !output.status.success() {
        return Err(String::from_utf8_lossy(&output.stderr).trim().to_owned());
    }
    Ok(String::from_utf8_lossy(&output.stdout)
        .lines()
        .filter_map(|line| line.split_once("refs/tags/").map(|(_, tag)| tag))
        .map(|tag| tag.trim_end_matches("^{}").to_owned())
        .collect())
}

fn list_remote_branches(
    channel: DownloadChannel,
    proxy_mode: &str,
    proxy_host: &str,
) -> Result<Vec<String>, String> {
    if channel == DownloadChannel::Official {
        return Ok(vec!["staging".to_owned()]);
    }
    let mut command = Command::new(resolve_command("git"));
    configure_git_proxy(&mut command, proxy_mode, proxy_host);
    let output = command
        .args(["ls-remote", "--heads", channel.clone_url()])
        .output()
        .map_err(|error| tf("network.mirror.branch_failed", &[("error", &error)]))?;
    if !output.status.success() {
        return Err(String::from_utf8_lossy(&output.stderr).trim().to_owned());
    }
    Ok(String::from_utf8_lossy(&output.stdout)
        .lines()
        .filter_map(|line| {
            line.split_once("refs/heads/")
                .map(|(_, branch)| branch.to_owned())
        })
        .collect())
}

#[allow(dead_code)]
pub fn resolve_sillytavern_install_channel(
    selected_channel: DownloadChannel,
    tag_name: &str,
    proxy_mode: &str,
    proxy_host: &str,
) -> DownloadChannel {
    let channel = resolve_download_channel(selected_channel);
    if channel == DownloadChannel::Official {
        return DownloadChannel::Official;
    }
    match list_remote_tags(channel, proxy_mode, proxy_host) {
        Ok(tags) if tags.iter().any(|tag| tag == tag_name) => channel,
        _ => DownloadChannel::Official,
    }
}

fn resolve_sillytavern_branch_channel(
    selected_channel: DownloadChannel,
    branch: &str,
    proxy_mode: &str,
    proxy_host: &str,
) -> DownloadChannel {
    let channel = resolve_download_channel(selected_channel);
    if channel == DownloadChannel::Official {
        return DownloadChannel::Official;
    }
    match list_remote_branches(channel, proxy_mode, proxy_host) {
        Ok(branches) if branches.iter().any(|item| item == branch) => channel,
        _ => DownloadChannel::Official,
    }
}

/// 安装或更新稳定 tag / staging 分支，并将 git/npm 的完整输出发送给界面。
#[allow(dead_code)]
pub fn run_sillytavern_install(
    target_ref: SillyTavernInstallTarget,
    target: PathBuf,
    source_channel: DownloadChannel,
    npm_registry: String,
    proxy_mode: String,
    proxy_host: String,
    env_source: EnvSource,
    sender: Sender<SillyTavernInstallEvent>,
) {
    run_sillytavern_install_with_cancel(
        target_ref,
        target,
        source_channel,
        npm_registry,
        proxy_mode,
        proxy_host,
        env_source,
        sender,
        Arc::new(AtomicBool::new(false)),
    );
}

#[allow(dead_code)]
pub fn run_sillytavern_install_with_cancel(
    target_ref: SillyTavernInstallTarget,
    target: PathBuf,
    source_channel: DownloadChannel,
    npm_registry: String,
    proxy_mode: String,
    proxy_host: String,
    env_source: EnvSource,
    sender: Sender<SillyTavernInstallEvent>,
    cancel: Arc<AtomicBool>,
) {
    let result = (|| -> Result<(), String> {
        let (ref_name, is_branch) = match &target_ref {
            SillyTavernInstallTarget::Tag(tag) => (tag.clone(), false),
            SillyTavernInstallTarget::Branch(branch) => (branch.clone(), true),
        };
        let configured_channel = resolve_download_channel(source_channel);
        let preferred_channel = if is_branch {
            resolve_sillytavern_branch_channel(source_channel, &ref_name, &proxy_mode, &proxy_host)
        } else {
            resolve_sillytavern_install_channel(source_channel, &ref_name, &proxy_mode, &proxy_host)
        };
        let mut channels = vec![preferred_channel];
        if preferred_channel != DownloadChannel::Official {
            channels.push(DownloadChannel::Official);
        }

        let target_existed = target.exists();
        let mut sync_error = None;
        for (index, channel) in channels.into_iter().enumerate() {
            if index > 0 {
                send_install_log(
                    &sender,
                    t("network.install.mirror_staging_fallback").to_owned(),
                );
            } else if channel == DownloadChannel::Official
                && configured_channel != DownloadChannel::Official
            {
                send_install_log(
                    &sender,
                    t("network.install.mirror_staging_missing").to_owned(),
                );
            }

            match install_git_ref(
                &ref_name,
                is_branch,
                channel,
                &target,
                target_existed,
                &proxy_mode,
                &proxy_host,
                &sender,
                &cancel,
            ) {
                Ok(()) => {
                    sync_error = None;
                    break;
                }
                Err(error) => {
                    send_install_log(&sender, tf("network.install.channel_failed", &[("channel", &t(channel.label_key())), ("error", &error)]));
                    sync_error = Some(error);
                    if !target_existed && target.exists() {
                        // 新目录克隆失败时清理残留目录，确保官方源可以重新 clone。
                        let _ = fs::remove_dir_all(&target);
                    }
                }
            }
        }
        if let Some(error) = sync_error {
            return Err(error);
        }

        ensure_not_cancelled(&cancel)?;
        let _ = sender.send(SillyTavernInstallEvent::DownloadComplete);
        wait_before_npm_install(&cancel)?;
        let _ = sender.send(SillyTavernInstallEvent::InstallStarted);
        // 统一命令环境：内置环境使用 lib/nodejs 下的 npm.cmd，系统环境走 where 解析。
        let mut npm = crate::core::settings::env_detect::command_for("npm", env_source);
        npm.current_dir(&target).arg("install");
        if !npm_registry.trim().is_empty() {
            npm.env("npm_config_registry", npm_registry);
        }
        configure_npm_proxy(&mut npm, &proxy_mode, &proxy_host);
        run_install_command(npm, &sender, &cancel)
    })();
    match &result {
        Err(error) if error == INSTALL_CANCELLED => {
            send_install_log(&sender, t("network.install.cancelled_log").to_owned());
            let _ = sender.send(SillyTavernInstallEvent::Cancelled);
        }
        Err(error) => send_install_log(&sender, tf("network.install.failed", &[("error", &error)])),
        Ok(()) => {}
    }
    let _ = sender.send(SillyTavernInstallEvent::Completed(result));
}

/// 使用指定 Git 源同步一个 tag 或分支。调用方负责在失败后切换备用源。
fn install_git_ref(
    ref_name: &str,
    is_branch: bool,
    channel: DownloadChannel,
    target: &PathBuf,
    target_existed: bool,
    proxy_mode: &str,
    proxy_host: &str,
    sender: &Sender<SillyTavernInstallEvent>,
    cancel: &AtomicBool,
) -> Result<(), String> {
    let source_url = channel.clone_url();
    let target_text = target.to_string_lossy().to_string();
    if target_existed {
        send_install_log(sender, tf("network.install.reuse_directory", &[("path", &target.display())]));
        if !target.join(".git").is_dir() {
            return Err(t("network.install.unsafe_existing_dir").to_owned());
        }
        let mut set_remote = Command::new(resolve_command("git"));
        configure_git_proxy(&mut set_remote, proxy_mode, proxy_host);
        set_remote.args([
            "-C",
            &target_text,
            "remote",
            "set-url",
            "origin",
            source_url,
        ]);
        run_install_command(set_remote, sender, cancel)?;

        let mut fetch = Command::new(resolve_command("git"));
        configure_git_proxy(&mut fetch, proxy_mode, proxy_host);
        if is_branch {
            // 显式写入远程跟踪分支；仅执行 `fetch origin staging` 只会更新 FETCH_HEAD，
            // 后续 checkout origin/staging 会因此找不到提交。
            let remote_ref = format!("+{ref_name}:refs/remotes/origin/{ref_name}");
            fetch.args(["-C", &target_text, "fetch", "origin", &remote_ref]);
        } else {
            fetch.args(["-C", &target_text, "fetch", "--tags", "--force", "origin"]);
        }
        run_install_command(fetch, sender, cancel)?;

        let mut checkout = Command::new(resolve_command("git"));
        configure_git_proxy(&mut checkout, proxy_mode, proxy_host);
        if is_branch {
            let remote_ref = format!("origin/{ref_name}");
            checkout.args(["-C", &target_text, "checkout", "-B", ref_name, &remote_ref]);
        } else {
            checkout.args([
                "-C",
                &target_text,
                "checkout",
                "--detach",
                "--force",
                ref_name,
            ]);
        }
        run_install_command(checkout, sender, cancel)
    } else {
        if let Some(parent) = target.parent() {
            fs::create_dir_all(parent).map_err(|error| tf("network.install.create_dir_failed", &[("error", &error)]))?;
        }
        let mut clone = Command::new(resolve_command("git"));
        configure_git_proxy(&mut clone, proxy_mode, proxy_host);
        clone
            .args([
                "clone",
                "--progress",
                "--branch",
                ref_name,
                "--depth",
                "1",
                source_url,
            ])
            .arg(target);
        run_install_command(clone, sender, cancel)
    }
}

pub(crate) fn configure_npm_proxy(command: &mut Command, proxy_mode: &str, proxy_host: &str) {
    if let Ok(Some(proxy)) = selected_proxy_url(proxy_mode, proxy_host) {
        command.env("HTTPS_PROXY", &proxy).env("HTTP_PROXY", proxy);
    }
}

fn ensure_not_cancelled(cancel: &AtomicBool) -> Result<(), String> {
    if cancel.load(Ordering::Relaxed) {
        Err(INSTALL_CANCELLED.to_owned())
    } else {
        Ok(())
    }
}

fn wait_before_npm_install(cancel: &AtomicBool) -> Result<(), String> {
    for _ in 0..30 {
        ensure_not_cancelled(cancel)?;
        std::thread::sleep(Duration::from_millis(100));
    }
    Ok(())
}

/// 安装任务被用户取消时使用的内部哨兵错误。
///
/// 它是**键**而不是展示文案：创建处与判定处引用同一个常量，
/// 展示时再按当前语言取值，避免「用翻译结果做比较」在切换语言后失效。
pub(crate) const INSTALL_CANCELLED: &str = "network.install.cancelled";

fn send_install_log(sender: &Sender<SillyTavernInstallEvent>, line: String) {
    // 安装日志同样可能是文案键或运行时文本，入队前解析一次。
    let _ = sender.send(SillyTavernInstallEvent::Log(resolve(&line)));
}

fn run_install_command(
    command: Command,
    sender: &Sender<SillyTavernInstallEvent>,
    cancel: &AtomicBool,
) -> Result<(), String> {
    run_logged_command(command, cancel, |line| send_install_log(sender, line))
}

/// 本地与在线安装共用的进程执行器；仅上层决定如何显示日志及安装结果。
pub(crate) fn run_logged_command(
    mut command: Command,
    cancel: &AtomicBool,
    mut log: impl FnMut(String),
) -> Result<(), String> {
    command.stdout(Stdio::piped()).stderr(Stdio::piped());
    let program = command.get_program().to_string_lossy();
    log(tf("network.command.execute", &[("program", &program)]));
    let mut child = command
        .spawn()
        .map_err(|error| tf("network.command.start_failed", &[("error", &error)]))?;
    let (line_sender, line_receiver) = mpsc::channel::<String>();
    if let Some(stdout) = child.stdout.take() {
        forward_process_stream(stdout, line_sender.clone());
    }
    if let Some(stderr) = child.stderr.take() {
        forward_process_stream(stderr, line_sender.clone());
    }
    drop(line_sender);
    loop {
        while let Ok(line) = line_receiver.try_recv() {
            log(line);
        }
        if cancel.load(Ordering::Relaxed) {
            let _ = child.kill();
            let _ = child.wait();
            return Err(INSTALL_CANCELLED.to_owned());
        }
        match child.try_wait() {
            Ok(Some(status)) => {
                while let Ok(line) = line_receiver.recv_timeout(Duration::from_millis(100)) {
                    log(line);
                }
                if status.success() {
                    return Ok(());
                }
                return Err(tf("network.command.exit_code", &[("code", &status.code().unwrap_or(-1))]));
            }
            Ok(None) => std::thread::sleep(Duration::from_millis(50)),
            Err(error) => return Err(tf("network.command.wait_failed", &[("error", &error)])),
        }
    }
}

fn forward_process_stream<R>(stream: R, sender: Sender<String>)
where
    R: Read + Send + 'static,
{
    std::thread::spawn(move || {
        use std::io::BufRead;
        for line in io::BufReader::new(stream).lines() {
            match line {
                Ok(line) => {
                    let _ = sender.send(line);
                }
                Err(error) => {
                    let _ = sender.send(tf("network.command.read_log_failed", &[("error", &error)]));
                    break;
                }
            }
        }
    });
}
