//! 本地实例弹窗和轻提示；后台任务生命周期保存在应用层，不依赖这些视图。

use super::VersionMessage;
use super::ring;
use crate::core::local_instances::LocalError;
use crate::core::local_instances::scan::{DrivePhase, DriveProgress, ScanProgress};
use crate::lang::lang::current_language;
use crate::lang::{raw, t_in, text, textf};
use crate::theme::button_style;
use astra_ui::{BLUE_600, ButtonVariant, DANGER, INK_MUTED, INK_SUBTLE, SUCCESS, icons};
use iced::widget::{button, column, container, mouse_area, row, scrollable, space, stack};
use iced::{Alignment, Element, Fill};
use lucide_icons::Icon;
use std::collections::VecDeque;
use std::time::{Duration, Instant};

/// 关闭弹窗只隐藏视图；主动取消整轮任务由应用层单独处理。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ScanPhase {
    #[default]
    Idle,
    Running,
    Completed,
    CompletedPartial,
    Failed,
    Cancelled,
}

impl ScanPhase {
    pub fn label_key(self) -> &'static str {
        match self {
            Self::Idle => "local.scan.status.idle",
            Self::Running => "local.scan.status.running",
            Self::Completed => "local.scan.status.completed",
            Self::CompletedPartial => "local.scan.status.completed_partial",
            Self::Failed => "local.scan.status.failed",
            Self::Cancelled => "local.scan.cancelled",
        }
    }
    pub fn active(self) -> bool {
        matches!(self, Self::Running)
    }
}

/// 最近扫描路径的展示条数。
const RECENT_PATH_LIMIT: usize = 5;

/// 详细日志的最小写入间隔。
///
/// 全盘扫描时目录切换极快，逐个目录写日志会在一秒内冲掉整份日志，
/// 因此限流成「每 400ms 一条」，既能看出扫描位置，又能保留足够长的历史。
const SCAN_LOG_INTERVAL: Duration = Duration::from_millis(400);

#[derive(Debug, Clone, Default)]
pub struct ScanState {
    pub started_at: Option<Instant>,
    /// 旧版只保留最近五条扫描路径作为进度提示。
    pub recent_paths: VecDeque<String>,
    pub cancel_confirm_visible: bool,
    pub show_details: bool,
    pub auto_hide_at: Option<Instant>,
    pub visible: bool,
    pub phase: ScanPhase,
    pub progress: ScanProgress,
    pub added: u64,
    pub duplicates: u64,
    pub logs: VecDeque<String>,
    pub error: Option<LocalError>,
    /// 上一条扫描日志的写入时刻，用于限流。
    pub last_log_at: Option<Instant>,
}

impl ScanState {
    pub fn status_key(&self) -> &'static str {
        self.phase.label_key()
    }

    /// 记录一条「当前正在遍历的目录」。
    ///
    /// 多个磁盘会并发上报，因此按内容去重而不是只看上一条：
    /// 否则多盘交替上报时同一个路径会被反复塞进列表。
    pub fn observe_path(&mut self, path: String) {
        if path.is_empty() || self.recent_paths.iter().any(|item| item == &path) {
            return;
        }
        while self.recent_paths.len() >= RECENT_PATH_LIMIT {
            self.recent_paths.pop_front();
        }
        self.recent_paths.push_back(path);
    }

    /// 把当前目录追加到详细日志（限流）。
    pub fn append_scan_log(&mut self, path: String) {
        let now = Instant::now();
        if self
            .last_log_at
            .is_some_and(|at| now.duration_since(at) < SCAN_LOG_INTERVAL)
        {
            return;
        }
        self.last_log_at = Some(now);
        append_log(&mut self.logs, path);
    }
}

/// 与旧版一样保留路径首尾；控制字符仅在展示时转义，实际文件路径不改变。
pub fn truncate_path(path: &str, max: usize) -> String {
    let display = path
        .replace('\n', "↵")
        .replace('\r', "␍")
        .replace('\t', "⇥");
    let count = display.chars().count();
    if count <= max {
        return display;
    }
    let head = max / 3;
    let tail = max.saturating_sub(head + 3);
    let start: String = display.chars().take(head).collect();
    let end: String = display.chars().skip(count - tail).collect();
    format!("{start}...{end}")
}

/// 详细日志单独限流，成功收起进度提示后仍可重新查看。
pub fn append_log(logs: &mut VecDeque<String>, line: String) {
    for line in line.lines() {
        while logs.len() >= 600 {
            logs.pop_front();
        }
        logs.push_back(line.chars().take(2000).collect());
    }
}

