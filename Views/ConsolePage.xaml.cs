using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

namespace AstraBrew_Launcher.Views
{
    /// <summary>
    /// 控制台：酒馆运行状态、实时日志与运行时指令。
    /// 页面只负责展示和下发命令；这里没有真实子进程，启动 / 停止用一段脚本时序模拟，
    /// 保证状态胶囊、日志染色和按钮组合都能被逐个看到。
    /// </summary>
    public sealed partial class ConsolePage : Page
    {
        private enum RunState
        {
            NotStarted,
            Starting,
            Running,
            Stopping,
            Stopped,
            Failed,
        }

        private enum LogKind
        {
            System,
            Info,
            Success,
            Warning,
            Error,
            Output,
        }

        private readonly record struct LogEntry(DateTime Time, LogKind Kind, string Text);

        private const int MaxLogLines = 2000;
        private const int ServicePort = 8000;
        private const int ServicePid = 14208;

        private readonly List<LogEntry> _logs = new();
        private readonly Queue<(int Delay, Action Step)> _script = new();
        private readonly DispatcherQueueTimer _timer;

        private RunState _state = RunState.NotStarted;
        private bool _autoFollow = true;
        private int _elapsed;
        private bool _portConflictPending = true;
        private bool _lanMode = true;

        public ConsolePage()
        {
            InitializeComponent();

            _timer = DispatcherQueue.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(100);
            _timer.Tick += OnTick;

            ActualThemeChanged += (_, _) => RenderLogs();

            Append(LogKind.System, "控制台就绪，等待运行时指令");
            Append(LogKind.System, @"工作目录 %AppData%\AstraBrew Launcher\lib\tavern");
            SessionText.Text = "SillyTavern v1.12.9";
            ApplyState();
        }

        // ==================== 状态 ====================

        private bool IsLight => ActualTheme == ElementTheme.Light;

        private void SetState(RunState state)
        {
            _state = state;
            ApplyState();
        }

        private void ApplyState()
        {
            (string label, Color color, string glyph) = _state switch
            {
                RunState.Starting => ("启动中…", InfoColor, "\uE895"),
                RunState.Running => ("运行中", SuccessColor, "\uE930"),
                RunState.Stopping => ("停止中…", WarningColor, "\uE895"),
                RunState.Stopped => ("已停止", WarningColor, "\uE71A"),
                RunState.Failed => ("启动失败", ErrorColor, "\uEA39"),
                _ => ("未启动", MutedColor, "\uE71A"),
            };

            var brush = new SolidColorBrush(color);
            StatusGlyph.Glyph = glyph;
            StatusGlyph.Foreground = brush;
            StatusText.Text = label;
            StatusText.Foreground = brush;
            StatusPill.Background = new SolidColorBrush(Color.FromArgb(0x1F, color.R, color.G, color.B));

            bool running = _state == RunState.Running;
            bool busy = _state is RunState.Starting or RunState.Stopping;

            StartButton.Visibility = running || busy ? Visibility.Collapsed : Visibility.Visible;
            RestartButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            KillButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

            PidText.Text = running ? $"PID {ServicePid}" : "PID —";
            ModeText.Text = running ? "Direct · PM2 托管" : "—";
            NetworkButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            NetworkGlyph.Glyph = _lanMode ? "\uE701" : "\uE774";
            NetworkText.Text = _lanMode ? "局域网" : "互联网";
            var networkBrush = new SolidColorBrush(_lanMode ? SuccessColor : ErrorColor);
            NetworkGlyph.Foreground = networkBrush;
            NetworkText.Foreground = networkBrush;

            if (running)
            {
                SessionText.Text = $"SillyTavern v1.12.9 · 127.0.0.1:{ServicePort}";
            }
            else
            {
                SessionText.Text = "SillyTavern v1.12.9";
            }
        }

        private Color MutedColor => IsLight ? Color.FromArgb(255, 96, 96, 96) : Color.FromArgb(255, 160, 160, 160);

        private Color InfoColor => IsLight ? Color.FromArgb(255, 15, 95, 191) : Color.FromArgb(255, 92, 176, 255);

        private Color SuccessColor => IsLight ? Color.FromArgb(255, 12, 107, 38) : Color.FromArgb(255, 95, 208, 122);

        private Color WarningColor => IsLight ? Color.FromArgb(255, 138, 90, 0) : Color.FromArgb(255, 240, 180, 76);

