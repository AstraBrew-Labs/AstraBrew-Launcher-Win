use std::fs::{self, File};
use std::io::{Read, Write};
use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::mpsc::Sender;
use std::time::Duration;

use serde::Deserialize;
use winreg::RegKey;
use winreg::enums::*;

use crate::EnvInstallProgress;

/// 隐藏 `expand.exe` 等控制台程序的命令行窗口，避免安装时闪黑窗。
const CREATE_NO_WINDOW: u32 = 0x08000000;
/// 当前项目仅支持 Windows x64，因此固定下载 x64 的 WebView2 运行时。
const WEBVIEW2_ARCH: &str = "x64";
/// WebView2 官方固定版版本接口。
const WEBVIEW2_API_URL: &str = "https://developer.microsoft.com/microsoft-edge/api/webview2";
/// 官方接口不可用时的兜底版本号。
const WEBVIEW2_FALLBACK_VERSION: &str = "150.0.4078.65";
/// 官方接口不可用时的兜底直链。
const WEBVIEW2_FALLBACK_URL: &str = "https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/c00b9782-0422-4114-be27-8eec079b394d/Microsoft.WebView2.FixedVersionRuntime.150.0.4078.65.x64.cab";

#[derive(Debug, Deserialize)]
struct WebView2Release {
    version: String,
    builds: Vec<WebView2Build>,
}

#[derive(Debug, Deserialize)]
struct WebView2Build {
    architecture: String,
    url: String,
}

/// 获取系统 WebView2 版本号（通过注册表）
pub fn get_webview2_version_system() -> Option<String> {
    let paths = [
        r"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients",
        r"SOFTWARE\Microsoft\EdgeUpdate\Clients",
    ];

    for path in paths {
        let hklm = RegKey::predef(HKEY_LOCAL_MACHINE);
        if let Ok(key) = hklm.open_subkey(path) {
            for guid in key.enum_keys().flatten() {
                if let Ok(client) = key.open_subkey(&guid) {
                    let name: Result<String, _> = client.get_value("name");
                    if let Ok(name) = name {
                        if name.contains("WebView2") {
                            let version: Result<String, _> = client.get_value("pv");
                            return version.ok();
                        }
                    }
                }
            }
        }
    }
    None
}

/// 获取内置 WebView2 安装目录。
fn get_webview2_install_dir() -> PathBuf {
    crate::utils::app_paths().lib.join("webview2")
}

/// 获取 WebView2 安装时使用的临时目录。
fn get_temp_download_dir() -> PathBuf {
    let dir = crate::utils::app_paths().temp.join("webview2");
    let _ = fs::create_dir_all(&dir);
    dir
}

/// 读取官方接口，拿到最新的固定版 WebView2 x64 下载地址。
fn resolve_fixed_runtime_package() -> Result<(String, String), Box<dyn std::error::Error>> {
    let client = reqwest::blocking::Client::builder()
        .timeout(Duration::from_secs(30))
        .build()?;

    let response = client.get(WEBVIEW2_API_URL).send()?;
    if !response.status().is_success() {
        return Err(format!("获取 WebView2 版本信息失败，HTTP 状态码: {}", response.status()).into());
    }

    let releases: Vec<WebView2Release> = response.json()?;
    for release in releases {
        if let Some(build) = release
            .builds
            .iter()
            .find(|item| item.architecture.eq_ignore_ascii_case(WEBVIEW2_ARCH))
        {
            return Ok((release.version, build.url.clone()));
        }
    }

    Err("未找到可用的 WebView2 x64 固定版下载地址".into())
}

/// 使用官方接口获取下载信息；若接口异常则回退到内置的已知稳定版本。
fn resolve_fixed_runtime_package_with_fallback() -> (String, String) {
    resolve_fixed_runtime_package().unwrap_or_else(|_| {
        (
            WEBVIEW2_FALLBACK_VERSION.to_string(),
            WEBVIEW2_FALLBACK_URL.to_string(),
        )
    })
}

/// 递归查找包含 `msedgewebview2.exe` 的运行时根目录。
fn find_runtime_root(dir: &Path) -> Option<PathBuf> {
    if dir.join("msedgewebview2.exe").exists() {
        return Some(dir.to_path_buf());
    }

    let entries = fs::read_dir(dir).ok()?;
    for entry in entries.flatten() {
        let path = entry.path();
        if path.is_dir() {
            if let Some(found) = find_runtime_root(&path) {
                return Some(found);
            }
        }
    }
    None
}

