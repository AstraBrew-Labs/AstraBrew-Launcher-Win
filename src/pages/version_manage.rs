use eframe::egui;
use serde::{Deserialize, Serialize};
use std::sync::mpsc::{self, Receiver};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::Arc;
use std::thread;
use std::time::{SystemTime, UNIX_EPOCH};
use std::collections::HashMap;
use std::fs;
use std::path::PathBuf;
use std::process::Command;

use crate::lang;
use crate::pages::settings::{EnvSource, SettingsState};
use crate::utils;

#[derive(PartialEq, Clone, Copy)]
pub enum VersionTab {
    Local,
    Online,
}

#[derive(Clone, Serialize, Deserialize)]
pub struct LocalInstance {
    pub version: String,
    pub path: String,
    #[serde(default)]
    pub is_current: bool,   // 运行时根据 settings 计算，不持久化
    #[serde(default, skip_serializing)]
    pub is_online: bool,    // true=在线实例(不持久化), false=本地实例
}

#[derive(Serialize, Deserialize, Clone)]
pub struct GithubRelease {
    pub name: Option<String>,
    pub tag_name: String,
    pub published_at: String,
    pub zipball_url: String,
}

#[derive(Serialize, Deserialize, Clone)]
pub struct ReleasesCache {
    pub timestamp: u64,
    pub latest: Option<GithubRelease>,
    pub recent: Vec<GithubRelease>,
}

pub enum ReleaseMsg {
    Latest(Option<GithubRelease>),
    Recent(Vec<GithubRelease>),
    Error(String),
    Forbidden, // API rate limit or 403
}

#[allow(dead_code)]
pub enum DownloadMsg {
    Log(String),
    Progress(f32, String),
    Finished { version: String, path: String },
    NpmError { error: String, version: String, path: String },
    Error(String),
}

pub enum ScanMsg {
    Found(LocalInstance),
    ScanningPath(String),
    DriveProgress { drive: String, path: String },
    DriveDone { drive: String, found: usize },
    Log(String),
    Finished,
}

/// 单盘扫描状态
#[derive(Clone)]
pub(crate) struct DriveScanState {
    drive: String,
    status: DriveScanStatus,
    current_path: String,
    found_count: usize,
}

#[derive(Clone, PartialEq)]
enum DriveScanStatus {
    Pending,
    Scanning,
    Done,
}

pub struct VersionManageState {
    pub active_tab: VersionTab,

    // Local Instances
    pub local_instances: Vec<LocalInstance>,

    // Online Download
    pub online_installed_version: Option<String>,
    pub latest_release: Option<GithubRelease>,
    pub recent_releases: Vec<GithubRelease>,
    pub is_fetching_releases: bool,
    pub fetch_error: Option<String>,
    pub fetch_forbidden: bool,

    pub release_receiver: Option<Receiver<ReleaseMsg>>,

    // Download state
    pub is_downloading: bool,
    pub download_progress: f32,
    pub download_status: String,
    pub download_logs: Vec<String>,
    pub install_error_alert: Option<String>,
    pub download_receiver: Option<Receiver<DownloadMsg>>,

    pub show_other_versions: bool,
    pub npm_install_failed: bool,
    pub show_cancel_confirm: bool,
    pub show_update_confirm: bool,
    pub show_already_latest: bool,
    pub update_target: Option<(String, String)>,
    pub active_pid: std::sync::Arc<std::sync::Mutex<Option<u32>>>,
    pub download_finished_time: Option<std::time::Instant>,

    // Scan state
    pub is_scanning: bool,
    pub scan_receiver: Option<Receiver<ScanMsg>>,
    pub scanning_paths: Vec<String>,      // 保留最近5条
    pub show_scan_tips: bool,
    pub scan_finished_time: Option<std::time::Instant>,
    pub cancel_scan_flag: Option<Arc<AtomicBool>>,
    pub show_cancel_scan_confirm: bool,

    // 首次扫描确认弹窗
    pub show_scan_confirm: bool,

    // 扫描详情弹窗
    pub show_scan_detail: bool,
    pub drive_states: std::collections::HashMap<String, DriveScanState>,
    pub scan_logs: Vec<String>,
    pub scan_thread_info: Option<ScanThreadInfo>,
}

/// 扫描线程配置信息
pub struct ScanThreadInfo {
    pub total_cores: usize,
    pub used_threads: usize,
    pub drive_count: usize,
    pub threads_per_drive: usize,
    pub allocation_mode: String,  // "Auto" / "Half" / "All"
}

impl Default for VersionManageState {
    fn default() -> Self {
        Self::new()
    }
}

impl VersionManageState {
    pub fn new() -> Self {
        Self {
            active_tab: VersionTab::Local,
            local_instances: vec![],
            online_installed_version: None,
            latest_release: None,
            recent_releases: vec![],
            is_fetching_releases: false,
            fetch_error: None,
            fetch_forbidden: false,
            release_receiver: None,
            is_downloading: false,
            download_progress: 0.0,
            download_status: String::new(),
            download_logs: vec![],
            install_error_alert: None,
            download_receiver: None,
            show_other_versions: false,
            npm_install_failed: false,
            show_cancel_confirm: false,
            show_update_confirm: false,
            show_already_latest: false,
            update_target: None,
            active_pid: std::sync::Arc::new(std::sync::Mutex::new(None)),
            download_finished_time: None,
            is_scanning: false,
            scan_receiver: None,
            scanning_paths: vec![],
            show_scan_tips: false,
            scan_finished_time: None,
            cancel_scan_flag: None,
            show_cancel_scan_confirm: false,
            show_scan_confirm: false,
            show_scan_detail: false,
            drive_states: HashMap::new(),
            scan_logs: vec![],
            scan_thread_info: None,
        }
    }

    pub fn fetch_releases(&mut self, force: bool, settings: &SettingsState) {
        if self.is_fetching_releases {
            return;
        }

        // Cache logic — use caches directory
        let cache_path = utils::app_paths().caches.join("releases_cache.json");
        if !force {
            if let Ok(content) = fs::read_to_string(&cache_path) {
                if let Ok(cache) = serde_json::from_str::<ReleasesCache>(&content) {
                    let now = SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_secs();
                    // 1 day TTL = 86400 seconds
                    if now - cache.timestamp < 86400 {
                        self.latest_release = cache.latest;
                        self.recent_releases = cache.recent;
                        return;
                    }
                }
            }
        }

        self.is_fetching_releases = true;
        self.fetch_error = None;
        self.fetch_forbidden = false;

        let (tx, rx) = mpsc::channel();
        self.release_receiver = Some(rx);

        let proxy_type = settings.proxy_type.clone();
        let custom_proxy = settings.custom_proxy.clone();

        thread::spawn(move || {
            let mut client_builder = reqwest::blocking::Client::builder()
                .user_agent("AstraBrew-Launcher-macOS");

            // Configure proxy based on settings
            match proxy_type {
                crate::pages::settings::ProxyType::None => {
                    client_builder = client_builder.no_proxy();
                }
                crate::pages::settings::ProxyType::System => {
                    // Read macOS system proxy
                    if let Some((proxy_url, enabled)) = crate::core::network::read_system_proxy() {
                        if enabled && !proxy_url.is_empty() {
                            let url = if !proxy_url.starts_with("http") {
                                format!("http://{}", proxy_url)
                            } else {
                                proxy_url
                            };
                            if let Ok(proxy) = reqwest::Proxy::all(&url) {
                                client_builder = client_builder.proxy(proxy);
                            }
                        }
                    }
                }
                crate::pages::settings::ProxyType::Custom => {
                    if !custom_proxy.is_empty() {
                        let url = if !custom_proxy.starts_with("http") {
                            format!("http://{}", custom_proxy)
                        } else {
                            custom_proxy.clone()
                        };
                        if let Ok(proxy) = reqwest::Proxy::all(&url) {
                            client_builder = client_builder.proxy(proxy);
                        }
                    }
                }
            }

            // Github API request requires headers
            let mut headers = reqwest::header::HeaderMap::new();
            headers.insert(
                reqwest::header::ACCEPT,
                reqwest::header::HeaderValue::from_static("application/vnd.github.v3+json"),
            );

            let client = client_builder
                .timeout(std::time::Duration::from_secs(15))
                .default_headers(headers)
                .build()
                .unwrap_or_else(|_| reqwest::blocking::Client::new());

            // Fetch latest
            let latest_resp = client.get("https://api.github.com/repos/SillyTavern/SillyTavern/releases/latest").send();
            match latest_resp {
                Ok(resp) => {
                    if resp.status() == reqwest::StatusCode::FORBIDDEN {
                        let _ = tx.send(ReleaseMsg::Forbidden);
                        return;
                    }
                    if resp.status().is_success() {
                        if let Ok(release) = resp.json::<GithubRelease>() {
                            let _ = tx.send(ReleaseMsg::Latest(Some(release)));
                        }
                    } else {
                        let _ = tx.send(ReleaseMsg::Latest(None));
                    }
                }
                Err(e) => {
                    let _ = tx.send(ReleaseMsg::Error(e.to_string()));
                    return;
                }
            }

            // Fetch recent (max 5)
            let recent_resp = client.get("https://api.github.com/repos/SillyTavern/SillyTavern/releases?per_page=5").send();
            match recent_resp {
                Ok(resp) => {
                    if resp.status() == reqwest::StatusCode::FORBIDDEN {
                        let _ = tx.send(ReleaseMsg::Forbidden);
                        return;
                    }
                    if resp.status().is_success() {
                        if let Ok(releases) = resp.json::<Vec<GithubRelease>>() {
                            let _ = tx.send(ReleaseMsg::Recent(releases));
                        }
                    }
                }
                Err(e) => {
                    let _ = tx.send(ReleaseMsg::Error(e.to_string()));
                }
            }
        });
    }
}

