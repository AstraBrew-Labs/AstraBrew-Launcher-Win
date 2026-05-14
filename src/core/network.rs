//! 网络相关功能：系统代理读取、Github 多节点连接测试
//! 仅支持 Windows 平台。

use std::sync::Mutex;
use std::time::{Duration, Instant};
use once_cell::sync::Lazy;

// ─── Windows 系统代理读取（三级回退） ────────────────────────────────────────

/// 从各渠道收集到的代理原始值，统一解析
#[cfg(target_os = "windows")]
fn parse_proxy_values(server_raw: &str, enable_raw: &str) -> Option<(String, bool)> {
    let server = server_raw.trim().to_string();
    if server.is_empty() {
        return None;
    }
    // enable_raw 可能是 "1", "0x1", "0x0" 等
    let enabled = matches!(
        enable_raw.trim().to_lowercase().as_str(),
        "1" | "0x1" | "0x00000001"
    );
    Some((server, enabled))
}

/// 方式1: winreg 直接读注册表
#[cfg(target_os = "windows")]
fn try_read_proxy_via_winreg() -> Option<(String, bool)> {
    use winreg::enums::HKEY_CURRENT_USER;
    use winreg::RegKey;

    let hkcu = RegKey::predef(HKEY_CURRENT_USER);
    let settings = hkcu
        .open_subkey("Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings")
        .ok()?;

    let proxy_server: String = settings.get_value("ProxyServer").ok()?;
    let proxy_enable: u32 = settings.get_value("ProxyEnable").unwrap_or(0);

    if proxy_server.is_empty() {
        return None;
    }
    Some((proxy_server, proxy_enable != 0))
}

/// 方式2: PowerShell 查询（无需管理员）
#[cfg(target_os = "windows")]
fn try_read_proxy_via_powershell() -> Option<(String, bool)> {
    use std::os::windows::process::CommandExt;
    use std::process::{Command, Stdio};

    const CREATE_NO_WINDOW: u32 = 0x08000000;

    // 检测 powershell.exe 是否可用（隐藏窗口）
    let ps_exe = if Command::new("powershell.exe")
        .arg("-?")
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .creation_flags(CREATE_NO_WINDOW)
        .output()
        .is_ok()
    {
        "powershell.exe"
    } else if Command::new("pwsh.exe")
        .arg("-?")
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .creation_flags(CREATE_NO_WINDOW)
        .output()
        .is_ok()
    {
        "pwsh.exe"
    } else {
        return None;
    };

    let script = r#"
$pr = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction SilentlyContinue;
if ($pr) { Write-Output "ProxyServer=$($pr.ProxyServer)"; Write-Output "ProxyEnable=$($pr.ProxyEnable)" }
"#;

    let output = Command::new(ps_exe)
        .args(["-NoProfile", "-NonInteractive", "-Command", script])
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .creation_flags(CREATE_NO_WINDOW)
        .output()
        .ok()?;

    let text = String::from_utf8_lossy(&output.stdout);
    let mut server = String::new();
    let mut enabled = false;

    for line in text.lines() {
        if let Some(v) = line.strip_prefix("ProxyServer=") {
            server = v.trim().to_string();
        }
        if let Some(v) = line.strip_prefix("ProxyEnable=") {
            enabled = v.trim() == "1";
        }
    }

    parse_proxy_values(&server, if enabled { "1" } else { "0" })
}

/// 方式3: 系统环境变量（兼容非中文环境）
#[cfg(target_os = "windows")]
fn try_read_proxy_via_env() -> Option<(String, bool)> {
    if let Ok(server) = std::env::var("HTTP_PROXY") {
        return parse_proxy_values(&server, "1");
    }
    if let Ok(server) = std::env::var("HTTPS_PROXY") {
        return parse_proxy_values(&server, "1");
    }
    None
}

