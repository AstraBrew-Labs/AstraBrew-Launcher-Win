//! 扩展管理页面。
//!
//! 页面保持当前启动器的布局和 Astra UI 视觉风格，真实文件操作统一交给
//! `core::extensions`，避免视图层直接修改扩展目录。

use std::path::PathBuf;

use iced::widget::{
    button, column, container, mouse_area, pick_list, row, scrollable, space, stack,
    text_input, tooltip,
};
use iced::{Alignment, Background, Border, Color, Element, Fill, Font, Theme};
use lucide_icons::Icon;

use astra_ui::{
    BLUE_600, ButtonVariant, DANGER, INK_MUTED, INK_SUBTLE, ProgressBar, ProgressBarColor,
    ProgressBarSize, SUCCESS, SURFACE_ALT,
    WARNING, WHITE, icons,
};

use super::notice::TransientNotice;
use super::versions::VersionSource;
use crate::core::extensions::{
    ExtensionError, ExtensionEvent, ExtensionInfo, ExtensionKind, GitHealth,
    OfflinePackageInspection, OperationSuccess, repository_name,
};
use crate::lang::lang::current_language;
use crate::lang::{raw, t_in, text, tf};
use crate::theme::{button_style, pick_list_menu_style, pick_list_style, text_input_style};

