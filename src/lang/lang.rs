//! 语言解析与翻译引擎。
//!
//! 全部界面文案以「键」为唯一标识，键到文案的映射由 `zh.rs` 与 `en.rs` 两张
//! 键值表分别提供。界面代码只允许出现键，不允许出现具体文案：
//!
//! ```ignore
//! text("settings.title")                    // 静态键
//! tf("versions.switched", &[("version", v)]) // 带命名占位符
//! t("common.cancel")                         // 需要 &'static str 的场合
//! ```
//!
//! 键命名规范：`<域>.<语义>`，全小写下划线分隔，例如 `settings.title`、
//! `extensions.error.clone_failed`、`tavern.field.port.hint`。

use std::cell::Cell;
use std::sync::OnceLock;

use crate::core::settings::DisplayLanguage;

/// 渲染时实际使用的语言。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Language {
    /// 简体中文。
    Chinese,
    /// 英文。
    English,
}

impl Language {
    /// 该语言的键值表。查不到时返回 `None`，由引擎负责回退。
    pub(crate) fn lookup(self, key: &'static str) -> Option<&'static str> {
        // 两张表由同一份词表生成，键集合必须一致；此断言同时让键清单在
        // 非测试构建下也被引用，避免死代码告警。
        debug_assert_eq!(super::zh::KEYS.len(), super::en::KEYS.len());
        match self {
            Self::Chinese => super::zh::lookup(key),
            Self::English => super::en::lookup(key),
        }
    }

    /// 回退语言：优先保证有文案，其次才要求语言精确。
    const fn fallback(self) -> Self {
        match self {
            Self::Chinese => Self::English,
            Self::English => Self::Chinese,
        }
    }
}

static SYSTEM_LANGUAGE: OnceLock<Language> = OnceLock::new();

thread_local! {
    /// iced 在同一 UI 线程构建并绘制元素；这里保存当前视图的只读渲染语言。
    static RENDER_LANGUAGE: Cell<Language> = const { Cell::new(Language::English) };
}

/// 将用户选择解析为实际语言。
pub fn effective_language(language: DisplayLanguage) -> Language {
    match language {
        DisplayLanguage::SimplifiedChinese => Language::Chinese,
        DisplayLanguage::English => Language::English,
        DisplayLanguage::System => *SYSTEM_LANGUAGE.get_or_init(detect_system_language),
    }
}

/// 在构建当前帧前设置渲染语言。
pub fn set_language(language: Language) {
    RENDER_LANGUAGE.set(language);
}

/// 当前帧使用的语言，供需要实现 `Display` 的控件选项读取。
pub fn current_language() -> Language {
    RENDER_LANGUAGE.get()
}

/// 按指定语言翻译键。
///
/// 查表顺序为「请求语言 → 回退语言 → 键本身」。回退到键本身只应出现在键表
/// 缺项的情况下，`zh.rs` 与 `en.rs` 的键集合一致性由单元测试守住。
pub fn t_in(key: &'static str, language: Language) -> &'static str {
    language
        .lookup(key)
        .or_else(|| language.fallback().lookup(key))
        .unwrap_or(key)
}

/// 按当前语言翻译键。
pub fn t(key: &'static str) -> &'static str {
    t_in(key, current_language())
}

/// 解析「可能是键」的运行时字符串。
///
/// 错误信息与日志行这类通道既可能是文案键（如 `extensions.error.clone_failed`），
/// 也可能是运行时拼接的自由文本（路径、退出码、外部工具输出）。命中键表时按当前语言
/// 翻译，否则原样返回。
///
/// 只应在字符串**进入通道时**调用一次（错误入 state、日志入队），
/// **不要**放在每帧渲染路径上——键清单是线性查找，频率高时会白烧 CPU。
pub fn resolve(content: &str) -> String {
    match crate::lang::zh::KEYS.iter().copied().find(|key| *key == content) {
        Some(key) => t(key).to_owned(),
        None => content.to_owned(),
    }
}

/// 按当前语言翻译带命名占位符的模板。
///
/// 模板中用 `{name}` 表示占位符，未在 `args` 中出现的占位符原样保留，便于
/// 在本地化文案尚未补齐时快速定位问题。实参可为任意实现 `ToString` 的类型。
pub fn tf(key: &'static str, args: &[(&str, &dyn ToString)]) -> String {
    interpolate(t(key), args)
}

/// 构造会自动翻译键的 iced 文本控件。
pub fn text<'a>(key: &'static str) -> iced::widget::Text<'a> {
    raw(t(key))
}

/// 构造会自动翻译模板并插值的 iced 文本控件。
pub fn textf<'a>(key: &'static str, args: &[(&str, &dyn ToString)]) -> iced::widget::Text<'a> {
    raw(tf(key, args))
}

/// 构造不参与翻译的原始文本控件，用于版本号、路径、错误详情等运行时数据。
///
/// **传入的必须是已经翻译好的值**。若内容恰好是文案键（说明某处漏了翻译），
/// 开启审计模式（环境变量 `ASTRA_I18N_AUDIT=1`）时会在 stderr 打印告警：
///
/// ```sh
/// ASTRA_I18N_AUDIT=1 cargo run      # 逐个操作界面，把漏网的键全扫出来
/// ```
pub fn raw<'a>(content: impl Into<String>) -> iced::widget::Text<'a> {
    let content = content.into();
    audit_raw_display(&content);
    iced::widget::text(content).font(crate::core::typography::regular())
}

