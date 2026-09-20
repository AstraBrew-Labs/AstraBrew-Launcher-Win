//! SillyTavern 扩展扫描、安装与本地文件管理。
//!
//! 页面层只负责收集用户意图，本模块统一执行路径校验、Git 命令和 ZIP 解压，
//! 防止界面状态与磁盘真实状态脱节。

use crate::lang::tf;
use std::ffi::OsStr;
use std::fmt;
use std::fs::{self, File};
use std::io::{BufRead, BufReader, Read, Write};
use std::path::{Component, Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::Sender;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use serde::{Deserialize, Serialize};

/// 扩展清单中启动器关心的稳定字段。
#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize, Serialize)]
pub struct ExtensionManifest {
    #[serde(default)]
    pub display_name: String,
    #[serde(rename = "homePage", alias = "homepage", default)]
    pub home_page: String,
    #[serde(default)]
    pub version: String,
    #[serde(default)]
    pub author: String,
    #[serde(default)]
    pub auto_update: Option<bool>,
    #[serde(default)]
    pub minimum_client_version: String,
}

/// 扩展由酒馆内置还是由用户安装。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ExtensionKind {
    ThirdParty,
    System,
}

/// 当前扩展的 Git 元数据状态。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum GitHealth {
    Ready,
    Repairable { remote_url: String },
    Unsupported,
}

/// 从磁盘扫描得到的扩展真实状态。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ExtensionInfo {
    pub id: String,
    pub path: PathBuf,
    pub manifest: ExtensionManifest,
    pub kind: ExtensionKind,
    pub enabled: bool,
    pub modified_at: u64,
    pub manifest_error: Option<String>,
    pub git_health: GitHealth,
}

/// Git 仓库分支探测结果。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GitBranchCatalog {
    pub branches: Vec<String>,
    pub selected: String,
}

/// 离线包校验结果。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct OfflinePackageInspection {
    pub path: PathBuf,
    pub file_name: String,
    pub extension_id: Option<String>,
    pub valid: bool,
    pub error: Option<ExtensionError>,
}

/// GitHub 下载加速配置，仅对 HTTPS GitHub 地址生效。
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct GithubProxyConfig {
    pub enabled: bool,
    pub base_url: String,
}

/// Git 安装请求。
#[derive(Debug, Clone)]
pub struct GitInstallRequest {
    pub instance_path: PathBuf,
    pub repository_url: String,
    pub branch: String,
    pub overwrite: bool,
    pub proxy: GithubProxyConfig,
}

/// 离线安装请求。
#[derive(Debug, Clone)]
pub struct OfflineInstallRequest {
    pub instance_path: PathBuf,
    pub packages: Vec<OfflinePackageInspection>,
    pub overwrite: bool,
}

/// 后台操作完成后供页面展示的结果。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum OperationSuccess {
    Installed(Vec<String>),
    Enabled { name: String, enabled: bool },
    Deleted(String),
    GitRepaired(String),
}

/// 扩展后台线程发送给应用层的事件。
#[derive(Debug, Clone)]
pub enum ExtensionEvent {
    ScanFinished(Result<Vec<ExtensionInfo>, ExtensionError>),
    BranchesFinished(Result<GitBranchCatalog, ExtensionError>),
    OfflineInspected(Vec<OfflinePackageInspection>),
    Log(String),
    OperationFinished(Result<OperationSuccess, ExtensionError>),
}

impl ExtensionEvent {
    /// 最终事件到达后可以释放当前任务通道。
    pub const fn is_terminal(&self) -> bool {
        matches!(
            self,
            Self::ScanFinished(_)
                | Self::BranchesFinished(_)
                | Self::OfflineInspected(_)
                | Self::OperationFinished(_)
        )
    }
}

/// 可翻译的扩展错误；detail 保存系统或 Git 返回的技术细节。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ExtensionError {
    pub key: &'static str,
    pub detail: String,
}

impl ExtensionError {
    pub fn new(key: &'static str, detail: impl Into<String>) -> Self {
        Self {
            key,
            detail: detail.into(),
        }
    }
}

impl fmt::Display for ExtensionError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        if self.detail.is_empty() {
            formatter.write_str(self.key)
        } else {
            write!(formatter, "{}: {}", self.key, self.detail)
        }
    }
}

impl std::error::Error for ExtensionError {}

/// 扫描当前实例的官方扩展和第三方扩展。
pub fn scan_extensions(instance_path: &Path) -> Result<Vec<ExtensionInfo>, ExtensionError> {
    let official_root = official_root(instance_path);
    if !official_root.is_dir() {
        return Err(ExtensionError::new(
            "extensions.error.root_missing",
            official_root.display().to_string(),
        ));
    }

    let mut extensions = Vec::new();
    scan_directory(
        &third_party_root(instance_path),
        ExtensionKind::ThirdParty,
        &mut extensions,
    )?;
    scan_directory(&official_root, ExtensionKind::System, &mut extensions)?;

    extensions.retain(|extension| extension.id != "third-party");
    extensions.sort_by(|left, right| {
        kind_rank(left.kind)
            .cmp(&kind_rank(right.kind))
            .then_with(|| right.modified_at.cmp(&left.modified_at))
            .then_with(|| left.id.cmp(&right.id))
    });
    Ok(extensions)
}

