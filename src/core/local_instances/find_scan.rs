//! 用户主目录快速扫描：查找 `package.json` 并校验是否为酒馆实例。
//!
//! 旧版依赖 macOS 自带的 `/usr/bin/find` 子进程；Windows 没有等价的内置工具，
//! 因此改用 `jwalk` 在进程内并行遍历，既省掉一次进程启动开销，
//! 也避免了外部命令在缺少 `PATH` / 权限异常时的各种边界问题。
//!
//! 扫描策略与旧版保持一致：
//! - 只遍历主目录，遇到启动器自带实例目录直接剪枝；
//! - 命中 `package.json` 后交给 [`inspect_package`] 判定是否为酒馆实例；
//! - 全程可取消，并周期性上报「已检查路径 / 已发现实例数」。

use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

use super::scan::{ScanEvent, ScanProgress, ScanReport};
use super::{LocalError, LocalErrorKind, display_path, inspect_package, normalized_path};

/// 扫描进度上报间隔：过密会拖慢遍历，过疏则界面显得卡住。
const PROGRESS_INTERVAL: Duration = Duration::from_millis(100);

/// 用户主目录中需要跳过的目录名。
///
/// 这些目录要么体量巨大且不可能存放酒馆实例（依赖、缓存、版本控制），
/// 要么本身是启动器或其他工具的数据区，遍历它们只是浪费时间。
const SKIPPED_DIRECTORIES: &[&str] = &[
    "node_modules",
    ".git",
    "AppData",
    ".cache",
    "$RECYCLE.BIN",
    "System Volume Information",
];

/// 判断取消标志是否已置位。
fn cancelled(cancel: &AtomicBool) -> Result<(), LocalError> {
    if cancel.load(Ordering::Relaxed) {
        Err(LocalError::cancelled())
    } else {
        Ok(())
    }
}

/// 该目录是否应当被剪枝（不进入其子树）。
fn should_prune(path: &Path, home: &Path, online: &Path) -> bool {
    // 启动器自带实例：其数据由启动器自身管理，不应被识别为「用户本地实例」。
    if path == online || path.starts_with(online) {
        return true;
    }

    let Some(name) = path.file_name().and_then(|name| name.to_str()) else {
        return false;
    };
    if SKIPPED_DIRECTORIES.contains(&name) {
        return true;
    }

    // 隐藏的版本控制与缓存目录统一跳过（`.vscode` 之类仍允许进入，
    // 因为它们体积小且可能包含用户自建的酒馆工程）。
    if name.starts_with('.') && name != "." {
        return !matches!(name, ".config" | ".local");
    }

    // 只扫描主目录内部；符号链接指向外部时跳过，避免绕出扫描范围。
    !path.starts_with(home)
}

/// 扫描主目录，查找全部酒馆实例。
///
/// `online` 是启动器在线下载实例的目录，扫描时整体排除。
/// `emit` 返回 `false` 表示调用方要求中止（等价于取消）。
pub fn run_home(online: PathBuf, cancel: &AtomicBool, mut emit: impl FnMut(ScanEvent) -> bool) {
    let result = scan_home(&online, cancel, &mut emit);
    emit(ScanEvent::Finished(result));
}

