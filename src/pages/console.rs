use crate::core::settings::pm2::Pm2Manager;
use crate::core::tavern_process::TavernProcess;
use crate::lang;
use crate::pages::settings::{Language, ProxyType, ServerServiceMode, TavernDataMode};
use egui::text::LayoutJob;
use egui::{Color32, RichText, TextFormat, Vec2};
use std::path::PathBuf;
use std::collections::VecDeque;

// 最大保留日志行数，超过时从头部修剪
const MAX_LOG_LINES: usize = 2000;

#[derive(PartialEq, Clone)]
pub enum ConsoleStatus {
    Stopped,
    Starting,
    Running,
    Stopping,
}

pub struct ConsoleState {
    pub status: ConsoleStatus,
    pub logs: VecDeque<String>,
    /// 缓存每条日志对应的解析结果（None 表示包含 URL，需要在渲染时动态处理）
    parsed_layouts: VecDeque<Option<LayoutJob>>,

    // 进程管理
    process: TavernProcess,
    /// PM2 管理器（当 allow_tavern_background 启用时使用）
    pm2_manager: Pm2Manager,
    /// 是否使用 PM2 模式
    use_pm2: bool,
    /// PM2 日志读取字节偏移量（追踪 out.log 文件已读位置）
    pm2_log_byte_offset: u64,
    /// 上次 PM2 轮询时间（节流用，避免每帧调用 pm2 CLI 导致 UI 卡顿）
    last_pm2_poll: std::time::Instant,
    /// PM2 可用性缓存（避免每帧检测 pm2 --version）
    pm2_available_cache: bool,
    /// 上次检测 PM2 可用性的时间
    last_pm2_check: std::time::Instant,
    /// PM2 状态是否已从进程中恢复（启动器重新打开时恢复 PM2 托管状态）
    pm2_state_restored: bool,
    /// 重启标志：停止完成后自动启动
    restart_pending: bool,
    /// 酒馆实例工作目录
    instance_path: String,
    /// 实例类型（"builtin" / "local"）
    instance_type: String,
    /// 实例版本号
    pub instance_version: String,
    /// 当前数据模式
    data_mode: TavernDataMode,
    /// HTTP 代理类型
    proxy_type: ProxyType,
    /// 自定义代理地址（ProxyType::Custom 时有效）
    custom_proxy: String,
    /// GitHub 加速代理 URL（启用时通过拦截器注入）
    github_proxy_url: Option<String>,
    /// 是否在启动日志中显示完整命令行
    show_startup_command: bool,
    /// 酒馆访问地址（从日志中解析 "Go to: http://... to open SillyTavern"）
    pub tavern_url: Option<String>,
    /// 桌面模式：关闭 WebView 时是否自动停止服务
    pub desktop_auto_stop: bool,
    /// 重新打开 WebView 的触发标记（控制台"打开酒馆"按钮点击后置 true，main.rs 消费后置 false）
    pub reopen_webview_triggered: bool,
    /// 桌面模式：WebView 是否已自动打开过（防止关闭后循环重新打开）
    pub webview_auto_opened: bool,
    /// 当前启动模式是否为桌面模式（状态栏不显示访问酒馆链接）
    pub is_desktop_mode: bool,
    /// 当前是否为服务器模式（禁止酒馆自动打开浏览器）
    pub is_server_mode: bool,
    /// 服务器模式下的服务模式（局域网/互联网），用于访问酒馆弹窗
    pub server_service_mode: ServerServiceMode,
    /// 待显示的连接通知队列（main.rs 每帧 drain 并推送到 NotificationStack）
    pub pending_connection_notifications: Vec<String>,
    /// 已通知过的连接 IP+UA 哈希集合，避免同一连接重复通知
    notified_connections: std::collections::HashSet<String>,
    /// 优化后的 settings.json 是否已针对当前实例准备完毕
    settings_prepared: bool,
    /// 全局数据路径（全局数据模式下用户自定义路径）
    global_data_path: Option<String>,
}

impl ConsoleState {
    pub fn new() -> Self {
        Self {
            status: ConsoleStatus::Stopped,
            logs: VecDeque::from(vec![String::from("[系统] 控制台已就绪")]),
            
            process: TavernProcess::new(),
            pm2_manager: Pm2Manager::new(),
            use_pm2: false,
            pm2_log_byte_offset: 0,
            last_pm2_poll: std::time::Instant::now(),
            pm2_available_cache: Pm2Manager::is_installed(),
            last_pm2_check: std::time::Instant::now(),
            pm2_state_restored: false,
            restart_pending: false,
            instance_path: String::new(),
            instance_type: String::new(),
            instance_version: String::new(),
            data_mode: TavernDataMode::Current,
            proxy_type: ProxyType::None,
            custom_proxy: String::new(),
            github_proxy_url: None,
            show_startup_command: false,
            tavern_url: None,
            desktop_auto_stop: true,
            reopen_webview_triggered: false,
            webview_auto_opened: false,
            is_desktop_mode: false,
            is_server_mode: false,
            server_service_mode: ServerServiceMode::default(),
            pending_connection_notifications: Vec::new(),
            notified_connections: std::collections::HashSet::new(),
            settings_prepared: false,
            global_data_path: None,
            parsed_layouts: VecDeque::from(vec![Some(parse_ansi_line(
                "[系统] 控制台已就绪",
                egui::FontId::monospace(12.0),
            ))]),
        }
    }

    /// 同步来自 SettingsState 的配置（每帧调用）
    pub fn sync_with_settings(
        &mut self,
        instance_path: String,
        instance_type: String,
        instance_version: String,
        data_mode: &TavernDataMode,
        proxy_type: &ProxyType,
        custom_proxy: &str,
        github_proxy_url: Option<String>,
        show_startup_command: bool,
        desktop_auto_stop: bool,
        is_desktop_mode: bool,
        allow_tavern_background: bool,
        server_mode_enabled: bool,
        server_service_mode: ServerServiceMode,
        global_data_path: Option<String>,
    ) {
        // 检测实例是否变更，重置优化设置标记
        let instance_changed = self.instance_path != instance_path;
        if instance_changed {
            self.settings_prepared = false;
        }

        self.instance_path = instance_path;
        self.instance_type = instance_type;
        self.instance_version = instance_version;
        self.data_mode = data_mode.clone();
        self.proxy_type = proxy_type.clone();
        self.custom_proxy = custom_proxy.to_string();
        self.github_proxy_url = github_proxy_url;
        self.show_startup_command = show_startup_command;
        self.desktop_auto_stop = desktop_auto_stop;
        self.is_desktop_mode = is_desktop_mode;
        self.is_server_mode = server_mode_enabled;
        self.server_service_mode = server_service_mode.clone();
        self.global_data_path = global_data_path;

        // PM2 接管条件：服务器模式 + 允许酒馆后台运行 + PM2 已安装
        // 仅当服务器模式开启时才能被 PM2 接管，关闭服务器模式后必须切回直接模式
        // PM2 可用性缓存：每 30 秒检测一次，避免每帧执行 pm2 --version
        let now = std::time::Instant::now();
        let check_interval = std::time::Duration::from_secs(30);
        if now.duration_since(self.last_pm2_check) > check_interval {
            self.pm2_available_cache = Pm2Manager::is_installed();
            self.last_pm2_check = now;
        }
        let new_use_pm2 = server_mode_enabled && allow_tavern_background && self.pm2_available_cache;

        // 处理模式切换
        if new_use_pm2 != self.use_pm2 {
            self.use_pm2 = new_use_pm2;
            if new_use_pm2 {
                // 切换到 PM2 模式：如果直接进程在运行，先杀掉
                if self.process.is_running() {
                    self.add_log("[系统] 切换到 PM2 后台模式，正在关闭直接进程...");
                    self.process.kill();
                    self.status = ConsoleStatus::Stopped;
                }
                // 标记需要恢复 PM2 状态（启动器重新打开时也走这里）
                self.pm2_state_restored = false;
                self.add_log("[系统] 已切换到 PM2 后台模式，关闭启动器不影响服务运行");
            } else {
                // 切换回直接模式：PM2 进程保持运行（用户手动切换回直接模式）
                self.add_log("[系统] 已切换回直接进程模式");
                self.status = ConsoleStatus::Stopped;
            }
        }

        // 确保优化后的酒馆设置已生成（在进程启动前完成，避免被酒馆自身的生成逻辑覆盖）
        if self.has_instance() && !self.settings_prepared {
            self.prepare_optimized_settings();
            self.settings_prepared = true;
        }
    }