fn scan_directory(
    directory: &Path,
    kind: ExtensionKind,
    output: &mut Vec<ExtensionInfo>,
) -> Result<(), ExtensionError> {
    if !directory.exists() {
        return Ok(());
    }
    let entries = fs::read_dir(directory)
        .map_err(|error| ExtensionError::new("extensions.error.scan_failed", error.to_string()))?;
    for entry in entries.flatten() {
        let file_name = entry.file_name();
        let file_name = file_name.to_string_lossy();
        if file_name.starts_with('.') && file_name.contains(".astrabrew-") {
            continue;
        }
        let path = entry.path();
        let Ok(metadata) = fs::symlink_metadata(&path) else {
            continue;
        };
        if metadata.file_type().is_symlink() || !metadata.is_dir() {
            continue;
        }
        if let Some(extension) = parse_extension(&path, kind) {
            output.push(extension);
        }
    }
    Ok(())
}

fn parse_extension(directory: &Path, kind: ExtensionKind) -> Option<ExtensionInfo> {
    let enabled_manifest = directory.join("manifest.json");
    let disabled_manifest = directory.join("manifest.json.disable");
    let (manifest_path, enabled) = if enabled_manifest.is_file() {
        (enabled_manifest, true)
    } else if disabled_manifest.is_file() {
        (disabled_manifest, false)
    } else {
        return None;
    };

    let id = directory.file_name()?.to_string_lossy().into_owned();
    let (mut manifest, manifest_error) = match fs::read_to_string(&manifest_path) {
        Ok(content) => match serde_json::from_str::<ExtensionManifest>(&content) {
            Ok(manifest) => (manifest, None),
            Err(error) => (ExtensionManifest::default(), Some(error.to_string())),
        },
        Err(error) => (ExtensionManifest::default(), Some(error.to_string())),
    };
    if manifest.display_name.trim().is_empty() {
        manifest.display_name = id.clone();
    }

    let modified_at = metadata_timestamp(directory);
    let git_health = if kind == ExtensionKind::System {
        GitHealth::Unsupported
    } else if directory.join(".git").exists() {
        GitHealth::Ready
    } else if is_github_repository(&manifest.home_page) {
        GitHealth::Repairable {
            remote_url: manifest.home_page.clone(),
        }
    } else {
        GitHealth::Unsupported
    };

    Some(ExtensionInfo {
        id,
        path: directory.to_path_buf(),
        manifest,
        kind,
        enabled,
        modified_at,
        manifest_error,
        git_health,
    })
}

/// 使用 git ls-remote 获取默认分支和所有远端分支。
pub fn fetch_git_branches(
    repository_url: &str,
    proxy: &GithubProxyConfig,
    cancel: &AtomicBool,
) -> Result<GitBranchCatalog, ExtensionError> {
    validate_git_url(repository_url)?;
    let urls = candidate_urls(repository_url, proxy);
    let mut last_error: Option<ExtensionError> = None;
    for url in urls {
        if cancel.load(Ordering::Relaxed) {
            return Err(ExtensionError::new("extensions.error.cancelled", ""));
        }
        match fetch_branches_once(&url, cancel) {
            Ok(catalog) => return Ok(catalog),
            Err(error) => last_error = Some(error),
        }
    }
    Err(last_error
        .unwrap_or_else(|| ExtensionError::new("extensions.error.branch_fetch_failed", "")))
}

/// 带超时的 Git 分支探测，避免网络异常时永久阻塞 UI 后台任务。
fn fetch_branches_once(
    repository_url: &str,
    cancel: &AtomicBool,
) -> Result<GitBranchCatalog, ExtensionError> {
    const DETECT_TIMEOUT: Duration = Duration::from_secs(15);
    let mut command = Command::new("git");
    crate::core::env::apply_no_window_to_command(&mut command);
    let mut child = command
        .args([
            "ls-remote",
            "--symref",
            repository_url,
            "HEAD",
            "refs/heads/*",
        ])
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|error| {
            ExtensionError::new("extensions.error.git_unavailable", error.to_string())
        })?;
    let started = Instant::now();
    loop {
        if cancel.load(Ordering::Relaxed) {
            let _ = child.kill();
            let _ = child.wait_with_output();
            return Err(ExtensionError::new("extensions.error.cancelled", ""));
        }
        match child.try_wait() {
            Ok(Some(status)) => {
                let output = child.wait_with_output().map_err(|error| {
                    ExtensionError::new("extensions.error.branch_fetch_failed", error.to_string())
                })?;
                if !status.success() {
                    return Err(ExtensionError::new(
                        "extensions.error.branch_fetch_failed",
                        String::from_utf8_lossy(&output.stderr).trim().to_owned(),
                    ));
                }
                return parse_branch_output(repository_url, &output.stdout);
            }
            Ok(None) if started.elapsed() >= DETECT_TIMEOUT => {
                let _ = child.kill();
                let _ = child.wait_with_output();
                return Err(ExtensionError::new(
                    "extensions.error.branch_fetch_timeout",
                    tf("extensions.timeout_seconds", &[("seconds", &DETECT_TIMEOUT.as_secs())]),
                ));
            }
            Ok(None) => std::thread::sleep(Duration::from_millis(50)),
            Err(error) => {
                let _ = child.kill();
                let _ = child.wait();
                return Err(ExtensionError::new(
                    "extensions.error.branch_fetch_failed",
                    error.to_string(),
                ));
            }
        }
    }
}

