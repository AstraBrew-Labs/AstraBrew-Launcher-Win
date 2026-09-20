//! 酒馆 YAML 无损读写服务。文档节点只在执行服务的线程内使用，不跨线程共享。

pub(crate) mod schema;
use serde_json::Value;
use std::collections::{BTreeMap, HashSet};
use std::fs;
use std::io::{self, Read, Write};
use std::path::{Path, PathBuf};
use std::str::FromStr;
use std::sync::atomic::{AtomicU64, Ordering};
use yaml_edit::{Mapping, YamlFile, YamlNode};

pub type Values = BTreeMap<String, Value>;
/// 酒馆服务模式决定由启动器托管的白名单地址集合。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum WhitelistServiceMode {
    Lan,
    Internet,
}

/// 当前配置目标的白名单策略签名。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct WhitelistPolicy {
    pub server_enabled: bool,
    pub service_mode: WhitelistServiceMode,
}

const ALL_RESERVED_WHITELIST_IPS: &[&str] = &[
    "::1",
    "127.0.0.1",
    "10.0.0.0/8",
    "172.16.0.0/12",
    "192.168.0.0/16",
    "0.0.0.0/0",
    "::/0",
];
const LOOPBACK_WHITELIST_IPS: &[&str] = &["::1", "127.0.0.1"];
const LAN_WHITELIST_IPS: &[&str] = &[
    "::1",
    "127.0.0.1",
    "10.0.0.0/8",
    "172.16.0.0/12",
    "192.168.0.0/16",
];
const INTERNET_WHITELIST_IPS: &[&str] = &["::1", "127.0.0.1", "0.0.0.0/0", "::/0"];

/// 返回跨全部服务模式由启动器保留的地址。
pub fn all_reserved_whitelist_ips() -> &'static [&'static str] {
    ALL_RESERVED_WHITELIST_IPS
}

/// 返回当前服务模式必须存在且不可删除的地址。
pub fn fixed_whitelist(policy: WhitelistPolicy) -> Vec<String> {
    let values = if !policy.server_enabled {
        LOOPBACK_WHITELIST_IPS
    } else {
        match policy.service_mode {
            WhitelistServiceMode::Lan => LAN_WHITELIST_IPS,
            WhitelistServiceMode::Internet => INTERNET_WHITELIST_IPS,
        }
    };
    values.iter().map(|value| (*value).to_owned()).collect()
}

/// 系统保留地址不能作为普通用户条目添加。
pub fn is_reserved_whitelist_ip(value: &str) -> bool {
    let value = value.trim();
    all_reserved_whitelist_ips().contains(&value)
}

/// 旧版语义：移除旧模式保留段、全表去重、保留用户地址并补齐当前固定地址。
pub fn normalize_whitelist(values: &[String], policy: WhitelistPolicy) -> Vec<String> {
    let fixed = fixed_whitelist(policy);
    let mut normalized = Vec::new();
    let mut seen = HashSet::new();
    for value in values {
        let value = value.trim();
        if value.is_empty() {
            // 输入中的空行由界面校验处理，系统修复不能擅自吞掉用户草稿。
            if seen.insert(String::new()) {
                normalized.push(String::new());
            }
            continue;
        }
        if is_reserved_whitelist_ip(value) && !fixed.iter().any(|item| item == value) {
            continue;
        }
        if seen.insert(value.to_owned()) {
            normalized.push(value.to_owned());
        }
    }
    for value in fixed {
        if seen.insert(value.clone()) {
            normalized.push(value);
        }
    }
    normalized
}

