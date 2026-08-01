//! GitHub Releases based updater for the Windows launcher.

#![allow(dead_code)]

use semver::Version;
use serde::Deserialize;
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::mpsc;
use std::time::Duration;

const RELEASE_REPOSITORY: &str = "AstraBrew-Labs/AstraBrew-Launcher-Win";
const UPDATE_PROXY_PREFIX: &str = "https://gh-proxy.org/";

fn releases_api_url() -> String {
    format!("https://api.github.com/repos/{RELEASE_REPOSITORY}/releases?per_page=20")
}

fn release_download_prefix() -> String {
    format!("https://github.com/{RELEASE_REPOSITORY}/releases/download/")
}

fn update_url_candidates(original_url: &str) -> [String; 2] {
    [
        format!("{UPDATE_PROXY_PREFIX}{original_url}"),
        original_url.to_string(),
    ]
}

#[derive(Debug, Clone)]
pub enum UpdateStatus {
    Checking,
    UpToDate,
    UpdateAvailable {
        version: String,
        notes: Option<String>,
        endpoint: String,
    },
    Downloading,
    Installed,
    Error(String),
}

#[derive(Clone, Debug, Deserialize)]
struct GithubRelease {
    tag_name: String,
    name: Option<String>,
    body: Option<String>,
    draft: bool,
    prerelease: bool,
    assets: Vec<GithubAsset>,
}

#[derive(Clone, Debug, Deserialize)]
struct GithubAsset {
    name: String,
    browser_download_url: String,
}

struct AvailableUpdate {
    version: Version,
    notes: Option<String>,
    endpoint: String,
}

pub fn start_check() -> mpsc::Receiver<UpdateStatus> {
    spawn_update_check()
}

pub fn check_update_manual() -> mpsc::Receiver<UpdateStatus> {
    spawn_update_check()
}

fn spawn_update_check() -> mpsc::Receiver<UpdateStatus> {
    let (tx, rx) = mpsc::channel();
    std::thread::spawn(move || {
        let _ = tx.send(UpdateStatus::Checking);
        match check_latest_release() {
            Ok(Some(update)) => {
                let _ = tx.send(UpdateStatus::UpdateAvailable {
                    version: update.version.to_string(),
                    notes: update.notes,
                    endpoint: update.endpoint,
                });
            }
            Ok(None) => {
                let _ = tx.send(UpdateStatus::UpToDate);
            }
            Err(error) => {
                let _ = tx.send(UpdateStatus::Error(error));
            }
        }
    });
    rx
}

fn check_latest_release() -> Result<Option<AvailableUpdate>, String> {
    let current = Version::parse(env!("CARGO_PKG_VERSION"))
        .map_err(|error| format!("当前版本号无效: {error}"))?;
    let beta_channel = cfg!(beta) || !current.pre.is_empty();
    let client = github_client()?;
    let releases = fetch_releases(&client)?;

    Ok(select_available_update(&releases, &current, beta_channel))
}

fn fetch_releases(client: &reqwest::blocking::Client) -> Result<Vec<GithubRelease>, String> {
    let mut errors = Vec::new();
    for (index, endpoint) in update_url_candidates(&releases_api_url())
        .into_iter()
        .enumerate()
    {
        let source = if index == 0 { "GitHub 加速" } else { "GitHub 直连" };
        let result = client
            .get(endpoint)
            .header(reqwest::header::ACCEPT, "application/vnd.github+json")
            .send()
            .and_then(reqwest::blocking::Response::error_for_status)
            .and_then(reqwest::blocking::Response::json::<Vec<GithubRelease>>);

        match result {
            Ok(releases) => return Ok(releases),
            Err(error) => errors.push(format!("{source}: {error}")),
        }
    }

    Err(format!(
        "无法获取 GitHub Releases（加速与直连均失败）: {}",
        errors.join("; ")
    ))
}

