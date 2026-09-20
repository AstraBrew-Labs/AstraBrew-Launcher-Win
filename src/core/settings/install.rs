//! 内置环境的下载与安装。
//!
//! 启动器把 Git / Node.js / Caddy / WebView2 / PM2 安装到
//! `%AppData%/AstraBrew Launcher/lib/` 下，供内置环境模式直接使用。
//! 下载产物统一先落到 `%Temp%/astrabrew-launcher/`，安装成功后清除。
//!
//! # 与界面线程的通信约定
//!
//! 安装全程运行在后台线程，通过 [`std::sync::mpsc::Sender<String>`] 以「行协议」
//! 上报进度，由 `app.rs` 的 `poll_environment_task` 解析。协议标记：
//!
//! | 标记 | 含义 |
//! | --- | --- |
//! | `__PROGRESS__:<0-100>` | 确定进度百分比 |
//! | `__STATUS__:<文案键>` | 阶段提示，**键**，由界面线程翻译 |
//! | `__NOTICE__:<文案键>` | 重点提示，**键**，由界面线程翻译 |
//! | `__VERSION__:<版本号>` | 安装后探测到的版本 |
//! | `__ERROR__:<已翻译文案>` | 错误详情，**进通道前已翻译完毕** |
//! | `__FAILED__` | 安装失败 |
//! | `__CANCELLED__` | 用户取消 |
//! | `__DONE__` | 安装成功 |
//!
//! 注意 `__STATUS__` / `__NOTICE__` 传键、`__ERROR__` 传已翻译文案，二者不可混用：
//! 后台线程读不到界面线程的语言设置（`lang::t` 依赖线程局部变量），
//! 所以要么传键让界面线程翻译，要么在**本线程**用 `tf()` / `t()` 固化成文案。

use std::fs::{self, File};
use std::io::{self, Read, Write};
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::Sender;
use std::time::{Duration, Instant};
use zip::ZipArchive;

use crate::core::env::{apply_no_window_to_command, get_lib_dir};
use crate::core::settings::EnvSource;
use crate::pages::settings::EnvironmentDependency;

/// 单块读取缓冲区大小（8 KiB，兼顾吞吐与内存）。
const READ_BUFFER_SIZE: usize = 8192;

/// 进度上报的最小间隔，避免高频 send 拖慢解压循环。
const PROGRESS_REPORT_INTERVAL: Duration = Duration::from_millis(500);

// ─── 下载源定义 ──────────────────────────────────────────────────────────────

/// Git for Windows（MinGit 便携版）版本与文件名。
const GIT_VERSION: &str = "2.55.0.windows.2";
const GIT_ARCHIVE: &str = "MinGit-2.55.0.2-64-bit.zip";

/// Node.js LTS 版本与文件名。
const NODE_VERSION: &str = "v24.14.0";
const NODE_ARCHIVE: &str = "node-v24.14.0-win-x64.zip";

/// Caddy 发行包文件名与下载地址。
///
/// 不额外维护版本常量：安装完成后由 `caddy version` 实测版本号，
/// 避免常量与实际下载内容不一致。
const CADDY_ARCHIVE: &str = "caddy_2.11.4_windows_amd64.zip";
const CADDY_SOURCE: &str = "https://github.com/caddyserver/caddy/releases/download/v2.11.4/caddy_2.11.4_windows_amd64.zip";

/// WebView2 固定版：官方接口不可用时的兜底版本与直链。
const WEBVIEW2_ARCH: &str = "x64";
const WEBVIEW2_API_URL: &str = "https://developer.microsoft.com/microsoft-edge/api/webview2";
const WEBVIEW2_FALLBACK_VERSION: &str = "150.0.4078.65";
const WEBVIEW2_FALLBACK_URL: &str = "https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/c00b9782-0422-4114-be27-8eec079b394d/Microsoft.WebView2.FixedVersionRuntime.150.0.4078.65.x64.cab";

// ─── 对外接口 ────────────────────────────────────────────────────────────────

/// 安装流程运行期参数。
///
/// 由界面线程从用户设置中整理好后传入，避免后台线程反向读取共享状态。
#[derive(Debug, Clone)]
pub struct InstallOptions {
    /// npm 软件源地址；为空字符串表示使用 npm 默认源。
    pub npm_registry: String,
    /// 代理模式：`none` / `system` / `custom`。
    pub proxy_mode: String,
    /// 自定义代理地址（`host:port`），仅 `custom` 模式使用。
    pub proxy_host: String,
}