#[derive(Debug, Clone, Default)]
pub struct LocalInstallState {
    pub visible: bool,
    pub running: bool,
    pub path: Option<String>,
    pub logs: VecDeque<String>,
    pub error: Option<LocalError>,
}

#[derive(Debug, Clone)]
pub struct ToastNotice {
    pub message: &'static str,
    pub detail: String,
    pub danger: bool,
    pub until: Instant,
}

#[derive(Debug, Clone, Default)]
pub struct LocalUiState {
    pub loading: bool,
    pub import_pending: bool,
    pub animation_frame: u16,
    pub scan: ScanState,
    pub install: LocalInstallState,
    pub toast: Option<ToastNotice>,
}

impl LocalUiState {
    pub fn notify(&mut self, message: &'static str, detail: impl ToString, danger: bool) {
        // 正文可能是文案键（错误码上送）或运行时路径，入队前解析一次。
        let detail = crate::lang::resolve(&detail.to_string());
        let mut summary: String = detail.chars().take(240).collect();
        if detail.chars().count() > 240 {
            summary.push('…');
        }
        self.toast = Some(ToastNotice {
            message,
            detail: summary,
            danger,
            until: Instant::now() + Duration::from_secs(5),
        });
    }
    pub fn report(&mut self, error: &LocalError) {
        self.notify(error.message, &error.detail, true);
    }
    pub fn tick(&mut self) {
        self.animation_frame = (self.animation_frame + 1) % 20;
        if self
            .scan
            .auto_hide_at
            .is_some_and(|at| Instant::now() >= at)
        {
            self.scan.visible = false;
            self.scan.auto_hide_at = None;
        }
        if self.scan.phase.active()
            && let Some(started) = self.scan.started_at
        {
            self.scan.progress.elapsed_seconds = started.elapsed().as_secs();
        }
        if self
            .toast
            .as_ref()
            .is_some_and(|toast| Instant::now() >= toast.until)
        {
            self.toast = None;
        }
    }
    pub fn close_scan(&mut self) {
        self.scan.visible = false;
        self.scan.cancel_confirm_visible = false;
    }
}

fn modal<'a>(
    content: Element<'a, VersionMessage>,
    close: VersionMessage,
) -> Element<'a, VersionMessage> {
    sized_modal(content, close, 720.0)
}

fn sized_modal<'a>(
    content: Element<'a, VersionMessage>,
    close: VersionMessage,
    width: f32,
) -> Element<'a, VersionMessage> {
    stack![
        button(space::Space::new())
            .on_press(close)
            .width(Fill)
            .height(Fill)
            .padding(0)
            .style(super::install_backdrop_style),
        container(
            mouse_area(
                container(content)
                    .width(width)
                    .padding(22)
                    .style(super::install_modal_style)
            )
            .on_press(VersionMessage::LocalModalInteract)
        )
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

/// 每行展示的磁盘数量。
const DRIVE_COLUMNS: usize = 4;

/// 单个磁盘的进度环 + 说明文字。
fn drive_cell<'a>(drive: &DriveProgress, animation_phase: f32) -> Element<'a, VersionMessage> {
    let (color, indeterminate, fraction) = match drive.phase {
        DrivePhase::Pending => (INK_SUBTLE, false, 0.0),
        DrivePhase::Running => (BLUE_600, false, drive.fraction),
        DrivePhase::Completed => (SUCCESS, false, 1.0),
        DrivePhase::Failed => (DANGER, false, drive.fraction),
        DrivePhase::Cancelled => (INK_MUTED, false, drive.fraction),
    };
    let percent = (fraction * 100.0).round().clamp(0.0, 100.0);

    // 环心：完成时给一个对勾，其余情况显示盘符与百分比。
    let center: Element<'a, VersionMessage> = if drive.phase == DrivePhase::Completed {
        astra_ui::icons::icon(Icon::Check, 22, SUCCESS).into()
    } else {
        column![
            raw(drive.letter.clone()).size(15).font(crate::core::typography::medium()),
            raw(format!("{percent:.0}%")).size(10).color(INK_MUTED),
        ]
        .spacing(1)
        .align_x(Alignment::Center)
        .into()
    };

    let ring = stack![
        ring::ring(ring::Ring {
            fraction,
            indeterminate,
            phase: animation_phase,
            color,
        }),
        container(center)
            .width(Fill)
            .height(Fill)
            .align_x(Alignment::Center)
            .align_y(Alignment::Center),
    ]
    .width(ring::DIAMETER)
    .height(ring::DIAMETER);

    // 卷标可能很长，截断后放在盘符旁，避免撑破网格列宽。
    let caption = if drive.label.is_empty() {
        drive.letter.clone()
    } else {
        format!("{} {}", drive.letter, truncate_path(&drive.label, 10))
    };

    let cell = container(
        column![
            ring,
            raw(caption).size(11).font(crate::core::typography::medium()),
            textf("local.scan.drive.found", &[("count", &drive.found)])
                .size(10)
                .color(INK_MUTED),
            text(drive.phase.label_key()).size(10).color(INK_MUTED),
        ]
        .spacing(4)
        .align_x(Alignment::Center),
    )
    .width(ring::CELL_WIDTH)
    .align_x(Alignment::Center);

    // 悬停展示磁盘细节：容量与跳过条目是「为什么结果可能不完整」的直接线索。
    iced::widget::tooltip(
        cell,
        container(drive_detail(drive))
            .padding(8)
            .max_width(280)
            .style(super::install_modal_style),
        iced::widget::tooltip::Position::Top,
    )
    .into()
}

