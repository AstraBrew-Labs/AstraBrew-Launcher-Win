//! 主界面左侧导航栏。
//!
//! 结构（自上而下）：Logo 品牌区 → 分割线 → 主功能导航组 →
//! 弹性留白 → 分割线 → 底部导航组。
//! 导航按钮采用「顶部图标 + 底部文字」布局，选中态使用蓝色强调高亮。

use iced::widget::{button, column, container, image, space};
use iced::{
    Alignment, Background, Border, Color, ContentFit, Element, Fill, Length, Theme,
};

use crate::app::Message;
use crate::lang::{raw, text};
use crate::pages::Page;
use crate::pages::versions::VersionState;

/// 侧边栏固定宽度（像素）。内容区宽度 = SIDEBAR_WIDTH - 左右内边距（各 12），
/// 恰好容纳 72px 的正方形导航按钮。
const SIDEBAR_WIDTH: f32 = 96.0;

/// 导航按钮圆角（与 astra_ui 的 RADIUS_FIELD 保持一致）。
const NAV_RADIUS: f32 = 12.0;

/// 导航按钮边长（像素）。宽高相等，构成正方形，图标在上、文字在下。
const NAV_ITEM_SIZE: f32 = 72.0;

/// 主功能导航项（上方组，位于第一道与第二道分割线之间）。
const PRIMARY_PAGES: [Page; 5] = [
    Page::Home,
    Page::TavernConfig,
    Page::Version,
    Page::Extensions,
    Page::Resources,
];

/// 底部导航项（下方组，固定在侧边栏底部）。
const SECONDARY_PAGES: [Page; 2] = [Page::Console, Page::Settings];

/// 渲染主界面左侧导航栏。
pub fn sidebar<'a>(page: Page, versions: &'a VersionState) -> Element<'a, Message> {
    let scale = crate::core::typography::current_ui_scale().max(1.0);
    // 侧边栏导航采用反向尺寸补偿：界面整体放大时缩小导航的逻辑尺寸，
    // 使其屏幕视觉尺寸基本稳定，并保证 150% 下“设置”入口仍然可见。
    let sidebar_width = (SIDEBAR_WIDTH / scale).clamp(64.0, SIDEBAR_WIDTH);
    let horizontal_padding = (12.0 / scale).clamp(8.0, 12.0);
    let vertical_padding = (16.0 / scale).clamp(8.0, 16.0);
    let nav_item_size = (NAV_ITEM_SIZE / scale).clamp(48.0, NAV_ITEM_SIZE);
    let nav_spacing = (4.0 / scale).clamp(2.0, 4.0);
    let section_spacing = (10.0 / scale).clamp(4.0, 10.0);

    let primary = PRIMARY_PAGES
        .iter()
        .map(|&item| nav_button(item, page, scale, nav_item_size));
    let secondary = SECONDARY_PAGES
        .iter()
        .map(|&item| nav_button(item, page, scale, nav_item_size));

    // 测试版标记位于左上角：侧边栏最顶端、logo 之上。
    let mut content = column![];
    if crate::build_info::build_channel().is_beta() {
        content = content.push(
            container(crate::theme::beta_badge())
                .width(Fill)
                .align_x(Alignment::Start),
        );
    }
    content = content.push(logo_section(versions, scale));
    content = content.push(crate::theme::separator());
    content = content.push(
        column(primary)
            .spacing(nav_spacing)
            .align_x(Alignment::Center)
            .width(Fill),
    );
    content = content.push(space::vertical());
    content = content.push(crate::theme::separator());
    content = content.push(
        column(secondary)
            .spacing(nav_spacing)
            .align_x(Alignment::Center)
            .width(Fill),
    );

    let content = content.spacing(section_spacing).height(Fill).width(Fill);

    container(content)
        .width(sidebar_width)
        .height(Fill)
        .padding(iced::Padding {
            top: vertical_padding,
            right: horizontal_padding,
            bottom: vertical_padding,
            left: horizontal_padding,
        })
        .style(crate::theme::sidebar_style)
        .into()
}

/// 品牌 Logo（`icon_eframe.png`，512×512）。
///
/// 图案自带一块居中的白色圆角底板（约占画布 80%，角半径约 23%），四周是透明边距，
/// 因此品牌位不必再垫底色。编译期内嵌而非按路径加载：安装到
/// `%LOCALAPPDATA%\AstraBrew Launcher\` 后工作目录不再是项目根目录，相对路径会失效。
const LOGO_BYTES: &[u8] = include_bytes!("../assets/icon/icon_eframe.png");

