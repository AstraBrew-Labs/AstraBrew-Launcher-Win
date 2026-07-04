//! 访问酒馆弹窗：服务器模式下展示局域网/公网访问地址与二维码
//!
//! 流程：
//! 1. 打开弹窗 → 显示 loading，后台并发检测 IPv4/IPv6 地址
//! 2. 地址获取完成后 → 根据数量切换布局（单列 / 左右对称）
//! 3. 每个地址下方生成二维码（后台线程，生成期间显示 loading 遮罩）

use eframe::egui;
use qrcode::QrCode;
use qrcode::types::Color;
use std::sync::{LazyLock, Mutex, mpsc::{self, Receiver, Sender}};

use crate::lang;
use crate::pages::settings::{Language, ServerServiceMode};

/// 二维码矩阵：true = 深色模块
type QrMatrix = Vec<Vec<bool>>;

/// 后台线程 → 主线程的消息
enum AccessMsg {
    Ipv4Addr(Option<String>),
    Ipv6Addr(Option<String>),
    Ipv4Qr(Option<QrMatrix>),
    Ipv6Qr(Option<QrMatrix>),
}

/// 单个 IP 版本的检测状态
#[derive(Clone)]
struct IpSlot {
    /// Some = 已获取地址；None = 未获取或失败
    addr: Option<String>,
    /// 是否获取失败
    addr_failed: bool,
    /// Some = 二维码已就绪；None = 未生成
    qr: Option<QrMatrix>,
    /// 是否已启动二维码生成线程（避免重复启动）
    qr_spawned: bool,
}

impl IpSlot {
    fn new() -> Self {
        Self {
            addr: None,
            addr_failed: false,
            qr: None,
            qr_spawned: false,
        }
    }
    /// 是否已结束检测（成功或失败）
    fn resolved(&self) -> bool {
        self.addr.is_some() || self.addr_failed
    }
}

pub struct AccessTavernPopupState {
    pub show: bool,
    tavern_url: String,
    service_mode: ServerServiceMode,
    port: u16,
    ipv4: IpSlot,
    ipv6: IpSlot,
    /// 通道发送端（用于在主线程中启动二维码生成线程）
    tx: Option<Sender<AccessMsg>>,
    /// 通道接收端
    rx: Option<Receiver<AccessMsg>>,
}

pub static ACCESS_TAVERN_POPUP: LazyLock<Mutex<AccessTavernPopupState>> = LazyLock::new(|| {
    Mutex::new(AccessTavernPopupState {
        show: false,
        tavern_url: String::new(),
        service_mode: ServerServiceMode::default(),
        port: 80,
        ipv4: IpSlot::new(),
        ipv6: IpSlot::new(),
        tx: None,
        rx: None,
    })
});

/// 打开弹窗并启动地址检测
pub fn open_popup(
    tavern_url: String,
    service_mode: ServerServiceMode,
    port: u16,
    ctx: &egui::Context,
) {
    let (tx, rx) = mpsc::channel();

    // 启动 IPv4 检测线程
    {
        let tx = tx.clone();
        let ctx = ctx.clone();
        let svc = service_mode.clone();
        std::thread::spawn(move || {
            let ip = match svc {
                ServerServiceMode::Lan => crate::core::network::get_lan_ipv4(),
                ServerServiceMode::Internet => crate::core::network::get_public_ipv4(),
            };
            let _ = tx.send(AccessMsg::Ipv4Addr(ip));
            ctx.request_repaint();
        });
    }
    // 启动 IPv6 检测线程
    {
        let tx = tx.clone();
        let ctx = ctx.clone();
        let svc = service_mode.clone();
        std::thread::spawn(move || {
            let ip = match svc {
                ServerServiceMode::Lan => crate::core::network::get_lan_ipv6(),
                ServerServiceMode::Internet => crate::core::network::get_public_ipv6(),
            };
            let _ = tx.send(AccessMsg::Ipv6Addr(ip));
            ctx.request_repaint();
        });
    }

    let mut state = ACCESS_TAVERN_POPUP.lock().unwrap();
    state.show = true;
    state.tavern_url = tavern_url;
    state.service_mode = service_mode;
    state.port = port;
    state.ipv4 = IpSlot::new();
    state.ipv6 = IpSlot::new();
    state.tx = Some(tx);
    state.rx = Some(rx);
}

/// 在弹窗内重试检测（复用已存储的 tavern_url / service_mode / port）
fn retry_detection(ctx: &egui::Context) {
    let (tavern_url, service_mode, port) = {
        let s = ACCESS_TAVERN_POPUP.lock().unwrap();
        (
            s.tavern_url.clone(),
            s.service_mode.clone(),
            s.port,
        )
    };
    open_popup(tavern_url, service_mode, port, ctx);
}

