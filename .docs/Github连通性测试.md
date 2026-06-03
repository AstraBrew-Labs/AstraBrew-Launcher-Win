# GitHub 连通性测试

---

## 数据结构

### `GithubTestResultItem`（第 868 行）

```rust
/// GitHub 多链接测试结果项
#[derive(serde::Serialize)]
pub struct GithubTestResultItem {
    pub key: String,
    pub name: String,
    pub url: String,
    pub success: bool,
    pub latency: Option<u64>,
    pub error: Option<String>,
    /// 警告消息，如果有则表示加速地址可用但无法加速特定资源
    pub warning: Option<String>,
}
```

### `DownloadSpeedResult`（第 1550 行）

```rust
/// 下载速度测试结果
#[derive(serde::Serialize)]
pub struct DownloadSpeedResult {
    /// 请求延迟 (ms)
    pub request_latency: u64,
    /// 平均下载速度 (MB/s)
    pub speed_mbps: f64,
    /// 下载用时 (ms)
    pub download_time_ms: u64,
    /// 下载数据量 (bytes)
    pub downloaded_bytes: u64,
    /// 错误信息（如果有）
    pub error: Option<String>,
}
```

---

## Tauri Command：`test_github_connection`（第 764 行）

**功能**：单链路连通性测试，GET `https://www.github.com`，返回延迟 ms。

```rust
/// 测试 GitHub 连接是否可达（考虑代理设置）
/// mode: "none" | "system" | "custom" | "proxy"
/// 当 mode 为 "proxy" 时，host 参数应传入 GitHub 加速地址
#[tauri::command]
pub async fn test_github_connection(
    app: AppHandle,
    mode: String,
    host: String,
    port: u16,
) -> Result<u64, String> {
    // Git 环境前置检查
    let git_exe = crate::git::get_git_exe(&app);
    let git_exists = if git_exe.to_string_lossy() == "git" {
        crate::git::has_system_git()
    } else {
        git_exe.exists()
    };
    if !git_exists {
        return Err("Git not found".to_string());
    }

    let mut builder = reqwest::Client::builder()
        .user_agent("SillyTavern-launcher")
        .gzip(true)
        .timeout(std::time::Duration::from_secs(10));

    match mode.as_str() {
        "proxy" => {
            let proxy_url = if host.starts_with("http://") || host.starts_with("https://") {
                host.clone()
            } else {
                format!("http://{}", host)
            };
            let proxy =
                reqwest::Proxy::all(&proxy_url).map_err(|e| format!("Invalid proxy: {}", e))?;
            builder = builder.proxy(proxy);
        }
        "custom" => {
            let proxy_url = format!("http://{}:{}", host, port);
            let proxy =
                reqwest::Proxy::all(&proxy_url).map_err(|e| format!("Invalid proxy: {}", e))?;
            builder = builder.proxy(proxy);
        }
        "system" => match read_windows_system_proxy() {
            Some((server, true)) => {
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
                let proxy = reqwest::Proxy::all(&proxy_url)
                    .map_err(|e| format!("Invalid system proxy: {}", e))?;
                builder = builder.proxy(proxy);
            }
            Some((_, false)) => {
                builder = builder.no_proxy();
            }
            None => {
                builder = builder.no_proxy();
            }
        },
        _ => {
            builder = builder.no_proxy();
        }
    }

    let client = builder
        .build()
        .map_err(|e| format!("Build client failed: {}", e))?;

    let start = std::time::Instant::now();
    let response = client
        .get("https://www.github.com")
        .header("Accept", "application/vnd.github.v3+json")
        .send()
        .await
        .map_err(|e| format!("连接失败: {}", e))?;

    if !response.status().is_success() {
        return Err(format!("HTTP {}", response.status()));
    }

    Ok(start.elapsed().as_millis() as u64)
}
```

---

## Tauri Command：`test_github_multi`（第 887 行）

**功能**：批量测试多个 GitHub 端点，返回 `Vec<GithubTestResultItem>`。

