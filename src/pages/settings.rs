use eframe::egui;
use serde::{Deserialize, Serialize};
use std::fs;
use std::sync::{LazyLock, Mutex};
use std::time::Instant;

use crate::core::settings::git::{GitMirrorNode, GitNodeSelectMsg};

use crate::utils;

#[derive(PartialEq, Default)]
pub enum SettingsTab {
    #[default]
    General,
    About,
}

#[derive(PartialEq, Default, Clone, Copy, Serialize, Deserialize)]
pub enum Language {
    Chinese,
    English,
    #[default]
    System,
}

#[derive(PartialEq, Default, Clone, Copy, Serialize, Deserialize)]
pub enum Theme {
    Light,
    Dark,
    #[default]
    System,
}

#[derive(PartialEq, Default, Clone, Serialize, Deserialize)]
pub enum CpuCores {
    #[default]
    Auto,
    Half,
    All,
}

#[derive(PartialEq, Default, Clone, Serialize, Deserialize)]
pub enum StartMode {
    #[default]
    Normal,
    Desktop,
}

#[derive(PartialEq, Default, Clone, Serialize, Deserialize)]
pub enum ServerServiceMode {
    #[default]
    Lan,
    Internet,
}

#[derive(PartialEq, Default, Clone, Serialize, Deserialize)]
pub enum TavernDataMode {
    #[default]
    Current,
    Global,
}

#[derive(PartialEq, Default, Clone, Serialize, Deserialize)]
pub enum NpmRegistry {
    Official,
    #[default]
    Taobao,
    Tencent,
}

#[derive(PartialEq, Default, Clone, Serialize, Deserialize)]
pub enum ProxyType {
    #[default]
    None,
    System,
    Custom,
}

#[derive(PartialEq, Clone, Serialize, Deserialize, Default)]
pub enum EnvSource {
    #[default]
    Builtin,
    System,
}

/// Github 测试弹窗状态
struct GithubTestPopupState {
    show: bool,
    results: Vec<crate::core::network::GithubMultiTestItem>,
}

static GITHUB_TEST_POPUP_STATE: LazyLock<Mutex<GithubTestPopupState>> = LazyLock::new(|| {
    Mutex::new(GithubTestPopupState {
        show: false,
        results: Vec::new(),
    })
});

/// 自启动状态缓存，避免每帧调用 SMAppService（底层 ObjC IPC 会卡 UI）
struct AutoLaunchCache {
    status: &'static str,
    checked_at: std::time::Instant,
}

static AUTO_LAUNCH_CACHE: LazyLock<Mutex<AutoLaunchCache>> = LazyLock::new(|| {
    Mutex::new(AutoLaunchCache {
        status: "disabled",
        checked_at: std::time::Instant::now(),
    })
});

/// 当前激活的 SillyTavern 实例
#[derive(Serialize, Deserialize, Clone, Debug, Default, PartialEq)]
pub struct CurrentInstance {
    #[serde(rename = "type")]
    pub instance_type: String,
    pub path: Option<String>,
    pub version: String,
}

#[derive(Serialize, Deserialize, Clone, PartialEq)]
pub struct SettingsState {
    // 界面设置
    pub language: Language,
    pub theme: Theme,
    pub remember_window_pos: bool,
    pub window_position: Option<[f32; 2]>,

    // 基本设置
    pub cpu_cores: CpuCores,
    pub start_mode: StartMode,
    /// Node.js 环境来源
    #[serde(default)]
    pub nodejs_env: EnvSource,
    /// Git 环境来源
    #[serde(default)]
    pub git_env: EnvSource,
    pub server_mode_enabled: bool,
    pub server_service_mode: ServerServiceMode,
    pub data_mode: TavernDataMode,
    /// 全局数据模式下的自定义存放路径
    #[serde(default)]
    pub global_data_path: Option<String>,
    pub auto_start: bool,
    pub allow_tavern_background: bool,
    /// 桌面模式：关闭 WebView 窗口时自动停止酒馆服务（默认开启）
    #[serde(default = "default_auto_stop")]
    pub auto_stop_tavern_on_webview_close: bool,
    /// 桌面模式：酒馆导出文件的默认保存路径
    #[serde(default = "default_export_path")]
    pub tavern_export_path: String,

    // 控制台设置
    pub show_startup_command: bool,

    pub npm_registry: NpmRegistry,

    // Github 设置
    pub github_proxy_enabled: bool,
    pub github_proxy_url: String,

    // 网络设置
    pub proxy_type: ProxyType,
    pub custom_proxy: String,

    // 反向代理设置
    #[serde(default)]
    pub reverse_proxy_enabled: bool,
    #[serde(default)]
    pub reverse_proxy_domain: String,
    #[serde(default = "default_reverse_proxy_http_port")]
    pub reverse_proxy_http_port: String,
    #[serde(default = "default_reverse_proxy_https_port")]
    pub reverse_proxy_https_port: String,

    #[serde(default)]
    pub reverse_proxy_ssl_enabled: bool,
    #[serde(default)]
    pub reverse_proxy_ssl_force_https: bool,
    #[serde(default)]
    pub reverse_proxy_ssl_cert: String,
    #[serde(default)]
    pub reverse_proxy_ssl_key: String,

    // 当前版本实例
    #[serde(default)]
    pub sillytavern: Option<CurrentInstance>,

    // Node.js 运行时版本（不持久化）
    #[serde(skip)]
    pub nodejs_version: String,

    /// 统一环境模式：系统 / 内置
    #[serde(default)]
    pub env_mode: EnvSource,

    // 环境依赖检测结果 — 系统环境（不持久化）
    #[serde(skip)]
    pub git_version: Option<String>,
    #[serde(skip)]
    pub caddy_version: Option<String>,
    #[serde(skip)]
    pub pm2_version: Option<String>,

    // 环境依赖检测结果 — 内置环境（不持久化）
    #[serde(skip)]
    pub git_version_builtin: Option<String>,
    #[serde(skip)]
    pub nodejs_version_builtin: String,
    #[serde(skip)]
    pub caddy_version_builtin: Option<String>,
    #[serde(skip)]
    pub pm2_version_builtin: Option<String>,

    // 恢复默认触发标记（不持久化）
    #[serde(skip)]
    pub restore_defaults_triggered: bool,

    // 文件夹选择器触发标记（不持久化）
    #[serde(skip)]
    pub trigger_folder_picker: bool,

    // 导出路径选择器触发标记（不持久化）
    #[serde(skip)]
    pub trigger_export_path_picker: bool,

    // ─── 更新检测（不持久化） ────────────────────────────────────────────────
    /// 触发手动检查更新（由设置页"检查更新"按钮设置）
    #[serde(skip)]
    pub check_update_trigger: bool,
    /// 是否显示"确认更新"弹窗
    #[serde(skip)]
    pub update_confirm_open: bool,
    /// 确认弹窗中的更新信息
    #[serde(skip)]
    pub update_confirm_version: String,
    #[serde(skip)]
    pub update_confirm_notes: Option<String>,
    #[serde(skip)]
    pub update_confirm_endpoint: String,
    /// 是否正在下载
    #[serde(skip)]
    pub update_downloading: bool,
    /// 是否正在检查更新（控制按钮禁用态）
    #[serde(skip)]
    pub update_checking: bool,
    /// 触发执行下载安装（含 endpoint）
    #[serde(skip)]
    pub do_update_trigger: Option<String>,
}

/// auto_stop_tavern_on_webview_close 默认值
fn default_auto_stop() -> bool {
    true
}

/// tavern_export_path 默认值 → ~/Downloads
fn default_export_path() -> String {
    std::env::var("USERPROFILE")
        .or_else(|_| std::env::var("HOME"))
        .map(|h| format!("{}\\Downloads", h))
        .unwrap_or_default()
}

fn default_reverse_proxy_http_port() -> String {
    "80".to_string()
}

fn default_reverse_proxy_https_port() -> String {
    "443".to_string()
}

impl Default for SettingsState {
    fn default() -> Self {
        Self {
            language: Language::default(),
            theme: Theme::default(),
            remember_window_pos: true,
            window_position: None,
            cpu_cores: CpuCores::default(),
            start_mode: StartMode::default(),
            nodejs_env: EnvSource::default(),
            git_env: EnvSource::default(),
            server_mode_enabled: false,
            server_service_mode: ServerServiceMode::default(),
            data_mode: TavernDataMode::default(),
            global_data_path: None,
            auto_start: false,
            allow_tavern_background: false,
            auto_stop_tavern_on_webview_close: true,
            tavern_export_path: default_export_path(),
            show_startup_command: false,
            npm_registry: NpmRegistry::default(),
            github_proxy_enabled: false,
            github_proxy_url: "https://gh-proxy.org/".to_string(),
            proxy_type: ProxyType::default(),
            custom_proxy: String::new(),
            reverse_proxy_enabled: false,
            reverse_proxy_domain: String::new(),
            reverse_proxy_http_port: default_reverse_proxy_http_port(),
            reverse_proxy_https_port: default_reverse_proxy_https_port(),

            reverse_proxy_ssl_enabled: false,
            reverse_proxy_ssl_force_https: false,
            reverse_proxy_ssl_cert: String::new(),
            reverse_proxy_ssl_key: String::new(),
            sillytavern: None,
            nodejs_version: String::new(),
            env_mode: EnvSource::default(),
            git_version: None,
            caddy_version: None,
            pm2_version: None,
            git_version_builtin: None,
            nodejs_version_builtin: String::new(),
            caddy_version_builtin: None,
            pm2_version_builtin: None,
            restore_defaults_triggered: false,
            trigger_folder_picker: false,
            trigger_export_path_picker: false,
            update_confirm_open: false,
            update_confirm_version: String::new(),
            update_confirm_notes: None,
            update_confirm_endpoint: String::new(),
            update_downloading: false,
            update_checking: false,
            check_update_trigger: false,
            do_update_trigger: None,
        }
    }
}

impl SettingsState {
    pub fn load() -> Self {
        let path = utils::app_paths().settings_file();
        if path.exists() {
            if let Ok(content) = fs::read_to_string(&path) {
                if let Ok(state) = serde_json::from_str(&content) {
                    return state;
                }
            }
        }
        let default_state = Self::default();
        default_state.save();
        default_state
    }

    pub fn save(&self) {
        let path = utils::app_paths().settings_file();
        if let Some(parent) = path.parent() {
            let _ = fs::create_dir_all(parent);
        }
        if let Ok(content) = serde_json::to_string_pretty(self) {
            let _ = fs::write(path, content);
        }
    }

    /// 检测所有环境依赖版本（系统 + 内置）
    pub fn detect_all_env(&mut self) {
        use crate::core::settings::env_detect;
        // 系统环境
        self.git_version = env_detect::detect_git_system();
        let node_ver = env_detect::detect_nodejs_system();
        if let Some(v) = node_ver {
            self.nodejs_version = v;
        }
        self.caddy_version = env_detect::detect_caddy_system();
        self.pm2_version = env_detect::detect_pm2_system();
        // 内置环境
        self.git_version_builtin = env_detect::detect_git_builtin();
        let node_ver_b = env_detect::detect_nodejs_builtin();
        if let Some(v) = node_ver_b {
            self.nodejs_version_builtin = v;
        }
        self.caddy_version_builtin = env_detect::detect_caddy_builtin();
        self.pm2_version_builtin = env_detect::detect_pm2_builtin();
    }
}

