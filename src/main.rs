//! AstraBrew Launcher 主入口。
//!
//! 负责初始化 iced 应用、注册字体与图标、配置窗口属性。
//! UI 组件统一来自 astra_ui（Astra UI）组件库。

// GUI 子系统：双击启动时不弹出控制台黑窗（仅 Windows 生效，其他平台忽略）。
#![windows_subsystem = "windows"]

mod app;
mod build_info;
mod core;
mod lang;
mod pages;
mod platform;
mod sidebar;
mod theme;
mod utils;

use iced::window;
use lucide_icons::LUCIDE_FONT_BYTES;

fn main() -> iced::Result {
    // 必须在创建任何窗口之前声明 DPI 感知，否则高分辨率屏上界面会被系统位图拉伸而模糊。
    platform::enable_per_monitor_dpi_awareness();

    let (settings_store, mut preferences) = core::settings::SettingsStore::load_default();
    // 启动前只扫描字体元数据，并注册上次选择的字体文件，避免首帧闪回默认字体。
    let font_catalog = core::typography::SystemFontCatalog::discover();
    let mut initial_font = font_catalog.resolve(&preferences.font_family);
    let startup_font_bytes = match font_catalog.load_family_bytes(initial_font) {
        Ok(bytes) => bytes,
        Err(_) => {
            initial_font = core::typography::FontChoice::default_choice();
            preferences.font_family = core::typography::DEFAULT_FONT_KEY.to_owned();
            Vec::new()
        }
    };
    let saved_position = preferences
        .remember_window_position
        .then_some(preferences.window_position)
        .flatten();
    let placement = platform::initial_window_placement(saved_position);
    let initial_store = settings_store.clone();
    let initial_font_catalog = font_catalog.clone();

    let mut application = iced::application(
        move || {
            app::Launcher::new(
                initial_store.clone(),
                preferences.clone(),
                initial_font_catalog.clone(),
                initial_font,
            )
        },
        app::Launcher::update,
        app::Launcher::view,
    )
    .title(app::Launcher::title)
    .subscription(app::Launcher::subscription)
    .theme(app::Launcher::theme)
    .scale_factor(app::Launcher::scale_factor)
    .font(LUCIDE_FONT_BYTES);

    // 注册 astra_ui 内置的 HarmonyOS Sans 字体（六档字重）。
    // 它只覆盖拉丁字形，保留注册是为了 astra_ui 组件内部直接引用该族时仍能渲染，
    // 界面正文不再走它（见 core::typography 模块文档）。
    for (_, bytes) in astra_ui::fonts::FONT_MAPPINGS {
        application = application.font(bytes);
    }
    // 内置默认字体（思源黑体三档字重）：同时覆盖中英文，三档都是独立设计。
    // 必须在渲染器创建前逐份注册：漏注册某一档，该字重就会退回系统字体，
    // 于是同一行里中文和英文的粗细又会对不上（见 core::typography 模块文档）。
    for bytes in core::typography::BUNDLED_FONT_FILES {
        application = application.font(*bytes);
    }
    // 用户上次选择的系统字体在渲染器创建前注册，首次绘制即可生效。
    for bytes in startup_font_bytes {
        application = application.font(bytes);
    }

    application
        // 默认字体同样指向内置思源黑体：未显式指定字体的组件（含 astra_ui 内部
        // 未调用 .font() 的文本）也走中英文同源的字体，避免零星文本出现粗细割裂。
        .default_font(core::typography::regular())
        .window(window::Settings {
            // 启动前已经按显示器比例选择固定尺寸，并验证历史窗口坐标。
            size: placement.size,
            min_size: Some(placement.size),
            max_size: Some(placement.size),
            position: placement.position,
            // 窗口尺寸固定，不以最大化 / 全屏方式启动。
            // resizable(false) 在 Windows 上会同时禁用最大化按钮，满足「不可最大化」要求。
            resizable: false,
            maximized: false,
            fullscreen: false,
            minimizable: true,
            closeable: true,
            // 关闭事件交给应用保存窗口位置后再显式退出。
            exit_on_close_request: false,
            ..window::Settings::default()
        })
        .antialiasing(true)
        .run()
}
