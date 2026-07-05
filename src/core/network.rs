//! 网络相关功能：Windows 系统代理读取、Github 多节点连接测试

use std::io::Read;
use std::process::Command;
use std::sync::{LazyLock, Mutex};
use std::time::{Duration, Instant};
use std::os::windows::process::CommandExt;

use winreg::enums::*;

const CREATE_NO_WINDOW: u32 = 0x08000000;

// ─── Windows 系统代理读取 ─────────────────────────────────────────────────────

/// 读取 Windows 系统代理设置，返回 `Some((代理地址, 是否启用))` 或 `None`。
///
/// 优先级（从高到低）：
/// 1. 环境变量 `HTTPS_PROXY` / `HTTP_PROXY`（用户手动覆盖）
/// 2. Windows 注册表中的 IE/WinINET 代理（用户通过"设置→网络和 Internet→代理"配置）
/// 3. WinHTTP 代理（`netsh winhttp show proxy`，通常用于系统服务）
pub fn read_system_proxy() -> Option<(String, bool)> {
    // 环境变量优先（用户级覆盖）
    if let Some(result) = read_env_proxy() {
        return Some(result);
    }

    // 注册表 IE/WinINET 代理（Windows 系统设置） — 这是绝大多数用户配置代理的地方
    if let Some(result) = read_registry_proxy() {
        return Some(result);
    }

    // WinHTTP 代理（命令行配置）— 最后的回退
    read_winhttp_proxy()
}

/// Windows 专用别名：供 process.rs 调用
pub fn read_windows_system_proxy() -> Option<(String, bool)> {
    read_system_proxy()
}

/// 从环境变量读取代理
fn read_env_proxy() -> Option<(String, bool)> {
    for var in &["HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy"] {
        if let Ok(server) = std::env::var(var) {
            let trimmed = server.trim().to_string();
            if !trimmed.is_empty() {
                return Some((trimmed, true));
            }
        }
    }
    None
}

/// 从 Windows 注册表读取 IE/WinINET 代理设置
///
/// 注册表路径：
/// `HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings`
///
/// 读取的键：
/// - `ProxyEnable` (REG_DWORD): 0 = 禁用, 非 0 = 启用
/// - `ProxyServer` (REG_SZ): 代理地址，格式如 `127.0.0.1:7890`
///   或 `http=127.0.0.1:7890;https=127.0.0.1:7890`
fn read_registry_proxy() -> Option<(String, bool)> {
    let hkcu = winreg::RegKey::predef(HKEY_CURRENT_USER);
    let subkey = hkcu
        .open_subkey(r"Software\Microsoft\Windows\CurrentVersion\Internet Settings")
        .ok()?;

    // 读取代理是否启用
    let enabled: u32 = subkey.get_value("ProxyEnable").unwrap_or(0);
    if enabled == 0 {
        // 代理已禁用，但仍可能存在 ProxyServer 值（用户之前配置过但关了）
        return None;
    }

    // 读取代理服务器地址
    let server: String = subkey.get_value("ProxyServer").ok()?;
    let server = server.trim().to_string();
    if server.is_empty() {
        return None;
    }

    // 解析代理地址：支持 `http=host:port;https=host:port` 格式
    // 优先返回 HTTPS 代理，其次 HTTP，最后取第一个
    let addr = resolve_proxy_server(&server);

    Some((addr, true))
}

/// 从 `ProxyServer` 值中解析出可用的代理地址
///
/// 支持格式：
/// - `127.0.0.1:7890` — 单地址
/// - `http=127.0.0.1:7890;https=127.0.0.1:7890` — 按协议分离
fn resolve_proxy_server(server: &str) -> String {
    if server.contains('=') {
        // 按协议分离的格式，优先取 https，其次 http
        for proto in &["https=", "http=", "socks="] {
            if let Some(pos) = server.find(proto) {
                let start = pos + proto.len();
                let end = server[start..]
                    .find(';')
                    .map(|p| start + p)
                    .unwrap_or(server.len());
                let addr = server[start..end].trim();
                if !addr.is_empty() {
                    return addr.to_string();
                }
            }
        }
    }
    // 单地址格式，直接返回
    server.to_string()
}

