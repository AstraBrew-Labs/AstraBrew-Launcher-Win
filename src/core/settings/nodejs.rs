#![allow(dead_code)]

use std::env;
use std::fs::{self, File};
use std::io;
use std::path::PathBuf;
use zip::ZipArchive;

/// 获取 data 目录的根路径
/// 在开发环境下 (如 target/debug), 会回退到项目根目录
fn get_data_dir() -> io::Result<PathBuf> {
    let mut current_exe = env::current_exe()?;
    current_exe.pop(); // 去除执行文件名

    let path_str = current_exe.to_string_lossy();
    if path_str.contains("target\\debug") || path_str.contains("target\\release") {
        // 回退到项目根目录
        let mut root = current_exe.clone();
        root.pop(); // pop debug/release
        root.pop(); // pop target
        Ok(root)
    } else {
        Ok(current_exe)
    }
}

/// 下载并解压 Node.js 到 data/lib/nodejs 目录
pub fn download_and_install_nodejs() -> Result<(), Box<dyn std::error::Error>> {
    let node_os = "win";
    let node_arch = match env::consts::ARCH {
        "x86_64" => "x64",
        "aarch64" => "arm64",
        _ => "x64",
    };

    // 仅考虑 Windows 平台
    let ext = if cfg!(target_os = "windows") {
        "zip"
    } else {
        "zip"
    };

    let version = "v22.12.0";
    let filename = format!("node-{}-{}-{}.{}", version, node_os, node_arch, ext);

    // 五阶回退下载策略：npmmirror → 阿里云 → 清华镜像 → 华为镜像 → 直连
    let urls = vec![
        format!(
            "https://npmmirror.com/mirrors/node/{}/{}",
            version, filename
        ),
        format!(
            "https://mirrors.aliyun.com/nodejs-release/{}/{}",
            version, filename
        ),
        format!(
            "https://mirrors.tuna.tsinghua.edu.cn/nodejs-release/{}/{}",
            version, filename
        ),
        format!(
            "https://mirrors.huaweicloud.com/nodejs/{}/{}",
            version, filename
        ),
        format!("https://nodejs.org/dist/{}/{}", version, filename),
    ];

    let temp_dir = env::temp_dir();
    let temp_file_path = temp_dir.join(&filename);

    let mut downloaded = false;
    for url in urls {
        println!("尝试下载 Node.js: {}", url);
        // 使用 reqwest blocking 客户端进行下载
        if let Ok(mut response) = reqwest::blocking::get(&url) {
            if response.status().is_success() {
                let mut dest = File::create(&temp_file_path)?;
                if response.copy_to(&mut dest).is_ok() {
                    downloaded = true;
                    println!("下载成功: {}", url);
                    break;
                }
            }
        }
        println!("下载失败，尝试下一个源...");
    }

    if !downloaded {
        return Err("所有 Node.js 下载源均失败，请检查网络连接".into());
    }

    // 目标解压目录：运行目录下的 data/lib/nodejs
    let base_dir = get_data_dir()?;
    let nodejs_dir = base_dir.join("data").join("lib").join("nodejs");

    // 如果已存在，先清空
    if nodejs_dir.exists() {
        fs::remove_dir_all(&nodejs_dir)?;
    }
    fs::create_dir_all(&nodejs_dir)?;

    println!("开始解压到: {}", nodejs_dir.display());

    // 解压 ZIP 文件
    let file = File::open(&temp_file_path)?;
    let mut archive = ZipArchive::new(file)?;

    for i in 0..archive.len() {
        let mut file = archive.by_index(i)?;
        let outpath = match file.enclosed_name() {
            Some(path) => path.to_owned(),
            None => continue,
        };

        // Node.js 的压缩包通常包含一个顶层目录 (例如 node-v22.12.0-win-x64)
        // 我们将其去掉，直接将内容解压到 nodejs_dir
        let mut components = outpath.components();
        components.next(); // 跳过顶层目录
        let stripped_path = components.as_path();

        if stripped_path.as_os_str().is_empty() {
            continue;
        }

        let final_path = nodejs_dir.join(stripped_path);

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

    // 清理临时文件
    let _ = fs::remove_file(temp_file_path);
    println!("Node.js 安装完成");

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_get_data_dir() {
        let base_dir = get_data_dir().unwrap();
        println!("Base dir: {:?}", base_dir);
        assert!(base_dir.is_absolute(), "The returned path should be absolute");
    }
}