    /// 是否有已选择的酒馆实例
    pub fn has_instance(&self) -> bool {
        !self.instance_path.is_empty()
    }

    // ---- 进程操作（供 UI 按钮和主页调用）----

    /// 根据 proxy_type 解析实际代理地址
    fn resolve_proxy(&self) -> Option<String> {
        match self.proxy_type {
            ProxyType::None => None,
            ProxyType::Custom => {
                if self.custom_proxy.is_empty() {
                    None
                } else {
                    Some(self.custom_proxy.clone())
                }
            }
            ProxyType::System => {
                // 读取 macOS 系统代理（优先 HTTPS，回退 HTTP）
                crate::core::network::read_system_proxy()
                    .map(|(addr, _enabled)| addr)
            }
        }
    }

    /// 首次启动酒馆时，将优化过的默认设置复制到目标位置。
    ///
    /// - **独立模式**：`<instance_path>/data/default-user/settings.json`
    /// - **全局模式**：全局数据目录下的 `default-user/settings.json`
    ///
    /// 仅当目标文件不存在时复制。复制前会检查 `currentVersion`，
    /// 如果与当前实例版本不一致则自动更新。
    fn prepare_optimized_settings(&mut self) {
        let paths = crate::utils::app_paths();

        // 1. 确保模板文件存在（不存在则从常量生成）
        paths.ensure_default_tavern_settings();

        // 2. 读取模板
        let content = match std::fs::read_to_string(paths.default_tavern_settings_file()) {
            Ok(c) => c,
            Err(e) => {
                self.add_log(&format!("[系统] 读取默认设置模板失败: {}", e));
                return;
            }
        };

        // 3. 解析 JSON
        let mut settings: serde_json::Value = match serde_json::from_str(&content) {
            Ok(v) => v,
            Err(e) => {
                self.add_log(&format!("[系统] 解析默认设置模板失败: {}", e));
                return;
            }
        };

        // 4. 检查并更新 currentVersion
        let version = if self.instance_version.is_empty() {
            "0.0.0"
        } else {
            &self.instance_version
        };

        if settings.get("currentVersion").and_then(|v| v.as_str()) != Some(version) {
            settings["currentVersion"] = serde_json::Value::String(version.to_string());
        }

        // 5. 确定目标路径
        let target = match self.data_mode {
            TavernDataMode::Current => PathBuf::from(&self.instance_path)
                .join("data")
                .join("default-user")
                .join("settings.json"),
            TavernDataMode::Global => paths.global_tavern_settings_file(),
        };

        // 6. 仅在目标不存在时复制（首次启动）
        if !target.exists() {
            if let Some(parent) = target.parent() {
                let _ = std::fs::create_dir_all(parent);
            }
            match serde_json::to_string_pretty(&settings) {
                Ok(updated) => {
                    if let Err(e) = std::fs::write(&target, &updated) {
                        self.add_log(&format!("[系统] 写入默认设置失败: {}", e));
                    }
                }
                Err(e) => {
                    self.add_log(&format!("[系统] 序列化默认设置失败: {}", e));
                }
            }
        }
    }

    /// 启动酒馆
    pub fn start(&mut self, lang: &Language) {
        if !self.has_instance() {
            self.add_log("[错误] 未选择酒馆实例，请先前往版本管理选择");
            return;
        }

        if self.use_pm2 {
            self.start_with_pm2(lang);
            return;
        }

        if self.process.is_running() {
            self.add_log("[警告] 酒馆已在运行中");
            return;
        }

        // 启动前清空日志
        self.logs.clear();
        self.parsed_layouts.clear();
        self.tavern_url = None;
        self.webview_auto_opened = false;
        // 重置连接去重记录：新进程会重新输出所有连接日志，避免重启后被误判为重复而漏掉通知
        self.notified_connections.clear();

        self.status = ConsoleStatus::Starting;
        self.add_log(&lang::t("console_log_starting_instance", lang));

        // GitHub 加速与 HTTP 代理互斥，加速优先
        let github_proxy = if self.github_proxy_url.is_some() {
            if crate::core::tavern_process::node_supports_import() {
                self.github_proxy_url.clone()
            } else {
                self.add_log(&format!(
                    "[警告] 当前 Node.js 版本不支持 GitHub 加速拦截器（需要 >= 19），已自动关闭加速"
                ));
                None
            }
        } else {
            None
        };

        let proxy = if github_proxy.is_some() {
            None
        } else {
            self.resolve_proxy()
        };
        if let Some(ref addr) = proxy {
            let normalized = crate::core::tavern_process::normalize_proxy_url(addr);
            self.add_log(&format!("[系统] 代理已应用: {}", normalized));
        }
        if let Some(ref gh_proxy) = github_proxy {
            self.add_log(&format!("[系统] GitHub 加速已启用: {}", gh_proxy));
        }
        if self.show_startup_command {
            let cmd = crate::core::tavern_process::build_startup_command(
                &self.instance_path,
                &self.data_mode,
                proxy.as_deref(),
                github_proxy.as_deref(),
                self.is_desktop_mode || self.is_server_mode,
            );
            self.add_log(&format!("[启动命令] {}", cmd));
        }
        match self.process.start(
            &self.instance_path,
            &self.data_mode,
            proxy.as_deref(),
            github_proxy.as_deref(),
            self.is_desktop_mode || self.is_server_mode,
        ) {
            Ok(()) => {
                self.status = ConsoleStatus::Running;
                self.add_log(&lang::t("console_log_started", lang));
            }
            Err(e) => {
                self.status = ConsoleStatus::Stopped;
                self.add_log(&format!("[错误] 启动失败: {}", e));
            }
        }
    }

