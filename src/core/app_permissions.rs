//! macOS 权限检测与引导
//!
//! 覆盖「完全磁盘访问权限」（Full Disk Access, FDA）和
//! 「文件与文件夹」TCC 权限。
//!
//! 检测原理（两层）：
//! 1. 沙箱检测（仅对沙箱应用有效）：对比 NSFileManager 返回的
//!    HOME 与真实 $HOME，若被重定向到
//!    `~/Library/Containers/<bundle-id>/...` → 判定无权限。
//! 2. 实际文件访问探测（对沙箱/非沙箱应用均有效）：尝试读取
//!    macOS 受 FDA 保护的目录（如 ~/Library/Safari），若返回
//!    PermissionDenied 则判定无权限。同时也探测 Desktop/
//!    Documents/Downloads 的"文件与文件夹"权限。

#[cfg(target_os = "macos")]
mod imp {
    use objc::{class, msg_send, sel};
    #[allow(unused_imports)]
    use objc::sel_impl;

    /// 读取真实环境变量 HOME（不受沙箱重定向影响）
    fn real_home() -> Option<String> {
        std::env::var("HOME").ok().filter(|h| !h.is_empty())
    }

    /// 通过 NSFileManager 读取 homeDirectoryForCurrentUser（沙箱应用会被重定向）
    fn ns_home() -> Option<String> {
        unsafe {
            let fm: *mut objc::runtime::Object = msg_send![class!(NSFileManager), defaultManager];
            if fm.is_null() {
                return None;
            }
            let url: *mut objc::runtime::Object = msg_send![fm, homeDirectoryForCurrentUser];
            if url.is_null() {
                return None;
            }
            let path: *mut objc::runtime::Object = msg_send![url, path];
            if path.is_null() {
                return None;
            }
            let c_str: *const std::os::raw::c_char = msg_send![path, UTF8String];
            if c_str.is_null() {
                return None;
            }
            let s = std::ffi::CStr::from_ptr(c_str).to_string_lossy().to_string();
            Some(s)
        }
    }

    /// 是否已授予完全磁盘访问权限。
    ///
    /// 判定：NSFileManager 给出的 HOME 与真实 HOME 一致 → 已授权；
    /// 若被重定向到 `Library/Containers/...` → 未授权。
    pub fn is_full_disk_access_granted() -> bool {
        let real = match real_home() {
            Some(h) => h,
            None => return true, // 无法读取环境变量时不阻断，保守视为已授权
        };
        let ns = match ns_home() {
            Some(h) => h,
            None => return true, // ObjC 调用失败时不阻断
        };
        // 关键判定：NSFileManager 路径包含 Library/Containers → 被沙箱重定向
        !ns.contains("Library/Containers") && ns == real
    }

    /// 打开「系统设置 → 隐私与安全性 → 完全磁盘访问权限」
    pub fn open_full_disk_access_settings() {
        let _ = std::process::Command::new("open")
            .arg("x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles")
            .spawn();
    }
}

#[cfg(not(target_os = "macos"))]
mod imp {
    pub fn is_full_disk_access_granted() -> bool {
        true
    }
    pub fn open_full_disk_access_settings() {}
}

/// 是否已授予完全磁盘访问权限
pub fn is_full_disk_access_granted() -> bool {
    imp::is_full_disk_access_granted()
}

/// 跳转到「系统设置 → 完全磁盘访问权限」面板
pub fn open_full_disk_access_settings() {
    imp::open_full_disk_access_settings();
}

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

/// 通过实际文件 I/O 探测扫描所需的各项权限。
///
/// 该函数不依赖沙箱机制，对沙箱/非沙箱应用均有效。
/// 注意：访问受保护目录可能触发 macOS 系统弹窗（TCC 授权请求），
/// 因此仅在用户主动操作（如点击"自动扫描"）时调用。
pub fn probe_scan_permissions() -> ScanPermissions {
    let home = match std::env::var("HOME").ok().filter(|h| !h.is_empty()) {
        Some(h) => std::path::PathBuf::from(h),
        None => {
            return ScanPermissions {
                fda_ok: false,
                desktop_ok: false,
                documents_ok: false,
                downloads_ok: false,
            };
        }
    };

    // FDA 探测：尝试读取受 FDA 保护的路径
    // 选几个常见 macOS 应用数据目录，任何一个可读即认为 FDA 正常
    let fda_paths = [
        home.join("Library/Safari"),
        home.join("Library/Mail"),
        home.join("Library/Messages"),
        home.join("Library/Calendars"),
    ];
    let fda_ok = fda_paths.iter().any(|p| {
        std::fs::read_dir(p)
            .map(|mut entries| entries.next().is_some())
            .unwrap_or(false)
    });

    // 文件与文件夹权限探测
    let desktop_ok = std::fs::read_dir(home.join("Desktop"))
        .map(|mut entries| entries.next().is_some())
        .unwrap_or(false);
    let documents_ok = std::fs::read_dir(home.join("Documents"))
        .map(|mut entries| entries.next().is_some())
        .unwrap_or(false);
    let downloads_ok = std::fs::read_dir(home.join("Downloads"))
        .map(|mut entries| entries.next().is_some())
        .unwrap_or(false);

    ScanPermissions {
        fda_ok,
        desktop_ok,
        documents_ok,
        downloads_ok,
    }
}
