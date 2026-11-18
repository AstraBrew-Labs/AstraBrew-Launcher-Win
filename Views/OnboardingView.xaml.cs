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
            (L.Get("Setup.Step1.Title"), L.Get("Setup.Step1.Hint")),

            // 第 2 步是「装运行环境」、第 3 步才是「网络检测」：测速与源检查要用到 Git，
            // 环境还没装时那一步的检测根本跑不起来，顺序反了会让人误以为网络有问题。
            (L.Get("Setup.Step3.Title"), L.Get("Setup.Step3.Hint")),
            (L.Get("Setup.Step2.Title"), L.Get("Setup.Step2.Hint")),
            (L.Get("Setup.Step4.Title"), L.Get("Setup.Step4.Hint")),
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
            _panels = new IOnboardingStep[] { Step1, Step3, Step2, Step4 };

            foreach (IOnboardingStep panel in _panels)
            {
                panel.StateChanged += Panel_StateChanged;
            }

            Step4.BackToRuntimeRequested += (_, _) => GoToStep(2);
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

            // 可见性跟着 _panels 的顺序走：以后调换步骤只改数组，不必回来改这四行
            for (var i = 0; i < _panels.Length; i++)
            {
                if (_panels[i] is FrameworkElement element)
                {
                    element.Visibility = i == step - 1 ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            StepEyebrow.Text = L.F("Setup.Step.Eyebrow", step, TotalSteps);
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
                AppNotify.Error(L.Get("Setup.Notify.Failed.Title"), ex.Message);
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

            // 环境没装齐时，页脚那颗按钮变成「补全」：点一下就开装，装完自己变回「下一步」。
            // 这样用户不用在页面里再找一遍按钮，也不会对着灰掉的「下一步」干等。
            bool repair = panel.WantsPrimaryAction;

            NextButton.Content = L.Get(repair
                ? "Setup.Action.Fill"
                : _step == TotalSteps
                    ? "Setup.Action.Finish"
                    : "Setup.Action.Next");

            NextButton.IsEnabled = repair || (panel.CanContinue && !panel.IsBusy);

            ToolTipService.SetToolTip(
                NextButton,
                repair
                    ? L.Get("Setup.Action.FillTip")
                    : panel.CanContinue ? null : panel.BlockingReason);

            // 长任务在跑时不拦「跳过」，但会先把它取消掉（Finish 里做）
            SkipButton.IsEnabled = true;
        }

        // ==================== 底部操作 ====================

        private async void Next_Click(object sender, RoutedEventArgs e)
        {
            IOnboardingStep panel = _panels[_step - 1];

            // 「补全」只是就地装环境，不进下一步：装完按钮会重算，变成正常的「下一步」
            if (panel.WantsPrimaryAction)
            {
                await panel.PrimaryActionAsync();
                return;
            }

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
