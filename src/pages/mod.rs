//! 主界面各功能页面的路由枚举与视图分发。
//!
//! 设置页和酒馆配置页已经接入真实视图，其余页面暂时提供居中的占位内容。

use iced::widget::{button, column, container, image, mouse_area, row, scrollable, space, stack};
use iced::{Alignment, Background, Border, Color, ContentFit, Element, Fill, Length, Padding, Theme};
use lucide_icons::Icon;

use astra_ui::{
    BLUE_600, ButtonVariant, DANGER, INK_MUTED, SUCCESS, ToggleButtonGroupItem, WHITE, icons,
};

use crate::app::Message;
use crate::lang::{raw, t, text};
use crate::theme::button_style;
pub(crate) mod console;
pub(crate) mod extensions;
pub(crate) mod markdown_doc;
pub(crate) mod notice;
pub(crate) mod pager;
pub(crate) mod resource_manage;
pub(crate) mod settings;
pub(crate) mod tavern;
pub(crate) mod versions;

use self::console::{ConsoleState, ConsoleStatus, console_view};
use self::extensions::{ExtensionsState, extensions_view};
use self::resource_manage::{ResourceManageState, resource_manage_view};
use self::settings::{
    EnvironmentDependency, QuickStartMode, ServerServiceMode, SettingsState, settings_view,
};
use self::tavern::{BrowserType, TavernState, tavern_view};
use self::versions::{DependencyStatus, VersionSource, VersionState, versions_view};

const HOME_HERO_HEIGHT: f32 = 240.0;

/// 主页版本快捷菜单中的一个可切换实例。
///
/// 这里保存完整路径而不是只保存版本号，因为本地可以同时存在多个相同版本的实例。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HomeTavernVersion {
    pub version: String,
    pub source: VersionSource,
    pub path: String,
}

/// 主界面导航页面。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Page {
    /// 主页
    Home,
    /// 酒馆配置
    TavernConfig,
    /// 版本管理
    Version,
    /// 扩展管理
    Extensions,
    /// 资源管理
    Resources,
    /// 控制台
    Console,
    /// 设置
    Settings,
}

impl Page {
    /// 页面在导航栏中的中文标题。
    pub const fn title(self) -> &'static str {
        match self {
            Page::Home => "app.home.title",
            Page::TavernConfig => "tavern.title",
            Page::Version => "nav.version",
            Page::Extensions => "extensions.title",
            Page::Resources => "resources.title",
            Page::Console => "app.quick.console.title",
            Page::Settings => "settings.title",
        }
    }

    /// 页面在导航栏中对应的 Lucide 图标。
    pub const fn icon(self) -> Icon {
        match self {
            Page::Home => Icon::House,
            Page::TavernConfig => Icon::SlidersHorizontal,
            Page::Version => Icon::GitBranch,
            Page::Extensions => Icon::Puzzle,
            Page::Resources => Icon::FolderOpen,
            Page::Console => Icon::SquareTerminal,
            Page::Settings => Icon::Settings,
        }
    }
}

/// 渲染指定页面的内容。
///
/// 设置页和酒馆配置页分发到真实视图，其余页面居中显示开发中说明。
pub fn page_view<'a>(
    page: Page,
    theme: &Theme,
    settings: &'a SettingsState,
    tavern: &'a TavernState,
    versions: &'a VersionState,
    home_version_selector_open: bool,
    extensions: &'a ExtensionsState,
    resources: &'a ResourceManageState,
    console: &'a ConsoleState,
) -> Element<'a, Message> {
    match page {
        Page::Home => home_view(
            settings,
            tavern,
            versions,
            console,
            home_version_selector_open,
        ),
        Page::Settings => settings_view(settings, console.status.is_transitioning() || console.is_running()),
        Page::TavernConfig => tavern_view(tavern).map(Message::Tavern),
        Page::Version => versions_view(versions, theme).map(Message::Version),
        Page::Extensions => extensions_view(extensions).map(Message::Extensions),
        Page::Resources => resource_manage_view(resources, theme).map(Message::Resources),
        Page::Console => console_view(console),
    }
}

