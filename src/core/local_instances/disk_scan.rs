//! 全盘本地实例扫描：每个磁盘一个 `jwalk` 并行遍历器。
//!
//! 旧实现只遍历用户主目录，用户把酒馆装在 `D:\` 或移动硬盘上就永远找不到。
//! 现在改为「枚举所有本地卷 → 每卷一个工作线程 → 全盘查找 `package.json`」，
//! 并且：
//!
//! * **并发受控**：并发线程数由设置里的「占用核心数」折算，磁盘数超过预算时
//!   按波次排队，避免一次性把机器所有核心吃满；
//! * **主动剪枝**：系统目录、依赖目录、各类缓存目录一律不进入，
//!   全盘扫描的绝大多数时间都花在这些目录上；
//! * **进度可解释**：完成度按「已处理目录 / 已发现目录」计算，
//!   不需要对每个文件额外做一次 `stat`（全盘 `stat` 会把扫描时间翻倍）。
//!
//! 扫描过程完全不写磁盘，只读取目录项与 `package.json`。

use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicU8, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use jwalk::{Parallelism, WalkDirGeneric};

use super::scan::{DrivePhase, DriveProgress, ScanEvent, ScanProgress, ScanReport};
use super::{LocalError, LocalErrorKind, display_path, inspect_package};

/// 扫描进度上报间隔：过密会拖慢遍历，过疏则界面显得卡住。
const PROGRESS_INTERVAL: Duration = Duration::from_millis(120);

/// 单个磁盘最多上报的警告条数。
///
/// 全盘扫描时权限不足会成片出现（系统还原点、其他用户目录等），
/// 逐条上报既刷屏又拖慢速度；超出部分只计入 `skipped`。
const MAX_WARNINGS_PER_DRIVE: u64 = 3;

/// Win32 卷类型常量（`GetDriveTypeW` 返回值）。
///
/// 这里直接写数值而不是从 `windows-sys` 引入：这些值由 Win32 固定，
/// 而引入常量所在的 feature 会额外带进一整个系统编程模块。
const DRIVE_REMOVABLE: u32 = 2;
const DRIVE_FIXED: u32 = 3;

/// 任意层级都要剪枝的目录名。
///
/// 这些目录要么体量巨大且不可能存放酒馆实例，要么本身就是缓存，
/// 遍历它们只会把扫描时间拉长几个数量级。
const PRUNED_DIRECTORY_NAMES: &[&str] = &[
    // 依赖与版本控制
    "node_modules",
    "site-packages",
    "vendor",
    "bower_components",
    "__pycache__",
    ".git",
    ".svn",
    ".hg",
    // 系统与回收站
    "AppData",
    "$RECYCLE.BIN",
    "System Volume Information",
    "Config.Msi",
    "Recovery",
    // 浏览器 / Electron / 运行时缓存
    "Cache",
    "Caches",
    "CacheStorage",
    "Code Cache",
    "GPUCache",
    "DawnCache",
    "DawnGraphiteCache",
    "DawnWebGPUCache",
    "ShaderCache",
    "Crashpad",
    "CrashDumps",
    "CachedData",
    // 构建产物与工具缓存
    "target",
    "dist-info",
    ".gradle",
    ".m2",
    ".nuget",
    ".cargo",
    ".rustup",
    ".conda",
    ".npm",
    ".pnpm-store",
    ".yarn",
    ".venv",
    "venv",
    "Temp",
    "temp",
    "tmp",
];

/// 只在盘根剪枝的系统目录名。
///
/// 其他位置的同名目录可能属于用户自己的工程，不能一刀切。
const PRUNED_ROOT_DIRECTORIES: &[&str] = &[
    "Windows",
    "Windows.old",
    "Program Files",
    "Program Files (x86)",
    "ProgramData",
    "PerfLogs",
    "MSOCache",
    "OneDriveTemp",
    "Documents and Settings",
    "All Users",
    "Default User",
    "Intel",
    "AMD",
    "NVIDIA",
    "Boot",
    "EFI",
    "$WinREAgent",
    "$SysReset",
    "$GetCurrent",
    "$Windows.~BT",
    "$Windows.~WS",
    "System Volume Information",
    "$RECYCLE.BIN",
];

