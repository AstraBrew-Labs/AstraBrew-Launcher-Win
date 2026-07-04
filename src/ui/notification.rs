//! 右下角通知系统
//!
//! - 通知从屏幕右侧外滑入（从右到左），显示在右下角
//! - 多条通知从下往上堆叠
//! - 自动消失（默认 4 秒），悬停时暂停倒计时
//! - 出现前 0.35s 为滑入动画，消失前 0.3s 为淡出

use eframe::egui;
use std::sync::Arc;
use std::time::Instant;

const MAX_NOTIFS: usize = 5;
const NOTIF_LIFETIME: f32 = 4.0;
const SLIDE_IN_DURATION: f32 = 0.35;
const FADE_OUT_DURATION: f32 = 0.3;
const PADDING: f32 = 14.0;
const GAP: f32 = 8.0;
const BOTTOM_OFFSET: f32 = 24.0;
const RIGHT_OFFSET: f32 = 24.0;
const MAX_WIDTH: f32 = 360.0;
const ICON_GAP: f32 = 10.0;

pub struct Notification {
    title: String,
    text: String,
    remaining: f32,
    age: f32,
    paused: bool,
}

pub struct NotificationStack {
    notifications: Vec<Notification>,
    last_frame: Option<Instant>,
}

impl NotificationStack {
    pub fn new() -> Self {
        Self {
            notifications: Vec::new(),
            last_frame: None,
        }
    }

    pub fn push(&mut self, title: String, text: String, ctx: &egui::Context) {
        while self.notifications.len() >= MAX_NOTIFS {
            self.notifications.remove(0);
        }
        self.notifications.push(Notification {
            title,
            text,
            remaining: NOTIF_LIFETIME,
            age: 0.0,
            paused: false,
        });
        ctx.request_repaint();
    }