fn select_available_update(
    releases: &[GithubRelease],
    current: &Version,
    beta_channel: bool,
) -> Option<AvailableUpdate> {
    releases
        .iter()
        .filter(|release| !release.draft)
        .filter(|release| beta_channel || !is_beta_release(release))
        .filter_map(|release| {
            let version = release_version(release)?;
            if version <= *current {
                return None;
            }
            let asset = release.assets.iter().find(|asset| {
                let name = asset.name.to_ascii_lowercase();
                name.ends_with(".exe") && (name.ends_with("_x64-setup.exe") || name.contains("setup"))
            })?;
            Some(AvailableUpdate {
                version,
                notes: release
                    .body
                    .clone()
                    .filter(|body| !body.trim().is_empty())
                    .or_else(|| release.name.clone()),
                endpoint: asset.browser_download_url.clone(),
            })
        })
        .max_by(|left, right| left.version.cmp(&right.version))
}

fn is_beta_release(release: &GithubRelease) -> bool {
    release.prerelease || release.tag_name.to_ascii_lowercase().starts_with("beta-v")
}

fn release_version(release: &GithubRelease) -> Option<Version> {
    let tag = release.tag_name.trim();
    let (raw_version, beta) = if let Some(version) = tag.strip_prefix("beta-v") {
        (version, true)
    } else if let Some(version) = tag.strip_prefix('v') {
        (version, release.prerelease)
    } else {
        return None;
    };

    let normalized = if beta && !raw_version.contains('-') {
        format!("{raw_version}-beta")
    } else {
        raw_version.to_string()
    };
    Version::parse(&normalized).ok()
}

pub fn do_install(endpoint: String) -> mpsc::Receiver<UpdateStatus> {
    let (tx, rx) = mpsc::channel();
    std::thread::spawn(move || {
        let _ = tx.send(UpdateStatus::Downloading);
        match download_and_launch_installer(&endpoint) {
            Ok(()) => {
                let _ = tx.send(UpdateStatus::Installed);
            }
            Err(error) => {
                let _ = tx.send(UpdateStatus::Error(error));
            }
        }
    });
    rx
}

fn download_and_launch_installer(endpoint: &str) -> Result<(), String> {
    if !endpoint.starts_with(&release_download_prefix()) {
        return Err("更新地址不是受信任的 AstraBrew GitHub Release".into());
    }

    let client = github_client()?;
    let installer_path = update_installer_path();
    if let Some(parent) = installer_path.parent() {
        std::fs::create_dir_all(parent)
            .map_err(|error| format!("无法创建更新目录: {error}"))?;
    }

    let temporary_path = installer_path.with_extension("exe.download");
    let mut errors = Vec::new();
    let mut downloaded = false;
    for (index, candidate) in update_url_candidates(endpoint).into_iter().enumerate() {
        let source = if index == 0 { "GitHub 加速" } else { "GitHub 直连" };
        match download_installer_candidate(&client, &candidate, &temporary_path) {
            Ok(()) => {
                downloaded = true;
                break;
            }
            Err(error) => {
                let _ = std::fs::remove_file(&temporary_path);
                errors.push(format!("{source}: {error}"));
            }
        }
    }
    if !downloaded {
        return Err(format!(
            "下载安装包失败（加速与直连均失败）: {}",
            errors.join("; ")
        ));
    }

    if installer_path.exists() {
        let _ = std::fs::remove_file(&installer_path);
    }
    std::fs::rename(&temporary_path, &installer_path)
        .map_err(|error| format!("无法完成安装包下载: {error}"))?;

    std::process::Command::new(&installer_path)
        .spawn()
        .map_err(|error| format!("无法启动更新安装程序: {error}"))?;
    Ok(())
}

fn download_installer_candidate(
    client: &reqwest::blocking::Client,
    endpoint: &str,
    temporary_path: &Path,
) -> Result<(), String> {
    let mut response = client
        .get(endpoint)
        .send()
        .and_then(reqwest::blocking::Response::error_for_status)
        .map_err(|error| error.to_string())?;
    let mut file = std::fs::File::create(temporary_path)
        .map_err(|error| format!("无法创建安装包文件: {error}"))?;
    std::io::copy(&mut response, &mut file)
        .map_err(|error| format!("无法保存安装包: {error}"))?;
    file.flush()
        .map_err(|error| format!("无法写入安装包: {error}"))?;
    drop(file);

    let mut file = std::fs::File::open(temporary_path)
        .map_err(|error| format!("无法校验安装包: {error}"))?;
    let mut magic = [0_u8; 2];
    file.read_exact(&mut magic)
        .map_err(|error| format!("安装包内容不完整: {error}"))?;
    if &magic != b"MZ" {
        return Err("下载内容不是有效的 Windows 安装程序".to_string());
    }

    Ok(())
}