/// 事件接收端：多线程共享，只负责投递。
type Sink = Arc<dyn Fn(ScanEvent) + Send + Sync>;

/// 目录遍历计数器。
///
/// `visited` 每处理完一个目录加一；`known` 每发现一个「将要下探的目录」加一。
/// 盘根也算一个目录，由首次回调统一计入，因此两个计数器的口径完全一致：
/// 目录一定先被发现、后被处理，`visited <= known` 恒成立，比值天然落在 `0.0..=1.0`，
/// 且扫描结束时 `visited == known`。
#[derive(Debug, Default)]
struct WalkCounter {
    visited: AtomicU64,
    known: AtomicU64,
}

impl WalkCounter {
    fn start() -> Self {
        Self::default()
    }

    fn fraction(&self) -> f32 {
        let known = self.known.load(Ordering::Relaxed);
        if known == 0 {
            return 0.0;
        }
        let visited = self.visited.load(Ordering::Relaxed);
        (visited as f32 / known as f32).clamp(0.0, 1.0)
    }

    fn visited(&self) -> u64 {
        self.visited.load(Ordering::Relaxed)
    }
}

/// 扫描时需要整体排除的启动器自身目录。
#[derive(Debug, Clone)]
struct PruneRules {
    /// 启动器在线下载实例目录。
    online: PathBuf,
    /// 启动器根目录（`%AppData%/AstraBrew Launcher`）。
    launcher_root: PathBuf,
    /// 启动器临时目录（`%Temp%/astrabrew-launcher`）。
    launcher_temp: PathBuf,
}

impl PruneRules {
    fn new(online: &Path) -> Self {
        let paths = crate::utils::app_paths();
        Self {
            online: super::normalized_path(online),
            launcher_root: super::normalized_path(&paths.root),
            launcher_temp: super::normalized_path(&paths.temp),
        }
    }

    /// 该条目是否应当被剪枝（不进入其子树，也不计入结果）。
    fn should_prune(&self, path: &Path, drive_root: &Path) -> bool {
        // 启动器自己的目录由启动器管理，其中 `lib/` 还内置了 Node.js 与 MinGit，
        // 体积可观且不可能被识别为用户实例。
        if path.starts_with(&self.online)
            || path.starts_with(&self.launcher_root)
            || path.starts_with(&self.launcher_temp)
        {
            return true;
        }

        // 只遍历磁盘内部：符号链接 / 联接点指向外部时剪枝，避免绕出扫描范围。
        if !path.starts_with(drive_root) {
            return true;
        }

        // 非 UTF-8 名称无法参与规则匹配；保守放行，交给 `package.json` 判定兜底。
        let Some(name) = path.file_name().and_then(|name| name.to_str()) else {
            return false;
        };

        if matches_any(name, PRUNED_DIRECTORY_NAMES) {
            return true;
        }

        // 隐藏目录（`.` 开头）在 Windows 上基本都是工具缓存或版本控制元数据。
        if name.starts_with('.') {
            return true;
        }

        // 系统目录只在盘根剪枝，其他位置的同名目录可能属于用户工程。
        path.parent() == Some(drive_root) && matches_any(name, PRUNED_ROOT_DIRECTORIES)
    }
}

/// 大小写无关的名称匹配：Windows 目录名保留原始大小写，但比较不该区分大小写。
fn matches_any(name: &str, candidates: &[&str]) -> bool {
    candidates
        .iter()
        .any(|candidate| name.eq_ignore_ascii_case(candidate))
}

/// 一个待扫描的本地卷。
#[derive(Debug, Clone)]
struct DriveInfo {
    /// 盘符，如 `C:`。
    letter: String,
    /// 卷标；无卷标时为空。
    label: String,
    /// 扫描根目录。
    root: PathBuf,
    /// 展示用根目录，如 `C:\`。
    display_root: String,
    total_bytes: u64,
    free_bytes: u64,
}

