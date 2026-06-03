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
    #[allow(dead_code)]
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

    if let Some(accel) = &accelerate_url {
        let accel_base = accel.trim_end_matches('/');
        for url_item in &mut test_urls {
            url_item.2 = format!("{}/{}", accel_base, url_item.2);
        }
    }

    // 构建 reqwest client
    let mut builder = reqwest::blocking::Client::builder()
        .timeout(Duration::from_secs(10))
        .user_agent("AstraBrew-Launcher");

    match proxy_mode {
        "custom" => {
            let mut proxy_url = proxy_host.to_string();
            if !proxy_url.starts_with("http://") && !proxy_url.starts_with("https://") && !proxy_url.starts_with("socks5://") {
                proxy_url = format!("http://{}", proxy_url);
            }
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
                
                let mut proxy_url = proxy_addr;
                if !proxy_url.starts_with("http://") && !proxy_url.starts_with("https://") && !proxy_url.starts_with("socks5://") {
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
                            use std::io::Read;
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

    // 下载速度测试
    let mut speed_test_url = "https://github.com/al01cn/sillyTavern-launcher/releases/download/v0.1.5/SillyTavern.Launcher.GUI_x64.app.tar.gz".to_string();
    if let Some(accel) = &accelerate_url {
        let accel_base = accel.trim_end_matches('/');
        speed_test_url = format!("{}/{}", accel_base, speed_test_url);
    }
    
    let speed_start = Instant::now();
    let global_timeout = Duration::from_secs(60);
    
    match client.get(&speed_test_url).send() {
        Ok(mut resp) => {
            let status = resp.status();
            if status.is_success() {
                use std::io::Read;
                let mut downloaded = 0u64;
                let mut buffer = [0u8; 8192];
                const MAX_TEST_BYTES: u64 = 4 * 1024 * 1024; // 最多下载 4MB
                
                while let Ok(n) = resp.read(&mut buffer) {
                    if n == 0 { break; }
                    downloaded += n as u64;
                    if downloaded >= MAX_TEST_BYTES { break; }
                    
                    // 中途检测下载是否超时，避免长时间挂起
                    if speed_start.elapsed() > global_timeout {
                        break;
                    }
                }
                
                let elapsed = speed_start.elapsed();
                let speed_mbps = (downloaded as f64 / 1_048_576.0) / elapsed.as_secs_f64().max(0.001);
                
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

                if accelerate_url.is_some() {
                    let status_u16 = status.as_u16();
                    if status_u16 == 403 {
                        success = true;
                        warning = Some("加速地址可用，但该资源无法加速 (403)".to_string());
                    } else if status_u16 == 404 {
                        success = true;
                        warning = Some("加速地址可用，但该资源无法加速 (404)".to_string());
                    } else {
                        use std::io::Read;
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

/// 多链接测试全局状态
struct GithubMultiTestState {
    in_progress: bool,
    test_id: u64,
    results: Option<Vec<GithubMultiTestItem>>,
    start_time: Option<Instant>,
}

static GITHUB_MULTI_TEST_STATE: Lazy<Mutex<GithubMultiTestState>> = Lazy::new(|| {
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
        let results = test_github_multi(&proxy_mode, &proxy_host, proxy_port, accelerate_url, include_api);
        let mut state = GITHUB_MULTI_TEST_STATE.lock().unwrap();
        // 只有在没被超时机制强制终止且为当前测试的情况下才写入结果
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