/// 按依赖类型分发到具体安装流程。
///
/// 无论成功、失败还是取消，都会通过 `sender` 发出对应的结束标记，
/// 界面线程据此收尾；本函数本身不返回错误，避免调用方遗漏上报。
pub fn install_dependency(
    dependency: EnvironmentDependency,
    source: EnvSource,
    options: InstallOptions,
    sender: Sender<String>,
    cancel: Arc<AtomicBool>,
) {
    // 内置环境是启动器唯一维护的安装目标；系统环境由用户自行管理。
    debug_assert_eq!(source, EnvSource::Builtin, "只应安装到内置环境目录");

    let context = InstallContext {
        options,
        sender: sender.clone(),
        cancel,
    };

    let result = match dependency {
        EnvironmentDependency::Git => install_git(&context),
        EnvironmentDependency::NodeJs => install_nodejs(&context),
        EnvironmentDependency::Caddy => install_caddy(&context),
        EnvironmentDependency::Pm2 => install_pm2(&context),
        EnvironmentDependency::WebView2 => install_webview2(&context),
    };

    match result {
        Ok(()) => {}
        Err(InstallError::Cancelled) => {
            let _ = sender.send(crate::core::settings::env_detect::MARKER_CANCELLED.to_owned());
        }
        Err(InstallError::Message(message)) => {
            // 错误文案在本线程翻译完毕后进入通道，界面线程直接显示。
            let _ = sender.send(format!("__ERROR__:{message}"));
            let _ = sender.send(crate::core::settings::env_detect::MARKER_FAILED.to_owned());
        }
    }
}

// ─── 安装上下文 ──────────────────────────────────────────────────────────────

/// 一次安装任务的运行上下文。
///
/// 把 channel、取消令牌与代理设置打包，减少层层传参；
/// 同时集中提供 `progress` / `status` / `notice` 等上报助手。
struct InstallContext {
    options: InstallOptions,
    sender: Sender<String>,
    cancel: Arc<AtomicBool>,
}

impl InstallContext {
    /// 是否已收到取消请求。
    fn is_cancelled(&self) -> bool {
        self.cancel.load(Ordering::Relaxed)
    }

    /// 上报确定进度（0-100）。
    fn progress(&self, percent: f32) {
        let clamped = percent.clamp(0.0, 100.0);
        let _ = self.sender.send(format!("__PROGRESS__:{clamped}"));
    }

    /// 上报阶段提示；传**文案键**，由界面线程翻译。
    fn status(&self, key: &str) {
        let _ = self.sender.send(format!("__STATUS__:{key}"));
    }

    /// 上报重点提示；传**文案键**，由界面线程翻译。
    fn notice(&self, key: &str) {
        let _ = self.sender.send(format!("__NOTICE__:{key}"));
    }

    /// 上报安装结果版本号。
    fn version(&self, version: &str) {
        let _ = self.sender.send(format!("__VERSION__:{version}"));
    }

    /// 上报成功结束标记。
    fn done(&self) {
        let _ = self
            .sender
            .send(crate::core::settings::env_detect::MARKER_DONE.to_owned());
    }

    /// 在日志区追加一行运行时信息（路径、大小等自由文本）。
    fn log(&self, line: impl Into<String>) {
        let _ = self.sender.send(line.into());
    }

    /// 构建带代理设置的阻塞式 HTTP 客户端。
    ///
    /// 安装包体积较大（Node.js 约 30 MB、WebView2 约 200 MB），
    /// 因此超时放宽到 30 分钟，并禁用 reqwest 的自动代理探测，
    /// 完全以用户选择的代理模式为准。
    fn client(&self, timeout: Duration) -> Result<reqwest::blocking::Client, InstallError> {
        let mut builder = reqwest::blocking::Client::builder()
            .no_proxy()
            .timeout(timeout)
            .user_agent("AstraBrew-Launcher-Windows");
        if let Some(proxy_url) = resolve_proxy_url(&self.options)? {
            let proxy = reqwest::Proxy::all(&proxy_url).map_err(|error| {
                InstallError::translated(crate::lang::tf(
                    "install.error.proxy_invalid",
                    &[("error", &error)],
                ))
            })?;
            builder = builder.proxy(proxy);
        }
        builder
            .build()
            .map_err(|error| InstallError::translated(error.to_string()))
    }
}

/// 解析出实际生效的代理地址。
///
/// - `none`：不使用代理；
/// - `system`：读取注册表中的系统代理；
/// - `custom`：直接使用用户填写的 `host:port`；
/// - 其余取值：视为不启用代理。
fn resolve_proxy_url(options: &InstallOptions) -> Result<Option<String>, InstallError> {
    match options.proxy_mode.as_str() {
        "system" => Ok(crate::core::network::read_system_proxy()
            .filter(|(_, enabled)| *enabled)
            .and_then(|(server, _)| normalize_proxy_url(&server))),
        "custom" => {
            let host = options.proxy_host.trim();
            if host.is_empty() {
                Ok(None)
            } else {
                Ok(normalize_proxy_url(host))
            }
        }
        _ => Ok(None),
    }
}

