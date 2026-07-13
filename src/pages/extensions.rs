use eframe::egui;
use serde::{Deserialize, Serialize};
use std::collections::HashSet;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::mpsc::Receiver;
use std::thread;

use crate::lang;
use crate::pages::settings::Language;
use crate::utils;
use crate::ui::switch::toggle;
use std::io::{BufRead, BufReader};

/// 为扩展管理里的命令行工具统一附加无黑窗标志。
fn apply_hidden_command(cmd: &mut std::process::Command) {
    crate::core::env::apply_no_window_to_command(cmd);
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct ExtensionManifest {
    #[serde(default)]
    pub display_name: String,
    #[serde(rename = "homePage", default)]
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

#[derive(Clone, Debug)]
#[allow(dead_code)]
pub struct ExtensionInfo {
    pub id: String,
    pub path: PathBuf,
    pub manifest: ExtensionManifest,
    pub is_official: bool,
    pub is_enabled: bool,     // 仅UI展示
    pub modified_at: u64,     // Unix 时间戳（秒），用于排序
}

#[allow(dead_code)]
pub enum ExtensionMsg {
    Loaded(Vec<ExtensionInfo>),
    Error(String),
}

#[derive(PartialEq)]
pub enum AddDialogTab {
    Git,
    Offline,
}

#[derive(Clone, Debug)]
pub struct OfflinePackage {
    pub path: String,
    pub name: String,
    pub valid: Option<bool>,
    pub error: Option<String>,
}

pub struct ExtensionManageState {
    pub extensions: Vec<ExtensionInfo>,
    pub is_loading: bool,
    pub has_loaded: bool,
    pub error_msg: Option<String>,
    rx: Option<Receiver<ExtensionMsg>>,
    pub show_add_dialog: bool,
    pub add_dialog_tab: AddDialogTab,
    pub git_url: String,
    pub git_branches: Vec<String>,
    pub git_error: Option<String>,
    last_git_url_change: Option<std::time::Instant>,
    pub git_selected_branch: String,
    pub is_fetching_branches: bool,
    git_branches_rx: Option<Receiver<(Vec<String>, Option<String>)>>,
    pub is_installing_git: bool,
    pub git_install_log: String,
    git_install_done: Option<bool>,
    git_install_done_at: Option<std::time::Instant>,
    git_install_rx: Option<Receiver<String>>,
    last_fetched_git_url: String,
    pub offline_packages: Vec<OfflinePackage>,
    offline_check_rx: Option<Receiver<Vec<(usize, bool, Option<String>)>>>,
    pub is_checking_offline: bool,
    pub selected_extensions: HashSet<String>,
    pub current_page: usize,
    pub page_size: usize,
    pub batch_mode: bool,
    pub show_system_extensions: bool,
    pub needs_refresh: bool,
    pub show_overwrite_confirm: bool,
    pub overwrite_packages: Vec<OfflinePackage>,
    pub github_proxy_enabled: bool,
    pub github_proxy_url: String,
    pub show_force_install: bool,
    pub force_install_url: String,
    pub force_install_branch: String,
    pub show_non_github_confirm: bool,
    pub non_github_skip_manifest_check: bool,
    git_temp_dir: Option<std::path::PathBuf>,
    git_target_dir: Option<std::path::PathBuf>,
    // 修复 Git 环境
    pub show_fix_git_dialog: bool,
    pub fix_git_extension_name: String,
    pub fix_git_ext_path: std::path::PathBuf,
    pub fix_git_remote_url: String,
    pub fix_git_status: String,
    fix_git_rx: Option<Receiver<String>>,
    fix_git_success: Option<bool>,
    fix_git_success_at: Option<std::time::Instant>,
}

impl ExtensionManageState {
    pub fn new() -> Self {
        Self {
            extensions: Vec::new(),
            is_loading: false,
            has_loaded: false,
            error_msg: None,
            rx: None,
            show_add_dialog: false,
            add_dialog_tab: AddDialogTab::Git,
            git_url: String::new(),
            git_branches: Vec::new(),
            git_error: None,
            last_git_url_change: None,
            git_selected_branch: String::new(),
            is_fetching_branches: false,
            git_branches_rx: None,
            is_installing_git: false,
            git_install_log: String::new(),
            git_install_done: None,
            git_install_done_at: None,
            git_install_rx: None,
            last_fetched_git_url: String::new(),
            offline_packages: Vec::new(),
            offline_check_rx: None,
            is_checking_offline: false,
            selected_extensions: HashSet::new(),
            current_page: 0,
            page_size: 10,
            batch_mode: false,
            show_system_extensions: false,
            needs_refresh: false,
            show_overwrite_confirm: false,
            overwrite_packages: Vec::new(),
            github_proxy_enabled: false,
            github_proxy_url: String::new(),
            show_force_install: false,
            force_install_url: String::new(),
            force_install_branch: String::new(),
            show_non_github_confirm: false,
            non_github_skip_manifest_check: false,
            git_temp_dir: None,
            git_target_dir: None,
            show_fix_git_dialog: false,
            fix_git_extension_name: String::new(),
            fix_git_ext_path: std::path::PathBuf::new(),
            fix_git_remote_url: String::new(),
            fix_git_status: String::new(),
            fix_git_rx: None,
            fix_git_success: None,
            fix_git_success_at: None,
        }
    }

    pub fn load_extensions(&mut self, instance_path: Option<&str>) {
        let (tx, rx) = std::sync::mpsc::channel();
        self.rx = Some(rx);
        self.is_loading = true;
        self.error_msg = None;

        let base_path = match instance_path {
            Some(p) if !p.is_empty() => PathBuf::from(p),
            _ => utils::app_paths().sillytavern_dir(),
        };

        thread::spawn(move || {
            let mut results = Vec::new();
            
            // 官方扩展目录
            let official_dir = base_path.join("public").join("scripts").join("extensions");
            // 第三方扩展目录
            let third_party_dir = official_dir.join("third-party");

            // 读取第三方扩展
            if third_party_dir.exists() && third_party_dir.is_dir() {
                if let Ok(entries) = fs::read_dir(&third_party_dir) {
                    for entry in entries.flatten() {
                        let path = entry.path();
                        if path.is_dir() {
                            if let Some(ext) = Self::parse_extension(&path, false) {
                                results.push(ext);
                            }
                        }
                    }
                }
            }

            // 读取官方扩展
            if official_dir.exists() && official_dir.is_dir() {
                if let Ok(entries) = fs::read_dir(&official_dir) {
                    for entry in entries.flatten() {
                        let path = entry.path();
                        if path.is_dir() && path.file_name().unwrap_or_default() != "third-party" {
                            if let Some(ext) = Self::parse_extension(&path, true) {
                                results.push(ext);
                            }
                        }
                    }
                }
            }

            // 排序：第三方在前（is_official=false），官方在后（is_official=true）
            // 同一组内按修改时间从新到旧排列
            results.sort_by(|a, b| {
                // 按 is_official 分组（false < true → 第三方在前）
                match a.is_official.cmp(&b.is_official) {
                    std::cmp::Ordering::Equal => b.modified_at.cmp(&a.modified_at), // 时间倒序
                    other => other,
                }
            });

            let _ = tx.send(ExtensionMsg::Loaded(results));
        });
    }

    fn parse_extension(dir: &Path, is_official: bool) -> Option<ExtensionInfo> {
        let json_path = dir.join("manifest.json");
        let disabled_path = dir.join("manifest.json.disable");

        // 根据实际文件判断启用状态：.json 存在 = 启用，.disable 存在 = 禁用
        let (read_path, is_enabled) = if json_path.exists() {
            (json_path, true)
        } else if disabled_path.exists() {
            (disabled_path, false)
        } else {
            return None;
        };

        let content = fs::read_to_string(&read_path).ok()?;
        let mut manifest: ExtensionManifest = serde_json::from_str(&content).unwrap_or_else(|_| ExtensionManifest {
            display_name: dir.file_name().unwrap_or_default().to_string_lossy().to_string(),
            home_page: String::new(),
            version: String::new(),
            author: String::new(),
            auto_update: None,
            minimum_client_version: String::new(),
        });

        if manifest.display_name.is_empty() {
            manifest.display_name = dir.file_name().unwrap_or_default().to_string_lossy().to_string();
        }

        let id = dir.file_name().unwrap_or_default().to_string_lossy().to_string();

        // 获取目录修改时间（用于排序）
        let modified_at = fs::metadata(dir)
            .ok()
            .and_then(|m| m.modified().ok())
            .and_then(|t| t.duration_since(std::time::UNIX_EPOCH).ok())
            .map(|d| d.as_secs())
            .unwrap_or(0);

        Some(ExtensionInfo {
            id,
            path: dir.to_path_buf(),
            manifest,
            is_official,
            is_enabled,
            modified_at,
        })
    }

    pub fn poll(&mut self) {
        if let Some(rx) = &self.rx {
            if let Ok(msg) = rx.try_recv() {
                match msg {
                    ExtensionMsg::Loaded(exts) => {
                        self.extensions = exts;
                        self.is_loading = false;
                        self.has_loaded = true;
                        self.current_page = 0; // 加载新数据后回到第一页
                    }
                    ExtensionMsg::Error(err) => {
                        self.error_msg = Some(err);
                        self.is_loading = false;
                        self.has_loaded = true;
                    }
                }
                self.rx = None;
            }
        }

        // 轮询离线包检查结果
        if let Some(rx) = &self.offline_check_rx {
            if let Ok(results) = rx.try_recv() {
                for (index, valid, error) in results {
                    if let Some(pkg) = self.offline_packages.get_mut(index) {
                        pkg.valid = Some(valid);
                        pkg.error = error;
                    }
                }
                self.is_checking_offline = false;
                self.offline_check_rx = None;
            }
        }

        // 轮询 git 分支获取结果
        if let Some(rx) = &self.git_branches_rx {
            if let Ok((branches, error)) = rx.try_recv() {
                self.git_branches = branches;
                self.is_fetching_branches = false;
                self.git_branches_rx = None;

                if let Some(err) = error {
                    self.git_error = Some(err);
                }

                // 默认选择 main/master/第一个
                self.git_selected_branch = if self.git_branches.iter().any(|b| b == "main") {
                    "main".to_string()
                } else if self.git_branches.iter().any(|b| b == "master") {
                    "master".to_string()
                } else {
                    self.git_branches.first().cloned().unwrap_or_default()
                };
            }
        }

        // 轮询 git 安装日志（克隆到临时目录）
        if let Some(rx) = &self.git_install_rx {
            let mut done = false;
            while let Ok(line) = rx.try_recv() {
                if line == "__DONE__" {
                    done = true;
                } else if line == "__ERROR__" {
                    self.is_installing_git = false;
                    self.git_install_done = Some(false);
                    // 清理临时目录
                    if let Some(temp) = self.git_temp_dir.take() {
                        let _ = fs::remove_dir_all(&temp);
                    }
                    self.git_target_dir = None;
                    done = true;
                } else {
                    self.git_install_log.push_str(&line);
                    self.git_install_log.push('\n');
                }
            }
            if done {
                self.git_install_rx = None;
                // 验证临时目录中的扩展
                if let Some(temp) = &self.git_temp_dir {
                    let skip_check = self.non_github_skip_manifest_check;
                    let has_manifest = temp.join("manifest.json").exists();
                    let should_install = skip_check || has_manifest;

                    if should_install {
                        if let Some(target) = &self.git_target_dir.clone() {
                            if skip_check {
                                self.git_install_log.push_str("\n→ 非 GitHub 仓库，跳过 API 检测，正在移动文件...\n");
                            } else {
                                self.git_install_log.push_str("\n✓ 检测到 manifest.json，正在移动文件...\n");
                            }
                            if let Err(e) = fs::rename(temp, target) {
                                self.git_install_log.push_str(&format!("✗ 移动文件失败: {}\n", e));
                                let _ = fs::remove_dir_all(temp);
                                self.is_installing_git = false;
                                self.git_install_done = Some(false);
                            } else {
                                self.git_install_log.push_str("✓ 扩展安装成功\n");
                                self.is_installing_git = false;
                                self.git_install_done = Some(true);
                                self.git_install_done_at = Some(std::time::Instant::now());
                            }
                        }
                    } else {
                        // 未检测到 manifest.json，提示用户（仅 GitHub 仓库走此分支）
                        self.is_installing_git = false;
                        self.git_install_log = "✗ 未检测到 manifest.json，该仓库可能不是有效的扩展仓库。".to_string();
                        self.show_force_install = true;
                    }
                    self.git_temp_dir = None;
                    self.git_target_dir = None;
                    self.non_github_skip_manifest_check = false;
                }
            }
        }

        // 安装成功后 3 秒自动关闭
        if self.git_install_done == Some(true) {
            if let Some(at) = self.git_install_done_at {
                if at.elapsed().as_secs() >= 3 {
                    self.show_add_dialog = false;
                    self.git_install_done = None;
                    self.git_install_done_at = None;
                    self.is_installing_git = false;
                    self.git_install_log.clear();
                    self.git_install_rx = None;
                    self.needs_refresh = true;
                }
            }
        }

        // 轮询修复 Git 日志
        if let Some(rx) = &self.fix_git_rx {
            while let Ok(line) = rx.try_recv() {
                if line == "__DONE__" {
                    self.fix_git_success = Some(true);
                    self.fix_git_success_at = Some(std::time::Instant::now());
                } else if line == "__ERROR__" {
                    self.fix_git_success = Some(false);
                } else {
                    self.fix_git_status = line;
                }
            }
            if self.fix_git_success.is_some() {
                self.fix_git_rx = None;
            }
        }

        // 修复 Git 成功后 3 秒自动关闭弹窗
        if self.fix_git_success == Some(true) {
            if let Some(at) = self.fix_git_success_at {
                if at.elapsed().as_secs() >= 3 {
                    self.show_fix_git_dialog = false;
                    self.fix_git_success = None;
                    self.fix_git_success_at = None;
                    self.fix_git_status.clear();
                    self.fix_git_rx = None;
                }
            }
        }
    }
}

fn start_fix_git(state: &mut ExtensionManageState) {
    let path = state.fix_git_ext_path.clone();
    let remote_url = state.fix_git_remote_url.clone();
    let (tx, rx) = std::sync::mpsc::channel();
    state.fix_git_rx = Some(rx);
    state.fix_git_success = None;
    state.fix_git_success_at = None;
    state.fix_git_status = String::new();

    std::thread::spawn(move || {
        // Step 1: git init
        let _ = tx.send(String::new()); // trigger status update
        let mut git_init = std::process::Command::new("git");
        apply_hidden_command(&mut git_init);
        let output = git_init.arg("init")
            .current_dir(&path)
            .output();

        match output {
            Ok(o) if o.status.success() => {
                let _ = tx.send("__STATUS_init__".to_string());
            }
            Ok(o) => {
                let err = String::from_utf8_lossy(&o.stderr).to_string();
                let _ = tx.send(format!("git init 失败: {}", err));
                let _ = tx.send("__ERROR__".to_string());
                return;
            }
            Err(e) => {
                let _ = tx.send(format!("git init 失败: {}", e));
                let _ = tx.send("__ERROR__".to_string());
                return;
            }
        }

        // Step 2: git remote add origin
        let _ = tx.send("__STATUS_remote__".to_string());
        let mut git_remote = std::process::Command::new("git");
        apply_hidden_command(&mut git_remote);
        let output = git_remote.args(["remote", "add", "origin", &remote_url])
            .current_dir(&path)
            .output();

        match output {
            Ok(o) if o.status.success() => {
                let _ = tx.send("__DONE__".to_string());
            }
            Ok(o) => {
                let err = String::from_utf8_lossy(&o.stderr).to_string();
                let _ = tx.send(format!("git remote add 失败: {}", err));
                let _ = tx.send("__ERROR__".to_string());
            }
            Err(e) => {
                let _ = tx.send(format!("git remote add 失败: {}", e));
                let _ = tx.send("__ERROR__".to_string());
            }
        }
    });
}

pub fn render(ui: &mut egui::Ui, state: &mut ExtensionManageState, lang: &Language, instance_path: Option<&str>) {
    state.poll();

    // 无实例选中时，显示提示信息
    let has_instance = instance_path.map_or(false, |p| !p.is_empty());
    if !has_instance {
        ui.heading(lang::t("extension_manage", lang));
        ui.separator();
        ui.add_space(40.0);
        ui.vertical_centered(|ui| {
            ui.label(
                egui::RichText::new(lang::t("ext_no_instance", lang))
                    .color(egui::Color32::GRAY)
                    .size(14.0),
            );
            ui.label(
                egui::RichText::new(lang::t("ext_no_instance_hint", lang))
                    .color(egui::Color32::GRAY)
                    .size(12.0),
            );
        });
        return;
    }

    // 计算可见扩展索引（受「显示系统扩展」开关控制）
    let visible_indices: Vec<usize> = state
        .extensions
        .iter()
        .enumerate()
        .filter(|(_, e)| state.show_system_extensions || !e.is_official)
        .map(|(i, _)| i)
        .collect();
    let total_visible = visible_indices.len();

    ui.horizontal(|ui| {
        ui.heading(lang::t("extension_manage", lang));
        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
            if ui.button(lang::t("btn_refresh", lang)).clicked() {
                state.load_extensions(instance_path);
            }
            ui.add_space(4.0);
            // 显示系统扩展开关
            ui.horizontal(|ui| {
                ui.spacing_mut().item_spacing.x = 4.0;
                ui.label(
                    egui::RichText::new(lang::t("ext_show_system", lang)).size(12.0),
                );
                ui.add(toggle(&mut state.show_system_extensions));
            });
        });
    });
    
    ui.separator();

    // ---- 工具栏 ----
    render_toolbar(ui, state, lang, &visible_indices);
    ui.add_space(4.0);

    // ---- 添加扩展弹窗（必须在所有 return 之前，确保弹窗始终可响应） ----
    render_add_dialog(ui, state, lang);

    // ---- 覆盖确认弹窗 ----
    if state.show_overwrite_confirm {
        render_overwrite_confirm(ui, state, lang);
    }

    // ---- 强制安装确认弹窗 ----
    if state.show_force_install {
        render_force_install_confirm(ui, state, lang);
    }

    // ---- 非 GitHub 仓库确认弹窗 ----
    if state.show_non_github_confirm {
        render_non_github_confirm(ui, state, lang);
    }

    // ---- 修复 Git 弹窗 ----
    if state.show_fix_git_dialog {
        render_fix_git_dialog(ui, state, lang);
    }

    // 离线安装成功后触发刷新
    if state.needs_refresh {
        state.needs_refresh = false;
        state.load_extensions(instance_path);
    }

    // 离线包检查中持续重绘
    if state.is_checking_offline || state.is_installing_git || state.is_fetching_branches || state.is_loading || state.fix_git_rx.is_some() {
        ui.ctx().request_repaint();
    }

    // 防抖等待中持续重绘
    if state.last_git_url_change.is_some()
        && !state.is_fetching_branches
        && state.git_branches.is_empty()
        && state.git_error.is_none()
    {
        ui.ctx().request_repaint();
    }

    // 安装成功后持续渲染（3 秒倒计时）
    if state.git_install_done == Some(true) {
        ui.ctx().request_repaint();
    }

    // 修复 Git 运行中/成功后持续渲染
    if state.fix_git_rx.is_some() || state.fix_git_success == Some(true) {
        ui.ctx().request_repaint();
    }

    if state.is_loading {
        ui.centered_and_justified(|ui| {
            ui.spinner();
        });
        return;
    }

    if let Some(err) = &state.error_msg {
        ui.colored_label(egui::Color32::RED, err);
        return;
    }

    if total_visible == 0 {
        let available = ui.available_size();
        let content_height = 80.0;
        let top_offset = (available.y - content_height) / 2.0;

        ui.vertical(|ui| {
            ui.add_space(top_offset.max(40.0));

            ui.vertical_centered(|ui| {
                ui.label(
                    egui::RichText::new(egui_phosphor::regular::PACKAGE)
                        .size(48.0)
                        .color(egui::Color32::from_gray(100)),
                );

                ui.add_space(12.0);

                ui.label(
                    egui::RichText::new(lang::t("no_extensions_found", lang))
                        .size(16.0)
                        .color(egui::Color32::GRAY),
                );
            });
        });
        return;
    }

    // 切换可见性时钳位页码
    let total_pages = if total_visible == 0 {
        0
    } else {
        (total_visible + state.page_size - 1) / state.page_size
    };
    if state.current_page >= total_pages {
        state.current_page = total_pages.saturating_sub(1);
    }

    // ---- 扩展卡片网格（2列，分页显示） ----
    // 为底部分页栏预留 32px 高度
    let pagination_height = if total_pages > 1 { 32.0 } else { 0.0 };
    let scroll_height = (ui.available_height() - pagination_height).max(0.0);

    let mut needs_refresh = false;
    let mut fix_git_trigger: Option<(String, std::path::PathBuf, String)> = None;

    egui::ScrollArea::vertical()
        .max_height(scroll_height)
        .auto_shrink([false, true])
        .show(ui, |ui| {
            egui::Frame::NONE
                .inner_margin(egui::Margin::symmetric(4, 8))
                .show(ui, |ui| {
                    let max_columns = 2;
                    let spacing = 16.0;
                    let available_width = ui.available_width();
                    let item_width = ((available_width - spacing * (max_columns as f32 - 1.0))
                        / max_columns as f32)
                        .floor();

                    egui::Grid::new("extensions_grid")
                        .spacing([spacing, spacing])
                        .min_col_width(item_width)
                        .max_col_width(item_width)
                        .show(ui, |ui| {
                            let start = (state.current_page * state.page_size).min(total_visible);
                            let end = ((state.current_page + 1) * state.page_size).min(total_visible);
                            let page_indices = &visible_indices[start..end];

                            for (col_idx, &idx) in page_indices.iter().enumerate() {
                                if col_idx > 0 && col_idx % max_columns == 0 {
                                    ui.end_row();
                                }

                                render_extension_card(
                                    ui, &mut state.extensions[idx], item_width, lang,
                                    &mut state.selected_extensions,
                                    state.batch_mode,
                                    &mut needs_refresh,
                                    &mut fix_git_trigger,
                                );
                            }
                        });
                });
        });

    // 处理修复 Git 触发
    if let Some((name, path, remote_url)) = fix_git_trigger {
        state.show_fix_git_dialog = true;
        state.fix_git_extension_name = name;
        state.fix_git_ext_path = path;
        state.fix_git_remote_url = remote_url;
        start_fix_git(state);
    }

    // 删除扩展后标记需要刷新
    if needs_refresh {
        state.needs_refresh = true;
    }

    // ---- 底部分页栏 ----
    if total_pages > 1 {
        render_pagination_bar(ui, state, total_visible);
    }
}