const PURPLE: Color = Color::from_rgb8(142, 68, 220);

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum InstallTab {
    #[default]
    Git,
    Offline,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
enum LoadStatus {
    #[default]
    Idle,
    Loading,
    Ready,
    Error,
}

#[derive(Debug, Clone, Default)]
pub struct InstallDialogState {
    pub visible: bool,
    pub tab: InstallTab,
    pub git_url: String,
    pub branches: Vec<String>,
    pub selected_branch: Option<String>,
    pub branches_loading: bool,
    pub offline_packages: Vec<OfflinePackageInspection>,
    pub offline_checking: bool,
    pub running: bool,
    pub logs: Vec<String>,
    pub error: Option<ExtensionError>,
    pub completed: bool,
    /// 是否已经进入安装任务布局；失败后保留日志，方便用户查看。
    pub operation_started: bool,
    /// 是否展开安装日志。
    pub show_logs: bool,
    /// 安装任务开始时间，用于显示运行时长和不确定进度动画。
    started_at: Option<std::time::Instant>,
    /// 安装成功后的自动关闭截止时间。
    auto_close_due: Option<std::time::Instant>,
    /// 输入仓库地址后等待两秒的自动检测截止时间。
    git_detect_due: Option<std::time::Instant>,
}

#[derive(Debug, Clone)]
enum PendingConfirmation {
    Delete {
        path: PathBuf,
        name: String,
    },
    GitOverwrite,
    OfflineOverwrite,
    RepairGit {
        path: PathBuf,
        name: String,
        remote_url: String,
    },
}

#[derive(Debug, Clone)]
pub struct ExtensionsState {
    pub show_system_extensions: bool,
    pub extensions: Vec<ExtensionInfo>,
    pub install: InstallDialogState,
    pub notice: Option<TransientNotice>,
    pub target_path: Option<PathBuf>,
    pub target_version: Option<String>,
    pub target_source: Option<VersionSource>,
    status: LoadStatus,
    error: Option<ExtensionError>,
    task_running: bool,
    confirmation: Option<PendingConfirmation>,
    /// 本次扫描结束后是否展示「已刷新」提示。
    ///
    /// 进入扩展页、安装/删除等操作后的自动重扫都走这里置 false 的静默路径，
    /// 只有用户点击刷新按钮才会弹出提示，避免自动刷新和操作提示反复打断浏览。
    scan_notify: bool,
}

impl Default for ExtensionsState {
    fn default() -> Self {
        Self {
            show_system_extensions: false,
            extensions: Vec::new(),
            install: InstallDialogState::default(),
            notice: None,
            target_path: None,
            target_version: None,
            target_source: None,
            status: LoadStatus::Idle,
            error: None,
            task_running: false,
            confirmation: None,
            scan_notify: false,
        }
    }
}

#[derive(Debug, Clone)]
pub enum ExtensionsMessage {
    ToggleShowSystem(bool),
    Refresh,
    OpenInstall,
    CloseInstall,
    SelectInstallTab(usize),
    GitUrlChanged(String),
    /// 输入停止三秒后自动检测仓库分支；URL 参数用于丢弃过期定时事件。
    AutoDetectBranches(String),
    FetchBranches,
    SelectBranch(String),
    StartGitInstall,
    ChooseOfflineFiles,
    OfflineFilesChosen(Vec<PathBuf>),
    RemoveOfflinePackage(usize),
    StartOfflineInstall,
    ToggleInstallLogs,
    OpenExtensionRoot,
    OpenHomepage(String),
    OpenDirectory(String),
    ToggleEnabled(String, bool),
    RequestDelete(String),
    RequestGitRepair(String),
    ConfirmPending,
    CancelPending,
    NavigateVersion,
    ModalInteract,
}

#[derive(Debug, Clone)]
pub enum ExtensionAction {
    None,
    Refresh,
    /// 取消当前扩展后台任务并关闭安装弹窗。
    CancelTask,
    PickOfflineFiles,
    FetchBranches {
        repository_url: String,
    },
    InspectOffline(Vec<PathBuf>),
    InstallGit {
        repository_url: String,
        branch: String,
        overwrite: bool,
    },
    InstallOffline {
        packages: Vec<OfflinePackageInspection>,
        overwrite: bool,
    },
    SetEnabled {
        path: PathBuf,
        name: String,
        enabled: bool,
    },
    Delete {
        path: PathBuf,
        name: String,
    },
    RepairGit {
        path: PathBuf,
        name: String,
        remote_url: String,
    },
    OpenPath(PathBuf),
    OpenUrl(String),
    NavigateVersion,
}

impl ExtensionsState {
    /// 将扩展页面绑定到版本管理页当前选中的实例。
    pub fn bind_target(
        &mut self,
        path: Option<&str>,
        version: Option<&str>,
        source: Option<VersionSource>,
    ) -> bool {
        let next_path = path.map(PathBuf::from);
        let changed = self.target_path != next_path;
        if changed {
            self.target_path = next_path;
            self.target_version = version.map(str::to_owned);
            self.target_source = source;
            self.extensions.clear();
            self.status = LoadStatus::Idle;
            self.error = None;
            self.notice = None;
            self.scan_notify = false;
            self.install = InstallDialogState::default();
            self.confirmation = None;
        } else {
            self.target_version = version.map(str::to_owned);
            self.target_source = source;
        }
        changed
    }

    /// 标记一次扩展扫描开始。
    ///
    /// `notify` 决定扫描结束后是否展示「已刷新」提示：只有用户主动刷新才传 true，
    /// 进入页面、操作完成后的自动重扫一律静默。
    pub fn begin_scan(&mut self, notify: bool) {
        if self.target_path.is_some() {
            self.status = LoadStatus::Loading;
            self.error = None;
            self.task_running = true;
            self.scan_notify = notify;
        }
    }

    /// 标记非安装类磁盘修改任务正在运行。
    pub fn begin_mutation(&mut self) {
        self.task_running = true;
    }

    pub fn set_blocked_notice(&mut self) {
        self.notice = Some(TransientNotice::warning(
            "notice.action_unavailable",
            tr("extensions.notice.stop_required"),
        ));
    }

    /// 当前是否有待执行的 Git 仓库自动检测。
    pub fn auto_detect_pending(&self) -> bool {
        self.install.visible && self.install.tab == InstallTab::Git && self.install.git_detect_due.is_some()
    }

    /// 当前是否有待执行的安装弹窗自动关闭。
    pub fn auto_close_pending(&self) -> bool {
        self.install.visible && self.install.auto_close_due.is_some()
    }

    /// 取出已经到期的安装弹窗自动关闭信号。
    pub fn take_auto_close(&mut self) -> bool {
        let Some(due) = self.install.auto_close_due else {
            return false;
        };
        if std::time::Instant::now() < due {
            return false;
        }
        self.install.auto_close_due = None;
        true
    }

    /// 取出已经到期的自动检测 URL。后台任务未结束时保留截止时间，
    /// 确保用户在检测期间修改地址后，旧任务完成后仍会检测最新地址。
    pub fn take_auto_detect_url(&mut self) -> Option<String> {
        if self.task_running {
            return None;
        }
        let due = self.install.git_detect_due?;
        if std::time::Instant::now() < due {
            return None;
        }
        self.install.git_detect_due = None;
        if self.install.visible && self.install.tab == InstallTab::Git {
            let url = self.install.git_url.trim().to_owned();
            if !url.is_empty() {
                return Some(url);
            }
        }
        None
    }

    pub fn set_action_error(&mut self, error: ExtensionError) {
        self.task_running = false;
        self.install.running = false;
        self.install.branches_loading = false;
        self.install.offline_checking = false;
        if self.install.visible {
            self.install.error = Some(error.clone());
        } else {
            self.notice = Some(TransientNotice::danger(
                "notice.operation_failed",
                format_error(&error),
            ));
        }
    }

    /// 清空一次安装流程的全部临时状态，避免下次打开弹窗沿用旧内容。
    fn reset_install_dialog(&mut self) {
        self.install = InstallDialogState::default();
        self.task_running = false;
    }

    pub fn update(&mut self, message: ExtensionsMessage) -> ExtensionAction {
        match message {
            ExtensionsMessage::ToggleShowSystem(enabled) => {
                self.show_system_extensions = enabled;
                ExtensionAction::None
            }
            ExtensionsMessage::Refresh => {
                if self.target_path.is_some() && !self.task_running {
                    self.begin_scan(true);
                    ExtensionAction::Refresh
                } else {
                    ExtensionAction::None
                }
            }
            ExtensionsMessage::OpenInstall => {
                if self.target_path.is_some() && !self.task_running {
                    // 每次重新打开都创建全新的安装会话，不保留上一次的 URL、分支、日志和离线包。
                    self.reset_install_dialog();
                    self.install.visible = true;
                }
                ExtensionAction::None
            }
            ExtensionsMessage::CloseInstall => {
                if self.install.running {
                    // 正式安装期间仍保持弹窗，避免半安装目录被误关闭。
                    ExtensionAction::None
                } else if self.install.branches_loading || self.install.offline_checking || self.task_running {
                    // 检测任务可能正在等待网络，关闭按钮必须立即解除界面阻塞。
                    self.reset_install_dialog();
                    ExtensionAction::CancelTask
                } else {
                    self.reset_install_dialog();
                    ExtensionAction::None
                }
            }
            ExtensionsMessage::SelectInstallTab(index) => {
                if !self.install.running {
                    self.install.tab = if index == 0 { InstallTab::Git } else { InstallTab::Offline };
                }
                ExtensionAction::None
            }
            ExtensionsMessage::GitUrlChanged(value) => {
                if !self.install.running {
                    self.install.git_url = value;
                    self.install.branches.clear();
                    self.install.selected_branch = None;
                    self.install.error = None;
                    self.install.completed = false;
                    self.install.auto_close_due = None;
                    self.install.git_detect_due = if self.install.git_url.trim().is_empty() {
                        None
                    } else {
                        Some(std::time::Instant::now() + std::time::Duration::from_secs(2))
                    };
                }
                ExtensionAction::None
            }
            ExtensionsMessage::AutoDetectBranches(url) => {
                if self.install.git_url.trim() != url || self.install.tab != InstallTab::Git {
                    return ExtensionAction::None;
                }
                self.start_branch_detection(url)
            }
            ExtensionsMessage::FetchBranches => {
                let url = self.install.git_url.trim().to_owned();
                self.start_branch_detection(url)
            }
            ExtensionsMessage::SelectBranch(branch) => {
                self.install.selected_branch = Some(branch);
                ExtensionAction::None
            }
            ExtensionsMessage::StartGitInstall => self.request_git_install(false),
            ExtensionsMessage::ChooseOfflineFiles => {
                if !self.task_running && !self.install.running {
                    ExtensionAction::PickOfflineFiles
                } else {
                    ExtensionAction::None
                }
            }
            ExtensionsMessage::OfflineFilesChosen(paths) => {
                if paths.is_empty() {
                    return ExtensionAction::None;
                }
                self.task_running = true;
                self.install.offline_checking = true;
                self.install.error = None;
                self.install.completed = false;
                ExtensionAction::InspectOffline(paths)
            }
            ExtensionsMessage::RemoveOfflinePackage(index) => {
                if !self.install.running && index < self.install.offline_packages.len() {
                    self.install.offline_packages.remove(index);
                }
                ExtensionAction::None
            }
            ExtensionsMessage::StartOfflineInstall => self.request_offline_install(false),
            ExtensionsMessage::ToggleInstallLogs => {
                self.install.show_logs = !self.install.show_logs;
                ExtensionAction::None
            }
            ExtensionsMessage::OpenExtensionRoot => self
                .target_path
                .as_ref()
                .map(|path| {
                    ExtensionAction::OpenPath(
                        path.join("public/scripts/extensions/third-party"),
                    )
                })
                .unwrap_or(ExtensionAction::None),
            ExtensionsMessage::OpenHomepage(id) => self
                .find_extension(&id)
                .filter(|extension| {
                    extension.manifest.home_page.starts_with("https://")
                        || extension.manifest.home_page.starts_with("http://")
                })
                .map(|extension| ExtensionAction::OpenUrl(extension.manifest.home_page.clone()))
                .unwrap_or(ExtensionAction::None),
            ExtensionsMessage::OpenDirectory(id) => self
                .find_extension(&id)
                .map(|extension| ExtensionAction::OpenPath(extension.path.clone()))
                .unwrap_or(ExtensionAction::None),
            ExtensionsMessage::ToggleEnabled(id, enabled) => self
                .find_extension(&id)
                .filter(|extension| extension.kind == ExtensionKind::ThirdParty)
                .map(|extension| ExtensionAction::SetEnabled {
                    path: extension.path.clone(),
                    name: extension.manifest.display_name.clone(),
                    enabled,
                })
                .unwrap_or(ExtensionAction::None),
            ExtensionsMessage::RequestDelete(id) => {
                if let Some(extension) = self
                    .find_extension(&id)
                    .filter(|extension| extension.kind == ExtensionKind::ThirdParty)
                {
                    self.confirmation = Some(PendingConfirmation::Delete {
                        path: extension.path.clone(),
                        name: extension.manifest.display_name.clone(),
                    });
                }
                ExtensionAction::None
            }
            ExtensionsMessage::RequestGitRepair(id) => {
                if let Some(extension) = self.find_extension(&id)
                    && let GitHealth::Repairable { remote_url } = &extension.git_health
                {
                    self.confirmation = Some(PendingConfirmation::RepairGit {
                        path: extension.path.clone(),
                        name: extension.manifest.display_name.clone(),
                        remote_url: remote_url.clone(),
                    });
                }
                ExtensionAction::None
            }
            ExtensionsMessage::ConfirmPending => self.confirm_pending(),
            ExtensionsMessage::CancelPending => {
                self.confirmation = None;
                ExtensionAction::None
            }
            ExtensionsMessage::NavigateVersion => ExtensionAction::NavigateVersion,
            ExtensionsMessage::ModalInteract => ExtensionAction::None,
        }
    }

    fn start_branch_detection(&mut self, url: String) -> ExtensionAction {
        self.install.git_detect_due = None;
        if url.is_empty() {
            self.install.error = Some(ExtensionError::new(
                "extensions.error.invalid_repository",
                url,
            ));
            return ExtensionAction::None;
        }
        if self.task_running {
            return ExtensionAction::None;
        }
        self.task_running = true;
        self.install.branches_loading = true;
        self.install.error = None;
        ExtensionAction::FetchBranches { repository_url: url }
    }

    fn request_git_install(&mut self, overwrite: bool) -> ExtensionAction {
        let url = self.install.git_url.trim().to_owned();
        let Some(branch) = self.install.selected_branch.clone() else {
            self.install.error = Some(ExtensionError::new("extensions.error.select_branch", ""));
            return ExtensionAction::None;
        };
        if self.task_running || self.install.running {
            return ExtensionAction::None;
        }
        if !overwrite
            && repository_name(&url).is_some_and(|id| self.extensions.iter().any(|item| item.id == id))
        {
            self.confirmation = Some(PendingConfirmation::GitOverwrite);
            return ExtensionAction::None;
        }
        self.begin_install();
        ExtensionAction::InstallGit {
            repository_url: url,
            branch,
            overwrite,
        }
    }

    fn request_offline_install(&mut self, overwrite: bool) -> ExtensionAction {
        if self.task_running || self.install.running || self.install.offline_packages.is_empty() {
            return ExtensionAction::None;
        }
        if self.install.offline_packages.iter().any(|package| !package.valid) {
            self.install.error = Some(ExtensionError::new("extensions.error.offline_invalid", ""));
            return ExtensionAction::None;
        }
        if !overwrite
            && self.install.offline_packages.iter().any(|package| {
                package.extension_id.as_ref().is_some_and(|id| {
                    self.extensions.iter().any(|extension| &extension.id == id)
                })
            })
        {
            self.confirmation = Some(PendingConfirmation::OfflineOverwrite);
            return ExtensionAction::None;
        }
        let packages = self.install.offline_packages.clone();
        self.begin_install();
        ExtensionAction::InstallOffline { packages, overwrite }
    }

    fn begin_install(&mut self) {
        self.task_running = true;
        self.install.running = true;
        self.install.completed = false;
        self.install.operation_started = true;
        self.install.show_logs = false;
        self.install.started_at = Some(std::time::Instant::now());
        self.install.auto_close_due = None;
        self.install.error = None;
        self.install.logs.clear();
    }

    fn confirm_pending(&mut self) -> ExtensionAction {
        let Some(confirmation) = self.confirmation.take() else {
            return ExtensionAction::None;
        };
        match confirmation {
            PendingConfirmation::Delete { path, name } => ExtensionAction::Delete { path, name },
            PendingConfirmation::GitOverwrite => self.request_git_install(true),
            PendingConfirmation::OfflineOverwrite => self.request_offline_install(true),
            PendingConfirmation::RepairGit {
                path,
                name,
                remote_url,
            } => ExtensionAction::RepairGit {
                path,
                name,
                remote_url,
            },
        }
    }

    pub fn apply_event(&mut self, event: ExtensionEvent) -> bool {
        let terminal = event.is_terminal();
        let refresh = match event {
            ExtensionEvent::ScanFinished(result) => {
                self.status = if result.is_ok() { LoadStatus::Ready } else { LoadStatus::Error };
                // 自动刷新不弹提示，只有用户主动刷新才展示扫描结果。
                let notify = std::mem::take(&mut self.scan_notify);
                match result {
                    Ok(extensions) => {
                        if notify {
                            self.notice = Some(TransientNotice::info(
                                "notice.refresh_complete",
                                format_count_notice(extensions.len()),
                            ));
                        }
                        self.extensions = extensions;
                        self.error = None;
                    }
                    Err(error) => {
                        self.error = Some(error.clone());
                    }
                }
                false
            }
            ExtensionEvent::BranchesFinished(result) => {
                self.install.branches_loading = false;
                match result {
                    Ok(catalog) => {
                        self.install.branches = catalog.branches;
                        self.install.selected_branch = Some(catalog.selected);
                        self.install.error = None;
                    }
                    Err(error) => self.install.error = Some(error),
                }
                false
            }
            ExtensionEvent::OfflineInspected(packages) => {
                self.install.offline_checking = false;
                self.install.offline_packages = packages;
                self.install.error = None;
                false
            }
            ExtensionEvent::Log(line) => {
                while self.install.logs.len() >= 300 {
                    self.install.logs.remove(0);
                }
                let line = if line == "extensions.log.proxy_fallback" {
                    tr("extensions.log.proxy_fallback").to_owned()
                } else {
                    line
                };
                let sanitized: String = line
                    .chars()
                    .filter(|character| !character.is_control() || *character == '\t')
                    .take(2000)
                    .collect();
                self.install.logs.push(sanitized);
                false
            }
            ExtensionEvent::OperationFinished(result) => {
                self.install.running = false;
                match result {
                    Ok(success) => {
                        let installed = matches!(success, OperationSuccess::Installed(_));
                        self.notice = Some(TransientNotice::success(
                            "notice.operation_complete",
                            format_success(&success),
                        ));
                        self.install.completed = installed;
                        self.install.started_at = self.install.started_at.or_else(|| Some(std::time::Instant::now()));
                        self.install.auto_close_due = installed.then(|| {
                            std::time::Instant::now() + std::time::Duration::from_secs(3)
                        });
                        self.install.error = None;
                        installed || matches!(success, OperationSuccess::Enabled { .. } | OperationSuccess::Deleted(_) | OperationSuccess::GitRepaired(_))
                    }
                    Err(error) => {
                        self.install.error = Some(error.clone());
                        if !self.install.visible {
                            self.notice = Some(TransientNotice::danger(
                                "notice.operation_failed",
                                format_error(&error),
                            ));
                        }
                        false
                    }
                }
            }
        };
        if terminal {
            self.task_running = false;
        }
        refresh
    }

    /// 取出扩展页面产生的轻提示，避免在每次重绘时重复展示。
    pub fn take_notice(&mut self) -> Option<TransientNotice> {
        self.notice.take()
    }

    fn find_extension(&self, id: &str) -> Option<&ExtensionInfo> {
        self.extensions.iter().find(|extension| extension.id == id)
    }

    fn visible_extensions(&self) -> Vec<&ExtensionInfo> {
        self.extensions
            .iter()
            .filter(|extension| self.show_system_extensions || extension.kind != ExtensionKind::System)
            .collect()
    }
}

pub fn extensions_view(state: &ExtensionsState) -> Element<'_, ExtensionsMessage> {
    let base = base_view(state);
    let layered = if state.install.visible {
        stack![base, install_modal(state)].width(Fill).height(Fill).into()
    } else {
        base
    };
    if state.confirmation.is_some() {
        stack![layered, confirmation_modal(state)]
            .width(Fill)
            .height(Fill)
            .into()
    } else {
        layered
    }
}

