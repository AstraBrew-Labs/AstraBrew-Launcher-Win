//! Windows 平台相关的原生窗口定制。
//!
//! 负责两件事：
//! 1. 启动时根据当前显示器布局决定窗口位置与固定尺寸档位；
//! 2. 处理「禁用缩放/全屏」与「设置应用图标」的平台差异。
//!
//! # 为什么需要窗口位置预处理
//!
//! 用户上次关窗时所在的那块显示器可能已经断开（笔记本外接屏被拔掉是常见场景），
//! 若直接沿用历史坐标，窗口会落在不存在的屏幕上，表现为「启动后看不见窗口」。
//! 因此启动前必须先枚举显示器并做可见性校验，校验不通过就居中到主屏。
//!
//! 显示器枚举使用 Win32 的 `EnumDisplayMonitors` + `GetMonitorInfoW`，
//! 它们返回的是**物理像素**；iced / winit 的窗口坐标是**逻辑像素**，
//! 所以需要按主屏的缩放比例换算后再交给 iced。

use std::sync::OnceLock;

use iced::{Point, Size, window};
use windows_sys::Win32::Foundation::{LPARAM, RECT, TRUE};
use windows_sys::Win32::Graphics::Gdi::{
    EnumDisplayMonitors, GetMonitorInfoW, HDC, HMONITOR, MONITOR_DEFAULTTOPRIMARY, MONITORINFO,
    MonitorFromPoint,
};
use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
use windows_sys::Win32::UI::HiDpi::{
    DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, GetDpiForSystem,
    SetProcessDpiAwarenessContext,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    CreateIcon, GetSystemMetrics, HICON, IMAGE_ICON, LoadImageW, SM_CXICON, SM_CXSMICON,
    SM_CYICON, SM_CYSMICON, SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN,
};
use windows_sys::core::BOOL;

/// 窗口在屏幕上至少要露出这么多逻辑像素，才认为「用户能找到它」。
const MIN_VISIBLE_WIDTH: f32 = 64.0;
/// 标题栏至少要露出这么多逻辑像素，才够用户拖回来。
const MIN_VISIBLE_HEIGHT: f32 = 24.0;

/// 启动时使用的窗口位置与固定尺寸。
pub struct InitialWindowPlacement {
    pub position: window::Position,
    pub size: Size,
}

/// 主屏缓冲区在窗口坐标系中的虚拟原点。
///
/// Win32 的虚拟桌面坐标**不区分**物理/逻辑像素，而 winit 使用的显示器坐标是
/// 以主屏左上角为原点的逻辑坐标。两者的差值恰好是「主屏物理原点」，
/// 减去它即可把任意显示器的物理坐标换算到 winit 的坐标系。
static PRIMARY_ORIGIN: OnceLock<(f32, f32)> = OnceLock::new();

/// 主屏缩放比例对应的每逻辑英寸点数（用于物理→逻辑换算）。
static PRIMARY_SCALE: OnceLock<f32> = OnceLock::new();

/// 首次枚举显示器时缓存的「主屏物理原点」。
fn primary_origin() -> (f32, f32) {
    *PRIMARY_ORIGIN.get_or_init(|| {
        // SAFETY: 两个查询函数无副作用，失败时返回 0，等价于原点在主屏。
        unsafe {
            (
                GetSystemMetrics(SM_XVIRTUALSCREEN) as f32,
                GetSystemMetrics(SM_YVIRTUALSCREEN) as f32,
            )
        }
    })
}

/// 首个显示器的缩放比例（物理像素 ÷ 逻辑像素）。
///
/// Win32 的显示器坐标是物理像素，而 iced 的窗口坐标是逻辑像素；
/// 在单显示器场景下用主屏比例换算即可，多显示器下比例不同带来的偏移
/// 只影响初始居中的精确度，不影响可用性。
fn primary_scale() -> f32 {
    *PRIMARY_SCALE.get_or_init(|| {
        // SAFETY: `GetDpiForSystem` 只读取进程的 DPI 上下文，无副作用。
        let dpi = unsafe { GetDpiForSystem() };
        if dpi == 0 { 96.0 } else { dpi as f32 / 96.0 }
    })
}

