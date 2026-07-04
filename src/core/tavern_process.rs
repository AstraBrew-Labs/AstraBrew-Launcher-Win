//! SillyTavern 进程管理器
//!
//! 负责 node server.js 子进程的完整生命周期：
//! - 启动（独立/全局数据模式）
//! - 优雅停止（SIGTERM）
//! - 强制停止（SIGKILL）
//! - 重启
//! - 实时日志输出（stdout/stderr → channel）
//!
//! 设计要点：
//! - 子进程 stdout/stderr 通过后台线程异步读取，不阻塞 UI
//! - 通过 mpsc channel 将日志行传递到主线程
//! - Drop 时自动 kill 子进程（启动器关闭 → 酒馆也关闭）
//! - GitHub 代理：通过 --import 预加载拦截器脚本，重写 GitHub URL

use crate::core::settings::env_detect;
use crate::pages::settings::TavernDataMode;
use std::io::{BufRead, BufReader};
use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use std::sync::mpsc;

/// GitHub 代理拦截器脚本（编译时内嵌）
const GITHUB_INTERCEPTOR_JS: &str = r#"// GitHub URL 拦截器 - 自动重写 GitHub 链接到镜像
// 代理地址从环境变量 GITHUB_PROXY_URL 读取
import https from 'https';
import http from 'http';

const originalHttpsRequest = https.request;
const originalHttpsGet = https.get;
const originalHttpRequest = http.request;
const originalHttpGet = http.get;

const PROXY_URL = (process.env.GITHUB_PROXY_URL || '').replace(/\/+$/, '');

function rewriteGitHubUrl(url) {
    if (!url || typeof url !== 'string') return url;

    // 不重写 API 请求
    if (url.includes('api.github.com')) return url;

    // 只重写 GitHub 相关的 URL
    if (url.includes('github.com') || url.includes('raw.githubusercontent.com')) {
        return PROXY_URL + '/' + url;
    }

    return url;
}

// 拦截 https.request
https.request = function(url, options, callback) {
    let req;
    if (typeof url === 'string') {
        const rewrittenUrl = rewriteGitHubUrl(url);
        req = originalHttpsRequest.call(https, rewrittenUrl, options, callback);
    } else if (url && typeof url === 'object') {
        if (url.href) {
            const newUrl = Object.assign({}, url);
            newUrl.href = rewriteGitHubUrl(url.href);
            if (newUrl.href !== url.href) {
                try {
                    const parsed = new URL(newUrl.href);
                    newUrl.host = parsed.host;
                    newUrl.hostname = parsed.hostname;
                    newUrl.pathname = parsed.pathname;
                    newUrl.protocol = parsed.protocol;
                    newUrl.port = parsed.port;
                } catch (e) {}
            }
            req = originalHttpsRequest.call(https, newUrl, options, callback);
        } else {
            req = originalHttpsRequest.call(https, url, options, callback);
        }
    } else {
        req = originalHttpsRequest.call(https, url, options, callback);
    }

    const originalWrite = req.write;
    req.write = function(chunk, encoding, callback) {
        if (chunk && typeof chunk === 'string') {
            try {
                const data = JSON.parse(chunk);
                if (data.url && typeof data.url === 'string') {
                    const rewrittenUrl = rewriteGitHubUrl(data.url);
                    if (rewrittenUrl !== data.url) {
                        data.url = rewrittenUrl;
                        chunk = JSON.stringify(data);
                    }
                }
            } catch (e) {}
        }
        return originalWrite.call(req, chunk, encoding, callback);
    };

    return req;
};