/// 品牌 Logo 的图像句柄，进程级只构造一次。
///
/// `image::Handle::from_bytes` 每次调用都会分配**新的唯一 id**，
/// 若在 `view()` 里现建句柄，渲染器会把每一帧都当成新图并重新上传纹理。
/// 因此这里缓存句柄，之后每帧仅做一次廉价克隆，纹理得以复用。
fn logo_handle() -> iced::widget::image::Handle {
    static LOGO: std::sync::OnceLock<iced::widget::image::Handle> = std::sync::OnceLock::new();
    LOGO.get_or_init(|| iced::widget::image::Handle::from_bytes(LOGO_BYTES))
        .clone()
}

/// 品牌 Logo 的显示边长（像素）。
///
/// 沿用原先头像位的 40 / 32，保证侧栏首屏高度与分割线位置完全不变。
fn logo_size(scale: f32) -> f32 {
    if scale >= 1.25 { 32.0 } else { 40.0 }
}

/// Logo 区会同步展示当前酒馆版本及实例来源。
/// 本地实例使用绿色，在线实例使用蓝色，未选择时仅保留中性占位信息。
fn logo_section<'a>(versions: &'a VersionState, scale: f32) -> Element<'a, Message> {
    let version_info: Element<'a, Message> =
        match (versions.current_version.as_deref(), versions.current_source) {
            (Some(version), Some(source)) => raw(format!(
                "{version} - {}",
                crate::lang::t(source.label_key())
            ))
            .size((10.0 / scale).clamp(8.0, 10.0))
            .font(crate::core::typography::regular())
            .style(move |_theme| iced::widget::text::Style {
                color: Some(source.color()),
            })
            .into(),
            _ => text("app.sidebar.version_placeholder")
                .size((10.0 / scale).clamp(8.0, 10.0))
                .font(crate::core::typography::regular())
                .style(crate::theme::subtle_text_style)
                .into(),
        };

    column![
        // 品牌位展示 logo 图片（原为 lucide 图标占位）。
        // 图片自带白色圆角底板，因此不再叠加 Avatar 的强调色底：
        // 否则会变成「蓝色圆角块外套白色圆角块」的双层轮廓。
        image(logo_handle())
            .width(Length::Fixed(logo_size(scale)))
            .height(Length::Fixed(logo_size(scale)))
            .content_fit(ContentFit::Contain),
        version_info,
    ]
    .spacing(6)
    .align_x(Alignment::Center)
    .width(Fill)
    .into()
}

/// 渲染单个导航按钮：顶部图标 + 底部文字，选中态蓝色高亮。
///
/// iced 的 `button` 不会自动居中内容，因此将图标与文字的列包在一个
/// `Fill` 且水平/垂直居中的 `container` 中，使内容在正方形按钮内居中。
fn nav_button(
    page: Page,
    current: Page,
    scale: f32,
    item_size: f32,
) -> Element<'static, Message> {
    let active = page == current;
    let icon_size = ((22.0 / scale).round() as u32).clamp(15, 22);
    let text_size = (11.0 / scale).clamp(8.0, 11.0);
    let content_spacing = (6.0 / scale).clamp(2.0, 6.0);

    button(
        container(
            column![
                if active {
                    crate::theme::primary_icon(page.icon(), icon_size)
                } else {
                    crate::theme::muted_icon(page.icon(), icon_size)
                },
                text(page.title())
                    .size(text_size)
                    .font(crate::core::typography::medium())
                    .style(move |theme| iced::widget::text::Style {
                        color: Some(if active {
                            theme.palette().primary
                        } else {
                            crate::theme::text_muted(theme)
                        }),
                    }),
            ]
            .spacing(content_spacing)
            .align_x(Alignment::Center),
        )
        .width(Fill)
        .height(Fill)
        .align_x(Alignment::Center)
        .align_y(Alignment::Center),
    )
    .width(item_size)
    .height(item_size)
    .padding(0)
    .on_press(Message::Navigate(page))
    .style(nav_item_style(active))
    .into()
}

/// 导航按钮样式：选中态蓝色 tint 背景，悬停态浅蓝 tint，默认透明。
fn nav_item_style(active: bool) -> impl Fn(&Theme, button::Status) -> button::Style {
    move |theme, status| {
        let hovered = matches!(status, button::Status::Hovered);
        let background = if active {
            Some(Background::Color(tint(theme.palette().primary, 0.16)))
        } else if hovered {
            Some(Background::Color(tint(theme.palette().primary, 0.10)))
        } else {
            None
        };

        button::Style {
            background,
            text_color: if active {
                theme.palette().primary
            } else {
                crate::theme::text_muted(theme)
            },
            border: Border {
                radius: NAV_RADIUS.into(),
                ..Border::default()
            },
            ..button::Style::default()
        }
    }
}

/// 以指定透明度构造一个颜色，用于生成蓝色 tint 背景。
fn tint(color: Color, alpha: f32) -> Color {
    Color::from_rgba(color.r, color.g, color.b, alpha)
}
