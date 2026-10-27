using System;
using AstraBrew_Launcher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;

namespace AstraBrew_Launcher.Views
{
    /// <summary>
    /// 仪表盘：服务状态、环境指标、启动配置（版本 / 启动模式）与快捷操作。
    /// 数据当前为静态样例，交互（切换、导航、复制、启停反馈）为真实行为。
    /// </summary>
    public sealed partial class DashboardPage : Page
    {
        private const string LocalEndpoint = "http://127.0.0.1:8000";

        /// <summary>可选酒馆版本：内置一份，本地若干份。</summary>
        private sealed class TavernVersion
        {
            public string Name { get; init; } = string.Empty;

            public string Kind { get; init; } = string.Empty;

            public string Kernel { get; init; } = string.Empty;

            public string Path { get; init; } = string.Empty;

            public string Size { get; init; } = string.Empty;
        }

        /// <summary>顺序与 <see cref="VersionCombo"/> 的项一一对应。</summary>
        private static readonly TavernVersion[] Versions =
        {
            new() { Name = "内置酒馆", Kind = "内置", Kernel = "v1.19.2", Path = @"…\lib\tavern", Size = "428 MB" },
            new() { Name = "本地-1.19.0", Kind = "本地", Kernel = "v1.19.0", Path = @"D:\Tavern\1.19.0", Size = "1.24 GB" },
            new() { Name = "本地-1.18.0", Kind = "本地", Kernel = "v1.18.0", Path = @"D:\Tavern\1.18.0", Size = "1.18 GB" },
            new() { Name = "本地-1.17.2", Kind = "本地", Kernel = "v1.17.2", Path = @"D:\Tavern\1.17.2", Size = "1.06 GB" },
        };

        private Storyboard? _pulseStoryboard;

        /// <summary>程序回填共享状态时屏蔽回写，避免与配置页来回触发。</summary>
        private bool _syncing;

        public DashboardPage()
        {
            InitializeComponent();

            // 初始化顺序：版本详情 → 模式面板 → 服务器地址，三者互相独立
            VersionCombo.SelectedIndex = 0;
            SyncFromShared();
            ApplyModeVisibility();
            SyncServiceScope();

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            TavernSettings.Changed += Shared_Changed;
            SyncFromShared();
            StartStatusPulse();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            TavernSettings.Changed -= Shared_Changed;
            _pulseStoryboard?.Stop();
            _pulseStoryboard = null;
        }

        /// <summary>
        /// 与酒馆配置页共用同一份设置：启动模式、默认浏览器、服务范围。
        /// 任一页面改动都会回到这里刷新控件，因此两边永远一致。
        /// </summary>
        private void SyncFromShared()
        {
            if (NormalModeRadio is null || ChromeRadio is null || LanRadio is null)
            {
                return;
            }

            _syncing = true;

            NormalModeRadio.IsChecked = TavernSettings.Mode == LaunchMode.Normal;
            DesktopModeRadio.IsChecked = TavernSettings.Mode == LaunchMode.Desktop;
            ServerModeRadio.IsChecked = TavernSettings.Mode == LaunchMode.Server;

            ChromeRadio.IsChecked = TavernSettings.Browser == BrowserKind.Chrome;
            EdgeRadio.IsChecked = TavernSettings.Browser == BrowserKind.Edge;
            FirefoxRadio.IsChecked = TavernSettings.Browser == BrowserKind.Firefox;

            LanRadio.IsChecked = TavernSettings.Scope == ServiceScope.Lan;
            WanRadio.IsChecked = TavernSettings.Scope == ServiceScope.Wan;

            _syncing = false;
        }

        private void Shared_Changed(object? sender, EventArgs e)
        {
            SyncFromShared();
            ApplyModeVisibility();
            SyncServiceScope();
            SyncEndpoint();
        }

        /// <summary>运行状态呼吸光晕：进入 Decelerate、循环播放。</summary>
        private void StartStatusPulse()
        {
            if (_pulseStoryboard is not null)
            {
                return;
            }

            var animation = new DoubleAnimation
            {
                From = 0.28,
                To = 0,
                Duration = new Duration(TimeSpan.FromSeconds(1.6)),
                RepeatBehavior = RepeatBehavior.Forever,
            };
            Storyboard.SetTarget(animation, StatusHalo);
            Storyboard.SetTargetProperty(animation, "(UIElement.Opacity)");

            _pulseStoryboard = new Storyboard();
            _pulseStoryboard.Children.Add(animation);
            _pulseStoryboard.Begin();
        }