// 拦截 https.get
https.get = function(url, options, callback) {
    if (typeof url === 'string') {
        const rewrittenUrl = rewriteGitHubUrl(url);
        return originalHttpsGet.call(https, rewrittenUrl, options, callback);
    } else if (url && typeof url === 'object') {
        if (url.href) {
            const newUrl = Object.assign({}, url);
            newUrl.href = rewriteGitHubUrl(url.href);
            if (newUrl.href !== url.href) {
                try {
                    const parsed = new URL(newUrl.href);
                    newUrl.host = parsed.host;
                    newUrl.hostname = parsed.hostname;
                    newUrl.pathname = parsed.pathname;
                    newUrl.protocol = parsed.protocol;
                    newUrl.port = parsed.port;
                } catch (e) {}
            }
            return originalHttpsGet.call(https, newUrl, options, callback);
        }
        return originalHttpsGet.call(https, url, options, callback);
    }
    return originalHttpsGet.call(https, url, options, callback);
};

// 拦截 http.request
http.request = function(url, options, callback) {
    if (typeof url === 'string') {
        const rewrittenUrl = rewriteGitHubUrl(url);
        return originalHttpRequest.call(http, rewrittenUrl, options, callback);
    } else if (url && typeof url === 'object') {
        if (url.href) {
            const newUrl = Object.assign({}, url);
            newUrl.href = rewriteGitHubUrl(url.href);
            if (newUrl.href !== url.href) {
                try {
                    const parsed = new URL(newUrl.href);
                    newUrl.host = parsed.host;
                    newUrl.hostname = parsed.hostname;
                    newUrl.pathname = parsed.pathname;
                    newUrl.protocol = parsed.protocol;
                    newUrl.port = parsed.port;
                } catch (e) {}
            }
            return originalHttpRequest.call(http, newUrl, options, callback);
        }
        return originalHttpRequest.call(http, url, options, callback);
    }
    return originalHttpRequest.call(http, url, options, callback);
};

// 拦截 http.get
http.get = function(url, options, callback) {
    if (typeof url === 'string') {
        const rewrittenUrl = rewriteGitHubUrl(url);
        return originalHttpGet.call(http, rewrittenUrl, options, callback);
    } else if (url && typeof url === 'object') {
        if (url.href) {
            const newUrl = Object.assign({}, url);
            newUrl.href = rewriteGitHubUrl(url.href);
            if (newUrl.href !== url.href) {
                try {
                    const parsed = new URL(newUrl.href);
                    newUrl.host = parsed.host;
                    newUrl.hostname = parsed.hostname;
                    newUrl.pathname = parsed.pathname;
                    newUrl.protocol = parsed.protocol;
                    newUrl.port = parsed.port;
                } catch (e) {}
            }
            return originalHttpGet.call(http, newUrl, options, callback);
        }
        return originalHttpGet.call(http, url, options, callback);
    }
    return originalHttpGet.call(http, url, options, callback);
};

console.log('[GitHub Proxy] URL interceptor loaded, proxy:', PROXY_URL);
"#;

/// 在临时目录生成 GitHub 代理拦截器文件，返回其绝对路径。
/// 每次启动时重新写入，确保内容始终与二进制内嵌版本一致。
pub fn prepare_interceptor() -> std::io::Result<PathBuf> {
    let dir = &crate::utils::app_paths().temp;
    std::fs::create_dir_all(dir)?;
    let path = dir.join("github-proxy-interceptor.js");
    std::fs::write(&path, GITHUB_INTERCEPTOR_JS)?;
    Ok(path)
}

/// 检查当前 Node.js 是否支持 `--import` 标志（Node.js >= 19.0.0）
pub fn node_supports_import() -> bool {
    let output = Command::new(env_detect::resolve_command("node"))
        .arg("--version")
        .output()
        .ok();
    match output {
        Some(out) => {
            let version = String::from_utf8_lossy(&out.stdout);
            // 格式: v22.16.0
            if let Some(ver) = version.strip_prefix('v') {
                let parts: Vec<&str> = ver.trim().split('.').collect();
                if let Some(major) = parts.first().and_then(|s| s.parse::<u32>().ok()) {
                    return major >= 19;
                }
            }
            false
        }
        None => false,
    }
}

/// 确保代理地址带协议前缀（默认 http://）
pub fn normalize_proxy_url(proxy: &str) -> String {
    if proxy.starts_with("http://")
        || proxy.starts_with("https://")
        || proxy.starts_with("socks5://")
        || proxy.starts_with("socks4://")
    {
        proxy.to_string()
    } else {
        format!("http://{}", proxy)
    }
}

