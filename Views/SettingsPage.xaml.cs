using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AstraBrew_Launcher.Services;
using AstraBrew_Launcher.Views.Controls;
using AstraBrew_Launcher.Views.Onboarding;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;


namespace AstraBrew_Launcher.Views
{
    /// <summary>
    /// 设置：按「界面 / 基础 / 控制台 / 运行环境 / 下载 / 网络」分组。
    ///
    /// 版本与更新只在关于页出现，这里不再重复摆一份。
    /// 分组结构照 AstraBrew-Launcher-Win 的 settings.rs：每组一个 38 图标座 + 标题 + 一句说明，
    /// 组内是一张卡片，每行「图标 + 标题/说明 + 右侧控件」，行间用 1px 分隔线。
    /// 条件行（桌面模式 / 服务器模式 / 自定义代理）连同它上面的分隔线一起收起，
    /// 避免出现「只剩一条线」的空档。
    /// </summary>
    public sealed partial class SettingsPage : Page
    {
        /// <summary>
        /// 两套运行环境的真实检测结果，由 <see cref="RuntimeProbe"/> 异步填入。
        /// 里没有某个来源的键＝还没探测完，界面显示「检测中…」而不是「未安装」。
        /// </summary>
        private readonly Dictionary<RuntimeSource, RuntimeEnvironment> _envs = new();

        /// <summary>探测进行中：避免切页签／连点「重新检测」时重复拉子进程。</summary>
        private bool _probingEnvironment;

        /// <summary>
        /// 初始为 true：XAML 解析阶段控件还没全部就位，此时任何 ValueChanged / Toggled
        /// 都不该回写状态，更不该去碰后面才声明的控件（滑杆的 Minimum 一赋值就会触发 ValueChanged）。
        /// <see cref="LoadStateToUi"/> 结束时统一放开。
        /// </summary>
        private bool _sync = true;

        /// <summary>
        /// 正在把「加速开关」的开关状态从配置回填到控件。
        /// 直接赋 <c>IsOn</c> 会触发 Toggled，不挡住就会把回填当成用户操作再写回配置。
        /// </summary>
        private bool _syncingAccel;

        /// <summary>
        /// 全局 git 配置里当前有没有加速规则。null = 还没核对，或没有 Git 无法判断。
        /// 只用来写加速行的说明，开关状态始终以配置里的加速开关为准。
        /// </summary>
        private bool? _gitRewriteApplied;

        public SettingsPage()
        {
            InitializeComponent();

            // 字体列表必须在回填状态之前建好，否则 SelectedItem 找不到对应条目会落空
            BuildFontList();
            LoadStateToUi();

            // 换字体后整棵树会被重刷，已展开过的下拉项可能还在树上：每次打开重钉一遍预览字体
            FontCombo.DropDownOpened += (_, _) => ApplyFontPreviews();

            LauncherSettings.Changed += OnSettingsChanged;

            // 保存态徽章反映的是「真的写进 settings.toml 了没有」，由 SettingsStore 通知
            SettingsStore.Saved += OnSettingsSaved;

            // 语言是实时切换的：换完由 LanguageSwitch 发一条通知，本页把自定控件的文案刷一遍
            LanguageSwitch.LanguageChanged += OnLanguageChanged;

            Unloaded += (_, _) =>
            {
                LauncherSettings.Changed -= OnSettingsChanged;
                SettingsStore.Saved -= OnSettingsSaved;
                LanguageSwitch.LanguageChanged -= OnLanguageChanged;
            };

            // 每次进页面重新探测一遍：可能刚在别的页面装完东西，也可能是后台装完回来的
            Loaded += OnPageLoaded;

            // 徽章的悬浮提示兼做「配置文件在哪」的入口：路径必须是展开后的真实路径
            ToolTipService.SetToolTip(SavePill, L.F("Setp.SaveTip", Environment.NewLine, SettingsStore.FilePath));

            // XAML 里的「已保存」只是设计时占位：首帧就得是当前语言
            SaveStateText.Text = L.Get("Settings.Save.Saved");
            RefreshPickerLabels();
        }

        /// <summary>
        /// <summary>
        /// 自定控件的文案走 <c>TitleKey</c> / <c>DescriptionKey</c>，控件自己在换语言时重取；
        /// 这里只剩分段器——它的选项是代码 new 出来的 TextBlock，Loc 摸不到，得让它重建一遍。
        /// </summary>
        private void RefreshPickerLabels() => LanguagePicker.RefreshText();

        /// <summary>语言已切换：本页由代码赋值的那几处文案跟着换。</summary>
        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            // 级别卡片、各分组说明都按当前级别取文案，重跑一次即可
            ApplyLevel();
            RefreshPickerLabels();
            SaveStateText.Text = L.Get("Settings.Save.Saved");

            // 字体下拉的项名（「软件默认」「xx（软件默认）」）也是文案，得整列表重建；
            // 重建会触发 SelectionChanged，用 _sync 挡住，别把用户的选择当成新输入
            bool wasSyncing = _sync;
            _sync = true;
            try
            {
                BuildFontList();
                SelectConfiguredFont();
            }
            finally
            {
                _sync = wasSyncing;
            }

            ApplyFontPreviews();
            UpdateFontHint();

            // 下面几行的说明不是固定文案，而是「当前状态 + 文案」拼出来的（哪个环境、
            // 缺哪几项、走的哪个代理）。SettingRow 换语言时只会重取 TitleKey，
            // 说明必须由页面按状态重写一遍，否则会停在上一种语言。
            ApplyEnvironment();
            ApplyRuntimeSummary();
            UpdateChannelUi();
            UpdateProxyUi();
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
                SimpleLevelRadio.IsChecked = LauncherSettings.Level == SettingsLevel.Simple;
                AdvancedLevelRadio.IsChecked = LauncherSettings.Level == SettingsLevel.Advanced;

                LanguagePicker.SelectedIndex = (int)LauncherSettings.Language;
                ThemePicker.SelectedIndex = (int)LauncherSettings.Theme;
                ScaleSlider.Value = LauncherSettings.UiScale * 100;
                ScaleValueText.Text = $"{ScaleSlider.Value:0}%";
                FontCombo.SelectedItem = null;
                SelectConfiguredFont();
                RememberWindowSwitch.IsOn = LauncherSettings.RememberWindow;

                AutoLaunchSwitch.IsOn = LauncherSettings.AutoLaunch;
                CpuPicker.SelectedIndex = (int)LauncherSettings.CpuCores;
                StartModePicker.SelectedIndex = (int)LauncherSettings.StartMode;
                RememberTavernSwitch.IsOn = LauncherSettings.RememberTavernWindow;

                // 先给上限再给值：反过来的话，旧上限（默认 4096）会先把值夹一次
                MemorySlider.Maximum = LauncherSettings.MaxWebViewMemoryMb;
                MemorySlider.Value = LauncherSettings.WebViewMemoryMb;
                MemoryValueText.Text = FormatMemory(LauncherSettings.WebViewMemoryMb);
                ShowPath(ExportPathText, Expand(LauncherSettings.ExportPath));

                // 服务范围的真值在 config.yaml（listen），不是 settings.toml
                ScopePicker.SelectedIndex = (int)TavernSettings.Scope;
                BackgroundSwitch.IsOn = LauncherSettings.AllowBackground;

                DataLocationPicker.SelectedIndex = (int)LauncherSettings.DataLocation;
                // 显示的是酒馆真正在用的那层目录（配置所在目录），不是另存的一份偏好值
                ShowPath(GlobalDataPathText, GlobalTavernConfig.GlobalDataRoot);

                StartupCommandSwitch.IsOn = LauncherSettings.ShowStartupCommand;

                KeepLatestLogsSwitch.IsOn = LauncherSettings.KeepLatestLogsOnly;

                // 先给上下限再给值：顺序反过来会被旧上下限夹一次
                LogSizeBox.Minimum = LauncherSettings.MinLogMaxSizeMb;
                LogSizeBox.Maximum = LauncherSettings.MaxLogMaxSizeMb;
                LogSizeBox.Value = LauncherSettings.LogMaxSizeMb;

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
            SyncExternalChoices();
            ApplyLevel();
            ApplyConditions();
            ApplyEnvironment();
            UpdateChannelUi();
            UpdateProxyUi();
            UpdateGithubTestUi();
            UpdateFontHint();
        }

        /// <summary>
        /// 把「别处改过的离散选项」拉回当前设置。
        ///
        /// 仪表盘上换启动模式、配置页改服务范围，都会经 <see cref="LauncherSettings.Changed"/> 回到这里。
        /// 以前这里只重画条件行、不回写控件，于是设置页的下拉还停在旧值——
        /// 仪表盘写着「服务器模式」，设置里却是「普通模式」。
        ///
        /// 只同步离散选择（分段选择器、开关）：滑杆与输入框不碰，
        /// 那会把正在拖动的位置、正在输入的文本打断。
        /// </summary>
        private void SyncExternalChoices()
        {
            if (StartModePicker is null)
            {
                return;
            }

            bool wasSyncing = _sync;
            _sync = true;
            try
            {
                Pick(StartModePicker, (int)LauncherSettings.StartMode);
                Pick(CpuPicker, (int)LauncherSettings.CpuCores);
                Pick(ScopePicker, (int)TavernSettings.Scope);
                Pick(DataLocationPicker, (int)LauncherSettings.DataLocation);
                Pick(ProxyPicker, (int)LauncherSettings.Proxy);

                Toggle(RememberTavernSwitch, LauncherSettings.RememberTavernWindow);
                Toggle(StartupCommandSwitch, LauncherSettings.ShowStartupCommand);
                Toggle(KeepLatestLogsSwitch, LauncherSettings.KeepLatestLogsOnly);
            }
            finally
            {
                _sync = wasSyncing;
            }
        }