/// 磁盘圆环的悬停详情。
fn drive_detail<'a>(drive: &DriveProgress) -> Element<'a, VersionMessage> {
    let mut detail = column![raw(drive.root.clone())
        .size(11)
        .font(crate::core::typography::medium())]
    .spacing(3);
    if drive.total_bytes > 0 {
        let used = drive.total_bytes.saturating_sub(drive.free_bytes);
        detail = detail.push(
            textf(
                "local.scan.drive.usage",
                &[
                    ("used", &crate::utils::format_bytes(used)),
                    ("total", &crate::utils::format_bytes(drive.total_bytes)),
                ],
            )
            .size(10)
            .color(INK_MUTED),
        );
    }
    if drive.skipped > 0 {
        detail = detail.push(
            textf("local.scan.drive.skipped", &[("count", &drive.skipped)])
                .size(10)
                .color(INK_MUTED),
        );
    }
    detail.into()
}

/// 磁盘进度网格：一行四个，整体居中。
///
/// 最后一行用等宽空位补齐，保证上下两行的列宽一致、整体仍然居中，
/// 否则 3 个磁盘时第二行会向右偏移半个格子。
fn drive_grid<'a>(drives: &'a [DriveProgress], animation_phase: f32) -> Element<'a, VersionMessage> {
    let mut rows = column![].spacing(14).width(Fill);
    for chunk in drives.chunks(DRIVE_COLUMNS) {
        let mut line = row![].spacing(6).align_y(Alignment::Start);
        for drive in chunk {
            line = line.push(drive_cell(drive, animation_phase));
        }
        for _ in chunk.len()..DRIVE_COLUMNS {
            line = line.push(container(space::Space::new()).width(ring::CELL_WIDTH));
        }
        rows = rows.push(container(line).width(Fill).align_x(Alignment::Center));
    }
    rows.into()
}

fn log_view(logs: &VecDeque<String>, height: f32) -> Element<'_, VersionMessage> {
    scrollable(
        column(logs.iter().map(|line| raw(line).size(11).into()))
            .spacing(3)
            .width(Fill),
    )
    .height(height)
    .into()
}

fn error_view(error: Option<&LocalError>) -> Element<'_, VersionMessage> {
    match error {
        Some(error) => column![
            text(error.message).size(12).color(astra_ui::DANGER),
            scrollable(raw(&error.detail).size(11)).height(45)
        ]
        .spacing(4)
        .into(),
        None => space::vertical().height(0).into(),
    }
}

