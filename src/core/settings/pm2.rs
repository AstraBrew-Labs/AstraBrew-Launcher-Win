//! PM2 进程管理器
//!
//! 封装 PM2 CLI，提供酒馆进程的完整生命周期管理：
//! - start: pm2 start server.js --name astrabrew-launcher-sillytavern
//! - stop: pm2 stop astrabrew-launcher-sillytavern
//! - restart: pm2 restart astrabrew-launcher-sillytavern
//! - force_kill: pm2 delete astrabrew-launcher-sillytavern
//! - get_status: pm2 jlist → 解析状态
//! - get_logs: 直接读取 ~/.pm2/logs/<name>-out.log（字节偏移追踪）
//! - clear_logs: pm2 flush + 直接删除日志文件
//!
//! 设计要点：
//! - 所有操作都是同步阻塞的（PM2 CLI 执行很快）
//! - 状态通过 pm2 jlist JSON 解析获取
//! - PM2 进程名固定为 "astrabrew-launcher-sillytavern"，避免与用户自行安装的冲突

use crate::pages::settings::TavernDataMode;
use std::process::Command;

/// 构建 PM2 命令，自动解析 pm2 路径并补全子进程 PATH。
///
/// 打包后的 .app 中 PATH 不含 Homebrew 路径，
/// PM2 是 Node.js 脚本（shebang `#!/usr/bin/env node`），
/// 子进程 PATH 必须包含 node 所在目录。
fn pm2_cmd() -> Command {
    let cmd = Command::new(super::env_detect::resolve_command("pm2"));
    cmd
}

/// PM2 进程名（固定，避免与用户自行安装的 sillytavern 冲突）
pub const PM2_PROCESS_NAME: &str = "astrabrew-launcher-sillytavern";

/// PM2 进程状态
#[derive(Debug, PartialEq, Clone)]
pub enum Pm2Status {
    /// PM2 中没有此进程记录（从未启动过）
    NotStarted,
    /// 运行中
    Online,
    /// 已停止（pm2 stop 后）
    Stopped,
    /// 启动中
    Launching,
    /// 停止中
    Stopping,
    /// 错误状态
    Errored,
    /// 未知状态
    Unknown,
}

impl Pm2Status {
    /// 从 pm2 jlist 返回的 status 字符串解析
    fn from_str(s: &str) -> Self {
        match s {
            "online" => Pm2Status::Online,
            "stopped" => Pm2Status::Stopped,
            "launching" => Pm2Status::Launching,
            "stopping" => Pm2Status::Stopping,
            "errored" => Pm2Status::Errored,
            _ => Pm2Status::Unknown,
        }
    }
}

/// PM2 管理器
pub struct Pm2Manager {
    process_name: String,
}

/// 内部结构：pm2 jlist 中单个进程的关键信息
struct ProcessInfo {
    status: String,
}

impl Pm2Manager {
    pub fn new() -> Self {
        Self {
            process_name: PM2_PROCESS_NAME.to_string(),
        }
    }

    // ---- 基础检查 ----