/// 单个磁盘的共享状态；工作线程写，界面线程通过事件快照读。
struct DriveShared {
    info: DriveInfo,
    counter: Arc<WalkCounter>,
    checked: AtomicU64,
    found: AtomicU64,
    skipped: AtomicU64,
    warnings: AtomicU64,
    phase: AtomicU8,
    current: Mutex<String>,
}

impl DriveShared {
    fn new(info: DriveInfo) -> Self {
        Self {
            info,
            counter: Arc::new(WalkCounter::start()),
            checked: AtomicU64::new(0),
            found: AtomicU64::new(0),
            skipped: AtomicU64::new(0),
            warnings: AtomicU64::new(0),
            phase: AtomicU8::new(phase_code(DrivePhase::Pending)),
            current: Mutex::new(String::new()),
        }
    }

    fn phase(&self) -> DrivePhase {
        phase_from_code(self.phase.load(Ordering::Relaxed))
    }

    fn set_phase(&self, phase: DrivePhase) {
        self.phase.store(phase_code(phase), Ordering::Relaxed);
    }

    /// 生成界面可直接渲染的快照。
    fn snapshot(&self) -> DriveProgress {
        let phase = self.phase();
        DriveProgress {
            letter: self.info.letter.clone(),
            label: self.info.label.clone(),
            root: self.info.display_root.clone(),
            total_bytes: self.info.total_bytes,
            free_bytes: self.info.free_bytes,
            // 完成后强制收敛到 1.0：个别目录读取失败时 `visited` 会略小于 `known`。
            fraction: if phase == DrivePhase::Completed {
                1.0
            } else {
                self.counter.fraction()
            },
            phase,
            checked: self.checked.load(Ordering::Relaxed),
            found: self.found.load(Ordering::Relaxed),
            current: self
                .current
                .lock()
                .map(|current| current.clone())
                .unwrap_or_default(),
            skipped: self.skipped.load(Ordering::Relaxed),
        }
    }
}

const fn phase_code(phase: DrivePhase) -> u8 {
    match phase {
        DrivePhase::Pending => 0,
        DrivePhase::Running => 1,
        DrivePhase::Completed => 2,
        DrivePhase::Failed => 3,
        DrivePhase::Cancelled => 4,
    }
}

const fn phase_from_code(code: u8) -> DrivePhase {
    match code {
        1 => DrivePhase::Running,
        2 => DrivePhase::Completed,
        3 => DrivePhase::Failed,
        4 => DrivePhase::Cancelled,
        _ => DrivePhase::Pending,
    }
}

/// 把设置里的「占用核心数」折算成本轮扫描可用的工作线程总数。
///
/// 取值策略集中在核心层：界面只提供「自动 / 一半 / 全部」三档，
/// 具体换算与「单核机器怎么办」的兜底都由这里决定。
pub fn thread_budget(cores: crate::core::settings::CpuCores) -> usize {
    let available = std::thread::available_parallelism()
        .map(|count| count.get())
        .unwrap_or(2);
    cores.thread_budget(available)
}

/// 全盘扫描入口。
///
/// * `online`：启动器在线实例目录，整体排除；
/// * `budget`：允许占用的工作线程总数，由设置里的「占用核心数」折算；
/// * `cancel`：置位后所有工作线程尽快收尾；
/// * `emit`：事件接收端，必须可跨线程共享（内部不得使用 `FnMut` 状态）。
///
/// `cancel` 取 `Arc` 而不是引用：工作线程需要 `'static` 的所有权，
/// 由调用方持有同一个 `Arc` 即可在任意时刻置位。
pub fn run(
    online: PathBuf,
    budget: usize,
    cancel: Arc<AtomicBool>,
    emit: impl Fn(ScanEvent) + Send + Sync + 'static,
) {
    let sink: Sink = Arc::new(emit);
    let result = scan_all(&online, budget, &cancel, &sink);
    sink(ScanEvent::Finished(result));
}

