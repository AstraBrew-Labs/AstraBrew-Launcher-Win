//! PM2 进程管理器
//!
//! 封装 PM2 CLI，提供酒馆进程的完整生命周期管理。

use crate::EnvInstallProgress;
use std::fs;
use std::io::{BufRead, BufReader, Write};
use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::sync::mpsc::Sender;

use crate::pages::settings::{EnvSource, TavernDataMode};
use std::process::Command;

const CREATE_NO_WINDOW: u32 = 0x08000000;

/// 统一为 PM2 / npm 子进程设置隐藏窗口标志，避免 Windows 下闪黑框。
fn apply_no_window(cmd: &mut Command) {
    cmd.creation_flags(CREATE_NO_WINDOW);
}

/// PM2 运行时目录固定到应用数据目录，避免污染用户全局 PM2 状态。
fn pm2_runtime_dir() -> PathBuf {
    crate::core::env::get_data_dir()
        .join("lib")
        .join("pm2")
        .join("runtime")
        .join("pm2")
}

/// 将 PM2_HOME 注入命令环境，确保所有 PM2 调用读写同一份运行时数据。
fn apply_pm2_runtime_env(cmd: &mut Command) {
    cmd.env("PM2_HOME", pm2_runtime_dir());
}

/// 根据 pm2 安装目录定位对应的 JS 入口，优先直接用 node 执行，避免走 .cmd 包装脚本。
fn find_pm2_script(pm2_root: &Path, script_name: &str) -> Option<PathBuf> {
    let bin_dir = pm2_root.join("node_modules").join("pm2").join("bin");
    let candidates = [
        bin_dir.join(script_name),
        bin_dir.join(format!("{}.js", script_name)),
        bin_dir.join(format!("{}.cjs", script_name)),
    ];

    candidates.into_iter().find(|candidate| candidate.exists())
}

/// 为 PM2 解析最合适的 Node.js 路径。
///
/// - 内置 PM2 优先配套内置 Node.js。
/// - 系统 PM2 优先系统 Node.js，不存在时回退到内置 Node.js。
fn resolve_pm2_node_path(pm2_root: &Path) -> Option<PathBuf> {
    let builtin_pm2_root = crate::core::env::get_data_dir().join("lib").join("pm2");
    if pm2_root == builtin_pm2_root {
        crate::core::env::get_builtin_node_path()
    } else {
        crate::core::env::get_system_cmd_path("node")
            .or_else(crate::core::env::get_builtin_node_path)
    }
}

/// 优先构建“node + pm2 JS 入口”的无黑窗命令。
///
/// 这样可以绕开 `pm2.cmd` / `cmd /c` 这类批处理层，显著降低命令行窗口闪烁概率。
fn build_pm2_node_command(script_name: &str) -> Option<Command> {
    let pm2_path = crate::core::env::get_pm2_path()?;
    let pm2_root = pm2_path.parent()?;
    let script_path = find_pm2_script(pm2_root, script_name)?;
    let node_path = resolve_pm2_node_path(pm2_root)?;

    let mut cmd = Command::new(node_path);
    apply_no_window(&mut cmd);
    cmd.arg(script_path);
    apply_pm2_runtime_env(&mut cmd);
    Some(cmd)
}