    /// PM2 模式启动
    fn start_with_pm2(&mut self, lang: &Language) {
        // 先清空 PM2 日志文件，再清空内存日志（避免下一帧 poll 拉回旧日志）
        let _ = self.pm2_manager.clear_logs();
        self.logs.clear();
        self.parsed_layouts.clear();
        self.tavern_url = None;
        self.webview_auto_opened = false;
        self.pm2_log_byte_offset = 0;
        // 重置连接去重记录：新进程会重新输出所有连接日志，避免重启后被误判为重复而漏掉通知
        self.notified_connections.clear();

        self.status = ConsoleStatus::Starting;
        self.add_log(&lang::t("pm2_starting", lang));

        // GitHub 加速
        let github_proxy = if self.github_proxy_url.is_some() {
            if crate::core::tavern_process::node_supports_import() {
                self.github_proxy_url.clone()
            } else {
                self.add_log("[警告] 当前 Node.js 版本不支持 GitHub 加速拦截器（需要 >= 19），已自动关闭加速");
                None
            }
        } else {
            None
        };

        let proxy = if github_proxy.is_some() {
            None
        } else {
            self.resolve_proxy()
        };

        // 准备拦截器文件
        let interceptor_path = github_proxy.as_ref().and_then(|_| {
            match crate::core::tavern_process::prepare_interceptor() {
                Ok(p) => {
                    self.add_log(&format!(
                        "[系统] GitHub 加速已启用: {}",
                        self.github_proxy_url.as_deref().unwrap_or("")
                    ));
                    Some(p.to_string_lossy().to_string())
                }
                Err(e) => {
                    self.add_log(&format!("[警告] GitHub 拦截器准备失败: {}", e));
                    None
                }
            }
        });

        if let Some(ref addr) = proxy {
            let normalized = crate::core::tavern_process::normalize_proxy_url(addr);
            self.add_log(&format!("[系统] 代理已应用: {}", normalized));
        }

        // 显示启动命令
        if self.show_startup_command {
            let proxy_display = proxy.as_ref().map(|p| crate::core::tavern_process::normalize_proxy_url(p));

            let mut parts: Vec<String> = vec![format!("pm2 start server.js --name {}", crate::core::settings::pm2::PM2_PROCESS_NAME)];
            if let Some(ref interceptor) = interceptor_path {
                parts.push(format!("--node-args \"--import {}\"", interceptor));
            }
            // 构建脚本参数（复用 build_startup_command 的逻辑）
            if self.is_desktop_mode || self.is_server_mode {
                parts.push("--browserLaunchEnabled false".to_string());
            }
            if self.data_mode == TavernDataMode::Global {
                let paths = crate::utils::app_paths();
                parts.push(format!("--configPath {}", paths.global_tavern_config_file().display()));
                parts.push(format!("--dataRoot {}", paths.default_global_data_dir().display()));
            }
            if let Some(ref pd) = proxy_display {
                parts.push("--requestProxyEnabled true".to_string());
                parts.push(format!("--requestProxyUrl {}", pd));
                parts.push("--requestProxyBypass \"localhost 127.0.0.1 ::1\"".to_string());
            }
            if github_proxy.is_some() {
                parts.push(format!(
                    "(env GITHUB_PROXY_URL={})",
                    github_proxy.as_ref().unwrap()
                ));
            }
            if let Some(ref pd) = proxy_display {
                parts.push(format!("(env HTTP_PROXY={})", pd));
            }
            self.add_log(&format!("[启动命令] {}", parts.join(" ")));
        }

        match self.pm2_manager.start(
            &self.instance_path,
            &self.data_mode,
            proxy.as_deref(),
            github_proxy.as_deref(),
            self.is_desktop_mode || self.is_server_mode,
            interceptor_path.as_deref(),
        ) {
            Ok(()) => {
                self.status = ConsoleStatus::Running;
                self.add_log(&lang::t("pm2_started", lang));
            }
            Err(e) => {
                self.status = ConsoleStatus::Stopped;
                self.add_log(&format!("[错误] PM2 启动失败: {}", e));
            }
        }
    }

    /// 优雅停止酒馆
    pub fn stop(&mut self, lang: &Language) {
        if self.use_pm2 {
            match self.pm2_manager.stop() {
                Ok(()) => {
                    self.status = ConsoleStatus::Stopping;
                    self.add_log(&lang::t("pm2_stopping", lang));
                }
                Err(e) => {
                    self.add_log(&format!("[错误] PM2 停止失败: {}", e));
                }
            }
            return;
        }

        if !self.process.is_running() {
            self.status = ConsoleStatus::Stopped;
            return;
        }

        self.status = ConsoleStatus::Stopping;
        self.add_log(&lang::t("console_log_stopping", lang));
        self.process.stop();
    }

    /// 强制停止酒馆
    pub fn force_kill(&mut self, lang: &Language) {
        if self.use_pm2 {
            match self.pm2_manager.delete() {
                Ok(()) => {
                    self.status = ConsoleStatus::Stopped;
                    self.restart_pending = false;
                    self.add_log(&lang::t("pm2_killed", lang));
                }
                Err(e) => {
                    self.add_log(&format!("[错误] PM2 强制停止失败: {}", e));
                }
            }
            return;
        }

        if !self.process.is_running() {
            self.status = ConsoleStatus::Stopped;
            return;
        }

        self.process.kill();
        self.status = ConsoleStatus::Stopped;
        self.restart_pending = false;
        self.add_log(&lang::t("console_log_killed", lang));
    }

    /// 重启酒馆
    pub fn restart(&mut self, lang: &Language) {
        if self.use_pm2 {
            // 清空 PM2 日志文件 + 内存日志
            let _ = self.pm2_manager.clear_logs();
            self.logs.clear();
            self.parsed_layouts.clear();
            self.pm2_log_byte_offset = 0;
            // 重置连接去重记录：新进程会重新输出所有连接日志，避免重启后被误判为重复而漏掉通知
            self.notified_connections.clear();

            self.add_log(&lang::t("pm2_restarting", lang));

            // GitHub 加速
            let github_proxy = if self.github_proxy_url.is_some() {
                if crate::core::tavern_process::node_supports_import() {
                    self.github_proxy_url.clone()
                } else {
                    self.add_log("[警告] 当前 Node.js 版本不支持 GitHub 加速拦截器（需要 >= 19），已自动关闭加速");
                    None
                }
            } else {
                None
            };

            let proxy = if github_proxy.is_some() { None } else { self.resolve_proxy() };

            if let Some(ref addr) = proxy {
                let normalized = crate::core::tavern_process::normalize_proxy_url(addr);
                self.add_log(&format!("[系统] 代理已应用: {}", normalized));
            }
            if let Some(ref gh_proxy) = github_proxy {
                self.add_log(&format!("[系统] GitHub 加速已启用: {}", gh_proxy));
            }
            if self.show_startup_command {
                self.add_log(&format!(
                    "[启动命令] pm2 restart {}",
                    crate::core::settings::pm2::PM2_PROCESS_NAME
                ));
            }

            match self.pm2_manager.restart() {
                Ok(()) => {
                    self.status = ConsoleStatus::Running;
                    self.add_log(&lang::t("pm2_restarted", lang));
                }
                Err(e) => {
                    self.add_log(&format!("[错误] PM2 重启失败: {}", e));
                }
            }
            return;
        }

        if !self.process.is_running() {
            // 未运行则直接启动
            self.start(lang);
            return;
        }

        self.status = ConsoleStatus::Stopping;
        self.restart_pending = true;
        self.add_log(&lang::t("console_log_restarting", lang));
        self.process.stop();
    }

