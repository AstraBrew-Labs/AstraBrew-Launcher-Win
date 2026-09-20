//! 启动器字体目录、动态字体加载与界面缩放规范。
//!
//! 系统字体只在启动时扫描元数据；真正的字体文件按用户选择加载，避免把所有
//! 系统字体长期放入内存。渲染字体通过线程局部状态传递给各页面，与语言模块
//! 采用相同方式，保证构建同一帧时使用一致的字体族。
//!
//! # 中英文粗细一致性（重要）
//!
//! 界面文字必须来自**同一套字体设计**，否则中英文的笔画粗细无法对齐。这条
//! 约束带来了下面三个看起来有点绕、但都不能省略的设计：
//!
//! 1. **默认字体选用内置思源黑体，而不是 astra_ui 的 HarmonyOS Sans**。
//!    HarmonyOS Sans 只有拉丁字形、**不含任何 CJK 字形**，中文只能靠
//!    cosmic-text 的字符级回退去系统里找，最终落到 `Microsoft YaHei UI`。
//!    雅黑的笔画设计天生比 HarmonyOS Sans 粗，于是同一行里「中文比英文数字
//!    更重」—— 这不是字重写错，而是两套字体设计的基线不同。
//!
//! 2. **内置三个独立字重，而不是一个可变字体**。思源黑体官方发布的静态字重
//!    文件每个字重都是一份独立设计，字形轮廓由设计师逐档调校；同一族里放着
//!    400 / 500 / 700 三份文件，fontdb 对界面用到的每一档都能精确命中。
//!    这解决的是「字重吸附」问题：雅黑在 `Microsoft YaHei UI` 这个族名下只有
//!    290 / 400 / 700，遇到 [`medium()`] 请求的 500 会被吸附到 400，而那 187
//!    处调用点里中文会比拉丁矮一档字重。内置字体有真正的 500，不会被吸附。
//!
//! 3. **三档字重都必须是同一个族的成员**。字体文件自带的名字表在发布时并不
//!    统一（部分字重把「族名 + 字重」写进 nid 1，族名藏进 nid 16），光靠原始
//!    命名是碰巧能聚成一组。因此打包前由 `assets/fonts/subset_fonts.py` 显式
//!    把三个文件的族名统一成 [`DEFAULT_FAMILY_NAME`]，让分组由代码决定而不是
//!    由偶然决定。
//!
//! 因此 [`DEFAULT_FONT_KEY`] 指向的字形数据由 [`BUNDLED_FONT_FILES`] 提供，
//! 它们同时覆盖中文、拉丁、数字与标点，中英文粗细天然一致。

use crate::lang::tf;
use std::cell::Cell;
use std::collections::{BTreeMap, BTreeSet, HashMap};
use std::fmt;
use std::path::PathBuf;
use std::sync::{Arc, OnceLock};

use fontdb::Source;
use iced::Font;
use iced::font::{Family, Stretch, Style, Weight};

/// settings.json 中表示内置默认字体的稳定值。
pub(crate) const DEFAULT_FONT_KEY: &str = "default";
/// 内置默认字体在 iced 字体表中的族名。
///
/// 这个名字来自字体文件自身的 name 表，iced 按它做族匹配，因此不能随意改动，
/// 必须与 `assets/fonts/subset_fonts.py` 写入的族名完全一致。
const DEFAULT_FAMILY_NAME: &str = "Source Han Sans SC";
/// 内置默认字体的字形数据（三档字重）。
///
/// 思源黑体按 GB2312 全字集 + 拉丁 + 常用符号裁剪后的子集，每档约 3.4 MB，
/// 三档合计约 10 MB。生成方式见 `assets/fonts/subset_fonts.py`，不要手工替换
/// 文件 —— 字体族名与 [`DEFAULT_FAMILY_NAME`] 必须对齐，否则 iced 匹配不到。
///
/// 选用思源黑体而非系统雅黑，是因为它同时覆盖中英文，中文字形在三个字重下
/// 与拉丁字形出自同一套设计，界面里不会出现「中文比英文粗」的割裂感。
pub(crate) const BUNDLED_FONT_FILES: &[&[u8]] = &[
    include_bytes!("../../assets/fonts/SourceHanSansSC-Regular.otf"),
    include_bytes!("../../assets/fonts/SourceHanSansSC-Medium.otf"),
    include_bytes!("../../assets/fonts/SourceHanSansSC-Bold.otf"),
];
/// Windows 自带的 CJK 字体族名。
///
/// 仅在用户主动改选系统字体、或内置字体加载失败时作为兜底，保证中文绝不会
/// 渲染成缺字方框。按「无衬线优先」排列。
const WINDOWS_CJK_FALLBACKS: &[&str] = &["Microsoft YaHei UI", "Microsoft YaHei", "SimSun"];
/// 用户可选的最小界面缩放。
pub(crate) const MIN_UI_SCALE: f32 = 0.90;
/// 用户可选的最大界面缩放。
pub(crate) const MAX_UI_SCALE: f32 = 1.50;
/// 界面缩放的调节步长。
pub(crate) const UI_SCALE_STEP: f32 = 0.05;
/// 新安装及旧配置缺少字段时使用的默认缩放。
pub(crate) const DEFAULT_UI_SCALE: f32 = 1.10;

