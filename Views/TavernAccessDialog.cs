using System;
using System.Diagnostics;
using System.Threading.Tasks;
using AstraBrew_Launcher.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using QRCoder;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.UI;

namespace AstraBrew_Launcher.Views
{
    /// <summary>当前实例能被谁访问：决定胶囊文字与访问弹窗的内容。</summary>
    public enum AccessScope
    {
        Local,
        Lan,
        Wan,
    }

    /// <summary>
    /// 「设备访问」弹窗：把酒馆此刻能用的地址列出来，扫码 / 复制 / 直接打开。
    ///
    /// 从 <see cref="ConsolePage"/> 里搬出来的：服务器模式下仪表盘点「打开酒馆」要看到
    /// 同一份地址表，两处各画一套迟早出现「控制台能打开、仪表盘打不开」的错位。
    ///
    /// 两种范围两种信息源：
    /// · 局域网 —— 地址来自本机网卡，手机连同一个 Wi-Fi 就能扫；
    /// · 互联网 —— 地址来自公网出口（<see cref="PublicIp"/>），手机在任何网络下都能扫到，
    ///   但路由器得先把这个端口映射进来（提示里写明）。
    /// </summary>
    internal static class TavernAccessDialog
    {
        /// <summary>地址卡统一宽度：几张卡叠在一起时左右边界才对得齐。</summary>
        private const double CardWidth = 500;

        /// <summary>码面目标边长（DIP）；实际大小是「整数模块 × 模块数」，不会超过这个值。</summary>
        private const int QrCodeTarget = 132;

        /// <summary>
        /// 访问范围由启动模式决定，不是由 config.yaml 的 <c>listen</c> 决定。
        /// 「服务范围」这个设置只在服务器模式里有意义；普通 / 桌面模式就是本机自用，
        /// 拿 listen 的值去显示「局域网」只会误导人。
        /// </summary>
        public static AccessScope CurrentScope() => TavernSettings.Mode switch
        {
            LaunchMode.Server => TavernSettings.Scope == ServiceScope.Lan ? AccessScope.Lan : AccessScope.Wan,
            _ => AccessScope.Local,
        };

        /// <summary>
        /// 打开访问地址弹窗。<paramref name="theme"/> 由调用页传入，
        /// 弹窗挂在 XamlRoot 上不跟随页面的缩放，但配色要跟当前主题一致。
        /// </summary>
        public static async Task ShowAsync(XamlRoot root, ElementTheme theme)
        {
            TavernProcessState state = TavernRuntime.State;

            if (!state.IsRunning || state.Port <= 0)
            {
                AppNotify.Info("酒馆还没启动", "启动服务之后这里才会列出可访问的地址。");
                return;
            }

            var palette = new Palette(theme == ElementTheme.Light);
            AccessScope scope = CurrentScope();
            int port = state.Port;

            // 卡片一律居中：只有一张时不会歪在左边，两张时上下边界也对得齐
            var body = new StackPanel
            {
                Spacing = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            StackPanel? wanArea = null;

            switch (scope)
            {
                case AccessScope.Local:
                    // 普通模式只列回环地址：酒馆就绑在 127.0.0.1，把网卡地址摆出来等于承诺一个打不开的链接。
                    // 手机连不上 127.0.0.1，二维码在这里没有意义，不画。
                    body.Children.Add(BuildCard(palette, "本机", $"http://127.0.0.1:{port}/", withQr: false));
                    break;

                case AccessScope.Lan:
                {
                    (string? v4, string? v6) = NetworkInfo.LocalAddresses();

                    if (v4 is null && v6 is null)
                    {
                        body.Children.Add(BuildNotice(
                            palette,
                            "没有检测到可用的局域网地址。请确认这台机器已连上网络（或已开启 Wi-Fi / 网线）。"));
                        break;
                    }

                    if (v4 is not null)
                    {
                        body.Children.Add(BuildCard(palette, "局域网 · IPv4", $"http://{v4}:{port}/", withQr: true));
                    }

                    if (v6 is not null)
                    {
                        body.Children.Add(BuildCard(palette, "局域网 · IPv6", $"http://[{v6}]:{port}/", withQr: true));
                    }

                    break;
                }

                default:
                {
                    // 公网地址要现问外面，先摆一个「正在获取」的占位，取到再替换
                    wanArea = new StackPanel
                    {
                        Spacing = 12,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    };
                    wanArea.Children.Add(BuildLoading(palette));
                    body.Children.Add(wanArea);
                    break;
                }
            }

            body.Children.Add(BuildHint(palette, scope, port));

            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = BuildAccessHeader(palette, scope, port),
            };

            dialog.Content = BuildContentHost(body, dialog);

            dialog.Resources["ContentDialogMaxWidth"] = 660.0;
            dialog.Resources["ControlCornerRadius"] = new CornerRadius(8);

            // 先让弹窗出来，公网 IP 边取边填：接口最长 5 秒，不该让人对着「正在获取」的空窗等
            var operation = dialog.ShowAsync();

            if (wanArea is not null)
            {
                _ = FillPublicAsync(wanArea, palette, port);
            }

            await operation;
        }