        /// <summary>只在值真的不同时才赋值：否则每次刷新都会重放一次 SelectionChanged。</summary>
        private static void Pick(SegmentedPicker picker, int index)
        {
            if (picker is not null && picker.SelectedIndex != index)
            {
                picker.SelectedIndex = index;
            }
        }

        private static void Toggle(ToggleSwitch toggle, bool value)
        {
            if (toggle is not null && toggle.IsOn != value)
            {
                toggle.IsOn = value;
            }
        }

        private void OnSettingsChanged(object? sender, EventArgs e)
        {
            if (!_sync)
            {
                RefreshDerived();
            }
        }

        // ==================== 版本切换 ====================

        private void LevelRadio_Checked(object sender, RoutedEventArgs e)
        {
            // XAML 解析早期控件还没就位；LoadStateToUi 回填状态期间也不该把「界面动作」写成配置
            if (SimpleLevelRadio is null || AdvancedLevelRadio is null || _sync)
            {
                return;
            }

            var level = SimpleLevelRadio.IsChecked == true ? SettingsLevel.Simple : SettingsLevel.Advanced;
            if (LauncherSettings.Level == level)
            {
                return;
            }

            LauncherSettings.SetLevel(level);
            MarkDirty();
        }

        /// <summary>
        /// 按当前版本决定哪几行/哪几组可见。
        ///
        /// 只改可见性，不改任何设置值：小白版里藏起来的分组仍按原设定生效，
        /// 切回高手版能原样看到，不会因为「看不见」就被重置。
        /// 条件行（服务器模式、自定义代理）另由 <see cref="ApplyConditions"/> 夹一层，
        /// 免得旧配置里的开关在小白版下把子行顶出来。
        /// </summary>
        private void ApplyLevel()
        {
            var simple = LauncherSettings.IsSimpleLevel;

            // 分段器与状态保持一致：外部（恢复默认、配置文件）改了状态时靠这里拉回
            if (SimpleLevelRadio.IsChecked != simple)
            {
                SimpleLevelRadio.IsChecked = simple;
            }

            if (AdvancedLevelRadio.IsChecked == simple)
            {
                AdvancedLevelRadio.IsChecked = !simple;
            }

            // 级别卡片与分组说明都随级别变，文案全在资源里；换语言时 ApplyLevel 会再跑一遍
            LevelTitle.Text = L.Get(simple
                ? "Settings.Level.Simple.Title"
                : "Settings.Level.Advanced.Title");

            LevelHint.Text = L.Get(simple
                ? "Settings.Level.Simple.Hint"
                : "Settings.Level.Advanced.Hint");

            // 组数写死在这里：小白版藏掉「控制台」与「日志」，其余 5 组全在；高手版 6 组全显示
            LevelCount.Text = L.F("Settings.Level.Count", simple ? 5 : 6);

            UiSectionHint.Text = L.Get(simple
                ? "Settings.Group.Appearance.Hint.Simple"
                : "Settings.Group.Appearance.Hint.Advanced");

            BasicSectionHint.Text = L.Get(simple
                ? "Settings.Group.Basics.Hint.Simple"
                : "Settings.Group.Basics.Hint.Advanced");

            RuntimeSectionHint.Text = L.Get(simple
                ? "Settings.Group.Runtime.Hint.Simple"
                : "Settings.Group.Runtime.Hint.Advanced");

            NetworkSectionHint.Text = L.Get(simple
                ? "Settings.Group.Network.Hint.Simple"
                : "Settings.Group.Network.Hint.Advanced");

            // 界面：留语言、主题、记住窗口位置
            SetShown(DividerBeforeScale, !simple);
            SetShown(ScaleRow, !simple);
            SetShown(DividerBeforeFont, !simple);
            SetShown(FontRow, !simple);

            // 基础：留开机自启、启动方式、关窗停服与资源目录
            SetShown(CpuRow, !simple);
            SetShown(DividerAfterCpu, !simple);
            SetShown(DividerBeforeDataLocation, !simple);
            SetShown(DataLocationRow, !simple);
            SetShown(DividerBeforeGlobalData, !simple);
            SetShown(GlobalDataRow, !simple);

            // 整组收起：控制台与日志都是排错用的，小白版不需要
            SetShown(ConsoleSection, !simple);
            SetShown(LogSection, !simple);

            // 运行环境：小白版一行结论，高手版保留明细
            SetShown(RuntimeSummaryRow, simple);
            SetShown(AdvancedRuntimeRows, !simple);

            // 网络：代理整组收起，GitHub 连通性保留（它上面那条分隔线要跟着一起走）
            SetShown(ProxyGroup, !simple);
            SetShown(DividerBeforeGithub, !simple);

            ApplyRuntimeSummary();
        }

        private static void SetShown(UIElement element, bool shown) =>
            element.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>按当前状态决定哪些条件行可见、哪些控件因依赖缺失而禁用。</summary>
        private void ApplyConditions()
        {
            var simple = LauncherSettings.IsSimpleLevel;

            DesktopRows.Visibility = LauncherSettings.StartMode == QuickStartMode.Desktop
                ? Visibility.Visible
                : Visibility.Collapsed;

            // 「服务器模式」就是启动方式的一种，没有第二个开关：
            // 启动方式选到它，服务范围、后台常驻这些子行才展开。
            // 小白版也要给——选了服务器模式却拿不到「服务范围」，等于把人锁在局域网里没法改。
            bool server = LauncherSettings.StartMode == QuickStartMode.Server;

            ServerRows.Visibility = server
                ? Visibility.Visible
                : Visibility.Collapsed;

            // 反向代理只在「服务器模式 + 互联网」下才出现：
            // 局域网走的是直连，套一层反代没有意义，摆出来只会让人以为必须配
            InternetRows.Visibility = server && TavernSettings.Scope == ServiceScope.Wan
                ? Visibility.Visible
                : Visibility.Collapsed;

            CustomProxyRows.Visibility = !simple && LauncherSettings.Proxy == ProxyModeSetting.Custom
                ? Visibility.Visible
                : Visibility.Collapsed;

            // 只有「跟随系统」才需要把 Windows 那边的真实状态摆出来
            SystemProxyRows.Visibility = !simple && LauncherSettings.Proxy == ProxyModeSetting.System
                ? Visibility.Visible
                : Visibility.Collapsed;

            // 判据就是 Pm2Service.IsAvailable（纯文件检查，不起进程，可以每次刷新都问）。
            // 界面必须和运行时问同一个问题，否则又会回到「界面能开、运行时却不用」的老 bug。
            bool pm2Available = Pm2Service.IsAvailable;

            BackgroundSwitch.IsEnabled = pm2Available;
            BackgroundSwitch.Opacity = pm2Available ? 1 : 0.45;
            BackgroundBlockedPill.Visibility = pm2Available ? Visibility.Collapsed : Visibility.Visible;

            // 显示值 = 意图 && 可用：PM2 缺了就显示为关（需求如此），但**存储里的意图一个字都不改**，
            // 装回 PM2 后开关自动回到用户上次的选择
            bool wasSyncing = _sync;
            _sync = true;
            try
            {
                BackgroundSwitch.IsOn = pm2Available && LauncherSettings.AllowBackground;
            }
            finally
            {
                _sync = wasSyncing;
            }

            // 禁用控件自身的 ToolTip 在 WinUI 里弹不出来（不参与命中测试），必须挂到整行上
            ToolTipService.SetToolTip(
                KeepRunningRow,
                pm2Available ? null : L.Get("Settings.KeepRunning.Pm2Tip"));
        }

        /// <summary>当前环境来源的检测结果；null 表示这一套还没探测完。</summary>
        private RuntimeEnvironment? CurrentEnvironment =>
            _envs.TryGetValue(LauncherSettings.Runtime, out var env) ? env : null;

        /// <summary>
        /// 重新探测两套运行环境（内置 / 系统）并刷新界面。
        ///
        /// 探测走 <see cref="RuntimeProbe"/>，只读、带超时；结果没回来之前行上显示「检测中…」，
        /// 不会拿上一轮的旧版本或「未安装」糊弄用户。
        /// </summary>
        private async Task RefreshEnvironmentAsync(bool force)
        {
            if (_probingEnvironment)
            {
                return;
            }

            _probingEnvironment = true;
            try
            {
                RuntimeEnvironment[] results = await Task.WhenAll(
                    RuntimeProbe.ProbeAsync(RuntimeSource.Builtin, force),
                    RuntimeProbe.ProbeAsync(RuntimeSource.System, force));

                _envs[RuntimeSource.Builtin] = results[0];
                _envs[RuntimeSource.System] = results[1];
            }
            catch
            {
                // 探测整体失手（极少数情况）：按「未安装」呈现，绝不谎报已安装
                _envs.TryAdd(RuntimeSource.Builtin, RuntimeEnvironment.Empty);
                _envs.TryAdd(RuntimeSource.System, RuntimeEnvironment.Empty);
            }
            finally
            {
                _probingEnvironment = false;
            }

            ApplyEnvironment();
            ApplyConditions();
        }

        private async void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            await RefreshEnvironmentAsync(force: true);
            ApplyPendingAnchor();

