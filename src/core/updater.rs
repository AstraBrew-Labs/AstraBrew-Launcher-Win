//! GitHub Releases based updater for the Windows launcher.

#![allow(dead_code)]

use semver::Version;
use serde::Deserialize;
use std::io::Write;
use std::path::PathBuf;
use std::sync::mpsc;
use std::time::Duration;

const RELEASES_API: &str =
    "https://api.github.com/repos/AstraBrew-Labs/AstraBrew-Launcher-Win/releases?per_page=20";
const RELEASE_DOWNLOAD_PREFIX: &str =
    "https://github.com/AstraBrew-Labs/AstraBrew-Launcher-Win/releases/download/";

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
    let releases = client
        .get(RELEASES_API)
        .header(reqwest::header::ACCEPT, "application/vnd.github+json")
        .send()
        .and_then(reqwest::blocking::Response::error_for_status)
        .map_err(|error| format!("无法获取 GitHub Releases: {error}"))?
        .json::<Vec<GithubRelease>>()
        .map_err(|error| format!("无法解析 GitHub Releases: {error}"))?;

    Ok(select_available_update(&releases, &current, beta_channel))
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
    if !endpoint.starts_with(RELEASE_DOWNLOAD_PREFIX) {
        return Err("更新地址不是受信任的 AstraBrew GitHub Release".into());
    }

    let client = github_client()?;
    let mut response = client
        .get(endpoint)
        .send()
        .and_then(reqwest::blocking::Response::error_for_status)
        .map_err(|error| format!("下载安装包失败: {error}"))?;
    let installer_path = update_installer_path();
    if let Some(parent) = installer_path.parent() {
        std::fs::create_dir_all(parent)
            .map_err(|error| format!("无法创建更新目录: {error}"))?;
    }

    let temporary_path = installer_path.with_extension("exe.download");
    let mut file = std::fs::File::create(&temporary_path)
        .map_err(|error| format!("无法创建安装包文件: {error}"))?;
    std::io::copy(&mut response, &mut file)
        .map_err(|error| format!("无法保存安装包: {error}"))?;
    file.flush()
        .map_err(|error| format!("无法写入安装包: {error}"))?;
    drop(file);
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
                browser_download_url: format!("{RELEASE_DOWNLOAD_PREFIX}{tag}/{asset}"),
            }],
        }
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