// ============ 工具栏 ============

fn render_toolbar(ui: &mut egui::Ui, state: &mut ExtensionManageState, lang: &Language, visible_indices: &[usize]) {
    ui.horizontal(|ui| {
        if state.batch_mode {
            // ---- 批量管理模式 ----
            let has_selection = !state.selected_extensions.is_empty();
            // 仅统计当前可见扩展中全部被选中的情况
            let all_visible_selected = !visible_indices.is_empty()
                && visible_indices
                    .iter()
                    .all(|&i| state.selected_extensions.contains(&state.extensions[i].id));

            let select_label = if all_visible_selected {
                lang::t("ext_batch_deselect_all", lang)
            } else {
                lang::t("ext_batch_select_all", lang)
            };
            if ui.button(select_label).clicked() {
                if all_visible_selected {
                    for &i in visible_indices {
                        state.selected_extensions.remove(&state.extensions[i].id);
                    }
                } else {
                    for &i in visible_indices {
                        state.selected_extensions.insert(state.extensions[i].id.clone());
                    }
                }
            }

            if ui.add_enabled(has_selection, egui::Button::new(lang::t("ext_batch_disable", lang))).clicked() {
                for ext in state.extensions.iter_mut() {
                    if state.selected_extensions.contains(&ext.id) && !ext.is_official {
                        set_extension_enabled(&ext.path, false);
                        ext.is_enabled = false;
                    }
                }
            }

            if ui.add_enabled(has_selection, egui::Button::new(lang::t("ext_batch_enable", lang))).clicked() {
                for ext in state.extensions.iter_mut() {
                    if state.selected_extensions.contains(&ext.id) && !ext.is_official {
                        set_extension_enabled(&ext.path, true);
                        ext.is_enabled = true;
                    }
                }
            }

            // 清除选中
            if has_selection {
                if ui.button("✕").clicked() {
                    state.selected_extensions.clear();
                }
            }

            ui.separator();

            // 退出批量模式
            if ui.button(lang::t("ext_batch_edit_exit", lang)).clicked() {
                state.batch_mode = false;
                state.selected_extensions.clear();
            }
        } else {
            // ---- 普通模式：仅显示 "批量编辑" 按钮 ----
            if ui.button(lang::t("ext_batch_edit", lang)).clicked() {
                state.batch_mode = true;
            }
        }

        // -- 右侧：添加扩展按钮（始终显示） --
        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
            let icon = egui_phosphor::regular::PLUS;
            if ui.button(format!(" {}  {}", icon, lang::t("ext_add_extension", lang))).clicked() {
                state.show_add_dialog = true;
                state.git_url.clear();
                state.git_error = None;
                state.offline_packages.clear();
                state.add_dialog_tab = AddDialogTab::Git;
            }
        });
    });
}