/// 解析 npm registry URL
fn npm_registry_url(registry: &crate::pages::settings::NpmRegistry) -> &'static str {
    match registry {
        crate::pages::settings::NpmRegistry::Official => "https://registry.npmjs.org/",
        crate::pages::settings::NpmRegistry::Taobao => "https://registry.npmmirror.com/",
        crate::pages::settings::NpmRegistry::Tencent => "https://mirrors.cloud.tencent.com/npm/",
    }
}

/// 查找系统命令的完整路径（通过 `which`）
fn find_command(cmd: &str) -> Option<PathBuf> {
    let output = Command::new("which").arg(cmd).output().ok()?;
    if output.status.success() {
        let path = String::from_utf8_lossy(&output.stdout).trim().to_string();
        if !path.is_empty() {
            return Some(PathBuf::from(path));
        }
    }
    None
}

/// 保存本地实例列表（仅保留 is_online == false 的本地实例）
pub fn save_local_instances(instances: &[LocalInstance]) {
    let path = utils::app_paths().instances_file();
    let locals: Vec<&LocalInstance> = instances.iter().filter(|i| !i.is_online).collect();
    if let Ok(content) = serde_json::to_string_pretty(&locals) {
        let _ = fs::write(path, content);
    }
}

/// 加载本地实例列表
pub fn load_local_instances() -> Vec<LocalInstance> {
    let path = utils::app_paths().instances_file();
    if path.exists() {
        if let Ok(content) = fs::read_to_string(&path) {
            if let Ok(instances) = serde_json::from_str::<Vec<LocalInstance>>(&content) {
                return instances;
            }
        }
    }
    vec![]
}

/// 计算在线实例（builtin）的固定路径
#[allow(dead_code)]
pub fn get_builtin_sillytavern_path() -> PathBuf {
    utils::app_paths().sillytavern_dir()
}

/// 保存当前版本到 settings.json
pub fn save_current_to_settings(
    instance_type: &str,
    path: Option<&str>,
    version: &str,
    settings: &mut SettingsState,
) {
    settings.sillytavern = Some(crate::pages::settings::CurrentInstance {
        instance_type: instance_type.to_string(),
        path: path.map(|p| p.to_string()),
        version: version.to_string(),
    });
    settings.save();
}

/// 全盘扫描时跳过的目录名（Windows 版，大小写不敏感）
///
/// 注意：jwalk 无法在遍历前裁剪子树，因此需要在遍历线程中对
/// `%USERPROFILE%` 下级目录做预过滤，直接跳过整个排除目录，避免无效遍历。
const SCAN_EXCLUDED_DIRS: &[&str] = &[
    // Windows 系统目录
    "Windows",
    "Program Files",
    "Program Files (x86)",
    "ProgramData",
    "$Recycle.Bin",
    "System Volume Information",
    "Recovery",
    "PerfLogs",
    "MSOCache",
    "Config.Msi",
    // Windows 用户目录下大概率无代码的文件夹
    "AppData",
    "Contacts",
    "Links",
    "Searches",
    "PrintHood",
    "NetHood",
    "SendTo",
    "Start Menu",
    "Templates",
    "Cookies",
    "Recent",
    "Local Settings",
    "My Documents",
    "My Music",
    "My Pictures",
    "My Videos",
    // 开发工具缓存 / 大型依赖目录
    "node_modules",
    ".git",
    ".npm",
    ".cargo",
    ".cache",
    ".vscode",
    ".idea",
    "target",
    "build",
    "dist",
    ".next",
    ".nuxt",
    ".venv",
    "venv",
    "__pycache__",
    // 大型应用/游戏数据目录
    "Microsoft",
    "MicrosoftEdge",
    "Google",
    "Mozilla",
];

/// 检查路径是否命中排除目录
fn is_path_excluded(path: &std::path::Path) -> bool {
    path.components().any(|c| {
        if let std::path::Component::Normal(name) = c {
            let name_lower = name.to_string_lossy().to_lowercase();
            SCAN_EXCLUDED_DIRS.iter().any(|excluded| {
                name_lower == excluded.to_lowercase()
            })
        } else {
            false
        }
    })
}

/// 路径太长时用省略号替换中间部分
fn truncate_path_mid(path: &str, max_len: usize) -> String {
    let char_count = path.chars().count();
    if char_count <= max_len {
        return path.to_string();
    }
    let head_len = max_len / 3;
    let tail_len = max_len.saturating_sub(head_len + 3); // 3 = "..." 的长度
    let head: String = path.chars().take(head_len).collect();
    let tail: String = path.chars().rev().take(tail_len).collect::<Vec<_>>().into_iter().rev().collect();
    format!("{}...{}", head, tail)
}

/// 获取当前时间字符串 HH:MM:SS
fn chrono_now() -> String {
    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default();
    let secs = now.as_secs();
    let h = (secs / 3600) % 24;
    let m = (secs / 60) % 60;
    let s = secs % 60;
    format!("{:02}:{:02}:{:02}", h, m, s)
}

/// 解析版本号字符串为 (major, minor)，支持 "v20.11.0" 或 "1.17.0" 格式
fn parse_version_major_minor(version: &str) -> Option<(u32, u32)> {
    let v = version.trim_start_matches('v');
    let parts: Vec<&str> = v.split('.').collect();
    if parts.len() >= 2 {
        let major = parts[0].parse::<u32>().ok()?;
        let minor = parts[1].parse::<u32>().ok()?;
        Some((major, minor))
    } else {
        None
    }
}

/// 检查酒馆版本对 Node.js 的最低要求
fn check_nodejs_requirement(st_version: &str, nodejs_version: &str) -> Option<String> {
    let st_mm = parse_version_major_minor(st_version)?;
    let node_mm = parse_version_major_minor(nodejs_version)?;

    if st_mm.0 > 1 || (st_mm.0 == 1 && st_mm.1 > 17) {
        if node_mm.0 < 20 {
            return Some(format!("Min Node.js: >= v20 (current: v{})", nodejs_version.trim_start_matches('v')));
        }
    } else if st_mm.0 > 1 || (st_mm.0 == 1 && st_mm.1 >= 14) {
        if node_mm.0 < 18 {
            return Some(format!("Min Node.js: > v18 (current: v{})", nodejs_version.trim_start_matches('v')));
        }
    }
    None
}