        private Color ErrorColor => IsLight ? Color.FromArgb(255, 180, 41, 31) : Color.FromArgb(255, 255, 138, 138);

        private Color SystemColor => IsLight ? Color.FromArgb(255, 107, 63, 196) : Color.FromArgb(255, 192, 140, 255);

        // ==================== 指令 ====================

        private void Start_Click(object sender, RoutedEventArgs e)
        {
            if (_state is RunState.Starting or RunState.Stopping or RunState.Running)
            {
                return;
            }

            if (_portConflictPending)
            {
                FailWithPortConflict();
                return;
            }

            BeginStart();
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            if (_state != RunState.Running)
            {
                return;
            }

            SetState(RunState.Stopping);
            Append(LogKind.Info, "正在停止服务…");
            Schedule(700, () =>
            {
                SetState(RunState.Stopped);
                Append(LogKind.Success, "服务已停止，端口已释放");
            });
        }

        private void Kill_Click(object sender, RoutedEventArgs e)
        {
            if (_state != RunState.Running)
            {
                return;
            }

            _script.Clear();
            _timer.Stop();
            SetState(RunState.Stopped);
            Append(LogKind.Warning, $"已强制结束进程树（PID {ServicePid}），未落盘的对话可能丢失");
        }

        private void Restart_Click(object sender, RoutedEventArgs e)
        {
            if (_state != RunState.Running)
            {
                return;
            }

            SetState(RunState.Stopping);
            Append(LogKind.Info, "正在重启运行时…");
            Schedule(600, BeginStart);
        }

        private void BeginStart()
        {
            SetState(RunState.Starting);
            _script.Clear();
            _timer.Stop();
            Append(LogKind.System, "正在启动 SillyTavern v1.12.9");
            Schedule(480, () => Append(LogKind.Info, "加载配置文件 config.yaml 成功"));
            Schedule(340, () => Append(LogKind.Info, "已加载 12 个扩展，其中 9 个启用"));
            Schedule(420, () => Append(LogKind.Warning, "扩展「变量脚本」未通过完整性校验，已跳过加载"));
            Schedule(380, () => Append(LogKind.Info, "角色卡索引完成：48 张 / 26.4 MB"));
            Schedule(520, () => Append(LogKind.Info, $"HTTP 服务已监听 http://127.0.0.1:{ServicePort}"));
            Schedule(300, () =>
            {
                SetState(RunState.Running);
                Append(LogKind.Success, $"启动完成，耗时 4.82s（PID {ServicePid}）");
                Schedule(1400, () => Append(LogKind.Output, "POST /api/chat 200 · 1.42s · 642 tokens"));
            });
        }

        private void FailWithPortConflict()
        {
            SetState(RunState.Failed);
            Append(LogKind.Error, $"端口 {ServicePort} 已被占用，启动无法继续");
            _ = ShowPortConflictAsync();
        }

        private void Schedule(int delayMs, Action step)
        {
            _script.Enqueue((delayMs, step));
            if (!_timer.IsRunning)
            {
                _elapsed = 0;
                _timer.Start();
            }
        }

        private void OnTick(DispatcherQueueTimer sender, object args)
        {
            if (_script.Count == 0)
            {
                _timer.Stop();
                return;
            }

            _elapsed += 100;
            if (_elapsed < _script.Peek().Delay)
            {
                return;
            }

            _elapsed = 0;
            Action step = _script.Dequeue().Step;
            step();
        }

        // ==================== 日志 ====================

        private void Append(LogKind kind, string text)
        {
            var entry = new LogEntry(DateTime.Now, kind, text);
            _logs.Add(entry);

            if (_logs.Count > MaxLogLines)
            {
                _logs.RemoveAt(0);
                RenderLogs();
                return;
            }

            LogStack.Children.Add(BuildLogLine(entry));
            LogCountText.Text = $"{_logs.Count} 行";
            ScrollToEnd();
        }

        private void RenderLogs()
        {
            LogStack.Children.Clear();
            foreach (LogEntry entry in _logs)
            {
                LogStack.Children.Add(BuildLogLine(entry));
            }

            LogCountText.Text = $"{_logs.Count} 行";
            ScrollToEnd();
        }

