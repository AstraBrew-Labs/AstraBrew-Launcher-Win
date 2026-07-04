//! macOS 开机自启动管理（SMAppService）
//!
//! 使用 macOS 13+ 的 `SMAppService.mainApp` API：
//! - `register()` 注册为登录项（系统设置 → 通用 → 登录项与扩展中可见可控）
//! - `unregister()` 取消注册
//! - `status()` 查询当前状态
//!
//! 注意：该 API 仅在应用被打包为 .app bundle 时生效。开发期裸二进制
//! 调用 `register()` 会失败，调用方需通过返回值 + UI toast 提示用户。
//! 渲染时会用 `is_auto_launch_enabled()` 校准开关状态，确保不卡在错误状态。

#[cfg(target_os = "macos")]
mod imp {
    use smappservice_rs::{AppService, ServiceStatus, ServiceType};

    /// 当前应用的 MainApp 服务句柄
    pub(crate) fn service() -> AppService {
        AppService::new(ServiceType::MainApp)
    }

    /// 注册为登录项，返回 Ok 表示成功
    pub fn register() -> Result<(), String> {
        service()
            .register()
            .map_err(|e| format!("{:?}", e))
    }

    /// 取消注册，返回 Ok 表示成功
    pub fn unregister() -> Result<(), String> {
        service()
            .unregister()
            .map_err(|e| format!("{:?}", e))
    }

    /// 查询是否已注册（Enabled 或 RequiresApproval 都视为已注册）
    pub fn is_registered() -> bool {
        matches!(
            service().status(),
            ServiceStatus::Enabled | ServiceStatus::RequiresApproval
        )
    }
}

#[cfg(not(target_os = "macos"))]
mod imp {
    pub fn register() -> Result<(), String> {
        Err("auto-launch only supported on macOS".into())
    }
    pub fn unregister() -> Result<(), String> {
        Ok(())
    }
    pub fn is_registered() -> bool {
        false
    }
}

/// 启用 / 禁用开机自启动
///
/// - `enabled = true`：注册为登录项
/// - `enabled = false`：取消注册
///
/// 返回 `Ok(())` 表示操作成功；`Err` 携带失败原因（用于 UI toast 提示）。
pub fn set_auto_launch(enabled: bool) -> Result<(), String> {
    if enabled {
        imp::register()
    } else {
        imp::unregister()
    }
}

/// 当前是否已注册为登录项（用于渲染时校准开关状态）
pub fn is_auto_launch_enabled() -> bool {
    imp::is_registered()
}

/// 查询自启动详细状态，用于 UI 显示
///
/// 返回："enabled" | "disabled" | "requires_approval"
pub fn get_auto_launch_status() -> &'static str {
    #[cfg(target_os = "macos")]
    {
        let status = imp::service().status();
        match status {
            smappservice_rs::ServiceStatus::Enabled => "enabled",
            smappservice_rs::ServiceStatus::RequiresApproval => "requires_approval",
            _ => "disabled",
        }
    }
    #[cfg(not(target_os = "macos"))]
    "disabled"
}
