//! SillyTavern 进程运行时。
//!
//! 运行时线程独占直接子进程和 PM2 CLI 调用，通过命令/事件通道与 iced 主线程通信。

use crate::lang::{t, tf};
use std::fs::{self, OpenOptions};
use std::io::{BufRead, BufReader, Write};
use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use std::sync::LazyLock;
use std::sync::mpsc::{self, Receiver, Sender, TryRecvError};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use regex::Regex;
use serde_json::Value;

use crate::core::pm2::Pm2Manager;
use crate::core::settings::EnvSource;

/// 进程日志的级别标记：发射端与解析端之间的内部协议，不是可翻译文案。
///
/// 统一用 ASCII 形式，避免与本地化语言耦合；控制台解析端据此分类与剥离前缀。
pub(crate) const LOG_MARK_SYSTEM: &str = "[system] ";
pub(crate) const LOG_MARK_WARNING: &str = "[warning] ";
pub(crate) const LOG_MARK_ERROR: &str = "[error] ";
pub(crate) const LOG_MARK_COMMAND: &str = "[command] ";


/// 酒馆数据目录模式。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TavernDataMode {
    Current,
    Global,
}

/// 酒馆界面启动模式。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TavernLaunchMode {
    Normal,
    Desktop,
    Server,
}

/// 当前进程由启动器直接持有，或交给 PM2 托管。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RuntimeMode {
    Direct,
    Pm2,
}

/// 冻结一次启动所需的全部配置，运行期间设置变化不会污染当前进程。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TavernLaunchSpec {
    pub instance_path: PathBuf,
    pub instance_version: String,
    pub data_mode: TavernDataMode,
    pub global_data_path: PathBuf,
    pub proxy: Option<String>,
    pub github_proxy_url: Option<String>,
    pub launch_mode: TavernLaunchMode,
    pub allow_background: bool,
    pub show_startup_command: bool,
    pub export_path: String,
    /// 本次运行使用的环境来源（内置 `lib/` 或系统 PATH）。
    pub env_source: EnvSource,
}

impl TavernLaunchSpec {
    pub fn runtime_mode(&self) -> RuntimeMode {
        if self.launch_mode == TavernLaunchMode::Server
            && self.allow_background
            && Pm2Manager::new(self.env_source).is_installed()
        {
            RuntimeMode::Pm2
        } else {
            RuntimeMode::Direct
        }
    }

    fn config_path(&self) -> PathBuf {
        match self.data_mode {
            TavernDataMode::Current => self.instance_path.join("config.yaml"),
            TavernDataMode::Global => self.global_data_path.join("config.yaml"),
        }
    }

    fn data_root(&self) -> Option<PathBuf> {
        (self.data_mode == TavernDataMode::Global).then(|| self.global_data_path.clone())
    }
}

/// 端口占用进程，仅允许用户确认后结束这里列出的 PID。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PortProcess {
    pub pid: u32,
    pub name: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PortConflict {
    pub port: u16,
    pub processes: Vec<PortProcess>,
    pub retry_available: bool,
}

/// 主线程发送给进程运行时的命令。
#[derive(Debug, Clone)]
pub enum ProcessCommand {
    Start(TavernLaunchSpec),
    Stop,
    Kill,
    Restart,
    ReleasePortAndRetry(PortConflict),
    Shutdown,
}

/// 进程运行时回传给控制台的事件。
#[derive(Debug, Clone)]
pub enum ProcessEvent {
    Starting(RuntimeMode),
    Running(RuntimeMode),
    Stopping,
    Stopped,
    Failed(String),
    Pid(Option<u32>),
    Log(String),
    HistoricalLog(String),
    ServerUrl(String),
    PortConflict(PortConflict),
    Exited(Option<i32>),
    Pm2Unavailable,
    Pm2Restored {
        pid: Option<u32>,
        cwd: Option<String>,
        replaying_logs: bool,
    },
}

/// 主线程持有的运行时句柄。
pub struct TavernRuntime {
    command_tx: Sender<ProcessCommand>,
    event_rx: Receiver<ProcessEvent>,
    worker: Option<JoinHandle<()>>,
}

impl std::fmt::Debug for TavernRuntime {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("TavernRuntime")
            .finish_non_exhaustive()
    }
}

impl Default for TavernRuntime {
    fn default() -> Self {
        let (command_tx, command_rx) = mpsc::channel();
        let (event_tx, event_rx) = mpsc::channel();
        let worker = thread::spawn(move || worker_loop(command_rx, event_tx));
        Self {
            command_tx,
            event_rx,
            worker: Some(worker),
        }
    }
}

impl TavernRuntime {
    pub fn send(&self, command: ProcessCommand) -> Result<(), String> {
        self.command_tx
            .send(command)
            .map_err(|_| t("tavern.process.already_stopped").to_owned())
    }

    pub fn drain(&self) -> Vec<ProcessEvent> {
        let mut events = Vec::new();
        loop {
            match self.event_rx.try_recv() {
                Ok(event) => events.push(event),
                Err(TryRecvError::Empty | TryRecvError::Disconnected) => break,
            }
        }
        events
    }
}

