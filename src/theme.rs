//! 启动器浅色与深色语义主题。

use astra_ui::AlertKind;
use astra_ui::ButtonVariant;
use astra_ui::WARNING;
use iced::theme;
use iced::widget::overlay::menu;
use iced::widget::{button, column, container, pick_list, row, slider, space, stack, text_input};
use iced::{Background, Border, Color, Element, Length, Theme};
use lucide_icons::Icon;

use crate::core::settings::ThemeMode;

/// 根据用户选择和系统模式生成最终主题。
pub fn resolve(mode: ThemeMode, system: theme::Mode) -> Theme {
    let dark = match mode {
        ThemeMode::Light => false,
        ThemeMode::Dark => true,
        ThemeMode::System => system == theme::Mode::Dark,
    };
    if dark { dark_theme() } else { light_theme() }
}

pub fn light_theme() -> Theme {
    Theme::custom(
        "Astra Light",
        theme::Palette {
            background: Color::from_rgb8(245, 245, 245),
            text: Color::from_rgb8(24, 24, 27),
            primary: Color::from_rgb8(4, 133, 247),
            success: Color::from_rgb8(23, 201, 100),
            warning: Color::from_rgb8(245, 165, 36),
            danger: Color::from_rgb8(255, 56, 60),
        },
    )
}

pub fn dark_theme() -> Theme {
    Theme::custom(
        "Astra Dark",
        theme::Palette {
            background: Color::from_rgb8(18, 18, 20),
            text: Color::from_rgb8(244, 244, 245),
            primary: Color::from_rgb8(53, 146, 249),
            success: Color::from_rgb8(54, 211, 124),
            warning: Color::from_rgb8(250, 183, 55),
            danger: Color::from_rgb8(255, 105, 101),
        },
    )
}

pub fn is_dark(theme: &Theme) -> bool {
    theme.extended_palette().is_dark
}

pub fn canvas(theme: &Theme) -> Color {
    theme.palette().background
}

pub fn surface(theme: &Theme) -> Color {
    if is_dark(theme) {
        Color::from_rgb8(28, 28, 31)
    } else {
        Color::WHITE
    }
}

#[allow(dead_code)]
pub fn surface_alt(theme: &Theme) -> Color {
    if is_dark(theme) {
        Color::from_rgb8(39, 39, 43)
    } else {
        Color::from_rgb8(235, 235, 236)
    }
}

pub fn text(theme: &Theme) -> Color {
    theme.palette().text
}

#[allow(dead_code)]
pub fn text_muted(theme: &Theme) -> Color {
    if is_dark(theme) {
        Color::from_rgb8(178, 178, 186)
    } else {
        Color::from_rgb8(113, 113, 122)
    }
}

#[allow(dead_code)]
pub fn text_subtle(theme: &Theme) -> Color {
    if is_dark(theme) {
        Color::from_rgb8(126, 126, 136)
    } else {
        Color::from_rgb8(161, 161, 168)
    }
}

pub fn line(theme: &Theme) -> Color {
    if is_dark(theme) {
        Color::from_rgb8(63, 63, 70)
    } else {
        Color::from_rgb8(222, 222, 224)
    }
}

/// 普通文字样式，供页面中的固定文案使用。
pub fn text_style(theme: &Theme) -> iced::widget::text::Style {
    iced::widget::text::Style {
        color: Some(text(theme)),
    }
}

/// 弱化文字样式，保证深色模式下说明文字仍有足够对比度。
pub fn muted_text_style(theme: &Theme) -> iced::widget::text::Style {
    iced::widget::text::Style {
        color: Some(text_muted(theme)),
    }
}

/// 次要文字样式，供路径、元数据和禁用态使用。
#[allow(dead_code)]
pub fn subtle_text_style(theme: &Theme) -> iced::widget::text::Style {
    iced::widget::text::Style {
        color: Some(text_subtle(theme)),
    }
}

/// 警告文字样式，用于「当前选择可用但有副作用」这类提示。
///
/// 用固定的警告色而不是主题文字色：提示语与正文混排在同一列里，
/// 只有颜色能把它区分出来；深浅色主题下用的都是同一组语义色。
pub fn warning_text_style(_theme: &Theme) -> iced::widget::text::Style {
    iced::widget::text::Style {
        color: Some(Color::from_rgb8(245, 165, 36)),
    }
}

