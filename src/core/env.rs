#![allow(dead_code)]

use std::path::PathBuf;
use std::process::Command;
use std::os::windows::process::CommandExt;

// 隐藏控制台窗口的标志
const CREATE_NO_WINDOW: u32 = 0x08000000;

/// 获取应用根目录：`%AppData%/AstraBrew Launcher/`
pub fn get_data_dir() -> PathBuf {
    let appdata = std::env::var("APPDATA").unwrap_or_else(|_| ".".into());
    PathBuf::from(appdata).join("AstraBrew Launcher")
}

/// 内置 Git 路径：`<root>/lib/git/cmd/git.exe` 或 `<root>/lib/git/bin/git.exe`
pub fn get_builtin_git_path() -> Option<PathBuf> {
    let base = get_data_dir().join("lib").join("git");

    let cmd_path = base.join("cmd").join("git.exe");
    if cmd_path.exists() {
        return Some(cmd_path);
    }

    let bin_path = base.join("bin").join("git.exe");
    if bin_path.exists() {
        return Some(bin_path);
    }

    None
}

/// 内置 Node.js 路径：`<root>/lib/nodejs/node.exe`
pub fn get_builtin_node_path() -> Option<PathBuf> {
    let path = get_data_dir().join("lib").join("nodejs").join("node.exe");
    if path.exists() { Some(path) } else { None }
}

/// 内置 npm 路径：`<root>/lib/nodejs/npm.cmd`
pub fn get_builtin_npm_path() -> Option<PathBuf> {
    let path = get_data_dir().join("lib").join("nodejs").join("npm.cmd");
    if path.exists() { Some(path) } else { None }
}

pub fn get_pm2_path() -> Option<PathBuf> {
    // 内置安装：lib/pm2/pm2.cmd（包装脚本）
    let builtin_pm2 = get_data_dir().join("lib").join("pm2").join("pm2.cmd");
    if builtin_pm2.exists() {
        return Some(builtin_pm2);
    }

    // 系统 PATH 中的 pm2
    if let Some(p) = get_system_cmd_path("pm2") {
        return Some(p);
    }

    // 回退：检查 npm 全局安装目录 (%APPDATA%\npm)
    if let Ok(appdata) = std::env::var("APPDATA") {
        let npm_global = PathBuf::from(&appdata).join("npm").join("pm2.cmd");
        if npm_global.exists() {
            return Some(npm_global);
        }
        let npm_global_ps = PathBuf::from(&appdata).join("npm").join("pm2.ps1");
        if npm_global_ps.exists() {
            return Some(npm_global_ps);
        }
    }

    None
}

pub fn get_system_cmd_path(cmd: &str) -> Option<PathBuf> {
    if let Ok(output) = Command::new("where")
        .arg(cmd)
        .creation_flags(CREATE_NO_WINDOW)
        .output() {
        if output.status.success() {
            let paths = String::from_utf8_lossy(&output.stdout);
            
            // 优先选择 .exe, .cmd, .bat
            for line in paths.lines() {
                let p = PathBuf::from(line.trim());
                if p.exists() {
                    let ext = p.extension().and_then(|s| s.to_str()).unwrap_or("").to_lowercase();
                    if ext == "exe" || ext == "cmd" || ext == "bat" {
                        return Some(p);
                    }
                }
            }
            
            // 如果没有匹配到特定扩展名，则回退到第一个路径
            if let Some(first_path) = paths.lines().next() {
                let p = PathBuf::from(first_path.trim());
                if p.exists() {
                    return Some(p);
                }
            }
        }
    }
    None
}

/// 获取内置环境应添加到 PATH 的目录列表
/// 顺序：nodejs → git/cmd (or git/bin) → git/usr/bin
/// 调用方将这些路径前置到子进程 PATH 中，确保酒馆能使用内置的 node/npm/git 工具
pub fn get_builtin_path_entries() -> Vec<PathBuf> {
    let mut entries = Vec::new();
    let lib = get_data_dir().join("lib");

    // Node.js 目录（包含 node.exe, npm.cmd 等）
    let nodejs_dir = lib.join("nodejs");
    if nodejs_dir.exists() {
        entries.push(nodejs_dir);
    }

    // MinGit cmd 目录（包含 git.exe）
    let git_cmd = lib.join("git").join("cmd");
    if git_cmd.exists() {
        entries.push(git_cmd);
    } else {
        let git_bin = lib.join("git").join("bin");
        if git_bin.exists() {
            entries.push(git_bin);
        }
    }

    // MinGit usr/bin（包含 bash, ssh 等 Unix 工具，git 某些操作需要）
    let git_usr_bin = lib.join("git").join("usr").join("bin");
    if git_usr_bin.exists() {
        entries.push(git_usr_bin);
    }

    entries
}

/// 将内置环境的 Node.js / MinGit 目录前置注入到 Command 的 PATH 中
pub fn apply_builtin_path_to_command(cmd: &mut std::process::Command) {
    let entries = get_builtin_path_entries();
    if entries.is_empty() {
        return;
    }
    let current_path = std::env::var("PATH").unwrap_or_default();
    let mut new_path_parts: Vec<String> = entries
        .iter()
        .map(|p| p.to_string_lossy().to_string())
        .collect();
    new_path_parts.push(current_path);
    let new_path = new_path_parts.join(";");
    cmd.env("PATH", &new_path);
}

pub fn get_cmd_version(path: &PathBuf) -> Option<String> {
    let ext = path.extension().and_then(|s| s.to_str()).unwrap_or("").to_lowercase();
    
    // 如果是批处理文件，使用 cmd /c 运行
    let result = if ext == "cmd" || ext == "bat" {
        Command::new("cmd")
            .arg("/c")
            .arg(path)
            .arg("--version")
            .creation_flags(CREATE_NO_WINDOW)
            .output()
    } else {
        Command::new(path)
            .arg("--version")
            .creation_flags(CREATE_NO_WINDOW)
            .output()
    };

    if let Ok(output) = result {
        if output.status.success() {
            return Some(String::from_utf8_lossy(&output.stdout).trim().to_string());
        }
    }
    None
}