fn base_view(state: &ExtensionsState) -> Element<'_, ExtensionsMessage> {
    let install_button = header_button(
        tr("extensions.install"),
        Icon::Download,
        ExtensionsMessage::OpenInstall,
        ButtonVariant::Primary,
    );
    let folder_button = header_button(
        tr("extensions.open_root"),
        Icon::FolderOpen,
        ExtensionsMessage::OpenExtensionRoot,
        ButtonVariant::Secondary,
    );
    let header = row![
        column![
            raw(tr("extensions.title"))
                .size(24)
                .font(crate::core::typography::medium())
                .style(crate::theme::text_style),
            raw(tr("extensions.description"))
                .size(12)
                .style(crate::theme::muted_text_style),
        ]
        .spacing(4),
        space::horizontal(),
        install_button,
        folder_button,
    ]
    .spacing(10)
    .align_y(Alignment::Center)
    .width(Fill);

    let body = if state.target_path.is_some() {
        column![extensions_panel(state)].height(Fill)
    } else {
        column![no_instance_card()].height(Fill)
    };

    container(
        container(column![header, body].spacing(16).width(Fill).height(Fill))
            .width(Fill)
            .height(Fill)
            .max_width(980),
    )
    .width(Fill)
    .height(Fill)
    .padding([26, 30])
    .align_x(Alignment::Center)
    .style(crate::theme::canvas_style)
    .into()
}

