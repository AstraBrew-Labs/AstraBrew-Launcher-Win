//! 启动器自动更新模块 —— 从发布源检测并安装新版本。
//!
//! 两个发布源按顺序回退，任一源不可用都不会打断检测：
//!
//! | 源 | 仓库地址 | 清单定位方式 |
//! |---|---|---|
//! | 镜像 | <https://gitcode.com/AstraBrew-Labs/AstraBrew-Launcher-Win> | GitCode OpenAPI v5 |
//! | 直连 | <https://github.com/AstraBrew-Labs/AstraBrew-Launcher-Win> | `releases/latest/download` |
//!
//! GitCode 不支持 GitHub 的 `releases/latest/download/<file>` 短链（该路径只会返回站点的
//! HTML 外壳），必须先用 API 查出最新 Release 与资产地址，再按 tag 拼出真实下载地址。
//!
//! 清单由本模块自己解析，只把「下载 + 验签 + 替换可执行文件」交给 cargo-packager-updater：
//!
//! - 它的解析契约比实际发布物更严（平台条目必须同时有 `url` / `signature` / `format`），
//!   为一个不影响行为的字段让整个更新不可用并不合理；
//! - 它按运行时自检的平台键去匹配清单里的键，而 cargo-packager 在 Windows 上写出的是
//!   `windows-x86_64` / `nsis`，两边容易对不上而直接 `TargetNotFound`；
//! - 自己解析后，清单只取一次，也顺带能用平台 API 给出的资产地址校正清单里写错的 tag。
//!
//! 无论走哪个源，下载到的包都用清单里的 `signature` 与内嵌公钥做 minisign 校验，
//! 因此换源只改变传输链路，不改变信任链。
//!
//! ## Windows 安装方式
//!
//! 发布的 Windows 资产有两类：NSIS 安装包（`.exe`）与免安装压缩包（`.zip`）。
//! 前者由 NSIS 自己处理「关闭旧进程 → 覆盖文件 → 重启」，本模块只需下载并拉起它；
//! 后者需要解压后覆盖当前可执行文件，由 `install_portable_archive` 完成。

use std::path::PathBuf;
use std::sync::mpsc;
use std::time::Duration;

use cargo_packager_updater::{Config, Update, UpdateFormat};
use serde::Deserialize;

// ─── 发布源 ──────────────────────────────────────────────────────────────────

/// 仓库路径（GitHub 与 GitCode 上的路径一致）。
const REPO: &str = "AstraBrew-Labs/AstraBrew-Launcher-Win";

/// 镜像源站点地址（GitCode）。
const MIRROR_SITE: &str = "https://gitcode.com";

/// 镜像源 OpenAPI v5 基址。
const MIRROR_API: &str = "https://api.gitcode.com/api/v5/repos";

/// 直连源站点地址（GitHub）。
const DIRECT_SITE: &str = "https://github.com";

/// 更新签名公钥；与打包私钥配对，私钥不入库（由 `CARGO_PACKAGER_SIGN_PRIVATE_KEY` 注入 CI）。
///
/// 由 `cargo packager signer generate` 在本仓库生成（与旧版仓库的密钥无关），
/// 每次重新生成密钥时都要同步这里。
const PUBKEY: &str = "dW50cnVzdGVkIGNvbW1lbnQ6IG1pbmlzaWduIHB1YmxpYyBrZXk6IEM4NzlCMTdDQ0Q1OTFDQkYKUldTL0hGbk5mTEY1eUJaTExLaHhRNzJSUTc1b29OVk1JbkdqeElZcjNvVlc5Q0c4elVETG9HTFUK";

/// 更新清单文件名，由 cargo-packager 在发布时生成。
const MANIFEST: &str = "latest.json";

/// 单次清单请求的超时；发布源不通时不应让检查一直挂着。
const CHECK_TIMEOUT: Duration = Duration::from_secs(20);

/// 安装包下载的超时；镜像带宽波动较大，留出足够余量。
const DOWNLOAD_TIMEOUT: Duration = Duration::from_secs(600);

/// 可用更新源，数组顺序即尝试优先级。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum UpdateSource {
    /// 镜像：GitCode。
    Mirror,
    /// 直连：GitHub。
    Direct,
}

impl UpdateSource {
    /// 全部更新源，按尝试优先级排列。
    pub const ALL: [Self; 2] = [Self::Mirror, Self::Direct];
}

// ─── 数据结构 ─────────────────────────────────────────────────────────────────

/// 更新失败的原因。
///
/// 核心层不拼用户可见的句子：前几种情况由界面按键取文案，
/// [`UpdateFailure::Detail`] 承载外部库给出的运行时详情。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum UpdateFailure {
    /// 清单下载不到（源不可达或尚未发布）
    ManifestUnreachable,
    /// 清单结构不符合预期（非法 JSON、缺版本号、缺 `url` / `signature`）
    ManifestInvalid,
    /// 清单里没有适用于本机的平台条目
    PlatformUnsupported,
    /// 判断不出当前可执行文件的安装位置
    InstallLocationUnknown,
    /// 所有更新源都不可用
    SourcesUnreachable,
    /// 更新信息已过期（检测与安装之间发布内容发生变化）
    Expired,
    /// 外部库报出的错误详情
    Detail(String),
}

impl UpdateFailure {
    /// 该原因对应的文案键；[`UpdateFailure::Detail`] 需要配合模板使用，返回 `None`。
    pub const fn message_key(&self) -> Option<&'static str> {
        match self {
            Self::ManifestUnreachable => Some("settings.update.error.manifest_unreachable"),
            Self::ManifestInvalid => Some("settings.update.error.manifest_invalid"),
            Self::PlatformUnsupported => Some("settings.update.error.platform_unsupported"),
            Self::InstallLocationUnknown => Some("settings.update.error.install_location"),
            Self::SourcesUnreachable => Some("settings.update.error.sources_unreachable"),
            Self::Expired => Some("settings.update.error.expired"),
            Self::Detail(_) => None,
        }
    }

    /// 外部错误详情。
    pub fn detail(&self) -> Option<&str> {
        match self {
            Self::Detail(detail) => Some(detail),
            _ => None,
        }
    }
}