/// 通过 netsh winhttp 读取 WinHTTP 代理
fn read_winhttp_proxy() -> Option<(String, bool)> {
    let output = Command::new("netsh")
        .args(["winhttp", "show", "proxy"])
        .creation_flags(CREATE_NO_WINDOW)
        .output()
        .ok()?;

    let text = String::from_utf8_lossy(&output.stdout);

    // 检查是否"直接访问"（即未配置代理）
    let has_direct = text
        .lines()
        .any(|l| l.contains("直接访问") || l.contains("Direct access"));
    if has_direct {
        return None;
    }

    for line in text.lines() {
        let trimmed = line.trim();
        if let Some(rest) = trimmed.strip_prefix("代理服务器:") {
            let server = rest.trim().to_string();
            if !server.is_empty() {
                return Some((server, true));
            }
        }
        if let Some(rest) = trimmed.strip_prefix("Proxy Server:") {
            let server = rest.trim().to_string();
            if !server.is_empty() {
                return Some((server, true));
            }
        }
    }
    None
}

// ─── GitHub 多链接测试 ─────────────────────────────────────────────────────────

/// 多链接测试结果项
#[derive(Debug, Clone)]
pub struct GithubMultiTestItem {
    #[allow(dead_code)]
    pub key: String,
    pub name: String,
    pub success: bool,
    pub latency_ms: Option<u64>,
    pub error: Option<String>,
    pub warning: Option<String>,
}

