//! Windows 开机自启动管理。
//!
//! 通过注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 登记启动项：
//! 写入一个「值名 → 可执行文件路径」的字符串，系统在用户登录时代为启动。
//!
//! # 为什么用 HKCU 而不是任务计划程序
//!
//! - `HKCU` 只影响当前用户，无需管理员权限，也不需要 UAC 提权；
//! - 用户可以在「任务管理器 → 启动」中看到并禁用该项，与系统预期一致；
//! - 卸载时只需删除一个注册表值，不留残渣。
//!
//! 值的写入与删除都是幂等的：重复启用只会覆盖同一个值，不会产生多条记录。

use crate::lang::t;
use crate::lang::tf;
use std::path::PathBuf;
use std::process::Command;

use winreg::RegKey;
use winreg::enums::{HKEY_CURRENT_USER, KEY_READ, KEY_WRITE};

/// 注册表启动项位置（相对 `HKEY_CURRENT_USER`）。
const RUN_KEY: &str = r"Software\Microsoft\Windows\CurrentVersion\Run";

/// 启动项在注册表中的值名。
///
/// 使用固定名称，保证「设置 → 取消 → 再设置」始终操作同一条记录。
const VALUE_NAME: &str = "AstraBrew Launcher";

/// 系统登录项状态。
///
/// Windows 的注册表启动项只有「已登记 / 未登记」两态，
/// 因此这里只保留必要的分支；`NotFound` 用于表达「注册表键不可读」
/// 这一异常情况，便于界面提示用户。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LoginItemStatus {
    /// 尚未登记自启动。
    NotRegistered,
    /// 已登记，登录时会自动启动。
    Enabled,
    /// 注册表不可读（策略限制等），无法判定当前状态。
    NotFound,
}

impl LoginItemStatus {
    /// 该状态是否表示「登录时会自动启动」。
    pub const fn is_active(self) -> bool {
        matches!(self, Self::Enabled)
    }
}

/// 打开启动项注册表键。
fn run_key(writable: bool) -> Result<RegKey, String> {
    let access = if writable { KEY_WRITE | KEY_READ } else { KEY_READ };
    RegKey::predef(HKEY_CURRENT_USER)
        .open_subkey_with_flags(RUN_KEY, access)
        .map_err(|error| tf("autolaunch.registry_open_failed", &[("error", &error)]))
}

/// 查询自启动状态。
///
/// 只按「注册表里有没有这一条」判定，不校验路径是否仍指向当前可执行文件：
/// 用户可能手动改过路径，此时界面应如实显示「已启用」，由用户自行决定是否重设。
fn login_item_status() -> LoginItemStatus {
    match run_key(false) {
        Ok(key) => match key.get_value::<String, _>(VALUE_NAME) {
            Ok(value) if !value.trim().is_empty() => LoginItemStatus::Enabled,
            _ => LoginItemStatus::NotRegistered,
        },
        // 注册表键打不开（策略限制等）时无法判定，如实上报而不是假装未启用。
        Err(_) => LoginItemStatus::NotFound,
    }
}

/// 把可执行路径包装成注册表命令行。
///
/// 安装目录名可能含空格（`AstraBrew Launcher`），因此必须加引号，
/// 否则系统会把路径在第一个空格处截断，导致登录时启动失败。
fn quoted_command(executable: &PathBuf) -> String {
    format!("\"{}\"", executable.display())
}

/// 设置开机自启动的结果。
///
/// Windows 的注册表启动项无需用户批准，因此只有「已生效」一种结果；
/// 保留枚举是为了让调用方显式处理返回值，将来若引入更高权限的
/// 启动方式（如计划任务）也能平滑扩展。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AutoLaunchOutcome {
    /// 已按预期生效。
    Applied,
}

/// 设置开机自启动。
///
/// 启用：把当前可执行文件的绝对路径写入 `Run` 键；
/// 禁用：删除该注册表值（不存在时也视为成功）。
pub fn set_auto_launch(enabled: bool) -> Result<AutoLaunchOutcome, String> {
    if !enabled {
        let key = run_key(true)?;
        // 删除不存在的值会返回 NotFound，这属于正常的「已经是关闭状态」。
        if let Err(error) = key.delete_value(VALUE_NAME)
            && error.kind() != std::io::ErrorKind::NotFound
        {
            return Err(tf("autolaunch.unregister_failed", &[("error", &error)]));
        }
        return Ok(AutoLaunchOutcome::Applied);
    }

    let executable = std::env::current_exe()
        .map_err(|error| tf("autolaunch.exe_path_failed", &[("error", &error)]))?;
    let key = run_key(true)?;
    key.set_value(VALUE_NAME, &quoted_command(&executable))
        .map_err(|error| tf("autolaunch.register_failed_reason", &[("error", &error)]))?;

    Ok(AutoLaunchOutcome::Applied)
}

/// 当前是否已经启用开机自启动。
pub fn is_auto_launch_enabled() -> bool {
    login_item_status().is_active()
}

/// 打开系统的「启动应用」设置页。
///
/// Windows 11 的「设置 → 应用 → 启动」与 Windows 10 的「启动」页
/// 使用同一个 URI 协议，直接交给 `explorer` 打开即可。
pub fn open_login_item_settings() -> Result<(), String> {
    let mut command = Command::new("explorer.exe");
    command.arg("ms-settings:startupapps");
    crate::core::env::apply_no_window_to_command(&mut command);
    let status = command
        .status()
        .map_err(|error| tf("autolaunch.open_settings_failed", &[("error", &error)]))?;
    if status.success() {
        Ok(())
    } else {
        Err(t("autolaunch.login_items_failed").to_owned())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn status_is_active_only_when_enabled() {
        assert!(LoginItemStatus::Enabled.is_active());
        assert!(!LoginItemStatus::NotRegistered.is_active());
        // 注册表不可读时不能当作「已启用」，否则界面会骗用户。
        assert!(!LoginItemStatus::NotFound.is_active());
    }

    #[test]
    fn outcome_is_comparable() {
        assert_eq!(AutoLaunchOutcome::Applied, AutoLaunchOutcome::Applied);
    }

    /// 路径含空格时必须加引号，否则登录时会被截断成非法命令。
    #[test]
    fn command_is_quoted_for_paths_with_spaces() {
        let executable = PathBuf::from(r"C:\Users\tester\AppData\Local\AstraBrew Launcher\launcher.exe");
        let command = quoted_command(&executable);
        assert!(command.starts_with('"'));
        assert!(command.ends_with('"'));
        assert!(command.contains("AstraBrew Launcher"));
    }

    #[test]
    fn run_key_points_into_current_user_hive() {
        // 只验证常量拼写，避免误写成 HKLM 导致需要管理员权限。
        assert!(RUN_KEY.starts_with("Software\\Microsoft\\Windows\\CurrentVersion"));
        assert!(!VALUE_NAME.is_empty());
    }
}
