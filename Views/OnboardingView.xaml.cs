using System;
using System.Threading;
using System.Threading.Tasks;
using AstraBrew_Launcher.Services;
using AstraBrew_Launcher.Views.Onboarding;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace AstraBrew_Launcher.Views
{
    /// <summary>
    /// 首次启动引导（四步：数据迁移 → 检查网络 → 运行环境 → 添加实例）。
    ///
    /// 关键约定：
    /// · 只在「已完成引导」标记不存在时由 MainWindow 显示；
    /// · 写标记的时机只有两处：最后一步「完成」与任意一步「跳过引导」；
    /// · 写标记与关闭界面都在 <see cref="Finish"/> 里完成，不会出现「界面关了但没记上」的中间态；
    /// · 每一步是一个独立的 <see cref="IOnboardingStep"/> 面板，外壳只负责切步骤、算页脚、
    ///   以及在离开时取消上一步还在跑的任务。
    /// </summary>
    public sealed partial class OnboardingView : UserControl
    {
        private const int TotalSteps = 4;

        private static readonly (string Title, string Description)[] Steps =
        {
            ("把旧数据搬过来", "以前的聊天和角色能一起搬过来。不搬也行。"),
            ("先看网络通不通", "能上网、能开网页、能下载，才装得动酒馆。"),
            ("装好要用的工具", "酒馆跑起来，得先有几个小工具撑着。"),
            ("选一个酒馆用", "用你装好的，或者让启动器装一份。"),
        };

        private readonly OnboardingRailItem[] _rail;

        private readonly IOnboardingStep[] _panels;

        private int _step = 1;

        private bool _closing;

        private CancellationTokenSource? _stepToken;

        /// <summary>引导结束（标记已写入）后触发，由 MainWindow 负责把浮层收起来。</summary>
        public event EventHandler<OnboardingResult>? Finished;

        public OnboardingView()
        {
            InitializeComponent();

            _rail = new[] { Rail1, Rail2, Rail3, Rail4 };
            _panels = new IOnboardingStep[] { Step1, Step2, Step3, Step4 };

            foreach (IOnboardingStep panel in _panels)
            {
                panel.StateChanged += Panel_StateChanged;
            }

            Step4.BackToRuntimeRequested += (_, _) => GoToStep(3);
        }

        /// <summary>重置到第 1 步并恢复可见状态（每次展示前调用）。</summary>
        public void Restart()
        {
            _closing = false;
            CancelStep();

            Root.Opacity = 1;
            Root.IsHitTestVisible = true;

            foreach (IOnboardingStep panel in _panels)
            {
                try
                {
                    panel.Reset();
                }
                catch (Exception)
                {
                    // 单步复位失败不该挡住整轮重播
                }
            }

            GoToStep(1);
        }

        // ==================== 步骤切换 ====================

        private void GoToStep(int step)
        {
            if (_closing || step < 1 || step > TotalSteps)
            {
                return;
            }

            // 离场即取消：上一步的扫描 / 测速 / 安装没有理由继续跑
            CancelStep();

            _step = step;

            Step1.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
            Step2.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
            Step3.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
            Step4.Visibility = step == 4 ? Visibility.Visible : Visibility.Collapsed;

            StepEyebrow.Text = $"第 {step} 步，共 {TotalSteps} 步";
            StepTitle.Text = Steps[step - 1].Title;
            StepDescription.Text = Steps[step - 1].Description;

            BackButton.Visibility = step > 1 ? Visibility.Visible : Visibility.Collapsed;

            UpdateRail(step);
            UpdateFooter();

            _stepToken = new CancellationTokenSource();
            _ = EnterStepAsync(_panels[step - 1], _stepToken.Token);
        }

        private async Task EnterStepAsync(IOnboardingStep panel, CancellationToken token)
        {
            try
            {
                await panel.EnterAsync(token);
            }
            catch (OperationCanceledException)
            {
                // 用户切走了，正常
            }
            catch (Exception ex)
            {
                AppNotify.Error("这一步没能准备好", ex.Message);
            }
        }

        private void CancelStep()
        {
            _stepToken?.Cancel();
            _stepToken?.Dispose();
            _stepToken = null;

            if (_step >= 1 && _step <= _panels.Length)
            {
                _panels[_step - 1].Leave();
            }
        }

        private void UpdateRail(int current)
        {
            for (int i = 0; i < _rail.Length; i++)
            {
                int index = i + 1;
                _rail[i].SetState(index == current
                    ? RailState.Active
                    : index < current
                        ? RailState.Done
                        : RailState.Idle);
            }
        }

        private void Panel_StateChanged(object? sender, EventArgs e)
        {
            if (sender is IOnboardingStep panel && ReferenceEquals(panel, _panels[_step - 1]))
            {
                UpdateFooter();
            }
        }

        private void UpdateFooter()
        {
            if (_panels.Length < _step)
            {
                return;
            }

            IOnboardingStep panel = _panels[_step - 1];

            NextButton.Content = _step == TotalSteps ? "完成" : "下一步";
            NextButton.IsEnabled = panel.CanContinue && !panel.IsBusy;

            ToolTipService.SetToolTip(NextButton, panel.CanContinue ? null : panel.BlockingReason);

            // 长任务在跑时不拦「跳过」，但会先把它取消掉（Finish 里做）
            SkipButton.IsEnabled = true;
        }

        // ==================== 底部操作 ====================

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_step >= TotalSteps)
            {
                Finish(OnboardingResult.Completed);
                return;
            }

            GoToStep(_step + 1);
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (_step > 1)
            {
                GoToStep(_step - 1);
            }
        }

        private void Skip_Click(object sender, RoutedEventArgs e)
        {
            Finish(OnboardingResult.Skipped);
        }

        /// <summary>
        /// 唯一的收尾入口：先写标记，再淡出并通知 MainWindow。
        /// 两种结束方式（完成 / 跳过）都走这里，保证行为一致。
        /// </summary>
        private void Finish(OnboardingResult result)
        {
            if (_closing)
            {
                return;
            }

            _closing = true;

            CancelStep();

            // 跳过时把「还没走到的步骤」画成虚线槽：淡出只有 180ms，但至少不会让
            // 用户看到一串「已完成」的勾——那是没做过的事。
            if (result == OnboardingResult.Skipped)
            {
                for (int i = _step - 1; i < _rail.Length; i++)
                {
                    _rail[i].SetState(RailState.Skipped);
                }
            }

            // ★ 标记写入时机
            OnboardingState.MarkCompleted(result);

            Root.IsHitTestVisible = false;

            var fade = new DoubleAnimation
            {
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            };
            Storyboard.SetTarget(fade, Root);
            Storyboard.SetTargetProperty(fade, "(UIElement.Opacity)");

            var storyboard = new Storyboard();
            storyboard.Children.Add(fade);
            storyboard.Completed += (_, _) => Finished?.Invoke(this, result);
            storyboard.Begin();
        }
    }
}