fn parse_branch_output(
    repository_url: &str,
    bytes: &[u8],
) -> Result<GitBranchCatalog, ExtensionError> {
    let stdout = String::from_utf8_lossy(bytes);
    let mut default_branch = None;
    let mut branches = Vec::new();
    for line in stdout.lines() {
        if let Some(reference) = line.strip_prefix("ref: refs/heads/")
            && let Some((branch, _)) = reference.split_once('\t')
        {
            default_branch = Some(branch.to_owned());
        }
        if let Some((_, reference)) = line.split_once('\t')
            && let Some(branch) = reference.strip_prefix("refs/heads/")
            && !branches.iter().any(|item| item == branch)
        {
            branches.push(branch.to_owned());
        }
    }
    branches.sort();
    if branches.is_empty() {
        return Err(ExtensionError::new(
            "extensions.error.no_branches",
            repository_url,
        ));
    }
    let selected = default_branch
        .filter(|branch| branches.contains(branch))
        .or_else(|| {
            branches
                .iter()
                .find(|branch| branch.as_str() == "main")
                .cloned()
        })
        .or_else(|| {
            branches
                .iter()
                .find(|branch| branch.as_str() == "master")
                .cloned()
        })
        .unwrap_or_else(|| branches[0].clone());
    Ok(GitBranchCatalog { branches, selected })
}

/// 校验所选 ZIP 包，不写入实例目录。
pub fn inspect_offline_packages(paths: Vec<PathBuf>) -> Vec<OfflinePackageInspection> {
    let mut packages: Vec<OfflinePackageInspection> = paths
        .into_iter()
        .map(|path| {
            let file_name = path
                .file_name()
                .unwrap_or_else(|| OsStr::new(""))
                .to_string_lossy()
                .into_owned();
            match inspect_zip(&path) {
                Ok(metadata) => OfflinePackageInspection {
                    path,
                    file_name,
                    extension_id: Some(metadata.extension_id),
                    valid: true,
                    error: None,
                },
                Err(error) => OfflinePackageInspection {
                    path,
                    file_name,
                    extension_id: None,
                    valid: false,
                    error: Some(error),
                },
            }
        })
        .collect();

    // 同一批次出现相同扩展目录名时会产生覆盖顺序歧义，因此整组标记为无效。
    let identifiers: Vec<String> = packages
        .iter()
        .filter_map(|package| package.extension_id.clone())
        .collect();
    for package in &mut packages {
        if let Some(identifier) = package.extension_id.as_ref()
            && identifiers
                .iter()
                .filter(|item| *item == identifier)
                .count()
                > 1
        {
            package.valid = false;
            package.error = Some(ExtensionError::new(
                "extensions.error.duplicate_package",
                identifier.clone(),
            ));
        }
    }
    packages
}

/// 安装 Git 扩展并流式发送日志。
pub fn install_git_extension(
    request: GitInstallRequest,
    sender: &Sender<ExtensionEvent>,
    cancel: &Arc<AtomicBool>,
) -> Result<OperationSuccess, ExtensionError> {
    validate_git_url(&request.repository_url)?;
    let extension_id = repository_name(&request.repository_url).ok_or_else(|| {
        ExtensionError::new(
            "extensions.error.invalid_repository",
            &request.repository_url,
        )
    })?;
    let root = prepare_third_party_root(&request.instance_path)?;
    let target = root.join(&extension_id);
    validate_target_slot(&root, &target)?;
    if target.exists() && !request.overwrite {
        return Err(ExtensionError::new(
            "extensions.error.conflict",
            extension_id,
        ));
    }

    let staging = unique_sibling(&root, &extension_id, "install");
    remove_if_exists(&staging)?;
    let urls = candidate_urls(&request.repository_url, &request.proxy);
    let mut last_error = String::new();
    for (index, url) in urls.iter().enumerate() {
        if cancel.load(Ordering::Relaxed) {
            return Err(ExtensionError::new("extensions.error.cancelled", ""));
        }
        if index > 0 {
            let _ = sender.send(ExtensionEvent::Log(
                crate::lang::t("extensions.log.proxy_fallback").to_owned(),
            ));
            remove_if_exists(&staging)?;
        }
        match clone_repository(url, &request.branch, &staging, sender, cancel) {
            Ok(()) => {
                if let Err(error) = validate_installed_manifest(&staging) {
                    let _ = remove_if_exists(&staging);
                    return Err(error);
                }
                if let Err(error) = replace_directory(&staging, &target, request.overwrite) {
                    let _ = remove_if_exists(&staging);
                    return Err(error);
                }
                return Ok(OperationSuccess::Installed(vec![extension_id]));
            }
            Err(error) => last_error = error.detail,
        }
    }
    let _ = remove_if_exists(&staging);
    Err(ExtensionError::new(
        "extensions.error.clone_failed",
        last_error,
    ))
}