/// 测试多个 GitHub 相关链接
/// proxy_mode: "none" | "system" | "custom"
/// accelerate_url: 加速地址前缀（可选）
/// include_api: 是否包含 api.github.com 测试
pub fn test_github_multi(
    proxy_mode: &str,
    proxy_host: &str,
    _proxy_port: u16,
    accelerate_url: Option<String>,
    include_api: bool,
) -> Vec<GithubMultiTestItem> {
    // 定义测试链接列表
    let mut test_urls: Vec<(String, String, String)> = vec![
        (
            "raw".to_string(),
            "文件访问".to_string(),
            "https://raw.githubusercontent.com/SillyTavern/SillyTavern/release/start.sh".to_string(),
        ),
        (
            "repo".to_string(),
            "仓库访问".to_string(),
            "https://github.com/SillyTavern/SillyTavern".to_string(),
        ),
        (
            "homepage".to_string(),
            "首页访问".to_string(),
            "https://www.github.com".to_string(),
        ),
    ];

    if include_api {
        test_urls.push((
            "api".to_string(),
            "API 访问".to_string(),
            "https://api.github.com/repos/SillyTavern/SillyTavern/releases".to_string(),
        ));
    }

    // 应用加速地址前缀
    if let Some(ref accel) = accelerate_url {
        let accel_base = accel.trim_end_matches('/');
        for url_item in &mut test_urls {
            url_item.2 = format!("{}/{}", accel_base, url_item.2);
        }
    }

    // 构建 reqwest client
    let mut builder = reqwest::blocking::Client::builder()
        .timeout(Duration::from_secs(10))
        .user_agent("AstraBrew-Launcher-Win");

    match proxy_mode {
        "custom" => {
            let mut proxy_url = proxy_host.to_string();
            if !proxy_url.starts_with("http://")
                && !proxy_url.starts_with("https://")
                && !proxy_url.starts_with("socks5://")
            {
                proxy_url = format!("http://{}", proxy_url);
            }
            if let Ok(proxy) = reqwest::Proxy::all(&proxy_url) {
                builder = builder.proxy(proxy);
            }
        }
        "system" => {
            if let Some((server, true)) = read_system_proxy() {
                let proxy_addr = if server.contains('=') {
                    server
                        .split(';')
                        .find_map(|part| {
                            let kv: Vec<&str> = part.splitn(2, '=').collect();
                            if kv.len() == 2 && (kv[0] == "https" || kv[0] == "http") {
                                Some(kv[1].to_string())
                            } else {
                                None
                            }
                        })
                        .unwrap_or_else(|| {
                            server
                                .split(';')
                                .next()
                                .and_then(|p| p.splitn(2, '=').nth(1))
                                .unwrap_or(&server)
                                .to_string()
                        })
                } else {
                    server.clone()
                };

                let mut proxy_url = proxy_addr;
                if !proxy_url.starts_with("http://")
                    && !proxy_url.starts_with("https://")
                    && !proxy_url.starts_with("socks5://")
                {
                    proxy_url = format!("http://{}", proxy_url);
                }

                if let Ok(proxy) = reqwest::Proxy::all(&proxy_url) {
                    builder = builder.proxy(proxy);
                }
            }
        }
        _ => {
            builder = builder.no_proxy();
        }
    }

    let client = match builder.build() {
        Ok(c) => c,
        Err(e) => {
            return test_urls
                .into_iter()
                .map(|(key, name, _)| GithubMultiTestItem {
                    key,
                    name,
                    success: false,
                    latency_ms: None,
                    error: Some(format!("构建客户端失败: {}", e)),
                    warning: None,
                })
                .collect();
        }
    };

    let mut results = Vec::new();
    for (key, name, url) in test_urls {
        let start = Instant::now();
        match client.get(&url).send() {
            Ok(mut resp) => {
                let latency = start.elapsed().as_millis() as u64;
                let status = resp.status();

                let mut success = status.is_success();
                let mut warning = None;
                let mut error = None;

                if !success {
                    if accelerate_url.is_some() {
                        let status_u16 = status.as_u16();
                        if status_u16 == 403 {
                            success = true;
                            warning = Some("加速地址可用，但该资源无法加速 (403)".to_string());
                        } else if status_u16 == 404 {
                            success = true;
                            warning = Some("加速地址可用，但该资源无法加速 (404)".to_string());
                        } else {
                            let mut body = String::new();
                            let _ = resp.read_to_string(&mut body);
                            let lower = body.to_lowercase();
                            if lower.contains("invalid input") || lower.contains("无效输入") {
                                success = true;
                                warning = Some("加速地址可用，但该资源无法加速".to_string());
                            } else {
                                error = Some(format!("HTTP {}", status));
                            }
                        }
                    } else {
                        error = Some(format!("HTTP {}", status));
                    }
                }

                results.push(GithubMultiTestItem {
                    key,
                    name,
                    success,
                    latency_ms: Some(latency),
                    error,
                    warning,
                });
            }
            Err(e) => {
                results.push(GithubMultiTestItem {
                    key,
                    name,
                    success: false,
                    latency_ms: None,
                    error: Some(format!("连接失败: {}", e)),
                    warning: None,
                });
            }
        }
    }

    // 下载速度测试（使用 GitHub 自动归档下载，稳定可靠）
    let mut speed_test_url =
        "https://github.com/SillyTavern/SillyTavern/archive/refs/heads/release.zip".to_string();
    if let Some(ref accel) = accelerate_url {
        let accel_base = accel.trim_end_matches('/');
        speed_test_url = format!("{}/{}", accel_base, speed_test_url);
    }

    let speed_start = Instant::now();
    let global_timeout = Duration::from_secs(60);

    match client.get(&speed_test_url).send() {
        Ok(mut resp) => {
            let status = resp.status();
            if status.is_success() {
                let mut downloaded = 0u64;
                let mut buffer = [0u8; 8192];
                const MAX_TEST_BYTES: u64 = 4 * 1024 * 1024; // 最多下载 4MB

                while let Ok(n) = resp.read(&mut buffer) {
                    if n == 0 {
                        break;
                    }
                    downloaded += n as u64;
                    if downloaded >= MAX_TEST_BYTES {
                        break;
                    }
                    if speed_start.elapsed() > global_timeout {
                        break;
                    }
                }

                let elapsed = speed_start.elapsed();
                let speed_mbps =
                    (downloaded as f64 / 1_048_576.0) / elapsed.as_secs_f64().max(0.001);

                let speed_msg = if speed_mbps < 1.0 {
                    format!("速度太慢 ({:.1} KB/s)", speed_mbps * 1024.0)
                } else if speed_mbps < 4.0 {
                    format!("速度正常 ({:.2} MB/s)", speed_mbps)
                } else if speed_mbps < 10.0 {
                    format!("速度很快 ({:.2} MB/s)", speed_mbps)
                } else {
                    format!("速度极快 ({:.2} MB/s)", speed_mbps)
                };

                results.push(GithubMultiTestItem {
                    key: "speed".to_string(),
                    name: "下载速度".to_string(),
                    success: true,
                    latency_ms: None,
                    error: None,
                    warning: Some(speed_msg),
                });
            } else {
                let mut success = false;
                let mut warning = None;
                let mut error = None;

                let status_u16 = status.as_u16();
                if accelerate_url.is_some() {
                    if status_u16 == 403 {
                        success = true;
                        warning = Some("加速地址可用，但该资源无法加速 (403)".to_string());
                    } else if status_u16 == 404 {
                        success = true;
                        warning = Some("加速地址可用，但该资源无法加速 (404)".to_string());
                    } else {
                        let mut body = String::new();
                        let _ = resp.read_to_string(&mut body);
                        let lower = body.to_lowercase();
                        if lower.contains("invalid input") || lower.contains("无效输入") {
                            success = true;
                            warning = Some("加速地址可用，但该资源无法加速".to_string());
                        } else {
                            error = Some(format!("HTTP {}", status));
                        }
                    }
                } else if status_u16 == 403 {
                    warning = Some("GitHub 拒绝访问 (403)，可能需要代理或加速地址".to_string());
                    error = Some("HTTP 403".to_string());
                } else if status_u16 == 404 {
                    warning = Some("测速文件未找到 (404)，请检查网络连接".to_string());
                    error = Some("HTTP 404".to_string());
                } else {
                    error = Some(format!("HTTP {}", status));
                }

                results.push(GithubMultiTestItem {
                    key: "speed".to_string(),
                    name: "下载速度".to_string(),
                    success,
                    latency_ms: None,
                    error,
                    warning,
                });
            }
        }
        Err(e) => {
            results.push(GithubMultiTestItem {
                key: "speed".to_string(),
                name: "下载速度".to_string(),
                success: false,
                latency_ms: None,
                error: Some(format!("测速失败: {}", e)),
                warning: None,
            });
        }
    }

    results
}