        private void StopStatusPulse()
        {
            _pulseStoryboard?.Stop();
            _pulseStoryboard = null;
        }

        // ==================== 页头 / 主卡操作 ====================

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            LastSyncText.Text = $"最后同步 {DateTime.Now:HH:mm:ss}";
            ShowBar("指标已刷新", "服务状态、资源占用与运行环境已重新采集。", InfoBarSeverity.Success);
        }

        private void CopyEndpoint_Click(object sender, RoutedEventArgs e)
        {
            var package = new DataPackage();
            package.SetText(EndpointText.Text);
            Clipboard.SetContent(package);

            FlashCopiedFeedback();
            ShowBar("地址已复制", EndpointText.Text, InfoBarSeverity.Success);
        }

        private void FlashCopiedFeedback()
        {
            CopyFeedback.Opacity = 1;

            var fade = new DoubleAnimation
            {
                To = 0,
                BeginTime = TimeSpan.FromSeconds(1.1),
                Duration = new Duration(TimeSpan.FromSeconds(0.5)),
            };
            Storyboard.SetTarget(fade, CopyFeedback);
            Storyboard.SetTargetProperty(fade, "(UIElement.Opacity)");

            var storyboard = new Storyboard();
            storyboard.Children.Add(fade);
            storyboard.Begin();
        }

        private void Restart_Click(object sender, RoutedEventArgs e)
        {
            SetServiceRunning(true);
            ShowBar("已发送重启指令", "SiliconTavern 将在数秒内重启，进度可在控制台查看。", InfoBarSeverity.Informational);
        }

