use eframe::egui;
use serde::{Deserialize, Serialize};
use std::sync::mpsc::{self, Receiver};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::thread;
use std::time::{SystemTime, UNIX_EPOCH};
use std::fs;
use std::io::BufRead;
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
    Finished,
}

#[derive(Clone, Copy, PartialEq)]
pub enum ScanMode {
    Quick,
    Full,
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
    /// 点击自动扫描后先让用户选择模式
    pub show_scan_mode_dialog: bool,
    /// 点击自动扫描时若无 FDA 权限，弹窗提示
    pub show_fda_dialog: bool,
    /// 待启动的扫描模式（帧末执行，None=空闲）
    pub pending_scan: Option<ScanMode>,
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
            show_scan_mode_dialog: false,
            show_fda_dialog: false,
            pending_scan: None,
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

/// 全盘扫描时跳过的目录名（macOS 版，大小写不敏感）
///
/// 注意：jwalk 无法在遍历前裁剪子树，因此需要在遍历线程中对
/// `$HOME` 下级目录做预过滤，直接跳过整个排除目录，避免无效遍历。
const SCAN_EXCLUDED_DIRS: &[&str] = &[
    // 系统/隐藏
    ".Trash",
    ".DocumentRevisions-V100",
    ".fseventsd",
    ".Spotlight-V100",
    ".TemporaryItems",
    ".VolumeIcon.icns",
    ".PKInstallSandboxManager",
    ".vol",
    "System",
    "private",
    "usr",
    "bin",
    "sbin",
    "opt",
    "dev",
    "cores",
    "Volumes",
    // macOS 用户目录下大概率无代码的文件夹
    "Library",
    "Movies",
    "Music",
    "Pictures",
    "Public",
    "Applications",
    // 开发工具缓存 / 大型依赖目录
    "node_modules",
    ".git",
    ".npm",
    ".cargo",
    ".cache",
    ".vscode",
    // 虚拟机（通常很大）
    "Parallels",
    "Virtual Machines",
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
                ScanMsg::Finished => {
                    state.is_scanning = false;
                    state.cancel_scan_flag = None;
                    state.scan_finished_time = Some(std::time::Instant::now());
                }
            }
        }
    }

    // 扫描完成后3秒自动隐藏提示
    if let Some(finished_at) = state.scan_finished_time {
        if finished_at.elapsed().as_secs() >= 3 {
            state.show_scan_tips = false;
            state.scanning_paths.clear();
            state.scan_finished_time = None;
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
                state.show_scan_mode_dialog = true;
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
            let latest_path = state.scanning_paths.last().map(|p| truncate_path_mid(p, 60)).unwrap_or_default();
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
        }
    });

    // --- 完全磁盘访问权限提示 ---
    if !state.is_scanning && !crate::core::app_permissions::is_full_disk_access_granted() {
        ui.add_space(4.0);
        egui::Frame::NONE
            .fill(egui::Color32::from_rgb(60, 45, 20))
            .corner_radius(6)
            .inner_margin(egui::Margin::symmetric(10, 6))
            .show(ui, |ui| {
                ui.horizontal(|ui| {
                    ui.add(
                        egui::Label::new(
                            egui::RichText::new(egui_phosphor::regular::WARNING_CIRCLE)
                                .size(16.0)
                                .color(egui::Color32::from_rgb(255, 200, 60)),
                        )
                        .selectable(false),
                    );
                    ui.add_space(4.0);
                    ui.vertical(|ui| {
                        ui.add_space(1.0);
                        ui.label(
                            egui::RichText::new(lang::t("fda_access_title", lang))
                                .size(13.0)
                                .strong()
                                .color(egui::Color32::from_rgb(255, 220, 120)),
                        );
                        ui.label(
                            egui::RichText::new(lang::t("fda_access_desc", lang))
                                .size(11.0)
                                .color(egui::Color32::from_rgb(200, 190, 170)),
                        );
                    });
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        if ui
                            .small_button(lang::t("fda_access_button", lang))
                            .clicked()
                        {
                            crate::core::app_permissions::open_full_disk_access_settings();
                        }
                    });
                });
            });
        ui.add_space(4.0);
    } else {
        ui.add_space(10.0);
    }

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
                                let _ = Command::new("open").arg(&instance.path).spawn();
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

    // --- 扫描模式选择弹窗 ---
    if state.show_scan_mode_dialog {
        let mut open = true;
        egui::Window::new(lang::t("scan_mode_title", lang))
            .open(&mut open)
            .collapsible(false)
            .resizable(false)
            .anchor(egui::Align2::CENTER_CENTER, [0.0, 0.0])
            .show(ui.ctx(), |ui| {
                ui.label(lang::t("scan_mode_desc", lang));
                ui.add_space(10.0);
                ui.horizontal(|ui| {
                    if ui.button(lang::t("quick_scan", lang))
                        .on_hover_text(lang::t("quick_scan_desc", lang))
                        .clicked()
                    {
                        state.show_scan_mode_dialog = false;
                        state.pending_scan = Some(ScanMode::Quick);
                    }
                    if ui.button(lang::t("full_scan", lang))
                        .on_hover_text(lang::t("full_scan_desc", lang))
                        .clicked()
                    {
                        state.show_scan_mode_dialog = false;
                        let perms = crate::core::app_permissions::probe_scan_permissions();
                        if perms.all_ok() {
                            state.pending_scan = Some(ScanMode::Full);
                        } else {
                            state.show_fda_dialog = true;
                        }
                    }
                });
            });
        if !open {
            state.show_scan_mode_dialog = false;
        }
    }

    // --- FDA 权限弹窗（仅全盘扫描时触发） ---
    if state.show_fda_dialog {
        let mut open = true;
        egui::Window::new(lang::t("fda_access_title", lang))
            .open(&mut open)
            .collapsible(false)
            .resizable(false)
            .anchor(egui::Align2::CENTER_CENTER, [0.0, 0.0])
            .show(ui.ctx(), |ui| {
                ui.label(lang::t("fda_access_dialog_desc", lang));
                ui.add_space(10.0);
                ui.horizontal(|ui| {
                    if ui.button(lang::t("fda_access_button", lang)).clicked() {
                        crate::core::app_permissions::open_full_disk_access_settings();
                        state.show_fda_dialog = false;
                        state.pending_scan = None;
                    }
                    if ui.button(lang::t("continue_anyway", lang)).clicked() {
                        state.show_fda_dialog = false;
                        state.pending_scan = Some(ScanMode::Full);
                    }
                });
            });
        if !open {
            state.show_fda_dialog = false;
            state.pending_scan = None;
        }
    }

    // --- 帧末启动扫描线程 ---
    if state.pending_scan.is_some() && !state.is_scanning && !state.show_scan_mode_dialog && !state.show_fda_dialog {
        let mode = state.pending_scan.take().unwrap();
        match mode {
            ScanMode::Quick => start_quick_scan(state, settings),
            ScanMode::Full => start_full_scan(state, settings),
        }
    }
}