impl Drop for TavernRuntime {
    fn drop(&mut self) {
        let _ = self.command_tx.send(ProcessCommand::Shutdown);
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}

struct DirectProcess {
    child: Child,
    logs: Receiver<String>,
    stop_deadline: Option<Instant>,
}

struct WorkerState {
    direct: Option<DirectProcess>,
    active_spec: Option<TavernLaunchSpec>,
    active_mode: Option<RuntimeMode>,
    restart_spec: Option<TavernLaunchSpec>,
    conflict_port: Option<u16>,
    conflict_retried: bool,
    pm2: Option<Pm2Manager>,
    pm2_out_offset: u64,
    pm2_error_offset: u64,
    last_pm2_poll: Instant,
    restoring_pm2_logs: bool,
}

impl WorkerState {
    /// 取出当前 PM2 管理器。
    ///
    /// 只有在 `active_mode` 已进入 `Pm2` 或 `start` 刚完成初始化时才存在；
    /// 调用点都处于这两种状态之一，因此缺失视为「PM2 不可用」。
    fn pm2(&self) -> Result<&Pm2Manager, String> {
        self.pm2
            .as_ref()
            .ok_or_else(|| t("pm2.not_available").to_owned())
    }
}

fn worker_loop(commands: Receiver<ProcessCommand>, events: Sender<ProcessEvent>) {
    let mut state = WorkerState {
        direct: None,
        active_spec: None,
        active_mode: None,
        restart_spec: None,
        conflict_port: None,
        conflict_retried: false,
        pm2: None,
        pm2_out_offset: 0,
        pm2_error_offset: 0,
        last_pm2_poll: Instant::now() - Duration::from_secs(2),
        restoring_pm2_logs: false,
    };
    restore_pm2(&mut state, &events);

    loop {
        match commands.recv_timeout(Duration::from_millis(50)) {
            Ok(ProcessCommand::Start(spec)) => start(&mut state, spec, &events),
            Ok(ProcessCommand::Stop) => stop(&mut state, &events),
            Ok(ProcessCommand::Kill) => kill(&mut state, &events),
            Ok(ProcessCommand::Restart) => restart(&mut state, &events),
            Ok(ProcessCommand::ReleasePortAndRetry(conflict)) => {
                release_port_and_retry(&mut state, conflict, &events)
            }
            Ok(ProcessCommand::Shutdown) | Err(mpsc::RecvTimeoutError::Disconnected) => {
                shutdown(&mut state);
                break;
            }
            Err(mpsc::RecvTimeoutError::Timeout) => {}
        }
        poll(&mut state, &events);
    }
}

fn restore_pm2(state: &mut WorkerState, events: &Sender<ProcessEvent>) {
    // 内置与系统两套环境都可能托管着酒馆进程，逐一检查，
    // 使用户在切换环境模式后仍能自动接管正在运行的实例。
    for source in [EnvSource::Builtin, EnvSource::System] {
        let pm2 = Pm2Manager::new(source);
        if !pm2.is_installed() {
            continue;
        }
        let Ok(Some(info)) = pm2.info() else {
            continue;
        };
        if info.status != "online" {
            continue;
        }
        state.pm2 = Some(pm2);
        state.active_mode = Some(RuntimeMode::Pm2);
        // 仅回放尾部 2 MiB，足以覆盖控制台最近 2000 行，同时避免超大日志阻塞界面。
        const RESTORE_BYTES: u64 = 2 * 1024 * 1024;
        state.pm2_out_offset = state
            .pm2
            .as_ref()
            .map(|pm2| pm2.tail_offset(false, RESTORE_BYTES))
            .unwrap_or(0);
        state.pm2_error_offset = state
            .pm2
            .as_ref()
            .map(|pm2| pm2.tail_offset(true, RESTORE_BYTES))
            .unwrap_or(0);
        state.restoring_pm2_logs = true;
        state.last_pm2_poll = Instant::now() - Duration::from_secs(2);
        let _ = events.send(ProcessEvent::Pm2Restored {
            pid: info.pid,
            cwd: info.cwd,
            replaying_logs: true,
        });
    }
}

fn start(state: &mut WorkerState, spec: TavernLaunchSpec, events: &Sender<ProcessEvent>) {
    if state.active_mode == Some(RuntimeMode::Pm2) {
        if let Some(pm2) = state.pm2.as_ref()
            && let Ok(Some(info)) = pm2.info()
        {
            let _ = events.send(ProcessEvent::Pm2Restored {
                pid: info.pid,
                cwd: info.cwd,
                replaying_logs: false,
            });
        }
        return;
    }
    if state.direct.is_some() || state.active_mode.is_some() {
        let _ = events.send(ProcessEvent::Running(RuntimeMode::Direct));
        return;
    }
    state.conflict_port = None;
    state.conflict_retried = false;
    let requested_pm2 = spec.launch_mode == TavernLaunchMode::Server && spec.allow_background;
    let mode = spec.runtime_mode();
    // 按本次启动使用的环境来源重建 PM2 管理器，保证 CLI 与 Node.js 版本配套。
    state.pm2 = Some(Pm2Manager::new(spec.env_source));
    // Starting 是新日志会话的边界，必须先于校验失败和任何新进程日志。
    let _ = events.send(ProcessEvent::Starting(mode));
    if let Err(error) = validate_spec(&spec) {
        let _ = events.send(ProcessEvent::Failed(error));
        return;
    }
    if let Err(error) = prepare_webui_settings(&spec) {
        let _ = events.send(ProcessEvent::Failed(error));
        return;
    }
    if requested_pm2 && mode == RuntimeMode::Direct {
        let _ = events.send(ProcessEvent::Pm2Unavailable);
    }
    if spec.github_proxy_url.is_some() && !node_supports_import(spec.env_source) {
        let warning = format!("{LOG_MARK_WARNING}{}", t("tavern.process.interceptor_unsupported"));
        let _ = events.send(ProcessEvent::Log(warning));
    }
    if spec.show_startup_command {
        let _ = events.send(ProcessEvent::Log(format!(
            "{LOG_MARK_COMMAND}{}",
            display_command(&spec, mode)
        )));
    }
    let result = match mode {
        RuntimeMode::Direct => start_direct(state, &spec, events),
        RuntimeMode::Pm2 => start_pm2(state, &spec, events),
    };
    match result {
        Ok(()) => {
            state.active_spec = Some(spec);
            state.active_mode = Some(mode);
            let _ = events.send(ProcessEvent::Running(mode));
        }
        Err(error) => {
            state.active_spec = None;
            state.active_mode = None;
            let _ = events.send(ProcessEvent::Failed(error));
        }
    }
}

fn validate_spec(spec: &TavernLaunchSpec) -> Result<(), String> {
    if !spec.instance_path.is_dir() {
        return Err(tf("tavern.process.instance_missing", &[("path", &spec.instance_path.display())]));
    }
    if !spec.instance_path.join("server.js").is_file() {
        return Err(tf("tavern.process.not_a_tavern", &[("path", &spec.instance_path.display())]));
    }
    if crate::core::settings::env_detect::detect_nodejs(spec.env_source).is_none() {
        return Err(t("tavern.process.nodejs_required").to_owned());
    }
    let config = spec.config_path();
    if !config.is_file() {
        return Err(tf("tavern.process.config_missing", &[("path", &config.display())]));
    }
    Ok(())
}

fn prepare_webui_settings(spec: &TavernLaunchSpec) -> Result<(), String> {
    let target = match spec.data_mode {
        TavernDataMode::Current => spec.instance_path.join("data/default-user/settings.json"),
        TavernDataMode::Global => spec.global_data_path.join("default-user/settings.json"),
    };
    if target.exists() {
        return Ok(());
    }
    let parent = target
        .parent()
        .ok_or_else(|| t("tavern.process.invalid_settings_path").to_owned())?;
    fs::create_dir_all(parent)
        .map_err(|error| tf("tavern.process.create_data_dir_failed", &[("path", &parent.display().to_string()), ("error", &error.to_string())]))?;
    crate::utils::app_paths().ensure_default_tavern_settings();
    let template = fs::read_to_string(crate::utils::app_paths().default_tavern_settings_file())
        .unwrap_or_else(|_| crate::utils::TEMPLATE_TAVERN_SETTINGS_JSON.to_owned());
    let mut value: Value = serde_json::from_str(&template)
        .map_err(|error| tf("tavern.process.template_invalid", &[("error", &error)]))?;
    if !spec.instance_version.trim().is_empty()
        && let Some(object) = value.as_object_mut()
    {
        object.insert(
            "currentVersion".to_owned(),
            Value::String(spec.instance_version.clone()),
        );
    }
    let bytes = serde_json::to_vec_pretty(&value)
        .map_err(|error| tf("tavern.process.generate_settings_failed", &[("error", &error)]))?;
    fs::write(&target, bytes)
        .map_err(|error| tf("tavern.process.write_settings_failed", &[("path", &target.display().to_string()), ("error", &error.to_string())]))
}

struct LaunchCommand {
    arguments: Vec<String>,
    environment: Vec<(String, String)>,
    node_import: Option<String>,
}

fn launch_command(spec: &TavernLaunchSpec) -> Result<LaunchCommand, String> {
    let mut arguments = vec!["server.js".to_owned()];
    if let Some(data_root) = spec.data_root() {
        arguments.extend([
            "--configPath".to_owned(),
            data_root.join("config.yaml").to_string_lossy().into_owned(),
            "--dataRoot".to_owned(),
            data_root.to_string_lossy().into_owned(),
        ]);
    }
    if spec.launch_mode != TavernLaunchMode::Normal {
        arguments.extend(["--browserLaunchEnabled".to_owned(), "false".to_owned()]);
    }
    let mut environment = Vec::new();
    let mut proxy = spec.proxy.clone().map(|value| normalize_proxy_url(&value));
    let node_import = if let Some(url) = spec.github_proxy_url.as_deref() {
        if node_supports_import(spec.env_source) {
            proxy = None;
            let path = prepare_interceptor()?;
            environment.push(("GITHUB_PROXY_URL".to_owned(), url.to_owned()));
            Some(path.to_string_lossy().into_owned())
        } else {
            None
        }
    } else {
        None
    };
    if let Some(proxy) = proxy {
        for key in ["HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy"] {
            environment.push((key.to_owned(), proxy.clone()));
        }
        arguments.extend([
            "--requestProxyEnabled".to_owned(),
            "true".to_owned(),
            "--requestProxyUrl".to_owned(),
            proxy,
            "--requestProxyBypass".to_owned(),
            "localhost 127.0.0.1 ::1".to_owned(),
        ]);
    }
    Ok(LaunchCommand {
        arguments,
        environment,
        node_import,
    })
}

fn start_direct(
    state: &mut WorkerState,
    spec: &TavernLaunchSpec,
    events: &Sender<ProcessEvent>,
) -> Result<(), String> {
    let launch = launch_command(spec)?;
    let mut command = crate::core::settings::env_detect::command_for("node", spec.env_source);
    if let Some(import) = launch.node_import {
        command.arg("--import").arg(import);
    }
    command
        .args(&launch.arguments)
        .envs(launch.environment.iter().cloned())
        .current_dir(&spec.instance_path)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    let mut child = command
        .spawn()
        .map_err(|error| tf("tavern.process.start_failed", &[("error", &error)]))?;
    let pid = child.id();
    let (log_tx, log_rx) = mpsc::channel();
    if let Some(stdout) = child.stdout.take() {
        spawn_log_reader(stdout, log_tx.clone());
    }
    if let Some(stderr) = child.stderr.take() {
        spawn_log_reader(stderr, log_tx);
    }
    state.direct = Some(DirectProcess {
        child,
        logs: log_rx,
        stop_deadline: None,
    });
    let _ = events.send(ProcessEvent::Pid(Some(pid)));
    Ok(())
}

fn spawn_log_reader(reader: impl std::io::Read + Send + 'static, sender: Sender<String>) {
    thread::spawn(move || {
        for line in BufReader::new(reader).lines() {
            match line {
                Ok(line) => {
                    if sender.send(line).is_err() {
                        break;
                    }
                }
                Err(_) => break,
            }
        }
    });
}

fn start_pm2(
    state: &mut WorkerState,
    spec: &TavernLaunchSpec,
    events: &Sender<ProcessEvent>,
) -> Result<(), String> {
    let launch = launch_command(spec)?;
    let pm2 = state
        .pm2
        .as_ref()
        .ok_or_else(|| t("pm2.not_available").to_owned())?;
    pm2.clear_logs();
    state.pm2_out_offset = 0;
    state.pm2_error_offset = 0;
    state.restoring_pm2_logs = false;
    let node_args = launch
        .node_import
        .as_deref()
        .map(|path| format!("--import {path}"));
    pm2.start(
        &spec.instance_path,
        node_args.as_deref(),
        &launch.arguments[1..],
        &launch.environment,
    )?;
    if let Some(info) = pm2.info()? {
        let _ = events.send(ProcessEvent::Pid(info.pid));
    }
    Ok(())
}

/// 结束指定进程。
///
/// `force = false` 时先尝试 `/T`（连同子进程树一起请求关闭）：Node 会先收到控制台关闭事件，
/// 有机会把日志刷完；`force = true` 时加上 `/F` 强制终止 —— Node 子进程通常不响应
/// 优雅关闭，端口释放路径必须能走到这一步。
///
/// 用 `taskkill` 而不是 `Child::kill()`：后台模式下进程树里还有 Node 派生的孙进程，
/// 只杀父进程会让端口继续被占着。
fn terminate_process(pid: u32, force: bool) -> Result<(), String> {
    let mut command = Command::new("taskkill");
    if force {
        command.arg("/F");
    }
    command.arg("/T").args(["/PID", &pid.to_string()]);
    crate::core::env::apply_no_window_to_command(&mut command);
    command
        .status()
        .map(|_| ())
        .map_err(|error| tf("tavern.process.stop_failed", &[("error", &error)]))
}

fn stop(state: &mut WorkerState, events: &Sender<ProcessEvent>) {
    let Some(mode) = state.active_mode else {
        return;
    };
    let _ = events.send(ProcessEvent::Stopping);
    match mode {
        RuntimeMode::Direct => {
            if let Some(process) = state.direct.as_mut() {
                let _ = terminate_process(process.child.id(), false);
                process.stop_deadline = Some(Instant::now() + Duration::from_secs(8));
            }
        }
        RuntimeMode::Pm2 => match state.pm2().and_then(Pm2Manager::stop) {
            Ok(()) => finish_stopped(state, events),
            Err(error) => recover_pm2_after_command_error(state, events, error),
        },
    }
}

fn kill(state: &mut WorkerState, events: &Sender<ProcessEvent>) {
    match state.active_mode {
        Some(RuntimeMode::Direct) => {
            if let Some(mut process) = state.direct.take() {
                let _ = process.child.kill();
                let _ = process.child.wait();
            }
            finish_stopped(state, events);
        }
        Some(RuntimeMode::Pm2) => match state.pm2().and_then(Pm2Manager::delete) {
            Ok(()) => finish_stopped(state, events),
            Err(error) => recover_pm2_after_command_error(state, events, error),
        },
        None => {}
    }
}

fn restart(state: &mut WorkerState, events: &Sender<ProcessEvent>) {
    if state.active_mode == Some(RuntimeMode::Pm2) {
        let _ = events.send(ProcessEvent::Starting(RuntimeMode::Pm2));
        state.pm2_out_offset = 0;
        state.pm2_error_offset = 0;
        state.restoring_pm2_logs = false;
        let result = state.pm2().map(|pm2| {
            pm2.clear_logs();
            pm2.restart()
        });
        match result.and_then(|result| result) {
            Ok(()) => {
                let _ = events.send(ProcessEvent::Running(RuntimeMode::Pm2));
            }
            Err(error) => recover_pm2_after_command_error(state, events, error),
        }
        return;
    }
    let Some(spec) = state.active_spec.clone() else {
        return;
    };
    match state.active_mode {
        Some(RuntimeMode::Direct) => {
            state.restart_spec = Some(spec);
            stop(state, events);
        }
        Some(RuntimeMode::Pm2) => unreachable!("PM2 已在前置分支处理"),
        None => start(state, spec, events),
    }
}

fn release_port_and_retry(
    state: &mut WorkerState,
    conflict: PortConflict,
    events: &Sender<ProcessEvent>,
) {
    if state.conflict_retried
        || state.conflict_port != Some(conflict.port)
        || conflict.processes.is_empty()
    {
        let _ = events.send(ProcessEvent::Failed(
            t("tavern.process.port_info_stale").to_owned(),
        ));
        return;
    }
    let current = match query_port_processes(conflict.port) {
        Ok(processes) => processes,
        Err(error) => {
            let _ = events.send(ProcessEvent::Failed(error));
            return;
        }
    };
    let confirmed: Vec<_> = current
        .into_iter()
        .filter(|current| {
            conflict
                .processes
                .iter()
                .any(|old| old.pid == current.pid && old.name == current.name)
        })
        .collect();
    if confirmed.is_empty() {
        let _ = events.send(ProcessEvent::Failed(
            t("tavern.process.port_process_changed").to_owned(),
        ));
        return;
    }
    for process in &confirmed {
        let _ = terminate_process(process.pid, false);
    }
    thread::sleep(Duration::from_millis(500));
    let remaining = query_port_processes(conflict.port).unwrap_or_default();
    for process in remaining {
        if confirmed
            .iter()
            .any(|old| old.pid == process.pid && old.name == process.name)
        {
            // Windows 上没有优雅/强制的两段式信号：Node 子进程不响应关闭消息，
            // 第二次直接强制结束，与旧版行为一致。
            let _ = terminate_process(process.pid, true);
        }
    }
    state.conflict_retried = true;
    state.conflict_port = None;
    if let Some(spec) = state.active_spec.take() {
        state.active_mode = None;
        start(state, spec, events);
        // `start` 会初始化普通启动状态；端口释放路径必须保留“已经重试过”。
        state.conflict_retried = true;
    }
}

fn poll(state: &mut WorkerState, events: &Sender<ProcessEvent>) {
    if state.active_mode == Some(RuntimeMode::Direct) {
        poll_direct(state, events);
    } else if state.active_mode == Some(RuntimeMode::Pm2)
        && state.last_pm2_poll.elapsed() >= Duration::from_secs(1)
    {
        poll_pm2(state, events);
        state.last_pm2_poll = Instant::now();
    }
}

fn poll_direct(state: &mut WorkerState, events: &Sender<ProcessEvent>) {
    let mut exited = None;
    let mut pending_logs = Vec::new();
    if let Some(process) = state.direct.as_mut() {
        while let Ok(line) = process.logs.try_recv() {
            pending_logs.push(line);
        }
        if process
            .stop_deadline
            .is_some_and(|deadline| Instant::now() >= deadline)
        {
            let _ = process.child.kill();
        }
        match process.child.try_wait() {
            Ok(Some(status)) => exited = Some(status.code()),
            Ok(None) => {}
            Err(error) => {
                let _ = events.send(ProcessEvent::Log(format!(
                    "{LOG_MARK_ERROR}{}",
                    tf("tavern.process.status_query_failed", &[("error", &error)])
                )));
            }
        }
    }
    for line in pending_logs {
        emit_log(state, events, line, false);
    }
    if let Some(code) = exited {
        state.direct = None;
        let _ = events.send(ProcessEvent::Pid(None));
        let _ = events.send(ProcessEvent::Exited(code));
        if let Some(port) = state.conflict_port {
            let processes = query_port_processes(port).unwrap_or_default();
            let _ = events.send(ProcessEvent::PortConflict(PortConflict {
                port,
                retry_available: !processes.is_empty() && !state.conflict_retried,
                processes,
            }));
            state.active_mode = None;
            return;
        }
        if let Some(spec) = state.restart_spec.take() {
            state.active_mode = None;
            state.active_spec = None;
            start(state, spec, events);
        } else {
            finish_stopped(state, events);
        }
    }
}

fn poll_pm2(state: &mut WorkerState, events: &Sender<ProcessEvent>) {
    let historical = state.restoring_pm2_logs;
    for error_log in [false, true] {
        // 先取出偏移量副本，避免在后续读取日志时同时持有对 `state` 的可变借用。
        let mut offset = if error_log {
            state.pm2_error_offset
        } else {
            state.pm2_out_offset
        };
        // 先把本批日志读完再交给渲染路径，避免在持有 `state` 不可变借用时
        // 再次可变借用以写日志行（`emit_log` 需要 `&mut WorkerState`）。
        let batch = state.pm2().ok().and_then(|pm2| {
            pm2.read_log(error_log, &mut offset).ok()
        });
        if let Some(lines) = batch {
            for line in lines {
                emit_log(state, events, line, historical);
            }
        }
        if error_log {
            state.pm2_error_offset = offset;
        } else {
            state.pm2_out_offset = offset;
        }
    }
    state.restoring_pm2_logs = false;
    let info = match state.pm2() {
        Ok(pm2) => pm2.info(),
        Err(error) => Err(error),
    };
    match info {
        Ok(Some(info)) if info.status == "online" => {
            let _ = events.send(ProcessEvent::Pid(info.pid));
        }
        Ok(Some(info)) if matches!(info.status.as_str(), "launching" | "stopping") => {}
        Ok(Some(info)) if info.status == "errored" => {
            state.active_mode = None;
            state.active_spec = None;
            let _ = events.send(ProcessEvent::Failed(
                t("tavern.process.pm2_error_state").to_owned(),
            ));
        }
        Ok(_) => finish_stopped(state, events),
        Err(error) => {
            let _ = events.send(ProcessEvent::Log(format!("{LOG_MARK_ERROR}{error}")));
        }
    }
}

/// PM2 命令失败后重新查询真实状态，避免界面误报服务已经停止。
fn recover_pm2_after_command_error(
    state: &mut WorkerState,
    events: &Sender<ProcessEvent>,
    error: String,
) {
    let _ = events.send(ProcessEvent::Log(format!("{LOG_MARK_ERROR}{error}")));
    let info = match state.pm2() {
        Ok(pm2) => pm2.info(),
        Err(error) => Err(error),
    };
    match info {
        Ok(Some(info)) if info.status == "online" => {
            state.active_mode = Some(RuntimeMode::Pm2);
            let _ = events.send(ProcessEvent::Pid(info.pid));
            let _ = events.send(ProcessEvent::Running(RuntimeMode::Pm2));
        }
        Ok(_) => finish_stopped(state, events),
        Err(query_error) => {
            let _ = events.send(ProcessEvent::Failed(tf(
                "tavern.process.pm2_query_failed",
                &[("error", &error), ("query_error", &query_error)],
            )));
        }
    }
}

fn emit_log(
    state: &mut WorkerState,
    events: &Sender<ProcessEvent>,
    line: String,
    historical: bool,
) {
    let cleaned = strip_terminal_sequences(&line);
    if let Some(url) = extract_tavern_url(&cleaned) {
        let _ = events.send(ProcessEvent::ServerUrl(url));
    }
    if let Some(port) = extract_conflict_port(&cleaned) {
        state.conflict_port = Some(port);
    }
    let event = if historical {
        ProcessEvent::HistoricalLog(cleaned)
    } else {
        ProcessEvent::Log(cleaned)
    };
    let _ = events.send(event);
}

fn finish_stopped(state: &mut WorkerState, events: &Sender<ProcessEvent>) {
    state.direct = None;
    state.active_mode = None;
    state.active_spec = None;
    state.restart_spec = None;
    let _ = events.send(ProcessEvent::Pid(None));
    let _ = events.send(ProcessEvent::Stopped);
}

fn shutdown(state: &mut WorkerState) {
    if state.active_mode == Some(RuntimeMode::Direct)
        && let Some(mut process) = state.direct.take()
    {
        let _ = process.child.kill();
        let _ = process.child.wait();
    }
}

/// 统一代理地址格式。
pub fn normalize_proxy_url(value: &str) -> String {
    let value = value.trim();
    if ["http://", "https://", "socks5://", "socks4://"]
        .iter()
        .any(|prefix| value.starts_with(prefix))
    {
        value.to_owned()
    } else {
        format!("http://{value}")
    }
}

fn node_supports_import(source: EnvSource) -> bool {
    crate::core::settings::env_detect::detect_nodejs(source)
        .and_then(|version| {
            version
                .trim_start_matches('v')
                .split('.')
                .next()
                .and_then(|major| major.parse::<u32>().ok())
        })
        .is_some_and(|major| major >= 19)
}

const INTERCEPTOR: &str = r#"const base=(process.env.GITHUB_PROXY_URL||'').replace(/\/+$/,'');
const rewrite=(value)=>typeof value==='string'&&(value.includes('github.com')||value.includes('raw.githubusercontent.com'))&&!value.includes('api.github.com')?base+'/'+value:value;
for (const name of ['https','http']) { const mod=await import(name); const original=mod.default.request; mod.default.request=function(url,...rest){ return original.call(this,rewrite(url),...rest); }; }
"#;

fn prepare_interceptor() -> Result<PathBuf, String> {
    let path = crate::utils::app_paths()
        .temp
        .join("github-interceptor.mjs");
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(|error| tf("network.test.temp_dir_failed", &[("error", &error)]))?;
    }
    fs::write(&path, INTERCEPTOR).map_err(|error| tf("tavern.process.write_interceptor_failed", &[("error", &error)]))?;
    Ok(path)
}

