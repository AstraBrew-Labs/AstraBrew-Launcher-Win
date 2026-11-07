using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AstraBrew_Launcher.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace AstraBrew_Launcher.Views.Onboarding
{
    /// <summary>
    /// 「安装运行环境」弹窗：一个多任务下载器。
    ///
    /// 为什么**不允许随手关掉**：装到一半跑掉会留下半套环境——探测能过、一跑就报错，
    /// 比没装更糟。所以 <c>Closing</c> 里一律取消关闭，只有两种时刻放行：
    /// ① 还在倒计时（用户改主意）；② 安装结束（成功 / 失败 / 已取消）。
    ///
    /// 注意这不是「把用户锁死」：倒计时阶段有「取消」，安装中有「取消安装」，
    /// 取消后会等任务真正停下再放开关闭，不会留下还在跑的进程。
    /// </summary>
    public static class InstallTaskDialog
    {
        private const int CountdownSeconds = 3;

        /// <summary>一行任务对应的控件，按 key 索引。</summary>
        private sealed record RowControls(RuntimeTask Task, Border Pill, TextBlock PillText, ProgressBar Bar, TextBlock Log);

        /// <summary>
        /// 弹出安装窗口并执行。<paramref name="autoStart"/> 为 true 时倒计时 3 秒自动开始。
        /// 返回 null 表示用户在开始前就取消了。
        /// </summary>
        public static async Task<RuntimeBatchResult?> ShowAsync(
            XamlRoot root,
            IReadOnlyList<RuntimeTask> tasks,
            bool autoStart = true)
        {
            var rows = new List<RowControls>();
            var cts = new CancellationTokenSource();
            RuntimeBatchResult? outcome = null;
            bool allowClose = false;
            bool started = false;

            var body = new StackPanel { Spacing = 12 };

            var summary = new TextBlock
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Res("TextFillColorSecondaryBrush"),
                Text = autoStart ? $"{CountdownSeconds} 秒后自动开始" : "准备好了",
            };

            var overall = new ProgressBar { Height = 4, Maximum = 100, Value = 0 };

            foreach (RuntimeTask task in tasks)
            {
                body.Children.Add(BuildRow(task, rows));
            }

            body.Children.Add(new Rectangle
            {
                Height = 1,
                Fill = Res("DividerStrokeColorDefaultBrush"),
            });
            body.Children.Add(overall);
            body.Children.Add(summary);

            var start = new Button
            {
                Content = "现在开始",
                Style = (Style)Application.Current.Resources["AccentActionButtonStyle"],
            };

            var cancel = new Button
            {
                Content = "取消",
                Style = (Style)Application.Current.Resources["GhostButtonStyle"],
            };

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0),
            };
            footer.Children.Add(cancel);
            footer.Children.Add(start);

            var host = new StackPanel();
            host.Children.Add(body);
            host.Children.Add(footer);

            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = "装一下要用的工具",
                Content = host,
                DefaultButton = ContentDialogButton.None,
            };
            dialog.Resources["ContentDialogMaxWidth"] = 640.0;
            dialog.Resources["ContentDialogMinWidth"] = 420.0;
            dialog.Resources["ControlCornerRadius"] = new CornerRadius(8);

            dialog.Closing += (_, args) =>
            {
                if (!allowClose)
                {
                    args.Cancel = true;
                }
            };

            var countdown = dialog.DispatcherQueue.CreateTimer();
            int remaining = CountdownSeconds;

            async Task StartAsync()
            {
                if (started)
                {
                    return;
                }

                started = true;
                countdown.Stop();
                start.IsEnabled = false;
                start.Visibility = Visibility.Collapsed;

                var progress = new Progress<RuntimeTaskUpdate>(update => Apply(rows, overall, update));

                RuntimeBatchResult result = await RuntimeSetup.InstallAsync(tasks, progress, cts.Token);
                outcome = result;

                // 装完再探一次，让调用方拿到的卡片是新的；这里只负责把结论写清楚
                bool ok = result.Success;
                summary.Text = ok
                    ? "装好了，状态已更新。"
                    : result.Cancelled
                        ? "已取消。"
                        : result.Error is { Length: > 0 } error
                            ? error
                            : "有的没装上，可以到设置里重试。";
                summary.Foreground = ok ? Res("SystemFillColorSuccessBrush") : Res("TextFillColorSecondaryBrush");
                overall.Value = ok ? 100 : overall.Value;

                allowClose = true;
                start.Content = "关闭";
                start.IsEnabled = true;
                start.Visibility = Visibility.Visible;
                cancel.Visibility = Visibility.Collapsed;
            }

            countdown.Tick += (_, _) =>
            {
                remaining--;

                if (remaining > 0)
                {
                    summary.Text = $"{remaining} 秒后自动开始";
                    return;
                }

                _ = StartAsync();
            };

            start.Click += (_, _) =>
            {
                if (started)
                {
                    dialog.Hide();
                    return;
                }

                _ = StartAsync();
            };

            cancel.Click += (_, _) =>
            {
                if (!started)
                {
                    // 还没开始：直接放行，关掉即可
                    countdown.Stop();
                    allowClose = true;
                    dialog.Hide();
                    return;
                }

                cancel.IsEnabled = false;
                cancel.Content = "正在停…";
                cts.Cancel();
            };

            dialog.Opened += (_, _) =>
            {
                if (autoStart)
                {
                    countdown.Interval = TimeSpan.FromSeconds(1);
                    countdown.Start();
                }
            };

            await dialog.ShowAsync();

            cts.Dispose();

            return outcome;
        }

        // ==================== 行 ====================

        private static UIElement BuildRow(RuntimeTask task, List<RowControls> rows)
        {
            var grid = new Grid
            {
                ColumnSpacing = 12,
                Padding = new Thickness(0, 2, 0, 2),
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            grid.Children.Add(new Border
            {
                Width = 32,
                Height = 32,
                VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x14, 0x2B, 0x88, 0xD8)),
                CornerRadius = new CornerRadius(8),
                Child = new FontIcon
                {
                    Glyph = task.Glyph,
                    FontSize = 14,
                    Foreground = Res("AccentTextFillColorPrimaryBrush"),
                },
            });

            var info = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };

            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            head.Children.Add(new TextBlock
            {
                Text = task.Name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
            });
            head.Children.Add(new TextBlock
            {
                Text = task.Detail,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Res("TextFillColorTertiaryBrush"),
            });
            info.Children.Add(head);

            var bar = new ProgressBar { Height = 3, Maximum = 100, Value = 0 };
            info.Children.Add(bar);

            var log = new TextBlock
            {
                Text = "等着",
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Res("TextFillColorTertiaryBrush"),
            };
            info.Children.Add(log);

            Grid.SetColumn(info, 1);
            grid.Children.Add(info);

            var pillText = new TextBlock { Text = "等着", FontSize = 10.5 };
            var pill = new Border
            {
                Padding = new Thickness(8, 2, 8, 3),
                CornerRadius = new CornerRadius(8),
                VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x14, 0x80, 0x80, 0x80)),
                Child = pillText,
            };

            Grid.SetColumn(pill, 2);
            grid.Children.Add(pill);

            rows.Add(new RowControls(task, pill, pillText, bar, log));

            return grid;
        }

        private static void Apply(List<RowControls> rows, ProgressBar overall, RuntimeTaskUpdate update)
        {
            RowControls? row = rows.FirstOrDefault(item => item.Task.Key == update.Key);

            if (row is not null)
            {
                row.Bar.IsIndeterminate = update.Indeterminate;
                row.Bar.Value = Math.Clamp(update.Percent, 0, 100);
                row.Log.Text = update.LogLine is { Length: > 0 } line ? line : update.Message;

                if (update.Failed)
                {
                    row.PillText.Text = "没装上";
                    row.Pill.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1F, 0xC4, 0x2B, 0x1C));
                }
                else if (update.Message == "完成")
                {
                    row.PillText.Text = "好了";
                    row.Pill.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1F, 0x0F, 0x7B, 0x0F));
                }
                else if (update.Message == "准备中…")
                {
                    row.PillText.Text = "装着呢";
                    row.Pill.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1F, 0x2B, 0x88, 0xD8));
                }
                else if (update.Percent <= 0 && !update.Indeterminate)
                {
                    row.PillText.Text = "等着";
                }
                else
                {
                    row.PillText.Text = $"{update.Percent:0}%";
                }
            }

            double total = rows.Sum(item => item.Task.Weight * 100);
            double done = rows.Sum(item => item.Task.Weight * Math.Clamp(item.Bar.Value, 0, 100));
            overall.Value = total > 0 ? done / total * 100 : overall.Value;
        }

        private static Brush Res(string key) =>
            Application.Current.Resources.TryGetValue(key, out object value) && value is Brush brush
                ? brush
                : new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x80, 0x80, 0x80));
    }
}
