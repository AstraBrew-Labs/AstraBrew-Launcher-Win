//! 酒馆配置的响应式协调器：草稿按目标隔离，所有磁盘任务串行，旧回执不覆盖新输入。

use crate::lang::t;
use super::{Launcher, Message};
use crate::core::tavern_config::{
    self as service, ConfigError, Context, ErrorKind, ImportPreview, ImportResult, NetworkOptions,
    Patch, SaveResult, Snapshot, Values, WhitelistPolicy, WhitelistServiceMode, fixed_whitelist,
    is_reserved_whitelist_ip, merge_whitelist, normalize_whitelist, schema,
};
use crate::pages::{
    Page,
    settings::{ProxyMode, ServerServiceMode, TavernDataMode},
    tavern::{
        TavernAction, TavernMessage, TavernState,
        sync::{ImportPrompt, Status, SyncView},
    },
    versions::VersionSource,
};
use iced::{Task, window};
use serde_json::Value;
use std::collections::BTreeMap;
use std::path::PathBuf;
use std::sync::mpsc::{self, Receiver, Sender};
use std::time::{Duration, Instant};

#[derive(Clone)]
struct Edit {
    ui: Value,
    base: Option<Value>,
    revision: u64,
    due: Instant,
    conflict: bool,
}
struct Session {
    context: Context,
    snapshot: Option<Snapshot>,
    draft: Values,
    edits: BTreeMap<String, Edit>,
    state: Status,
    error: Option<ConfigError>,
    write_failed: bool,
    saved: bool,
    next_read: Instant,
    whitelist_policy: Option<WhitelistPolicy>,
}
impl Session {
    fn new(context: Context, defaults: &Values) -> Self {
        Self {
            context,
            snapshot: None,
            draft: defaults.clone(),
            edits: BTreeMap::new(),
            state: Status::Loading,
            error: None,
            write_failed: false,
            saved: false,
            next_read: Instant::now(),
            whitelist_policy: None,
        }
    }
    fn accept(&mut self, snapshot: Snapshot, applied: &[(String, u64)]) {
        for (key, revision) in applied {
            if self
                .edits
                .get(key)
                .is_some_and(|edit| edit.revision == *revision)
            {
                self.edits.remove(key);
            } else if let Some(edit) = self.edits.get_mut(key) {
                // 自己上一笔保存的结果是新编辑的基线，不应被误报成外部冲突。
                edit.base = snapshot.raw.get(key).cloned().flatten();
            }
        }
        let retargeted = self
            .snapshot
            .as_ref()
            .is_some_and(|old| old.physical_path != snapshot.physical_path);
        self.draft = snapshot.values.clone();
        for (key, edit) in &mut self.edits {
            let disk = snapshot.raw.get(key).cloned().flatten();
            let desired = schema::field(key).and_then(|field| schema::encode(field, &edit.ui).ok());
            edit.conflict = retargeted || (disk != edit.base && disk != desired);
            self.draft.insert(key.clone(), edit.ui.clone());
        }
        self.snapshot = Some(snapshot);
        self.state = Status::Ready;
        if !self.write_failed {
            self.error = None;
        }
        self.next_read = Instant::now() + Duration::from_millis(500);
    }
    fn valid_patches(&self, now: Instant) -> Vec<Patch> {
        if self.state != Status::Ready || self.write_failed {
            return Vec::new();
        }
        self.edits
            .iter()
            .filter_map(|(key, edit)| {
                if edit.conflict || edit.due > now {
                    return None;
                }
                let value =
                    schema::field(key).and_then(|field| schema::encode(field, &edit.ui).ok())?;
                Some(Patch {
                    key: key.clone(),
                    expected: edit.base.clone(),
                    value,
                    revision: edit.revision,
                })
            })
            .collect()
    }
    fn invalid_or_blocked(&self) -> bool {
        !self.edits.is_empty()
            && (self.write_failed
                || self.state != Status::Ready
                || self.edits.iter().any(|(key, edit)| {
                    edit.conflict
                        || schema::field(key)
                            .is_none_or(|field| schema::encode(field, &edit.ui).is_err())
                }))
    }
    fn flush(&mut self) {
        for edit in self.edits.values_mut() {
            edit.due = Instant::now();
        }
    }

    fn value_list(value: Option<&Value>) -> Vec<String> {
        value
            .and_then(Value::as_array)
            .map(|items| {
                items
                    .iter()
                    .filter_map(Value::as_str)
                    .map(str::to_owned)
                    .collect()
            })
            .unwrap_or_default()
    }

