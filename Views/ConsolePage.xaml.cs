using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AstraBrew_Launcher.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace AstraBrew_Launcher.Views
{
    /// <summary>
    /// 控制台：酒馆进程的真实运行状态、实时输出与运行时指令。
    ///
    /// 这个页面不持有任何运行状态：进程、PID、端口、日志全在 <see cref="TavernRuntime"/> 里。
    /// 切走再回来接上的是同一个进程与同一份日志——以前这里用一段脚本时序假装启动，
    /// 离开页面「启动」就白跑了，日志也跟着没了。
    /// </summary>
    public sealed partial class ConsolePage : Page
    {
        private bool _autoFollow = true;

        public ConsolePage()
        {
            InitializeComponent();

            ActualThemeChanged += (_, _) => RenderLogs();
        }

        // ==================== 生命周期 ====================

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 先减后加：同一页面重复 Loaded 时不该累积多份订阅
            TavernRuntime.Changed -= Runtime_Changed;
            TavernRuntime.Changed += Runtime_Changed;
            TavernRuntime.Logged -= Runtime_Logged;
            TavernRuntime.Logged += Runtime_Logged;

            RenderLogs();
            ApplyState();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            TavernRuntime.Changed -= Runtime_Changed;
            TavernRuntime.Logged -= Runtime_Logged;
        }

        private void Runtime_Changed(object? sender, EventArgs e) => ApplyState();

        private void Runtime_Logged(object? sender, TavernLogEntry entry) => AppendLine(entry);

        // ==================== 状态 ====================

        private bool IsLight => ActualTheme == ElementTheme.Light;

        private void ApplyState()
        {
            TavernProcessState state = TavernRuntime.State;

            (string label, Color color, string glyph) = state.State switch
            {
                TavernRunState.Starting => ("启动中…", InfoColor, "\uE895"),
                TavernRunState.Running => ("运行中", SuccessColor, "\uE930"),
                TavernRunState.Stopping => ("停止中…", WarningColor, "\uE895"),
                TavernRunState.Stopped => ("已停止", WarningColor, "\uE71A"),
                TavernRunState.Failed => ("启动失败", ErrorColor, "\uEA39"),
                _ => ("未启动", MutedColor, "\uE71A"),
            };

            var brush = new SolidColorBrush(color);
            StatusGlyph.Glyph = glyph;
            StatusGlyph.Foreground = brush;
            StatusText.Text = label;
            StatusText.Foreground = brush;
            StatusPill.Background = new SolidColorBrush(Color.FromArgb(0x1F, color.R, color.G, color.B));

            bool running = state.IsRunning;
            bool busy = state.IsBusy;

            // 启停过程中不给任何指令：中途再点一次只会和正在跑的那次抢端口
            StartButton.Visibility = running || busy ? Visibility.Collapsed : Visibility.Visible;
            RestartButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            KillButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

            PidText.Text = state.Pid is { } pid ? $"PID {pid}" : "PID —";
            ModeText.Text = running || busy ? state.Mode : "—";

            NetworkButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

            bool lan = TavernSettings.Scope == ServiceScope.Lan;
            NetworkGlyph.Glyph = lan ? "\uE701" : "\uE774";
            NetworkText.Text = lan ? "局域网" : "互联网";
            var networkBrush = new SolidColorBrush(lan ? SuccessColor : ErrorColor);
            NetworkGlyph.Foreground = networkBrush;
            NetworkText.Foreground = networkBrush;

            // 没启动过也要显示当前实例的版本：标题写着「未安装」而侧边栏显示内置 v1.19.2
            // 会让人以为实例挂了
            string kernel = state.VersionLabel.Length > 0
                ? state.VersionLabel
                : TavernInstances.Current.KernelLabel;
            SessionText.Text = running
                ? $"SillyTavern {kernel} · 127.0.0.1:{state.Port}"
                : $"SillyTavern {kernel}";
        }

        private Color MutedColor => IsLight ? Color.FromArgb(255, 96, 96, 96) : Color.FromArgb(255, 160, 160, 160);

        private Color InfoColor => IsLight ? Color.FromArgb(255, 15, 95, 191) : Color.FromArgb(255, 92, 176, 255);

        private Color SuccessColor => IsLight ? Color.FromArgb(255, 12, 107, 38) : Color.FromArgb(255, 95, 208, 122);

        private Color WarningColor => IsLight ? Color.FromArgb(255, 138, 90, 0) : Color.FromArgb(255, 240, 180, 76);

        private Color ErrorColor => IsLight ? Color.FromArgb(255, 180, 41, 31) : Color.FromArgb(255, 255, 138, 138);

        private Color SystemColor => IsLight ? Color.FromArgb(255, 107, 63, 196) : Color.FromArgb(255, 192, 140, 255);

        private Color PrimaryTextColor =>
            IsLight ? Color.FromArgb(255, 26, 26, 26) : Color.FromArgb(255, 245, 245, 245);

        // ==================== 指令 ====================

        private void Start_Click(object sender, RoutedEventArgs e) => _ = TavernRuntime.StartAsync();

        private async void Stop_Click(object sender, RoutedEventArgs e)
        {
            if (!TavernRuntime.IsRunning)
            {
                return;
            }

            TavernProcessState state = TavernRuntime.State;

            if (!await ConfirmAsync(
                "停止酒馆服务？",
                $"正在进行的对话请求会被中断，端口 {state.Port} 将被释放。",
                "停止服务"))
            {
                return;
            }

            await TavernRuntime.StopAsync();
        }

        private async void Kill_Click(object sender, RoutedEventArgs e)
        {
            if (!TavernRuntime.IsRunning)
            {
                return;
            }

            TavernProcessState state = TavernRuntime.State;

            if (!await ConfirmAsync(
                "强制结束酒馆进程？",
                $"会直接结束 PID {state.Pid} 的整棵进程树，正在写的对话可能丢失。"
                    + "只有在「停止」没有反应时才需要这样做。",
                "强制结束",
                danger: true))
            {
                return;
            }

            await TavernRuntime.StopAsync(force: true);
        }

        private async void Restart_Click(object sender, RoutedEventArgs e)
        {
            if (!TavernRuntime.IsRunning)
            {
                return;
            }

            await TavernRuntime.RestartAsync();
        }

        /// <summary>不可逆操作前的确认框；返回 false 表示用户取消。</summary>
        private async Task<bool> ConfirmAsync(string title, string body, string primary, bool danger = false)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = title,
                Content = body,
                PrimaryButtonText = primary,
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };

            // DefaultButton 会被模板套上框架 AccentButtonStyle，圆角取自 ControlCornerRadius（4）
            dialog.Resources["ControlCornerRadius"] = new CornerRadius(8);

            if (danger
                && Application.Current.Resources.TryGetValue("DangerDialogButtonStyle", out object style)
                && style is Style dangerStyle)
            {
                dialog.PrimaryButtonStyle = dangerStyle;
            }

            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        // ==================== 日志 ====================

        private void RenderLogs()
        {
            LogStack.Children.Clear();

            foreach (TavernLogEntry entry in TavernRuntime.Logs)
            {
                LogStack.Children.Add(BuildLogLine(entry));
            }

            LogCountText.Text = $"{TavernRuntime.Logs.Count} 行";
            ScrollToEnd();
        }

        private void AppendLine(TavernLogEntry entry)
        {
            LogStack.Children.Add(BuildLogLine(entry));
            LogCountText.Text = $"{TavernRuntime.Logs.Count} 行";

            // 超过上限时最老的行已经被服务层丢掉，界面上也得跟着去掉一行
            while (LogStack.Children.Count > TavernRuntime.Logs.Count)
            {
                LogStack.Children.RemoveAt(0);
            }

            if (_autoFollow)
            {
                ScrollToEnd();
            }
        }

        private TextBlock BuildLogLine(TavernLogEntry entry)
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
                TavernLogKind.System => ("SYS ", SystemColor),
                TavernLogKind.Info => ("INFO", InfoColor),
                TavernLogKind.Success => ("OK  ", SuccessColor),
                TavernLogKind.Warning => ("WARN", WarningColor),
                TavernLogKind.Error => ("ERR ", ErrorColor),
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
                    entry.Kind == TavernLogKind.Output ? PrimaryTextColor : color),
            });

            return line;
        }

        /// <summary>
        /// 自动跟随下我们主动滚到的位置。
        /// 用它区分「用户往上拖了」与「刚追加了一行、内容变长」——
        /// 只看「离底部多远」的话，酒馆刷屏时会一路把自己判成「已暂停跟随」。
        /// </summary>
        private double _followTarget;

        private void ScrollToEnd()
        {
            LogScroll.UpdateLayout();
            _followTarget = LogScroll.ScrollableHeight;
            LogScroll.ChangeView(null, _followTarget, null, true);
        }

        private void LogScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (!_autoFollow)
            {
                // 用户自己拖回底部就恢复跟随
                if (LogScroll.VerticalOffset >= LogScroll.ScrollableHeight - 8)
                {
                    SetFollow(true);
                }

                return;
            }

            // 明显低于我们上次滚到的位置 = 用户往上翻了
            if (LogScroll.VerticalOffset < _followTarget - 40)
            {
                SetFollow(false);
            }
        }

        private void SetFollow(bool follow)
        {
            _autoFollow = follow;
            FollowButton.Visibility = follow ? Visibility.Collapsed : Visibility.Visible;
            FollowStateText.Text = follow ? "自动跟随" : "已暂停跟随";
        }

        private void Follow_Click(object sender, RoutedEventArgs e)
        {
            SetFollow(true);
            ScrollToEnd();
        }

        private void Clear_Click(object sender, RoutedEventArgs e) => TavernRuntime.ClearLogs();

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
                var builder = new StringBuilder();

                foreach (TavernLogEntry entry in TavernRuntime.Logs)
                {
                    builder.AppendLine(
                        $"{entry.Time:HH:mm:ss}  {entry.Kind.ToString().ToUpperInvariant(),-7}{entry.Text}");
                }

                await File.WriteAllTextAsync(path, builder.ToString());
                AppNotify.Success("日志已导出", path);
            }
            catch (Exception ex)
            {
                AppNotify.Error("导出失败", ex.Message);
            }
        }

        // ==================== 设备访问 ====================

        private async void NetworkButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowAccessDialogAsync();
        }

        private async Task ShowAccessDialogAsync()
        {
            TavernProcessState state = TavernRuntime.State;
            if (!state.IsRunning)
            {
                return;
            }

            bool lan = TavernSettings.Scope == ServiceScope.Lan;
            (string? v4, string? v6) = NetworkInfo.LocalAddresses();

            var body = new StackPanel { Spacing = 14 };

            if (v4 is null && v6 is null)
            {
                body.Children.Add(new TextBlock
                {
                    Text = "没有检测到可用的局域网地址。请确认这台机器已连上网络（或已开启 Wi-Fi / 网线）。",
                    FontSize = 12.5,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(PrimaryTextColor),
                });
            }
            else
            {
                // 用等分列而不是定宽卡片：弹窗宽度由主题决定，写死 316 会在窄屏上被裁掉一半
                var cards = new Grid { ColumnSpacing = 12 };

                if (v4 is not null)
                {
                    cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                }

                if (v6 is not null)
                {
                    cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                }

                int column = 0;

                if (v4 is not null)
                {
                    FrameworkElement card = BuildAccessCard("IPv4", v4, $"http://{v4}:{state.Port}/");
                    Grid.SetColumn(card, column++);
                    cards.Children.Add(card);
                }

                if (v6 is not null)
                {
                    FrameworkElement card = BuildAccessCard("IPv6", v6, $"http://[{v6}]:{state.Port}/");
                    Grid.SetColumn(card, column);
                    cards.Children.Add(card);
                }

                body.Children.Add(cards);
            }

            body.Children.Add(new TextBlock
            {
                Text = lan
                    ? "手机、平板与这台电脑连同一个 Wi-Fi 就能打开上面的地址；访问不了时先看防火墙是否放行。"
                    : "公网访问需要路由器端口映射或反向代理，地址那一栏填的是本机网卡地址。",
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(MutedColor),
            });

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = BuildAccessHeader(lan, state.Port),
            };
            dialog.Resources["ContentDialogMaxWidth"] = 780.0;
            dialog.Resources["ControlCornerRadius"] = new CornerRadius(8);

            // 自建底栏：ContentDialog 自带的按钮区会把唯一那颗按钮拉成整行宽条
            var close = new Button
            {
                Content = "关闭",
                Style = (Style)Application.Current.Resources["AccentActionButtonStyle"],
            };
            close.Click += (_, _) => dialog.Hide();

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };
            footer.Children.Add(close);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(body);
            Grid.SetRow(footer, 1);
            root.Children.Add(footer);

            dialog.Content = root;

            await dialog.ShowAsync();
        }

        private UIElement BuildAccessHeader(bool lan, int port)
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
                Text = $"{(lan ? "局域网" : "互联网")} · 端口 {port} · 把地址复制到手机浏览器",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(MutedColor),
            });

            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Margin = new Thickness(0, 4, 0, 2),
            };

            header.Children.Add(new Border
            {
                Width = 38,
                Height = 38,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(0x1F, 0x2B, 0x88, 0xD8)),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new FontIcon
                {
                    Glyph = lan ? "\uE701" : "\uE774",
                    FontSize = 16,
                    Foreground = new SolidColorBrush(InfoColor),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });

            header.Children.Add(title);
            return header;
        }

        private FrameworkElement BuildAccessCard(string label, string address, string url)
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
                IsReadOnly = true,
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

            return new Border
            {
                Padding = new Thickness(14),
                HorizontalAlignment = HorizontalAlignment.Stretch,
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
    }
}
