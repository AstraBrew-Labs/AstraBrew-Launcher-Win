//! 开机自启动管理（Windows 注册表）
//!
//! 使用 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 注册表键。
//! HKCU 级别无需管理员权限。

use crate::core::settings::autostart;

/// 启用 / 禁用开机自启动
pub fn set_auto_launch(enabled: bool) -> Result<(), String> {
    autostart::sync(enabled);
    Ok(())
}

/// 当前是否已注册为登录项
pub fn is_auto_launch_enabled() -> bool {
    let mut reg_cmd = std::process::Command::new("reg");
    crate::core::env::apply_no_window_to_command(&mut reg_cmd);
    let output = reg_cmd
        .args([
            "query",
            autostart::REG_KEY,
            "/v",
            autostart::REG_VALUE_NAME,
        ])
        .output();
    match output {
        Ok(out) => out.status.success(),
        Err(_) => false,
    }
}

/// 查询自启动详细状态（Windows 上仅 enabled/disabled）
pub fn get_auto_launch_status() -> &'static str {
    if is_auto_launch_enabled() {
        "enabled"
    } else {
        "disabled"
    }
}