/// 主题感知的弱化图标，避免直接传入固定浅色 token。
pub fn muted_icon<'a, Message: 'a>(glyph: Icon, size: u32) -> Element<'a, Message> {
    let icon: iced::widget::Text<'a> = glyph.into();
    icon.size(size).style(muted_text_style).into()
}

/// 主题感知的次要图标，适用于路径、占位和禁用态。
pub fn subtle_icon<'a, Message: 'a>(glyph: Icon, size: u32) -> Element<'a, Message> {
    let icon: iced::widget::Text<'a> = glyph.into();
    icon.size(size).style(subtle_text_style).into()
}

/// 主题感知的强调色图标，适用于当前选中项。
pub fn primary_icon<'a, Message: 'a>(glyph: Icon, size: u32) -> Element<'a, Message> {
    let icon: iced::widget::Text<'a> = glyph.into();
    icon.size(size)
        .style(|theme| iced::widget::text::Style {
            color: Some(theme.palette().primary),
        })
        .into()
}

/// 主题感知开关，避免 Astra UI 默认开关使用固定浅色轨道和文字。
pub fn switch<'a, Message: Clone + 'a>(
    label: &'a str,
    toggled: bool,
    on_toggle: impl Fn(bool) -> Message + 'a,
) -> Element<'a, Message> {
    let track = container(space::horizontal())
        .width(40)
        .height(22)
        .style(move |theme| switch_track_style(theme, toggled));
    let thumb = container(space::horizontal())
        .width(18)
        .height(18)
        .style(switch_thumb_style);
    let offset = if toggled { 20.0 } else { 2.0 };
    let control = stack![
        track,
        row![space::horizontal().width(offset), thumb]
            .width(40)
            .height(22)
            .align_y(iced::Alignment::Center),
    ]
    .width(40)
    .height(22);

    button(
        row![
            control,
            iced::widget::text(label)
                .size(12)
                .font(crate::core::typography::regular())
                .style(text_style),
        ]
        .spacing(10)
        .align_y(iced::Alignment::Center),
    )
    .on_press(on_toggle(!toggled))
    .padding(0)
    .height(22)
    .style(|_theme, _status| button::Style::default())
    .into()
}

/// 主题感知提示框，替代 Astra UI 中固定浅色的 Alert。
pub fn alert<'a, Message: 'a>(
    title: &'a str,
    description: &'a str,
    kind: AlertKind,
) -> Element<'a, Message> {
    let icon = match kind {
        AlertKind::Info => Icon::Info,
        AlertKind::Success => Icon::CircleCheck,
        AlertKind::Warning => Icon::TriangleAlert,
        AlertKind::Danger => Icon::CircleX,
    };
    let accent = move |theme: &Theme| match kind {
        AlertKind::Info => theme.palette().primary,
        AlertKind::Success => theme.palette().success,
        AlertKind::Warning => theme.palette().warning,
        AlertKind::Danger => theme.palette().danger,
    };
    let indicator = container(iced::widget::Text::from(icon).size(17).style(move |theme| {
        iced::widget::text::Style {
            color: Some(accent(theme)),
        }
    }))
    .width(32)
    .height(32)
    .align_x(iced::Alignment::Center)
    .align_y(iced::Alignment::Center)
    .style(move |theme| container::Style {
        background: Some(Background::Color(Color::from_rgba(
            accent(theme).r,
            accent(theme).g,
            accent(theme).b,
            if is_dark(theme) { 0.18 } else { 0.10 },
        ))),
        border: Border {
            color: Color::from_rgba(accent(theme).r, accent(theme).g, accent(theme).b, 0.26),
            width: 1.0,
            radius: 8.0.into(),
        },
        ..container::Style::default()
    });
    container(
        row![
            indicator,
            column![
                crate::lang::raw(crate::lang::resolve(title))
                    .size(13)
                    .font(crate::core::typography::medium())
                    .style(move |theme| iced::widget::text::Style {
                        color: Some(accent(theme)),
                    }),
                crate::lang::raw(crate::lang::resolve(description))
                    .size(12)
                    .font(crate::core::typography::regular())
                    .style(muted_text_style),
            ]
            .spacing(2)
            .width(iced::Fill),
        ]
        .spacing(12)
        .align_y(iced::Alignment::Start),
    )
    .width(iced::Fill)
    .padding([12, 14])
    .style(move |theme| container::Style {
        background: Some(Background::Color(surface(theme))),
        border: Border {
            color: Color::from_rgba(accent(theme).r, accent(theme).g, accent(theme).b, 0.28),
            width: 1.0,
            radius: 10.0.into(),
        },
        text_color: Some(text(theme)),
        ..container::Style::default()
    })
    .into()
}