/// 给缺协议的代理地址补上 `http://`，并做最基础的合法性校验。
fn normalize_proxy_url(raw: &str) -> Option<String> {
    let trimmed = raw.trim();
    if trimmed.is_empty() {
        return None;
    }
    let candidate = if trimmed.contains("://") {
        trimmed.to_owned()
    } else {
        format!("http://{trimmed}")
    };
    let parsed = reqwest::Url::parse(&candidate).ok()?;
    matches!(parsed.scheme(), "http" | "https").then_some(candidate)
}

// ─── 错误类型 ────────────────────────────────────────────────────────────────

/// 安装过程中的失败原因。
///
/// 区分「用户取消」与「真实错误」，是因为两者在界面上的收尾方式完全不同：
/// 取消要静默关闭进度窗口，错误要保留日志并高亮。
#[derive(Debug)]
enum InstallError {
    /// 用户主动取消。
    Cancelled,
    /// 已翻译好的错误文案。
    Message(String),
}

impl InstallError {
    /// 由已翻译文案构造错误。
    fn translated(message: impl Into<String>) -> Self {
        Self::Message(message.into())
    }

    /// 由文案键构造错误，在本线程翻译成实际文案。
    fn key(key: &'static str) -> Self {
        Self::Message(crate::lang::t(key).to_owned())
    }
}

impl From<String> for InstallError {
    fn from(message: String) -> Self {
        Self::Message(message)
    }
}

impl From<&str> for InstallError {
    fn from(message: &str) -> Self {
        Self::Message(message.to_owned())
    }
}

impl From<io::Error> for InstallError {
    fn from(error: io::Error) -> Self {
        if error.kind() == io::ErrorKind::Interrupted {
            Self::Cancelled
        } else {
            Self::Message(error.to_string())
        }
    }
}

impl From<zip::result::ZipError> for InstallError {
    fn from(error: zip::result::ZipError) -> Self {
        Self::Message(error.to_string())
    }
}

impl From<std::path::StripPrefixError> for InstallError {
    fn from(error: std::path::StripPrefixError) -> Self {
        Self::Message(error.to_string())
    }
}

/// 结果别名，减少签名噪声。
type InstallResult = Result<(), InstallError>;

/// 检查取消状态；已取消则删除半成品并返回 [`InstallError::Cancelled`]。
macro_rules! ensure_not_cancelled {
    ($context:expr, $cleanup:expr) => {
        if $context.is_cancelled() {
            $cleanup;
            return Err(InstallError::Cancelled);
        }
    };
}

// ─── 通用路径 ────────────────────────────────────────────────────────────────

/// 内置依赖的安装目录：`<root>/lib/<name>/`。
fn install_dir(name: &str) -> PathBuf {
    get_lib_dir().join(name)
}

/// 下载临时目录：`%Temp%/astrabrew-launcher/`。
fn temp_dir() -> PathBuf {
    crate::utils::app_paths().temp.clone()
}

/// 确保临时目录存在并返回。
fn ensure_temp_dir() -> PathResult<PathBuf> {
    let dir = temp_dir();
    fs::create_dir_all(&dir)?;
    Ok(dir)
}

/// 带返回值的内部结果别名。
type PathResult<T> = Result<T, InstallError>;

// ─── 通用下载 ────────────────────────────────────────────────────────────────

/// 下载进度回调：`(已下载字节, 总字节, 进度比例 0-1)`。
type ProgressFn<'a> = &'a dyn Fn(u64, u64, f32);

