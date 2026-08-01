pub mod en;
pub mod zh;

use crate::pages::settings::Language;
use std::sync::OnceLock;
use windows_sys::Win32::Globalization::GetUserDefaultUILanguage;

/// 缓存系统语言检测结果，避免每次翻译都重复调用 Windows API。
static SYSTEM_LANG: OnceLock<Language> = OnceLock::new();

/// 解析语言：跟随系统时读取 Windows 用户界面语言，其他语言回退到英文。
pub fn effective_language(lang: &Language) -> Language {
    match lang {
        Language::System => *SYSTEM_LANG.get_or_init(detect_system_language),
        other => *other,
    }
}

/// 根据 Windows LANGID 判断启动器支持的语言。
fn language_from_windows_lang_id(lang_id: u16) -> Language {
    const PRIMARY_LANGUAGE_MASK: u16 = 0x03ff;
    const LANG_CHINESE: u16 = 0x0004;

    if lang_id & PRIMARY_LANGUAGE_MASK == LANG_CHINESE {
        Language::Chinese
    } else {
        Language::English
    }
}

/// 检测 Windows 用户界面语言（仅首次调用时执行）。
fn detect_system_language() -> Language {
    language_from_windows_lang_id(unsafe { GetUserDefaultUILanguage() })
}

pub fn t<'a>(key: &'a str, lang: &Language) -> &'a str {
    let lang = effective_language(lang);
    match lang {
        Language::Chinese => zh::translate(key),
        Language::English => en::translate(key),
        Language::System => unreachable!("effective_language already resolved System"),
    }
}

#[cfg(test)]
mod tests {
    use super::{Language, language_from_windows_lang_id};

    #[test]
    fn recognizes_all_windows_chinese_ui_language_variants() {
        for lang_id in [0x0804, 0x0404, 0x0c04, 0x1004] {
            assert!(matches!(
                language_from_windows_lang_id(lang_id),
                Language::Chinese
            ));
        }
    }

    #[test]
    fn unsupported_windows_ui_languages_fall_back_to_english() {
        for lang_id in [0x0409, 0x0411, 0x0412] {
            assert!(matches!(
                language_from_windows_lang_id(lang_id),
                Language::English
            ));
        }
    }
}