/// 可在字体搜索框中展示的一项字体族。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub(crate) struct FontChoice {
    key: &'static str,
    family: Option<&'static str>,
    /// 该字体族自身是否带中文字形。
    ///
    /// 界面文字必须由一个覆盖中英文的字体渲染，否则中文会回退到系统字体、
    /// 与拉丁数字形成粗细差。系统里大多数西文字体（Arial、Calibri 等）都不含
    /// 中文，因此选择器需要把这个信息透出来提醒用户。
    cjk: bool,
}

impl FontChoice {
    /// 内置思源黑体选项（同时覆盖中英文）。
    pub(crate) const fn default_choice() -> Self {
        Self {
            key: DEFAULT_FONT_KEY,
            family: None,
            cjk: true,
        }
    }

    /// settings.json 中使用的稳定键。
    pub(crate) const fn key(self) -> &'static str {
        self.key
    }

    /// 传给 iced 字体选择器的真实字体族名。
    pub(crate) const fn family(self) -> Option<&'static str> {
        self.family
    }

    /// 该字体族是否自身带中文字形。
    pub(crate) const fn covers_cjk(self) -> bool {
        self.cjk
    }
}

impl Default for FontChoice {
    fn default() -> Self {
        Self::default_choice()
    }
}

impl fmt::Display for FontChoice {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self.family {
            Some(family) => f.write_str(family),
            None => f.write_str(crate::lang::t("settings.interface.font.default")),
        }
    }
}

/// Windows 可见字体族目录。
#[derive(Debug, Clone)]
pub(crate) struct SystemFontCatalog {
    choices: Arc<Vec<FontChoice>>,
    sources: Arc<HashMap<&'static str, Vec<PathBuf>>>,
}

impl Default for SystemFontCatalog {
    fn default() -> Self {
        Self {
            choices: Arc::new(vec![FontChoice::default_choice()]),
            sources: Arc::new(HashMap::new()),
        }
    }
}

impl SystemFontCatalog {
    /// 扫描 Windows 系统字体目录与当前用户字体目录。
    pub(crate) fn discover() -> Self {
        let mut database = fontdb::Database::new();
        database.load_system_fonts();

        // 三列含义：原始族名、字体文件、是否含中文字形。
        let mut families: BTreeMap<String, (String, BTreeSet<PathBuf>, bool)> = BTreeMap::new();
        for face in database.faces() {
            if face.style != fontdb::Style::Normal {
                continue;
            }
            let Some((family, _)) = face.families.first() else {
                continue;
            };
            let family = family.trim();
            if !is_visible_family(family) {
                continue;
            }

            let path = match &face.source {
                Source::File(path) | Source::SharedFile(path, _) => Some(path.clone()),
                Source::Binary(_) => None,
            };
            if let Some(path) = path {
                let normalized = family.to_lowercase();
                // 同一字体族可能有多个字重文件，任一带中文字形即视为该族支持中文。
                let entry = families
                    .entry(normalized)
                    .or_insert_with(|| (family.to_owned(), BTreeSet::new(), false));
                entry.2 |= face_covers_cjk(&database, face.id);
                entry.1.insert(path);
            }
        }

        let mut entries = families
            .into_values()
            .filter(|(_, paths, _)| !paths.is_empty())
            .collect::<Vec<_>>();
        entries.sort_by(|(left, _, _), (right, _, _)| {
            left.to_lowercase()
                .cmp(&right.to_lowercase())
                .then_with(|| left.cmp(right))
        });

        let mut choices = Vec::with_capacity(entries.len() + 1);
        choices.push(FontChoice::default_choice());
        let mut sources = HashMap::with_capacity(entries.len());
        for (family, paths, cjk) in entries {
            // 字体目录在进程生命周期内固定，泄漏少量名称可满足 iced 对静态族名的要求。
            let family: &'static str = Box::leak(family.into_boxed_str());
            choices.push(FontChoice {
                key: family,
                family: Some(family),
                cjk,
            });
            sources.insert(family, paths.into_iter().collect());
        }

        Self {
            choices: Arc::new(choices),
            sources: Arc::new(sources),
        }
    }