fn clone_repository(
    url: &str,
    branch: &str,
    staging: &Path,
    sender: &Sender<ExtensionEvent>,
    cancel: &Arc<AtomicBool>,
) -> Result<(), ExtensionError> {
    let _ = sender.send(ExtensionEvent::Log(format!(
        "git clone --branch {branch} {url}"
    )));
    let mut command = Command::new("git");
    crate::core::env::apply_no_window_to_command(&mut command);
    command.args(["clone", "--progress", "--depth", "1"]);
    if !branch.trim().is_empty() {
        command.args(["--branch", branch]);
    }
    let mut child = command
        .arg(url)
        .arg(staging)
        .stdout(Stdio::null())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|error| {
            ExtensionError::new("extensions.error.git_unavailable", error.to_string())
        })?;

    if let Some(stderr) = child.stderr.take() {
        for line in BufReader::new(stderr).lines().map_while(Result::ok) {
            if cancel.load(Ordering::Relaxed) {
                let _ = child.kill();
                let _ = child.wait();
                return Err(ExtensionError::new("extensions.error.cancelled", ""));
            }
            let sanitized: String = line
                .chars()
                .filter(|character| !character.is_control() || *character == '\t')
                .take(2000)
                .collect();
            let sanitized = sanitized.trim();
            if !sanitized.is_empty() {
                let _ = sender.send(ExtensionEvent::Log(sanitized.to_owned()));
            }
        }
    }
    let status = child
        .wait()
        .map_err(|error| ExtensionError::new("extensions.error.clone_failed", error.to_string()))?;
    if status.success() {
        Ok(())
    } else {
        Err(ExtensionError::new(
            "extensions.error.clone_failed",
            status.to_string(),
        ))
    }
}

/// 安装所有已经通过校验的离线扩展包。
pub fn install_offline_packages(
    request: OfflineInstallRequest,
    sender: &Sender<ExtensionEvent>,
    cancel: &Arc<AtomicBool>,
) -> Result<OperationSuccess, ExtensionError> {
    if request.packages.is_empty() || request.packages.iter().any(|package| !package.valid) {
        return Err(ExtensionError::new("extensions.error.offline_invalid", ""));
    }
    let root = prepare_third_party_root(&request.instance_path)?;
    let mut installed = Vec::new();
    for package in request.packages {
        if cancel.load(Ordering::Relaxed) {
            return Err(ExtensionError::new("extensions.error.cancelled", ""));
        }
        let metadata = inspect_zip(&package.path)?;
        let target = root.join(&metadata.extension_id);
        validate_target_slot(&root, &target)?;
        if target.exists() && !request.overwrite {
            return Err(ExtensionError::new(
                "extensions.error.conflict",
                metadata.extension_id,
            ));
        }
        let _ = sender.send(ExtensionEvent::Log(format!(
            "{}: {}",
            package.file_name, metadata.extension_id
        )));
        let staging = unique_sibling(&root, &metadata.extension_id, "install");
        remove_if_exists(&staging)?;
        fs::create_dir_all(&staging).map_err(|error| {
            ExtensionError::new("extensions.error.create_directory", error.to_string())
        })?;
        if let Err(error) = extract_zip(&package.path, &metadata, &staging) {
            let _ = remove_if_exists(&staging);
            return Err(error);
        }
        if let Err(error) = validate_installed_manifest(&staging) {
            let _ = remove_if_exists(&staging);
            return Err(error);
        }
        if let Err(error) = replace_directory(&staging, &target, request.overwrite) {
            let _ = remove_if_exists(&staging);
            return Err(error);
        }
        installed.push(metadata.extension_id);
    }
    Ok(OperationSuccess::Installed(installed))
}

/// 切换第三方扩展的启用状态。
pub fn set_extension_enabled(
    instance_path: &Path,
    extension_path: &Path,
    display_name: &str,
    enabled: bool,
) -> Result<OperationSuccess, ExtensionError> {
    validate_existing_third_party(instance_path, extension_path)?;
    let manifest = extension_path.join("manifest.json");
    let disabled_manifest = extension_path.join("manifest.json.disable");
    let (source, target) = if enabled {
        (&disabled_manifest, &manifest)
    } else {
        (&manifest, &disabled_manifest)
    };
    if !source.is_file() || target.exists() {
        return Err(ExtensionError::new(
            "extensions.error.toggle_failed",
            extension_path.display().to_string(),
        ));
    }
    fs::rename(source, target).map_err(|error| {
        ExtensionError::new("extensions.error.toggle_failed", error.to_string())
    })?;
    Ok(OperationSuccess::Enabled {
        name: display_name.to_owned(),
        enabled,
    })
}

