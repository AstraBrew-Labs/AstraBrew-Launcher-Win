//! 页面内 Markdown 渲染的统一入口。
//!
//! iced 自带的 `markdown` 组件开箱即用，但有两处不适合直接用在启动器里：
//!
//! 1. 默认 viewer 的段落与标题用 `rich_text` 且宽度是 `Length::Shrink`，
//!    长文本会横向溢出而不是换行（聊天消息、Release 日志都是长文本）。
//! 2. 默认字号按基准的 2 倍生成 h1，放在弹窗或气泡里层级差过大。
//!
//! 这里集中提供「按可用宽度换行」的渲染策略与主题感知的渲染参数，
//! 调用方只需给出基准字号和链接点击要产生的消息。

use crate::lang::t;
use crate::lang::tf;
use iced::widget::{container, markdown, rich_text};
use iced::{Background, Border, Color, Element, Fill, Font, Padding, Theme};

use astra_ui::BLUE_600;

/// 按当前主题与基准字号生成 Markdown 渲染参数。
///
/// 正文颜色与代码块底色由 iced 的主题 Catalog 决定，这里只需要处理
/// 链接、内联代码配色，以及标题 / 代码块 / 段间距的层级关系。
pub fn settings(theme: &Theme, text_size: f32) -> markdown::Settings {
    let dark = crate::theme::is_dark(theme);
    let mut settings = markdown::Settings::with_text_size(
        text_size,
        markdown::Style {
            font: crate::core::typography::regular(),
            inline_code_highlight: markdown::Highlight {
                // 半透明叠加色在深浅主题下都能贴合所在底色。
                background: Background::Color(if dark {
                    Color::from_rgba(1.0, 1.0, 1.0, 0.10)
                } else {
                    Color::from_rgba(0.0, 0.0, 0.0, 0.06)
                }),
                border: Border {
                    radius: 4.0.into(),
                    ..Border::default()
                },
            },
            inline_code_padding: Padding {
                top: 1.0,
                right: 3.0,
                bottom: 1.0,
                left: 3.0,
            },
            inline_code_color: if dark {
                Color::from_rgb8(255, 138, 128)
            } else {
                Color::from_rgb8(196, 62, 62)
            },
            inline_code_font: Font::MONOSPACE,
            code_block_font: Font::MONOSPACE,
            link_color: BLUE_600,
        },
    );
    // 标题只按基准字号小幅递进，避免默认的 2 倍 h1 撑破气泡或弹窗版式。
    settings.h1_size = (text_size + 5.0).into();
    settings.h2_size = (text_size + 4.0).into();
    settings.h3_size = (text_size + 3.0).into();
    settings.h4_size = (text_size + 2.0).into();
    settings.h5_size = (text_size + 1.0).into();
    settings.h6_size = text_size.into();
    settings.code_size = (text_size - 1.0).into();
    settings.spacing = (text_size - 4.0).into();
    settings
}

/// 渲染一段已解析的 Markdown，链接点击映射为调用方给定的消息。
pub fn view<'a, Message: 'a>(
    items: &'a [markdown::Item],
    theme: &Theme,
    text_size: f32,
    on_link_click: impl Fn(markdown::Uri) -> Message + 'a,
) -> Element<'a, Message> {
    markdown::view_with(items, settings(theme, text_size), &WrappingViewer).map(on_link_click)
}

/// 用系统默认浏览器打开 Markdown 里的链接；未打开时返回可直接展示的失败原因。
///
/// 只放行 http/https，避免把聊天记录或更新日志里的本地文件、脚本协议交给系统处理。
pub fn open_link(url: &str) -> Result<(), String> {
    let trimmed = url.trim();
    let lowered = trimmed.to_ascii_lowercase();
    if !lowered.starts_with("http://") && !lowered.starts_with("https://") {
        return Err(t("markdown.only_http").to_owned());
    }
    crate::core::shell::open_target(trimmed).map_err(|error| tf("markdown.open_failed", &[("error", &error)]))
}

/// 让段落与标题按可用宽度换行的渲染策略。
///
/// 消息类型取 `markdown::Uri`，与 iced 默认 viewer 一致，调用方再用 `view`
/// 把它映射到自己的 `Message`，这样同一份策略可以给所有页面复用。
struct WrappingViewer;

impl<'a> markdown::Viewer<'a, markdown::Uri> for WrappingViewer {
    fn on_link_click(url: markdown::Uri) -> markdown::Uri {
        url
    }

    fn heading(
        &self,
        settings: markdown::Settings,
        level: &'a markdown::HeadingLevel,
        text: &'a markdown::Text,
        index: usize,
    ) -> Element<'a, markdown::Uri> {
        let size = match level {
            markdown::HeadingLevel::H1 => settings.h1_size,
            markdown::HeadingLevel::H2 => settings.h2_size,
            markdown::HeadingLevel::H3 => settings.h3_size,
            markdown::HeadingLevel::H4 => settings.h4_size,
            markdown::HeadingLevel::H5 => settings.h5_size,
            markdown::HeadingLevel::H6 => settings.h6_size,
        };
        container(
            rich_text(text.spans(settings.style))
                .on_link_click(Self::on_link_click)
                .width(Fill)
                .size(size),
        )
        // 与 iced 默认实现一致：非首个标题保留半行上间距，拉开与上一段的距离。
        .padding(Padding {
            top: if index > 0 {
                settings.text_size.0 / 2.0
            } else {
                0.0
            },
            ..Padding::default()
        })
        .into()
    }

    fn paragraph(
        &self,
        settings: markdown::Settings,
        text: &markdown::Text,
    ) -> Element<'a, markdown::Uri> {
        rich_text(text.spans(settings.style))
            .on_link_click(Self::on_link_click)
            .width(Fill)
            .size(settings.text_size)
            .into()
    }
}