fn no_instance_card<'a>() -> Element<'a, ExtensionsMessage> {
    container(
        column![
            crate::theme::subtle_icon(Icon::Puzzle, 38),
            raw(tr("extensions.no_instance"))
                .size(17)
                .font(crate::core::typography::medium())
                .style(crate::theme::text_style),
            raw(tr("extensions.no_instance_hint"))
                .size(11)
                .style(crate::theme::muted_text_style),
            button(raw(tr("extensions.go_versions")).size(12))
                .on_press(ExtensionsMessage::NavigateVersion)
                .padding([9, 16])
                .style(button_style(ButtonVariant::Primary)),
        ]
        .spacing(12)
        .align_x(Alignment::Center),
    )
    .width(Fill)
    .height(Fill)
    .align_x(Alignment::Center)
    .align_y(Alignment::Center)
    .style(panel_surface)
    .into()
}

fn extensions_panel(state: &ExtensionsState) -> Element<'_, ExtensionsMessage> {
    let visible = state.visible_extensions();
    let header = container(
        row![
            crate::theme::muted_icon(Icon::Puzzle, 18),
            raw(tr("extensions.installed"))
                .size(15)
                .font(crate::core::typography::medium())
                .style(crate::theme::text_style),
            container(raw(format!("{} {}", visible.len(), tr("extensions.items"))).size(9))
                .padding([4, 8])
                .style(meta_surface),
            space::horizontal(),
            raw(tr("extensions.show_system"))
                .size(11)
                .font(crate::core::typography::medium())
                .style(crate::theme::muted_text_style),
            compact_switch(
                state.show_system_extensions,
                BLUE_600,
                ExtensionsMessage::ToggleShowSystem(!state.show_system_extensions),
            ),
            icon_action(
                Icon::RefreshCw,
                tr("extensions.refresh"),
                ExtensionsMessage::Refresh,
                INK_MUTED,
            ),
        ]
        .spacing(9)
        .align_y(Alignment::Center),
    )
    .width(Fill)
    .height(58)
    .padding([0, 20])
    .align_y(Alignment::Center);

    let list: Element<'_, ExtensionsMessage> = match state.status {
        LoadStatus::Loading => centered_state(Icon::LoaderCircle, "extensions.loading", None),
        LoadStatus::Error => centered_state(
            Icon::CircleAlert,
            "extensions.load_failed",
            state.error.as_ref().map(format_error),
        ),
        _ if visible.is_empty() => centered_state(Icon::Puzzle, "extensions.empty", Some(tr("extensions.empty_hint").to_owned())),
        _ => {
            let count = visible.len();
            let rows = visible.into_iter().enumerate().fold(column![].width(Fill), |rows, (index, extension)| {
                let rows = rows.push(extension_row(extension));
                if index + 1 < count { rows.push(separator_line()) } else { rows }
            });
            scrollable(rows).height(Fill).into()
        }
    };

    container(column![header, separator_line(), list].height(Fill).width(Fill))
        .width(Fill)
        .height(Fill)
        .style(panel_surface)
        .into()
}

fn centered_state<'a>(icon: Icon, key: &'static str, detail: Option<String>) -> Element<'a, ExtensionsMessage> {
    let mut content = column![
        crate::theme::subtle_icon(icon, 34),
        raw(tr(key))
            .size(13)
            .font(crate::core::typography::medium())
            .style(crate::theme::muted_text_style),
    ]
    .spacing(8)
    .align_x(Alignment::Center);
    if let Some(detail) = detail {
        content = content.push(raw(detail).size(10).style(crate::theme::subtle_text_style));
    }
    container(content)
        .width(Fill)
        .height(Fill)
        .align_x(Alignment::Center)
        .align_y(Alignment::Center)
        .into()
}