/// 更新检测 / 下载安装的状态推进。
#[derive(Debug, Clone)]
pub enum UpdateStatus {
    /// 正在检查
    Checking,
    /// 已是最新版本
    UpToDate,
    /// 发现新版本（版本号、更新说明、命中的更新源）
    UpdateAvailable {
        version: String,
        notes: Option<String>,
        source: UpdateSource,
    },
    /// 正在下载安装
    Downloading,
    /// 安装完成（需重启生效）
    Installed,
    /// 出错
    Error(UpdateFailure),
}

/// 已解析的发布源：清单地址 + 该源上的资产地址表。
struct ResolvedSource {
    /// `latest.json` 的下载地址。
    manifest: String,
    /// 资产文件名 → 权威下载地址；直连源无法列出资产时为空。
    assets: Vec<(String, String)>,
}

/// 清单解析结果。
#[derive(Debug)]
struct Manifest {
    /// 清单声明的版本号。
    version: cargo_packager_updater::semver::Version,
    /// 发行说明。
    notes: Option<String>,
    /// 本机可用的平台条目。
    platform: ManifestPlatform,
}

/// 清单里本机对应的平台条目。
#[derive(Debug)]
struct ManifestPlatform {
    /// 安装包地址。
    url: String,
    /// 安装包的 minisign 签名。
    signature: String,
}

/// GitCode Release 的 API 表示。
///
/// 字段与 GitHub 基本一致，差异有两点：GitCode 用 `release_status: "latest"` 显式标记最新
/// 发布（GitHub 没有该字段），且 `assets[].type` 区分源码包（`source`）与上传附件（`attach`）。
#[derive(Debug, Deserialize)]
struct GitCodeRelease {
    tag_name: String,
    #[serde(default)]
    release_status: Option<String>,
    #[serde(default, deserialize_with = "deserialize_assets")]
    assets: Vec<GitCodeAsset>,
}

#[derive(Debug, Deserialize)]
struct GitCodeAsset {
    name: String,
    browser_download_url: String,
}

/// 把缺省与 `null` 都当成空列表；无附件的发布可能返回 `null`，不能因此让整份列表解析失败。
fn deserialize_assets<'de, D>(deserializer: D) -> Result<Vec<GitCodeAsset>, D::Error>
where
    D: serde::Deserializer<'de>,
{
    Ok(Option::<Vec<GitCodeAsset>>::deserialize(deserializer)?.unwrap_or_default())
}

impl GitCodeRelease {
    /// 是否为 GitCode 标记的最新发布。
    fn is_latest(&self) -> bool {
        self.release_status.as_deref() == Some("latest")
    }

    /// 取指定文件名的附件地址。
    fn asset_url(&self, name: &str) -> Option<&str> {
        self.assets
            .iter()
            .find(|asset| asset.name == name)
            .map(|asset| asset.browser_download_url.as_str())
    }

    /// 资产文件名 → 下载地址。
    fn asset_table(&self) -> Vec<(String, String)> {
        self.assets
            .iter()
            .map(|asset| (asset.name.clone(), asset.browser_download_url.clone()))
            .collect()
    }
}

// ─── 公开 API ─────────────────────────────────────────────────────────────────

/// 用户点击「检查更新」时调用。
///
/// 找到新版本后发送 [`UpdateStatus::UpdateAvailable`]，由界面弹出确认框；
/// 已是最新版本或全部源不可用则分别发送 [`UpdateStatus::UpToDate`] 与 [`UpdateStatus::Error`]。
pub fn check_update_manual() -> mpsc::Receiver<UpdateStatus> {
    let (tx, rx) = mpsc::channel();
    std::thread::spawn(move || {
        let _ = tx.send(UpdateStatus::Checking);
        match try_check() {
            Ok(Some((version, notes, source))) => {
                let _ = tx.send(UpdateStatus::UpdateAvailable {
                    version,
                    notes,
                    source,
                });
            }
            Ok(None) => {
                let _ = tx.send(UpdateStatus::UpToDate);
            }
            Err(failure) => {
                let _ = tx.send(UpdateStatus::Error(failure));
            }
        }
    });
    rx
}

/// 用户在确认框中选择「立即更新」后调用。
///
/// 只带上命中的更新源，安装前会重新解析一次该源的地址：
/// 检测与安装之间可能已经发布了新版本，重新解析比沿用旧地址更可靠。
pub fn do_install(source: UpdateSource) -> mpsc::Receiver<UpdateStatus> {
    let (tx, rx) = mpsc::channel();
    std::thread::spawn(move || {
        let _ = tx.send(UpdateStatus::Downloading);
        match download_and_install(source) {
            Ok(()) => {
                let _ = tx.send(UpdateStatus::Installed);
            }
            Err(failure) => {
                let _ = tx.send(UpdateStatus::Error(failure));
            }
        }
    });
    rx
}

// ─── 内部实现：检测 ───────────────────────────────────────────────────────────

/// 依次探测所有源。
///
/// 只有真正「发现新版本」才会提前结束；「已是最新」不会，因为镜像同步存在滞后 ——
/// 镜像答「没有更新」时仍要去直连确认一次，否则用户在镜像追平之前永远看不到新版本。
/// 多个源都报出更新时取版本号更高的那个。
fn try_check() -> Result<Option<(String, Option<String>, UpdateSource)>, UpdateFailure> {
    let current = current_version().map_err(UpdateFailure::Detail)?;
    let mut newest: Option<(String, Option<String>, UpdateSource)> = None;
    let mut reachable = false;
    let mut broken: Option<UpdateFailure> = None;

    for source in UpdateSource::ALL {
        match check_at(source, &current) {
            CheckResult::Update(remote, notes) => {
                reachable = true;
                match &newest {
                    Some((best, _, _)) if !is_newer(&remote, best) => {}
                    _ => newest = Some((remote, notes, source)),
                }
            }
            CheckResult::UpToDate => reachable = true,
            // 清单结构不对属于发布侧问题，与「网络不通」要分开报，否则会把人引到错误方向。
            CheckResult::Broken(failure) => broken = broken.or(Some(failure)),
            // 该源不可用（未发布、网络不通），换下一个源继续。
            CheckResult::Unreachable => continue,
        }
    }

    if let Some(found) = newest {
        return Ok(Some(found));
    }
    if reachable {
        return Ok(None);
    }
    Err(broken.unwrap_or(UpdateFailure::SourcesUnreachable))
}