fn display_command(spec: &TavernLaunchSpec, mode: RuntimeMode) -> String {
    let launch = launch_command(spec).unwrap_or(LaunchCommand {
        arguments: vec!["server.js".to_owned()],
        environment: Vec::new(),
        node_import: None,
    });
    let mut parts = Vec::new();
    for (key, value) in launch.environment {
        parts.push(format!("{key}={value}"));
    }
    match mode {
        RuntimeMode::Direct => {
            parts.push("node".to_owned());
            if let Some(path) = launch.node_import {
                parts.extend(["--import".to_owned(), shell_quote(&path)]);
            }
            parts.extend(launch.arguments.into_iter().map(|part| shell_quote(&part)));
        }
        RuntimeMode::Pm2 => {
            parts.extend([
                "pm2".to_owned(),
                "start".to_owned(),
                "server.js".to_owned(),
                "--name".to_owned(),
                crate::core::pm2::PROCESS_NAME.to_owned(),
            ]);
            if let Some(path) = launch.node_import {
                parts.extend([
                    "--node-args".to_owned(),
                    shell_quote(&format!("--import {path}")),
                ]);
            }
            if launch.arguments.len() > 1 {
                parts.push("--".to_owned());
                parts.extend(launch.arguments[1..].iter().map(|part| shell_quote(part)));
            }
        }
    }
    parts.join(" ")
}