// ─── 多链接测试全局状态 ──────────────────────────────────────────────────────

struct GithubMultiTestState {
    in_progress: bool,
    test_id: u64,
    results: Option<Vec<GithubMultiTestItem>>,
    start_time: Option<Instant>,
}

static GITHUB_MULTI_TEST_STATE: LazyLock<Mutex<GithubMultiTestState>> = LazyLock::new(|| {
    Mutex::new(GithubMultiTestState {
        in_progress: false,
        test_id: 0,
        results: None,
        start_time: None,
    })
});

/// 取消多链接测试
pub fn cancel_github_multi_test() {
    let mut state = GITHUB_MULTI_TEST_STATE.lock().unwrap();
    state.in_progress = false;
    state.results = None;
    state.start_time = None;
    state.test_id = state.test_id.wrapping_add(1);
}

/// 检查多链接测试是否正在运行
pub fn is_github_multi_test_in_progress() -> bool {
    let mut state = GITHUB_MULTI_TEST_STATE.lock().unwrap();
    if state.in_progress {
        if let Some(st) = state.start_time {
            if st.elapsed() > Duration::from_secs(60) {
                // 超时处理：强行结束测试状态并填入超时结果
                state.in_progress = false;
                let dummy_results = vec![
                    GithubMultiTestItem {
                        key: "raw".to_string(),
                        name: "文件访问".to_string(),
                        success: false,
                        latency_ms: None,
                        error: Some("连接超时".to_string()),
                        warning: None,
                    },
                    GithubMultiTestItem {
                        key: "repo".to_string(),
                        name: "仓库访问".to_string(),
                        success: false,
                        latency_ms: None,
                        error: Some("连接超时".to_string()),
                        warning: None,
                    },
                    GithubMultiTestItem {
                        key: "homepage".to_string(),
                        name: "首页访问".to_string(),
                        success: false,
                        latency_ms: None,
                        error: Some("连接超时".to_string()),
                        warning: None,
                    },
                    GithubMultiTestItem {
                        key: "api".to_string(),
                        name: "API 访问".to_string(),
                        success: false,
                        latency_ms: None,
                        error: Some("连接超时".to_string()),
                        warning: None,
                    },
                    GithubMultiTestItem {
                        key: "speed".to_string(),
                        name: "下载速度".to_string(),
                        success: false,
                        latency_ms: None,
                        error: Some("连接超时".to_string()),
                        warning: None,
                    },
                ];
                state.results = Some(dummy_results);
            }
        }
    }
    state.in_progress
}

