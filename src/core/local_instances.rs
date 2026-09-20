//! 本地实例的身份识别与原子存储；不在这里操作界面或启动安装。

pub(crate) mod dependencies;
pub(crate) mod find_scan;
pub(crate) mod scan;

use std::fs;
use std::io::{self, Write};
use std::path::{Component, Path, PathBuf};

use serde::{Deserialize, Serialize};

/// 无法从 `package.json` 解析出版本号时使用的数据哨兵。
///
/// 这是**数据**而不是界面文案：展示层比较该常量后改用 `versions.unknown_version`
/// 文案键渲染，避免把显示文本写进数据层。
pub const UNKNOWN_VERSION: &str = "local.unknown_version";

/// 运行依赖必须经过实际检测，不能从 node_modules 是否存在推断完整性。
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub enum DependencyStatus {
    #[default]
    Checking,
    Ready,
    Incomplete,
    Failed(String),
    Installing,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LocalInstance {
    pub version: String,
    pub path: String,
    pub dependencies: DependencyStatus,
    /// 后台读取的文件系统身份（规范化绝对路径），UI 去重不再同步访问磁盘。
    pub identity: Option<PathBuf>,
}

/// 后台任务使用错误类别，不以界面语言判断取消或权限错误。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LocalErrorKind {
    Service,
    Io,
    PermissionDenied,
    /// 本地实例依赖检查无法找到 Node.js 或 npm，需要引导用户安装运行环境。
    MissingNodeJs,
    Cancelled,
    InvalidInstance,
    OnlineInstance,
}

/// 可翻译的标题、原始诊断及目标路径分开保存。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LocalError {
    pub message: &'static str,
    pub detail: String,
    pub kind: LocalErrorKind,
    pub path: Option<PathBuf>,
}

impl LocalError {
    pub fn new(message: &'static str, detail: impl ToString) -> Self {
        Self {
            message,
            detail: detail.to_string(),
            kind: LocalErrorKind::Service,
            path: None,
        }
    }

    pub fn with_kind(mut self, kind: LocalErrorKind) -> Self {
        self.kind = kind;
        self
    }

    pub fn cancelled() -> Self {
        Self::new("local.scan.cancelled", "").with_kind(LocalErrorKind::Cancelled)
    }

    pub fn io(message: &'static str, path: &Path, error: io::Error) -> Self {
        Self {
            message,
            detail: format!("{}: {error}", path.display()),
            kind: if error.kind() == io::ErrorKind::PermissionDenied {
                LocalErrorKind::PermissionDenied
            } else {
                LocalErrorKind::Io
            },
            path: Some(path.to_owned()),
        }
    }
}

pub fn online_dir() -> PathBuf {
    crate::core::network::sillytavern_install_dir()
}

pub fn store_path() -> PathBuf {
    crate::utils::app_paths().instances_file()
}

/// 保留无法访问的路径用于展示，但不靠字符串小写化合并大小写敏感卷上的目录。
pub fn normalized_path(path: &Path) -> PathBuf {
    let expanded = if let Ok(rest) = path.strip_prefix("~") {
        std::env::var_os("USERPROFILE")
            .map(PathBuf::from)
            .unwrap_or_default()
            .join(rest)
    } else {
        path.to_owned()
    };
    if let Ok(canonical) = fs::canonicalize(&expanded) {
        return canonical;
    }
    let mut result = PathBuf::new();
    for part in expanded.components() {
        match part {
            Component::CurDir => {}
            Component::ParentDir => {
                result.pop();
            }
            _ => result.push(part.as_os_str()),
        }
    }
    result
}