fn extension_row(extension: &ExtensionInfo) -> Element<'_, ExtensionsMessage> {
    let id = extension.id.clone();
    let extension_name: Element<'_, ExtensionsMessage> = if extension.enabled {
        raw(&extension.manifest.display_name)
            .size(15)
            .font(crate::core::typography::medium())
            .style(crate::theme::text_style)
            .into()
    } else {
        raw(&extension.manifest.display_name)
            .size(15)
            .font(crate::core::typography::medium())
            .style(crate::theme::muted_text_style)
            .into()
    };
    let mut title = row![extension_name]
    .spacing(8)
    .align_y(Alignment::Center);
    if !extension.manifest.version.is_empty() {
        title = title.push(owned_badge(format!("v{}", extension.manifest.version), INK_MUTED));
    }
    if !extension.manifest.minimum_client_version.is_empty() {
        title = title.push(owned_badge(format!("ST ≥ {}", extension.manifest.minimum_client_version), BLUE_600));
    }
    title = title.push(badge(tr("extensions.scope.global"), PURPLE));
    if extension.kind == ExtensionKind::System {
        title = title.push(icon_badge(tr("extensions.system"), Icon::ShieldCheck, WARNING));
    }
    if !extension.enabled {
        title = title.push(badge(tr("extensions.disabled"), INK_SUBTLE));
    }
    if extension.manifest_error.is_some() {
        title = title.push(icon_badge(tr("extensions.manifest_broken"), Icon::TriangleAlert, DANGER));
    }

    let mut meta = row![
        crate::theme::subtle_icon(Icon::User, 12),
        raw(if extension.manifest.author.is_empty() {
            "-"
        } else {
            extension.manifest.author.as_str()
        })
            .size(10)
            .style(crate::theme::muted_text_style),
        text("|").size(10).style(crate::theme::subtle_text_style),
        crate::theme::subtle_icon(Icon::Folder, 12),
        raw(&extension.id).size(10).style(crate::theme::muted_text_style),
    ]
    .spacing(6)
    .align_y(Alignment::Center);
    if !extension.manifest.home_page.is_empty() {
        meta = meta.push(small_action(tr("extensions.homepage"), Icon::Globe, ExtensionsMessage::OpenHomepage(id.clone())));
    }
    meta = meta.push(small_action(tr("extensions.open_directory"), Icon::FolderOpen, ExtensionsMessage::OpenDirectory(id.clone())));
    if matches!(extension.git_health, GitHealth::Repairable { .. }) {
        meta = meta.push(small_action(tr("extensions.repair_git"), Icon::Wrench, ExtensionsMessage::RequestGitRepair(id.clone())));
    }

    let controls: Element<'_, ExtensionsMessage> = if extension.kind == ExtensionKind::System {
        badge(tr("extensions.system_enabled"), WARNING)
    } else {
        row![
            text(if extension.enabled { tr("extensions.enabled") } else { tr("extensions.disabled") })
                .size(11)
                .font(crate::core::typography::medium())
                .style(crate::theme::muted_text_style),
            compact_switch(extension.enabled, SUCCESS, ExtensionsMessage::ToggleEnabled(id.clone(), !extension.enabled)),
            icon_action(Icon::Trash2, tr("extensions.delete"), ExtensionsMessage::RequestDelete(id), DANGER),
        ]
        .spacing(8)
        .align_y(Alignment::Center)
        .into()
    };

    let auto_update = extension.manifest.auto_update.map(|enabled| {
        owned_badge(
            format!("{}: {}", tr("extensions.auto_update"), if enabled { tr("extensions.on") } else { tr("extensions.off") }),
            if enabled { SUCCESS } else { INK_SUBTLE },
        )
    });
    let mut details = row![meta, space::horizontal()].align_y(Alignment::Center);
    if let Some(auto_update) = auto_update {
        details = details.push(auto_update);
    }

    container(
        row![
            container(extension_icon(extension.enabled))
                .width(38)
                .height(38)
                .align_x(Alignment::Center)
                .align_y(Alignment::Center)
                .style(control_surface),
            column![title, details].spacing(7).width(Fill),
            controls,
        ]
        .spacing(14)
        .align_y(Alignment::Center),
    )
    .width(Fill)
    .padding([14, 20])
    .into()
}

fn install_modal(state: &ExtensionsState) -> Element<'_, ExtensionsMessage> {
    let selected = state.install.tab;
    let tab_bar = container(
        row![
            install_tab_button(
                tr("extensions.install.git"),
                selected == InstallTab::Git,
                ExtensionsMessage::SelectInstallTab(0),
            ),
            install_tab_button(
                tr("extensions.install.offline"),
                selected == InstallTab::Offline,
                ExtensionsMessage::SelectInstallTab(1),
            ),
        ]
        .spacing(4)
        .width(Fill),
    )
    .width(Fill)
    .padding(4)
    .style(modal_tab_bar_surface);
    let installing = state.install.operation_started;
    let body: Element<'_, ExtensionsMessage> = if installing {
        install_progress_panel(state).into()
    } else {
        let panel = if selected == InstallTab::Git {
            git_install_panel(state)
        } else {
            offline_install_panel(state)
        };
        column![tab_bar, panel].spacing(14).width(Fill).into()
    };
    let confirm = if state.install.running {
        tr("extensions.installing")
    } else if installing {
        tr("extensions.close")
    } else {
        tr("extensions.install")
    };
    let confirm_message = if installing {
        if state.install.running {
            ExtensionsMessage::ModalInteract
        } else {
            ExtensionsMessage::CloseInstall
        }
    } else if selected == InstallTab::Git {
        ExtensionsMessage::StartGitInstall
    } else {
        ExtensionsMessage::StartOfflineInstall
    };
    extension_modal(
        tr("extensions.install.title"),
        tr("extensions.install.description"),
        body,
        tr("extensions.cancel"),
        confirm,
        false,
        ExtensionsMessage::CloseInstall,
        confirm_message,
        ExtensionsMessage::CloseInstall,
    )
}

fn install_progress_panel(state: &ExtensionsState) -> Element<'_, ExtensionsMessage> {
    let elapsed = state
        .install
        .started_at
        .map(|started| started.elapsed().as_secs())
        .unwrap_or_default();
    let (status_title, description, color, icon): (&str, &str, ProgressBarColor, Icon) =
        if state.install.running {
            (
                tr("extensions.installing"),
                tr("extensions.install.executing"),
                ProgressBarColor::Accent,
                Icon::LoaderCircle,
            )
        } else if state.install.error.is_some() {
            (
                tr("extensions.install.failed"),
                tr("extensions.install.not_completed"),
                ProgressBarColor::Danger,
                Icon::CircleX,
            )
        } else {
            (
                tr("extensions.install.success_title"),
                tr("extensions.install.ready"),
                ProgressBarColor::Success,
                Icon::CircleCheck,
            )
        };
    let status_icon: Element<'_, ExtensionsMessage> = container(
        icons::icon(
            if state.install.running { Icon::Download } else { icon },
            17,
            if state.install.running { BLUE_600 } else if state.install.error.is_some() { DANGER } else { SUCCESS },
        ),
    )
    .width(28)
    .height(28)
    .align_x(Alignment::Center)
    .align_y(Alignment::Center)
    .style(control_surface)
    .into();
    let latest_log = state
        .install
        .logs
        .iter()
        .rev()
        .find(|line| !line.trim().is_empty())
        .map(|line| truncate_install_log(line, 78))
        .unwrap_or_else(|| tr("extensions.install.waiting").to_owned());
    let details_label = if state.install.show_logs {
        tr("extensions.install.hide_logs")
    } else {
        tr("extensions.install.show_logs")
    };
    let details_toggle = button(
        row![
            crate::theme::muted_icon(
                if state.install.show_logs { Icon::ChevronUp } else { Icon::ChevronDown },
                14,
            ),
            text(details_label)
                .size(10)
                .font(crate::core::typography::medium()),
            space::horizontal(),
        ]
        .spacing(7)
        .align_y(Alignment::Center)
        .width(Fill),
    )
    .on_press(ExtensionsMessage::ToggleInstallLogs)
    .width(Fill)
    .padding([6, 0])
    .style(button_style(ButtonVariant::Ghost));
    let header = row![
        status_icon,
        column![
            text(status_title)
                .size(14)
                .font(crate::core::typography::medium()),
            raw(format!(
                "{}  ·  {} {}s",
                description,
                tr("extensions.install.elapsed"),
                elapsed
            ))
            .size(10)
            .style(crate::theme::muted_text_style),
        ]
        .spacing(2)
        .width(Fill),
    ]
    .spacing(10)
    .align_y(Alignment::Center)
    .width(Fill);
    let summary = row![
        crate::theme::subtle_icon(Icon::Circle, 13),
        raw(latest_log)
            .size(10)
            .style(crate::theme::muted_text_style),
    ]
    .spacing(6)
    .align_y(Alignment::Center);
    let mut content = column![
        header,
        ProgressBar::new(if state.install.running { 0.0 } else { 100.0 })
            .show_value(false)
            .is_indeterminate(state.install.running)
            .size(ProgressBarSize::Small)
            .color(color),
    ]
    .spacing(8)
    .width(Fill);
    if state.install.running {
        content = content.push(
            raw(tr("extensions.install.progress_unknown"))
                .size(9)
                .style(crate::theme::muted_text_style),
        );
    }
    content = content.push(summary).push(details_toggle);
    if state.install.show_logs {
        let logs = if state.install.logs.is_empty() {
            tr("extensions.install.waiting")
        } else {
            ""
        };
        let log_content: Element<'_, ExtensionsMessage> = if logs.is_empty() {
            column(state.install.logs.iter().map(|line| raw(line).size(10).font(Font::MONOSPACE).style(crate::theme::text_style).into()))
                .spacing(2)
                .width(Fill)
                .into()
        } else {
            text(logs).size(10).font(Font::MONOSPACE).style(crate::theme::muted_text_style).into()
        };
        content = content.push(
            scrollable(container(log_content).width(Fill).padding(12).style(log_surface))
                .height(if crate::core::typography::current_ui_scale() >= 1.35 { 120 } else { 180 }),
        );
    }
    content.into()
}

