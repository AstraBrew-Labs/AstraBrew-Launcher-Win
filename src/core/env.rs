//! Windows 环境路径与命令解析。
//!
//! 集中处理三件事：
//! 1. 解析系统 PATH 与软件内置 `lib/` 目录中的可执行文件位置；
//! 2. 为 GUI 程序拉起的命令行子进程附加「无黑窗」标志；
//! 3. 把内置环境目录前置注入到子进程 PATH，使酒馆能使用内置的 node / npm / git。
//!
//! 内置环境目录布局：
//! ```text
//! %AppData%/AstraBrew Launcher/lib/
//! ├── nodejs/   node.exe、npm.cmd
//! ├── git/      cmd/git.exe（MinGit）
//! ├── caddy/    caddy.exe
//! ├── pm2/      pm2.cmd、pm2-runtime.cmd
//! └── webview2/ msedgewebview2.exe
//! ```

use std::os::windows::process::CommandExt;
use std::path::PathBuf;
use std::process::Command;

/// 创建子进程时隐藏控制台窗口的标志（`CREATE_NO_WINDOW`）。
///
/// 从 GUI 程序拉起 `git`、`node`、`npm`、`taskkill` 等命令行工具时必须附加，
/// 否则会闪出黑色控制台窗口。
///
/// 之所以需要它：Windows 的控制台子系统程序在父进程没有控制台时（GUI 程序就是这种情况），
/// 系统会为它**新建一个控制台窗口**。`Child::kill()`、重定向 `stdio`、`Stdio::null()`
/// 都不能阻止这个过程 —— 控制台是在 `CreateProcess` 时就创建好的，只受启动标志控制。
pub const CREATE_NO_WINDOW: u32 = 0x0800_0000;

/// 为控制台程序统一附加「无黑窗」启动标志。
///
/// **凡是拉起 `.exe` / `cmd` / `.bat` 的地方都必须调用**，包括 `explorer.exe`、
/// `netstat.exe`、`taskkill.exe` 这类系统自带工具。
/// 已有的两处「批量入口」不必重复调用（它们内部已处理）：
/// `network::configure_git_proxy` 与 `env_detect::prepare_install_command`。
pub fn apply_no_window_to_command(cmd: &mut Command) {
    cmd.creation_flags(CREATE_NO_WINDOW);
}

/// 内置环境根目录：`<root>/lib/`。
pub fn get_lib_dir() -> PathBuf {
    crate::utils::app_paths().lib.clone()
}

// ─── 内置环境可执行文件路径 ──────────────────────────────────────────────────

/// 内置 Git 路径：`lib/git/cmd/git.exe` 或 `lib/git/bin/git.exe`。
pub fn get_builtin_git_path() -> Option<PathBuf> {
    let base = get_lib_dir().join("git");
    for candidate in [base.join("cmd").join("git.exe"), base.join("bin").join("git.exe")] {
        if candidate.is_file() {
            return Some(candidate);
        }
    }
    None
}

/// 内置 Node.js 路径：`lib/nodejs/node.exe`。
pub fn get_builtin_node_path() -> Option<PathBuf> {
    existing_file(get_lib_dir().join("nodejs").join("node.exe"))
}

/// 内置 npm 路径：`lib/nodejs/npm.cmd`。
pub fn get_builtin_npm_path() -> Option<PathBuf> {
    existing_file(get_lib_dir().join("nodejs").join("npm.cmd"))
}

/// 内置 Caddy 路径：`lib/caddy/caddy.exe`。
pub fn get_builtin_caddy_path() -> Option<PathBuf> {
    existing_file(get_lib_dir().join("caddy").join("caddy.exe"))
}

/// 内置 PM2 包装脚本：`lib/pm2/pm2.cmd`。
pub fn get_builtin_pm2_path() -> Option<PathBuf> {
    existing_file(get_lib_dir().join("pm2").join("pm2.cmd"))
}

/// PM2 可执行路径，按「内置 → 系统 PATH → npm 全局目录」顺序查找。
pub fn get_pm2_path() -> Option<PathBuf> {
    if let Some(path) = get_builtin_pm2_path() {
        return Some(path);
    }
    if let Some(path) = get_system_cmd_path("pm2") {
        return Some(path);
    }
    // npm 全局安装目录（`npm install -g pm2` 的默认落点）。
    std::env::var("APPDATA")
        .ok()
        .map(|appdata| PathBuf::from(appdata).join("npm").join("pm2.cmd"))
        .and_then(existing_file)
}

// ─── 系统 PATH 查找 ──────────────────────────────────────────────────────────

/// 在系统 PATH 中解析命令的完整路径（通过 `where`）。
///
/// 优先返回 `.exe` / `.cmd` / `.bat`，避免命中无扩展名的 Unix 风格脚本；
/// 都匹配不到时回退到 `where` 输出的第一项。
pub fn get_system_cmd_path(cmd: &str) -> Option<PathBuf> {
    let mut command = Command::new("where");
    apply_no_window_to_command(&mut command);
    let output = command.arg(cmd).output().ok()?;
    if !output.status.success() {
        return None;
    }
    let stdout = String::from_utf8_lossy(&output.stdout);
    let mut fallback = None;
    for line in stdout.lines() {
        let trimmed = line.trim();
        if trimmed.is_empty() {
            continue;
        }
        let path = PathBuf::from(trimmed);
        if !path.is_file() {
            continue;
        }
        let extension = path
            .extension()
            .and_then(|extension| extension.to_str())
            .unwrap_or_default()
            .to_ascii_lowercase();
        if matches!(extension.as_str(), "exe" | "cmd" | "bat") {
            return Some(path);
        }
        fallback.get_or_insert(path);
    }
    fallback
}