/// 声明进程为「每显示器 DPI 感知 v2」。
///
/// 若不声明，Windows 会对高 DPI 屏幕上的窗口做位图拉伸，
/// 界面模糊；v2 模式还允许运行时跨屏切换时自动调整缩放。
pub fn enable_per_monitor_dpi_awareness() {
    // SAFETY: 必须在创建任何窗口前调用；重复调用是无害的。
    unsafe {
        let _ = SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    }
}

/// 一块显示器：可见区域（逻辑坐标）+ 物理尺寸（用于判定宽高比）。
#[derive(Clone, Copy)]
struct MonitorLayout {
    /// 工作区（已扣除任务栏），逻辑坐标。
    work_area: iced::Rectangle,
    /// 完整分辨率（物理像素），用于选择 16:9 / 4:3 尺寸档。
    physical: Size,
}

/// `EnumDisplayMonitors` 的回调上下文。
///
/// 回调是 `extern "system"` 函数指针，无法捕获环境，只能通过 `LPARAM`
/// 传递 `&mut Vec<MonitorLayout>`。
struct MonitorEnumerator {
    layouts: Vec<MonitorLayout>,
}

/// 枚举全部显示器的回调。
///
/// 返回 `TRUE` 表示继续枚举；本实现始终继续。
unsafe extern "system" fn enum_monitor_callback(
    _monitor: HMONITOR,
    _hdc: HDC,
    _clip: *mut RECT,
    data: LPARAM,
) -> BOOL {
    // SAFETY: `data` 由 `enumerate_monitors` 传入，指向调用栈上仍然存活的 `MonitorEnumerator`。
    let enumerator = unsafe { &mut *(data as *mut MonitorEnumerator) };
    // SAFETY: `_monitor` 由系统提供，是当前正在枚举的显示器句柄。
    if let Some(layout) = unsafe { monitor_layout(_monitor) } {
        enumerator.layouts.push(layout);
    }
    TRUE
}

/// 读取单个显示器的工作区与物理尺寸。
///
/// 返回 `None` 表示该显示器已被移除（枚举过程中热插拔）。
unsafe fn monitor_layout(monitor: HMONITOR) -> Option<MonitorLayout> {
    let mut info = MONITORINFO {
        cbSize: std::mem::size_of::<MONITORINFO>() as u32,
        ..Default::default()
    };
    // SAFETY: `info` 已按约定填好 `cbSize`，是有效的输出缓冲区。
    if unsafe { GetMonitorInfoW(monitor, &mut info) } == 0 {
        return None;
    }

    let (origin_x, origin_y) = primary_origin();
    let scale = primary_scale();
    let work = info.rcWork;
    let full = info.rcMonitor;

    // 物理像素 → 逻辑像素：先平移主屏原点，再按比例缩放。
    let to_logical = |value: i32, origin: f32| (value as f32 - origin) / scale;

    let work_area = iced::Rectangle {
        x: to_logical(work.left, origin_x),
        y: to_logical(work.top, origin_y),
        width: to_logical(work.right - work.left, 0.0),
        height: to_logical(work.bottom - work.top, 0.0),
    };
    let physical = Size::new(
        (full.right - full.left).max(1) as f32,
        (full.bottom - full.top).max(1) as f32,
    );

    Some(MonitorLayout {
        work_area,
        physical,
    })
}