    /// 系统白名单修复与用户编辑分开合并，服务模式变化不会丢失用户地址。
    fn apply_whitelist_policy(&mut self, policy: WhitelistPolicy, serial: &mut u64) {
        if self.state != Status::Ready {
            self.whitelist_policy = Some(policy);
            return;
        }
        let Some(snapshot) = self.snapshot.as_ref() else {
            self.whitelist_policy = Some(policy);
            return;
        };
        let policy_changed = self.whitelist_policy.replace(policy) != Some(policy);
        let disk = Self::value_list(snapshot.values.get("whitelist"));
        let disk_raw = snapshot.raw.get("whitelist").cloned().flatten();
        let local = Self::value_list(self.draft.get("whitelist"));
        let (normalized, existing_needs_refresh) = if let Some(edit) = self.edits.get("whitelist") {
            let normalized = if edit.base == disk_raw && !edit.conflict {
                // 没有外部并发修改时沿用旧版顺序，只替换系统保留段。
                normalize_whitelist(&local, policy)
            } else {
                let base = Self::value_list(edit.base.as_ref());
                merge_whitelist(&base, &local, &disk, policy)
            };
            let needs = edit.conflict
                || edit.base != disk_raw
                || edit.ui != Value::Array(normalized.iter().cloned().map(Value::String).collect())
                || policy_changed;
            (normalized, needs)
        } else {
            (normalize_whitelist(&disk, policy), false)
        };
        let normalized_value =
            Value::Array(normalized.iter().cloned().map(Value::String).collect());
        // 键被外部完整删除时，decode 会给出界面默认值；必须比较原始磁盘值才能发现缺失。
        let needs_repair = disk_raw.as_ref() != Some(&normalized_value);
        if needs_repair || existing_needs_refresh {
            let already_queued = self
                .edits
                .get("whitelist")
                .is_some_and(|edit| !existing_needs_refresh && edit.ui == normalized_value);
            if already_queued {
                self.draft.insert("whitelist".into(), normalized_value);
                return;
            }
            *serial = serial.wrapping_add(1);
            self.edits.insert(
                "whitelist".into(),
                Edit {
                    ui: normalized_value.clone(),
                    base: disk_raw,
                    revision: *serial,
                    due: Instant::now(),
                    conflict: false,
                },
            );
            self.draft.insert("whitelist".into(), normalized_value);
            self.write_failed = false;
        } else if self.edits.get("whitelist").is_none() {
            self.draft.insert("whitelist".into(), normalized_value);
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum JobKind {
    Load,
    Save,
    Generate,
    PrepareImport,
    Import,
    Reveal,
}
struct Running {
    id: u64,
    key: String,
    kind: JobKind,
}
enum Action {
    Generate,
    Prepare(PathBuf),
    Commit(ImportPreview),
    Reveal,
}
enum ResultData {
    Load(Result<Snapshot, ConfigError>),
    Save(Result<SaveResult, ConfigError>),
    Generated(Result<Snapshot, ConfigError>),
    Preview(Result<ImportPreview, ConfigError>),
    Imported(Result<ImportResult, ConfigError>),
    Revealed(Result<(), ConfigError>),
}
enum Event {
    Done(u64, String, ResultData),
    Progress(u64, String, u64, Option<u64>),
}

pub(super) struct ConfigRuntime {
    active: Option<String>,
    sessions: BTreeMap<String, Session>,
    defaults: Values,
    tx: Sender<Event>,
    rx: Receiver<Event>,
    busy: Option<Running>,
    action: Option<(String, Action)>,
    preview: Option<ImportPreview>,
    preview_changed: bool,
    picker_key: Option<String>,
    serial: u64,
    download: (u64, Option<u64>),
    close_window: Option<window::Id>,
    discard_close: bool,
    close_ready: bool,
    workers_enabled: bool,
}
impl Default for ConfigRuntime {
    fn default() -> Self {
        let (tx, rx) = mpsc::channel();
        Self {
            active: None,
            sessions: BTreeMap::new(),
            defaults: TavernState::default().values(),
            tx,
            rx,
            busy: None,
            action: None,
            preview: None,
            preview_changed: false,
            picker_key: None,
            serial: 0,
            download: (0, None),
            close_window: None,
            discard_close: false,
            close_ready: false,
            workers_enabled: !cfg!(test),
        }
    }
}

impl Launcher {
    pub(super) fn config_needs_tick(&self) -> bool {
        self.config_runtime.active.is_some()
            || self.config_runtime.busy.is_some()
            || self.config_runtime.close_window.is_some()
            || self
                .config_runtime
                .sessions
                .values()
                .any(|session| !session.edits.is_empty())
    }

    /// 启动酒馆前刷新并保存当前配置。
    ///
    /// 返回 `Ok(true)` 表示配置已经稳定可读；`Ok(false)` 表示保存任务已排队，
    /// 完成后由 `poll_tavern_config` 自动继续启动。
    pub(super) fn prepare_config_for_launch(&mut self) -> Result<bool, String> {
        let Some(key) = self.config_runtime.active.clone() else {
            return Err(t("tavern.config.no_instance").to_owned());
        };
        if self.config_runtime.busy.is_some() {
            return Ok(false);
        }
        let Some(session) = self.config_runtime.sessions.get_mut(&key) else {
            return Err(t("tavern.config.not_loaded").to_owned());
        };
        if session.invalid_or_blocked() {
            return Err(session.error.as_ref().map_or_else(
                || t("tavern.config.invalid_input").to_owned(),
                |error| format!("{} {}", error.message, error.detail),
            ));
        }
        if session.state == Status::Loading {
            return Ok(false);
        }
        if session.state != Status::Ready || session.snapshot.is_none() {
            return Err(t("tavern.config.not_loaded_retry").to_owned());
        }
        if !session.edits.is_empty() {
            session.flush();
            self.schedule_config_job();
            return Ok(false);
        }
        Ok(true)
    }

    /// 检查待启动配置是否已经完成保存。
    fn config_ready_for_pending_launch(&self) -> Result<bool, String> {
        let Some(key) = self.config_runtime.active.as_ref() else {
            return Err(t("tavern.config.no_instance").to_owned());
        };
        if self.config_runtime.busy.is_some() {
            return Ok(false);
        }
        let Some(session) = self.config_runtime.sessions.get(key) else {
            return Err(t("tavern.config.not_loaded").to_owned());
        };
        if session.invalid_or_blocked() || session.write_failed {
            return Err(session.error.as_ref().map_or_else(
                || t("tavern.config.save_conflict").to_owned(),
                |error| format!("{} {}", error.message, error.detail),
            ));
        }
        Ok(session.state == Status::Ready && session.edits.is_empty())
    }
    fn current_whitelist_policy(&self) -> WhitelistPolicy {
        WhitelistPolicy {
            server_enabled: self.settings.server_mode_enabled,
            service_mode: match self.settings.server_service_mode {
                ServerServiceMode::Lan => WhitelistServiceMode::Lan,
                ServerServiceMode::Internet => WhitelistServiceMode::Internet,
            },
        }
    }

    pub(super) fn reconcile_tavern_config(&mut self) {
        let whitelist_policy = self.current_whitelist_policy();
        let source = match self.versions.current_source {
            Some(VersionSource::Local) => "local",
            Some(VersionSource::Online) => "online",
            None => "",
        };
        let context = Context::resolve(
            self.versions
                .current_path
                .as_deref()
                .filter(|_| !source.is_empty()),
            source,
            self.settings.data_mode == TavernDataMode::Global,
            &self.settings.global_data_path,
        );
        let key = context.as_ref().map(|context| context.key.clone());
        if self.config_runtime.active != key {
            if let Some(old) = self
                .config_runtime
                .active
                .as_ref()
                .and_then(|key| self.config_runtime.sessions.get_mut(key))
            {
                old.flush();
            }
            self.config_runtime.active = key;
            // 选择目标后才能确认导入，旧上下文的未确认导入不能跟随到新实例。
            self.config_runtime.preview = None;
            self.config_runtime.picker_key = None;
            if let Some(context) = context {
                let session = self
                    .config_runtime
                    .sessions
                    .entry(context.key.clone())
                    .or_insert_with(|| Session::new(context, &self.config_runtime.defaults));
                session.next_read = Instant::now();
            }
        }
        if let Some(session) = self
            .config_runtime
            .active
            .as_ref()
            .and_then(|key| self.config_runtime.sessions.get_mut(key))
        {
            session.apply_whitelist_policy(whitelist_policy, &mut self.config_runtime.serial);
        }
        self.schedule_config_job();
        self.refresh_config_view();
    }
    pub(super) fn request_config_refresh(&mut self) {
        if let Some(session) = self
            .config_runtime
            .active
            .as_ref()
            .and_then(|key| self.config_runtime.sessions.get_mut(key))
        {
            session.next_read = Instant::now();
        }
    }

    fn config_network(&self) -> NetworkOptions {
        NetworkOptions {
            proxy_mode: match self.settings.proxy_mode {
                ProxyMode::None => "none",
                ProxyMode::System => "system",
                ProxyMode::Custom => "custom",
            }
            .into(),
            proxy_host: self.settings.custom_proxy.clone(),
            github_proxy: self
                .settings
                .github_proxy_enabled
                .then(|| self.settings.github_proxy_url.clone())
                .filter(|v| !v.trim().is_empty()),
        }
    }
    fn refresh_config_view(&mut self) {
        let close_prompt = self.config_runtime.close_window.is_some()
            && !self.config_runtime.discard_close
            && (self
                .config_runtime
                .sessions
                .values()
                .any(Session::invalid_or_blocked)
                || self.config_runtime.preview.is_some());
        let pending_targets: Vec<_> = self
            .config_runtime
            .sessions
            .values()
            .filter(|session| !session.edits.is_empty())
            .map(|session| session.context.path.clone())
            .collect();
        let Some(key) = &self.config_runtime.active else {
            self.tavern.sync = SyncView {
                close_prompt,
                pending_targets,
                fixed_whitelist: fixed_whitelist(self.current_whitelist_policy()),
                ..Default::default()
            };
            return;
        };
        let Some(session) = self.config_runtime.sessions.get(key) else {
            return;
        };
        let busy = self
            .config_runtime
            .busy
            .as_ref()
            .filter(|job| &job.key == key)
            .map(|job| job.kind);
        let pending_action = self
            .config_runtime
            .action
            .as_ref()
            .filter(|(target, _)| target == key)
            .map(|(_, action)| action);
        let mut status = session.state;
        let conflicts: Vec<_> = session
            .edits
            .iter()
            .filter(|(_, edit)| edit.conflict)
            .map(|(key, _)| key.clone())
            .collect();
        if status == Status::Ready {
            status = if !conflicts.is_empty() {
                Status::Conflict
            } else if session.edits.iter().any(|(key, edit)| {
                schema::field(key).is_some_and(|field| schema::encode(field, &edit.ui).is_err())
            }) {
                Status::Validation
            } else if session.write_failed {
                Status::SaveFailed
            } else if busy == Some(JobKind::Save) {
                Status::Saving
            } else if !session.edits.is_empty() {
                Status::Pending
            } else if session.saved {
                Status::Saved
            } else {
                Status::Ready
            };
        }
        if busy == Some(JobKind::Generate) || matches!(pending_action, Some(Action::Generate)) {
            status = Status::Generating;
        }
        if matches!(busy, Some(JobKind::PrepareImport | JobKind::Import))
            || matches!(pending_action, Some(Action::Prepare(_) | Action::Commit(_)))
            || self.config_runtime.picker_key.as_ref() == Some(key)
        {
            status = Status::Importing;
        }
        let import = self
            .config_runtime
            .preview
            .as_ref()
            .filter(|preview| &preview.context.key == key && status != Status::Importing)
            .map(|preview| ImportPrompt {
                source: preview.source.clone(),
                target: preview.context.path.clone(),
                changed: self.config_runtime.preview_changed,
            });
        self.tavern.sync = SyncView {
            status,
            target: Some(session.context.path.clone()),
            error: session.error.clone(),
            import,
            conflicts,
            downloaded: self.config_runtime.download.0,
            total: self.config_runtime.download.1,
            close_prompt,
            pending_targets,
            fixed_whitelist: fixed_whitelist(self.current_whitelist_policy()),
        };
        if let Err(error) = self.tavern.apply_values(session.draft.clone()) {
            self.tavern.sync.status = Status::Invalid;
            self.tavern.sync.error = Some(error);
        }
    }

    fn field_changed(message: &TavernMessage) -> Option<(&'static str, bool)> {
        Some(match message {
            TavernMessage::Toggle(field, _) => (field.key(), true),
            TavernMessage::Edit(field, _) => (field.key(), false),
            TavernMessage::EditList(field, _, _) | TavernMessage::AddListItem(field) => {
                (field.key(), false)
            }
            TavernMessage::RemoveListItem(field, _) => (field.key(), true),
            TavernMessage::SelectBrowser(_) => ("browser_type", true),
            TavernMessage::SelectThumbnailFormat(_) => ("thumbnail_format", true),
            TavernMessage::SelectLogLevel(_) => ("min_log_level", true),
            _ => return None,
        })
    }
    pub(super) fn handle_tavern_config_message(&mut self, message: TavernMessage) -> Task<Message> {
        if matches!(message, TavernMessage::ContinueEditing) {
            self.config_runtime.close_window = None;
            self.config_runtime.discard_close = false;
            return Task::none();
        }
        if matches!(message, TavernMessage::DiscardAndClose) {
            self.config_runtime.discard_close = true;
            self.config_runtime.preview = None;
            self.config_runtime.action = None;
            for session in self.config_runtime.sessions.values_mut() {
                session.edits.clear();
            }
            return Task::none();
        }
        if matches!(message, TavernMessage::GoToVersions) {
            self.page = Page::Version;
            return Task::none();
        }
        if matches!(
            message,
            TavernMessage::ToggleAdvancedSection(_) | TavernMessage::ConfigOverlayInteract
        ) {
            self.tavern.update(message);
            return Task::none();
        }
        let Some(key) = self.config_runtime.active.clone() else {
            self.versions.local.notify("tavern.config.select_instance", "", true);
            return Task::none();
        };
        match &message {
            TavernMessage::EditList(crate::pages::tavern::ListField::Whitelist, index, value) => {
                let current = self.tavern.whitelist().get(*index).map(String::as_str);
                if current.is_some_and(is_reserved_whitelist_ip) || is_reserved_whitelist_ip(value)
                {
                    self.versions
                        .local
                        .notify("tavern.config.managed_address", value, true);
                    return Task::none();
                }
            }
            TavernMessage::RemoveListItem(crate::pages::tavern::ListField::Whitelist, index) => {
                if self
                    .tavern
                    .whitelist()
                    .get(*index)
                    .is_some_and(|value| is_reserved_whitelist_ip(value))
                {
                    self.versions
                        .local
                        .notify("tavern.config.reserved_whitelist", "", true);
                    return Task::none();
                }
            }
            _ => {}
        }
        if let Some((field, immediate)) = Self::field_changed(&message) {
            let Some(session) = self.config_runtime.sessions.get(&key) else {
                return Task::none();
            };
            if session.state != Status::Ready
                || self.config_runtime.preview.is_some()
                || self.config_runtime.action.is_some()
                || self.config_runtime.picker_key.is_some()
                || self.config_runtime.busy.as_ref().is_some_and(|job| {
                    job.key == key
                        && matches!(
                            job.kind,
                            JobKind::Generate | JobKind::PrepareImport | JobKind::Import
                        )
                })
            {
                self.versions
                    .local
                    .notify("tavern.config.not_ready", "", true);
                return Task::none();
            }
            self.tavern.update(message);
            let values = self.tavern.values();
            let Some(ui) = values.get(field).cloned() else {
                return Task::none();
            };
            let Some(session) = self.config_runtime.sessions.get_mut(&key) else {
                return Task::none();
            };
            if session.draft.get(field) == Some(&ui) {
                return Task::none();
            }
            self.config_runtime.serial += 1;
            let revision = self.config_runtime.serial;
            let base = session
                .edits
                .get(field)
                .map(|edit| edit.base.clone())
                .unwrap_or_else(|| {
                    session
                        .snapshot
                        .as_ref()
                        .and_then(|snapshot| snapshot.raw.get(field).cloned().flatten())
                });
            let conflict = session.edits.get(field).is_some_and(|edit| edit.conflict);
            session.draft = values;
            session.edits.insert(
                field.into(),
                Edit {
                    ui,
                    base,
                    revision,
                    due: Instant::now()
                        + if immediate {
                            Duration::ZERO
                        } else {
                            Duration::from_millis(300)
                        },
                    conflict,
                },
            );
            session.write_failed = false;
            session.error = None;
            return Task::none();
        }
        match message {
            TavernMessage::Action(TavernAction::ImportConfig) => {
                if self.config_runtime.busy.as_ref().is_some_and(|job| {
                    matches!(
                        job.kind,
                        JobKind::Generate | JobKind::PrepareImport | JobKind::Import
                    )
                }) || self.config_runtime.preview.is_some()
                    || self.config_runtime.action.is_some()
                    || self.config_runtime.picker_key.is_some()
                {
                    return Task::none();
                }
                if self
                    .config_runtime
                    .sessions
                    .get(&key)
                    .is_none_or(|s| s.state != Status::Ready)
                {
                    self.versions
                        .local
                        .notify("tavern.config.not_ready", "", true);
                    return Task::none();
                }
                self.config_runtime.picker_key = Some(key.clone());
                let title = crate::lang::t("tavern.action.import_config_file");
                return Task::perform(
                    async move {
                        let file = rfd::AsyncFileDialog::new()
                            .set_title(title)
                            .add_filter("YAML", &["yaml", "yml"])
                            .pick_file()
                            .await;
                        (key, file.map(|file| file.path().to_owned()))
                    },
                    |(key, path)| Message::ConfigImportChosen(key, path),
                );
            }
            TavernMessage::Action(TavernAction::OpenConfigFile) => {
                if self.config_runtime.action.is_none() {
                    if let Some(session) = self.config_runtime.sessions.get_mut(&key) {
                        session.flush();
                    }
                    self.config_runtime.action = Some((key, Action::Reveal));
                }
            }
            TavernMessage::GenerateConfig => {
                if !self.config_runtime.busy.as_ref().is_some_and(|job| {
                    matches!(
                        job.kind,
                        JobKind::Generate | JobKind::PrepareImport | JobKind::Import
                    )
                }) && self.config_runtime.action.is_none()
                    && self
                        .config_runtime
                        .sessions
                        .get(&key)
                        .is_some_and(|s| s.state == Status::Missing)
                {
                    if let Some(session) = self.config_runtime.sessions.get_mut(&key) {
                        session.error = None;
                    }
                    self.config_runtime.download = (0, None);
                    self.config_runtime.action = Some((key, Action::Generate));
                }
            }
            TavernMessage::RetryConfig => {
                if let Some(session) = self.config_runtime.sessions.get_mut(&key) {
                    session.write_failed = false;
                    session.error = None;
                    session.next_read = Instant::now();
                    session.flush();
                }
            }
            TavernMessage::CancelImport => {
                self.config_runtime.preview = None;
                self.config_runtime.preview_changed = false;
            }
            TavernMessage::ConfirmImport => {
                if self.config_runtime.busy.is_none()
                    && self.config_runtime.action.is_none()
                    && let Some(preview) = self
                        .config_runtime
                        .preview
                        .as_ref()
                        .filter(|p| p.context.key == key)
                        .cloned()
                {
                    self.config_runtime.action = Some((key, Action::Commit(preview)));
                }
            }
            TavernMessage::UseDiskValues | TavernMessage::KeepDraftValues => {
                if let Some(session) = self.config_runtime.sessions.get_mut(&key) {
                    let conflicts: Vec<_> = session
                        .edits
                        .iter()
                        .filter(|(_, edit)| edit.conflict)
                        .map(|(key, _)| key.clone())
                        .collect();
                    for field in conflicts {
                        if matches!(message, TavernMessage::UseDiskValues) {
                            session.edits.remove(&field);
                            if let Some(value) =
                                session.snapshot.as_ref().and_then(|s| s.values.get(&field))
                            {
                                session.draft.insert(field, value.clone());
                            }
                        } else if let Some(edit) = session.edits.get_mut(&field) {
                            edit.base = session
                                .snapshot
                                .as_ref()
                                .and_then(|s| s.raw.get(&field).cloned().flatten());
                            edit.conflict = false;
                            edit.due = Instant::now();
                            self.config_runtime.serial += 1;
                            edit.revision = self.config_runtime.serial;
                        }
                    }
                }
            }
            _ => {}
        }
        Task::none()
    }

    pub(super) fn config_import_chosen(&mut self, key: String, path: Option<PathBuf>) {
        if self.config_runtime.picker_key.as_deref() != Some(key.as_str()) {
            return;
        }
        self.config_runtime.picker_key = None;
        let Some(path) = path else {
            return;
        };
        if self.config_runtime.active.as_ref() != Some(&key) {
            return;
        }
        if let Some(session) = self.config_runtime.sessions.get_mut(&key) {
            session.flush();
        }
        self.config_runtime.action = Some((key, Action::Prepare(path)));
        self.config_runtime.download = (0, None);
    }

    fn schedule_config_job(&mut self) {
        if self.config_runtime.busy.is_some() || self.config_runtime.discard_close {
            return;
        }
        let now = Instant::now();
        // 冻结待确认导入的目标，防止自己的自动保存不断使确认内容过期。
        let frozen = self
            .config_runtime
            .preview
            .as_ref()
            .map(|preview| preview.context.key.as_str());
        let save = self
            .config_runtime
            .sessions
            .iter()
            .find_map(|(key, session)| {
                if frozen == Some(key.as_str()) {
                    return None;
                }
                let patches = session.valid_patches(now);
                if patches.is_empty() {
                    None
                } else {
                    session.snapshot.as_ref().map(|snapshot| {
                        (
                            key.clone(),
                            session.context.clone(),
                            snapshot.physical_path.clone(),
                            patches,
                        )
                    })
                }
            });
        if let Some((key, context, physical, patches)) = save {
            self.launch_config_job(key, JobKind::Save, move |defaults, _, _, _| {
                ResultData::Save(service::save(&context, &physical, &patches, defaults))
            });
            return;
        }
        if let Some((key, action)) = self.config_runtime.action.take() {
            let whitelist_policy = self.current_whitelist_policy();
            let Some(context) = self
                .config_runtime
                .sessions
                .get(&key)
                .map(|s| s.context.clone())
            else {
                return;
            };
            match action {
                Action::Generate => self.launch_config_job(
                    key,
                    JobKind::Generate,
                    move |defaults, network, id, tx| {
                        ResultData::Generated(service::generate(
                            &context,
                            network,
                            defaults,
                            whitelist_policy,
                            &mut |n, total| {
                                let _ = tx.send(Event::Progress(id, context.key.clone(), n, total));
                            },
                        ))
                    },
                ),
                Action::Prepare(source) => self.launch_config_job(
                    key,
                    JobKind::PrepareImport,
                    move |defaults, network, id, tx| {
                        ResultData::Preview(service::prepare_import(
                            &context,
                            &source,
                            network,
                            defaults,
                            whitelist_policy,
                            &mut |n, total| {
                                let _ = tx.send(Event::Progress(id, context.key.clone(), n, total));
                            },
                        ))
                    },
                ),
                Action::Commit(preview) => {
                    self.launch_config_job(key, JobKind::Import, move |defaults, _, _, _| {
                        ResultData::Imported(service::import(&preview, defaults, whitelist_policy))
                    })
                }
                Action::Reveal => {
                    self.launch_config_job(key, JobKind::Reveal, move |_, _, _, _| {
                        ResultData::Revealed(service::reveal(&context))
                    })
                }
            }
            return;
        }
        if self.config_runtime.close_window.is_some() {
            return;
        }
        if let Some(key) = self.config_runtime.active.clone()
            && frozen != Some(key.as_str())
            && let Some(session) = self.config_runtime.sessions.get(&key)
            && session.next_read <= now
        {
            let context = session.context.clone();
            self.launch_config_job(key, JobKind::Load, move |defaults, _, _, _| {
                ResultData::Load(service::load(&context, defaults))
            });
        }
    }
    fn launch_config_job(
        &mut self,
        key: String,
        kind: JobKind,
        work: impl FnOnce(&Values, &NetworkOptions, u64, &Sender<Event>) -> ResultData + Send + 'static,
    ) {
        self.config_runtime.serial += 1;
        let id = self.config_runtime.serial;
        self.config_runtime.busy = Some(Running {
            id,
            key: key.clone(),
            kind,
        });
        if !self.config_runtime.workers_enabled {
            return;
        }
        let defaults = self.config_runtime.defaults.clone();
        let network = self.config_network();
        let tx = self.config_runtime.tx.clone();
        std::thread::spawn(move || {
            // 第三方 YAML 编辑器异常也必须回传失败，不能让界面永久停留在“保存中”。
            let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
                work(&defaults, &network, id, &tx)
            }))
            .unwrap_or_else(|_| {
                let error = ConfigError::new(ErrorKind::Io, "tavern.config.task_failed", "");
                match kind {
                    JobKind::Load => ResultData::Load(Err(error)),
                    JobKind::Save => ResultData::Save(Err(error)),
                    JobKind::Generate => ResultData::Generated(Err(error)),
                    JobKind::PrepareImport => ResultData::Preview(Err(error)),
                    JobKind::Import => ResultData::Imported(Err(error)),
                    JobKind::Reveal => ResultData::Revealed(Err(error)),
                }
            });
            let _ = tx.send(Event::Done(id, key, result));
        });
    }
    pub(super) fn poll_tavern_config(&mut self) -> Task<Message> {
        for _ in 0..128 {
            let Ok(event) = self.config_runtime.rx.try_recv() else {
                break;
            };
            match event {
                Event::Progress(id, key, n, total) => {
                    if self
                        .config_runtime
                        .busy
                        .as_ref()
                        .is_some_and(|job| job.id == id && job.key == key)
                    {
                        self.config_runtime.download = (n, total);
                    }
                }
                Event::Done(id, key, result) => {
                    if !self
                        .config_runtime
                        .busy
                        .as_ref()
                        .is_some_and(|job| job.id == id && job.key == key)
                    {
                        continue;
                    }
                    self.config_runtime.busy = None;
                    let Some(session) = self.config_runtime.sessions.get_mut(&key) else {
                        continue;
                    };
                    match result {
                        ResultData::Load(Ok(snapshot)) => session.accept(snapshot, &[]),
                        ResultData::Load(Err(error)) => {
                            let state = match error.kind {
                                ErrorKind::Missing => Status::Missing,
                                ErrorKind::Invalid => Status::Invalid,
                                _ => Status::ReadFailed,
                            };
                            if session.state != state || session.error.is_none() {
                                session.error = Some(error);
                            }
                            session.state = state;
                            session.next_read = Instant::now() + Duration::from_millis(500);
                        }
                        ResultData::Save(Ok(saved)) => {
                            session.write_failed = false;
                            session.saved = true;
                            session.accept(saved.snapshot, &saved.applied);
                            for (key, revision) in saved.conflicts {
                                if let Some(edit) = session
                                    .edits
                                    .get_mut(&key)
                                    .filter(|edit| edit.revision == revision)
                                {
                                    edit.conflict = true;
                                }
                            }
                        }
                        ResultData::Save(Err(error)) => {
                            if error.kind == ErrorKind::Changed {
                                session.state = Status::Loading;
                                session.next_read = Instant::now();
                            } else {
                                session.write_failed = true;
                                if error.kind == ErrorKind::Missing {
                                    session.state = Status::Missing;
                                }
                                if error.kind == ErrorKind::Invalid {
                                    session.state = Status::Invalid;
                                }
                            }
                            session.error = Some(error);
                        }
                        ResultData::Generated(Ok(snapshot)) => {
                            // 外部删除文件后生成新文件时，保留该目标未完成草稿并走冲突检查。
                            session.write_failed = false;
                            session.saved = true;
                            session.accept(snapshot, &[]);
                            if self.config_runtime.active.as_ref() == Some(&key) {
                                self.versions.local.notify(
                                    "tavern.config.generated",
                                    "",
                                    false,
                                );
                            }
                        }
                        ResultData::Generated(Err(error)) => {
                            session.error = Some(error);
                            session.next_read = Instant::now() + Duration::from_secs(1);
                        }
                        ResultData::Preview(Ok(preview)) => {
                            if self.config_runtime.active.as_ref() == Some(&key) {
                                self.config_runtime.preview = Some(preview);
                            }
                        }
                        ResultData::Imported(Ok(ImportResult::Reconfirm(preview))) => {
                            if self.config_runtime.active.as_ref() == Some(&key) {
                                self.config_runtime.preview = Some(preview);
                                self.config_runtime.preview_changed = true;
                            }
                        }
                        ResultData::Imported(Ok(ImportResult::Saved(snapshot, backup))) => {
                            session.edits.clear();
                            session.write_failed = false;
                            session.saved = true;
                            session.accept(snapshot, &[]);
                            self.config_runtime.preview = None;
                            self.config_runtime.preview_changed = false;
                            if self.config_runtime.active.as_ref() == Some(&key) {
                                self.versions.local.notify(
                                    "tavern.config.imported",
                                    backup.display(),
                                    false,
                                );
                            }
                        }
                        ResultData::Imported(Err(error)) if error.kind == ErrorKind::Changed => {
                            if self.config_runtime.active.as_ref() == Some(&key)
                                && let Some(preview) = &self.config_runtime.preview
                            {
                                self.config_runtime.action =
                                    Some((key, Action::Prepare(preview.source.clone())));
                                self.config_runtime.preview_changed = true;
                            }
                        }
                        ResultData::Preview(Err(error)) | ResultData::Imported(Err(error)) => {
                            self.config_runtime.preview = None;
                            self.config_runtime.preview_changed = false;
                            session.error = Some(error.clone());
                            if self.config_runtime.active.as_ref() == Some(&key) {
                                self.versions
                                    .local
                                    .notify(error.message, error.detail, true);
                            }
                        }
                        ResultData::Revealed(Err(error)) => {
                            session.error = Some(error.clone());
                            if self.config_runtime.active.as_ref() == Some(&key) {
                                self.versions
                                    .local
                                    .notify(error.message, error.detail, true);
                            }
                        }
                        ResultData::Revealed(Ok(())) => {}
                    }
                }
            }
        }
        if self.pending_console_launch {
            match self.config_ready_for_pending_launch() {
                Ok(true) => {
                    self.pending_console_launch = false;
                    self.start_tavern_now();
                }
                Ok(false) => {}
                Err(error) => {
                    self.pending_console_launch = false;
                    self.console.add_error(error);
                }
            }
        }
        if let Some(id) = self.config_runtime.close_window {
            let dirty = self
                .config_runtime
                .sessions
                .values()
                .any(|session| !session.edits.is_empty());
            if self.config_runtime.busy.is_none()
                && (self.config_runtime.discard_close
                    || (!dirty && self.config_runtime.preview.is_none()))
            {
                self.config_runtime.close_ready = true;
                return Task::done(Message::WindowCloseRequested(id));
            }
        }
        Task::none()
    }

