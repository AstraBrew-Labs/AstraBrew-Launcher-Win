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

        private Storyboard? _pulseStoryboard;

        /// <summary>程序回填共享状态时屏蔽回写，避免与配置页来回触发。</summary>
        private bool _syncing;

        /// <summary>程序回填版本下拉时屏蔽回写，避免把「同步」当成用户切换。</summary>
        private bool _syncingVersion;

        /// <summary>下拉框当前承载的清单快照，只有清单变化才重建控件。</summary>
        private string _versionSignature = string.Empty;

        public DashboardPage()
        {
            InitializeComponent();

            // 初始化顺序：版本 → 模式面板 → 服务器地址，三者互相独立
            SyncFromCatalog();
            SyncFromShared();
            ApplyModeVisibility();
            SyncServiceScope();

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            TavernSettings.Changed += Shared_Changed;

            // 先减后加：避免同一页面重复 Loaded 时累积多份订阅
            TavernInstances.Changed -= Instances_Changed;
            TavernInstances.Changed += Instances_Changed;

            SyncFromCatalog();
            SyncFromShared();
            StartStatusPulse();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            TavernSettings.Changed -= Shared_Changed;
            TavernInstances.Changed -= Instances_Changed;

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

        // ==================== 启动配置：酒馆版本 ====================

        /// <summary>
        /// 清单或当前实例变化后重画版本区。
        /// 本页与版本管理页读写同一份 <see cref="TavernInstances"/>，
        /// 任何一边切换都会走到这里，不存在「这边改了那边不知道」。
        /// </summary>
        private void Instances_Changed(object? sender, EventArgs e) => SyncFromCatalog();

        private void SyncFromCatalog()
        {
            if (VersionCombo is null || VersionTypeText is null || CurrentVersionValue is null)
            {
                return;
            }

            string signature = BuildVersionSignature();
            if (!string.Equals(signature, _versionSignature, StringComparison.Ordinal))
            {
                _versionSignature = signature;
                RebuildVersionCombo();
            }

            SelectCurrentInCombo();
            ShowInstanceDetails(TavernInstances.Current);
        }

        /// <summary>清单指纹：来源 + 版本 + 依赖状态，任一变化都要重建下拉项。</summary>
        private static string BuildVersionSignature()
        {
            var builder = new System.Text.StringBuilder();

            foreach (var instance in TavernInstances.All)
            {
                builder.Append(instance.Kind)
                    .Append(':')
                    .Append(instance.Version)
                    .Append(':')
                    .Append(instance.DepsReady ? '1' : '0')
                    .Append('|');
            }

            return builder.ToString();
        }

        private void RebuildVersionCombo()
        {
            _syncingVersion = true;

            VersionCombo.Items.Clear();

            foreach (var instance in TavernInstances.All)
            {
                VersionCombo.Items.Add(new ComboBoxItem
                {
                    Content = instance.DisplayName,
                    Tag = instance,
                });
            }

            _syncingVersion = false;
        }

        /// <summary>把下拉框的选中项对齐到共享状态里的当前实例。</summary>
        private void SelectCurrentInCombo()
        {
            var current = TavernInstances.Current;

            for (int i = 0; i < VersionCombo.Items.Count; i++)
            {
                if (VersionCombo.Items[i] is ComboBoxItem { Tag: TavernInstance instance }
                    && ReferenceEquals(instance, current))
                {
                    if (VersionCombo.SelectedIndex != i)
                    {
                        _syncingVersion = true;
                        VersionCombo.SelectedIndex = i;
                        _syncingVersion = false;
                    }

                    return;
                }
            }
        }

        private void ShowInstanceDetails(TavernInstance instance)
        {
            VersionTypeText.Text = instance.KindLabel;
            VersionKernelText.Text = $"内核 {instance.KernelLabel}";
            VersionPathText.Text = instance.Path;
            VersionSizeText.Text = instance.Size;

            HeroVersionPillText.Text = $"{instance.KindLabel} · {instance.KernelLabel}";
            CurrentVersionValue.Text = instance.KernelLabel;
            CurrentVersionCaption.Text = BuildVersionCaption(instance);
        }

        private static string BuildVersionCaption(TavernInstance instance)
        {
            if (instance.IsBuiltin)
            {
                return instance.Version == TavernInstances.LatestStable
                    ? $"{instance.DisplayName} · 已是最新"
                    : instance.Version == TavernInstances.StagingTag
                        ? $"{instance.DisplayName} · 开发版"
                        : $"{instance.DisplayName} · 可更新到 v{TavernInstances.LatestStable}";
            }

            return instance.DepsReady
                ? $"{instance.DisplayName} · 依赖就绪"
                : $"{instance.DisplayName} · 缺少依赖";
        }

        private void VersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingVersion)
            {
                return;
            }

            if (VersionCombo.SelectedItem is not ComboBoxItem { Tag: TavernInstance instance })
            {
                return;
            }

            // 缺少依赖的本地实例不允许成为当前实例：把选择弹回原处并说明原因
            if (!instance.IsBuiltin && !instance.DepsReady)
            {
                ShowBar(
                    "该实例缺少依赖",
                    $"{instance.DisplayName} 需要先在「版本管理」安装 Node 依赖才能启动，当前仍使用 {TavernInstances.Current.DisplayName}。",
                    InfoBarSeverity.Warning);

                SelectCurrentInCombo();
                return;
            }

            if (ReferenceEquals(instance, TavernInstances.Current))
            {
                return;
            }

            TavernInstances.Use(instance);

            ShowBar(
                "已切换版本",
                instance.IsBuiltin
                    ? $"后续启动将使用内置实例 {instance.KernelLabel}。"
                    : $"后续启动将使用本地 {instance.KernelLabel}，目录：{instance.Path}",
                InfoBarSeverity.Success);
        }

        private void RescanVersions_Click(object sender, RoutedEventArgs e)
        {
            SyncFromCatalog();

            int missing = 0;
            foreach (var instance in TavernInstances.Locals)
            {
                if (!instance.DepsReady)
                {
                    missing++;
                }
            }

            ShowBar(
                "已重新扫描本地版本",
                missing == 0
                    ? $"发现 {TavernInstances.Locals.Count} 个本地版本，均可正常启动。"
                    : $"发现 {TavernInstances.Locals.Count} 个本地版本，其中 {missing} 个缺少 Node 依赖。",
                InfoBarSeverity.Success);
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