/// 构建启动命令的可读字符串（用于日志展示）
pub fn build_startup_command(
    _working_dir: &str,
    data_mode: &TavernDataMode,
    http_proxy: Option<&str>,
    _github_proxy_url: Option<&str>,
    is_desktop_mode: bool,
) -> String {
    let mut parts: Vec<String> = vec!["node".to_string()];

    parts.push("server.js".to_string());

    if is_desktop_mode {
        parts.push("--browserLaunchEnabled false".to_string());
    }

    if *data_mode == TavernDataMode::Global {
        let paths = crate::utils::app_paths();
        parts.push(format!(
            "--configPath {}",
            paths.global_tavern_config_file().display()
        ));
        parts.push(format!(
            "--dataRoot {}",
            paths.default_global_data_dir().display()
        ));
    }

    if let Some(proxy) = http_proxy {
        let proxy_url = normalize_proxy_url(proxy);
        parts.push("--requestProxyEnabled true".to_string());
        parts.push(format!("--requestProxyUrl {}", proxy_url));
        parts.push("--requestProxyBypass \"localhost 127.0.0.1 ::1\"".to_string());
    }

    parts.join(" ")
}

/// 进程管理器
pub struct TavernProcess {
    /// 当前运行的子进程
    child: Option<Child>,
    /// 日志接收端（后台线程写入此处）
    log_rx: Option<mpsc::Receiver<String>>,
    /// 标记正在等待进程退出（已发送 SIGTERM）
    waiting_exit: bool,
}

impl TavernProcess {
    pub fn new() -> Self {
        Self {
            child: None,
            log_rx: None,
            waiting_exit: false,
        }
    }

    /// 是否正在运行（或等待退出）
    pub fn is_running(&self) -> bool {
        self.child.is_some()
    }

    /// 启动 SillyTavern 进程
    ///
    /// - `working_dir`: 酒馆实例文件夹路径（node server.js 所在目录）
    /// - `data_mode`: 数据模式 — 独立模式不带额外参数，全局模式附加 --configPath 和 --dataRoot
    /// - `http_proxy`: HTTP(S) 代理地址（启用代理时传入，设置 HTTP_PROXY / HTTPS_PROXY 环境变量）
    /// - `github_proxy_url`: GitHub 加速代理 URL（启用时通过 --import 预加载拦截器脚本，重写 GitHub 请求到加速地址）
    pub fn start(
        &mut self,
        working_dir: &str,
        data_mode: &TavernDataMode,
        http_proxy: Option<&str>,
        github_proxy_url: Option<&str>,
        is_desktop_mode: bool,
    ) -> Result<(), String> {
        if self.child.is_some() {
            return Err("进程已在运行".into());
        }

        let mut cmd = Command::new(env_detect::resolve_command("node"));

        // GitHub 加速代理：通过 --import 预加载拦截器脚本
        if let Some(proxy_url) = github_proxy_url {
            match prepare_interceptor() {
                Ok(interceptor_path) => {
                    cmd.arg("--import");
                    cmd.arg(interceptor_path.to_string_lossy().to_string());
                    cmd.env("GITHUB_PROXY_URL", proxy_url);
                }
                Err(e) => {
                    eprintln!("[AstraBrew] 写入 GitHub 拦截器失败: {}", e);
                }
            }
        }

        cmd.arg("server.js");

        // 全局数据模式 → 附加配置路径参数
        if *data_mode == TavernDataMode::Global {
            let paths = crate::utils::app_paths();
            cmd.arg("--configPath");
            cmd.arg(
                paths
                    .global_tavern_config_file()
                    .to_string_lossy()
                    .to_string(),
            );
            cmd.arg("--dataRoot");
            cmd.arg(
                paths
                    .default_global_data_dir()
                    .to_string_lossy()
                    .to_string(),
            );
        }

        // 代理设置：注入环境变量 + 酒馆 CLI 参数
        if let Some(proxy) = http_proxy {
            // 确保代理地址带协议前缀（http:// 或 socks5://）
            let proxy_url = normalize_proxy_url(proxy);

            cmd.env("HTTP_PROXY", &proxy_url);
            cmd.env("HTTPS_PROXY", &proxy_url);
            cmd.env("http_proxy", &proxy_url);
            cmd.env("https_proxy", &proxy_url);

            // 告知酒馆代理配置
            cmd.arg("--requestProxyEnabled");
            cmd.arg("true");
            cmd.arg("--requestProxyUrl");
            cmd.arg(&proxy_url);
            cmd.arg("--requestProxyBypass");
            cmd.arg("localhost 127.0.0.1 ::1");
        }

        // 桌面模式/服务器模式：禁止酒馆自动打开浏览器
        if is_desktop_mode {
            cmd.arg("--browserLaunchEnabled");
            cmd.arg("false");
        }

        cmd.current_dir(working_dir);
        cmd.stdout(Stdio::piped());
        cmd.stderr(Stdio::piped());

        let mut child = cmd.spawn().map_err(|e| format!("无法启动进程: {}", e))?;

        let stdout = child.stdout.take();
        let stderr = child.stderr.take();
        let (tx, rx) = mpsc::channel();

        // 后台线程读取 stdout
        if let Some(stdout) = stdout {
            let tx = tx.clone();
            std::thread::spawn(move || {
                let reader = BufReader::new(stdout);
                for line in reader.lines() {
                    match line {
                        Ok(l) => {
                            if tx.send(l).is_err() {
                                break; // 接收端已关闭
                            }
                        }
                        Err(_) => break,
                    }
                }
            });
        }

        // 后台线程读取 stderr
        if let Some(stderr) = stderr {
            std::thread::spawn(move || {
                let reader = BufReader::new(stderr);
                for line in reader.lines() {
                    match line {
                        Ok(l) => {
                            if tx.send(l).is_err() {
                                break;
                            }
                        }
                        Err(_) => break,
                    }
                }
            });
        }

        self.child = Some(child);
        self.log_rx = Some(rx);
        self.waiting_exit = false;

        Ok(())
    }