/// 判断候选版本号是否高于当前结果。
///
/// 任一版本号无法解析时返回 `false`：宁可保留先拿到的结果，也不因为比较失败丢掉已发现的更新。
fn is_newer(candidate: &str, current: &str) -> bool {
    match (
        candidate.parse::<cargo_packager_updater::semver::Version>(),
        current.parse::<cargo_packager_updater::semver::Version>(),
    ) {
        (Ok(candidate), Ok(current)) => candidate > current,
        _ => false,
    }
}

/// 单个更新源的探测结果。
enum CheckResult {
    /// 发现新版本（版本号、更新说明）
    Update(String, Option<String>),
    /// 已是最新版本
    UpToDate,
    /// 该源不可用
    Unreachable,
    /// 该源的清单存在但内容不可用
    Broken(UpdateFailure),
}

/// 通过单个源探测更新。
fn check_at(
    source: UpdateSource,
    current: &cargo_packager_updater::semver::Version,
) -> CheckResult {
    let Some(resolved) = resolve(source) else {
        return CheckResult::Unreachable;
    };

    match newer_than(&resolved, current) {
        Ok(Some(manifest)) => CheckResult::Update(manifest.version.to_string(), manifest.notes),
        Ok(None) => CheckResult::UpToDate,
        // 清单拿不到说明该源不可达，继续回退；其余失败按「发布侧问题」单独上报。
        Err(UpdateFailure::ManifestUnreachable) => CheckResult::Unreachable,
        Err(failure) => CheckResult::Broken(failure),
    }
}

// ─── 内部实现：安装 ───────────────────────────────────────────────────────────

/// 在指定源上执行下载与安装。
///
/// Windows 上不能沿用 cargo-packager-updater 的 macOS 流程（它会去解 `.app.tar.gz`），
/// 而是按发布物的真实形态分流：
///
/// - `.exe`（NSIS 安装包）：落盘到临时目录后拉起，交回 NSIS 处理关闭旧进程 / 覆盖 / 重启；
/// - `.zip`（免安装包）：下载并解压后覆盖当前可执行文件。
///
/// 交接给下游的 `Update` 只用于下载与 minisign 验签，不再调用它的
/// `download_and_install()`，因为该方法的安装分支是给 NSIS/MSI 用的（内部走
/// `powershell Start-Process` 并直接 `process::exit(0)`），免安装包需要另走一条路。
fn download_and_install(source: UpdateSource) -> Result<(), UpdateFailure> {
    let current = current_version().map_err(UpdateFailure::Detail)?;
    let resolved = resolve(source).ok_or(UpdateFailure::ManifestUnreachable)?;
    let manifest = newer_than(&resolved, &current)?.ok_or(UpdateFailure::Expired)?;
    let target = install_target(&manifest.platform.url);
    let update = build_update(manifest, &resolved)?;

    // 下载与验签交给上游库：它按清单里的 `signature` 做 minisign 校验，验签失败即报错，
    // 因此换源只改变传输链路，不改变信任链。
    let archive = update
        .download()
        .map_err(|error| UpdateFailure::Detail(error.to_string()))?;

    match target {
        InstallTarget::InstallerScript => launch_installer(&archive),
        InstallTarget::PortableArchive => install_portable_archive(&archive),
        InstallTarget::Unknown => Err(UpdateFailure::PlatformUnsupported),
    }
}

/// 发布物的形态，决定安装方式。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum InstallTarget {
    /// NSIS 安装包（`.exe`）：交回安装器处理。
    InstallerScript,
    /// 免安装压缩包（`.zip`）：解压后覆盖当前可执行文件。
    PortableArchive,
    /// 清单里出现了两者之外的扩展名，无法安全安装。
    Unknown,
}

/// 按下载地址的扩展名判断发布物形态。
///
/// 只看扩展名不看清单里的 `format` 字段：该字段由发布流水线填写，缺省或与实际资产
/// 不符时扩展名仍能给出正确答案，而误判会导致装错东西。
fn install_target(url: &str) -> InstallTarget {
    // 去掉查询串与片段再取扩展名，避免 `...?token=x` 之类让判断落空。
    let path = url.split(['?', '#']).next().unwrap_or(url);
    match path.rsplit('.').next().map(str::to_ascii_lowercase).as_deref() {
        Some("exe") => InstallTarget::InstallerScript,
        Some("zip") => InstallTarget::PortableArchive,
        _ => InstallTarget::Unknown,
    }
}

/// 把下载到的安装包写入系统临时目录并返回落盘路径。
///
/// 交给 `tempfile` 生成随机名，避免两个实例同时更新时互相覆盖；
/// 文件用 `keep()` 保留到进程退出之后，因为安装器要等本进程退出才继续。
fn stage_download(bytes: &[u8], extension: &str) -> Result<PathBuf, UpdateFailure> {
    use std::io::Write;

    let mut file = tempfile::Builder::new()
        .prefix("astrabrew-update-")
        .suffix(extension)
        .tempfile()
        .map_err(|error| UpdateFailure::Detail(error.to_string()))?;
    file.write_all(bytes)
        .map_err(|error| UpdateFailure::Detail(error.to_string()))?;
    let (_, path) = file
        .keep()
        .map_err(|error| UpdateFailure::Detail(error.error.to_string()))?;
    Ok(path)
}

/// 拉起 NSIS 安装包。
///
/// NSIS 安装器自己会请求关闭同名进程、覆盖文件并重新拉起。这里必须**先把安装器拉起
/// 再返回**，让界面去走退出流程；本进程若继续占用可执行文件，覆盖就会失败。
fn launch_installer(bytes: &[u8]) -> Result<(), UpdateFailure> {
    let installer = stage_download(bytes, ".exe")?;
    let mut command = std::process::Command::new(&installer);
    crate::core::env::apply_no_window_to_command(&mut command);
    command
        .spawn()
        .map(|_| ())
        .map_err(|error| UpdateFailure::Detail(error.to_string()))
}