/// 安装任务弹窗状态（更新/安装通用）
pub struct InstallTaskState {
    pub show: bool,
    pub log: String,
    pub running: bool,
    pub receiver: Option<std::sync::mpsc::Receiver<String>>,
    /// 任务完成的时间点（用于 3 秒后自动关闭）
    pub done_at: Option<std::time::Instant>,
    /// 任务开始的时间点（用于超时检测）
    pub started_at: Option<std::time::Instant>,
    /// 是否已超时
    pub timed_out: bool,
    /// 进度条模式：进度值 0.0-1.0
    pub progress: f32,
    /// 进度条模式：当前阶段文字
    pub progress_text: String,
    /// 进度条模式：下载速度文字（如 "3.2 MB/s"）
    pub speed_text: String,
    /// 是否为进度条模式（Git 安装等下载场景）
    pub progress_mode: bool,
    /// 安装成功后检测到的版本号（用于验证 + 自动刷新设置页）
    pub installed_version: Option<String>,
}

/// 安装/更新任务的超时时间（5 分钟）
const BREW_TASK_TIMEOUT_SECS: u64 = 300;

impl InstallTaskState {
    pub fn new() -> Self {
        Self {
            show: false,
            log: String::new(),
            running: false,
            receiver: None,
            done_at: None,
            started_at: None,
            timed_out: false,
            progress: 0.0,
            progress_text: String::new(),
            speed_text: String::new(),
            progress_mode: false,
            installed_version: None,
        }
    }

    /// 启动安装 <package>（Windows 平台使用内置下载机制替代 brew）
    pub fn start_install(&mut self, package: &str) {
        let package = package.to_string();
        let (tx, rx) = std::sync::mpsc::channel();
        self.receiver = Some(rx);
        self.log = String::new();
        self.running = true;
        self.show = true;
        self.done_at = None;
        self.timed_out = false;
        self.started_at = Some(std::time::Instant::now());
        std::thread::spawn(move || {
            // Windows: 显示提示，实际安装请通过内置下载机制完成
            let _ = tx.send(format!("Windows 平台不支持 brew install，请使用内置环境下载功能安装 {}", package));
            let _ = tx.send("__DONE__".to_string());
        });
    }

    /// 启动 npm install -g <package>（用于 PM2 等全局 npm 包安装）
    pub fn start_npm_install(&mut self, package: &str) {
        use crate::core::settings::env_detect;
        let package = package.to_string();
        let (tx, rx) = std::sync::mpsc::channel();
        self.receiver = Some(rx);
        self.log = String::new();
        self.running = true;
        self.show = true;
        self.done_at = None;
        self.timed_out = false;
        self.started_at = Some(std::time::Instant::now());
        std::thread::spawn(move || {
            env_detect::run_npm_install_global(&package, tx);
        });
    }

    /// 启动 Git 下载安装（使用指定 URL）- 进度条模式
    pub fn start_git_install(&mut self, url: &str) {
        use crate::EnvInstallProgress;
        let url = url.to_string();
        let temp_dir = std::env::temp_dir().join("astrabrew-launcher");
        let temp_path = temp_dir.join("MinGit-2.55.0.2-64-bit.zip");
        let install_dir = crate::core::env::get_data_dir().join("lib").join("git");
        let (tx, rx) = std::sync::mpsc::channel();
        self.receiver = Some(rx);
        self.log = String::new();
        self.running = true;
        self.show = true;
        self.done_at = None;
        self.timed_out = false;
        self.progress = 0.0;
        self.progress_text = String::new();
        self.speed_text = String::new();
        self.installed_version = None;
        self.progress_mode = true;
        self.started_at = Some(std::time::Instant::now());

        // 先发送初始信息到日志
        let _ = tx.send(format!("__LOG__:下载地址: {}", url));
        let _ = tx.send(format!("__LOG__:临时文件: {}", temp_path.display()));
        let _ = tx.send(format!("__LOG__:安装目录: {}", install_dir.display()));

        std::thread::spawn(move || {
            let (prog_tx, prog_rx) = std::sync::mpsc::channel();

            let download_thread = std::thread::spawn(move || {
                crate::core::settings::git::download_and_install_git_from_url(
                    &url,
                    Some(prog_tx),
                )
                .map_err(|e| e.to_string())
            });

            let mut last_sent_pct: i32 = -1;
            let mut is_download_phase = true;
            for msg in prog_rx {
                match msg {
                    EnvInstallProgress::Progress(p) => {
                        let display_pct = (p * 100.0) as i32;
                        if display_pct != last_sent_pct {
                            last_sent_pct = display_pct;
                            let _ = tx.send(format!("__PROGRESS__:{}", p));

                            // 0.5 作为下载/解压分界线
                            let phase_text = if p < 0.5 {
                                "下载中..."
                            } else {
                                if is_download_phase {
                                    is_download_phase = false;
                                }
                                "安装中..."
                            };
                            let _ = tx.send(format!("__STATUS__:{}", phase_text));
                        }
                    }
                    EnvInstallProgress::Speed(speed) => {
                        let _ = tx.send(format!("__SPEED__:{}", speed));
                    }
                    EnvInstallProgress::Status(s) => {
                        // 所有状态消息都转发到日志
                        let _ = tx.send(format!("__LOG__:{}", s));
                        // 顺便更新状态文字
                        if s.contains("下载完成") {
                            let _ = tx.send("__STATUS__:安装中...".to_string());
                            let _ = tx.send("__SPEED__:0.0".to_string());
                        }
                    }
                    EnvInstallProgress::Error(e) => {
                        let _ = tx.send(format!("__LOG__:错误: {}", e));
                        let _ = tx.send(format!("__STATUS__:错误"));
                    }
                    EnvInstallProgress::Version(ver) => {
                        let _ = tx.send(format!("__VERSION__:{}", ver));
                        let _ = tx.send(format!("__LOG__:检测到版本: {}", ver));
                    }
                    EnvInstallProgress::Finished => {}
                }
            }

            match download_thread.join().unwrap_or_else(|_| Err("线程异常".to_string())) {
                Ok(()) => {}
                Err(e) => {
                    let _ = tx.send(format!("__LOG__:错误: {}", e));
                    let _ = tx.send(format!("__STATUS__:错误"));
                }
            }
            let _ = tx.send("__DONE__".to_string());
        });
    }

    /// 轮询日志，返回完成后的新版本号
    #[allow(dead_code)]
    pub fn poll(&mut self) -> Option<String> {
        let mut new_version = None;
        if let Some(ref rx) = self.receiver {
            while let Ok(line) = rx.try_recv() {
                if line == "__DONE__" {
                    self.running = false;
                    if self.progress_mode {
                        self.progress = 1.0;
                        // 只有收到版本号才算安装成功
                        if self.installed_version.is_some() {
                            self.progress_text = "安装完成".to_string();
                        } else if !self.timed_out {
                            // DONE 了但没有版本号 → 安装失败
                            self.progress_text = "安装失败，请检查网络".to_string();
                        }
                    }
                    self.done_at = Some(std::time::Instant::now());
                    self.receiver = None;
                    break;
                }
                if let Some(ver) = line.strip_prefix("__VERSION__:") {
                    new_version = Some(ver.to_string());
                    // 进度条模式下也存版本号
                    if self.progress_mode {
                        self.installed_version = Some(ver.to_string());
                    }
                    continue;
                }
                if self.progress_mode {
                    if let Some(p_str) = line.strip_prefix("__PROGRESS__:") {
                        if let Ok(p) = p_str.parse::<f32>() {
                            self.progress = p;
                        }
                        continue;
                    }
                    if let Some(status) = line.strip_prefix("__STATUS__:") {
                        self.progress_text = status.to_string();
                        continue;
                    }
                    if let Some(speed_str) = line.strip_prefix("__SPEED__:") {
                        if let Ok(bytes_per_sec) = speed_str.parse::<f32>() {
                            self.speed_text = format_speed(bytes_per_sec);
                        }
                        continue;
                    }
                    if let Some(log_line) = line.strip_prefix("__LOG__:") {
                        if !self.log.is_empty() {
                            self.log.push('\n');
                        }
                        self.log.push_str(log_line);
                        continue;
                    }
                }
                if !self.log.is_empty() {
                    self.log.push('\n');
                }
                self.log.push_str(&line);
            }
        }
        new_version
    }
}

/// Git 节点选择弹窗状态
pub struct GitNodeSelectState {
    /// 是否显示弹窗
    pub show: bool,
    /// 所有节点列表（含延迟信息）
    pub nodes: Vec<GitMirrorNode>,
    /// 是否正在测速
    pub loading: bool,
    /// 测速进度文字
    pub testing_info: String,
    /// 已选中的节点索引，None 表示未选
    pub selected_index: Option<usize>,
    /// 3 秒倒计时开始时间
    pub countdown_start: Option<Instant>,
    /// 是否已自动选择
    pub auto_selected: bool,
    /// 接收后台测速结果的 channel
    pub receiver: Option<std::sync::mpsc::Receiver<GitNodeSelectMsg>>,
    /// 是否已触发安装（用于传递给 InstallTaskState）
    pub install_triggered_url: Option<String>,
}

impl GitNodeSelectState {
    pub fn new() -> Self {
        Self {
            show: false,
            nodes: Vec::new(),
            loading: false,
            testing_info: String::new(),
            selected_index: None,
            countdown_start: None,
            auto_selected: false,
            receiver: None,
            install_triggered_url: None,
        }
    }

    /// 打开弹窗并启动后台测速
    pub fn open(&mut self) {
        self.show = true;
        self.loading = true;
        self.nodes = crate::core::settings::git::get_git_mirror_nodes();
        self.testing_info = String::new();
        self.selected_index = None;
        self.countdown_start = None;
        self.auto_selected = false;
        self.install_triggered_url = None;

        let (tx, rx) = std::sync::mpsc::channel();
        self.receiver = Some(rx);
        std::thread::spawn(move || {
            crate::core::settings::git::test_mirror_latency(tx);
        });
    }

    /// 轮询后台测速结果，更新节点状态
    pub fn poll(&mut self) {
        let mut done = false;
        if let Some(ref rx) = self.receiver {
            while let Ok(msg) = rx.try_recv() {
                match msg {
                    GitNodeSelectMsg::TestingProgress(info) => {
                        self.testing_info = info;
                    }
                    GitNodeSelectMsg::LatencyResults(nodes) => {
                        self.nodes = nodes;
                        self.loading = false;
                        done = true;
                        // 测速完成后开始 3 秒倒计时
                        self.countdown_start = Some(Instant::now());
                    }
                }
            }
        }
        if done {
            self.receiver = None;
        }
    }

    /// 手动选择一个节点
    pub fn select(&mut self, index: usize) {
        if let Some(node) = self.nodes.get(index) {
            self.selected_index = Some(index);
            self.countdown_start = None; // 用户手动选，取消倒计时
            self.install_triggered_url = Some(node.url.clone());
            self.show = false; // 立即关闭弹窗
        }
    }

    /// 检查是否应该自动选择（3 秒后）
    pub fn check_auto_select(&mut self) -> bool {
        if self.loading || self.selected_index.is_some() || self.nodes.is_empty() {
            return false;
        }
        if let Some(start) = self.countdown_start {
            if start.elapsed().as_secs() >= 3 {
                let url = self.nodes[0].url.clone();
                self.selected_index = Some(0);
                self.auto_selected = true;
                self.countdown_start = None;
                self.install_triggered_url = Some(url);
                return true;
            }
        }
        false
    }