pub fn render(ui: &mut egui::Ui, state: &mut VersionManageState, settings: &mut SettingsState) {
    let lang_owned = settings.language.clone();
    let lang = &lang_owned;

    // 启动时检测已存在的在线安装
    if state.online_installed_version.is_none() {
        let st_dir = utils::app_paths().sillytavern_dir();
        let pkg_path = st_dir.join("package.json");

        if st_dir.exists() && pkg_path.exists() {
            let mut local_ver = "Unknown".to_string();
            if let Ok(content) = fs::read_to_string(&pkg_path) {
                if let Ok(json) = serde_json::from_str::<serde_json::Value>(&content) {
                    if let Some(v) = json.get("version").and_then(|v| v.as_str()) {
                        local_ver = v.to_string();
                    }
                }
            }
            state.online_installed_version = Some(local_ver);
        }
    }

    // Process messages
    if let Some(rx) = &state.release_receiver {
        while let Ok(msg) = rx.try_recv() {
            match msg {
                ReleaseMsg::Latest(release) => {
                    state.latest_release = release;
                }
                ReleaseMsg::Recent(releases) => {
                    state.recent_releases = releases;
                    state.is_fetching_releases = false;

                    // Save to cache
                    let cache = ReleasesCache {
                        timestamp: SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_secs(),
                        latest: state.latest_release.clone(),
                        recent: state.recent_releases.clone(),
                    };
                    if let Ok(content) = serde_json::to_string(&cache) {
                        let path = utils::app_paths().caches.join("releases_cache.json");
                        let _ = fs::write(path, content);
                    }
                }
                ReleaseMsg::Error(err) => {
                    state.fetch_error = Some(err);
                    state.is_fetching_releases = false;
                }
                ReleaseMsg::Forbidden => {
                    state.fetch_forbidden = true;
                    state.is_fetching_releases = false;
                }
            }
        }
    }

    if let Some(rx) = &state.download_receiver {
        while let Ok(msg) = rx.try_recv() {
            match msg {
                DownloadMsg::Log(log) => {
                    state.download_logs.push(log);
                }
                DownloadMsg::Progress(p, status) => {
                    state.download_progress = p;
                    state.download_status = status;
                }
                DownloadMsg::Finished { version, path: _ } => {
                    state.download_progress = 1.0;
                    state.download_status = lang::t("download_finished", lang).to_string();
                    state.online_installed_version = Some(version.clone());
                    state.npm_install_failed = false;
                    state.download_finished_time = Some(std::time::Instant::now());

                    // 清除所有本地实例的 is_current，保存为 builtin 类型
                    for inst in state.local_instances.iter_mut() {
                        inst.is_current = false;
                    }
                    save_current_to_settings("builtin", None, &version, settings);
                }
                DownloadMsg::NpmError { error, version, path: _ } => {
                    state.download_status = format!("{}: {}", lang::t("download_error", lang), error);
                    state.online_installed_version = Some(version.clone());
                    state.npm_install_failed = true;
                }
                DownloadMsg::Error(err) => {
                    state.download_status = format!("{}: {}", lang::t("download_error", lang), err);
                }
            }
        }
    }

    if let Some(rx) = &state.scan_receiver {
        while let Ok(msg) = rx.try_recv() {
            match msg {
                ScanMsg::Found(instance) => {
                    // 拦截在线下载的酒馆实例路径（自动扫描时也过滤）
                    let builtin_path = utils::app_paths().sillytavern_dir();
                    let builtin_str = builtin_path.to_string_lossy().to_string();
                    let is_builtin = instance.path == builtin_str
                        || instance.path.starts_with(&format!("{}/", builtin_str));
                    if is_builtin {
                        // 跳过，不添加到本地实例列表
                    } else {
                        let exists = state.local_instances.iter().any(|i| i.path == instance.path);
                        if !exists {
                            let has_current = settings.sillytavern.is_some();
                            let mut inst = instance;
                            inst.is_current = !has_current;
                            let inst_version = inst.version.clone();
                            let inst_path = inst.path.clone();
                            state.local_instances.push(inst);
                            save_local_instances(&state.local_instances);
                            if !has_current {
                                save_current_to_settings("local", Some(&inst_path), &inst_version, settings);
                            }
                        }
                    }
                }
                ScanMsg::ScanningPath(path) => {
                    state.show_scan_tips = true;
                    if state.scanning_paths.len() >= 5 {
                        state.scanning_paths.remove(0);
                    }
                    state.scanning_paths.push(path);
                }
                ScanMsg::DriveProgress { drive, path } => {
                    if let Some(ds) = state.drive_states.get_mut(&drive) {
                        if ds.status == DriveScanStatus::Pending {
                            ds.status = DriveScanStatus::Scanning;
                        }
                        ds.current_path = path;
                    }
                }
                ScanMsg::DriveDone { drive, found } => {
                    if let Some(ds) = state.drive_states.get_mut(&drive) {
                        ds.status = DriveScanStatus::Done;
                        ds.found_count = found;
                    }
                    let timestamp = chrono_now();
                    state.scan_logs.push(format!(
                        "[{}] {} 扫描完成，找到 {} 个实例",
                        timestamp, drive, found
                    ));
                }
                ScanMsg::Log(log) => {
                    state.scan_logs.push(log);
                }
                ScanMsg::Finished => {
                    state.is_scanning = false;
                    state.cancel_scan_flag = None;
                    state.scan_finished_time = Some(std::time::Instant::now());
                }
            }
        }
    }

    // 扫描完成后3秒自动隐藏提示和详情弹窗
    if let Some(finished_at) = state.scan_finished_time {
        if finished_at.elapsed().as_secs() >= 3 {
            state.show_scan_tips = false;
            state.scanning_paths.clear();
            state.scan_finished_time = None;
            state.show_scan_detail = false;
        }
    }

    ui.horizontal(|ui| {
        ui.selectable_value(&mut state.active_tab, VersionTab::Local, lang::t("tab_local_instances", lang));
        ui.selectable_value(&mut state.active_tab, VersionTab::Online, lang::t("tab_online_download", lang));
    });

    ui.separator();

    match state.active_tab {
        VersionTab::Local => {
            render_local_tab(ui, state, settings);
        }
        VersionTab::Online => {
            render_online_tab(ui, state, settings);
        }
    }
}

