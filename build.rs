//! AstraBrew Launcher 构建脚本
//!
//! 根据环境变量 ASTRABREW_BUILD_TYPE 决定是否启用 beta 编译标识：
//!   - 设为 "beta" 时启用 `cfg(beta)`，UI 会渲染 BETA 角标
//!   - 未设置或其他值时为正式版，不渲染角标
//!
//! 使用环境变量而非 Cargo feature，是因为 cargo-packager 的 beforePackagingCommand
//! 固定为 `cargo build --release`，环境变量会自动继承到子进程，无需修改 Cargo.toml。
//!
//! 构建脚本（bash / PowerShell）在 beta 模式下 export 此环境变量即可。

fn main() {
    // 告知编译器 `beta` 是合法的 cfg 名称，消除 unexpected_cfgs warning
    println!("cargo::rustc-check-cfg=cfg(beta)");

    // 环境变量变化时重新运行本脚本，避免缓存导致 cfg 不更新
    println!("cargo::rerun-if-env-changed=ASTRABREW_BUILD_TYPE");

    if std::env::var("ASTRABREW_BUILD_TYPE").as_deref() == Ok("beta") {
        println!("cargo::rustc-cfg=beta");
    }

    // Windows：嵌入 exe 图标（任务栏、资源管理器等位置显示）
    #[cfg(target_os = "windows")]
    {
        if let Err(e) = winresource::WindowsResource::new()
            .set_icon("icons/icon.ico")
            .compile()
        {
            eprintln!("警告：嵌入 exe 图标失败: {e}（继续构建）");
        }
    }
}
