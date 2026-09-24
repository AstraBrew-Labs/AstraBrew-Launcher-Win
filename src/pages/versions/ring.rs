//! 扫描网格使用的环形进度指示器。
//!
//! `astra_ui` 自带的 `ProgressCircle` 最大只有 36px，塞不下「盘符 + 完成度」，
//! 而扫描弹窗需要一眼看清每个磁盘各自的进度，因此这里自绘一个更大的环形控件。
//! 绘制逻辑与 `astra_ui` 保持一致（同一条轨道、圆头端点），只是尺寸与配色可控。

use iced::widget::canvas;
use iced::{Color, Element, Radians, Rectangle, Renderer, Theme, mouse};

/// 环形进度条的直径（逻辑像素）。
pub const DIAMETER: f32 = 76.0;

/// 单个网格单元的宽度：环 + 下方两行文案，保证同一列的上下元素对齐。
pub const CELL_WIDTH: f32 = 104.0;

/// 环形进度指示器。
pub struct Ring {
    /// 完成度，取值 `0.0..=1.0`；`indeterminate` 为真时忽略。
    pub fraction: f32,
    /// 显示为不定长旋转弧（无法量化的阶段）。
    pub indeterminate: bool,
    /// 动画相位，取值 `0.0..=1.0`。
    pub phase: f32,
    /// 进度弧颜色。
    pub color: Color,
}

impl<Message> canvas::Program<Message> for Ring {
    type State = ();

    fn draw(
        &self,
        _state: &Self::State,
        renderer: &Renderer,
        theme: &Theme,
        bounds: Rectangle,
        _cursor: mouse::Cursor,
    ) -> Vec<canvas::Geometry> {
        let mut frame = canvas::Frame::new(renderer, bounds.size());
        let diameter = bounds.width.min(bounds.height);
        // 线宽按直径等比缩放，保证不同尺寸下的视觉比例一致。
        let stroke = diameter * (7.0 / DIAMETER);
        let radius = ((diameter - stroke) / 2.0 - 1.0).max(1.0);
        let center = frame.center();

        // 轨道始终画满一圈，即使进度为 0 也能看出这里有个指示器。
        frame.stroke(
            &canvas::Path::circle(center, radius),
            canvas::Stroke::default()
                .with_color(crate::theme::line(theme))
                .with_width(stroke),
        );

        let (start, sweep) = arc(self.fraction, self.indeterminate, self.phase);
        if sweep > 0.0001 {
            // 满进度直接画整圆：弧线在起止点重叠会留下一个可见的接缝。
            let path = if !self.indeterminate && self.fraction >= 1.0 {
                canvas::Path::circle(center, radius)
            } else {
                canvas::Path::new(|builder| {
                    builder.arc(canvas::path::Arc {
                        center,
                        radius,
                        start_angle: Radians(start),
                        end_angle: Radians(start + sweep),
                    });
                })
            };
            frame.stroke(
                &path,
                canvas::Stroke::default()
                    .with_color(self.color)
                    .with_width(stroke)
                    .with_line_cap(canvas::LineCap::Round),
            );
        }

        vec![frame.into_geometry()]
    }
}

/// 把环形指示器组装成 iced 控件。
pub fn ring<'a, Message: 'a>(ring: Ring) -> Element<'a, Message> {
    canvas(ring).width(DIAMETER).height(DIAMETER).into()
}

/// 计算进度弧的起始角与扫过角（弧度）。
///
/// 起点固定在正上方（`-π/2`），与 `astra_ui` 的环形指示器方向一致。
fn arc(fraction: f32, indeterminate: bool, phase: f32) -> (f32, f32) {
    let phase = if phase.is_finite() {
        phase.rem_euclid(1.0)
    } else {
        0.0
    };
    let start = -std::f32::consts::FRAC_PI_2
        + if indeterminate {
            phase * std::f32::consts::TAU
        } else {
            0.0
        };
    let sweep = if indeterminate {
        std::f32::consts::TAU * 0.25
    } else {
        std::f32::consts::TAU * fraction.clamp(0.0, 1.0)
    };
    (start, sweep)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn determinate_arc_sweeps_proportionally() {
        let (start, sweep) = arc(0.25, false, 0.0);
        assert!((start + std::f32::consts::FRAC_PI_2).abs() < f32::EPSILON);
        assert!((sweep - std::f32::consts::TAU * 0.25).abs() < 1e-6);
    }

    #[test]
    fn indeterminate_arc_rotates_with_phase() {
        let (start, sweep) = arc(0.0, true, 0.5);
        assert!((start + std::f32::consts::FRAC_PI_2 - std::f32::consts::PI).abs() < 1e-5);
        assert!((sweep - std::f32::consts::TAU * 0.25).abs() < 1e-6);
    }

    #[test]
    fn fraction_is_clamped_and_non_finite_phase_is_safe() {
        let (_, sweep) = arc(3.0, false, 0.0);
        assert!((sweep - std::f32::consts::TAU).abs() < 1e-6);
        let (start, _) = arc(0.0, true, f32::NAN);
        assert!(start.is_finite());
    }
}