/// 枚举当前所有显示器。
///
/// 枚举失败（例如处于无桌面会话）时返回空列表，由调用方走主屏兜底。
fn enumerate_monitors() -> Vec<MonitorLayout> {
    let mut enumerator = MonitorEnumerator {
        layouts: Vec::new(),
    };
    // SAFETY: 回调通过 LPARAM 拿到 `enumerator` 的可变引用；
    // `EnumDisplayMonitors` 在本调用内同步完成枚举，引用不会逃逸。
    unsafe {
        EnumDisplayMonitors(
            std::ptr::null_mut(),
            std::ptr::null(),
            Some(enum_monitor_callback),
            (&mut enumerator as *mut MonitorEnumerator) as isize,
        );
    }
    enumerator.layouts
}

/// 主屏的工作区；枚举不到时回退到 1920×1080 的假设值。
fn primary_work_area() -> iced::Rectangle {
    const FALLBACK: iced::Rectangle = iced::Rectangle {
        x: 0.0,
        y: 0.0,
        width: 1920.0,
        height: 1080.0,
    };
    let mut info = MONITORINFO {
        cbSize: std::mem::size_of::<MONITORINFO>() as u32,
        ..Default::default()
    };
    // SAFETY: 传入 (0,0) 配合 MONITOR_DEFAULTTOPRIMARY 必然命中主屏或最近的显示器；
    // `info` 是有效的输出缓冲区。
    unsafe {
        let monitor = MonitorFromPoint(
            windows_sys::Win32::Foundation::POINT { x: 0, y: 0 },
            MONITOR_DEFAULTTOPRIMARY,
        );
        if monitor.is_null() || GetMonitorInfoW(monitor, &mut info) == 0 {
            return FALLBACK;
        }
    }
    let (origin_x, origin_y) = primary_origin();
    let scale = primary_scale();
    let work = info.rcWork;
    iced::Rectangle {
        x: (work.left as f32 - origin_x) / scale,
        y: (work.top as f32 - origin_y) / scale,
        width: (work.right - work.left) as f32 / scale,
        height: (work.bottom - work.top) as f32 / scale,
    }
}

/// 根据当前显示器布局验证历史坐标，并选择对应的固定尺寸档位。
///
/// 历史坐标有效（与某块显示器有足够重叠）时沿用，避免用户每次启动都要重新摆窗口；
/// 无效（显示器已断开、坐标越界）时在主屏居中，保证窗口一定可见。
pub fn initial_window_placement(saved: Option<[f32; 2]>) -> InitialWindowPlacement {
    let monitors = enumerate_monitors();
    let primary = primary_work_area();

    // 历史坐标有效性校验：必须与某块显示器有足够的可见重叠。
    if let Some([x, y]) = saved {
        let candidates = if monitors.is_empty() {
            vec![primary]
        } else {
            monitors.iter().map(|monitor| monitor.work_area).collect()
        };
        for work_area in candidates {
            let size = fixed_window_size(monitor_physical_size(&monitors, work_area, primary));
            let rect = iced::Rectangle::new(Point::new(x, y), size);
            if visible_intersection(rect, work_area) {
                return InitialWindowPlacement {
                    position: window::Position::Specific(Point::new(x, y)),
                    size,
                };
            }
        }
    }

    // 兜底：主屏居中。
    let size = fixed_window_size(monitor_physical_size(&monitors, primary, primary));
    let centered = Point::new(
        primary.x + ((primary.width - size.width) / 2.0).max(0.0),
        primary.y + ((primary.height - size.height) / 2.0).max(0.0),
    );
    InitialWindowPlacement {
        position: window::Position::Specific(centered),
        size,
    }
}

/// 取某块工作区对应的物理分辨率；找不到时用主屏的物理尺寸。
fn monitor_physical_size(
    monitors: &[MonitorLayout],
    work_area: iced::Rectangle,
    primary: iced::Rectangle,
) -> Size {
    monitors
        .iter()
        .find(|monitor| monitor.work_area == work_area)
        .map(|monitor| monitor.physical)
        .or_else(|| monitors.first().map(|monitor| monitor.physical))
        .unwrap_or(Size::new(primary.width, primary.height))
}