/// 下载 `url` 到 `destination`，支持取消与进度回报。
///
/// `progress_scale` 是本次下载在整体进度中的占比（如 0.5 表示下载占总进度的一半），
/// 进度会按该比例缩放到 0-100 后上报。
fn download_to_file(
    context: &InstallContext,
    url: &str,
    destination: &Path,
    progress_scale: f32,
    on_progress: Option<ProgressFn<'_>>,
) -> InstallResult {
    let client = context.client(Duration::from_secs(1800))?;
    let mut response = client
        .get(url)
        .send()
        .map_err(|error| InstallError::translated(error.to_string()))?;

    if !response.status().is_success() {
        return Err(InstallError::translated(crate::lang::tf(
            "install.error.http_status",
            &[("code", &response.status().as_u16())],
        )));
    }

    let total_size = response.content_length().unwrap_or(0);
    if total_size > 0 {
        let size_mb = total_size as f64 / 1_048_576.0;
        context.log(crate::lang::tf(
            "install.log.download_size",
            &[("size", &format!("{size_mb:.1}"))],
        ));
    }

    let mut file = File::create(destination)?;
    let mut buffer = [0u8; READ_BUFFER_SIZE];
    let mut downloaded: u64 = 0;
    let mut last_report = Instant::now();

    loop {
        if context.is_cancelled() {
            drop(file);
            let _ = fs::remove_file(destination);
            return Err(InstallError::Cancelled);
        }
        let read = response
            .read(&mut buffer)
            .map_err(|error| InstallError::translated(error.to_string()))?;
        if read == 0 {
            break;
        }
        file.write_all(&buffer[..read])?;
        downloaded += read as u64;

        let ratio = if total_size > 0 {
            downloaded as f32 / total_size as f32
        } else {
            0.0
        };
        if last_report.elapsed() >= PROGRESS_REPORT_INTERVAL {
            context.progress(ratio * progress_scale * 100.0);
            if let Some(callback) = on_progress {
                callback(downloaded, total_size, ratio);
            }
            last_report = Instant::now();
        }
    }

    // 收尾时补一次 100%，避免最后一小段因节流被吞掉。
    context.progress(progress_scale * 100.0);
    Ok(())
}

// ─── 通用解压 ────────────────────────────────────────────────────────────────

/// 解压 ZIP 到 `target`，可选剥离顶层目录。
///
/// - `strip_top_level`：Node.js 的官方 ZIP 顶层是 `node-v24.14.0-win-x64/`，
///   必须剥离才能让 `node.exe` 落在 `lib/nodejs/` 根下；Git 与 Caddy 不需剥离。
/// - `progress_range`：解压在整体进度中的区间 `(起点%, 终点%)`。
fn extract_zip(
    context: &InstallContext,
    archive_path: &Path,
    target: &Path,
    strip_top_level: bool,
    progress_range: (f32, f32),
) -> InstallResult {
    let file = File::open(archive_path)?;
    let mut archive = ZipArchive::new(file)?;
    let total_entries = archive.len();
    if total_entries == 0 {
        return Err(InstallError::key("install.error.archive_empty"));
    }

    let (start, end) = progress_range;
    let mut last_report = Instant::now();

    for index in 0..total_entries {
        ensure_not_cancelled!(
            context,
            {
                drop(archive);
                let _ = fs::remove_dir_all(target);
            }
        );

        let mut entry = archive.by_index(index)?;
        // `enclosed_name` 会拒绝 `..` 等越界路径，避免 ZIP 目录穿越。
        let Some(relative) = entry.enclosed_name() else {
            continue;
        };
        let relative = if strip_top_level {
            let mut components = relative.components();
            components.next();
            components.as_path().to_path_buf()
        } else {
            relative.to_path_buf()
        };
        if relative.as_os_str().is_empty() {
            continue;
        }

        let final_path = target.join(&relative);
        if entry.is_dir() {
            fs::create_dir_all(&final_path)?;
        } else {
            if let Some(parent) = final_path.parent() {
                fs::create_dir_all(parent)?;
            }
            let mut output = File::create(&final_path)?;
            io::copy(&mut entry, &mut output)?;
        }

        if last_report.elapsed() >= PROGRESS_REPORT_INTERVAL {
            let ratio = (index + 1) as f32 / total_entries as f32;
            context.progress(start + (end - start) * ratio);
            last_report = Instant::now();
        }
    }

    context.progress(end);
    Ok(())
}

/// 清空并重建目标目录，避免旧版本残留文件造成冲突。
fn reset_directory(target: &Path) -> InstallResult {
    if target.exists() {
        fs::remove_dir_all(target)?;
    }
    fs::create_dir_all(target)?;
    Ok(())
}

// ─── 通用校验 ────────────────────────────────────────────────────────────────

/// 执行可执行文件并返回 stdout（合并 stderr），失败返回 `None`。
fn run_version_output(executable: &Path, args: &[&str]) -> Option<String> {
    let mut command = Command::new(executable);
    command.args(args);
    apply_no_window_to_command(&mut command);
    let output = command.output().ok()?;
    if !output.status.success() {
        return None;
    }
    let mut text = String::from_utf8_lossy(&output.stdout).trim().to_owned();
    if text.is_empty() {
        text = String::from_utf8_lossy(&output.stderr).trim().to_owned();
    }
    (!text.is_empty()).then_some(text)
}

// ─── Git ─────────────────────────────────────────────────────────────────────

/// Git 镜像节点：名称 + 下载地址。
struct MirrorNode {
    name: &'static str,
    url: String,
}