/// 删除第三方扩展目录。
pub fn delete_extension(
    instance_path: &Path,
    extension_path: &Path,
    display_name: &str,
) -> Result<OperationSuccess, ExtensionError> {
    validate_existing_third_party(instance_path, extension_path)?;
    fs::remove_dir_all(extension_path).map_err(|error| {
        ExtensionError::new("extensions.error.delete_failed", error.to_string())
    })?;
    Ok(OperationSuccess::Deleted(display_name.to_owned()))
}

/// 为离线扩展补充旧版兼容的 Git 元数据。
pub fn repair_extension_git(
    instance_path: &Path,
    extension_path: &Path,
    display_name: &str,
    remote_url: &str,
) -> Result<OperationSuccess, ExtensionError> {
    validate_existing_third_party(instance_path, extension_path)?;
    if !is_github_repository(remote_url) {
        return Err(ExtensionError::new(
            "extensions.error.git_repair_unsupported",
            remote_url,
        ));
    }
    run_git(extension_path, &["init"])?;
    let mut command = Command::new("git");
    crate::core::env::apply_no_window_to_command(&mut command);
    let existing = command
        .args(["remote", "get-url", "origin"])
        .current_dir(extension_path)
        .output()
        .map_err(|error| {
            ExtensionError::new("extensions.error.git_unavailable", error.to_string())
        })?;
    if existing.status.success() {
        let current = String::from_utf8_lossy(&existing.stdout).trim().to_owned();
        if normalize_repository_url(&current) != normalize_repository_url(remote_url) {
            return Err(ExtensionError::new(
                "extensions.error.remote_conflict",
                current,
            ));
        }
    } else {
        run_git(extension_path, &["remote", "add", "origin", remote_url])?;
    }
    Ok(OperationSuccess::GitRepaired(display_name.to_owned()))
}

fn run_git(directory: &Path, arguments: &[&str]) -> Result<(), ExtensionError> {
    let mut command = Command::new("git");
    crate::core::env::apply_no_window_to_command(&mut command);
    let output = command
        .args(arguments)
        .current_dir(directory)
        .output()
        .map_err(|error| {
            ExtensionError::new("extensions.error.git_unavailable", error.to_string())
        })?;
    if output.status.success() {
        Ok(())
    } else {
        Err(ExtensionError::new(
            "extensions.error.git_repair_failed",
            String::from_utf8_lossy(&output.stderr).trim().to_owned(),
        ))
    }
}

#[derive(Debug)]
struct ZipMetadata {
    extension_id: String,
    manifest_prefix: PathBuf,
}

fn inspect_zip(path: &Path) -> Result<ZipMetadata, ExtensionError> {
    let file = File::open(path).map_err(|error| {
        ExtensionError::new("extensions.error.offline_open_failed", error.to_string())
    })?;
    let mut archive = zip::ZipArchive::new(BufReader::new(file)).map_err(|error| {
        ExtensionError::new("extensions.error.offline_invalid", error.to_string())
    })?;
    let mut manifests = Vec::new();
    for index in 0..archive.len() {
        let mut entry = archive.by_index(index).map_err(|error| {
            ExtensionError::new("extensions.error.offline_invalid", error.to_string())
        })?;
        let enclosed = validate_zip_entry(&entry)?;
        if enclosed.file_name() == Some(OsStr::new("manifest.json")) {
            let mut content = String::new();
            entry.read_to_string(&mut content).map_err(|error| {
                ExtensionError::new("extensions.error.manifest_invalid", error.to_string())
            })?;
            serde_json::from_str::<ExtensionManifest>(&content).map_err(|error| {
                ExtensionError::new("extensions.error.manifest_invalid", error.to_string())
            })?;
            manifests.push(enclosed);
        }
    }
    if manifests.len() != 1 {
        return Err(ExtensionError::new(
            "extensions.error.manifest_count",
            manifests.len().to_string(),
        ));
    }
    let manifest_prefix = manifests[0]
        .parent()
        .unwrap_or_else(|| Path::new(""))
        .to_path_buf();
    let raw_id = manifest_prefix
        .file_name()
        .map(|name| name.to_string_lossy().into_owned())
        .or_else(|| {
            path.file_stem()
                .map(|name| name.to_string_lossy().into_owned())
        })
        .unwrap_or_default();
    let extension_id = sanitize_extension_id(&raw_id)?;
    Ok(ZipMetadata {
        extension_id,
        manifest_prefix,
    })
}