/// 三方合并白名单的用户地址，同时让系统保留段始终服从当前服务模式。
pub fn merge_whitelist(
    base: &[String],
    local: &[String],
    disk: &[String],
    policy: WhitelistPolicy,
) -> Vec<String> {
    let custom = |values: &[String]| {
        values
            .iter()
            .map(|value| value.trim())
            .filter(|value| !value.is_empty() && !is_reserved_whitelist_ip(value))
            .map(str::to_owned)
            .collect::<Vec<_>>()
    };
    let keep_blank_draft = local.iter().any(|value| value.trim().is_empty());
    let base_custom = custom(base);
    let local_custom = custom(local);
    let disk_custom = custom(disk);
    let base_set = base_custom.iter().cloned().collect::<HashSet<_>>();
    let disk_set = disk_custom.iter().cloned().collect::<HashSet<_>>();
    let mut merged = Vec::new();
    let mut seen = HashSet::new();

    // 本地仍保留的基线条目若被文件明确删除，则采用文件删除；本地新增和修改保留。
    for value in local_custom {
        if base_set.contains(&value) && !disk_set.contains(&value) {
            continue;
        }
        if seen.insert(value.clone()) {
            merged.push(value);
        }
    }
    // 外部新增的用户地址追加到列表，不覆盖界面里的用户修改。
    for value in disk_custom {
        if !base_set.contains(&value) && seen.insert(value.clone()) {
            merged.push(value);
        }
    }
    if keep_blank_draft {
        merged.push(String::new());
    }
    normalize_whitelist(&merged, policy)
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Context {
    pub key: String,
    pub path: PathBuf,
    pub instance: PathBuf,
}
impl Context {
    /// 全局路径保持用户设置，独立路径只绑定当前真正选中的实例。
    pub fn resolve(
        instance: Option<&str>,
        source: &str,
        global: bool,
        global_path: &str,
    ) -> Option<Self> {
        let instance = instance.filter(|path| !path.is_empty()).map(expand_home)?;
        let path = if global {
            if global_path.trim().is_empty() {
                app_root().join("data/config.yaml")
            } else {
                expand_home(global_path).join("config.yaml")
            }
        } else {
            instance.join("config.yaml")
        };
        Some(Self {
            key: format!(
                "{global}|{source}|{}|{}",
                instance.display(),
                path.display()
            ),
            path,
            instance,
        })
    }
}
/// 展开用户路径（`~/`、`%APPDATA%` 等），实现统一在 [`crate::utils::expand_user_path`]。
pub fn expand_home(path: &str) -> PathBuf {
    crate::utils::expand_user_path(path)
}
fn app_root() -> PathBuf {
    crate::core::network::sillytavern_install_dir()
        .parent()
        .unwrap_or(Path::new("."))
        .to_owned()
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ErrorKind {
    Missing,
    Invalid,
    Io,
    Changed,
    Template,
}
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ConfigError {
    pub kind: ErrorKind,
    pub message: &'static str,
    pub detail: String,
}
impl ConfigError {
    pub fn new(kind: ErrorKind, message: &'static str, detail: impl ToString) -> Self {
        Self {
            kind,
            message,
            detail: detail.to_string(),
        }
    }
    fn io(path: &Path, error: io::Error) -> Self {
        Self::new(
            if error.kind() == io::ErrorKind::NotFound {
                ErrorKind::Missing
            } else {
                ErrorKind::Io
            },
            "tavern.error.config_io",
            format!("{}: {error}", path.display()),
        )
    }
}

#[derive(Debug, Clone)]
pub struct Snapshot {
    pub text: String,
    pub physical_path: PathBuf,
    pub values: Values,
    pub raw: BTreeMap<String, Option<Value>>,
}

fn parse(text: &str) -> Result<(YamlFile, Mapping), ConfigError> {
    let file = YamlFile::from_str(text).map_err(|_| {
        ConfigError::new(ErrorKind::Invalid, "tavern.error.yaml_invalid", "")
    })?;
    let docs: Vec<_> = file.documents().collect();
    if docs.len() != 1 {
        return Err(ConfigError::new(
            ErrorKind::Invalid,
            "tavern.error.not_mapping",
            "",
        ));
    }
    let root = docs[0].as_mapping().ok_or_else(|| {
        ConfigError::new(ErrorKind::Invalid, "tavern.error.not_mapping", "")
    })?;
    validate_keys(&YamlNode::Mapping(root.clone()))?;
    Ok((file, root))
}
fn validate_keys(node: &YamlNode) -> Result<(), ConfigError> {
    if let Some(map) = node.as_mapping() {
        let mut seen = HashSet::new();
        for (key, value) in map.iter() {
            let key = key
                .as_scalar()
                .ok_or_else(|| {
                    ConfigError::new(ErrorKind::Invalid, "tavern.error.key_must_be_text", "")
                })?
                .as_string();
            if !seen.insert(key) {
                return Err(ConfigError::new(
                    ErrorKind::Invalid,
                    "tavern.error.key_must_be_text",
                    "",
                ));
            }
            validate_keys(&value)?;
        }
    } else if let Some(sequence) = node.as_sequence() {
        for value in sequence.values() {
            validate_keys(&value)?;
        }
    }
    Ok(())
}

fn raw_value(node: &YamlNode) -> Result<Value, ConfigError> {
    let error = || ConfigError::new(ErrorKind::Invalid, "tavern.error.unsupported_yaml_type", "");
    match node {
        YamlNode::Scalar(scalar) => Ok(if scalar.is_null() {
            Value::Null
        } else if let Some(value) = scalar.as_i64() {
            value.into()
        } else if let Some(value) = scalar.as_bool() {
            value.into()
        } else {
            Value::String(scalar.as_string())
        }),
        YamlNode::Sequence(sequence) => sequence
            .values()
            .map(|item| raw_value(&item))
            .collect::<Result<Vec<_>, _>>()
            .map(Value::Array),
        YamlNode::TaggedNode(tag) if tag.tag().is_some_and(|tag| tag.contains("null")) => {
            Ok(Value::Null)
        }
        _ => Err(error()),
    }
}
fn get_at(root: &Mapping, path: &str) -> Result<Option<YamlNode>, ConfigError> {
    let mut current = YamlNode::Mapping(root.clone());
    for part in path.split('.') {
        let value = if let Some(map) = current.as_mapping() {
            map.get(part)
        } else if let Some(sequence) = current.as_sequence() {
            part.parse().ok().and_then(|index| sequence.get(index))
        } else {
            return Err(ConfigError::new(
                ErrorKind::Invalid,
                "tavern.error.parent_type_invalid",
                path,
            ));
        };
        let Some(value) = value else {
            return Ok(None);
        };
        current = value;
    }
    Ok(Some(current))
}

pub fn snapshot(
    text: String,
    physical_path: PathBuf,
    defaults: &Values,
) -> Result<Snapshot, ConfigError> {
    let (_, root) = parse(&text)?;
    let mut values = defaults.clone();
    let mut raw = BTreeMap::new();
    for field in schema::FIELDS {
        let value = get_at(&root, field.path)?
            .map(|node| raw_value(&node))
            .transpose()
            .map_err(|mut error| {
                error.detail = field.path.into();
                error
            })?;
        if let Some(value) = &value {
            let ui = schema::decode(field, value)
                .map_err(|message| ConfigError::new(ErrorKind::Invalid, message, field.path))?;
            values.insert(field.key.into(), ui);
        }
        raw.insert(field.key.into(), value);
    }
    Ok(Snapshot {
        text,
        physical_path,
        values,
        raw,
    })
}
pub fn load(context: &Context, defaults: &Values) -> Result<Snapshot, ConfigError> {
    let path =
        fs::canonicalize(&context.path).map_err(|error| ConfigError::io(&context.path, error))?;
    let text = fs::read_to_string(&path).map_err(|error| ConfigError::io(&path, error))?;
    snapshot(text, path, defaults)
}

fn value_node(value: &Value) -> Result<YamlNode, ConfigError> {
    let text = format!("value: {}\n", value);
    let (_, mapping) = parse(&text)?;
    mapping
        .get("value")
        .ok_or_else(|| ConfigError::new(ErrorKind::Invalid, "tavern.error.build_value_failed", ""))
}
fn set_at(
    root: &Mapping,
    field: &schema::Field,
    value: &Value,
    defaults: &Values,
) -> Result<(), ConfigError> {
    let parts: Vec<_> = field.path.split('.').collect();
    let mut current = YamlNode::Mapping(root.clone());
    for (i, part) in parts.iter().enumerate() {
        let last = i + 1 == parts.len();
        if let Some(mapping) = current.as_mapping() {
            if last {
                mapping.set(*part, value_node(value)?);
                return Ok(());
            }
            if mapping.get(*part).is_none() {
                if parts[i + 1].parse::<usize>().is_ok() {
                    let prefix = parts[..=i].join(".");
                    let mut values = Vec::new();
                    for index in 0..2 {
                        let sibling = schema::FIELDS
                            .iter()
                            .find(|f| f.path == format!("{prefix}.{index}"));
                        let value = sibling
                            .and_then(|f| {
                                defaults.get(f.key).and_then(|v| schema::encode(f, v).ok())
                            })
                            .unwrap_or(Value::Null);
                        values.push(value);
                    }
                    mapping.set(*part, value_node(&Value::Array(values))?);
                } else {
                    mapping.set(*part, Mapping::new());
                }
            }
            current = mapping.get(*part).ok_or_else(|| {
                ConfigError::new(ErrorKind::Invalid, "tavern.error.build_value_failed", field.path)
            })?;
        } else if let Some(sequence) = current.as_sequence() {
            let index: usize = part.parse().map_err(|_| {
                ConfigError::new(ErrorKind::Invalid, "tavern.error.parent_type_invalid", field.path)
            })?;
            if !last {
                return Err(ConfigError::new(
                    ErrorKind::Invalid,
                    "tavern.error.parent_type_invalid",
                    field.path,
                ));
            }
            while sequence.len() <= index {
                sequence.push(value_node(&Value::Null)?);
            }
            sequence.set(index, value_node(value)?);
            return Ok(());
        } else {
            return Err(ConfigError::new(
                ErrorKind::Invalid,
                "tavern.error.parent_type_invalid",
                field.path,
            ));
        }
    }
    Ok(())
}

#[derive(Debug, Clone)]
pub struct Patch {
    pub key: String,
    pub expected: Option<Value>,
    pub value: Value,
    pub revision: u64,
}
#[derive(Debug, Clone)]
pub struct SaveResult {
    pub snapshot: Snapshot,
    pub applied: Vec<(String, u64)>,
    pub conflicts: Vec<(String, u64)>,
}
/// 在最新磁盘文档上逐字段三方比较；冲突字段不写，其他有效字段继续保存。
pub fn save(
    context: &Context,
    base_path: &Path,
    patches: &[Patch],
    defaults: &Values,
) -> Result<SaveResult, ConfigError> {
    let disk = load(context, defaults)?;
    if disk.physical_path != base_path {
        return Err(ConfigError::new(
            ErrorKind::Changed,
            "tavern.error.target_changed",
            context.path.display(),
        ));
    }
    let (file, root) = parse(&disk.text)?;
    let mut applied = Vec::new();
    let mut conflicts = Vec::new();
    for patch in patches {
        let field = schema::field(&patch.key)
            .ok_or_else(|| ConfigError::new(ErrorKind::Invalid, "tavern.error.unknown_field", &patch.key))?;
        let current = disk.raw.get(&patch.key).cloned().flatten();
        if current != patch.expected && current.as_ref() != Some(&patch.value) {
            conflicts.push((patch.key.clone(), patch.revision));
            continue;
        }
        if current.as_ref() != Some(&patch.value) {
            set_at(&root, field, &patch.value, defaults)?;
        }
        applied.push((patch.key.clone(), patch.revision));
    }
    let text = file.to_string();
    let updated = snapshot(text.clone(), disk.physical_path.clone(), defaults)?;
    if text != disk.text {
        replace(context, &disk, &text)?;
    }
    Ok(SaveResult {
        snapshot: updated,
        applied,
        conflicts,
    })
}

static NONCE: AtomicU64 = AtomicU64::new(0);
fn temporary(path: &Path, suffix: &str) -> PathBuf {
    let time = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_nanos();
    path.with_file_name(format!(
        ".config.yaml.{suffix}.{}.{time}.{}",
        std::process::id(),
        NONCE.fetch_add(1, Ordering::Relaxed)
    ))
}
fn check_unchanged(context: &Context, expected: &Snapshot) -> Result<(), ConfigError> {
    let path =
        fs::canonicalize(&context.path).map_err(|error| ConfigError::io(&context.path, error))?;
    if path != expected.physical_path
        || fs::read_to_string(&path).map_err(|error| ConfigError::io(&path, error))?
            != expected.text
    {
        return Err(ConfigError::new(
            ErrorKind::Changed,
            "tavern.error.externally_modified",
            context.path.display(),
        ));
    }
    Ok(())
}
fn replace(context: &Context, expected: &Snapshot, text: &str) -> Result<(), ConfigError> {
    check_unchanged(context, expected)?;
    let permissions = fs::metadata(&expected.physical_path)
        .map_err(|e| ConfigError::io(&context.path, e))?
        .permissions();
    let temp = temporary(&expected.physical_path, "saving");
    let result = (|| {
        // 临时文件先以默认权限创建，写入完成后再继承原文件权限；
        // Windows 没有 POSIX 位权限，权限继承通过 `set_permissions` 完成。
        let mut file = fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temp)
            .map_err(|e| ConfigError::io(&temp, e))?;
        file.write_all(text.as_bytes())
            .map_err(|e| ConfigError::io(&temp, e))?;
        file.set_permissions(permissions)
            .map_err(|e| ConfigError::io(&temp, e))?;
        file.sync_all().map_err(|e| ConfigError::io(&temp, e))?;
        check_unchanged(context, expected)?;
        fs::rename(&temp, &expected.physical_path).map_err(|e| ConfigError::io(&context.path, e))
    })();
    if result.is_err() {
        let _ = fs::remove_file(&temp);
    }
    result
}

#[derive(Debug, Clone)]
pub struct NetworkOptions {
    pub proxy_mode: String,
    pub proxy_host: String,
    pub github_proxy: Option<String>,
}
pub fn template(
    context: &Context,
    options: &NetworkOptions,
    progress: &mut impl FnMut(u64, Option<u64>),
    defaults: &Values,
) -> Result<String, ConfigError> {
    let cache = app_root().join("default/config.yaml");
    let candidates = [
        context.instance.join("default/config.yaml"),
        cache.clone(),
        app_root().join("data/default/sillytavern/config.yaml"),
    ];
    for path in candidates {
        match fs::read_to_string(&path) {
            Ok(text) => {
                snapshot(text.clone(), path, defaults)?;
                return Ok(text);
            }
            Err(error) if error.kind() == io::ErrorKind::NotFound => {}
            Err(error) => return Err(ConfigError::io(&path, error)),
        }
    }
    let url = "https://raw.githubusercontent.com/SillyTavern/SillyTavern/refs/heads/release/default/config.yaml";
    let mut urls = Vec::new();
    if let Some(proxy) = &options.github_proxy {
        urls.push(format!("{}/{url}", proxy.trim_end_matches('/')));
    }
    urls.push(url.into());
    let client = crate::core::network::build_client(&options.proxy_mode, &options.proxy_host)
        .map_err(|_| ConfigError::new(ErrorKind::Template, "tavern.error.template_request", ""))?;
    for url in urls {
        let result = (|| -> Result<String, ConfigError> {
            let mut response = client
                .get(url)
                .send()
                .and_then(reqwest::blocking::Response::error_for_status)
                .map_err(|_| ConfigError::new(ErrorKind::Template, "tavern.error.template_download", ""))?;
            let total = response.content_length();
            let mut bytes = Vec::new();
            let mut buffer = [0; 8192];
            loop {
                let n = response
                    .read(&mut buffer)
                    .map_err(|_| ConfigError::new(ErrorKind::Template, "tavern.error.template_download", ""))?;
                if n == 0 {
                    break;
                }
                bytes.extend_from_slice(&buffer[..n]);
                if bytes.len() > 8 * 1024 * 1024 {
                    return Err(ConfigError::new(
                        ErrorKind::Template,
                        "tavern.error.template_content_invalid",
                        "",
                    ));
                }
                progress(bytes.len() as u64, total);
            }
            let text = String::from_utf8(bytes)
                .map_err(|_| ConfigError::new(ErrorKind::Template, "tavern.error.template_content_invalid", ""))?;
            snapshot(text.clone(), cache.clone(), defaults)?;
            Ok(text)
        })();
        if let Ok(text) = result {
            // 模板缓存失败不覆盖目标文件；下次仍可重新下载。
            let _ = create_new(&cache, &text);
            return Ok(text);
        }
    }
    Err(ConfigError::new(
        ErrorKind::Template,
        "tavern.error.template_all_failed",
        "",
    ))
}

/// 先写临时文件，再用硬链接进行“不覆盖”发布，防止生成期间外部已创建配置。
fn create_new(path: &Path, text: &str) -> Result<(), ConfigError> {
    let parent = path
        .parent()
        .ok_or_else(|| ConfigError::new(ErrorKind::Io, "tavern.error.target_path_invalid", ""))?;
    fs::create_dir_all(parent).map_err(|e| ConfigError::io(parent, e))?;
    let temp = temporary(path, "creating");
    let result = (|| {
        let mut file = fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temp)
            .map_err(|e| ConfigError::io(&temp, e))?;
        file.write_all(text.as_bytes())
            .map_err(|e| ConfigError::io(&temp, e))?;
        file.sync_all().map_err(|e| ConfigError::io(&temp, e))?;
        fs::hard_link(&temp, path).map_err(|error| {
            if error.kind() == io::ErrorKind::AlreadyExists {
                ConfigError::new(
                    ErrorKind::Changed,
                    "tavern.error.config_exists",
                    path.display(),
                )
            } else {
                ConfigError::io(path, error)
            }
        })
    })();
    let _ = fs::remove_file(&temp);
    result
}
fn normalize_text_whitelist(
    text: &str,
    defaults: &Values,
    policy: WhitelistPolicy,
) -> Result<String, ConfigError> {
    let (file, root) = parse(text)?;
    let loaded = snapshot(text.to_owned(), PathBuf::new(), defaults)?;
    let current = loaded
        .values
        .get("whitelist")
        .and_then(Value::as_array)
        .map(|items| {
            items
                .iter()
                .filter_map(Value::as_str)
                .map(str::to_owned)
                .collect::<Vec<_>>()
        })
        .unwrap_or_default();
    let normalized = normalize_whitelist(&current, policy);
    let normalized_value = Value::Array(normalized.into_iter().map(Value::String).collect());
    if loaded.raw.get("whitelist").and_then(Option::as_ref) != Some(&normalized_value) {
        let field = schema::field("whitelist")
            .ok_or_else(|| ConfigError::new(ErrorKind::Invalid, "tavern.error.unknown_field", "whitelist"))?;
        set_at(&root, field, &normalized_value, defaults)?;
    }
    Ok(file.to_string())
}

