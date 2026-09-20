//! 本地实例任务调度。应用级订阅消费事件，关闭弹窗或切换页面不会丢失后台任务。

use crate::lang::tf;
use crate::lang::t;
use iced::Task;
use std::collections::{HashMap, VecDeque};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{
    Arc,
    mpsc::{self, Receiver, SyncSender},
};

use super::{Launcher, Message};
use crate::core::local_instances::{
    self as service, DependencyStatus, LocalError, LocalErrorKind, LocalInstance, dependencies,
    find_scan, scan::ScanEvent,
};
use crate::pages::notice::TransientNotice;
use crate::pages::versions::{
    VersionMessage, VersionSource,
    local::{LocalInstallState, ScanPhase, ScanState, append_log},
};

enum Event {
    Loaded(Result<Vec<LocalInstance>, LocalError>),
    Imported(Result<LocalInstance, LocalError>),
    Scan(u64, ScanEvent),
    Checked(String, u64, Result<DependencyStatus, LocalError>),
    InstallLog(u64, String),
    Installed(u64, String, Result<DependencyStatus, LocalError>),
    Saved(Result<(), LocalError>),
}

/// 实例检测请求携带序号，移除、重新导入、开始安装后旧结果不能覆盖新状态。
struct CheckRequest {
    path: String,
    id: u64,
}

pub(super) struct LocalRuntime {
    tx: SyncSender<Event>,
    rx: Receiver<Event>,
    serial: u64,
    scan_id: u64,
    scan_cancel: Arc<AtomicBool>,
    scan_workers: Vec<std::thread::JoinHandle<()>>,
    shutdown: Arc<AtomicBool>,
    checks: VecDeque<CheckRequest>,
    check_ids: HashMap<String, u64>,
    checking: usize,
    switch_intent: Option<(String, u64)>,
    install_id: u64,
    saving: bool,
    pending_save: Option<Vec<LocalInstance>>,
    writable: bool,
    /// 测试注入后台事件，不访问真实磁盘、网络或 npm。
    workers_enabled: bool,
}

impl Default for LocalRuntime {
    fn default() -> Self {
        let (tx, rx) = mpsc::sync_channel(256);
        Self {
            tx,
            rx,
            serial: 0,
            scan_id: 0,
            scan_cancel: Arc::new(AtomicBool::new(false)),
            scan_workers: Vec::new(),
            shutdown: Arc::new(AtomicBool::new(false)),
            checks: VecDeque::new(),
            check_ids: HashMap::new(),
            checking: 0,
            switch_intent: None,
            install_id: 0,
            saving: false,
            pending_save: None,
            writable: true,
            workers_enabled: !cfg!(test),
        }
    }
}

impl Drop for LocalRuntime {
    fn drop(&mut self) {
        self.shutdown.store(true, Ordering::Relaxed);
        self.scan_cancel.store(true, Ordering::Relaxed);
        for worker in self.scan_workers.drain(..) {
            let _ = worker.join();
        }
    }
}

/// 扫描事件使用可取消的背压发送，退出时不能被已满的 UI 通道卡住。
fn send_scan(tx: &SyncSender<Event>, mut event: Event, cancel: &AtomicBool) -> bool {
    loop {
        if cancel.load(Ordering::Relaxed) {
            return false;
        }
        match tx.try_send(event) {
            Ok(()) => return true,
            Err(mpsc::TrySendError::Disconnected(_)) => return false,
            Err(mpsc::TrySendError::Full(pending)) => {
                event = pending;
                std::thread::sleep(std::time::Duration::from_millis(10));
            }
        }
    }
}

impl Launcher {
    pub(super) fn local_needs_tick(&self) -> bool {
        self.versions.local.loading
            || self.versions.local.import_pending
            || self.versions.local.scan.phase.active()
            || self.versions.local.scan.auto_hide_at.is_some()
            || self.versions.local.install.running
            || self.versions.local.toast.is_some()
            || self.local_runtime.checking > 0
            || !self.local_runtime.checks.is_empty()
            || self.local_runtime.saving
            || !self.local_runtime.scan_workers.is_empty()
    }

    pub(super) fn local_has_pending_save(&self) -> bool {
        self.local_runtime.saving
    }

    pub(super) fn invalidate_local_switch(&mut self) {
        self.local_runtime.switch_intent = None;
    }

    #[cfg(not(test))]
    pub(super) fn load_local_instances(&mut self) {
        self.versions.local.loading = true;
        let tx = self.local_runtime.tx.clone();
        std::thread::spawn(move || {
            let result = service::load(&service::store_path(), &service::online_dir());
            let _ = tx.send(Event::Loaded(result));
        });
    }

    /// 原生面板使用异步接口，磁盘解析另交后台执行，避免冻结进度条。
    pub(super) fn pick_local_instance(&mut self) -> Task<Message> {
        if self.versions.local.loading || self.versions.local.import_pending {
            return Task::none();
        }
        if !self.local_runtime.writable {
            self.versions
                .local
                .notify("local.list.not_writable", "", true);
            return Task::none();
        }
        self.versions.local.import_pending = true;
        let title = crate::lang::t("local.import.dialog_title");
        Task::perform(
            async move {
                rfd::AsyncFileDialog::new()
                    .set_title(title)
                    .add_filter("package.json", &["json"])
                    .pick_file()
                    .await
                    .map(|file| file.path().to_owned())
            },
            Message::LocalImportChosen,
        )
    }