pub fn modal_view(state: &LocalUiState) -> Option<Element<'_, VersionMessage>> {
    if state.install.visible {
        let task = &state.install;
        let mut footer = row![space::horizontal()].spacing(8);
        if !task.running {
            if task.error.is_some()
                && let Some(path) = &task.path
            {
                footer = footer.push(
                    button(text("local.install.retry"))
                        .on_press(VersionMessage::InstallLocalDependencies(path.clone()))
                        .style(button_style(ButtonVariant::Primary)),
                );
            }
            footer = footer.push(
                button(text("resources.import.close"))
                    .on_press(VersionMessage::CloseLocalInstall)
                    .style(button_style(ButtonVariant::Secondary)),
            );
        }
        let body = column![
            text("local.install.title").size(19).font(crate::core::typography::medium()),
            scrollable(
                raw(task.path.as_deref().unwrap_or(""))
                    .size(12)
                    .color(INK_MUTED)
            )
            .height(36),
            text(if task.running {
                "local.install.installing"
            } else if task.error.is_some() {
                "local.deps.install_failed"
            } else {
                "local.install.completed"
            })
            .size(13),
            log_view(&task.logs, 190.0),
            error_view(task.error.as_ref()),
            footer,
        ]
        .spacing(14)
        .into();
        return Some(modal(body, VersionMessage::LocalModalInteract));
    }
    let scan = &state.scan;
    if scan.cancel_confirm_visible && scan.phase.active() {
        let body = column![
            row![
                text("versions.local.confirm_warning").size(17).font(crate::core::typography::medium()),
                space::horizontal(),
                button(icons::icon(Icon::X, 15, INK_MUTED))
                    .on_press(VersionMessage::KeepScanning)
                    .style(button_style(ButtonVariant::Ghost))
            ]
            .align_y(Alignment::Center),
            text("local.scan.cancel_confirm").size(13),
            row![
                button(text("versions.local.confirm_ok"))
                    .on_press(VersionMessage::CancelScan)
                    .style(button_style(ButtonVariant::Primary)),
                button(text("tavern.sync.import.cancel"))
                    .on_press(VersionMessage::KeepScanning)
                    .style(button_style(ButtonVariant::Secondary)),
            ]
            .spacing(10),
        ]
        .spacing(14)
        .into();
        return Some(sized_modal(body, VersionMessage::KeepScanning, 380.0));
    }
    if !scan.visible {
        return None;
    }
    let indicator: Element<'_, VersionMessage> = if scan.phase.active() {
        astra_ui::ProgressCircle::new(0.0)
            .is_indeterminate(true)
            .size(astra_ui::ProgressCircleSize::Small)
            .animation_phase(f32::from(state.animation_frame) / 20.0)
            .into()
    } else {
        icons::icon(
            if scan.phase == ScanPhase::Completed {
                Icon::CircleCheck
            } else {
                Icon::Info
            },
            18,
            INK_MUTED,
        )
        .into()
    };
    let mut status_line = row![indicator, text(scan.status_key()).size(13)]
        .spacing(8)
        .align_y(Alignment::Center);
    if !scan.progress.drives.is_empty() {
        status_line = status_line.push(
            textf(
                "local.scan.drives_progress",
                &[
                    ("done", &scan.progress.finished_drives()),
                    ("total", &scan.progress.drives.len()),
                ],
            )
            .size(11)
            .color(INK_MUTED),
        );
    }
    if scan.progress.threads > 0 {
        // 让用户看得见「占用核心数」设置到底分到了多少线程。
        status_line = status_line.push(space::horizontal()).push(
            textf("local.scan.threads", &[("count", &scan.progress.threads)])
                .size(11)
                .color(INK_MUTED),
        );
    }
    let mut body = column![
        row![
            text("local.scan.title").size(18).font(crate::core::typography::medium()),
            space::horizontal(),
            button(icons::icon(Icon::X, 16, INK_MUTED))
                .on_press(VersionMessage::CloseScanLog)
                .style(button_style(ButtonVariant::Ghost))
        ]
        .align_y(Alignment::Center),
        status_line,
        text("local.scan.hint").size(11).color(INK_MUTED),
    ]
    .spacing(12);
    // 磁盘进度网格居中展示：有多少块盘就有多少个环，一行四个。
    if !scan.progress.drives.is_empty() {
        let grid = drive_grid(
            &scan.progress.drives,
            f32::from(state.animation_frame) / 20.0,
        );
        // 超过两行的磁盘数（外接硬盘柜等）会顶破弹窗高度，这里加一层滚动兜底。
        if scan.progress.drives.len() > DRIVE_COLUMNS * 2 {
            body = body.push(scrollable(grid).height(320.0));
        } else {
            body = body.push(grid);
        }
    }
    body = body.push(
        row![
            text("local.scan.checked").size(11),
            raw(scan.progress.checked.to_string()).size(11),
            text("local.scan.found").size(11),
            raw(scan.progress.found.to_string()).size(11),
            text("local.scan.added").size(11),
            raw(scan.added.to_string()).size(11),
            text("local.scan.duplicates").size(11),
            raw(scan.duplicates.to_string()).size(11),
        ]
        .spacing(8),
    );
    if !scan.recent_paths.is_empty() {
        let recent = column(scan.recent_paths.iter().map(|path| {
            iced::widget::tooltip(
                raw(truncate_path(path, 60)).size(11).color(INK_MUTED),
                container(raw(path).size(10))
                    .padding(8)
                    .max_width(420)
                    .style(super::install_modal_style),
                iced::widget::tooltip::Position::Bottom,
            )
            .into()
        }))
        .spacing(5);
        body = body.push(recent);
    }
    if let Some(error) = &scan.error {
        body = body.push(error_view(Some(error)));
    }
    if scan.show_details {
        body = body.push(log_view(&scan.logs, 160.0));
    }
    let action = if scan.phase.active() {
        button(text("local.scan.cancel"))
            .on_press(VersionMessage::RequestCancelScan)
            .style(button_style(ButtonVariant::Secondary))
    } else {
        button(text("resources.tooltip.rescan"))
            .on_press(VersionMessage::ScanLocal)
            .style(button_style(ButtonVariant::Primary))
    };
    body = body.push(
        row![
            button(text(if scan.show_details {
                "local.scan.hide_details"
            } else {
                "local.scan.show_details"
            }))
            .on_press(VersionMessage::ToggleScanDetails)
            .style(button_style(ButtonVariant::Ghost)),
            space::horizontal(),
            action,
            button(text("resources.import.close"))
                .on_press(VersionMessage::CloseScanLog)
                .style(button_style(ButtonVariant::Ghost)),
        ]
        .spacing(8)
        .align_y(Alignment::Center),
    );
    // 宽度按「四列进度环 + 内边距」定，保证一行四个时不会挤在一起。
    Some(sized_modal(
        body.into(),
        VersionMessage::CloseScanLog,
        560.0,
    ))
}

