use eframe::egui;
use std::sync::Arc;
use std::time::Instant;

const MAX_TOASTS: usize = 5;
const TOAST_LIFETIME: f32 = 3.0;
const PADDING: f32 = 12.0;
const GAP: f32 = 6.0;
const BOTTOM_OFFSET: f32 = 50.0;
const COLLAPSED_PEEK: f32 = 16.0;

pub struct Toast {
    text: String,
    remaining: f32,
    paused: bool,
}

pub struct ToastStack {
    toasts: Vec<Toast>,
    last_frame: Option<Instant>,
    was_hovered: bool,
    expanded: bool,
}

impl ToastStack {
    pub fn new() -> Self {
        Self {
            toasts: Vec::new(),
            last_frame: None,
            was_hovered: false,
            expanded: false,
        }
    }

    pub fn push(&mut self, text: String, ctx: &egui::Context) {
        while self.toasts.len() >= MAX_TOASTS {
            self.toasts.remove(0);
        }
        self.toasts.push(Toast {
            text,
            remaining: TOAST_LIFETIME,
            paused: false,
        });
        ctx.request_repaint();
    }

    /// 每帧调用，更新倒计时并返回是否需要继续重绘
    fn update_timers(&mut self, ctx: &egui::Context, hovered: bool) -> bool {
        let now = Instant::now();
        let dt = self
            .last_frame
            .map(|t| t.elapsed().as_secs_f32().min(0.1))
            .unwrap_or(0.0);
        self.last_frame = Some(now);

        if hovered != self.was_hovered {
            for toast in &mut self.toasts {
                toast.paused = hovered;
            }
            self.was_hovered = hovered;
            self.expanded = hovered;
        }

        if !hovered {
            for toast in &mut self.toasts {
                toast.remaining -= dt;
            }
        }

        self.toasts.retain(|t| t.remaining > 0.0);

        if !self.toasts.is_empty() {
            ctx.request_repaint();
        }

        !self.toasts.is_empty()
    }

    /// 渲染 toast 堆叠
    pub fn render(&mut self, ctx: &egui::Context) {
        if self.toasts.is_empty() {
            return;
        }

        let hover_pos = ctx.input(|i| i.pointer.hover_pos());
        let screen_rect = ctx.viewport_rect();
        let visuals = ctx.style().visuals.clone();
        let font_id = egui::FontId::proportional(14.0);
        let text_color = visuals.text_color();

        let painter = ctx.layer_painter(egui::LayerId::new(
            egui::Order::Tooltip,
            egui::Id::new("toast_stack"),
        ));

        // ---- 第一步：测量每个 toast 的尺寸 ----
        struct Dim {
            size: egui::Vec2,
            galley: Arc<egui::Galley>,
        }

        let mut dims: Vec<Dim> = Vec::new();
        for toast in &self.toasts {
            let galley =
                painter.layout_no_wrap(toast.text.clone(), font_id.clone(), text_color);
            let size = galley.size() + egui::vec2(PADDING * 2.0, PADDING * 2.0);
            dims.push(Dim { size, galley });
        }

        let center_x = screen_rect.center().x;
        let base_y = screen_rect.max.y - BOTTOM_OFFSET;

        // ---- 第二步：计算 collapsed 位置 ----
        let mut positions: Vec<egui::Rect> = vec![egui::Rect::NOTHING; self.toasts.len()];
        let mut y = base_y;

        for i in (0..self.toasts.len()).rev() {
            let size = dims[i].size;
            let rect = egui::Rect::from_center_size(egui::pos2(center_x, y - size.y / 2.0), size);
            positions[i] = rect;

            if i > 0 {
                y = rect.top() + (size.y - COLLAPSED_PEEK);
            }
        }

        // ---- 第三步：检测 hover ----
        let total_rect: egui::Rect =
            positions.iter().fold(egui::Rect::NOTHING, |a, b| a.union(*b));
        let hovered = hover_pos.map_or(false, |p| total_rect.contains(p));
        self.expanded = hovered;

        // ---- 第四步：悬停时展开 ----
        if hovered {
            y = base_y;
            for i in (0..self.toasts.len()).rev() {
                let size = dims[i].size;
                let rect =
                    egui::Rect::from_center_size(egui::pos2(center_x, y - size.y / 2.0), size);
                positions[i] = rect;
                y = rect.top() - GAP;
            }
        }

        // ---- 第五步：渲染 ----
        for (i, rect) in positions.iter().enumerate() {
            let toast = &self.toasts[i];
            let dim = &dims[i];

            let alpha = (toast.remaining.min(1.0)).max(0.0);
            let is_newest = i == self.toasts.len() - 1;

            let bg_alpha = if hovered || is_newest {
                0.88 * alpha
            } else {
                0.55 * alpha
            };

            let bg_color = visuals.panel_fill.linear_multiply(bg_alpha);
            let stroke_color = visuals.window_stroke().color.linear_multiply(alpha);
            let txt_color = text_color.linear_multiply(alpha);

            painter.rect(
                *rect,
                8.0,
                bg_color,
                egui::Stroke::new(1.0, stroke_color),
                egui::StrokeKind::Middle,
            );

            let text_pos = egui::pos2(
                rect.center().x - dim.galley.size().x / 2.0,
                rect.center().y - dim.galley.size().y / 2.0,
            );
            painter.galley(text_pos, dim.galley.clone(), txt_color);
        }

        // ---- 第六步：更新计时器 ----
        self.update_timers(ctx, hovered);
    }
}
