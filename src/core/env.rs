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