/// 主页：用启动入口、环境摘要和运行配置把常用操作集中在首屏。
fn home_view<'a>(
    state: &'a SettingsState,
    tavern: &'a TavernState,
    versions: &'a VersionState,
    console: &'a ConsoleState,
    home_version_selector_open: bool,
) -> Element<'a, Message> {
    let mode_controls_locked = console.status.is_transitioning() || console.is_running();
    let (launch_label, launch_icon, launch_color) = match console.status {
        ConsoleStatus::Running => ("app.quick.stop_now", Icon::Square, DANGER),
        ConsoleStatus::Starting => ("app.quick.starting", Icon::Loader, WHITE),
        ConsoleStatus::Stopping => ("app.quick.stopping", Icon::Loader, WHITE),
        ConsoleStatus::NotStarted | ConsoleStatus::Stopped | ConsoleStatus::Failed => {
            ("app.quick.start", Icon::Play, WHITE)
        }
    };

    let hero = crate::theme::card(
        image("assets/imgs/og.png")
            .width(Fill)
            .height(Length::Fixed(HOME_HERO_HEIGHT))
            .content_fit(ContentFit::Cover),
        Fill,
        0,
    );

    let environment = crate::theme::card(
        column![
            row![
                column![
                    text("app.home.environment").size(16).font(crate::core::typography::medium()),
                    text("app.home.subtitle")
                        .size(11)
                        .font(crate::core::typography::regular())
                        .style(crate::theme::muted_text_style),
                ]
                .spacing(4),
                space::horizontal(),
                environment_status_badge(state),
            ]
            .align_y(Alignment::Center),
            container(
                row![
                    info_item_owned(Icon::GitBranch, "Git", environment_version(active_env(state, EnvironmentDependency::Git))),
                    info_item_owned(Icon::Hexagon, "Node.js", environment_version(active_env(state, EnvironmentDependency::NodeJs))),
                    current_tavern_info_item(versions),
                    info_item(Icon::Rocket, "app.quick.start_mode", current_quick_mode(state).label_key()),
                ]
                .spacing(10),
            )
            .width(Fill)
            .padding([14, 16])
            .style(info_surface),
        ]
        .spacing(16),
        Fill,
        20,
    );

    let version_select = home_version_selector(
        versions,
        home_version_selector_open,
        mode_controls_locked,
    );

    let selected_mode = current_quick_mode(state);
    let mode_items = [
        (QuickStartMode::Normal, Icon::Play),
        (QuickStartMode::Desktop, Icon::AppWindow),
        (QuickStartMode::Server, Icon::Server),
    ]
    .into_iter()
    .map(|(mode, icon)| {
        ToggleButtonGroupItem::new(Some(mode.label_key()), Some(icon), mode == selected_mode)
    })
    .collect();
    let mode_select = column![
        text("app.quick.start_mode")
            .size(11)
            .font(crate::core::typography::medium())
            .style(crate::theme::muted_text_style),
        themed_segmented_group_enabled(mode_items, !mode_controls_locked, |index| {
            Message::SettingsLaunchModeSelected(quick_mode_from_index(index))
        }),
    ]
    .spacing(6);

    let browser_select: Option<Element<'_, Message>> = (selected_mode == QuickStartMode::Normal)
        .then(|| {
            let browser_items = [
                (BrowserType::System, Icon::Globe),
                (BrowserType::Chrome, Icon::Monitor),
                (BrowserType::Firefox, Icon::Compass),
                (BrowserType::Edge, Icon::PanelsTopLeft),
            ]
            .into_iter()
            .map(|(browser, icon)| {
                ToggleButtonGroupItem::new(
                    Some(browser.label_key()),
                    Some(icon),
                    browser == tavern.browser_type(),
                )
            })
            .collect();

            column![
                text("app.quick.browser")
                    .size(11)
                    .font(crate::core::typography::medium())
                    .style(crate::theme::muted_text_style),
                themed_segmented_group_enabled(browser_items, !mode_controls_locked, |index| {
                    Message::HomeBrowserSelected(browser_type_from_index(index))
                }),
            ]
            .spacing(6)
            .into()
        });

    let service_mode_select: Option<Element<'_, Message>> = state.server_mode_enabled.then(|| {
        let items = [
            (ServerServiceMode::Lan, "settings.service_mode.lan", Icon::Wifi),
            (ServerServiceMode::Internet, "settings.service_mode.internet", Icon::Globe),
        ]
        .into_iter()
        .map(|(mode, label, icon)| {
            ToggleButtonGroupItem::new(Some(label), Some(icon), mode == state.server_service_mode)
        })
        .collect();

        column![
            text("app.quick.service_mode")
                .size(11)
                .font(crate::core::typography::medium())
                .style(crate::theme::muted_text_style),
            themed_segmented_group_enabled(items, !mode_controls_locked, |index| {
                Message::SettingsServerServiceModeSelected(server_service_mode_from_index(index))
            }),
        ]
        .spacing(6)
        .into()
    });

    let launch_button = button(
        row![
            icons::icon(launch_icon, 17, launch_color),
            text(launch_label)
                .size(13)
                .font(crate::core::typography::medium())
                .color(launch_color),
        ]
        .spacing(8)
        .align_y(Alignment::Center),
    )
    .height(42)
    .padding([10, 18])
    .style(button_style(if console.status == ConsoleStatus::Running {
        ButtonVariant::DangerSoft
    } else {
        ButtonVariant::Primary
    }));
    let launch_button = if console.status.is_transitioning() {
        launch_button
    } else {
        launch_button.on_press(Message::LaunchTavern)
    };

    // 放大到 125% 及以上时，固定窗口的逻辑可用宽度会缩小；
    // 将启动控件改为多行，避免浏览器和服务模式被裁切。
    let launch_content: Element<'_, Message> = if state.ui_scale >= 1.25 {
        let mut controls = column![
            row![version_select, mode_select]
                .spacing(16)
                .align_y(Alignment::Center)
        ]
        .spacing(12)
        .width(Fill);
        if let Some(browser_select) = browser_select {
            controls = controls.push(browser_select);
        }
        if let Some(service_mode_select) = service_mode_select {
            controls = controls.push(service_mode_select);
        }
        controls.push(launch_button.width(Fill)).into()
    } else {
        let mut controls = row![version_select, mode_select]
            .spacing(16)
            .align_y(Alignment::Center);
        if let Some(browser_select) = browser_select {
            controls = controls.push(browser_select);
        }
        if let Some(service_mode_select) = service_mode_select {
            controls = controls.push(service_mode_select);
        }
        controls
            .push(space::horizontal())
            .push(launch_button)
            .into()
    };
    let launch_panel = crate::theme::card(launch_content, Fill, 18);

    let home_content = container(
        column![
            scrollable(
                column![
                    column![
                        text("app.home.title").size(25).font(crate::core::typography::medium()),
                        text("AstraBrew Launcher")
                            .size(12)
                            .font(crate::core::typography::regular())
                            .style(crate::theme::muted_text_style),
                    ]
                    .spacing(4),
                    hero,
                    environment,
                ]
                .spacing(18)
                .width(Fill),
            )
            .width(Fill)
            .height(Fill),
            launch_panel,
        ]
        .spacing(14)
        .width(Fill)
        .height(Fill),
    )
    .width(Fill)
    .height(Fill)
    .padding([26, 30])
    .style(crate::theme::canvas_style);

    if home_version_selector_open {
        // 根 Stack 的第一层决定主页尺寸；菜单放在第二层并向上浮动，
        // 不会把启动面板或上方内容向外推开。
        stack![
            home_content,
            // 全屏透明点击层负责消费菜单外的点击，避免事件穿透到主页控件。
            mouse_area(container(space::horizontal()).width(Fill).height(Fill))
                .on_press(Message::HomeTavernVersionSelectorClosed),
            container(home_version_dropdown(versions, mode_controls_locked))
                .width(Fill)
                .height(Fill)
                .padding(Padding {
                    top: 0.0,
                    right: 0.0,
                    bottom: 54.0,
                    left: 48.0,
                })
                .align_x(Alignment::Start)
                .align_y(Alignment::End),
        ]
        .clip(false)
        .into()
    } else {
        home_content.into()
    }
}