/// 把路径转成适合展示与存入配置的普通形式。
///
/// `fs::canonicalize` 在 Windows 上返回的是**扩展长度路径**（verbatim path），
/// 形如 `\\?\D:\SillyTavern\SillyTavern-1.18.0`。那个前缀是给 Win32 API 用的
/// 转义标记，让系统跳过路径规范化（`MAX_PATH` 限制、`.`/`..` 解析、保留名检查），
/// 对用户没有任何意义，直接显示出来既难看又容易被误解成路径本身的一部分。
///
/// 这里只剥掉前缀，**不做**任何进一步的路径处理：
///
/// * 反斜杠分隔符保留 —— Windows 上的用户认的就是 `D:\a\b` 这种写法。
/// * 不解析符号链接、不转换大小写 —— 那会改变路径指向，展示层不该做这种决定，
///   真正的身份比较交给 [`directory_identity`]。
///
/// 只去掉 `\\?\` 与 `\\?\UNC\` 两种前缀；其余原样返回，因此对非 Windows 路径
/// 或已经处理过的路径都是幂等的。
pub fn display_path(path: &Path) -> String {
    let text = path.to_string_lossy();
    // `\\?\UNC\server\share` 要还原成 `\\server\share`，否则 UNC 路径会失真。
    const VERBATIM_UNC: &str = r"\\?\UNC\";
    const VERBATIM: &str = r"\\?\";
    if let Some(rest) = text.strip_prefix(VERBATIM_UNC) {
        return format!(r"\\{rest}");
    }
    if let Some(rest) = text.strip_prefix(VERBATIM) {
        return rest.to_owned();
    }
    text.into_owned()
}

/// 目录身份：用于识别「同一个目录的不同路径写法」。
///
/// macOS 时代的实现读取 inode 与设备号；Windows 没有这两项，
/// 改用规范化后的绝对路径作为身份，同样能识别 8.3 短名、相对路径、
/// 大小写差异等别名写法。
pub fn directory_identity(path: &Path) -> io::Result<PathBuf> {
    let metadata = fs::metadata(path)?;
    if !metadata.is_dir() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "not a directory",
        ));
    }
    // `canonicalize` 会解析符号链接与短名，并统一大小写。
    fs::canonicalize(path)
}

pub fn same_directory(left: &Path, right: &Path) -> bool {
    match (directory_identity(left), directory_identity(right)) {
        (Ok(left), Ok(right)) => left == right,
        _ => normalized_path(left) == normalized_path(right),
    }
}

/// 逐层比较身份，覆盖 /Users 与 /System/Volumes/Data/Users 等路径别名。
pub fn inside_online(path: &Path, online: &Path) -> bool {
    let path = normalized_path(path);
    path.ancestors()
        .any(|ancestor| same_directory(ancestor, online))
}

/// 实例本体是否已经从磁盘上消失：目录或 `package.json` 明确「找不到」。
///
/// 只有 `NotFound` 才判定为已删除；权限不足、IO 错误一律返回 false，
/// 避免把暂时读不到的实例从列表里误删。`package.json` 被单独删除时目录里已经没有
/// 酒馆实例，同样视为失效。
pub fn instance_missing(root: &Path) -> bool {
    not_found(&root.join("package.json")) || not_found(root)
}

fn not_found(path: &Path) -> bool {
    matches!(fs::metadata(path), Err(error) if error.kind() == io::ErrorKind::NotFound)
}

pub fn inspect_package(package: &Path, online: &Path) -> Result<LocalInstance, LocalError> {
    let invalid = || {
        LocalError::new("local.instance.invalid", "")
            .with_kind(LocalErrorKind::InvalidInstance)
    };
    if package.file_name().and_then(|name| name.to_str()) != Some("package.json") {
        return Err(invalid());
    }
    let original_root = package.parent().ok_or_else(invalid)?;
    // 文件本身也可能是符号链接，必须以真实清单所在目录识别实例。
    let resolved = fs::canonicalize(package).unwrap_or_else(|_| package.to_owned());
    let root = resolved.parent().ok_or_else(invalid)?;
    // 优先识别在线目录，即使其 package.json 正处于下载或更新阶段。
    if inside_online(root, online) || inside_online(original_root, online) {
        return Err(
            LocalError::new("local.instance.online_rejected", "").with_kind(LocalErrorKind::OnlineInstance)
        );
    }
    let bytes =
        fs::read(package).map_err(|error| LocalError::io("local.instance.read_failed", package, error))?;
    let document: serde_json::Value = serde_json::from_slice(&bytes).map_err(|_| invalid())?;
    if !document
        .get("name")
        .and_then(|value| value.as_str())
        .is_some_and(|name| name.eq_ignore_ascii_case("sillytavern"))
    {
        return Err(invalid());
    }
    Ok(LocalInstance {
        version: document
            .get("version")
            .and_then(|v| v.as_str())
            .filter(|v| !v.trim().is_empty())
            .unwrap_or(UNKNOWN_VERSION)
            .to_owned(),
        path: display_path(&normalized_path(root)),
        dependencies: DependencyStatus::Checking,
        identity: directory_identity(root).ok(),
    })
}