/// 按显示器宽高比选择固定尺寸档位。
///
/// - 16:9 及更宽：1280×720
/// - 更接近 4:3：1280×800
fn fixed_window_size(monitor: Size) -> Size {
    if monitor.width / monitor.height.max(1.0) >= 1.5 {
        Size::new(1280.0, 720.0)
    } else {
        Size::new(1280.0, 800.0)
    }
}

/// 判断窗口是否在某块显示器上留有足够的可见区域。
///
/// 只要求标题栏所在的一条横向区域有重叠：用户能抓到标题栏就能拖回窗口。
fn visible_intersection(window: iced::Rectangle, screen: iced::Rectangle) -> bool {
    let left = window.x.max(screen.x);
    let right = (window.x + window.width).min(screen.x + screen.width);
    let top = window.y.max(screen.y);
    let bottom = (window.y + MIN_VISIBLE_HEIGHT).min(screen.y + screen.height);
    right - left >= MIN_VISIBLE_WIDTH && bottom - top >= MIN_VISIBLE_HEIGHT
}

/// 判断给定窗口坐标是否仍落在某块已连接的显示器上。
///
/// 副屏被拔出后，窗口坐标会指向不存在的区域；本函数用于把这种「幽灵窗口」
/// 识别出来，交给上层拉回主屏。
pub fn is_position_visible(position: Point, size: Size) -> bool {
    let rect = iced::Rectangle::new(position, size);
    let monitors = enumerate_monitors();
    if monitors.is_empty() {
        // 枚举不到显示器（远程会话等）时不做干预，避免误判把窗口乱搬。
        return true;
    }
    monitors
        .iter()
        .any(|monitor| visible_intersection(rect, monitor.work_area))
}

/// 主屏居中的窗口坐标。
pub fn centered_on_primary(size: Size) -> Point {
    let primary = primary_work_area();
    Point::new(
        primary.x + ((primary.width - size.width) / 2.0).max(0.0),
        primary.y + ((primary.height - size.height) / 2.0).max(0.0),
    )
}

/// 窗口图标（标题栏左上角 + 任务栏按钮）。
///
/// **必须显式给窗口设图标**：winit 注册窗口类时若 `hIcon` 为空，Windows 会退回内置的
/// 通用应用图标（那张白底的「窗口」图），看起来就像「软件图标缺失」。
/// `build.rs` 嵌进 exe 的 PE 资源图标只作用于资源管理器与快捷方式，
/// **不会**自动变成窗口图标 —— 那一步只能靠 winit 收到 `window::Settings.icon` 后
/// 写 `WM_SETICON` 完成。
///
/// 图形取自 `assets/icon/icon_window.png`（256×256、透明底，与 `icon.ico` 同一母版）：
/// 256 是 Windows 期望的窗口图标基准尺寸；直接把 1024 的母版塞进去会生成 4 MB 的 HICON，
/// 且小尺寸下由系统缩放的观感并不更好。
///
/// 解码失败时返回 `None`（退回系统默认图标），不阻断启动。
pub fn window_icon() -> Option<window::Icon> {
    const ICON_BYTES: &[u8] = include_bytes!("../assets/icon/icon_window.png");
    window::icon::from_file_data(ICON_BYTES, None).ok()
}

/// exe 内嵌图标资源的 ID。
///
/// `build.rs` 通过 `winresource::set_icon` 写入，该函数固定使用 ID 1
/// （即 `IDI_APPLICATION` 所在的槽位），所以这里也按 1 读取。
const ICON_RESOURCE_ID: *const u16 = 1usize as *const u16;

/// 已加载的原生窗口图标句柄缓存，顺序为 `[大图标, 小图标]`。
///
/// `HICON` 是裸指针、不满足 `Sync`，无法直接放进 `static`，因此以 `usize` 存放。
/// 句柄在本进程的整个生命周期内有效，由本模块持有，**不释放**。
static NATIVE_WINDOW_ICONS: OnceLock<[usize; 2]> = OnceLock::new();