    pub(super) fn import_local_file(&mut self, path: Option<PathBuf>) {
        let Some(path) = path else {
            self.versions.local.import_pending = false;
            return;
        };
        let tx = self.local_runtime.tx.clone();
        std::thread::spawn(move || {
            let _ = tx.send(Event::Imported(service::inspect_package(
                &path,
                &service::online_dir(),
            )));
        });
    }

    /// 返回 true 表示已经消费本地消息，不再进入页面占位分支。
    pub(super) fn handle_local_message(&mut self, message: &VersionMessage) -> bool {
        match message {
            VersionMessage::ScanLocal => {
                if self.versions.local.loading {
                    return true;
                }
                if self.versions.local.scan.phase.active() {
                    self.versions.local.scan.visible = true;
                } else if self.local_runtime.writable {
                    self.begin_scan();
                } else {
                    self.versions
                        .local
                        .notify("local.list.not_writable", "", true);
                }
            }
            VersionMessage::CloseScanLog => self.versions.local.close_scan(),
            VersionMessage::CancelScan => {
                if self.versions.local.scan.phase.active() {
                    self.local_runtime
                        .scan_cancel
                        .store(true, Ordering::Relaxed);
                    self.local_runtime.scan_id += 1;
                    self.versions.local.scan.phase = ScanPhase::Cancelled;
                    self.versions.local.scan.cancel_confirm_visible = false;
                    self.versions.local.scan.auto_hide_at = None;
                    self.versions.local.notify("local.scan.cancelled", "", false);
                }
            }
            VersionMessage::SwitchLocal(path) => {
                if !self.versions.local.install.running && !self.versions.install_task.running {
                    self.queue_dependency_check(path.clone(), true);
                } else {
                    self.versions
                        .local
                        .notify("app.notice.install_running", "", true);
                }
            }
            VersionMessage::RecheckLocalDependencies(path) => {
                self.queue_dependency_check(path.clone(), false)
            }
            VersionMessage::InstallLocalDependencies(path) => {
                self.install_local_dependencies(path.clone())
            }
            VersionMessage::RemoveLocal(path) => {
                if !self.local_runtime.writable {
                    self.versions
                        .local
                        .notify("local.list.not_writable", "", true);
                    return true;
                }
                let before = self.versions.local_instances.len();
                self.versions.update(message.clone());
                if before != self.versions.local_instances.len() {
                    self.local_runtime.check_ids.remove(path);
                    self.queue_local_save();
                }
            }
            _ => return false,
        }
        true
    }

    /// 关闭应用前先回收扫描子进程，防止后台 find 脱离启动器存活。
    pub(super) fn stop_scan_for_exit(&mut self) {
        self.local_runtime
            .scan_cancel
            .store(true, Ordering::Relaxed);
        for worker in self.local_runtime.scan_workers.drain(..) {
            let _ = worker.join();
        }
    }

    fn reap_scan_workers(&mut self) {
        let mut index = 0;
        while index < self.local_runtime.scan_workers.len() {
            if self.local_runtime.scan_workers[index].is_finished() {
                let worker = self.local_runtime.scan_workers.swap_remove(index);
                let _ = worker.join();
            } else {
                index += 1;
            }
        }
    }

    fn begin_scan(&mut self) {
        self.reap_scan_workers();
        if !self.local_runtime.scan_workers.is_empty() {
            self.versions.local.scan.visible = true;
            self.versions
                .local
                .notify("app.local_instances.scan_stopping", "", false);
            return;
        }
        self.local_runtime
            .scan_cancel
            .store(true, Ordering::Relaxed);
        self.local_runtime.scan_cancel = Arc::new(AtomicBool::new(false));
        self.local_runtime.scan_id += 1;
        let id = self.local_runtime.scan_id;
        self.versions.local.scan = ScanState {
            visible: true,
            phase: ScanPhase::Running,
            started_at: Some(std::time::Instant::now()),
            ..ScanState::default()
        };
        if !self.local_runtime.workers_enabled {
            return;
        }
        let tx = self.local_runtime.tx.clone();
        let cancel = self.local_runtime.scan_cancel.clone();
        self.local_runtime
            .scan_workers
            .push(std::thread::spawn(move || {
                find_scan::run_home(service::online_dir(), &cancel, |event| {
                    send_scan(&tx, Event::Scan(id, event), &cancel)
                });
            }));
    }

    /// 快速扫描失败只显示结果，不再进入另一种扫描方式。
    fn handle_scan_failure(&mut self, error: LocalError) {
        self.versions.local.scan.cancel_confirm_visible = false;
        self.versions.local.scan.auto_hide_at = None;
        if error.kind == LocalErrorKind::Cancelled
            || self.local_runtime.scan_cancel.load(Ordering::Relaxed)
        {
            self.versions.local.scan.phase = ScanPhase::Cancelled;
            return;
        }
        append_log(&mut self.versions.local.scan.logs, error.message.into());
        append_log(&mut self.versions.local.scan.logs, error.detail.clone());
        self.versions.local.scan.phase = ScanPhase::Failed;
        self.versions.local.scan.error = Some(error.clone());
        self.versions.local.report(&error);
    }

