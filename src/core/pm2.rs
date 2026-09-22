//! PM2 进程管理封装。
//!
//! 所有 PM2 CLI 调用都由控制台运行时线程执行，避免阻塞 iced 主线程。
//!
//! PM2 可按环境来源解析：内置环境使用 `lib/pm2/` 下的独立安装，
//! 系统环境使用用户全局 `npm install -g pm2` 的结果。无论哪种来源，
//! 运行时数据（daemon、日志、启动脚本）都统一落在启动器的数据目录下，
//! 避免污染用户的全局 PM2 状态。

use crate::lang::tf;
use std::fs;
use std::io::{BufRead, BufReader, Read, Seek, SeekFrom};
use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};

use serde_json::Value;

use crate::core::env::{CREATE_NO_WINDOW, get_builtin_node_path, get_lib_dir};
use crate::core::settings::EnvSource;

/// 启动器托管的固定 PM2 进程名。
pub const PROCESS_NAME: &str = "astrabrew-launcher-sillytavern";

/// PM2 运行时目录：`<root>/lib/pm2/runtime/pm2`。
///
/// 固定到应用数据目录，使内置与系统两套 PM2 共享同一份进程表与日志。
pub(crate) fn pm2_runtime_dir() -> PathBuf {
    get_lib_dir().join("pm2").join("runtime").join("pm2")
}

/// 把 `PM2_HOME` 注入命令环境，确保所有 PM2 调用读写同一份运行时数据。
fn apply_pm2_runtime_env(command: &mut Command) {
    command.env("PM2_HOME", pm2_runtime_dir());
    // 首次拉起 PM2 daemon 时默认会在 jlist JSON 前输出提示；静默模式减少混合输出。
    command
        .env("PM2_SILENT", "true")
        .env("NO_COLOR", "1")
        .env("FORCE_COLOR", "0");
}

/// 在 PM2 安装目录中定位 JS 入口。
///
/// 优先直接用 node 执行 JS 入口而非 `.cmd` 包装脚本，
/// 可以绕开批处理层，显著降低命令行窗口闪烁概率。
fn find_pm2_script(pm2_root: &Path, script_name: &str) -> Option<PathBuf> {
    let bin_dir = pm2_root.join("node_modules").join("pm2").join("bin");
    let candidates = [
        bin_dir.join(script_name),
        bin_dir.join(format!("{script_name}.js")),
        bin_dir.join(format!("{script_name}.cjs")),
    ];
    candidates.into_iter().find(|candidate| candidate.is_file())
}

/// 为指定的 PM2 安装解析配套的 Node.js 路径。
///
/// 内置 PM2 必须配套内置 Node.js（用户可能根本没装过系统 node）；
/// 系统 PM2 优先系统 Node.js，缺失时回退到内置 Node.js。
fn resolve_pm2_node_path(pm2_root: &Path) -> Option<PathBuf> {
    let builtin_pm2_root = get_lib_dir().join("pm2");
    if pm2_root == builtin_pm2_root {
        get_builtin_node_path()
    } else {
        crate::core::env::get_system_cmd_path("node").or_else(get_builtin_node_path)
    }
}

/// 解析指定来源下 PM2 的包装脚本路径。
pub fn pm2_wrapper_path(source: EnvSource) -> Option<PathBuf> {
    match source {
        EnvSource::Builtin => crate::core::env::get_builtin_pm2_path(),
        EnvSource::System => crate::core::env::get_pm2_path(),
    }
}

/// 构建指定来源下的 PM2 命令。
///
/// 优先构造「node + PM2 JS 入口」的组合；找不到 JS 入口时回退到包装脚本
/// （`.cmd` 经 `cmd /c` 启动）。两路都会附加无黑窗标志并注入 `PM2_HOME`。
pub fn pm2_command_for(source: EnvSource) -> Option<Command> {
    let wrapper = pm2_wrapper_path(source)?;
    let root = wrapper.parent()?;

    if let Some(script) = find_pm2_script(root, "pm2")
        && let Some(node) = resolve_pm2_node_path(root)
    {
        let mut command = Command::new(node);
        command.creation_flags(CREATE_NO_WINDOW);
        command.arg(script);
        // 内置环境必须把 lib/ 前置注入 PATH，否则 PM2 拉起的子进程会命中系统 node，
        // 表现为「选了内置却用了系统环境」（对齐旧版 pm2.rs 的处理）。
        if source == EnvSource::Builtin {
            crate::core::env::apply_builtin_path_to_command(&mut command);
        }
        apply_pm2_runtime_env(&mut command);
        return Some(command);
    }

    let mut command = crate::core::settings::env_detect::command_for("pm2", source);
    apply_pm2_runtime_env(&mut command);
    Some(command)
}

/// PM2 返回的精简进程状态。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProcessInfo {
    pub status: String,
    pub pid: Option<u32>,
    pub cwd: Option<String>,
}