    /// 获取倒计时剩余秒数
    pub fn countdown_remaining(&self) -> Option<u64> {
        self.countdown_start.map(|start| {
            let elapsed = start.elapsed().as_secs();
            if elapsed >= 3 {
                0
            } else {
                3 - elapsed
            }
        })
    }
}

use crate::lang;

fn render_brew_task_window(
    ctx: &egui::Context,
    task: &mut InstallTaskState,
    title: &str,
    desc: &str,
    waiting: &str,
    running_label: &str,
    close_label: &str,
    timeout_msg: &str,
) {
    // 完成后 3 秒自动关闭
    if let Some(done_at) = task.done_at {
        if done_at.elapsed().as_secs() >= 3 {
            task.show = false;
            task.done_at = None;
            return;
        }
        ctx.request_repaint();
    }

    // 超时检测：运行超过 5 分钟则标记超时，停止等待
    if task.running && !task.timed_out {
        if let Some(started_at) = task.started_at {
            if started_at.elapsed().as_secs() >= BREW_TASK_TIMEOUT_SECS {
                task.timed_out = true;
                task.running = false;
                task.receiver = None; // 丢弃 receiver，不再轮询
                task.done_at = None;
                if !task.log.is_empty() {
                    task.log.push('\n');
                }
                task.log.push('\n');
                task.log.push_str("⏰ ");
                task.log.push_str(timeout_msg);
            } else {
                ctx.request_repaint();
            }
        }
    }

    if !task.show {
        return;
    }
    egui::Window::new(title)
        .collapsible(false)
        .resizable(true)
        .min_width(500.0)
        .show(ctx, |ui| {
            ui.label(desc);
            ui.add_space(10.0);
            egui::ScrollArea::vertical()
                .max_height(300.0)
                .auto_shrink([false, false])
                .show(ui, |ui| {
                    let log = if task.log.is_empty() {
                        waiting.to_string()
                    } else {
                        task.log.clone()
                    };
                    ui.label(log);
                });
            ui.add_space(10.0);
            ui.horizontal(|ui| {
                if task.running {
                    ui.spinner();
                    ui.label(running_label);
                } else if task.timed_out {
                    ui.label(
                        egui::RichText::new(timeout_msg)
                            .color(egui::Color32::from_rgb(220, 80, 80))
                            .strong(),
                    );
                }
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if !task.running {
                        if ui.button(close_label).clicked() {
                            task.show = false;
                            task.timed_out = false;
                            task.started_at = None;
                        }
                    }
                });
            });
        });
}

/// 渲染 Git 安装进度弹窗（仅进度条 + 状态文字，无日志区）
/// 返回 true 表示安装成功且弹窗已关闭，调用方应刷新环境检测
fn render_git_install_window(
    ctx: &egui::Context,
    task: &mut InstallTaskState,
    title: &str,
    desc: &str,
    close_label: &str,
    timeout_msg: &str,
) -> bool {
    let mut should_refresh = false;

    // 先轮询消息更新进度
    task.poll();
    // 完成后 3 秒自动关闭（仅安装成功时）
    if let Some(done_at) = task.done_at {
        if done_at.elapsed().as_secs() >= 3 {
            if task.installed_version.is_some() {
                should_refresh = true;
            }
            task.show = false;
            task.done_at = None;
            task.progress_mode = false;
            task.installed_version = None;
            task.speed_text.clear();
            return should_refresh;
        }
        ctx.request_repaint();
    }

    // 超时检测
    if task.running && !task.timed_out {
        if let Some(started_at) = task.started_at {
            if started_at.elapsed().as_secs() >= BREW_TASK_TIMEOUT_SECS {
                task.timed_out = true;
                task.running = false;
                task.receiver = None;
                task.done_at = None;
                task.progress_text = timeout_msg.to_string();
            } else {
                ctx.request_repaint();
            }
        }
    }

    if !task.show {
        return false;
    }
    egui::Window::new(title)
        .collapsible(false)
        .resizable(false)
        .min_width(500.0)
        .anchor(egui::Align2::CENTER_CENTER, egui::vec2(0.0, 0.0))
        .show(ctx, |ui| {
            ui.label(desc);
            ui.add_space(8.0);

            // 日志区域
            egui::ScrollArea::vertical()
                .max_height(200.0)
                .auto_shrink([false, false])
                .show(ui, |ui| {
                    if task.log.is_empty() {
                        ui.label(
                            egui::RichText::new("准备下载...")
                                .color(egui::Color32::GRAY),
                        );
                    } else {
                        ui.label(&task.log);
                    }
                });
            ui.add_space(10.0);

            // 进度条
            let progress_bar = egui::ProgressBar::new(task.progress)
                .show_percentage()
                .animate(task.running);
            ui.add(progress_bar);
            ui.add_space(6.0);

            // 下载速度 + 状态文字
            ui.horizontal(|ui| {
                if task.running {
                    ui.spinner();
                    let status_text = if task.progress_text.is_empty() {
                        "准备中..."
                    } else {
                        &task.progress_text
                    };
                    ui.label(
                        egui::RichText::new(status_text)
                            .size(14.0)
                            .color(egui::Color32::from_rgb(120, 200, 255)),
                    );
                } else if task.timed_out {
                    ui.label(
                        egui::RichText::new(timeout_msg)
                            .color(egui::Color32::from_rgb(220, 80, 80))
                            .strong(),
                    );
                } else {
                    ui.label(
                        egui::RichText::new(&task.progress_text)
                            .size(14.0)
                            .color(if task.installed_version.is_some() {
                                egui::Color32::GREEN
                            } else {
                                egui::Color32::from_rgb(220, 80, 80)
                            }),
                    );
                }
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if task.running && !task.speed_text.is_empty() {
                        ui.label(
                            egui::RichText::new(&task.speed_text)
                                .size(13.0)
                                .color(egui::Color32::from_rgb(160, 210, 255)),
                        );
                    }
                });
            });

            ui.add_space(10.0);
            ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                if !task.running {
                    if ui.button(close_label).clicked() {
                        task.show = false;
                        task.timed_out = false;
                        task.started_at = None;
                        task.progress_mode = false;
                        task.installed_version = None;
                        task.speed_text.clear();
                    }
                }
            });
        });
    should_refresh
}

/// 格式化速度为易读字符串
fn format_speed(bytes_per_sec: f32) -> String {
    if bytes_per_sec >= 1_048_576.0 {
        format!("{:.1} MB/s", bytes_per_sec / 1_048_576.0)
    } else if bytes_per_sec >= 1024.0 {
        format!("{:.0} KB/s", bytes_per_sec / 1024.0)
    } else {
        format!("{:.0} B/s", bytes_per_sec)
    }
}

/// 渲染 Git 节点选择弹窗
fn render_git_node_select_popup(
    ctx: &egui::Context,
    state: &mut GitNodeSelectState,
    lang: &Language,
) {
    if !state.show {
        return;
    }

    // 先轮询测速结果
    state.poll();
    // 检查是否应该自动选择
    state.check_auto_select();

    let mut open = true;

    // 如果已有选中触发（auto-select 或手动选），隐藏弹窗
    if state.install_triggered_url.is_some() {
        open = false;
    }

    egui::Window::new(lang::t("git_node_select_title", lang))
        .collapsible(false)
        .resizable(false)
        .min_width(500.0)
        .anchor(egui::Align2::CENTER_CENTER, egui::vec2(0.0, 0.0))
        .open(&mut open)
        .show(ctx, |ui| {
            ui.label(egui::RichText::new(lang::t("git_node_select_desc", lang)).strong());
            ui.add_space(8.0);

            if state.loading {
                // 测速中
                ui.horizontal(|ui| {
                    ui.spinner();
                    ui.label(state.testing_info.as_str());
                });
                ui.add_space(8.0);

                // 显示节点占位
                for node in &state.nodes {
                    ui.horizontal(|ui| {
                        ui.label(format!("  {}", node.name));
                        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                            ui.spinner();
                            ui.label(lang::t("testing", lang));
                        });
                    });
                    ui.separator();
                }
            } else {
                // 测速完成，显示结果和倒计时
                let remaining = state.countdown_remaining().unwrap_or(0);
                ui.label(
                    egui::RichText::new(format!(
                        "{} {}s",
                        lang::t("git_node_auto_select", lang),
                        remaining
                    ))
                    .color(egui::Color32::from_rgb(255, 180, 50)),
                );
                ui.add_space(8.0);

                egui::ScrollArea::vertical()
                    .max_height(350.0)
                    .show(ui, |ui| {
                        let mut clicked_index: Option<usize> = None;
                        for (i, node) in state.nodes.iter().enumerate() {
                            let is_selected = state.selected_index == Some(i);
                            let row_bg = if is_selected {
                                egui::Color32::from_rgb(30, 80, 160)
                            } else {
                                egui::Color32::TRANSPARENT
                            };

                            egui::Frame::NONE
                                .fill(row_bg)
                                .inner_margin(egui::vec2(8.0, 4.0))
                                .show(ui, |ui| {
                                    ui.set_min_width(ui.available_width());
                                    ui.horizontal(|ui| {
                                        // 序号
                                        if is_selected {
                                            ui.label(
                                                egui::RichText::new("●")
                                                    .color(egui::Color32::GREEN),
                                            );
                                        } else {
                                            ui.label(format!("{}", i + 1));
                                        }
                                        ui.add_space(6.0);
                                        // 节点名称
                                        ui.label(
                                            egui::RichText::new(&node.name).size(14.0),
                                        );
                                        ui.with_layout(
                                            egui::Layout::right_to_left(egui::Align::Center),
                                            |ui| {
                                                // 延迟显示
                                                if node.blocked {
                                                    ui.label(
                                                        egui::RichText::new("403/404")
                                                            .color(egui::Color32::RED),
                                                    ).on_hover_text("节点已阻止此文件访问");
                                                } else if node.timed_out {
                                                    ui.label(
                                                        egui::RichText::new(
                                                            lang::t("timeout", lang),
                                                        )
                                                        .color(egui::Color32::RED),
                                                    );
                                                } else if let Some(ms) = node.latency_ms {
                                                    let color = if ms < 100 {
                                                        egui::Color32::GREEN
                                                    } else if ms < 300 {
                                                        egui::Color32::YELLOW
                                                    } else {
                                                        egui::Color32::from_rgb(
                                                            255, 150, 50,
                                                        )
                                                    };
                                                    ui.label(
                                                        egui::RichText::new(format!(
                                                            "{}ms",
                                                            ms
                                                        ))
                                                        .color(color),
                                                    );
                                                } else {
                                                    ui.label(
                                                        egui::RichText::new("--")
                                                            .color(egui::Color32::GRAY),
                                                    );
                                                }
                                                ui.add_space(8.0);
                                                // 选择按钮
                                                if node.timed_out || node.blocked {
                                                    ui.add_enabled(false, egui::Button::new(
                                                        lang::t("install", lang),
                                                    ));
                                                } else if is_selected {
                                                    ui.label(
                                                        egui::RichText::new("已选")
                                                            .color(egui::Color32::GREEN),
                                                    );
                                                } else {
                                                    if ui.small_button(
                                                        lang::t("install", lang),
                                                    ).clicked() {
                                                        clicked_index = Some(i);
                                                    }
                                                }
                                            },
                                        );
                                    });
                                });

                            ui.separator();
                        }
                        // 在循环外处理点击，避免同时借用
                        if let Some(i) = clicked_index {
                            state.select(i);
                        }
                    });
            }
            ctx.request_repaint();
        });

    // 弹窗关闭（open=false、手动选中、或安装触发）
    if !open || !state.show || state.install_triggered_url.is_some() {
        state.show = false;
        state.receiver = None;
    }
}