/// 递归收集目录中的全部文件，用于复制阶段进度统计。
fn collect_files(dir: &Path, output: &mut Vec<PathBuf>) -> std::io::Result<()> {
    for entry in fs::read_dir(dir)? {
        let entry = entry?;
        let path = entry.path();
        if path.is_dir() {
            collect_files(&path, output)?;
        } else {
            output.push(path);
        }
    }
    Ok(())
}

/// 递归复制固定版运行时到 `lib/webview2`。
fn copy_runtime_to_install_dir(
    source_root: &Path,
    install_dir: &Path,
    progress_sender: Option<&Sender<EnvInstallProgress>>,
) -> Result<(), Box<dyn std::error::Error>> {
    let mut files = Vec::new();
    collect_files(source_root, &mut files)?;
    if files.is_empty() {
        return Err("解压后的 WebView2 目录为空".into());
    }

    for (index, file_path) in files.iter().enumerate() {
        let relative = file_path.strip_prefix(source_root)?;
        let target_path = install_dir.join(relative);
        if let Some(parent) = target_path.parent() {
            fs::create_dir_all(parent)?;
        }
        fs::copy(file_path, &target_path)?;

        if let Some(tx) = progress_sender {
            let progress = 0.6 + 0.35 * ((index + 1) as f32 / files.len() as f32);
            let _ = tx.send(EnvInstallProgress::Progress(progress));
            let _ = tx.send(EnvInstallProgress::Status(format!(
                "安装文件中: {}/{}",
                index + 1,
                files.len()
            )));
        }
    }

    Ok(())
}

/// 获取内置 WebView2 版本号。
///
/// 安装完成后会写入 `version.txt`，用于设置页快速显示版本号。
pub fn get_webview2_version_builtin() -> Option<String> {
    let install_dir = get_webview2_install_dir();
    let version_file = install_dir.join("version.txt");
    if let Ok(content) = fs::read_to_string(version_file) {
        let version = content.trim();
        if !version.is_empty() {
            return Some(version.to_string());
        }
    }

    if install_dir.join("msedgewebview2.exe").exists() {
        Some("未知版本".to_string())
    } else {
        None
    }
}