fn switch_track_style(theme: &Theme, toggled: bool) -> container::Style {
    let color = if toggled {
        theme.palette().primary
    } else {
        surface_alt(theme)
    };
    container::Style {
        background: Some(Background::Color(color)),
        border: Border {
            color: line(theme),
            width: 1.0,
            radius: 11.0.into(),
        },
        ..container::Style::default()
    }
}

fn switch_thumb_style(theme: &Theme) -> container::Style {
    container::Style {
        background: Some(Background::Color(if is_dark(theme) {
            Color::from_rgb8(232, 232, 236)
        } else {
            Color::WHITE
        })),
        border: Border {
            radius: 9.0.into(),
            ..Border::default()
        },
        ..container::Style::default()
    }
}

/// 使用当前动态字体渲染的扁平状态标签。
/// 小尺寸标签胶囊。`label` 是文案键，渲染时按当前语言取值。
pub fn flat_chip<'a, Message: 'a>(label: &'static str, color: Color) -> Element<'a, Message> {
    chip(crate::lang::text(label), color)
}

/// 与 [`flat_chip`] 同款样式的胶囊，用于版本号、路径等运行时数据（不参与翻译）。
pub fn flat_chip_raw<'a, Message: 'a>(
    label: impl Into<String>,
    color: Color,
) -> Element<'a, Message> {
    chip(crate::lang::raw(label), color)
}

/// 胶囊外观；两入口只在文本来源（键 / 运行时数据）上不同。
fn chip<'a, Message: 'a>(content: iced::widget::Text<'a>, color: Color) -> Element<'a, Message> {
    container(
        content
            .size(11)
            .font(crate::core::typography::medium())
            .color(color),
    )
    .height(24)
    .padding([2, 8])
    .align_x(iced::Alignment::Center)
    .align_y(iced::Alignment::Center)
    .style(move |theme| container::Style {
        background: Some(Background::Color(Color::from_rgba(
            color.r,
            color.g,
            color.b,
            if is_dark(theme) { 0.18 } else { 0.10 },
        ))),
        border: Border {
            color: Color::from_rgba(color.r, color.g, color.b, 0.22),
            width: 1.0,
            radius: 999.0.into(),
        },
        ..container::Style::default()
    })
    .into()
}

/// 左上角（侧边栏 logo 上方）的「测试版」标记胶囊。
pub fn beta_badge<'a, Message: 'a>() -> Element<'a, Message> {
    chip_sized(crate::lang::text("app.build.beta"), WARNING, 18.0, 10.0)
}

/// 按指定高度 / 字号构造胶囊；[`flat_chip`] 与 [`beta_badge`] 共用同一套外观。
fn chip_sized<'a, Message: 'a>(
    content: iced::widget::Text<'a>,
    color: Color,
    height: f32,
    size: f32,
) -> Element<'a, Message> {
    container(
        content
            .size(size)
            .font(crate::core::typography::medium())
            .color(color),
    )
    .height(height)
    .padding([0, 7])
    .align_x(iced::Alignment::Center)
    .align_y(iced::Alignment::Center)
    .style(move |theme| container::Style {
        background: Some(Background::Color(Color::from_rgba(
            color.r,
            color.g,
            color.b,
            if is_dark(theme) { 0.20 } else { 0.14 },
        ))),
        border: Border {
            color: Color::from_rgba(color.r, color.g, color.b, 0.30),
            width: 1.0,
            radius: 999.0.into(),
        },
        ..container::Style::default()
    })
    .into()
}