fn scan_all(
    online: &Path,
    budget: usize,
    cancel: &Arc<AtomicBool>,
    sink: &Sink,
) -> Result<ScanReport, LocalError> {
    let started = Instant::now();
    let drives = enumerate_drives();
    if drives.is_empty() {
        return Err(LocalError::new("local.scan.no_drive", ""));
    }

    let budget = budget.max(1);
    // 并发度取「磁盘数」与「线程预算」的较小值：
    // 磁盘少时按盘并发并把预算摊到每个盘的内部并行度上；
    // 磁盘多时按预算分批排队，避免一次性开满线程。
    let concurrency = drives.len().min(budget);
    let per_drive = (budget / concurrency).max(1);
    let rules = PruneRules::new(online);

    let shared: Vec<Arc<DriveShared>> = drives
        .into_iter()
        .map(|info| Arc::new(DriveShared::new(info)))
        .collect();
    // 先把完整磁盘清单交给界面，网格可以立刻画出来，不必等第一波扫描开始。
    sink(ScanEvent::Drives(
        shared.iter().map(|drive| drive.snapshot()).collect(),
    ));

    let mut partial = false;
    'waves: for wave in shared.chunks(concurrency) {
        let mut handles = Vec::with_capacity(wave.len());
        for drive in wave {
            if cancel.load(Ordering::Relaxed) {
                break 'waves;
            }
            drive.set_phase(DrivePhase::Running);
            sink(ScanEvent::Drive(drive.snapshot()));
            handles.push((
                drive.clone(),
                spawn_drive(
                    drive.clone(),
                    rules.clone(),
                    per_drive,
                    Arc::clone(cancel),
                    sink.clone(),
                ),
            ));
        }
        for (drive, handle) in handles {
            // 工作线程内部已把所有错误转成事件，join 失败只可能是 panic；
            // 此时该盘必须显式标记为失败，绝不能让它停在「已完成」上。
            if handle.join().is_err() {
                drive.set_phase(DrivePhase::Failed);
            }
        }
        if cancel.load(Ordering::Relaxed) {
            break;
        }
    }

    let cancelled = cancel.load(Ordering::Relaxed);
    for drive in &shared {
        if !drive.phase().finished() {
            // 取消后不再有线程推进，尚未开始的磁盘一并标记为取消。
            drive.set_phase(if cancelled {
                DrivePhase::Cancelled
            } else {
                DrivePhase::Completed
            });
        }
        if drive.phase() != DrivePhase::Completed || drive.skipped.load(Ordering::Relaxed) > 0 {
            partial = true;
        }
    }
    let snapshots: Vec<DriveProgress> = shared.iter().map(|drive| drive.snapshot()).collect();
    sink(ScanEvent::Drives(snapshots.clone()));

    if cancelled {
        return Err(LocalError::cancelled());
    }

    let progress = ScanProgress {
        checked: snapshots.iter().map(|drive| drive.checked).sum(),
        found: snapshots.iter().map(|drive| drive.found).sum(),
        elapsed_seconds: started.elapsed().as_secs(),
        threads: per_drive * concurrency,
        drives: snapshots,
    };
    Ok(ScanReport { progress, partial })
}

/// 为一个磁盘启动工作线程。
fn spawn_drive(
    drive: Arc<DriveShared>,
    rules: PruneRules,
    threads: usize,
    cancel: Arc<AtomicBool>,
    sink: Sink,
) -> std::thread::JoinHandle<()> {
    std::thread::spawn(move || {
        scan_one_drive(&drive, &rules, threads, &cancel, &sink);
    })
}

