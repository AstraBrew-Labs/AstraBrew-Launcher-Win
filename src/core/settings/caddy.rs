use std::fs::{self, File};
use std::io::{self, Read, Write};
use std::path::PathBuf;
use std::sync::mpsc::Sender;
use std::time::{Duration, Instant};
use zip::ZipArchive;

use crate::EnvInstallProgress;

#[allow(dead_code)]
const CADDY_VERSION: &str = "v2.11.4";
const CADDY_FILENAME: &str = "caddy_2.11.4_windows_amd64.zip";
const CADDY_SOURCE: &str =
    "https://github.com/caddyserver/caddy/releases/download/v2.11.4/caddy_2.11.4_windows_amd64.zip";

/// 构造带代理的 Caddy 下载 URL
/// - `None` / 空字符串 → 直连
/// - `Some(proxy)` → `{proxy}/{source}`
pub fn build_caddy_url(proxy: Option<&str>) -> String {
    match proxy {
        Some(p) if !p.is_empty() => {
            format!("{}/{}", p.trim_end_matches('/'), CADDY_SOURCE)
        }
        _ => CADDY_SOURCE.to_string(),
    }
}

/// 获取临时下载目录
fn get_temp_download_dir() -> PathBuf {
    let dir = std::env::temp_dir().join("astrabrew-launcher");
    let _ = fs::create_dir_all(&dir);
    dir
}

/// 获取 Caddy 安装目录
pub fn get_caddy_install_dir() -> PathBuf {
    crate::core::env::get_data_dir().join("lib").join("caddy")
}

/// 下载并安装 Caddy
pub fn install_caddy(
    download_url: &str,
    progress_sender: Option<Sender<EnvInstallProgress>>,
) -> Result<(), Box<dyn std::error::Error>> {
    let temp_dir = get_temp_download_dir();
    let temp_file_path = temp_dir.join(CADDY_FILENAME);

    // 下载
    if let Some(tx) = &progress_sender {
        let _ = tx.send(EnvInstallProgress::Status(format!(
            "正在连接: {}",
            download_url
        )));
    }

    let client = reqwest::blocking::Client::builder()
        .timeout(Duration::from_secs(600))
        .build()?;

    let mut response = client.get(download_url).send()?;
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
                "开始下载 (文件大小: {:.1} MB)",
                size_mb
            )));
        } else {
            let _ = tx.send(EnvInstallProgress::Status("开始下载...".to_string()));
        }
    }

    {
        let mut dest = File::create(&temp_file_path)?;
        let mut buffer = [0; 8192];
        let mut downloaded_size: u64 = 0;
        let download_start = Instant::now();
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
                    let _ = tx.send(EnvInstallProgress::Progress(progress * 0.5));
                    let elapsed = last_speed_report.elapsed();
                    if elapsed.as_millis() >= 500 {
                        let total_elapsed = download_start.elapsed().as_secs_f32();
                        if total_elapsed > 0.0 {
                            let speed = (downloaded_size as f32) / total_elapsed;
                            let _ = tx.send(EnvInstallProgress::Speed(speed));
                        }
                        last_speed_report = Instant::now();
                    }
                }
            }
        }
    }

    if let Some(tx) = &progress_sender {
        let _ = tx.send(EnvInstallProgress::Status("下载完成，正在解压...".to_string()));
    }

    // 解压
    let caddy_dir = get_caddy_install_dir();

    if caddy_dir.exists() {
        fs::remove_dir_all(&caddy_dir)?;
    }
    fs::create_dir_all(&caddy_dir)?;

    let file = File::open(&temp_file_path)?;
    let mut archive = ZipArchive::new(file)?;
    let total_files = archive.len();
    let extract_start = Instant::now();
    let mut last_extract_report = extract_start;
    let mut extracted_bytes: u64 = 0;

    for i in 0..total_files {
        let mut file = archive.by_index(i)?;
        let outpath = match file.enclosed_name() {
            Some(path) => path.to_owned(),
            None => continue,
        };
        let file_size = file.size();

        let final_path = caddy_dir.join(&outpath);

        if file.name().ends_with('/') {
            fs::create_dir_all(&final_path)?;
        } else {
            if let Some(p) = final_path.parent() {
                if !p.exists() {
                    fs::create_dir_all(p)?;
                }
            }
            let mut outfile = File::create(&final_path)?;
            io::copy(&mut file, &mut outfile)?;
            extracted_bytes += file_size;
        }

        let elapsed = last_extract_report.elapsed();
        if i % 10 == 0 || elapsed.as_millis() >= 500 {
            if let Some(tx) = &progress_sender {
                let _ = tx.send(EnvInstallProgress::Progress(
                    0.5 + 0.5 * ((i as f32) / (total_files as f32)),
                ));
                let _ = tx.send(EnvInstallProgress::Status(format!(
                    "解压中: {}/{}",
                    i + 1,
                    total_files
                )));
                let total_elapsed = extract_start.elapsed().as_secs_f32();
                if total_elapsed > 0.0 {
                    let speed = (extracted_bytes as f32) / total_elapsed;
                    let _ = tx.send(EnvInstallProgress::Speed(speed));
                }
            }
            last_extract_report = Instant::now();
        }
    }

    let _ = fs::remove_file(&temp_file_path);

    // 验证安装：执行 caddy version
    let caddy_exe = caddy_dir.join("caddy_windows_amd64.exe");
    let actual_exe = if !caddy_exe.exists() {
        caddy_dir.join("caddy.exe")
    } else {
        caddy_exe
    };

    if actual_exe.exists() {
        // 重命名为 caddy.exe（统一名称）
        if actual_exe != caddy_dir.join("caddy.exe") {
            let target = caddy_dir.join("caddy.exe");
            let _ = fs::rename(&actual_exe, &target);
        }

        let exe_path = caddy_dir.join("caddy.exe");
        let output = std::process::Command::new(&exe_path)
            .arg("version")
            .output()
            .ok()
            .and_then(|o| {
                if o.status.success() {
                    let stdout = String::from_utf8_lossy(&o.stdout);
                    let ver = stdout.trim().to_string();
                    // "v2.11.4 ..." → "v2.11.4"
                    Some(ver.split_whitespace().next().unwrap_or("").to_string())
                } else {
                    None
                }
            });

        if let Some(ver) = output {
            if let Some(tx) = &progress_sender {
                let _ = tx.send(EnvInstallProgress::Version(ver));
                let _ = tx.send(EnvInstallProgress::Status(format!(
                    "安装完成 -> {}",
                    caddy_dir.display()
                )));
                let _ = tx.send(EnvInstallProgress::Finished);
            }
            return Ok(());
        }
    }

    let err = "安装验证失败：无法获取 caddy version".to_string();
    if let Some(tx) = &progress_sender {
        let _ = tx.send(EnvInstallProgress::Error(err.clone()));
    }
    Err(err.into())
}