            // 全局 git 配置才是真相源：用户可能在终端里用 --unset 改过，界面得跟上
            // 全局 git 配置跟着开关走：用户可能在终端里 --unset 过、或换过机器，这里补一次
            await SyncGlobalGitAsync(announce: false);
        }

        /// <summary>
        /// 从别的页面跳过来时要落在哪一段。设置页是滚动长页，只说「去设置」等于把用户
        /// 丢在页首自己找；导航之前先把锚点记下来，页面到位后再滚过去。
        /// </summary>
        private static string? _pendingAnchor;

        /// <summary>导航到设置页的「运行环境」分组——版本页发现缺 Git / Node.js 时用。</summary>
        public static void OpenRuntimeSection() => _pendingAnchor = "runtime";

        private void ApplyPendingAnchor()
        {
            if (_pendingAnchor is null || RuntimeSection is null)
            {
                return;
            }

            bool runtime = _pendingAnchor == "runtime";
            _pendingAnchor = null;

            if (runtime)
            {
                RuntimeSection.StartBringIntoView(new BringIntoViewOptions
                {
                    AnimationDesired = true,
                    VerticalAlignmentRatio = 0.1,
                });
            }
        }

        private async void RecheckEnvironment_Click(object sender, RoutedEventArgs e)
        {
            RecheckEnvironmentButton.IsEnabled = false;
            try
            {
                await RefreshEnvironmentAsync(force: true);
            }
            finally
            {
                RecheckEnvironmentButton.IsEnabled = true;
            }
        }

        /// <summary>把当前环境来源的版本写进依赖行：有版本显示版本，没有就给安装入口。</summary>
        private void ApplyEnvironment()
        {
            var env = CurrentEnvironment;
            var probed = env is not null;
            var isSystem = LauncherSettings.Runtime == RuntimeSource.System;

            ApplyDependency(GitVersionText, GitInstallButton, null, env?.Git, probed, !isSystem);
            ApplyDependency(
                NodeVersionText,
                NodeInstallButton,
                NodeOutdatedPill,
                env?.Node,
                probed,
                !isSystem,
                RuntimeProbe.IsNodeOutdated(env?.Node));
            ApplyDependency(
                CaddyVersionText,
                CaddyInstallButton,
                null,
                env?.Caddy,
                probed,
                !isSystem,
                alwaysInstall: true);

            // 已装也能重装：换版本 / 换架构（比如换了机器）都靠这一步
            CaddyInstallLabel.Text = L.Get(env?.Caddy is null ? "Common.Install" : "Common.Reinstall");

            // 系统环境里不提供安装入口；内置环境里 PM2 还要等 Node.js 先就位
            var pm2Blocked = probed && env!.Node is null;
            ApplyDependency(
                Pm2VersionText,
                Pm2InstallButton,
                null,
                env?.Pm2,
                probed,
                !isSystem && !pm2Blocked,
                alwaysInstall: true);

            Pm2InstallLabel.Text = L.Get(env?.Pm2 is null ? "Common.Install" : "Common.Reinstall");
            Pm2NoteText.Visibility = pm2Blocked && !isSystem ? Visibility.Visible : Visibility.Collapsed;

            ApplyDependency(WebView2VersionText, WebView2InstallButton, null, env?.WebView2, probed, !isSystem);

            GitRow.Description = L.Get(isSystem
                ? "Settings.Row.Git.Description.System"
                : "Settings.Row.Git.Description.Bundled");

            NodeRow.Description = L.Get(isSystem
                ? "Settings.Row.Node.Description.System"
                : "Settings.Row.Node.Description.Bundled");

            CaddyRow.Description = L.Get(LauncherSettings.ServerMode
                ? "Settings.Row.Caddy.Description.Server"
                : "Settings.Row.Caddy.Description.Optional");
            // npm 挂在 Node.js 下面：Node 没装（含还在探测）时这两行都没有意义，
            // 连同分隔线一起收起，免得留个「给不存在的运行时选包源」的空档
            var nodeReady = env?.Node is not null;
            SetShown(DividerBeforeNpm, nodeReady);
            SetShown(NpmRow, nodeReady);
            SetShown(DividerBeforeNpmRegistry, nodeReady);
            SetShown(NpmRegistryRow, nodeReady);
            NpmRegistryRow.Description = L.F(
                "Settings.Row.NpmRegistry.Description",
                LauncherSettings.NpmRegistryName);

            // npm 版本取自 <nodejs>\node_modules\npm\package.json：Node 在却读不到，
            // 说明这份 Node 不完整（或 npm 装在别处），直接说「未检测到」，别空着让人等
            NpmVersionText.Text = env?.Npm ?? L.Get("Setp.NotDetected");
            NpmVersionText.Foreground = env?.Npm is null ? Tertiary() : Primary();

            ApplyRuntimeSummary();
        }

        /// <summary>小白版的运行环境结论：三项必要组件齐了就「正常」，缺任意一项就「环境缺失」。</summary>
        private void ApplyRuntimeSummary()
        {
            if (!LauncherSettings.IsSimpleLevel)
            {
                return;
            }

            var env = CurrentEnvironment;
            if (env is null)
            {
                // 还没探测完：既不说正常也不说缺失，避免先闪一下假的结论
                RuntimeStateText.Text = L.Get("Settings.Runtime.State.Pending");
                RuntimeStateText.Foreground = Tertiary();
                RuntimeStatePill.Background = Tint(0x8A, 0x8A, 0x8A);
                RepairRuntimeButton.Visibility = Visibility.Collapsed;
                RuntimeSummaryRow.Description = L.Get("Settings.Runtime.Summary.Pending");
                return;
            }

            List<string> missing = MissingComponents(env);
            if (missing.Count == 0)
            {
                RuntimeStateText.Text = L.Get("Settings.Runtime.State.Ready");
                RuntimeStateText.Foreground = Success();
                RuntimeStatePill.Background = Tint(0x16, 0xA3, 0x4A);
                RepairRuntimeButton.Visibility = Visibility.Collapsed;
                RuntimeSummaryRow.Description = L.Get("Settings.Runtime.Summary.Ready");
                return;
            }

            RuntimeStateText.Text = L.Get("Settings.Runtime.State.Missing");
            RuntimeStateText.Foreground = Caution();
            RuntimeStatePill.Background = Tint(0x9D, 0x5D, 0x00);

            // 「修复」只往内置目录里装：系统环境的 Git / Node.js 动的是用户的 PATH，
            // 启动器不该插手。系统环境缺组件时给出去哪装的说明，不给一个点了没反应的按钮。
            var builtin = LauncherSettings.Runtime == RuntimeSource.Builtin;
            RepairRuntimeButton.Visibility = builtin ? Visibility.Visible : Visibility.Collapsed;
            // 组件名用中文顿号拼在句子里，英文下面用逗号：整句交给资源，避免语序错位
            string list = string.Join(L.IsEnglish ? ", " : "、", missing);
            RuntimeSummaryRow.Description = L.F(
                builtin
                    ? "Settings.Runtime.Summary.MissingBuiltin"
                    : "Settings.Runtime.Summary.MissingSystem",
                list);
        }

        /// <summary>小白版只认这三项：Git 拉源码、Node.js 跑酒馆、WebView2 撑桌面窗口。</summary>
        private static List<string> MissingComponents(RuntimeEnvironment env)
        {
            var missing = new List<string>();

            if (env.Git is null)
            {
                missing.Add("Git");
            }

            if (env.Node is null)
            {
                missing.Add("Node.js");
            }

            if (env.WebView2 is null)
            {
                missing.Add("WebView2");
            }

            return missing;
        }

        /// <summary>状态胶囊的底色：强调色压到 14% 透明度，深浅主题共用一套值。</summary>
        private static Brush Tint(byte r, byte g, byte b) =>
            new SolidColorBrush(Color.FromArgb(0x24, r, g, b));

        private void ApplyDependency(
            TextBlock versionText,
            Button installButton,
            Border? outdatedPill,
            string? version,
            bool probed,
            bool canInstall,
            bool outdated = false,
            bool alwaysInstall = false)
        {
            if (!probed)
            {
                // 还没探测完：不写「未安装」，否则会先闪一下假的缺失状态
                versionText.Text = L.Get("Setp.Checking");
                versionText.Foreground = Tertiary();
                installButton.Visibility = Visibility.Collapsed;
                if (outdatedPill is not null)
                {
                    outdatedPill.Visibility = Visibility.Collapsed;
                }

                return;
            }

            versionText.Text = version ?? L.Get("Setp.NotInstalled");
            versionText.Foreground = version is null ? Tertiary() : Primary();

            // alwaysInstall：装好了也给一个入口（PM2 要从「非全局装」升级成全局装，只能靠重装）
            installButton.Visibility = (version is null || alwaysInstall) && canInstall
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (outdatedPill is not null)
            {
                outdatedPill.Visibility = outdated && version is not null ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void UpdateChannelUi()
        {
            var channel = LauncherSettings.Channel;
            var accelerated = LauncherSettings.GithubAcceleration;

            ChannelRow.Description = LauncherSettings.ChannelHint(channel);
            ChannelUrlText.Text = LauncherSettings.SourceUrl;

            // 加速行常显：它是全应用的总开关，作用于所有 GitHub 链接，
            // 与当前选的是镜像还是直连无关；收起只会让人以为选镜像时加速就失效了
            AccelRow.Description = BuildAccelDescription(accelerated);

            // 外部改过状态（恢复默认、配置文件被换掉）时把开关拉回来；
            // 直接赋 IsOn 会触发 Toggled，用 _syncingAccel 挡住，免得又把状态写回去绕成环
            if (AccelSwitch.IsOn != LauncherSettings.GithubAcceleration)
            {
                _syncingAccel = true;
                AccelSwitch.IsOn = LauncherSettings.GithubAcceleration;
                _syncingAccel = false;
            }
        }

        /// <summary>
        /// 加速行的说明。一个开关管两条路，说明里两条都要提到：
        /// · 应用内请求 —— 由 <see cref="GithubAccel"/> 在地址前拼中转域名；
        /// · 命令行 git —— 靠 <c>~/.gitconfig</c> 里的 insteadOf 规则，酒馆自动更新、
        ///   酒馆里装扩展都用得上。
        /// 只讲前者的话，用户会以为「开了加速，命令行里还是直连」是 bug。
        /// </summary>
        private string BuildAccelDescription(bool accelerated)
        {
            if (!accelerated)
            {
                return L.Get("Settings.Row.GithubAccel.Description.Off");
            }

            // 没有 Git 就没有 insteadOf 可写，先说清这一点，否则「正在核对」会一直挂着
            if (RuntimeProbe.LocateGit() is not { Length: > 0 })
            {
                return L.Get("Settings.Row.GithubAccel.Description.OnNoGit");
            }

            return L.Get(_gitRewriteApplied switch
            {
                true => "Settings.Row.GithubAccel.Description.OnGitApplied",
                false => "Settings.Row.GithubAccel.Description.OnGitFailed",
                _ => "Settings.Row.GithubAccel.Description.OnChecking",
            });
        }

        // ==================== 全局 git 规则 ====================

        /// <summary>
        /// 让全局 git 配置跟上加速开关，再刷新说明。
        ///
        /// 进设置页时也跑一次：用户可能在终端里手动 <c>--unset</c> 过，或者换过机器——
        /// 开关说「开着」而配置里没有规则，命令行那条路就断了。
        /// </summary>
        /// <param name="announce">用户刚拨动开关时为 true（播报结果）；进页面静默对齐时为 false。</param>
        private async Task SyncGlobalGitAsync(bool announce)
        {
            bool enabled = LauncherSettings.GithubAcceleration;
            GitConfigResult? result = await TavernGitConfig.SyncAsync(enabled);

            _gitRewriteApplied = await TavernGitConfig.IsAppliedAsync();
            UpdateChannelUi();

            if (result is { Ok: false } failed)
            {
                // 开关不回滚：加速服务对应用内请求仍然有效，只是命令行那条路没铺好。
                // 进页面时的静默对齐不弹消息——没装 Git 的话每次进设置页都会失败一次，太吵。
                if (!announce)
                {
                    return;
                }

                // 关闭失败更严重——规则还在，git 会继续走加速，所以按错误报。
                if (enabled)
                {
                    AppNotify.Warning(
                        L.Get("Setp.GitCfgFail.Title"),
                        L.F("Setp.GitCfgFail.Body", failed.Message));
                }
                else
                {
                    AppNotify.Error(
                        L.Get("Setp.GitCfgRemoveFail.Title"),
                        L.F("Setp.GitCfgRemoveFail.Body", failed.Message));
                }

                return;
            }

            if (!announce)
            {
                return;
            }

            AppNotify.Success(
                L.Get(enabled ? "Setp.AccelOn.Title" : "Setp.AccelOff.Title"),
                L.Get(enabled ? "Setp.AccelOn.Body" : "Setp.AccelOff.Body"));
        }

        private void UpdateProxyUi()
        {
            ProxyRow.Description = L.Get(LauncherSettings.Proxy switch
            {
                ProxyModeSetting.None => "Settings.Proxy.None",
                ProxyModeSetting.Custom => "Settings.Proxy.Custom",
                _ => "Settings.Proxy.System",
            });

            // 只在值真的变了才回写：否则每敲一个字都会被重置，光标会跳回开头
            if (CustomProxyBox.Text != LauncherSettings.CustomProxy)
            {
                CustomProxyBox.Text = LauncherSettings.CustomProxy;
            }

            RefreshSystemProxyState();
        }

        /// <summary>
        /// 读出 Windows 当前的实际代理状态并画到「系统代理状态」那一行。
        ///
        /// 「跟随系统」这四个字本身不传递信息：系统那边很可能压根没开代理，
        /// 用户却以为流量已经走了代理。这里用胶囊标明开/关，并给出实际地址；
        /// PAC 与自动检测拿不到固定地址，也要如实说明，而不是显示一个空白。
        /// </summary>
        private void RefreshSystemProxyState()
        {
            if (SystemProxyPill is null)
            {
                return;
            }

            SystemProxyState state = SystemProxy.Read();

            SystemProxyRow.Description = state.Detail;
            SystemProxyPillText.Text = state.Label;

            Color accent = state.Enabled
                ? (IsLight ? Color.FromArgb(0xFF, 0x0F, 0x7B, 0x0F) : Color.FromArgb(0xFF, 0x6C, 0xCB, 0x5F))
                : (IsLight ? Color.FromArgb(0xFF, 0x74, 0x74, 0x74) : Color.FromArgb(0xFF, 0x96, 0x96, 0x96));

            var brush = new SolidColorBrush(accent);
            SystemProxyPillText.Foreground = brush;
            SystemProxyDot.Fill = brush;
            SystemProxyPill.Background = new SolidColorBrush(
                Color.FromArgb(state.Enabled ? (byte)0x24 : (byte)0x12, accent.R, accent.G, accent.B));
        }

        private void RefreshSystemProxy_Click(object sender, RoutedEventArgs e)
        {
            RefreshSystemProxyState();

            SystemProxyState state = SystemProxy.Read();
            AppNotify.Post(
                state.Enabled ? NotifySeverity.Success : NotifySeverity.Informational,
                L.Get(state.Enabled
                    ? "Settings.SystemProxy.Notify.On"
                    : "Settings.SystemProxy.Notify.Off"),
                state.Detail);
        }

        /// <summary>直接跳到 Windows 的代理设置页，省得用户自己去翻「Internet 选项」。</summary>
        private void OpenWindowsProxy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = "ms-settings:network-proxy", UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                AppNotify.Warning(L.Get("Setp.OpenWindowsSettingsFail"), ex.Message);
            }
        }

        private void UpdateGithubTestUi()
        {
            GithubTestText.Text = LauncherSettings.GithubBestLatency is { } latency
                ? L.F("Settings.GithubTest.Best", latency)
                : L.Get("Settings.GithubTest.Untested");
        }

        /// <summary>字体行说明。</summary>
        /// <remarks>
        /// 选中的字体在本机找不到时必须写出来：WinUI 会静默回退，不说清楚用户只会以为「设置没生效」。
        /// </remarks>
        private void UpdateFontHint()
        {
            var total = FontCatalog.Families.Count;
            var choice = FontCatalog.Resolve(LauncherSettings.FontFamily);

            if (choice.Missing is { Length: > 0 } missing)
            {
                FontRow.Description = L.F(
                    "Settings.Row.Font.Description.Missing",
                    missing,
                    FontCatalog.EmbeddedLabel,
                    total);
                return;
            }

            FontRow.Description = choice.Kind switch
            {
                FontChoiceKind.Embedded =>
                    L.F("Settings.Row.Font.Description.Embedded", FontCatalog.EmbeddedLabel, total),
                FontChoiceKind.SystemFamily =>
                    L.F("Settings.Row.Font.Description.System", choice.Family),
                _ =>
                    L.F("Settings.Row.Font.Description.Follow", total),
            };
        }

        /// <summary>改动后先显示「保存中…」，真正落盘后由 <see cref="OnSettingsSaved"/> 收回。</summary>
        private void MarkDirty()
        {
            SaveStateText.Text = L.Get("Settings.Save.Saving");
            SaveDot.Fill = Caution();
        }

        /// <summary>写盘成功。可能在定时器线程上回调，必须切回 UI 线程再动控件。</summary>
        private void OnSettingsSaved(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                SaveStateText.Text = L.Get("Settings.Save.Saved");
                SaveDot.Fill = Success();
            });
        }

        // ==================== 界面 ====================

        private void LanguagePicker_SelectionChanged(object sender, EventArgs e)
        {
            if (_sync)
            {
                return;
            }

            var choice = (AppLanguage)LanguagePicker.SelectedIndex;
            if (LauncherSettings.Language == choice)
            {
                return;
            }

            // 实时切换：改资源上下文 + 重刷文案，窗口与页面都原地不动
            LanguageSwitch.Apply(choice);

            // 语言本身也是一项设置，同样要经过「保存中…→ 已保存」这条反馈
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

            LauncherSettings.SetFontFamily((FontCombo.SelectedItem as ComboBoxItem)?.Tag as string);
            UpdateFontHint();
            MarkDirty();
        }

        // ==================== 字体列表 ====================

        /// <summary>
        /// 把「软件默认」+ 本机所有可用字体族填进下拉框。
        ///
        /// 排序：内置字体钉在最前（它是出厂默认，也是唯一保证可用的那项），
        /// 之后按 <see cref="FontCatalog.Families"/> 的顺序（中文优先、其余按名称）。
        /// 每项用自己那套字渲染，选之前就能看到观感。写法上直接塞 ComboBoxItem
        /// 而不是绑 ItemsSource：条目数量是几百，一次建完没有虚拟化开销问题，
        /// 但省掉一套 DataTemplate + 转换器。
        /// </summary>
        private void BuildFontList()
        {
            FontCombo.Items.Clear();

            // Tag 用 embedded 这个哨兵值而不是字体族名：本机要是正好也装了同款，
            // 按族名选会走系统那一份，两者不该混为一谈。
            FontCombo.Items.Add(CreateFontItem(
                FontCatalog.EmbeddedLabel,
                FontCatalog.EmbeddedId,
                FontCatalog.IsEmbeddedAvailable ? FontCatalog.CreateEmbeddedFont() : null));

            foreach (var family in FontCatalog.Families)
            {
                // 本机也装了同款时标出来，用户才知道这一项和「软件默认」是同一套字
                var display = FontCatalog.IsEmbeddedFamily(family)
                    ? L.F("Settings.Font.Item.WithEmbedded", family, FontCatalog.EmbeddedLabel)
                    : family;

                FontCombo.Items.Add(CreateFontItem(display, family, new FontFamily(family)));
            }

            // 配置里记着、但本机已删掉的字体：补一条占位项，否则下拉框会空着
            if (LauncherSettings.FontFamily is { Length: > 0 } configured
                && !FontCatalog.IsEmbeddedId(configured)
                && !FontCatalog.IsInstalled(configured))
            {
                FontCombo.Items.Add(CreateFontItem(
                    L.F("Settings.Font.Item.Missing", configured), configured, null));
            }
        }

        /// <param name="display">列表里显示的文字。</param>
        /// <param name="tag">选中后写进配置的值（字体族名，或 <c>embedded</c>）。</param>
        /// <param name="preview">用于渲染该项自身文字的字形；null 表示跟随界面字体。</param>
        private static ComboBoxItem CreateFontItem(string display, string? tag, FontFamily? preview)
        {
            var item = new ComboBoxItem
            {
                Content = display,
                Tag = tag,
            };

            if (preview is not null)
            {
                item.FontFamily = preview;
            }

            ToolTipService.SetToolTip(item, tag switch
            {
                null => L.Get("Settings.Font.ToolTip.Follow"),
                _ when FontCatalog.IsEmbeddedId(tag) =>
                    L.F("Settings.Font.ToolTip.Embedded", FontCatalog.EmbeddedFamily),
                _ => tag,
            });

            return item;
        }

        /// <summary>按当前状态选中对应条目；没有任何匹配时退回「软件默认」。</summary>
        private void SelectConfiguredFont()
        {
            var configured = LauncherSettings.FontFamily;

            foreach (var entry in FontCombo.Items)
            {
                if (entry is ComboBoxItem item
                    && string.Equals(item.Tag as string, configured, StringComparison.OrdinalIgnoreCase))
                {
                    FontCombo.SelectedItem = item;
                    return;
                }
            }

            FontCombo.SelectedIndex = 0;
        }

        /// <summary>让每个下拉项用它自己的字形渲染（字体预览）。</summary>
        private void ApplyFontPreviews()
        {
            foreach (var entry in FontCombo.Items)
            {
                if (entry is not ComboBoxItem item || item.Tag is not string tag)
                {
                    continue;
                }

                if (FontCatalog.IsEmbeddedId(tag))
                {
                    if (FontCatalog.IsEmbeddedAvailable)
                    {
                        item.FontFamily = FontCatalog.CreateEmbeddedFont();
                    }

                    continue;
                }

                // 「未安装」的占位项没有字形可预览，保持界面字体
                if (FontCatalog.IsInstalled(tag))
                {
                    item.FontFamily = new FontFamily(tag);
                }
            }
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

            // 真正的「开机自启」是注册表里的启动项，不是这个布尔值本身：
            // 只存配置的话，Windows 的「启动应用」列表里什么都看不到（用户以为没生效）。
            bool enabled = AutoLaunchSwitch.IsOn;
            bool ok = StartupRegistration.Apply(enabled);

            LauncherSettings.SetAutoLaunch(enabled);
            MarkDirty();

            if (!ok)
            {
                AppNotify.Warning(
                    L.Get("Settings.AutoStart.Failed.Title"),
                    L.Get("Settings.AutoStart.Failed.Hint"));
            }
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

        /// <summary>滑杆单位是 MB，显示成 GB；不是整 GB 时保留一位小数（如 2.5 GB）。</summary>
        private static string FormatMemory(int mb) => $"{mb / 1024.0:0.#} GB";

        /// <summary>
        /// WebView 内存上限。滑杆步进 512 MB，范围是 1 GB ~ 本机物理内存减去系统预留，
        /// 上限值在 <see cref="LauncherSettings.MaxWebViewMemoryMb"/> 里算，页面不重复推导。
        /// </summary>
        private void MemorySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (MemoryValueText is null)
            {
                return;
            }

            int megabytes = (int)Math.Round(MemorySlider.Value);

            // 文字总是跟着滑杆走：程序回填时也要显示当前值
            MemoryValueText.Text = FormatMemory(megabytes);

            if (_sync)
            {
                return;
            }

            LauncherSettings.SetWebViewMemoryMb(megabytes);
            MarkDirty();
        }

        /// <summary>
        /// 记住酒馆窗口的位置：只改开关，不动已经存下的坐标——
        /// 用户临时关掉它再打开，上次的位置还在，不用重新摆一遍。
        /// </summary>
        private void RememberTavernSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_sync)
            {
                return;
            }

            LauncherSettings.SetRememberTavernWindow(RememberTavernSwitch.IsOn);
            MarkDirty();
        }

        private void ScopePicker_SelectionChanged(object sender, EventArgs e)
        {
            if (_sync)
            {
                return;
            }

            // 与仪表盘同一个入口：写 config.yaml 的 listen，并同步 settings.toml
            TavernSettings.SetScope(
                ScopePicker.SelectedIndex == (int)ServiceScope.Lan ? ServiceScope.Lan : ServiceScope.Wan);

            ApplyConditions();
            MarkDirty();
        }

        private void BackgroundSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            // 禁用的那一刻显示值会被强制成「关」（PM2 不在），那不是用户的意图：
            // 这里直接丢弃，否则 PM2 一卸一装，用户当年的选择就被抹掉了
            if (_sync || !BackgroundSwitch.IsEnabled)
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

        // ==================== 日志 ====================

        private void KeepLatestLogsSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_sync)
            {
                return;
            }

            LauncherSettings.SetKeepLatestLogsOnly(KeepLatestLogsSwitch.IsOn);
            MarkDirty();
        }

        private void LogSizeBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            // 清空输入框时 Value 是 NaN：不能当成 0 写进去（会被夹成 1MB）
            if (LogSizeBox is null || _sync || double.IsNaN(args.NewValue))
            {
                return;
            }

            LauncherSettings.SetLogMaxSizeMb((int)Math.Round(args.NewValue));
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
            await InstallGitAsync();

        private async void InstallNode_Click(object sender, RoutedEventArgs e) =>
            await InstallNodeAsync();

        private async void InstallCaddy_Click(object sender, RoutedEventArgs e) =>
            await InstallCaddyAsync();

        private async void InstallPm2_Click(object sender, RoutedEventArgs e) =>
            await InstallAsync("PM2", "6.0.5", "Pm2");

        private async void InstallWebView2_Click(object sender, RoutedEventArgs e) =>
            await InstallAsync(L.Get("Setp.WebView2Name"), "Evergreen Standalone", "WebView2");

        /// <summary>
        /// 小白版的「修复」：把缺失的必要组件逐个补齐。
        /// 顺序上先 Node.js —— PM2、npm 包都挂在它下面；每装完一步都重新探测，
        /// 上一步被取消或失败时，别再拿旧快照继续往下装。
        /// </summary>
        private async void RepairRuntime_Click(object sender, RoutedEventArgs e)
        {
            // 内置环境：与引导里的「一次装齐」走同一套多任务弹窗，
            // 缺几样就在同一个窗口里排几行，而不是一个个弹、每装完一个再弹一次。
            if (LauncherSettings.Runtime == RuntimeSource.Builtin)
            {
                RepairRuntimeButton.IsEnabled = false;
                try
                {
                    RuntimeProfile profile = await RuntimeSetup.DescribeAsync(RuntimeSide.Builtin, force: true);
                    IReadOnlyList<RuntimeTask> tasks = RuntimeSetup.Plan(
                        RuntimeSide.Builtin,
                        RuntimePackage.Full,
                        profile);

                    if (tasks.Count == 0)
                    {
                        AppNotify.Success(
                            L.Get("Settings.Runtime.Repair.Ready.Title"),
                            L.Get("Settings.Runtime.Repair.Ready.Hint"));
                        return;
                    }

                    await InstallTaskDialog.ShowAsync(
                        XamlRoot,
                        tasks,
                        autoStart: true,
                        note: L.Get("Settings.Runtime.Repair.Note"));

                    await RefreshEnvironmentAsync(force: true);
                }
                finally
                {
                    RepairRuntimeButton.IsEnabled = true;
                }

                return;
            }

            // 系统侧：那些依赖是用户自己装的，我们只逐个给出安装引导
            if (CurrentEnvironment is null)
            {
                return;
            }

            RepairRuntimeButton.IsEnabled = false;
            try
            {
                if (CurrentEnvironment?.Node is null)
                {
                    await InstallNodeAsync();
                    await RefreshEnvironmentAsync(force: true);
                }

                if (CurrentEnvironment?.Git is null)
                {
                    await InstallGitAsync();
                    await RefreshEnvironmentAsync(force: true);
                }

                if (CurrentEnvironment?.WebView2 is null)
                {
                    await InstallAsync(L.Get("Setp.WebView2Name"), "Evergreen Standalone", "WebView2");
                }

                await RefreshEnvironmentAsync(force: true);
            }
            finally
            {
                RepairRuntimeButton.IsEnabled = true;
            }
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

        /// <summary>
        /// 加速服务总开关。一次拨动要同时改两处：
        /// · 应用内请求 —— 立刻按新值拼地址（版本列表、安装包、扩展克隆）；
        /// · 全局 git 配置 —— 写 / 删 insteadOf，让命令行里的 git 也跟着走。
        /// 后者要起子进程，拨完再异步补齐；期间先禁用开关，连点会攒出两条互相覆盖的命令。
        /// </summary>
        private async void AccelSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_sync || _syncingAccel)
            {
                return;
            }

            LauncherSettings.SetGithubAcceleration(AccelSwitch.IsOn);
            _gitRewriteApplied = null;   // 说明里先显示「正在核对」，别拿上一次的结果冒充
            UpdateChannelUi();
            MarkDirty();

            AccelSwitch.IsEnabled = false;
            try
            {
                await SyncGlobalGitAsync(announce: true);
            }
            finally
            {
                AccelSwitch.IsEnabled = true;
            }
        }

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

        private void RestoreDefaults_Click(object sender, RoutedEventArgs e) => _ = ShowRestoreDialogAsync();

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

        private void OpenGlobalDataPath_Click(object sender, RoutedEventArgs e) =>
            RevealInExplorer(GlobalTavernConfig.GlobalDataRoot);

        // ==================== 系统交互 ====================

        /// <summary>
        /// 路径控件宽度有限：显示完整真实路径，超宽由 TextTrimming 截断，完整值同时放进 ToolTip。
        /// 不要折叠成 %APPDATA% —— 用户没法把变量粘进资源管理器。
        /// </summary>
        private static void ShowPath(TextBlock target, string fullPath)
        {
            target.Text = fullPath;
            ToolTipService.SetToolTip(target, fullPath);
        }

        private static string Expand(string path) => AppPaths.Expand(path);

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

            // 没有底部按钮区时 ContentDialog 自己不吃 Esc，这里补一个快捷键。
            // 必须 Hidden：默认会在弹窗里飘一个「Esc」提示框，用户根本不知道那是什么
            // （而且它跟进度气泡挤在同一片区域，看起来像自己长出来的）。
            var escape = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
            escape.Invoked += (_, args) =>
            {
                args.Handled = true;
                dialog.Hide();
            };
            dialog.KeyboardAccelerators.Add(escape);
            dialog.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

            return (dialog, body, footer);
        }

        private static Button ShellButton(string text, bool accent = false, bool danger = false)
        {
            var button = new Button { Content = text, MinWidth = 96 };
            var key = danger ? "DangerDialogButtonStyle" : accent ? "AccentActionButtonStyle" : null;
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
        private Grid ProbeRow(string name, string hint, FrameworkElement status)
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
            MaxWidth = 260,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Secondary(),
        };

        // ==================== 安装任务弹窗 ====================

        private async Task InstallAsync(string name, string version, string field)
        {
            // 这三个已经接了真实安装链路（镜像 → 直连 / npm → 日志落盘），不再走下面的模拟弹窗
            if (field == "Git")
            {
                await InstallGitAsync();
                return;
            }

            if (field == "Caddy")
            {
                await InstallCaddyAsync();
                return;
            }

            if (field == "Pm2")
            {
                await InstallPm2Async();
                return;
            }

            var accent = Color.FromArgb(0xFF, 0x2B, 0x88, 0xD8);
            var (dialog, body, footer) = CreateShell(
                "\uE896",
                accent,
                L.F("Setp.Install.Title", name),
                L.F("Setp.Install.Subtitle", version),
                560);

            var phaseText = new TextBlock
            {
                Text = L.Get("Setp.PrepareDownload"),
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

            var close = ShellButton(L.Get("Setp.Cancel"));
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
                $"GET {GithubAccel.Apply("https://github.com/")}…/{name} · 12.4 MB",
                L.Get("Setp.Step.Verify"),
                L.F("Setp.Step.Extract", Path.Combine(AppPaths.RoamingRoot, "lib")),
                L.Get("Setp.Step.Register"),
            };

            // 装完三秒自动收起，和真实安装弹窗保持同一套节奏
            var autoClose = DispatcherQueue.CreateTimer();
            autoClose.Interval = TimeSpan.FromSeconds(3);
            autoClose.IsRepeating = false;
            autoClose.Tick += (_, _) =>
            {
                autoClose.Stop();
                dialog.Hide();
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
                phaseText.Text = L.Get("Setp.Done.Redetecting");
                logText.Text = string.Join(Environment.NewLine, lines);
                logScroll.ChangeView(null, logScroll.ScrollableHeight, null);

                // 装完不做任何「假装已安装」的写入：重新探测一次，行上显示什么由真实磁盘状态决定
                await RefreshEnvironmentAsync(force: true);

                string? detected = CurrentEnvironment?.VersionOf(field);
                phaseText.Text = detected is null ? L.Get("Setp.Done.NotDetected") : L.Get("Setp.Done.Ok");
                lines.Add(detected is null
                    ? L.F("Setp.Log.NotDetected", DateTime.Now.ToString("HH:mm:ss"), name)
                    : L.F("Setp.Log.Detected", DateTime.Now.ToString("HH:mm:ss"), name, detected));

                logText.Text = string.Join(Environment.NewLine, lines);
                logScroll.ChangeView(null, logScroll.ScrollableHeight, null);
                close.Content = L.Get("Setp.DoneButton");

                autoClose.Stop();
                autoClose.Start();
            };

            // 关掉之后再探一次，卡片上的版本号以磁盘为准
            dialog.Closed += async (_, _) =>
            {
                autoClose.Stop();
                await RefreshEnvironmentAsync(force: true);
            };

            await dialog.ShowAsync();
        }

        // ==================== 内置环境真实安装 ====================

        private Task InstallGitAsync() => InstallComponentAsync(MingitInstaller.Spec, "\uE896");

        /// <summary>Caddy：官方 Release 的 zip，按系统架构选 amd64 / arm64，加速优先、直连兜底。</summary>
        private Task InstallCaddyAsync() => InstallComponentAsync(CaddyInstaller.Spec, "\uE774");

        private Task InstallNodeAsync() => InstallComponentAsync(NodejsInstaller.Spec, "\uE943");

        /// <summary>
        /// 内置环境里「要跑一段真实安装」的组件：zip 类的 Git / Node.js、npm 类的 PM2 全走这一条。
        /// 弹窗只负责画进度、把结果翻成人话，怎么装由 <see cref="InstallJob.Run"/> 决定。
        /// </summary>
        private Task InstallComponentAsync(InstallSpec spec, string glyph) =>
            InstallDialogAsync(new InstallJob(
                spec.DisplayName,
                L.F("Setp.Install.MirrorHint", spec.VersionLabel),
                glyph,
                spec.ProbeKey,
                (progress, token) => ComponentInstaller.InstallAsync(spec, progress, token),
                detected => spec.SuccessDetail(detected)));

        /// <summary>
        /// PM2：npm 包，没有 zip 可解，走 `npm install -g --prefix &lt;共享目录&gt;`。
        /// 装完命令落在 <see cref="Pm2Installer.CommandPath"/>，内置与系统两个运行时共用这一份。
        /// </summary>
        private Task InstallPm2Async() =>
            InstallDialogAsync(new InstallJob(
                "PM2",
                L.F("Setp.Install.Pm2Hint", Pm2Installer.Version),
                "\uE950",
                "Pm2",
                (progress, token) => Pm2Installer.InstallAsync(progress, token),
                detected => L.F("Setp.Install.Pm2Done", detected, Pm2Installer.CommandPath),
                HasProgress: false));

        /// <summary>真实安装弹窗里要用到的全部信息。</summary>
        /// <param name="HasProgress">
        /// 安装过程会不会报真实百分比。npm 这类只吐日志的传 false：
        /// 界面上就是一个转着的环，不摆一条永远停在 0% 的进度条。
        /// </param>
        private sealed record InstallJob(
            string DisplayName,
            string VersionLabel,
            string Glyph,
            string ProbeKey,
            Func<IProgress<InstallProgress>, CancellationToken, Task<InstallResult>> Run,
            Func<string, string> SuccessDetail,
            bool HasProgress = true);

        /// <summary>
        /// 真实安装弹窗：跑 <see cref="InstallJob.Run"/>，把它报的进度画出来、把结果翻成一句能操作的话。
        ///
        /// 界面只做两件事：把 <see cref="InstallProgress"/> 画成进度，把 <see cref="InstallResult"/>
        /// 翻成一句能操作的话。换源细节、HTTP 状态、异常堆栈全部进日志文件——
        /// 弹窗里糊三百行日志，用户看不懂，反馈时也没法整段贴出来。
        /// </summary>
        private async Task InstallDialogAsync(InstallJob job)
        {
            var accent = Color.FromArgb(0xFF, 0x2B, 0x88, 0xD8);
            var (dialog, body, footer) = CreateShell(
                job.Glyph,
                accent,
                L.F("Setp.InstallHeading", job.DisplayName),
                job.VersionLabel,
                580);

            var phaseText = new TextBlock
            {
                Text = L.Get("Setp.Preparing"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };
            var percentText = new TextBlock
            {
                Text = string.Empty,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Secondary(),
            };

            // 没有真实百分比时用这个环代替「进度条 + 0%」：npm 只吐日志，
            // 一条永远停在 0% 的进度条比没有更让人以为卡住了
            var ring = new ProgressRing
            {
                Width = 20,
                Height = 20,
                IsActive = false,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = job.HasProgress ? Visibility.Collapsed : Visibility.Visible,
            };

            var progressHead = new Grid { ColumnSpacing = 12 };
            progressHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            progressHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            progressHead.Children.Add(phaseText);
            Grid.SetColumn(percentText, 1);
            progressHead.Children.Add(percentText);
            Grid.SetColumn(ring, 1);
            progressHead.Children.Add(ring);

            var bar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Height = 4,
                Value = 0,
                Visibility = job.HasProgress ? Visibility.Visible : Visibility.Collapsed,
            };

            var detailText = new TextBlock
            {
                Text = L.Get("Setp.CheckingFolder"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Tertiary(),
            };

            // 失败时的一句话结论 + 日志路径；成功时整块收起
            var hintText = new TextBlock
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Secondary(),
                Visibility = Visibility.Collapsed,
            };

            var logText = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Secondary(),
            };
            var logScroll = new ScrollViewer
            {
                MaxHeight = 112,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = logText,
            };

            body.Children.Add(progressHead);
            body.Children.Add(bar);
            body.Children.Add(detailText);
            body.Children.Add(hintText);
            body.Children.Add(ShellPanel(logScroll));

            var logButton = ShellButton(L.Get("Setp.ViewLog"));
            logButton.Visibility = Visibility.Collapsed;
            var retryButton = ShellButton(L.Get("Setp.Retry"), accent: true);
            retryButton.Visibility = Visibility.Collapsed;
            var close = ShellButton(L.Get("Setp.Cancel"));

            // 右对齐底栏里「查看日志」放最左：它不是主操作，不该挨着关闭
            footer.Children.Add(logButton);
            footer.Children.Add(retryButton);
            footer.Children.Add(close);

            var lines = new List<string>();
            var attempt = new CancellationTokenSource();
            var closed = false;
            string? lastMessage = null;
            string? logPath = null;

            // 装完停三秒自动收起：结论能看清，又不用再点一次「关闭」
            var autoClose = DispatcherQueue.CreateTimer();
            autoClose.Interval = TimeSpan.FromSeconds(3);
            autoClose.IsRepeating = false;
            autoClose.Tick += (_, _) =>
            {
                autoClose.Stop();
                if (!closed)
                {
                    dialog.Hide();
                }
            };

            static string Clip(string value, int max) =>
                value.Length <= max ? value : value[..max] + "…";

            void Append(string text)
            {
                if (lines.Count > 0 && lines[^1].Contains(text, StringComparison.Ordinal))
                {
                    return;
                }

                lines.Add($"[{DateTime.Now:HH:mm:ss}] {text}");
                if (lines.Count > 60)
                {
                    lines.RemoveAt(0);
                }

                logText.Text = string.Join(Environment.NewLine, lines);
                logScroll.ChangeView(null, logScroll.ScrollableHeight, null);
            }

            var progress = new Progress<InstallProgress>(p =>
            {
                if (closed)
                {
                    return;
                }

                phaseText.Foreground = Primary();

                if (job.HasProgress)
                {
                    phaseText.Text = p.Message;
                    bar.Value = p.Percent;
                    percentText.Text = $"{p.Percent:0}%";

                    switch (p.Stage)
                    {
                        case InstallStage.Downloading:
                            // 服务器没给 Content-Length 时不假装知道进度，改成滚动条
                            bar.IsIndeterminate = p.Total <= 0;
                            detailText.Text = DescribeDownload(p);
                            break;
                        case InstallStage.Extracting:
                            bar.IsIndeterminate = false;
                            detailText.Text = L.Get("Setp.ExtractToBundled");
                            break;
                        case InstallStage.Done:
                            bar.IsIndeterminate = false;
                            bar.Value = 100;
                            percentText.Text = "100%";
                            detailText.Text = L.F("Setp.Redetecting", job.DisplayName);
                            break;
                        default:
                            bar.IsIndeterminate = false;
                            break;
                    }
                }
                else
                {
                    // 无进度：标题固定不晃，包管理器的输出挪到下面一行小字
                    ring.IsActive = true;
                    detailText.Text = Clip(p.Message, 110);
                }

                if (!string.Equals(p.Message, lastMessage, StringComparison.Ordinal))
                {
                    lastMessage = p.Message;
                    Append(p.Message);
                }
            });

            async Task RunAsync()
            {
                attempt.Dispose();
                attempt = new CancellationTokenSource();
                autoClose.Stop();

                bar.IsIndeterminate = false;
                bar.Value = 0;
                percentText.Text = "0%";
                percentText.Visibility = job.HasProgress ? Visibility.Visible : Visibility.Collapsed;
                bar.Visibility = job.HasProgress ? Visibility.Visible : Visibility.Collapsed;
                ring.Visibility = job.HasProgress ? Visibility.Collapsed : Visibility.Visible;
                ring.IsActive = !job.HasProgress;

                // 有进度的从「准备中」起步；没有进度的直接写死一句话，别让它随日志抖
                phaseText.Text = job.HasProgress ? L.Get("Setp.Preparing") : L.F("Setp.Installing", job.DisplayName);
                phaseText.Foreground = Primary();
                detailText.Text = L.Get("Setp.CheckingFolder");
                hintText.Visibility = Visibility.Collapsed;
                logButton.Visibility = Visibility.Collapsed;
                retryButton.Visibility = Visibility.Collapsed;
                close.Content = L.Get("Setp.Cancel");
                lines.Clear();
                logText.Text = string.Empty;
                lastMessage = null;

                InstallResult result = await job.Run(progress, attempt.Token);
                logPath = result.LogPath;

                if (result.Error == InstallError.Cancelled)
                {
                    ring.IsActive = false;
                    ring.Visibility = Visibility.Collapsed;
                    return;
                }

                if (closed)
                {
                    // 弹窗已被关掉：只把探测结果刷新掉，别再碰已卸载的控件
                    await RefreshEnvironmentAsync(force: true);
                    return;
                }

                ring.IsActive = false;
                ring.Visibility = Visibility.Collapsed;

                if (result.Success)
                {
                    if (job.HasProgress)
                    {
                        bar.IsIndeterminate = false;
                        bar.Value = 100;
                        percentText.Text = "100%";
                    }

                    phaseText.Text = L.Get("Setp.Done.Ok");
                    detailText.Text = L.F("Setp.Redetecting", job.DisplayName);

                    await RefreshEnvironmentAsync(force: true);

                    string? detected = CurrentEnvironment?.VersionOf(job.ProbeKey);
                    detailText.Text = detected is null
                        ? L.Get("Setp.FinishedNoCommand")
                        : job.SuccessDetail(detected);
                    Append(detected is null
                        ? L.Get("Setp.FinishedNoCommandShort")
                        : L.F("Setp.DetectedAs", job.DisplayName, detected));

                    autoClose.Stop();
                    autoClose.Start();
                }
                else
                {
                    if (job.HasProgress)
                    {
                        bar.IsIndeterminate = false;
                        bar.Value = 0;
                        percentText.Text = string.Empty;
                    }

                    phaseText.Text = result.Message;   // 一句话，不带堆栈
                    phaseText.Foreground = Caution();
                    detailText.Text = string.Empty;

                    var hints = new List<string>();
                    if (!string.IsNullOrWhiteSpace(result.Hint))
                    {
                        hints.Add(result.Hint!);
                    }

                    if (!string.IsNullOrWhiteSpace(result.LogPath))
                    {
                        hints.Add(L.F("Setp.DetailsInLog", result.LogPath));
                    }

                    hintText.Text = string.Join(Environment.NewLine, hints);
                    hintText.Visibility = hints.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

                    logButton.Visibility = result.LogPath is null ? Visibility.Collapsed : Visibility.Visible;
                    retryButton.Visibility = Visibility.Visible;
                }

                close.Content = L.Get("Setp.Close");
            }

            close.Click += (_, _) => dialog.Hide();

            retryButton.Click += async (_, _) => await RunAsync();

            logButton.Click += (_, _) => RevealFile(logPath);

            // Hide() / Esc / 关窗统一走这里：安装线程收到取消信号后自己清理暂存目录
            dialog.Closing += (_, _) =>
            {
                autoClose.Stop();
                closed = true;
                try
                {
                    attempt.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // 重试时旧令牌已被释放，此时本来也没有在跑的任务
                }
            };

            dialog.Opened += async (_, _) => await RunAsync();

            // 关掉之后再探一次：弹窗里那份是安装当口的快照，卡片上的版本号必须以磁盘为准
            dialog.Closed += async (_, _) =>
            {
                autoClose.Stop();
                await RefreshEnvironmentAsync(force: true);
            };

            await dialog.ShowAsync();
        }

        /// <summary>下载阶段的副标题：大小 + 实时速度；不知道总长就只说已下载多少。</summary>
        private static string DescribeDownload(InstallProgress progress)
        {
            if (progress.Total > 0)
            {
                string head = $"{SizeText(progress.Received)} / {SizeText(progress.Total)}";
                return progress.BytesPerSecond > 0
                    ? $"{head} · {SizeText((long)progress.BytesPerSecond)}/s"
                    : head;
            }

            return progress.Received > 0
                ? L.F("Setp.Downloaded", SizeText(progress.Received))
                : L.Get("Setp.AwaitingServer");
        }

        private static string SizeText(long bytes) => bytes switch
        {
            >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.0} GB",
            >= 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.0} MB",
            >= 1024 => $"{bytes / 1024.0:0} KB",
            _ => $"{bytes} B",
        };

        /// <summary>用默认程序打开日志；.log 没有关联程序时退回到在资源管理器里定位。</summary>
        private static void RevealFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    return;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
            {
                // 没有默认打开方式：退到目录里让用户自己挑
            }

            RevealInExplorer(Path.GetDirectoryName(path));
        }

        // ==================== 连通性测速弹窗 ====================

        /// <summary>
        /// 连通性测试弹窗。行与状态全部来自 <see cref="GithubConnectivity"/> 的真实请求，
        /// 打开即测一次，之后由「重新测试」重跑。
        /// </summary>
        private async Task ShowGithubTestAsync()
        {
            var accent = Color.FromArgb(0xFF, 0x0E, 0x9A, 0xA7);
            var (dialog, body, footer) = CreateShell(
                "\uEC4A",
                accent,
                L.Get("Setp.Conn.Title"),
                L.Get("Setp.Conn.Subtitle"),
                640);

            // 出口由「代理方式 × 加速开关」四种组合决定，这里直说这一次测试实际走的哪条
            var exitText = new TextBlock
            {
                Text = DescribeExitLine(),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Tertiary(),
            };

            body.Children.Add(exitText);

            IReadOnlyList<GithubProbe> probes = GithubConnectivity.BuildProbes();
            var statuses = new List<TextBlock>();
            int cloneIndex = -1;

            for (int index = 0; index < probes.Count; index++)
            {
                GithubProbe probe = probes[index];

                if (probe.IsGit)
                {
                    cloneIndex = index;
                }

                TextBlock status = CreateStatus(L.Get("Setp.Conn.Waiting"));
                statuses.Add(status);
                body.Children.Add(ProbeRow(probe.Name, probe.Hint, status));
            }

            var summary = new TextBlock
            {
                Text = string.Empty,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Secondary(),
            };

            body.Children.Add(summary);

            var close = ShellButton(L.Get("Setp.Close"));
            var retest = ShellButton(L.Get("Setp.Conn.Retest"), accent: true);
            footer.Children.Add(close);
            footer.Children.Add(retest);

            // 每一轮测试都拿自己的取消源：出口一变就把旧的那一轮作废，
            // 否则它会继续按旧出口把四条探测（尤其是几十秒的克隆）跑完，
            // 用户看到的就是「点了开启加速，进度还在没加速的环境里往前爬」
            var runCts = new CancellationTokenSource();

            void Restart() => runCts.Cancel();

            async Task RunAsync()
            {
                // 同一时刻只允许一轮：旧的那轮还挂着的话先作废，两轮结果会互相盖
                runCts.Cancel();
                runCts = new CancellationTokenSource();
                await ProbeGithubAsync(probes, statuses, summary, retest, cloneIndex, runCts.Token);
            }

            close.Click += (_, _) => dialog.Hide();
            retest.Click += async (_, _) => await RunAsync();

            // 加速 / 代理被改动 → 立刻重来一轮，并把顶部那行「当前出口」一起刷新。
            // 弹窗是模态的，正常点不到设置里的开关；但设置可能被别处（仪表盘、引导、命令行同步）
            // 改掉，而且这一条也让「开着加速再点开始测试」永远是新的出口。
            (bool Accel, ProxyModeSetting Proxy) exit = (GithubAccel.Enabled, LauncherSettings.Proxy);

            void OnExitChanged(object? sender, EventArgs e)
            {
                // 设置可能被后台（命令行同步、引导）改掉：换到 UI 线程再动控件
                if (!DispatcherQueue.HasThreadAccess)
                {
                    DispatcherQueue.TryEnqueue(() => OnExitChanged(sender, e));
                    return;
                }

                (bool Accel, ProxyModeSetting Proxy) now = (GithubAccel.Enabled, LauncherSettings.Proxy);
                if (now == exit)
                {
                    return;
                }

                exit = now;
                exitText.Text = DescribeExitLine();
                Restart();
                _ = RunAsync();
            }

            LauncherSettings.Changed += OnExitChanged;
            dialog.Closed += (_, _) =>
            {
                LauncherSettings.Changed -= OnExitChanged;
                Restart();
            };

            dialog.Opened += async (_, _) => await RunAsync();
            await dialog.ShowAsync();
        }

        /// <summary>弹窗顶部那句说明：出口 + 四项怎么跑 + 克隆的时限。</summary>
        private static string DescribeExitLine() =>
            L.F(
                "Setp.Conn.Description",
                GithubConnectivity.DescribeExit(),
                GithubConnectivity.CloneBudgetSeconds);

        /// <summary>
        /// 并发探测所有节点：各自计时、各自回填那一行，最后汇总。
        /// 并发而不是逐个来，是因为最坏的超时是 8 秒一条——串行六条能让人等到关掉弹窗。
        /// 克隆那条是例外：它要真拉约 100 MB，跑得比 HTTP 三条久得多，过程中把进度写进它自己那一行。
        /// </summary>
        private async Task ProbeGithubAsync(
            IReadOnlyList<GithubProbe> probes,
            List<TextBlock> statuses,
            TextBlock summary,
            Button retest,
            int cloneIndex,
            CancellationToken token)
        {
            retest.IsEnabled = false;
            summary.Text = L.Get("Setp.Conn.Probing");
            summary.Foreground = Secondary();

            foreach (TextBlock status in statuses)
            {
                status.Text = L.Get("Setp.Conn.ProbingShort");
                status.Foreground = Secondary();
            }

            IProgress<GithubCloneProgress>? live = cloneIndex >= 0 && cloneIndex < statuses.Count
                ? new Progress<GithubCloneProgress>(update =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        ApplyCloneProgress(statuses[cloneIndex], update);
                    }
                })
                : null;

            GithubProbeResult[] results = await Task.WhenAll(
                probes.Select(async (probe, index) =>
                {
                    GithubProbeResult result = await GithubConnectivity.RunAsync(
                        probe,
                        token,
                        probe.IsGit ? live : null);

                    // 这一轮已经被作废（出口改了 / 点了重新测试）：结果只属于旧出口，别写进界面
                    if (!token.IsCancellationRequested)
                    {
                        ApplyProbeResult(statuses[index], result);
                    }

                    return result;
                }));

            if (token.IsCancellationRequested)
            {
                return;
            }

            SummarizeProbe(results, summary);
            retest.IsEnabled = true;
        }

        /// <summary>
        /// 克隆拉取中的实时进度。与引导里「从哪下载最快」同一套写法：
        /// 「下载 45% · 1.2 MiB/s」——阶段 + 百分比 + 网速，一行读完就知道传到哪、多快。
        /// </summary>
        private void ApplyCloneProgress(TextBlock status, GithubCloneProgress update)
        {
            status.MaxWidth = 260;
            status.TextTrimming = TextTrimming.CharacterEllipsis;

            string head = $"{update.Phase} {update.Percent}%";
            status.Text = update.Speed is { Length: > 0 } speed ? $"{head} · {speed}" : head;
            status.Foreground = Secondary();
        }

        private void ApplyProbeResult(TextBlock status, GithubProbeResult result)
        {
            // 失败原因可能很长（Git 的原始报错），截断但保留完整内容在提示里
            status.MaxWidth = 260;
            status.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTipService.SetToolTip(status, null);

            if (result.Error is { Length: > 0 } error)
            {
                status.Text = error;
                ToolTipService.SetToolTip(status, error);
                status.Foreground = Critical();
                return;
            }

            if (result.Note is { Length: > 0 } note)
            {
                // 「跳过」是没测，「中转 403」是测了但不理想，两者颜色要分开
                status.Text = note;
                ToolTipService.SetToolTip(status, note);
                status.Foreground = result.Probe.IsGit ? Tertiary() : Caution();
                return;
            }

            if (result.LatencyMs is int latency)
            {
                // 克隆拉的是完整仓库，动辄十几秒：写「15200 ms」没人算得出来，
                // 写「15.2 秒」一眼就懂——这也是这个数字唯一的用途
                if (result.Probe.IsGit)
                {
                    string via = result.Route is { Length: > 0 } route ? $" · {route}" : string.Empty;
                    status.Text = MirrorBenchmark.DescribeElapsed(latency) + via;
                    status.Foreground = Success();
                    return;
                }

                status.Text = $"{latency} ms";
                status.Foreground = latency <= 300 ? Success() : Caution();
                return;
            }

            status.Text = "—";
        }

        private void SummarizeProbe(IReadOnlyList<GithubProbeResult> results, TextBlock summary)
        {
            // 「最快」只从三条 HTTP 里挑：克隆的耗时是另一回事（它要建仓库、要握手），
            // 混在一起比会让「克隆 3 秒」压过「API 200 ms」，数字就失去意义了
            List<GithubProbeResult> measured = results
                .Where(item => item.Ok && !item.Probe.IsGit && item.LatencyMs is not null)
                .ToList();

            var notes = new List<string>();

            if (results.FirstOrDefault(item => item.Probe.Key == "api") is { Ok: false })
            {
                notes.Add(L.Get("Setp.Conn.ApiFail"));
            }

            if (results.FirstOrDefault(item => item.Probe.Key == "raw") is { Ok: false })
            {
                notes.Add(L.Get("Setp.Conn.RawFail"));
            }

            if (results.FirstOrDefault(item => item.Probe.IsGit) is { Ok: false, Error: not null })
            {
                notes.Add(L.Get("Setp.Conn.CloneFail"));
            }

            if (results.FirstOrDefault(item => item.Probe.Key == "web") is { Ok: false })
            {
                notes.Add(L.Get("Setp.Conn.WebsiteFail"));
            }

            if (measured.Count == 0)
            {
                summary.Text = L.Get("Setp.Conn.AllFailed")
                    + (GithubAccel.Enabled
                        ? L.Get("Setp.Conn.AdviceDirect")
                        : L.Get("Setp.Conn.AdviceSwitch"));
                summary.Foreground = Critical();
                return;
            }

            GithubProbeResult fastest = measured.OrderBy(item => item.LatencyMs).First();
            LauncherSettings.SetGithubTestResult(fastest.LatencyMs!.Value);
            UpdateGithubTestUi();

            // 耗时最长的克隆单独说一句：它通了才代表安装/更新真的能用
            GithubProbeResult? clone = results.FirstOrDefault(item => item.Probe.IsGit);

            string cloneNote = clone switch
            {
                { Ok: true, LatencyMs: { } ms } =>
                    L.F("Setp.Conn.CloneOk", MirrorBenchmark.DescribeElapsed(ms)),
                { Note: { Length: > 0 } note } => note + "。",
                _ => string.Empty,
            };

            summary.Text = L.F("Setp.Conn.Fastest", fastest.LatencyMs, fastest.Probe.Name)
                + cloneNote
                + (notes.Count == 0 ? string.Empty : string.Join(" ", notes));
            summary.Foreground = notes.Count == 0 ? Success() : Caution();
        }

        // ==================== 恢复默认 ====================

        private async Task ShowRestoreDialogAsync()
        {
            var accent = Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C);
            var (dialog, body, footer) = CreateShell(
                "\uE7A7",
                accent,
                L.Get("Setp.Reset.Title"),
                L.Get("Setp.Reset.Body"),
                480);

            body.Children.Add(new TextBlock
            {
                Text = L.Get("Setp.Reset.Note"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Secondary(),
            });

            var cancel = ShellButton(L.Get("Setp.Cancel"));
            var confirm = ShellButton(L.Get("Setp.Reset.Confirm"), danger: true);
            footer.Children.Add(cancel);
            footer.Children.Add(confirm);

            cancel.Click += (_, _) => dialog.Hide();
            confirm.Click += (_, _) =>
            {
                dialog.Hide();
                LauncherSettings.Reset();
                LoadStateToUi();
                MarkDirty();

                // 「恢复默认」是一次性决策，不等防抖：万一用户立刻关窗口也要落盘
                SettingsStore.Flush();
            };

            await dialog.ShowAsync();
        }
    }
}