    /// 移除磁盘上已经不存在的本地实例，并用全局提醒告知用户。
    ///
    /// 只在明确「找不到」时移除（见 `service::instance_missing`），正在安装依赖的实例保持
    /// 不动，避免打断正在写入该目录的安装流程；当前实例被删除时一并取消选择，
    /// 否则启动与资源绑定会一直指向一个空目录。
    fn prune_missing_local_instances(&mut self) {
        let removed: Vec<String> = self
            .versions
            .local_instances
            .iter()
            .filter(|instance| {
                instance.dependencies != DependencyStatus::Installing
                    && service::instance_missing(Path::new(&instance.path))
            })
            .map(|instance| instance.path.clone())
            .collect();
        if removed.is_empty() {
            return;
        }
        self.versions
            .local_instances
            .retain(|instance| !removed.contains(&instance.path));
        for path in &removed {
            self.local_runtime.check_ids.remove(path);
        }
        if let Some((pending, _)) = self.local_runtime.switch_intent.as_ref()
            && removed.contains(pending)
        {
            self.local_runtime.switch_intent = None;
        }
        let cleared_current = self.versions.current_source == Some(VersionSource::Local)
            && self
                .versions
                .current_path
                .as_deref()
                .is_some_and(|path| removed.iter().any(|item| item == path));
        if cleared_current {
            self.versions.current_source = None;
            self.versions.current_path = None;
            self.versions.current_version = None;
        }
        self.queue_local_save();
        self.push_global_notice(TransientNotice::warning(
            "notice.local_instances_removed",
            format_removed_instances(&removed, cleared_current),
        ));
    }

    fn add_local_instance(&mut self, instance: LocalInstance) -> bool {
        if self.versions.local_instances.iter().any(|other| {
            other.path == instance.path
                || (instance.identity.is_some() && other.identity == instance.identity)
        }) {
            return false;
        }
        let path = instance.path.clone();
        self.versions.local_instances.push(instance);
        self.queue_local_save();
        self.queue_dependency_check(path, false);
        true
    }

    fn queue_local_save(&mut self) {
        if !self.local_runtime.writable {
            return;
        }
        self.local_runtime.pending_save = Some(self.versions.local_instances.clone());
        self.start_local_save();
    }

    fn start_local_save(&mut self) {
        if self.local_runtime.saving {
            return;
        }
        let Some(instances) = self.local_runtime.pending_save.take() else {
            return;
        };
        self.local_runtime.saving = true;
        if !self.local_runtime.workers_enabled {
            return;
        }
        let tx = self.local_runtime.tx.clone();
        std::thread::spawn(move || {
            let result = service::save(&service::store_path(), &instances)
                .map_err(|error| LocalError::new("app.local_instances.save_failed", error));
            let _ = tx.send(Event::Saved(result));
        });
    }

    fn queue_dependency_check(&mut self, path: String, switch: bool) {
        let Some(instance) = self
            .versions
            .local_instances
            .iter_mut()
            .find(|instance| instance.path == path)
        else {
            return;
        };
        if instance.dependencies == DependencyStatus::Installing
            || self.local_runtime.check_ids.contains_key(&path)
        {
            return;
        }
        instance.dependencies = DependencyStatus::Checking;
        self.local_runtime.serial += 1;
        let id = self.local_runtime.serial;
        self.local_runtime.check_ids.insert(path.clone(), id);
        if switch {
            self.local_runtime.switch_intent = Some((path.clone(), id));
        }
        self.local_runtime
            .checks
            .push_back(CheckRequest { path, id });
        self.start_local_checks();
    }

    fn start_local_checks(&mut self) {
        if !self.local_runtime.workers_enabled {
            return;
        }
        // 快速扫描可能同时发现很多实例，限制 npm 检测并发，不能每个目录无限开进程。
        while self.local_runtime.checking < 2 {
            let Some(request) = self.local_runtime.checks.pop_front() else {
                break;
            };
            if self.local_runtime.check_ids.get(&request.path) != Some(&request.id) {
                continue;
            }
            self.local_runtime.checking += 1;
            let tx = self.local_runtime.tx.clone();
            let cancel = self.local_runtime.shutdown.clone();
            let source = self.settings.env_mode;
            std::thread::spawn(move || {
                let result = dependencies::check(Path::new(&request.path), source, &cancel);
                let _ = tx.send(Event::Checked(request.path, request.id, result));
            });
        }
    }

    /// Node.js 安装完成后重新检查所有本地实例，使界面无需用户逐个点击重试。
    pub(super) fn recheck_all_local_dependencies(&mut self) {
        let paths: Vec<_> = self
            .versions
            .local_instances
            .iter()
            .filter(|instance| instance.dependencies != DependencyStatus::Installing)
            .map(|instance| instance.path.clone())
            .collect();
        for path in paths {
            self.queue_dependency_check(path, false);
        }
    }

    fn install_local_dependencies(&mut self, path: String) {
        if self.versions.install_task.running || self.versions.local.install.running {
            self.versions
                .local
                .notify("app.notice.install_running", "", true);
            return;
        }
        let Some(instance) = self
            .versions
            .local_instances
            .iter_mut()
            .find(|item| item.path == path)
        else {
            return;
        };
        if matches!(
            instance.dependencies,
            DependencyStatus::Checking | DependencyStatus::Installing
        ) {
            return;
        }
        // 从失败安装弹窗重试也必须属于同一实例；不允许伪造消息安装在线目录。
        instance.dependencies = DependencyStatus::Installing;
        self.local_runtime.check_ids.remove(&path);
        self.local_runtime.switch_intent = None;
        self.local_runtime.install_id += 1;
        let id = self.local_runtime.install_id;
        self.versions.local.install = LocalInstallState {
            visible: true,
            running: true,
            path: Some(path.clone()),
            ..LocalInstallState::default()
        };
        if !self.local_runtime.workers_enabled {
            return;
        }
        let registry = self.settings.npm_registry.url().to_owned();
        let mode = match self.settings.proxy_mode {
            crate::pages::settings::ProxyMode::None => "none",
            crate::pages::settings::ProxyMode::System => "system",
            crate::pages::settings::ProxyMode::Custom => "custom",
        }
        .to_owned();
        let host = self.settings.custom_proxy.clone();
        let source = self.settings.env_mode;
        let tx = self.local_runtime.tx.clone();
        let cancel = self.local_runtime.shutdown.clone();
        std::thread::spawn(move || {
            let result = dependencies::install(
                Path::new(&path),
                &registry,
                &mode,
                &host,
                source,
                &cancel,
                |line| {
                    let _ = tx.send(Event::InstallLog(id, line));
                },
            );
            let _ = tx.send(Event::Installed(id, path, result));
        });
    }