    /// 返回可供搜索框使用的全部选项。
    pub(crate) fn choices(&self) -> Vec<FontChoice> {
        self.choices.as_ref().clone()
    }

    /// 将保存值解析为当前机器真实存在的字体，缺失时回退默认字体。
    pub(crate) fn resolve(&self, key: &str) -> FontChoice {
        if key.is_empty() || key == DEFAULT_FONT_KEY {
            return FontChoice::default_choice();
        }
        self.choices
            .iter()
            .copied()
            .find(|choice| choice.key.eq_ignore_ascii_case(key))
            .unwrap_or_default()
    }

    /// 读取字体族关联的去重字体文件，供 iced 注册所有可用字重。
    ///
    /// 内置默认字体的字形数据编译在二进制里，直接返回即可，不需要读磁盘；
    /// 其余系统字体按族名查出字体文件路径后读取。
    pub(crate) fn load_family_bytes(&self, choice: FontChoice) -> Result<Vec<Vec<u8>>, String> {
        let Some(family) = choice.family else {
            // 三档字重的字节都在二进制里，这里按 iced 的注册接口逐份交出；
            // 用 to_vec 复制是接口要求（iced 需要取得所有权），单份 3.4 MB，
            // 仅在切换字体时发生一次。
            return Ok(BUNDLED_FONT_FILES.iter().map(|bytes| bytes.to_vec()).collect());
        };
        let Some(paths) = self.sources.get(family) else {
            return Err(tf("typing.font_missing", &[("family", &family)]));
        };

        paths
            .iter()
            .map(|path| {
                std::fs::read(path)
                    .map_err(|error| tf("typing.font_read_failed", &[("path", &path.display().to_string()), ("error", &error.to_string())]))
            })
            .collect()
    }
}

/// 判断字体族是否适合出现在用户选择器中。
///
/// 内置字体由启动器自己提供，不需要出现在系统字体列表里；以 `.` 开头的是
/// 系统隐藏字体。
fn is_visible_family(family: &str) -> bool {
    let family = family.trim();
    !family.is_empty()
        && !family.starts_with('.')
        && !family.eq_ignore_ascii_case(DEFAULT_FAMILY_NAME)
}

/// 判断某个字体 face 是否自带中文字形。
///
/// 只看一个代表性码位（`中`，U+4E2D）：字体对中日韩汉字的覆盖要么整体具备、
/// 要么整体缺失，不会出现「有『中』却没有『文』」的情况，因此逐个检查
/// 常用区间既慢又没有必要。
///
/// 读取失败（字体损坏、格式不支持）时按「不支持中文」处理：选择器上多打一个
/// 提醒标记无害，漏标才会让用户选到渲染不出中文的字体。
fn face_covers_cjk(database: &fontdb::Database, id: fontdb::ID) -> bool {
    const PROBE: char = '中';
    database
        .with_face_data(id, |data, index| {
            ttf_parser::Face::parse(data, index)
                .ok()
                .is_some_and(|face| face.glyph_index(PROBE).is_some())
        })
        .unwrap_or(false)
}

