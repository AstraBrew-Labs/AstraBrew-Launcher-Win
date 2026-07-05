//! 自动更新模块（Windows 版本占位）
//!
//! 原 macOS 版本使用 cargo-packager-updater 从 GitHub Releases 检测并安装更新。
//! Windows 更新方案待替换（可选方案：WinSparkle, msix, 或自实现）。
//!
//! 当前所有功能为占位实现，始终返回"已是最新版本"。

#![allow(dead_code)]

use std::sync::mpsc;

/// 更新检测/下载状态
#[derive(Debug, Clone)]
pub enum UpdateStatus {
    /// 正在检查
    Checking,
    /// 已是最新版本
    UpToDate,
    /// 发现新版本（版本号, 更新说明, 可用端点）
    UpdateAvailable {
        version: String,
        notes: Option<String>,
        endpoint: String,
    },
    /// 正在下载安装
    Downloading,
    /// 安装完成（需重启）
    Installed,
    /// 出错
    Error(String),
}

/// 启动后台更新检测（占位，始终返回 UpToDate）
#[allow(dead_code)]
pub fn start_check() -> mpsc::Receiver<UpdateStatus> {
    let (tx, rx) = mpsc::channel();
    std::thread::spawn(move || {
        let _ = tx.send(UpdateStatus::UpToDate);
    });
    rx
}

/// 启动手动更新检测（占位）
pub fn check_update_manual() -> mpsc::Receiver<UpdateStatus> {
    let (tx, rx) = mpsc::channel();
    std::thread::spawn(move || {
        let _ = tx.send(UpdateStatus::Checking);
        std::thread::sleep(std::time::Duration::from_secs(1));
        let _ = tx.send(UpdateStatus::UpToDate);
    });
    rx
}

/// 执行下载安装（占位）
pub fn do_install(_endpoint: String) -> mpsc::Receiver<UpdateStatus> {
    let (tx, rx) = mpsc::channel();
    std::thread::spawn(move || {
        let _ = tx.send(UpdateStatus::Error("Windows 自动更新功能暂未实现".into()));
    });
    rx
}
