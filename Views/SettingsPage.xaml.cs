using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AstraBrew_Launcher.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace AstraBrew_Launcher.Views
{
    /// <summary>
    /// 设置：按「界面 / 基础 / 控制台 / 运行环境 / 下载 / 网络 / 更新」分组。
    ///
    /// 分组结构照 AstraBrew-Launcher-Win 的 settings.rs：每组一个 38 图标座 + 标题 + 一句说明，
    /// 组内是一张卡片，每行「图标 + 标题/说明 + 右侧控件」，行间用 1px 分隔线。
    /// 条件行（桌面模式 / 服务器模式 / 自定义代理）连同它上面的分隔线一起收起，
    /// 避免出现「只剩一条线」的空档。
    /// </summary>
    public sealed partial class SettingsPage : Page
    {
        /// <summary>一套环境里各依赖的版本；null 表示未安装。</summary>
        private sealed record EnvProfile(
            string? Git,
            string? Node,
            bool NodeOutdated,
            string? Caddy,
            string? Pm2,
            string? WebView2);

        private static readonly Dictionary<RuntimeSource, EnvProfile> Environments = new()
        {
            [RuntimeSource.Builtin] = new("2.55.0", "v24.14.0", false, "v2.10.0", "6.0.5", "128.0.6613.119"),
            [RuntimeSource.System] = new("2.43.0", null, false, null, null, "120.0.2210.91"),
        };

        /// <summary>GitHub 连通性探测的节点，顺序即探测顺序。</summary>
        private static readonly (string Name, string Host, int Latency, bool Ok)[] GithubProbes =
        {
            ("github.com", "网页与克隆入口", 412, true),
            ("api.github.com", "Release 元数据", 268, true),
            ("codeload.github.com", "源码压缩包", 0, false),
            ("gh-proxy.org", "公益反代通道", 168, true),
            ("ghfast.top", "备用反代通道", 245, true),
            ("gt.astrabrew.cn", "自建反代通道", 312, true),
        };

        /// <summary>下载通道测速结果，与 <see cref="DownloadChannel"/> 顺序一致。</summary>
        private static readonly (DownloadChannel Channel, double Speed, bool Ok)[] ChannelProbes =
        {
            (DownloadChannel.Auto, 4.8, true),
            (DownloadChannel.GhProxy, 4.8, true),
            (DownloadChannel.GhFast, 2.6, true),
            (DownloadChannel.Official, 0, false),
        };

        private readonly DispatcherQueueTimer _saveTimer;

        /// <summary>
        /// 初始为 true：XAML 解析阶段控件还没全部就位，此时任何 ValueChanged / Toggled
        /// 都不该回写状态，更不该去碰后面才声明的控件（滑杆的 Minimum 一赋值就会触发 ValueChanged）。
        /// <see cref="LoadStateToUi"/> 结束时统一放开。
        /// </summary>
        private bool _sync = true;

        public SettingsPage()
        {
            InitializeComponent();

            _saveTimer = DispatcherQueue.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(650);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) =>
            {
                SaveStateText.Text = "已生效";
                SaveDot.Fill = Success();
            };

            LoadStateToUi();

            LauncherSettings.Changed += OnSettingsChanged;
            Unloaded += (_, _) => LauncherSettings.Changed -= OnSettingsChanged;

            ShowPath(OnboardingPathText, OnboardingState.FilePath);
            VersionChipText.Text = ReadAppVersion();
        }

        private bool IsLight => ActualTheme == ElementTheme.Light;

        // 代码里拼出来的弹窗拿不到 ThemeResource 的自动换色，这里按当前主题各给一套值。
        // 取色与 Controls.xaml 里的语义色一致：浅色压深、深色提亮。
        private Brush Primary() => Solid((byte)(IsLight ? 0x1A : 0xF5));

        private Brush Secondary() => Solid((byte)(IsLight ? 0x5C : 0xA8));

        private Brush Tertiary() => Solid((byte)(IsLight ? 0x7A : 0x8A));

        private Brush Success() => new SolidColorBrush(IsLight
            ? Color.FromArgb(0xFF, 0x0F, 0x7B, 0x0F)
            : Color.FromArgb(0xFF, 0x6C, 0xCB, 0x5F));

        private Brush Caution() => new SolidColorBrush(IsLight
            ? Color.FromArgb(0xFF, 0x9D, 0x5D, 0x00)
            : Color.FromArgb(0xFF, 0xFC, 0xE1, 0x00));

        private Brush Critical() => new SolidColorBrush(IsLight
            ? Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C)
            : Color.FromArgb(0xFF, 0xFF, 0x99, 0xA4));

        private static Brush Solid(byte value) =>
            new SolidColorBrush(Color.FromArgb(0xFF, value, value, value));

        // ==================== 状态 → 界面 ====================

        private void LoadStateToUi()
        {
            _sync = true;
            try
            {
                LanguagePicker.SelectedIndex = (int)LauncherSettings.Language;
                ThemePicker.SelectedIndex = (int)LauncherSettings.Theme;
                ScaleSlider.Value = LauncherSettings.UiScale * 100;
                ScaleValueText.Text = $"{ScaleSlider.Value:0}%";
                FontCombo.SelectedIndex = LauncherSettings.FontIndex;
                RememberWindowSwitch.IsOn = LauncherSettings.RememberWindow;

                AutoLaunchSwitch.IsOn = LauncherSettings.AutoLaunch;
                CpuPicker.SelectedIndex = (int)LauncherSettings.CpuCores;
                StartModePicker.SelectedIndex = (int)LauncherSettings.StartMode;
                StopOnCloseSwitch.IsOn = LauncherSettings.StopTavernOnWindowClose;
                ShowPath(ExportPathText, Expand(LauncherSettings.ExportPath));

                ServerModeSwitch.IsOn = LauncherSettings.ServerMode;
                ScopePicker.SelectedIndex = (int)LauncherSettings.ServiceScope;
                BackgroundSwitch.IsOn = LauncherSettings.AllowBackground;

                DataLocationPicker.SelectedIndex = (int)LauncherSettings.DataLocation;
                ShowPath(GlobalDataPathText, Expand(LauncherSettings.GlobalDataPath));

                StartupCommandSwitch.IsOn = LauncherSettings.ShowStartupCommand;

                RuntimePicker.SelectedIndex = (int)LauncherSettings.Runtime;
                NpmPicker.SelectedIndex = (int)LauncherSettings.NpmRegistry;
                NpmUrlText.Text = LauncherSettings.NpmRegistryUrl;

                ChannelPicker.SelectedIndex = (int)LauncherSettings.Channel;

                ProxyPicker.SelectedIndex = (int)LauncherSettings.Proxy;
                CustomProxyBox.Text = LauncherSettings.CustomProxy;
            }
            finally
            {
                _sync = false;
            }

            RefreshDerived();
        }

        /// <summary>
        /// 只重画「由状态推导出来」的部分（条件行、依赖版本、测速文案）。
        ///
        /// 不去回写控件本身：外部改了状态时回写会把正在拖动的滑杆拽回去，
        /// 也会让「用户在操作 A 行」的输入焦点被无谓打断。
        /// </summary>
        private void RefreshDerived()
        {
            ApplyConditions();
            ApplyEnvironment();
            UpdateChannelUi();
            UpdateProxyUi();
            UpdateGithubTestUi();
        }

        private void OnSettingsChanged(object? sender, EventArgs e)
        {
            if (!_sync)
            {
                RefreshDerived();
            }
        }

        /// <summary>按当前状态决定哪些条件行可见、哪些控件因依赖缺失而禁用。</summary>
        private void ApplyConditions()
        {
            DesktopRows.Visibility = LauncherSettings.StartMode == QuickStartMode.Desktop
                ? Visibility.Visible
                : Visibility.Collapsed;

            ServerRows.Visibility = LauncherSettings.ServerMode
                ? Visibility.Visible
                : Visibility.Collapsed;

            InternetRows.Visibility = LauncherSettings.ServerMode
                && LauncherSettings.ServiceScope == ServiceScopeSetting.Internet
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            CustomProxyRows.Visibility = LauncherSettings.Proxy == ProxyModeSetting.Custom
                ? Visibility.Visible
                : Visibility.Collapsed;

            // 后台常驻依赖 PM2；PM2 又依赖 Node.js。缺一环就禁用开关并说明原因。
            var env = Environments[LauncherSettings.Runtime];
            var pm2Ready = env.Pm2 is not null;
            BackgroundSwitch.IsEnabled = pm2Ready;
            BackgroundSwitch.Opacity = pm2Ready ? 1 : 0.45;
            BackgroundBlockedPill.Visibility = pm2Ready ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>把当前环境来源的版本写进依赖行：有版本显示版本，没有就给安装入口。</summary>
        private void ApplyEnvironment()
        {
            var env = Environments[LauncherSettings.Runtime];
            var isSystem = LauncherSettings.Runtime == RuntimeSource.System;

            ApplyDependency(GitVersionText, GitInstallButton, null, env.Git, !isSystem);
            ApplyDependency(NodeVersionText, NodeInstallButton, NodeOutdatedPill, env.Node, !isSystem, env.NodeOutdated);
            ApplyDependency(CaddyVersionText, CaddyInstallButton, null, env.Caddy, !isSystem);

            // 系统环境里不提供安装入口；内置环境里 PM2 还要等 Node.js 先就位
            var pm2Blocked = env.Node is null;
            ApplyDependency(Pm2VersionText, Pm2InstallButton, null, env.Pm2, !isSystem && !pm2Blocked);
            Pm2NoteText.Visibility = pm2Blocked && !isSystem ? Visibility.Visible : Visibility.Collapsed;

            ApplyDependency(WebView2VersionText, WebView2InstallButton, null, env.WebView2, !isSystem);

            GitRow.Description = isSystem
                ? "读取系统 PATH 里的 Git，启动器不负责安装与升级。"
                : "拉取与更新酒馆源码，随启动器一起搬走。";
            NodeRow.Description = isSystem
                ? "读取系统环境里的 Node.js，缺失时请自行到官网安装。"
                : "运行酒馆与全部扩展的 JavaScript 运行时。";
            CaddyRow.Description = LauncherSettings.ServerMode
                ? "服务器模式必需：负责反向代理与 HTTPS。"
                : "服务器模式下才需要：负责反向代理与 HTTPS。";
            NpmRow.Description = $"安装扩展与 PM2 时的包源，当前 {LauncherSettings.NpmRegistryName}。";
        }

        private void ApplyDependency(
            TextBlock versionText,
            Button installButton,
            Border? outdatedPill,
            string? version,
            bool canInstall,
            bool outdated = false)
        {
            versionText.Text = version ?? "未安装";
            versionText.Foreground = version is null ? Tertiary() : Primary();

            installButton.Visibility = version is null && canInstall ? Visibility.Visible : Visibility.Collapsed;

            if (outdatedPill is not null)
            {
                outdatedPill.Visibility = outdated && version is not null ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void UpdateChannelUi()
        {
            var channel = LauncherSettings.Channel;
            var effective = LauncherSettings.EffectiveChannel;

            ChannelRow.Description = channel == DownloadChannel.Auto
                ? $"先测速再挑最快的通道；当前解析为 {LauncherSettings.ChannelName(effective)}。"
                : $"{LauncherSettings.ChannelName(channel)}：{LauncherSettings.ChannelHint(channel)}";

            if (!LauncherSettings.ChannelTested)
            {
                ChannelCacheText.Text = "未测速";
                ChannelCacheText.Foreground = Secondary();
                ChannelCacheHintText.Text = "自动通道依赖这条结果";
                return;
            }

            var valid = LauncherSettings.IsChannelCacheValid;
            ChannelCacheText.Text = valid ? "有效" : "已过期";
            ChannelCacheText.Foreground = valid ? Success() : Caution();
            ChannelCacheHintText.Text = LauncherSettings.ChannelTestedAt is { } tested
                ? $"{tested:HH:mm} 测速 · 最快 {LauncherSettings.ChannelName(effective)}"
                : string.Empty;
        }

        private void UpdateProxyUi()
        {
            ProxyRow.Description = LauncherSettings.Proxy switch
            {
                ProxyModeSetting.None => "直连，不经过任何代理；GitHub 访问失败时请改成系统或自定义。",
                ProxyModeSetting.Custom => "使用下面填写的地址，仅对本启动器生效。",
                _ => "跟随 Windows「Internet 选项」里的代理设置。",
            };

            // 只在值真的变了才回写：否则每敲一个字都会被重置，光标会跳回开头
            if (CustomProxyBox.Text != LauncherSettings.CustomProxy)
            {
                CustomProxyBox.Text = LauncherSettings.CustomProxy;
            }
        }

        private void UpdateGithubTestUi()
        {
            GithubTestText.Text = LauncherSettings.GithubBestLatency is { } latency
                ? $"最佳 {latency} ms"
                : "未测试";
        }

        /// <summary>改动后先显示「保存中…」，落定后再回到「已生效」。</summary>
        private void MarkDirty()
        {
            SaveStateText.Text = "保存中…";
            SaveDot.Fill = Caution();
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        // ==================== 界面 ====================

        private void LanguagePicker_SelectionChanged(object sender, EventArgs e)
        {
            if (_sync)
            {
                return;
            }

            LauncherSettings.SetLanguage((AppLanguage)LanguagePicker.SelectedIndex);
            MarkDirty();
        }

        private void ThemePicker_SelectionChanged(object sender, EventArgs e)
        {
            if (_sync)
            {
                return;
            }

            LauncherSettings.SetTheme((AppTheme)ThemePicker.SelectedIndex);
            MarkDirty();
        }

        private void ScaleSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_sync)
            {
                return;
            }

            ScaleValueText.Text = $"{e.NewValue:0}%";
            LauncherSettings.SetUiScale(e.NewValue / 100);
            MarkDirty();
        }

        private void FontCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_sync)
            {
                return;
            }

            LauncherSettings.SetFontIndex(FontCombo.SelectedIndex);
            MarkDirty();
        }

        private void RememberWindowSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_sync)
            {
                return;
            }

            LauncherSettings.SetRememberWindow(RememberWindowSwitch.IsOn);
            MarkDirty();
        }

        // ==================== 基础 ====================

        private void AutoLaunchSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_sync)
            {
                return;
            }

            LauncherSettings.SetAutoLaunch(AutoLaunchSwitch.IsOn);
            MarkDirty();
        }

        private void CpuPicker_SelectionChanged(object sender, EventArgs e)
        {
            if (_sync)
            {
                return;
            }

            LauncherSettings.SetCpuCores((CpuCorePreference)CpuPicker.SelectedIndex);
            MarkDirty();
        }

        private void StartModePicker_SelectionChanged(object sender, EventArgs e)
        {
            if (_sync)
            {
                return;
            }

            LauncherSettings.SetStartMode((QuickStartMode)StartModePicker.SelectedIndex);
            ApplyConditions();
            MarkDirty();
        }

        private void StopOnCloseSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_sync)
            {
                return;
            }

            LauncherSettings.SetStopTavernOnWindowClose(StopOnCloseSwitch.IsOn);
            MarkDirty();
        }

        private void ServerModeSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_sync)
            {
                return;
            }

            LauncherSettings.SetServerMode(ServerModeSwitch.IsOn);
            ApplyConditions();
            ApplyEnvironment();
            MarkDirty();
        }

        private void ScopePicker_SelectionChanged(object sender, EventArgs e)
        {
            if (_sync)
            {
                return;
            }

            LauncherSettings.SetServiceScope((ServiceScopeSetting)ScopePicker.SelectedIndex);
            ApplyConditions();
            MarkDirty();
        }

        private void BackgroundSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_sync)
            {
                return;
            }

            LauncherSettings.SetAllowBackground(BackgroundSwitch.IsOn);
            MarkDirty();
        }

        private void DataLocationPicker_SelectionChanged(object sender, EventArgs e)
        {
            if (_sync)
            {
                return;
            }

            LauncherSettings.SetDataLocation((DataLocationMode)DataLocationPicker.SelectedIndex);
            MarkDirty();
        }

        // ==================== 控制台 ====================

        private void StartupCommandSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_sync)
            {
                return;
            }

            LauncherSettings.SetShowStartupCommand(StartupCommandSwitch.IsOn);
            MarkDirty();
        }

        // ==================== 运行环境 ====================

        private void RuntimePicker_SelectionChanged(object sender, EventArgs e)
        {
            if (_sync)
            {
                return;
            }

            LauncherSettings.SetRuntime((RuntimeSource)RuntimePicker.SelectedIndex);
            ApplyEnvironment();
            ApplyConditions();
            MarkDirty();
        }

        private void NpmPicker_SelectionChanged(object sender, EventArgs e)
        {
            if (_sync)
            {
                return;
            }

            LauncherSettings.SetNpmRegistry((NpmRegistrySource)NpmPicker.SelectedIndex);
            NpmUrlText.Text = LauncherSettings.NpmRegistryUrl;
            ApplyEnvironment();
            MarkDirty();
        }

        private async void InstallGit_Click(object sender, RoutedEventArgs e) =>
            await InstallAsync("Git", "MinGit 2.55.0.windows.2", "Git", "2.55.0");

        private async void InstallNode_Click(object sender, RoutedEventArgs e) =>
            await InstallAsync("Node.js", "v24.14.0", "Node", "v24.14.0");

        private async void InstallCaddy_Click(object sender, RoutedEventArgs e) =>
            await InstallAsync("Caddy", "v2.10.0", "Caddy", "v2.10.0");

        private async void InstallPm2_Click(object sender, RoutedEventArgs e) =>
            await InstallAsync("PM2", "6.0.5", "Pm2", "6.0.5");

        private async void InstallWebView2_Click(object sender, RoutedEventArgs e) =>
            await InstallAsync("WebView2 运行时", "Evergreen Standalone", "WebView2", "128.0.6613.119");

        /// <summary>装完之后把版本写进当前环境，行上的「安装」按钮随之换成版本号。</summary>
        private void MarkInstalled(string field, string version)
        {
            var source = LauncherSettings.Runtime;
            var env = Environments[source];
            Environments[source] = field switch
            {
                "Git" => env with { Git = version },
                "Node" => env with { Node = version },
                "Caddy" => env with { Caddy = version },
                "Pm2" => env with { Pm2 = version },
                _ => env with { WebView2 = version },
            };

            ApplyEnvironment();
            ApplyConditions();
        }

        // ==================== 下载 ====================

        private void ChannelPicker_SelectionChanged(object sender, EventArgs e)
        {
            if (_sync)
            {
                return;
            }

            LauncherSettings.SetChannel((DownloadChannel)ChannelPicker.SelectedIndex);
            UpdateChannelUi();
            MarkDirty();
        }

        private async void RetestChannel_Click(object sender, RoutedEventArgs e) =>
            await ShowChannelTestAsync();

        // ==================== 网络 ====================

        private void ProxyPicker_SelectionChanged(object sender, EventArgs e)
        {
            if (_sync)
            {
                return;
            }

            LauncherSettings.SetProxy((ProxyModeSetting)ProxyPicker.SelectedIndex);
            ApplyConditions();
            UpdateProxyUi();
            MarkDirty();
        }

        private void CustomProxyBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_sync)
            {
                return;
            }

            LauncherSettings.SetCustomProxy(CustomProxyBox.Text);
            MarkDirty();
        }

        private async void TestGithub_Click(object sender, RoutedEventArgs e) =>
            await ShowGithubTestAsync();

        // ==================== 更新 ====================

        private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            CheckUpdateButton.IsEnabled = false;
            CheckUpdateLabel.Text = "检查中…";

            try
            {
                await Task.Delay(900);
            }
            finally
            {
                CheckUpdateButton.IsEnabled = true;
                CheckUpdateLabel.Text = "检查更新";
            }

            await ShowUpdateDialogAsync();
        }

        private void RestoreDefaults_Click(object sender, RoutedEventArgs e) => _ = ShowRestoreDialogAsync();

        // ==================== 原型工具 ====================

        private void ReplayOnboarding_Click(object sender, RoutedEventArgs e)
        {
            OnboardingState.Reset();
            App.MainWindow?.ShowOnboarding();
        }

        private void CopyOnboardingPath_Click(object sender, RoutedEventArgs e)
        {
            var package = new DataPackage();
            package.SetText(OnboardingState.FilePath);
            Clipboard.SetContent(package);
        }

        private void OpenConfigDir_Click(object sender, RoutedEventArgs e) =>
            RevealInExplorer(Path.GetDirectoryName(OnboardingState.FilePath));

        private void OpenStartupSettings_Click(object sender, RoutedEventArgs e) =>
            Process.Start(new ProcessStartInfo { FileName = "ms-settings:startupapps", UseShellExecute = true });

        private async void PickExportPath_Click(object sender, RoutedEventArgs e)
        {
            var folder = await PickFolderAsync();
            if (folder is null)
            {
                return;
            }

            LauncherSettings.SetExportPath(folder);
            ShowPath(ExportPathText, folder);
            MarkDirty();
        }

        private void OpenExportPath_Click(object sender, RoutedEventArgs e) =>
            RevealInExplorer(Expand(LauncherSettings.ExportPath));

        private async void PickGlobalDataPath_Click(object sender, RoutedEventArgs e)
        {
            var folder = await PickFolderAsync();
            if (folder is null)
            {
                return;
            }

            LauncherSettings.SetGlobalDataPath(folder);
            ShowPath(GlobalDataPathText, folder);
            MarkDirty();
        }

        private void OpenGlobalDataPath_Click(object sender, RoutedEventArgs e) =>
            RevealInExplorer(Expand(LauncherSettings.GlobalDataPath));

        // ==================== 系统交互 ====================

        /// <summary>
        /// 路径控件宽度有限，把用户目录折叠成 %APPDATA% / %LOCALAPPDATA%，
        /// 让「装在哪一级目录」一眼可见；完整路径放进 ToolTip。
        /// </summary>
        private static void ShowPath(TextBlock target, string fullPath)
        {
            var text = fullPath;
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            if (text.StartsWith(local, StringComparison.OrdinalIgnoreCase))
            {
                text = "%LOCALAPPDATA%" + text[local.Length..];
            }
            else if (text.StartsWith(roaming, StringComparison.OrdinalIgnoreCase))
            {
                text = "%APPDATA%" + text[roaming.Length..];
            }

            target.Text = text;
            ToolTipService.SetToolTip(target, fullPath);
        }

        private static string Expand(string path) =>
            Environment.ExpandEnvironmentVariables(path.Replace(
                "%APPDATA%",
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)));

        private async Task<string?> PickFolderAsync()
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");

            if (App.MainWindow is not null)
            {
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            }

            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }

        private static void RevealInExplorer(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = true,
                });
            }
            catch (IOException)
            {
                // 目标不可访问时静默失败：设置页不该因为一个目录打不开而弹错误。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static string ReadAppVersion()
        {
            var version = typeof(SettingsPage).Assembly.GetName().Version;
            return version is null ? "v0.1.0" : $"v{version.Major}.{version.Minor}.{version.Build}";
        }

        // ==================== 弹窗外壳 ====================

        /// <summary>
        /// 统一的弹窗外壳：图标座 + 标题 / 副标题 + 自定义内容 + 右对齐底栏。
        ///
        /// 不用 ContentDialog 自带的按钮区：它是等分两列，弹窗一宽按钮就被拉成两块砖头，
        /// 设置 Width / HorizontalAlignment 也改不动位置。因此按钮全部自己拼在内容里。
        /// </summary>
        private (ContentDialog Dialog, StackPanel Body, StackPanel Footer) CreateShell(
            string glyph,
            Color accent,
            string title,
            string subtitle,
            double width)
        {
            var icon = new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(10),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(0x1F, accent.R, accent.G, accent.B)),
                Child = new FontIcon
                {
                    FontSize = 18,
                    Glyph = glyph,
                    Foreground = new SolidColorBrush(accent),
                },
            };

            var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            heading.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            heading.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Secondary(),
            });

            var head = new Grid { ColumnSpacing = 12 };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.Children.Add(icon);
            Grid.SetColumn(heading, 1);
            head.Children.Add(heading);

            var body = new StackPanel { Spacing = 12 };
            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            var root = new StackPanel { Spacing = 16, MinWidth = width - 48 };
            root.Children.Add(head);
            root.Children.Add(body);
            root.Children.Add(footer);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Content = root,
                DefaultButton = ContentDialogButton.None,
            };
            dialog.Resources["ContentDialogMaxWidth"] = width;

            // 没有底部按钮区时 ContentDialog 自己不吃 Esc，这里补一个快捷键
            var escape = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
            escape.Invoked += (_, args) =>
            {
                args.Handled = true;
                dialog.Hide();
            };
            dialog.KeyboardAccelerators.Add(escape);

            return (dialog, body, footer);
        }

        private static Button ShellButton(string text, bool accent = false, bool danger = false)
        {
            var button = new Button { Content = text, MinWidth = 96 };
            var key = danger ? "DangerDialogButtonStyle" : accent ? "AccentButtonStyle" : null;
            if (key is not null
                && Application.Current.Resources.TryGetValue(key, out object style)
                && style is Style buttonStyle)
            {
                button.Style = buttonStyle;
            }

            return button;
        }

        private static Border ShellPanel(UIElement child) =>
            new()
            {
                Style = (Style)Application.Current.Resources["SubPanelStyle"],
                Padding = new Thickness(12, 10, 12, 10),
                Child = child,
            };

        /// <summary>两列行：左侧「名称 / 说明」，右侧状态值。</summary>
        private Grid ProbeRow(string name, string hint, TextBlock status)
        {
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            label.Children.Add(new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeights.SemiBold });
            label.Children.Add(new TextBlock { Text = hint, FontSize = 11, Foreground = Secondary() });

            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(label);
            Grid.SetColumn(status, 1);
            row.Children.Add(status);
            return row;
        }

        private TextBlock CreateStatus(string text) => new()
        {
            Text = text,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Secondary(),
        };

        // ==================== 安装任务弹窗 ====================

        private async Task InstallAsync(string name, string version, string field, string newVersion)
        {
            var accent = Color.FromArgb(0xFF, 0x2B, 0x88, 0xD8);
            var (dialog, body, footer) = CreateShell(
                "\uE896",
                accent,
                $"安装 {name}",
                $"{version} · 下载并解压到内置环境",
                560);

            var phaseText = new TextBlock
            {
                Text = "准备下载…",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var percentText = new TextBlock
            {
                Text = "0%",
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Secondary(),
            };
            var progressHead = new Grid();
            progressHead.Children.Add(phaseText);
            progressHead.Children.Add(percentText);

            var bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 4, Value = 0 };

            var logText = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Secondary(),
            };
            var logScroll = new ScrollViewer
            {
                MaxHeight = 128,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = logText,
            };

            body.Children.Add(progressHead);
            body.Children.Add(bar);
            body.Children.Add(ShellPanel(logScroll));

            var close = ShellButton("取消");
            footer.Children.Add(close);

            var cancelled = new CancellationTokenSource();
            close.Click += (_, _) =>
            {
                cancelled.Cancel();
                dialog.Hide();
            };

            var lines = new List<string>();
            var steps = new[]
            {
                $"GET https://github.com/…/{name} · 12.4 MB",
                "校验 SHA-256 与发布清单一致",
                "解压到 %APPDATA%\\AstraBrew Launcher\\lib",
                "写入版本标记，注册到 PATH 前置目录",
            };

            dialog.Opened += async (_, _) =>
            {
                for (var i = 0; i < steps.Length; i++)
                {
                    if (cancelled.IsCancellationRequested)
                    {
                        return;
                    }

                    phaseText.Text = steps[i];
                    lines.Add($"[{DateTime.Now:HH:mm:ss}] {steps[i]}");
                    logText.Text = string.Join(Environment.NewLine, lines);
                    logScroll.ChangeView(null, logScroll.ScrollableHeight, null);

                    var from = i * 100.0 / steps.Length;
                    var to = (i + 1) * 100.0 / steps.Length;
                    for (var value = from; value <= to; value += 4)
                    {
                        if (cancelled.IsCancellationRequested)
                        {
                            return;
                        }

                        bar.Value = value;
                        percentText.Text = $"{value:0}%";
                        await Task.Delay(28);
                    }
                }

                bar.Value = 100;
                percentText.Text = "100%";
                phaseText.Text = "安装完成";
                lines.Add($"[{DateTime.Now:HH:mm:ss}] {name} {newVersion} 已就绪");
                logText.Text = string.Join(Environment.NewLine, lines);
                logScroll.ChangeView(null, logScroll.ScrollableHeight, null);

                close.Content = "完成";
                MarkInstalled(field, newVersion);
            };

            await dialog.ShowAsync();
        }

        // ==================== 连通性测速弹窗 ====================

        private async Task ShowGithubTestAsync()
        {
            var accent = Color.FromArgb(0xFF, 0x0E, 0x9A, 0xA7);
            var (dialog, body, footer) = CreateShell(
                "\uEC4A",
                accent,
                "GitHub 连通性测试",
                "依次探测网页、API 与下载域名；取最快的一条，失败会标红。",
                640);

            var statuses = new List<TextBlock>();
            foreach (var probe in GithubProbes)
            {
                var status = CreateStatus("等待");
                statuses.Add(status);
                body.Children.Add(ProbeRow(probe.Name, probe.Host, status));
            }

            var summary = new TextBlock
            {
                Text = string.Empty,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Secondary(),
            };
            body.Children.Add(summary);

            var close = ShellButton("关闭");
            var retest = ShellButton("重新测试", accent: true);
            footer.Children.Add(close);
            footer.Children.Add(retest);

            close.Click += (_, _) => dialog.Hide();
            retest.Click += async (_, _) => await ProbeGithubAsync(statuses, summary, retest);

            dialog.Opened += async (_, _) => await ProbeGithubAsync(statuses, summary, retest);
            await dialog.ShowAsync();
        }

        private async Task ProbeGithubAsync(List<TextBlock> statuses, TextBlock summary, Button retest)
        {
            retest.IsEnabled = false;
            summary.Text = "正在探测…";

            foreach (var status in statuses)
            {
                status.Text = "探测中…";
                status.Foreground = Secondary();
            }

            for (var i = 0; i < GithubProbes.Length; i++)
            {
                await Task.Delay(240);
                var probe = GithubProbes[i];
                var status = statuses[i];

                if (!probe.Ok)
                {
                    status.Text = "超时";
                    status.Foreground = Critical();
                    continue;
                }

                status.Text = $"{probe.Latency} ms";
                status.Foreground = probe.Latency <= 300 ? Success() : Caution();
            }

            var fastest = GithubProbes.Where(p => p.Ok).OrderBy(p => p.Latency).First();
            summary.Text = $"最快 {fastest.Latency} ms（{fastest.Name}）。codeload 直连超时，下载源码必须走反代。";

            LauncherSettings.SetGithubTestResult(fastest.Latency);
            UpdateGithubTestUi();
            retest.IsEnabled = true;
        }

        private async Task ShowChannelTestAsync()
        {
            var accent = Color.FromArgb(0xFF, 0x2B, 0x88, 0xD8);
            var (dialog, body, footer) = CreateShell(
                "\uE72C",
                accent,
                "下载通道测速",
                "各通道各取一个 512 KB 分片，按平均速度排序；结果用于「自动」通道。",
                620);

            var statuses = new List<TextBlock>();
            foreach (var probe in ChannelProbes)
            {
                var status = CreateStatus("等待");
                statuses.Add(status);
                body.Children.Add(ProbeRow(
                    LauncherSettings.ChannelName(probe.Channel),
                    LauncherSettings.ChannelHint(probe.Channel),
                    status));
            }

            var summary = new TextBlock
            {
                Text = string.Empty,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Secondary(),
            };
            body.Children.Add(summary);

            var close = ShellButton("关闭");
            footer.Children.Add(close);
            close.Click += (_, _) => dialog.Hide();

            dialog.Opened += async (_, _) =>
            {
                for (var i = 0; i < ChannelProbes.Length; i++)
                {
                    statuses[i].Text = "测速中…";
                    await Task.Delay(320);

                    var probe = ChannelProbes[i];
                    if (!probe.Ok)
                    {
                        statuses[i].Text = "不可用";
                        statuses[i].Foreground = Critical();
                        continue;
                    }

                    statuses[i].Text = $"{probe.Speed:0.0} MB/s";
                    statuses[i].Foreground = probe.Speed >= 4 ? Success() : Caution();
                }

                summary.Text = "自动通道已解析为 gh-proxy.org（4.8 MB/s）；GitHub 直连不可用，需先配代理。";
                LauncherSettings.SetChannelTestResult(DownloadChannel.GhProxy);
                UpdateChannelUi();
            };

            await dialog.ShowAsync();
        }

        // ==================== 更新弹窗 ====================

        private async Task ShowUpdateDialogAsync()
        {
            var accent = Color.FromArgb(0xFF, 0x2B, 0x88, 0xD8);
            var (dialog, body, footer) = CreateShell(
                "\uE896",
                accent,
                "发现新版本 v0.4.2",
                $"当前 {VersionChipText.Text} · 2026-10-27 发布 · 12.6 MB",
                540);

            body.Children.Add(new TextBlock
            {
                Text = "更新内容",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Secondary(),
            });
            body.Children.Add(ShellPanel(new ScrollViewer
            {
                MaxHeight = 132,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Primary(),
                    Text = "· 控制台支持按关键字过滤日志\n"
                        + "· 资源工作台新增世界书条目搜索\n"
                        + "· 修复 125% 缩放下卡片网格列数算错的问题\n"
                        + "· 启动失败时给出可复制的诊断信息",
                },
            }));

            var later = ShellButton("稍后");
            var install = ShellButton("立即安装", accent: true);
            footer.Children.Add(later);
            footer.Children.Add(install);

            later.Click += (_, _) => dialog.Hide();
            install.Click += async (_, _) =>
            {
                dialog.Hide();
                CheckUpdateButton.IsEnabled = false;
                for (var percent = 0; percent <= 100; percent += 10)
                {
                    CheckUpdateLabel.Text = $"下载中 {percent}%";
                    await Task.Delay(90);
                }

                CheckUpdateLabel.Text = "已是最新";
            };

            await dialog.ShowAsync();
        }

        // ==================== 恢复默认 ====================

        private async Task ShowRestoreDialogAsync()
        {
            var accent = Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C);
            var (dialog, body, footer) = CreateShell(
                "\uE7A7",
                accent,
                "恢复默认设置",
                "所有偏好回到首次启动时的状态，包括环境来源、代理与下载通道。",
                480);

            body.Children.Add(new TextBlock
            {
                Text = "已经安装的 Git / Node.js / PM2 不会被删除，只是设置不再指向它们。路径类选项会重置为默认目录。",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Secondary(),
            });

            var cancel = ShellButton("取消");
            var confirm = ShellButton("恢复默认", danger: true);
            footer.Children.Add(cancel);
            footer.Children.Add(confirm);

            cancel.Click += (_, _) => dialog.Hide();
            confirm.Click += (_, _) =>
            {
                dialog.Hide();
                LauncherSettings.Reset();
                LoadStateToUi();
                MarkDirty();
            };

            await dialog.ShowAsync();
        }
    }
}
