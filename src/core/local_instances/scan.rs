//! 全盘扫描的共享事件与进度快照：核心层产出数据，界面层只负责渲染。
//!
//! 一轮扫描会同时推进多个磁盘，因此进度是「整体汇总 + 每盘独立」两层结构：
//! 界面既需要总计数，也需要为每个磁盘画一条独立的圆形进度条。

use super::{LocalError, LocalInstance};

/// 单个磁盘的扫描阶段。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum DrivePhase {
    /// 已枚举到该磁盘，但尚未轮到它（并发预算被其他磁盘占用）。
    #[default]
    Pending,
    /// 正在遍历。
    Running,
    /// 遍历完成。
    Completed,
    /// 该盘扫描失败：卷根目录读不进去，或工作线程异常退出。
    Failed,
    /// 用户取消导致未完成。
    Cancelled,
}

impl DrivePhase {
    /// 状态文案键；界面按当前语言渲染。
    pub const fn label_key(self) -> &'static str {
        match self {
            Self::Pending => "local.scan.drive.pending",
            Self::Running => "local.scan.drive.running",
            Self::Completed => "local.scan.drive.completed",
            Self::Failed => "local.scan.drive.failed",
            Self::Cancelled => "local.scan.drive.cancelled",
        }
    }

    /// 该阶段是否已经结束（不再产生新事件）。
    pub const fn finished(self) -> bool {
        matches!(self, Self::Completed | Self::Failed | Self::Cancelled)
    }
}

/// 单个磁盘的扫描进度快照。
#[derive(Debug, Clone, Default)]
pub struct DriveProgress {
    /// 盘符，如 `C:`。
    pub letter: String,
    /// 卷标；没有卷标时为空字符串。
    pub label: String,
    /// 扫描根目录，如 `C:\`。
    pub root: String,
    /// 卷总容量（字节）。
    pub total_bytes: u64,
    /// 卷可用容量（字节）。
    pub free_bytes: u64,
    /// 遍历完成度，取值 `0.0..=1.0`。
    ///
    /// 由「已处理目录数 / 已发现目录数」得出：目录是本轮扫描的工作单元，
    /// 该比值在扫描结束时必然收敛到 1.0，且不需要对每个文件额外做一次 `stat`。
    pub fraction: f32,
    pub phase: DrivePhase,
    /// 已检查的目录项数量。
    pub checked: u64,
    /// 该磁盘上发现的实例数量。
    pub found: u64,
    /// 当前正在遍历的目录（用于日志与提示）。
    pub current: String,
    /// 跳过（权限不足、路径异常等）的条目数量。
    pub skipped: u64,
}

/// 一轮扫描的整体进度。
#[derive(Debug, Clone, Default)]
pub struct ScanProgress {
    /// 跨磁盘汇总：已检查的目录项数量。
    pub checked: u64,
    /// 跨磁盘汇总：已发现的实例数量。
    pub found: u64,
    /// 已耗时（秒）。
    pub elapsed_seconds: u64,
    /// 本轮实际使用的并发线程数（由「占用核心数」设置折算而来）。
    pub threads: usize,
    /// 每个磁盘的独立进度，顺序与磁盘枚举结果一致。
    pub drives: Vec<DriveProgress>,
}

impl ScanProgress {
    /// 汇总进度里「最近活跃」的目录，用于标题栏与日志。
    pub fn current_path(&self) -> &str {
        self.drives
            .iter()
            .find(|drive| drive.phase == DrivePhase::Running && !drive.current.is_empty())
            .or_else(|| self.drives.iter().find(|drive| !drive.current.is_empty()))
            .map(|drive| drive.current.as_str())
            .unwrap_or("")
    }

    /// 已经结束的磁盘数量。
    pub fn finished_drives(&self) -> usize {
        self.drives
            .iter()
            .filter(|drive| drive.phase.finished())
            .count()
    }
}

#[derive(Debug, Clone, Default)]
pub struct ScanReport {
    pub progress: ScanProgress,
    /// 结果可能不完整：出现过权限不足、目录读取失败或用户取消。
    pub partial: bool,
}

#[derive(Debug, Clone)]
pub enum ScanEvent {
    /// 磁盘清单已枚举完成，界面据此立刻画出完整网格。
    Drives(Vec<DriveProgress>),
    /// 单个磁盘的进度快照。
    Drive(DriveProgress),
    /// 发现一个本地实例。
    Found(LocalInstance),
    /// 非致命警告（权限不足、路径无法表示等）。
    Warning(LocalError),
    /// 整轮扫描结束。
    Finished(Result<ScanReport, LocalError>),
}