        private async void Stop_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "停止酒馆服务？",
                Content = "正在进行的对话请求会被中断，本地端口 8000 将被释放。",
                PrimaryButtonText = "停止服务",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            SetServiceRunning(false);
            ShowBar("服务已停止", "端口 8000 已释放，可随时重新启动。", InfoBarSeverity.Warning);
        }

        private void OpenConsole_Click(object sender, RoutedEventArgs e)
        {
            App.MainWindow?.NavigateTo("console");
        }

        // ==================== 启动配置 ====================

        private void VersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (VersionCombo.SelectedIndex < 0 || VersionCombo.SelectedIndex >= Versions.Length)
            {
                return;
            }

            // 构造函数里设 SelectedIndex 时，下方控件可能尚未赋值
            if (VersionTypeText is null || CurrentVersionValue is null)
            {
                return;
            }

            var version = Versions[VersionCombo.SelectedIndex];

            VersionTypeText.Text = version.Kind;
            VersionKernelText.Text = $"内核 {version.Kernel}";
            VersionPathText.Text = version.Path;
            VersionSizeText.Text = version.Size;

            HeroVersionPillText.Text = $"{version.Kind} · {version.Kernel}";
            CurrentVersionValue.Text = version.Kernel;
            CurrentVersionCaption.Text = $"{version.Name} · 已是最新";
        }

        private void RescanVersions_Click(object sender, RoutedEventArgs e)
        {
            ShowBar("已重新扫描本地版本", "发现 3 个本地版本，均可正常启动。", InfoBarSeverity.Success);
        }

        private void ManageVersions_Click(object sender, RoutedEventArgs e)
        {
            App.MainWindow?.NavigateTo("versions");
        }

        /// <summary>启动模式切换：普通 / 桌面 / 服务器三种面板互斥显示。</summary>
        private void Mode_Checked(object sender, RoutedEventArgs e)
        {
            if (NormalModePanel is null)
            {
                return;
            }

            if (!_syncing)
            {
                TavernSettings.Mode = ServerModeRadio.IsChecked == true
                    ? LaunchMode.Server
                    : DesktopModeRadio.IsChecked == true
                        ? LaunchMode.Desktop
                        : LaunchMode.Normal;
            }

            ApplyModeVisibility();
            SyncEndpoint();
        }

        private void ApplyModeVisibility()
        {
            if (NormalModePanel is null)
            {
                return;
            }

            bool normal = NormalModeRadio.IsChecked == true;
            bool desktop = DesktopModeRadio.IsChecked == true;
            bool server = ServerModeRadio.IsChecked == true;

            NormalModePanel.Visibility = normal ? Visibility.Visible : Visibility.Collapsed;
            DesktopModePanel.Visibility = desktop ? Visibility.Visible : Visibility.Collapsed;
            ServerModePanel.Visibility = server ? Visibility.Visible : Visibility.Collapsed;

            ModeSettingsTag.Text = normal ? "普通模式" : desktop ? "桌面模式" : "服务器模式";

            if (ModeDescriptionText is not null)
            {
                ModeDescriptionText.Text = normal
                    ? "用默认浏览器打开酒馆，适合日常对话。"
                    : desktop
                        ? "在独立窗口中运行酒馆界面，不经过浏览器。"
                        : "开放给其他设备访问，按服务模式决定可访问范围。";
            }
        }

        private void ServiceScope_Checked(object sender, RoutedEventArgs e)
        {
            if (AccessAddressText is null)
            {
                return;
            }

            if (!_syncing)
            {
                TavernSettings.Scope = LanRadio.IsChecked == true ? ServiceScope.Lan : ServiceScope.Wan;
            }

            SyncServiceScope();
            SyncEndpoint();
        }

        /// <summary>局域网 / 互联网：切换访问地址与说明文案。</summary>
        private void SyncServiceScope()
        {
            if (AccessAddressText is null)
            {
                return;
            }

            bool lan = LanRadio.IsChecked != false;

            AccessAddressText.Text = lan ? "http://192.168.1.24:8000" : "https://tavern.astrabrew.cn";
            ServiceScopeHint.Text = lan
                ? "同一 Wi-Fi 下的设备可直接访问，无需额外配置。"
                : "经反向代理映射到公网，需保证端口转发与证书有效。";
        }

        private void Browser_Checked(object sender, RoutedEventArgs e)
        {
            if (OpenTavernHint is null)
            {
                return;
            }

            if (!_syncing)
            {
                TavernSettings.Browser = EdgeRadio.IsChecked == true
                    ? BrowserKind.Edge
                    : FirefoxRadio.IsChecked == true
                        ? BrowserKind.Firefox
                        : BrowserKind.Chrome;
            }

            OpenTavernHint.Text = $"用 {SelectedBrowserName} 打开";
        }

        private string SelectedBrowserName =>
            EdgeRadio.IsChecked == true ? "Edge" :
            FirefoxRadio.IsChecked == true ? "火狐浏览器" :
            "谷歌浏览器";

        /// <summary>主卡对外展示的地址：服务器模式下跟随服务模式。</summary>
        private void SyncEndpoint()
        {
            if (EndpointText is null || ServerModeRadio is null)
            {
                return;
            }

            bool server = ServerModeRadio.IsChecked == true;
            EndpointText.Text = server && AccessAddressText is not null
                ? AccessAddressText.Text
                : LocalEndpoint;
        }

        // ==================== 快捷操作 ====================

        private void OpenTavern_Click(object sender, RoutedEventArgs e)
        {
            ShowBar("已在浏览器中打开酒馆", $"使用「{SelectedBrowserName}」访问 {EndpointText.Text}。", InfoBarSeverity.Informational);
        }

        private void OpenDataDir_Click(object sender, RoutedEventArgs e)
        {
            ShowBar("已打开数据目录", @"%AppData%\AstraBrew Launcher\data", InfoBarSeverity.Informational);
            App.MainWindow?.NavigateTo("resources");
        }

        private void ImportCard_Click(object sender, RoutedEventArgs e)
        {
            ShowBar("请选择角色卡文件", "支持 PNG 与 JSON 两种格式，可一次选择多张。", InfoBarSeverity.Informational);
            App.MainWindow?.NavigateTo("resources");
        }

        private void OpenConfig_Click(object sender, RoutedEventArgs e)
        {
            App.MainWindow?.NavigateTo("tavern");
        }

        // ==================== 状态同步 ====================

        /// <summary>切换运行/停止态：主卡光晕与状态文案保持一致。</summary>
        private void SetServiceRunning(bool running)
        {
            ServiceStateText.Text = running ? "服务运行中" : "服务已停止";

            StatusHalo.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            StatusDotRunning.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            StatusDotStopped.Visibility = running ? Visibility.Collapsed : Visibility.Visible;

            if (running)
            {
                StopStatusPulse();
                StartStatusPulse();
            }
            else
            {
                StopStatusPulse();
            }
        }

        private void ShowBar(string title, string message, InfoBarSeverity severity)
        {
            ActionInfoBar.Title = title;
            ActionInfoBar.Message = message;
            ActionInfoBar.Severity = severity;
            ActionInfoBar.IsOpen = true;
        }
    }
}