/// 一个覆盖中文的系统字体族，用作最后兜底。
///
/// 正常情况下界面用的是内置思源黑体，不会走到这里；只有内置字体加载失败、
/// 或用户显式改选系统字体时才会用到，用于保证中文绝不落到缺字方框。
///
/// 结果在进程内缓存：字体数据库查询要遍历全部已装字体，而该结果在一帧内会
/// 被每个文字组件读取，不能每帧重算。
fn cjk_fallback_family() -> Option<&'static str> {
    static CACHED: OnceLock<Option<&'static str>> = OnceLock::new();
    *CACHED.get_or_init(|| {
        let mut database = fontdb::Database::new();
        database.load_system_fonts();
        WINDOWS_CJK_FALLBACKS
            .iter()
            .copied()
            .find(|candidate| {
                database
                    .faces()
                    .any(|face| face.families.iter().any(|(name, _)| name == candidate))
            })
    })
}

thread_local! {
    /// 当前帧普通界面文字使用的字体族。
    static RENDER_FAMILY: Cell<Family> = const {
        Cell::new(Family::Name(DEFAULT_FAMILY_NAME))
    };
    /// 当前帧用户选择的额外界面缩放。
    static RENDER_SCALE: Cell<f32> = const { Cell::new(DEFAULT_UI_SCALE) };
}

/// 在构建当前帧前设置普通界面字体。
pub(crate) fn set_render_font(choice: FontChoice) {
    let family = match choice.family() {
        Some(family) => family,
        // 内置字体不可用时（例如目标机器上注册失败）退回系统 CJK 字体：
        // 宁可字重不一致，也不能让中文渲染成缺字方框。
        None => cjk_fallback_family().unwrap_or(DEFAULT_FAMILY_NAME),
    };
    RENDER_FAMILY.set(Family::Name(family));
}

/// 在构建当前帧前记录界面缩放，供响应式组件选择排版。
pub(crate) fn set_render_scale(scale: f32) {
    RENDER_SCALE.set(normalize_ui_scale(scale));
}

/// 当前帧使用的用户界面缩放。
pub(crate) fn current_ui_scale() -> f32 {
    RENDER_SCALE.get()
}

fn with_weight(weight: Weight) -> Font {
    RENDER_FAMILY.with(|family| Font {
        family: family.get(),
        weight,
        stretch: Stretch::Normal,
        style: Style::Normal,
    })
}

/// 普通字重（思源黑体 Regular，400）。
///
/// 对应 [`BUNDLED_FONT_FILES`] 里的 Regular 那一份；用户改选系统字体后，
/// 由 fontdb 在该族的全部 face 中挑最接近 400 的一个。
pub(crate) fn regular() -> Font {
    with_weight(Weight::Normal)
}

/// 中等字重（思源黑体 Medium，500）。
///
/// 界面里 187 处用它：标题、标签、数值都属于这一档。思源黑体有独立的
/// Medium 字面，中英文都是真正的 500，不会被吸附到 400。这一点是选它
/// 而不是系统雅黑的关键原因 —— 雅黑在这个字重上会被 fontdb 吸附。
pub(crate) fn medium() -> Font {
    with_weight(Weight::Medium)
}

/// 粗体字重（思源黑体 Bold，700）。
pub(crate) fn bold() -> Font {
    with_weight(Weight::Bold)
}

/// 将任意设置值限制到合法范围并吸附到 5% 档位。
pub(crate) fn normalize_ui_scale(value: f32) -> f32 {
    if !value.is_finite() {
        return DEFAULT_UI_SCALE;
    }
    let clamped = value.clamp(MIN_UI_SCALE, MAX_UI_SCALE);
    let steps = ((clamped - MIN_UI_SCALE) / UI_SCALE_STEP).round();
    (MIN_UI_SCALE + steps * UI_SCALE_STEP).clamp(MIN_UI_SCALE, MAX_UI_SCALE)
}

#[cfg(test)]
mod tests {
    use super::{
        BUNDLED_FONT_FILES, DEFAULT_FAMILY_NAME, DEFAULT_UI_SCALE, MAX_UI_SCALE, MIN_UI_SCALE,
        WINDOWS_CJK_FALLBACKS, cjk_fallback_family, is_visible_family, normalize_ui_scale,
    };