    /// 优雅停止 — 发送 SIGTERM，不阻塞
    pub fn stop(&mut self) {
        if self.child.is_none() {
            return;
        }
        // 获取 pid
        if let Some(ref child) = self.child {
            let pid = child.id();
            // 发送 SIGTERM（优雅关闭）
            let _ = Command::new("kill").arg(pid.to_string()).spawn();
        }
        self.waiting_exit = true;
    }

    /// 强制停止 — 发送 SIGKILL，立即结束
    pub fn kill(&mut self) {
        if let Some(ref mut child) = self.child {
            let _ = child.kill(); // SIGKILL
            let _ = child.wait();
        }
        self.child = None;
        self.log_rx = None;
        self.waiting_exit = false;
    }

    /// 从 channel 拉取新日志行（非阻塞）
    pub fn poll_logs(&mut self) -> Vec<String> {
        let mut logs = Vec::new();
        if let Some(ref rx) = self.log_rx {
            while let Ok(line) = rx.try_recv() {
                logs.push(line);
            }
        }
        logs
    }

    /// 检查进程是否已退出（非阻塞）
    ///
    /// 返回 `Some(exit_code)` 表示进程已退出，`None` 表示仍在运行
    pub fn check_exited(&mut self) -> Option<Option<i32>> {
        if let Some(ref mut child) = self.child {
            match child.try_wait() {
                Ok(Some(status)) => {
                    let code = status.code();
                    self.child = None;
                    self.log_rx = None;
                    self.waiting_exit = false;
                    Some(code)
                }
                Ok(None) => None, // 仍在运行
                Err(_) => None,
            }
        } else {
            None
        }
    }
}

impl Drop for TavernProcess {
    fn drop(&mut self) {
        // 启动器关闭 → 自动杀死子进程（不留在后台）
        if let Some(ref mut child) = self.child {
            let _ = child.kill();
            let _ = child.wait();
        }
    }
}