fn current_quick_mode(state: &SettingsState) -> QuickStartMode {
    if state.server_mode_enabled {
        QuickStartMode::Server
    } else if state.start_mode == settings::StartMode::Desktop {
        QuickStartMode::Desktop
    } else {
        QuickStartMode::Normal
    }
}

/// 主页使用的主题感知分段选择器，避免 Astra UI 默认的固定浅色背景。
pub(crate) fn themed_segmented_group<Message: Clone + 'static>(
    items: Vec<ToggleButtonGroupItem<'static>>,
    on_toggle: impl Fn(usize) -> Message + Clone,
) -> Element<'static, Message> {
    themed_segmented_group_enabled(items, true, on_toggle)
}

/// 可禁用的分段选择器，酒馆运行期间用于锁定启动模式相关设置。
pub(crate) fn themed_segmented_group_enabled<Message: Clone + 'static>(
    items: Vec<ToggleButtonGroupItem<'static>>,
    enabled: bool,
    on_toggle: impl Fn(usize) -> Message + Clone,
) -> Element<'static, Message> {
    let item_count = items.len();
    let controls = items
        .into_iter()
        .enumerate()
        .map(|(index, item)| {
            let selected = item.selected;
            let label = item.label.unwrap_or_default();
            let icon = item.icon;
            let mut content = row![].spacing(6).align_y(Alignment::Center);
            if let Some(icon) = icon {
                let icon_text: iced::widget::Text<'static> = icon.into();
                content = content.push(icon_text.size(15).style(move |theme| {
                    iced::widget::text::Style {
                        color: Some(if selected && enabled {
                            WHITE
                        } else {
                            crate::theme::text_muted(theme)
                        }),
                    }
                }));
            }
            content = content.push(text(label).size(11).font(crate::core::typography::medium()));
            let control = button(
                container(content)
                    .align_x(Alignment::Center)
                    .align_y(Alignment::Center),
            )
            .height(34)
            .padding([0, 11])
            .style(move |theme, status| {
                segmented_button_style(theme, selected, enabled, status, index, item_count)
            });
            if enabled {
                control.on_press(on_toggle.clone()(index)).into()
            } else {
                control.into()
            }
        })
        .collect::<Vec<_>>();

    container(row(controls).spacing(0))
        .style(crate::theme::segmented_group_style)
        .into()
}