fn shell_quote(value: &str) -> String {
    if value
        .chars()
        .all(|ch| ch.is_ascii_alphanumeric() || "-._/:".contains(ch))
    {
        value.to_owned()
    } else {
        format!("'{}'", value.replace('\'', "'\\''"))
    }
}

/// 清理 ANSI SGR 与 OSC 控制序列，日志文件和界面仅保留可读文本。
pub fn strip_terminal_sequences(line: &str) -> String {
    let bytes = line.as_bytes();
    let mut result = String::with_capacity(line.len());
    let mut index = 0;
    while index < bytes.len() {
        if bytes[index] != 0x1b {
            let ch = line[index..].chars().next().unwrap_or_default();
            result.push(ch);
            index += ch.len_utf8();
            continue;
        }
        index += 1;
        if index >= bytes.len() {
            break;
        }
        match bytes[index] {
            b'[' => {
                index += 1;
                while index < bytes.len() {
                    let byte = bytes[index];
                    index += 1;
                    if (0x40..=0x7e).contains(&byte) {
                        break;
                    }
                }
            }
            b']' => {
                index += 1;
                while index < bytes.len() {
                    if bytes[index] == 0x07 {
                        index += 1;
                        break;
                    }
                    if bytes[index] == 0x1b && bytes.get(index + 1).copied() == Some(b'\\') {
                        index += 2;
                        break;
                    }
                    index += 1;
                }
            }
            _ => index += 1,
        }
    }
    result
}