fn render_local_tab(ui: &mut egui::Ui, state: &mut VersionManageState, settings: &mut SettingsState) {
    let lang_owned = settings.language.clone();
    let lang = &lang_owned;
    ui.horizontal(|ui| {
        // --- 左侧：按钮组 ---
        if state.is_scanning {
            ui.spinner();
            if ui.button(lang::t("btn_cancel_scan", lang)).clicked() {
                state.show_cancel_scan_confirm = true;
            }
        } else {
            if ui.button(lang::t("btn_auto_scan", lang)).clicked() {
                if settings.has_seen_scan_warning {
                    start_full_scan(state, settings);
                    state.show_scan_detail = true;
                } else {
                    state.show_scan_confirm = true;
                }
            }
        }
        if ui.button(lang::t("btn_manual_add", lang)).clicked() {
            if let Some(path) = rfd::FileDialog::new()
                .add_filter("package", &["json"])
                .set_title(lang::t("dialog_select_package_json", lang))
                .pick_file()
            {
                if path.file_name().and_then(|n| n.to_str()) == Some("package.json") {
                    if let Ok(content) = fs::read_to_string(&path) {
                        if let Ok(json) = serde_json::from_str::<serde_json::Value>(&content) {
                            if let Some(name) = json.get("name").and_then(|n| n.as_str()) {
                                if name == "sillytavern" {
                                    let version = json.get("version").and_then(|v| v.as_str()).unwrap_or("Unknown").to_string();
                                    let mut parent_path = path.clone();
                                    parent_path.pop();

                                    let path_str = parent_path.to_string_lossy().to_string();

                                    // 拦截在线下载的酒馆实例路径
                                    let builtin_path = utils::app_paths().sillytavern_dir();
                                    let builtin_str = builtin_path.to_string_lossy().to_string();
                                    let is_builtin = path_str == builtin_str
                                        || path_str.starts_with(&format!("{}/", builtin_str));

                                    if is_builtin {
                                        state.install_error_alert = Some(lang::t("cannot_add_online_instance", lang).to_string());
                                    } else {
                                        let exists = state.local_instances.iter().any(|i| i.path == path_str);
                                        if !exists {
                                            let is_current = settings.sillytavern.as_ref()
                                                .map(|s| s.instance_type == "local" && s.path.as_deref() == Some(&path_str))
                                                .unwrap_or(false);
                                            state.local_instances.push(LocalInstance {
                                                version: version.clone(),
                                                path: path_str.clone(),
                                                is_current,
                                                is_online: false,
                                            });
                                            save_local_instances(&state.local_instances);
                                            if settings.sillytavern.is_none() {
                                                save_current_to_settings("local", Some(&path_str), &version, settings);
                                            }
                                        }
                                    }
                                } else {
                                    state.install_error_alert = Some(lang::t("not_sillytavern_instance", lang).to_string());
                                }
                            } else {
                                state.install_error_alert = Some(lang::t("not_sillytavern_instance", lang).to_string());
                            }
                        } else {
                            state.install_error_alert = Some(lang::t("not_sillytavern_instance", lang).to_string());
                        }
                    }
                } else {
                    state.install_error_alert = Some(lang::t("not_sillytavern_instance", lang).to_string());
                }
            }
        }

        // --- 右侧：扫描进度提示 ---
        if state.show_scan_tips && !state.scanning_paths.is_empty() {
            ui.horizontal(|ui| {
                // 详情图标
                let icon_response = ui.add(
                    egui::Label::new(
                        egui::RichText::new(egui_phosphor::regular::INFO)
                            .size(14.0)
                            .color(egui::Color32::from_rgb(120, 180, 255)),
                    )
                    .sense(egui::Sense::click()),
                );
                if icon_response.clicked() {
                    state.show_scan_detail = true;
                }
                icon_response.on_hover_text("点击查看扫描详情");

                let latest_path = state.scanning_paths.last()
                    .map(|p| truncate_path_mid(p, 55))
                    .unwrap_or_default();
                let header = if state.is_scanning {
                    lang::t("scan_tips_scanning_header", lang)
                } else {
                    lang::t("scan_tips_done_header", lang)
                };
                let tips_text = format!("{}  {}", header, latest_path);
                ui.label(
                    egui::RichText::new(tips_text)
                        .size(11.0)
                        .color(egui::Color32::GRAY),
                );
            });
        }
    });

    ui.add_space(10.0);

    egui::ScrollArea::vertical().show(ui, |ui| {
        if state.local_instances.is_empty() {
            ui.label(lang::t("no_local_instances", lang));
        } else {
            let mut remove_idx = None;
            let mut switch_idx = None;

            for (i, instance) in state.local_instances.iter().enumerate() {
                ui.group(|ui| {
                    ui.horizontal(|ui| {
                        ui.vertical(|ui| {
                            ui.label(egui::RichText::new(&instance.version).heading());
                            ui.label(egui::RichText::new(&instance.path).small().color(egui::Color32::GRAY));
                            let nodejs_ver = if settings.env_mode == EnvSource::Builtin {
                                &settings.nodejs_version_builtin
                            } else {
                                &settings.nodejs_version
                            };
                            if !nodejs_ver.is_empty() {
                                if let Some(warning) = check_nodejs_requirement(&instance.version, nodejs_ver) {
                                    ui.label(
                                        egui::RichText::new(warning)
                                            .size(10.0)
                                            .color(egui::Color32::from_rgb(255, 150, 80)),
                                    );
                                }
                            } else {
                                ui.label(
                                    egui::RichText::new("Node.js: not detected")
                                        .size(10.0)
                                        .color(egui::Color32::from_rgb(255, 100, 80)),
                                );
                            }
                        });

                        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                            let is_current = match &settings.sillytavern {
                                Some(s) if s.instance_type == "local" => s.path.as_deref() == Some(&instance.path),
                                _ => false,
                            };
                            if is_current {
                                ui.label(egui::RichText::new(lang::t("status_current_version", lang)).color(egui::Color32::GREEN));
                            } else {
                                if ui.button(lang::t("btn_switch_version", lang)).clicked() {
                                    switch_idx = Some(i);
                                }
                            }

                            if ui.add_enabled(!is_current, egui::Button::new(lang::t("btn_remove_list", lang))).clicked() {
                                remove_idx = Some(i);
                            }

                            if ui.button(lang::t("btn_view_info", lang)).clicked() {
                                // View info — open in Finder
                                let _ = Command::new("explorer").arg(&instance.path).spawn();
                            }
                        });
                    });
                });
            }

            if let Some(idx) = remove_idx {
                state.local_instances.remove(idx);
                save_local_instances(&state.local_instances);
            }
            if let Some(idx) = switch_idx {
                for (i, inst) in state.local_instances.iter_mut().enumerate() {
                    inst.is_current = i == idx;
                }
                if let Some(inst) = state.local_instances.get(idx) {
                    save_current_to_settings("local", Some(&inst.path), &inst.version, settings);
                }
            }
        }
    });

    // 取消扫描二次确认弹窗
    if state.show_cancel_scan_confirm {
        let mut confirm_open = true;
        egui::Window::new(lang::t("warning", lang))
            .open(&mut confirm_open)
            .collapsible(false)
            .resizable(false)
            .anchor(egui::Align2::CENTER_CENTER, [0.0, 0.0])
            .show(ui.ctx(), |ui| {
                ui.label(lang::t("confirm_cancel_scan_desc", lang));
                ui.add_space(10.0);
                ui.horizontal(|ui| {
                    if ui.button(lang::t("btn_confirm", lang)).clicked() {
                        if let Some(ref flag) = state.cancel_scan_flag {
                            flag.store(true, Ordering::Relaxed);
                        }
                        state.scan_receiver = None;
                        state.is_scanning = false;
                        state.show_cancel_scan_confirm = false;
                        state.scan_finished_time = Some(std::time::Instant::now());
                    }
                    if ui.button(lang::t("cancel", lang)).clicked() {
                        state.show_cancel_scan_confirm = false;
                    }
                });
            });
        if !confirm_open {
            state.show_cancel_scan_confirm = false;
        }
    }

    // 首次扫描二次确认弹窗
    if state.show_scan_confirm {
        let mut confirm_open = true;
        egui::Window::new(lang::t("warning", lang))
            .open(&mut confirm_open)
            .collapsible(false)
            .resizable(false)
            .anchor(egui::Align2::CENTER_CENTER, [0.0, 0.0])
            .show(ui.ctx(), |ui| {
                ui.label(lang::t("confirm_first_scan_desc", lang));
                ui.add_space(10.0);
                ui.horizontal(|ui| {
                    if ui.button(lang::t("btn_continue", lang)).clicked() {
                        settings.has_seen_scan_warning = true;
                        settings.save();
                        state.show_scan_confirm = false;
                        start_full_scan(state, settings);
                        state.show_scan_detail = true;
                    }
                    if ui.button(lang::t("cancel", lang)).clicked() {
                        state.show_scan_confirm = false;
                    }
                });
            });
        if !confirm_open {
            state.show_scan_confirm = false;
        }
    }

    // --- 扫描详情弹窗 ---
    render_scan_detail_popup(ui.ctx(), state, settings);
}

/// 绘制扫描详情弹窗：每盘圆盘进度 + 线程信息 + 实时日志
fn render_scan_detail_popup(
    ctx: &egui::Context,
    state: &mut VersionManageState,
    settings: &SettingsState,
) {
    if !state.show_scan_detail {
        return;
    }
    let mut open = true;
    let lang_owned = settings.language.clone();
    let _lang = &lang_owned;

    egui::Window::new("扫描详情")
        .open(&mut open)
        .collapsible(true)
        .resizable(true)
        .default_size([640.0, 480.0])
        .min_size([480.0, 360.0])
        .max_size([800.0, f32::INFINITY])
        .show(ctx, |ui| {
            let mut drives: Vec<DriveScanState> = state.drive_states.values().cloned().collect();
            drives.sort_by(|a, b| a.drive.cmp(&b.drive));
            if drives.is_empty() {
                ui.label("暂无扫描数据");
            } else {
                let total_h = ui.available_height();
                let top_h = (total_h * 0.55).max(140.0);
                let avail_w = ui.available_width();
                let card_w = (avail_w * 0.62).min(480.0).max(280.0);

                // === 上半区：左卡片列 + 右信息区 ===
                ui.horizontal(|ui| {
                    // 左：进度圆盘卡片
                    ui.vertical(|ui| {
                        ui.set_min_width(card_w);
                        ui.set_max_width(card_w);
                        ui.set_min_height(top_h);
                        egui::ScrollArea::vertical()
                            .id_salt("scan_cards_scroll")
                            .max_height(top_h)
                            .show(ui, |ui| {
                                ui.set_min_width(card_w);
                                ui.set_max_width(card_w);
                                for ds in &drives {
                                    ui.push_id(&ds.drive, |ui| {
                                        render_drive_card(ui, ds, card_w);
                                    });
                                    ui.add_space(4.0);
                                }
                            });
                    });

                    ui.separator();

                    // 右：信息区
                    ui.vertical(|ui| {
                        ui.set_min_width(160.0);
                        ui.set_min_height(top_h);
                        ui.set_max_height(top_h);
                        egui::ScrollArea::vertical()
                            .id_salt("scan_info_scroll")
                            .max_height(top_h)
                            .show(ui, |ui| {
                                if let Some(ref info) = state.scan_thread_info {
                                    ui.heading("线程信息");
                                    ui.add_space(4.0);
                                    ui.label(format!("物理核心: {}", info.total_cores));
                                    ui.label(format!("使用线程: {}", info.used_threads));
                                    ui.label(format!("分配模式: {}",
                                        match info.allocation_mode.as_str() {
                                            "Auto" => "Auto (-2)", "Half" => "Half (/2)", "All" => "All",
                                            _ => &info.allocation_mode,
                                        }
                                    ));
                                    ui.label(format!("磁盘数量: {}", info.drive_count));
                                    ui.label(format!("每盘线程: {}", info.threads_per_drive));
                                    ui.add_space(8.0);
                                    let done = drives.iter().filter(|d| d.status == DriveScanStatus::Done).count();
                                    let scanning = drives.iter().filter(|d| d.status == DriveScanStatus::Scanning).count();
                                    let found: usize = drives.iter().map(|d| d.found_count).sum();
                                    ui.separator();
                                    ui.label(format!("扫描进度: {}/{}", done, drives.len()));
                                    if state.is_scanning { ui.label(format!("正在扫描: {} 个盘", scanning)); }
                                    ui.label(format!("已找到: {} 个实例", found));
                                } else {
                                    ui.label("暂无线程信息");
                                }
                            });
                    });
                });

                ui.separator();

                // === 下半区：日志区（填满剩余空间）===
                ui.heading("扫描日志");
                let log_text = state.scan_logs.join("\n");
                egui::ScrollArea::vertical()
                    .id_salt("scan_log_scroll")
                    .max_height(ui.available_height().max(80.0))
                    .stick_to_bottom(true)
                    .show(ui, |ui| {
                        ui.add(
                            egui::TextEdit::multiline(&mut log_text.as_str())
                                .font(egui::TextStyle::Monospace)
                                .desired_width(f32::INFINITY)
                                .interactive(false),
                        );
                    });
            }
        });

    if !open {
        state.show_scan_detail = false;
    }
}