/// 旧版字段允许反序列化；依赖检测结果不写入磁盘，避免重启后误信缓存。
#[derive(Debug, Serialize, Deserialize)]
struct StoredInstance {
    #[serde(default)]
    version: String,
    path: String,
    #[serde(default, skip_serializing)]
    is_online: bool,
}

pub fn load(path: &Path, online: &Path) -> Result<Vec<LocalInstance>, LocalError> {
    let bytes = match fs::read(path) {
        Ok(bytes) => bytes,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(Vec::new()),
        Err(error) => return Err(LocalError::new("local.list.load_failed", error)),
    };
    let stored: Vec<StoredInstance> = serde_json::from_slice(&bytes)
        .map_err(|error| LocalError::new("local.list.corrupted", error))?;
    let mut instances: Vec<LocalInstance> = Vec::new();
    for item in stored {
        if item.path.trim().is_empty() || !normalized_path(Path::new(&item.path)).is_absolute() {
            return Err(LocalError::new(
                "local.list.corrupted",
                &item.path,
            ));
        }
        let path = normalized_path(Path::new(&item.path));
        if item.is_online
            || inside_online(&path, online)
            || instances
                .iter()
                .any(|other| same_directory(Path::new(&other.path), &path))
        {
            continue;
        }
        let instance = match inspect_package(&path.join("package.json"), online) {
            Ok(instance) => instance,
            Err(error) => LocalInstance {
                version: if item.version.is_empty() {
                    UNKNOWN_VERSION.into()
                } else {
                    item.version
                },
                path: display_path(&path),
                dependencies: DependencyStatus::Failed(error.detail),
                identity: directory_identity(&path).ok(),
            },
        };
        instances.push(instance);
    }
    Ok(instances)
}