    /// 从字体的 name 表里取一个名称（优先英文记录）。
    ///
    /// 这里手写 name 表解析而不是引入额外依赖：需要校验的只有族名与子族名，
    /// 而 ttf-parser 的 name API 足够直接。
    ///
    /// 刻意先扫一遍英文记录再退回任意 Unicode 记录，而不是直接取第一条：
    /// 字体里同一个 nid 会有多语言版本（简体中文记录里族名写作「思源黑体」），
    /// 取第一条的结果取决于记录顺序。fontdb 自己也做了同样的「优先英文」处理，
    /// 这里对齐它才能真实反映运行时行为。
    fn name_record(face: &ttf_parser::Face<'_>, name_id: u16) -> Option<String> {
        const ENGLISH_UNITED_STATES: u16 = 0x0409;
        let names = face.names();
        let mut fallback = None;
        for index in 0..names.len() {
            let Some(name) = names.get(index) else {
                continue;
            };
            // 只认 Unicode 编码的记录：其余编码 to_string 会返回 None。
            if name.name_id != name_id || !name.is_unicode() {
                continue;
            }
            let Some(value) = name.to_string() else {
                continue;
            };
            if name.platform_id == ttf_parser::PlatformId::Windows
                && name.language_id == ENGLISH_UNITED_STATES
            {
                return Some(value);
            }
            fallback.get_or_insert(value);
        }
        fallback
    }