- 测试点：文件访问 / 仓库访问 / 首页访问 / API 访问（可选）/ 下载速度
- `mode == "accelerate"` 时转发给 `test_github_accelerate`

```rust
/// 测试多个 GitHub 相关链接
/// mode: "none" | "system" | "custom" | "proxy" | "accelerate"
/// - "accelerate": 加速模式，URL 前面拼接加速地址，仓库用 git ls-remote 测试
/// - "proxy": 代理模式，使用 GitHub 加速地址作为代理
/// - "custom" / "system" / "none": 直连或自定义代理模式
/// include_api: 是否包含 api.github.com 测试（仅非加速模式生效）
#[tauri::command]
pub async fn test_github_multi(
    app: AppHandle,
    mode: String,
    host: String,
    port: u16,
    include_api: bool,
) -> Result<Vec<GithubTestResultItem>, String> {
    // 加速模式：URL 拼接 + git ls-remote
    if mode == "accelerate" {
        return test_github_accelerate(&app, &host).await;
    }

    let mut builder = reqwest::Client::builder()
        .user_agent("Mozilla/5.0 ...")
        .gzip(true)
        .timeout(std::time::Duration::from_secs(10));

    // ... 代理配置（同 test_github_connection）...

    let client = builder.build().map_err(|e| format!("Build client failed: {}", e))?;

    let mut test_urls = vec![
        ("raw",      "文件访问", "https://raw.githubusercontent.com/SillyTavern/SillyTavern/release/start.sh"),
        ("repo",     "仓库访问", "https://github.com/SillyTavern/SillyTavern"),
        ("homepage", "首页访问", "https://www.github.com"),
    ];

    if include_api {
        test_urls.push(("api", "API 访问", "https://api.github.com/repos/SillyTavern/SillyTavern/releases"));
    }

    // 系统代理时最多重试 2 次（503 / 网络错误）
    // 2xx / 301 / 302 算成功
    // ...

    // 下载速度测试（直连）
    let speed_test_url = "https://github.com/al01cn/sillyTavern-launcher/releases/download/v0.1.5/SillyTavern.Launcher.GUI_x64.app.tar.gz";
    let speed_result = run_download_speed_test(&client, speed_test_url).await;
    results.push(GithubTestResultItem {
        key: "speed".to_string(),
        name: "下载速度".to_string(),
        // ...
        warning: speed_result.ok().map(|r| format!("{:.2} MB/s", r.speed_mbps)),
    });

    Ok(results)
}
```

---

## 内部函数：`is_accelerate_success`（第 1128 行）

**功能**：加速测试的宽松成功判定，403/404 算"可用但受限"而非失败。

```rust
fn is_accelerate_success(status: reqwest::StatusCode, body: &str) -> (bool, Option<String>) {
    if status.is_success() {
        return (true, None);
    }
    let lower = body.to_lowercase();
    if status.as_u16() == 403 {
        return (true, Some("加速地址可用，但该资源无法加速 (403)".to_string()));
    }
    if status.as_u16() == 404 {
        return (true, Some("加速地址可用，但该资源无法加速 (404)".to_string()));
    }
    if lower.contains("invalid input") || lower.contains("无效输入") {
        return (true, Some("加速地址可用，但该资源无法加速".to_string()));
    }
    (false, None)
}
```

---

## 内部函数：`test_github_accelerate`（第 1153 行）

**功能**：加速模式专用，依次测试 4 个端点 + 下载速度：

| 序号 | 测试项   | 方式         | URL                                                              |
|------|----------|--------------|------------------------------------------------------------------|
| 1    | 文件访问 | HTTP GET     | `{accel}/{raw.githubusercontent.com}/...`                         |
| 2    | 首页访问 | HTTP GET     | `{accel}/https://www.github.com`                                  |
| 3    | 仓库访问 | git ls-remote | `{accel}/https://github.com/SillyTavern/SillyTavern`             |
| 4    | API 访问 | HTTP GET     | `{accel}/https://api.github.com/repos/.../releases`              |
| 5    | 下载速度 | 流式下载     | `{accel}/https://github.com/al01cn/.../v0.1.5/...tar.gz`         |