/// 启动 Github 多链接测试（后台线程）
pub fn start_github_multi_test(
    proxy_mode: &str,
    proxy_host: &str,
    proxy_port: u16,
    accelerate_url: Option<String>,
    include_api: bool,
) {
    let mut state = GITHUB_MULTI_TEST_STATE.lock().unwrap();
    if state.in_progress {
        return;
    }
    state.in_progress = true;
    state.test_id = state.test_id.wrapping_add(1);
    let current_test_id = state.test_id;
    state.results = None;
    state.start_time = Some(Instant::now());
    drop(state);

    let proxy_mode = proxy_mode.to_string();
    let proxy_host = proxy_host.to_string();

    std::thread::spawn(move || {
        let results =
            test_github_multi(&proxy_mode, &proxy_host, proxy_port, accelerate_url, include_api);
        let mut state = GITHUB_MULTI_TEST_STATE.lock().unwrap();
        if state.in_progress && state.test_id == current_test_id {
            state.in_progress = false;
            state.results = Some(results);
        }
    });
}

/// 获取多链接测试结果（调用后清空）
pub fn get_github_multi_test_result() -> Option<Vec<GithubMultiTestItem>> {
    let mut state = GITHUB_MULTI_TEST_STATE.lock().unwrap();
    state.results.take()
}

// ─── 本机 IP 地址检测 ─────────────────────────────────────────────────────────

/// 获取局域网 IPv4 地址（解析 ipconfig）
pub fn get_lan_ipv4() -> Option<String> {
    let output = Command::new("ipconfig")
        .creation_flags(CREATE_NO_WINDOW)
        .output()
        .ok()?;
    let text = String::from_utf8_lossy(&output.stdout);
    for line in text.lines() {
        let trimmed = line.trim();
        // 格式: "IPv4 地址 . . . . . . . . . . . . : 192.168.1.100"
        if let Some(rest) = trimmed.strip_prefix("IPv4") {
            if let Some(ip) = rest.split(':').nth(1) {
                let ip = ip.trim();
                if ip != "127.0.0.1" && !ip.starts_with("169.254.") {
                    return Some(ip.to_string());
                }
            }
        }
        // 英文格式: "IPv4 Address. . . . . . . . . . . : 192.168.1.100"
        if trimmed.starts_with("IPv4 Address") || trimmed.starts_with("IP Address") {
            if let Some(ip) = trimmed.split(':').last() {
                let ip = ip.trim();
                if ip != "127.0.0.1" && !ip.starts_with("169.254.") {
                    return Some(ip.to_string());
                }
            }
        }
    }
    None
}

/// 获取局域网 IPv6 地址（解析 ipconfig，跳过回环和链路本地 fe80::）
pub fn get_lan_ipv6() -> Option<String> {
    let output = Command::new("ipconfig")
        .creation_flags(CREATE_NO_WINDOW)
        .output()
        .ok()?;
    let text = String::from_utf8_lossy(&output.stdout);
    for line in text.lines() {
        let trimmed = line.trim();
        if let Some(rest) = trimmed.strip_prefix("IPv6") {
            if let Some(ip) = rest.split(':').nth(1) {
                let ip = ip.trim().trim_start_matches(": ").trim();
                if ip != "::1" && !ip.starts_with("fe80:") && !ip.is_empty() {
                    return Some(ip.to_string());
                }
            }
        }
    }
    None
}

/// 获取公网 IPv4 地址（优先 ip.sb，备用 ipify / ident.me）
/// 通过 local_address 强制使用 IPv4 socket；**禁用所有代理**，确保获取到真实公网 IP
pub fn get_public_ipv4() -> Option<String> {
    let client = reqwest::blocking::Client::builder()
        .local_address(std::net::IpAddr::V4(std::net::Ipv4Addr::UNSPECIFIED))
        .timeout(Duration::from_secs(8))
        .no_proxy()
        .build()
        .ok()?;

    let endpoints = [
        "https://api-ipv4.ip.sb/ip",
        "https://api4.ipify.org",
        "https://v4.ident.me",
    ];
    for url in endpoints {
        if let Ok(resp) = client.get(url).send() {
            if resp.status().is_success() {
                if let Ok(text) = resp.text() {
                    let ip = text.trim().to_string();
                    if !ip.is_empty() && ip.contains('.') && !ip.contains(':') {
                        return Some(ip);
                    }
                }
            }
        }
    }
    None
}