// ============ 分页栏 ============

fn render_pagination_bar(ui: &mut egui::Ui, state: &mut ExtensionManageState, total_visible: usize) {
    let total = (total_visible + state.page_size - 1) / state.page_size;
    if total <= 1 {
        return;
    }

    ui.horizontal(|ui| {
        ui.spacing_mut().item_spacing.x = 4.0;

        // ◀ 上一页
        let prev_enabled = state.current_page > 0;
        if ui
            .add_enabled(
                prev_enabled,
                egui::Button::new(egui::RichText::new(egui_phosphor::regular::CARET_LEFT).size(14.0)),
            )
            .clicked()
        {
            state.current_page -= 1;
        }

        ui.add_space(4.0);

        // 页码按钮
        let max_visible = 7usize;
        if total <= max_visible {
            for p in 0..total {
                render_page_button(ui, state, p);
            }
        } else {
            // 总是显示第一页
            render_page_button(ui, state, 0);

            let window_start = state.current_page.saturating_sub(2).max(1);
            let window_end = (state.current_page + 2).min(total - 2);

            // 前面省略号
            if window_start > 1 {
                ui.add_sized(
                    [24.0, 20.0],
                    egui::Label::new(
                        egui::RichText::new("…").color(egui::Color32::GRAY),
                    )
                    .selectable(false),
                );
            }

            // 中间窗口
            for p in window_start..=window_end {
                render_page_button(ui, state, p);
            }

            // 后面省略号
            if window_end < total - 2 {
                ui.add_sized(
                    [24.0, 20.0],
                    egui::Label::new(
                        egui::RichText::new("…").color(egui::Color32::GRAY),
                    )
                    .selectable(false),
                );
            }

            // 总是显示最后一页
            render_page_button(ui, state, total - 1);
        }

        ui.add_space(4.0);

        // ▶ 下一页
        let next_enabled = state.current_page + 1 < total;
        if ui
            .add_enabled(
                next_enabled,
                egui::Button::new(egui::RichText::new(egui_phosphor::regular::CARET_RIGHT).size(14.0)),
            )
            .clicked()
        {
            state.current_page += 1;
        }

        // 总数
        ui.add_space(8.0);
        ui.label(
            egui::RichText::new(format!("共 {} 个", total_visible))
                .size(12.0)
                .color(egui::Color32::GRAY),
        );
    });
}

