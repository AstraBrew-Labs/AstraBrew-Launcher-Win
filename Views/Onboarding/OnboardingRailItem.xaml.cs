using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace AstraBrew_Launcher.Views.Onboarding
{
    /// <summary>步骤轨的一节状态：未到 / 正在做 / 已完成 / 跳过了。</summary>
    public enum RailState
    {
        Idle,
        Active,
        Done,
        Skipped,
    }

    /// <summary>
    /// 左侧时间轴的一节：圆点 + 标题 + 副标 + 连到下一节的竖线。
    ///
    /// 四种状态用四个叠在同一位置的圆圈表示（切显隐），连线颜色只由「本节是否完成」决定，
    /// 所以外壳不需要知道偏移量——四节按 71 的固定长度叠起来就是一条连续的线。
    /// </summary>
    public sealed partial class OnboardingRailItem : UserControl
    {
        public OnboardingRailItem()
        {
            InitializeComponent();
        }

        /// <summary>序号，从 1 开始。</summary>
        public int Number
        {
            get => _number;
            set
            {
                _number = value;
                NumberText.Text = value.ToString();
                ActiveNumberText.Text = value.ToString();
            }
        }

        /// <summary>步骤标题（一行，四个字以内最稳）。</summary>
        public string Title
        {
            get => TitleText.Text;
            set
            {
                TitleText.Text = value;
                UpdateAutomationName();
            }
        }

        /// <summary>步骤副标（一行，四个字以内）。</summary>
        public string Subtitle
        {
            get => SubtitleText.Text;
            set => SubtitleText.Text = value;
        }

        /// <summary>最后一节不画连线，否则线会拖到卡片底部外面。</summary>
        public bool IsLast
        {
            get => _isLast;
            set
            {
                _isLast = value;
                Connector.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private int _number = 1;

        private bool _isLast;

        private RailState _state = RailState.Idle;

        /// <summary>切换状态：当前步强调色实心 + 标题加粗，已完成打勾并点亮连线。</summary>
        public void SetState(RailState state)
        {
            _state = state;

            IdleDot.Visibility = state == RailState.Idle ? Visibility.Visible : Visibility.Collapsed;
            ActiveDot.Visibility = state == RailState.Active ? Visibility.Visible : Visibility.Collapsed;
            DoneDot.Visibility = state == RailState.Done ? Visibility.Visible : Visibility.Collapsed;
            SkippedDot.Visibility = state == RailState.Skipped ? Visibility.Visible : Visibility.Collapsed;

            TitleText.FontWeight = state == RailState.Active ? FontWeights.SemiBold : FontWeights.Normal;

            TitleText.Foreground = Res(state switch
            {
                RailState.Idle => "TextFillColorSecondaryBrush",
                RailState.Skipped => "TextFillColorTertiaryBrush",
                _ => "TextFillColorPrimaryBrush",
            });

            SubtitleText.Foreground = Res("TextFillColorTertiaryBrush");

            // 连线代表「走到下一节」这条路：本节做完了才点亮，跳过的画虚线
            Connector.Stroke = Res(state switch
            {
                RailState.Done => "AccentFillColorDefaultBrush",
                RailState.Skipped => "TextFillColorTertiaryBrush",
                _ => "DividerStrokeColorDefaultBrush",
            });

            Connector.StrokeDashArray = state == RailState.Skipped
                ? new DoubleCollection { 2, 2 }
                : null;

            UpdateAutomationName();
        }

        private void UpdateAutomationName()
        {
            string state = _state switch
            {
                RailState.Active => "正在进行",
                RailState.Done => "已完成",
                RailState.Skipped => "已跳过",
                _ => "未开始",
            };

            AutomationProperties.SetName(this, $"第 {_number} 步 {TitleText.Text}，{state}");
        }

        private static Brush Res(string key) =>
            Application.Current.Resources.TryGetValue(key, out object value) && value is Brush brush
                ? brush
                : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }
}