/// PM2 安装函数：通过 npm 安装到内置目录，生成包装脚本
/// `registry` 可选：None 使用默认源，Some(url) 使用指定 registry
pub fn install_pm2(
    progress_sender: Option<Sender<EnvInstallProgress>>,
    registry: Option<String>,
) -> Result<(), Box<dyn std::error::Error>> {
    let data_dir = crate::core::env::get_data_dir();
    let lib_dir = data_dir.join("lib");
    let pm2_dir = lib_dir.join("pm2");
    let nodejs_dir = lib_dir.join("nodejs");
    let npm_cmd = nodejs_dir.join("npm.cmd");

    if !npm_cmd.exists() {
        let err = "Node.js 未安装，PM2 安装需要 Node.js 环境".to_string();
        if let Some(tx) = &progress_sender {
            let _ = tx.send(EnvInstallProgress::Error(err.clone()));
        }
        return Err(err.into());
    }

    if let Some(tx) = &progress_sender {
        let _ = tx.send(EnvInstallProgress::Status(format!(
            "安装目录: {}",
            pm2_dir.display()
        )));
    }

    // 清理旧安装
    if pm2_dir.exists() {
        if let Some(tx) = &progress_sender {
            let _ = tx.send(EnvInstallProgress::Status("清理旧安装...".to_string()));
        }
        let _ = fs::remove_dir_all(&pm2_dir);
    }
    fs::create_dir_all(&pm2_dir)?;

    // npm install pm2 --prefix <pm2_dir> [--registry <url>]
    if let Some(ref url) = registry {
        if let Some(tx) = &progress_sender {
            let _ = tx.send(EnvInstallProgress::Status(format!(
                "正在安装 PM2 (npm --registry {})...",
                url
            )));
        }
    } else {
        if let Some(tx) = &progress_sender {
            let _ = tx.send(EnvInstallProgress::Status("正在安装 PM2 (npm)...".to_string()));
        }
    }

    let is_script = npm_cmd
        .extension()
        .and_then(|ext| ext.to_str())
        .map(|ext| ext.eq_ignore_ascii_case("cmd") || ext.eq_ignore_ascii_case("bat"))
        .unwrap_or(false);

    let mut cmd = if is_script {
        let mut c = Command::new("cmd");
        apply_no_window(&mut c);
        c.arg("/c").arg(&npm_cmd);
        c
    } else {
        let mut c = Command::new(&npm_cmd);
        apply_no_window(&mut c);
        c
    };

    cmd.args(["install", "pm2", "--prefix"]);
    cmd.arg(&pm2_dir);
    // 不生成 package-lock.json（不需要锁版本）
    cmd.arg("--no-package-lock");
    // 关闭 npm 的 spinner/进度条（输出更干净）
    cmd.arg("--no-progress");
    // 关闭审计
    cmd.arg("--no-audit");
    // 关闭 funding 消息
    cmd.arg("--no-fund");
    // 自定义 registry
    if let Some(ref url) = registry {
        cmd.arg("--registry");
        cmd.arg(url);
    }
    cmd.stdout(std::process::Stdio::piped());
    cmd.stderr(std::process::Stdio::piped());

    let mut child = cmd.spawn().map_err(|e| format!("无法执行 npm install: {}", e))?;

    // 实时读取 stdout 发到日志
    if let Some(stdout) = child.stdout.take() {
        let tx_clone = progress_sender.clone();
        std::thread::spawn(move || {
            let reader = BufReader::new(stdout);
            for line in reader.lines().map_while(Result::ok) {
                let trimmed = line.trim();
                if !trimmed.is_empty() && !is_npm_noise(trimmed) {
                    if let Some(tx) = &tx_clone {
                        let _ = tx.send(EnvInstallProgress::Status(trimmed.to_string()));
                    }
                }
            }
        });
    }

    // 实时读取 stderr（只保留有用的错误日志）
    if let Some(stderr) = child.stderr.take() {
        let tx_clone = progress_sender.clone();
        std::thread::spawn(move || {
            let reader = BufReader::new(stderr);
            for line in reader.lines().map_while(Result::ok) {
                let trimmed = line.trim();
                if !trimmed.is_empty() && !is_npm_noise(trimmed) {
                    if let Some(tx) = &tx_clone {
                        let _ = tx.send(EnvInstallProgress::Status(format!("[npm] {}", trimmed)));
                    }
                }
            }
        });
    }

    let status = child.wait().map_err(|e| format!("npm install 异常: {}", e))?;

    if !status.success() {
        let err = format!("PM2 安装失败 (exit code: {})", status);
        if let Some(tx) = &progress_sender {
            let _ = tx.send(EnvInstallProgress::Error(err.clone()));
        }
        return Err(err.into());
    }

    if let Some(tx) = &progress_sender {
        let _ = tx.send(EnvInstallProgress::Status("生成包装脚本...".to_string()));
    }

    // 生成 pm2.cmd 包装脚本（通过内置 node.exe 执行 pm2/p2-runtime JS 脚本）
    let pm2_cmd_path = pm2_dir.join("pm2.cmd");

    let wrapper_content = "\
@echo off\r\n\
set PM2_HOME=%~dp0runtime\\pm2\r\n\
\"%~dp0..\\nodejs\\node.exe\" \"%~dp0node_modules\\pm2\\bin\\pm2\" %*\r\n";

    let mut f = fs::File::create(&pm2_cmd_path)?;
    f.write_all(wrapper_content.as_bytes())?;

    // 生成 pm2-runtime.cmd
    let wrapper_runtime = "\
@echo off\r\n\
set PM2_HOME=%~dp0runtime\\pm2\r\n\
\"%~dp0..\\nodejs\\node.exe\" \"%~dp0node_modules\\pm2\\bin\\pm2-runtime\" %*\r\n";
    let mut f2 = fs::File::create(pm2_dir.join("pm2-runtime.cmd"))?;
    f2.write_all(wrapper_runtime.as_bytes())?;

    // 创建运行时目录
    let runtime_dir = pm2_dir.join("runtime").join("pm2");
    fs::create_dir_all(&runtime_dir)?;

    if let Some(tx) = &progress_sender {
        let _ = tx.send(EnvInstallProgress::Status(format!(
            "PM2_HOME: {}",
            runtime_dir.display()
        )));
    }

    // 验证安装
    if let Some(tx) = &progress_sender {
        let _ = tx.send(EnvInstallProgress::Status("验证安装...".to_string()));
    }

    let verify_output = pm2_cmd()
        .arg("-v")
        .stdout(std::process::Stdio::piped())
        .stderr(std::process::Stdio::piped())
        .output()
        .map_err(|e| format!("PM2 验证失败: {}", e))?;

    if verify_output.status.success() {
        let stdout = String::from_utf8_lossy(&verify_output.stdout);
        let version = stdout.trim().to_string();
        if let Some(tx) = &progress_sender {
            let _ = tx.send(EnvInstallProgress::Version(version));
            let _ = tx.send(EnvInstallProgress::Finished);
        }
        Ok(())
    } else {
        let stderr = String::from_utf8_lossy(&verify_output.stderr);
        let err = format!("PM2 安装验证失败: {}", stderr.trim());
        if let Some(tx) = &progress_sender {
            let _ = tx.send(EnvInstallProgress::Error(err.clone()));
        }
        Err(err.into())
    }
}