fn render_page_button(ui: &mut egui::Ui, state: &mut ExtensionManageState, page: usize) {
    let is_current = page == state.current_page;
    let resp = ui.add_sized(
        [24.0, 20.0],
        egui::Button::selectable(is_current, (page + 1).to_string()),
    );
    if resp.clicked() {
        state.current_page = page;
    }
}

// ============ 添加扩展弹窗 ============

fn render_add_dialog(ui: &mut egui::Ui, state: &mut ExtensionManageState, lang: &Language) {
    if !state.show_add_dialog {
        return;
    }

    let mut was_open = true;

    egui::Window::new(lang::t("ext_add_dialog_title", lang))
        .collapsible(false)
        .resizable(false)
        .fixed_size([480.0, 260.0])
        .anchor(egui::Align2::CENTER_CENTER, [0.0, 0.0])
        .open(&mut was_open)
        .show(ui.ctx(), |ui| {
            // ---- Tab 切换栏 ----
            ui.horizontal(|ui| {
                let git_sel = state.add_dialog_tab == AddDialogTab::Git;
                if ui.selectable_label(git_sel, lang::t("ext_add_git_tab", lang)).clicked() {
                    state.add_dialog_tab = AddDialogTab::Git;
                }

                let off_sel = state.add_dialog_tab == AddDialogTab::Offline;
                if ui.selectable_label(off_sel, lang::t("ext_add_offline_tab", lang)).clicked() {
                    state.add_dialog_tab = AddDialogTab::Offline;
                }
            });

            ui.separator();
            ui.add_space(8.0);

            // ---- Tab 内容 ----
            match state.add_dialog_tab {
                AddDialogTab::Git => {
                    let input_disabled = state.is_installing_git || state.is_fetching_branches;

                    ui.label(lang::t("ext_add_git_url", lang));
                    ui.add_space(4.0);

                    // Git URL 输入框（安装/获取分支中禁用）
                    let mut url_changed = false;
                    if input_disabled {
                        ui.add_enabled(false, egui::TextEdit::singleline(&mut state.git_url)
                            .hint_text(lang::t("ext_add_git_placeholder", lang))
                            .desired_width(f32::INFINITY));
                    } else {
                        url_changed = ui.add(
                            egui::TextEdit::singleline(&mut state.git_url)
                                .hint_text(lang::t("ext_add_git_placeholder", lang))
                                .desired_width(f32::INFINITY),
                        ).changed();
                    }

                    // URL 变更时记录时间（防抖）
                    if url_changed && !state.git_url.trim().is_empty() {
                        state.git_branches.clear();
                        state.git_selected_branch.clear();
                        state.last_fetched_git_url.clear();
                        state.git_error = None;
                        state.last_git_url_change = Some(std::time::Instant::now());
                    }

                    // URL 审查 + 防抖 3 秒后触发获取
                    let url_trimmed = state.git_url.trim();
                    let is_valid_url = url_trimmed.starts_with("http://")
                        || url_trimmed.starts_with("https://")
                        || url_trimmed.starts_with("git@");

                    if !url_trimmed.is_empty() && !is_valid_url {
                        state.git_error = Some("请输入正确的 Git 仓库地址（http/https/git 协议）".to_string());
                    } else {
                        let elapsed = state.last_git_url_change
                            .map(|t| t.elapsed().as_secs())
                            .unwrap_or(0);
                        let debounce_ready = elapsed >= 3;

                        let need_fetch = !url_trimmed.is_empty()
                            && is_valid_url
                            && debounce_ready
                            && state.last_fetched_git_url != state.git_url
                            && !state.is_fetching_branches
                            && state.git_branches.is_empty();

                        if need_fetch {
                            fetch_git_branches(state);
                        }
                    }

                    // 错误提示（弹窗内）
                    if let Some(ref err) = state.git_error {
                        ui.add_space(4.0);
                        ui.label(
                            egui::RichText::new(err).color(egui::Color32::RED).size(12.0),
                        );
                    }

                    // 分支选择器
                    if state.is_fetching_branches {
                        ui.add_space(4.0);
                        ui.horizontal(|ui| {
                            ui.spinner();
                            ui.label("正在获取分支列表...");
                        });
                    } else if !state.git_branches.is_empty() {
                        ui.add_space(6.0);
                        ui.horizontal(|ui| {
                            ui.label("分支:");
                            egui::ComboBox::from_id_salt("git_branch_selector")
                                .selected_text(&state.git_selected_branch)
                                .show_ui(ui, |ui| {
                                    for branch in &state.git_branches.clone() {
                                        ui.selectable_value(
                                            &mut state.git_selected_branch,
                                            branch.clone(),
                                            branch,
                                        );
                                    }
                                });
                        });
                    }

                    // 安装日志区
                    if state.is_installing_git || state.git_install_done.is_some() {
                        ui.add_space(6.0);
                        ui.separator();
                        let status_text = if state.is_installing_git {
                            "安装中..."
                        } else if state.git_install_done == Some(true) {
                            "安装成功！3秒后自动关闭..."
                        } else {
                            "安装失败"
                        };
                        ui.label(egui::RichText::new(status_text).size(12.0));

                        let log_h = 100.0;
                        egui::ScrollArea::vertical()
                            .max_height(log_h)
                            .stick_to_bottom(true)
                            .show(ui, |ui| {
                                ui.add(
                                    egui::TextEdit::multiline(&mut state.git_install_log)
                                        .desired_width(f32::INFINITY)
                                        .font(egui::TextStyle::Monospace)
                                        .interactive(false),
                                );
                            });
                    }
                }
                AddDialogTab::Offline => {
                    // 选择压缩包按钮
                    ui.horizontal(|ui| {
                        if ui.button(lang::t("ext_browse", lang)).clicked() {
                            let title = lang::t("ext_add_offline_hint", lang);
                            let picked = rfd::FileDialog::new()
                                .set_title(title)
                                .add_filter("压缩包", &["zip"])
                                .pick_files();
                            if let Some(paths) = picked {
                                for path in paths {
                                    let name = path
                                        .file_name()
                                        .map(|n| n.to_string_lossy().to_string())
                                        .unwrap_or_default();
                                    state.offline_packages.push(OfflinePackage {
                                        path: path.to_string_lossy().to_string(),
                                        name,
                                        valid: None,
                                        error: None,
                                    });
                                }
                                // 异步批量检查
                                start_offline_check(state);
                            }
                        }
                        ui.add_space(6.0);
                        if state.is_checking_offline {
                            ui.spinner();
                            ui.label("检查中...");
                        }
                    });

                    ui.add_space(6.0);

                    // 包列表 + 状态
                    if state.offline_packages.is_empty() {
                        ui.label(
                            egui::RichText::new(lang::t("ext_add_offline_hint", lang))
                                .color(egui::Color32::GRAY)
                                .size(12.0),
                        );
                    } else {
                        let max_h = 140.0;
                        egui::ScrollArea::vertical()
                            .max_height(max_h)
                            .show(ui, |ui| {
                                // 移除按钮用到的索引
                                let mut to_remove = Vec::new();
                                for (i, pkg) in state.offline_packages.iter().enumerate() {
                                    ui.horizontal(|ui| {
                                        // 状态图标
                                        match pkg.valid {
                                            Some(true) => {
                                                ui.label(
                                                    egui::RichText::new("✓")
                                                        .color(egui::Color32::from_rgb(80, 220, 80))
                                                        .size(14.0),
                                                );
                                            }
                                            Some(false) => {
                                                ui.label(
                                                    egui::RichText::new("✗")
                                                        .color(egui::Color32::RED)
                                                        .size(14.0),
                                                );
                                            }
                                            None => {
                                                ui.add(
                                                    egui::Spinner::new()
                                                        .size(12.0),
                                                );
                                            }
                                        }

                                        // 文件名
                                        ui.label(
                                            egui::RichText::new(&pkg.name)
                                                .size(12.0),
                                        );

                                        // 错误信息
                                        if let Some(ref err) = pkg.error {
                                            ui.label(
                                                egui::RichText::new(err)
                                                    .color(egui::Color32::RED)
                                                    .size(11.0),
                                            );
                                        }

                                        // 移除按钮
                                        if pkg.valid.is_some() {
                                            if ui.button("✕").clicked() {
                                                to_remove.push(i);
                                            }
                                        }
                                    });
                                }
                                // 从后往前移除
                                for i in to_remove.iter().rev() {
                                    state.offline_packages.remove(*i);
                                }
                            });
                    }
                }
            }

            ui.add_space(12.0);
            ui.separator();

            // ---- 底部按钮 ----
            ui.horizontal(|ui| {
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if ui.button(lang::t("ext_add_cancel", lang)).clicked() {
                        state.show_add_dialog = false;
                    }
                    ui.add_space(8.0);

                    let valid_count = state.offline_packages.iter()
                        .filter(|p| p.valid == Some(true))
                        .count();
                    let can_confirm = match state.add_dialog_tab {
                        AddDialogTab::Git => !state.git_url.trim().is_empty() && !state.is_installing_git,
                        AddDialogTab::Offline => valid_count > 0 && !state.is_checking_offline,
                    };
                    if ui.add_enabled(can_confirm, egui::Button::new(lang::t("ext_add_confirm", lang))).clicked() {
                        if state.add_dialog_tab == AddDialogTab::Offline {
                            // 收集有效包
                            let valid_pkgs: Vec<OfflinePackage> = state.offline_packages.iter()
                                .filter(|p| p.valid == Some(true))
                                .cloned()
                                .collect();

                            if valid_pkgs.is_empty() {
                                // 不应到达这里（can_confirm 已保证 valid_count > 0）
                            } else {

                            // 检查是否有重复的扩展（已存在于 third-party 目录）
                            let base_path = utils::app_paths().sillytavern_dir();
                            let third_party = base_path
                                .join("public").join("scripts").join("extensions").join("third-party");
                            let mut conflicts: Vec<String> = Vec::new();
                            for pkg in &valid_pkgs {
                                if let Some(name) = get_extension_name_from_zip(&pkg.path) {
                                    if third_party.join(&name).exists() {
                                        conflicts.push(name);
                                    }
                                }
                            }

                            if conflicts.is_empty() {
                                // 无重复，直接安装
                                install_all_packages(state, &valid_pkgs);
                            } else {
                                // 有重复，弹出覆盖确认
                                state.overwrite_packages = valid_pkgs;
                                state.show_overwrite_confirm = true;
                            }
                            }
                        } else {
                            // Git 添加
                            if is_github_url(&state.git_url) {
                                state.non_github_skip_manifest_check = false;
                                start_git_install(state);
                            } else {
                                state.show_non_github_confirm = true;
                            }
                        }
                    }
                });
            });
        });

    if !was_open || !state.show_add_dialog {
        state.show_add_dialog = false;
        state.git_url.clear();
        state.git_branches.clear();
        state.git_error = None;
        state.git_selected_branch.clear();
        state.git_install_log.clear();
        state.git_install_done = None;
        // 清理临时目录
        if let Some(temp) = state.git_temp_dir.take() {
            let _ = fs::remove_dir_all(&temp);
        }
        state.git_target_dir = None;
        state.offline_packages.clear();
        state.overwrite_packages.clear();
        state.show_overwrite_confirm = false;
        state.show_force_install = false;
        state.show_non_github_confirm = false;
        state.non_github_skip_manifest_check = false;
        state.add_dialog_tab = AddDialogTab::Git;
    }
}