/// 轮询通道消息，更新状态并按需启动二维码生成线程
fn poll_messages(state: &mut AccessTavernPopupState, ctx: &egui::Context) {
    // 先收集所有待处理消息，避免 rx 的不可变借用与 state 的可变借用冲突
    let messages: Vec<AccessMsg> = {
        let Some(rx) = &state.rx else {
            return;
        };
        let mut msgs = Vec::new();
        while let Ok(msg) = rx.try_recv() {
            msgs.push(msg);
        }
        msgs
    };

    for msg in messages {
        match msg {
            AccessMsg::Ipv4Addr(opt) => match opt {
                Some(ip) => {
                    state.ipv4.addr = Some(ip);
                    spawn_qr_thread(state, IpVersion::V4, ctx);
                }
                None => state.ipv4.addr_failed = true,
            },
            AccessMsg::Ipv6Addr(opt) => match opt {
                Some(ip) => {
                    state.ipv6.addr = Some(ip);
                    spawn_qr_thread(state, IpVersion::V6, ctx);
                }
                None => state.ipv6.addr_failed = true,
            },
            AccessMsg::Ipv4Qr(opt) => {
                state.ipv4.qr = opt;
            }
            AccessMsg::Ipv6Qr(opt) => {
                state.ipv6.qr = opt;
            }
        }
    }
}

#[derive(Clone, Copy)]
enum IpVersion {
    V4,
    V6,
}

/// 启动二维码生成线程
fn spawn_qr_thread(state: &mut AccessTavernPopupState, ver: IpVersion, ctx: &egui::Context) {
    let (addr, port) = match ver {
        IpVersion::V4 => {
            if state.ipv4.qr_spawned {
                return;
            }
            state.ipv4.qr_spawned = true;
            let Some(ref a) = state.ipv4.addr else {
                return;
            };
            (a.clone(), state.port)
        }
        IpVersion::V6 => {
            if state.ipv6.qr_spawned {
                return;
            }
            state.ipv6.qr_spawned = true;
            let Some(ref a) = state.ipv6.addr else {
                return;
            };
            (a.clone(), state.port)
        }
    };
    let url = build_access_url(&addr, port, matches!(ver, IpVersion::V6));
    let tx = match state.tx.as_ref() {
        Some(t) => t.clone(),
        None => return,
    };
    let ctx = ctx.clone();
    std::thread::spawn(move || {
        let matrix = generate_qr(&url);
        let msg = match ver {
            IpVersion::V4 => AccessMsg::Ipv4Qr(matrix),
            IpVersion::V6 => AccessMsg::Ipv6Qr(matrix),
        };
        let _ = tx.send(msg);
        ctx.request_repaint();
    });
}

/// 从酒馆访问 URL 中提取端口
fn extract_port(url: &str) -> u16 {
    let after_scheme = url.split("://").nth(1).unwrap_or(url);
    let authority = after_scheme.split('/').next().unwrap_or(after_scheme);
    if let Some(colon) = authority.rfind(':') {
        let port_str = &authority[colon + 1..];
        if let Ok(p) = port_str.parse::<u16>() {
            return p;
        }
    }
    if url.starts_with("https") {
        443
    } else {
        80
    }
}

/// 构造访问地址 URL（IPv6 需加方括号）
fn build_access_url(ip: &str, port: u16, is_ipv6: bool) -> String {
    if is_ipv6 {
        format!("http://[{}]:{}/", ip, port)
    } else {
        format!("http://{}:{}/", ip, port)
    }
}

/// 生成二维码矩阵
fn generate_qr(url: &str) -> Option<QrMatrix> {
    let code = QrCode::new(url.as_bytes()).ok()?;
    let width = code.width();
    let colors = code.to_colors();
    let mut matrix = vec![vec![false; width]; width];
    for y in 0..width {
        for x in 0..width {
            matrix[y][x] = colors[y * width + x] == Color::Dark;
        }
    }
    Some(matrix)
}

// ─── 渲染 ────────────────────────────────────────────────────────────────────

