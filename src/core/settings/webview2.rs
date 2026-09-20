//! WebView2 运行时探测。
//!
//! 桌面模式依赖 WebView2 渲染酒馆界面。启动器支持两种来源：
//! - 系统来源：读取注册表中 EdgeUpdate 登记的 WebView2 运行时版本；
//! - 内置来源：`%AppData%/AstraBrew Launcher/lib/webview2/` 下的独立运行时。
//!
//! 本文件当前只实现探测；下载与安装流程在后续阶段补齐。

use std::fs;
use std::path::PathBuf;

use winreg::RegKey;
use winreg::enums::HKEY_LOCAL_MACHINE;

use crate::core::settings::EnvSource;

/// 内置 WebView2 目录中记录的版本文件名。
const VERSION_FILE: &str = "version.txt";
/// 固定版运行时的主可执行文件名，用于确认目录内容完整。
const RUNTIME_EXECUTABLE: &str = "msedgewebview2.exe";

/// 按来源探测 WebView2 版本。
pub fn detect_webview2(source: EnvSource) -> Option<String> {
    match source {
        EnvSource::System => get_webview2_version_system(),
        EnvSource::Builtin => get_webview2_version_builtin(),
    }
}

/// 读取系统安装的 WebView2 运行时版本。
///
/// 版本信息登记在 `EdgeUpdate\Clients` 下的各客户端子键中，
/// 需要按 `name` 字段筛出 WebView2 对应的那一项再读 `pv`（产品版本）。
/// 同时检查 32 位视图（`WOW6432Node`）与原生视图，覆盖不同安装来源。
pub fn get_webview2_version_system() -> Option<String> {
    let paths = [
        r"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients",
        r"SOFTWARE\Microsoft\EdgeUpdate\Clients",
    ];

    for path in paths {
        let hklm = RegKey::predef(HKEY_LOCAL_MACHINE);
        let Ok(key) = hklm.open_subkey(path) else {
            continue;
        };
        for guid in key.enum_keys().flatten() {
            let Ok(client) = key.open_subkey(&guid) else {
                continue;
            };
            let Ok(name) = client.get_value::<String, _>("name") else {
                continue;
            };
            if name.contains("WebView2") {
                if let Ok(version) = client.get_value::<String, _>("pv") {
                    return Some(version);
                }
            }
        }
    }
    None
}

/// 内置 WebView2 运行时目录：`<root>/lib/webview2/`。
pub fn get_webview2_install_dir() -> PathBuf {
    crate::core::env::get_lib_dir().join("webview2")
}

/// 读取内置 WebView2 版本。
///
/// 优先读安装时写入的 `version.txt`；文件缺失但主程序存在时，
/// 说明运行时可用但版本未知，返回占位文案键以外的中性描述，
/// 保证界面不会把它误判为「未安装」。
pub fn get_webview2_version_builtin() -> Option<String> {
    let install_dir = get_webview2_install_dir();
    if let Ok(content) = fs::read_to_string(install_dir.join(VERSION_FILE)) {
        let version = content.trim();
        if !version.is_empty() {
            return Some(version.to_owned());
        }
    }

    install_dir
        .join(RUNTIME_EXECUTABLE)
        .is_file()
        .then(|| "unknown".to_owned())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn builtin_install_dir_sits_under_lib() {
        let dir = get_webview2_install_dir();
        assert!(dir.ends_with("webview2"));
        assert!(dir.is_absolute());
    }
}