// ============ 覆盖确认弹窗 ============

fn render_overwrite_confirm(ui: &mut egui::Ui, state: &mut ExtensionManageState, lang: &Language) {
    let mut was_open = true;

    egui::Window::new(lang::t("ext_overwrite_title", lang))
        .collapsible(false)
        .resizable(false)
        .fixed_size([420.0, 200.0])
        .anchor(egui::Align2::CENTER_CENTER, [0.0, 0.0])
        .open(&mut was_open)
        .show(ui.ctx(), |ui| {
            ui.label(lang::t("ext_overwrite_msg", lang));
            ui.add_space(8.0);

            // 列出将要覆盖的扩展
            for pkg in &state.overwrite_packages {
                if let Some(name) = get_extension_name_from_zip(&pkg.path) {
                    ui.label(egui::RichText::new(format!("  • {}", name)).size(12.0));
                }
            }

            ui.add_space(12.0);
            ui.separator();

            ui.horizontal(|ui| {
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if ui.button(lang::t("ext_overwrite_cancel", lang)).clicked() {
                        state.show_overwrite_confirm = false;
                    }
                    ui.add_space(8.0);
                    if ui.button(lang::t("ext_overwrite_confirm", lang)).clicked() {
                        let packages: Vec<_> = state.overwrite_packages.clone();
                        install_all_packages(state, &packages);
                        state.show_overwrite_confirm = false;
                    }
                });
            });
        });

    if !was_open {
        state.show_overwrite_confirm = false;
        state.overwrite_packages.clear();
    }
}

// ============ 强制安装确认弹窗 ============

fn render_force_install_confirm(ui: &mut egui::Ui, state: &mut ExtensionManageState, _lang: &Language) {
    let mut was_open = true;

    egui::Window::new("强制添加")
        .collapsible(false)
        .resizable(false)
        .fixed_size([400.0, 180.0])
        .anchor(egui::Align2::CENTER_CENTER, [0.0, 0.0])
        .open(&mut was_open)
        .show(ui.ctx(), |ui| {
            ui.label("未检测到 manifest.json，该仓库可能不是有效的扩展仓库。");
            ui.label("是否要强制添加？");
            ui.add_space(4.0);
            ui.label(egui::RichText::new("注意：强制添加可能安装无效的扩展").size(11.0).color(egui::Color32::YELLOW));

            ui.add_space(12.0);
            ui.separator();

            ui.horizontal(|ui| {
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if ui.button("取消").clicked() {
                        state.show_force_install = false;
                        // 清理临时目录
                        if let Some(temp) = state.git_temp_dir.take() {
                            let _ = fs::remove_dir_all(&temp);
                        }
                        state.git_target_dir = None;
                        state.git_install_log.clear();
                    }
                    ui.add_space(8.0);
                    if ui.button("强制添加").clicked() {
                        state.show_force_install = false;
                        // 移动临时目录到目标
                        if let (Some(temp), Some(target)) = (state.git_temp_dir.take(), state.git_target_dir.take()) {
                            if let Err(e) = fs::rename(&temp, &target) {
                                state.git_install_log = format!("✗ 移动文件失败: {}\n", e);
                                state.git_install_done = Some(false);
                            } else {
                                state.git_install_log = "✓ 强制添加成功\n".to_string();
                                state.git_install_done = Some(true);
                                state.git_install_done_at = Some(std::time::Instant::now());
                            }
                        }
                    }
                });
            });
        });

    if !was_open {
        // 关闭弹窗时清理临时目录
        if let Some(temp) = state.git_temp_dir.take() {
            let _ = fs::remove_dir_all(&temp);
        }
        state.git_target_dir = None;
        state.show_force_install = false;
    }
}

// ============ 非 GitHub 仓库确认弹窗 ============

fn render_non_github_confirm(ui: &mut egui::Ui, state: &mut ExtensionManageState, lang: &Language) {
    let mut was_open = true;

    egui::Window::new(lang::t("ext_non_github_title", lang))
        .collapsible(false)
        .resizable(false)
        .fixed_size([400.0, 180.0])
        .anchor(egui::Align2::CENTER_CENTER, [0.0, 0.0])
        .open(&mut was_open)
        .show(ui.ctx(), |ui| {
            ui.label(lang::t("ext_non_github_msg", lang));
            ui.add_space(12.0);
            ui.separator();

            ui.horizontal(|ui| {
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if ui.button(lang::t("ext_non_github_cancel", lang)).clicked() {
                        state.show_non_github_confirm = false;
                    }
                    ui.add_space(8.0);
                    if ui.button(lang::t("ext_non_github_confirm", lang)).clicked() {
                        state.show_non_github_confirm = false;
                        state.non_github_skip_manifest_check = true;
                        start_git_install(state);
                    }
                });
            });
        });

    if !was_open {
        state.show_non_github_confirm = false;
    }
}

