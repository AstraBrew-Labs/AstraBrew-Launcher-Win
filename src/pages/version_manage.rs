use eframe::egui;
use serde::{Deserialize, Serialize};
use std::sync::mpsc::{self, Receiver};
use std::thread;
use std::time::{SystemTime, UNIX_EPOCH};
use std::fs;
use std::path::PathBuf;

use crate::lang;
use crate::pages::settings::{Language, SettingsState};

#[derive(PartialEq, Clone, Copy)]
pub enum VersionTab {
    Local,
    Online,
}

#[derive(Clone)]
pub struct LocalInstance {
    pub version: String,
    pub path: String,
    pub is_current: bool,
    pub is_online: bool,
}

#[derive(Serialize, Deserialize, Clone)]
pub struct GithubRelease {
    pub name: Option<String>,
    pub tag_name: String,
    pub published_at: String,
    pub zipball_url: String,
    // There are assets, but zipball_url is available in the root of release
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
        }
    }

    pub fn fetch_releases(&mut self, force: bool, settings: &SettingsState) {
        if self.is_fetching_releases {
            return;
        }

        // Cache logic
        let cache_path = get_cache_path();
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
                .user_agent("AstraBrew-Launcher");
                
            // Configure proxy based on settings
            use crate::pages::settings::ProxyType;
            match proxy_type {
                ProxyType::None => {
                    client_builder = client_builder.no_proxy();
                }
                ProxyType::System => {
                    // Try to fetch system proxy if enabled
                    if let Some((proxy_url, enabled)) = crate::core::network::read_windows_system_proxy() {
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
                ProxyType::Custom => {
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
            
            // Re-build client with headers
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

fn get_cache_path() -> PathBuf {
    let mut current_exe = std::env::current_exe().unwrap_or_else(|_| PathBuf::from("."));
    current_exe.pop(); // remove executable name
    let path_str = current_exe.to_string_lossy();
    let mut root = if path_str.contains("target\\debug") || path_str.contains("target\\release") {
        let mut p = current_exe.clone();
        p.pop();
        p.pop();
        p
    } else {
        current_exe
    };
    root.push("data");
    root.push("releases_cache.json");
    root
}

pub fn render(ui: &mut egui::Ui, state: &mut VersionManageState, settings: &SettingsState) {
    let lang = &settings.language;
    
    // Check if sillytavern is already installed by looking at data/sillytavern/package.json
    if state.online_installed_version.is_none() {
        let mut st_dir = crate::core::env::get_data_dir();
        st_dir.push("sillytavern");
        let mut pkg_path = st_dir.clone();
        pkg_path.push("package.json");
        
        if st_dir.exists() && pkg_path.exists() {
            // Read package.json to get the exact version
            let mut local_ver = "Local Installed".to_string();
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
                        let path = get_cache_path();
                        if let Some(parent) = path.parent() {
                            let _ = fs::create_dir_all(parent);
                        }
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
                DownloadMsg::Finished { version, path } => {
                    state.download_progress = 1.0;
                    state.download_status = lang::t("download_finished", lang).to_string();
                    state.online_installed_version = Some(version.clone());
                    state.npm_install_failed = false;
                    state.download_finished_time = Some(std::time::Instant::now());
                    
                    // Remove old online instance
                    state.local_instances.retain(|inst| !inst.is_online);
                    
                    // Auto switch to this version
                    state.local_instances.push(LocalInstance {
                        version,
                        path,
                        is_current: true,
                        is_online: true,
                    });
                    // Set others to not current
                    let len = state.local_instances.len();
                    for i in 0..len - 1 {
                        state.local_instances[i].is_current = false;
                    }
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

    ui.horizontal(|ui| {
        ui.selectable_value(&mut state.active_tab, VersionTab::Local, lang::t("tab_local_instances", lang));
        ui.selectable_value(&mut state.active_tab, VersionTab::Online, lang::t("tab_online_download", lang));
    });
    
    ui.separator();

    match state.active_tab {
        VersionTab::Local => {
            render_local_tab(ui, state, lang);
        }
        VersionTab::Online => {
            render_online_tab(ui, state, settings);
        }
    }
}

fn render_local_tab(ui: &mut egui::Ui, state: &mut VersionManageState, lang: &Language) {
    ui.horizontal(|ui| {
        if ui.button(lang::t("btn_auto_scan", lang)).clicked() {
            // Scan logic
        }
        if ui.button(lang::t("btn_manual_add", lang)).clicked() {
            // Manual add logic
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
                        });
                        
                        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                            if instance.is_current {
                                ui.label(egui::RichText::new(lang::t("status_current_version", lang)).color(egui::Color32::GREEN));
                            } else {
                                if ui.button(lang::t("btn_switch_version", lang)).clicked() {
                                    switch_idx = Some(i);
                                }
                            }
                            
                            if ui.button(lang::t("btn_remove_list", lang)).clicked() {
                                remove_idx = Some(i);
                            }
                            
                            if ui.button(lang::t("btn_view_info", lang)).clicked() {
                                // View info logic
                            }
                        });
                    });
                });
            }
            
            if let Some(idx) = remove_idx {
                state.local_instances.remove(idx);
            }
            if let Some(idx) = switch_idx {
                for (i, inst) in state.local_instances.iter_mut().enumerate() {
                    inst.is_current = i == idx;
                }
            }
        }
    });
}

        fn render_online_tab(ui: &mut egui::Ui, state: &mut VersionManageState, settings: &SettingsState) {
    let lang = &settings.language;
    if state.latest_release.is_none() && !state.is_fetching_releases {
        state.fetch_releases(false, settings);
    }
    
    ui.vertical_centered(|ui| {
        ui.add_space(40.0);
        
        // 中间酒馆 LOGO (使用图标代替)
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
        if let Some(latest) = &state.latest_release {
            ui.horizontal(|ui| {
                // 将获取到的最新版本号及刷新按钮组合在一起
                ui.add_space(ui.available_width() / 2.0 - 100.0);
                ui.heading(format!("{}: {}", lang::t("latest_version", lang), latest.tag_name));
                
                if ui.button(egui_phosphor::regular::ARROWS_CLOCKWISE).on_hover_text(lang::t("btn_refresh", lang)).clicked() {
                    fetch_now = true;
                }
            });
            
            if let Some(name) = &latest.name {
                ui.label(name);
            }
            ui.label(format!("{}: {}", lang::t("published_at", lang), latest.published_at));
            
            ui.add_space(30.0);
            
            ui.horizontal(|ui| {
                // To center buttons perfectly, we use available_width and spacing calculation
                let total_width = ui.available_width();
                // Estimate buttons width (approximate)
                let is_installed = state.online_installed_version.is_some();
                let spacing = 8.0;
                let btn1_w = 120.0;
                let btn2_w = 120.0;
                let used_width = btn1_w + spacing + btn2_w;
                ui.add_space((total_width - used_width) / 2.0);
                
                if state.npm_install_failed {
                    if ui.button(lang::t("btn_reinstall_deps", lang)).clicked() {
                        download_target = Some((latest.zipball_url.clone(), latest.tag_name.clone(), true));
                    }
                } else if !is_installed {
                    if ui.button(lang::t("btn_download_and_install", lang)).clicked() {
                        download_target = Some((latest.zipball_url.clone(), latest.tag_name.clone(), false));
                    }
                } else {
                        let is_update_enabled = true; // Always enable the button so we can show "already latest" msg
                        
                        if ui.add_enabled(is_update_enabled, egui::Button::new(lang::t("btn_update_to_latest", lang))).clicked() {
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
                
                if ui.button(lang::t("btn_install_other_versions", lang)).clicked() {
                    state.show_other_versions = true;
                }
            });
        }
        
        if fetch_now {
            state.fetch_releases(true, settings);
        }
        
        if let Some((url, version, skip_clone)) = download_target {
            start_install(state, &url, &version, settings, skip_clone);
        }
    });
    
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
                        if let Some(pid) = *state.active_pid.lock().unwrap() {
                            use std::os::windows::process::CommandExt;
                            let _ = std::process::Command::new("taskkill")
                                .args(&["/F", "/T", "/PID", &pid.to_string()])
                                .creation_flags(0x08000000)
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
                    for release in &state.recent_releases {
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
                                    ui.add_enabled(false, egui::Button::new(lang::t("btn_installed", lang)));
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

fn start_install(state: &mut VersionManageState, _url: &str, version: &str, settings: &SettingsState, skip_clone: bool) {
    // 1. Check Env
    let git_path = match settings.git_env {
        crate::pages::settings::EnvSource::Builtin => crate::core::env::get_builtin_git_path(),
        crate::pages::settings::EnvSource::System => crate::core::env::get_system_cmd_path("git"),
    };
    let node_path = match settings.nodejs_env {
        crate::pages::settings::EnvSource::Builtin => crate::core::env::get_builtin_node_path(),
        crate::pages::settings::EnvSource::System => crate::core::env::get_system_cmd_path("node"),
    };
    let npm_path = match settings.nodejs_env {
        crate::pages::settings::EnvSource::Builtin => crate::core::env::get_builtin_npm_path(),
        crate::pages::settings::EnvSource::System => crate::core::env::get_system_cmd_path("npm"),
    };

    if git_path.is_none() || node_path.is_none() || npm_path.is_none() {
        state.install_error_alert = Some(lang::t("missing_env_warning", &settings.language).to_string());
        return;
    }

    let git_path = git_path.unwrap();
    let node_path = node_path.unwrap();
    let npm_path = npm_path.unwrap();
    
    let mut st_dir = crate::core::env::get_data_dir();
    st_dir.push("sillytavern");
    
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
    
    let repo_url = if let Some(proxy) = github_proxy_url {
        format!("{}https://github.com/SillyTavern/SillyTavern.git", proxy)
    } else {
        "https://github.com/SillyTavern/SillyTavern.git".to_string()
    };
    
    thread::spawn(move || {
        let _ = tx.send(DownloadMsg::Log("Starting installation...".to_string()));
        
        if !skip_clone {
            let _ = tx.send(DownloadMsg::Progress(0.1, "Cloning repository...".to_string()));
            
            // 1. Git clone / fetch & checkout
            if !target_dir.exists() {
                let _ = fs::create_dir_all(&target_dir);
                let mut git_cmd = std::process::Command::new(&git_path);
                git_cmd.arg("clone")
                       .arg("-b").arg(&version_str)
                       .arg("--depth").arg("1")
                       .arg("--progress")
                       .arg(&repo_url)
                       .arg(&target_dir)
                       .creation_flags(0x08000000) // CREATE_NO_WINDOW
                       .stdout(std::process::Stdio::piped())
                       .stderr(std::process::Stdio::piped());
                       
                let _ = tx.send(DownloadMsg::Log(format!("Executing: {:?}", git_cmd)));
                
                use std::os::windows::process::CommandExt;
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
                // Fetch and checkout to the specific version instead of removing dir
                let _ = tx.send(DownloadMsg::Log("Directory exists, fetching updates...".to_string()));
                
                // Fetch
                let mut git_fetch = std::process::Command::new(&git_path);
                git_fetch.arg("fetch")
                         .arg("origin")
                         .arg(&version_str)
                         .arg("--depth").arg("1")
                         .arg("--progress")
                         .current_dir(&target_dir)
                         .creation_flags(0x08000000)
                         .stdout(std::process::Stdio::piped())
                         .stderr(std::process::Stdio::piped());
                
                let _ = tx.send(DownloadMsg::Log(format!("Executing: {:?}", git_fetch)));
                
                use std::os::windows::process::CommandExt;
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
                let mut git_checkout = std::process::Command::new(&git_path);
                git_checkout.arg("checkout")
                            .arg("-B").arg(&version_str)
                            .arg("FETCH_HEAD")
                            .current_dir(&target_dir)
                            .creation_flags(0x08000000)
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
        
        let mut npm_cmd = std::process::Command::new(&npm_path);
        npm_cmd.env("NODE_ENV", "production");
        
        if let Some(node_dir) = node_path.parent() {
            if let Some(path_val) = std::env::var_os("PATH") {
                let mut new_path = std::ffi::OsString::new();
                new_path.push(node_dir);
                new_path.push(";");
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
               .current_dir(&target_dir)
               .creation_flags(0x08000000)
               .stdout(std::process::Stdio::piped())
               .stderr(std::process::Stdio::piped());
               
        let _ = tx.send(DownloadMsg::Log(format!("Executing: {:?}", npm_cmd)));
        
        use std::os::windows::process::CommandExt;
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
