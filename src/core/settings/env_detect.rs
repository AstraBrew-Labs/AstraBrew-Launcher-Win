//! Windows 环境依赖探测与命令执行。
//!
//! 启动器支持两套运行环境，所有探测都必须按来源区分：
//! - [`EnvSource::Builtin`]：软件内置的 `%AppData%/AstraBrew Launcher/lib/` 目录；
//! - [`EnvSource::System`]：用户系统 PATH 中已安装的工具。
//!
//! 本模块只负责「解析可执行文件 + 读取版本号 + 报告安装进度」，
//! 具体的下载/解压安装流程在 `core/settings/install/` 下的各依赖模块中。

use crate::lang::t;
use crate::lang::tf;
use std::io::{BufRead, BufReader};
use std::process::{Child, Command, Stdio};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Duration;

use crate::core::env::{
    apply_builtin_path_to_command, apply_no_window_to_command, get_builtin_caddy_path,
    get_builtin_git_path, get_builtin_node_path, get_builtin_npm_path, get_lib_dir,
    get_system_cmd_path,
};
use crate::core::settings::EnvSource;

// ─── 命令构建 ────────────────────────────────────────────────────────────────

/// 按来源构建可执行命令。
///
/// - 内置环境：直接使用 `lib/` 中的绝对路径；`.cmd`/`.bat` 经 `cmd /c` 启动。
/// - 系统环境：先用 `where` 解析绝对路径，找不到时回退到裸命令名，
///   交由 Windows 的 `CreateProcess` 依据 `PATH` 与 `PATHEXT` 继续查找。
///
/// 所有分支都会附加「无黑窗」标志，并把内置环境目录前置注入 `PATH`，
/// 使 npm / node 这类会二次拉起子进程的工具也能正常工作。
pub fn command_for(name: &str, source: EnvSource) -> Command {
    let resolved = match source {
        EnvSource::Builtin => resolve_builtin_command(name),
        EnvSource::System => get_system_cmd_path(name),
    };

    let mut command = match resolved {
        Some(path) => command_from_path(&path),
        None => Command::new(name),
    };

    apply_no_window_to_command(&mut command);
    apply_builtin_path_to_command(&mut command);
    command
}

/// 内置环境中的命令绝对路径；未安装时返回 `None`。
pub fn resolve_builtin_command(name: &str) -> Option<std::path::PathBuf> {
    match name {
        "git" => get_builtin_git_path(),
        "node" => get_builtin_node_path(),
        "npm" => get_builtin_npm_path(),
        "caddy" => get_builtin_caddy_path(),
        _ => builtin_generic_command(name),
    }
}

/// 内置 `lib/<name>/<name>.exe` 的通用查找（覆盖 PM2 之外的第三方工具）。
fn builtin_generic_command(name: &str) -> Option<std::path::PathBuf> {
    let base = get_lib_dir().join(name);
    for candidate in [
        base.join(format!("{name}.exe")),
        base.join(format!("{name}.cmd")),
        base.join(format!("{name}.bat")),
    ] {
        if candidate.is_file() {
            return Some(candidate);
        }
    }
    None
}

/// 依据扩展名决定是否需要 `cmd /c` 包装。
///
/// Windows 的 `CreateProcess` 无法直接执行 `.cmd` / `.bat`，
/// 必须交给 `cmd.exe` 解释，否则会报「不是有效的 Win32 应用程序」。
/// 按可执行文件路径构造命令。
///
/// `.cmd` / `.bat` 是批处理脚本，必须交给 `cmd.exe` 解释，否则会报
/// 「不是有效的 Win32 应用程序」。两种分支都要隐藏控制台窗口 ——
/// 否则每次环境检测都会闪出黑框，而 `cmd /c` 的窗口还会一直停在界面前面。
fn command_from_path(path: &std::path::Path) -> Command {
    let is_script = path
        .extension()
        .and_then(|extension| extension.to_str())
        .is_some_and(|extension| {
            let extension = extension.to_ascii_lowercase();
            extension == "cmd" || extension == "bat"
        });

    let mut command = if is_script {
        let mut command = Command::new("cmd");
        command.arg("/c").arg(path);
        command
    } else {
        Command::new(path)
    };
    crate::core::env::apply_no_window_to_command(&mut command);
    command
}

// ─── 版本探测 ────────────────────────────────────────────────────────────────

/// 检测 Git 版本，返回版本号字符串，如 "2.47.1"。
pub fn detect_git(source: EnvSource) -> Option<String> {
    let output = run_version_command(command_for("git", source), ["--version"])?;
    parse_git_version(&output)
}