/// SillyTavern 启动完成后输出的唯一可信访问地址格式。
///
/// 只接受 `Go to: <URL> to open SillyTavern`，禁止从启动命令、代理配置或
/// 其他包含 HTTP 地址的普通日志中猜测，避免把代理端口当成酒馆端口。
static TAVERN_URL_PATTERN: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"(?i)\bGo\s+to:\s*(https?://[^\s]+?)\s+to\s+open\s+SillyTavern\b")
        .expect("固定的 SillyTavern 地址正则必须有效")
});

pub fn extract_tavern_url(line: &str) -> Option<String> {
    let captures = TAVERN_URL_PATTERN.captures(line)?;
    let url = captures.get(1)?.as_str().trim_end_matches([',', '.', ')']);
    Some(url.to_owned())
}

pub fn extract_conflict_port(line: &str) -> Option<u16> {
    let lower = line.to_ascii_lowercase();
    if !lower.contains("eaddrinuse") && !lower.contains("already in use") {
        return None;
    }
    line.rsplit(':')
        .find_map(|part| {
            let digits: String = part.chars().take_while(|ch| ch.is_ascii_digit()).collect();
            (!digits.is_empty()).then(|| digits.parse().ok()).flatten()
        })
        .or_else(|| {
            line.split(|ch: char| !ch.is_ascii_digit())
                .filter(|value| value.len() >= 2)
                .filter_map(|value| value.parse::<u16>().ok())
                .next_back()
        })
}

