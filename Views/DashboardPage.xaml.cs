using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using AstraBrew_Launcher.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AstraBrew_Launcher.Views
{
    /// <summary>
    /// 仪表盘：服务状态、运行指标、启动配置与快捷操作。
    ///
    /// 这里不放任何「样例数字」：服务状态与 PID 来自 <see cref="TavernRuntime"/>，
    /// 内存 / CPU 读子进程，扩展与角色卡数、目录体积现数磁盘，访问次数读酒馆自己写的
    /// access.log。改设置、改酒馆配置、在控制台启停之后，这一页都跟着变——
    /// 因为它读的全是那些页面写的那一份状态。
    /// </summary>
    public sealed partial class DashboardPage : Page
    {
        /// <summary>每秒刷新：运行时长、CPU、内存。磁盘类指标走 <see cref="TavernMetrics"/> 的缓存。</summary>
        private readonly DispatcherQueueTimer _tick;

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

            _tick = DispatcherQueue.CreateTimer();
            _tick.Interval = TimeSpan.FromSeconds(1);
            _tick.Tick += OnTick;

            // 初始化顺序：版本 → 模式面板 → 服务器地址，三者互相独立
            SyncFromCatalog();
            SyncFromShared();
            ApplyModeVisibility();
            SyncServiceScope();

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        // ==================== 生命周期 ====================

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 先减后加：同一页面重复 Loaded 时不该累积多份订阅
            TavernSettings.Changed -= Shared_Changed;
            TavernSettings.Changed += Shared_Changed;

            TavernInstances.Changed -= Instances_Changed;
            TavernInstances.Changed += Instances_Changed;

            TavernRuntime.Changed -= Runtime_Changed;
            TavernRuntime.Changed += Runtime_Changed;

            LauncherSettings.Changed -= Settings_Changed;
            LauncherSettings.Changed += Settings_Changed;

            GlobalTavernConfig.Saved -= Config_Saved;
            GlobalTavernConfig.Saved += Config_Saved;

            DesktopWindow.Changed -= Desktop_Changed;
            DesktopWindow.Changed += Desktop_Changed;

            SyncFromCatalog();
            SyncFromShared();
            ApplyModeVisibility();
            SyncServiceScope();
            SyncOpenTavernAction();
            SetServiceRunning();

            StartStatusPulse();
            _tick.Start();

            _ = RefreshMetricsAsync(force: false);
            _ = SyncRuntimeLabelAsync();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            TavernSettings.Changed -= Shared_Changed;
            TavernInstances.Changed -= Instances_Changed;
            TavernRuntime.Changed -= Runtime_Changed;
            LauncherSettings.Changed -= Settings_Changed;
            GlobalTavernConfig.Saved -= Config_Saved;
            DesktopWindow.Changed -= Desktop_Changed;

            _tick.Stop();

            _pulseStoryboard?.Stop();
            _pulseStoryboard = null;
        }

        // ==================== 数据采集 ====================

        /// <summary>每秒一次：运行时长、子进程内存与 CPU。</summary>
        private void OnTick(DispatcherQueueTimer sender, object args)
        {
            UpdateUptime();
            UpdateProcessMetrics();
        }

        private async Task RefreshMetricsAsync(bool force)
        {
            TavernMetrics metrics;

            try
            {
                metrics = await TavernMetricsService.CollectAsync(force);
            }
            catch
            {
                // 采集失败不该让整页变成错误状态：数字留空，其余照常
                return;
            }

            ApplyMetrics(metrics);
        }

        /// <summary>把采集结果铺到四张卡上。</summary>
        private void ApplyMetrics(TavernMetrics metrics)
        {
            if (ExtensionsValue is null)
            {
                return;
            }

            int extensions = metrics.BuiltinExtensions + metrics.ThirdPartyExtensions;

            ExtensionsValue.Text = extensions.ToString();
            ExtensionsCaption.Text = extensions == 0
                ? "还没有安装扩展"
                : $"内置 {metrics.BuiltinExtensions} · 第三方 {metrics.ThirdPartyExtensions}";

            CharactersCount.Text = metrics.CharacterCount.ToString();
            CharactersUnit.Visibility = metrics.CharacterCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            CharactersCaption.Text = metrics.CharacterCount == 0
                ? "还没有角色卡，右键导入即可"
                : "共 " + TavernMetricsService.FormatSize(metrics.CharacterBytes);

            DiskValue.Text = TavernMetricsService.FormatSize(metrics.TotalBytes);
            DiskCaption.Text = $"安装 {TavernMetricsService.FormatSize(metrics.InstallBytes)} · "
                + $"数据 {TavernMetricsService.FormatSize(metrics.DataBytes)}";

            RequestsValue.Text = metrics.AccessLogEnabled ? metrics.TodayConnections.ToString() : "—";
            ToolTipService.SetToolTip(
                RequestsValue,
                metrics.AccessLogEnabled
                    ? "来自酒馆的 access.log（今天零点起）"
                    : "酒馆的访问日志未开启（logging.enableAccessLog），无法统计");

            LastSyncText.Text = $"最后同步 {metrics.SampledAt:HH:mm:ss}";
        }

        /// <summary>
        /// 子进程的内存与 CPU。没在跑就清空——显示上一次的残留值会让人以为服务还在跑。
        /// </summary>
        private void UpdateProcessMetrics()
        {
            if (MemoryValue is null)
            {
                return;
            }

            TavernProcessState state = TavernRuntime.State;

            if (!state.IsRunning)
            {
                MemoryValue.Text = "—";
                CpuValue.Text = "—";
                StartupValue.Text = state.StartupSeconds is { } last ? $"{last:0.0} s" : "—";
                return;
            }

            (long memory, double? cpu) = TavernMetricsService.SampleProcess();

            MemoryValue.Text = memory > 0 ? TavernMetricsService.FormatSize(memory) : "—";
            CpuValue.Text = cpu is { } percent ? $"{percent:0.0}%" : "—";
            StartupValue.Text = state.StartupSeconds is { } seconds ? $"{seconds:0.0} s" : "—";
        }

        /// <summary>
        /// 让版本行显示真实的 Node 版本：环境来源可能在设置里改过，
        /// 探测结果由 <see cref="RuntimeProbe"/> 缓存 30 秒，来回切页面不会反复拉进程。
        /// </summary>
        private async Task SyncRuntimeLabelAsync()
        {
            try
            {
                RuntimeEnvironment env = await RuntimeProbe.ProbeAsync(LauncherSettings.Runtime);
                TavernInstances.SetRuntimeLabel(env.Node);
            }
            catch
            {
                // 探测失败保持「Node 未检测」
            }

            UpdateEnvironmentText();
        }

        private void UpdateEnvironmentText()
        {
            if (RuntimeEnvText is null)
            {
                return;
            }

            bool builtin = LauncherSettings.Runtime == RuntimeSource.Builtin;
            string node = TavernInstances.Current.Node;

            RuntimeEnvText.Text = $"运行环境 · {(builtin ? "内置环境" : "系统环境")} · {node}";
            ToolTipService.SetToolTip(
                RuntimeEnvText,
                builtin
                    ? @"内置环境装在 %AppData%\AstraBrew Launcher\bin，可随启动器搬走"
                    : "系统环境使用 PATH 里的 Node.js 与 Git，由你自己维护");

            string dataRoot = TavernRuntime.ResolveDataRoot();
            DataModeText.Text = LauncherSettings.DataLocation == DataLocationMode.Global
                ? "数据存放 · 全局目录（所有实例共用）"
                : "数据存放 · 跟随实例目录";
            ToolTipService.SetToolTip(DataModeText, dataRoot);
        }

        // ==================== 共享状态 ====================

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

            // 「打开酒馆」的说法跟着启动模式变（浏览器 / 独立窗口 / 地址弹窗），
            // 以前这里写死「用 xxx 浏览器打开」，桌面模式下点下去开的是内嵌窗口
            SyncOpenTavernAction();

            if (BrowserHint is not null)
            {
                // 三个 chip 全不选中表示「跟随系统」，这件事得说出来，
                // 不然用户只会看到一排都没亮的按钮
                BrowserHint.Text = TavernSettings.Browser == BrowserKind.System
                    ? "当前跟随系统默认浏览器；点上面任一项可以固定使用。"
                    : "启动酒馆后自动用所选浏览器打开界面。";
            }
        }

        private void Shared_Changed(object? sender, EventArgs e)
        {
            SyncFromShared();
            ApplyModeVisibility();
            SyncServiceScope();
            SyncEndpoint();
        }

        /// <summary>环境来源 / 数据存放改了：版本行与地址都要跟着动。</summary>
        private void Settings_Changed(object? sender, EventArgs e)
        {
            UpdateEnvironmentText();
            SyncEndpoint();
            SyncServiceScope();
            _ = SyncRuntimeLabelAsync();
        }

        /// <summary>config.yaml 被写过（本页、配置页或恢复默认）：端口可能变了。</summary>
        private void Config_Saved(object? sender, EventArgs e)
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                SyncEndpoint();
                SyncServiceScope();
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                SyncEndpoint();
                SyncServiceScope();
            });
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

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            // 顺带按磁盘现状复核一遍实例：用户可能刚 git pull 过、或删了某个目录
            TavernInstances.ReloadBuiltin();

            foreach (TavernInstance instance in TavernInstances.Locals)
            {
                TavernInstances.ReloadLocal(instance);
            }

            await RefreshMetricsAsync(force: true);
            _ = SyncRuntimeLabelAsync();

            AppNotify.Success("指标已刷新", "服务状态、资源占用与磁盘数据已重新采集。");
        }

        private void CopyEndpoint_Click(object sender, RoutedEventArgs e)
        {
            var package = new DataPackage();
            package.SetText(EndpointText.Text);
            Clipboard.SetContent(package);

            FlashCopiedFeedback();
            AppNotify.Success("地址已复制", EndpointText.Text);
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

        /// <summary>主操作：没在跑就是「启动」，跑着就是「重启」。</summary>
        private async void PrimaryAction_Click(object sender, RoutedEventArgs e)
        {
            TavernProcessState state = TavernRuntime.State;

            if (state.IsBusy)
            {
                return;
            }

            if (state.IsRunning)
            {
                AppNotify.Info("正在重启酒馆", "停止旧进程后会重新启动，进度可在控制台查看。");
                await TavernRuntime.RestartAsync();
                return;
            }

            await TavernRuntime.StartAsync();
        }

        private async void Stop_Click(object sender, RoutedEventArgs e)
        {
            if (!TavernRuntime.IsRunning)
            {
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "停止酒馆服务？",
                Content = $"正在进行的对话请求会被中断，本地端口 {TavernRuntime.CurrentPort} 将被释放。",
                PrimaryButtonText = "停止服务",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };

            // DefaultButton 会被模板套上框架 AccentButtonStyle，圆角取自 ControlCornerRadius（4）。
            // 在弹窗作用域里把它收到 8，让「取消」这颗强调按钮跟页面按钮同一个圆角。
            dialog.Resources["ControlCornerRadius"] = new CornerRadius(8);

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            await TavernRuntime.StopAsync();
        }

        private void OpenConsole_Click(object sender, RoutedEventArgs e)
        {
            App.MainWindow?.NavigateTo("console");
        }

        // ==================== 启动配置：酒馆版本 ====================

        private void Instances_Changed(object? sender, EventArgs e)
        {
            SyncFromCatalog();
            UpdateEnvironmentText();
        }

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

            foreach (TavernInstance instance in TavernInstances.All)
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

            foreach (TavernInstance instance in TavernInstances.All)
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
            TavernInstance current = TavernInstances.Current;

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
                if (!instance.IsInstalled)
                {
                    return "内置酒馆还没有安装代码，去「版本管理」装一个版本";
                }

                // 依赖没装好的那份代码起不来，先让用户去补依赖，别显示「可更新」误导
                if (!TavernInstances.BuiltinDepsReady)
                {
                    return $"{instance.DisplayName} · 缺少依赖，去「版本管理」点「安装依赖」";
                }

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

            // 缺少依赖的实例不允许成为当前实例（内置的也一样）：
            // 把选择弹回原处并说明原因
            if (!instance.DepsReady)
            {
                AppNotify.Warning(
                    "该实例缺少依赖",
                    $"{instance.DisplayName} 需要先在「版本管理」安装 Node 依赖才能启动，当前仍使用 {TavernInstances.Current.DisplayName}。");

                SelectCurrentInCombo();
                return;
            }

            if (ReferenceEquals(instance, TavernInstances.Current))
            {
                return;
            }

            TavernInstances.Use(instance);

            // 换实例就换了数据目录（实例模式）或配置来源，地址与指标跟着重算
            SyncEndpoint();
            UpdateEnvironmentText();
            _ = RefreshMetricsAsync(force: true);

            AppNotify.Success(
                "已切换版本",
                instance.IsBuiltin
                    ? $"后续启动将使用内置实例 {instance.KernelLabel}。"
                    : $"后续启动将使用本地 {instance.KernelLabel}，目录：{instance.Path}");
        }

        private void RescanVersions_Click(object sender, RoutedEventArgs e)
        {
            TavernInstances.ReloadBuiltin();

            foreach (TavernInstance instance in TavernInstances.Locals)
            {
                TavernInstances.ReloadLocal(instance);
            }

            SyncFromCatalog();
            _ = RefreshMetricsAsync(force: true);

            int missing = 0;
            foreach (TavernInstance instance in TavernInstances.Locals)
            {
                if (!instance.DepsReady)
                {
                    missing++;
                }
            }

            AppNotify.Success(
                "已重新扫描本地版本",
                missing == 0
                    ? $"发现 {TavernInstances.Locals.Count} 个本地版本，均可正常启动。"
                    : $"发现 {TavernInstances.Locals.Count} 个本地版本，其中 {missing} 个缺少 Node 依赖。");
        }

        private void ManageVersions_Click(object sender, RoutedEventArgs e)
        {
            App.MainWindow?.NavigateTo("versions");
        }

        // ==================== 启动配置：模式 / 浏览器 / 范围 ====================

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

                ApplyModeVisibility();
                SyncEndpoint();
                SyncServiceScope();
            }
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

            // 外部同步引起的选中：Shared_Changed 已经刷过地址了，别再算一遍
            if (_syncing)
            {
                return;
            }

            // 写文件这一步收在 TavernSettings.SetScope 里，
            // 与「设置 → 服务范围」走同一条路，两边不会再各写各的。
            // 值真的变了它会发 Changed，由 Shared_Changed 统一刷新地址——
            // 这里再调一次 SyncServiceScope 等于把同一件事做两遍。
            TavernSettings.SetScope(LanRadio.IsChecked == true ? ServiceScope.Lan : ServiceScope.Wan);
        }

        /// <summary>局域网 / 互联网：都显示本机真实地址，区别只在于「能不能从外面连进来」。</summary>
        private void SyncServiceScope()
        {
            if (AccessAddressText is null)
            {
                return;
            }

            bool lan = LanRadio.IsChecked != false;
            int port = TavernRuntime.CurrentPort;

            (string? v4, _) = NetworkInfo.LocalAddresses();

            // 复用这次读到的 v4：HttpUrl(port) 会再枚举一遍网卡（100 ms 起步）
            AccessAddressText.Text = NetworkInfo.HttpUrl(port, v4);
            ServiceScopeHint.Text = lan
                ? "同一 Wi-Fi 下的设备可直接访问，无需额外配置。"
                : v4 is null
                    ? "没有检测到局域网地址；公网访问需要路由器端口映射或反向代理。"
                    : "公网访问需要路由器端口映射或反向代理；这里显示的是本机网卡地址。";
        }

        private void Browser_Checked(object sender, RoutedEventArgs e)
        {
            if (OpenTavernHint is null)
            {
                return;
            }

            if (!_syncing)
            {
                // 三个 chip 全不选中 = 跟随系统（官方配置里的 default）
                BrowserKind browser = EdgeRadio.IsChecked == true
                    ? BrowserKind.Edge
                    : FirefoxRadio.IsChecked == true
                        ? BrowserKind.Firefox
                        : ChromeRadio.IsChecked == true
                            ? BrowserKind.Chrome
                            : BrowserKind.System;

                TavernSettings.Browser = browser;

                // 专属浏览器是酒馆自己的配置（browserLaunch.browser），顺手写回文件
                GlobalTavernConfig.PatchScalar("browserLaunch.browser", BrowserKey(browser));
            }

            OpenTavernHint.Text = $"用 {TavernSettings.BrowserName} 打开";

            if (BrowserHint is not null)
            {
                BrowserHint.Text = TavernSettings.Browser == BrowserKind.System
                    ? "当前跟随系统默认浏览器；点上面任一项可以固定使用。"
                    : "启动酒馆后自动用所选浏览器打开界面。";
            }
        }

        private static string BrowserKey(BrowserKind browser) => browser switch
        {
            BrowserKind.Edge => "edge",
            BrowserKind.Firefox => "firefox",
            BrowserKind.System => "default",
            _ => "chrome",
        };

        /// <summary>主卡对外展示的地址：服务器模式下跟随服务模式，否则是本机回环地址。</summary>
        private void SyncEndpoint()
        {
            if (EndpointText is null || ServerModeRadio is null)
            {
                return;
            }

            int port = TavernRuntime.CurrentPort;
            bool server = ServerModeRadio.IsChecked == true;

            EndpointText.Text = server && AccessAddressText is not null
                ? AccessAddressText.Text
                : $"http://127.0.0.1:{port}";
        }

        // ==================== 快捷操作 ====================

        /// <summary>桌面窗口开了 / 关了：说明文字与主卡上的入口跟着变。</summary>
        private void Desktop_Changed(object? sender, EventArgs e) => SyncOpenTavernAction();

        /// <summary>
        /// 「打开酒馆」在三种模式下是三件事，说明文字必须跟着改：
        /// 普通模式交给浏览器、桌面模式开（或唤回）内嵌窗口、服务器模式弹地址表。
        /// </summary>
        private void SyncOpenTavernAction()
        {
            if (OpenTavernHint is null)
            {
                return;
            }

            switch (TavernSettings.Mode)
            {
                case LaunchMode.Desktop:
                    OpenTavernHint.Text = DesktopWindow.IsOpen
                        ? "窗口已打开 · 点击唤到前台"
                        : "窗口已关闭 · 点击重新打开";
                    break;

                case LaunchMode.Server:
                    OpenTavernHint.Text = "查看连接地址";
                    break;

                default:
                    OpenTavernHint.Text = $"用 {TavernSettings.BrowserName} 打开";
                    break;
            }

            if (HeroOpenButton is not null)
            {
                // 服务跑起来之后主卡上就得有入口：桌面模式开/唤回窗口，服务器模式弹访问地址，
                // 普通模式用浏览器打开。停下时收起——没进程可开的时候点它只会弹一句「还没启动」
                Visibility open = TavernRuntime.IsRunning ? Visibility.Visible : Visibility.Collapsed;

                HeroOpenButton.Visibility = open;

                // 分隔线只是这颗按钮的陪衬：按钮不在，它就跟着收起，别在启停两颗按钮右边留一根孤线
                if (OpenDivider is not null)
                {
                    OpenDivider.Visibility = open;
                }

                // 三种模式都是「访问酒馆」这一个动作，区别只体现在图标与提示上：
                // 图标沿用「默认启动方式」里那三颗（地球 = 浏览器、窗口 = 桌面、设备 = 服务器），
                // 用户在同一套界面里看到的是同一套符号，不会以为点了别的东西。
                string glyph = TavernSettings.Mode switch
                {
                    LaunchMode.Desktop => "\uE7F4",
                    LaunchMode.Server => "\uE968",
                    _ => "\uE774",
                };

                if (HeroOpenText is not null && HeroOpenGlyph is not null)
                {
                    HeroOpenText.Text = "访问酒馆";
                    HeroOpenGlyph.Glyph = glyph;

                    ToolTipService.SetToolTip(
                        HeroOpenButton,
                        TavernSettings.Mode switch
                        {
                            LaunchMode.Desktop => "打开酒馆窗口（已开着就唤到前台）",
                            LaunchMode.Server => "查看手机 / 平板的访问地址与二维码",
                            _ => "用浏览器打开酒馆",
                        });
                }

                if (Qa1Text is not null && Qa1Glyph is not null)
                {
                    Qa1Text.Text = "访问酒馆";
                    Qa1Glyph.Glyph = glyph;
                }
            }
        }

        private async void OpenTavern_Click(object sender, RoutedEventArgs e)
        {
            if (!TavernRuntime.IsRunning)
            {
                AppNotify.Info(
                    "酒馆还没启动",
                    "点「启动服务」把酒馆跑起来之后，这里会直接用浏览器打开它。",
                    "启动",
                    () => _ = TavernRuntime.StartAsync());
                return;
            }

            string url = EndpointText.Text;

            // 服务器模式：酒馆本来就是给别的设备用的，在本机再开一个页面不如把地址摆出来
            if (TavernSettings.Mode == LaunchMode.Server)
            {
                await TavernAccessDialog.ShowAsync(XamlRoot, ActualTheme);
                return;
            }

            bool windowOpen = DesktopWindow.IsOpen;

            // 桌面模式开内嵌窗口（已经在跑就只唤到前台），普通模式仍用浏览器
            if (DesktopWindow.OpenTavern(url, out string detail))
            {
                string title = TavernSettings.Mode == LaunchMode.Desktop
                    ? windowOpen ? "桌面窗口已在前台" : "已打开桌面窗口"
                    : "已在浏览器中打开酒馆";

                AppNotify.Success(title, $"{url} · {detail}");
                return;
            }

            AppNotify.Error("打开失败", $"{url} · {detail}");
        }

        private void OpenDataDir_Click(object sender, RoutedEventArgs e)
        {
            string path = TavernRuntime.ResolveDataRoot();

            if (!Directory.Exists(path))
            {
                AppNotify.Warning("数据目录还不存在", path + " 会在酒馆第一次启动时创建。");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                AppNotify.Success("已打开数据目录", path);
            }
            catch (Exception ex)
            {
                AppNotify.Error("打不开数据目录", ex.Message);
            }
        }

        /// <summary>
        /// 导入角色卡：选 PNG / JSON 直接复制进酒馆的 characters 目录。
        /// 复制完刷新一次指标——卡片数字应该当场变，而不是等下次进页面。
        /// </summary>
        private async void ImportCard_Click(object sender, RoutedEventArgs e)
        {
            string root = TavernRuntime.ResolveDataRoot();
            string target = Path.Combine(root, "default-user", "characters");

            IReadOnlyList<StorageFile> files;

            try
            {
                var picker = new FileOpenPicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                    ViewMode = PickerViewMode.Thumbnail,
                };

                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".json");

                if (App.MainWindow is not null)
                {
                    InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
                }

                files = await picker.PickMultipleFilesAsync();
            }
            catch (Exception ex)
            {
                AppNotify.Error("打开文件选择器失败", ex.Message);
                return;
            }

            if (files.Count == 0)
            {
                return;
            }

            int copied = 0;
            string? firstError = null;

            try
            {
                Directory.CreateDirectory(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppNotify.Error("角色卡目录不可写", target + "：" + ex.Message);
                return;
            }

            foreach (StorageFile file in files)
            {
                try
                {
                    File.Copy(file.Path, UniquePath(target, file.Name), overwrite: false);
                    copied++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    firstError ??= ex.Message;
                }
            }

            if (copied == 0)
            {
                AppNotify.Error("导入失败", firstError ?? "没有文件被复制。");
                return;
            }

            AppNotify.Success(
                $"已导入 {copied} 张角色卡",
                firstError is null
                    ? "已复制到酒馆的角色卡目录，刷新酒馆页面即可看到。"
                    : $"其中 {files.Count - copied} 张失败：{firstError}");

            await RefreshMetricsAsync(force: true);
        }

        /// <summary>同名文件不覆盖：加「(2)」后缀，避免把用户已有的卡顶掉。</summary>
        private static string UniquePath(string directory, string fileName)
        {
            string path = Path.Combine(directory, fileName);

            if (!File.Exists(path))
            {
                return path;
            }

            string stem = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);

            for (int index = 2; index < 1000; index++)
            {
                path = Path.Combine(directory, $"{stem} ({index}){extension}");

                if (!File.Exists(path))
                {
                    return path;
                }
            }

            return Path.Combine(directory, $"{stem}-{Guid.NewGuid():N}{extension}");
        }

        private void OpenConfig_Click(object sender, RoutedEventArgs e)
        {
            App.MainWindow?.NavigateTo("tavern");
        }

        // ==================== 状态同步 ====================

        /// <summary>切换运行/停止态：主卡、主按钮、指标条保持一致。</summary>
        private void SetServiceRunning()
        {
            TavernProcessState state = TavernRuntime.State;
            bool running = state.IsRunning;
            bool busy = state.IsBusy;

            ServiceStateText.Text = state.State switch
            {
                TavernRunState.Starting => "正在启动…",
                TavernRunState.Running => "服务运行中",
                TavernRunState.Stopping => "正在停止…",
                TavernRunState.Failed => "启动失败",
                _ => "服务已停止",
            };

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

            // 没在跑时主按钮是「启动」，跑起来才变成「重启」——一个按钮兼顾两件事，
            // 也避免「停止状态点重启什么都不会发生」
            PrimaryActionButton.IsEnabled = !busy;
            PrimaryActionText.Text = running ? "重启服务" : "启动服务";
            PrimaryActionGlyph.Glyph = running ? "\uE72C" : "\uE768";
            HeroStopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

            UpdateUptime();
            UpdateProcessMetrics();
            SyncEndpoint();
            UpdateEnvironmentText();
            SyncOpenTavernAction();
        }

        /// <summary>运行状态只有一份：控制台在跑什么，仪表盘的徽标就显示什么。</summary>
        private void Runtime_Changed(object? sender, EventArgs e) => SetServiceRunning();

        /// <summary>已持续运行 / PID：都取自运行时快照，没在跑就显示占位。</summary>
        private void UpdateUptime()
        {
            if (HeroUptimeValue is null || HeroUptimePid is null)
            {
                return;
            }

            TavernProcessState state = TavernRuntime.State;

            if (!state.IsRunning || state.StartedAt is not { } started)
            {
                HeroUptimeValue.Text = "--:--:--";
                HeroUptimePid.Text = "PID —";
                return;
            }

            TimeSpan span = DateTime.Now - started;
            HeroUptimeValue.Text = $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
            HeroUptimePid.Text = state.Pid is { } pid ? $"PID {pid}" : "PID —";
        }
    }
}