fn extract_zip(
    path: &Path,
    metadata: &ZipMetadata,
    destination: &Path,
) -> Result<(), ExtensionError> {
    let file = File::open(path).map_err(|error| {
        ExtensionError::new("extensions.error.offline_open_failed", error.to_string())
    })?;
    let mut archive = zip::ZipArchive::new(BufReader::new(file)).map_err(|error| {
        ExtensionError::new("extensions.error.offline_invalid", error.to_string())
    })?;
    for index in 0..archive.len() {
        let mut entry = archive.by_index(index).map_err(|error| {
            ExtensionError::new("extensions.error.offline_invalid", error.to_string())
        })?;
        let enclosed = validate_zip_entry(&entry)?;
        if enclosed.starts_with("__MACOSX") || enclosed.file_name() == Some(OsStr::new(".DS_Store"))
        {
            continue;
        }
        let relative = if metadata.manifest_prefix.as_os_str().is_empty() {
            enclosed.as_path()
        } else if let Ok(relative) = enclosed.strip_prefix(&metadata.manifest_prefix) {
            relative
        } else {
            continue;
        };
        if relative.as_os_str().is_empty() {
            continue;
        }
        let target = destination.join(relative);
        if entry.is_dir() {
            fs::create_dir_all(&target).map_err(|error| {
                ExtensionError::new("extensions.error.create_directory", error.to_string())
            })?;
        } else {
            if let Some(parent) = target.parent() {
                fs::create_dir_all(parent).map_err(|error| {
                    ExtensionError::new("extensions.error.create_directory", error.to_string())
                })?;
            }
            let mut output = File::create(&target).map_err(|error| {
                ExtensionError::new("extensions.error.offline_write_failed", error.to_string())
            })?;
            std::io::copy(&mut entry, &mut output).map_err(|error| {
                ExtensionError::new("extensions.error.offline_write_failed", error.to_string())
            })?;
            output.flush().map_err(|error| {
                ExtensionError::new("extensions.error.offline_write_failed", error.to_string())
            })?;
        }
    }
    Ok(())
}

fn validate_zip_entry(entry: &zip::read::ZipFile<'_>) -> Result<PathBuf, ExtensionError> {
    if entry
        .unix_mode()
        .is_some_and(|mode| mode & 0o170000 == 0o120000)
    {
        return Err(ExtensionError::new(
            "extensions.error.archive_symlink",
            entry.name(),
        ));
    }
    let enclosed = entry
        .enclosed_name()
        .ok_or_else(|| ExtensionError::new("extensions.error.archive_path", entry.name()))?;
    if enclosed.is_absolute()
        || enclosed.components().any(|component| {
            matches!(
                component,
                Component::ParentDir | Component::RootDir | Component::Prefix(_)
            )
        })
    {
        return Err(ExtensionError::new(
            "extensions.error.archive_path",
            entry.name(),
        ));
    }
    Ok(enclosed.to_path_buf())
}

fn validate_installed_manifest(directory: &Path) -> Result<(), ExtensionError> {
    let path = directory.join("manifest.json");
    let content = fs::read_to_string(&path).map_err(|error| {
        ExtensionError::new("extensions.error.manifest_missing", error.to_string())
    })?;
    serde_json::from_str::<ExtensionManifest>(&content).map_err(|error| {
        ExtensionError::new("extensions.error.manifest_invalid", error.to_string())
    })?;
    Ok(())
}

fn replace_directory(staging: &Path, target: &Path, overwrite: bool) -> Result<(), ExtensionError> {
    let parent = target.parent().ok_or_else(|| {
        ExtensionError::new(
            "extensions.error.invalid_target",
            target.display().to_string(),
        )
    })?;
    let backup = unique_sibling(
        parent,
        &target.file_name().unwrap_or_default().to_string_lossy(),
        "backup",
    );
    let had_target = target.exists();
    if had_target {
        if !overwrite {
            return Err(ExtensionError::new(
                "extensions.error.conflict",
                target.display().to_string(),
            ));
        }
        remove_if_exists(&backup)?;
        fs::rename(target, &backup).map_err(|error| {
            ExtensionError::new("extensions.error.replace_failed", error.to_string())
        })?;
    }
    if let Err(error) = fs::rename(staging, target) {
        if had_target {
            let _ = fs::rename(&backup, target);
        }
        return Err(ExtensionError::new(
            "extensions.error.replace_failed",
            error.to_string(),
        ));
    }
    if had_target {
        remove_if_exists(&backup)?;
    }
    Ok(())
}

fn validate_existing_third_party(
    instance_path: &Path,
    target: &Path,
) -> Result<(), ExtensionError> {
    let root = third_party_root(instance_path);
    if !root.is_dir() || !target.exists() {
        return Err(ExtensionError::new(
            "extensions.error.invalid_target",
            target.display().to_string(),
        ));
    }
    let metadata = fs::symlink_metadata(target).map_err(|error| {
        ExtensionError::new("extensions.error.invalid_target", error.to_string())
    })?;
    if metadata.file_type().is_symlink() || !metadata.is_dir() {
        return Err(ExtensionError::new(
            "extensions.error.invalid_target",
            target.display().to_string(),
        ));
    }
    let canonical_root = fs::canonicalize(&root).map_err(|error| {
        ExtensionError::new("extensions.error.invalid_target", error.to_string())
    })?;
    let canonical_target = fs::canonicalize(target).map_err(|error| {
        ExtensionError::new("extensions.error.invalid_target", error.to_string())
    })?;
    if canonical_target.parent() != Some(canonical_root.as_path()) {
        return Err(ExtensionError::new(
            "extensions.error.path_outside_root",
            // 这条错误会显示在界面上，必须剥掉 canonicalize 带出的 `\\?\` 前缀。
            // 判断用的仍是 canonical_target 本身，只是展示时换一种写法。
            crate::core::local_instances::display_path(&canonical_target),
        ));
    }
    Ok(())
}