/// PM2 CLI 管理器。
///
/// 持有环境来源，所有 CLI 调用都据此解析 PM2 与配套 Node.js 的位置。
#[derive(Debug, Default)]
pub struct Pm2Manager {
    /// 运行 PM2 时使用的环境来源。
    source: EnvSource,
}

impl Pm2Manager {
    /// 按指定环境来源创建管理器。
    pub fn new(source: EnvSource) -> Self {
        Self { source }
    }

    /// 构建当前来源下的 PM2 命令。
    ///
    /// 理论上 `pm2_command_for` 只在两套环境都找不到 PM2 时返回 `None`；
    /// 此时回退到裸命令名，让 Windows 依据 `PATH` 再试一次，
    /// 避免把「未安装」误报成「无法执行」。
    fn command(&self) -> Command {
        pm2_command_for(self.source).unwrap_or_else(|| {
            let mut command = Command::new("pm2");
            command.creation_flags(CREATE_NO_WINDOW);
            apply_pm2_runtime_env(&mut command);
            command
        })
    }

    /// 检查当前来源下 PM2 是否可执行。
    pub fn is_installed(&self) -> bool {
        self.command()
            .arg("--version")
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status()
            .is_ok_and(|status| status.success())
    }

    /// 使用 PM2 启动 SillyTavern。
    pub fn start(
        &self,
        working_dir: &Path,
        node_args: Option<&str>,
        app_args: &[String],
        environment: &[(String, String)],
    ) -> Result<(), String> {
        if self.info()?.is_some() {
            self.delete()?;
        }
        let mut command = self.command();
        command
            .arg("start")
            .arg("server.js")
            .arg("--name")
            .arg(PROCESS_NAME)
            .current_dir(working_dir);
        if let Some(node_args) = node_args {
            command.arg("--node-args").arg(node_args);
        }
        if !app_args.is_empty() {
            command.arg("--").args(app_args);
        }
        for (key, value) in environment {
            command.env(key, value);
        }
        run_checked(command, "pm2.start_failed")
    }

    pub fn stop(&self) -> Result<(), String> {
        let mut command = self.command();
        command.arg("stop").arg(PROCESS_NAME);
        run_checked(command, "pm2.stop_failed")
    }

    pub fn restart(&self) -> Result<(), String> {
        let mut command = self.command();
        command.arg("restart").arg(PROCESS_NAME).arg("--update-env");
        run_checked(command, "pm2.restart_failed")
    }

    pub fn delete(&self) -> Result<(), String> {
        let mut command = self.command();
        command.arg("delete").arg(PROCESS_NAME);
        run_checked(command, "pm2.delete_failed")
    }

    /// 读取 PM2 中当前托管进程的状态。
    pub fn info(&self) -> Result<Option<ProcessInfo>, String> {
        let output = self
            .command()
            .arg("jlist")
            .output()
            .map_err(|error| tf("pm2.status_query_failed", &[("error", &error)]))?;
        if !output.status.success() {
            return Err(String::from_utf8_lossy(&output.stderr).trim().to_owned());
        }
        let values = parse_jlist_output(&output.stdout).map_err(|error| {
            let preview = String::from_utf8_lossy(&output.stdout)
                .chars()
                .take(240)
                .collect::<String>();
            if preview.trim().is_empty() {
                tf("pm2.status_format_invalid", &[("error", &error)])
            } else {
                tf("pm2.status_format_invalid_output", &[("error", &error.to_string()), ("output", &preview.trim().to_string())])
            }
        })?;
        let Some(value) = values
            .iter()
            .find(|value| value.get("name").and_then(Value::as_str) == Some(PROCESS_NAME))
        else {
            return Ok(None);
        };
        let environment = value.get("pm2_env").and_then(Value::as_object);
        Ok(Some(ProcessInfo {
            status: environment
                .and_then(|env| env.get("status"))
                .and_then(Value::as_str)
                .unwrap_or("unknown")
                .to_owned(),
            pid: value
                .get("pid")
                .and_then(Value::as_u64)
                .and_then(|pid| u32::try_from(pid).ok())
                .filter(|pid| *pid > 0),
            cwd: environment
                .and_then(|env| env.get("pm_cwd"))
                .and_then(Value::as_str)
                .map(str::to_owned),
        }))
    }

    /// 返回日志尾部安全读取起点，并对齐到下一行边界。
    pub fn tail_offset(&self, error_log: bool, max_bytes: u64) -> u64 {
        let path = log_path(error_log);
        let Ok(file) = fs::File::open(path) else {
            return 0;
        };
        let length = file.metadata().map(|metadata| metadata.len()).unwrap_or(0);
        let start = length.saturating_sub(max_bytes);
        if start == 0 {
            return 0;
        }
        let mut reader = BufReader::new(file);
        // 起点正好位于换行之后时已经对齐，不应再丢弃一条完整日志。
        if reader.seek(SeekFrom::Start(start - 1)).is_err() {
            return 0;
        }
        let mut previous = [0_u8; 1];
        if reader.read_exact(&mut previous).is_ok() && previous[0] == b'\n' {
            return start;
        }
        if reader.seek(SeekFrom::Start(start)).is_err() {
            return 0;
        }
        let mut partial_line = Vec::new();
        if reader.read_until(b'\n', &mut partial_line).is_err() {
            return start;
        }
        reader.stream_position().unwrap_or(start)
    }