/// 构建 PM2 命令（使用内置包装脚本，自动设置 PM2_HOME）
fn pm2_cmd() -> Command {
    if let Some(cmd) = build_pm2_node_command("pm2") {
        return cmd;
    }

    let pm2_path = crate::core::env::get_pm2_path().unwrap_or_else(|| PathBuf::from("pm2"));
    let mut cmd = Command::new(&pm2_path);
    apply_no_window(&mut cmd);
    apply_pm2_runtime_env(&mut cmd);
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
        env_mode: EnvSource,
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

        // 内置环境模式：将 Node.js / MinGit 路径注入到子进程 PATH
        // PM2 会捕获当前环境并传递给托管的子进程
        if env_mode == EnvSource::Builtin {
            crate::core::env::apply_builtin_path_to_command(&mut cmd);
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

        let process_list: serde_json::Value = serde_json::from_slice(&output.stdout).ok()?;
        let processes = process_list.as_array()?;

        processes.iter().find_map(|process| {
            let name = process.get("name")?.as_str()?;
            if name != self.process_name {
                return None;
            }

            let status = process
                .get("pm2_env")?
                .get("status")?
                .as_str()?
                .to_string();

            Some(ProcessInfo { status })
        })
    }

    // ---- 日志操作 ----

    /// 获取 PM2 运行时目录下的日志路径
    fn pm2_home_dir(&self) -> Option<PathBuf> {
        Some(pm2_runtime_dir())
    }

    /// 获取 PM2 stdout 日志文件路径（`lib/pm2/runtime/pm2/logs/<name>-out.log`）
    fn out_log_path(&self) -> Option<PathBuf> {
        self.pm2_home_dir()
            .map(|d| d.join("logs").join(format!("{}-out.log", self.process_name)))
    }

    /// 获取 PM2 stderr 日志文件路径（`lib/pm2/runtime/pm2/logs/<name>-error.log`）
    fn error_log_path(&self) -> Option<PathBuf> {
        self.pm2_home_dir()
            .map(|d| d.join("logs").join(format!("{}-error.log", self.process_name)))
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

    /// 仅删除日志文件（不调用 `pm2 flush`，无阻塞）。
    ///
    /// 适用于启动/重启前清空旧日志——新进程启动后会自动创建新日志文件，
    /// 无需通过 PM2 CLI 清空。
    pub fn clear_logs_files_only(&self) {
        if let Some(path) = self.out_log_path() {
            let _ = std::fs::remove_file(&path);
        }
        if let Some(path) = self.error_log_path() {
            let _ = std::fs::remove_file(&path);
        }
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

/// 过滤 npm 输出的噪音日志
fn is_npm_noise(line: &str) -> bool {
    let s = line.trim().to_lowercase();
    // npm 审计消息
    if s.contains("found 0 vulnerabilities") || s.contains("npm audit") || s.contains("run `npm audit fix`") {
        return true;
    }
    // funding 提示
    if s.contains("for funding") || s.contains("type `npm fund`") {
        return true;
    }
    // 废弃警告（Deprecated）
    if s.contains("npm warn deprecated") {
        return true;
    }
    // 可选依赖跳过（optional dep skipped）
    if s.contains("npm warn optional") && s.contains("skipping") {
        return true;
    }
    // 空包/元数据信息
    if s.starts_with("npm notice") || s.starts_with("npm http") {
        return true;
    }
    // package-lock 相关（我们禁用了 lock 文件）
    if s.contains("package-lock.json") || s.contains("created a lockfile") {
        return true;
    }
    // 空行和纯空格
    if s.trim().is_empty() {
        return true;
    }
    false
}

/// 根据 NpmRegistry 枚举获取 registry URL
pub fn npm_registry_url(reg: &crate::pages::settings::NpmRegistry) -> Option<String> {
    match reg {
        crate::pages::settings::NpmRegistry::Official => None, // 使用 npm 默认源
        crate::pages::settings::NpmRegistry::Taobao => Some("https://registry.npmmirror.com/".to_string()),
        crate::pages::settings::NpmRegistry::Tencent => Some("https://mirrors.cloud.tencent.com/npm/".to_string()),
    }
}