fn setting_section(
    ui: &mut egui::Ui,
    icon: &str,
    title: &str,
    add_content: impl FnOnce(&mut egui::Ui),
) {
    ui.add_space(10.0);
    ui.horizontal(|ui| {
        ui.label(egui::RichText::new(icon).size(18.0).color(ui.visuals().text_color()));
        ui.heading(egui::RichText::new(title).strong());
    });
    ui.add_space(5.0);

    egui::Frame::NONE
        .fill(ui.visuals().faint_bg_color)
        .corner_radius(8.0)
        .inner_margin(15.0)
        .show(ui, |ui| {
            add_content(ui);
        });
}

fn setting_row(
    ui: &mut egui::Ui,
    icon: &str,
    title: &str,
    description: &str,
    add_content: impl FnOnce(&mut egui::Ui),
) {
    ui.horizontal(|ui| {
        ui.add_sized(
            [30.0, 30.0],
            egui::Label::new(egui::RichText::new(icon).size(20.0)),
        );

        ui.vertical(|ui| {
            ui.add_space(2.0);
            ui.label(egui::RichText::new(title).size(14.0).strong());
            if !description.is_empty() {
                ui.label(
                    egui::RichText::new(description)
                        .color(egui::Color32::GRAY)
                        .size(12.0),
                );
            }
        });

        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
            add_content(ui);
        });
    });
}