/// 获取公网 IPv6 地址（优先 ip.sb，备用 ipify / ident.me）
/// 通过 local_address 强制使用 IPv6 socket；**禁用所有代理**，确保获取到真实公网 IP
pub fn get_public_ipv6() -> Option<String> {
    let client = reqwest::blocking::Client::builder()
        .local_address(std::net::IpAddr::V6(std::net::Ipv6Addr::UNSPECIFIED))
        .timeout(Duration::from_secs(8))
        .no_proxy()
        .build()
        .ok()?;

    let endpoints = [
        "https://api-ipv6.ip.sb/ip",
        "https://api6.ipify.org",
        "https://v6.ident.me",
    ];
    for url in endpoints {
        if let Ok(resp) = client.get(url).send() {
            if resp.status().is_success() {
                if let Ok(text) = resp.text() {
                    let ip = text.trim().to_string();
                    if !ip.is_empty() && ip.contains(':') {
                        return Some(ip);
                    }
                }
            }
        }
    }
    None
}

// ─── 酒馆连接日志解析 ─────────────────────────────────────────────────────────

/// 解析酒馆日志得到的连接信息
#[derive(Debug, Clone)]
pub struct ConnectionInfo {
    /// 客户端 IP（IPv4 或 IPv6）
    pub ip: String,
    /// 从 User Agent 解析出的操作系统（如 "macOS 10.15.7"）
    pub os: String,
    /// 从 User Agent 解析出的设备型号/品牌（如 "iPhone"、"SM-S901B"），无法识别时为 None
    pub device: Option<String>,
    /// 原始 User Agent
    pub user_agent: String,
}

/// 剥离 ANSI 转义序列（CSI 和 OSC）
fn strip_ansi_simple(line: &str) -> String {
    if !line.contains('\x1b') {
        return line.to_string();
    }
    let mut result = String::with_capacity(line.len());
    let mut chars = line.chars().peekable();
    while let Some(ch) = chars.next() {
        if ch == '\x1b' {
            match chars.peek() {
                Some(&'[') => {
                    chars.next();
                    while let Some(&c) = chars.peek() {
                        chars.next();
                        if c.is_ascii_alphabetic() || c == '~' {
                            break;
                        }
                    }
                }
                Some(&']') => {
                    chars.next();
                    while let Some(&c) = chars.peek() {
                        if c == '\x07' {
                            chars.next();
                            break;
                        }
                        if c == '\x1b' {
                            chars.next();
                            if chars.peek() == Some(&'\\') {
                                chars.next();
                            }
                            break;
                        }
                        chars.next();
                    }
                }
                _ => {
                    if chars.peek().is_some() {
                        chars.next();
                    }
                }
            }
        } else {
            result.push(ch);
        }
    }
    result
}

/// 从 User Agent 字符串解析操作系统
fn parse_os_from_ua(ua: &str) -> String {
    // macOS: "Macintosh; Intel Mac OS X 10_15_7"
    if let Some(idx) = ua.find("Mac OS X ") {
        let rest = &ua[idx + 9..];
        let version: String = rest
            .chars()
            .take_while(|c| c.is_alphanumeric() || *c == '_' || *c == '.')
            .collect();
        let v = version.replace('_', ".");
        if !v.is_empty() {
            return format!("macOS {}", v);
        }
    }
    // Windows: "Windows NT 10.0"
    if let Some(idx) = ua.find("Windows NT ") {
        let rest = &ua[idx + 11..];
        let version: String = rest
            .chars()
            .take_while(|c| c.is_ascii_digit() || *c == '.')
            .collect();
        if !version.is_empty() {
            return format!("Windows {}", version);
        }
    }
    // iPhone: "iPhone; CPU iPhone OS 17_0"
    if let Some(idx) = ua.find("iPhone OS ") {
        let rest = &ua[idx + 10..];
        let version: String = rest
            .chars()
            .take_while(|c| c.is_alphanumeric() || *c == '_' || *c == '.')
            .collect();
        let v = version.replace('_', ".");
        if !v.is_empty() {
            return format!("iOS {}", v);
        }
    }
    // iPad: "iPad; CPU OS 17_0"
    if let Some(idx) = ua.find("CPU OS ") {
        let rest = &ua[idx + 7..];
        let version: String = rest
            .chars()
            .take_while(|c| c.is_alphanumeric() || *c == '_' || *c == '.')
            .collect();
        let v = version.replace('_', ".");
        if !v.is_empty() {
            return format!("iPadOS {}", v);
        }
    }
    // Android: "Linux; Android 13"
    if let Some(idx) = ua.find("Android ") {
        let rest = &ua[idx + 8..];
        let version: String = rest
            .chars()
            .take_while(|c| c.is_ascii_digit() || *c == '.')
            .collect();
        if !version.is_empty() {
            return format!("Android {}", version);
        }
    }
    // Linux
    if ua.contains("Linux") {
        return "Linux".to_string();
    }
    "Unknown".to_string()
}

