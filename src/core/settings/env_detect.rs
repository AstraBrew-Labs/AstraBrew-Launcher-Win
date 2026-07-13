use std::io::{BufRead, BufReader};
use std::process::{Command, Stdio};
use std::os::windows::process::CommandExt;

const CREATE_NO_WINDOW: u32 = 0x08000000;

// ─── 路径解析 ─────────────────────────────────────────────────────────────────

/// 解析命令的完整路径（内置优先，回退系统 PATH）
pub fn resolve_command(name: &str) -> String {
    // 先检查内置路径
    let builtin_dir = crate::core::env::get_data_dir().join("lib");
    for sub_dir in &["git/cmd", "git/bin", "nodejs"] {
        let exe_name = if *sub_dir == "nodejs" {
            "node.exe"
        } else {
            &format!("{}.exe", name)
        };
        let full = builtin_dir.join(sub_dir).join(exe_name);
        if full.exists() {
            return full.to_string_lossy().to_string();
        }
    }
    // 回退到系统 PATH
    if let Some(p) = crate::core::env::get_system_cmd_path(name) {
        return p.to_string_lossy().to_string();
    }
    name.to_string()
}

/// 仅在系统 PATH 中查找命令（`where` 命令）
fn resolve_command_system(name: &str) -> String {
    if let Some(p) = crate::core::env::get_system_cmd_path(name) {
        return p.to_string_lossy().to_string();
    }
    name.to_string()
}

/// 仅在软件内置 lib 目录中查找命令
fn resolve_command_builtin(name: &str, sub_dirs: &[&str]) -> Option<String> {
    let builtin_dir = crate::core::env::get_data_dir().join("lib");
    for sub_dir in sub_dirs {
        let exe_name = if *sub_dir == "nodejs" {
            "node.exe"
        } else {
            &format!("{}.exe", name)
        };
        let full = builtin_dir.join(sub_dir).join(exe_name);
        if full.exists() {
            return Some(full.to_string_lossy().to_string());
        }
    }
    None
}

/// 创建 Command（系统 PATH 查找）
fn cmd_system(name: &str) -> Command {
    let path = resolve_command_system(name);
    let is_script = path.ends_with(".cmd") || path.ends_with(".bat");
    
    let cmd = if is_script {
        let mut c = Command::new("cmd");
        c.creation_flags(CREATE_NO_WINDOW);
        c.arg("/c").arg(path);
        c
    } else {
        let mut c = Command::new(path);
        c.creation_flags(CREATE_NO_WINDOW);
        c
    };
    cmd
}

/// 创建 Command（仅内置路径）
fn cmd_builtin(_name: &str, path: &str) -> Command {
    let is_script = path.ends_with(".cmd") || path.ends_with(".bat");
    
    let cmd = if is_script {
        let mut c = Command::new("cmd");
        c.creation_flags(CREATE_NO_WINDOW);
        c.arg("/c").arg(path);
        c
    } else {
        let mut c = Command::new(path);
        c.creation_flags(CREATE_NO_WINDOW);
        c
    };
    cmd
}

// ─── 系统环境检测（仅查系统 PATH）─────────────────────────────────────────────

/// 检测 Git 版本（系统 PATH）
pub fn detect_git_system() -> Option<String> {
    let output = cmd_system("git").arg("--version").output().ok()?;
    if !output.status.success() { return None; }
    let stdout = String::from_utf8_lossy(&output.stdout);
    parse_git_version(&stdout)
}

/// 检测 Node.js 版本（系统 PATH）
pub fn detect_nodejs_system() -> Option<String> {
    let output = cmd_system("node").arg("--version").output().ok()?;
    if !output.status.success() { return None; }
    let stdout = String::from_utf8_lossy(&output.stdout);
    let version = stdout.trim().to_string();
    if version.is_empty() { None } else { Some(version) }
}

/// 检测 Caddy 版本（系统 PATH）
pub fn detect_caddy_system() -> Option<String> {
    let output = cmd_system("caddy").arg("version").output().ok()?;
    if !output.status.success() { return None; }
    let stdout = String::from_utf8_lossy(&output.stdout);
    let version = stdout.trim().split_whitespace().next()?;
    if version.is_empty() { None } else { Some(version.to_string()) }
}