/// 查询占用指定端口的监听进程。
///
/// 用 `netstat -ano` 而不是 Unix 的 `lsof`：`-a` 列出全部连接、`-n` 用数字形式显示地址与端口
/// （省去反向域名解析，快很多）、`-o` 附带拥有该连接的 PID。
pub fn query_port_processes(port: u16) -> Result<Vec<PortProcess>, String> {
    let mut command = Command::new("netstat");
    command.args(["-ano", "-p", "TCP"]);
    crate::core::env::apply_no_window_to_command(&mut command);
    let output = command
        .output()
        .map_err(|error| tf("tavern.process.port_query_failed", &[("error", &error)]))?;
    // netstat 在「没有匹配连接」时也会返回成功，因此不能只看退出码。
    if !output.status.success() && output.stdout.is_empty() {
        return Ok(Vec::new());
    }
    parse_netstat_processes(&String::from_utf8_lossy(&output.stdout), port)
}

/// 从 `netstat -ano` 输出里解析出监听指定端口的进程。
///
/// 每行的形态是：
/// ```text
///   TCP    0.0.0.0:8000           0.0.0.0:0              LISTENING       12345
///   TCP    [::]:8000              [::]:0                 LISTENING       12345
/// ```
/// 只看 `LISTENING` 行，并按本地地址末尾的端口号过滤 —— 用 `:8000` 而不是
/// `8000` 做匹配，避免 `18000` 被误判成 `8000`。
fn parse_netstat_processes(output: &str, port: u16) -> Result<Vec<PortProcess>, String> {
    let suffix = format!(":{port}");
    let mut processes: Vec<PortProcess> = Vec::new();

    for line in output.lines() {
        let columns: Vec<&str> = line.split_whitespace().collect();
        // 至少要有「协议 本地地址 外部地址 状态 PID」五列。
        if columns.len() < 5 || !columns[0].eq_ignore_ascii_case("TCP") {
            continue;
        }
        if !columns[3].eq_ignore_ascii_case("LISTENING") {
            continue;
        }
        if !columns[1].ends_with(&suffix) {
            continue;
        }
        let Ok(pid) = columns[4].parse::<u32>() else {
            continue;
        };
        // PID 0 是系统空闲进程，杀掉它没有任何意义。
        if pid == 0 || processes.iter().any(|process| process.pid == pid) {
            continue;
        }
        processes.push(PortProcess {
            pid,
            name: process_name(pid).unwrap_or_else(|| "unknown".to_owned()),
        });
    }

    Ok(processes)
}

