using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            TavernRuntime.Cleared -= Runtime_Cleared;
            TavernRuntime.Cleared += Runtime_Cleared;

            // 启动模式 / 服务范围改了也要立刻反映到网络胶囊上
            TavernSettings.Changed -= Settings_Changed;
            TavernSettings.Changed += Settings_Changed;

            RenderLogs();
            ApplyState();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            TavernRuntime.Changed -= Runtime_Changed;
            TavernRuntime.Logged -= Runtime_Logged;
            TavernRuntime.Cleared -= Runtime_Cleared;
            TavernSettings.Changed -= Settings_Changed;
        }

        private void Settings_Changed(object? sender, EventArgs e) => ApplyState();

        private void Runtime_Changed(object? sender, EventArgs e) => ApplyState();

        private void Runtime_Logged(object? sender, TavernLogEntry entry) => AppendLine(entry);

        /// <summary>启动前日志被整体清空：列表要重画，否则旧行会一直留在界面上。</summary>
        private void Runtime_Cleared(object? sender, EventArgs e) => RenderLogs();

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

            // 胶囊说的是「现在这台机器能从哪儿访问」，跟着启动模式走：
            // 普通 / 桌面模式只绑本机，显示「局域网」会让人以为手机能连上，点开还是空的。
            AccessScope scope = CurrentScope();
            bool lan = scope == AccessScope.Lan;
            bool local = scope == AccessScope.Local;

            NetworkGlyph.Glyph = local ? "\uE72E" : lan ? "\uE701" : "\uE774";
            NetworkText.Text = local ? "本机" : lan ? "局域网" : "互联网";

            var networkBrush = new SolidColorBrush(local ? MutedColor : lan ? SuccessColor : ErrorColor);
            NetworkGlyph.Foreground = networkBrush;
            NetworkText.Foreground = networkBrush;
            ToolTipService.SetToolTip(NetworkButton, local
                ? "查看本机地址（普通模式不对外提供访问）"
                : "查看手机 / 平板访问地址");

            // 没启动过也要显示当前实例的版本：标题写着「未安装」而侧边栏显示内置 v1.19.2
            // 会让人以为实例挂了
            string kernel = state.VersionLabel.Length > 0
                ? state.VersionLabel
                : TavernInstances.Current.KernelLabel;
            SessionText.Text = running
                ? $"SillyTavern {kernel} · 127.0.0.1:{state.Port}"
                : $"SillyTavern {kernel}";
        }

        /// <summary>当前实例能被谁访问：决定胶囊文字与访问弹窗的内容。</summary>
        private enum AccessScope
        {
            Local,
            Lan,
            Wan,
        }

        /// <summary>
        /// 访问范围由启动模式决定，不是由 config.yaml 的 <c>listen</c> 决定。
        /// 「服务范围」这个设置只在服务器模式里有意义；普通 / 桌面模式就是本机自用，
        /// 拿 listen 的值去显示「局域网」只会误导人。
        /// </summary>
        private static AccessScope CurrentScope() => TavernSettings.Mode switch
        {
            LaunchMode.Server => TavernSettings.Scope == ServiceScope.Lan ? AccessScope.Lan : AccessScope.Wan,
            _ => AccessScope.Local,
        };

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

            AccessScope scope = CurrentScope();
            bool local = scope == AccessScope.Local;

            // 本机模式只列回环地址：普通模式下酒馆就绑在 127.0.0.1，
            // 把网卡地址摆出来等于承诺一个打不开的链接。
            (string? v4, string? v6) = local
                ? ("127.0.0.1", null)
                : NetworkInfo.LocalAddresses();

            var body = new StackPanel { Spacing = 14 };

            if (v4 is null && v6 is null)
            {
                body.Children.Add(new TextBlock
                {
                    Text = local
                        ? "没有读到本机回环地址，这通常意味着服务绑定的地址不是 127.0.0.1。"
                        : "没有检测到可用的局域网地址。请确认这台机器已连上网络（或已开启 Wi-Fi / 网线）。",
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
                    FrameworkElement card = BuildAccessCard(
                        local ? "本机" : "IPv4",
                        $"http://{v4}:{state.Port}/");
                    Grid.SetColumn(card, column++);
                    cards.Children.Add(card);
                }

                if (v6 is not null)
                {
                    FrameworkElement card = BuildAccessCard("IPv6", $"http://[{v6}]:{state.Port}/");
                    Grid.SetColumn(card, column);
                    cards.Children.Add(card);
                }

                body.Children.Add(cards);
            }

            body.Children.Add(new TextBlock
            {
                Text = scope switch
                {
                    AccessScope.Local => LocalScopeHint(),
                    AccessScope.Lan =>
                        "手机、平板与这台电脑连同一个 Wi-Fi 就能打开上面的地址；访问不了时先看防火墙是否放行。",
                    _ =>
                        "公网访问需要路由器端口映射或反向代理，地址那一栏填的是本机网卡地址。",
                },
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(MutedColor),
            });

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = BuildAccessHeader(scope, state.Port),
            };
            // 本机只有一张卡，780 宽会空出大半屏；按内容收窄
            dialog.Resources["ContentDialogMaxWidth"] = local ? 520.0 : 780.0;
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

        /// <summary>本机模式下的说明。配置里的「局域网访问」开着时如实点出来，别让人以为手机一定连不上。</summary>
        private static string LocalScopeHint()
        {
            const string basic =
                "普通模式用这台电脑的浏览器打开酒馆。要让手机、平板也能打开，把启动模式切成「服务器模式」并选「局域网」。";

            return TavernSettings.Scope == ServiceScope.Lan
                ? basic + "当前配置里开着「局域网访问」，同一 Wi-Fi 的设备其实也能连上这个端口。"
                : basic;
        }

        private UIElement BuildAccessHeader(AccessScope scope, int port)
        {
            bool local = scope == AccessScope.Local;

            var title = new StackPanel { Spacing = 2 };
            title.Children.Add(new TextBlock
            {
                Text = local ? "本机访问" : "设备访问",
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(PrimaryTextColor),
            });
            title.Children.Add(new TextBlock
            {
                Text = local
                    ? $"普通模式 · 端口 {port} · 只有这台电脑能打开"
                    : $"{(scope == AccessScope.Lan ? "局域网" : "互联网")} · 端口 {port} · 把地址复制到手机浏览器",
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
                    Glyph = local ? "\uE72E" : scope == AccessScope.Lan ? "\uE701" : "\uE774",
                    FontSize = 16,
                    Foreground = new SolidColorBrush(InfoColor),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });

            header.Children.Add(title);
            return header;
        }

        /// <summary>
        /// 一张地址卡：完整 URL + 复制 / 打开两个动作。
        ///
        /// 以前框里只放 IP，旁边只有「复制地址」——用户拿到 192.168.8.21 还得自己补协议和端口；
        /// 现在框里就是能直接粘进地址栏的地址，「打开」用默认浏览器打开它。
        /// </summary>
        private FrameworkElement BuildAccessCard(string label, string url)
        {
            var content = new StackPanel { Spacing = 10 };

            content.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(PrimaryTextColor),
            });

            var box = new TextBox
            {
                Text = url,
                IsReadOnly = true,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Padding = new Thickness(10, 7, 10, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            // 只读框也要能选中：UIA / 键盘复制之外，鼠标拖选是最直觉的
            box.IsTabStop = true;
            box.SelectionStart = box.Text.Length;
            content.Children.Add(box);

            var actions = new Grid { ColumnSpacing = 8 };
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

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
                copy.Content = BuildCopyLabel("已复制");
            };

            var open = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Style = (Style)Application.Current.Resources["AccentActionButtonStyle"],

                // 强调色由系统决定，浅色主题下白字会糊在背景上：明确用「强调色上的文字」刷子
                Content = BuildOpenLabel(Res("TextOnAccentFillColorPrimaryBrush", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF))),
            };

            open.Click += (_, _) =>
            {
                try
                {
                    // 明确要「默认浏览器」：不走设置里选的那个
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    AppNotify.Error("打不开浏览器", ex.Message);
                }
            };

            actions.Children.Add(copy);
            Grid.SetColumn(open, 1);
            actions.Children.Add(open);
            content.Children.Add(actions);

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

        private static UIElement BuildCopyLabel(string text = "复制")
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

        /// <summary>取主题资源里的刷子；取不到就用兜底色（控件没有主题资源时也不会崩）。</summary>
        private static Brush Res(string key, Color fallback)
            => Application.Current.Resources.TryGetValue(key, out object value) && value is Brush brush
                ? brush
                : new SolidColorBrush(fallback);

        private static UIElement BuildOpenLabel(Brush foreground)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            row.Children.Add(new FontIcon { Glyph = "\uE8A7", FontSize = 12, Foreground = foreground });
            row.Children.Add(new TextBlock { Text = "打开", FontSize = 12, Foreground = foreground });
            return row;
        }
    }
}
