/// 自动更新模块 — 从 GitHub Releases 检测并安装更新。
///
/// 支持多代理 URL 按顺序回退：
///   1. https://gh-proxy.org/
///   2. https://ghfast.top/
///   3. https://gt.astrabrew.cn/
///   4. https://github.com/  （直连兜底）
use std::sync::mpsc;

// ─── 代理 URL 列表 ────────────────────────────────────────────────────────────

/// 代理基础 URL（按优先顺序）
const PROXY_BASES: &[&str] = &[
    "https://gh-proxy.org/",
    "https://ghfast.top/",
    "https://gt.astrabrew.cn/",
];

/// 直连兜底
const DIRECT_BASE: &str = "https://github.com/";

/// GitHub Releases 仓库
const REPO: &str = "AstraBrew-Labs/AstraBrew-Launcher-Mac";

/// 更新签名公钥（与打包时的私钥配对，见 keys/update_key.pem）
const PUBKEY: &str = "dW50cnVzdGVkIGNvbW1lbnQ6IG1pbmlzaWduIHB1YmxpYyBrZXk6IEFGRjBFQkJCNzUxQjZGNTUKUldSVmJ4dDF1K3Z3ci9oWENPcHUzckVQL2N1OWFqQmc0QUIydzJvUzVadm5SN1NzaGhVdVdpcTEK";

// ─── 数据结构 ─────────────────────────────────────────────────────────────────

/// 更新检测/下载状态
#[derive(Debug, Clone)]
pub enum UpdateStatus {
    /// 正在检查
    Checking,
    /// 已是最新版本
    UpToDate,
    /// 发现新版本（版本号, 更新说明, 可用端点）
    UpdateAvailable {
        version: String,
        notes: Option<String>,
        endpoint: String,
    },
    /// 正在下载安装
    Downloading,
    /// 安装完成（需重启）
    Installed,
    /// 出错
    Error(String),
}

// ─── 公开 API ─────────────────────────────────────────────────────────────────

/// 启动后台更新检测（启动时自动静默检测，不弹窗）。
#[allow(dead_code)]
pub fn start_check() -> mpsc::Receiver<UpdateStatus> {
    let (tx, rx) = mpsc::channel();
    std::thread::spawn(move || {
        let _ = tx.send(UpdateStatus::Checking);
        match try_check() {
            Ok(Some((_version, _notes, _endpoint))) => {
                // 自动检测不弹窗，静默尝试下载安装
                let _ = tx.send(UpdateStatus::UpToDate);
            }
            Ok(None) => {
                let _ = tx.send(UpdateStatus::UpToDate);
            }
            Err(e) => {
                let _ = tx.send(UpdateStatus::Error(e));
            }
        }
    });
    rx
}

/// 启动手动更新检测（用户点击"检查更新"按钮）。
///
/// 找到更新后会发送 `UpdateAvailable`（含 endpoint），由 UI 展示确认弹窗。
pub fn check_update_manual() -> mpsc::Receiver<UpdateStatus> {
    let (tx, rx) = mpsc::channel();
    std::thread::spawn(move || {
        let _ = tx.send(UpdateStatus::Checking);
        match try_check() {
            Ok(Some((version, notes, endpoint))) => {
                let _ = tx.send(UpdateStatus::UpdateAvailable {
                    version,
                    notes,
                    endpoint,
                });
            }
            Ok(None) => {
                let _ = tx.send(UpdateStatus::UpToDate);
            }
            Err(e) => {
                let _ = tx.send(UpdateStatus::Error(e));
            }
        }
    });
    rx
}

/// 执行下载安装（用户确认后调用）。
///
/// `endpoint` 是之前检测时找到的可用端点 URL。
pub fn do_install(endpoint: String) -> mpsc::Receiver<UpdateStatus> {
    let (tx, rx) = mpsc::channel();
    std::thread::spawn(move || {
        let _ = tx.send(UpdateStatus::Downloading);

        let version = env!("CARGO_PKG_VERSION")
            .parse::<cargo_packager_updater::semver::Version>()
            .expect("invalid CARGO_PKG_VERSION");

        let config = cargo_packager_updater::Config {
            endpoints: vec![endpoint.parse().expect("invalid endpoint URL")],
            pubkey: PUBKEY.into(),
            ..Default::default()
        };

        match cargo_packager_updater::check_update(version, config) {
            Ok(Some(update)) => match update.download_and_install() {
                Ok(()) => {
                    let _ = tx.send(UpdateStatus::Installed);
                }
                Err(e) => {
                    let _ = tx.send(UpdateStatus::Error(format!("下载/安装失败: {e}")));
                }
            },
            Ok(None) => {
                let _ = tx.send(UpdateStatus::Error("更新信息已过期，请重新检查".into()));
            }
            Err(e) => {
                let _ = tx.send(UpdateStatus::Error(format!("下载/安装失败: {e}")));
            }
        }
    });
    rx
}

// ─── 内部实现 ─────────────────────────────────────────────────────────────────

/// 按代理优先顺序尝试获取更新信息。
///
/// 返回 `Ok(Some((version, notes, endpoint)))` 表示找到更新，
/// `Ok(None)` 表示已是最新版本，
/// `Err(...)` 表示所有端点均不可用。
fn try_check() -> Result<Option<(String, Option<String>, String)>, String> {
    let version = env!("CARGO_PKG_VERSION")
        .parse::<cargo_packager_updater::semver::Version>()
        .expect("invalid CARGO_PKG_VERSION");

    // 1. 先试代理
    for base in PROXY_BASES {
        match try_check_at(base, &version) {
            CheckResult::Update(v, n) => return Ok(Some((v, n, build_endpoint(base)))),
            CheckResult::UpToDate => return Ok(None),
            CheckResult::Unreachable => continue,
        }
    }

    // 2. 兜底直连
    match try_check_at(DIRECT_BASE, &version) {
        CheckResult::Update(v, n) => Ok(Some((v, n, build_endpoint(DIRECT_BASE)))),
        CheckResult::UpToDate => Ok(None),
        CheckResult::Unreachable => Err("所有更新源均不可用，请检查网络连接".into()),
    }
}

/// 单个代理源的检测结果
enum CheckResult {
    Update(String, Option<String>),
    UpToDate,
    Unreachable,
}

/// 通过单个 endpoint 检测更新。
///
/// 构造 `{base}https://github.com/{REPO}/releases/latest/download/latest.json`
/// 作为配置端点，使用 cargo-packager-updater 验证。
fn try_check_at(base: &str, version: &cargo_packager_updater::semver::Version) -> CheckResult {
    let endpoint = build_endpoint(base);
    let config = cargo_packager_updater::Config {
        endpoints: vec![endpoint.parse().expect("invalid endpoint URL")],
        pubkey: PUBKEY.into(),
        ..Default::default()
    };

    match cargo_packager_updater::check_update(version.clone(), config) {
        Ok(Some(update)) => {
            CheckResult::Update(update.version.to_string(), update.body.clone())
        }
        Ok(None) => CheckResult::UpToDate,
        Err(_) => CheckResult::Unreachable,
    }
}

/// 构造 endpoint URL: {base}https://github.com/{repo}/releases/latest/download/latest.json
fn build_endpoint(base: &str) -> String {
    let base = base.trim_end_matches('/');
    format!("{base}/https://github.com/{REPO}/releases/latest/download/latest.json")
}