    // ---- 每帧轮询 ----

    /// 每帧调用：拉取日志、检测进程退出、处理重启逻辑
    pub fn poll(&mut self, lang: &Language) {
        if self.use_pm2 {
            self.poll_pm2(lang);
            return;
        }
        self.poll_direct(lang);
    }

    /// PM2 模式轮询：获取状态和日志
    /// 注意：pm2 CLI 是同步阻塞调用，节流到 ~1 秒一次，避免每帧调用导致 UI 卡顿
    fn poll_pm2(&mut self, lang: &Language) {
        // 节流：最多每秒轮询一次 PM2（状态恢复时首次立即轮询）
        let now = std::time::Instant::now();
        let poll_interval = std::time::Duration::from_millis(1000);
        if !self.pm2_state_restored {
            // 启动器重新打开，需要立即恢复 PM2 托管状态
        } else if now.duration_since(self.last_pm2_poll) < poll_interval {
            return;
        }
        self.last_pm2_poll = now;
        let pm2_status = self.pm2_manager.get_status();

        // 状态恢复：启动器重新打开时，从 PM2 恢复实际运行状态
        if !self.pm2_state_restored {
            self.pm2_state_restored = true;
            match pm2_status {
                crate::core::settings::pm2::Pm2Status::Online => {
                    if self.status != ConsoleStatus::Running {
                        self.status = ConsoleStatus::Running;
                        self.add_log("[系统] 检测到 PM2 托管进程正在运行，已恢复控制");
                        // 拉取当前日志（从文件开头读取全部已有日志）
                        let (existing_logs, new_offset) =
                            self.pm2_manager.read_out_logs_since(0);
                        for line in &existing_logs {
                            let cleaned = strip_osc(line);
                            if self.tavern_url.is_none() {
                                let plain = strip_ansi(&cleaned);
                                if let Some(url) = extract_tavern_url(&plain) {
                                    self.tavern_url = Some(url);
                                }
                            }
                            self.add_log(&cleaned);
                        }
                        self.pm2_log_byte_offset = new_offset;
                    }
                    return;
                }
                crate::core::settings::pm2::Pm2Status::Stopped => {
                    // PM2 中存在记录但已停止 → 状态一致，不需要额外操作
                }
                crate::core::settings::pm2::Pm2Status::Errored => {
                    self.add_log("[系统] PM2 托管进程处于错误状态");
                }
                crate::core::settings::pm2::Pm2Status::Launching => {
                    self.status = ConsoleStatus::Starting;
                    self.add_log("[系统] PM2 托管进程正在启动中...");
                    return;
                }
                crate::core::settings::pm2::Pm2Status::Stopping => {
                    self.status = ConsoleStatus::Stopping;
                    self.add_log("[系统] PM2 托管进程正在停止中...");
                    return;
                }
                crate::core::settings::pm2::Pm2Status::NotStarted
                | crate::core::settings::pm2::Pm2Status::Unknown => {
                    // PM2 中无此进程记录，保持 Stopped 状态
                }
            }
            // 首次恢复完成，后续走正常轮询
        }

        // 同步状态
        match pm2_status {
            crate::core::settings::pm2::Pm2Status::Online => {
                // 如果之前是 Starting，现在变成 Online → 启动成功
                if self.status == ConsoleStatus::Starting {
                    self.status = ConsoleStatus::Running;
                    self.add_log(&lang::t("pm2_started", lang));
                }
            }
            crate::core::settings::pm2::Pm2Status::Stopped => {
                // 如果之前是 Stopping，现在变成 Stopped → 停止成功
                if self.status == ConsoleStatus::Stopping {
                    self.status = ConsoleStatus::Stopped;
                    self.add_log(&lang::t("pm2_stopped", lang));
                } else if self.status == ConsoleStatus::Running {
                    // 异常退出
                    self.status = ConsoleStatus::Stopped;
                    self.add_log("[系统] PM2 酒馆进程已停止");
                }
            }
            crate::core::settings::pm2::Pm2Status::Errored => {
                if self.status != ConsoleStatus::Stopped {
                    self.status = ConsoleStatus::Stopped;
                    self.add_log("[系统] PM2 酒馆进程异常退出（errored）");
                }
            }
            crate::core::settings::pm2::Pm2Status::Launching => {
                if self.status != ConsoleStatus::Starting {
                    self.status = ConsoleStatus::Starting;
                }
            }
            crate::core::settings::pm2::Pm2Status::Stopping => {
                if self.status != ConsoleStatus::Stopping {
                    self.status = ConsoleStatus::Stopping;
                }
            }
            crate::core::settings::pm2::Pm2Status::NotStarted | crate::core::settings::pm2::Pm2Status::Unknown => {
                if self.status == ConsoleStatus::Running || self.status == ConsoleStatus::Starting {
                    self.status = ConsoleStatus::Stopped;
                    self.add_log("[系统] PM2 酒馆进程未找到");
                }
            }
        }

        // 拉取 PM2 日志（仅在运行或启动时）
        if self.status == ConsoleStatus::Running
            || self.status == ConsoleStatus::Starting
        {
            // 直接读取 out.log 文件从上次偏移开始的新内容
            let (new_logs, new_offset) =
                self.pm2_manager.read_out_logs_since(self.pm2_log_byte_offset);
            if !new_logs.is_empty() {
                for line in &new_logs {
                    let cleaned = strip_osc(line);

                    // 解析酒馆访问地址
                    if self.tavern_url.is_none() {
                        let plain = strip_ansi(&cleaned);
                        if let Some(url) = extract_tavern_url(&plain) {
                            self.tavern_url = Some(url);
                        }
                    }

                    self.add_log(&cleaned);
                }
                self.pm2_log_byte_offset = new_offset;
            }
        }
    }