    /// 按字体文件在 [`BUNDLED_FONT_FILES`] 里的顺序解析出对应 face。
    ///
    /// 顺序就是字重顺序：0=Regular(400)、1=Medium(500)、2=Bold(700)。这个约定
    /// 由 [`BUNDLED_FONT_FILES`] 的声明顺序保证，测试直接依赖它。
    fn bundled_faces() -> Vec<ttf_parser::Face<'static>> {
        BUNDLED_FONT_FILES
            .iter()
            .map(|bytes| ttf_parser::Face::parse(bytes, 0).expect("内置字体必须能被解析"))
            .collect()
    }

    #[test]
    fn scale_is_clamped_and_snapped() {
        assert_eq!(normalize_ui_scale(0.1), MIN_UI_SCALE);
        assert_eq!(normalize_ui_scale(2.0), MAX_UI_SCALE);
        assert!((normalize_ui_scale(1.13) - 1.15).abs() < f32::EPSILON);
        assert_eq!(normalize_ui_scale(f32::NAN), DEFAULT_UI_SCALE);
    }

    #[test]
    fn hidden_and_empty_font_families_are_filtered() {
        assert!(!is_visible_family(""));
        assert!(!is_visible_family("  "));
        assert!(!is_visible_family(".AppleSystemUIFont"));
        // 内置字体不该出现在系统字体列表里，否则会和「默认」选项重复。
        assert!(!is_visible_family(DEFAULT_FAMILY_NAME));
        assert!(is_visible_family("Microsoft YaHei UI"));
    }

    /// 三档字重的文件数量与顺序必须和界面约定一致。
    ///
    /// 界面只调用 `regular()` / `medium()` / `bold()`，而 iced 靠字体文件里面的
    /// 字重信息分档。这份清单少一份，对应字重就会退回系统字体，中英文粗细又
    /// 会对不上 —— 而这在编译期是看不出来的。
    #[test]
    fn bundled_font_files_cover_the_three_ui_weights() {
        assert_eq!(
            BUNDLED_FONT_FILES.len(),
            3,
            "内置字体必须是 Regular / Medium / Bold 三份"
        );
        for (index, expected) in [(0usize, 400u16), (1, 500), (2, 700)] {
            let face = ttf_parser::Face::parse(BUNDLED_FONT_FILES[index], 0)
                .expect("内置字体必须能被解析");
            assert_eq!(
                face.weight().to_number(),
                expected,
                "第 {index} 份内置字体的字重应为 {expected}"
            );
        }
    }

    /// 三档字重必须聚成**同一个字体族**，否则 iced 只会匹配到其中一份。
    ///
    /// 原始发布文件的命名并不统一（部分字重把「族名 + 字重」写进 nid 1、把纯族
    /// 名写进 nid 16），是打包脚本显式改名后才对齐的。这条测试守住那个前提：
    /// 一旦有人手工替换了字体文件却忘了改名，这个断言会先失败。
    #[test]
    fn bundled_font_files_share_one_family_name() {
        for (index, face) in bundled_faces().iter().enumerate() {
            let family = name_record(face, 1).unwrap_or_else(|| panic!("第 {index} 份字体缺少族名"));
            assert_eq!(
                family, DEFAULT_FAMILY_NAME,
                "第 {index} 份字体的族名与 DEFAULT_FAMILY_NAME 不一致"
            );
            // nid 16 若存在也必须一致：fontdb 优先读它，两者不同就会分裂成两组。
            if let Some(typographic) = name_record(face, 16) {
                assert_eq!(
                    typographic, DEFAULT_FAMILY_NAME,
                    "第 {index} 份字体的排版族名（nid 16）与 DEFAULT_FAMILY_NAME 不一致"
                );
            }
        }
    }

    /// 内置字体必须同时覆盖中英文。
    ///
    /// 这是整个「中英文粗细一致」方案的地基：只要内置字体缺中文，中文就必然
    /// 回退到系统字体，两套设计的笔画粗细对不上，问题立刻复现。
    #[test]
    fn bundled_font_covers_both_scripts() {
        let samples = [
            ("中文", '中'),
            ("汉字", '汉'),
            ("拉丁大写", 'A'),
            ("拉丁小写", 'a'),
            ("数字", '8'),
            ("全角标点", '，'),
        ];
        // 三档都检查：某一档万一裁坏了，中文就只在那档字重里缺字。
        for (index, face) in bundled_faces().iter().enumerate() {
            for (name, ch) in samples {
                assert!(
                    face.glyph_index(ch).is_some(),
                    "第 {index} 份内置字体缺少{name}「{ch}」的字形"
                );
            }
        }
    }

    /// 收集字形轮廓的横向坐标，用于确认某个字符确有可绘制外形。
    struct WidthProbe(Vec<f32>);

    impl WidthProbe {
        fn span(&self) -> f32 {
            let min = self.0.iter().copied().fold(f32::INFINITY, f32::min);
            let max = self.0.iter().copied().fold(f32::NEG_INFINITY, f32::max);
            if min.is_finite() && max.is_finite() {
                max - min
            } else {
                0.0
            }
        }
    }

    impl ttf_parser::OutlineBuilder for WidthProbe {
        fn move_to(&mut self, x: f32, _y: f32) {
            self.0.push(x);
        }
        fn line_to(&mut self, x: f32, _y: f32) {
            self.0.push(x);
        }
        fn quad_to(&mut self, x1: f32, _y1: f32, x: f32, _y: f32) {
            self.0.push(x1);
            self.0.push(x);
        }
        fn curve_to(&mut self, x1: f32, _y1: f32, x2: f32, _y2: f32, x: f32, _y: f32) {
            self.0.push(x1);
            self.0.push(x2);
            self.0.push(x);
        }
        fn close(&mut self) {}
    }

    /// 界面用到的汉字必须真的有可绘制轮廓，而不是只有码位映射。
    ///
    /// 两者会脱节：字体可以声明某码位存在，却提供一个空字形，渲染出来是空白。
    /// 思源黑体是 CFF 轮廓，与 TrueType 的 `glyf` 走不同的解析路径，因此这条
    /// 测试同时也在验证 CFF 轮廓确实能被解析出来。
    #[test]
    fn bundled_cjk_glyph_has_drawable_outline() {
        for (index, face) in bundled_faces().iter().enumerate() {
            for ch in ['中', '汉', '文', '版', '本'] {
                let glyph = face
                    .glyph_index(ch)
                    .unwrap_or_else(|| panic!("第 {index} 份内置字体缺少『{ch}』的字形"));
                let mut probe = WidthProbe(Vec::new());
                assert!(
                    face.outline_glyph(glyph, &mut probe).is_some(),
                    "第 {index} 份内置字体的『{ch}』没有可绘制轮廓"
                );
                assert!(
                    probe.span() > 0.0,
                    "第 {index} 份内置字体的『{ch}』轮廓宽度为 0，渲染出来是空白"
                );
            }
        }
    }

    /// 系统里必须能找到一个可用的中文回退字体，否则内置字体加载失败时
    /// 中文会渲染成缺字方框。
    #[test]
    fn a_cjk_fallback_family_is_always_available() {
        let fallback = cjk_fallback_family();
        assert!(
            fallback.is_some(),
            "未找到任何中文回退字体，界面中文会显示为缺字方框"
        );
        assert!(WINDOWS_CJK_FALLBACKS.contains(&fallback.unwrap()));
    }
}