fn truncate_install_log(line: &str, max_chars: usize) -> String {
    let clean = line.trim();
    if clean.chars().count() <= max_chars {
        return clean.to_owned();
    }
    let mut result: String = clean.chars().take(max_chars.saturating_sub(1)).collect();
    result.push('…');
    result
}

fn git_install_panel(state: &ExtensionsState) -> Element<'_, ExtensionsMessage> {
    let branch_picker = pick_list(
        state.install.branches.clone(),
        state.install.selected_branch.clone(),
        ExtensionsMessage::SelectBranch,
    )
    .placeholder(tr("extensions.branch.placeholder"))
    .width(Fill)
    .padding([8, 10])
    .text_size(12)
    .style(pick_list_style)
    .menu_style(pick_list_menu_style);
    let mut content = column![
        raw(tr("extensions.git_url")).size(11).font(crate::core::typography::medium()),
        row![
            text_input(tr("extensions.git_url.placeholder"), &state.install.git_url)
                .on_input(ExtensionsMessage::GitUrlChanged)
                .padding([9, 11])
                .size(12)
                .style(text_input_style)
                .width(Fill),
            button(text(if state.install.branches_loading { tr("extensions.detecting") } else { tr("extensions.detect") }).size(11))
                .on_press(ExtensionsMessage::FetchBranches)
                .padding([9, 12])
                .style(button_style(ButtonVariant::Secondary)),
        ]
        .spacing(8),
        raw(tr("extensions.branch")).size(11).font(crate::core::typography::medium()),
        branch_picker,
    ]
    .spacing(8)
    .width(Fill);
    content = append_install_feedback(content, state);
    content.into()
}

fn offline_install_panel(state: &ExtensionsState) -> Element<'_, ExtensionsMessage> {
    let mut packages = column![].spacing(6).width(Fill);
    for (index, package) in state.install.offline_packages.iter().enumerate() {
        let status = if package.valid { SUCCESS } else { DANGER };
        let detail = package
            .extension_id
            .clone()
            .or_else(|| package.error.as_ref().map(format_error))
            .unwrap_or_default();
        packages = packages.push(
            container(
                row![
                    icons::icon(if package.valid { Icon::CircleCheck } else { Icon::CircleX }, 14, status),
                    column![
                        raw(&package.file_name).size(11),
                        raw(detail).size(9).style(crate::theme::subtle_text_style),
                    ]
                    .spacing(2)
                    .width(Fill),
                    icon_action(Icon::X, tr("extensions.remove"), ExtensionsMessage::RemoveOfflinePackage(index), INK_MUTED),
                ]
                .spacing(8)
                .align_y(Alignment::Center),
            )
            .padding([7, 9])
            .style(control_surface),
        );
    }
    let list: Element<'_, ExtensionsMessage> = if state.install.offline_packages.is_empty() {
        raw(tr("extensions.offline.empty"))
            .size(10)
            .style(crate::theme::subtle_text_style)
            .into()
    } else {
        scrollable(packages).height(120).into()
    };
    let mut content = column![
        button(text(if state.install.offline_checking { tr("extensions.checking") } else { tr("extensions.choose_zip") }).size(11))
            .on_press(ExtensionsMessage::ChooseOfflineFiles)
            .padding([9, 14])
            .style(button_style(ButtonVariant::Secondary)),
        list,
    ]
    .spacing(8)
    .width(Fill);
    content = append_install_feedback(content, state);
    content.into()
}

fn append_install_feedback<'a>(
    mut content: iced::widget::Column<'a, ExtensionsMessage>,
    state: &'a ExtensionsState,
) -> iced::widget::Column<'a, ExtensionsMessage> {
    if let Some(error) = &state.install.error {
        content = content.push(
            container(raw(format_error(error)).size(10).color(DANGER))
                .padding([6, 8])
                .style(error_surface),
        );
    }
    if !state.install.logs.is_empty() {
        let logs = column(state.install.logs.iter().map(|line| raw(line).size(9).into())).spacing(2);
        content = content.push(
            container(scrollable(logs).height(100))
                .padding(8)
                .style(log_surface),
        );
    }
    if state.install.completed {
        content = content.push(raw(tr("extensions.install.success")).size(11).color(SUCCESS));
    }
    content
}

fn confirmation_modal(state: &ExtensionsState) -> Element<'_, ExtensionsMessage> {
    let (title, description, confirm, destructive) = match state.confirmation.as_ref() {
        Some(PendingConfirmation::Delete { name, .. }) => (
            tr("extensions.confirm.delete.title"),
            format!("{} {name}", tr("extensions.confirm.delete.description")),
            tr("extensions.delete"),
            true,
        ),
        Some(PendingConfirmation::GitOverwrite) | Some(PendingConfirmation::OfflineOverwrite) => (
            tr("extensions.confirm.overwrite.title"),
            tr("extensions.confirm.overwrite.description").to_owned(),
            tr("extensions.overwrite"),
            true,
        ),
        Some(PendingConfirmation::RepairGit { name, .. }) => (
            tr("extensions.confirm.repair.title"),
            format!("{} {name}", tr("extensions.confirm.repair.description")),
            tr("extensions.repair_git"),
            false,
        ),
        None => ("", String::new(), "", false),
    };
    extension_modal(
        title,
        "",
        raw(description)
            .size(12)
            .style(crate::theme::muted_text_style),
        tr("extensions.cancel"),
        confirm,
        destructive,
        ExtensionsMessage::CancelPending,
        ExtensionsMessage::ConfirmPending,
        ExtensionsMessage::CancelPending,
    )
}

