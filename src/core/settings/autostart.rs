//! Windows 注册表自启动管理
//! 
//! 写入/删除 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 下的 AstraBrew 键值。
//! HKCU 级别无需管理员权限。

const REG_KEY: &str = r"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
const REG_VALUE_NAME: &str = "AstraBrewLauncher";

/// 启用自启动：将当前 exe 路径写入注册表 Run 键
pub fn enable() -> Result<(), String> {
    let exe_path = std::env::current_exe()
        .map_err(|e| format!("无法获取 exe 路径: {}", e))?;
    let exe_path_str = exe_path.to_string_lossy().to_string();

    // 使用 reg add 命令，/f 强制覆盖
    let output = std::process::Command::new("reg")
        .args([
            "add",
            REG_KEY,
            "/v", REG_VALUE_NAME,
            "/t", "REG_SZ",
            "/d", &exe_path_str,
            "/f",
        ])
        .output()
        .map_err(|e| format!("执行 reg add 失败: {}", e))?;

    if output.status.success() {
        Ok(())
    } else {
        let stderr = String::from_utf8_lossy(&output.stderr);
        Err(format!("注册表写入失败: {}", stderr))
    }
}

/// 禁用自启动：从注册表 Run 键删除 AstraBrewLauncher 值
pub fn disable() -> Result<(), String> {
    let output = std::process::Command::new("reg")
        .args([
            "delete",
            REG_KEY,
            "/v", REG_VALUE_NAME,
            "/f",
        ])
        .output()
        .map_err(|e| format!("执行 reg delete 失败: {}", e))?;

    if output.status.success() {
        Ok(())
    } else {
        // 如果键值不存在，也视为成功（幂等）
        let stderr = String::from_utf8_lossy(&output.stderr);
        if stderr.contains("unable to find") || stderr.contains("找不到") {
            Ok(())
        } else {
            Err(format!("注册表删除失败: {}", stderr))
        }
    }
}

/// 根据开关状态同步注册表
pub fn sync(enabled: bool) {
    let result = if enabled { enable() } else { disable() };
    match result {
        Ok(()) => {
            eprintln!(
                "[autostart] {}: 注册表已{}",
                if enabled { "启用" } else { "禁用" },
                if enabled { "写入" } else { "删除" }
            );
        }
        Err(e) => {
            eprintln!("[autostart] 操作失败: {}", e);
        }
    }
}