        private TextBlock BuildLogLine(LogEntry entry)
        {
            var line = new TextBlock
            {
                Style = (Style)Resources["LogLineTextStyle"],
                TextWrapping = TextWrapping.Wrap,
            };

            line.Inlines.Add(new Run
            {
                Text = entry.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  ",
                Foreground = new SolidColorBrush(
                    IsLight ? Color.FromArgb(255, 138, 138, 138) : Color.FromArgb(255, 128, 128, 128)),
            });

            (string tag, Color color) = entry.Kind switch
            {
                LogKind.System => ("SYS ", SystemColor),
                LogKind.Info => ("INFO", InfoColor),
                LogKind.Success => ("OK  ", SuccessColor),
                LogKind.Warning => ("WARN", WarningColor),
                LogKind.Error => ("ERR ", ErrorColor),
                _ => ("OUT ", MutedColor),
            };

            line.Inlines.Add(new Run
            {
                Text = tag + "  ",
                Foreground = new SolidColorBrush(color),
                FontWeight = FontWeights.SemiBold,
            });

            line.Inlines.Add(new Run
            {
                Text = entry.Text,
                Foreground = new SolidColorBrush(
                    entry.Kind == LogKind.Output ? PrimaryTextColor : color),
            });

            return line;
        }

        private Color PrimaryTextColor =>
            IsLight ? Color.FromArgb(255, 26, 26, 26) : Color.FromArgb(255, 245, 245, 245);

        private void ScrollToEnd()
        {
            if (!_autoFollow)
            {
                return;
            }

            LogScroll.UpdateLayout();
            LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, true);
        }

        private void LogScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            bool atBottom = LogScroll.VerticalOffset >= LogScroll.ScrollableHeight - 6;
            if (atBottom == _autoFollow)
            {
                return;
            }