/// 绘制单个磁盘的进度卡片
fn render_drive_card(ui: &mut egui::Ui, ds: &DriveScanState, card_width: f32) {
    let card_color = match ds.status {
        DriveScanStatus::Done => egui::Color32::from_rgb(30, 70, 40),
        DriveScanStatus::Scanning => egui::Color32::from_rgb(30, 50, 70),
        DriveScanStatus::Pending => egui::Color32::from_rgb(50, 50, 50),
    };

    egui::Frame::NONE
        .fill(card_color)
        .corner_radius(6)
        .inner_margin(egui::Margin::symmetric(10, 8))
        .show(ui, |ui| {
            ui.set_min_width(card_width);
            ui.set_max_width(card_width);
            ui.horizontal(|ui| {
                // 圆形进度指示器
                let size = 40.0;
                let (rect, _) =
                    ui.allocate_exact_size(egui::vec2(size, size), egui::Sense::hover());
                let center = rect.center();
                let radius = size / 2.0 - 3.0;
                let painter = ui.painter_at(rect);

                match ds.status {
                    DriveScanStatus::Pending => {
                        painter.circle_stroke(
                            center,
                            radius,
                            (2.0, egui::Color32::from_gray(120)),
                        );
                    }
                    DriveScanStatus::Scanning => {
                        painter.circle_stroke(
                            center,
                            radius,
                            (2.0, egui::Color32::from_gray(70)),
                        );
                        let time = ui.input(|i| i.time) as f32;
                        let sweep = 1.2;
                        let start = time * 3.0;
                        let end = start + sweep;
                        draw_arc(&painter, center, radius - 1.0, start, end, egui::Color32::from_rgb(80, 160, 255), 2.5);
                    }
                    DriveScanStatus::Done => {
                        painter.circle_filled(center, radius, egui::Color32::from_rgb(40, 160, 70));
                        let check_color = egui::Color32::WHITE;
                        let s = radius * 0.5;
                        painter.line_segment(
                            [
                                center + egui::vec2(-s * 0.5, 0.0),
                                center + egui::vec2(-s * 0.1, s * 0.5),
                            ],
                            (2.5, check_color),
                        );
                        painter.line_segment(
                            [
                                center + egui::vec2(-s * 0.1, s * 0.5),
                                center + egui::vec2(s * 0.7, -s * 0.4),
                            ],
                            (2.5, check_color),
                        );
                    }
                }

                ui.add_space(8.0);

                // 右侧文字信息（根据可用宽度截断路径）
                let text_area_w = card_width - size - 8.0 - 20.0;
                let path_max_chars = (text_area_w / 6.5) as usize;

                ui.vertical(|ui| {
                    let status_text = match ds.status {
                        DriveScanStatus::Pending => "等待中",
                        DriveScanStatus::Scanning => "扫描中...",
                        DriveScanStatus::Done => {
                            if ds.found_count > 0 {
                                &format!("已完成 ({} 个)", ds.found_count)
                            } else {
                                "已完成"
                            }
                        }
                    };
                    ui.horizontal(|ui| {
                        ui.label(
                            egui::RichText::new(&ds.drive)
                                .strong()
                                .size(13.0),
                        );
                        ui.label(
                            egui::RichText::new(status_text)
                                .size(11.0)
                                .color(match ds.status {
                                    DriveScanStatus::Done => egui::Color32::from_rgb(100, 220, 130),
                                    DriveScanStatus::Scanning => egui::Color32::from_rgb(150, 200, 255),
                                    DriveScanStatus::Pending => egui::Color32::GRAY,
                                }),
                        );
                    });
                    if ds.status == DriveScanStatus::Scanning && !ds.current_path.is_empty() {
                        ui.label(
                            egui::RichText::new(truncate_path_mid(&ds.current_path, path_max_chars))
                                .size(10.0)
                                .color(egui::Color32::from_gray(180)),
                        );
                    }
                });
            });
        });
}

/// 绘制圆弧
fn draw_arc(
    painter: &egui::Painter,
    center: egui::Pos2,
    radius: f32,
    start_angle: f32,
    end_angle: f32,
    color: egui::Color32,
    width: f32,
) {
    let steps = 20;
    let mut points = Vec::with_capacity(steps + 1);
    for i in 0..=steps {
        let t = i as f32 / steps as f32;
        let angle = start_angle + t * (end_angle - start_angle);
        points.push(egui::Pos2::new(
            center.x + radius * angle.cos(),
            center.y + radius * angle.sin(),
        ));
    }
    painter.add(egui::Shape::line(points, (width, color)));
}

/// 枚举所有可访问的磁盘根目录（C:\ ~ Z:\）
fn enumerate_drives() -> Vec<String> {
    let mut drives = Vec::new();
    for letter in b'C'..=b'Z' {
        let drive = format!("{}:\\", letter as char);
        if std::fs::read_dir(&drive).is_ok() {
            drives.push(drive);
        }
    }
    drives
}