/// MinGit 的可用镜像列表。
fn git_mirrors() -> Vec<MirrorNode> {
    vec![
        MirrorNode {
            name: "GitHub",
            url: format!(
                "https://github.com/git-for-windows/git/releases/download/v{GIT_VERSION}/{GIT_ARCHIVE}"
            ),
        },
        MirrorNode {
            name: "HuaweiCloud",
            url: format!("https://mirrors.huaweicloud.com/git-for-windows/v{GIT_VERSION}/{GIT_ARCHIVE}"),
        },
        MirrorNode {
            name: "NPMMirror",
            url: format!(
                "https://registry.npmmirror.com/-/binary/git-for-windows/v{GIT_VERSION}/{GIT_ARCHIVE}"
            ),
        },
        MirrorNode {
            name: "Tsinghua",
            url: format!(
                "https://mirrors.tuna.tsinghua.edu.cn/github-release/git-for-windows/git/LatestRelease/{GIT_ARCHIVE}"
            ),
        },
    ]
}

/// 下载并安装 MinGit 到 `lib/git/`。
fn install_git(context: &InstallContext) -> InstallResult {
    let target = install_dir("git");
    let temp = ensure_temp_dir()?;
    let archive_path = temp.join(GIT_ARCHIVE);

    context.status("install.status.selecting_mirror");
    let node = select_mirror(context, git_mirrors(), "install.status.testing_git_mirror")?;
    context.log(crate::lang::tf(
        "install.log.mirror_selected",
        &[("name", &node.name)],
    ));

    context.status("install.status.downloading");
    download_to_file(context, &node.url, &archive_path, 50.0, None)?;

    context.status("install.status.extracting");
    reset_directory(&target)?;
    extract_zip(context, &archive_path, &target, false, (50.0, 100.0))?;
    let _ = fs::remove_file(&archive_path);

    // 验证：`git version 2.55.0.windows.2` → `2.55.0.windows.2`
    let executable = target.join("cmd").join("git.exe");
    let version = run_version_output(&executable, &["--version"])
        .and_then(|text| text.strip_prefix("git version ").map(str::to_owned));
    finish(context, version, "install.error.git_verify_failed", &target)
}

// ─── Node.js ─────────────────────────────────────────────────────────────────

/// Node.js 镜像列表。
fn node_mirrors() -> Vec<MirrorNode> {
    vec![
        MirrorNode {
            name: "Node.js",
            url: format!("https://nodejs.org/download/release/{NODE_VERSION}/{NODE_ARCHIVE}"),
        },
        MirrorNode {
            name: "HuaweiCloud",
            url: format!("https://mirrors.huaweicloud.com/nodejs/{NODE_VERSION}/{NODE_ARCHIVE}"),
        },
        MirrorNode {
            name: "NPMMirror",
            url: format!("https://registry.npmmirror.com/-/binary/node/{NODE_VERSION}/{NODE_ARCHIVE}"),
        },
        MirrorNode {
            name: "Aliyun",
            url: format!("https://mirrors.aliyun.com/nodejs-release/{NODE_VERSION}/{NODE_ARCHIVE}"),
        },
    ]
}

/// 下载并安装 Node.js 到 `lib/nodejs/`。
fn install_nodejs(context: &InstallContext) -> InstallResult {
    let target = install_dir("nodejs");
    let temp = ensure_temp_dir()?;
    let archive_path = temp.join(NODE_ARCHIVE);

    context.status("install.status.selecting_mirror");
    let node = select_mirror(context, node_mirrors(), "install.status.testing_node_mirror")?;
    context.log(crate::lang::tf(
        "install.log.mirror_selected",
        &[("name", &node.name)],
    ));

    context.status("install.status.downloading");
    download_to_file(context, &node.url, &archive_path, 50.0, None)?;

    context.status("install.status.extracting");
    reset_directory(&target)?;
    // 官方 ZIP 顶层带 `node-vXX-win-x64/`，必须剥离。
    extract_zip(context, &archive_path, &target, true, (50.0, 100.0))?;
    let _ = fs::remove_file(&archive_path);

    let executable = target.join("node.exe");
    let version = run_version_output(&executable, &["--version"]);
    let result = finish(
        context,
        version,
        "install.error.nodejs_verify_failed",
        &target,
    );
    if result.is_ok() {
        // Node.js 就位后 npm 才可用，这里顺带提示用户无需全局链接。
        context.notice("environment.install.nodejs_keg_ready");
    }
    result
}

// ─── Caddy ───────────────────────────────────────────────────────────────────