    /// 从 PM2 日志文件的指定字节位置读取增量内容。
    pub fn read_log(&self, error_log: bool, offset: &mut u64) -> Result<Vec<String>, String> {
        let path = log_path(error_log);
        let mut file = match fs::File::open(&path) {
            Ok(file) => file,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(Vec::new()),
            Err(error) => return Err(tf("pm2.log_read_failed", &[("path", &path.display().to_string()), ("error", &error.to_string())])),
        };
        let length = file.metadata().map(|metadata| metadata.len()).unwrap_or(0);
        if *offset > length {
            *offset = 0;
        }
        file.seek(SeekFrom::Start(*offset))
            .map_err(|error| tf("pm2.log_locate_failed", &[("error", &error)]))?;
        let mut bytes = Vec::new();
        file.read_to_end(&mut bytes)
            .map_err(|error| tf("pm2.log_open_failed", &[("error", &error)]))?;
        *offset = file.stream_position().unwrap_or(length);
        Ok(String::from_utf8_lossy(&bytes)
            .lines()
            .map(str::to_owned)
            .collect())
    }

    /// 清空当前托管进程的日志，确保新会话不会混入旧输出。
    pub fn clear_logs(&self) {
        // 先通知 PM2 关闭并刷新当前日志流，再直接截断文件处理残留内容。
        let _ = self
            .command()
            .arg("flush")
            .arg(PROCESS_NAME)
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status();
        for path in [log_path(false), log_path(true)] {
            let _ = fs::File::create(path);
        }
    }
}

/// 从 PM2 的混合 stdout 中提取首个合法 JSON 数组。
///
/// PM2 首次启动守护进程时可能输出 `[PM2] Spawning...`、ANSI 颜色提示，随后才
/// 输出真正的 `[]`/`[{...}]`。逐个尝试 `[` 起点可跳过这些非 JSON 前缀，同时
/// `StreamDeserializer` 允许 JSON 后仍有额外提示。
fn parse_jlist_output(output: &[u8]) -> Result<Vec<Value>, serde_json::Error> {
    if let Ok(values) = serde_json::from_slice::<Vec<Value>>(output) {
        return Ok(values);
    }

    let text = String::from_utf8_lossy(output);
    let mut last_error = serde_json::from_str::<Vec<Value>>(text.trim()).unwrap_err();
    for (start, character) in text.char_indices() {
        if character != '[' {
            continue;
        }
        let mut stream =
            serde_json::Deserializer::from_str(&text[start..]).into_iter::<Vec<Value>>();
        match stream.next() {
            Some(Ok(values)) => return Ok(values),
            Some(Err(error)) => last_error = error,
            None => {}
        }
    }
    Err(last_error)
}

fn run_checked(mut command: Command, context: &str) -> Result<(), String> {
    let output = command
        .output()
        .map_err(|error| format!("{context}：{error}"))?;
    if output.status.success() {
        Ok(())
    } else {
        let detail = String::from_utf8_lossy(&output.stderr).trim().to_owned();
        Err(if detail.is_empty() {
            context.to_owned()
        } else {
            format!("{context}：{detail}")
        })
    }
}

/// PM2 日志文件路径。
///
/// 路径由 `PM2_HOME` 决定：daemon 会把日志写在 `<PM2_HOME>/logs/` 下。
/// 这里必须与 `apply_pm2_runtime_env` 注入的目录保持一致，否则会读不到日志。
fn log_path(error_log: bool) -> PathBuf {
    let suffix = if error_log { "error" } else { "out" };
    pm2_runtime_dir()
        .join("logs")
        .join(format!("{PROCESS_NAME}-{suffix}.log"))
}

#[cfg(test)]
mod tests {
    use super::parse_jlist_output;

    #[test]
    fn parses_clean_pm2_jlist() {
        assert!(parse_jlist_output(b"[]").unwrap().is_empty());
        let values =
            parse_jlist_output(br#"[{"name":"astrabrew-launcher-sillytavern","pid":42}]"#).unwrap();
        assert_eq!(values.len(), 1);
    }

    #[test]
    fn skips_pm2_daemon_banner_before_json() {
        let output = b"[PM2] Spawning PM2 daemon\n[PM2] PM2 Successfully daemonized\n[]\n";
        assert!(parse_jlist_output(output).unwrap().is_empty());
    }

    #[test]
    fn accepts_trailing_pm2_messages_after_json() {
        let output = b"[]\n[PM2] Done\n";
        assert!(parse_jlist_output(output).unwrap().is_empty());
    }
}