    /// 渲染通知堆叠（右下角，从右到左滑入）
    pub fn render(&mut self, ctx: &egui::Context) {
        if self.notifications.is_empty() {
            self.last_frame = None;
            return;
        }

        // 时间增量
        let now = Instant::now();
        let dt = self
            .last_frame
            .map(|t| t.elapsed().as_secs_f32().min(0.1))
            .unwrap_or(0.0);
        self.last_frame = Some(now);

        let screen_rect = ctx.content_rect();
        let visuals = ctx.style().visuals.clone();
        let font_id = egui::FontId::proportional(13.0);
        let title_font = egui::FontId::proportional(12.0);
        let text_color = visuals.text_color();

        let painter = ctx.layer_painter(egui::LayerId::new(
            egui::Order::Foreground,
            egui::Id::new("notification_stack"),
        ));

        // ---- 第一步：测量每个通知尺寸（含图标 + 标题 + 正文）----
        struct Dim {
            size: egui::Vec2,
            body_galley: Arc<egui::Galley>,
        }

        let icon = egui_phosphor::regular::GLOBE;
        let icon_size = 20.0;
        let icon_font = egui::FontId::proportional(icon_size);

        let mut dims: Vec<Dim> = Vec::new();
        for n in &self.notifications {
            let body_galley = painter.layout(
                n.text.clone(),
                font_id.clone(),
                text_color,
                MAX_WIDTH - ICON_GAP - icon_size - PADDING * 2.0,
            );
            // 高度 = max(图标高度, 标题+正文高度) + padding*2
            let content_h = body_galley.size().y + 16.0; // 16 = 标题行高
            let h = content_h.max(icon_size) + PADDING * 2.0;
            let w = body_galley.size().x + ICON_GAP + icon_size + PADDING * 2.0 + 40.0;
            let w = w.min(MAX_WIDTH).max(280.0);
            dims.push(Dim {
                size: egui::vec2(w, h),
                body_galley,
            });
        }

        // ---- 第二步：计算目标位置（右下角，从下往上堆叠）----
        let base_right = screen_rect.max.x - RIGHT_OFFSET;
        let base_bottom = screen_rect.max.y - BOTTOM_OFFSET;

        let mut positions: Vec<egui::Rect> = Vec::with_capacity(self.notifications.len());
        let mut y_bottom = base_bottom;
        for dim in &dims {
            let rect = egui::Rect::from_min_size(
                egui::pos2(base_right - dim.size.x, y_bottom - dim.size.y),
                dim.size,
            );
            positions.push(rect);
            y_bottom = rect.min.y - GAP;
        }

        // ---- 第三步：检测 hover ----
        let total_rect: egui::Rect = positions.iter().fold(egui::Rect::NOTHING, |a, b| a.union(*b));
        let hover_pos = ctx.input(|i| i.pointer.hover_pos());
        let hovered = hover_pos.map_or(false, |p| total_rect.contains(p));

        // ---- 第四步：更新计时器 ----
        for n in &mut self.notifications {
            n.paused = hovered;
            if !hovered {
                n.remaining -= dt;
            }
            n.age += dt;
        }
        self.notifications.retain(|n| n.remaining > 0.0);

        if !self.notifications.is_empty() {
            ctx.request_repaint();
        }

        // ---- 第五步：渲染（带滑入动画）----
        for (i, rect) in positions.iter().enumerate() {
            if i >= self.notifications.len() {
                break;
            }
            let n = &self.notifications[i];
            let dim = &dims[i];

            // 滑入进度：age < SLIDE_IN_DURATION 时从右侧外滑入
            let slide_progress = (n.age / SLIDE_IN_DURATION).min(1.0);
            // ease-out cubic
            let slide_eased = 1.0 - (1.0 - slide_progress).powi(3);
            let slide_offset_x = (1.0 - slide_eased) * (dim.size.x + RIGHT_OFFSET + 20.0);

            // 淡出进度：remaining < FADE_OUT_DURATION 时淡出
            let fade_alpha = if n.remaining < FADE_OUT_DURATION {
                (n.remaining / FADE_OUT_DURATION).max(0.0)
            } else {
                1.0
            };

            // 滑入期间也淡入
            let slide_alpha = slide_eased;
            let alpha = fade_alpha.min(slide_alpha);

            let anim_rect = egui::Rect::from_min_size(
                egui::pos2(rect.min.x + slide_offset_x, rect.min.y),
                rect.size(),
            );

            // 背景渐变（深色卡片）
            let bg_base = egui::Color32::from_rgb(38, 42, 52);
            let bg_color = bg_base.linear_multiply(0.95 * alpha);
            let accent = egui::Color32::from_rgb(80, 180, 255).linear_multiply(alpha);
            let stroke_color = egui::Color32::from_rgb(70, 78, 92).linear_multiply(alpha);
            let txt_color = text_color.linear_multiply(alpha);
            let sub_color = egui::Color32::from_rgb(160, 168, 180).linear_multiply(alpha);

            // 卡片背景
            painter.rect(
                anim_rect,
                10.0,
                bg_color,
                egui::Stroke::new(1.0, stroke_color),
                egui::StrokeKind::Middle,
            );

            // 左侧 accent 条
            let accent_rect = egui::Rect::from_min_size(
                egui::pos2(anim_rect.min.x, anim_rect.min.y + 6.0),
                egui::vec2(3.0, anim_rect.size().y - 12.0),
            );
            painter.rect_filled(accent_rect, egui::CornerRadius::same(2), accent);

            // 图标
            let icon_pos = egui::pos2(
                anim_rect.min.x + PADDING,
                anim_rect.min.y + PADDING + 2.0,
            );
            painter.text(
                icon_pos,
                egui::Align2::LEFT_TOP,
                icon,
                icon_font.clone(),
                accent,
            );

            // 标题
            let title_pos = egui::pos2(
                anim_rect.min.x + PADDING + icon_size + ICON_GAP,
                anim_rect.min.y + PADDING,
            );
            painter.text(
                title_pos,
                egui::Align2::LEFT_TOP,
                n.title.clone(),
                title_font.clone(),
                sub_color,
            );

            // 正文
            let body_pos = egui::pos2(
                anim_rect.min.x + PADDING + icon_size + ICON_GAP,
                anim_rect.min.y + PADDING + 18.0,
            );
            painter.galley(body_pos, dim.body_galley.clone(), txt_color);
        }
    }
}