    /// 直接进程模式轮询（原逻辑）
    fn poll_direct(&mut self, lang: &Language) {
        // 拉取新日志
        let new_logs = self.process.poll_logs();
        for line in new_logs {
            let cleaned = strip_osc(&line);

            // 解析酒馆访问地址: "Go to: http://localhost:11451/ to open SillyTavern"
            if self.tavern_url.is_none() {
                // 先剥离 ANSI 再匹配
                let plain = strip_ansi(&cleaned);
                if let Some(url) = extract_tavern_url(&plain) {
                    self.tavern_url = Some(url);
                }
            }

            self.add_log(&cleaned);
        }

        // 检查进程是否已退出
        if let Some(exit_code) = self.process.check_exited() {
            self.add_log(&format!(
                "[系统] 酒馆进程已退出, 退出码: {}",
                exit_code.map_or("无".to_string(), |c| c.to_string())
            ));

            // 检测端口冲突，自动解除并重启
            let port_conflict = exit_code == Some(1)
                && self.logs.iter().any(|l| l.contains("already in use"));
            if port_conflict && !self.restart_pending {
                // 从日志中提取端口号
                let port = self.logs.iter().find_map(|l| {
                    if l.contains("already in use") {
                        // 匹配 :<port> 模式（如 :11451）
                        if let Some(pos) = l.rfind(':') {
                            let after = &l[pos + 1..];
                            let num: String = after.chars().take_while(|c| c.is_ascii_digit()).collect();
                            if !num.is_empty() {
                                return num.parse::<u16>().ok();
                            }
                        }
                    }
                    None
                });

                if let Some(p) = port {
                    self.add_log(&format!(
                        "[系统] 检测到端口 {} 被占用，正在强制解除...",
                        p
                    ));
                    kill_port(p);
                    self.add_log(&format!("[系统] 端口 {} 已释放，自动重启酒馆", p));
                    self.restart_pending = true;
                }
            }

            if self.restart_pending {
                // 重启流程：停止已完成 → 清空日志并自动启动
                self.restart_pending = false;
                self.logs.clear();
                self.parsed_layouts.clear();
                // 重置连接去重记录：新进程会重新输出所有连接日志，避免重启后被误判为重复而漏掉通知
                self.notified_connections.clear();
                self.add_log(&lang::t("console_log_restarting_start", lang));

                let github_proxy = if self.github_proxy_url.is_some() {
                    if crate::core::tavern_process::node_supports_import() {
                        self.github_proxy_url.clone()
                    } else {
                        self.add_log(&format!(
                            "[警告] 当前 Node.js 版本不支持 GitHub 加速拦截器（需要 >= 19），已自动关闭加速"
                        ));
                        None
                    }
                } else {
                    None
                };

                let proxy = if github_proxy.is_some() {
                    None
                } else {
                    self.resolve_proxy()
                };
                if let Some(ref addr) = proxy {
                    let normalized = crate::core::tavern_process::normalize_proxy_url(addr);
                    self.add_log(&format!("[系统] 代理已应用: {}", normalized));
                }
                if let Some(ref gh_proxy) = github_proxy {
                    self.add_log(&format!("[系统] GitHub 加速已启用: {}", gh_proxy));
                }
                if self.show_startup_command {
                    let cmd = crate::core::tavern_process::build_startup_command(
                        &self.instance_path,
                        &self.data_mode,
                        proxy.as_deref(),
                        github_proxy.as_deref(),
                        self.is_desktop_mode || self.is_server_mode,
                    );
                    self.add_log(&format!("[启动命令] {}", cmd));
                }
                match self.process.start(
                    &self.instance_path,
                    &self.data_mode,
                    proxy.as_deref(),
                    github_proxy.as_deref(),
                    self.is_desktop_mode || self.is_server_mode,
                ) {
                    Ok(()) => {
                        self.status = ConsoleStatus::Running;
                        self.add_log(&lang::t("console_log_restarted", lang));
                    }
                    Err(e) => {
                        self.status = ConsoleStatus::Stopped;
                        self.add_log(&format!("[错误] 重启时启动失败: {}", e));
                    }
                }
            } else {
                // 正常停止完成
                self.status = ConsoleStatus::Stopped;
            }
        }

        // 如果状态已为 Stopped 但进程仍在（异常情况），同步状态
        if self.status == ConsoleStatus::Stopped && self.process.is_running() {
            // 不应该出现，但做保护
            self.status = ConsoleStatus::Running;
        }
    }

    // ---- 日志 ----

    pub fn add_log(&mut self, msg: &str) {
        let timestamp = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| {
                let secs = d.as_secs();
                let h = (secs / 3600) % 24 + 8; // UTC+8
                let m = (secs / 60) % 60;
                let s = secs % 60;
                format!("{:02}:{:02}:{:02}", h, m, s)
            })
            .unwrap_or_else(|_| String::from("--:--:--"));
        let full = format!("[{}] {}", timestamp, msg);
        // 推入原始文本
        self.logs.push_back(full.clone());

        // 缓存解析结果（仅在不包含 URL 时缓存 LayoutJob）
        if full.contains("http://") || full.contains("https://") {
            self.parsed_layouts.push_back(None);
        } else {
            let job = parse_ansi_line(&full, egui::FontId::monospace(12.0));
            self.parsed_layouts.push_back(Some(job));
        }

        // 修剪过多的历史，保留最近 N 行
        while self.logs.len() > MAX_LOG_LINES {
            self.logs.pop_front();
            self.parsed_layouts.pop_front();
        }

        // 检测酒馆连接日志 → 推送通知（仅服务器模式 + 互联网模式启用）
        if let Some(info) = crate::core::network::parse_connection_log(msg) {
            // 门禁：仅在"服务器模式开启 + 服务模式为互联网"时启用连接通知
            // 局域网模式或非服务器模式下不弹通知（避免本机和内网设备频繁打扰）
            if !self.is_server_mode
                || !matches!(self.server_service_mode, ServerServiceMode::Internet)
            {
                return;
            }
            // 本机访问（127.0.0.1 / ::1 / 本机网卡 IP）不弹通知
            if crate::core::network::is_local_ip(&info.ip) {
                return;
            }
            let dedup_key = format!("{}|{}", info.ip, info.user_agent);
            if !self.notified_connections.contains(&dedup_key) {
                self.notified_connections.insert(dedup_key);
                let time_str = std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .map(|d| {
                        let secs = d.as_secs();
                        let h = (secs / 3600) % 24 + 8;
                        let m = (secs / 60) % 60;
                        let s = secs % 60;
                        format!("{:02}:{:02}:{:02}", h, m, s)
                    })
                    .unwrap_or_else(|_| "--:--:--".to_string());

                // 通知正文：第 1 行 IP；第 2 行设备/系统；第 3 行时间
                // - 有设备型号时显示"设备 · 系统"，无则仅显示系统
                // - 设备型号可能为 None（PC 浏览器无法识别具体硬件）
                let second_line = match &info.device {
                    Some(dev) if !dev.is_empty() => format!("{}  ·  {}", dev, info.os),
                    _ => info.os.clone(),
                };
                self.pending_connection_notifications.push(format!(
                    "{}\n{}\n{}",
                    info.ip, second_line, time_str
                ));
            }
        }
    }
}

/// 剥离 OSC 终端序列（窗口标题设置等），例如 `\x1b]2;...\x07` 或 `\x1b]2;...\x1b\\`
fn strip_osc(line: &str) -> String {
    if !line.contains('\x1b') {
        return line.to_string();
    }
    let mut result = String::with_capacity(line.len());
    let mut chars = line.chars().peekable();
    while let Some(ch) = chars.next() {
        if ch == '\x1b' && chars.peek() == Some(&']') {
            chars.next(); // skip ']'
            // 消耗直到 BEL (\x07) 或 ST (\x1b\\)
            while let Some(&c) = chars.peek() {
                if c == '\x07' {
                    chars.next();
                    break;
                }
                if c == '\x1b' {
                    chars.next();
                    if chars.peek() == Some(&'\\') {
                        chars.next();
                        break;
                    }
                    break; // 意外情况，跳出
                }
                chars.next();
            }
        } else {
            result.push(ch);
        }
    }
    result
}