/// 由 PID 反查进程名；查不到时返回 `None`。
///
/// `tasklist` 的输出受系统语言影响，因此不解析表头，直接从数据行里取：
/// 第一列是映像名，第二列是 PID。
fn process_name(pid: u32) -> Option<String> {
    let mut command = Command::new("tasklist");
    command.args(["/FI", &format!("PID eq {pid}"), "/FO", "CSV", "/NH"]);
    crate::core::env::apply_no_window_to_command(&mut command);
    let output = command.output().ok()?;
    let text = String::from_utf8_lossy(&output.stdout);
    let name = text.lines().next()?.split(',').next()?.trim().trim_matches('"');
    (!name.is_empty() && !name.contains("没有运行") && !name.contains("No tasks")).then(|| name.to_owned())
}

/// 创建规范日志目录，并保证当前日志文件存在。
pub fn ensure_sillytavern_log_file() -> Result<(), String> {
    let paths = crate::utils::app_paths();
    fs::create_dir_all(&paths.logs)
        .map_err(|error| tf("tavern.process.create_log_dir_failed", &[("path", &paths.logs.display().to_string()), ("error", &error.to_string())]))?;
    OpenOptions::new()
        .create(true)
        .append(true)
        .open(paths.sillytavern_log_file())
        .map(|_| ())
        .map_err(|error| tf("tavern.process.create_log_failed", &[("error", &error)]))
}

/// 新启动前将当前日志轮换为 latest，并创建新的实时日志。
pub fn prepare_sillytavern_log_file() -> Result<(), String> {
    let paths = crate::utils::app_paths();
    fs::create_dir_all(&paths.logs)
        .map_err(|error| tf("tavern.process.create_log_dir_failed", &[("path", &paths.logs.display().to_string()), ("error", &error.to_string())]))?;
    let current = paths.sillytavern_log_file();
    let latest = paths.sillytavern_latest_log_file();
    if current.exists() {
        if latest.exists() {
            fs::remove_file(&latest).map_err(|error| tf("tavern.process.rotate_latest_failed", &[("error", &error)]))?;
        }
        fs::rename(&current, &latest).map_err(|error| tf("tavern.process.rotate_failed", &[("error", &error)]))?;
    }
    fs::File::create(&current)
        .map(|_| ())
        .map_err(|error| tf("tavern.process.create_live_log_failed", &[("path", &current.display().to_string()), ("error", &error.to_string())]))
}

/// 将一行已经清理和分类的日志写入规范实时日志。
pub fn append_sillytavern_log_line(line: &str) -> Result<(), String> {
    let path = crate::utils::app_paths().sillytavern_log_file();
    let mut file = OpenOptions::new()
        .create(true)
        .append(true)
        .open(&path)
        .map_err(|error| tf("tavern.process.open_live_log_failed", &[("path", &path.display().to_string()), ("error", &error.to_string())]))?;
    writeln!(file, "{line}")
        .map_err(|error| tf("tavern.process.write_live_log_failed", &[("path", &path.display().to_string()), ("error", &error)]))
}

#[cfg(test)]
mod tests {
    use super::{
        TavernDataMode, TavernLaunchMode, TavernLaunchSpec, extract_conflict_port,
        extract_tavern_url, launch_command, normalize_proxy_url, parse_netstat_processes,
        strip_terminal_sequences,
    };
    use crate::core::settings::EnvSource;
    use std::path::PathBuf;