    pub(super) fn poll_local_instances(&mut self) {
        self.versions.local.tick();
        self.reap_scan_workers();
        // 每帧限制处理量，持续产生日志时仍给窗口事件留出处理机会。
        for _ in 0..128 {
            let Ok(event) = self.local_runtime.rx.try_recv() else {
                break;
            };
            match event {
                Event::Loaded(result) => {
                    self.versions.local.loading = false;
                    match result {
                        Ok(instances) => {
                            self.versions.local_instances = instances;
                            // 列表里的实例可能已经在启动器之外被删除或移动。
                            self.prune_missing_local_instances();
                            let paths: Vec<_> = self
                                .versions
                                .local_instances
                                .iter()
                                .map(|item| item.path.clone())
                                .collect();
                            for path in paths {
                                self.queue_dependency_check(path, false);
                            }
                        }
                        Err(error) => {
                            self.local_runtime.writable = false;
                            self.versions.local.report(&error);
                        }
                    }
                }
                Event::Imported(result) => {
                    self.versions.local.import_pending = false;
                    match result {
                        Ok(instance) => {
                            let added = self.add_local_instance(instance);
                            self.versions.local.notify(
                                if added {
                                    "app.local_instances.imported"
                                } else {
                                    "app.local_instances.duplicate"
                                },
                                "",
                                false,
                            );
                        }
                        Err(error) => self.versions.local.report(&error),
                    }
                }
                Event::Scan(id, event) => {
                    if id != self.local_runtime.scan_id
                        || self.versions.local.scan.phase != ScanPhase::Running
                    {
                        continue;
                    }
                    match event {
                        ScanEvent::Progress(mut progress) => {
                            if let Some(started) = self.versions.local.scan.started_at {
                                progress.elapsed_seconds = started.elapsed().as_secs();
                            }
                            self.versions.local.scan.progress = progress;
                        }
                        ScanEvent::ScanningPath(path) => {
                            self.versions.local.scan.observe_path(path.clone());
                            append_log(&mut self.versions.local.scan.logs, path);
                        }
                        ScanEvent::Found(instance) => {
                            if self.add_local_instance(instance) {
                                self.versions.local.scan.added += 1;
                            } else {
                                self.versions.local.scan.duplicates += 1;
                            }
                        }
                        ScanEvent::Warning(error) => {
                            append_log(&mut self.versions.local.scan.logs, error.message.into());
                            append_log(&mut self.versions.local.scan.logs, error.detail);
                        }
                        ScanEvent::Finished(result) => match result {
                            Ok(mut report) => {
                                if let Some(started) = self.versions.local.scan.started_at {
                                    report.progress.elapsed_seconds = started.elapsed().as_secs();
                                }
                                self.versions.local.scan.progress = report.progress;
                                self.versions.local.scan.cancel_confirm_visible = false;
                                self.versions.local.scan.phase = if report.partial {
                                    ScanPhase::CompletedPartial
                                } else {
                                    ScanPhase::Completed
                                };
                                // 沿用旧版成功后 3 秒收起进度提示，但保留日志供再次打开。
                                self.versions.local.scan.auto_hide_at = (!report.partial
                                    && !self.versions.local.scan.show_details)
                                    .then(|| {
                                        std::time::Instant::now()
                                            + std::time::Duration::from_secs(3)
                                    });
                                self.versions.local.notify(
                                    if report.partial {
                                        "app.local_instances.scan_partial"
                                    } else {
                                        "app.local_instances.scan_done"
                                    },
                                    "",
                                    report.partial,
                                );
                                // 扫描是用户主动刷新列表的时机，顺手清理已经被删除的实例。
                                self.prune_missing_local_instances();
                            }
                            Err(error) => self.handle_scan_failure(error),
                        },
                    }
                }
                Event::Checked(path, id, result) => {
                    self.local_runtime.checking = self.local_runtime.checking.saturating_sub(1);
                    if self.local_runtime.check_ids.get(&path) != Some(&id) {
                        continue;
                    }
                    self.local_runtime.check_ids.remove(&path);
                    let switch =
                        self.local_runtime.switch_intent.as_ref() == Some(&(path.clone(), id));
                    if switch {
                        self.local_runtime.switch_intent = None;
                    }
                    if let Some(item) = self
                        .versions
                        .local_instances
                        .iter_mut()
                        .find(|item| item.path == path)
                    {
                        match result {
                            Ok(status) => {
                                let ready = status == DependencyStatus::Ready;
                                item.dependencies = status;
                                if switch && ready {
                                    self.versions.current_source = Some(VersionSource::Local);
                                    self.versions.current_path = Some(path.clone());
                                    self.versions.current_version = Some(item.version.clone());
                                    self.versions
                                        .local
                                        .notify("versions.local.switched", &path, false);
                                } else if switch {
                                    self.versions.local.notify(
                                        "app.local_instances.deps_incomplete",
                                        &path,
                                        true,
                                    );
                                }
                            }
                            Err(error) => {
                                if error.kind == LocalErrorKind::MissingNodeJs {
                                    // Node.js 缺失属于可恢复的环境问题：不显示底层 ENOENT，
                                    // 改为全局安装引导，并避免多个实例重复弹出提示。
                                    item.dependencies =
                                        DependencyStatus::Failed(error.message.to_owned());
                                    self.versions.local.toast = None;
                                    if !self.settings.environment_task.running {
                                        self.nodejs_required_visible = true;
                                    }
                                } else {
                                    item.dependencies = DependencyStatus::Failed(format!(
                                        "{}\n{}",
                                        error.message, error.detail
                                    ));
                                    self.versions.local.report(&error);
                                }
                            }
                        }
                    }
                }
                Event::InstallLog(id, line) if id == self.local_runtime.install_id => {
                    append_log(&mut self.versions.local.install.logs, line);
                }
                Event::Installed(id, path, result) if id == self.local_runtime.install_id => {
                    self.versions.local.install.running = false;
                    let status = match result {
                        Ok(status) => {
                            self.versions.local.notify(
                                "local.install.completed",
                                "",
                                false,
                            );
                            status
                        }
                        Err(error) => {
                            append_log(&mut self.versions.local.install.logs, error.detail.clone());
                            self.versions.local.install.error = Some(error.clone());
                            self.versions.local.report(&error);
                            if matches!(
                                error.message,
                                "local.deps.install_failed"
                                    | "local.deps.incomplete_after_install"
                            ) {
                                DependencyStatus::Incomplete
                            } else {
                                DependencyStatus::Failed(error.detail)
                            }
                        }
                    };
                    if let Some(item) = self
                        .versions
                        .local_instances
                        .iter_mut()
                        .find(|item| item.path == path)
                    {
                        item.dependencies = status;
                    }
                }
                Event::Saved(result) => {
                    self.local_runtime.saving = false;
                    if let Err(error) = result {
                        self.versions.local.report(&error);
                    }
                    self.start_local_save();
                }
                Event::InstallLog(_, _) | Event::Installed(_, _, _) => {}
            }
        }
        self.start_local_checks();
    }
}