fn segmented_button_style(
    theme: &Theme,
    selected: bool,
    enabled: bool,
    status: button::Status,
    index: usize,
    item_count: usize,
) -> button::Style {
    let hovered = enabled && matches!(status, button::Status::Hovered | button::Status::Pressed);
    let background = if selected && enabled {
        theme.palette().primary
    } else if hovered {
        crate::theme::surface(theme)
    } else {
        crate::theme::surface_alt(theme)
    };
    let radius = if item_count <= 1 {
        iced::border::Radius::from(10.0)
    } else if index == 0 {
        iced::border::Radius::default().left(10.0)
    } else if index + 1 == item_count {
        iced::border::Radius::default().right(10.0)
    } else {
        iced::border::Radius::default()
    };
    button::Style {
        background: Some(Background::Color(background)),
        text_color: if selected && enabled {
            WHITE
        } else if enabled {
            crate::theme::text(theme)
        } else {
            crate::theme::text_muted(theme)
        },
        border: Border {
            radius,
            ..Border::default()
        },
        ..button::Style::default()
    }
}

fn quick_mode_from_index(index: usize) -> QuickStartMode {
    match index {
        1 => QuickStartMode::Desktop,
        2 => QuickStartMode::Server,
        _ => QuickStartMode::Normal,
    }
}

fn server_service_mode_from_index(index: usize) -> ServerServiceMode {
    match index {
        1 => ServerServiceMode::Internet,
        _ => ServerServiceMode::Lan,
    }
}

fn browser_type_from_index(index: usize) -> BrowserType {
    match index {
        1 => BrowserType::Chrome,
        2 => BrowserType::Firefox,
        3 => BrowserType::Edge,
        _ => BrowserType::System,
    }
}

