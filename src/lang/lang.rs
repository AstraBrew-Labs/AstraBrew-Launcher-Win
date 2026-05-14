pub mod en;
pub mod zh;

use crate::pages::settings::Language;

pub fn t<'a>(key: &'a str, lang: &Language) -> &'a str {
    match lang {
        Language::Chinese => zh::translate(key),
        Language::English => en::translate(key),
    }
}