        // ==================== 公网地址 ====================

        private static async Task FillPublicAsync(StackPanel host, Palette palette, int port)
        {
            (string? v4, string? v6) = await PublicIp.ResolveAsync();

            host.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    // 弹窗已经关掉了：别再去动一棵已经脱离可视树的控件树
                    if (host.XamlRoot is null)
                    {
                        return;
                    }

                    host.Children.Clear();

                    if (v4 is not null)
                    {
                        host.Children.Add(BuildCard(palette, "公网 · IPv4", $"http://{v4}:{port}/", withQr: true));
                    }

                    if (v6 is not null)
                    {
                        host.Children.Add(BuildCard(palette, "公网 · IPv6", $"http://[{v6}]:{port}/", withQr: true));
                    }

                    if (v4 is null && v6 is null)
                    {
                        host.Children.Add(BuildNotice(
                            palette,
                            "没取到公网 IP：可能这台机器没有公网出口，或者请求被网络 / 代理拦截了。"
                            + "下面这个内网地址只能在同一个 Wi-Fi 下访问。"));

                        (string? lanV4, _) = NetworkInfo.LocalAddresses();

                        if (lanV4 is not null)
                        {
                            host.Children.Add(BuildCard(palette, "内网 · IPv4", $"http://{lanV4}:{port}/", withQr: true));
                        }
                    }
                }
                catch (Exception)
                {
                    // 弹窗在这期间被关掉 / 控件已释放：结果没人看也无所谓
                }
            });
        }

        private static UIElement BuildLoading(Palette palette)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 2),
            };

            row.Children.Add(new ProgressRing
            {
                IsActive = true,
                Width = 18,
                Height = 18,
                Margin = new Thickness(0, 0, 0, 0),
            });

            row.Children.Add(new TextBlock
            {
                Text = "正在获取公网 IP…",
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(palette.Muted),
            });

            return row;
        }

        // ==================== 卡片 ====================

        /// <summary>
        /// 一张地址卡：左边二维码、右边地址与动作，几张卡同宽，整体居中。
        ///
        /// 二维码用 <see cref="QRCodeGenerator"/> 的模块矩阵现画成 Path，不生成位图：
        /// 缩放屏上不会糊，也不用管图片解码和 DPI。
        /// </summary>
        private static FrameworkElement BuildCard(Palette palette, string label, string url, bool withQr)
        {
            var content = new Grid { ColumnSpacing = 16 };
            int column = 0;

            if (withQr)
            {
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                content.Children.Add(BuildQr(url));
                column = 1;
            }

            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var text = new StackPanel
            {
                Spacing = 9,
                VerticalAlignment = VerticalAlignment.Center,
            };

            text.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(palette.PrimaryText),
            });

            var box = new TextBox
            {
                Text = url,
                IsReadOnly = true,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Padding = new Thickness(10, 7, 10, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,

                // 地址通常比框宽：让它在框内自己横向滚，不要把卡片撑开
                TextWrapping = TextWrapping.NoWrap,
            };
            box.IsTabStop = true;
            box.SelectionStart = box.Text.Length;
            text.Children.Add(box);

            text.Children.Add(BuildActions(palette, url));

            Grid.SetColumn(text, column);
            content.Children.Add(text);

            return new Border
            {
                Width = CardWidth,
                Padding = new Thickness(14),
                HorizontalAlignment = HorizontalAlignment.Center,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(palette.CardSurface),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(palette.CardBorder),
                Child = content,
            };
        }

        /// <summary>复制 / 打开两个动作，等宽铺满文字列。</summary>
        private static UIElement BuildActions(Palette palette, string url)
        {
            var actions = new Grid { ColumnSpacing = 8 };
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var copy = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Content = BuildCopyLabel("复制"),
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
                // 桌面模式开内嵌窗口；其余模式走系统默认浏览器（这里刻意不走设置里选的那个）
                if (TavernSettings.Mode == LaunchMode.Desktop)
                {
                    if (DesktopWindow.Open(url, out string desktopDetail))
                    {
                        return;
                    }

                    AppNotify.Warning("桌面窗口没能打开，改用浏览器", desktopDetail);
                }

                try
                {
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
            return actions;
        }

        // ==================== 二维码 ====================

        /// <summary>
        /// 白底方框 + 黑色模块。二维码必须深色压在浅色上，深色主题下也不例外，
        /// 所以这里固定白底、黑模块，不吃主题色。
        ///
        /// 方框只负责「白底 + 圆角 + 一点内边距」，码面本身是一块尺寸精确的 Canvas：
        /// Shape 自己量几何、Stretch 缩放都靠不住（量出来的比实际小 → 码面在框里偏 + 被裁），
        /// 一块块矩形反而是最确定的画法。
        /// </summary>
        private static FrameworkElement BuildQr(string url) => new Border
        {
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Colors.White),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = BuildQrCanvas(url),
        };

        private static UIElement BuildQrCanvas(string url)
        {
            try
            {
                using var generator = new QRCodeGenerator();

                // M 级纠错：手机屏反光、角度偏一点也能读出来；再高一级会让码面变大
                QRCodeData data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);

                int dimension = data.ModuleMatrix.Count;

                // 模块取整数 DIP：非整数会让边缘糊成灰边，反而不好扫
                int module = Math.Max(2, (int)Math.Floor(QrCodeTarget / (double)dimension));
                int size = dimension * module;

                var canvas = new Canvas { Width = size, Height = size };
                var brush = new SolidColorBrush(Colors.Black);

                for (int y = 0; y < dimension; y++)
                {
                    var row = data.ModuleMatrix[y];
                    int x = 0;

                    while (x < dimension)
                    {
                        if (!row[x])
                        {
                            x++;
                            continue;
                        }

                        int start = x;

                        while (x < dimension && row[x])
                        {
                            x++;
                        }

                        // 同一行连续的暗模块合成一个矩形：比逐格画少几百个图元
                        var block = new Rectangle
                        {
                            Width = (x - start) * module,
                            Height = module,
                            Fill = brush,
                        };

                        Canvas.SetLeft(block, start * module);
                        Canvas.SetTop(block, y * module);
                        canvas.Children.Add(block);
                    }
                }

                return canvas;
            }
            catch (Exception)
            {
                return new FontIcon
                {
                    Glyph = "\uE9CE",
                    FontSize = 24,
                    Foreground = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)),
                };
            }
        }

        // ==================== 头部 / 提示 ====================

        /// <summary>
        /// 弹窗正文 + 自建底栏。
        ///
        /// 关闭按钮必须自己画：上一版把底栏整个丢了，用户只能按 ESC 或点右上角，
        /// 弹窗在触摸屏上等于关不掉。也别用 ContentDialog 自带的按钮区——
        /// 只有一颗按钮时它会被拉成整行宽的横条，跟卡片宽度完全对不上。
        /// </summary>
        private static UIElement BuildContentHost(UIElement body, ContentDialog dialog)
        {
            var host = new Grid { Padding = new Thickness(0, 2, 0, 0) };
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            host.Children.Add(body);

            var close = new Button
            {
                Content = "关闭",
                MinWidth = 84,
                HorizontalAlignment = HorizontalAlignment.Right,
                Style = (Style)Application.Current.Resources["AccentActionButtonStyle"],
            };
            close.Click += (_, _) => dialog.Hide();

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };
            footer.Children.Add(close);

            Grid.SetRow(footer, 1);
            host.Children.Add(footer);
            return host;
        }

        private static UIElement BuildAccessHeader(Palette palette, AccessScope scope, int port)
        {
            bool local = scope == AccessScope.Local;

            var title = new StackPanel { Spacing = 2 };
            title.Children.Add(new TextBlock
            {
                Text = local ? "本机访问" : "设备访问",
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(palette.PrimaryText),
            });
            title.Children.Add(new TextBlock
            {
                Text = local
                    ? $"普通模式 · 端口 {port} · 只有这台电脑能打开"
                    : scope == AccessScope.Lan
                        ? $"局域网 · 端口 {port} · 手机连同一个 Wi-Fi 后扫码"
                        : $"互联网 · 端口 {port} · 手机扫码（任何网络）",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(palette.Muted),
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
                    Foreground = new SolidColorBrush(palette.Info),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });

            header.Children.Add(title);
            return header;
        }

        private static UIElement BuildHint(Palette palette, AccessScope scope, int port)
        {
            string text = scope switch
            {
                AccessScope.Local => LocalScopeHint(),
                AccessScope.Lan =>
                    "手机、平板与这台电脑连同一个 Wi-Fi 就能扫码打开；扫不开时先确认防火墙放行了这个端口。",
                _ =>
                    $"公网访问要先在路由器上把 {port} 端口映射到这台机器（或走反向代理）；"
                    + "这里显示的是本机出口地址，映射没做好的话外网仍然连不上。",
            };

            return new TextBlock
            {
                Text = text,
                Width = CardWidth,
                FontSize = 11.5,
                LineHeight = 18,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = new SolidColorBrush(palette.Muted),
            };
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

        private static UIElement BuildNotice(Palette palette, string text) => new Border
        {
            Width = CardWidth,
            Padding = new Thickness(14, 12, 14, 12),
            HorizontalAlignment = HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(palette.CardSurface),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(palette.CardBorder),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12.5,
                LineHeight = 19,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(palette.PrimaryText),
            },
        };

        private static UIElement BuildCopyLabel(string text)
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

        /// <summary>取主题资源里的刷子；取不到就用兜底色（控件没有主题资源时也不会崩）。</summary>
        private static Brush Res(string key, Color fallback)
            => Application.Current.Resources.TryGetValue(key, out object value) && value is Brush brush
                ? brush
                : new SolidColorBrush(fallback);

        /// <summary>
        /// 弹窗里的语义色。以前这些值挂在 <see cref="ConsolePage"/> 的实例属性上，
        /// 现在两个页面共用同一份配色，主题一变两边一起变。
        /// </summary>
        private readonly struct Palette
        {
            public Palette(bool light) => Light = light;

            private bool Light { get; }

            public Color Info => Light ? Color.FromArgb(255, 15, 95, 191) : Color.FromArgb(255, 92, 176, 255);

            public Color Muted => Light ? Color.FromArgb(255, 96, 96, 96) : Color.FromArgb(255, 160, 160, 160);

            public Color PrimaryText => Light ? Color.FromArgb(255, 26, 26, 26) : Color.FromArgb(255, 245, 245, 245);

            public Color CardSurface => Light ? Color.FromArgb(0x0A, 0, 0, 0) : Color.FromArgb(0x0A, 255, 255, 255);

            public Color CardBorder => Light ? Color.FromArgb(0x14, 0, 0, 0) : Color.FromArgb(0x18, 255, 255, 255);
        }
    }
}