/// 读取 Windows 系统代理（三级回退）
#[cfg(target_os = "windows")]
pub fn read_windows_system_proxy() -> Option<(String, bool)> {
    try_read_proxy_via_winreg()
        .or_else(try_read_proxy_via_powershell)
        .or_else(try_read_proxy_via_env)
}

#[cfg(not(target_os = "windows"))]
pub fn read_windows_system_proxy() -> Option<(String, bool)> {
    None
}

// ─── GitHub 多链接测试 ─────────────────────────────────────────────────────────

/// 多链接测试结果项
#[derive(Debug, Clone)]
pub struct GithubMultiTestItem {
    pub key: String,
    pub name: String,
    pub success: bool,
    pub latency_ms: Option<u64>,
    pub error: Option<String>,
    pub warning: Option<String>,
}

/// 测试多个 GitHub 相关链接
/// mode: "none" | "system" | "custom" | "proxy"
/// include_api: 是否包含 api.github.com 测试
pub fn test_github_multi(
    mode: &str,
    host: &str,
    port: u16,
    _include_api: bool,
) -> Vec<GithubMultiTestItem> {
    // 定义测试链接列表
    let test_urls: Vec<(String, String, String)> = vec![
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

    // 构建 reqwest client
    let mut builder = reqwest::blocking::Client::builder()
        .timeout(Duration::from_secs(10));

    match mode {
        "proxy" => {
            let proxy_url = if host.starts_with("http://") || host.starts_with("https://") {
                host.to_string()
            } else {
                format!("http://{}", host)
            };
            if let Ok(proxy) = reqwest::Proxy::all(&proxy_url) {
                builder = builder.proxy(proxy);
            }
        }
        "custom" => {
            let proxy_url = format!("http://{}:{}", host, port);
            if let Ok(proxy) = reqwest::Proxy::all(&proxy_url) {
                builder = builder.proxy(proxy);
            }
        }
        "system" => {
            if let Some((server, true)) = read_windows_system_proxy() {
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
                let proxy_url = format!("http://{}", proxy_addr);
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
            Ok(resp) => {
                let latency = start.elapsed().as_millis() as u64;
                if resp.status().is_success() {
                    results.push(GithubMultiTestItem {
                        key,
                        name,
                        success: true,
                        latency_ms: Some(latency),
                        error: None,
                        warning: None,
                    });
                } else {
                    results.push(GithubMultiTestItem {
                        key,
                        name,
                        success: false,
                        latency_ms: Some(latency),
                        error: Some(format!("HTTP {}", resp.status())),
                        warning: None,
                    });
                }
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

    results
}

/// 多链接测试全局状态
struct GithubMultiTestState {
    in_progress: bool,
    results: Option<Vec<GithubMultiTestItem>>,
}

static GITHUB_MULTI_TEST_STATE: Lazy<Mutex<GithubMultiTestState>> = Lazy::new(|| {
    Mutex::new(GithubMultiTestState {
        in_progress: false,
        results: None,
    })
});

/// 检查多链接测试是否正在运行
pub fn is_github_multi_test_in_progress() -> bool {
    GITHUB_MULTI_TEST_STATE.lock().unwrap().in_progress
}

/// 启动 Github 多链接测试（后台线程）
pub fn start_github_multi_test(mode: &str, host: &str, port: u16, include_api: bool) {
    let mut state = GITHUB_MULTI_TEST_STATE.lock().unwrap();
    if state.in_progress {
        return;
    }
    state.in_progress = true;
    state.results = None;
    drop(state);

    let mode = mode.to_string();
    let host = host.to_string();

    std::thread::spawn(move || {
        let results = test_github_multi(&mode, &host, port, include_api);
        let mut state = GITHUB_MULTI_TEST_STATE.lock().unwrap();
        state.in_progress = false;
        state.results = Some(results);
    });
}

/// 获取多链接测试结果（调用后清空）
pub fn get_github_multi_test_result() -> Option<Vec<GithubMultiTestItem>> {
    let mut state = GITHUB_MULTI_TEST_STATE.lock().unwrap();
    state.results.take()
}