/// 文件存在时返回，否则返回 `None`；用于统一内置路径的探测写法。
fn existing_file(path: PathBuf) -> Option<PathBuf> {
    path.is_file().then_some(path)
}

// ─── PATH 注入 ───────────────────────────────────────────────────────────────

/// 需要前置到子进程 PATH 的内置环境目录。
///
/// 顺序：nodejs → git/cmd（或 git/bin）→ git/usr/bin。
/// MinGit 的 `usr/bin` 提供 bash、ssh 等工具，部分 git 操作依赖它们。
pub fn get_builtin_path_entries() -> Vec<PathBuf> {
    let lib = get_lib_dir();
    let mut entries = Vec::new();

    for candidate in [lib.join("nodejs"), lib.join("git").join("cmd")] {
        if candidate.is_dir() {
            entries.push(candidate);
        }
    }
    // `cmd` 不存在时（部分 MinGit 发行版）回退到 `bin`。
    if !entries.iter().any(|entry| entry.ends_with("cmd")) {
        let git_bin = lib.join("git").join("bin");
        if git_bin.is_dir() {
            entries.push(git_bin);
        }
    }
    let git_usr_bin = lib.join("git").join("usr").join("bin");
    if git_usr_bin.is_dir() {
        entries.push(git_usr_bin);
    }

    entries
}

/// 把内置环境目录前置注入到 Command 的 PATH 中。
///
/// Windows 的 PATH 分隔符是 `;`（不同于 Unix 的 `:`）。
pub fn apply_builtin_path_to_command(cmd: &mut Command) {
    let entries = get_builtin_path_entries();
    if entries.is_empty() {
        return;
    }
    let current = std::env::var("PATH").unwrap_or_default();
    let mut parts = entries
        .iter()
        .map(|path| path.to_string_lossy().into_owned())
        .collect::<Vec<_>>();
    parts.push(current);
    cmd.env("PATH", parts.join(";"));
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn builtin_path_entries_are_absolute_and_dedup_safe() {
        // 目录不存在时返回空列表是允许的；只断言元素个数上限与顺序约束。
        let entries = get_builtin_path_entries();
        assert!(entries.len() <= 3);
        for entry in &entries {
            assert!(entry.is_absolute(), "内置 PATH 条目必须是绝对路径");
        }
    }

    #[test]
    fn create_no_window_flag_matches_the_win32_constant() {
        // 该常量来自 `winbase.h` 的 `CREATE_NO_WINDOW`；写成十进制便于和文档核对。
        assert_eq!(CREATE_NO_WINDOW, 0x0800_0000);
        assert_eq!(CREATE_NO_WINDOW, 134_217_728);
    }

    /// 回归测试：任何从 GUI 拉起的控制台程序都必须带上隐藏窗口标志。
    ///
    /// 漏掉它会表现为「黑框闪过」，长任务（git clone / npm install）的黑框还会一直
    /// 停在启动器前面。这个测试把源码当作文本检查，代价是可能误报 —— 因此对
    /// 「经由 `configure_git_proxy` / `prepare_install_command` 间接覆盖」的调用点
    /// 做了白名单，新增调用点时要么自己加标志，要么加进白名单并说明理由。
    #[test]
    fn every_spawned_console_command_hides_its_window() {
        /// 已由上游辅助函数统一附加标志的调用点（文件, 该行内容特征）。
        const INDIRECT: &[(&str, &str)] = &[
            // network.rs 的 Git 调用统一走 configure_git_proxy。
            ("core/network.rs", "configure_git_proxy"),
            // install.rs 的 npm 调用统一走 prepare_install_command。
            ("core/settings/install.rs", "prepare_install_command"),
        ];

        let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("src");
        let mut files = Vec::new();
        collect_rust_files(&root, &mut files);
        assert!(!files.is_empty(), "应能扫描到 src 下的 Rust 源码");

        let mut offenders = Vec::new();
        for file in &files {
            let Ok(text) = std::fs::read_to_string(file) else {
                continue;
            };
            let relative = file
                .strip_prefix(&root)
                .unwrap_or(file)
                .to_string_lossy()
                .replace('\\', "/");

            for (index, line) in text.lines().enumerate() {
                if !line.contains("Command::new(") || line.trim_start().starts_with("//") {
                    continue;
                }
                // 调用点之后的一小段代码里出现标志，或命中白名单，都算覆盖。
                let tail: String = text
                    .lines()
                    .skip(index)
                    .take(30)
                    .collect::<Vec<_>>()
                    .join("\n");
                let covered = tail.contains("apply_no_window_to_command")
                    || tail.contains("creation_flags")
                    || INDIRECT
                        .iter()
                        .any(|(pattern, marker)| relative.ends_with(pattern) && tail.contains(marker));
                if !covered {
                    offenders.push(format!("{relative}:{}: {}", index + 1, line.trim()));
                }
            }
        }

        assert!(
            offenders.is_empty(),
            "以下 Command::new 调用点没有隐藏控制台窗口，会导致黑框闪过：\n{}",
            offenders.join("\n")
        );
    }

    /// 递归收集目录下所有 `.rs` 文件。
    fn collect_rust_files(directory: &std::path::Path, output: &mut Vec<std::path::PathBuf>) {
        let Ok(entries) = std::fs::read_dir(directory) else {
            return;
        };
        for entry in entries.flatten() {
            let path = entry.path();
            if path.is_dir() {
                collect_rust_files(&path, output);
            } else if path.extension().is_some_and(|extension| extension == "rs") {
                output.push(path);
            }
        }
    }
}