/// 主页版本快捷切换按钮；下拉内容由外层主页 Stack 以 overlay 方式承载。
fn home_version_selector<'a>(
    versions: &'a VersionState,
    open: bool,
    locked: bool,
) -> Element<'a, Message> {
    let selected_label = current_tavern_label(versions);
    let selected_color = versions
        .current_source
        .map(VersionSource::color)
        .unwrap_or_else(|| crate::theme::text_muted(&crate::theme::light_theme()));
    let trigger = button(
        row![
            icons::icon(Icon::Beer, 15, selected_color),
            raw(selected_label)
                .size(12)
                .font(crate::core::typography::medium())
                .style(move |_theme| iced::widget::text::Style {
                    color: Some(selected_color),
                }),
            space::horizontal(),
            icons::icon(
                if open { Icon::ChevronUp } else { Icon::ChevronDown },
                14,
                INK_MUTED,
            ),
        ]
        .spacing(8)
        .align_y(Alignment::Center),
    )
    .width(230)
    .padding([8, 11])
    .style(move |theme, status| {
        let hovered = matches!(
            status,
            iced::widget::button::Status::Hovered | iced::widget::button::Status::Pressed
        );
        iced::widget::button::Style {
            background: Some(Background::Color(if hovered {
                crate::theme::surface_alt(theme)
            } else {
                crate::theme::surface(theme)
            })),
            border: Border {
                radius: 8.0.into(),
                color: source_selected_border(theme, selected_color),
                width: 1.0,
            },
            text_color: crate::theme::text(theme),
            ..iced::widget::button::Style::default()
        }
    });
    let trigger = if locked {
        trigger
    } else {
        trigger.on_press(Message::HomeTavernVersionSelectorToggled)
    };
    column![
        text("app.quick.tavern_version")
            .size(11)
            .font(crate::core::typography::medium())
            .style(crate::theme::muted_text_style),
        trigger,
    ]
    .spacing(6)
    .into()
}

/// 主页版本菜单，作为主页 Stack 的上层内容显示，不参与主页布局计算。
fn home_version_dropdown<'a>(
    versions: &'a VersionState,
    locked: bool,
) -> Element<'a, Message> {
    let version_items = home_version_items(versions);
    let mut menu = column![].spacing(2).width(230);

    for item in version_items.iter().cloned() {
        let selected = versions.current_source == Some(item.source)
            && versions.current_path.as_deref() == Some(item.path.as_str())
            && versions.current_version.as_deref() == Some(item.version.as_str());
        let source_color = item.source.color();
        let label = format!(
            "{} - {}",
            item.version,
            crate::lang::t(item.source.label_key())
        );
        let item_button = button(
            row![
                container(space::horizontal())
                    .width(4)
                    .height(22)
                    .style(move |_theme| iced::widget::container::Style {
                        background: Some(Background::Color(source_color)),
                        ..iced::widget::container::Style::default()
                    }),
                raw(label)
                    .size(12)
                    .font(crate::core::typography::medium())
                    .style(move |_theme| iced::widget::text::Style {
                        color: Some(source_color),
                    }),
                space::horizontal(),
                if selected {
                    icons::icon(Icon::Check, 14, source_color)
                } else {
                    icons::icon(Icon::Circle, 14, INK_MUTED)
                },
            ]
            .spacing(8)
            .align_y(Alignment::Center),
        )
        .width(Fill)
        .padding([8, 10])
        .style(move |theme, status| {
            let hovered = matches!(
                status,
                iced::widget::button::Status::Hovered | iced::widget::button::Status::Pressed
            );
            iced::widget::button::Style {
                background: (selected || hovered).then_some(Background::Color(if crate::theme::is_dark(theme) {
                    Color::from_rgba(source_color.r, source_color.g, source_color.b, 0.16)
                } else {
                    Color::from_rgba(source_color.r, source_color.g, source_color.b, 0.08)
                })),
                text_color: crate::theme::text(theme),
                ..iced::widget::button::Style::default()
            }
        });
        if !locked && !selected {
            menu = menu.push(item_button.on_press(Message::HomeTavernVersionSelected(item)));
        } else {
            menu = menu.push(item_button);
        }
    }

    if version_items.is_empty() {
        menu = menu.push(
            container(text("app.quick.no_switchable").size(12).style(crate::theme::muted_text_style))
                .padding([10, 12]),
        );
    }

    container(scrollable(menu).height(Length::Fixed(220.0)))
        .padding([4, 0])
        .style(home_version_menu_style)
        .into()
}

