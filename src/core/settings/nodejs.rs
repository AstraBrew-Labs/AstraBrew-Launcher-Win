use std::fs::{self, File};
use std::io::{self, Read, Write};
use std::path::PathBuf;
use std::sync::mpsc::Sender;
use std::time::{Duration, Instant};
use zip::ZipArchive;

use crate::EnvInstallProgress;

/// Node.js 镜像节点信息
#[derive(Debug, Clone)]
pub struct NodejsMirrorNode {
    pub name: String,
    pub url: String,
    pub latency_ms: Option<u64>,
    pub timed_out: bool,
    pub blocked: bool,
}

impl NodejsMirrorNode {
    pub fn new(name: &str, url: &str) -> Self {
        Self {
            name: name.to_string(),
            url: url.to_string(),
            latency_ms: None,
            timed_out: false,
            blocked: false,
        }
    }
}

/// Node.js 节点选择弹窗状态（与 Git 共用 mpsc 模式）
#[derive(Debug, Clone)]
pub enum NodejsNodeSelectMsg {
    LatencyResults(Vec<NodejsMirrorNode>),
    TestingProgress(String),
}

/// 获取所有 Node.js 镜像节点
pub fn get_nodejs_mirror_nodes() -> Vec<NodejsMirrorNode> {
    let filename = "node-v24.14.0-win-x64.zip";
    vec![
        NodejsMirrorNode::new(
            "官方",
            &format!("https://nodejs.org/download/release/v24.14.0/{}", filename),
        ),
        NodejsMirrorNode::new(
            "华为云",
            &format!("https://mirrors.huaweicloud.com/nodejs/v24.14.0/{}", filename),
        ),
        NodejsMirrorNode::new(
            "NPMMirror",
            &format!("https://registry.npmmirror.com/-/binary/node/v24.14.0/{}", filename),
        ),
        NodejsMirrorNode::new(
            "阿里云",
            &format!("https://mirrors.aliyun.com/nodejs-release/v24.14.0/{}", filename),
        ),
    ]
}

/// 后台线程中测试所有节点延迟
pub fn test_mirror_latency(tx: Sender<NodejsNodeSelectMsg>) {
    let mut nodes = get_nodejs_mirror_nodes();

    for i in 0..nodes.len() {
        let name = nodes[i].name.clone();
        let _ = tx.send(NodejsNodeSelectMsg::TestingProgress(
            format!("正在测速: {} ({}/{})", name, i + 1, nodes.len()),
        ));

        match test_url_latency(&nodes[i].url) {
            Ok(ms) => nodes[i].latency_ms = Some(ms),
            Err(status) => {
                if status == 403 || status == 404 {
                    nodes[i].blocked = true;
                } else {
                    nodes[i].timed_out = true;
                }
            }
        }
    }

    nodes.sort_by(|a, b| {
        let a_usable = a.latency_ms.is_some() && !a.blocked && !a.timed_out;
        let b_usable = b.latency_ms.is_some() && !b.blocked && !b.timed_out;
        match (a_usable, b_usable) {
            (true, true) => a.latency_ms.unwrap().cmp(&b.latency_ms.unwrap()),
            (true, false) => std::cmp::Ordering::Less,
            (false, true) => std::cmp::Ordering::Greater,
            (false, false) => match (a.blocked, b.blocked) {
                (true, false) => std::cmp::Ordering::Less,
                (false, true) => std::cmp::Ordering::Greater,
                _ => std::cmp::Ordering::Equal,
            },
        }
    });

    let _ = tx.send(NodejsNodeSelectMsg::LatencyResults(nodes));
}

/// 测试单个 URL 的延迟（HEAD 请求，5 秒超时）
fn test_url_latency(url: &str) -> Result<u64, u16> {
    let client = match reqwest::blocking::Client::builder()
        .timeout(Duration::from_secs(5))
        .build()
    {
        Ok(c) => c,
        Err(_) => return Err(0),
    };

    let start = Instant::now();
    let resp = match client.head(url).send() {
        Ok(r) => r,
        Err(_) => return Err(0),
    };
    let elapsed = start.elapsed().as_millis() as u64;

    let status = resp.status().as_u16();
    if resp.status().is_success() || resp.status().is_redirection() {
        Ok(elapsed)
    } else {
        Err(status)
    }
}

/// 获取 Node.js 临时下载目录
fn get_temp_download_dir() -> PathBuf {
    let dir = std::env::temp_dir().join("astrabrew-launcher");
    let _ = fs::create_dir_all(&dir);
    dir
}

/// 获取 Node.js 安装目录
fn get_nodejs_install_dir() -> PathBuf {
    crate::core::env::get_data_dir().join("lib").join("nodejs")
}