            _autoFollow = atBottom;
            FollowButton.Visibility = atBottom ? Visibility.Collapsed : Visibility.Visible;
            FollowStateText.Text = atBottom ? "自动跟随" : "已暂停跟随";
        }

        private void Follow_Click(object sender, RoutedEventArgs e)
        {
            _autoFollow = true;
            FollowButton.Visibility = Visibility.Collapsed;
            FollowStateText.Text = "自动跟随";
            ScrollToEnd();
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            _logs.Clear();
            _script.Clear();
            _timer.Stop();
            Append(LogKind.System, "已清空本次会话日志");
        }

        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Temp",
                "astrabrew-launcher");
            Directory.CreateDirectory(directory);

            string path = Path.Combine(
                directory,
                $"tavern-console-{DateTime.Now:yyyyMMdd-HHmmss}.log");

            try
            {
                var builder = new System.Text.StringBuilder();
                foreach (LogEntry entry in _logs)
                {
                    builder.AppendLine($"{entry.Time:HH:mm:ss}  {entry.Kind.ToString().ToUpperInvariant(),-7}{entry.Text}");
                }

                await File.WriteAllTextAsync(path, builder.ToString());
                Append(LogKind.Success, $"日志已导出 {path}");
            }
            catch (Exception ex)
            {
                Append(LogKind.Error, $"导出失败 {ex.Message}");
            }
        }

        // ==================== 设备访问 ====================

        private async void NetworkButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowAccessDialogAsync();
        }

        private async Task ShowAccessDialogAsync()
        {
            if (_state != RunState.Running)
            {
                return;
            }

            var loading = new StackPanel
            {
                Spacing = 12,
                Height = 268,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            loading.Children.Add(new ProgressRing
            {
                IsActive = true,
                Width = 36,
                Height = 36,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            loading.Children.Add(new TextBlock
            {
                Text = "正在检测访问地址…",
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(PrimaryTextColor),
            });
            loading.Children.Add(new TextBlock
            {
                Text = _lanMode ? "局域网 · 同网段的设备可访问" : "互联网 · 需要公网 IP 与端口映射",
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(MutedColor),
            });

            var result = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            result.Children.Add(BuildAccessCard("IPv4", "192.168.31.64", $"http://192.168.31.64:{ServicePort}/"));
            result.Children.Add(BuildAccessCard("IPv6", "2408:8207:38d2:6a10:9f3c", $"http://[2408:8207:38d2:6a10:9f3c]:{ServicePort}/"));

            var host = new Grid();
            host.Children.Add(loading);
            host.Children.Add(result);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = BuildAccessHeader(),
            };
            dialog.Resources["ContentDialogMaxWidth"] = 780.0;

            // ContentDialog 自带的按钮区会把按钮拉成等分大块，这里自建底栏贴右下角。
            var retry = new Button { Content = "重新检测" };
            var close = new Button
            {
                Content = "关闭",
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            };
            retry.Click += (_, _) =>
            {
                loading.Visibility = Visibility.Visible;
                result.Visibility = Visibility.Collapsed;
                RevealLater(loading, result);
            };
            close.Click += (_, _) => dialog.Hide();

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };
            footer.Children.Add(retry);
            footer.Children.Add(close);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(host);
            Grid.SetRow(footer, 1);
            root.Children.Add(footer);
            dialog.Content = root;

            dialog.Opened += (_, _) => RevealLater(loading, result);

            await dialog.ShowAsync();
        }

        /// <summary>检测大约要 900ms，先给转圈再切到结果，避免弹窗瞬间跳变。</summary>
        private void RevealLater(UIElement loading, UIElement result)
        {
            var reveal = DispatcherQueue.CreateTimer();
            reveal.Interval = TimeSpan.FromMilliseconds(900);
            reveal.IsRepeating = false;
            reveal.Tick += (_, _) =>
            {
                reveal.Stop();
                loading.Visibility = Visibility.Collapsed;
                result.Visibility = Visibility.Visible;
            };
            reveal.Start();
        }

        private UIElement BuildAccessHeader()
        {
            var title = new StackPanel { Spacing = 2 };
            title.Children.Add(new TextBlock
            {
                Text = "设备访问",
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(PrimaryTextColor),
            });
            title.Children.Add(new TextBlock
            {
                Text = $"{(_lanMode ? "局域网" : "互联网")} · 端口 {ServicePort} · 扫码或把地址复制到浏览器",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(MutedColor),
            });

            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Margin = new Thickness(0, 4, 0, 2),
            };

            var tile = new Border
            {
                Width = 38,
                Height = 38,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(0x1F, 0x2B, 0x88, 0xD8)),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new FontIcon
                {
                    Glyph = _lanMode ? "\uE701" : "\uE774",
                    FontSize = 16,
                    Foreground = new SolidColorBrush(InfoColor),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            header.Children.Add(tile);
            header.Children.Add(title);
            return header;
        }

        private UIElement BuildAccessCard(string label, string address, string url)
        {
            var content = new StackPanel { Spacing = 10 };

            content.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(PrimaryTextColor),
            });

            content.Children.Add(new TextBox
            {
                Text = address,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Padding = new Thickness(10, 7, 10, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            });

            var copy = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Content = BuildCopyLabel(),
            };
            copy.Click += (_, _) =>
            {
                var package = new DataPackage();
                package.SetText(url);
                Clipboard.SetContent(package);
                copy.Content = BuildCopyLabel("已复制到剪贴板");
            };
            content.Children.Add(copy);

            content.Children.Add(BuildQrMock(url));

            content.Children.Add(new TextBlock
            {
                Text = "手机扫一扫 · 或把地址粘贴到浏览器",
                FontSize = 10.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(MutedColor),
            });

            return new Border
            {
                Width = 316,
                Padding = new Thickness(14),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(IsLight
                    ? Color.FromArgb(0x0A, 0, 0, 0)
                    : Color.FromArgb(0x0A, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(IsLight
                    ? Color.FromArgb(0x14, 0, 0, 0)
                    : Color.FromArgb(0x18, 255, 255, 255)),
                Child = content,
            };
        }

        private static UIElement BuildCopyLabel(string text = "复制地址")
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            row.Children.Add(new FontIcon { Glyph = "\uE8C8", FontSize = 12 });
            row.Children.Add(new TextBlock { Text = text, FontSize = 12 });
            return row;
        }

        /// <summary>
        /// 二维码示意：真机上这里应该由编码库生成，原型只按地址散列出一张
        /// 带定位角、时序线的稳定图案，保证每次打开形状一致而不是随机噪声。
        /// </summary>
        private UIElement BuildQrMock(string payload)
        {
            const int modules = 25;
            const int quiet = 2;
            const int total = modules + quiet * 2;
            const double side = 150;
            double cell = side / total;

            bool[,] dark = new bool[modules, modules];

            void Finder(int ox, int oy)
            {
                for (int y = 0; y < 7; y++)
                {
                    for (int x = 0; x < 7; x++)
                    {
                        bool ring = x is 0 or 6 || y is 0 or 6;
                        bool core = x is >= 2 and <= 4 && y is >= 2 and <= 4;
                        dark[ox + x, oy + y] = ring || core;
                    }
                }
            }

            Finder(0, 0);
            Finder(modules - 7, 0);
            Finder(0, modules - 7);

            uint seed = 2166136261;
            foreach (char c in payload)
            {
                seed = (seed ^ c) * 16777619;
            }

            for (int y = 0; y < modules; y++)
            {
                for (int x = 0; x < modules; x++)
                {
                    bool inFinder =
                        (x < 8 && y < 8)
                        || (x >= modules - 8 && y < 8)
                        || (x < 8 && y >= modules - 8);
                    if (inFinder)
                    {
                        continue;
                    }

                    if (y == 6 || x == 6)
                    {
                        dark[x, y] = (x + y) % 2 == 0;
                        continue;
                    }

                    seed = seed * 1664525 + 1013904223;
                    dark[x, y] = (seed >> 16) % 100 < 47;
                }
            }

            var canvas = new Canvas
            {
                Width = side,
                Height = side,
                Background = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            };

            for (int y = 0; y < modules; y++)
            {
                for (int x = 0; x < modules; x++)
                {
                    if (!dark[x, y])
                    {
                        continue;
                    }

                    var dot = new Rectangle
                    {
                        Width = cell,
                        Height = cell,
                        Fill = new SolidColorBrush(Color.FromArgb(255, 0, 0, 0)),
                    };
                    Canvas.SetLeft(dot, (x + quiet) * cell);
                    Canvas.SetTop(dot, (y + quiet) * cell);
                    canvas.Children.Add(dot);
                }
            }

            return new Border
            {
                Width = side,
                Height = side,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0, 0, 0)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = canvas,
            };
        }

        // ==================== 端口冲突 ====================

        private async Task ShowPortConflictAsync()
        {
            var body = new StackPanel { Spacing = 14 };

            body.Children.Add(new TextBlock
            {
                Text = $"端口 {ServicePort} 已被以下进程占用，启动无法继续。",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(PrimaryTextColor),
            });

            var list = new StackPanel { Spacing = 6 };
            foreach (string process in new[] { "PID 4412 · node.exe", "PID 12884 · SillyTavern.exe（上次异常退出残留）" })
            {
                list.Children.Add(new TextBlock
                {
                    Text = process,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    Foreground = new SolidColorBrush(PrimaryTextColor),
                });
            }

            body.Children.Add(new Border
            {
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(IsLight
                    ? Color.FromArgb(0x0A, 0, 0, 0)
                    : Color.FromArgb(0x0A, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(IsLight
                    ? Color.FromArgb(0x14, 0, 0, 0)
                    : Color.FromArgb(0x18, 255, 255, 255)),
                Child = list,
            });

            body.Children.Add(new TextBlock
            {
                Text = "强制释放会结束上述进程，未保存的对话可能丢失。",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(ErrorColor),
            });

            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            title.Children.Add(new FontIcon
            {
                Glyph = "\uE7BA",
                FontSize = 18,
                Foreground = new SolidColorBrush(ErrorColor),
                VerticalAlignment = VerticalAlignment.Center,
            });
            title.Children.Add(new TextBlock
            {
                Text = "端口被占用",
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(PrimaryTextColor),
                VerticalAlignment = VerticalAlignment.Center,
            });

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = title,
            };
            dialog.Resources["ContentDialogMaxWidth"] = 520.0;

            var cancel = new Button { Content = "取消" };
            var release = new Button { Content = "强制释放并启动" };
            if (Application.Current.Resources.TryGetValue("DangerDialogButtonStyle", out object style)
                && style is Style dangerStyle)
            {
                release.Style = dangerStyle;
            }

            cancel.Click += (_, _) => dialog.Hide();
            release.Click += (_, _) =>
            {
                dialog.Hide();
                _portConflictPending = false;
                Append(LogKind.System, $"已结束占用进程，释放端口 {ServicePort}");
                BeginStart();
            };

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };
            footer.Children.Add(release);
            footer.Children.Add(cancel);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(body);
            Grid.SetRow(footer, 1);
            root.Children.Add(footer);
            dialog.Content = root;

            await dialog.ShowAsync();
            if (_state == RunState.Failed && _portConflictPending)
            {
                Append(LogKind.Warning, "保留占用进程，启动已取消");
            }
        }
    }
}