// ============ 修复 Git 环境弹窗 ============

fn render_fix_git_dialog(ui: &mut egui::Ui, state: &mut ExtensionManageState, lang: &Language) {
    let mut was_open = true;

    egui::Window::new(lang::t("ext_fix_git_title", lang))
        .collapsible(false)
        .resizable(false)
        .fixed_size([420.0, 200.0])
        .anchor(egui::Align2::CENTER_CENTER, [0.0, 0.0])
        .open(&mut was_open)
        .show(ui.ctx(), |ui| {
            ui.vertical_centered(|ui| {
                ui.add_space(16.0);

                match state.fix_git_success {
                    None => {
                        // 运行中
                        ui.add(egui::Spinner::new());
                        ui.add_space(12.0);
                        ui.label(egui::RichText::new(&state.fix_git_extension_name).strong().size(14.0));
                        ui.add_space(8.0);
                        // 根据状态码显示对应文本
                        if state.fix_git_status == "__STATUS_init__" {
                            ui.label(egui::RichText::new(lang::t("ext_fix_git_initializing", lang)).size(12.0));
                        } else if state.fix_git_status == "__STATUS_remote__" {
                            ui.label(egui::RichText::new(lang::t("ext_fix_git_adding_remote", lang)).size(12.0));
                        } else {
                            ui.label(egui::RichText::new(lang::t("ext_fix_git_initializing", lang)).size(12.0));
                        }
                    }
                    Some(true) => {
                        // 成功
                        ui.label(
                            egui::RichText::new(egui_phosphor::regular::CHECK_CIRCLE)
                                .size(36.0)
                                .color(egui::Color32::from_rgb(80, 200, 120)),
                        );
                        ui.add_space(12.0);
                        ui.label(egui::RichText::new(lang::t("ext_fix_git_success", lang)).size(14.0));
                    }
                    Some(false) => {
                        // 失败
                        ui.label(
                            egui::RichText::new(egui_phosphor::regular::X_CIRCLE)
                                .size(36.0)
                                .color(egui::Color32::from_rgb(220, 60, 60)),
                        );
                        ui.add_space(12.0);
                        ui.label(egui::RichText::new(lang::t("ext_fix_git_failed", lang)).size(14.0));
                        ui.add_space(8.0);
                        if !state.fix_git_status.is_empty() {
                            ui.label(
                                egui::RichText::new(&state.fix_git_status)
                                    .size(11.0)
                                    .color(egui::Color32::from_rgb(200, 80, 80)),
                            );
                        }
                        ui.add_space(12.0);
                        if ui.button(lang::t("ext_fix_git_close", lang)).clicked() {
                            state.show_fix_git_dialog = false;
                            state.fix_git_success = None;
                            state.fix_git_success_at = None;
                            state.fix_git_status.clear();
                            state.fix_git_rx = None;
                        }
                    }
                }
            });
        });

    if !was_open {
        state.show_fix_git_dialog = false;
        state.fix_git_success = None;
        state.fix_git_success_at = None;
        state.fix_git_status.clear();
        state.fix_git_rx = None;
    }
}

// ============ 离线包异步检查 ============

fn start_offline_check(state: &mut ExtensionManageState) {
    let packages: Vec<_> = state.offline_packages.iter()
        .enumerate()
        .filter(|(_, p)| p.valid.is_none())
        .map(|(i, p)| (i, p.path.clone()))
        .collect();

    if packages.is_empty() {
        return;
    }

    state.is_checking_offline = true;
    let (tx, rx) = std::sync::mpsc::channel();
    state.offline_check_rx = Some(rx);

    std::thread::spawn(move || {
        let mut results = Vec::new();
        for (index, path) in packages {
            let (valid, error) = check_zip_contains_manifest(&path);
            results.push((index, valid, error));
        }
        let _ = tx.send(results);
    });
}

fn check_zip_contains_manifest(path: &str) -> (bool, Option<String>) {
    let file = match fs::File::open(path) {
        Ok(f) => f,
        Err(e) => return (false, Some(format!("无法打开: {}", e))),
    };

    let reader = std::io::BufReader::new(file);
    let mut archive = match zip::ZipArchive::new(reader) {
        Ok(a) => a,
        Err(e) => return (false, Some(format!("无法解压: {}", e))),
    };

    for i in 0..archive.len() {
        if let Ok(entry) = archive.by_index(i) {
            let name = entry.name();
            let path = Path::new(name);
            if path.file_name().map(|f| f == "manifest.json").unwrap_or(false) {
                return (true, None);
            }
        }
    }

    (false, Some("未找到 manifest.json".to_string()))
}

// ============ 启用/禁用扩展 ============

/// 通过重命名 manifest.json / manifest.json.disable 来控制扩展启用状态
fn set_extension_enabled(dir: &Path, enabled: bool) {
    let json_path = dir.join("manifest.json");
    let disabled_path = dir.join("manifest.json.disable");

    if enabled {
        // 启用：manifest.json.disable → manifest.json
        if disabled_path.exists() && !json_path.exists() {
            let _ = fs::rename(&disabled_path, &json_path);
        }
    } else {
        // 禁用：manifest.json → manifest.json.disable
        if json_path.exists() {
            let _ = fs::rename(&json_path, &disabled_path);
        }
    }
}

// ============ 离线安装扩展 ============

fn install_offline_extension(zip_path: &str) -> Result<(), String> {
    use std::io::Read;
    use std::io::Write;

    let zip_path = Path::new(zip_path);
    let file = fs::File::open(zip_path)
        .map_err(|e| format!("无法打开压缩包: {}", e))?;

    let reader = std::io::BufReader::new(file);
    let mut archive = zip::ZipArchive::new(reader)
        .map_err(|e| format!("无法解压: {}", e))?;

    // 查找 manifest.json，确定扩展名称
    let mut extension_name: Option<String> = None;
    for i in 0..archive.len() {
        let entry = archive.by_index(i)
            .map_err(|e| format!("读取压缩包条目失败: {}", e))?;
        let name = entry.name().to_string();

        // 匹配根级别或一级子目录中的 manifest.json
        let path = Path::new(&name);
        if path.file_name().map(|f| f == "manifest.json").unwrap_or(false) {
            // 确定扩展名称：
            // 如果 manifest.json 在根级别 → 用压缩包文件名
            // 如果在一级子目录里 → 用子目录名
            extension_name = if let Some(parent) = path.parent() {
                let parent_str = parent.to_string_lossy();
                if parent_str.is_empty() || parent_str == "." || parent_str == "/" {
                    // 根级别 manifest.json
                    zip_path.file_stem()
                        .map(|s| s.to_string_lossy().to_string())
                } else {
                    // 子目录中
                    parent.iter().next()
                        .map(|p| p.to_string_lossy().to_string())
                }
            } else {
                zip_path.file_stem().map(|s| s.to_string_lossy().to_string())
            };
            break;
        }
    }

    let ext_name = extension_name.ok_or("请选择正确的扩展压缩包，需包含 manifest.json".to_string())?;

    // 确保名称合法（去掉路径分隔符）
    let ext_name = ext_name.replace(['/', '\\'], "_").trim().to_string();
    if ext_name.is_empty() {
        return Err("无法确定扩展名称".to_string());
    }

    // 计算目标目录
    let base_path = utils::app_paths().sillytavern_dir();
    let third_party = base_path
        .join("public")
        .join("scripts")
        .join("extensions")
        .join("third-party");
    let dest_dir = third_party.join(&ext_name);

    // 已存在则先删除
    if dest_dir.exists() {
        fs::remove_dir_all(&dest_dir)
            .map_err(|e| format!("无法删除旧版本: {}", e))?;
    }
    fs::create_dir_all(&dest_dir)
        .map_err(|e| format!("无法创建扩展目录: {}", e))?;

    // 确定 manifest.json 所在的前缀路径（需要去掉的公共前缀）
    let mut common_prefix = String::new();
    for i in 0..archive.len() {
        if let Ok(entry) = archive.by_index(i) {
            let name = entry.name().to_string();
            if name.ends_with("manifest.json") {
                let p = Path::new(&name);
                if let Some(parent) = p.parent() {
                    common_prefix = parent.to_string_lossy().to_string();
                    if common_prefix != "." && !common_prefix.is_empty() {
                        if !common_prefix.ends_with('/') && !common_prefix.ends_with('\\') {
                            common_prefix.push('/');
                        }
                    } else {
                        common_prefix = String::new();
                    }
                }
                break;
            }
        }
    }

    // 解压所有文件
    for i in 0..archive.len() {
        let mut entry = archive.by_index(i)
            .map_err(|e| format!("读取条目失败: {}", e))?;
        let name = entry.name().to_string();

        // 计算相对路径（去掉公共前缀）
        let relative = if name.starts_with(&common_prefix) && !common_prefix.is_empty() {
            name[common_prefix.len()..].to_string()
        } else {
            name.clone()
        };

        if relative.is_empty() || relative == "." {
            continue;
        }

        let target_path = dest_dir.join(&relative);

        if entry.is_dir() {
            fs::create_dir_all(&target_path).ok();
        } else {
            if let Some(parent) = target_path.parent() {
                fs::create_dir_all(parent).ok();
            }
            let mut content = Vec::new();
            entry.read_to_end(&mut content)
                .map_err(|e| format!("读取文件失败: {}", e))?;
            let mut out = fs::File::create(&target_path)
                .map_err(|e| format!("创建文件失败: {}", e))?;
            out.write_all(&content)
                .map_err(|e| format!("写入文件失败: {}", e))?;
        }
    }

    Ok(())
}

