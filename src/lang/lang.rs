pub mod en;
pub mod zh;

use crate::pages::settings::Language;
use std::sync::OnceLock;

/// 缓存系统语言检测结果，避免每次翻译都执行子进程
static SYSTEM_LANG: OnceLock<Language> = OnceLock::new();

/// 解析语言：跟随系统时自动检测 macOS 系统语言，回退到英文
pub fn effective_language(lang: &Language) -> Language {
    match lang {
        Language::System => *SYSTEM_LANG.get_or_init(detect_system_language),
        other => *other,
    }
}

/// 检测 macOS 系统语言（仅首次调用时执行）
fn detect_system_language() -> Language {
    // 优先检查 LANG 环境变量
    if let Ok(locale) = std::env::var("LANG") {
        if locale.starts_with("zh") {
            return Language::Chinese;
        }
    }
    // 回退方案：读取 macOS 首选语言列表
    if let Ok(output) = std::process::Command::new("defaults")
        .args(["read", "-g", "AppleLanguages"])
        .output()
    {
        if let Ok(s) = String::from_utf8(output.stdout) {
            if s.contains("zh") {
                return Language::Chinese;
            }
        }
    }
    // 默认英文
    Language::English
}

pub fn t<'a>(key: &'a str, lang: &Language) -> &'a str {
    let lang = effective_language(lang);
    match lang {
        Language::Chinese => zh::translate(key),
        Language::English => en::translate(key),
        Language::System => unreachable!("effective_language already resolved System"),
    }
}