/// 安装免安装压缩包：解压后用其中的可执行文件覆盖当前可执行文件。
///
/// 覆盖采取「先重命名旧文件、再落新文件」的顺序：Windows 不允许删除正在运行的可执行
/// 文件，但允许重命名它。旧文件保留到下次启动时由 [`cleanup_stale_executables`] 清理，
/// 这样即使中途失败，用户手上仍有一个可用的旧版本。
fn install_portable_archive(bytes: &[u8]) -> Result<(), UpdateFailure> {
    let archive_path = stage_download(bytes, ".zip")?;
    let extract_dir = archive_path.with_extension("extracted");
    let _ = std::fs::remove_dir_all(&extract_dir);
    std::fs::create_dir_all(&extract_dir)
        .map_err(|error| UpdateFailure::Detail(error.to_string()))?;

    let executable_name = current_executable_name()?;
    extract_zip(&archive_path, &extract_dir)?;

    // zip 里可能带一层目录，递归找到那个可执行文件。
    let source = find_file_recursively(&extract_dir, &executable_name)
        .ok_or(UpdateFailure::InstallLocationUnknown)?;
    let destination =
        std::env::current_exe().map_err(|_| UpdateFailure::InstallLocationUnknown)?;

    let backup = destination.with_extension("old.exe");
    let _ = std::fs::remove_file(&backup);
    std::fs::rename(&destination, &backup)
        .map_err(|error| UpdateFailure::Detail(error.to_string()))?;

    if let Err(error) = std::fs::copy(&source, &destination) {
        // 落新文件失败时把旧文件放回去，避免把用户留在一个没有可执行文件的状态。
        let _ = std::fs::rename(&backup, &destination);
        return Err(UpdateFailure::Detail(error.to_string()));
    }

    // 临时解压目录只是这一次安装的中间产物，装完即可丢弃。
    let _ = std::fs::remove_dir_all(&extract_dir);
    let _ = std::fs::remove_file(&archive_path);
    Ok(())
}

/// 当前可执行文件名（含扩展名），用于在压缩包里定位要覆盖的文件。
fn current_executable_name() -> Result<String, UpdateFailure> {
    std::env::current_exe()
        .ok()
        .and_then(|path| path.file_name().map(|name| name.to_string_lossy().into_owned()))
        .ok_or(UpdateFailure::InstallLocationUnknown)
}

/// 把 zip 里的全部条目解压到目标目录。
///
/// 条目名里的路径分隔符可能是 `/` 或 `\`，统一交给 `enclosed_name()` 归一化；
/// 它同时会拒绝 `..` 与绝对路径，避免发布侧的问题变成「写到系统盘别处」。
fn extract_zip(
    archive: &std::path::Path,
    destination: &std::path::Path,
) -> Result<(), UpdateFailure> {
    let file =
        std::fs::File::open(archive).map_err(|error| UpdateFailure::Detail(error.to_string()))?;
    let mut zip =
        zip::ZipArchive::new(file).map_err(|error| UpdateFailure::Detail(error.to_string()))?;

    for index in 0..zip.len() {
        let mut entry = zip
            .by_index(index)
            .map_err(|error| UpdateFailure::Detail(error.to_string()))?;
        // `enclosed_name()` 返回 `None` 表示该条目越出了解压根，直接跳过。
        let Some(relative) = entry.enclosed_name() else {
            continue;
        };
        let target = destination.join(relative);

        if entry.is_dir() {
            std::fs::create_dir_all(&target)
                .map_err(|error| UpdateFailure::Detail(error.to_string()))?;
            continue;
        }
        if let Some(parent) = target.parent() {
            std::fs::create_dir_all(parent)
                .map_err(|error| UpdateFailure::Detail(error.to_string()))?;
        }
        let mut output =
            std::fs::File::create(&target).map_err(|error| UpdateFailure::Detail(error.to_string()))?;
        std::io::copy(&mut entry, &mut output)
            .map_err(|error| UpdateFailure::Detail(error.to_string()))?;
    }
    Ok(())
}

/// 在目录里递归查找指定文件名。
fn find_file_recursively(root: &std::path::Path, name: &str) -> Option<PathBuf> {
    let entries = std::fs::read_dir(root).ok()?;
    let mut subdirectories = Vec::new();
    for entry in entries.flatten() {
        let path = entry.path();
        if path.is_dir() {
            subdirectories.push(path);
        } else if path.file_name().is_some_and(|file| file == name) {
            return Some(path);
        }
    }
    // 先找同级再看子目录，压缩包里带一层目录时优先取外层那份。
    subdirectories
        .into_iter()
        .find_map(|directory| find_file_recursively(&directory, name))
}

/// 清理上一次更新留下的旧可执行文件。
///
/// 更新时旧文件被重命名成 `<名字>.old.exe` 留在安装目录里；本进程退出后就没人占着它了，
/// 于是每次启动时顺手删掉，避免安装目录里越积越多。
///
/// 只在正式启动路径调用（测试进程不会产生这种残留文件，调用点本身也被 `cfg(not(test))` 排除）。
#[cfg(not(test))]
pub fn cleanup_stale_executables() {
    let Ok(executable) = std::env::current_exe() else {
        return;
    };
    let Some(directory) = executable.parent() else {
        return;
    };
    let stale = directory.join(format!(
        "{}.old.exe",
        executable
            .file_stem()
            .map(|stem| stem.to_string_lossy().into_owned())
            .unwrap_or_default()
    ));
    let _ = std::fs::remove_file(stale);
}

/// 读取清单，并在其中的版本高于本机时返回解析结果。
fn newer_than(
    resolved: &ResolvedSource,
    current: &cargo_packager_updater::semver::Version,
) -> Result<Option<Manifest>, UpdateFailure> {
    let text = http_get(&resolved.manifest).ok_or(UpdateFailure::ManifestUnreachable)?;
    let manifest = parse_manifest(&text)?;
    Ok((manifest.version > *current).then_some(manifest))
}