fn validate_target_slot(root: &Path, target: &Path) -> Result<(), ExtensionError> {
    if target.parent() != Some(root) || target.file_name().is_none() {
        return Err(ExtensionError::new(
            "extensions.error.path_outside_root",
            target.display().to_string(),
        ));
    }
    if target.exists() {
        let metadata = fs::symlink_metadata(target).map_err(|error| {
            ExtensionError::new("extensions.error.invalid_target", error.to_string())
        })?;
        if metadata.file_type().is_symlink() || !metadata.is_dir() {
            return Err(ExtensionError::new(
                "extensions.error.invalid_target",
                target.display().to_string(),
            ));
        }
    }
    Ok(())
}

fn prepare_third_party_root(instance_path: &Path) -> Result<PathBuf, ExtensionError> {
    let official = official_root(instance_path);
    if !official.is_dir() {
        return Err(ExtensionError::new(
            "extensions.error.root_missing",
            official.display().to_string(),
        ));
    }
    let root = official.join("third-party");
    fs::create_dir_all(&root).map_err(|error| {
        ExtensionError::new("extensions.error.create_directory", error.to_string())
    })?;
    Ok(root)
}

fn official_root(instance_path: &Path) -> PathBuf {
    instance_path
        .join("public")
        .join("scripts")
        .join("extensions")
}

fn third_party_root(instance_path: &Path) -> PathBuf {
    official_root(instance_path).join("third-party")
}

fn kind_rank(kind: ExtensionKind) -> u8 {
    match kind {
        ExtensionKind::ThirdParty => 0,
        ExtensionKind::System => 1,
    }
}

fn metadata_timestamp(path: &Path) -> u64 {
    fs::metadata(path)
        .ok()
        .and_then(|metadata| metadata.modified().ok())
        .and_then(|modified| modified.duration_since(UNIX_EPOCH).ok())
        .map_or(0, |duration| duration.as_secs())
}

fn unique_sibling(parent: &Path, id: &str, suffix: &str) -> PathBuf {
    let nonce = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |duration| duration.as_nanos());
    parent.join(format!(
        ".{id}.astrabrew-{suffix}-{}-{nonce}",
        std::process::id()
    ))
}

fn remove_if_exists(path: &Path) -> Result<(), ExtensionError> {
    if path.exists() {
        fs::remove_dir_all(path).map_err(|error| {
            ExtensionError::new("extensions.error.cleanup_failed", error.to_string())
        })?;
    }
    Ok(())
}

fn sanitize_extension_id(value: &str) -> Result<String, ExtensionError> {
    let sanitized: String = value
        .chars()
        .map(|character| {
            if character.is_ascii_alphanumeric() || matches!(character, '-' | '_' | '.') {
                character
            } else {
                '_'
            }
        })
        .collect();
    let sanitized = sanitized.trim_matches(['.', '_']).to_owned();
    if sanitized.is_empty() || sanitized == "third-party" {
        Err(ExtensionError::new(
            "extensions.error.invalid_extension_id",
            value,
        ))
    } else {
        Ok(sanitized)
    }
}

/// 从常见 Git URL 中提取稳定的仓库目录名。
pub fn repository_name(repository_url: &str) -> Option<String> {
    let trimmed = repository_url.trim().trim_end_matches('/');
    let segment = trimmed
        .rsplit(['/', ':'])
        .next()?
        .strip_suffix(".git")
        .unwrap_or_else(|| trimmed.rsplit(['/', ':']).next().unwrap_or_default());
    sanitize_extension_id(segment).ok()
}

fn validate_git_url(repository_url: &str) -> Result<(), ExtensionError> {
    let value = repository_url.trim();
    let valid = value.starts_with("https://")
        || value.starts_with("http://")
        || value.starts_with("ssh://")
        || (value.starts_with("git@") && value.contains(':'));
    if valid && repository_name(value).is_some() {
        Ok(())
    } else {
        Err(ExtensionError::new(
            "extensions.error.invalid_repository",
            value,
        ))
    }
}

fn is_github_repository(value: &str) -> bool {
    let normalized = value.trim().trim_end_matches('/').to_ascii_lowercase();
    let Some(path) = normalized
        .strip_prefix("https://github.com/")
        .or_else(|| normalized.strip_prefix("http://github.com/"))
    else {
        return false;
    };
    let mut components = path.split('/');
    let owner = components.next().unwrap_or_default();
    let repository = components
        .next()
        .unwrap_or_default()
        .trim_end_matches(".git");
    !owner.is_empty() && !repository.is_empty() && components.next().is_none()
}

fn normalize_repository_url(value: &str) -> String {
    value
        .trim()
        .trim_end_matches('/')
        .trim_end_matches(".git")
        .to_ascii_lowercase()
}

fn candidate_urls(repository_url: &str, proxy: &GithubProxyConfig) -> Vec<String> {
    let original = repository_url.trim().to_owned();
    if !proxy.enabled
        || proxy.base_url.trim().is_empty()
        || !original
            .to_ascii_lowercase()
            .starts_with("https://github.com/")
    {
        return vec![original];
    }
    let mut base = proxy.base_url.trim().to_owned();
    if !base.ends_with('/') {
        base.push('/');
    }
    vec![format!("{base}{original}"), original]
}