/// 主题感知的一像素分隔线。
pub fn separator_style(theme: &Theme) -> iced::widget::rule::Style {
    iced::widget::rule::Style {
        color: line(theme),
        radius: 1.0.into(),
        fill_mode: iced::widget::rule::FillMode::Full,
        snap: true,
    }
}

/// 主题感知的横向分隔线。
pub fn separator<'a, Message: 'a>() -> Element<'a, Message> {
    iced::widget::rule::horizontal(1.0)
        .style(separator_style)
        .into()
}

/// 根画布样式，随最终解析出的 Astra 主题变化。
pub fn canvas_style(theme: &Theme) -> container::Style {
    container::Style {
        background: Some(Background::Color(canvas(theme))),
        text_color: Some(text(theme)),
        ..container::Style::default()
    }
}

/// 侧栏样式，避免沿用 Astra UI 固定的浅色背景。
pub fn sidebar_style(theme: &Theme) -> container::Style {
    container::Style {
        background: Some(Background::Color(surface(theme))),
        text_color: Some(text(theme)),
        border: Border {
            color: line(theme),
            width: 1.0,
            ..Border::default()
        },
        ..container::Style::default()
    }
}

/// 分段选择器外框，使用当前主题的表面和边框色。
pub fn segmented_group_style(theme: &Theme) -> container::Style {
    container::Style {
        background: Some(Background::Color(surface_alt(theme))),
        border: Border {
            color: line(theme),
            width: 1.0,
            radius: 10.0.into(),
        },
        ..container::Style::default()
    }
}

/// 主题感知卡片，覆盖 Astra UI 默认固定浅色卡片。
pub fn card<'a, Message: 'a>(
    content: impl Into<Element<'a, Message>>,
    width: impl Into<Length>,
    padding: u16,
) -> Element<'a, Message> {
    container(content)
        .width(width)
        .padding(padding)
        .style(card_style)
        .into()
}

fn card_style(theme: &Theme) -> container::Style {
    container::Style {
        background: Some(Background::Color(surface(theme))),
        text_color: Some(text(theme)),
        border: Border {
            color: line(theme),
            width: 1.0,
            radius: 12.0.into(),
        },
        ..container::Style::default()
    }
}

/// 主题感知按钮样式，替代 Astra UI 固定浅色按钮。
pub fn button_style(variant: ButtonVariant) -> impl Fn(&Theme, button::Status) -> button::Style {
    move |theme, status| {
        let hovered = matches!(status, button::Status::Hovered | button::Status::Pressed);
        let disabled = matches!(status, button::Status::Disabled);
        let palette = theme.palette();
        let (background, text_color, outlined) = match variant {
            ButtonVariant::Primary => (
                Some(if hovered {
                    lighten(palette.primary, 0.08)
                } else {
                    palette.primary
                }),
                Color::WHITE,
                false,
            ),
            ButtonVariant::Secondary => (
                Some(if hovered {
                    lighten(surface_alt(theme), 0.06)
                } else {
                    surface_alt(theme)
                }),
                palette.primary,
                false,
            ),
            ButtonVariant::Tertiary => (
                Some(if hovered {
                    lighten(surface_alt(theme), 0.06)
                } else {
                    surface_alt(theme)
                }),
                text(theme),
                false,
            ),
            ButtonVariant::Ghost => (hovered.then(|| surface_alt(theme)), text(theme), false),
            ButtonVariant::Destructive => (
                Some(if hovered {
                    lighten(palette.danger, 0.06)
                } else {
                    palette.danger
                }),
                Color::WHITE,
                false,
            ),
            ButtonVariant::DangerSoft => (
                Some(Color::from_rgba(
                    palette.danger.r,
                    palette.danger.g,
                    palette.danger.b,
                    if hovered { 0.24 } else { 0.16 },
                )),
                palette.danger,
                false,
            ),
            ButtonVariant::Outline => (None, text(theme), true),
        };

        button::Style {
            background: background.map(|color| {
                if disabled {
                    Background::Color(Color::from_rgba(color.r, color.g, color.b, color.a * 0.5))
                } else {
                    Background::Color(color)
                }
            }),
            text_color: if disabled {
                Color::from_rgba(text_color.r, text_color.g, text_color.b, 0.5)
            } else {
                text_color
            },
            border: Border {
                color: if outlined {
                    if disabled {
                        Color::from_rgba(line(theme).r, line(theme).g, line(theme).b, 0.55)
                    } else if hovered {
                        palette.primary
                    } else {
                        line(theme)
                    }
                } else {
                    Color::TRANSPARENT
                },
                width: if outlined { 1.0 } else { 0.0 },
                radius: 10.0.into(),
            },
            ..button::Style::default()
        }
    }
}