/// 解析日志行中的 ANSI 颜色转义码，生成 egui LayoutJob
/// - 包含 ANSI 码的行：按码着色
/// - 不包含 ANSI 码的行：根据前缀 [错误]/[警告] 着色
fn parse_ansi_line(line: &str, font_id: egui::FontId) -> LayoutJob {
    // SGR 颜色码 → Color32
    fn sgr_to_color(code: u8) -> Option<Color32> {
        match code {
            30 => Some(Color32::BLACK),
            31 => Some(Color32::RED),
            32 => Some(Color32::GREEN),
            33 => Some(Color32::from_rgb(220, 190, 50)), // 终端黄色
            34 => Some(Color32::from_rgb(80, 120, 255)), // 终端蓝
            35 => Some(Color32::from_rgb(200, 80, 255)), // 终端品红
            36 => Some(Color32::from_rgb(60, 200, 200)), // 终端青
            37 => Some(Color32::from_rgb(210, 210, 210)), // 终端白
            90 => Some(Color32::from_rgb(128, 128, 128)), // 亮黑(灰)
            91 => Some(Color32::from_rgb(255, 110, 110)), // 亮红
            92 => Some(Color32::from_rgb(100, 255, 100)), // 亮绿
            93 => Some(Color32::from_rgb(255, 255, 120)), // 亮黄
            94 => Some(Color32::from_rgb(140, 160, 255)), // 亮蓝
            95 => Some(Color32::from_rgb(255, 130, 255)), // 亮品红
            96 => Some(Color32::from_rgb(100, 255, 255)), // 亮青
            97 => Some(Color32::from_rgb(255, 255, 255)), // 亮白
            _ => None,
        }
    }

    fn default_color() -> Color32 {
        Color32::from_rgb(180, 200, 220)
    }

    // 没有 ANSI 码 → 前缀着色
    if !line.contains('\x1b') {
        let color = if line.contains("[错误]") || line.contains("[ERROR]") {
            Color32::from_rgb(255, 100, 100)
        } else if line.contains("[警告]") || line.contains("[WARN]") {
            Color32::from_rgb(255, 200, 80)
        } else {
            default_color()
        };
        let mut job = LayoutJob::default();
        job.append(
            line,
            0.0,
            TextFormat {
                font_id,
                color,
                ..Default::default()
            },
        );
        return job;
    }

    // 有 ANSI 码 → 逐段解析着色
    let mut job = LayoutJob::default();
    let mut current_color = default_color();
    let mut buf = String::new();
    let mut chars = line.chars().peekable();

    while let Some(ch) = chars.next() {
        if ch == '\x1b' && chars.peek() == Some(&'[') {
            // 清空前一个文字段
            if !buf.is_empty() {
                job.append(
                    &buf,
                    0.0,
                    TextFormat {
                        font_id: font_id.clone(),
                        color: current_color,
                        ..Default::default()
                    },
                );
                buf.clear();
            }
            chars.next(); // 跳过 '['

            // 读取参数直到 'm'
            let mut params = String::new();
            while let Some(&p) = chars.peek() {
                if p == 'm' {
                    chars.next();
                    break;
                }
                if p.is_ascii_digit() || p == ';' {
                    params.push(p);
                    chars.next();
                } else {
                    // 非 SGR 序列（如光标移动等），跳过剩余
                    while let Some(&q) = chars.peek() {
                        chars.next();
                        if q.is_ascii_alphabetic() {
                            break;
                        }
                    }
                    params.clear();
                    break;
                }
            }

            if params.is_empty() {
                continue;
            }

            // 应用 SGR 参数
            for code_str in params.split(';') {
                if let Ok(n) = code_str.parse::<u8>() {
                    match n {
                        0 => current_color = default_color(),
                        1 => {} // bold — egui 不支持 layoutjob 内 bold，忽略
                        c => {
                            if let Some(color) = sgr_to_color(c) {
                                current_color = color;
                            }
                        }
                    }
                }
            }
        } else {
            buf.push(ch);
        }
    }

    // 清空末尾文字段
    if !buf.is_empty() {
        job.append(
            &buf,
            0.0,
            TextFormat {
                font_id,
                color: current_color,
                ..Default::default()
            },
        );
    }

    job
}

/// 渲染单行日志：自动识别 URL 并渲染为可点击超链接，无 URL 时使用 ANSI 着色
fn render_log_line(ui: &mut egui::Ui, line: &str, monospace: &egui::FontId) {
    // 检查是否包含 URL
    let has_http = line.contains("http://") || line.contains("https://");

    if !has_http {
        // 无 URL — 纯 ANSI 着色
        ui.label(parse_ansi_line(line, monospace.clone()));
        return;
    }

    // 含 URL — 剥离 ANSI 码后按 URL 拆段渲染
    let plain = strip_ansi(line);
    let url_color = Color32::from_rgb(80, 180, 255);
    let text_color = Color32::from_rgb(180, 200, 220);

    // 前缀着色（系统日志）
    let text_color = if plain.contains("[错误]") || plain.contains("[ERROR]") {
        Color32::from_rgb(255, 100, 100)
    } else if plain.contains("[警告]") || plain.contains("[WARN]") {
        Color32::from_rgb(255, 200, 80)
    } else {
        text_color
    };

    let fmt = |s: &str, c: Color32| {
        RichText::new(s.to_string())
            .font(monospace.clone())
            .color(c)
    };

    ui.horizontal_wrapped(|ui| {
        ui.spacing_mut().item_spacing = egui::Vec2::ZERO;
        let mut remaining = plain.as_str();
        while !remaining.is_empty() {
            if let Some(pos) = remaining.find("http://")
                .or_else(|| remaining.find("https://"))
            {
                // 渲染 URL 前的文本
                if pos > 0 {
                    ui.label(fmt(&remaining[..pos], text_color));
                }
                // 提取 URL（直到空白或行尾）
                let url_start = pos;
                let url_end = remaining[url_start..]
                    .find(|c: char| c.is_whitespace())
                    .map(|p| url_start + p)
                    .unwrap_or(remaining.len());
                let url = &remaining[url_start..url_end];
                ui.add(
                    egui::Hyperlink::from_label_and_url(fmt(url, url_color), url),
                );
                remaining = &remaining[url_end..];
            } else {
                ui.label(fmt(remaining, text_color));
                break;
            }
        }
    });
}

/// 剥离 ANSI 转义码，返回纯文本
fn strip_ansi(line: &str) -> String {
    if !line.contains('\x1b') {
        return line.to_string();
    }
    let mut result = String::with_capacity(line.len());
    let mut chars = line.chars().peekable();
    while let Some(ch) = chars.next() {
        if ch == '\x1b' && chars.peek() == Some(&'[') {
            chars.next(); // skip '['
            // 消耗至终止字符
            while let Some(&c) = chars.peek() {
                chars.next();
                if c.is_ascii_alphabetic() || c == '~' {
                    break;
                }
            }
        } else if ch == '\x1b' && chars.peek() == Some(&']') {
            chars.next();
            while let Some(&c) = chars.peek() {
                if c == '\x07' {
                    chars.next();
                    break;
                }
                if c == '\x1b' {
                    chars.next();
                    if chars.peek() == Some(&'\\') {
                        chars.next();
                        break;
                    }
                    break;
                }
                chars.next();
            }
        } else {
            result.push(ch);
        }
    }
    result
}