/// 扫描主体；所有错误都以 [`LocalError`] 形式返回给 `run_home` 统一上报。
fn scan_home(
    online: &Path,
    cancel: &AtomicBool,
    emit: &mut impl FnMut(ScanEvent) -> bool,
) -> Result<ScanReport, LocalError> {
    cancelled(cancel)?;

    let home = user_home()?;
    let online = normalized_path(online);

    if !emit(ScanEvent::ScanningPath(display_path(&home))) {
        return Err(LocalError::cancelled());
    }

    let started = Instant::now();
    let mut progress = ScanProgress {
        path: display_path(&home),
        ..Default::default()
    };
    let mut partial = false;
    let mut last_report = Instant::now() - PROGRESS_INTERVAL;

    // jwalk 以并行方式遍历；`process_read_dir` 负责剪枝，`path` 回调负责判定命中。
    let walker = jwalk::WalkDir::new(&home)
        .skip_hidden(false)
        .follow_links(false)
        .process_read_dir({
            let online = online.clone();
            let home = home.clone();
            move |_depth, _path, _state, children| {
                children.retain(|entry| match entry {
                    Ok(entry) => !should_prune(&entry.path(), &home, &online),
                    // 读取失败的条目直接丢弃，由遍历循环统计为「跳过」。
                    Err(_) => false,
                });
            }
        });

    for entry in walker {
        cancelled(cancel)?;

        let entry = match entry {
            Ok(entry) => entry,
            Err(error) => {
                // 权限不足、路径过长等单个条目错误不应中断整轮扫描，
                // 只标记为「结果可能不完整」并提示用户。
                partial = true;
                if !emit(ScanEvent::Warning(LocalError::new(
                    "local.scan.skipped_or_error",
                    error.to_string(),
                ))) {
                    return Err(LocalError::cancelled());
                }
                continue;
            }
        };

        if let Some(name) = entry.file_name().to_str()
            && name == "package.json"
        {
            let path = entry.path();
            progress.checked += 1;
            // 扫描进度会实时显示在界面上，去掉 canonicalize 可能带出的 `\\?\` 前缀。
            progress.path = display_path(path.parent().unwrap_or(&path));

            if last_report.elapsed() >= PROGRESS_INTERVAL {
                progress.elapsed_seconds = started.elapsed().as_secs();
                if !emit(ScanEvent::Progress(progress.clone())) {
                    return Err(LocalError::cancelled());
                }
                last_report = Instant::now();
            }

            // 非 UTF-8 路径无法交给后续的字符串处理流程，只做警告不视为失败。
            if path.to_str().is_none() {
                partial = true;
                if !emit(ScanEvent::Warning(LocalError::new(
                    "local.scan.path_not_text",
                    // 该警告会展示给用户，同样需要剥掉 verbatim 前缀。
                    display_path(&path),
                ))) {
                    return Err(LocalError::cancelled());
                }
                continue;
            }

            match inspect_package(&path, &online) {
                Ok(instance) => {
                    progress.found += 1;
                    if !emit(ScanEvent::Found(instance)) {
                        return Err(LocalError::cancelled());
                    }
                }
                // 不是酒馆实例（或属于在线实例）是正常情况，不计入警告。
                Err(error)
                    if matches!(
                        error.kind,
                        LocalErrorKind::InvalidInstance | LocalErrorKind::OnlineInstance
                    ) => {}
                Err(error) => {
                    partial = true;
                    if !emit(ScanEvent::Warning(error)) {
                        return Err(LocalError::cancelled());
                    }
                }
            }
        }

        // 目录本身不计入进度，只在命中文件后递增，避免数字虚高。
        if last_report.elapsed() >= PROGRESS_INTERVAL {
            progress.elapsed_seconds = started.elapsed().as_secs();
            if !emit(ScanEvent::Progress(progress.clone())) {
                return Err(LocalError::cancelled());
            }
            last_report = Instant::now();
        }
    }

    progress.elapsed_seconds = started.elapsed().as_secs();
    Ok(ScanReport { progress, partial })
}

/// 解析当前用户主目录。
///
/// Windows 优先使用 `USERPROFILE`，回退到 `HOMEDRIVE` + `HOMEPATH` 组合。
/// 主目录缺失或异常时直接报错——绝不能像旧版那样回退到根目录，
/// 否则会退化成整盘扫描。
fn user_home() -> Result<PathBuf, LocalError> {
    let home = std::env::var_os("USERPROFILE")
        .filter(|value| !value.is_empty())
        .map(PathBuf::from)
        .or_else(|| {
            let drive = std::env::var_os("HOMEDRIVE")?;
            let path = std::env::var_os("HOMEPATH")?;
            if drive.is_empty() || path.is_empty() {
                return None;
            }
            let mut combined = drive;
            combined.push(path);
            Some(PathBuf::from(combined))
        })
        .ok_or_else(|| LocalError::new("local.scan.home_unknown", "USERPROFILE"))?;

    if !home.is_absolute() {
        return Err(LocalError::new("local.scan.home_unknown", home.display()));
    }
    // 拒绝盘符根目录（`C:\`），那等同于整盘扫描。
    if home.parent().is_none() {
        return Err(LocalError::new("local.scan.home_unknown", home.display()));
    }
    Ok(normalized_path(&home))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn prunes_dependency_and_vcs_directories() {
        let home = Path::new(r"C:\Users\tester");
        let online = home.join("online");
        assert!(should_prune(
            &home.join("project").join("node_modules"),
            home,
            &online
        ));
        assert!(should_prune(&home.join("project").join(".git"), home, &online));
        assert!(should_prune(&home.join("AppData"), home, &online));
    }

    #[test]
    fn prunes_online_instance_subtree() {
        let home = Path::new(r"C:\Users\tester");
        let online = home.join("online");
        assert!(should_prune(&online, home, &online));
        assert!(should_prune(&online.join("data").join("default-user"), home, &online));
    }

    #[test]
    fn keeps_ordinary_project_directories() {
        let home = Path::new(r"C:\Users\tester");
        let online = home.join("online");
        assert!(!should_prune(&home.join("tavern"), home, &online));
        assert!(!should_prune(&home.join("Documents").join("sillytavern"), home, &online));
    }

    /// 主目录必须有父目录；盘符根目录会被拒绝，避免整盘扫描。
    #[test]
    fn user_home_is_never_a_drive_root() {
        let home = user_home().expect("测试环境应能解析主目录");
        assert!(home.is_absolute());
        assert!(home.parent().is_some(), "主目录不能是盘符根目录");
    }
}