    /// 检查 PM2 是否已安装（pm2 --version 成功返回即视为已安装）
    pub fn is_installed() -> bool {
        pm2_cmd()
            .arg("--version")
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null())
            .status()
            .map(|s| s.success())
            .unwrap_or(false)
    }

    // ---- 进程操作 ----

    /// 启动酒馆进程（通过 PM2）
    ///
    /// 等价命令行：
    /// ```text
    /// cd <working_dir>
    /// [env GITHUB_PROXY_URL=xxx] \
    /// pm2 start server.js --name astrabrew-launcher-sillytavern \
    ///   [--node-args "--import /path/to/interceptor.js"] \
    ///   [-- --configPath ... --dataRoot ... --browserLaunchEnabled false ...]
    /// ```
    pub fn start(
        &self,
        working_dir: &str,
        data_mode: &TavernDataMode,
        http_proxy: Option<&str>,
        github_proxy_url: Option<&str>,
        is_desktop_mode: bool,
        interceptor_path: Option<&str>,
    ) -> Result<(), String> {
        // 先检查是否已存在（如果已存在则先 delete 再 start，确保全新启动）
        if self.process_exists() {
            self.delete_internal()?;
        }

        let mut cmd = pm2_cmd();
        cmd.arg("start").arg("server.js");
        cmd.arg("--name").arg(&self.process_name);

        // GitHub 加速代理：通过 --node-args 传递 --import
        if let Some(proxy_url) = github_proxy_url {
            if let Some(interceptor) = interceptor_path {
                cmd.arg("--node-args");
                cmd.arg(format!("--import {}", interceptor));
                cmd.env("GITHUB_PROXY_URL", proxy_url);
            }
        }

        // HTTP 代理：注入环境变量（PM2 会自动将当前环境变量传给子进程）
        if let Some(proxy) = http_proxy {
            let proxy_url = crate::core::tavern_process::normalize_proxy_url(proxy);
            cmd.env("HTTP_PROXY", &proxy_url);
            cmd.env("HTTPS_PROXY", &proxy_url);
            cmd.env("http_proxy", &proxy_url);
            cmd.env("https_proxy", &proxy_url);
        }

        // 酒馆脚本参数
        let mut script_args: Vec<String> = Vec::new();

        // 全局数据模式参数
        if *data_mode == TavernDataMode::Global {
            let paths = crate::utils::app_paths();
            script_args.push("--configPath".to_string());
            script_args.push(
                paths
                    .global_tavern_config_file()
                    .to_string_lossy()
                    .to_string(),
            );
            script_args.push("--dataRoot".to_string());
            script_args.push(
                paths
                    .default_global_data_dir()
                    .to_string_lossy()
                    .to_string(),
            );
        }

        // HTTP 代理酒馆参数
        if let Some(proxy) = http_proxy {
            let proxy_url = crate::core::tavern_process::normalize_proxy_url(proxy);
            script_args.push("--requestProxyEnabled".to_string());
            script_args.push("true".to_string());
            script_args.push("--requestProxyUrl".to_string());
            script_args.push(proxy_url);
            script_args.push("--requestProxyBypass".to_string());
            script_args.push("localhost 127.0.0.1 ::1".to_string());
        }

        // 桌面模式
        if is_desktop_mode {
            script_args.push("--browserLaunchEnabled".to_string());
            script_args.push("false".to_string());
        }

        // PM2 分隔符：-- 之后是脚本参数
        if !script_args.is_empty() {
            cmd.arg("--");
            for arg in script_args {
                cmd.arg(arg);
            }
        }

        cmd.current_dir(working_dir);
        cmd.stdout(std::process::Stdio::piped());
        cmd.stderr(std::process::Stdio::piped());

        let output = cmd.output().map_err(|e| format!("无法执行 pm2 start: {}", e))?;

        if output.status.success() {
            Ok(())
        } else {
            let stderr = String::from_utf8_lossy(&output.stderr);
            let stdout = String::from_utf8_lossy(&output.stdout);
            Err(format!(
                "PM2 启动失败: {}",
                if stderr.is_empty() { stdout } else { stderr }
            )
            .trim()
            .to_string())
        }
    }

    /// 停止酒馆进程（pm2 stop astrabrew-launcher-sillytavern）
    pub fn stop(&self) -> Result<(), String> {
        let output = pm2_cmd()
            .arg("stop")
            .arg(&self.process_name)
            .stdout(std::process::Stdio::piped())
            .stderr(std::process::Stdio::piped())
            .output()
            .map_err(|e| format!("无法执行 pm2 stop: {}", e))?;

        if output.status.success() {
            Ok(())
        } else {
            let stderr = String::from_utf8_lossy(&output.stderr);
            // "process not found" 不算错误
            if stderr.contains("not found") || stderr.contains("not exist") {
                Ok(())
            } else {
                Err(format!("PM2 停止失败: {}", stderr.trim()))
            }
        }
    }

    /// 强制停止并删除进程（pm2 delete astrabrew-launcher-sillytavern）
    pub fn delete(&self) -> Result<(), String> {
        self.delete_internal()
    }

    /// 内部删除实现
    fn delete_internal(&self) -> Result<(), String> {
        let output = pm2_cmd()
            .arg("delete")
            .arg(&self.process_name)
            .stdout(std::process::Stdio::piped())
            .stderr(std::process::Stdio::piped())
            .output()
            .map_err(|e| format!("无法执行 pm2 delete: {}", e))?;

        if output.status.success() {
            Ok(())
        } else {
            let stderr = String::from_utf8_lossy(&output.stderr);
            // "process not found" 不算错误
            if stderr.contains("not found") || stderr.contains("not exist") {
                Ok(())
            } else {
                Err(format!("PM2 删除失败: {}", stderr.trim()))
            }
        }
    }

    /// 重启酒馆进程（pm2 restart astrabrew-launcher-sillytavern）
    pub fn restart(&self) -> Result<(), String> {
        let output = pm2_cmd()
            .arg("restart")
            .arg(&self.process_name)
            .stdout(std::process::Stdio::piped())
            .stderr(std::process::Stdio::piped())
            .output()
            .map_err(|e| format!("无法执行 pm2 restart: {}", e))?;

        if output.status.success() {
            Ok(())
        } else {
            let stderr = String::from_utf8_lossy(&output.stderr);
            let stdout = String::from_utf8_lossy(&output.stdout);
            Err(format!(
                "PM2 重启失败: {}",
                if stderr.is_empty() { stdout } else { stderr }
            )
            .trim()
            .to_string())
        }
    }

    // ---- 状态获取 ----

    /// 获取 PM2 进程状态
    ///
    /// 通过 `pm2 jlist` 获取 JSON 列表，解析目标进程状态。
    /// 返回 Pm2Status 枚举值。
    pub fn get_status(&self) -> Pm2Status {
        match self.get_process_info() {
            Some(info) => Pm2Status::from_str(&info.status),
            None => Pm2Status::NotStarted,
        }
    }

    /// 检查 PM2 中是否有此进程记录
    fn process_exists(&self) -> bool {
        self.get_process_info().is_some()
    }

    /// 从 pm2 jlist 输出中提取目标进程信息
    fn get_process_info(&self) -> Option<ProcessInfo> {
        let output = pm2_cmd()
            .arg("jlist")
            .stdout(std::process::Stdio::piped())
            .stderr(std::process::Stdio::null())
            .output()
            .ok()?;

        if !output.status.success() {
            return None;
        }

        let stdout = String::from_utf8_lossy(&output.stdout);
        // 简易 JSON 解析：在 JSON 数组中查找 name 匹配的对象
        // 我们使用简单的字符串搜索而非引入 serde_json 依赖
        // pm2 jlist 返回格式: [{"name":"astrabrew-launcher-sillytavern","pm2_env":{"status":"online",...},...}]

        // 查找 "name":"<process_name>" 的位置
        let search_name = format!("\"name\":\"{}\"", self.process_name);
        if let Some(_name_pos) = stdout.find(&search_name) {
            // 在该对象的范围内查找 "status":"xxx"
            // 从 name 位置向后搜索 status
            let after_name = &stdout[_name_pos..];
            if let Some(status_pos) = after_name.find("\"status\":\"") {
                let status_start = status_pos + "\"status\":\"".len();
                let status_rest = &after_name[status_start..];
                if let Some(quote_end) = status_rest.find('"') {
                    let status = status_rest[..quote_end].to_string();
                    return Some(ProcessInfo { status });
                }
            }
        }

        None
    }

    // ---- 日志操作 ----

    /// 获取 PM2 stdout 日志文件路径（`%USERPROFILE%\.pm2\logs\<name>-out.log`）
    fn out_log_path(&self) -> Option<std::path::PathBuf> {
        let home = std::env::var("USERPROFILE")
            .or_else(|_| std::env::var("HOME"))
            .ok()?;
        Some(std::path::PathBuf::from(home)
            .join(".pm2")
            .join("logs")
            .join(format!("{}-out.log", self.process_name)))
    }

    /// 获取 PM2 stderr 日志文件路径（`%USERPROFILE%\.pm2\logs\<name>-error.log`）
    fn error_log_path(&self) -> Option<std::path::PathBuf> {
        let home = std::env::var("USERPROFILE")
            .or_else(|_| std::env::var("HOME"))
            .ok()?;
        Some(std::path::PathBuf::from(home)
            .join(".pm2")
            .join("logs")
            .join(format!("{}-error.log", self.process_name)))
    }

    /// 读取 stdout 日志文件从指定字节偏移开始的新内容。
    ///
    /// 返回 `(新行列表, 新字节偏移)`。
    /// - 如果文件不存在或读取失败，返回空列表和原偏移。
    /// - 如果文件被截断（偏移超出文件大小），自动从头开始。
    ///
    /// 直接读取文件而非 `pm2 logs` 命令，原因：
    /// 1. `pm2 flush` 在进程不存在时不会清空日志文件 → 旧日志残留 → 重复显示
    /// 2. `pm2 logs --lines N` 返回最后 N 行，与行偏移追踪不兼容（sliding window）
    /// 3. `pm2 logs --raw` 输出含 [TAILING] 和文件路径行，需额外解析
    pub fn read_out_logs_since(&self, byte_offset: u64) -> (Vec<String>, u64) {
        let path = match self.out_log_path() {
            Some(p) => p,
            None => return (Vec::new(), byte_offset),
        };

        use std::io::{Read, Seek, SeekFrom};
        let mut file = match std::fs::File::open(&path) {
            Ok(f) => f,
            Err(_) => return (Vec::new(), byte_offset),
        };

        let file_size = file.metadata().map(|m| m.len()).unwrap_or(0);

        // 文件被截断（pm2 flush / 删除后重建），重置到开头
        let start = if byte_offset > file_size {
            0
        } else {
            byte_offset
        };

        if start > 0 {
            if file.seek(SeekFrom::Start(start)).is_err() {
                return (Vec::new(), byte_offset);
            }
        }

        let mut content = String::new();
        if file.read_to_string(&mut content).is_err() {
            return (Vec::new(), byte_offset);
        }

        let new_offset = start + content.len() as u64;
        let lines: Vec<String> = content
            .lines()
            .map(|l| l.trim().to_string())
            .filter(|l| !l.is_empty())
            .collect();

        (lines, new_offset)
    }

    /// 清空 PM2 日志。
    ///
    /// 先执行 `pm2 flush`（处理进程运行中的情况），再直接删除日志文件
    /// （处理进程不存在时 `pm2 flush` 不清空文件的问题）。
    pub fn clear_logs(&self) -> Result<(), String> {
        // 先尝试 pm2 flush（进程运行中时正确清空）
        let _ = pm2_cmd()
            .arg("flush")
            .arg(&self.process_name)
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null())
            .output();

        // 直接删除日志文件，确保即使 PM2 进程不存在也彻底清空
        if let Some(path) = self.out_log_path() {
            let _ = std::fs::remove_file(&path);
        }
        if let Some(path) = self.error_log_path() {
            let _ = std::fs::remove_file(&path);
        }

        Ok(())
    }

    // ---- 更新配置 ----

    /// 更新配置后重启（pm2 restart astrabrew-launcher-sillytavern）
    ///
    /// 等同于 restart，因为 PM2 没有单独的 "reload config" 命令。
    /// 酒馆配置变更后需要重启才能生效。
    #[allow(dead_code)]
    pub fn update_config(&self) -> Result<(), String> {
        self.restart()
    }
}