**仓库访问**（git ls-remote）关键代码：

```rust
let mut cmd = std::process::Command::new(&git_exe);
cmd.args(["-c", "credential.helper=", "ls-remote", &accel_repo_url])
    .env("GIT_TERMINAL_PROMPT", "0")
    .stdin(std::process::Stdio::null());
#[cfg(target_os = "windows")]
{
    use std::os::windows::process::CommandExt;
    cmd.creation_flags(0x08000000);
}
let output = cmd.output();
```

---

## Tauri Command：`test_download_speed`（第 1569 行）

**功能**：独立下载速度测试。

```rust
/// 测试下载速度
/// mode: "direct" | "accelerate"
#[tauri::command]
pub async fn test_download_speed(
    mode: String,
    host: String,
) -> Result<DownloadSpeedResult, String> {
    let target_url = "https://github.com/al01cn/sillyTavern-launcher/releases/download/v0.1.5/SillyTavern.Launcher.GUI_x64.app.tar.gz";

    let test_url = if mode == "accelerate" {
        let accel_base = host.trim_end_matches('/');
        format!("{}/{}", accel_base, target_url)
    } else {
        target_url.to_string()
    };

    let client = reqwest::Client::builder()
        .user_agent("sillyTavern-launcher")
        .redirect(reqwest::redirect::Policy::limited(15))
        .timeout(std::time::Duration::from_secs(60))
        .build()
        .map_err(|e| format!("Build client failed: {}", e))?;

    run_download_speed_test(&client, &test_url).await
}
```

---

## 内部函数：`run_download_speed_test`（第 1458 行）

**功能**：通用流式下载测速辅助函数，最多下载 4 MB，计算 MB/s。

```rust
async fn run_download_speed_test(
    client: &reqwest::Client,
    url: &str,
) -> Result<DownloadSpeedResult, String> {
    // 生成随机临时文件：$TEMP/spd_{ts}_{pid}.tmp
    let tmp_path = std::env::temp_dir().join(format!("spd_{:x}_{}.tmp", ts, pid));

    let response = client.get(url).send().await?;

    // 流式写入，最多 4 MB
    const MAX_TEST_BYTES: u64 = 4 * 1024 * 1024;
    while let Some(chunk) = stream.next().await {
        file.write_all(&chunk).await?;
        downloaded += chunk.len() as u64;
        if downloaded >= MAX_TEST_BYTES { break; }
    }

    let speed_mbps = (downloaded as f64 / 1_048_576.0) / download_time.as_secs_f64().max(0.001);

    // 异步延迟 2 秒删除临时文件
    tokio::spawn(async move {
        tokio::time::sleep(Duration::from_secs(2)).await;
        let _ = tokio::fs::remove_file(&tmp_path_clone).await;
    });

    Ok(DownloadSpeedResult { speed_mbps, download_time_ms, ... })
}
```

---

## 辅助函数：`format_speed_message`（第 1440 行）

```rust
fn format_speed_message(speed_mbps: f64) -> String {
    if speed_mbps < 1.0       { format!("速度太慢 ({:.2} MB/s)", speed_mbps) }
    else if speed_mbps < 4.0  { format!("速度正常 ({:.2} MB/s)", speed_mbps) }
    else if speed_mbps < 10.0 { format!("速度很快 ({:.2} MB/s)", speed_mbps) }
    else                      { format!("速度极快 ({:.2} MB/s)", speed_mbps) }
}
```

---

## 函数调用关系

```
test_github_multi
├── (mode == "accelerate") → test_github_accelerate
│       ├── is_accelerate_success      （HTTP 判定）
│       └── run_download_speed_test    （下载速度）
│               └── format_speed_message
└── (其他 mode) → 直接执行
        └── run_download_speed_test    （下载速度）

test_github_connection                  （单点延迟）

test_download_speed
└── run_download_speed_test
```