pub fn render(ui: &mut egui::Ui, state: &mut ConsoleState, lang: &Language) {
    let available = ui.available_size();

    // ---- 状态栏区域（固定高度）----
    let status_bar_height = 72.0;
    let log_area_height = (available.y - status_bar_height - 8.0).max(100.0);

    // 根据状态选择颜色和图标
    let (status_color, status_icon) = match state.status {
        ConsoleStatus::Stopped => (
            Color32::from_rgb(150, 150, 150),
            egui_phosphor::regular::STOP_CIRCLE,
        ),
        ConsoleStatus::Starting => (
            Color32::from_rgb(255, 200, 50),
            egui_phosphor::regular::ARROW_CLOCKWISE,
        ),
        ConsoleStatus::Running => (
            Color32::from_rgb(80, 220, 80),
            egui_phosphor::regular::PLAY_CIRCLE,
        ),
        ConsoleStatus::Stopping => (
            Color32::from_rgb(255, 150, 50),
            egui_phosphor::regular::STOP_CIRCLE,
        ),
    };

    let (status_title, status_subtitle) = match state.status {
        ConsoleStatus::Stopped => (
            lang::t("console_status_stopped", lang),
            lang::t("console_subtitle_stopped", lang),
        ),
        ConsoleStatus::Starting => (
            lang::t("console_status_starting", lang),
            lang::t("console_subtitle_starting", lang),
        ),
        ConsoleStatus::Running => (
            lang::t("console_status_running", lang),
            lang::t("console_subtitle_running", lang),
        ),
        ConsoleStatus::Stopping => (
            lang::t("console_status_stopping", lang),
            lang::t("console_subtitle_stopping", lang),
        ),
    };

    // 绘制状态栏
    egui::Frame::NONE
        .fill(ui.style().visuals.extreme_bg_color)
        .corner_radius(egui::CornerRadius::same(6))
        .inner_margin(egui::Margin::symmetric(12, 8))
        .show(ui, |ui| {
            ui.horizontal(|ui| {
                // 左侧：状态图标 + 文本
                ui.horizontal(|ui| {
                    ui.add(
                        egui::Label::new(
                            RichText::new(status_icon).size(28.0).color(status_color),
                        )
                        .selectable(false),
                    );
                    ui.add_space(10.0);
                    ui.vertical(|ui| {
                        ui.horizontal(|ui| {
                            ui.add(
                                egui::Label::new(RichText::new(status_title).size(18.0).strong())
                                    .selectable(false),
                            );
                            // PM2 模式标记
                            if state.use_pm2 {
                                ui.add_space(8.0);
                                let pm2_badge = egui::Frame::NONE
                                    .fill(Color32::from_rgb(40, 100, 180))
                                    .corner_radius(egui::CornerRadius::same(4))
                                    .inner_margin(egui::Margin::symmetric(6, 2));
                                pm2_badge.show(ui, |ui| {
                                    ui.add(
                                        egui::Label::new(
                                            RichText::new(format!(
                                                "{} {}",
                                                egui_phosphor::regular::CLOUD,
                                                lang::t("pm2_mode_active", lang)
                                            ))
                                            .size(11.0)
                                            .color(Color32::WHITE),
                                        )
                                        .selectable(false),
                                    );
                                });
                            }
                            // 访问/打开酒馆链接（运行中 + URL 已捕获时显示）
                            // - 正常模式/服务器模式 → 显示"访问酒馆"（浏览器打开）
                            // - 桌面模式 + 不自动停止 → 显示"打开酒馆"（重新唤出 WebView）
                            // - 桌面模式 + 自动停止 → 不显示（关闭 WebView 即停服务）

                            // 桌面模式 + 自动停止 → 隐藏链接
                            let show_link = !state.is_desktop_mode || !state.desktop_auto_stop;

                            if state.status == ConsoleStatus::Running && show_link {
                                if let Some(ref url) = state.tavern_url {
                                    // 分隔符
                                    ui.add(
                                        egui::Label::new(
                                            RichText::new("|").size(18.0).color(Color32::from_rgb(100, 100, 100)),
                                        )
                                        .selectable(false),
                                    );
                                    ui.add_space(6.0);

                                    // 桌面模式：重新打开 WebView；服务器模式：打开访问弹窗；正常模式：浏览器打开
                                    #[derive(Clone, Copy)]
                                    enum VisitAction {
                                        ReopenWebview,
                                        OpenPopup,
                                        OpenBrowser,
                                    }
                                    let (btn_key, action, icon) = if state.is_desktop_mode {
                                        ("console_btn_open", VisitAction::ReopenWebview, egui_phosphor::regular::ARROW_SQUARE_OUT)
                                    } else if state.is_server_mode {
                                        ("console_btn_visit", VisitAction::OpenPopup, egui_phosphor::regular::GLOBE)
                                    } else {
                                        ("console_btn_visit", VisitAction::OpenBrowser, egui_phosphor::regular::GLOBE)
                                    };

                                    let link_color = Color32::from_rgb(80, 180, 255);
                                    let link = RichText::new(
                                        format!("{} {}", icon, lang::t(btn_key, lang)),
                                    )
                                    .size(15.0)
                                    .color(link_color);
                                    let resp = ui.add(
                                        egui::Label::new(link)
                                            .sense(egui::Sense::click()),
                                    );
                                    if resp.hovered() {
                                        ui.ctx().set_cursor_icon(egui::CursorIcon::PointingHand);
                                    }
                                    if resp.clicked() {
                                        match action {
                                            VisitAction::OpenBrowser => {
                                                let _ = std::process::Command::new("open")
                                                    .arg(url)
                                                    .spawn();
                                            }
                                            VisitAction::ReopenWebview => {
                                                state.reopen_webview_triggered = true;
                                            }
                                            VisitAction::OpenPopup => {
                                                let port = crate::pages::access_tavern_popup::parse_port(url);
                                                crate::pages::access_tavern_popup::open_popup(
                                                    url.clone(),
                                                    state.server_service_mode.clone(),
                                                    port,
                                                    ui.ctx(),
                                                );
                                            }
                                        }
                                    }
                                }
                            }
                        });
                        // 显示实例信息
                        if state.has_instance() {
                            if state.instance_type == "builtin" {
                                let subtitle = format!(
                                    "{}  |  {} - v{}",
                                    status_subtitle,
                                    lang::t("console_online_instance", lang),
                                    state.instance_version,
                                );
                                ui.add(
                                    egui::Label::new(
                                        RichText::new(subtitle).size(12.0).color(Color32::GRAY),
                                    )
                                    .selectable(false),
                                );
                            } else {
                                // 本地实例：路径按宽度智能截断，框选/点击复制完整路径
                                render_instance_path(
                                    ui,
                                    status_subtitle,
                                    &state.instance_path,
                                );
                            }
                        } else {
                            ui.add(
                                egui::Label::new(
                                    RichText::new(status_subtitle.to_string())
                                        .size(12.0)
                                        .color(Color32::GRAY),
                                )
                                .selectable(false),
                            );
                        }
                    });
                });

                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    // 右侧：按钮组
                    let is_stopped = state.status == ConsoleStatus::Stopped;
                    let is_running = state.status == ConsoleStatus::Running;
                    let is_transitioning = state.status == ConsoleStatus::Starting
                        || state.status == ConsoleStatus::Stopping;

                    let btn_height = 30.0;

                    // 强行停止
                    let kill_enabled = is_running || is_transitioning;
                    let kill_btn = egui::Button::new(
                        RichText::new(lang::t("console_btn_kill", lang)).size(13.0),
                    )
                    .min_size(Vec2::new(90.0, btn_height))
                    .fill(if kill_enabled {
                        Color32::from_rgb(200, 50, 50)
                    } else {
                        Color32::from_rgb(80, 30, 30)
                    });
                    if ui.add_enabled(kill_enabled, kill_btn).clicked() {
                        state.force_kill(lang);
                    }

                    ui.add_space(6.0);

                    // 停止
                    let stop_enabled = is_running;
                    let stop_btn = egui::Button::new(
                        RichText::new(lang::t("console_btn_stop", lang)).size(13.0),
                    )
                    .min_size(Vec2::new(70.0, btn_height))
                    .fill(if stop_enabled {
                        Color32::from_rgb(200, 120, 30)
                    } else {
                        Color32::from_rgb(60, 40, 20)
                    });
                    if ui.add_enabled(stop_enabled, stop_btn).clicked() {
                        state.stop(lang);
                    }

                    ui.add_space(6.0);

                    // 重启
                    let restart_enabled = is_running;
                    let restart_btn = egui::Button::new(
                        RichText::new(lang::t("console_btn_restart", lang)).size(13.0),
                    )
                    .min_size(Vec2::new(70.0, btn_height))
                    .fill(if restart_enabled {
                        Color32::from_rgb(60, 120, 200)
                    } else {
                        Color32::from_rgb(30, 50, 80)
                    });
                    if ui.add_enabled(restart_enabled, restart_btn).clicked() {
                        state.restart(lang);
                    }

                    ui.add_space(6.0);

                    // 启动
                    let start_enabled = is_stopped && state.has_instance();
                    let start_btn = egui::Button::new(
                        RichText::new(lang::t("console_btn_start", lang)).size(13.0),
                    )
                    .min_size(Vec2::new(70.0, btn_height))
                    .fill(if start_enabled {
                        Color32::from_rgb(50, 180, 80)
                    } else {
                        Color32::from_rgb(30, 60, 35)
                    });
                    if ui.add_enabled(start_enabled, start_btn).clicked() {
                        state.start(lang);
                    }
                });
            });
        });

    ui.add_space(8.0);

    // ---- 日志输出区域 ----
    egui::Frame::NONE
        .fill(ui.style().visuals.extreme_bg_color)
        .corner_radius(egui::CornerRadius::same(6))
        .inner_margin(egui::Margin::symmetric(12, 8))
        .show(ui, |ui| {
            ui.horizontal(|ui| {
                ui.add(
                    egui::Label::new(
                        RichText::new(lang::t("console_log_area", lang))
                            .size(13.0)
                            .strong(),
                    )
                    .selectable(false),
                );
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if ui
                        .add_sized(
                            [60.0, 22.0],
                            egui::Button::new(
                                RichText::new(lang::t("console_btn_clear", lang)).size(11.0),
                            ),
                        )
                        .clicked()
                    {
                        state.logs.clear();
                        state.pm2_log_byte_offset = 0;
                        if state.use_pm2 {
                            if let Err(e) = state.pm2_manager.clear_logs() {
                                state.add_log(&format!("[错误] PM2 清空日志失败: {}", e));
                            }
                        }
                        state.add_log(lang::t("console_log_cleared", lang));
                    }
                });
            });
            ui.separator();

            let log_text_height = log_area_height - 36.0;
            egui::ScrollArea::vertical()
                .max_height(log_text_height)
                .auto_shrink([false; 2])
                .stick_to_bottom(true)
                .show(ui, |ui| {
                    let monospace = egui::FontId::monospace(12.0);
                    for (line, cached) in state.logs.iter().zip(state.parsed_layouts.iter()) {
                        if let Some(job) = cached {
                            ui.label(job.clone());
                        } else {
                            render_log_line(ui, line, &monospace);
                        }
                    }
                });
        });
}