/// 从 User Agent 字符串解析设备型号/品牌
///
/// 返回 `Some(可读型号)` 或 `None`（PC / 桌面浏览器 / 无法识别）。
/// 桌面浏览器（Mac/Windows/Linux PC）通常无法识别具体硬件，返回 None。
fn parse_device_from_ua(ua: &str) -> Option<String> {
    // ---- iOS / iPadOS：UA 中常带机型代号，如 "iPhone14,3"（取自可选的设备标识段）----
    // 注：标准 Safari UA 通常不含机型，但 SillyTavern 记录的 UA 若含 "iPhone<iOS>" 即识别
    if ua.contains("iPhone") {
        return Some("iPhone".to_string());
    }
    if ua.contains("iPad") {
        return Some("iPad".to_string());
    }
    if ua.contains("iPod") {
        return Some("iPod".to_string());
    }

    // ---- Android：品牌/机型编码在 "(Linux; Android 13; <model>)" 中 ----
    // 例：Mozilla/5.0 (Linux; Android 13; SM-S901B) ...
    //     Mozilla/5.0 (Linux; Android 12; Pixel 6) ...
    //     Mozilla/5.0 (Linux; Android 14; CPH2581) ...
    if let Some(android_idx) = ua.find("Android") {
        // Android 之后通常跟着 "版本;" 再跟机型，机型位于 "Android X; <model>)"
        let after = &ua[android_idx..];
        // 形如 "Android 13; SM-S901B)" — 取最后一个分号后、右括号前的内容
        if let Some(paren_end) = after.find(')') {
            let segment = &after[..paren_end];
            // 取分号后的最后一段作为机型
            if let Some(semi) = segment.rfind(';') {
                let model = segment[semi + 1..].trim();
                // 过滤空值或明显非机型占位（如 "wv" 表示 WebView）
                if !model.is_empty() && model.len() <= 40 {
                    // 尝试把机型代号映射为品牌可读名
                    let brand = android_brand_from_model(model);
                    return Some(brand.unwrap_or_else(|| model.to_string()));
                }
            }
        }
    }

    None
}

/// 将部分已知的 Android 机型代号映射为"品牌 可读名"，无法识别时返回 None。
///
/// 这只是一张覆盖常见机型的小表，命中则更友好；不命中则直接回退显示原始代号。
fn android_brand_from_model(model: &str) -> Option<String> {
    let m = model.to_uppercase();
    // 三星：SM-XXXX / SGH-XXXX / SCH-XXXX / GT-XXXX
    if m.starts_with("SM-")
        || m.starts_with("SGH-")
        || m.starts_with("SCH-")
        || m.starts_with("GT-")
    {
        return Some(format!("Samsung {}", model));
    }
    // 小米 / Redmi / POCO：常见前缀 2XXXXXXX（数字串）或 M2xxx / Redmi / POCO
    if model.starts_with("Redmi") || model.starts_with("POCO") || model.starts_with("Mi ") {
        return Some(format!("Xiaomi {}", model));
    }
    if model.starts_with("M2") && model.len() >= 6 && model[2..].chars().all(|c| c.is_ascii_digit()) {
        return Some(format!("Xiaomi {}", model));
    }
    // OPPO / OnePlus / realme
    if m.starts_with("CPH") || m.starts_with("ONEPLUS") || m.starts_with("RMX") {
        if m.starts_with("ONEPLUS") {
            return Some(format!("OnePlus {}", model));
        }
        if m.starts_with("RMX") {
            return Some(format!("realme {}", model));
        }
        return Some(format!("OPPO {}", model));
    }
    // vivo：VXXXX / V2xxx / IXXXX
    if (m.starts_with('V') || m.starts_with('I'))
        && model.len() >= 5
        && model[1..].chars().all(|c| c.is_ascii_digit())
    {
        return Some(format!("vivo {}", model));
    }
    // 华为：HUAWEI / Honor / HW-XXX / DCO-XXX
    if model.starts_with("HUAWEI") || model.starts_with("Honor") {
        return Some(model.to_string());
    }
    if m.starts_with("HW-") || m.starts_with("DCO-") {
        return Some(format!("HUAWEI {}", model));
    }
    // Google Pixel
    if model.starts_with("Pixel") {
        return Some(format!("Google {}", model));
    }
    None
}