/// 把清单解析结果组装成 cargo-packager-updater 可直接执行的更新描述。
///
/// 只有下载、验签与替换可执行文件交给它；版本比较与平台选择已经在前面自己做完。
fn build_update(manifest: Manifest, resolved: &ResolvedSource) -> Result<Update, UpdateFailure> {
    let endpoint = resolved
        .manifest
        .parse()
        .map_err(|_| UpdateFailure::ManifestInvalid)?;
    // 清单里的地址可能带着与实际 Release 不一致的 tag，优先用源上真实的资产地址。
    let url = corrected_download_url(&manifest.platform.url, &resolved.assets)
        .unwrap_or(manifest.platform.url);
    let download_url = url
        .parse()
        .map_err(|_| UpdateFailure::ManifestInvalid)?;

    Ok(Update {
        config: Config {
            endpoints: vec![endpoint],
            pubkey: PUBKEY.into(),
            ..Default::default()
        },
        body: manifest.notes,
        current_version: env!("CARGO_PKG_VERSION").to_owned(),
        version: manifest.version.to_string(),
        // 发布时间只用于展示，本项目界面不读它，无需为此引入 `time` 依赖。
        date: None,
        target: std::env::consts::OS.to_owned(),
        extract_path: extract_path()?,
        download_url,
        signature: manifest.platform.signature,
        timeout: Some(DOWNLOAD_TIMEOUT),
        headers: Default::default(),
        // Windows 上发布物为 NSIS 安装包；该字段仅用于 cargo-packager-updater
        // 自身的格式判定，实际安装流程（下载 → 拉起安装器 / 解压覆盖）由本模块接管。
        format: UpdateFormat::Nsis,
    })
}

/// 判断当前可执行文件所在的安装目录。
///
/// Windows 上是单文件安装，可执行文件所在目录即安装目录：
/// NSIS 默认落在 `%LOCALAPPDATA%\AstraBrew Launcher\`，
/// 免安装版则由用户自行决定位置。两者都不需要再做路径上溯。
fn extract_path() -> Result<PathBuf, UpdateFailure> {
    let executable = std::env::current_exe().map_err(|_| UpdateFailure::InstallLocationUnknown)?;
    executable
        .parent()
        .map(PathBuf::from)
        .ok_or(UpdateFailure::InstallLocationUnknown)
}

// ─── 内部实现：发布源解析 ─────────────────────────────────────────────────────

/// 把更新源解析成清单地址与资产地址表。
fn resolve(source: UpdateSource) -> Option<ResolvedSource> {
    match source {
        UpdateSource::Mirror => resolve_mirror(),
        UpdateSource::Direct => Some(resolve_direct()),
    }
}

/// 解析 GitCode 上的最新 Release。
///
/// GitCode 没有 `releases/latest/download/<file>` 短链，必须读 API 才能定位到具体 tag 下的资产。
fn resolve_mirror() -> Option<ResolvedSource> {
    let releases: Vec<GitCodeRelease> = serde_json::from_str(&http_get(&format!(
        "{MIRROR_API}/{REPO}/releases"
    ))?)
    .ok()?;
    let release = pick_latest(&releases)?;

    // 附件地址由 API 给出，是权威值；万一附件列表里没有清单，再按 tag 拼标准地址兜底。
    let manifest = release
        .asset_url(MANIFEST)
        .map(str::to_owned)
        .unwrap_or_else(|| mirror_download_url(&release.tag_name, MANIFEST));

    Some(ResolvedSource {
        manifest,
        assets: release.asset_table(),
    })
}

/// 解析 GitHub 上的清单地址。
///
/// GitHub 原生支持 `releases/latest/download/<file>`，因此不需要调用 API，
/// 也就不受 `api.github.com` 的匿名限流影响；代价是拿不到资产列表，无法校正清单里的下载地址。
fn resolve_direct() -> ResolvedSource {
    ResolvedSource {
        manifest: format!("{DIRECT_SITE}/{REPO}/releases/latest/download/{MANIFEST}"),
        assets: Vec::new(),
    }
}

/// 选取要使用的 Release。
///
/// 优先采纳 GitCode 用 `release_status` 显式标记的最新发布；标记缺失时退回列表首个
/// 带清单附件的条目（GitCode 与 GitHub 一样按发布时间倒序返回）。
fn pick_latest(releases: &[GitCodeRelease]) -> Option<&GitCodeRelease> {
    releases
        .iter()
        .find(|release| release.is_latest() && release.asset_url(MANIFEST).is_some())
        .or_else(|| {
            releases
                .iter()
                .find(|release| release.asset_url(MANIFEST).is_some())
        })
}

/// 按 tag 拼出 GitCode 的标准资产下载地址。
fn mirror_download_url(tag: &str, name: &str) -> String {
    format!("{MIRROR_SITE}/{REPO}/releases/download/{tag}/{name}")
}

/// 用源上真实存在的资产地址校正清单里的下载地址。
///
/// 清单由发布流水线生成，其中的平台地址由 tag 与文件名拼成；一旦 tag 写法与实际 Release
/// 不一致（例如清单写 `v0.0.1` 而 Release tag 是 `beta-v0.0.1`），下载必定 404。
/// 此时按文件名在资产表里找到的地址就是可用的。资产表为空（直连源）或文件名对不上时返回 `None`。
fn corrected_download_url(manifest_url: &str, assets: &[(String, String)]) -> Option<String> {
    let name = manifest_url.rsplit('/').next()?;
    assets
        .iter()
        .find(|(asset, _)| asset == name)
        .map(|(_, url)| url.clone())
}

// ─── 内部实现：清单解析 ───────────────────────────────────────────────────────

/// 解析清单。
///
/// 只读取真正会用到的字段：版本号、发行说明与平台条目。
/// **不校验 `format`** —— 它是 cargo-packager-updater 反序列化的必填项，而本模块按
/// 下载地址的扩展名自行判断安装方式；为一个不影响行为的字段拒绝整份清单，
/// 会让更新功能无故失效。
fn parse_manifest(text: &str) -> Result<Manifest, UpdateFailure> {
    let value: serde_json::Value =
        serde_json::from_str(text).map_err(|_| UpdateFailure::ManifestInvalid)?;

    let version = value
        .get("version")
        .and_then(|value| value.as_str())
        .ok_or(UpdateFailure::ManifestInvalid)?
        // 版本号可能带 `v` 前缀，与 cargo-packager-updater 的解析保持一致。
        .trim_start_matches('v')
        .parse()
        .map_err(|_| UpdateFailure::ManifestInvalid)?;
    let notes = value
        .get("notes")
        .and_then(|value| value.as_str())
        .map(str::to_owned);

    Ok(Manifest {
        version,
        notes,
        platform: platform_entry(&value)?,
    })
}