/// 主页快捷菜单按照本地列表、在线实例的顺序提供真实可用项。
fn home_version_items(versions: &VersionState) -> Vec<HomeTavernVersion> {
    let mut items = versions
        .local_instances
        .iter()
        .filter(|item| item.dependencies == DependencyStatus::Ready)
        .map(|item| HomeTavernVersion {
            version: item.version.clone(),
            source: VersionSource::Local,
            path: item.path.clone(),
        })
        .collect::<Vec<_>>();

    // 在线快捷切换只展示已经完成安装的版本，未安装版本必须通过版本管理页安装，
    // 避免主页菜单出现点击后才开始下载的“伪快捷切换”选项。
    let online_path = versions
        .online_instance_path
        .clone()
        .unwrap_or_else(|| crate::core::network::sillytavern_install_dir().to_string_lossy().into_owned());
    for release in versions.online_releases.iter().filter(|release| release.installed) {
        items.push(HomeTavernVersion {
            version: release.version.clone(),
            source: VersionSource::Online,
            path: online_path.clone(),
        });
    }
    if versions.staging_installed {
        items.push(HomeTavernVersion {
            version: "staging".to_owned(),
            source: VersionSource::Online,
            path: online_path.clone(),
        });
    }

    // 在线版本列表尚未返回时，使用启动时从 Git 仓库恢复的真实版本。
    if !items.iter().any(|item| item.source == VersionSource::Online)
        && versions.online_instance_exists
        && versions.current_source == Some(VersionSource::Online)
        && let Some(version) = versions.current_version.as_ref()
    {
        items.push(HomeTavernVersion {
            version: version.clone(),
            source: VersionSource::Online,
            path: online_path,
        });
    }
    items
}

fn current_tavern_label(versions: &VersionState) -> String {
    match (&versions.current_version, versions.current_source) {
        (Some(version), Some(source)) => format!(
            "{} - {}",
            version,
            crate::lang::t(source.label_key())
        ),
        _ => t("app.quick.select_version").to_owned(),
    }
}

fn current_tavern_info_item<'a>(versions: &'a VersionState) -> Element<'a, Message> {
    let value = current_tavern_label(versions);
    let color = versions
        .current_source
        .map(VersionSource::color)
        .unwrap_or(BLUE_600);
    container(
        row![
            container(icons::icon(Icon::Beer, 16, color))
                .width(28)
                .height(28)
                .align_x(Alignment::Center)
                .align_y(Alignment::Center)
                .style(icon_surface),
            column![
                text("app.quick.tavern_version")
                    .size(11)
                    .font(crate::core::typography::regular())
                    .style(crate::theme::muted_text_style),
                raw(value)
                    .size(13)
                    .font(crate::core::typography::medium())
                    .style(move |_theme| iced::widget::text::Style { color: Some(color) }),
            ]
            .spacing(2),
        ]
        .spacing(8)
        .align_y(Alignment::Center),
    )
    .width(Fill)
    .into()
}

fn info_item_owned<'a>(
    icon: Icon,
    label: &'static str,
    value: String,
) -> Element<'a, Message> {
    container(
        row![
            container(icons::icon(icon, 16, BLUE_600))
                .width(28)
                .height(28)
                .align_x(Alignment::Center)
                .align_y(Alignment::Center)
                .style(icon_surface),
            column![
                text(label)
                    .size(11)
                    .font(crate::core::typography::regular())
                    .style(crate::theme::muted_text_style),
                raw(value).size(13).font(crate::core::typography::medium()),
            ]
            .spacing(2),
        ]
        .spacing(8)
        .align_y(Alignment::Center),
    )
    .width(Fill)
    .into()
}

fn environment_version(value: Option<&str>) -> String {
    value.map(str::to_owned).unwrap_or_else(|| t("app.quick.not_detected").to_owned())
}

/// 读取当前环境模式下某个依赖的版本号。
///
/// 首页只关心「当前正在使用的那套环境」装了什么，因此按 `env_mode` 取对应快照，
/// 而不是分别展示内置与系统两套结果。
fn active_env(
    state: &SettingsState,
    dependency: EnvironmentDependency,
) -> Option<&str> {
    state
        .environment
        .for_source(state.env_mode)
        .get(dependency)
        .map(String::as_str)
}