pub fn render_access_tavern_popup(ctx: &egui::Context, lang: &Language) {
    let mut retry = false;
    let mut close = false;

    {
        let mut popup = ACCESS_TAVERN_POPUP.lock().unwrap();
        if !popup.show {
            return;
        }
        poll_messages(&mut popup, ctx);

        let port = popup.port;
        let v4 = popup.ipv4.clone();
        let v6 = popup.ipv6.clone();
        let service_mode = popup.service_mode.clone();

        let mut open = true;
        egui::Window::new(lang::t("at_popup_title", lang))
            .collapsible(false)
            .resizable(false)
            .fixed_size(egui::vec2(460.0, 340.0))
            .open(&mut open)
            .show(ctx, |ui| {
                let both_resolved = v4.resolved() && v6.resolved();
                if !both_resolved {
                    render_loading(ui, lang, &service_mode);
                } else {
                    match (v4.addr.is_some(), v6.addr.is_some()) {
                        (true, true) => render_dual(ui, &v4, &v6, port, lang),
                        (true, false) => render_single(ui, &v4, true, port, lang),
                        (false, true) => render_single(ui, &v6, false, port, lang),
                        (false, false) => render_error(ui, lang),
                    }
                    ui.add_space(8.0);
                    ui.vertical_centered(|ui| {
                        if ui
                            .button(lang::t("at_retry", lang))
                            .clicked()
                        {
                            retry = true;
                        }
                    });
                }
            });

        if !open {
            popup.show = false;
            close = true;
        }
    }

    if retry {
        retry_detection(ctx);
    }
    let _ = close;
}

/// 全屏 loading（正在检测地址）
fn render_loading(ui: &mut egui::Ui, lang: &Language, service_mode: &ServerServiceMode) {
    let mode_label = match service_mode {
        ServerServiceMode::Lan => lang::t("at_lan_mode", lang),
        ServerServiceMode::Internet => lang::t("at_internet_mode", lang),
    };
    ui.vertical_centered(|ui| {
        ui.add_space(40.0);
        ui.add(egui::Spinner::new().size(48.0));
        ui.add_space(12.0);
        ui.label(
            egui::RichText::new(lang::t("at_loading", lang))
                .size(15.0)
                .color(egui::Color32::from_rgb(200, 200, 200)),
        );
        ui.add_space(4.0);
        ui.label(
            egui::RichText::new(mode_label)
                .size(12.0)
                .color(egui::Color32::GRAY),
        );
        ui.add_space(40.0);
    });
}

/// 两个地址都获取到 → 左右对称布局
///
/// 关键：IPv6 地址通常比 IPv4 长很多，换行后地址区域更高。
/// 通过预先测量两列"地址头"（标签+地址文本）的高度，给较短的一列
/// 追加垂直间距，使两列的二维码起点 Y 坐标对齐，整体左右对称。
fn render_dual(
    ui: &mut egui::Ui,
    v4: &IpSlot,
    v6: &IpSlot,
    port: u16,
    lang: &Language,
) {
    let col_width = 200.0;
    let col_height = 300.0;
    let addr_font = egui::FontId::monospace(13.0);
    let label_size = 14.0f32;
    let gap_after_label = 4.0f32;

    // 预测量每列"地址头"高度（标签行 + 间距 + 换行后地址文本）
    let v4_url = v4
        .addr
        .as_ref()
        .map(|a| build_access_url(a, port, false))
        .unwrap_or_default();
    let v6_url = v6
        .addr
        .as_ref()
        .map(|a| build_access_url(a, port, true))
        .unwrap_or_default();

    let v4_addr_h = if v4.addr.is_some() {
        ui.painter()
            .layout(v4_url.clone(), addr_font.clone(), egui::Color32::WHITE, col_width)
            .size()
            .y
    } else {
        0.0
    };
    let v6_addr_h = if v6.addr.is_some() {
        ui.painter()
            .layout(v6_url.clone(), addr_font.clone(), egui::Color32::WHITE, col_width)
            .size()
            .y
    } else {
        0.0
    };

    // 标签行高约等于字号 * 1.4，加上与地址之间的间距
    let label_line_h = label_size * 1.4;
    let v4_header_h = label_line_h + gap_after_label + v4_addr_h;
    let v6_header_h = label_line_h + gap_after_label + v6_addr_h;
    let target_header_h = v4_header_h.max(v6_header_h);

    ui.horizontal_top(|ui| {
        // IPv4 列
        ui.allocate_ui(egui::vec2(col_width, col_height), |ui| {
            render_addr_panel_padded(
                ui,
                v4,
                true,
                port,
                lang,
                target_header_h - v4_header_h,
            );
        });
        ui.add_space(16.0);
        // IPv6 列
        ui.allocate_ui(egui::vec2(col_width, col_height), |ui| {
            render_addr_panel_padded(
                ui,
                v6,
                false,
                port,
                lang,
                target_header_h - v6_header_h,
            );
        });
    });
}

/// 只有一个地址 → 单列布局
fn render_single(
    ui: &mut egui::Ui,
    slot: &IpSlot,
    is_ipv4: bool,
    port: u16,
    lang: &Language,
) {
    ui.vertical_centered(|ui| {
        render_addr_panel_padded(ui, slot, is_ipv4, port, lang, 0.0);
    });
}