pub fn save(path: &Path, instances: &[LocalInstance]) -> io::Result<()> {
    let parent = path
        .parent()
        .ok_or_else(|| io::Error::other("missing parent directory"))?;
    fs::create_dir_all(parent)?;
    let stored: Vec<StoredInstance> = instances
        .iter()
        .map(|item| StoredInstance {
            version: item.version.clone(),
            path: item.path.clone(),
            is_online: false,
        })
        .collect();
    let bytes = serde_json::to_vec_pretty(&stored).map_err(io::Error::other)?;
    // 使用同目录临时文件加 rename，避免异常退出留下半份 JSON。
    let nonce = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_err(io::Error::other)?
        .as_nanos();
    let temp = parent.join(format!(
        ".local_instances.{}.{nonce}.tmp",
        std::process::id()
    ));
    let mut created = false;
    let result = (|| {
        let mut file = fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temp)?;
        created = true;
        file.write_all(&bytes)?;
        file.sync_all()?;
        fs::rename(&temp, path)
    })();
    if result.is_err() && created {
        // 只清理本轮成功创建的临时文件。
        // 同一进程的存储调用由应用层串行化。
        let _ = fs::remove_file(&temp);
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicU64, Ordering};
    static NEXT: AtomicU64 = AtomicU64::new(0);
    pub(super) struct Fixture(pub PathBuf);
    impl Fixture {
        pub(super) fn new() -> Self {
            let path = std::env::temp_dir().join(format!(
                "astra-local-test-{}-{}",
                std::process::id(),
                NEXT.fetch_add(1, Ordering::Relaxed)
            ));
            fs::create_dir_all(&path).unwrap();
            Self(path)
        }
        pub(super) fn package(&self, value: &str) -> PathBuf {
            let path = self.0.join("package.json");
            fs::write(&path, value).unwrap();
            path
        }
    }
    impl Drop for Fixture {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }
    #[test]
    fn display_path_strips_windows_verbatim_prefix() {
        // `fs::canonicalize` 在 Windows 上产出的就是这种带前缀的形式，
        // 这个前缀不该出现在界面或 instances.json 里。
        assert_eq!(
            display_path(Path::new(r"\\?\D:\SillyTavern\SillyTavern-1.18.0")),
            r"D:\SillyTavern\SillyTavern-1.18.0"
        );
        // 盘符大小写、中文目录名都要原样保留。
        assert_eq!(
            display_path(Path::new(r"\\?\C:\用户\文档")),
            r"C:\用户\文档"
        );
        // 前缀大小写不敏感：系统在部分路径上会返回 `\\?\` 之外的大小写变体。
        assert_eq!(
            display_path(Path::new(r"\\?\c:\Temp\build")),
            r"c:\Temp\build"
        );
    }

    #[test]
    fn display_path_restores_unc_form() {
        // UNC 的 verbatim 形式多了一层 `UNC\`，必须还原成标准 `\\server\share`，
        // 否则去掉前缀会得到一个不存在的相对路径。
        assert_eq!(
            display_path(Path::new(r"\\?\UNC\server\share\folder")),
            r"\\server\share\folder"
        );
    }

    #[test]
    fn display_path_is_idempotent_for_normal_paths() {
        // 已经是普通形式的路径必须原样返回，否则反复规范化会损坏路径。
        // 尤其不能把 UNC 的 `\\` 误当成 verbatim 前缀的起始。
        for original in [
            r"D:\SillyTavern\SillyTavern-1.18.0",
            r"\\server\share\folder",
            r"C:\",
        ] {
            assert_eq!(display_path(Path::new(original)), original);
            // 再跑一次也必须不变：save/load 会反复经过这条路径。
            let once = display_path(Path::new(original));
            assert_eq!(display_path(Path::new(&once)), once);
        }
    }

    /// 回归测试：进入实例列表的路径不能带 `\\?\` 前缀。
    ///
    /// 旧数据（已存盘的 `instances.json`、旧版写入的路径）不需要迁移脚本：
    /// [`load`] 会把每条记录重新跑一遍 [`normalized_path`] + [`display_path`]，
    /// 前缀在下次保存时自然消失。这条测试守住那个前提。
    #[test]
    fn loading_normalizes_legacy_verbatim_paths() {
        let fixture = Fixture::new();
        fixture.package(r#"{"name":"sillytavern","version":"1.18.0"}"#);
        let store = fixture.0.join("instances.json");

        // 模拟旧版写下的带前缀路径。
        let verbatim = format!(r"\\?\{}", fixture.0.display());
        fs::write(
            &store,
            serde_json::to_vec(&serde_json::json!([{ "path": verbatim, "version": "1.18.0" }]))
                .unwrap(),
        )
        .unwrap();

        let online = fixture.0.join("online");
        let instances = load(&store, &online).unwrap();
        let instance = instances.first().expect("旧记录应被载入");
        assert!(
            !instance.path.contains(r"\\?\"),
            "载入后的路径仍带 verbatim 前缀：{}",
            instance.path
        );
        // 路径本身必须仍然可用：去掉前缀不能把目录指错。
        assert!(Path::new(&instance.path).is_dir(), "规范化后的路径应仍指向原目录");
        assert!(same_directory(Path::new(&instance.path), &fixture.0));
    }

    #[test]
    fn package_validation_and_online_aliases() {
        let fixture = Fixture::new();
        let online = fixture.0.join("online");
        for name in ["sillytavern", "SillyTavern", "SILLYTAVERN"] {
            let path = fixture.package(&format!(r#"{{"name":"{name}"}}"#));
            assert_eq!(inspect_package(&path, &online).unwrap().version, UNKNOWN_VERSION);
        }
        for content in [
            "{}",
            "{",
            r#"{"name":42}"#,
            r#"{"name":" sillytavern"}"#,
            r#"{"name":"other"}"#,
        ] {
            assert!(inspect_package(&fixture.package(content), &online).is_err());
        }
        let path = fixture.package(r#"{"name":"sillytavern"}"#);
        assert!(inspect_package(&path, &fixture.0).is_err());
        let alias = fixture.0.join("alias");
        // 无管理员权限 / 未开启开发者模式时无法创建符号链接，跳过该断言。
        if crate::utils::try_symlink_dir(&fixture.0, &alias) {
            assert!(same_directory(&alias, &fixture.0));
            assert!(inspect_package(&alias.join("package.json"), &fixture.0).is_err());
        }
        assert!(inspect_package(&fixture.0.join("other.json"), &online).is_err());
    }
    #[test]
    fn legacy_store_retains_unavailable_and_deduplicates() {
        let fixture = Fixture::new();
        fixture.package(r#"{"name":"sillytavern","version":"1"}"#);
        let path = fixture.0.join("instances.json");
        fs::write(
            &path,
            serde_json::to_vec(&serde_json::json!([
                {"path":fixture.0,"version":"0","is_current":true},
                {"path":fixture.0,"version":"0"},
                {"path":fixture.0.join("missing"),"version":"2"}
            ]))
            .unwrap(),
        )
        .unwrap();
        let instances = load(&path, &fixture.0.join("online")).unwrap();
        assert_eq!(instances.len(), 2);
        assert_eq!(instances[0].version, "1");
        assert!(matches!(
            instances[1].dependencies,
            DependencyStatus::Failed(_)
        ));
        save(&path, &instances).unwrap();
        assert_eq!(load(&path, &fixture.0.join("online")).unwrap().len(), 2);
        fs::write(&path, "{").unwrap();
        assert!(load(&path, &fixture.0.join("online")).is_err());
    }
    #[test]
    fn missing_instance_is_detected_only_when_files_are_gone() {
        let fixture = Fixture::new();
        let root = fixture.0.join("instance");
        fs::create_dir_all(&root).unwrap();
        fs::write(root.join("package.json"), r#"{"name":"sillytavern"}"#).unwrap();
        assert!(!instance_missing(&root));
        fs::remove_file(root.join("package.json")).unwrap();
        assert!(instance_missing(&root), "清单被删除后应判定为失效实例");
        fs::write(root.join("package.json"), r#"{"name":"sillytavern"}"#).unwrap();
        fs::remove_dir_all(&root).unwrap();
        assert!(instance_missing(&root), "目录被删除后应判定为失效实例");
    }

    #[test]
    fn failed_atomic_save_preserves_existing_file() {
        let fixture = Fixture::new();
        let path = fixture.0.join("existing");
        fs::write(&path, "original").unwrap();
        assert!(save(&path.join("instances.json"), &[]).is_err());
        assert_eq!(fs::read_to_string(path).unwrap(), "original");
    }

    #[test]
    fn manifest_symlink_to_online_is_rejected() {
        let fixture = Fixture::new();
        let online = fixture.0.join("online");
        let local = fixture.0.join("local");
        fs::create_dir(&online).unwrap();
        fs::create_dir(&local).unwrap();
        fs::write(online.join("package.json"), r#"{"name":"sillytavern"}"#).unwrap();
        // 无权限创建符号链接时跳过：该用例校验的是「指向在线实例的清单被拒绝」。
        if !crate::utils::try_symlink_file(&online.join("package.json"), &local.join("package.json"))
        {
            return;
        }
        assert_eq!(
            inspect_package(&local.join("package.json"), &online)
                .unwrap_err()
                .message,
            "local.instance.online_rejected"
        );
    }
    #[test]
    fn permission_errors_carry_machine_readable_kind_and_path() {
        for code in [1, 13] {
            let error = LocalError::io(
                "任意语言的错误标题",
                Path::new("/fixture/denied"),
                io::Error::from_raw_os_error(code),
            );
            assert_eq!(error.kind, LocalErrorKind::PermissionDenied);
            assert_eq!(error.path, Some(PathBuf::from("/fixture/denied")));
        }
        assert_eq!(LocalError::cancelled().kind, LocalErrorKind::Cancelled);
    }
}