fn get_extension_name_from_zip(zip_path: &str) -> Option<String> {
    let file = fs::File::open(zip_path).ok()?;
    let reader = std::io::BufReader::new(file);
    let mut archive = zip::ZipArchive::new(reader).ok()?;

    for i in 0..archive.len() {
        if let Ok(entry) = archive.by_index(i) {
            let name = entry.name().to_string();
            let path = Path::new(&name);
            if path.file_name().map(|f| f == "manifest.json").unwrap_or(false) {
                if let Some(parent) = path.parent() {
                    let parent_str = parent.to_string_lossy();
                    if parent_str.is_empty() || parent_str == "." || parent_str == "/" {
                        return Path::new(zip_path).file_stem().map(|s| s.to_string_lossy().to_string());
                    } else {
                        return parent.iter().next().map(|p| p.to_string_lossy().to_string());
                    }
                }
                return Path::new(zip_path).file_stem().map(|s| s.to_string_lossy().to_string());
            }
        }
    }
    None
}

fn fetch_git_branches(state: &mut ExtensionManageState) {
    let url = state.git_url.trim().to_string();
    if url.is_empty() {
        return;
    }

    state.last_fetched_git_url = url.clone();
    state.is_fetching_branches = true;
    state.git_branches.clear();

    let (tx, rx) = std::sync::mpsc::channel();
    state.git_branches_rx = Some(rx);

    let proxy_url = state.github_proxy_url.clone();
    let proxy_enabled = state.github_proxy_enabled;
    let orig_url = url.clone();

    std::thread::spawn(move || {
        let result = try_fetch_branches(&orig_url, &proxy_url, proxy_enabled);
        let _ = tx.send(result);
    });
}

fn try_fetch_branches(url: &str, proxy: &str, proxy_enabled: bool) -> (Vec<String>, Option<String>) {
    fn run_ls_remote(target_url: &str) -> (Vec<String>, bool) {
        let mut branches = Vec::new();
        let mut git_ls_remote = std::process::Command::new("git");
        apply_hidden_command(&mut git_ls_remote);
        let output = git_ls_remote.args(["ls-remote", "--heads", target_url])
            .output();
        match output {
            Ok(o) if o.status.success() => {
                let stdout = String::from_utf8_lossy(&o.stdout);
                for line in stdout.lines() {
                    if let Some(pos) = line.find("refs/heads/") {
                        let branch = line[pos + 11..].trim().to_string();
                        if !branch.is_empty() && !branch.contains(' ') {
                            branches.push(branch);
                        }
                    }
                }
                (branches, true)
            }
            _ => (branches, false),
        }
    }

    // 尝试加速地址
    let proxied_url = if proxy_enabled && !proxy.is_empty() {
        apply_github_proxy(url, proxy)
    } else {
        String::new()
    };
    let is_proxied = !proxied_url.is_empty() && proxied_url != url;

    if is_proxied {
        let (branches, ok) = run_ls_remote(&proxied_url);
        if ok && !branches.is_empty() {
            return (branches, None);
        }
        // 回退到原始地址
        let (branches, ok) = run_ls_remote(url);
        if ok {
            return (branches, None);
        }
    } else {
        let (branches, ok) = run_ls_remote(url);
        if ok {
            return (branches, None);
        }
    }

    (Vec::new(), Some("无法获取分支列表，请检查网络或仓库地址".to_string()))
}

fn is_github_url(url: &str) -> bool {
    let lower = url.to_lowercase();
    // https://github.com/... 或 git@github.com:...
    lower.contains("://github.com") || lower.contains("@github.com:")
}

fn apply_github_proxy(url: &str, proxy: &str) -> String {
    if proxy.is_empty() {
        return url.to_string();
    }
    let lower = url.to_lowercase();
    if lower.contains("github.com") {
        format!("{}/{}", proxy.trim_end_matches('/'), url)
    } else {
        url.to_string()
    }
}

fn extract_repo_name(url: &str) -> String {
    let url = url.trim_end_matches('/').trim_end_matches(".git");
    url.rsplit('/').next().unwrap_or("extension").to_string()
}

fn try_git_clone(
    tx: &std::sync::mpsc::Sender<String>,
    branch: &str,
    orig_url: &str,
    proxied_url: Option<&str>,
    proxy_display: &str,
    dest: &Path,
) -> Option<String> {
    fn run_clone_streamed(
        tx: &std::sync::mpsc::Sender<String>,
        branch: &str,
        url: &str,
        dest: &Path,
    ) -> bool {
        let _ = tx.send(format!("git clone --branch {} \"{}\" \"{}\"", branch, url, dest.display()));

        let mut git_clone = std::process::Command::new("git");
        apply_hidden_command(&mut git_clone);
        let mut child = match git_clone
            .args(["clone", "--progress", "--branch", branch, url, dest.to_str().unwrap_or(".")])
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::piped())
            .spawn()
        {
            Ok(c) => c,
            Err(e) => {
                let _ = tx.send(format!("错误：无法执行 git - {}", e));
                return false;
            }
        };

        if let Some(stderr) = child.stderr.take() {
            let reader = BufReader::new(stderr);
            for line in reader.lines() {
                if let Ok(line) = line {
                    let trimmed = line.trim().to_string();
                    if !trimmed.is_empty() {
                        let _ = tx.send(trimmed);
                    }
                }
            }
        } else {
            let _ = tx.send("(git clone stderr not available)".to_string());
        }

        let success = child.wait().map(|s| s.success()).unwrap_or(false);
        if !success {
            let _ = tx.send(format!("git clone 退出码异常").to_string());
        }
        success
    }

    if let Some(proxy) = proxied_url {
        let _ = tx.send(format!("已使用 Github加速地址：{}", proxy_display));
        if run_clone_streamed(tx, branch, proxy, dest) {
            return None;
        }
        let _ = tx.send("加速地址失败，回退到原始地址重试...".to_string());

        if dest.exists() {
            let _ = fs::remove_dir_all(dest);
        }

        if run_clone_streamed(tx, branch, orig_url, dest) {
            return None;
        }
    } else {
        if run_clone_streamed(tx, branch, orig_url, dest) {
            return None;
        }
    }

    Some("克隆失败，请检查网络或仓库地址".to_string())
}

fn start_git_install(state: &mut ExtensionManageState) {
    let url = state.git_url.trim().to_string();
    let branch = state.git_selected_branch.clone();
    if url.is_empty() {
        return;
    }

    let repo_name = extract_repo_name(&url);
    let base_path = utils::app_paths().sillytavern_dir();
    let target_dir = base_path
        .join("public")
        .join("scripts")
        .join("extensions")
        .join("third-party")
        .join(&repo_name);
    let temp_dir = std::env::temp_dir().join(format!("astrabrew_clone_{}", repo_name));

    // 清理旧的临时目录
    if temp_dir.exists() {
        let _ = fs::remove_dir_all(&temp_dir);
    }
    // 清理旧的目标目录（如果存在）
    if target_dir.exists() {
        let _ = fs::remove_dir_all(&target_dir);
    }

    state.force_install_url = url.clone();
    state.force_install_branch = branch.clone();
    state.git_temp_dir = Some(temp_dir.clone());
    state.git_target_dir = Some(target_dir.clone());

    // 开始克隆到临时目录
    let proxy_url = state.github_proxy_url.clone();
    let proxy_enabled = state.github_proxy_enabled;
    let proxied_url = if proxy_enabled && !proxy_url.is_empty() {
        let p = apply_github_proxy(&url, &proxy_url);
        if p != url { Some(p) } else { None }
    } else {
        None
    };

    state.is_installing_git = true;
    state.git_install_done = None;
    state.git_install_done_at = None;
    state.git_install_log.clear();

    let (tx, rx) = std::sync::mpsc::channel();
    state.git_install_rx = Some(rx);

    let prompt_branch = if branch.is_empty() { "HEAD".to_string() } else { branch };

    std::thread::spawn(move || {
        let result = try_git_clone(
            &tx, &prompt_branch, &url,
            proxied_url.as_deref(), &proxy_url, &temp_dir,
        );
        if let Some(err) = result {
            let _ = tx.send(err);
            let _ = tx.send("__ERROR__".to_string());
        } else {
            let _ = tx.send("__DONE__".to_string());
        }
        drop(tx);
    });

    state.git_install_log = "正在克隆仓库到临时目录...".to_string();
}