pub fn render(
    ui: &mut egui::Ui,
    tab: &mut SettingsTab,
    state: &mut SettingsState,
    git_install: &mut InstallTaskState,
    nodejs_install: &mut InstallTaskState,
    caddy_install: &mut InstallTaskState,
    pm2_install: &mut InstallTaskState,
    github_node_state: &crate::core::settings::github_proxy::NodeLoadState,
    on_refresh_nodes: &mut bool,
    git_node_select: &mut GitNodeSelectState,
) {
    ui.horizontal(|ui| {
        ui.selectable_value(tab, SettingsTab::General, lang::t("general_settings", &state.language));
        ui.selectable_value(tab, SettingsTab::About, lang::t("about_software", &state.language));
        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
            if ui.small_button(lang::t("restore_defaults", &state.language)).clicked() {
                // 保留环境依赖检测结果，避免误显示安装按钮
                let nodejs_version = state.nodejs_version.clone();
                let git_version = state.git_version.clone();
                let caddy_version = state.caddy_version.clone();
                let pm2_version = state.pm2_version.clone();
                let nodejs_version_builtin = state.nodejs_version_builtin.clone();
                let git_version_builtin = state.git_version_builtin.clone();
                let caddy_version_builtin = state.caddy_version_builtin.clone();
                let pm2_version_builtin = state.pm2_version_builtin.clone();
                *state = SettingsState::default();
                state.nodejs_version = nodejs_version;
                state.git_version = git_version;
                state.caddy_version = caddy_version;
                state.pm2_version = pm2_version;
                state.nodejs_version_builtin = nodejs_version_builtin;
                state.git_version_builtin = git_version_builtin;
                state.caddy_version_builtin = caddy_version_builtin;
                state.pm2_version_builtin = pm2_version_builtin;
                state.restore_defaults_triggered = true;
            }
        });
    });
    ui.separator();

    match tab {
        SettingsTab::General => {
            egui::ScrollArea::vertical()
                .auto_shrink([false, false])
                .show(ui, |ui| {
                    ui.add_space(10.0);

                    // 界面设置
                    setting_section(
                        ui,
                        egui_phosphor::regular::PAINT_BRUSH,
                        lang::t("interface_settings", &state.language),
                        |ui| {
                            setting_row(
                                ui,
                                egui_phosphor::regular::TRANSLATE,
                                lang::t("language", &state.language),
                                lang::t("language_desc", &state.language),
                                |ui| {
                                    egui::ComboBox::from_id_salt("lang_combo")
                                        .selected_text(match state.language {
                                            Language::Chinese => lang::t("zh_cn", &state.language),
                                            Language::English => lang::t("en_us", &state.language),
                                            Language::System => lang::t("system_language", &state.language),
                                        })
                                        .show_ui(ui, |ui| {
                                            let text_zh = lang::t("zh_cn", &state.language);
                                            let text_en = lang::t("en_us", &state.language);
                                            let text_sys = lang::t("system_language", &state.language);
                                            ui.selectable_value(&mut state.language, Language::Chinese, text_zh);
                                            ui.selectable_value(&mut state.language, Language::English, text_en);
                                            ui.selectable_value(&mut state.language, Language::System, text_sys);
                                        });
                                },
                            );
                            ui.add_space(10.0);
                            setting_row(
                                ui,
                                egui_phosphor::regular::PALETTE,
                                lang::t("theme", &state.language),
                                lang::t("theme_desc", &state.language),
                                |ui| {
                                    egui::ComboBox::from_id_salt("theme_combo")
                                        .selected_text(match state.theme {
                                            Theme::Light => lang::t("light_theme", &state.language),
                                            Theme::Dark => lang::t("dark_theme", &state.language),
                                            Theme::System => lang::t("system_theme", &state.language),
                                        })
                                        .show_ui(ui, |ui| {
                                            ui.selectable_value(&mut state.theme, Theme::Light, lang::t("light_theme", &state.language));
                                            ui.selectable_value(&mut state.theme, Theme::Dark, lang::t("dark_theme", &state.language));
                                            ui.selectable_value(&mut state.theme, Theme::System, lang::t("system_theme", &state.language));
                                        });
                                },
                            );
                            ui.add_space(10.0);
                            setting_row(
                                ui,
                                egui_phosphor::regular::CORNERS_OUT,
                                lang::t("remember_window_pos", &state.language),
                                lang::t("remember_window_pos_desc", &state.language),
                                |ui| {
                                    ui.add(crate::ui::switch::toggle(&mut state.remember_window_pos));
                                },
                            );
                        },
                    );

                    // 基本设置
                    setting_section(ui, egui_phosphor::regular::SLIDERS, lang::t("basic_settings", &state.language), |ui| {
                        setting_row(
                            ui,
                            egui_phosphor::regular::POWER,
                            lang::t("auto_start", &state.language),
                            lang::t("auto_start_desc", &state.language),
                            |ui| {
                                let prev = state.auto_start;
                                ui.add(crate::ui::switch::toggle(&mut state.auto_start));
                                if prev != state.auto_start {
                                    let enabled = state.auto_start;
                                    match crate::core::auto_launch::set_auto_launch(enabled) {
                                        Ok(()) => {
                                            // 操作成功，强制刷新缓存
                                            let mut cache = AUTO_LAUNCH_CACHE.lock().unwrap();
                                            cache.checked_at = std::time::Instant::now() - std::time::Duration::from_secs(10);
                                        }
                                        Err(e) => {
                                            eprintln!("[auto_launch] set_auto_launch({}) failed: {}", enabled, e);
                                            state.auto_start = crate::core::auto_launch::is_auto_launch_enabled();
                                        }
                                    }
                                }
                                // 显示自启动状态（节流缓存，最多每 3 秒查一次，避免高频 ObjC IPC 卡 UI）
                                ui.add_space(4.0);
                                let status = {
                                    let mut cache = AUTO_LAUNCH_CACHE.lock().unwrap();
                                    if cache.checked_at.elapsed() > std::time::Duration::from_secs(3) {
                                        cache.status = crate::core::auto_launch::get_auto_launch_status();
                                        cache.checked_at = std::time::Instant::now();
                                        // 同步开关状态与系统真实状态
                                        let system_enabled = crate::core::auto_launch::is_auto_launch_enabled();
                                        if state.auto_start != system_enabled {
                                            state.auto_start = system_enabled;
                                        }
                                    }
                                    cache.status
                                };
                                let (status_text, status_color) = match status {
                                    "enabled" => (lang::t("auto_start_enabled", &state.language), egui::Color32::GREEN),
                                    "requires_approval" => (lang::t("auto_start_requires_approval", &state.language), egui::Color32::from_rgb(255, 200, 60)),
                                    _ => (lang::t("auto_start_disabled", &state.language), egui::Color32::GRAY),
                                };
                                ui.label(egui::RichText::new(status_text).size(11.0).color(status_color));
                                if status == "requires_approval" {
                                    ui.add_space(4.0);
                                    if ui.small_button(lang::t("open_system_settings", &state.language)).clicked() {
                                        let _ = std::process::Command::new("open")
                                            .arg("x-apple.systempreferences:com.apple.LoginItems-Settings.extension")
                                            .spawn();
                                    }
                                }
                            },
                        );
                        ui.add_space(10.0);

                        setting_row(
                            ui,
                            egui_phosphor::regular::CPU,
                            lang::t("cpu_cores", &state.language),
                            lang::t("cpu_cores_desc", &state.language),
                            |ui| {
                                egui::ComboBox::from_id_salt("cpu_combo")
                                    .selected_text(match state.cpu_cores {
                                        CpuCores::Auto => lang::t("auto", &state.language),
                                        CpuCores::Half => lang::t("half_cores", &state.language),
                                        CpuCores::All => lang::t("all_cores", &state.language),
                                    })
                                    .show_ui(ui, |ui| {
                                        ui.selectable_value(&mut state.cpu_cores, CpuCores::Auto, lang::t("auto", &state.language));
                                        ui.selectable_value(&mut state.cpu_cores, CpuCores::Half, lang::t("half_cores", &state.language));
                                        ui.selectable_value(&mut state.cpu_cores, CpuCores::All, lang::t("all_cores", &state.language));
                                    });
                            },
                        );
                        ui.add_space(10.0);

                        let mode_desc = if state.server_mode_enabled {
                            lang::t("server_mode_enabled_desc", &state.language)
                        } else {
                            match state.start_mode {
                                StartMode::Normal => lang::t("normal_mode_desc", &state.language),
                                StartMode::Desktop => lang::t("desktop_mode_desc", &state.language),
                            }
                        };
                        setting_row(
                            ui,
                            egui_phosphor::regular::PLAY_CIRCLE,
                            lang::t("start_mode", &state.language),
                            mode_desc,
                            |ui| {
                                if state.server_mode_enabled {
                                    ui.add_enabled_ui(false, |ui| {
                                        crate::ui::segmented::segmented_control(
                                            ui,
                                            &mut state.start_mode,
                                            &[
                                                (StartMode::Normal, lang::t("server_start_mode", &state.language)),
                                            ],
                                        );
                                    });
                                } else {
                                    crate::ui::segmented::segmented_control(
                                        ui,
                                        &mut state.start_mode,
                                        &[
                                            (StartMode::Normal, lang::t("normal_mode", &state.language)),
                                            (StartMode::Desktop, lang::t("desktop_mode", &state.language)),
                                        ],
                                    );
                                }
                            },
                        );
                        ui.add_space(10.0);

                        // 桌面模式专属选项：关闭窗口时自动停止服务
                        if state.start_mode == StartMode::Desktop {
                            setting_row(
                                ui,
                                egui_phosphor::regular::X_CIRCLE,
                                lang::t("desktop_auto_stop", &state.language),
                                lang::t("desktop_auto_stop_desc", &state.language),
                                |ui| {
                                    ui.add(crate::ui::switch::toggle(&mut state.auto_stop_tavern_on_webview_close));
                                },
                            );
                            ui.add_space(10.0);

                            // 导出路径
                            let desc = format!("{}\n{}",
                                lang::t("desktop_export_path_desc", &state.language),
                                state.tavern_export_path,
                            );
                            setting_row(
                                ui,
                                egui_phosphor::regular::FOLDER,
                                lang::t("desktop_export_path", &state.language),
                                &desc,
                                |ui| {
                                    if ui.button(lang::t("change_path", &state.language)).clicked() {
                                        state.trigger_export_path_picker = true;
                                    }
                                },
                            );
                            ui.add_space(10.0);
                        }

                        // 启用服务器模式
                        setting_row(
                            ui,
                            egui_phosphor::regular::HARD_DRIVES,
                            lang::t("server_mode_enabled", &state.language),
                            lang::t("server_mode_enabled_desc", &state.language),
                            |ui| {
                                ui.add(crate::ui::switch::toggle(&mut state.server_mode_enabled));
                            },
                        );
                        ui.add_space(10.0);

                        // 服务器模式开启时：强制锁定启动模式为正常模式
                        if state.server_mode_enabled && state.start_mode != StartMode::Normal {
                            state.start_mode = StartMode::Normal;
                        }

                        // 酒馆服务模式（仅服务器模式开启时显示）
                        if state.server_mode_enabled {
                            let svc_desc = match state.server_service_mode {
                                ServerServiceMode::Lan => lang::t("server_mode_lan_desc", &state.language),
                                ServerServiceMode::Internet => lang::t("server_mode_internet_desc", &state.language),
                            };
                            setting_row(
                                ui,
                                egui_phosphor::regular::GLOBE,
                                lang::t("server_service_mode", &state.language),
                                svc_desc,
                                |ui| {
                                    crate::ui::segmented::segmented_control(
                                        ui,
                                        &mut state.server_service_mode,
                                        &[
                                            (ServerServiceMode::Lan, lang::t("server_mode_lan", &state.language)),
                                            (ServerServiceMode::Internet, lang::t("server_mode_internet", &state.language)),
                                        ],
                                    );
                                },
                            );
                            ui.add_space(10.0);
                        }

                        // 允许酒馆后台运行（仅服务器模式开启时显示）
                        if state.server_mode_enabled {
                            setting_row(
                                ui,
                                egui_phosphor::regular::ARROW_ARC_LEFT,
                                lang::t("allow_tavern_background", &state.language),
                                lang::t("allow_tavern_background_desc", &state.language),
                                |ui| {
                                    let pm2_installed = state.pm2_version.is_some();
                                    ui.add_enabled_ui(pm2_installed, |ui| {
                                        ui.add(crate::ui::switch::toggle(&mut state.allow_tavern_background));
                                    });
                                    if !pm2_installed {
                                        state.allow_tavern_background = false;
                                    }
                                },
                            );
                            ui.add_space(10.0);
                        }

                        // 反向代理（仅服务器模式 + 互联网时显示，依赖 Caddy）
                        if state.server_mode_enabled && state.server_service_mode == ServerServiceMode::Internet {
                            // let caddy_installed = state.caddy_version.is_some();
                            setting_row(
                                ui,
                                egui_phosphor::regular::ARROWS_LEFT_RIGHT,
                                lang::t("rp_title", &state.language),
                                lang::t("rp_manage_desc", &state.language),
                                |ui| {
                                    // 暂时将按钮替换为不可交互的文本 "待开发..."，保留原始按钮逻辑为注释以便将来恢复
                                    ui.label(
                                        egui::RichText::new("待开发...")
                                            .color(egui::Color32::GRAY)
                                            .size(13.0),
                                    );
                                    /*
                                    let btn = egui::Button::new(lang::t("rp_manage", &state.language));
                                    let resp = if caddy_installed {
                                        ui.add_enabled(true, btn)
                                    } else {
                                        ui.add_enabled(false, btn)
                                            .on_disabled_hover_text(lang::t("rp_need_caddy", &state.language))
                                    };
                                    if resp.clicked() {
                                        let mut popup = crate::pages::reverse_proxy_popup::REVERSE_PROXY_POPUP.lock().unwrap();
                                        popup.show = true;
                                    }
                                    */
                                },
                            );
                            ui.add_space(10.0);
                        }

                        let data_mode_desc = match state.data_mode {
                            TavernDataMode::Global => lang::t("data_mode_global_desc", &state.language),
                            TavernDataMode::Current => lang::t("data_mode_current_desc", &state.language),
                        };
                        setting_row(
                            ui,
                            egui_phosphor::regular::DATABASE,
                            lang::t("data_mode", &state.language),
                            data_mode_desc,
                            |ui| {
                                crate::ui::segmented::segmented_control(
                                    ui,
                                    &mut state.data_mode,
                                    &[
                                        (TavernDataMode::Global, lang::t("data_mode_global", &state.language)),
                                        (TavernDataMode::Current, lang::t("data_mode_current", &state.language)),
                                    ],
                                );
                            },
                        );
                        ui.add_space(10.0);

                        // 全局数据模式 — 自定义路径
                        if state.data_mode == TavernDataMode::Global {
                            let default_path = crate::utils::app_paths().default_global_data_dir().to_string_lossy().to_string();
                            let current_path = state.global_data_path.as_deref().unwrap_or(&default_path);
                            let desc = format!("{}\n{}",
                                lang::t("global_data_path_desc", &state.language),
                                current_path,
                            );
                            setting_row(
                                ui,
                                egui_phosphor::regular::FOLDER_OPEN,
                                lang::t("global_data_path", &state.language),
                                &desc,
                                |ui| {
                                    if ui.button(lang::t("change_path", &state.language)).clicked() {
                                        state.trigger_folder_picker = true;
                                    }
                                },
                            );
                            ui.add_space(10.0);
                        }
                    });

                    // 控制台设置
                    setting_section(
                        ui,
                        egui_phosphor::regular::TERMINAL_WINDOW,
                        lang::t("console_settings", &state.language),
                        |ui| {
                            setting_row(
                                ui,
                                egui_phosphor::regular::TERMINAL_WINDOW,
                                lang::t("show_startup_command", &state.language),
                                lang::t("show_startup_command_desc", &state.language),
                                |ui| {
                                    ui.add(crate::ui::switch::toggle(
                                        &mut state.show_startup_command,
                                    ));
                                },
                            );
                        },
                    );

                    // 环境依赖
                    {
                        let is_system = state.env_mode == EnvSource::System;
                        let is_builtin = state.env_mode == EnvSource::Builtin;

                        setting_section(ui, egui_phosphor::regular::PACKAGE, lang::t("env_dependencies", &state.language), |ui| {
                        // 环境模式
                        setting_row(
                            ui,
                            egui_phosphor::regular::WRENCH,
                            lang::t("env_mode", &state.language),
                            lang::t("env_mode_desc", &state.language),
                            |ui| {
                                egui::ComboBox::from_id_salt("env_mode_combo")
                                    .selected_text(match state.env_mode {
                                        EnvSource::System => lang::t("env_mode_system", &state.language),
                                        EnvSource::Builtin => lang::t("env_mode_builtin", &state.language),
                                    })
                                    .show_ui(ui, |ui| {
                                        ui.selectable_value(&mut state.env_mode, EnvSource::System, lang::t("env_mode_system", &state.language));
                                        ui.selectable_value(&mut state.env_mode, EnvSource::Builtin, lang::t("env_mode_builtin", &state.language));
                                    });
                            },
                        );
                        ui.add_space(10.0);
                        // Git
                        {
                            let gv = if is_builtin { state.git_version_builtin.clone() } else { state.git_version.clone() };
                            setting_row(
                                ui,
                                egui_phosphor::regular::GIT_BRANCH,
                                "Git",
                                lang::t("git_purpose", &state.language),
                                |ui| {
                                    match gv {
                                        Some(ref ver) => {
                                            ui.label(egui::RichText::new(ver.as_str()).size(14.0));
                                        }
                                        None => {
                                            if is_system {
                                                ui.label(egui::RichText::new(lang::t("not_installed", &state.language)).size(14.0).color(egui::Color32::GRAY));
                                            } else if ui.button(lang::t("install", &state.language)).clicked() {
                                                git_node_select.open();
                                            }
                                        }
                                    }
                                },
                            );
                        }
                        ui.add_space(10.0);
                        // NodeJs
                        {
                            let nv = if is_builtin {
                                if state.nodejs_version_builtin.is_empty() { None } else { Some(state.nodejs_version_builtin.clone()) }
                            } else {
                                if state.nodejs_version.is_empty() { None } else { Some(state.nodejs_version.clone()) }
                            };
                            let nv_outdated = nv.as_ref().map_or(false, |v| {
                                crate::core::settings::env_detect::is_nodejs_outdated(v)
                            });
                            let title = if nv_outdated {
                                format!("Node.js  ⚠ {}", lang::t("version_too_low", &state.language))
                            } else {
                                "Node.js".to_string()
                            };
                            setting_row(
                                ui,
                                egui_phosphor::regular::CODE,
                                &title,
                                lang::t("nodejs_purpose", &state.language),
                                |ui| {
                                    match nv {
                                        Some(ref ver) if nv_outdated && is_builtin => {
                                            if ui.button(lang::t("upgrade_btn", &state.language)).clicked() {
                                                nodejs_install.start_install("node@24");
                                            }
                                        }
                                        Some(ref ver) => {
                                            ui.label(egui::RichText::new(ver.as_str()).size(14.0));
                                        }
                                        None => {
                                            if is_system {
                                                ui.label(egui::RichText::new(lang::t("not_installed", &state.language)).size(14.0).color(egui::Color32::GRAY));
                                            } else if ui.button(lang::t("install", &state.language)).clicked() {
                                                nodejs_install.start_install("node@24");
                                            }
                                        }
                                    }
                                },
                            );
                        }
                        ui.add_space(10.0);
                        // NPM 源设置
                        setting_row(
                            ui,
                            egui_phosphor::regular::GLOBE,
                            lang::t("npm_registry", &state.language),
                            lang::t("npm_registry_desc", &state.language),
                            |ui| {
                                egui::ComboBox::from_id_salt("npm_registry_combo")
                                    .selected_text(match state.npm_registry {
                                        NpmRegistry::Official => lang::t("official_registry", &state.language),
                                        NpmRegistry::Taobao => lang::t("taobao_registry", &state.language),
                                        NpmRegistry::Tencent => lang::t("tencent_registry", &state.language),
                                    })
                                    .show_ui(ui, |ui| {
                                        ui.selectable_value(&mut state.npm_registry, NpmRegistry::Official, lang::t("official_registry", &state.language));
                                        ui.selectable_value(&mut state.npm_registry, NpmRegistry::Taobao, lang::t("taobao_registry", &state.language));
                                        ui.selectable_value(&mut state.npm_registry, NpmRegistry::Tencent, lang::t("tencent_registry", &state.language));
                                    });
                            },
                        );
                        ui.add_space(10.0);
                        // Caddy
                        {
                            let cv = if is_builtin { state.caddy_version_builtin.clone() } else { state.caddy_version.clone() };
                            setting_row(
                                ui,
                                egui_phosphor::regular::SHIELD_CHECK,
                                "Caddy",
                                lang::t(if state.server_mode_enabled { "caddy_purpose_required" } else { "caddy_purpose" }, &state.language),
                                |ui| {
                                    match cv {
                                        Some(ref ver) => {
                                            ui.label(egui::RichText::new(ver.as_str()).size(14.0));
                                        }
                                        None => {
                                            if is_system {
                                                ui.label(egui::RichText::new(lang::t("not_installed", &state.language)).size(14.0).color(egui::Color32::GRAY));
                                            } else if ui.button(lang::t("install", &state.language)).clicked() {
                                                caddy_install.start_install("caddy");
                                            }
                                        }
                                    }
                                },
                            );
                        }
                        ui.add_space(10.0);
                        // PM2
                        {
                            let pv = if is_builtin { state.pm2_version_builtin.clone() } else { state.pm2_version.clone() };
                            let nodejs_installed = if is_builtin {
                                !state.nodejs_version_builtin.is_empty()
                            } else {
                                !state.nodejs_version.is_empty()
                            };
                            setting_row(
                                ui,
                                egui_phosphor::regular::CLOUD_ARROW_DOWN,
                                "PM2",
                                lang::t(if state.server_mode_enabled { "pm2_purpose_required" } else { "pm2_purpose" }, &state.language),
                                |ui| {
                                    match pv {
                                        Some(ref ver) => {
                                            ui.label(egui::RichText::new(ver.as_str()).size(14.0));
                                        }
                                        None => {
                                            if is_system {
                                                ui.label(egui::RichText::new(lang::t("not_installed", &state.language)).size(14.0).color(egui::Color32::GRAY));
                                            } else {
                                                let btn = egui::Button::new(lang::t("install", &state.language));
                                                let resp = if nodejs_installed {
                                                    ui.add_enabled(true, btn)
                                                } else {
                                                    ui.add_enabled(false, btn)
                                                        .on_disabled_hover_text(lang::t("pm2_need_nodejs", &state.language))
                                                };
                                                if resp.clicked() {
                                                    pm2_install.start_npm_install("pm2");
                                                }
                                            }
                                        }
                                    }
                                },
                            );
                        }
                    });

                    // Git 节点选择弹窗（安装前选择下载源）
                    {
                        render_git_node_select_popup(
                            ui.ctx(),
                            git_node_select,
                            &state.language,
                        );
                        // 检查节点选中后的安装触发
                        if let Some(url) = git_node_select.install_triggered_url.take() {
                            git_install.start_git_install(&url);
                        }
                    }

                    // Git 安装弹窗（进度条模式）
                    if render_git_install_window(
                        ui.ctx(),
                        git_install,
                        lang::t("git_install_title", &state.language),
                        lang::t("git_install_desc", &state.language),
                        lang::t("close", &state.language),
                        lang::t("install_timeout", &state.language),
                    ) {
                        // 安装成功后自动刷新环境检测
                        state.detect_all_env();
                        state.save();
                    }

                    // NodeJs 安装弹窗
                    render_brew_task_window(
                        ui.ctx(),
                        nodejs_install,
                        lang::t("nodejs_install_title", &state.language),
                        lang::t("nodejs_install_desc", &state.language),
                        lang::t("brew_install_waiting", &state.language),
                        lang::t("brew_install_running", &state.language),
                        lang::t("close", &state.language),
                        lang::t("install_timeout", &state.language),
                    );

                    // Caddy 安装弹窗
                    render_brew_task_window(
                        ui.ctx(),
                        caddy_install,
                        lang::t("caddy_install_title", &state.language),
                        lang::t("caddy_install_desc", &state.language),
                        lang::t("brew_install_waiting", &state.language),
                        lang::t("brew_install_running", &state.language),
                        lang::t("close", &state.language),
                        lang::t("install_timeout", &state.language),
                    );

                    // PM2 安装弹窗
                    render_brew_task_window(
                        ui.ctx(),
                        pm2_install,
                        lang::t("pm2_install_title", &state.language),
                        lang::t("pm2_install_desc", &state.language),
                        lang::t("brew_install_waiting", &state.language),
                        lang::t("brew_install_running", &state.language),
                        lang::t("close", &state.language),
                        lang::t("install_timeout", &state.language),
                    );
                    }

                    // Github 设置
                    setting_section(
                        ui,
                        egui_phosphor::regular::GITHUB_LOGO,
                        lang::t("github_settings", &state.language),
                        |ui| {
                            setting_row(
                                ui,
                                egui_phosphor::regular::POWER,
                                lang::t("github_proxy", &state.language),
                                lang::t("github_proxy_desc", &state.language),
                                |ui| {
                                    let mut enabled = state.github_proxy_enabled;
                                    if ui.add(crate::ui::switch::toggle(&mut enabled)).changed() {
                                        state.github_proxy_enabled = enabled;
                                        if enabled {
                                            state.proxy_type = ProxyType::None;
                                        }
                                    }
                                },
                            );
                            ui.add_space(10.0);

                            // 节点列表标题行（带刷新按钮）
                            ui.horizontal(|ui| {
                                ui.add_sized(
                                    [30.0, 30.0],
                                    egui::Label::new(
                                        egui::RichText::new(egui_phosphor::regular::LIST)
                                            .size(20.0),
                                    ),
                                );
                                ui.vertical(|ui| {
                                    ui.add_space(2.0);
                                    ui.label(
                                        egui::RichText::new(lang::t("github_nodes", &state.language))
                                            .size(14.0)
                                            .strong(),
                                    );
                                    ui.label(
                                        egui::RichText::new(lang::t("github_nodes_desc", &state.language))
                                            .color(egui::Color32::GRAY)
                                            .size(12.0),
                                    );
                                });
                                ui.with_layout(
                                    egui::Layout::right_to_left(egui::Align::Center),
                                    |ui| {
                                        let is_loading = matches!(
                                            github_node_state,
                                            crate::core::settings::github_proxy::NodeLoadState::Loading
                                        );
                                        ui.add_enabled_ui(!is_loading, |ui| {
                                            if ui
                                                .button(lang::t("refresh_nodes", &state.language))
                                                .clicked()
                                            {
                                                *on_refresh_nodes = true;
                                            }
                                        });
                                        if is_loading {
                                            ui.spinner();
                                        }
                                    },
                                );
                            });

                            ui.add_space(8.0);

                            if !state.github_proxy_enabled {
                                ui.label(
                                    egui::RichText::new(lang::t("enable_proxy_first", &state.language))
                                        .color(egui::Color32::GRAY),
                                );
                            } else {
                                match github_node_state {
                                    crate::core::settings::github_proxy::NodeLoadState::Loading => {
                                        ui.horizontal(|ui| {
                                            ui.spinner();
                                            ui.label(lang::t("loading_nodes", &state.language));
                                        });
                                    }
                                    crate::core::settings::github_proxy::NodeLoadState::Done(entries) => {

                                        // 排序：已认证节点优先，同组内按实测延迟排序
                                        let mut sorted_entries = entries.clone();
                                        sorted_entries.sort_by(|a, b| {
                                            let a_dev = a.source == "开发者提供";
                                            let b_dev = b.source == "开发者提供";
                                            // 开发者优先
                                            match (a_dev, b_dev) {
                                                (true, false) => std::cmp::Ordering::Less,
                                                (false, true) => std::cmp::Ordering::Greater,
                                                _ => {
                                                    let a_ms = *a.measured_ms.lock().unwrap();
                                                    let b_ms = *b.measured_ms.lock().unwrap();
                                                    match (a_ms, b_ms) {
                                                        (None, None) => std::cmp::Ordering::Equal,
                                                        (None, _) => std::cmp::Ordering::Greater,
                                                        (_, None) => std::cmp::Ordering::Less,
                                                        (Some(None), Some(None)) => std::cmp::Ordering::Equal,
                                                        (Some(None), Some(Some(_))) => std::cmp::Ordering::Greater,
                                                        (Some(Some(_)), Some(None)) => std::cmp::Ordering::Less,
                                                        (Some(Some(a)), Some(Some(b))) => a.cmp(&b),
                                                    }
                                                }
                                            }
                                        });

                                        // 节点表格 — 4 列：选择 / 节点地址 / 实测延迟 / 来源
                                        let select_w: f32 = 50.0;
                                        let latency_w: f32 = 100.0;
                                        let tag_w: f32 = 150.0;
                                        let url_min_w: f32 = 220.0;
                                        let spacing: f32 = 16.0;

                                        egui::ScrollArea::new(egui::Vec2b::TRUE)
                                            .id_salt("github_nodes_scroll")
                                            .max_height(400.0)
                                            .min_scrolled_height(400.0)
                                            .show(ui, |ui| {
                                                let url_calc: f32 = (ui.available_width()
                                                    - select_w - latency_w - tag_w - spacing * 3.0)
                                                    .max(url_min_w);
                                                egui::Grid::new("github_nodes_grid")
                                                    .striped(true)
                                                    .num_columns(4)
                                                    .spacing(egui::vec2(spacing, 6.0))
                                                    .show(ui, |ui| {
                                                        let centered = egui::Layout::centered_and_justified(egui::Direction::TopDown);

                                                        // 表头
                                                        ui.allocate_ui_with_layout(
                                                            egui::vec2(select_w, 28.0),
                                                            centered,
                                                            |ui| {
                                                                ui.strong(lang::t("col_select", &state.language));
                                                            },
                                                        );
                                                        ui.allocate_ui_with_layout(
                                                            egui::vec2(url_calc, 28.0),
                                                            egui::Layout::left_to_right(egui::Align::Center),
                                                            |ui| {
                                                                ui.strong(lang::t("col_url", &state.language));
                                                            },
                                                        );
                                                        ui.allocate_ui_with_layout(
                                                            egui::vec2(latency_w, 28.0),
                                                            centered,
                                                            |ui| {
                                                                ui.strong(lang::t("col_latency", &state.language));
                                                            },
                                                        );
                                                        ui.allocate_ui_with_layout(
                                                            egui::vec2(tag_w, 28.0),
                                                            centered,
                                                            |ui| {
                                                                ui.strong(lang::t("col_source", &state.language));
                                                            },
                                                        );
                                                        ui.end_row();

                                                        for entry in sorted_entries.iter() {
                                                            let is_selected =
                                                                state.github_proxy_url == entry.url;

                                                            // 选择列
                                                            ui.allocate_ui_with_layout(
                                                                egui::vec2(select_w, 28.0),
                                                                centered,
                                                                |ui| {
                                                                    let mut sel = is_selected;
                                                                    if ui.radio(sel, "").clicked() {
                                                                        sel = true;
                                                                    }
                                                                    if sel && !is_selected {
                                                                        state.github_proxy_url =
                                                                            entry.url.clone();
                                                                    }
                                                                },
                                                            );
                                                            // 节点地址
                                                            let url_display = entry
                                                                .url
                                                                .trim_start_matches("https://")
                                                                .trim_start_matches("http://")
                                                                .trim_end_matches('/');
                                                            ui.allocate_ui_with_layout(
                                                                egui::vec2(url_calc, 28.0),
                                                                egui::Layout::left_to_right(egui::Align::Center),
                                                                |ui| {
                                                                    ui.label(
                                                                        egui::RichText::new(url_display)
                                                                            .size(13.0)
                                                                            .color(ui.visuals().text_color()),
                                                                    )
                                                                    .on_hover_text(entry.url.clone());
                                                                },
                                                            );
                                                            // 实测延迟
                                                            let latency_text = {
                                                                let guard =
                                                                    entry.measured_ms.lock().unwrap();
                                                                match &*guard {
                                                                    None => lang::t("testing", &state.language).to_string(),
                                                                    Some(None) => lang::t("timeout", &state.language).to_string(),
                                                                    Some(Some(ms)) => format!("{ms} ms"),
                                                                }
                                                            };
                                                            let latency_color = {
                                                                let guard =
                                                                    entry.measured_ms.lock().unwrap();
                                                                match &*guard {
                                                                    Some(Some(ms)) if *ms < 200 => {
                                                                        egui::Color32::from_rgb(80, 200, 100)
                                                                    }
                                                                    Some(Some(ms)) if *ms < 500 => {
                                                                        egui::Color32::from_rgb(230, 180, 60)
                                                                    }
                                                                    Some(Some(_)) => {
                                                                        egui::Color32::from_rgb(220, 80, 60)
                                                                    }
                                                                    _ => egui::Color32::GRAY,
                                                                }
                                                            };
                                                            ui.allocate_ui_with_layout(
                                                                egui::vec2(latency_w, 28.0),
                                                                centered,
                                                                |ui| {
                                                                    ui.label(
                                                                        egui::RichText::new(&latency_text)
                                                                            .size(13.0)
                                                                            .color(latency_color),
                                                                    );
                                                                },
                                                            );
                                                            // 来源
                                                            ui.allocate_ui_with_layout(
                                                                egui::vec2(tag_w, 28.0),
                                                                centered,
                                                                |ui| {
                                                                    let is_dev = entry.source == "开发者提供";
                                                                    let lang_key = state.language;
                                                                    if is_dev {
                                                                        ui.vertical_centered(|ui| {
                                                                            ui.horizontal(|ui| {
                                                                                ui.label(
                                                                                    egui::RichText::new(format!(
                                                                                        "✓ {}",
                                                                                        lang::t("verified_badge", &lang_key)
                                                                                    ))
                                                                                    .size(12.0)
                                                                                    .color(egui::Color32::from_rgb(0, 180, 80)),
                                                                                );
                                                                                let tag_text = if entry.tag.is_empty() { "-" } else { &entry.tag };
                                                                                ui.label(
                                                                                    egui::RichText::new(tag_text)
                                                                                        .size(12.0)
                                                                                        .color(egui::Color32::from_rgb(60, 160, 80)),
                                                                                );
                                                                            });
                                                                        });
                                                                    } else {
                                                                        ui.vertical_centered(|ui| {
                                                                            ui.horizontal(|ui| {
                                                                                ui.label(
                                                                                    egui::RichText::new(lang::t("source_third_party", &lang_key))
                                                                                        .size(12.0)
                                                                                        .color(egui::Color32::GRAY),
                                                                                );
                                                                                let tag_text = if entry.tag.is_empty() { "-" } else { &entry.tag };
                                                                                ui.label(
                                                                                    egui::RichText::new(tag_text)
                                                                                        .size(12.0),
                                                                                );
                                                                            });
                                                                        });
                                                                    }
                                                                },
                                                            );
                                                            ui.end_row();
                                                        }
                                                    });
                                            });

                                        // 当前选中节点提示
                                        if !state.github_proxy_url.is_empty() {
                                            ui.add_space(6.0);
                                            ui.horizontal(|ui| {
                                                ui.label(
                                                    egui::RichText::new(lang::t("selected_node", &state.language))
                                                        .size(12.0)
                                                        .color(egui::Color32::GRAY),
                                                );
                                                ui.label(
                                                    egui::RichText::new(&state.github_proxy_url)
                                                        .size(12.0)
                                                        .color(egui::Color32::from_rgb(100, 160, 240)),
                                                );
                                            });
                                        }
                                    }
                                }
                            }
                        },
                    );

                    // 网络设置
                    setting_section(
                        ui,
                        egui_phosphor::regular::WIFI_HIGH,
                        lang::t("network_settings", &state.language),
                        |ui| {
                            let mut proxy_desc =
                                lang::t("proxy_settings_desc", &state.language).to_string();
                            if state.proxy_type == ProxyType::System {
                                let status_text = match crate::core::network::read_system_proxy()
                                {
                                    Some((_, true)) => {
                                        lang::t("on", &state.language)
                                    }
                                    _ => {
                                        // None 或无代理配置 = 关闭
                                        lang::t("off", &state.language)
                                    }
                                };
                                proxy_desc = format!(
                                    "{} ({} {})",
                                    proxy_desc,
                                    lang::t("system_proxy_status", &state.language),
                                    status_text
                                );
                            }

                            setting_row(
                                ui,
                                egui_phosphor::regular::SHIELD,
                                lang::t("proxy_settings", &state.language),
                                &proxy_desc,
                                |ui| {
                                    let mut pt = state.proxy_type.clone();
                                    egui::ComboBox::from_id_salt("proxy_type_combo")
                                        .selected_text(match pt {
                                            ProxyType::None => lang::t("off", &state.language),
                                            ProxyType::System => {
                                                lang::t("follow_system", &state.language)
                                            }
                                            ProxyType::Custom => {
                                                lang::t("custom_proxy", &state.language)
                                            }
                                        })
                                        .show_ui(ui, |ui| {
                                            ui.selectable_value(
                                                &mut pt,
                                                ProxyType::None,
                                                lang::t("off", &state.language),
                                            );
                                            ui.selectable_value(
                                                &mut pt,
                                                ProxyType::System,
                                                lang::t("follow_system", &state.language),
                                            );
                                            ui.selectable_value(
                                                &mut pt,
                                                ProxyType::Custom,
                                                lang::t("custom_proxy", &state.language),
                                            );
                                        });

                                    if pt != state.proxy_type {
                                        state.proxy_type = pt;
                                        if state.proxy_type != ProxyType::None {
                                            state.github_proxy_enabled = false;
                                        }
                                    }
                                },
                            );

                            if state.proxy_type == ProxyType::Custom {
                                ui.add_space(10.0);
                                setting_row(
                                    ui,
                                    egui_phosphor::regular::LINK,
                                    lang::t("proxy_address", &state.language),
                                    lang::t("proxy_address_desc", &state.language),
                                    |ui| {
                                        ui.text_edit_singleline(&mut state.custom_proxy);
                                    },
                                );
                            }

                            ui.add_space(10.0);
                            setting_row(
                                ui,
                                egui_phosphor::regular::PLUG,
                                lang::t("github_test", &state.language),
                                lang::t("github_test_desc", &state.language),
                                |ui| {
                                    if ui
                                        .button(lang::t("start_test", &state.language))
                                        .clicked()
                                    {
                                        let mut popup_state =
                                            GITHUB_TEST_POPUP_STATE.lock().unwrap();
                                        popup_state.show = true;
                                        popup_state.results.clear();

                                        // 启动测试
                                        let has_proxy = state.proxy_type != ProxyType::None;
                                        let has_accelerate = state.github_proxy_enabled;

                                        let (proxy_mode, proxy_host, accelerate_url) =
                                            match (has_proxy, has_accelerate) {
                                                (true, true) => {
                                                    let p_mode = match state.proxy_type {
                                                        ProxyType::System => "system",
                                                        ProxyType::Custom => "custom",
                                                        ProxyType::None => "none",
                                                    };
                                                    (
                                                        p_mode,
                                                        state.custom_proxy.clone(),
                                                        Some(state.github_proxy_url.clone()),
                                                    )
                                                }
                                                (true, false) => {
                                                    let p_mode = match state.proxy_type {
                                                        ProxyType::System => "system",
                                                        ProxyType::Custom => "custom",
                                                        ProxyType::None => "none",
                                                    };
                                                    (
                                                        p_mode,
                                                        state.custom_proxy.clone(),
                                                        None,
                                                    )
                                                }
                                                (false, true) => (
                                                    "none",
                                                    String::new(),
                                                    Some(state.github_proxy_url.clone()),
                                                ),
                                                (false, false) => ("none", String::new(), None),
                                            };

                                        crate::core::network::start_github_multi_test(
                                            proxy_mode,
                                            &proxy_host,
                                            0,
                                            accelerate_url,
                                            true,
                                        );
                                    }
                                },
                            );
                        },
                    );

                    ui.add_space(20.0);
                });
            }
        SettingsTab::About => {
            ui.vertical_centered(|ui| {
                ui.heading(lang::t("about_title", &state.language));
                ui.label(lang::t("about_version", &state.language));
                ui.label(lang::t("about_desc", &state.language));

                // 检查更新按钮
                ui.add_space(10.0);
                let busy = state.update_checking || state.update_downloading;
                if ui
                    .add_enabled(
                        !busy,
                        egui::Button::new(lang::t(
                            if state.update_downloading {
                                "updating"
                            } else if state.update_checking {
                                "checking_update"
                            } else {
                                "check_update"
                            },
                            &state.language,
                        )),
                    )
                    .clicked()
                {
                    state.check_update_trigger = true;
                    state.update_checking = true;
                }

                ui.add_space(20.0);
                ui.separator();
                ui.add_space(10.0);

                ui.heading(lang::t("tech_stack", &state.language));
                ui.add_space(10.0);

                ui.with_layout(egui::Layout::top_down(egui::Align::Center), |ui| {
                    egui::ScrollArea::vertical()
                        .auto_shrink([false, false])
                        .show(ui, |ui| {
                            egui::Grid::new("tech_stack_grid")
                                .striped(true)
                                .num_columns(4)
                                .min_col_width(100.0)
                                .spacing(egui::vec2(30.0, 15.0))
                                .show(ui, |ui| {
                                    ui.vertical_centered(|ui| ui.strong(lang::t("tech_col_1", &state.language)));
                                    ui.vertical_centered(|ui| ui.strong(lang::t("tech_col_2", &state.language)));
                                    ui.vertical_centered(|ui| ui.strong(lang::t("tech_col_3", &state.language)));
                                    ui.vertical_centered(|ui| ui.strong(lang::t("tech_col_4", &state.language)));
                                    ui.end_row();

                                    ui.vertical_centered(|ui| ui.label("MiSans"));
                                    ui.vertical_centered(|ui| ui.label("2022"));
                                    ui.vertical_centered(|ui| {
                                        ui.hyperlink_to(lang::t("free_commercial", &state.language), "https://hyperos.mi.com/font/zh/faq/");
                                    });
                                    ui.vertical_centered(|ui| ui.label(lang::t("mi_font", &state.language)));
                                    ui.end_row();

                                    ui.vertical_centered(|ui| ui.label("Rust"));
                                    ui.vertical_centered(|ui| ui.label("2024"));
                                    ui.vertical_centered(|ui| {
                                        ui.hyperlink_to("MIT / Apache-2.0", "https://github.com/rust-lang/rust/blob/master/LICENSE-MIT");
                                    });
                                    ui.vertical_centered(|ui| ui.label(lang::t("rust_desc", &state.language)));
                                    ui.end_row();

                                    ui.vertical_centered(|ui| ui.label("egui"));
                                    ui.vertical_centered(|ui| ui.label("0.33"));
                                    ui.vertical_centered(|ui| {
                                        ui.hyperlink_to("MIT / Apache-2.0", "https://github.com/emilk/egui/blob/master/LICENSE-MIT");
                                    });
                                    ui.vertical_centered(|ui| ui.label(lang::t("egui_desc", &state.language)));
                                    ui.end_row();

                                    ui.vertical_centered(|ui| ui.label("eframe"));
                                    ui.vertical_centered(|ui| ui.label("0.33"));
                                    ui.vertical_centered(|ui| {
                                        ui.hyperlink_to("MIT / Apache-2.0", "https://github.com/emilk/egui/blob/master/LICENSE-MIT");
                                    });
                                    ui.vertical_centered(|ui| ui.label(lang::t("eframe_desc", &state.language)));
                                    ui.end_row();

                                    ui.vertical_centered(|ui| ui.label("egui_phosphor"));
                                    ui.vertical_centered(|ui| ui.label("0.11"));
                                    ui.vertical_centered(|ui| {
                                        ui.hyperlink_to("MIT / Apache-2.0", "https://github.com/amPerl/egui-phosphor/blob/main/LICENSE-MIT");
                                    });
                                    ui.vertical_centered(|ui| ui.label(lang::t("phosphor_desc", &state.language)));
                                    ui.end_row();
                                });
                        });
                });
            });

            // 确认更新弹窗
            if state.update_confirm_open {
                let ctx = ui.ctx();
                egui::Window::new(lang::t("update_found", &state.language))
                    .collapsible(false)
                    .resizable(false)
                    .anchor(egui::Align2::CENTER_CENTER, egui::vec2(0.0, 0.0))
                    .show(ctx, |ui| {
                        ui.add_space(8.0);
                        let desc = lang::t("update_confirm_desc", &state.language)
                            .replace("{version}", &state.update_confirm_version)
                            .replace(
                                "{notes}",
                                &state
                                    .update_confirm_notes
                                    .as_ref()
                                    .map(|n| format!("{n}\n\n"))
                                    .unwrap_or_default(),
                            );
                        ui.label(desc);
                        ui.add_space(16.0);
                        ui.horizontal(|ui| {
                            if ui
                                .button(lang::t("update_later", &state.language))
                                .clicked()
                            {
                                state.update_confirm_open = false;
                            }
                            if ui
                                .button(lang::t("update_now", &state.language))
                                .clicked()
                            {
                                state.update_confirm_open = false;
                                state.update_downloading = true;
                                state.do_update_trigger = Some(state.update_confirm_endpoint.clone());
                            }
                        });
                        ui.add_space(8.0);
                    });
            }
        }
    }

    // === Github 测试弹窗 ===
    {
        // 获取弹窗状态（不持有锁）
        let (show, results) = {
            let popup_state = GITHUB_TEST_POPUP_STATE.lock().unwrap();
            (popup_state.show, popup_state.results.clone())
        };

        if show {
            let mut open = true;
            let mut results = results;

            egui::Window::new(lang::t("github_test", &state.language))
                .open(&mut open)
                .resizable(true)
                .default_width(400.0)
                .show(ui.ctx(), |ui| {
                    // 检查测试是否正在进行
                    let testing = crate::core::network::is_github_multi_test_in_progress();

                    let has_proxy = state.proxy_type != ProxyType::None;
                    let has_accelerate = state.github_proxy_enabled;

                    let mode_text = match (has_proxy, has_accelerate) {
                        (false, false) => lang::t("direct_mode", &state.language),
                        (true, false) => lang::t("proxy_only_mode", &state.language),
                        (false, true) => lang::t("accelerate_only_mode", &state.language),
                        (true, true) => lang::t("proxy_and_accelerate_mode", &state.language),
                    };

                    ui.horizontal(|ui| {
                        ui.label(
                            egui::RichText::new(lang::t("test_mode", &state.language)).strong(),
                        );
                        ui.label(mode_text);

                        ui.with_layout(
                            egui::Layout::right_to_left(egui::Align::Center),
                            |ui| {
                                if testing {
                                    ui.spinner();
                                    ui.label(lang::t("testing", &state.language));
                                } else {
                                    if ui
                                        .button(lang::t("start_test", &state.language))
                                        .clicked()
                                    {
                                        results.clear();
                                        let mut popup_state =
                                            GITHUB_TEST_POPUP_STATE.lock().unwrap();
                                        popup_state.show = true;
                                        popup_state.results.clear();

                                        // 启动测试
                                        let has_proxy =
                                            state.proxy_type != ProxyType::None;
                                        let has_accelerate = state.github_proxy_enabled;

                                        let (proxy_mode, proxy_host, accelerate_url) =
                                            match (has_proxy, has_accelerate) {
                                                (true, true) => {
                                                    let p_mode = match state.proxy_type {
                                                        ProxyType::System => "system",
                                                        ProxyType::Custom => "custom",
                                                        ProxyType::None => "none",
                                                    };
                                                    (
                                                        p_mode,
                                                        state.custom_proxy.clone(),
                                                        Some(
                                                            state.github_proxy_url.clone(),
                                                        ),
                                                    )
                                                }
                                                (true, false) => {
                                                    let p_mode = match state.proxy_type {
                                                        ProxyType::System => "system",
                                                        ProxyType::Custom => "custom",
                                                        ProxyType::None => "none",
                                                    };
                                                    (
                                                        p_mode,
                                                        state.custom_proxy.clone(),
                                                        None,
                                                    )
                                                }
                                                (false, true) => (
                                                    "none",
                                                    String::new(),
                                                    Some(state.github_proxy_url.clone()),
                                                ),
                                                (false, false) => {
                                                    ("none", String::new(), None)
                                                }
                                            };

                                        crate::core::network::start_github_multi_test(
                                            proxy_mode,
                                            &proxy_host,
                                            0,
                                            accelerate_url,
                                            true,
                                        );
                                    }
                                }
                            },
                        );
                    });

                    if has_accelerate {
                        ui.horizontal(|ui| {
                            ui.label(
                                egui::RichText::new(lang::t("accelerate_url", &state.language)).strong(),
                            );
                            ui.label(
                                egui::RichText::new(&state.github_proxy_url)
                                    .color(egui::Color32::LIGHT_BLUE),
                            );
                        });
                    }

                    ui.separator();

                    if testing || !results.is_empty() {
                        ui.heading(lang::t("test_results", &state.language));
                        ui.add_space(5.0);

                        egui::ScrollArea::vertical()
                            .max_height(300.0)
                            .show(ui, |ui| {
                                if testing && results.is_empty() {
                                    let expected_tests = [
                                        "文件访问",
                                        "仓库访问",
                                        "首页访问",
                                        "API 访问",
                                        "下载速度",
                                    ];
                                    for name in expected_tests {
                                        ui.horizontal(|ui| {
                                            ui.label(name);
                                            ui.with_layout(
                                                egui::Layout::right_to_left(
                                                    egui::Align::Center,
                                                ),
                                                |ui| {
                                                    ui.spinner();
                                                    ui.label(
                                                        egui::RichText::new(
                                                            lang::t(
                                                                "testing",
                                                                &state.language,
                                                            ),
                                                        )
                                                        .color(egui::Color32::GRAY),
                                                    );
                                                },
                                            );
                                        });
                                        ui.separator();
                                    }
                                } else {
                                    for item in &results {
                                        ui.horizontal(|ui| {
                                            ui.label(&item.name);

                                            ui.with_layout(
                                                egui::Layout::right_to_left(
                                                    egui::Align::Center,
                                                ),
                                                |ui| {
                                                    if let Some(warn) = &item.warning
                                                    {
                                                        ui.label(
                                                            egui::RichText::new(
                                                                egui_phosphor::regular::WARNING_CIRCLE,
                                                            )
                                                            .color(
                                                                egui::Color32::YELLOW,
                                                            ),
                                                        )
                                                        .on_hover_text(warn);

                                                        let short_msg =
                                                            if warn.contains("速度") {
                                                                if let Some(start) =
                                                                    warn.find('(')
                                                                {
                                                                    if let Some(end) =
                                                                        warn.find(')')
                                                                    {
                                                                        &warn[start + 1
                                                                            ..end]
                                                                    } else {
                                                                        "异常"
                                                                    }
                                                                } else {
                                                                    "异常"
                                                                }
                                                            } else if warn.contains("加速") {
                                                                "受限"
                                                            } else {
                                                                "异常"
                                                            };
                                                        ui.label(
                                                            egui::RichText::new(short_msg)
                                                                .color(
                                                                    egui::Color32::GRAY,
                                                                ),
                                                        );
                                                    } else if item.success {
                                                        let mut hover_text = lang::t(
                                                            "connectivity_available",
                                                            &state.language,
                                                        )
                                                        .to_string();
                                                        if let Some(latency) =
                                                            item.latency_ms
                                                        {
                                                            hover_text.push_str(&format!(
                                                                "\n{} ms",
                                                                latency
                                                            ));
                                                        }
                                                        ui.label(
                                                            egui::RichText::new(
                                                                egui_phosphor::regular::CHECK_CIRCLE,
                                                            )
                                                            .color(
                                                                egui::Color32::GREEN,
                                                            ),
                                                        )
                                                        .on_hover_text(hover_text);

                                                        if let Some(latency) =
                                                            item.latency_ms
                                                        {
                                                            ui.label(
                                                                egui::RichText::new(
                                                                    format!(
                                                                        "{}ms",
                                                                        latency
                                                                    ),
                                                                )
                                                                .color(
                                                                    egui::Color32::GRAY,
                                                                ),
                                                            );
                                                        } else {
                                                            ui.label(
                                                                egui::RichText::new(lang::t(
                                                                    "success",
                                                                    &state.language,
                                                                ))
                                                                .color(
                                                                    egui::Color32::GRAY,
                                                                ),
                                                            );
                                                        }
                                                    } else {
                                                        let err_text =
                                                            item.error.as_deref().unwrap_or(
                                                                lang::t(
                                                                    "connectivity_unavailable",
                                                                    &state.language,
                                                                ),
                                                            );
                                                        ui.label(
                                                            egui::RichText::new(
                                                                egui_phosphor::regular::X_CIRCLE,
                                                            )
                                                            .color(
                                                                egui::Color32::RED,
                                                            ),
                                                        )
                                                        .on_hover_text(err_text);

                                                        let short_err =
                                                            if err_text.contains("超时") || err_text.contains("timeout")
                                                            {
                                                                "超时"
                                                            } else if err_text
                                                                .contains("HTTP")
                                                            {
                                                                "拒绝"
                                                            } else {
                                                                "失败"
                                                            };
                                                        ui.label(
                                                            egui::RichText::new(short_err)
                                                                .color(
                                                                    egui::Color32::GRAY,
                                                                ),
                                                        );
                                                    }
                                                },
                                            );
                                        });
                                        ui.separator();
                                    }
                                }
                            });
                    }
                });

            // 检查测试是否完成（不持有锁时调用）
            if let Some(test_results) = crate::core::network::get_github_multi_test_result()
            {
                results = test_results;
            }

            // 保存状态
            let mut popup_state = GITHUB_TEST_POPUP_STATE.lock().unwrap();

            if !open && popup_state.show {
                crate::core::network::cancel_github_multi_test();
                results.clear();
            }

            popup_state.show = open;
            popup_state.results = results;
        }
    }

    // === 反向代理弹窗 ===
    crate::pages::reverse_proxy_popup::render_reverse_proxy_popup(
        ui.ctx(),
        state,
        &state.language.clone(),
    );
}