/// 下载并安装 Caddy 到 `lib/caddy/`。
///
/// Caddy 的发行包托管在 GitHub Releases，安装阶段直接复用用户选择的代理，
/// 不再单独维护镜像列表。
fn install_caddy(context: &InstallContext) -> InstallResult {
    let target = install_dir("caddy");
    let temp = ensure_temp_dir()?;
    let archive_path = temp.join(CADDY_ARCHIVE);

    context.status("install.status.downloading");
    download_to_file(context, CADDY_SOURCE, &archive_path, 50.0, None)?;

    context.status("install.status.extracting");
    reset_directory(&target)?;
    extract_zip(context, &archive_path, &target, false, (50.0, 100.0))?;
    let _ = fs::remove_file(&archive_path);

    // 发行包内的文件名带平台后缀，统一改名为 `caddy.exe` 便于后续调用。
    let packaged = target.join("caddy_windows_amd64.exe");
    let executable = target.join("caddy.exe");
    if packaged.is_file() && !executable.is_file() {
        fs::rename(&packaged, &executable)?;
    }

    // `caddy version` 输出形如 `v2.11.4 h1:...`，只取第一段。
    let version = run_version_output(&executable, &["version"])
        .and_then(|text| text.split_whitespace().next().map(str::to_owned));
    finish(
        context,
        version,
        "install.error.caddy_verify_failed",
        &target,
    )
}

// ─── PM2 ─────────────────────────────────────────────────────────────────────

/// 通过 npm 把 PM2 安装到 `lib/pm2/`，并生成包装脚本。
///
/// PM2 依赖 Node.js，因此未检测到内置 npm 时直接报错而非回退，
/// 避免把 PM2 散落到用户全局 npm 目录里。
fn install_pm2(context: &InstallContext) -> InstallResult {
    use crate::core::settings::env_detect::{prepare_install_command, run_logged_command};

    let lib = get_lib_dir();
    let target = lib.join("pm2");
    let npm = lib.join("nodejs").join("npm.cmd");
    if !npm.is_file() {
        return Err(InstallError::key("install.error.pm2_needs_nodejs"));
    }

    context.notice("environment.install.pm2.preparing");
    context.status("environment.install.pm2.installing");
    reset_directory(&target)?;

    // `cmd /c` 包装：Windows 无法直接执行 `.cmd`。
    let mut command = Command::new("cmd");
    command.arg("/c").arg(&npm);
    command.args(["install", "pm2", "--prefix"]).arg(&target);
    // 不生成锁文件、关闭进度条/审计/funding，让日志更干净。
    command.args([
        "--no-package-lock",
        "--no-progress",
        "--no-audit",
        "--no-fund",
    ]);
    if !context.options.npm_registry.trim().is_empty() {
        command
            .arg("--registry")
            .arg(context.options.npm_registry.trim());
    }
    let mut command = prepare_install_command(command);

    let child = command
        .spawn()
        .map_err(|error| InstallError::translated(error.to_string()))?;
    run_logged_command(
        child,
        context.sender.clone(),
        "pm2",
        EnvSource::Builtin,
        context.cancel.clone(),
    );

    // 只有确认 npm 安装成功后才写包装脚本：`run_logged_command` 会自行上报
    // 版本号与结束标记，此处只需判断是否需要补齐收尾产物。
    let script = target.join("node_modules").join("pm2").join("bin").join("pm2");
    if !script.is_file() {
        return Ok(());
    }
    write_pm2_wrapper(&target)?;
    fs::create_dir_all(crate::core::pm2::pm2_runtime_dir())?;
    Ok(())
}

/// 生成 `pm2.cmd` / `pm2-runtime.cmd` 包装脚本。
///
/// 脚本内显式固定 `PM2_HOME`，保证内置 PM2 与系统 PM2 读写同一份进程表。
fn write_pm2_wrapper(target: &Path) -> InstallResult {
    let template = |entry: &str| {
        format!(
            "@echo off\r\nset PM2_HOME=%~dp0runtime\\pm2\r\n\"%~dp0..\\nodejs\\node.exe\" \"%~dp0node_modules\\pm2\\bin\\{entry}\" %*\r\n"
        )
    };
    fs::write(target.join("pm2.cmd"), template("pm2"))?;
    fs::write(target.join("pm2-runtime.cmd"), template("pm2-runtime"))?;
    Ok(())
}

// ─── WebView2 ────────────────────────────────────────────────────────────────

/// 官方接口返回的固定版运行时清单项。
#[derive(serde::Deserialize)]
struct WebView2Release {
    version: String,
    builds: Vec<WebView2Build>,
}

/// 单一架构的下载信息。
#[derive(serde::Deserialize)]
struct WebView2Build {
    architecture: String,
    url: String,
}