/// 检测 Node.js 版本，返回版本号字符串，如 "v22.14.0"。
///
/// 同时要求 `npm` 可用：仅装 node 而未装 npm 的环境无法运行酒馆依赖安装，
/// 应当视为未安装，避免用户在控制台阶段才遇到失败。
pub fn detect_nodejs(source: EnvSource) -> Option<String> {
    let node = run_version_command(command_for("node", source), ["--version"])?;
    let version = node.trim();
    if version.is_empty() {
        return None;
    }
    // npm 在 Windows 上是 npm.cmd，必须以 cmd /c 方式探测。
    run_version_command(command_for("npm", source), ["--version"])?;
    Some(version.to_owned())
}

/// 检测 Caddy 版本，返回版本号字符串，如 "v2.9.1"。
pub fn detect_caddy(source: EnvSource) -> Option<String> {
    let output = run_version_command(command_for("caddy", source), ["version"])?;
    // 输出格式: "v2.9.1 h1:..."，取第一段。
    let version = output.split_whitespace().next()?;
    (!version.is_empty()).then(|| version.to_owned())
}

/// 检测 PM2 版本，返回版本号字符串，如 "7.0.1"。
///
/// `pm2 -v` 首次运行会夹杂 daemon 启动日志，因此合并 stdout + stderr 后
/// 用 semver 提取器从混合输出中取版本号。
pub fn detect_pm2(source: EnvSource) -> Option<String> {
    let output = run_version_command(command_for("pm2", source), ["-v"])?;
    extract_semver(&output)
}

/// 按来源分发版本探测，供安装流程结束后复用同一套判定逻辑。
pub fn detect_version(target: &str, source: EnvSource) -> Option<String> {
    match target {
        "git" => detect_git(source),
        "nodejs" => detect_nodejs(source),
        "caddy" => detect_caddy(source),
        "pm2" => detect_pm2(source),
        "webview2" => Some(crate::core::settings::webview2::detect_webview2(source)).flatten(),
        _ => None,
    }
}