/// 遍历单个磁盘。
fn scan_one_drive(
    drive: &Arc<DriveShared>,
    rules: &PruneRules,
    threads: usize,
    cancel: &Arc<AtomicBool>,
    sink: &Sink,
) {
    let drive_root = drive.info.root.clone();
    let process_drive = drive.clone();
    let process_rules = rules.clone();
    let process_sink = sink.clone();
    // 回调需要 `'static`，取消标志必须交出所有权而不是借用。
    let process_cancel = Arc::clone(cancel);

    let walker = WalkDirGeneric::<(Arc<WalkCounter>, ())>::new(&drive_root)
        .skip_hidden(false)
        .follow_links(false)
        // 每个磁盘用独立的 rayon 池，池大小来自核心预算，避免多盘同时开满全局池。
        .parallelism(Parallelism::RayonNewPool(threads))
        .root_read_dir_state(drive.counter.clone())
        .process_read_dir(move |_depth, path, counter, children| {
            counter.visited.fetch_add(1, Ordering::Relaxed);
            let cancelled = process_cancel.load(Ordering::Relaxed);

            let mut kept = Vec::with_capacity(children.len());
            for child in children.drain(..) {
                let Ok(entry) = child else {
                    process_drive.skipped.fetch_add(1, Ordering::Relaxed);
                    continue;
                };
                process_drive.checked.fetch_add(1, Ordering::Relaxed);

                if entry.file_name().to_str() == Some("package.json") {
                    inspect_candidate(&entry.path(), &process_drive, &process_rules, &process_sink);
                }

                if cancelled {
                    // 取消时不再下探，把子树全部丢掉，遍历会很快收敛。
                    continue;
                }
                if !process_rules.should_prune(&entry.path(), &drive_root) {
                    kept.push(Ok(entry));
                }
            }

            if !cancelled {
                for entry in &kept {
                    if let Ok(entry) = entry
                        && entry.read_children_path.is_some()
                    {
                        counter.known.fetch_add(1, Ordering::Relaxed);
                    }
                }
            }

            // 供界面显示「当前目录」；每目录一次加锁，相对 read_dir 系统调用可以忽略。
            // 盘根那次回调拿到的是盘根的父路径（空），不能覆盖掉已有值。
            if !path.as_os_str().is_empty()
                && let Ok(mut current) = process_drive.current.lock()
            {
                *current = display_path(path);
            }

            *children = kept;
        });

    let mut last_report = Instant::now();
    for entry in walker {
        if cancel.load(Ordering::Relaxed) {
            drive.set_phase(DrivePhase::Cancelled);
            sink(ScanEvent::Drive(drive.snapshot()));
            return;
        }
        if entry.is_err() {
            drive.skipped.fetch_add(1, Ordering::Relaxed);
        }
        if last_report.elapsed() >= PROGRESS_INTERVAL {
            sink(ScanEvent::Drive(drive.snapshot()));
            last_report = Instant::now();
        }
    }

    drive.set_phase(if cancel.load(Ordering::Relaxed) {
        DrivePhase::Cancelled
    } else if drive.counter.visited() == 0 && drive.skipped.load(Ordering::Relaxed) > 0 {
        // 一个目录都没读成功：卷根本进不去（未就绪、被独占），属于失败而不是「空盘」。
        DrivePhase::Failed
    } else {
        DrivePhase::Completed
    });
    sink(ScanEvent::Drive(drive.snapshot()));
}

/// 校验一个 `package.json` 是否为可用的本地实例。
fn inspect_candidate(path: &Path, drive: &DriveShared, rules: &PruneRules, sink: &Sink) {
    // 非 UTF-8 路径无法交给后续的字符串处理流程，只做警告不视为失败。
    if path.to_str().is_none() {
        drive.skipped.fetch_add(1, Ordering::Relaxed);
        warn(
            drive,
            sink,
            LocalError::new("local.scan.path_not_text", display_path(path)),
        );
        return;
    }

    match inspect_package(path, &rules.online) {
        Ok(instance) => {
            drive.found.fetch_add(1, Ordering::Relaxed);
            sink(ScanEvent::Found(instance));
        }
        // 不是酒馆实例（或属于在线实例）是正常情况，不计入警告。
        Err(error)
            if matches!(
                error.kind,
                LocalErrorKind::InvalidInstance | LocalErrorKind::OnlineInstance
            ) => {}
        Err(error) => {
            drive.skipped.fetch_add(1, Ordering::Relaxed);
            warn(drive, sink, error);
        }
    }
}

