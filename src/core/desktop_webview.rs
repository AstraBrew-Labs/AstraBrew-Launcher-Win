//! 桌面模式 WebView 管理器（Windows 版本占位）
//!
//! 原 macOS 版本使用 NSWindow + WKWebView 实现原生 WebView 窗口。
//! Windows 版本待替换为 WebView2 或等价实现。
//!
//! 当前所有方法均为空实现，桌面模式在 Windows 上暂不可用。

#![allow(dead_code)]

use std::sync::{LazyLock, Mutex};

/// 导出路径（占位，桌面模式暂不可用）
static EXPORT_PATH: LazyLock<Mutex<String>> = LazyLock::new(|| {
    Mutex::new(String::new())
});

/// blob 下载结果通知队列（占位）
pub static DOWNLOAD_NOTIFICATIONS: LazyLock<Mutex<Vec<String>>> =
    LazyLock::new(|| Mutex::new(Vec::new()));

/// 桌面模式 WebView（Windows 占位桩）
///
/// 当前桌面模式在 Windows 上暂不可用。
/// TODO: 使用 Microsoft Edge WebView2 实现
pub struct DesktopWebView;

impl DesktopWebView {
    /// 更新导出文件保存目录（占位）
    pub fn set_export_path(_path: &str) {
        // Windows 桌面模式待实现
    }

    /// 创建 WebView 窗口（占位，始终返回错误）
    pub fn open(_url: &str, _title: &str, _export_path: String) -> Result<Self, String> {
        Err("桌面模式在 Windows 上暂不可用，将使用普通浏览器打开酒馆".into())
    }

    /// 关闭 WebView 窗口（占位）
    pub fn close(&mut self) {}

    /// 将 WebView 窗口唤回前台（占位）
    pub fn bring_to_front(&self) {}

    /// 检查 WebView 窗口是否已关闭（占位）
    pub fn is_closed(&self) -> bool {
        true
    }

    /// WebView 是否仍在运行（占位）
    pub fn is_running(&self) -> bool {
        false
    }
}