/// 扩展页面专用的主题感知弹窗，避免固定浅色令牌污染暗色模式。
#[allow(clippy::too_many_arguments)]
fn extension_modal<'a>(
    title: &'a str,
    description: &'a str,
    body: impl Into<Element<'a, ExtensionsMessage>>,
    cancel_label: &'a str,
    confirm_label: &'a str,
    destructive: bool,
    on_cancel: ExtensionsMessage,
    on_confirm: ExtensionsMessage,
    on_close: ExtensionsMessage,
) -> Element<'a, ExtensionsMessage> {
    let mut heading = column![
        raw(title)
            .size(18)
            .font(crate::core::typography::medium())
            .style(crate::theme::text_style),
    ]
    .spacing(4)
    .width(Fill);
    if !description.is_empty() {
        heading = heading.push(
            raw(description)
                .size(12)
                .style(crate::theme::muted_text_style),
        );
    }
    let header = row![
        heading,
        centered_icon_button(Icon::X, on_close, INK_MUTED, 32.0, 17),
    ]
    .spacing(14)
    .align_y(Alignment::Center)
    .width(Fill);
    let footer = row![
        space::horizontal(),
        button(raw(cancel_label).size(12).font(crate::core::typography::medium()))
            .on_press(on_cancel)
            .height(36)
            .padding([8, 16])
            .style(button_style(ButtonVariant::Secondary)),
        button(
            raw(confirm_label)
                .size(12)
                .font(crate::core::typography::medium())
                .color(WHITE),
        )
        .on_press(on_confirm)
        .height(36)
        .padding([8, 16])
        .style(button_style(if destructive {
            ButtonVariant::Destructive
        } else {
            ButtonVariant::Primary
        })),
    ]
    .spacing(8)
    .align_y(Alignment::Center)
    .width(Fill);
    let panel = mouse_area(
        container(
            column![header, separator_line(), body.into(), separator_line(), footer]
                .spacing(16)
                .width(Fill),
        )
        .width(480)
        .padding(20)
        .style(extension_modal_surface),
    )
    .on_press(ExtensionsMessage::ModalInteract);

    stack![
        button(space::Space::new())
            .on_press(ExtensionsMessage::ModalInteract)
            .width(Fill)
            .height(Fill)
            .padding(0)
            .style(extension_modal_backdrop_style),
        container(panel)
            .width(Fill)
            .height(Fill)
            .align_x(Alignment::Center)
            .align_y(Alignment::Center)
            .padding(24),
    ]
    .width(Fill)
    .height(Fill)
    .into()
}

fn install_tab_button<'a>(
    label: &'a str,
    active: bool,
    message: ExtensionsMessage,
) -> Element<'a, ExtensionsMessage> {
    button(
        container(
            raw(label)
                .size(12)
                .font(crate::core::typography::medium()),
        )
        .width(Fill)
        .align_x(Alignment::Center),
    )
    .on_press(message)
    .width(Fill)
    .height(36)
    .padding([8, 12])
    .style(move |theme, status| install_tab_button_style(theme, status, active))
    .into()
}

fn header_button<'a>(label: &'a str, icon: Icon, message: ExtensionsMessage, variant: ButtonVariant) -> Element<'a, ExtensionsMessage> {
    button(row![icons::icon(icon, 14, if variant == ButtonVariant::Primary { WHITE } else { BLUE_600 }), raw(label).size(11)].spacing(6).align_y(Alignment::Center))
        .on_press(message)
        .padding([9, 13])
        .style(button_style(variant))
        .into()
}

fn badge<'a>(label: &'a str, color: Color) -> Element<'a, ExtensionsMessage> {
    container(raw(label).size(9).color(color)).padding([3, 7]).style(move |_theme| badge_surface(color)).into()
}

fn owned_badge(label: String, color: Color) -> Element<'static, ExtensionsMessage> {
    container(raw(label).size(9).color(color)).padding([3, 7]).style(move |_theme| badge_surface(color)).into()
}

fn icon_badge<'a>(label: &'a str, icon: Icon, color: Color) -> Element<'a, ExtensionsMessage> {
    container(row![icons::icon(icon, 10, color), raw(label).size(9).color(color)].spacing(4).align_y(Alignment::Center))
        .padding([3, 7])
        .style(move |_theme| badge_surface(color))
        .into()
}

fn small_action<'a>(label: &'a str, icon: Icon, message: ExtensionsMessage) -> Element<'a, ExtensionsMessage> {
    button(row![crate::theme::muted_icon(icon, 11), raw(label).size(9).style(crate::theme::muted_text_style)].spacing(4).align_y(Alignment::Center))
        .on_press(message)
        .padding([3, 5])
        .style(small_action_style)
        .into()
}

fn icon_action<'a>(icon: Icon, label: &'a str, message: ExtensionsMessage, color: Color) -> Element<'a, ExtensionsMessage> {
    tooltip(
        centered_icon_button(icon, message, color, 28.0, 14),
        container(raw(label).size(10).color(WHITE)).padding([5, 8]).style(tooltip_surface),
        tooltip::Position::Top,
    )
    .gap(5)
    .into()
}

/// 用填满按钮的容器执行双轴居中，避免字体图标受文本基线影响而偏移。
fn centered_icon_button<'a>(
    icon: Icon,
    message: ExtensionsMessage,
    color: Color,
    size: f32,
    icon_size: u32,
) -> Element<'a, ExtensionsMessage> {
    let glyph: Element<'a, ExtensionsMessage> = if color == INK_MUTED {
        crate::theme::muted_icon(icon, icon_size)
    } else {
        icons::icon(icon, icon_size, color).into()
    };
    button(
        container(glyph)
            .width(Fill)
            .height(Fill)
            .align_x(Alignment::Center)
            .align_y(Alignment::Center),
    )
    .on_press(message)
    .width(size)
    .height(size)
    .padding(0)
    .style(small_action_style)
    .into()
}

fn extension_icon<'a>(enabled: bool) -> Element<'a, ExtensionsMessage> {
    let icon: iced::widget::Text<'a> = Icon::Puzzle.into();
    if enabled {
        icon.size(18).style(crate::theme::text_style).into()
    } else {
        icon.size(18).style(crate::theme::muted_text_style).into()
    }
}

fn compact_switch(active: bool, color: Color, message: ExtensionsMessage) -> Element<'static, ExtensionsMessage> {
    let track = container(row![if active { space::horizontal() } else { space::horizontal().width(0) }, container(space::horizontal()).width(14).height(14).style(switch_thumb), if active { space::horizontal().width(0) } else { space::horizontal() }])
        .width(32)
        .height(18)
        .padding(2)
        .style(move |_theme| switch_track(if active { color } else { SURFACE_ALT }));
    button(track).on_press(message).padding(0).style(switch_button_style).into()
}

fn separator_line<'a>() -> Element<'a, ExtensionsMessage> {
    container(space::vertical()).width(Fill).height(1).style(separator_surface).into()
}