/// 下载并安装 WebView2 固定版运行时到 `lib/webview2/`。
///
/// 流程：查询官方接口拿最新固定版 → 下载 CAB → `expand.exe` 解包 →
/// 定位含 `msedgewebview2.exe` 的目录 → 复制到安装目录 → 写 `version.txt`。
fn install_webview2(context: &InstallContext) -> InstallResult {
    let target = install_dir("webview2");
    let temp = ensure_temp_dir()?;

    context.status("install.status.fetching_webview2");
    let (version, url) = resolve_webview2_package(context);

    let archive_name = format!("Microsoft.WebView2.FixedVersionRuntime.{version}.{WEBVIEW2_ARCH}.cab");
    let archive_path = temp.join(&archive_name);
    let expand_dir = temp.join(format!("webview2-expand-{version}"));

    context.log(crate::lang::tf(
        "install.log.mirror_selected",
        &[("name", &format!("WebView2 {version}"))],
    ));

    context.status("install.status.downloading");
    download_to_file(context, &url, &archive_path, 60.0, None)?;
    ensure_not_cancelled!(context, {
        let _ = fs::remove_file(&archive_path);
    });

    context.status("install.status.extracting");
    if expand_dir.exists() {
        fs::remove_dir_all(&expand_dir)?;
    }
    fs::create_dir_all(&expand_dir)?;
    expand_cab(&archive_path, &expand_dir)?;
    let _ = fs::remove_file(&archive_path);

    let runtime_root = find_runtime_root(&expand_dir)
        .ok_or_else(|| InstallError::key("install.error.webview2_runtime_missing"))?;

    context.status("install.status.installing_files");
    reset_directory(&target)?;
    copy_tree(context, &runtime_root, &target, (60.0, 100.0))?;
    let _ = fs::remove_dir_all(&expand_dir);

    // `version.txt` 是设置页快速读取内置 WebView2 版本的依据。
    fs::write(target.join("version.txt"), &version)?;

    if !target.join("msedgewebview2.exe").is_file() {
        return Err(InstallError::key("install.error.webview2_verify_failed"));
    }
    finish(
        context,
        Some(version),
        "install.error.webview2_verify_failed",
        &target,
    )
}

/// 查询官方接口获取最新固定版；接口异常时回退到内置的已知稳定版本。
fn resolve_webview2_package(context: &InstallContext) -> (String, String) {
    let fallback = || {
        (
            WEBVIEW2_FALLBACK_VERSION.to_owned(),
            WEBVIEW2_FALLBACK_URL.to_owned(),
        )
    };
    let Ok(client) = context.client(Duration::from_secs(30)) else {
        return fallback();
    };
    let Ok(response) = client.get(WEBVIEW2_API_URL).send() else {
        return fallback();
    };
    let Ok(releases) = response.json::<Vec<WebView2Release>>() else {
        return fallback();
    };
    releases
        .into_iter()
        .find_map(|release| {
            release
                .builds
                .into_iter()
                .find(|build| build.architecture.eq_ignore_ascii_case(WEBVIEW2_ARCH))
                .map(|build| (release.version, build.url))
        })
        .unwrap_or_else(fallback)
}

/// 用系统自带的 `expand.exe` 解包 CAB。
fn expand_cab(archive: &Path, destination: &Path) -> InstallResult {
    let mut command = Command::new("expand.exe");
    apply_no_window_to_command(&mut command);
    let output = command
        .arg(archive)
        .arg("-F:*")
        .arg(destination)
        .output()
        .map_err(|error| InstallError::translated(error.to_string()))?;
    if output.status.success() {
        return Ok(());
    }
    // 失败时优先展示 stderr，为空则退回 stdout。
    let stderr = String::from_utf8_lossy(&output.stderr).trim().to_owned();
    let stdout = String::from_utf8_lossy(&output.stdout).trim().to_owned();
    let detail = if stderr.is_empty() { stdout } else { stderr };
    Err(InstallError::translated(crate::lang::tf(
        "install.error.cab_expand_failed",
        &[("error", &detail)],
    )))
}

/// 递归查找包含 `msedgewebview2.exe` 的运行时根目录。
fn find_runtime_root(dir: &Path) -> Option<PathBuf> {
    if dir.join("msedgewebview2.exe").is_file() {
        return Some(dir.to_path_buf());
    }
    let entries = fs::read_dir(dir).ok()?;
    for entry in entries.flatten() {
        let path = entry.path();
        if path.is_dir()
            && let Some(found) = find_runtime_root(&path)
        {
            return Some(found);
        }
    }
    None
}