#[cfg(test)]
mod tests {
    use std::fs;
    use std::io::Write;
    use std::path::{Path, PathBuf};
    use std::time::{SystemTime, UNIX_EPOCH};

    use super::{
        ExtensionKind, inspect_offline_packages, repository_name, sanitize_extension_id,
        scan_extensions, set_extension_enabled,
    };

    /// 测试夹具只写系统临时目录，Drop 时清理，避免污染真实用户数据。
    struct Fixture {
        root: PathBuf,
    }

    impl Fixture {
        fn new(label: &str) -> Self {
            let nonce = SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .map_or(0, |duration| duration.as_nanos());
            let root = std::env::temp_dir().join(format!(
                "astrabrew-extension-{label}-{}-{nonce}",
                std::process::id()
            ));
            fs::create_dir_all(&root).unwrap();
            Self { root }
        }

        fn instance(&self) -> PathBuf {
            self.root.join("SillyTavern")
        }

        fn extension_root(&self) -> PathBuf {
            self.instance().join("public/scripts/extensions")
        }

        fn write_manifest(&self, relative: &str, disabled: bool) -> PathBuf {
            let directory = self.extension_root().join(relative);
            fs::create_dir_all(&directory).unwrap();
            let file_name = if disabled {
                "manifest.json.disable"
            } else {
                "manifest.json"
            };
            fs::write(
                directory.join(file_name),
                r#"{"display_name":"Fixture","version":"1.0.0","homePage":"https://github.com/example/fixture"}"#,
            )
            .unwrap();
            directory
        }
    }

    impl Drop for Fixture {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.root);
        }
    }

    fn write_zip(path: &Path, entries: &[(&str, &str)]) {
        let file = fs::File::create(path).unwrap();
        let mut archive = zip::ZipWriter::new(file);
        let options = zip::write::SimpleFileOptions::default()
            .compression_method(zip::CompressionMethod::Deflated);
        for (name, content) in entries {
            archive.start_file(*name, options).unwrap();
            archive.write_all(content.as_bytes()).unwrap();
        }
        archive.finish().unwrap();
    }

    #[test]
    fn extracts_repository_names() {
        assert_eq!(
            repository_name("https://github.com/example/demo.git"),
            Some("demo".into())
        );
        assert_eq!(
            repository_name("git@github.com:example/demo.git"),
            Some("demo".into())
        );
    }

    #[test]
    fn sanitizes_archive_directory_name() {
        assert_eq!(
            sanitize_extension_id("demo extension").unwrap(),
            "demo_extension"
        );
        assert!(sanitize_extension_id("../").is_err());
    }

    #[test]
    fn scans_and_toggles_real_manifest_files() {
        let fixture = Fixture::new("scan");
        let third_party = fixture.write_manifest("third-party/demo", false);
        fixture.write_manifest("quick-reply", false);

        let extensions = scan_extensions(&fixture.instance()).unwrap();
        assert_eq!(extensions.len(), 2);
        assert_eq!(extensions[0].kind, ExtensionKind::ThirdParty);
        assert_eq!(extensions[1].kind, ExtensionKind::System);

        set_extension_enabled(&fixture.instance(), &third_party, "Fixture", false).unwrap();
        assert!(!third_party.join("manifest.json").exists());
        assert!(third_party.join("manifest.json.disable").is_file());
        set_extension_enabled(&fixture.instance(), &third_party, "Fixture", true).unwrap();
        assert!(third_party.join("manifest.json").is_file());
    }

    #[test]
    fn validates_zip_root_and_rejects_ambiguous_manifests() {
        let fixture = Fixture::new("zip");
        let valid = fixture.root.join("valid.zip");
        write_zip(
            &valid,
            &[(
                "demo/manifest.json",
                r#"{"display_name":"Demo","version":"1.0.0"}"#,
            )],
        );
        let inspected = inspect_offline_packages(vec![valid]);
        assert!(inspected[0].valid);
        assert_eq!(inspected[0].extension_id.as_deref(), Some("demo"));

        let ambiguous = fixture.root.join("ambiguous.zip");
        write_zip(
            &ambiguous,
            &[
                ("one/manifest.json", r#"{"display_name":"One"}"#),
                ("two/manifest.json", r#"{"display_name":"Two"}"#),
            ],
        );
        let inspected = inspect_offline_packages(vec![ambiguous]);
        assert!(!inspected[0].valid);
    }

    #[test]
    fn rejects_duplicate_packages_in_one_batch() {
        let fixture = Fixture::new("duplicates");
        let first = fixture.root.join("first.zip");
        let second = fixture.root.join("second.zip");
        let entries = &[(
            "same/manifest.json",
            r#"{"display_name":"Same","version":"1.0.0"}"#,
        )];
        write_zip(&first, entries);
        write_zip(&second, entries);
        let inspected = inspect_offline_packages(vec![first, second]);
        assert!(inspected.iter().all(|package| !package.valid));
    }
}