/// 使用指定 URL 下载并安装 Node.js
pub fn download_and_install_nodejs_from_url(
    url: &str,
    progress_sender: Option<Sender<EnvInstallProgress>>,
) -> Result<(), Box<dyn std::error::Error>> {
    let filename = "node-v24.14.0-win-x64.zip";

    if let Some(tx) = &progress_sender {
        let _ = tx.send(EnvInstallProgress::Status(format!("正在连接: {}", url)));
    }

    let temp_dir = get_temp_download_dir();
    let temp_file_path = temp_dir.join(filename);

    // 下载
    let client = reqwest::blocking::Client::builder()
        .timeout(Duration::from_secs(600))
        .build()?;

    let mut response = client.get(url).send()?;
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
            } else if let Some(tx) = &progress_sender {
                let mb = downloaded_size as f64 / 1048576.0;
                let _ = tx.send(EnvInstallProgress::Status(format!(
                    "下载中: {:.1} MB",
                    mb
                )));
            }
        }
    }

    if let Some(tx) = &progress_sender {
        let _ = tx.send(EnvInstallProgress::Status("下载完成，正在解压...".to_string()));
    }

    // 解压
    let nodejs_dir = get_nodejs_install_dir();

    if nodejs_dir.exists() {
        fs::remove_dir_all(&nodejs_dir)?;
    }
    fs::create_dir_all(&nodejs_dir)?;

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

        // 去除 ZIP 顶层目录
        let mut components = outpath.components();
        components.next();
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

    // 验证安装：执行 node --version
    let node_exe = nodejs_dir.join("node.exe");
    let version = if node_exe.exists() {
        let mut node_cmd = std::process::Command::new(&node_exe);
        crate::core::env::apply_no_window_to_command(&mut node_cmd);
        let output = node_cmd.arg("--version")
            .output()
            .ok()
            .and_then(|o| {
                if o.status.success() {
                    let stdout = String::from_utf8_lossy(&o.stdout);
                    // "v24.14.0" → "24.14.0" or keep as "v24.14.0"
                    Some(stdout.trim().to_string())
                } else {
                    None
                }
            });
        output
    } else {
        None
    };

    if let Some(ver) = version {
        if let Some(tx) = &progress_sender {
            let _ = tx.send(EnvInstallProgress::Version(ver));
            let _ = tx.send(EnvInstallProgress::Status(format!(
                "安装完成 -> {}",
                nodejs_dir.display()
            )));
            let _ = tx.send(EnvInstallProgress::Finished);
        }
        Ok(())
    } else {
        let err = "安装验证失败：无法执行 node --version".to_string();
        if let Some(tx) = &progress_sender {
            let _ = tx.send(EnvInstallProgress::Error(err.clone()));
        }
        Err(err.into())
    }
}

/// 兼容旧接口：自动选择最快节点下载安装
#[allow(dead_code)]
pub fn download_and_install_nodejs(
    progress_sender: Option<Sender<EnvInstallProgress>>,
) -> Result<(), Box<dyn std::error::Error>> {
    let mut nodes = get_nodejs_mirror_nodes();

    for node in &mut nodes {
        if let Some(tx) = &progress_sender {
            let _ = tx.send(EnvInstallProgress::Status(format!("测速: {}", node.name)));
        }
        match test_url_latency(&node.url) {
            Ok(ms) => node.latency_ms = Some(ms),
            Err(status) => {
                if status == 403 || status == 404 {
                    node.blocked = true;
                } else {
                    node.timed_out = true;
                }
            }
        }
    }

    nodes.sort_by(|a, b| {
        let a_usable = a.latency_ms.is_some() && !a.blocked && !a.timed_out;
        let b_usable = b.latency_ms.is_some() && !b.blocked && !b.timed_out;
        match (a_usable, b_usable) {
            (true, true) => a.latency_ms.unwrap().cmp(&b.latency_ms.unwrap()),
            (true, false) => std::cmp::Ordering::Less,
            (false, true) => std::cmp::Ordering::Greater,
            (false, false) => match (a.blocked, b.blocked) {
                (true, false) => std::cmp::Ordering::Less,
                (false, true) => std::cmp::Ordering::Greater,
                _ => std::cmp::Ordering::Equal,
            },
        }
    });

    let best = nodes.first().ok_or("没有可用的 Node.js 下载节点")?;
    if best.timed_out && best.blocked {
        return Err("所有 Node.js 下载节点均不可用，请检查网络".into());
    }

    download_and_install_nodejs_from_url(&best.url, progress_sender)
}