/// 限流上报警告：全盘扫描中权限不足会成片出现。
fn warn(drive: &DriveShared, sink: &Sink, error: LocalError) {
    if drive.warnings.fetch_add(1, Ordering::Relaxed) < MAX_WARNINGS_PER_DRIVE {
        sink(ScanEvent::Warning(error));
    }
}

/// 枚举所有可扫描的本地卷。
///
/// 只保留固定磁盘与可移动磁盘：网络驱动器速度不可控，光驱与内存盘不可能存放实例。
/// 未就绪的卷（空读卡器、未挂载分区）会被 `GetDiskFreeSpaceExW` 过滤掉。
#[cfg(windows)]
fn enumerate_drives() -> Vec<DriveInfo> {
    use windows_sys::Win32::Storage::FileSystem::{
        GetDiskFreeSpaceExW, GetDriveTypeW, GetLogicalDrives, GetVolumeInformationW,
    };

    // SAFETY: 无参数、无副作用，返回 26 位盘符位图。
    let mask = unsafe { GetLogicalDrives() };
    let mut drives = Vec::new();

    for index in 0..26u32 {
        if mask & (1 << index) == 0 {
            continue;
        }
        let letter = char::from(b'A' + index as u8);
        let display_root = format!("{letter}:\\");
        let wide: Vec<u16> = display_root
            .encode_utf16()
            .chain(std::iter::once(0))
            .collect();

        // SAFETY: `wide` 以 NUL 结尾，且在调用期间存活。
        let kind = unsafe { GetDriveTypeW(wide.as_ptr()) };
        if kind != DRIVE_FIXED && kind != DRIVE_REMOVABLE {
            continue;
        }

        let mut available = 0u64;
        let mut total = 0u64;
        let mut free = 0u64;
        // SAFETY: 三个输出参数均为有效可写指针；卷未就绪时返回 0，直接跳过。
        let ready = unsafe { GetDiskFreeSpaceExW(wide.as_ptr(), &mut available, &mut total, &mut free) };
        if ready == 0 {
            continue;
        }

        let mut label_buffer = [0u16; 64];
        // SAFETY: 缓冲区长度按 u16 元素个数传入，与 API 约定一致；其余输出参数可省略。
        let labelled = unsafe {
            GetVolumeInformationW(
                wide.as_ptr(),
                label_buffer.as_mut_ptr(),
                label_buffer.len() as u32,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                0,
            )
        };

        drives.push(DriveInfo {
            letter: format!("{letter}:"),
            label: if labelled != 0 {
                utf16_until_nul(&label_buffer)
            } else {
                String::new()
            },
            root: PathBuf::from(&display_root),
            display_root,
            total_bytes: total,
            free_bytes: free,
        });
    }

    drives
}

/// 非 Windows 平台没有盘符概念；本启动器只发布 Windows 版本，这里仅保证可编译。
#[cfg(not(windows))]
fn enumerate_drives() -> Vec<DriveInfo> {
    Vec::new()
}

