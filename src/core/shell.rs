//! Windows Shell 辅助函数。
//!
//! 统一处理：
//! - 打开 URL / URI / 文件时避免走 `cmd /c start`，防止黑窗闪烁；
//! - 把「用系统默认程序打开」的职责交给 `ShellExecuteW`；
//! - 在资源管理器中定位并选中指定文件。

use std::iter;
use std::path::Path;

use windows_sys::Win32::UI::Shell::ShellExecuteW;
use windows_sys::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;

/// 使用系统 Shell 打开目标。
///
/// 支持 HTTP/HTTPS 链接、`ms-settings:` 这类 URI、以及本地文件或目录。
/// 这样可以绕开 `cmd.exe`，避免在 GUI 程序中闪出命令行窗口。
pub fn open_target(target: &str) -> Result<(), String> {
    let operation: Vec<u16> = "open".encode_utf16().chain(iter::once(0)).collect();
    let target_wide: Vec<u16> = target.encode_utf16().chain(iter::once(0)).collect();

    // SAFETY: 两个字符串都已补 NUL 结尾并在调用期间存活；其余参数按 ShellExecuteW
    // 约定传空指针，表示使用默认工作目录与默认显示方式。
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

    // ShellExecuteW 返回值 <= 32 表示失败（32 以下是错误码，不是句柄）。
    if result as usize <= 32 {
        Err(format!("系统打开失败（错误码 {}）: {target}", result as usize))
    } else {
        Ok(())
    }
}

/// 在资源管理器中打开目录。
pub fn open_path(path: &Path) -> Result<(), String> {
    open_target(&path.to_string_lossy())
}

/// 在资源管理器中定位并选中指定文件。
///
/// 文件不存在时退化为打开其父目录，避免用户看到「找不到文件」的系统报错。
pub fn reveal_in_explorer(path: &Path) -> Result<(), String> {
    if path.exists() {
        // `explorer /select,<path>` 会打开父目录并高亮目标文件。
        // 注意斜杠必须是反斜杠，且 `/select,` 与路径之间不能有空格。
        let target = format!("/select,{}", path.to_string_lossy().replace('/', "\\"));
        let mut command = std::process::Command::new("explorer");
        command.arg(target);
        crate::core::env::apply_no_window_to_command(&mut command);
        return command
            .spawn()
            .map(|_| ())
            .map_err(|error| format!("打开资源管理器失败: {error}"));
    }
    match path.parent() {
        Some(parent) if parent.is_dir() => open_path(parent),
        _ => open_path(path),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn open_target_rejects_unresolvable_uri() {
        // 用一个不存在的自定义协议，ShellExecuteW 应返回错误码而非 panic。
        assert!(open_target("astrabrew-nonexistent-scheme://probe").is_err());
    }

    #[test]
    fn reveal_missing_file_falls_back_without_panic() {
        let missing = std::env::temp_dir().join("astrabrew-missing-probe.txt");
        // 仅验证不 panic；实际会尝试打开父目录，这里不关心是否成功。
        let _ = reveal_in_explorer(&missing);
    }
}