pub fn generate(
    context: &Context,
    options: &NetworkOptions,
    defaults: &Values,
    policy: WhitelistPolicy,
    progress: &mut impl FnMut(u64, Option<u64>),
) -> Result<Snapshot, ConfigError> {
    match load(context, defaults) {
        Ok(snapshot) => return Ok(snapshot),
        Err(error) if error.kind == ErrorKind::Missing => {}
        Err(error) => return Err(error),
    }
    let text = template(context, options, progress, defaults)?;
    let text = normalize_text_whitelist(&text, defaults, policy)?;
    match create_new(&context.path, &text) {
        Ok(()) => load(context, defaults),
        Err(error) if error.kind == ErrorKind::Changed => load(context, defaults),
        Err(error) => Err(error),
    }
}

/// 递归合并映射；用现有文档作底稿，保留未改动的注释和排版。
fn merge_maps(target: &Mapping, source: &Mapping, overwrite: bool) {
    for (key, value) in source.iter() {
        if let Some(key) = key.as_scalar() {
            let key = key.as_string();
            if let (Some(existing), Some(source)) = (target.get_mapping(&key), value.as_mapping()) {
                merge_maps(&existing, source, overwrite);
            } else if let Some(existing) = target.get(&key) {
                let equal = raw_value(&existing)
                    .ok()
                    .zip(raw_value(&value).ok())
                    .is_some_and(|(a, b)| a == b);
                if overwrite && !equal {
                    target.set(key, value);
                }
            } else {
                target.set(key, value);
            }
        }
    }
}
pub fn merge_text(
    template: &str,
    current: &str,
    imported: &str,
    defaults: &Values,
) -> Result<String, ConfigError> {
    let (file, root) = parse(current)?;
    let (_, default_map) = parse(template)?;
    let (_, incoming) = parse(imported)?;
    for text in [template, current, imported] {
        snapshot(text.into(), PathBuf::new(), defaults)?;
    }
    merge_maps(&root, &default_map, false);
    merge_maps(&root, &incoming, true);
    let text = file.to_string();
    snapshot(text.clone(), PathBuf::new(), defaults)?;
    Ok(text)
}
#[derive(Debug, Clone)]
pub struct ImportPreview {
    pub context: Context,
    pub source: PathBuf,
    pub source_text: String,
    pub target: Snapshot,
    pub template_text: String,
    pub merged_text: String,
    pub policy: WhitelistPolicy,
}
pub fn prepare_import(
    context: &Context,
    source: &Path,
    options: &NetworkOptions,
    defaults: &Values,
    policy: WhitelistPolicy,
    progress: &mut impl FnMut(u64, Option<u64>),
) -> Result<ImportPreview, ConfigError> {
    let target = load(context, defaults)?;
    let source_text = fs::read_to_string(source).map_err(|e| ConfigError::io(source, e))?;
    snapshot(source_text.clone(), source.to_owned(), defaults)?;
    let template_text = template(context, options, progress, defaults)?;
    let merged_text = merge_text(&template_text, &target.text, &source_text, defaults)?;
    let merged_text = normalize_text_whitelist(&merged_text, defaults, policy)?;
    Ok(ImportPreview {
        context: context.clone(),
        source: source.to_owned(),
        source_text,
        target,
        template_text,
        merged_text,
        policy,
    })
}
#[derive(Debug, Clone)]
pub enum ImportResult {
    Saved(Snapshot, PathBuf),
    Reconfirm(ImportPreview),
}
pub fn import(
    preview: &ImportPreview,
    defaults: &Values,
    policy: WhitelistPolicy,
) -> Result<ImportResult, ConfigError> {
    let target = load(&preview.context, defaults)?;
    let source_text =
        fs::read_to_string(&preview.source).map_err(|e| ConfigError::io(&preview.source, e))?;
    if target.text != preview.target.text
        || target.physical_path != preview.target.physical_path
        || source_text != preview.source_text
        || policy != preview.policy
    {
        let merged_text = merge_text(&preview.template_text, &target.text, &source_text, defaults)?;
        let merged_text = normalize_text_whitelist(&merged_text, defaults, policy)?;
        return Ok(ImportResult::Reconfirm(ImportPreview {
            target,
            source_text,
            merged_text,
            policy,
            ..preview.clone()
        }));
    }
    let backup = temporary(&target.physical_path, "backup");
    let mut file = fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&backup)
        .map_err(|e| ConfigError::io(&backup, e))?;
    file.write_all(target.text.as_bytes())
        .and_then(|()| file.sync_all())
        .map_err(|e| ConfigError::io(&backup, e))?;
    check_unchanged(&preview.context, &target)?;
    if fs::read_to_string(&preview.source).map_err(|e| ConfigError::io(&preview.source, e))?
        != source_text
    {
        return Err(ConfigError::new(
            ErrorKind::Changed,
            "tavern.error.import_source_changed",
            preview.source.display(),
        ));
    }
    replace(&preview.context, &target, &preview.merged_text)?;
    Ok(ImportResult::Saved(
        load(&preview.context, defaults)?,
        backup,
    ))
}