/// 单个地址面板：上方地址头（标签+地址），下方二维码。
///
/// `extra_header_pad`：地址头之后、二维码之前的额外垂直间距，
/// 用于双列布局时让两列二维码起点对齐（IPv6 地址更长 → IPv4 列补间距）。
fn render_addr_panel_padded(
    ui: &mut egui::Ui,
    slot: &IpSlot,
    is_ipv4: bool,
    port: u16,
    lang: &Language,
    extra_header_pad: f32,
) {
    let label = if is_ipv4 {
        lang::t("at_ipv4", lang)
    } else {
        lang::t("at_ipv6", lang)
    };

    ui.vertical_centered(|ui| {
        ui.label(egui::RichText::new(label).size(14.0).strong());

        if let Some(ref addr) = slot.addr {
            let url = build_access_url(addr, port, !is_ipv4);
            // 地址文本（可选中、可点击打开浏览器）
            let resp = ui.add(
                egui::Label::new(
                    egui::RichText::new(&url)
                        .monospace()
                        .size(13.0)
                        .color(egui::Color32::from_rgb(80, 180, 255)),
                )
                .selectable(true)
                .sense(egui::Sense::click()),
            );
            if resp.hovered() {
                ui.ctx().set_cursor_icon(egui::CursorIcon::PointingHand);
            }
            if resp.clicked() {
                let _ = std::process::Command::new("open").arg(&url).spawn();
            }
        }

        // 地址头补齐间距（双列对齐用）
        if extra_header_pad > 0.0 {
            ui.add_space(extra_header_pad);
        }

        ui.add_space(8.0);

        // 二维码区域
        let qr_size = 160.0;
        match &slot.qr {
            Some(matrix) => {
                render_qr(ui, matrix, qr_size);
                ui.add_space(4.0);
                ui.label(
                    egui::RichText::new(lang::t("at_scan_hint", lang))
                        .size(11.0)
                        .color(egui::Color32::GRAY),
                );
            }
            None => {
                render_qr_loading(ui, qr_size, lang);
            }
        }
    });
}

/// 渲染二维码
fn render_qr(ui: &mut egui::Ui, matrix: &QrMatrix, display_size: f32) {
    let n = matrix.len();
    let quiet = 4;
    let total_modules = (n + 2 * quiet) as f32;
    let cell = display_size / total_modules;

    let (rect, _) =
        ui.allocate_exact_size(egui::vec2(display_size, display_size), egui::Sense::hover());
    let painter = ui.painter();

    // 白色背景（含静区）
    painter.rect_filled(rect, egui::CornerRadius::same(4), egui::Color32::WHITE);

    let origin = rect.min;
    for y in 0..n {
        for x in 0..n {
            if matrix[y][x] {
                let px = origin.x + (x + quiet) as f32 * cell;
                let py = origin.y + (y + quiet) as f32 * cell;
                painter.rect_filled(
                    egui::Rect::from_min_size(egui::pos2(px, py), egui::vec2(cell, cell)),
                    egui::CornerRadius::same(0),
                    egui::Color32::BLACK,
                );
            }
        }
    }
}

/// 二维码生成中的 loading 遮罩
fn render_qr_loading(ui: &mut egui::Ui, size: f32, lang: &Language) {
    let (rect, _) = ui.allocate_exact_size(egui::vec2(size, size), egui::Sense::hover());
    let painter = ui.painter();
    // 暗色背景
    painter.rect_filled(
        rect,
        egui::CornerRadius::same(4),
        egui::Color32::from_rgb(45, 45, 48),
    );
    // 中心 spinner + 文案
    let center = rect.center();
    painter.text(
        center - egui::vec2(0.0, 14.0),
        egui::Align2::CENTER_CENTER,
        egui_phosphor::regular::SPINNER,
        egui::FontId::proportional(28.0),
        egui::Color32::from_rgb(150, 150, 150),
    );
    painter.text(
        center + egui::vec2(0.0, 22.0),
        egui::Align2::CENTER_CENTER,
        lang::t("at_qr_generating", lang),
        egui::FontId::proportional(11.0),
        egui::Color32::from_rgb(150, 150, 150),
    );
}

/// 两个地址都获取失败
fn render_error(ui: &mut egui::Ui, lang: &Language) {
    ui.vertical_centered(|ui| {
        ui.add_space(30.0);
        ui.label(
            egui::RichText::new(egui_phosphor::regular::WARNING_CIRCLE)
                .size(40.0)
                .color(egui::Color32::from_rgb(220, 150, 50)),
        );
        ui.add_space(12.0);
        ui.label(
            egui::RichText::new(lang::t("at_no_address", lang))
                .size(14.0)
                .color(egui::Color32::from_rgb(200, 200, 200)),
        );
        ui.add_space(30.0);
    });
}

/// 对外暴露的端口提取工具（供 console.rs 调用）
pub fn parse_port(url: &str) -> u16 {
    extract_port(url)
}