/// 解析酒馆日志中的连接信息
///
/// 日志格式：`New connection from <IP>; User Agent: <UA>`
/// 例：`New connection from 240a:...; User Agent: Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) ...`
///
/// 返回 `Some(ConnectionInfo)` 或 `None`（不是连接日志/解析失败）
pub fn parse_connection_log(line: &str) -> Option<ConnectionInfo> {
    let plain = strip_ansi_simple(line);
    let marker = "New connection from ";
    let idx = plain.find(marker)?;
    let rest = &plain[idx + marker.len()..];

    let ua_marker = "; User Agent:";
    let ua_idx = rest.find(ua_marker)?;
    let ip = rest[..ua_idx].trim().to_string();
    if ip.is_empty() {
        return None;
    }

    let ua = rest[ua_idx + ua_marker.len()..].trim().to_string();
    if ua.is_empty() {
        return None;
    }

    let os = parse_os_from_ua(&ua);
    let device = parse_device_from_ua(&ua);
    Some(ConnectionInfo {
        ip,
        os,
        device,
        user_agent: ua,
    })
}

// ─── 本机 / 局域网 IP 识别（连接通知过滤）────────────────────────────────────

/// 判断给定 IP 字符串是否为本机访问（无需弹出连接通知）。
///
/// 命中以下任一条件即视为本机：
/// - 字面量回环：`127.0.0.1`、`::1`、`localhost`
/// - 本机任一网卡分配的地址（含 LAN IPv4 / 全局 IPv6）
///
/// 注意：服务器模式下手机经路由器访问 MAC 的 LAN IP（如 192.168.x.x），
/// 在 MAC 的网卡上即为本机地址 → 此时会判定为本机访问并跳过通知，
/// 但手机自身 IP（如 192.168.1.50）不在本机网卡上，仍正常通知。
pub fn is_local_ip(ip: &str) -> bool {
    let trimmed = ip.trim();
    if trimmed.is_empty() {
        return true; // 异常情况，保守过滤
    }
    // 字面量回环
    if matches!(trimmed, "127.0.0.1" | "::1" | "localhost") || trimmed.starts_with("127.") {
        return true;
    }
    // 命中本机网卡 IP
    LOCAL_IP_SET.contains(trimmed)
}

/// 本机所有网卡 IP 的集合（启动时扫描一次后缓存）
static LOCAL_IP_SET: LazyLock<std::collections::HashSet<String>> = LazyLock::new(|| {
    let mut set = std::collections::HashSet::new();
    if let Ok(output) = Command::new("ipconfig")
        .creation_flags(CREATE_NO_WINDOW)
        .output()
    {
        let text = String::from_utf8_lossy(&output.stdout);
        for line in text.lines() {
            let trimmed = line.trim();
            // 解析 IPv4 地址
            if let Some(rest) = trimmed.strip_prefix("IPv4") {
                if let Some(ip) = rest.split(':').nth(1) {
                    let ip = ip.trim();
                    if ip != "127.0.0.1" && !ip.starts_with("169.254.") {
                        set.insert(ip.to_string());
                    }
                }
            }
            // 解析 IPv6 地址
            if let Some(rest) = trimmed.strip_prefix("IPv6") {
                if let Some(ip) = rest.split(':').nth(1) {
                    let ip = ip.trim().trim_start_matches(": ").trim();
                    if ip != "::1" && !ip.starts_with("fe80:") && !ip.is_empty() {
                        set.insert(ip.to_string());
                    }
                }
            }
            // 英文回退
            if let Some(rest) = trimmed.strip_prefix("IPv4 Address") {
                if let Some(ip) = rest.split(':').last() {
                    let ip = ip.trim();
                    if ip != "127.0.0.1" && !ip.starts_with("169.254.") {
                        set.insert(ip.to_string());
                    }
                }
            }
        }
    }
    set
});