fn tr(key: &'static str) -> &'static str {
    t_in(key, current_language())
}

fn format_error(error: &ExtensionError) -> String {
    let label = tr(error.key);
    if error.detail.is_empty() { label.to_owned() } else { format!("{label}: {}", error.detail) }
}

fn format_count_notice(count: usize) -> String {
    tf("extensions.success.scanned", &[("count", &count)])
}

fn format_success(success: &OperationSuccess) -> String {
    match success {
        OperationSuccess::Installed(ids) => {
            tf("extensions.success.installed", &[("names", &ids.join("、"))])
        }
        OperationSuccess::Enabled { name, enabled } => {
            let key = if *enabled {
                "extensions.success.enabled"
            } else {
                "extensions.success.disabled"
            };
            tf(key, &[("name", name)])
        }
        OperationSuccess::Deleted(name) => {
            tf("extensions.success.deleted", &[("name", name)])
        }
        OperationSuccess::GitRepaired(name) => {
            tf("extensions.success.git_repaired", &[("name", name)])
        }
    }
}

fn panel_surface(theme: &Theme) -> container::Style {
    container::Style { background: Some(Background::Color(crate::theme::surface(theme))), border: Border { color: crate::theme::line(theme), width: 1.0, radius: 12.0.into() }, ..container::Style::default() }
}
fn control_surface(theme: &Theme) -> container::Style {
    container::Style { background: Some(Background::Color(crate::theme::surface_alt(theme))), border: Border { color: crate::theme::line(theme), width: 1.0, radius: 10.0.into() }, ..container::Style::default() }
}
fn badge_surface(color: Color) -> container::Style {
    container::Style { background: Some(Background::Color(Color::from_rgba(color.r, color.g, color.b, 0.10))), border: Border { radius: 9.0.into(), ..Border::default() }, ..container::Style::default() }
}
fn meta_surface(theme: &Theme) -> container::Style {
    container::Style { background: Some(Background::Color(crate::theme::surface_alt(theme))), border: Border { color: crate::theme::line(theme), width: 1.0, radius: 10.0.into() }, ..container::Style::default() }
}
fn error_surface(_theme: &Theme) -> container::Style {
    container::Style { background: Some(Background::Color(Color::from_rgba(DANGER.r, DANGER.g, DANGER.b, 0.08))), border: Border { color: Color::from_rgba(DANGER.r, DANGER.g, DANGER.b, 0.25), width: 1.0, radius: 8.0.into() }, ..container::Style::default() }
}
fn log_surface(theme: &Theme) -> container::Style {
    container::Style { background: Some(Background::Color(crate::theme::surface_alt(theme))), border: Border { color: crate::theme::line(theme), width: 1.0, radius: 8.0.into() }, ..container::Style::default() }
}
fn extension_modal_surface(theme: &Theme) -> container::Style {
    container::Style {
        background: Some(Background::Color(crate::theme::surface(theme))),
        border: Border {
            color: crate::theme::line(theme),
            width: 1.0,
            radius: 16.0.into(),
        },
        shadow: iced::Shadow {
            color: Color::from_rgba(
                0.0,
                0.0,
                0.0,
                if crate::theme::is_dark(theme) { 0.46 } else { 0.20 },
            ),
            offset: iced::Vector::new(0.0, 10.0),
            blur_radius: 30.0,
        },
        ..container::Style::default()
    }
}

fn modal_tab_bar_surface(theme: &Theme) -> container::Style {
    container::Style {
        background: Some(Background::Color(crate::theme::surface_alt(theme))),
        border: Border {
            color: crate::theme::line(theme),
            width: 1.0,
            radius: 12.0.into(),
        },
        ..container::Style::default()
    }
}

fn install_tab_button_style(
    theme: &Theme,
    status: button::Status,
    active: bool,
) -> button::Style {
    let hovered = matches!(status, button::Status::Hovered | button::Status::Pressed);
    let background = if active {
        Some(Background::Color(crate::theme::surface(theme)))
    } else if hovered {
        Some(Background::Color(Color::from_rgba(
            theme.palette().primary.r,
            theme.palette().primary.g,
            theme.palette().primary.b,
            0.10,
        )))
    } else {
        None
    };
    button::Style {
        background,
        text_color: if active {
            crate::theme::text(theme)
        } else {
            crate::theme::text_muted(theme)
        },
        border: Border {
            color: if active {
                crate::theme::line(theme)
            } else {
                Color::TRANSPARENT
            },
            width: if active { 1.0 } else { 0.0 },
            radius: 9.0.into(),
        },
        ..button::Style::default()
    }
}

fn extension_modal_backdrop_style(_theme: &Theme, _status: button::Status) -> button::Style {
    button::Style {
        background: Some(Background::Color(Color::from_rgba(0.0, 0.0, 0.0, 0.52))),
        ..button::Style::default()
    }
}

fn small_action_style(theme: &Theme, status: button::Status) -> button::Style {
    button::Style { background: matches!(status, button::Status::Hovered | button::Status::Pressed).then_some(Background::Color(crate::theme::surface_alt(theme))), border: Border { radius: 7.0.into(), ..Border::default() }, ..button::Style::default() }
}
fn tooltip_surface(_theme: &Theme) -> container::Style {
    container::Style { background: Some(Background::Color(Color::from_rgb8(38, 38, 42))), border: Border { radius: 7.0.into(), ..Border::default() }, ..container::Style::default() }
}
fn separator_surface(theme: &Theme) -> container::Style {
    container::Style { background: Some(Background::Color(crate::theme::line(theme))), ..container::Style::default() }
}
fn switch_track(color: Color) -> container::Style {
    container::Style { background: Some(Background::Color(color)), border: Border { radius: 10.0.into(), ..Border::default() }, ..container::Style::default() }
}
fn switch_thumb(_theme: &Theme) -> container::Style {
    container::Style { background: Some(Background::Color(WHITE)), border: Border { radius: 8.0.into(), ..Border::default() }, ..container::Style::default() }
}
fn switch_button_style(_theme: &Theme, _status: button::Status) -> button::Style { button::Style::default() }

#[cfg(test)]
mod tests {
    use super::*;

    fn bound_state() -> ExtensionsState {
        let mut state = ExtensionsState::default();
        state.target_path = Some(PathBuf::from(r"C:\AstraBrew\extensions"));
        state
    }

    #[test]
    fn automatic_scan_finish_stays_silent() {
        let mut state = bound_state();
        state.begin_scan(false);
        let refresh = state.apply_event(ExtensionEvent::ScanFinished(Ok(Vec::new())));
        assert!(state.notice.is_none(), "自动刷新不应弹出扫描完成提示");
        assert_eq!(state.status, LoadStatus::Ready);
        assert!(!refresh, "扫描结束不应再触发重扫");
    }

    #[test]
    fn manual_scan_finish_reports_result() {
        let mut state = bound_state();
        state.begin_scan(true);
        state.apply_event(ExtensionEvent::ScanFinished(Ok(Vec::new())));
        assert!(state.notice.is_some(), "手动刷新应弹出扫描完成提示");
    }

    #[test]
    fn scan_notify_flag_does_not_leak_to_next_scan() {
        let mut state = bound_state();
        state.begin_scan(true);
        state.apply_event(ExtensionEvent::ScanFinished(Ok(Vec::new())));
        state.take_notice();
        // 手动刷新之后的自动重扫必须回到静默。
        state.begin_scan(false);
        state.apply_event(ExtensionEvent::ScanFinished(Ok(Vec::new())));
        assert!(state.notice.is_none(), "提示标志不得沿用到下一次自动扫描");
    }
}