/// 主题感知输入框样式。
pub fn text_input_style(theme: &Theme, status: text_input::Status) -> text_input::Style {
    let focused = matches!(status, text_input::Status::Focused { .. });
    let hovered = matches!(
        status,
        text_input::Status::Hovered | text_input::Status::Focused { is_hovered: true }
    );
    let disabled = matches!(status, text_input::Status::Disabled);
    let palette = theme.palette();
    text_input::Style {
        background: Background::Color(if disabled {
            surface_alt(theme)
        } else {
            surface(theme)
        }),
        border: Border {
            color: if focused {
                palette.primary
            } else if hovered {
                lighten(palette.primary, 0.08)
            } else {
                line(theme)
            },
            width: if focused { 2.0 } else { 1.0 },
            radius: 10.0.into(),
        },
        icon: palette.primary,
        placeholder: text_subtle(theme),
        value: text(theme),
        selection: Color::from_rgba(
            palette.primary.r,
            palette.primary.g,
            palette.primary.b,
            0.25,
        ),
    }
}

/// 主题感知滑块样式。
pub fn slider_style(theme: &Theme, status: slider::Status) -> slider::Style {
    let primary = match status {
        slider::Status::Active => theme.palette().primary,
        slider::Status::Hovered => lighten(theme.palette().primary, 0.08),
        slider::Status::Dragged => lighten(theme.palette().primary, 0.14),
    };
    slider::Style {
        rail: slider::Rail {
            backgrounds: (
                Background::Color(primary),
                Background::Color(surface_alt(theme)),
            ),
            width: 5.0,
            border: Border {
                color: line(theme),
                width: 1.0,
                radius: 3.0.into(),
            },
        },
        handle: slider::Handle {
            shape: slider::HandleShape::Circle { radius: 8.0 },
            background: Background::Color(surface(theme)),
            border_width: 2.0,
            border_color: primary,
        },
    }
}

/// 主题感知下拉框样式。
pub fn pick_list_style(theme: &Theme, status: pick_list::Status) -> pick_list::Style {
    let hovered = matches!(status, pick_list::Status::Hovered);
    let opened = matches!(status, pick_list::Status::Opened { .. });
    let palette = theme.palette();
    pick_list::Style {
        text_color: text(theme),
        placeholder_color: text_subtle(theme),
        handle_color: if opened {
            palette.primary
        } else {
            text_muted(theme)
        },
        background: Background::Color(surface(theme)),
        border: Border {
            color: if opened {
                palette.primary
            } else if hovered {
                lighten(palette.primary, 0.08)
            } else {
                line(theme)
            },
            width: if opened { 2.0 } else { 1.0 },
            radius: 10.0.into(),
        },
    }
}

/// 主题感知下拉菜单样式。
pub fn pick_list_menu_style(theme: &Theme) -> menu::Style {
    let palette = theme.palette();
    menu::Style {
        background: Background::Color(surface(theme)),
        border: Border {
            color: line(theme),
            width: 1.0,
            radius: 10.0.into(),
        },
        text_color: text(theme),
        selected_text_color: palette.primary,
        selected_background: Background::Color(Color::from_rgba(
            palette.primary.r,
            palette.primary.g,
            palette.primary.b,
            0.16,
        )),
        shadow: iced::Shadow {
            color: Color::from_rgba(0.0, 0.0, 0.0, 0.18),
            offset: iced::Vector::new(0.0, 4.0),
            blur_radius: 12.0,
        },
    }
}

fn lighten(color: Color, amount: f32) -> Color {
    Color::from_rgba(
        color.r + (1.0 - color.r) * amount,
        color.g + (1.0 - color.g) * amount,
        color.b + (1.0 - color.b) * amount,
        color.a,
    )
}