    /// 返回 true 时由配置协调器暂缓关闭，保存完成后重新发送关闭消息。
    pub(super) fn defer_config_close(&mut self, id: window::Id) -> bool {
        if self.config_runtime.close_ready {
            self.config_runtime.close_ready = false;
            self.config_runtime.close_window = None;
            return false;
        }
        let dirty = self
            .config_runtime
            .sessions
            .values()
            .any(|session| !session.edits.is_empty());
        if !dirty && self.config_runtime.busy.is_none() && self.config_runtime.preview.is_none() {
            return false;
        }
        self.config_runtime.close_window = Some(id);
        self.config_runtime.action = None;
        for session in self.config_runtime.sessions.values_mut() {
            session.flush();
        }
        true
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn context(name: &str) -> Context {
        Context {
            key: name.into(),
            path: PathBuf::from(format!("/fixture/{name}/config.yaml")),
            instance: PathBuf::from(format!("/fixture/{name}")),
        }
    }
    fn defaults() -> Values {
        TavernState::default().values()
    }
    fn session(name: &str) -> Session {
        let context = context(name);
        let mut session = Session::new(context.clone(), &defaults());
        session.accept(
            service::snapshot(
                "port: 8000\nlisten: false\n".into(),
                context.path,
                &defaults(),
            )
            .unwrap(),
            &[],
        );
        session
    }
    fn edit(session: &mut Session, key: &str, value: Value, revision: u64, due: Instant) {
        let base = session.snapshot.as_ref().unwrap().raw[key].clone();
        session.draft.insert(key.into(), value.clone());
        session.edits.insert(
            key.into(),
            Edit {
                ui: value,
                base,
                revision,
                due,
                conflict: false,
            },
        );
    }
    fn launcher() -> Launcher {
        use crate::core::settings::SettingsStore;
        let settings_path = std::env::temp_dir().join(format!(
            "astra-config-test-{}-{:?}.json",
            std::process::id(),
            std::thread::current().id()
        ));
        Launcher {
            config_runtime: ConfigRuntime::default(),
            local_runtime: super::super::local_instances::LocalRuntime::default(),
            screen: super::super::Screen::Main,
            page: Page::TavernConfig,
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
            versions: Default::default(),
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
            settings_store: SettingsStore::load(settings_path).0,
            window_position: None,
            system_theme: iced::theme::Mode::Light,
            window_ready: false,
            home_version_selector_open: false,
        }
    }
    fn attach(app: &mut Launcher, session: Session) -> String {
        let key = session.context.key.clone();
        app.config_runtime.sessions.insert(key.clone(), session);
        app.config_runtime.active = Some(key.clone());
        app.refresh_config_view();
        key
    }
    fn complete(app: &mut Launcher, key: &str, id: u64, result: ResultData) {
        app.config_runtime
            .tx
            .send(Event::Done(id, key.into(), result))
            .unwrap();
        let _ = app.poll_tavern_config();
        app.refresh_config_view();
    }

    #[test]
    fn debounce_and_valid_fields_do_not_wait_for_invalid_inputs() {
        let mut session = session("one");
        let now = Instant::now();
        edit(
            &mut session,
            "port",
            json!("9000"),
            1,
            now + Duration::from_millis(300),
        );
        assert!(
            session
                .valid_patches(now + Duration::from_millis(299))
                .is_empty()
        );
        assert_eq!(
            session
                .valid_patches(now + Duration::from_millis(300))
                .len(),
            1
        );
        edit(&mut session, "port", json!(""), 2, now);
        edit(&mut session, "listen", json!(true), 3, now);
        let patches = session.valid_patches(now);
        assert_eq!(patches.len(), 1);
        assert_eq!(patches[0].key, "listen");
        assert!(session.invalid_or_blocked());
        assert_eq!(session.draft["port"], "");
    }

    #[test]
    fn old_save_acknowledgement_keeps_newer_input_and_updates_its_base() {
        let mut session = session("one");
        edit(&mut session, "port", json!("9100"), 2, Instant::now());
        let snapshot = service::snapshot(
            "port: 9000\nlisten: false\n".into(),
            session.context.path.clone(),
            &defaults(),
        )
        .unwrap();
        session.accept(snapshot, &[("port".into(), 1)]);
        assert_eq!(session.draft["port"], "9100");
        assert_eq!(session.edits["port"].revision, 2);
        assert_eq!(session.edits["port"].base, Some(json!(9000)));
        assert!(!session.edits["port"].conflict);
        let saved = service::snapshot(
            "port: 9100\nlisten: false\n".into(),
            session.context.path.clone(),
            &defaults(),
        )
        .unwrap();
        session.accept(saved, &[("port".into(), 2)]);
        assert!(session.edits.is_empty());
    }

    #[test]
    fn external_updates_merge_clean_fields_but_keep_conflicting_draft() {
        let mut session = session("one");
        edit(&mut session, "port", json!("9000"), 1, Instant::now());
        let external = service::snapshot(
            "port: 9200\nlisten: true\n".into(),
            session.context.path.clone(),
            &defaults(),
        )
        .unwrap();
        session.accept(external, &[]);
        assert_eq!(session.draft["port"], "9000");
        assert_eq!(session.draft["listen"], true);
        assert!(session.edits["port"].conflict);
        assert!(session.valid_patches(Instant::now()).is_empty());
    }

    #[test]
    fn own_file_change_does_not_create_another_save() {
        let mut session = session("one");
        let current = session.snapshot.as_ref().unwrap().clone();
        session.accept(current, &[]);
        assert!(session.edits.is_empty());
        assert!(session.valid_patches(Instant::now()).is_empty());
    }

    #[test]
    fn target_switch_flushes_old_file_and_does_not_leak_its_result_into_new_form() {
        let mut app = launcher();
        let mut first = session("one");
        edit(
            &mut first,
            "port",
            json!("9000"),
            1,
            Instant::now() + Duration::from_secs(10),
        );
        first.flush();
        let old = first.context.path.clone();
        let key = attach(&mut app, first);
        app.schedule_config_job();
        let id = app.config_runtime.busy.as_ref().unwrap().id;
        assert_eq!(
            app.config_runtime.busy.as_ref().unwrap().kind,
            JobKind::Save
        );
        let second = session("two");
        attach(&mut app, second);
        let saved =
            service::snapshot("port: 9000\nlisten: false\n".into(), old, &defaults()).unwrap();
        complete(
            &mut app,
            &key,
            id,
            ResultData::Save(Ok(SaveResult {
                snapshot: saved,
                applied: vec![("port".into(), 1)],
                conflicts: Vec::new(),
            })),
        );
        assert_eq!(app.tavern.values()["port"], "8000");
        assert!(app.config_runtime.sessions[&key].edits.is_empty());
    }

    #[test]
    fn stale_job_results_are_ignored_and_missing_file_preserves_draft() {
        let mut app = launcher();
        let mut session = session("one");
        edit(&mut session, "port", json!(""), 2, Instant::now());
        let key = attach(&mut app, session);
        app.config_runtime.busy = Some(Running {
            id: 4,
            key: key.clone(),
            kind: JobKind::Load,
        });
        complete(
            &mut app,
            &key,
            3,
            ResultData::Load(Err(ConfigError::new(ErrorKind::Missing, "missing", ""))),
        );
        assert!(app.config_runtime.busy.is_some());
        complete(
            &mut app,
            &key,
            4,
            ResultData::Load(Err(ConfigError::new(ErrorKind::Missing, "missing", ""))),
        );
        assert_eq!(app.tavern.sync.status, Status::Missing);
        assert_eq!(app.tavern.values()["port"], "");
        assert!(!app.config_runtime.sessions[&key].edits.is_empty());
        assert!(
            app.config_runtime.sessions[&key]
                .valid_patches(Instant::now())
                .is_empty()
        );
    }

    #[test]
    fn input_messages_only_mutate_configuration_fields_and_correctly_schedule() {
        use crate::pages::tavern::{BoolField, TextField};
        let mut app = launcher();
        let key = attach(&mut app, session("one"));
        let _ = app.handle_tavern_config_message(TavernMessage::ToggleAdvancedSection(1));
        assert!(app.config_runtime.sessions[&key].edits.is_empty());
        let before = Instant::now();
        let _ =
            app.handle_tavern_config_message(TavernMessage::Edit(TextField::Port, "9000".into()));
        assert!(
            app.config_runtime.sessions[&key].edits["port"].due
                >= before + Duration::from_millis(300)
        );
        let _ = app.handle_tavern_config_message(TavernMessage::Toggle(BoolField::Listen, true));
        let due = app.config_runtime.sessions[&key].valid_patches(Instant::now());
        assert_eq!(due.len(), 1);
        assert_eq!(due[0].key, "listen");
    }

    #[test]
    fn failed_save_is_visible_and_explicit_retry_unblocks_it() {
        let mut app = launcher();
        let mut session = session("one");
        edit(&mut session, "port", json!("9000"), 1, Instant::now());
        let key = attach(&mut app, session);
        app.schedule_config_job();
        let id = app.config_runtime.busy.as_ref().unwrap().id;
        complete(
            &mut app,
            &key,
            id,
            ResultData::Save(Err(ConfigError::new(
                ErrorKind::Io,
                "write failed",
                "fixture",
            ))),
        );
        assert_eq!(app.tavern.sync.status, Status::SaveFailed);
        assert!(app.config_runtime.sessions[&key].invalid_or_blocked());
        let _ = app.handle_tavern_config_message(TavernMessage::RetryConfig);
        assert!(!app.config_runtime.sessions[&key].write_failed);
    }

    #[test]
    fn generation_is_explicit_and_duplicate_clicks_do_not_start_more_jobs() {
        let mut app = launcher();
        let mut session = session("one");
        session.state = Status::Missing;
        let key = attach(&mut app, session);
        assert!(app.config_runtime.action.is_none());
        let _ = app.handle_tavern_config_message(TavernMessage::GenerateConfig);
        assert!(app.config_runtime.action.is_some());
        app.schedule_config_job();
        let id = app.config_runtime.busy.as_ref().unwrap().id;
        let _ = app.handle_tavern_config_message(TavernMessage::GenerateConfig);
        assert!(app.config_runtime.action.is_none());
        assert_eq!(app.config_runtime.busy.as_ref().unwrap().id, id);
        let new = session_for_generation();
        attach(&mut app, new);
        let snapshot = service::snapshot(
            "port: 8000\nlisten: false\n".into(),
            PathBuf::from("/fixture/one/config.yaml"),
            &defaults(),
        )
        .unwrap();
        complete(&mut app, &key, id, ResultData::Generated(Ok(snapshot)));
        assert_eq!(app.config_runtime.active.as_deref(), Some("two"));
        assert_eq!(
            app.tavern.sync.target.as_deref(),
            Some(std::path::Path::new("/fixture/two/config.yaml"))
        );
    }
    fn session_for_generation() -> Session {
        session("two")
    }

    #[test]
    fn conflict_choices_do_not_silently_discard_local_input() {
        let mut app = launcher();
        let mut session = session("one");
        edit(&mut session, "port", json!("9000"), 1, Instant::now());
        let snapshot = service::snapshot(
            "port: 9200\nlisten: false\n".into(),
            session.context.path.clone(),
            &defaults(),
        )
        .unwrap();
        session.accept(snapshot, &[]);
        let key = attach(&mut app, session);
        let _ = app.handle_tavern_config_message(TavernMessage::KeepDraftValues);
        assert!(!app.config_runtime.sessions[&key].edits["port"].conflict);
        assert_eq!(
            app.config_runtime.sessions[&key].edits["port"].base,
            Some(json!(9200))
        );
        app.config_runtime
            .sessions
            .get_mut(&key)
            .unwrap()
            .edits
            .get_mut("port")
            .unwrap()
            .conflict = true;
        let _ = app.handle_tavern_config_message(TavernMessage::UseDiskValues);
        assert!(app.config_runtime.sessions[&key].edits.is_empty());
        assert_eq!(app.config_runtime.sessions[&key].draft["port"], "9200");
    }

    #[test]
    fn import_picker_cancellation_and_context_change_have_no_write_actions() {
        let mut app = launcher();
        let key = attach(&mut app, session("one"));
        app.config_runtime.picker_key = Some(key.clone());
        app.config_import_chosen(key.clone(), None);
        assert!(app.config_runtime.action.is_none());
        assert!(app.config_runtime.preview.is_none());
        app.config_runtime.picker_key = Some(key.clone());
        attach(&mut app, session("two"));
        app.config_import_chosen(key, Some(PathBuf::from("/fixture/source.yaml")));
        assert!(app.config_runtime.action.is_none());
    }
    #[test]
    fn closing_flushes_valid_changes_and_waits_for_their_acknowledgement() {
        let mut app = launcher();
        let mut session = session("one");
        edit(
            &mut session,
            "port",
            json!("9000"),
            1,
            Instant::now() + Duration::from_secs(10),
        );
        let key = attach(&mut app, session);
        let id = window::Id::unique();
        assert!(app.defer_config_close(id));
        app.schedule_config_job();
        assert_eq!(
            app.config_runtime.busy.as_ref().unwrap().kind,
            JobKind::Save
        );
        assert!(!app.config_runtime.close_ready);
        let job_id = app.config_runtime.busy.as_ref().unwrap().id;
        let snapshot = service::snapshot(
            "port: 9000\nlisten: false\n".into(),
            context("one").path,
            &defaults(),
        )
        .unwrap();
        complete(
            &mut app,
            &key,
            job_id,
            ResultData::Save(Ok(SaveResult {
                snapshot,
                applied: vec![("port".into(), 1)],
                conflicts: Vec::new(),
            })),
        );
        assert!(app.config_runtime.close_ready);
        assert!(!app.defer_config_close(id));
    }

    #[test]
    fn invalid_input_blocks_close_until_user_explicitly_discards_it() {
        let mut app = launcher();
        let mut session = session("one");
        edit(&mut session, "port", json!(""), 1, Instant::now());
        let key = attach(&mut app, session);
        let id = window::Id::unique();
        assert!(app.defer_config_close(id));
        app.refresh_config_view();
        assert!(app.tavern.sync.close_prompt);
        assert_eq!(app.tavern.sync.pending_targets, vec![context("one").path]);
        let _ = app.handle_tavern_config_message(TavernMessage::ContinueEditing);
        assert!(app.config_runtime.close_window.is_none());
        assert!(!app.config_runtime.sessions[&key].edits.is_empty());
        assert!(app.defer_config_close(id));
        let _ = app.handle_tavern_config_message(TavernMessage::DiscardAndClose);
        let _ = app.poll_tavern_config();
        assert!(app.config_runtime.close_ready);
        assert!(app.config_runtime.sessions[&key].edits.is_empty());
    }

    #[test]
    fn import_failure_preserves_draft_and_does_not_leave_frozen_preview() {
        let mut app = launcher();
        let mut session = session("one");
        edit(&mut session, "port", json!(""), 1, Instant::now());
        let key = attach(&mut app, session);
        app.config_runtime.busy = Some(Running {
            id: 4,
            key: key.clone(),
            kind: JobKind::Import,
        });
        complete(
            &mut app,
            &key,
            4,
            ResultData::Imported(Err(ConfigError::new(
                ErrorKind::Io,
                "backup failed",
                "fixture",
            ))),
        );
        assert_eq!(app.tavern.values()["port"], "");
        assert!(!app.config_runtime.sessions[&key].edits.is_empty());
        assert!(app.config_runtime.preview.is_none());
        assert!(app.versions.local.toast.as_ref().unwrap().danger);
    }

    #[test]
    fn template_download_failure_keeps_missing_overlay_and_allows_retry() {
        let mut app = launcher();
        let mut session = session("one");
        session.state = Status::Missing;
        let key = attach(&mut app, session);
        let _ = app.handle_tavern_config_message(TavernMessage::GenerateConfig);
        app.schedule_config_job();
        let id = app.config_runtime.busy.as_ref().unwrap().id;
        complete(
            &mut app,
            &key,
            id,
            ResultData::Generated(Err(ConfigError::new(
                ErrorKind::Template,
                "download failed",
                "fixture",
            ))),
        );
        assert_eq!(app.tavern.sync.status, Status::Missing);
        assert!(app.tavern.sync.error.is_some());
        let _ = app.handle_tavern_config_message(TavernMessage::GenerateConfig);
        assert!(matches!(
            app.config_runtime.action,
            Some((_, Action::Generate))
        ));
    }
    fn whitelist_policy(server: bool, mode: WhitelistServiceMode) -> WhitelistPolicy {
        WhitelistPolicy {
            server_enabled: server,
            service_mode: mode,
        }
    }

    #[test]
    fn external_deletion_of_fixed_ips_is_repaired_even_when_whitelist_is_disabled() {
        let context = context("whitelist-disabled");
        let mut session = Session::new(context.clone(), &defaults());
        session.accept(
            service::snapshot(
                "whitelistMode: false
whitelist: [203.0.113.9]
"
                .into(),
                context.path,
                &defaults(),
            )
            .unwrap(),
            &[],
        );
        let mut serial = 0;
        session.apply_whitelist_policy(
            whitelist_policy(false, WhitelistServiceMode::Lan),
            &mut serial,
        );
        assert_eq!(
            session.draft["whitelist"],
            json!(["203.0.113.9", "::1", "127.0.0.1"])
        );
        assert_eq!(session.valid_patches(Instant::now()).len(), 1);
        assert!(!session.edits["whitelist"].conflict);
    }

    #[test]
    fn service_mode_changes_only_replace_reserved_ranges() {
        let context = context("service-mode");
        let mut session = Session::new(context.clone(), &defaults());
        session.accept(
            service::snapshot(
                "whitelist: ['::1', '127.0.0.1', '10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16', '203.0.113.9']
".into(),
                context.path,
                &defaults(),
            )
            .unwrap(),
            &[],
        );
        let mut serial = 0;
        session.apply_whitelist_policy(
            whitelist_policy(true, WhitelistServiceMode::Lan),
            &mut serial,
        );
        assert!(session.edits.get("whitelist").is_none());
        session.apply_whitelist_policy(
            whitelist_policy(true, WhitelistServiceMode::Internet),
            &mut serial,
        );
        assert_eq!(
            session.draft["whitelist"],
            json!(["::1", "127.0.0.1", "203.0.113.9", "0.0.0.0/0", "::/0"])
        );
        session.apply_whitelist_policy(
            whitelist_policy(false, WhitelistServiceMode::Internet),
            &mut serial,
        );
        assert_eq!(
            session.draft["whitelist"],
            json!(["::1", "127.0.0.1", "203.0.113.9"])
        );
    }

    #[test]
    fn system_repair_merges_external_and_local_user_ips_without_conflict() {
        let context = context("whitelist-merge");
        let mut session = Session::new(context.clone(), &defaults());
        session.accept(
            service::snapshot(
                "whitelist: ['::1', '127.0.0.1', '198.51.100.1']
"
                .into(),
                context.path.clone(),
                &defaults(),
            )
            .unwrap(),
            &[],
        );
        edit(
            &mut session,
            "whitelist",
            json!(["::1", "127.0.0.1", "198.51.100.1", "203.0.113.1"]),
            1,
            Instant::now(),
        );
        session.accept(
            service::snapshot(
                "whitelist: ['127.0.0.1', '198.51.100.1', '203.0.113.2']
"
                .into(),
                context.path,
                &defaults(),
            )
            .unwrap(),
            &[],
        );
        assert!(session.edits["whitelist"].conflict);
        let mut serial = 1;
        session.apply_whitelist_policy(
            whitelist_policy(true, WhitelistServiceMode::Lan),
            &mut serial,
        );
        assert!(!session.edits["whitelist"].conflict);
        assert_eq!(
            session.draft["whitelist"],
            json!([
                "198.51.100.1",
                "203.0.113.1",
                "203.0.113.2",
                "::1",
                "127.0.0.1",
                "10.0.0.0/8",
                "172.16.0.0/12",
                "192.168.0.0/16"
            ])
        );
    }

    #[test]
    fn fixed_entries_and_reserved_custom_values_are_rejected_before_editing() {
        use crate::pages::tavern::ListField;
        let mut app = launcher();
        let mut session = session("locked-whitelist");
        session.draft.insert(
            "whitelist".into(),
            json!(["::1", "127.0.0.1", "203.0.113.9"]),
        );
        let key = attach(&mut app, session);
        let _ = app
            .handle_tavern_config_message(TavernMessage::RemoveListItem(ListField::Whitelist, 0));
        assert!(app.config_runtime.sessions[&key].edits.is_empty());
        assert!(app.versions.local.toast.as_ref().unwrap().danger);
        let _ = app.handle_tavern_config_message(TavernMessage::EditList(
            ListField::Whitelist,
            2,
            "10.0.0.0/8".into(),
        ));
        assert_eq!(app.tavern.whitelist()[2], "203.0.113.9");
        assert!(app.config_runtime.sessions[&key].edits.is_empty());
        let _ = app
            .handle_tavern_config_message(TavernMessage::RemoveListItem(ListField::Whitelist, 2));
        assert!(
            app.config_runtime.sessions[&key]
                .edits
                .contains_key("whitelist")
        );
        assert_eq!(app.tavern.whitelist(), ["::1", "127.0.0.1"]);
    }

    #[test]
    fn repeated_reconcile_does_not_churn_the_same_system_repair_revision() {
        let context = context("repair-once");
        let mut session = Session::new(context.clone(), &defaults());
        session.accept(
            service::snapshot(
                "whitelist: []
"
                .into(),
                context.path,
                &defaults(),
            )
            .unwrap(),
            &[],
        );
        let mut serial = 0;
        let policy = whitelist_policy(false, WhitelistServiceMode::Lan);
        session.apply_whitelist_policy(policy, &mut serial);
        let revision = session.edits["whitelist"].revision;
        session.apply_whitelist_policy(policy, &mut serial);
        assert_eq!(session.edits["whitelist"].revision, revision);
        assert_eq!(serial, revision);
    }
}