/// 读取以 NUL 结尾的 UTF-16 缓冲区。
#[cfg(windows)]
fn utf16_until_nul(buffer: &[u16]) -> String {
    let end = buffer
        .iter()
        .position(|unit| *unit == 0)
        .unwrap_or(buffer.len());
    String::from_utf16_lossy(&buffer[..end]).trim().to_owned()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn rules() -> PruneRules {
        PruneRules {
            online: PathBuf::from(r"C:\Users\tester\AppData\Roaming\AstraBrew Launcher\sillytavern"),
            launcher_root: PathBuf::from(r"C:\Users\tester\AppData\Roaming\AstraBrew Launcher"),
            launcher_temp: PathBuf::from(r"C:\Users\tester\AppData\Local\Temp\astrabrew-launcher"),
        }
    }

    #[test]
    fn prunes_system_and_cache_directories() {
        let rules = rules();
        let root = Path::new(r"C:\");
        for path in [
            r"C:\Windows",
            r"C:\Program Files",
            r"C:\ProgramData",
            r"C:\$Recycle.Bin",
            r"C:\project\node_modules",
            r"C:\project\target",
            r"C:\project\.git",
            r"C:\project\Cache",
            r"C:\Users\tester\AppData",
        ] {
            assert!(
                rules.should_prune(Path::new(path), root),
                "{path} 应当被剪枝"
            );
        }
    }

    #[test]
    fn keeps_ordinary_project_directories() {
        let rules = rules();
        let root = Path::new(r"C:\");
        for path in [
            r"C:\SillyTavern",
            r"C:\Users\tester\Documents\sillytavern",
            r"D:\Games\tavern",
        ] {
            let drive_root = if path.starts_with(r"D:") {
                Path::new(r"D:\")
            } else {
                root
            };
            assert!(
                !rules.should_prune(Path::new(path), drive_root),
                "{path} 不应被剪枝"
            );
        }
    }

    /// 系统目录只在盘根剪枝：用户自己的 `D:\Windows` 目录仍应进入。
    #[test]
    fn root_only_rules_do_not_leak_into_subdirectories() {
        let rules = rules();
        assert!(rules.should_prune(Path::new(r"C:\Windows"), Path::new(r"C:\")));
        assert!(!rules.should_prune(
            Path::new(r"D:\backup\Windows"),
            Path::new(r"D:\")
        ));
    }

    #[test]
    fn prunes_launcher_own_directories_and_out_of_drive_paths() {
        let rules = rules();
        let root = Path::new(r"C:\");
        assert!(rules.should_prune(
            Path::new(r"C:\Users\tester\AppData\Roaming\AstraBrew Launcher\lib"),
            root
        ));
        assert!(rules.should_prune(
            Path::new(r"C:\Users\tester\AppData\Local\Temp\astrabrew-launcher\caches"),
            root
        ));
        // 盘符不匹配（符号链接逃逸）时剪枝。
        assert!(rules.should_prune(Path::new(r"D:\elsewhere"), root));
    }

    #[test]
    fn walk_counter_converges_to_one() {
        let counter = WalkCounter::start();
        // 尚未发现任何目录时不能除零，也不能报出非零进度。
        assert_eq!(counter.fraction(), 0.0);
        counter.known.store(4, Ordering::Relaxed);
        counter.visited.store(2, Ordering::Relaxed);
        assert!((counter.fraction() - 0.5).abs() < f32::EPSILON);
        // 扫描结束时两个计数器口径一致，比值必然收敛到 1.0。
        counter.visited.store(4, Ordering::Relaxed);
        assert_eq!(counter.fraction(), 1.0);
    }

    #[test]
    fn drive_phase_roundtrips_through_storage_code() {
        for phase in [
            DrivePhase::Pending,
            DrivePhase::Running,
            DrivePhase::Completed,
            DrivePhase::Failed,
            DrivePhase::Cancelled,
        ] {
            assert_eq!(phase_from_code(phase_code(phase)), phase);
        }
        assert!(DrivePhase::Completed.finished());
        assert!(!DrivePhase::Running.finished());
    }

    #[test]
    fn snapshot_reports_full_fraction_when_completed() {
        let drive = DriveShared::new(DriveInfo {
            letter: "C:".into(),
            label: "系统".into(),
            root: PathBuf::from(r"C:\"),
            display_root: r"C:\".into(),
            total_bytes: 100,
            free_bytes: 40,
        });
        drive.counter.known.store(10, Ordering::Relaxed);
        drive.counter.visited.store(4, Ordering::Relaxed);
        assert!((drive.snapshot().fraction - 0.4).abs() < f32::EPSILON);
        drive.set_phase(DrivePhase::Completed);
        assert_eq!(drive.snapshot().fraction, 1.0);
    }
}