/// 取出本机可用的平台条目。
///
/// 清单有两种形态：`platforms` 映射（按 `<os>-<arch>` 分区）与扁平形态（`url` / `signature`
/// 直接挂在顶层）。前者按本机架构选键，后者直接采用。
fn platform_entry(value: &serde_json::Value) -> Result<ManifestPlatform, UpdateFailure> {
    let Some(platforms) = value.get("platforms").and_then(|value| value.as_object()) else {
        return entry_fields(value).ok_or(UpdateFailure::ManifestInvalid);
    };
    let key = pick_platform_key(platforms).ok_or(UpdateFailure::PlatformUnsupported)?;
    platforms
        .get(&key)
        .and_then(entry_fields)
        .ok_or(UpdateFailure::ManifestInvalid)
}

/// 从平台条目里取 `url` 与 `signature`。
fn entry_fields(value: &serde_json::Value) -> Option<ManifestPlatform> {
    Some(ManifestPlatform {
        url: value.get("url")?.as_str()?.to_owned(),
        signature: value.get("signature")?.as_str()?.to_owned(),
    })
}

/// 在清单的 `platforms` 里选出本机可用的键。
///
/// Windows 上 cargo-packager 按 `<os>-<arch>` 或打包格式命名平台键，
/// 常见取值有 `windows-x86_64`、`windows-x86_64-nsis`、`nsis`、`x86_64-pc-windows-msvc`。
/// 这里按下列优先级挑选：
///
/// 1. 与目标三元组完全一致（`x86_64-pc-windows-msvc`）；
/// 2. `windows-<arch>` 前缀且带安装格式后缀（`windows-x86_64-nsis`）；
/// 3. 纯 `windows-<arch>`；
/// 4. 仅以打包格式命名的键（`nsis`）——单平台发布时 cargo-packager 会这样写。
///
/// 清单里完全没有 Windows 条目时返回 `None`，避免把别的平台的包当成 Windows 更新下下来。
fn pick_platform_key(platforms: &serde_json::Map<String, serde_json::Value>) -> Option<String> {
    /// 本机运行时的目标三元组，例如 `x86_64-pc-windows-msvc`。
    const TARGET_TRIPLE: &str = env!("TARGET_TRIPLE_FOR_UPDATER");
    /// 本机架构后缀，例如 `x86_64`。
    const ARCH_SUFFIX: &str = env!("TARGET_ARCH_FOR_UPDATER");

    // 只保留 Windows 相关条目，避免误选别的平台的包。
    let windows: Vec<&String> = platforms
        .keys()
        .filter(|key| {
            let lowered = key.to_ascii_lowercase();
            lowered.starts_with("windows")
                || lowered.contains("windows")
                || lowered == "nsis"
                || lowered == "msi"
        })
        .collect();

    let windows_arch = format!("windows-{ARCH_SUFFIX}");

    windows
        .iter()
        .find(|key| key.as_str() == TARGET_TRIPLE)
        .or_else(|| {
            windows.iter().find(|key| {
                let lowered = key.to_ascii_lowercase();
                // 例如 `windows-x86_64-nsis`。
                lowered.starts_with(&format!("{windows_arch}-"))
            })
        })
        .or_else(|| windows.iter().find(|key| key.as_str() == &windows_arch))
        .or_else(|| {
            windows.iter().find(|key| {
                let lowered = key.to_ascii_lowercase();
                lowered == "nsis" || lowered == "msi"
            })
        })
        .map(|key| (*key).clone())
}

// ─── 内部实现：基础工具 ───────────────────────────────────────────────────────

/// 发起一次简单的 GET 并取回文本；失败时返回 `None`，由调用方按「源不可用」处理。
fn http_get(url: &str) -> Option<String> {
    let client = reqwest::blocking::Client::builder()
        .timeout(CHECK_TIMEOUT)
        .user_agent(concat!("AstraBrew-Launcher/", env!("CARGO_PKG_VERSION")))
        .build()
        .ok()?;

    let response = client.get(url).send().ok()?.error_for_status().ok()?;
    // 非文本响应（例如 GitCode 的 HTML 外壳）会走到这里，交给调用方判为清单不可用。
    response.text().ok()
}

/// 读取当前编译版本号。
fn current_version() -> Result<cargo_packager_updater::semver::Version, String> {
    env!("CARGO_PKG_VERSION")
        .parse()
        .map_err(|_| "当前版本号格式不正确，无法检查更新。".to_owned())
}

#[cfg(test)]
mod tests {
    use super::*;

