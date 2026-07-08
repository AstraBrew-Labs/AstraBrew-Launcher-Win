//! 权限检测与引导（Windows 版本）
//!
//! 原 macOS 版本检测「完全磁盘访问权限」(FDA) 和 TCC 权限。
//! Windows 上 FDA/TCC 概念不适用，所有权限检测恒返回已授权。
//!
//! 当前版本管理页面直接启动全盘扫描，不再需要权限检测，
//! 保留此模块以备将来扩展需求。

#![allow(dead_code)]

/// 权限探测结果
#[derive(Debug, Clone)]
pub struct ScanPermissions {
    /// 是否至少能成功读取一个 FDA 保护目录
    pub fda_ok: bool,
    /// Desktop 是否可访问
    pub desktop_ok: bool,
    /// Documents 是否可访问
    pub documents_ok: bool,
    /// Downloads 是否可访问
    pub downloads_ok: bool,
}

impl ScanPermissions {
    /// 所有权限都 OK
    pub fn all_ok(&self) -> bool {
        self.fda_ok && self.desktop_ok && self.documents_ok && self.downloads_ok
    }
}

/// Windows 上权限概念不适用，恒返回 true
pub fn is_full_disk_access_granted() -> bool {
    true
}

/// Windows 上无对应设置面板（占位）
pub fn open_full_disk_access_settings() {}

/// 探测扫描所需的各项权限（Windows 上恒返回全通过）
pub fn probe_scan_permissions() -> ScanPermissions {
    ScanPermissions {
        fda_ok: true,
        desktop_ok: true,
        documents_ok: true,
        downloads_ok: true,
    }
}