fn environment_status_badge(state: &SettingsState) -> Element<'static, Message> {
    // Git 与 Node.js 是运行酒馆的硬前提，按当前环境模式判定是否齐备。
    let ready = active_env(state, EnvironmentDependency::Git).is_some()
        && active_env(state, EnvironmentDependency::NodeJs).is_some();
    if ready {
        status_badge("app.quick.env_ok")
    } else {
        status_badge_with_color(
            "app.quick.env_incomplete",
            Color::from_rgb8(245, 165, 36),
            Icon::TriangleAlert,
        )
    }
}

fn source_selected_border(theme: &Theme, color: Color) -> Color {
    if color == crate::theme::text_muted(theme) {
        crate::theme::line(theme)
    } else {
        Color::from_rgba(color.r, color.g, color.b, 0.45)
    }
}

fn home_version_menu_style(theme: &Theme) -> iced::widget::container::Style {
    iced::widget::container::Style {
        background: Some(Background::Color(crate::theme::surface(theme))),
        border: Border {
            radius: 8.0.into(),
            color: crate::theme::line(theme),
            width: 1.0,
        },
        ..iced::widget::container::Style::default()
    }
}

fn info_item(icon: Icon, label: &'static str, value: &'static str) -> Element<'static, Message> {
    container(
        row![
            container(icons::icon(icon, 16, BLUE_600))
                .width(28)
                .height(28)
                .align_x(Alignment::Center)
                .align_y(Alignment::Center)
                .style(icon_surface),
            column![
                text(label)
                    .size(11)
                    .font(crate::core::typography::regular())
                    .style(crate::theme::muted_text_style),
                text(value).size(13).font(crate::core::typography::medium()),
            ]
            .spacing(2),
        ]
        .spacing(8)
        .align_y(Alignment::Center),
    )
    .width(Fill)
    .into()
}

fn status_badge(label: &'static str) -> Element<'static, Message> {
    status_badge_with_color(label, SUCCESS, Icon::CircleCheck)
}

fn status_badge_with_color(
    label: &'static str,
    color: Color,
    icon: Icon,
) -> Element<'static, Message> {
    container(
        row![
            icons::icon(icon, 14, color),
            text(label)
                .size(11)
                .font(crate::core::typography::medium())
                .color(color),
        ]
        .spacing(5)
        .align_y(Alignment::Center),
    )
    .padding([6, 10])
    .style(move |theme| status_surface_with_color(theme, color))
    .into()
}

fn info_surface(theme: &Theme) -> iced::widget::container::Style {
    iced::widget::container::Style {
        background: Some(Background::Color(if crate::theme::is_dark(theme) {
            crate::theme::surface_alt(theme)
        } else {
            Color::from_rgb8(246, 250, 255)
        })),
        border: Border {
            radius: 12.0.into(),
            color: if crate::theme::is_dark(theme) {
                crate::theme::line(theme)
            } else {
                Color::from_rgb8(224, 235, 248)
            },
            width: 1.0,
        },
        ..iced::widget::container::Style::default()
    }
}

fn icon_surface(theme: &Theme) -> iced::widget::container::Style {
    iced::widget::container::Style {
        background: Some(Background::Color(Color::from_rgba(
            theme.palette().primary.r,
            theme.palette().primary.g,
            theme.palette().primary.b,
            if crate::theme::is_dark(theme) {
                0.18
            } else {
                0.10
            },
        ))),
        border: Border {
            radius: 8.0.into(),
            ..Border::default()
        },
        ..iced::widget::container::Style::default()
    }
}

fn status_surface_with_color(theme: &Theme, color: Color) -> iced::widget::container::Style {
    iced::widget::container::Style {
        background: Some(Background::Color(Color::from_rgba(
            color.r,
            color.g,
            color.b,
            if crate::theme::is_dark(theme) { 0.18 } else { 0.12 },
        ))),
        border: Border {
            radius: 20.0.into(),
            ..Border::default()
        },
        ..iced::widget::container::Style::default()
    }
}