/// 检测 PM2 版本（系统 PATH）
pub fn detect_pm2_system() -> Option<String> {
    let output = cmd_system("pm2").arg("-v").output().ok()?;
    let combined = format!(
        "{}\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    extract_semver(&combined)
}

// ─── 内置环境检测（仅查 lib/ 目录）────────────────────────────────────────────

/// 检测 Git 版本（内置 lib/git/）
pub fn detect_git_builtin() -> Option<String> {
    let path = resolve_command_builtin("git", &["git/cmd", "git/bin"])?;
    let output = cmd_builtin("git", &path).arg("--version").output().ok()?;
    if !output.status.success() { return None; }
    let stdout = String::from_utf8_lossy(&output.stdout);
    parse_git_version(&stdout)
}

/// 检测 Node.js 版本（内置 lib/nodejs/）
pub fn detect_nodejs_builtin() -> Option<String> {
    let path = resolve_command_builtin("node", &["nodejs"])?;
    let output = cmd_builtin("node", &path).arg("--version").output().ok()?;
    if !output.status.success() { return None; }
    let stdout = String::from_utf8_lossy(&output.stdout);
    let version = stdout.trim().to_string();
    if version.is_empty() { None } else { Some(version) }
}

/// 检测 Caddy 版本（内置 lib/caddy/）
pub fn detect_caddy_builtin() -> Option<String> {
    let path = resolve_command_builtin("caddy", &["caddy"])?;
    let output = cmd_builtin("caddy", &path).arg("version").output().ok()?;
    if !output.status.success() { return None; }
    let stdout = String::from_utf8_lossy(&output.stdout);
    let version = stdout.trim().split_whitespace().next()?;
    if version.is_empty() { None } else { Some(version.to_string()) }
}

/// 检测 PM2 版本（通过内置 npx）
pub fn detect_pm2_builtin() -> Option<String> {
    let pm2_cmd = crate::core::env::get_data_dir()
        .join("lib")
        .join("pm2")
        .join("pm2.cmd");
    if !pm2_cmd.exists() {
        return None;
    }
    let output = cmd_builtin("pm2", &pm2_cmd.to_string_lossy())
        .arg("-v")
        .output()
        .ok()?;
    let combined = format!(
        "{}\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    extract_semver(&combined)
}

// ─── 版本解析 ─────────────────────────────────────────────────────────────────

fn parse_git_version(output: &str) -> Option<String> {
    let prefix = "git version ";
    if let Some(pos) = output.find(prefix) {
        let rest = &output[pos + prefix.len()..];
        let version = rest.split_whitespace().next()?;
        Some(version.to_string())
    } else {
        None
    }
}

/// 从文本中提取第一个符合 X.Y.Z 模式的版本号
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

/// 运行 npm install -g <package>（用于 PM2 等全局 npm 包）
#[allow(dead_code)]
pub fn run_npm_install_global(package: &str, sender: std::sync::mpsc::Sender<String>) {
    let mut cmd = cmd_system("npm");
    cmd.args(["install", "-g", package]);
    cmd.stdout(Stdio::piped());
    cmd.stderr(Stdio::piped());

    let mut child = match cmd.spawn() {
        Ok(c) => c,
        Err(e) => {
            let _ = sender.send(format!("无法启动进程: {}", e));
            let _ = sender.send("__DONE__".to_string());
            return;
        }
    };

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

    let _ = done_rx.recv();
    let _ = done_rx.recv();

    if let Some(ver) = detect_pm2_system() {
        let _ = tx_final.send(format!("__VERSION__:{}", ver));
    }

    let _ = tx_final.send("__DONE__".to_string());
    drop(child);
}

/// 解析 semver 主版本号
fn parse_major(version: &str) -> Option<u32> {
    let version = version.trim_start_matches('v').trim_start_matches('V');
    version.split('.').next()?.parse::<u32>().ok()
}

/// Node.js 版本是否低于 v22
pub fn is_nodejs_outdated(version: &str) -> bool {
    match parse_major(version) {
        Some(major) => major < 22,
        None => false,
    }
}

/// 简易 ANSI 转义序列清理（SGR 颜色码 + 光标控制）
fn strip_ansi(input: &str) -> String {
    let mut result = String::with_capacity(input.len());
    let mut chars = input.chars().peekable();
    while let Some(c) = chars.next() {
        if c == '\x1b' && chars.peek() == Some(&'[') {
            chars.next();
            while let Some(&next) = chars.peek() {
                if next.is_ascii_digit() || next == ';' {
                    chars.next();
                } else {
                    break;
                }
            }
            chars.next();
        } else {
            result.push(c);
        }
    }
    result
}

// ─── WebView2 检测 ───────────────────────────────────────────────────────────

/// 检测系统 WebView2 版本（通过注册表）
pub fn detect_webview2_system() -> Option<String> {
    crate::core::settings::webview2::get_webview2_version_system()
}

/// 检测内置 WebView2 状态（检查 lib/webview2/ 目录）
pub fn detect_webview2_builtin() -> Option<String> {
    crate::core::settings::webview2::get_webview2_version_builtin()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_parse_git_version() {
        assert_eq!(parse_git_version("git version 2.39.0"), Some("2.39.0".to_string()));
        assert_eq!(parse_git_version("git version 2.39.0 (Apple Git-xxx)"), Some("2.39.0".to_string()));
    }

    #[test]
    fn test_is_nodejs_outdated() {
        assert!(is_nodejs_outdated("v18.19.0"));
        assert!(!is_nodejs_outdated("v22.0.0"));
        assert!(!is_nodejs_outdated("v23.1.0"));
    }

    #[test]
    fn test_parse_major() {
        assert_eq!(parse_major("4.2.0"), Some(4));
        assert_eq!(parse_major("v22.1.0"), Some(22));
        assert_eq!(parse_major("v5.0.0"), Some(5));
    }

    #[test]
    fn test_strip_ansi() {
        assert_eq!(strip_ansi("hello"), "hello");
        assert_eq!(strip_ansi("\x1b[32mhello\x1b[0m"), "hello");
        assert_eq!(strip_ansi("\x1b[1;32mworld\x1b[0m"), "world");
        assert_eq!(strip_ansi("no ansi here"), "no ansi here");
    }
}