fn github_client() -> Result<reqwest::blocking::Client, String> {
    reqwest::blocking::Client::builder()
        .user_agent(format!(
            "AstraBrew-Launcher-Win/{}",
            env!("CARGO_PKG_VERSION")
        ))
        .connect_timeout(Duration::from_secs(10))
        .timeout(Duration::from_secs(60))
        .build()
        .map_err(|error| format!("无法初始化更新客户端: {error}"))
}

fn update_installer_path() -> PathBuf {
    std::env::temp_dir()
        .join("AstraBrew Launcher")
        .join("updates")
        .join("AstraBrew-Launcher-update.exe")
}

#[cfg(test)]
mod tests {
    use super::*;

    fn release(tag: &str, prerelease: bool, asset: &str) -> GithubRelease {
        GithubRelease {
            tag_name: tag.into(),
            name: Some(tag.into()),
            body: Some(format!("notes for {tag}")),
            draft: false,
            prerelease,
            assets: vec![GithubAsset {
                name: asset.into(),
                browser_download_url: format!("{}{tag}/{asset}", release_download_prefix()),
            }],
        }
    }

    #[test]
    fn updater_urls_target_the_windows_repository() {
        assert_eq!(
            releases_api_url(),
            "https://api.github.com/repos/AstraBrew-Labs/AstraBrew-Launcher-Win/releases?per_page=20"
        );
        assert_eq!(
            release_download_prefix(),
            "https://github.com/AstraBrew-Labs/AstraBrew-Launcher-Win/releases/download/"
        );
    }

    #[test]
    fn updater_uses_gh_proxy_before_the_original_url() {
        let original_api = releases_api_url();
        assert_eq!(
            update_url_candidates(&original_api),
            [
                format!("https://gh-proxy.org/{original_api}"),
                original_api,
            ]
        );

        let original_download = format!("{}v1.2.3/setup.exe", release_download_prefix());
        assert_eq!(
            update_url_candidates(&original_download),
            [
                format!("https://gh-proxy.org/{original_download}"),
                original_download,
            ]
        );
    }

    #[test]
    fn parses_stable_and_beta_tags() {
        assert_eq!(
            release_version(&release("v1.2.3", false, "setup.exe")).unwrap(),
            Version::parse("1.2.3").unwrap()
        );
        assert_eq!(
            release_version(&release("beta-v1.3.0", false, "setup.exe")).unwrap(),
            Version::parse("1.3.0-beta").unwrap()
        );
    }

    #[test]
    fn stable_channel_ignores_beta_releases() {
        let releases = vec![
            release("beta-v1.2.0", false, "AstraBrew_1.2.0_x64-setup.exe"),
            release("v1.1.0", false, "AstraBrew_1.1.0_x64-setup.exe"),
        ];
        let update = select_available_update(
            &releases,
            &Version::parse("1.0.0").unwrap(),
            false,
        )
        .unwrap();
        assert_eq!(update.version, Version::parse("1.1.0").unwrap());
    }

    #[test]
    fn beta_channel_accepts_new_beta_and_stable_releases() {
        let releases = vec![
            release("beta-v1.2.0", false, "AstraBrew_1.2.0_x64-setup.exe"),
            release("v1.1.0", false, "AstraBrew_1.1.0_x64-setup.exe"),
        ];
        let update = select_available_update(
            &releases,
            &Version::parse("1.1.0-beta").unwrap(),
            true,
        )
        .unwrap();
        assert_eq!(update.version, Version::parse("1.2.0-beta").unwrap());
    }

    #[test]
    fn ignores_releases_without_windows_installer() {
        let releases = vec![release("v2.0.0", false, "source.zip")];
        assert!(
            select_available_update(&releases, &Version::parse("1.0.0").unwrap(), false)
                .is_none()
        );
    }

    #[test]
    fn release_workflow_has_valid_yaml_syntax() {
        let workflow = include_str!("../../.github/workflows/release.yml");
        serde_yaml::from_str::<serde_yaml::Value>(workflow).unwrap();
    }
}