fn install_all_packages(state: &mut ExtensionManageState, packages: &[OfflinePackage]) {
    let mut all_ok = true;
    for pkg in packages {
        match install_offline_extension(&pkg.path) {
            Ok(_) => {}
            Err(err) => {
                for p in &mut state.offline_packages {
                    if p.path == pkg.path {
                        p.error = Some(err);
                        break;
                    }
                }
                all_ok = false;
            }
        }
    }
    if all_ok {
        state.show_add_dialog = false;
        state.needs_refresh = true;
    }
}

// ============ 扩展卡片 ============

fn render_extension_card(
    ui: &mut egui::Ui,
    ext: &mut ExtensionInfo,
    _cell_width: f32,
    lang: &Language,
    selected_extensions: &mut HashSet<String>,
    batch_mode: bool,
    needs_refresh: &mut bool,
    fix_git_trigger: &mut Option<(String, std::path::PathBuf, String)>,
) {
    let is_selected = selected_extensions.contains(&ext.id);

    let border_stroke = if is_selected {
        egui::Stroke::new(1.5, egui::Color32::from_rgb(100, 180, 255))
    } else {
        ui.visuals().widgets.noninteractive.bg_stroke
    };

    let _card_rect = egui::Frame::NONE
        .fill(ui.visuals().window_fill())
        .corner_radius(8.0)
        .stroke(border_stroke)
        .inner_margin(12.0)
        .show(ui, |ui| {
            // 用 available_width() 确保内容填满 Frame 内边距后的剩余空间，绝不超出 Grid 单元格
            ui.set_width(ui.available_width());

            // 整体垂直布局
            ui.vertical(|ui| {
                // 上层：选中框（仅批量模式）+ 图标 + 名称 + [按钮组|分割线|开关]
                ui.horizontal(|ui| {
                    // 选中复选框（仅批量模式下显示）
                    if batch_mode {
                        let check_icon = if is_selected {
                            egui_phosphor::regular::CHECK_SQUARE
                        } else {
                            egui_phosphor::regular::SQUARE
                        };
                        let check_resp = ui.add_sized(
                            [22.0, 22.0],
                            egui::Button::selectable(
                                is_selected,
                                egui::RichText::new(check_icon).size(18.0),
                            ),
                        );
                        if check_resp.clicked() {
                            if is_selected {
                                selected_extensions.remove(&ext.id);
                            } else {
                                selected_extensions.insert(ext.id.clone());
                            }
                        }
                    }

                    // 图标
                    ui.label(egui::RichText::new(egui_phosphor::regular::PUZZLE_PIECE).size(20.0));
                    
                    // 名称
                    ui.label(
                        egui::RichText::new(&ext.manifest.display_name)
                            .strong()
                            .size(14.0)
                    );

                    // 靠右：按钮组 | 分割线 | 开关（系统扩展不显示开关）
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        // 开关（系统扩展不可禁用）
                        if !ext.is_official {
                            let mut enabled = ext.is_enabled;
                            if ui.add(toggle(&mut enabled)).changed() {
                                set_extension_enabled(&ext.path, enabled);
                                ext.is_enabled = enabled;
                            }
                        }

                        // 垂直分割线
                        ui.add(
                            egui::Separator::default()
                                .vertical()
                                .spacing(4.0),
                        );

                        // 图标按钮组
                        let btn_size = egui::vec2(20.0, 20.0);
                        let icon_size = 14.0;

                        // 删除扩展（官方扩展不可删除，防止酒馆异常）
                        if !ext.is_official {
                            if ui.add_sized(
                                btn_size,
                                egui::Button::new(
                                    egui::RichText::new(egui_phosphor::regular::TRASH).size(icon_size),
                                ),
                            ).on_hover_text(lang::t("ext_delete", lang)).clicked() {
                                let _ = fs::remove_dir_all(&ext.path);
                                *needs_refresh = true;
                            }
                        }

                        // 打开目录
                        let folder_path = ext.path.clone();
                        if ui.add_sized(
                            btn_size,
                            egui::Button::new(
                                egui::RichText::new(egui_phosphor::regular::FOLDER_OPEN).size(icon_size),
                            ),
                        ).on_hover_text(lang::t("ext_open_folder", lang)).clicked() {
                            let _ = std::process::Command::new("explorer")
                                .arg(&folder_path)
                                .spawn();
                        }

                        // 打开主页（仅当 URL 存在且非默认值）
                        let hp = &ext.manifest.home_page;
                        if !hp.is_empty() && hp != "https://github.com/SillyTavern/SillyTavern" && hp != "None" {
                            let url = hp.clone();
                            if ui.add_sized(
                                btn_size,
                                egui::Button::new(
                                    egui::RichText::new(egui_phosphor::regular::LINK).size(icon_size),
                                ),
                            ).on_hover_text(lang::t("ext_open_homepage", lang)).clicked() {
                                let _ = crate::core::shell::open_target(&url);
                            }
                        }

                        // 修复 Git（仅离线扩展，无 .git 目录时显示）
                        if !ext.is_official && !ext.path.join(".git").exists() {
                            let hp = &ext.manifest.home_page;
                            let is_git_url = !hp.is_empty()
                                && hp != "https://github.com/SillyTavern/SillyTavern"
                                && hp != "None"
                                && (hp.starts_with("https://github.com/") || hp.starts_with("http://github.com/"));
                            if is_git_url {
                                let ext_path = ext.path.clone();
                                let ext_name = ext.manifest.display_name.clone();
                                let remote_url = hp.clone();
                                if ui.add_sized(
                                    btn_size,
                                    egui::Button::new(
                                        egui::RichText::new(egui_phosphor::regular::GIT_FORK).size(icon_size),
                                    ),
                                ).on_hover_text(lang::t("ext_fix_git", lang)).clicked() {
                                    *fix_git_trigger = Some((ext_name, ext_path, remote_url));
                                }
                            }
                        }
                    });
                });

                ui.add_space(6.0);
                ui.separator();
                ui.add_space(6.0);

                // 下层：信息区域 (自动换行流式布局)
                ui.horizontal_wrapped(|ui| {
                    ui.spacing_mut().item_spacing = egui::vec2(8.0, 6.0);
                    
                    let mut items_added = 0;

                    // 官方标记（移到信息栏第一位，在 add_item 闭包定义之前渲染）
                    if ext.is_official {
                        let tag_color = egui::Color32::from_rgb(100, 180, 255);
                        egui::Frame::NONE
                            .fill(tag_color.linear_multiply(0.1))
                            .corner_radius(4.0)
                            .inner_margin(egui::Margin::symmetric(4, 2))
                            .show(ui, |ui| {
                                ui.label(
                                    egui::RichText::new(lang::t("ext_official", lang))
                                        .color(tag_color)
                                        .size(10.0)
                                );
                            });
                        items_added += 1;
                    }

                    let mut add_item = |ui: &mut egui::Ui, label: &str, value_widget: Box<dyn FnOnce(&mut egui::Ui)>| {
                        if items_added > 0 {
                            ui.label(egui::RichText::new("|").color(egui::Color32::from_gray(80)));
                        }
                        ui.horizontal(|ui| {
                            ui.spacing_mut().item_spacing.x = 4.0;
                            ui.label(egui::RichText::new(label).color(egui::Color32::GRAY).size(12.0));
                            value_widget(ui);
                        });
                        items_added += 1;
                    };

                    // 版本
                    if !ext.manifest.version.is_empty() {
                        add_item(ui, lang::t("ext_version", lang), Box::new(|ui| {
                            ui.label(egui::RichText::new(&ext.manifest.version).size(12.0));
                        }));
                    }

                    // 作者
                    if !ext.manifest.author.is_empty() {
                        add_item(ui, lang::t("ext_author", lang), Box::new(|ui| {
                            ui.label(egui::RichText::new(&ext.manifest.author).size(12.0));
                        }));
                    }

                    // 自动更新（仅当清单中有该字段时才显示）
                    if let Some(auto_update) = ext.manifest.auto_update {
                        add_item(ui, lang::t("ext_auto_update", lang), Box::new(move |ui| {
                            let text = if auto_update {
                                lang::t("on", lang)
                            } else {
                                lang::t("off", lang)
                            };
                            ui.label(egui::RichText::new(text).size(12.0));
                        }));
                    }

                    // 最低客户端版本 (必须放在最后)
                    if !ext.manifest.minimum_client_version.is_empty() {
                        add_item(ui, lang::t("ext_min_version", lang), Box::new(|ui| {
                            ui.label(egui::RichText::new(&ext.manifest.minimum_client_version).size(12.0));
                        }));
                    }
                });
            });
        });
}