/// 全盘扫描：jwalk 多线程遍历 $HOME 下所有非排除目录，匹配 package.json
fn start_full_scan(state: &mut VersionManageState, settings: &SettingsState) {
    state.is_scanning = true;
    state.scanning_paths.clear();
    state.show_scan_tips = true;
    state.scan_finished_time = None;
    let (tx, rx) = mpsc::channel();
    state.scan_receiver = Some(rx);
    let cpu_cores = settings.cpu_cores.clone();
    let cancel_flag = Arc::new(AtomicBool::new(false));
    state.cancel_scan_flag = Some(cancel_flag.clone());
    thread::spawn(move || {
        let cores = std::thread::available_parallelism().map(|n| n.get()).unwrap_or(1);
        let threads = match cpu_cores {
            crate::pages::settings::CpuCores::Auto => std::cmp::max(1, cores.saturating_sub(2)),
            crate::pages::settings::CpuCores::Half => std::cmp::max(1, cores / 2),
            crate::pages::settings::CpuCores::All => cores,
        };
        let home = PathBuf::from(std::env::var("HOME").unwrap_or_else(|_| "/tmp".into()));
        let _ = tx.send(ScanMsg::ScanningPath(home.to_string_lossy().to_string()));
        // 预过滤顶层目录，直接跳过排除目录
        let top_dirs: Vec<PathBuf> = match std::fs::read_dir(&home) {
            Ok(entries) => entries
                .filter_map(|e| e.ok())
                .filter(|e| e.path().is_dir() && !is_path_excluded(&e.path()))
                .map(|e| e.path())
                .collect(),
            Err(_) => vec![home.clone()],
        };
        if top_dirs.is_empty() {
            let _ = tx.send(ScanMsg::Finished);
            return;
        }
        for dir in &top_dirs {
            if cancel_flag.load(Ordering::Relaxed) {
                let _ = tx.send(ScanMsg::Finished);
                return;
            }
            let walk = jwalk::WalkDir::new(dir)
                .parallelism(jwalk::Parallelism::RayonNewPool(threads))
                .skip_hidden(false);
            for entry in walk.into_iter().filter_map(|e| e.ok()) {
                if cancel_flag.load(Ordering::Relaxed) {
                    let _ = tx.send(ScanMsg::Finished);
                    return;
                }
                if is_path_excluded(&entry.path()) {
                    continue;
                }
                if entry.file_name() == "package.json" {
                    let path = entry.path();
                    let dir_path = path.parent()
                        .map(|p| p.to_string_lossy().to_string())
                        .unwrap_or_else(|| path.to_string_lossy().to_string());
                    let _ = tx.send(ScanMsg::ScanningPath(dir_path));
                    if let Ok(content) = fs::read_to_string(&path) {
                        if let Ok(json) = serde_json::from_str::<serde_json::Value>(&content) {
                            if let Some(name) = json.get("name").and_then(|n| n.as_str()) {
                                if name == "sillytavern" {
                                    let version = json.get("version")
                                        .and_then(|v| v.as_str())
                                        .unwrap_or("Unknown")
                                        .to_string();
                                    let mut parent_path = path.clone();
                                    parent_path.pop();
                                    let path_str = parent_path.to_string_lossy().to_string();
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
        let _ = tx.send(ScanMsg::Finished);
    });
}

/// 快速扫描：用系统 find 命令定位 package.json，跳过星酿自带酒馆
fn start_quick_scan(state: &mut VersionManageState, _settings: &SettingsState) {
    state.is_scanning = true;
    state.scanning_paths.clear();
    state.show_scan_tips = true;
    state.scan_finished_time = None;
    let (tx, rx) = mpsc::channel();
    state.scan_receiver = Some(rx);
    let cancel_flag = Arc::new(AtomicBool::new(false));
    state.cancel_scan_flag = Some(cancel_flag.clone());
    thread::spawn(move || {
        let home = std::env::var("HOME").unwrap_or_else(|_| "/tmp".into());
        let _ = tx.send(ScanMsg::ScanningPath(home.clone()));
        let prune_path = format!(
            "{}/Library/Application Support/AstraBrew Launcher/sillytavern",
            home
        );
        let mut child = match std::process::Command::new("find")
            .arg(&home)
            .arg("-path")
            .arg(&prune_path)
            .arg("-prune")
            .arg("-o")
            .arg("-type")
            .arg("f")
            .arg("-name")
            .arg("package.json")
            .arg("-print")
            .stdout(std::process::Stdio::piped())
            .stderr(std::process::Stdio::null())
            .spawn()
        {
            Ok(c) => c,
            Err(e) => {
                eprintln!("[quick_scan] find spawn failed: {}", e);
                let _ = tx.send(ScanMsg::Finished);
                return;
            }
        };
        let stdout = match child.stdout.take() {
            Some(s) => s,
            None => {
                let _ = tx.send(ScanMsg::Finished);
                return;
            }
        };
        let reader = std::io::BufReader::new(stdout);
        for line in reader.lines() {
            if cancel_flag.load(Ordering::Relaxed) {
                let _ = child.kill();
                let _ = tx.send(ScanMsg::Finished);
                return;
            }
            let path_str = match line {
                Ok(l) => l,
                Err(_) => continue,
            };
            let path = PathBuf::from(&path_str);
            let dir_path = path.parent()
                .map(|p| p.to_string_lossy().to_string())
                .unwrap_or_else(|| path_str.clone());
            let _ = tx.send(ScanMsg::ScanningPath(dir_path.clone()));
            if let Ok(content) = fs::read_to_string(&path) {
                if let Ok(json) = serde_json::from_str::<serde_json::Value>(&content) {
                    if let Some(name) = json.get("name").and_then(|n| n.as_str()) {
                        if name == "sillytavern" {
                            let version = json.get("version")
                                .and_then(|v| v.as_str())
                                .unwrap_or("Unknown")
                                .to_string();
                            let _ = tx.send(ScanMsg::Found(LocalInstance {
                                version,
                                path: dir_path,
                                is_current: false,
                                is_online: false,
                            }));
                        }
                    }
                }
            }
        }
        let _ = child.wait();
        let _ = tx.send(ScanMsg::Finished);
    });
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
