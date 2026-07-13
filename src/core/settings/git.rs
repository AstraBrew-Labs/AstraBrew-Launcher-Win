use std::fs::{self, File};
use std::io::{self, Read, Write};
use std::path::PathBuf;
use std::sync::mpsc::Sender;
use std::time::{Duration, Instant};
use zip::ZipArchive;

use crate::EnvInstallProgress;

/// Git 镜像节点信息
#[derive(Debug, Clone)]
pub struct GitMirrorNode {
    /// 节点名称（显示用）
    pub name: String,
    /// 下载 URL
    pub url: String,
    /// 实测延迟（毫秒），None 表示未测试或不可用
    pub latency_ms: Option<u64>,
    /// 延迟测试是否超时（连接失败）
    pub timed_out: bool,
    /// 是否被节点阻止访问（HTTP 403/404）
    pub blocked: bool,
}

impl GitMirrorNode {
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

/// Git 节点选择弹窗状态
#[derive(Debug, Clone)]
pub enum GitNodeSelectMsg {
    /// 延迟测试完成，返回排序后的节点列表
    LatencyResults(Vec<GitMirrorNode>),
    /// 延迟测试中的状态更新
    TestingProgress(String),
}

/// 获取所有 Git 镜像节点（未测试延迟）
pub fn get_git_mirror_nodes() -> Vec<GitMirrorNode> {
    let filename = "MinGit-2.55.0.2-64-bit.zip";
    vec![
        GitMirrorNode::new(
            "Github (直连)",
            &format!("https://github.com/git-for-windows/git/releases/download/v2.55.0.windows.2/{}", filename),
        ),
        GitMirrorNode::new(
            "华为云",
            &format!("https://mirrors.huaweicloud.com/git-for-windows/v2.55.0.windows.2/{}", filename),
        ),
        GitMirrorNode::new(
            "NPMMirror",
            &format!("https://registry.npmmirror.com/-/binary/git-for-windows/v2.55.0.windows.2/{}", filename),
        ),
        GitMirrorNode::new(
            "清华大学",
            &format!("https://mirrors.tuna.tsinghua.edu.cn/github-release/git-for-windows/git/LatestRelease/{}", filename),
        ),
    ]
}

/// 后台线程中测试所有节点的延迟，结果通过 channel 发送
pub fn test_mirror_latency(tx: Sender<GitNodeSelectMsg>) {
    let mut nodes = get_git_mirror_nodes();

    for i in 0..nodes.len() {
        let name = nodes[i].name.clone();
        let _ = tx.send(GitNodeSelectMsg::TestingProgress(
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

    // 排序：可用节点按延迟升序 → 被阻止节点 → 超时节点
    nodes.sort_by(|a, b| {
        let a_usable = a.latency_ms.is_some() && !a.blocked && !a.timed_out;
        let b_usable = b.latency_ms.is_some() && !b.blocked && !b.timed_out;
        match (a_usable, b_usable) {
            (true, true) => a.latency_ms.unwrap().cmp(&b.latency_ms.unwrap()),
            (true, false) => std::cmp::Ordering::Less,
            (false, true) => std::cmp::Ordering::Greater,
            (false, false) => {
                // blocked 排在 timed_out 前面
                match (a.blocked, b.blocked) {
                    (true, false) => std::cmp::Ordering::Less,
                    (false, true) => std::cmp::Ordering::Greater,
                    _ => std::cmp::Ordering::Equal,
                }
            }
        }
    });

    let _ = tx.send(GitNodeSelectMsg::LatencyResults(nodes));
}

/// 测试单个 URL 的延迟（HEAD 请求，5 秒超时）
/// 成功返回毫秒，403/404 返回对应状态码，其他错误返回 0
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

/// 获取内置 Git 安装目录：`%APPDATA%/AstraBrew Launcher/lib/git/`
fn get_git_install_dir() -> PathBuf {
    crate::core::env::get_data_dir().join("lib").join("git")
}

/// 获取 Git 下载临时目录：`%TEMP%/astrabrew-launcher/`
fn get_temp_download_dir() -> PathBuf {
    let dir = std::env::temp_dir().join("astrabrew-launcher");
    let _ = fs::create_dir_all(&dir);
    dir
}

/// 使用指定 URL 下载并安装 Git 到内置目录
pub fn download_and_install_git_from_url(
    url: &str,
    progress_sender: Option<Sender<EnvInstallProgress>>,
) -> Result<(), Box<dyn std::error::Error>> {
    let filename = "MinGit-2.55.0.2-64-bit.zip";

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
                    // 每 ~500ms 报告速度（全局平均速度，首次也需等够时间）
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
    let git_dir = get_git_install_dir();

    if git_dir.exists() {
        fs::remove_dir_all(&git_dir)?;
    }
    fs::create_dir_all(&git_dir)?;

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
            extracted_bytes += file_size;
        }

        // 每 10 个文件或每 ~500ms 报告一次
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
                // 解压速度
                let total_elapsed = extract_start.elapsed().as_secs_f32();
                if total_elapsed > 0.0 {
                    let speed = (extracted_bytes as f32) / total_elapsed;
                    let _ = tx.send(EnvInstallProgress::Speed(speed));
                }
            }
            last_extract_report = Instant::now();
        }
    }

    // 清理临时文件
    let _ = fs::remove_file(&temp_file_path);

    // 验证安装：执行 git --version
    let git_exe = git_dir
        .join("cmd")
        .join("git.exe");
    let version = if git_exe.exists() {
        let mut git_cmd = std::process::Command::new(&git_exe);
        crate::core::env::apply_no_window_to_command(&mut git_cmd);
        let result = git_cmd.arg("--version")
            .output()
            .ok()
            .and_then(|o| {
                if o.status.success() {
                    let stdout = String::from_utf8_lossy(&o.stdout);
                    // 解析 "git version 2.55.0.windows.2" → "2.55.0.2"
                    stdout
                        .trim()
                        .strip_prefix("git version ")
                        .map(|v| v.to_string())
                } else {
                    None
                }
            });
        let _ = fs::remove_file(&temp_file_path);
        result
    } else {
        let _ = fs::remove_file(&temp_file_path);
        None
    };

    if let Some(ver) = version {
        if let Some(tx) = &progress_sender {
            let _ = tx.send(EnvInstallProgress::Version(ver));
            let _ = tx.send(EnvInstallProgress::Status(format!(
                "安装完成 -> {}",
                git_dir.display()
            )));
            let _ = tx.send(EnvInstallProgress::Finished);
        }
        Ok(())
    } else {
        let err = "安装验证失败：无法执行 git --version".to_string();
        if let Some(tx) = &progress_sender {
            let _ = tx.send(EnvInstallProgress::Error(err.clone()));
        }
        Err(err.into())
    }
}

/// 兼容旧接口：自动选择最快节点下载安装
#[allow(dead_code)]
pub fn download_and_install_git(
    progress_sender: Option<Sender<EnvInstallProgress>>,
) -> Result<(), Box<dyn std::error::Error>> {
    // 在单线程中测试延迟、排序、选最快
    let mut nodes = get_git_mirror_nodes();

    for node in &mut nodes {
        if let Some(tx) = &progress_sender {
            let _ = tx.send(EnvInstallProgress::Status(format!(
                "测速: {}",
                node.name
            )));
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

    let best = nodes
        .first()
        .ok_or("没有可用的 Git 下载节点")?;

    if best.timed_out && best.latency_ms.is_none() {
        return Err("所有 Git 下载节点均超时，请检查网络".into());
    }

    download_and_install_git_from_url(&best.url, progress_sender)
}
