using System;
using System.Threading.Tasks;
using AstraBrew_Launcher.Services;
using AstraBrew_Launcher.Views.Controls;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace AstraBrew_Launcher.Views
{
    /// <summary>
    /// 首次启动引导（三步，原型：检测结果为模拟数据）。
    ///
    /// 关键约定：
    /// · 只在「已完成引导」标记不存在时由 MainWindow 显示；
    /// · 写标记的时机只有三处：第 3 步「完成」/ 任意一步「跳过引导」/ 第 1 步选「用过」后点「下一步」；
    /// · 写标记与关闭界面在同一个方法 <see cref="Finish"/> 里完成，不会出现「界面关了但没记上」的中间态。
    /// </summary>
    public sealed partial class OnboardingView : UserControl
    {
        private const int TotalSteps = 3;

        /// <summary>原型：固定的模拟检测结果，顺序与 StepPanel2 里的四行一致。</summary>
        private static readonly CheckState[] SimulatedResults =
        {
            CheckState.Ready,    // NodeJS
            CheckState.Ready,    // Git / MinGit
            CheckState.Update,   // PM2
            CheckState.Missing,  // 酒馆本体
        };

        private readonly (Border Idle, Border Active, Border Done, TextBlock Title)[] _railItems;

        private int _step = 1;
        private int _checkToken;
        private bool _closing;

        /// <summary>引导结束（标记已写入）后触发，由 MainWindow 负责把浮层收起来。</summary>
        public event EventHandler<OnboardingResult>? Finished;

        public OnboardingView()
        {
            InitializeComponent();

            _railItems = new[]
            {
                (Rail1Idle, Rail1Active, Rail1Done, Rail1Title),
                (Rail2Idle, Rail2Active, Rail2Done, Rail2Title),
                (Rail3Idle, Rail3Active, Rail3Done, Rail3Title),
            };
        }

        /// <summary>重置到第 1 步并恢复可见状态（每次展示前调用）。</summary>
        public void Restart()
        {
            _closing = false;
            _checkToken++;

            Root.Opacity = 1;
            Root.IsHitTestVisible = true;

            OptionUsed.IsChecked = false;
            OptionNew.IsChecked = false;

            GoToStep(1);
        }

        // ==================== 步骤切换 ====================

        private void GoToStep(int step)
        {
            if (_closing)
            {
                return;
            }

            _step = step;

            StepPanel1.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
            StepPanel2.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
            StepPanel3.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;

            StepEyebrow.Text = $"第 {step} 步 · 共 {TotalSteps} 步";
            (StepTitle.Text, StepDescription.Text) = step switch
            {
                2 => ("检查运行环境", "酒馆依赖 NodeJS、Git 与 PM2，启动器负责托管它们，这里做一次快速确认。"),
                3 => ("检查使用环境", "确认浏览器与内存是否够用，别等打开对话页才发现问题。"),
                _ => ("你之前用过酒馆吗？", "有经验的话可以直接进入主界面，我们不再占用你的时间。"),
            };

            BackButton.Visibility = step > 1 ? Visibility.Visible : Visibility.Collapsed;
            NextButton.Content = step == TotalSteps ? "完成" : "下一步";
            NextButton.IsEnabled = step > 1 || HasExperienceChoice();

            UpdateRail(step);

            if (step == 2)
            {
                _ = RunChecksAsync();
            }
            else if (step == 3)
            {
                UpdateMemory();
            }
        }

        private void UpdateRail(int current)
        {
            for (int i = 0; i < _railItems.Length; i++)
            {
                var item = _railItems[i];
                int index = i + 1;
                bool isActive = index == current;
                bool isDone = index < current;

                item.Idle.Visibility = !isActive && !isDone ? Visibility.Visible : Visibility.Collapsed;
                item.Active.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
                item.Done.Visibility = isDone ? Visibility.Visible : Visibility.Collapsed;
                item.Title.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        // ==================== 第 1 步 ====================

        private bool HasExperienceChoice()
        {
            return OptionUsed.IsChecked == true || OptionNew.IsChecked == true;
        }

        private void Experience_Checked(object sender, RoutedEventArgs e)
        {
            // 初始化期间 RadioButton 会先于按钮就绪触发，这里做个保护
            if (NextButton is not null)
            {
                NextButton.IsEnabled = HasExperienceChoice();
            }
        }

        // ==================== 第 2 步 ====================

        private async Task RunChecksAsync()
        {
            int token = ++_checkToken;

            var rows = new[] { RowNode, RowGit, RowPm2, RowTavern };

            CheckSpinner.Visibility = Visibility.Visible;
            CheckSpinner.IsActive = true;
            CheckSummary.Text = "正在检测运行环境…";

            foreach (var row in rows)
            {
                row.SetState(CheckState.Pending);
            }

            // 逐项出结果，模拟真实检测的耗时差异
            for (int i = 0; i < rows.Length; i++)
            {
                await Task.Delay(420);
                if (token != _checkToken || _closing)
                {
                    return;
                }

                rows[i].SetState(SimulatedResults[i]);
            }

            await Task.Delay(180);
            if (token != _checkToken || _closing)
            {
                return;
            }

            int missing = 0;
            foreach (var state in SimulatedResults)
            {
                if (state == CheckState.Missing)
                {
                    missing++;
                }
            }

            CheckSpinner.IsActive = false;
            CheckSpinner.Visibility = Visibility.Collapsed;
            CheckSummary.Text = missing == 0
                ? $"检测完成 · {rows.Length} 项全部就绪"
                : $"检测完成 · {rows.Length - missing} 项就绪，{missing} 项待安装";
        }

        private void Recheck_Click(object sender, RoutedEventArgs e)
        {
            _ = RunChecksAsync();
        }

        // ==================== 第 3 步 ====================

        private void MemoryDemo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MemoryValue is not null)
            {
                UpdateMemory();
            }
        }

        private void UpdateMemory()
        {
            // 原型：用下拉框模拟三档内存，真实实现读 GlobalMemoryStatusEx
            double gigabytes = MemoryDemo.SelectedIndex switch
            {
                0 => 2.0,
                1 => 6.0,
                _ => 16.0,
            };

            MemoryValue.Text = $"{gigabytes:0.0} GB";

            MemoryLowBanner.Visibility = gigabytes < 4 ? Visibility.Visible : Visibility.Collapsed;
            MemoryMidBanner.Visibility = gigabytes >= 4 && gigabytes <= 8 ? Visibility.Visible : Visibility.Collapsed;
            MemoryHighBanner.Visibility = gigabytes > 8 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ==================== 底部操作 ====================

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_step == 1)
            {
                if (OptionUsed.IsChecked == true)
                {
                    Finish(OnboardingResult.Experienced);
                    return;
                }

                GoToStep(2);
                return;
            }

            if (_step == TotalSteps)
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
        /// 三种结束方式（完成 / 跳过 / 用过）都走这里，保证行为一致。
        /// </summary>
        private void Finish(OnboardingResult result)
        {
            if (_closing)
            {
                return;
            }

            _closing = true;

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