    /// GitCode `GET /repos/{owner}/{repo}/releases` 的真实响应（截取，字段名原样保留）。
    const GITCODE_RELEASES: &str = r#"[
      {
        "tag_name": "beta-v0.2.1",
        "target_commitish": "09bf076625366c5e0b516ae7e8d208e226b625a1",
        "prerelease": false,
        "name": "beta-v0.2.1",
        "body": "Full Changelog",
        "created_at": "2026-09-19T16:39:45+08:00",
        "assets": [
          {
            "browser_download_url": "https://raw.gitcode.com/AstraBrew-Labs/AstraBrew-Launcher-Win/archive/refs/heads/beta-v0.2.1.zip",
            "name": "beta-v0.2.1.zip",
            "type": "source"
          },
          {
            "browser_download_url": "https://gitcode.com/AstraBrew-Labs/AstraBrew-Launcher-Win/releases/download/beta-v0.2.1/AstraBrew Launcher_0.2.1_x64-setup.exe",
            "name": "AstraBrew Launcher_0.2.1_x64-setup.exe",
            "type": "attach"
          },
          {
            "browser_download_url": "https://gitcode.com/AstraBrew-Labs/AstraBrew-Launcher-Win/releases/download/beta-v0.2.1/latest.json",
            "name": "latest.json",
            "type": "attach"
          }
        ],
        "release_status": "latest"
      }
    ]"#;

    /// NSIS 安装包在清单里的下载地址（tag 写成 `v0.2.1`，与真实 Release tag 不一致）。
    const INSTALLER_URL: &str = "https://github.com/AstraBrew-Labs/AstraBrew-Launcher-Win/releases/download/v0.2.1/AstraBrew Launcher_0.2.1_x64-setup.exe";

    /// 仓库里当前**实际发布**的 `latest.json`：平台条目缺 `format`，且下载地址的 tag 写错了。
    const PUBLISHED_MANIFEST: &str = r#"{
      "version":"0.2.1",
      "notes":"新版本发布",
      "pub_date":"2026-09-19T11:49:52Z",
      "platforms":{
        "windows-x86_64":{
          "signature":"dW50cnVzdGVkIGNvbW1lbnQ6",
          "url":"https://github.com/AstraBrew-Labs/AstraBrew-Launcher-Win/releases/download/v0.2.1/AstraBrew Launcher_0.2.1_x64-setup.exe"
        }
      }
    }"#;

    fn releases() -> Vec<GitCodeRelease> {
        serde_json::from_str(GITCODE_RELEASES).expect("GitCode 响应样例必须可解析")
    }

    fn platforms(json: &str) -> serde_json::Map<String, serde_json::Value> {
        serde_json::from_str(json).expect("platforms 样例必须可解析")
    }

    fn version(text: &str) -> cargo_packager_updater::semver::Version {
        text.parse().expect("样例版本号必须可解析")
    }

    #[test]
    fn published_manifest_is_accepted() {
        // 回归测试：线上清单缺 `format`，而安装方式由扩展名决定、根本不读它，
        // 不能因为它拒绝整份清单。平台键 `windows-x86_64` 也必须能选中。
        let manifest = parse_manifest(PUBLISHED_MANIFEST).expect("线上清单必须可用");
        assert_eq!(manifest.version, version("0.2.1"));
        assert_eq!(manifest.notes.as_deref(), Some("新版本发布"));
        assert_eq!(manifest.platform.url, INSTALLER_URL);
        assert_eq!(manifest.platform.signature, "dW50cnVzdGVkIGNvbW1lbnQ6");
    }

    #[test]
    fn version_may_carry_a_v_prefix() {
        let json =
            r#"{"version":"v1.2.3","platforms":{"windows-x86_64":{"url":"u","signature":"s"}}}"#;
        assert_eq!(
            parse_manifest(json).expect("带 v 前缀的版本号应可解析").version,
            version("1.2.3")
        );
    }

    #[test]
    fn flat_manifest_is_accepted() {
        let flat =
            r#"{"version":"1.0.0","url":"https://example.com/setup.exe","signature":"sig"}"#;
        let manifest = parse_manifest(flat).expect("扁平清单必须可用");
        assert_eq!(manifest.platform.url, "https://example.com/setup.exe");
    }

    #[test]
    fn manifest_without_signature_is_rejected() {
        let json = r#"{"version":"1.0.0","platforms":{"windows-x86_64":{"url":"u"}}}"#;
        assert_eq!(
            parse_manifest(json).unwrap_err(),
            UpdateFailure::ManifestInvalid
        );
    }

    #[test]
    fn manifest_without_version_is_rejected() {
        let json = r#"{"platforms":{"windows-x86_64":{"url":"u","signature":"s"}}}"#;
        assert_eq!(
            parse_manifest(json).unwrap_err(),
            UpdateFailure::ManifestInvalid
        );
    }

    #[test]
    fn invalid_json_is_reported_as_manifest_invalid() {
        // 源站返回 HTML 外壳（GitCode 的 `releases/latest/download` 就是这样）时走到这里。
        assert_eq!(
            parse_manifest("<!DOCTYPE html><html></html>").unwrap_err(),
            UpdateFailure::ManifestInvalid
        );
    }

    #[test]
    fn manifest_without_windows_platform_is_rejected() {
        // 只有别的平台的包时绝不能当成 Windows 更新，否则会下载并拉起一个装不上的安装器。
        let json = r#"{"version":"1.0.0","platforms":{"linux-x86_64":{"url":"u","signature":"s"}}}"#;
        assert_eq!(
            parse_manifest(json).unwrap_err(),
            UpdateFailure::PlatformUnsupported
        );
    }

    #[test]
    fn windows_arch_key_wins_over_bare_nsis() {
        // cargo-packager 单平台发布时可能只写 `nsis`；带架构的键信息更全，应优先。
        let json = r#"{"nsis":{"url":"bare","signature":"s"},"windows-x86_64":{"url":"arch","signature":"s"}}"#;
        assert_eq!(
            parse_manifest(json).expect("应可解析").platform.url,
            "arch"
        );
    }

    #[test]
    fn installer_format_key_is_accepted_as_fallback() {
        // 只有格式命名（`nsis`）时仍应采用：它确实是 Windows 的安装包。
        let json = r#"{"nsis":{"url":"u","signature":"s"},"linux-x86_64":{"url":"l","signature":"s"}}"#;
        assert_eq!(
            parse_manifest(json).expect("应可解析").platform.url,
            "u"
        );
    }

    #[test]
    fn platform_is_rejected_when_only_foreign_keys_exist() {
        // 回归测试：曾经 macOS 版本会采纳另一架构的包（Rosetta 兜底），
        // Windows 没有这种等价关系，遇到 Linux 条目必须拒绝。
        let json = r#"{"darwin-x86_64":{"url":"u","signature":"s"},"linux-aarch64":{"url":"u","signature":"s"}}"#;
        assert_eq!(pick_platform_key(&platforms(json)), None);
    }

    #[test]
    fn manifest_download_url_is_corrected_by_asset_table() {
        // 清单里写的是 `v0.2.1`，实际 Release tag 是 `beta-v0.2.1`；靠资产表纠正。
        let list = releases();
        let release = pick_latest(&list).expect("应能选出最新发布");
        let corrected = corrected_download_url(INSTALLER_URL, &release.asset_table());
        assert_eq!(
            corrected.as_deref(),
            Some("https://gitcode.com/AstraBrew-Labs/AstraBrew-Launcher-Win/releases/download/beta-v0.2.1/AstraBrew Launcher_0.2.1_x64-setup.exe")
        );
    }

    #[test]
    fn correction_is_skipped_without_matching_asset() {
        // 直连源没有资产表；文件名对不上时也不应改动清单地址。
        let manifest_url = "https://github.com/example/releases/download/v1/setup.exe";
        assert_eq!(corrected_download_url(manifest_url, &[]), None);
        assert_eq!(
            corrected_download_url(manifest_url, &[("other.exe".to_owned(), "u".to_owned())]),
            None
        );
    }

    #[test]
    fn latest_release_is_picked_by_status_flag() {
        let list = releases();
        let release = pick_latest(&list).expect("应能选出最新发布");
        assert_eq!(release.tag_name, "beta-v0.2.1");
        assert!(release.is_latest());
    }

    #[test]
    fn release_assets_tolerate_missing_or_null_list() {
        let json = r#"[{"tag_name":"v1"},{"tag_name":"v2","assets":null}]"#;
        let list: Vec<GitCodeRelease> = serde_json::from_str(json).expect("缺省与 null 都应可解析");
        assert_eq!(list.len(), 2);
        assert!(list.iter().all(|release| release.assets.is_empty()));
    }

    #[test]
    fn manifest_endpoint_follows_the_real_tag() {
        // GitCode 的 `releases/latest/download` 不可用，必须落到具体 tag 上。
        let list = releases();
        let release = pick_latest(&list).expect("应能选出最新发布");
        let expected = "https://gitcode.com/AstraBrew-Labs/AstraBrew-Launcher-Win/releases/download/beta-v0.2.1/latest.json";
        assert_eq!(release.asset_url(MANIFEST), Some(expected));
        assert_eq!(mirror_download_url(&release.tag_name, MANIFEST), expected);
    }

    #[test]
    fn release_without_manifest_is_skipped() {
        let mut list = releases();
        list.push(GitCodeRelease {
            tag_name: "v9.9.9".to_owned(),
            release_status: Some("latest".to_owned()),
            assets: Vec::new(),
        });
        // 标记为 latest 却没有清单附件，应退回真正带清单的条目，而不是选中它。
        let release = pick_latest(&list).expect("应能选出最新发布");
        assert_eq!(release.tag_name, "beta-v0.2.1");
    }

    #[test]
    fn pick_latest_returns_none_for_empty_list() {
        assert!(pick_latest(&[]).is_none());
    }

    #[test]
    fn direct_source_uses_github_latest_shortcut() {
        assert_eq!(
            resolve_direct().manifest,
            "https://github.com/AstraBrew-Labs/AstraBrew-Launcher-Win/releases/latest/download/latest.json"
        );
    }

    #[test]
    fn current_version_parses_package_version() {
        let version = current_version().expect("CARGO_PKG_VERSION 必须可解析");
        assert_eq!(version.to_string(), env!("CARGO_PKG_VERSION"));
    }

    #[test]
    fn newer_version_wins_between_sources() {
        assert!(is_newer("0.3.0", "0.2.0"));
        assert!(is_newer("0.2.1", "0.2.0"));
        assert!(!is_newer("0.2.0", "0.2.0"));
        assert!(!is_newer("0.1.0", "0.2.0"));
    }

    #[test]
    fn unparsable_version_never_replaces_the_incumbent() {
        assert!(!is_newer("beta-v0.0.1", "0.2.0"));
        assert!(!is_newer("0.3.0", "not-a-version"));
    }

    #[test]
    fn extract_path_uses_the_executable_directory() {
        // Windows 上是单文件安装，可执行文件所在目录即安装目录，无需路径上溯。
        let path = extract_path().expect("测试环境应能确定可执行文件目录");
        assert!(path.is_dir(), "{path:?} 应是已存在的目录");
    }

    #[test]
    fn installer_and_portable_assets_are_told_apart_by_extension() {
        assert_eq!(
            install_target("https://example.com/AstraBrew Launcher_0.2.1_x64-setup.exe"),
            InstallTarget::InstallerScript
        );
        assert_eq!(
            install_target("https://example.com/AstraBrew Launcher_0.2.1_x64_portable.zip"),
            InstallTarget::PortableArchive
        );
        // 大小写不敏感：发布侧可能写成 `.EXE`。
        assert_eq!(
            install_target("https://example.com/SETUP.EXE"),
            InstallTarget::InstallerScript
        );
        // 查询串不应干扰扩展名判断。
        assert_eq!(
            install_target("https://example.com/setup.exe?token=abc"),
            InstallTarget::InstallerScript
        );
    }

    #[test]
    fn unknown_asset_extension_is_rejected() {
        // `.dmg` / `.app.tar.gz` 是旧平台的发布物，装不了也绝不能当成 Windows 包。
        assert_eq!(
            install_target("https://example.com/AstraBrew.dmg"),
            InstallTarget::Unknown
        );
        assert_eq!(
            install_target("https://example.com/AstraBrew-Launcher_universal.app.tar.gz"),
            InstallTarget::Unknown
        );
        assert_eq!(install_target("https://example.com/no-extension"), InstallTarget::Unknown);
    }

    #[test]
    fn current_executable_name_matches_the_test_binary() {
        let name = current_executable_name().expect("测试环境应能取出可执行文件名");
        assert!(name.to_ascii_lowercase().ends_with(".exe"), "{name} 应是 .exe");
    }

    #[test]
    fn recursive_lookup_finds_files_nested_in_archives() {
        let root = std::env::temp_dir().join(format!(
            "astrabrew-updater-test-{}",
            std::process::id()
        ));
        let nested = root.join("inner").join("deeper");
        std::fs::create_dir_all(&nested).expect("应能建立临时目录");
        let target = nested.join("AstraBrew Launcher.exe");
        std::fs::write(&target, b"stub").expect("应能写入临时文件");

        assert_eq!(
            find_file_recursively(&root, "AstraBrew Launcher.exe"),
            Some(target)
        );
        assert_eq!(find_file_recursively(&root, "missing.exe"), None);

        let _ = std::fs::remove_dir_all(&root);
    }

    #[test]
    fn only_detail_failures_need_a_template() {
        for failure in [
            UpdateFailure::ManifestUnreachable,
            UpdateFailure::ManifestInvalid,
            UpdateFailure::PlatformUnsupported,
            UpdateFailure::InstallLocationUnknown,
            UpdateFailure::SourcesUnreachable,
            UpdateFailure::Expired,
        ] {
            assert!(failure.message_key().is_some(), "{failure:?} 应有文案键");
            assert!(failure.detail().is_none(), "{failure:?} 不应携带详情");
        }
        let detail = UpdateFailure::Detail("boom".to_owned());
        assert!(detail.message_key().is_none());
        assert_eq!(detail.detail(), Some("boom"));
    }
}