/// 全盘扫描：多线程并行扫描所有磁盘，每盘独立线程，匹配 package.json
fn start_full_scan(state: &mut VersionManageState, settings: &SettingsState) {
    state.is_scanning = true;
    state.scanning_paths.clear();
    state.show_scan_tips = true;
    state.scan_finished_time = None;
    state.drive_states.clear();
    state.scan_logs.clear();

    let (tx, rx) = mpsc::channel();
    state.scan_receiver = Some(rx);
    let cpu_cores = settings.cpu_cores.clone();
    let cancel_flag = Arc::new(AtomicBool::new(false));
    state.cancel_scan_flag = Some(cancel_flag.clone());

    let drives = enumerate_drives();
    // 初始化每盘状态
    for drive in &drives {
        state.drive_states.insert(
            drive.clone(),
            DriveScanState {
                drive: drive.clone(),
                status: DriveScanStatus::Pending,
                current_path: String::new(),
                found_count: 0,
            },
        );
    }

    let cores = std::thread::available_parallelism().map(|n| n.get()).unwrap_or(1);
    let total_threads = match &cpu_cores {
        crate::pages::settings::CpuCores::Auto => std::cmp::max(1, cores.saturating_sub(2)),
        crate::pages::settings::CpuCores::Half => std::cmp::max(1, cores / 2),
        crate::pages::settings::CpuCores::All => cores,
    };
    let drive_count = drives.len();
    let threads_per_drive = std::cmp::max(1, total_threads / drive_count.max(1));

    // 保存线程信息供 UI 显示
    let allocation_mode = match &cpu_cores {
        crate::pages::settings::CpuCores::Auto => "Auto",
        crate::pages::settings::CpuCores::Half => "Half",
        crate::pages::settings::CpuCores::All => "All",
    };
    state.scan_thread_info = Some(ScanThreadInfo {
        total_cores: cores,
        used_threads: total_threads,
        drive_count,
        threads_per_drive,
        allocation_mode: allocation_mode.to_string(),
    });

    let tx_init = tx.clone();
    let _ = tx_init.send(ScanMsg::Log(format!(
        "[{}] 开始全盘扫描，检测到 {} 个磁盘，{} 核心 ({} 模式)，每盘 {} 线程",
        chrono_now(),
        drive_count,
        if matches!(&cpu_cores, crate::pages::settings::CpuCores::All) {
            cores
        } else {
            total_threads
        },
        allocation_mode,
        threads_per_drive,
    )));

    if drives.is_empty() {
        let _ = tx.send(ScanMsg::Finished);
        return;
    }

    let pending = Arc::new(AtomicUsize::new(drives.len()));
    for drive in drives.into_iter() {
        let tx = tx.clone();
        let cancel_flag = cancel_flag.clone();
        let pending = pending.clone();

        thread::spawn(move || {
            let _ = tx.send(ScanMsg::Log(format!(
                "[{}] {} 开始扫描...",
                chrono_now(),
                drive,
            )));
            let _ = tx.send(ScanMsg::ScanningPath(drive.clone()));

            // 标记为扫描中
            let _ = tx.send(ScanMsg::DriveProgress {
                drive: drive.clone(),
                path: String::new(),
            });

            let drive_path = PathBuf::from(&drive);
            let top_dirs: Vec<PathBuf> = match std::fs::read_dir(&drive_path) {
                Ok(entries) => entries
                    .filter_map(|e| e.ok())
                        .filter(|e| e.path().is_dir() && !is_path_excluded(&e.path()))
                        .map(|e| e.path())
                        .collect(),
                    Err(_) => vec![],
                };

                if top_dirs.is_empty() {
                    let _ = tx.send(ScanMsg::DriveDone {
                        drive: drive.clone(),
                        found: 0,
                    });
                    if pending.fetch_sub(1, Ordering::SeqCst) == 1 {
                        let _ = tx.send(ScanMsg::Finished);
                    }
                    return;
                }

                let excluded_count = {
                    if let Ok(all) = std::fs::read_dir(&drive_path) {
                        all.filter_map(|e| e.ok()).count()
                    } else {
                        0
                    }
                };
                let _ = tx.send(ScanMsg::Log(format!(
                    "[{}] {} 跳过 {} 个系统目录，扫描 {} 个目录",
                    chrono_now(),
                    drive,
                    excluded_count.saturating_sub(top_dirs.len()),
                    top_dirs.len(),
                )));

                let mut found_count: usize = 0;

                for dir in &top_dirs {
                    if cancel_flag.load(Ordering::Relaxed) {
                        break;
                    }
                    let walk = jwalk::WalkDir::new(dir)
                        .parallelism(jwalk::Parallelism::Serial)
                        .skip_hidden(false);
                    for entry in walk.into_iter().filter_map(|e| e.ok()) {
                        if cancel_flag.load(Ordering::Relaxed) {
                            break;
                        }
                        if is_path_excluded(&entry.path()) {
                            continue;
                        }
                        if entry.file_name() == "package.json" {
                            let path = entry.path();
                            let dir_path = path
                                .parent()
                                .map(|p| p.to_string_lossy().to_string())
                                .unwrap_or_else(|| path.to_string_lossy().to_string());

                            let _ = tx.send(ScanMsg::DriveProgress {
                                drive: drive.clone(),
                                path: dir_path.clone(),
                            });
                            let _ = tx.send(ScanMsg::ScanningPath(dir_path));

                            if let Ok(content) = fs::read_to_string(&path) {
                                if let Ok(json) =
                                    serde_json::from_str::<serde_json::Value>(&content)
                                {
                                    if let Some(name) = json.get("name").and_then(|n| n.as_str())
                                    {
                                        if name == "sillytavern" {
                                            let version = json
                                                .get("version")
                                                .and_then(|v| v.as_str())
                                                .unwrap_or("Unknown")
                                                .to_string();
                                            let mut parent_path = path.clone();
                                            parent_path.pop();
                                            let path_str =
                                                parent_path.to_string_lossy().to_string();
                                            found_count += 1;
                                            let _ = tx.send(ScanMsg::Log(format!(
                                                "[{}] {} 发现实例: v{} ({})",
                                                chrono_now(),
                                                drive,
                                                version,
                                                truncate_path_mid(&path_str, 50),
                                            )));
                                            let _ = tx.send(ScanMsg::Found(LocalInstance {
                                                version,
                                                path: path_str,
                                                is_current: false,
                                                is_online: false,
                                            }));
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                let _ = tx.send(ScanMsg::DriveDone {
                    drive: drive.clone(),
                    found: found_count,
                });
                if pending.fetch_sub(1, Ordering::SeqCst) == 1 {
                    let _ = tx.send(ScanMsg::Log(format!(
                        "[{}] 全盘扫描完成",
                        chrono_now(),
                    )));
                    let _ = tx.send(ScanMsg::Finished);
                }
            });
        }
}

fn render_online_tab(ui: &mut egui::Ui, state: &mut VersionManageState, settings: &mut SettingsState) {
    let lang_owned = settings.language.clone();
    let lang = &lang_owned;
    if state.latest_release.is_none() && !state.is_fetching_releases {
        state.fetch_releases(false, settings);
    }

    ui.vertical_centered(|ui| {
        ui.add_space(40.0);

        // 中间酒馆图标
        ui.label(egui::RichText::new(egui_phosphor::regular::BEER_BOTTLE).size(80.0));

        if let Some(ver) = &state.online_installed_version {
            ui.label(egui::RichText::new(format!("{}: {}", lang::t("status_current_version", lang), ver)).size(16.0).color(egui::Color32::GRAY));
        }
        ui.add_space(20.0);

        if state.is_fetching_releases {
            ui.spinner();
            ui.label(lang::t("status_fetching_releases", lang));
            return;
        }

        if state.fetch_forbidden {
            ui.heading(lang::t("info_fetch_failed", lang));
            ui.label(lang::t("info_fetch_failed_desc", lang));
            ui.add_space(10.0);
            if ui.button(lang::t("btn_retry", lang)).clicked() {
                state.fetch_releases(true, settings);
            }
            return;
        }

        if let Some(err) = &state.fetch_error {
            ui.heading(lang::t("info_fetch_failed", lang));
            ui.colored_label(egui::Color32::RED, err);
            ui.add_space(10.0);
            if ui.button(lang::t("btn_retry", lang)).clicked() {
                state.fetch_releases(true, settings);
            }
            return;
        }

        if !state.is_downloading && !state.download_status.is_empty() {
            ui.label(&state.download_status);
            ui.add_space(10.0);
        }

        let mut download_target = None;
        let mut fetch_now = false;

        if let Some(latest) = state.latest_release.clone() {
            egui::Frame::NONE
                .fill(ui.visuals().faint_bg_color)
                .corner_radius(8.0)
                .inner_margin(20.0)
                .show(ui, |ui| {
                    ui.vertical_centered(|ui| {
                        ui.horizontal(|ui| {
                            ui.add_space(ui.available_width() / 2.0 - 120.0);
                            ui.heading(format!("{}: {}", lang::t("latest_version", lang), latest.tag_name));

                            if ui.button(egui_phosphor::regular::ARROWS_CLOCKWISE).on_hover_text(lang::t("btn_refresh", lang)).clicked() {
                                fetch_now = true;
                            }
                        });

                        ui.add_space(8.0);
                        if let Some(name) = &latest.name {
                            if name != &latest.tag_name {
                                ui.label(egui::RichText::new(name).size(16.0));
                            }
                        }
                        ui.label(egui::RichText::new(format!("{}: {}", lang::t("published_at", lang), latest.published_at)).color(egui::Color32::GRAY));

                        ui.add_space(24.0);

                        ui.horizontal(|ui| {
                            let is_installed = state.online_installed_version.is_some();
                            let is_current_online = settings.sillytavern.as_ref().map(|s| s.instance_type == "builtin").unwrap_or(false);
                            let show_switch = is_installed && !is_current_online;

                            let total_width = ui.available_width();
                            let spacing = 8.0;
                            let switch_w = 120.0;
                            let btn1_w = 140.0;
                            let btn2_w = 140.0;
                            let used_width = if show_switch {
                                switch_w + spacing + btn1_w + spacing + btn2_w
                            } else {
                                btn1_w + spacing + btn2_w
                            };
                            ui.add_space((total_width - used_width) / 2.0);

                            // 切换按钮
                            if show_switch {
                                if ui.add_sized([switch_w, 36.0], egui::Button::new(lang::t("btn_switch_to_online", lang))).clicked() {
                                    for inst in state.local_instances.iter_mut() {
                                        inst.is_current = false;
                                    }
                                    let ver = state.online_installed_version.clone().unwrap_or_default();
                                    save_current_to_settings("builtin", None, &ver, settings);
                                }
                            }

                            if state.npm_install_failed {
                                if ui.add_sized([btn1_w, 36.0], egui::Button::new(lang::t("btn_reinstall_deps", lang))).clicked() {
                                    download_target = Some((latest.zipball_url.clone(), latest.tag_name.clone(), true));
                                }
                            } else if !is_installed {
                                if ui.add_sized([btn1_w, 36.0], egui::Button::new(lang::t("btn_download_and_install", lang))).clicked() {
                                    download_target = Some((latest.zipball_url.clone(), latest.tag_name.clone(), false));
                                }
                            } else {
                                if ui.add_sized([btn1_w, 36.0], egui::Button::new(lang::t("btn_update_to_latest", lang))).clicked() {
                                    if let Some(ver) = &state.online_installed_version {
                                        let mut local_clean = ver.clone();
                                        if local_clean.starts_with('v') {
                                            local_clean = local_clean[1..].to_string();
                                        }
                                        let mut latest_clean = latest.tag_name.clone();
                                        if latest_clean.starts_with('v') {
                                            latest_clean = latest_clean[1..].to_string();
                                        }

                                        if local_clean == latest_clean {
                                            state.show_already_latest = true;
                                        } else {
                                            state.update_target = Some((latest.zipball_url.clone(), latest.tag_name.clone()));
                                            state.show_update_confirm = true;
                                        }
                                    } else {
                                        state.update_target = Some((latest.zipball_url.clone(), latest.tag_name.clone()));
                                        state.show_update_confirm = true;
                                    }
                                }
                            }

                            if ui.add_sized([btn2_w, 36.0], egui::Button::new(lang::t("btn_install_other_versions", lang))).clicked() {
                                state.show_other_versions = true;
                            }
                        });
                    });
                });
        }

        if fetch_now {
            state.fetch_releases(true, settings);
        }

        if let Some((url, version, skip_clone)) = download_target {
            start_install(state, &url, &version, settings, skip_clone);
        }
    });

    // 下载进度窗口
    if state.is_downloading {
        let mut show = true;
        let mut close_clicked = false;

        if let Some(finish_time) = state.download_finished_time {
            if finish_time.elapsed().as_secs() >= 3 {
                close_clicked = true;
            } else {
                ui.ctx().request_repaint();
            }
        }

        egui::Window::new(lang::t("btn_download_and_install", lang))
            .open(&mut show)
            .collapsible(false)
            .resizable(true)
            .default_size([600.0, 400.0])
            .anchor(egui::Align2::CENTER_CENTER, [0.0, 0.0])
            .show(ui.ctx(), |ui| {
                ui.vertical_centered(|ui| {
                    ui.label(egui::RichText::new(egui_phosphor::regular::BEER_BOTTLE).size(40.0));
                    ui.heading("SillyTavern");
                });

                ui.add_space(10.0);

                // Logs
                egui::Frame::NONE
                    .fill(ui.visuals().faint_bg_color)
                    .corner_radius(4.0)
                    .inner_margin(8.0)
                    .show(ui, |ui| {
                        egui::ScrollArea::vertical()
                            .max_height(200.0)
                            .auto_shrink([false, false])
                            .stick_to_bottom(true)
                            .show(ui, |ui| {
                                for log in &state.download_logs {
                                    ui.label(egui::RichText::new(log).size(12.0).family(egui::FontFamily::Monospace));
                                }
                            });
                    });

                ui.add_space(10.0);

                // Progress
                ui.add(egui::ProgressBar::new(state.download_progress).text(&state.download_status));

                if state.download_progress >= 1.0 || state.download_status.contains(lang::t("download_error", lang)) {
                    ui.add_space(10.0);
                    if ui.button(lang::t("btn_confirm", lang)).clicked() {
                        close_clicked = true;
                    }
                }
            });

        if !show || close_clicked {
            if state.download_progress < 1.0 && !state.download_status.contains(lang::t("download_error", lang)) {
                state.show_cancel_confirm = true;
            } else {
                state.is_downloading = false;
                state.download_finished_time = None;
            }
        }
    }

    // 取消下载确认
    if state.show_cancel_confirm {
        let mut confirm_open = true;
        egui::Window::new(lang::t("warning", lang))
            .open(&mut confirm_open)
            .collapsible(false)
            .resizable(false)
            .anchor(egui::Align2::CENTER_CENTER, [0.0, 0.0])
            .show(ui.ctx(), |ui| {
                ui.label(lang::t("confirm_cancel_download", lang));
                ui.add_space(10.0);
                ui.horizontal(|ui| {
                    if ui.button(lang::t("btn_confirm", lang)).clicked() {
                        state.is_downloading = false;
                        state.show_cancel_confirm = false;
                        // macOS: kill process group
                        if let Some(pid) = *state.active_pid.lock().unwrap() {
                            let _ = Command::new("kill")
                                .args(&["-9", &pid.to_string()])
                                .spawn();
                        }
                    }
                    if ui.button(lang::t("cancel", lang)).clicked() {
                        state.show_cancel_confirm = false;
                    }
                });
            });
        if !confirm_open {
            state.show_cancel_confirm = false;
        }
    }

    // 已是最新版本提示
    if state.show_already_latest {
        let mut show = true;
        let mut close_clicked = false;
        egui::Window::new(lang::t("info", lang))
            .open(&mut show)
            .collapsible(false)
            .resizable(false)
            .anchor(egui::Align2::CENTER_CENTER, [0.0, 0.0])
            .show(ui.ctx(), |ui| {
                ui.label(lang::t("already_latest", lang));
                ui.add_space(10.0);
                if ui.button(lang::t("btn_confirm", lang)).clicked() {
                    close_clicked = true;
                }
            });
        if !show || close_clicked {
            state.show_already_latest = false;
        }
    }

    // 更新确认
    if state.show_update_confirm {
        let mut show = true;
        let mut do_update = false;
        let mut close_clicked = false;
        egui::Window::new(lang::t("confirm_update", lang))
            .open(&mut show)
            .collapsible(false)
            .resizable(false)
            .anchor(egui::Align2::CENTER_CENTER, [0.0, 0.0])
            .show(ui.ctx(), |ui| {
                ui.label(lang::t("confirm_update_desc", lang));
                ui.add_space(10.0);
                ui.horizontal(|ui| {
                    if ui.button(lang::t("btn_confirm", lang)).clicked() {
                        do_update = true;
                        close_clicked = true;
                    }
                    if ui.button(lang::t("cancel", lang)).clicked() {
                        close_clicked = true;
                    }
                });
            });

        if do_update {
            if let Some((url, version)) = state.update_target.take() {
                start_install(state, &url, &version, settings, false);
            }
        }

        if !show || close_clicked {
            state.show_update_confirm = false;
        }
    }

    // 错误弹窗
    if let Some(err) = &state.install_error_alert {
        let mut show = true;
        let mut close_clicked = false;
        egui::Window::new(lang::t("warning", lang))
            .open(&mut show)
            .collapsible(false)
            .resizable(false)
            .anchor(egui::Align2::CENTER_CENTER, [0.0, 0.0])
            .show(ui.ctx(), |ui| {
                ui.label(egui::RichText::new(err).color(egui::Color32::RED));
                ui.add_space(10.0);
                if ui.button(lang::t("btn_confirm", lang)).clicked() {
                    close_clicked = true;
                }
            });
        if !show || close_clicked {
            state.install_error_alert = None;
        }
    }

    // 其他版本窗口
    if state.show_other_versions {
        let mut show = state.show_other_versions;
        egui::Window::new(lang::t("btn_install_other_versions", lang))
            .open(&mut show)
            .collapsible(false)
            .resizable(false)
            .anchor(egui::Align2::CENTER_CENTER, [0.0, 0.0])
            .show(ui.ctx(), |ui| {
                egui::ScrollArea::vertical().max_height(300.0).show(ui, |ui| {
                    let mut download_target = None;
                    let releases = state.recent_releases.clone();
                    for release in &releases {
                        ui.horizontal(|ui| {
                            ui.vertical(|ui| {
                                ui.label(egui::RichText::new(&release.tag_name).strong());
                                ui.label(&release.published_at);
                            });

                            ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                                let mut is_this_installed = false;
                                if let Some(installed_ver) = &state.online_installed_version {
                                    let mut local_clean = installed_ver.clone();
                                    if local_clean.starts_with('v') {
                                        local_clean = local_clean[1..].to_string();
                                    }
                                    let mut tag_clean = release.tag_name.clone();
                                    if tag_clean.starts_with('v') {
                                        tag_clean = tag_clean[1..].to_string();
                                    }
                                    if local_clean == tag_clean {
                                        is_this_installed = true;
                                    }
                                }

                                if is_this_installed {
                                    let is_current_online = settings.sillytavern.as_ref().map(|s| s.instance_type == "builtin").unwrap_or(false);
                                    if is_current_online {
                                        ui.add_enabled(false, egui::Button::new(lang::t("btn_installed", lang)));
                                    } else {
                                        if ui.button(lang::t("btn_switch_to_online", lang)).clicked() {
                                            for inst in state.local_instances.iter_mut() {
                                                inst.is_current = false;
                                            }
                                            let ver = state.online_installed_version.clone().unwrap_or_default();
                                            save_current_to_settings("builtin", None, &ver, settings);
                                        }
                                    }
                                } else {
                                    if ui.button(lang::t("btn_install", lang)).clicked() {
                                        download_target = Some((release.zipball_url.clone(), release.tag_name.clone(), false));
                                    }
                                }
                            });
                        });
                        ui.separator();
                    }
                    if let Some((url, version, skip_clone)) = download_target {
                        state.show_other_versions = false;
                        start_install(state, &url, &version, settings, skip_clone);
                    }
                });
            });
        state.show_other_versions = show;
    }
}

fn start_install(state: &mut VersionManageState, _url: &str, version: &str, settings: &mut SettingsState, skip_clone: bool) {
    // 1. 检查环境 — 根据 env_mode 使用内置或系统 PATH
    let is_builtin = settings.env_mode == EnvSource::Builtin;
    let git_path = if is_builtin {
        crate::core::env::get_builtin_git_path()
    } else {
        find_command("git")
    };
    let node_path = if is_builtin {
        crate::core::env::get_builtin_node_path()
    } else {
        find_command("node")
    };
    let npm_path = if is_builtin {
        crate::core::env::get_builtin_npm_path()
    } else {
        find_command("npm")
    };

    if git_path.is_none() || node_path.is_none() || npm_path.is_none() {
        state.install_error_alert = Some(lang::t("missing_env_warning", &settings.language).to_string());
        return;
    }

    let git_path = git_path.unwrap();
    let node_path = node_path.unwrap();
    let npm_path = npm_path.unwrap();

    let st_dir = utils::app_paths().sillytavern_dir();

    state.is_downloading = true;
    state.download_progress = 0.0;
    state.download_logs.clear();
    state.download_status = lang::t("status_preparing", &settings.language).to_string();
    state.download_finished_time = None;

    let (tx, rx) = mpsc::channel();
    state.download_receiver = Some(rx);

    let version_str = version.to_string();
    let target_dir = st_dir.clone();
    let active_pid = state.active_pid.clone();

    let github_proxy_url = if settings.github_proxy_enabled {
        Some(settings.github_proxy_url.clone())
    } else {
        None
    };

    let npm_registry = npm_registry_url(&settings.npm_registry).to_string();

    let repo_url = github_proxy_url
        .as_ref()
        .map(|proxy| format!("{}https://github.com/SillyTavern/SillyTavern.git", proxy))
        .unwrap_or_else(|| "https://github.com/SillyTavern/SillyTavern.git".to_string());

    thread::spawn(move || {
        let _ = tx.send(DownloadMsg::Log("Starting installation...".to_string()));

        if !skip_clone {
            let _ = tx.send(DownloadMsg::Progress(0.1, "Cloning repository...".to_string()));

            // 1. Git clone / fetch & checkout
            let is_git_repo = target_dir.join(".git").exists();
            if !target_dir.exists() || (target_dir.exists() && !is_git_repo) {
                // 目录不存在、或存在但不是 git 仓库 → 清理后重新 clone
                if target_dir.exists() {
                    let _ = tx.send(DownloadMsg::Log("Directory exists but not a git repo, removing...".to_string()));
                    let _ = fs::remove_dir_all(&target_dir);
                }
                let _ = fs::create_dir_all(&target_dir);
                let mut git_cmd = Command::new(&git_path);
                git_cmd.arg("clone")
                       .arg("-b").arg(&version_str)
                       .arg("--depth").arg("1")
                       .arg("--progress")
                       .arg(&repo_url)
                       .arg(&target_dir)
                       .stdout(std::process::Stdio::piped())
                       .stderr(std::process::Stdio::piped());

                let _ = tx.send(DownloadMsg::Log(format!("Executing: {:?}", git_cmd)));

                let mut child = match git_cmd.spawn() {
                    Ok(c) => c,
                    Err(e) => {
                        let _ = tx.send(DownloadMsg::Error(format!("Git clone failed: {}", e)));
                        return;
                    }
                };

                *active_pid.lock().unwrap() = Some(child.id());

                if let Some(stderr) = child.stderr.take() {
                    use std::io::{BufRead, BufReader};
                    let reader = BufReader::new(stderr);
                    for line in reader.lines() {
                        if let Ok(l) = line {
                            let _ = tx.send(DownloadMsg::Log(l));
                        }
                    }
                }

                let status = child.wait().unwrap_or_else(|_| std::process::ExitStatus::default());
                *active_pid.lock().unwrap() = None;
                if !status.success() {
                    let _ = tx.send(DownloadMsg::Error("Git clone failed.".to_string()));
                    return;
                }
            } else {
                // 目录已存在，fetch + checkout
                let _ = tx.send(DownloadMsg::Log("Directory exists, fetching updates...".to_string()));

                // 更新 remote URL，确保走代理加速（如果启用）
                let remote_url = github_proxy_url
                    .as_ref()
                    .map(|p| format!("{}https://github.com/SillyTavern/SillyTavern.git", p))
                    .unwrap_or_else(|| "https://github.com/SillyTavern/SillyTavern.git".to_string());
                let _ = tx.send(DownloadMsg::Log(format!("Setting remote origin to: {}", remote_url)));
                let _ = Command::new(&git_path)
                    .arg("remote")
                    .arg("set-url")
                    .arg("origin")
                    .arg(&remote_url)
                    .current_dir(&target_dir)
                    .output();

                // Fetch
                let mut git_fetch = Command::new(&git_path);
                git_fetch.arg("fetch")
                         .arg("origin")
                         .arg(&version_str)
                         .arg("--depth").arg("1")
                         .arg("--progress")
                         .current_dir(&target_dir)
                         .stdout(std::process::Stdio::piped())
                         .stderr(std::process::Stdio::piped());

                let _ = tx.send(DownloadMsg::Log(format!("Executing: {:?}", git_fetch)));

                let mut child = match git_fetch.spawn() {
                    Ok(c) => c,
                    Err(e) => {
                        let _ = tx.send(DownloadMsg::Error(format!("Git fetch failed: {}", e)));
                        return;
                    }
                };

                *active_pid.lock().unwrap() = Some(child.id());
                if let Some(stderr) = child.stderr.take() {
                    use std::io::{BufRead, BufReader};
                    let reader = BufReader::new(stderr);
                    for line in reader.lines() {
                        if let Ok(l) = line {
                            let _ = tx.send(DownloadMsg::Log(l));
                        }
                    }
                }
                let status = child.wait().unwrap_or_else(|_| std::process::ExitStatus::default());
                *active_pid.lock().unwrap() = None;
                if !status.success() {
                    let _ = tx.send(DownloadMsg::Error("Git fetch failed.".to_string()));
                    return;
                }

                // Checkout
                let mut git_checkout = Command::new(&git_path);
                git_checkout.arg("checkout")
                            .arg("-B").arg(&version_str)
                            .arg("FETCH_HEAD")
                            .current_dir(&target_dir)
                            .stdout(std::process::Stdio::piped())
                            .stderr(std::process::Stdio::piped());

                let _ = tx.send(DownloadMsg::Log(format!("Executing: {:?}", git_checkout)));
                let mut child = match git_checkout.spawn() {
                    Ok(c) => c,
                    Err(e) => {
                        let _ = tx.send(DownloadMsg::Error(format!("Git checkout failed: {}", e)));
                        return;
                    }
                };
                *active_pid.lock().unwrap() = Some(child.id());
                if let Some(stderr) = child.stderr.take() {
                    use std::io::{BufRead, BufReader};
                    let reader = BufReader::new(stderr);
                    for line in reader.lines() {
                        if let Ok(l) = line {
                            let _ = tx.send(DownloadMsg::Log(l));
                        }
                    }
                }
                let status = child.wait().unwrap_or_else(|_| std::process::ExitStatus::default());
                *active_pid.lock().unwrap() = None;
                if !status.success() {
                    let _ = tx.send(DownloadMsg::Error("Git checkout failed.".to_string()));
                    return;
                }
            }
        }

        // 2. NPM install
        let _ = tx.send(DownloadMsg::Progress(0.5, "Installing dependencies...".to_string()));

        let mut npm_cmd = Command::new(&npm_path);
        npm_cmd.env("NODE_ENV", "production");

        // Ensure node is in PATH for npm
        if let Some(node_dir) = node_path.parent() {
            let separator = if cfg!(target_os = "windows") { ";" } else { ":" };
            if let Ok(path_val) = std::env::var("PATH") {
                let mut new_path = std::ffi::OsString::new();
                new_path.push(node_dir);
                new_path.push(separator);
                new_path.push(path_val);
                npm_cmd.env("PATH", new_path);
            }
        }

        npm_cmd.arg("install")
               .arg("--no-save")
               .arg("--omit=dev")
               .arg("--no-audit")
               .arg("--no-fund")
               .arg("--progress")
               .arg("--foreground-scripts")
               .arg("--verbose")
               .arg("--registry")
               .arg(&npm_registry)
               .current_dir(&target_dir)
               .stdout(std::process::Stdio::piped())
               .stderr(std::process::Stdio::piped());

        let _ = tx.send(DownloadMsg::Log(format!("Executing: {:?}", npm_cmd)));

        let mut child = match npm_cmd.spawn() {
            Ok(c) => c,
            Err(e) => {
                let _ = tx.send(DownloadMsg::NpmError {
                    error: format!("NPM install failed: {}", e),
                    version: version_str,
                    path: target_dir.to_string_lossy().to_string(),
                });
                return;
            }
        };

        *active_pid.lock().unwrap() = Some(child.id());

        let stdout = child.stdout.take();
        let stderr = child.stderr.take();

        let tx_out = tx.clone();
        if let Some(out) = stdout {
            thread::spawn(move || {
                use std::io::{BufRead, BufReader};
                let reader = BufReader::new(out);
                for line in reader.lines() {
                    if let Ok(l) = line {
                        let _ = tx_out.send(DownloadMsg::Log(l));
                    }
                }
            });
        }

        let tx_err = tx.clone();
        if let Some(err) = stderr {
            thread::spawn(move || {
                use std::io::{BufRead, BufReader};
                let reader = BufReader::new(err);
                for line in reader.lines() {
                    if let Ok(l) = line {
                        let _ = tx_err.send(DownloadMsg::Log(l));
                    }
                }
            });
        }

        let status = child.wait().unwrap_or_else(|_| std::process::ExitStatus::default());
        *active_pid.lock().unwrap() = None;
        if !status.success() {
            let _ = tx.send(DownloadMsg::NpmError {
                error: "NPM install failed.".to_string(),
                version: version_str,
                path: target_dir.to_string_lossy().to_string(),
            });
            return;
        }

        let _ = tx.send(DownloadMsg::Progress(1.0, "Finished".to_string()));
        let _ = tx.send(DownloadMsg::Finished {
            version: version_str,
            path: target_dir.to_string_lossy().to_string(),
        });
    });
}