/// 自建原生窗口需要的图标尺寸档位。
#[derive(Clone, Copy)]
pub enum WindowIconSize {
    /// 标题栏与任务栏按钮：`SM_CXSMICON`（通常 16×16）。
    Small,
    /// Alt+Tab 与任务栏缩略图：`SM_CXICON`（通常 32×32）。
    Big,
}

/// 取启动器图标对应的 Windows `HICON`，供**自建**的原生窗口使用。
///
/// iced 主窗口的图标走 winit 的 `window::Settings.icon`（见 [`window_icon`]）；
/// 而桌面模式的 WebView 窗口是本项目手写 `CreateWindowExW` 创建的，拿不到那条通道，
/// 必须自己把图标挂上去 —— 否则它会退回 Windows 的通用应用图标，
/// 与主窗口并排出现在任务栏时一眼就能看出不是同一个程序。
///
/// 加载顺序：
/// 1. exe 自带的 PE 图标资源（`assets/icon/icon.ico`，含 16/24/32/48/64/128/256 七档）。
///    `LoadImageW` 会挑最贴近目标尺寸的那一档，标题栏要 16×16、Alt+Tab 要 32×32
///    时都能命中专门优化的位图；
/// 2. 资源不可用（构建时缺 `icon.ico`）时退回内嵌的 `icon_window.png`，
///    现场走一遍 RGBA → `CreateIcon`，保证至少与 iced 主窗口完全一致。
///
/// 返回的句柄归进程所有并持有至退出，调用方**不得** `DestroyIcon`。
pub fn native_window_icon(size: WindowIconSize) -> Option<HICON> {
    let [big, small] = *NATIVE_WINDOW_ICONS.get_or_init(|| {
        [
            load_native_window_icon(WindowIconSize::Big),
            load_native_window_icon(WindowIconSize::Small),
        ]
    });
    let raw = match size {
        WindowIconSize::Big => big,
        WindowIconSize::Small => small,
    };
    (raw != 0).then_some(raw as HICON)
}

/// 按目标尺寸加载一枚原生窗口图标，失败时返回 0。
fn load_native_window_icon(size: WindowIconSize) -> usize {
    let (metric_width, metric_height) = match size {
        WindowIconSize::Big => (SM_CXICON, SM_CYICON),
        WindowIconSize::Small => (SM_CXSMICON, SM_CYSMICON),
    };
    // SAFETY: 只读取系统度量值，无副作用。
    let (width, height) = unsafe {
        (
            GetSystemMetrics(metric_width),
            GetSystemMetrics(metric_height),
        )
    };

    // SAFETY: 返回本进程的模块句柄；失败时为空指针，已在下文判空。
    let module = unsafe { GetModuleHandleW(std::ptr::null()) };
    if !module.is_null() {
        // SAFETY: `ICON_RESOURCE_ID` 是整数形式的资源名，指向本 exe 已加载的图标资源；
        // 模块句柄来自当前进程，生命周期覆盖整个调用。
        let handle = unsafe { LoadImageW(module, ICON_RESOURCE_ID, IMAGE_ICON, width, height, 0) };
        if !handle.is_null() {
            return handle as usize;
        }
    }

    // 资源不可用：退回与 iced 主窗口同源的 PNG。
    let Some(icon) = window_icon() else {
        return 0;
    };
    let (mut rgba, size) = icon.into_raw();
    create_icon_from_rgba(&mut rgba, size.width as i32, size.height as i32)
        .map(|handle| handle as usize)
        .unwrap_or(0)
}