/// 渲染本地实例路径：宽度不够时按文件夹级截断，悬停显示完整路径，点击复制
fn render_instance_path(ui: &mut egui::Ui, prefix: &str, full_path: &str) {
    let full_text = format!("{}  |  {}", prefix, full_path);
    let font_id = egui::FontId::monospace(12.0);

    let text_width = ui
        .painter()
        .layout(full_text.clone(), font_id.clone(), Color32::WHITE, f32::MAX)
        .size()
        .x;
    let available = ui.available_width();

    if text_width <= available {
        // 放得下 → 直接显示完整文本，可选
        ui.add(
            egui::Label::new(
                RichText::new(full_text).font(font_id).color(Color32::GRAY),
            )
            .selectable(true),
        );
    } else {
        // 放不下 → 文件夹级截断: xxx/.../xxx
        let display = folder_truncate(full_path);
        let display_text = format!("{}  |  {}", prefix, display);

        let resp = ui
            .add(
                egui::Label::new(
                    RichText::new(display_text).font(font_id).color(Color32::GRAY),
                )
                .sense(egui::Sense::click()),
            )
            .on_hover_text(full_path);
        if resp.clicked() {
            ui.ctx().copy_text(full_path.to_string());
        }
    }
}

/// 强制释放指定端口（找到占用进程并 kill -9）
fn kill_port(port: u16) {
    let output = std::process::Command::new("lsof")
        .args(["-ti", &format!(":{}", port)])
        .output()
        .ok();
    if let Some(out) = output {
        let pids = String::from_utf8_lossy(&out.stdout);
        for pid in pids.lines().filter(|l| !l.is_empty()) {
            let _ = std::process::Command::new("kill")
                .arg("-9")
                .arg(pid)
                .status();
        }
    }
}

/// 文件夹级路径截断：保留首个和末个文件夹，中间用 /.../ 替换
fn folder_truncate(path: &str) -> String {
    let sep = if path.contains('/') { '/' } else { std::path::MAIN_SEPARATOR };
    let parts: Vec<&str> = path.split(sep).filter(|p| !p.is_empty()).collect();
    if parts.len() <= 2 {
        return path.to_string();
    }
    format!("{}{}...{}{}", parts[0], sep, sep, parts[parts.len() - 1])
}

/// 从日志行提取酒馆访问地址: "Go to: http://localhost:11451/ to open SillyTavern"
fn extract_tavern_url(line: &str) -> Option<String> {
    let prefix = "Go to: ";
    let suffix = " to open SillyTavern";
    if let Some(start) = line.find(prefix) {
        let after = &line[start + prefix.len()..];
        if let Some(end) = after.find(suffix) {
            return Some(after[..end].trim().to_string());
        }
    }
    None
}