/// 执行版本命令并把 stdout + stderr 合并为一段文本。
///
/// 合并两路输出是必需的：`pm2`/`npm` 的部分版本信息会写到 stderr，
/// 只看 stdout 会在部分环境下漏掉版本号。
fn run_version_command(mut command: Command, args: [&str; 1]) -> Option<String> {
    let output = command.args(args).output().ok()?;
    let merged = format!(
        "{}\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    (!merged.trim().is_empty()).then_some(merged)
}

/// 从 `git --version` 输出中提取版本号。
///
/// Windows 的 Git for Windows 会输出
/// `git version 2.47.1.windows.1`，MinGit 则是 `git version 2.47.1`。
fn parse_git_version(output: &str) -> Option<String> {
    let prefix = "git version ";
    let position = output.find(prefix)?;
    let rest = &output[position + prefix.len()..];
    let raw = rest.split_whitespace().next()?;
    // 截断 `.windows.N` 之类的发行版后缀，只保留 semver 主体。
    let version = extract_semver(raw).unwrap_or_else(|| raw.to_owned());
    Some(version)
}

/// 从文本中提取第一个符合 X.Y.Z 模式的版本号。
fn extract_semver(text: &str) -> Option<String> {
    let bytes = text.as_bytes();
    let len = bytes.len();
    let mut i = 0;
    while i < len {
        // 找数字开头
        if bytes[i].is_ascii_digit() {
            let start = i;
            let mut dots = 0u8;
            let mut valid = true;
            i += 1;
            while i < len && dots < 2 {
                if bytes[i].is_ascii_digit() {
                    i += 1;
                } else if bytes[i] == b'.' && i + 1 < len && bytes[i + 1].is_ascii_digit() {
                    dots += 1;
                    i += 1; // 跳过 '.'
                } else {
                    valid = false;
                    break;
                }
            }
            if valid && dots == 2 {
                // 截断尾部非数字字符（如换行后的额外文本）
                let mut end = i;
                while end > start && !bytes[end - 1].is_ascii_digit() {
                    end -= 1;
                }
                return Some(String::from_utf8_lossy(&bytes[start..end]).to_string());
            }
        } else {
            i += 1;
        }
    }
    None
}

/// 解析 semver 主版本号。
fn parse_major(version: &str) -> Option<u32> {
    let version = version.trim_start_matches('v').trim_start_matches('V');
    version.split('.').next()?.parse::<u32>().ok()
}

/// Node.js 版本是否低于 v22。
pub fn is_nodejs_outdated(version: &str) -> bool {
    match parse_major(version) {
        Some(major) => major < 22,
        None => false,
    }
}

// ─── 安装执行 ────────────────────────────────────────────────────────────────

/// 以「行协议」把子进程日志转发给界面线程。
///
/// 行协议标记（必须与 `app.rs` 的解析保持一致）：
/// - `__DONE__`：安装成功完成
/// - `__FAILED__`：安装失败，前一条消息是 `__ERROR__:<已翻译文案>`
/// - `__CANCELLED__`：用户主动取消
/// - `__VERSION__:<版本号>`：安装后探测到的版本
/// - `__NOTICE__:<文案键>`：阶段提示（键，由界面线程翻译）
/// - `__PROGRESS__:<0-100>`：确定进度百分比
pub const MARKER_DONE: &str = "__DONE__";
pub const MARKER_FAILED: &str = "__FAILED__";
pub const MARKER_CANCELLED: &str = "__CANCELLED__";

/// 运行一个已构建好的安装命令，并把结果按行协议回报。
///
/// `detect_target` 为安装完成后需要复检的依赖标识（如 `"nodejs"`）；
/// 传空字符串表示只关心退出码，不做版本复检。
pub fn run_logged_command(
    mut child: Child,
    sender: std::sync::mpsc::Sender<String>,
    detect_target: &'static str,
    source: EnvSource,
    cancel: Arc<AtomicBool>,
) {
    let stdout = child.stdout.take();
    let stderr = child.stderr.take();

    let tx_stdout = sender.clone();
    let tx_stderr = sender.clone();
    let tx_final = sender;
    let (done_tx, done_rx) = std::sync::mpsc::channel();

    if let Some(out) = stdout {
        let done = done_tx.clone();
        std::thread::spawn(move || {
            let reader = BufReader::new(out);
            for line_result in reader.lines() {
                match line_result {
                    Ok(line) => {
                        let cleaned = strip_ansi(&line).trim().to_string();
                        if !cleaned.is_empty() {
                            let _ = tx_stdout.send(cleaned);
                        }
                    }
                    Err(_) => break,
                }
            }
            let _ = done.send(());
        });
    } else {
        let _ = done_tx.send(());
    }

    if let Some(err) = stderr {
        let done = done_tx;
        std::thread::spawn(move || {
            let reader = BufReader::new(err);
            for line_result in reader.lines() {
                match line_result {
                    Ok(line) => {
                        let cleaned = strip_ansi(&line).trim().to_string();
                        if !cleaned.is_empty() {
                            let _ = tx_stderr.send(cleaned);
                        }
                    }
                    Err(_) => break,
                }
            }
            let _ = done.send(());
        });
    } else {
        let _ = done_tx.send(());
    }

    // 主线程轮询子进程，使界面上的取消操作可以及时终止安装。
    let mut was_cancelled = false;
    let status = loop {
        if cancel.load(Ordering::Relaxed) {
            was_cancelled = true;
            kill_process_tree(&mut child);
            break child.wait();
        }
        match child.try_wait() {
            Ok(Some(status)) => break Ok(status),
            Ok(None) => std::thread::sleep(Duration::from_millis(50)),
            Err(error) => {
                kill_process_tree(&mut child);
                break Err(error);
            }
        }
    };

    // 进程结束后等待两路管道排空，保证折叠详情中的日志完整。
    let _ = done_rx.recv();
    let _ = done_rx.recv();

    if was_cancelled {
        let _ = tx_final.send(MARKER_CANCELLED.to_owned());
        return;
    }

    let status = match status {
        Ok(status) => status,
        Err(error) => {
            send_failure(
                tx_final,
                tf("env.wait_install_failed", &[("error", &error)]),
            );
            return;
        }
    };

    if !status.success() {
        let code = status
            .code()
            .map(|code| code.to_string())
            .unwrap_or_else(|| t("resources.unknown").to_owned());
        send_failure(tx_final, tf("env.command_failed", &[("code", &code)]));
        return;
    }

    if detect_target.is_empty() {
        let _ = tx_final.send(MARKER_DONE.to_owned());
        return;
    }

    if detect_target == "pm2" {
        let _ = tx_final.send("__NOTICE__:environment.install.pm2.verifying".to_owned());
    }
    if let Some(version) = detect_version(detect_target, source) {
        let _ = tx_final.send(format!("__VERSION__:{version}"));
        let _ = tx_final.send(MARKER_DONE.to_owned());
    } else {
        send_failure(
            tx_final,
            tf(
                "env.command_done_not_detected",
                &[("target", &detect_target)],
            ),
        );
    }
}

/// 结束子进程及其派生进程树。
///
/// `npm install` 会派生出 node 子进程，只杀父进程会留下孤儿进程继续占用文件，
/// 因此必须用 `taskkill /T` 连同整棵树一起结束。
fn kill_process_tree(child: &mut Child) {
    let pid = child.id().to_string();
    let mut killer = Command::new("taskkill");
    apply_no_window_to_command(&mut killer);
    let _ = killer
        .args(["/PID", &pid, "/T", "/F"])
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .status();
    let _ = child.kill();
}

// ─── 通用工具 ────────────────────────────────────────────────────────────────

/// 构造一个已配置好管道与「无黑窗」标志的安装命令。
///
/// `CREATE_NO_WINDOW` 必须在这里补上：安装子进程的 stdout/stderr 已被重定向到管道，
/// 不会被用户看到，控制台窗口纯属多余的干扰，而且 node/npm 安装耗时长，
/// 那个黑框会一直挡在启动器前面。
pub fn prepare_install_command(mut command: Command) -> Command {
    crate::core::env::apply_no_window_to_command(&mut command);
    command.stdin(Stdio::null());
    command.stdout(Stdio::piped());
    command.stderr(Stdio::piped());
    command
}

fn send_failure(sender: std::sync::mpsc::Sender<String>, message: String) {
    let _ = sender.send(format!("__ERROR__:{message}"));
    let _ = sender.send(MARKER_FAILED.to_owned());
}

/// 简易 ANSI 转义序列清理（SGR 颜色码 + 光标控制）。
///
/// npm / git 在部分终端下会输出颜色码，直接进入日志会让界面出现乱码方块。
fn strip_ansi(input: &str) -> String {
    let mut result = String::with_capacity(input.len());
    let mut chars = input.chars().peekable();
    while let Some(c) = chars.next() {
        if c == '\x1b' && chars.peek() == Some(&'[') {
            chars.next(); // skip '['
            // 跳过参数部分 (数字和分号)
            while let Some(&next) = chars.peek() {
                if next.is_ascii_digit() || next == ';' {
                    chars.next();
                } else {
                    break;
                }
            }
            // 跳过终止字符 (通常是 m，但也可能是其他)
            chars.next();
        } else {
            result.push(c);
        }
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_parse_git_version() {
        assert_eq!(
            parse_git_version("git version 2.47.1"),
            Some("2.47.1".to_string())
        );
        assert_eq!(
            parse_git_version("git version 2.47.1.windows.1"),
            Some("2.47.1".to_string())
        );
    }

    #[test]
    fn test_is_nodejs_outdated() {
        assert!(is_nodejs_outdated("v18.19.0"));
        assert!(!is_nodejs_outdated("v22.0.0"));
        assert!(!is_nodejs_outdated("v23.1.0"));
    }

    #[test]
    fn test_parse_major() {
        assert_eq!(parse_major("22.14.0"), Some(22));
        assert_eq!(parse_major("v22.1.0"), Some(22));
        assert_eq!(parse_major("v7.0.1"), Some(7));
    }

    #[test]
    fn test_strip_ansi() {
        assert_eq!(strip_ansi("hello"), "hello");
        assert_eq!(strip_ansi("\x1b[32mhello\x1b[0m"), "hello");
        assert_eq!(strip_ansi("\x1b[1;32mworld\x1b[0m"), "world");
        assert_eq!(strip_ansi("no ansi here"), "no ansi here");
    }

    #[test]
    fn test_extract_semver() {
        assert_eq!(extract_semver("7.0.1"), Some("7.0.1".to_string()));
        assert_eq!(
            extract_semver("[PM2] Spawning\n7.0.1\n"),
            Some("7.0.1".to_string())
        );
        assert_eq!(extract_semver("no version"), None);
    }

    /// 系统来源下命令必须带上「无黑窗」标志，避免 GUI 拉起子进程时闪黑框。
    #[test]
    fn system_command_hides_console_window() {
        let command = command_for("git", EnvSource::System);
        // `creation_flags` 无法直接读回，这里通过 Debug 输出确认已被设置。
        assert!(format!("{command:?}").contains("creation_flags"));
    }

    #[test]
    fn failed_command_emits_failure_marker_instead_of_done() {
        // Windows 的 cmd.exe 用 `exit /b 7` 指定退出码。
        let mut command = Command::new("cmd");
        command
            .args(["/c", "echo failed output& exit /b 7"])
            .stdout(Stdio::piped())
            .stderr(Stdio::piped());
        apply_no_window_to_command(&mut command);
        let child = command.spawn().expect("spawn test command");
        let (sender, receiver) = std::sync::mpsc::channel();

        run_logged_command(
            child,
            sender,
            "",
            EnvSource::System,
            Arc::new(AtomicBool::new(false)),
        );
        let messages = receiver.into_iter().collect::<Vec<_>>();

        assert!(messages.iter().any(|message| message.contains("failed output")));
        assert!(
            messages
                .iter()
                .any(|message| message.starts_with("__ERROR__:"))
        );
        assert!(messages.iter().any(|message| message == MARKER_FAILED));
        assert!(!messages.iter().any(|message| message == MARKER_DONE));
    }
}