    #[test]
    fn builds_global_data_and_proxy_arguments() {
        let command = launch_command(&TavernLaunchSpec {
            instance_path: PathBuf::from(r"C:\AstraBrew\sillytavern"),
            instance_version: "1.0.0".to_owned(),
            data_mode: TavernDataMode::Global,
            global_data_path: PathBuf::from(r"C:\AstraBrew\data\global"),
            proxy: Some("127.0.0.1:7890".to_owned()),
            github_proxy_url: None,
            launch_mode: TavernLaunchMode::Server,
            allow_background: false,
            show_startup_command: true,
            export_path: r"C:\AstraBrew\exports".to_owned(),
            env_source: EnvSource::System,
        })
        .unwrap();
        assert!(
            command
                .arguments
                .windows(2)
                .any(|pair| pair == ["--dataRoot", r"C:\AstraBrew\data\global"])
        );
        assert!(
            command
                .arguments
                .windows(2)
                .any(|pair| pair == ["--browserLaunchEnabled", "false"])
        );
        assert!(
            command
                .environment
                .iter()
                .any(|(key, value)| { key == "HTTP_PROXY" && value == "http://127.0.0.1:7890" })
        );
    }

    #[test]
    fn browser_launch_argument_matches_launch_mode() {
        let base = TavernLaunchSpec {
            instance_path: PathBuf::from(r"C:\AstraBrew\sillytavern"),
            instance_version: "1.0.0".to_owned(),
            data_mode: TavernDataMode::Current,
            global_data_path: PathBuf::from(r"C:\AstraBrew\data\global"),
            proxy: None,
            github_proxy_url: None,
            launch_mode: TavernLaunchMode::Normal,
            allow_background: false,
            show_startup_command: false,
            export_path: r"C:\AstraBrew\exports".to_owned(),
            env_source: EnvSource::System,
        };
        let normal = launch_command(&base).unwrap();
        assert!(
            !normal
                .arguments
                .iter()
                .any(|argument| argument == "--browserLaunchEnabled")
        );

        let desktop = launch_command(&TavernLaunchSpec {
            launch_mode: TavernLaunchMode::Desktop,
            ..base.clone()
        })
        .unwrap();
        assert!(
            desktop
                .arguments
                .windows(2)
                .any(|pair| { pair == ["--browserLaunchEnabled", "false"] })
        );

        let server = launch_command(&TavernLaunchSpec {
            launch_mode: TavernLaunchMode::Server,
            ..base
        })
        .unwrap();
        assert!(
            server
                .arguments
                .windows(2)
                .any(|pair| { pair == ["--browserLaunchEnabled", "false"] })
        );
    }

    #[test]
    fn normalizes_proxy_protocol() {
        assert_eq!(
            normalize_proxy_url("127.0.0.1:7890"),
            "http://127.0.0.1:7890"
        );
        assert_eq!(
            normalize_proxy_url("socks5://127.0.0.1:1080"),
            "socks5://127.0.0.1:1080"
        );
    }

    #[test]
    fn extracts_url_and_conflict_port() {
        assert_eq!(
            extract_tavern_url("Go to: http://localhost:8000/ to open SillyTavern").as_deref(),
            Some("http://localhost:8000/")
        );
        assert_eq!(
            extract_tavern_url(
                "[command] HTTP_PROXY=http://127.0.0.1:7892 node server.js --requestProxyUrl http://127.0.0.1:7892"
            ),
            None
        );
        assert_eq!(
            extract_tavern_url("Proxy URL is used: http://127.0.0.1:7892"),
            None
        );
        assert_eq!(
            extract_conflict_port("Error: listen EADDRINUSE: address already in use :::8000"),
            Some(8000)
        );
    }

    #[test]
    fn strips_ansi_and_osc_sequences() {
        assert_eq!(strip_terminal_sequences("\x1b[31merror\x1b[0m"), "error");
        assert_eq!(strip_terminal_sequences("a\x1b]2;title\x07b"), "ab");
    }

    #[test]
    fn parses_netstat_listeners_for_the_requested_port() {
        // 真实 `netstat -ano` 输出的列布局：协议 / 本地地址 / 外部地址 / 状态 / PID。
        let output = "  协议  本地地址          外部地址        状态           PID\n\
                      \x20 TCP    0.0.0.0:8000           0.0.0.0:0              LISTENING       42\n\
                      \x20 TCP    [::]:8000              [::]:0                 LISTENING       42\n\
                      \x20 TCP    0.0.0.0:18000          0.0.0.0:0              LISTENING       99\n\
                      \x20 TCP    0.0.0.0:8000           1.2.3.4:55000          ESTABLISHED     77\n";
        let processes = parse_netstat_processes(output, 8000).unwrap();
        // 同一 PID 的两个监听地址（IPv4/IPv6）去重成一条；`18000` 不能被当成 `8000`；
        // `ESTABLISHED` 的连接不算监听。
        assert_eq!(processes.len(), 1);
        assert_eq!(processes[0].pid, 42);
    }

    #[test]
    fn netstat_output_without_listeners_is_empty() {
        let output = "  协议  本地地址          外部地址        状态           PID\n\
                      \x20 TCP    0.0.0.0:9000           0.0.0.0:0              LISTENING       7\n";
        assert!(parse_netstat_processes(output, 8000).unwrap().is_empty());
        // 空输出（端口完全没人监听）同样应是空列表而不是错误。
        assert!(parse_netstat_processes("", 8000).unwrap().is_empty());
    }

    #[test]
    fn netstat_parsing_skips_the_idle_process() {
        // PID 0 是系统空闲进程，永远不该被当成「占用端口的进程」。
        let output = "  TCP    0.0.0.0:8000           0.0.0.0:0              LISTENING       0\n";
        assert!(parse_netstat_processes(output, 8000).unwrap().is_empty());
    }
}