/// 递归复制目录树，并按区间上报进度。
fn copy_tree(
    context: &InstallContext,
    source: &Path,
    destination: &Path,
    progress_range: (f32, f32),
) -> InstallResult {
    let mut files = Vec::new();
    collect_files(source, &mut files)?;
    if files.is_empty() {
        return Err(InstallError::key("install.error.archive_empty"));
    }

    let (start, end) = progress_range;
    let total = files.len() as f32;
    for (index, file) in files.iter().enumerate() {
        ensure_not_cancelled!(context, {
            let _ = fs::remove_dir_all(destination);
        });
        let relative = file.strip_prefix(source)?;
        let target_path = destination.join(relative);
        if let Some(parent) = target_path.parent() {
            fs::create_dir_all(parent)?;
        }
        fs::copy(file, &target_path)?;

        if index % 16 == 0 {
            context.progress(start + (end - start) * ((index + 1) as f32 / total));
        }
    }
    Ok(())
}

/// 递归收集目录中的全部文件路径。
fn collect_files(dir: &Path, output: &mut Vec<PathBuf>) -> io::Result<()> {
    for entry in fs::read_dir(dir)? {
        let path = entry?.path();
        if path.is_dir() {
            collect_files(&path, output)?;
        } else {
            output.push(path);
        }
    }
    Ok(())
}

// ─── 镜像选择 ────────────────────────────────────────────────────────────────

/// 逐个探测镜像延迟并选出最快可用节点。
///
/// 排序规则：可用节点按延迟升序 → 被拒绝（403/404）→ 超时。
/// 全部不可用时仍返回第一个节点，让下载阶段给出更明确的失败原因。
fn select_mirror(
    context: &InstallContext,
    nodes: Vec<MirrorNode>,
    status_key: &'static str,
) -> Result<MirrorNode, InstallError> {
    let client = context.client(Duration::from_secs(5))?;
    let total = nodes.len();
    let mut scored = Vec::with_capacity(total);

    for (index, node) in nodes.into_iter().enumerate() {
        if context.is_cancelled() {
            return Err(InstallError::Cancelled);
        }
        context.status(status_key);
        let started = Instant::now();
        let latency = match client.head(&node.url).send() {
            Ok(response) if response.status().is_success() || response.status().is_redirection() => {
                Some(started.elapsed().as_millis() as u64)
            }
            _ => None,
        };
        scored.push((latency, index, node));
    }

    // 可用节点优先，其次保持原有顺序（官方源排在前面）。
    scored.sort_by_key(|(latency, index, _)| (latency.is_none(), *latency, *index));
    scored
        .into_iter()
        .next()
        .map(|(_, _, node)| node)
        .ok_or_else(|| InstallError::key("install.error.no_mirror"))
}

// ─── 收尾 ────────────────────────────────────────────────────────────────────

/// 安装收尾：版本号有效则上报成功，否则返回校验失败。
fn finish(
    context: &InstallContext,
    version: Option<String>,
    failure_key: &'static str,
    install_path: &Path,
) -> InstallResult {
    match version.filter(|value| !value.trim().is_empty()) {
        Some(version) => {
            context.progress(100.0);
            context.version(&version);
            context.log(crate::lang::tf(
                "install.log.installed_to",
                &[("path", &install_path.display().to_string())],
            ));
            context.done();
            Ok(())
        }
        None => Err(InstallError::key(failure_key)),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn proxy_url_gains_scheme_when_missing() {
        assert_eq!(
            normalize_proxy_url("127.0.0.1:7890"),
            Some("http://127.0.0.1:7890".to_owned())
        );
        assert_eq!(
            normalize_proxy_url("http://127.0.0.1:7890"),
            Some("http://127.0.0.1:7890".to_owned())
        );
    }

    #[test]
    fn proxy_url_rejects_blank_and_unknown_scheme() {
        assert_eq!(normalize_proxy_url("   "), None);
        assert_eq!(normalize_proxy_url("socks5://127.0.0.1:1080"), None);
    }

    /// 关闭代理时不应解析出任何代理地址，避免误用系统代理。
    #[test]
    fn disabled_proxy_resolves_to_none() {
        let options = InstallOptions {
            npm_registry: String::new(),
            proxy_mode: "none".to_owned(),
            proxy_host: "127.0.0.1:7890".to_owned(),
        };
        assert_eq!(resolve_proxy_url(&options).unwrap(), None);
    }

    #[test]
    fn mirror_lists_are_non_empty_and_https() {
        for node in git_mirrors().into_iter().chain(node_mirrors()) {
            assert!(node.url.starts_with("https://"), "镜像必须使用 HTTPS");
        }
        assert!(CADDY_SOURCE.starts_with("https://"));
    }

    #[test]
    fn webview2_fallback_url_matches_fallback_version() {
        assert!(WEBVIEW2_FALLBACK_URL.contains(WEBVIEW2_FALLBACK_VERSION));
        assert!(WEBVIEW2_FALLBACK_URL.ends_with(".cab"));
    }
}
