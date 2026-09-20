//! 构建期脚本。
//!
//! 做两件事：
//! 1. 把构建渠道注入为编译期环境变量 `ASTRA_BUILD_CHANNEL`，供界面标注测试版；
//! 2. 在 Windows 上把应用图标与版本信息写进 exe 的 PE 资源，
//!    使任务栏、资源管理器与快捷方式显示正确的图标和产品名。
//!
//! 注意：本项目的自启动能力通过注册表实现（见 `core/auto_launch.rs`），
//! 因此**不需要**在构建期链接任何额外系统库。

fn main() {
    // 渠道由构建脚本通过 `ASTRA_BUILD_CHANNEL` 传入；改动后需要触发重编译。
    println!("cargo:rerun-if-env-changed=ASTRA_BUILD_CHANNEL");
    let channel = std::env::var("ASTRA_BUILD_CHANNEL").unwrap_or_else(|_| "release".to_owned());
    println!("cargo:rustc-env=ASTRA_BUILD_CHANNEL={channel}");

    // 更新模块需要按目标三元组与架构匹配发布清单里的平台键。
    // 这两个值在运行时无法可靠获取（`std::env::consts` 只给 OS 与 ARCH），
    // 因此在编译期注入。
    let target = std::env::var("TARGET").unwrap_or_else(|_| "x86_64-pc-windows-msvc".to_owned());
    println!("cargo:rustc-env=TARGET_TRIPLE_FOR_UPDATER={target}");
    let arch = std::env::var("CARGO_CFG_TARGET_ARCH").unwrap_or_else(|_| "x86_64".to_owned());
    println!("cargo:rustc-env=TARGET_ARCH_FOR_UPDATER={arch}");
    println!("cargo:rerun-if-env-changed=TARGET");
    println!("cargo:rerun-if-env-changed=CARGO_CFG_TARGET_ARCH");

    #[cfg(target_os = "windows")]
    embed_windows_resources();
}

/// 嵌入 Windows PE 资源：应用图标 + 版本信息。
///
/// 图标来自 `assets/icon/icon.ico`（需含 16/32/48/256 多尺寸，保证各缩放档清晰）；
/// 缺失时跳过而不报错，避免开发环境必需先准备图标才能编译。
#[cfg(target_os = "windows")]
fn embed_windows_resources() {
    let icon_path = std::path::Path::new("assets/icon/icon.ico");
    if !icon_path.exists() {
        println!("cargo:warning=未找到 assets/icon/icon.ico，跳过图标嵌入");
        return;
    }
    println!("cargo:rerun-if-changed=assets/icon/icon.ico");

    let mut res = winresource::WindowsResource::new();
    res.set_icon(icon_path.to_str().unwrap_or("assets/icon/icon.ico"));
    res.set("ProductName", "AstraBrew Launcher");
    res.set("FileDescription", "AstraBrew Launcher");
    res.set("CompanyName", "AstraBrew-Labs");
    res.set("LegalCopyright", "MIT License");
    // 版本号取自 Cargo.toml，保证 exe 属性面板与安装包版本一致。
    res.set("ProductVersion", env!("CARGO_PKG_VERSION"));
    res.set("FileVersion", env!("CARGO_PKG_VERSION"));

    // 显式指定资源编译器所在目录：`rc.exe` 通常不在 PATH 上（只有 VS 开发者
    // 命令行才有），这里直接按已安装的 Windows SDK 版本查找，避免图标悄悄嵌入失败。
    if let Some(bin_dir) = locate_sdk_rc_bin() {
        res.set_toolkit_path(bin_dir.to_str().unwrap_or_default());
    }

    if let Err(error) = res.compile() {
        println!("cargo:warning=嵌入 Windows 资源失败: {error}");
    }
}

/// 在已安装的 Windows SDK 中定位资源编译器 `rc.exe` 所在目录。
///
/// 找不到时返回 `None`，此时退回 `winresource` 的默认查找逻辑（查注册表 / PATH）。
#[cfg(target_os = "windows")]
fn locate_sdk_rc_bin() -> Option<std::path::PathBuf> {
    const KITS_ROOT: &str = r"C:\Program Files (x86)\Windows Kits\10\bin";
    // 资源编译器需要与目标架构匹配。
    let arch = if cfg!(target_arch = "x86_64") {
        "x64"
    } else {
        "x86"
    };
    let mut versions: Vec<_> = std::fs::read_dir(KITS_ROOT)
        .ok()?
        .filter_map(Result::ok)
        .map(|entry| entry.path())
        .filter(|path| path.is_dir())
        .collect();
    // 目录名即 SDK 版本号；取字典序最大的（通常为最高版本）。
    versions.sort();
    versions
        .into_iter()
        .rev()
        .map(|version| version.join(arch))
        .find(|bin| bin.join("rc.exe").is_file())
}
