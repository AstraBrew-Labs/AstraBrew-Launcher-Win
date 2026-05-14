#![allow(dead_code)]

use std::env;
use std::fs::{self, File};
use std::io::{self, Read, Write};
use std::path::PathBuf;
use std::sync::mpsc::Sender;
use zip::ZipArchive;

use crate::EnvInstallProgress;

/// 获取 data 目录的根路径
fn get_data_dir() -> io::Result<PathBuf> {
    let mut current_exe = env::current_exe()?;
    current_exe.pop(); // 去除执行文件名

    let path_str = current_exe.to_string_lossy();
    if path_str.contains("target\\debug") || path_str.contains("target\\release") {
        let mut root = current_exe.clone();
        root.pop(); // pop debug/release
        root.pop(); // pop target
        Ok(root)
    } else {
        Ok(current_exe)
    }
}

/// 下载并解压 Git 到 data/lib/Git 目录
pub fn download_and_install_git(progress_sender: Option<Sender<EnvInstallProgress>>) -> Result<(), Box<dyn std::error::Error>> {
    let filename = "MinGit-2.53.0.2-64-bit.zip";
    let urls = vec![
        format!("https://registry.npmmirror.com/-/binary/git-for-windows/v2.53.0.windows.2/{}", filename),
        format!("https://mirrors.tuna.tsinghua.edu.cn/github-release/git-for-windows/git/Git%20for%20Windows%202.53.0%282%29/{}", filename),
        format!("https://mirrors.huaweicloud.com/git-for-windows/v2.53.0.windows.2/{}", filename),
        format!("https://github.moeyy.xyz/https://github.com/git-for-windows/git/releases/download/v2.53.0.windows.2/{}", filename), // 加速地址示例
        format!("https://github.com/git-for-windows/git/releases/download/v2.53.0.windows.2/{}", filename),
    ];

    let temp_dir = env::temp_dir();
    let temp_file_path = temp_dir.join(&filename);

    let mut downloaded = false;
    for url in urls {
        if let Some(tx) = &progress_sender {
            let _ = tx.send(EnvInstallProgress::Status(format!("Connecting: {}", url)));
        }

        if let Ok(mut response) = reqwest::blocking::get(&url) {
            if response.status().is_success() {
                let total_size = response.content_length().unwrap_or(0);
                let mut dest = File::create(&temp_file_path)?;
                let mut buffer = [0; 8192];
                let mut downloaded_size = 0;

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
                            let _ = tx.send(EnvInstallProgress::Progress(progress * 0.5)); // First 50% is download
                        }
                    }
                }
                
                downloaded = true;
                break;
            }
        }
    }

    if !downloaded {
        let err_msg = "所有 Git 下载源均失败，请检查网络连接".to_string();
        if let Some(tx) = &progress_sender {
            let _ = tx.send(EnvInstallProgress::Error(err_msg.clone()));
        }
        return Err(err_msg.into());
    }

    let base_dir = get_data_dir()?;
    let git_dir = base_dir.join("data").join("lib").join("Git");

    if git_dir.exists() {
        fs::remove_dir_all(&git_dir)?;
    }
    fs::create_dir_all(&git_dir)?;

    let file = File::open(&temp_file_path)?;
    let mut archive = ZipArchive::new(file)?;
    let total_files = archive.len();

    for i in 0..total_files {
        if i % 10 == 0 {
            if let Some(tx) = &progress_sender {
                let _ = tx.send(EnvInstallProgress::Progress(0.5 + 0.5 * ((i as f32) / (total_files as f32))));
            }
        }

        let mut file = archive.by_index(i)?;
        let outpath = match file.enclosed_name() {
            Some(path) => path.to_owned(),
            None => continue,
        };

        // Note: MinGit zip does not have a top-level directory usually, but we extract it directly into data/lib/Git.
        // We will just extract as is.
        let final_path = git_dir.join(outpath);

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
        }
    }

    let _ = fs::remove_file(temp_file_path);
    if let Some(tx) = &progress_sender {
        let _ = tx.send(EnvInstallProgress::Finished);
    }

    Ok(())
}
