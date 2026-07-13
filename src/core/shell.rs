//! Windows Shell 辅助函数
//!
//! 统一处理：
//! - 打开 URL / URI / 文件时避免走 `cmd /c start`，防止黑窗闪烁
//! - 将系统决定的默认处理程序交给 ShellExecuteW

use std::iter;

use windows_sys::Win32::UI::Shell::ShellExecuteW;
use windows_sys::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;

/// 使用系统 Shell 打开目标。
///
/// 支持 HTTP/HTTPS 链接、`ms-settings:` 这类 URI、以及本地文件/目录。
/// 这样可以绕开 `cmd.exe`，避免在 GUI 程序中闪出命令行窗口。
pub fn open_target(target: &str) -> Result<(), String> {
    let operation: Vec<u16> = "open".encode_utf16().chain(iter::once(0)).collect();
    let target_wide: Vec<u16> = target.encode_utf16().chain(iter::once(0)).collect();

    let result = unsafe {
        ShellExecuteW(
            std::ptr::null_mut(),
            operation.as_ptr(),
            target_wide.as_ptr(),
            std::ptr::null(),
            std::ptr::null(),
            SW_SHOWNORMAL,
        )
    };

    if result as usize <= 32 {
        Err(format!("系统打开失败: {}", target))
    } else {
        Ok(())
    }
}