/// `raw()` 的开发期自检：内容命中键表时告警。
///
/// 默认关闭——键清单是线性查找，常开会让调试构建的白帧明显变慢；
/// 排查时用 `ASTRA_I18N_AUDIT=1` 临时打开即可。
fn audit_raw_display(content: &str) {
    if !audit_enabled() {
        return;
    }
    if let Some(key) = crate::lang::zh::KEYS.iter().copied().find(|key| *key == content) {
        eprintln!(
            "[i18n] raw() 收到文案键 `{key}`：应改用 text()/t()，或在字符串进入通道时 resolve()"
        );
    }
}

/// 审计模式是否开启（`ASTRA_I18N_AUDIT` 存在且不为 `0`）。
fn audit_enabled() -> bool {
    static ENABLED: OnceLock<bool> = OnceLock::new();
    *ENABLED.get_or_init(|| {
        std::env::var_os("ASTRA_I18N_AUDIT").is_some_and(|value| value != "0")
    })
}

/// 将 `{name}` 占位符替换为对应实参。
fn interpolate(template: &str, args: &[(&str, &dyn ToString)]) -> String {
    if args.is_empty() {
        return template.to_owned();
    }

    let mut result = String::with_capacity(template.len() + 32);
    let mut rest = template;

    while let Some(open) = rest.find('{') {
        result.push_str(&rest[..open]);
        let after_open = &rest[open + 1..];
        let Some(close) = after_open.find('}') else {
            // 没有闭合括号，剩余部分按字面量处理。
            result.push_str(&rest[open..]);
            return result;
        };
        let name = &after_open[..close];
        match args.iter().find(|(key, _)| *key == name) {
            Some((_, value)) => result.push_str(&value.to_string()),
            None => {
                result.push('{');
                result.push_str(name);
                result.push('}');
            }
        }
        rest = &after_open[close + 1..];
    }

    result.push_str(rest);
    result
}

/// 读取系统首选语言，无法识别时回退英文。
///
/// Windows 上通过 `GetUserDefaultLocaleName` 取 BCP-47 语言标签
/// （如 `zh-CN`、`en-US`）；该接口不依赖控制台，也不会拉起子进程。
fn detect_system_language() -> Language {
    if let Some(locale) = user_default_locale() {
        let lowered = locale.to_ascii_lowercase();
        // `zh`、`zh-CN`、`zh-Hans` 等一律视为中文。
        if lowered == "zh" || lowered.starts_with("zh-") {
            return Language::Chinese;
        }
        return Language::English;
    }

    // 环境变量兜底：某些精简环境（如 CI）取不到区域设置。
    if let Ok(locale) = std::env::var("LANG")
        && locale.to_ascii_lowercase().starts_with("zh")
    {
        return Language::Chinese;
    }

    Language::English
}

/// 调用 `GetUserDefaultLocaleName` 取当前用户的区域名称。
///
/// 失败（返回值 <= 0）或结果非 UTF-16 时返回 `None`。
#[cfg(windows)]
fn user_default_locale() -> Option<String> {
    use windows_sys::Win32::Globalization::GetUserDefaultLocaleName;

    // 区域名称最长 85 字节（含结尾 NUL）；留出余量避免边界问题。
    let mut buffer = [0_u16; 128];
    // SAFETY: 缓冲区在调用期间存活，长度以 u16 元素个数计，与 API 约定一致。
    let written = unsafe { GetUserDefaultLocaleName(buffer.as_mut_ptr(), buffer.len() as i32) };
    if written <= 0 {
        return None;
    }
    // `written` 包含结尾 NUL，需剔除后再解码。
    let length = (written as usize).saturating_sub(1);
    String::from_utf16(&buffer[..length]).ok()
}

/// 非 Windows 平台不做系统语言识别，直接回退英文。
#[cfg(not(windows))]
fn user_default_locale() -> Option<String> {
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn interpolate_replaces_named_placeholders() {
        let name = "酒馆".to_string();
        let version = "1.2.3".to_string();
        assert_eq!(
            interpolate(
                "已切换到 {name} v{version}。",
                &[("name", &name), ("version", &version)]
            ),
            "已切换到 酒馆 v1.2.3。"
        );
    }

    #[test]
    fn interpolate_keeps_unknown_placeholders() {
        let other = "x".to_string();
        assert_eq!(
            interpolate("正在处理 {target}", &[("other", &other)]),
            "正在处理 {target}"
        );
    }

    #[test]
    fn interpolate_returns_template_without_args() {
        assert_eq!(interpolate("无参数文案", &[]), "无参数文案");
    }

    #[test]
    fn interpolate_handles_unclosed_brace() {
        assert_eq!(interpolate("前缀 {未闭合", &[("a", &"b".to_string())]), "前缀 {未闭合");
    }

    #[test]
    fn lookup_matches_language() {
        assert_eq!(t_in("common.cancel", Language::Chinese), "取消");
        assert_eq!(t_in("common.cancel", Language::English), "Cancel");
    }

    #[test]
    fn unknown_key_falls_back_to_key_itself() {
        assert_eq!(t_in("no.such.key", Language::Chinese), "no.such.key");
    }

    #[test]
    fn key_sets_match_between_languages() {
        assert_eq!(
            crate::lang::zh::KEYS,
            crate::lang::en::KEYS,
            "中英键值表的键集合必须完全一致"
        );
    }

    #[test]
    fn every_key_resolves_in_every_language() {
        for key in crate::lang::zh::KEYS {
            for language in [Language::Chinese, Language::English] {
                let value = language.lookup(key);
                assert!(value.is_some(), "{key} 在 {language:?} 表中缺失");
                assert!(!value.unwrap().is_empty(), "{key} 在 {language:?} 表中为空");
            }
        }
    }
}