/// 在资源管理器里定位到配置文件。
pub fn reveal(context: &Context) -> Result<(), ConfigError> {
    crate::core::shell::reveal_in_explorer(&context.path).map_err(|error| {
        ConfigError::new(
            ErrorKind::Io,
            "tavern.error.reveal_failed",
            // 把失败原因一起带上：路径对了但打不开时，真正的原因才是排错线索。
            format!("{}: {error}", context.path.display()),
        )
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    struct Fixture {
        root: PathBuf,
        context: Context,
    }
    impl Fixture {
        fn new() -> Self {
            let root = temporary(&std::env::temp_dir().join("config.yaml"), "test");
            fs::create_dir_all(root.join("default")).unwrap();
            let context = Context {
                key: root.display().to_string(),
                path: root.join("config.yaml"),
                instance: root.clone(),
            };
            Self { root, context }
        }
        fn write(&self, text: &str) {
            fs::write(&self.context.path, text).unwrap();
        }
        fn defaults(&self) -> Values {
            crate::pages::tavern::TavernState::default().values()
        }
        fn load(&self) -> Snapshot {
            load(&self.context, &self.defaults()).unwrap()
        }
        fn patch(&self, key: &str, value: Value) -> Patch {
            Patch {
                key: key.into(),
                value,
                expected: self.load().raw[key].clone(),
                revision: 1,
            }
        }
    }
    impl Drop for Fixture {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.root);
        }
    }
    fn no_network() -> NetworkOptions {
        NetworkOptions {
            proxy_mode: "none".into(),
            proxy_host: String::new(),
            github_proxy: None,
        }
    }

    #[test]
    fn every_ui_field_has_a_unique_legacy_mapping_and_round_trips() {
        let fixture = Fixture::new();
        let defaults = fixture.defaults();
        assert_eq!(defaults.len(), schema::FIELDS.len());
        let mut keys = HashSet::new();
        let mut paths = HashSet::new();
        let (file, root) = parse("# configuration\n{}\n").unwrap();
        for field in schema::FIELDS {
            assert!(keys.insert(field.key));
            assert!(paths.insert(field.path));
            let raw = schema::encode(field, &defaults[field.key]).unwrap();
            set_at(&root, field, &raw, &defaults).unwrap();
        }
        let loaded = snapshot(file.to_string(), fixture.context.path.clone(), &defaults).unwrap();
        assert_eq!(loaded.values, defaults);
        assert_eq!(loaded.raw["cors_enabled"], Some(json!(true)));
        assert!(file.to_string().contains("cors:"));
        assert!(!file.to_string().contains("corsProxy:"));
    }

    #[test]
    fn scalar_patch_preserves_unknown_fields_comments_and_permissions() {
        let fixture = Fixture::new();
        fixture.write("# leading comment\nport: 8000 # port comment\n# unknown block\ncustomExtension:\n  token: 'quoted text' # keep me\nlisten: false\n");
        // Windows 没有 POSIX 权限位，用只读属性验证「原文件属性不被静默改写」。
        let mut permissions = fs::metadata(&fixture.context.path).unwrap().permissions();
        permissions.set_readonly(true);
        fs::set_permissions(&fixture.context.path, permissions.clone()).unwrap();
        let before = fixture.load();
        let patch = fixture.patch("port", json!(9000));
        let saved = save(
            &fixture.context,
            &before.physical_path,
            &[patch],
            &fixture.defaults(),
        )
        .unwrap();
        assert!(saved.snapshot.text.contains("# leading comment"));
        assert!(saved.snapshot.text.contains("# port comment"));
        assert!(
            saved
                .snapshot
                .text
                .contains("customExtension:\n  token: 'quoted text' # keep me")
        );
        assert_eq!(saved.snapshot.values["port"], "9000");
        let after = fs::metadata(&fixture.context.path).unwrap().permissions();
        assert_eq!(
            after.readonly(),
            permissions.readonly(),
            "保存配置后必须保持原文件的只读属性"
        );
        // 恢复可写，避免影响后续清理。
        let mut writable = fs::metadata(&fixture.context.path).unwrap().permissions();
        writable.set_readonly(false);
        let _ = fs::set_permissions(&fixture.context.path, writable);
        assert_eq!(saved.applied, vec![("port".into(), 1)]);
    }

    #[test]
    fn nested_scalar_and_dimension_patch_leave_siblings_untouched() {
        let fixture = Fixture::new();
        fixture.write("protocol:\n  ipv4: true # keep ipv4\n  ipv6: false\nthumbnails:\n  dimensions:\n    bg: [160, 90]\n    avatar: [96, 144]\n");
        let before = fixture.load();
        let patches = [
            fixture.patch("protocol_ipv6", json!(true)),
            fixture.patch("background_width", json!(320)),
        ];
        let saved = save(
            &fixture.context,
            &before.physical_path,
            &patches,
            &fixture.defaults(),
        )
        .unwrap();
        assert!(saved.snapshot.text.contains("# keep ipv4"));
        assert_eq!(saved.snapshot.values["background_width"], "320");
        assert_eq!(saved.snapshot.values["background_height"], "90");
        assert_eq!(saved.snapshot.values["avatar_width"], "96");
    }

    #[test]
    fn external_changes_merge_other_fields_and_conflict_on_same_field() {
        let fixture = Fixture::new();
        fixture.write("port: 8000\nlisten: false\n");
        let before = fixture.load();
        let patches = [
            fixture.patch("port", json!(9000)),
            fixture.patch("listen", json!(true)),
        ];
        fixture.write("port: 9100\nlisten: false\nexternal: preserved\n");
        let saved = save(
            &fixture.context,
            &before.physical_path,
            &patches,
            &fixture.defaults(),
        )
        .unwrap();
        assert_eq!(saved.conflicts, vec![("port".into(), 1)]);
        assert_eq!(saved.snapshot.values["port"], "9100");
        assert_eq!(saved.snapshot.values["listen"], true);
        assert!(saved.snapshot.text.contains("external: preserved"));
    }

    #[test]
    fn missing_invalid_and_multiple_documents_never_become_defaults_on_disk() {
        let fixture = Fixture::new();
        assert_eq!(
            load(&fixture.context, &fixture.defaults())
                .unwrap_err()
                .kind,
            ErrorKind::Missing
        );
        for text in [
            "- not-a-map\n",
            "port: invalid\n",
            "port: [\n",
            "---\nport: 8000\n---\nport: 9000\n",
            "port: 8000\nport: 9000\n",
            "protocol: true\n",
        ] {
            fixture.write(text);
            assert!(
                load(&fixture.context, &fixture.defaults()).is_err(),
                "{text}"
            );
            assert_eq!(fs::read_to_string(&fixture.context.path).unwrap(), text);
        }
    }

    #[test]
    fn explicit_null_empty_lists_and_unknown_enum_are_preserved() {
        let fixture = Fixture::new();
        fixture.write("cors:\n  maxAge: null\n  allowedHeaders: []\nbrowserLaunch:\n  browser: custom-browser\n");
        let before = fixture.load();
        assert_eq!(before.values["cors_max_age"], "");
        assert_eq!(before.values["browser_type"], "Unknown");
        let patch = fixture.patch("listen", json!(true));
        let after = save(
            &fixture.context,
            &before.physical_path,
            &[patch],
            &fixture.defaults(),
        )
        .unwrap();
        assert!(after.snapshot.text.contains("custom-browser"));
        assert_eq!(after.snapshot.raw["cors_max_age"], Some(Value::Null));
    }

    #[test]
    fn import_merges_new_defaults_and_old_fields_with_atomic_list_replacement() {
        let fixture = Fixture::new();
        let template = "port: 8000\nlisten: false\nprotocol:\n  ipv4: true\n  ipv6: false\nfutureOption: 123\nwhitelist: ['127.0.0.1']\n";
        let current = "# preserve current\nport: 9000\ncustom: {keep: true}\nprotocol:\n  ipv4: false # don't touch\nwhitelist: ['127.0.0.1', '::1']\n";
        let imported = "port: 9100\nprotocol:\n  ipv6: true\nwhitelist: ['::1']\nother: incoming\n";
        let merged = merge_text(template, current, imported, &fixture.defaults()).unwrap();
        let result = snapshot(
            merged.clone(),
            fixture.context.path.clone(),
            &fixture.defaults(),
        )
        .unwrap();
        assert_eq!(result.values["port"], "9100");
        assert_eq!(result.values["listen"], false);
        assert_eq!(result.values["protocol_ipv4"], false);
        assert_eq!(result.values["protocol_ipv6"], true);
        assert_eq!(result.values["whitelist"], json!(["::1"]));
        assert!(merged.contains("# preserve current"));
        assert!(merged.contains("# don't touch"));
        assert!(merged.contains("futureOption"));
        assert!(merged.contains("custom:"));
        assert!(merged.contains("other:"));
    }

    #[test]
    fn import_backs_up_and_reconfirms_changed_source_or_target() {
        let fixture = Fixture::new();
        fixture.write("port: 8000\n");
        fs::write(
            fixture.root.join("default/config.yaml"),
            "port: 8000\nlisten: false\n",
        )
        .unwrap();
        let source = fixture.root.join("import.yml");
        fs::write(&source, "port: 9000\n").unwrap();
        let preview = prepare_import(
            &fixture.context,
            &source,
            &no_network(),
            &fixture.defaults(),
            policy(false, WhitelistServiceMode::Lan),
            &mut |_, _| {},
        )
        .unwrap();
        assert_eq!(fixture.load().values["port"], "8000");
        fixture.write("port: 8100\nlisten: true\n");
        let ImportResult::Reconfirm(preview) = import(
            &preview,
            &fixture.defaults(),
            policy(false, WhitelistServiceMode::Lan),
        )
        .unwrap() else {
            panic!("requires reconfirmation");
        };
        assert_eq!(fixture.load().values["port"], "8100");
        fs::write(&source, "port: 9200\n").unwrap();
        let ImportResult::Reconfirm(preview) = import(
            &preview,
            &fixture.defaults(),
            policy(false, WhitelistServiceMode::Lan),
        )
        .unwrap() else {
            panic!("requires reconfirmation");
        };
        let ImportResult::Saved(saved, backup) = import(
            &preview,
            &fixture.defaults(),
            policy(false, WhitelistServiceMode::Lan),
        )
        .unwrap() else {
            panic!("expected saved");
        };
        assert_eq!(saved.values["port"], "9200");
        assert_eq!(saved.values["listen"], true);
        assert_eq!(
            fs::read_to_string(backup).unwrap(),
            "port: 8100\nlisten: true\n"
        );
    }

    #[test]
    fn import_requires_reconfirmation_when_service_policy_changes() {
        let fixture = Fixture::new();
        fixture.write(
            "whitelist: ['::1', '127.0.0.1', '203.0.113.9']
",
        );
        fs::write(
            fixture.root.join("default/config.yaml"),
            "whitelist: ['::1', '127.0.0.1']
",
        )
        .unwrap();
        let source = fixture.root.join("policy-import.yml");
        fs::write(
            &source,
            "port: 9000
",
        )
        .unwrap();
        let preview = prepare_import(
            &fixture.context,
            &source,
            &no_network(),
            &fixture.defaults(),
            policy(false, WhitelistServiceMode::Lan),
            &mut |_, _| {},
        )
        .unwrap();
        let ImportResult::Reconfirm(preview) = import(
            &preview,
            &fixture.defaults(),
            policy(true, WhitelistServiceMode::Internet),
        )
        .unwrap() else {
            panic!("policy change must be reconfirmed");
        };
        let merged = snapshot(
            preview.merged_text,
            fixture.context.path.clone(),
            &fixture.defaults(),
        )
        .unwrap();
        assert_eq!(
            merged.values["whitelist"],
            json!(["::1", "127.0.0.1", "203.0.113.9", "0.0.0.0/0", "::/0"])
        );
        assert_eq!(fixture.load().values["port"], "8000");
    }

    #[test]
    fn generation_keeps_template_network_values_and_never_overwrites_existing_file() {
        let fixture = Fixture::new();
        fs::write(
            fixture.root.join("default/config.yaml"),
            "# template\nport: 8000\nlisten: false\nprotocol:\n  ipv6: false\n",
        )
        .unwrap();
        let generated = generate(
            &fixture.context,
            &no_network(),
            &fixture.defaults(),
            policy(false, WhitelistServiceMode::Lan),
            &mut |_, _| {},
        )
        .unwrap();
        assert_eq!(generated.values["port"], "8000");
        assert_eq!(generated.values["listen"], false);
        assert_eq!(generated.values["protocol_ipv6"], false);
        assert_eq!(generated.values["whitelist"], json!(["::1", "127.0.0.1"]));
        assert!(create_new(&fixture.context.path, "port: 9999\n").is_err());
        assert_eq!(fixture.load().values["port"], "8000");
    }

    #[test]
    fn symlink_retarget_and_corrupt_template_stop_without_writing() {
        let fixture = Fixture::new();
        fixture.write("port: 8000\n");
        let other = fixture.root.join("other.yaml");
        fs::write(&other, "port: 9100\n").unwrap();
        let before = fixture.load();
        fs::remove_file(&fixture.context.path).unwrap();
        // 无权限创建符号链接时跳过：该用例校验的是「重定向后仍按物理路径写入」。
        if !crate::utils::try_symlink_file(&other, &fixture.context.path) {
            return;
        }
        let patch = Patch {
            key: "port".into(),
            value: json!(9000),
            expected: Some(json!(8000)),
            revision: 1,
        };
        assert_eq!(
            save(
                &fixture.context,
                &before.physical_path,
                &[patch],
                &fixture.defaults()
            )
            .unwrap_err()
            .kind,
            ErrorKind::Changed
        );
        assert_eq!(fs::read_to_string(other).unwrap(), "port: 9100\n");
        fs::remove_file(&fixture.context.path).unwrap();
        fs::write(fixture.root.join("default/config.yaml"), "- invalid\n").unwrap();
        assert!(
            generate(
                &fixture.context,
                &no_network(),
                &fixture.defaults(),
                policy(false, WhitelistServiceMode::Lan),
                &mut |_, _| {},
            )
            .is_err()
        );
        assert!(!fixture.context.path.exists());
    }

    #[test]
    fn target_resolution_requires_selection_and_respects_data_mode() {
        assert!(Context::resolve(None, "online", true, "/custom").is_none());
        let local = Context::resolve(Some("/fixture/local"), "local", false, "/ignored").unwrap();
        assert_eq!(local.path, Path::new("/fixture/local/config.yaml"));
        let global = Context::resolve(Some("/fixture/local"), "local", true, "/custom").unwrap();
        assert_eq!(global.path, Path::new("/custom/config.yaml"));
        assert_ne!(local.key, global.key);
    }
    fn policy(server_enabled: bool, service_mode: WhitelistServiceMode) -> WhitelistPolicy {
        WhitelistPolicy {
            server_enabled,
            service_mode,
        }
    }

    #[test]
    fn fixed_whitelist_matches_the_legacy_service_modes() {
        assert_eq!(
            fixed_whitelist(policy(false, WhitelistServiceMode::Internet)),
            ["::1", "127.0.0.1"]
        );
        assert_eq!(
            fixed_whitelist(policy(true, WhitelistServiceMode::Lan)),
            [
                "::1",
                "127.0.0.1",
                "10.0.0.0/8",
                "172.16.0.0/12",
                "192.168.0.0/16"
            ]
        );
        assert_eq!(
            fixed_whitelist(policy(true, WhitelistServiceMode::Internet)),
            ["::1", "127.0.0.1", "0.0.0.0/0", "::/0"]
        );
        assert_eq!(all_reserved_whitelist_ips().len(), 7);
        for value in all_reserved_whitelist_ips() {
            assert!(is_reserved_whitelist_ip(value));
        }
    }

    #[test]
    fn mode_switch_replaces_only_reserved_ranges_and_deduplicates() {
        let values = vec![
            "::1".into(),
            "203.0.113.8".into(),
            "10.0.0.0/8".into(),
            "203.0.113.8".into(),
            "0.0.0.0/0".into(),
            "2001:db8::/32".into(),
        ];
        assert_eq!(
            normalize_whitelist(&values, policy(true, WhitelistServiceMode::Internet)),
            [
                "::1",
                "203.0.113.8",
                "0.0.0.0/0",
                "2001:db8::/32",
                "127.0.0.1",
                "::/0"
            ]
        );
        assert_eq!(
            normalize_whitelist(&values, policy(false, WhitelistServiceMode::Lan)),
            ["::1", "203.0.113.8", "2001:db8::/32", "127.0.0.1"]
        );
    }

    #[test]
    fn whitelist_three_way_merge_preserves_user_changes_on_both_sides() {
        let base = vec![
            "::1".into(),
            "127.0.0.1".into(),
            "198.51.100.1".into(),
            "198.51.100.2".into(),
        ];
        let local = vec![
            "::1".into(),
            "127.0.0.1".into(),
            "198.51.100.2".into(),
            "203.0.113.1".into(),
        ];
        let disk = vec![
            "127.0.0.1".into(),
            "198.51.100.1".into(),
            "198.51.100.2".into(),
            "203.0.113.2".into(),
        ];
        assert_eq!(
            merge_whitelist(
                &base,
                &local,
                &disk,
                policy(true, WhitelistServiceMode::Lan)
            ),
            [
                "198.51.100.2",
                "203.0.113.1",
                "203.0.113.2",
                "::1",
                "127.0.0.1",
                "10.0.0.0/8",
                "172.16.0.0/12",
                "192.168.0.0/16"
            ]
        );
    }

    #[test]
    fn whitelist_normalization_preserves_one_blank_editing_row() {
        let values = vec!["".into(), " ".into(), "127.0.0.1".into()];
        assert_eq!(
            normalize_whitelist(&values, policy(false, WhitelistServiceMode::Lan)),
            ["", "127.0.0.1", "::1"]
        );
    }
}