/// 用 RGBA 位图现场生成 `HICON`。
///
/// `CreateIcon` 需要两份位图：彩色位图（32 bpp、BGRA 顺序）+ 单色 AND 掩码（1 bpp）。
/// 对带 alpha 通道的 32 bpp 图标，Windows 以 alpha 为准、不看掩码；
/// 但这里仍按「完全透明的处置位」写一份语义正确的掩码（每行按 4 字节对齐），
/// 以免系统真的回退到掩码判定时给出一整块黑底。
fn create_icon_from_rgba(rgba: &mut [u8], width: i32, height: i32) -> Option<HICON> {
    if width <= 0 || height <= 0 {
        return None;
    }
    let row_bytes = (width as usize).div_ceil(8).next_multiple_of(4);
    let mut and_mask = vec![0u8; row_bytes * height as usize];
    for (index, pixel) in rgba.chunks_exact_mut(4).enumerate() {
        let column = index % width as usize;
        let row = index / width as usize;
        // AND 掩码：1 表示保留目标像素（即透明），0 表示绘制彩色位图。
        if pixel[3] == 0 {
            and_mask[row * row_bytes + column / 8] |= 0x80u8 >> (column % 8);
        }
        // `CreateIcon` 的彩色位图要求 BGRA 顺序：互换 R / B 两个通道。
        pixel.swap(0, 2);
    }
    // SAFETY: 两个缓冲区分别按 1 bpp 掩码与 32 bpp 彩色位图的约定填好，
    // 长度足以让 `CreateIcon` 读取 `width × height` 个像素。
    let handle = unsafe {
        CreateIcon(
            std::ptr::null_mut(),
            width,
            height,
            1,
            32,
            and_mask.as_ptr(),
            rgba.as_ptr(),
        )
    };
    (!handle.is_null()).then_some(handle)
}

/// 窗口创建后再补写图标的兜底钩子。
///
/// 图标已在窗口创建时通过 `window::Settings.icon` 交给 winit（见 [`window_icon`]），
/// 窗口类一旦注册就带上了正确的 `hIcon`，这里无需重复设置。
/// 保留空实现是为了让 cross-platform 的调用点保持一致。
pub fn apply_application_icon() {}

/// Windows 侧无需额外禁用缩放按钮。
///
/// 窗口以 `resizable(false)` 创建时，winit 会一并移除 `WS_THICKFRAME` 与
/// `WS_MAXIMIZEBOX`，最大化按钮自动置灰，独占全屏也无法进入。
pub fn disable_zoom_button_and_fullscreen() {}

#[cfg(test)]
mod tests {
    use super::{fixed_window_size, visible_intersection};
    use iced::{Point, Rectangle, Size};

    fn window(x: f32, y: f32) -> Rectangle {
        Rectangle::new(Point::new(x, y), Size::new(1280.0, 720.0))
    }

    #[test]
    fn accepts_primary_and_negative_coordinate_displays() {
        let primary = Rectangle::new(Point::ORIGIN, Size::new(1920.0, 1040.0));
        let left = Rectangle::new(Point::new(-1920.0, 0.0), Size::new(1920.0, 1080.0));
        assert!(visible_intersection(window(200.0, 100.0), primary));
        assert!(visible_intersection(window(-1800.0, 80.0), left));
    }

    #[test]
    fn rejects_completely_offscreen_and_tiny_titlebar_overlap() {
        let screen = Rectangle::new(Point::ORIGIN, Size::new(1920.0, 1040.0));
        assert!(!visible_intersection(window(2500.0, 100.0), screen));
        assert!(!visible_intersection(window(1880.0, 100.0), screen));
        assert!(visible_intersection(window(1840.0, 100.0), screen));
    }

    /// 16:9 与更宽的屏幕使用 1280×720，4:3 档位使用 1280×800。
    #[test]
    fn picks_size_by_aspect_ratio() {
        assert_eq!(fixed_window_size(Size::new(1920.0, 1080.0)), Size::new(1280.0, 720.0));
        assert_eq!(fixed_window_size(Size::new(2560.0, 1440.0)), Size::new(1280.0, 720.0));
        assert_eq!(fixed_window_size(Size::new(1600.0, 1200.0)), Size::new(1280.0, 800.0));
    }
}
