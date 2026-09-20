//! 页面与应用根层之间共用的短暂轻提示数据。

use std::path::PathBuf;
use std::time::Duration;

use astra_ui::ToastVariant;

/// 轻提示可选操作。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum TransientNoticeAction {
    /// 在访达中显示指定文件。
    RevealPath(PathBuf),
}

/// 页面产生、由应用根层统一展示的短暂轻提示。
#[derive(Debug, Clone)]
pub struct TransientNotice {
    pub title_key: &'static str,
    pub detail: String,
    pub variant: ToastVariant,
    pub duration: Duration,
    pub action: Option<TransientNoticeAction>,
}

impl TransientNotice {
    /// 构造约三秒后消失的普通信息提示。
    pub fn info(title_key: &'static str, detail: impl Into<String>) -> Self {
        Self::new(title_key, detail, ToastVariant::Default, Duration::from_secs(3))
    }

    /// 构造约三秒后消失的成功提示。
    pub fn success(title_key: &'static str, detail: impl Into<String>) -> Self {
        Self::new(title_key, detail, ToastVariant::Success, Duration::from_secs(3))
    }

    /// 构造约五秒后消失的警告提示。
    pub fn warning(title_key: &'static str, detail: impl Into<String>) -> Self {
        Self::new(title_key, detail, ToastVariant::Warning, Duration::from_secs(5))
    }

    /// 构造约六秒后消失的错误提示。
    pub fn danger(title_key: &'static str, detail: impl Into<String>) -> Self {
        Self::new(title_key, detail, ToastVariant::Danger, Duration::from_secs(6))
    }

    /// 为轻提示附加可选操作。
    pub fn with_action(mut self, action: TransientNoticeAction) -> Self {
        self.action = Some(action);
        self
    }

    fn new(
        title_key: &'static str,
        detail: impl Into<String>,
        variant: ToastVariant,
        duration: Duration,
    ) -> Self {
        Self {
            title_key,
            detail: detail.into(),
            variant,
            duration,
            action: None,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn notice_constructors_use_expected_variants_and_durations() {
        let info = TransientNotice::info("notice.refresh_complete", "done");
        let success = TransientNotice::success("notice.operation_complete", "done");
        let warning = TransientNotice::warning("notice.action_unavailable", "wait");
        let danger = TransientNotice::danger("notice.operation_failed", "failed");

        assert_eq!(info.variant, ToastVariant::Default);
        assert_eq!(info.duration, Duration::from_secs(3));
        assert_eq!(success.variant, ToastVariant::Success);
        assert_eq!(success.duration, Duration::from_secs(3));
        assert_eq!(warning.variant, ToastVariant::Warning);
        assert_eq!(warning.duration, Duration::from_secs(5));
        assert_eq!(danger.variant, ToastVariant::Danger);
        assert_eq!(danger.duration, Duration::from_secs(6));
    }

    #[test]
    fn notice_can_carry_a_reveal_action() {
        let path = PathBuf::from(r"C:\AstraBrew\example.txt");
        let notice = TransientNotice::success("webview.download.saved", "saved")
            .with_action(TransientNoticeAction::RevealPath(path.clone()));

        assert_eq!(
            notice.action,
            Some(TransientNoticeAction::RevealPath(path))
        );
    }
}