/// 失效实例的提醒文案。
///
/// 整句走键值表；路径是运行时数据，只作为占位符实参传入。
/// 列表分隔符也走键，避免英文界面出现中文顿号。
fn format_removed_instances(paths: &[String], cleared_current: bool) -> String {
    // Toast 宽度固定，路径太多时只列前几条。
    const MAX_LISTED: usize = 3;
    let listed = paths
        .iter()
        .take(MAX_LISTED)
        .cloned()
        .collect::<Vec<_>>()
        .join(crate::lang::t("common.list_separator"));
    let more = if paths.len() > MAX_LISTED {
        tf("app.local_instances.removed_more", &[("count", &paths.len())])
    } else {
        String::new()
    };
    let mut text = tf(
        "app.local_instances.removed_title",
        &[("listed", &listed), ("more", &more)],
    );
    if cleared_current {
        text.push_str(t("app.local_instances.current_cleared"));
    }
    text
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::core::settings::SettingsStore;
    use crate::pages::{Page, versions::VersionState};

    /// 构造无后台副作用的应用，避免测试触发启动时的在线目录加载。
    fn launcher() -> Launcher {
        let path = std::env::temp_dir().join(format!(
            "astra-local-state-{}-{:?}.json",
            std::process::id(),
            std::thread::current().id()
        ));
        Launcher {
            config_runtime: super::super::tavern_config::ConfigRuntime::default(),
            local_runtime: LocalRuntime::default(),
            screen: super::super::Screen::Main,
            page: Page::Home,
            stage: super::super::InitStage::Welcome,
            progress: 0.0,
            last_tick: None,
            settings: Default::default(),
            font_catalog: Default::default(),
            active_font: crate::core::typography::FontChoice::default_choice(),
            loaded_fonts: Default::default(),
            font_load_request_id: 0,
            font_load_pending: 0,
            font_load_failed: false,
            environment_detect_receiver: None,
            window_size: iced::Size::new(1280.0, 720.0),
            main_window_id: None,
            monitor_relocated: false,
            environment_task_receiver: None,
            environment_task_cancel: None,
            nodejs_required_visible: false,
            github_test_receiver: None,
            github_test_id: 0,
            github_test_cancel: None,
            download_channel_test_receiver: None,
            download_channel_test_cancel: None,
            version_catalog_receiver: None,
            version_catalog_notify: false,
            version_install_receiver: None,
            version_install_cancel: None,
            update_receiver: None,
            extension_task_receiver: None,
            extension_task_cancel: None,
            tavern: Default::default(),
            versions: VersionState::default(),
            extensions: Default::default(),
            resources: Default::default(),
            console: Default::default(),
            global_notices: Default::default(),
            global_notice_serial: 0,
            pending_console_launch: false,
            #[cfg(target_os = "windows")]
            desktop_webview: None,
            #[cfg(target_os = "windows")]
            desktop_webview_suppressed: false,
            #[cfg(target_os = "windows")]
            desktop_webview_ready: false,
            #[cfg(target_os = "windows")]
            desktop_webview_retry_count: 0,
            #[cfg(target_os = "windows")]
            desktop_webview_retry_at: None,
            #[cfg(target_os = "windows")]
            desktop_webview_load_deadline: None,
            settings_store: SettingsStore::load(path).0,
            window_position: None,
            system_theme: iced::theme::Mode::Light,
            window_ready: false,
            home_version_selector_open: false,
        }
    }
    fn instance(path: &str, dependencies: DependencyStatus) -> LocalInstance {
        LocalInstance {
            path: path.to_owned(),
            version: "1".into(),
            dependencies,
            identity: None,
        }
    }
    fn send(app: &mut Launcher, event: Event) {
        app.local_runtime.tx.send(event).unwrap();
        app.poll_local_instances();
    }
    /// 建立带 package.json 的临时实例目录，用于区分「存在」与「已删除」。
    fn present_instance_dir() -> PathBuf {
        let root = std::env::temp_dir().join(format!(
            "astra-local-prune-{}-{:?}",
            std::process::id(),
            std::thread::current().id()
        ));
        std::fs::create_dir_all(&root).unwrap();
        std::fs::write(root.join("package.json"), r#"{"name":"sillytavern"}"#).unwrap();
        root
    }

    #[test]
    fn missing_instances_are_pruned_and_reported() {
        let mut app = launcher();
        let present = present_instance_dir();
        app.versions.local_instances = vec![
            instance("/astrabrew-test-missing-instance", DependencyStatus::Ready),
            instance(&present.to_string_lossy(), DependencyStatus::Ready),
        ];
        app.prune_missing_local_instances();
        assert_eq!(
            app.versions.local_instances.len(),
            1,
            "不存在的实例应被移除"
        );
        assert_eq!(
            app.versions.local_instances[0].path,
            present.to_string_lossy()
        );
        assert_eq!(app.global_notices.len(), 1, "移除失效实例后应弹出全局提醒");
        let _ = std::fs::remove_dir_all(&present);
    }

    #[test]
    fn pruning_the_current_instance_clears_the_selection() {
        let mut app = launcher();
        let missing = "/astrabrew-test-missing-current";
        app.versions.local_instances = vec![instance(missing, DependencyStatus::Ready)];
        app.versions.current_source = Some(VersionSource::Local);
        app.versions.current_path = Some(missing.into());
        app.versions.current_version = Some("1".into());
        app.prune_missing_local_instances();
        assert!(app.versions.local_instances.is_empty());
        assert!(
            app.versions.current_path.is_none(),
            "当前实例消失后应取消选择"
        );
        assert!(app.versions.current_source.is_none());
        assert!(app.global_notices.len() == 1);
    }

    #[test]
    fn installing_instances_are_never_pruned() {
        let mut app = launcher();
        app.versions.local_instances = vec![instance(
            "/astrabrew-test-installing",
            DependencyStatus::Installing,
        )];
        app.prune_missing_local_instances();
        assert_eq!(
            app.versions.local_instances.len(),
            1,
            "安装中的实例不应被移除"
        );
        assert!(app.global_notices.is_empty());
    }

    #[test]
    fn cancelled_picker_and_invalid_import_do_not_add_instances() {
        let mut app = launcher();
        app.versions.local.import_pending = true;
        app.import_local_file(None);
        assert!(!app.versions.local.import_pending);
        assert!(app.versions.local.toast.is_none());
        send(
            &mut app,
            Event::Imported(Err(LocalError::new("选择的不是酒馆实例，请重新选择。", ""))),
        );
        assert!(app.versions.local_instances.is_empty());
        assert!(app.versions.local.toast.as_ref().unwrap().danger);
    }
    #[test]
    fn loaded_failure_preserves_original_store_and_disables_overwrites() {
        let mut app = launcher();
        send(
            &mut app,
            Event::Loaded(Err(LocalError::new("无法加载本地实例列表。", "fixture"))),
        );
        assert!(!app.local_runtime.writable);
        app.queue_local_save();
        assert!(!app.local_runtime.saving);
    }
    #[test]
    fn quick_scan_starts_directly_and_keeps_running_when_dialog_closes() {
        let mut app = launcher();
        app.handle_local_message(&VersionMessage::ScanLocal);
        let id = app.local_runtime.scan_id;
        assert_eq!(app.versions.local.scan.phase, ScanPhase::Running);
        app.handle_local_message(&VersionMessage::CloseScanLog);
        app.page = Page::Home;
        send(
            &mut app,
            Event::Scan(
                id,
                ScanEvent::Found(instance("/fixture/one", DependencyStatus::Checking)),
            ),
        );
        assert!(!app.versions.local.scan.visible);
        assert_eq!(app.versions.local.scan.added, 1);
        app.handle_local_message(&VersionMessage::ScanLocal);
        assert_eq!(app.local_runtime.scan_id, id);
        assert!(app.versions.local.scan.visible);
        send(
            &mut app,
            Event::Scan(id, ScanEvent::Finished(Ok(Default::default()))),
        );
        assert!(app.versions.local.scan.auto_hide_at.is_some());
        app.versions.update(VersionMessage::OpenScanLog);
        assert!(app.versions.local.scan.show_details);
        assert!(app.versions.local.scan.auto_hide_at.is_none());
    }

    #[test]
    fn legacy_cancel_confirmation_does_not_cancel_until_confirmed() {
        let mut app = launcher();
        app.handle_local_message(&VersionMessage::ScanLocal);
        let id = app.local_runtime.scan_id;
        app.handle_version_message(VersionMessage::RequestCancelScan);
        assert!(app.versions.local.scan.cancel_confirm_visible);
        assert!(!app.local_runtime.scan_cancel.load(Ordering::Relaxed));
        app.handle_version_message(VersionMessage::KeepScanning);
        assert!(!app.versions.local.scan.cancel_confirm_visible);
        assert_eq!(app.versions.local.scan.phase, ScanPhase::Running);
        app.handle_version_message(VersionMessage::RequestCancelScan);
        app.handle_local_message(&VersionMessage::CancelScan);
        assert_eq!(app.versions.local.scan.phase, ScanPhase::Cancelled);
        assert!(!app.versions.local.scan.cancel_confirm_visible);
        send(
            &mut app,
            Event::Scan(
                id,
                ScanEvent::Found(instance("/fixture/late", DependencyStatus::Checking)),
            ),
        );
        assert!(app.versions.local_instances.is_empty());
    }

    #[test]
    fn scan_keeps_only_five_recent_paths_and_preserves_detailed_log() {
        let mut app = launcher();
        app.handle_local_message(&VersionMessage::ScanLocal);
        let id = app.local_runtime.scan_id;
        for i in 0..8 {
            send(
                &mut app,
                Event::Scan(id, ScanEvent::ScanningPath(format!("/fixture/{i}"))),
            );
        }
        assert_eq!(app.versions.local.scan.recent_paths.len(), 5);
        assert_eq!(app.versions.local.scan.logs.len(), 8);
        send(
            &mut app,
            Event::Scan(
                id,
                ScanEvent::Finished(Ok(crate::core::local_instances::scan::ScanReport {
                    partial: true,
                    ..Default::default()
                })),
            ),
        );
        assert_eq!(app.versions.local.scan.phase, ScanPhase::CompletedPartial);
        assert!(app.versions.local.scan.auto_hide_at.is_none());
    }

    #[test]
    fn failed_scan_does_not_start_another_scan_automatically() {
        let mut app = launcher();
        app.handle_local_message(&VersionMessage::ScanLocal);
        let id = app.local_runtime.scan_id;
        send(
            &mut app,
            Event::Scan(
                id,
                ScanEvent::Finished(Err(LocalError::new("fixture error", "find failed"))),
            ),
        );
        assert_eq!(app.versions.local.scan.phase, ScanPhase::Failed);
        assert_eq!(app.local_runtime.scan_id, id);
        app.handle_local_message(&VersionMessage::ScanLocal);
        assert!(app.local_runtime.scan_id > id);
        assert_eq!(app.versions.local.scan.phase, ScanPhase::Running);
    }

    #[test]
    fn cancellation_unblocks_full_event_channels() {
        let (tx, _rx) = mpsc::sync_channel(1);
        tx.send(Event::Scan(0, ScanEvent::Progress(Default::default())))
            .unwrap();
        let cancel = Arc::new(AtomicBool::new(false));
        let flag = cancel.clone();
        let thread = std::thread::spawn(move || {
            send_scan(
                &tx,
                Event::Scan(0, ScanEvent::Progress(Default::default())),
                &flag,
            )
        });
        cancel.store(true, Ordering::Relaxed);
        assert!(!thread.join().unwrap());
    }

    #[test]
    fn simultaneous_import_and_scan_deduplicate_identity() {
        let mut app = launcher();
        let mut first = instance("/fixture/one", DependencyStatus::Checking);
        // Windows 没有 inode/dev，目录身份用规范化路径表示。
        first.identity = Some(PathBuf::from("/fixture/real"));
        let mut alias = first.clone();
        alias.path = "/fixture/alias".into();
        send(&mut app, Event::Imported(Ok(first)));
        app.versions.local.scan.phase = ScanPhase::Running;
        send(&mut app, Event::Scan(0, ScanEvent::Found(alias)));
        assert_eq!(app.versions.local_instances.len(), 1);
        assert_eq!(app.versions.local.scan.duplicates, 1);
    }
    #[test]
    fn dependency_result_does_not_switch_without_explicit_request() {
        let mut app = launcher();
        app.versions
            .local_instances
            .push(instance("/fixture/one", DependencyStatus::Ready));
        app.queue_dependency_check("/fixture/one".into(), false);
        let id = app.local_runtime.check_ids["/fixture/one"];
        send(
            &mut app,
            Event::Checked("/fixture/one".into(), id, Ok(DependencyStatus::Ready)),
        );
        assert!(app.versions.current_path.is_none());
        app.queue_dependency_check("/fixture/one".into(), true);
        let id = app.local_runtime.check_ids["/fixture/one"];
        send(
            &mut app,
            Event::Checked("/fixture/one".into(), id, Ok(DependencyStatus::Incomplete)),
        );
        assert!(app.versions.current_path.is_none());
        app.queue_dependency_check("/fixture/one".into(), true);
        let id = app.local_runtime.check_ids["/fixture/one"];
        send(
            &mut app,
            Event::Checked("/fixture/one".into(), id, Ok(DependencyStatus::Ready)),
        );
        assert_eq!(app.versions.current_source, Some(VersionSource::Local));
    }
    #[test]
    fn missing_nodejs_uses_install_prompt_instead_of_raw_spawn_error() {
        let mut app = launcher();
        let path = "/fixture/one";
        app.versions
            .local_instances
            .push(instance(path, DependencyStatus::Checking));
        app.local_runtime.check_ids.insert(path.into(), 1);
        app.local_runtime.checking = 1;

        send(
            &mut app,
            Event::Checked(
                path.into(),
                1,
                Err(LocalError::new("environment.nodejs_required.error", "")
                    .with_kind(LocalErrorKind::MissingNodeJs)),
            ),
        );

        assert!(app.nodejs_required_visible);
        assert!(app.versions.local.toast.is_none());
        assert!(matches!(
            &app.versions.local_instances[0].dependencies,
            DependencyStatus::Failed(detail) if !detail.contains("No such file or directory")
        ));

        let _ = app.update_inner(Message::InstallRequiredNodeJs);
        assert_eq!(app.page, Page::Settings);
        assert!(!app.nodejs_required_visible);
    }
    #[test]
    fn install_is_exclusive_and_success_does_not_switch() {
        let mut app = launcher();
        app.versions
            .local_instances
            .push(instance("/fixture/one", DependencyStatus::Incomplete));
        app.install_local_dependencies("/fixture/one".into());
        let id = app.local_runtime.install_id;
        app.install_local_dependencies("/fixture/one".into());
        assert_eq!(app.local_runtime.install_id, id);
        app.handle_version_message(VersionMessage::InstallOnline("1".into()));
        assert!(!app.versions.install_task.running);
        app.handle_local_message(&VersionMessage::RemoveLocal("/fixture/one".into()));
        assert_eq!(app.versions.local_instances.len(), 1);
        send(
            &mut app,
            Event::Installed(id, "/fixture/one".into(), Ok(DependencyStatus::Ready)),
        );
        assert_eq!(
            app.versions.local_instances[0].dependencies,
            DependencyStatus::Ready
        );
        assert!(!app.versions.local.install.running);
        assert!(app.versions.current_path.is_none());
    }
    #[test]
    fn failed_post_install_check_never_marks_ready() {
        let mut app = launcher();
        app.versions
            .local_instances
            .push(instance("/fixture/one", DependencyStatus::Incomplete));
        app.install_local_dependencies("/fixture/one".into());
        let id = app.local_runtime.install_id;
        send(
            &mut app,
            Event::Installed(
                id,
                "/fixture/one".into(),
                Err(LocalError::new(
                    "安装已结束，但运行依赖仍不完整。",
                    "fixture",
                )),
            ),
        );
        assert_eq!(
            app.versions.local_instances[0].dependencies,
            DependencyStatus::Incomplete
        );
        assert!(app.versions.local.install.error.is_some());
        assert!(app.versions.current_path.is_none());
    }
    #[test]
    fn obsolete_dependency_results_cannot_overwrite_reimported_instance() {
        let mut app = launcher();
        app.versions
            .local_instances
            .push(instance("/fixture/one", DependencyStatus::Checking));
        app.local_runtime.check_ids.insert("/fixture/one".into(), 2);
        send(
            &mut app,
            Event::Checked("/fixture/one".into(), 1, Ok(DependencyStatus::Ready)),
        );
        assert_eq!(
            app.versions.local_instances[0].dependencies,
            DependencyStatus::Checking
        );
    }
    #[test]
    fn save_failure_is_reported_without_discarding_memory_state() {
        let mut app = launcher();
        app.versions.local.scan.phase = ScanPhase::Running;
        app.add_local_instance(instance("/fixture/one", DependencyStatus::Checking));
        send(
            &mut app,
            Event::Saved(Err(LocalError::new("保存本地实例列表失败。", "fixture"))),
        );
        assert_eq!(app.versions.local_instances.len(), 1);
        assert!(app.versions.local.toast.as_ref().unwrap().danger);
        assert_eq!(app.versions.local.scan.phase, ScanPhase::Running);
    }
    #[test]
    fn switching_back_to_stable_uses_disk_snapshot_even_when_ui_flag_is_stale() {
        let mut app = launcher();
        let version = app.versions.online_releases[0].version.clone();
        let snapshot = crate::core::network::InstalledSillyTavern {
            tag_name: Some(app.versions.online_releases[0].tag_name.clone()),
            branch: None,
            head: "fixture".into(),
        };
        let mut local = instance("/fixture/local", DependencyStatus::Ready);
        local.version = version.clone();
        app.versions.local_instances.push(local);
        for _ in 0..2 {
            app.versions
                .update(VersionMessage::SwitchLocal("/fixture/local".into()));
            assert_eq!(app.versions.current_source, Some(VersionSource::Local));
            app.versions.online_releases[0].installed = false;
            app.switch_online_version(version.clone(), Some(&snapshot));
            assert_eq!(app.versions.current_source, Some(VersionSource::Online));
            assert_eq!(
                app.versions.current_version.as_deref(),
                Some(version.as_str())
            );
            assert_eq!(app.versions.current_path, app.versions.online_instance_path);
            assert_ne!(app.versions.current_path.as_deref(), Some("/fixture/local"));
            assert!(app.versions.is_online_installed(&version));
            assert!(!app.versions.install_task.running);
            assert!(app.version_install_receiver.is_none());
            assert_eq!(app.versions.local_instances.len(), 1);
        }
    }

    #[test]
    fn switching_to_unknown_online_version_cannot_silently_succeed_on_empty_tags() {
        let mut app = launcher();
        app.versions.current_source = Some(VersionSource::Local);
        app.versions.current_path = Some("/fixture/local".into());
        app.switch_online_version("missing-fixture-version".into(), None);
        assert_eq!(app.versions.current_source, Some(VersionSource::Local));
        assert_eq!(app.versions.current_path.as_deref(), Some("/fixture/local"));
        assert!(app.versions.local.toast.as_ref().unwrap().danger);
        assert!(!app.versions.install_task.running);
        assert!(app.version_install_receiver.is_none());
    }
}