/// 下载并安装固定版 WebView2 到内置 `lib/webview2` 目录。
pub fn download_and_install_webview2(
    progress_sender: Option<Sender<EnvInstallProgress>>,
) -> Result<(), Box<dyn std::error::Error>> {
    if let Some(tx) = &progress_sender {
        let _ = tx.send(EnvInstallProgress::Status(
            "正在获取 WebView2 固定版下载信息...".to_string(),
        ));
    }

    let (version, url) = resolve_fixed_runtime_package_with_fallback();
    let filename = format!(
        "Microsoft.WebView2.FixedVersionRuntime.{}.{}.cab",
        version, WEBVIEW2_ARCH
    );
    let temp_dir = get_temp_download_dir();
    let temp_file_path = temp_dir.join(&filename);
    let expand_dir = temp_dir.join(format!("expand-{}", version));
    let install_dir = get_webview2_install_dir();

    if let Some(tx) = &progress_sender {
        let _ = tx.send(EnvInstallProgress::Status(format!(
            "已选择版本: {} ({})",
            version, WEBVIEW2_ARCH
        )));
        let _ = tx.send(EnvInstallProgress::Status(format!("下载地址: {}", url)));
    }

    let client = reqwest::blocking::Client::builder()
        .timeout(Duration::from_secs(900))
        .build()?;

    let mut response = client.get(&url).send()?;
    if !response.status().is_success() {
        let err_msg = format!("下载失败，HTTP 状态码: {}", response.status());
        if let Some(tx) = &progress_sender {
            let _ = tx.send(EnvInstallProgress::Error(err_msg.clone()));
        }
        return Err(err_msg.into());
    }

    let total_size = response.content_length().unwrap_or(0);
    if let Some(tx) = &progress_sender {
        if total_size > 0 {
            let size_mb = total_size as f64 / 1048576.0;
            let _ = tx.send(EnvInstallProgress::Status(format!(
                "开始下载固定版运行时 (文件大小: {:.1} MB)",
                size_mb
            )));
        } else {
            let _ = tx.send(EnvInstallProgress::Status(
                "开始下载固定版运行时...".to_string(),
            ));
        }
    }

    {
        let mut dest = File::create(&temp_file_path)?;
        let mut buffer = [0; 8192];
        let mut downloaded_size: u64 = 0;
        let download_start = std::time::Instant::now();
        let mut last_speed_report = download_start;

        loop {
            let bytes_read = response.read(&mut buffer)?;
            if bytes_read == 0 {
                break;
            }
            dest.write_all(&buffer[..bytes_read])?;
            downloaded_size += bytes_read as u64;

            if total_size > 0 {
                let progress = (downloaded_size as f32) / (total_size as f32);
                if let Some(tx) = &progress_sender {
                    let _ = tx.send(EnvInstallProgress::Progress(progress * 0.6));
                    if last_speed_report.elapsed().as_millis() >= 500 {
                        let elapsed = download_start.elapsed().as_secs_f32();
                        if elapsed > 0.0 {
                            let speed = downloaded_size as f32 / elapsed;
                            let _ = tx.send(EnvInstallProgress::Speed(speed));
                        }
                        last_speed_report = std::time::Instant::now();
                    }
                }
            } else if let Some(tx) = &progress_sender {
                let mb = downloaded_size as f64 / 1048576.0;
                let _ = tx.send(EnvInstallProgress::Status(format!(
                    "下载中: {:.1} MB",
                    mb
                )));
            }
        }
    }

    if expand_dir.exists() {
        fs::remove_dir_all(&expand_dir)?;
    }
    fs::create_dir_all(&expand_dir)?;

    if let Some(tx) = &progress_sender {
        let _ = tx.send(EnvInstallProgress::Status(
            "下载完成，正在解压固定版运行时...".to_string(),
        ));
        let _ = tx.send(EnvInstallProgress::Speed(0.0));
    }

    let expand_output = Command::new("expand.exe")
        .creation_flags(CREATE_NO_WINDOW)
        .arg(&temp_file_path)
        .arg("-F:*")
        .arg(&expand_dir)
        .output()?;

    if !expand_output.status.success() {
        let stderr = String::from_utf8_lossy(&expand_output.stderr);
        let stdout = String::from_utf8_lossy(&expand_output.stdout);
        let err_msg = format!(
            "解压 WebView2 CAB 失败: {}{}{}",
            stdout.trim(),
            if stdout.trim().is_empty() || stderr.trim().is_empty() {
                ""
            } else {
                " "
            },
            stderr.trim()
        );
        if let Some(tx) = &progress_sender {
            let _ = tx.send(EnvInstallProgress::Error(err_msg.clone()));
        }
        return Err(err_msg.into());
    }

    let runtime_root = find_runtime_root(&expand_dir)
        .ok_or("解压完成，但未找到 msedgewebview2.exe")?;

    if install_dir.exists() {
        fs::remove_dir_all(&install_dir)?;
    }
    fs::create_dir_all(&install_dir)?;

    if let Some(tx) = &progress_sender {
        let _ = tx.send(EnvInstallProgress::Status(
            "正在写入内置 WebView2 目录...".to_string(),
        ));
    }
    copy_runtime_to_install_dir(&runtime_root, &install_dir, progress_sender.as_ref())?;

    fs::write(install_dir.join("version.txt"), &version)?;

    let runtime_exe = install_dir.join("msedgewebview2.exe");
    if !runtime_exe.exists() {
        let err_msg = "安装验证失败：未找到 msedgewebview2.exe".to_string();
        if let Some(tx) = &progress_sender {
            let _ = tx.send(EnvInstallProgress::Error(err_msg.clone()));
        }
        return Err(err_msg.into());
    }

    let _ = fs::remove_file(&temp_file_path);
    let _ = fs::remove_dir_all(&expand_dir);

    if let Some(tx) = &progress_sender {
        let _ = tx.send(EnvInstallProgress::Progress(1.0));
        let _ = tx.send(EnvInstallProgress::Version(version.clone()));
        let _ = tx.send(EnvInstallProgress::Status(format!(
            "安装完成 -> {}",
            install_dir.display()
        )));
        let _ = tx.send(EnvInstallProgress::Finished);
    }

    Ok(())
}