pub fn toast_view(state: &LocalUiState) -> Option<Element<'_, VersionMessage>> {
    let toast = state.toast.as_ref()?;
    Some(
        container(astra_ui::toast(
            t_in(toast.message, current_language()),
            &toast.detail,
            if toast.danger {
                astra_ui::ToastVariant::Danger
            } else {
                astra_ui::ToastVariant::Success
            },
            None,
            VersionMessage::DismissLocalToast,
            VersionMessage::LocalModalInteract,
        ))
        .max_width(680)
        .padding(16)
        .into(),
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn closing_progress_keeps_scanning_and_recent_paths_are_bounded() {
        let mut state = LocalUiState::default();
        state.scan.phase = ScanPhase::Running;
        state.scan.visible = true;
        for i in 0..8 {
            state.scan.observe_path(format!("/fixture/{i}"));
        }
        assert_eq!(state.scan.recent_paths.len(), 5);
        assert_eq!(state.scan.recent_paths.front().unwrap(), "/fixture/3");
        state.close_scan();
        assert_eq!(state.scan.phase, ScanPhase::Running);
        assert!(!state.scan.visible);
    }
    #[test]
    fn success_auto_hides_but_does_not_discard_logs() {
        let mut state = LocalUiState::default();
        state.scan.visible = true;
        state.scan.phase = ScanPhase::Completed;
        state.scan.auto_hide_at = Some(Instant::now() - Duration::from_secs(1));
        append_log(&mut state.scan.logs, "/fixture".into());
        state.tick();
        assert!(!state.scan.visible);
        assert_eq!(state.scan.logs.len(), 1);
    }
    /// 全盘扫描时目录切换极快，详细日志必须限流，否则日志会被瞬间冲掉。
    #[test]
    fn scan_log_is_throttled_but_recent_paths_keep_updating() {
        let mut state = ScanState::default();
        state.append_scan_log("/fixture/a".into());
        state.append_scan_log("/fixture/b".into());
        assert_eq!(state.logs.len(), 1, "限流窗口内的第二条日志应被丢弃");

        // 把上一次写入时间往前推，越过窗口后应重新允许写入。
        state.last_log_at = Some(Instant::now() - SCAN_LOG_INTERVAL);
        state.append_scan_log("/fixture/c".into());
        assert_eq!(state.logs.len(), 2);

        // 最近路径列表不受日志限流影响，始终反映最新的扫描位置。
        state.observe_path("/fixture/a".into());
        state.observe_path("/fixture/b".into());
        state.observe_path("/fixture/a".into());
        assert_eq!(state.recent_paths.len(), 2, "重复路径不应重复占位");
    }

    #[test]
    fn path_labels_are_compact_without_breaking_unicode() {
        let path = format!("/用户/{}\n/end", "很长的目录".repeat(30));
        let label = truncate_path(&path, 60);
        assert!(label.chars().count() <= 60);
        assert!(label.starts_with("/用户/"));
        assert!(label.ends_with("/end"));
        assert!(!label.contains('\n'));
    }
    #[test]
    fn detailed_log_memory_is_bounded() {
        let mut logs = VecDeque::new();
        for _ in 0..800 {
            append_log(&mut logs, "line".into());
        }
        assert_eq!(logs.len(), 600);
    }
}
