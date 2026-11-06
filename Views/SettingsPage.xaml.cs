using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AstraBrew_Launcher.Services;
using AstraBrew_Launcher.Views.Controls;
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

            Unloaded += (_, _) =>
            {
                LauncherSettings.Changed -= OnSettingsChanged;
                SettingsStore.Saved -= OnSettingsSaved;
            };

            // 每次进页面重新探测一遍：可能刚在别的页面装完东西，也可能是后台装完回来的
            Loaded += OnPageLoaded;

            // 徽章的悬浮提示兼做「配置文件在哪」的入口：路径必须是展开后的真实路径
            ToolTipService.SetToolTip(SavePill, $"设置文件{Environment.NewLine}{SettingsStore.FilePath}");

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
                StopOnCloseSwitch.IsOn = LauncherSettings.StopTavernOnWindowClose;
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

                Toggle(StopOnCloseSwitch, LauncherSettings.StopTavernOnWindowClose);
                Toggle(RememberTavernSwitch, LauncherSettings.RememberTavernWindow);
                Toggle(BackgroundSwitch, LauncherSettings.AllowBackground);
                Toggle(StartupCommandSwitch, LauncherSettings.ShowStartupCommand);
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

            LevelTitle.Text = simple ? "小白版 · 常用设置" : "高手版 · 全部选项";
            LevelHint.Text = simple
                ? "只保留日常会用到的分组：界面、基础、运行环境、下载、网络与更新；其余按推荐值运行。"
                : "完整展开 8 个分组，含控制台、代理、服务器模式与原型工具等排障与进阶选项。";
            LevelCount.Text = simple ? "6 组" : "8 组";

            UiSectionHint.Text = simple
                ? "语言与主题；这些选项只影响启动器本身。"
                : "语言、主题、缩放与字体；这些选项只影响启动器本身。";
            BasicSectionHint.Text = simple
                ? "启动时机与酒馆的默认启动方式。"
                : "启动时机、资源分配与酒馆的默认启动方式。";
            RuntimeSectionHint.Text = simple
                ? "只需确认 Git、Node.js 与 WebView2 三项就位；缺什么点「修复」补齐。"
                : "酒馆依赖的 Git、Node.js、PM2 等组件；下面显示的是本机实际检测到的版本，没装就是「未安装」。";
            NetworkSectionHint.Text = simple
                ? "检查与 GitHub 的连通情况；下载走哪条通道请到「下载」分组调整。"
                : "访问 GitHub 与 npm 时使用的代理；系统代理跟随 Windows 的 Internet 选项。";

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

            // 整组收起
            SetShown(ConsoleSection, !simple);
            SetShown(PrototypeSection, !simple);

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

            // 后台常驻依赖 PM2；PM2 又依赖 Node.js。缺一环就禁用开关并说明原因。
            // 探测结果还没回来时不动开关，避免「刚进页面开关先灰一下」的闪烁。
            var env = CurrentEnvironment;
            var pm2Ready = env is null || env.Pm2 is not null;
            BackgroundSwitch.IsEnabled = pm2Ready;
            BackgroundSwitch.Opacity = pm2Ready ? 1 : 0.45;
            BackgroundBlockedPill.Visibility = pm2Ready ? Visibility.Collapsed : Visibility.Visible;
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
            ApplyDependency(CaddyVersionText, CaddyInstallButton, null, env?.Caddy, probed, !isSystem);

            // 系统环境里不提供安装入口；内置环境里 PM2 还要等 Node.js 先就位
            var pm2Blocked = probed && env!.Node is null;
            ApplyDependency(Pm2VersionText, Pm2InstallButton, null, env?.Pm2, probed, !isSystem && !pm2Blocked);
            Pm2NoteText.Visibility = pm2Blocked && !isSystem ? Visibility.Visible : Visibility.Collapsed;

            ApplyDependency(WebView2VersionText, WebView2InstallButton, null, env?.WebView2, probed, !isSystem);

            GitRow.Description = isSystem
                ? "读取系统 PATH 里的 Git，启动器不负责安装与升级。"
                : "拉取与更新酒馆源码，随启动器一起搬走。";
            NodeRow.Description = isSystem
                ? "读取系统环境里的 Node.js，缺失时请自行到官网安装。"
                : "运行酒馆与全部扩展的 JavaScript 运行时。";
            CaddyRow.Description = LauncherSettings.ServerMode
                ? "服务器模式必需：负责反向代理与 HTTPS。"
                : "服务器模式下才需要：负责反向代理与 HTTPS。";
            // npm 挂在 Node.js 下面：Node 没装（含还在探测）时这两行都没有意义，
            // 连同分隔线一起收起，免得留个「给不存在的运行时选包源」的空档
            var nodeReady = env?.Node is not null;
            SetShown(DividerBeforeNpm, nodeReady);
            SetShown(NpmRow, nodeReady);
            SetShown(DividerBeforeNpmRegistry, nodeReady);
            SetShown(NpmRegistryRow, nodeReady);
            NpmRegistryRow.Description = $"安装扩展与 PM2 时的包源，当前 {LauncherSettings.NpmRegistryName}。";

            // npm 版本取自 <nodejs>\node_modules\npm\package.json：Node 在却读不到，
            // 说明这份 Node 不完整（或 npm 装在别处），直接说「未检测到」，别空着让人等
            NpmVersionText.Text = env?.Npm ?? "未检测到";
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
                RuntimeStateText.Text = "检测中…";
                RuntimeStateText.Foreground = Tertiary();
                RuntimeStatePill.Background = Tint(0x8A, 0x8A, 0x8A);
                RepairRuntimeButton.Visibility = Visibility.Collapsed;
                RuntimeSummaryRow.Description = "Git、Node.js 与 WebView2 三项齐全即可正常启动。";
                return;
            }

            List<string> missing = MissingComponents(env);
            if (missing.Count == 0)
            {
                RuntimeStateText.Text = "正常";
                RuntimeStateText.Foreground = Success();
                RuntimeStatePill.Background = Tint(0x16, 0xA3, 0x4A);
                RepairRuntimeButton.Visibility = Visibility.Collapsed;
                RuntimeSummaryRow.Description = "Git、Node.js 与 WebView2 三项齐全，酒馆可以正常启动。";
                return;
            }

            RuntimeStateText.Text = "环境缺失";
            RuntimeStateText.Foreground = Caution();
            RuntimeStatePill.Background = Tint(0x9D, 0x5D, 0x00);

            // 「修复」只往内置目录里装：系统环境的 Git / Node.js 动的是用户的 PATH，
            // 启动器不该插手。系统环境缺组件时给出去哪装的说明，不给一个点了没反应的按钮。
            var builtin = LauncherSettings.Runtime == RuntimeSource.Builtin;
            RepairRuntimeButton.Visibility = builtin ? Visibility.Visible : Visibility.Collapsed;
            RuntimeSummaryRow.Description = builtin
                ? $"缺少 {string.Join("、", missing)}；点「修复」自动下载并安装。"
                : $"系统环境缺少 {string.Join("、", missing)}，请自行安装；或把运行环境切到「内置」由启动器代装。";
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
            bool outdated = false)
        {
            if (!probed)
            {
                // 还没探测完：不写「未安装」，否则会先闪一下假的缺失状态
                versionText.Text = "检测中…";
                versionText.Foreground = Tertiary();
                installButton.Visibility = Visibility.Collapsed;
                if (outdatedPill is not null)
                {
                    outdatedPill.Visibility = Visibility.Collapsed;
                }

                return;
            }

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
                return "GitHub 请求一律直连；镜像与 npm 源本来就不走加速。"
                    + "全局 git 配置里的加速规则也已移除，终端与酒馆更新同样直连。";
            }

            // 没有 Git 就没有 insteadOf 可写，先说清这一点，否则「正在核对」会一直挂着
            if (RuntimeProbe.LocateGit() is not { Length: > 0 })
            {
                return $"应用内 GitHub 请求（版本列表、安装包、扩展克隆）经 {GithubAccel.Prefix} 中转；"
                    + "没找到 Git，命令行与酒馆更新里的 GitHub 拉取无法加速（装好 Git 后重开这个开关即可写入）。"
                    + "镜像与 npm 源一律直连。";
            }

            string globalGit = _gitRewriteApplied switch
            {
                true => "全局 git 配置已写入 insteadOf，终端、酒馆自动更新与扩展安装里的 github.com 拉取也走加速。",
                false => "全局 git 规则还没写进配置（写入失败），命令行里的 GitHub 拉取仍是直连。",
                _ => "正在核对全局 git 配置…",
            };

            return $"应用内 GitHub 请求（版本列表、安装包、扩展克隆）经 {GithubAccel.Prefix} 中转；"
                + "中转端有自己的 API 额度，版本列表不必挤直连那 60 次/小时；"
                + globalGit
                + "镜像与 npm 源一律直连。";
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
            GitConfigResult? result = await GitGlobalConfig.SyncAsync(enabled);

            _gitRewriteApplied = await GitGlobalConfig.IsAppliedAsync();
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
                        "全局 git 规则没写上",
                        failed.Message + " 应用内下载仍然走加速，只有命令行里的 git 拉取还是直连。");
                }
                else
                {
                    AppNotify.Error(
                        "全局 git 规则没能移除",
                        failed.Message + " 应用内已恢复直连，但终端里的 git 仍会被这条规则带去加速。");
                }

                return;
            }

            if (!announce)
            {
                return;
            }

            AppNotify.Success(
                enabled ? "GitHub 加速服务已开启" : "GitHub 加速服务已关闭",
                enabled
                    ? "应用内请求已走中转；全局 git 配置也写好了，终端与酒馆更新里的 GitHub 拉取同样加速。"
                    : "应用内请求恢复直连，全局 git 配置里的加速规则也已移除。");
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
                state.Enabled ? "系统代理已开启" : "系统代理未开启",
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
                AppNotify.Warning("打不开 Windows 设置", ex.Message);
            }
        }

        private void UpdateGithubTestUi()
        {
            GithubTestText.Text = LauncherSettings.GithubBestLatency is { } latency
                ? $"最佳 {latency} ms"
                : "未测试";
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
                FontRow.Description = $"「{missing}」不在本机，已回退到{FontCatalog.EmbeddedLabel}字体；本机可选 {total} 种。";
                return;
            }

            FontRow.Description = choice.Kind switch
            {
                FontChoiceKind.Embedded =>
                    $"{FontCatalog.EmbeddedLabel}：随包内置的 {FontCatalog.EmbeddedFamily}，不依赖本机安装；另有 {total} 种可选。",
                FontChoiceKind.SystemFamily =>
                    $"当前：{choice.Family}；缺字时自动回退，仅影响观感。",
                _ =>
                    $"跟随 Windows 系统字体；本机可选 {total} 种。",
            };
        }

        /// <summary>改动后先显示「保存中…」，真正落盘后由 <see cref="OnSettingsSaved"/> 收回。</summary>
        private void MarkDirty()
        {
            SaveStateText.Text = "保存中…";
            SaveDot.Fill = Caution();
        }

        /// <summary>写盘成功。可能在定时器线程上回调，必须切回 UI 线程再动控件。</summary>
        private void OnSettingsSaved(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                SaveStateText.Text = "已保存";
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
                    ? $"{family}（{FontCatalog.EmbeddedLabel}）"
                    : family;

                FontCombo.Items.Add(CreateFontItem(display, family, new FontFamily(family)));
            }

            // 配置里记着、但本机已删掉的字体：补一条占位项，否则下拉框会空着
            if (LauncherSettings.FontFamily is { Length: > 0 } configured
                && !FontCatalog.IsEmbeddedId(configured)
                && !FontCatalog.IsInstalled(configured))
            {
                FontCombo.Items.Add(CreateFontItem($"{configured}（未安装）", configured, null));
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
                null => "不覆盖界面字体",
                _ when FontCatalog.IsEmbeddedId(tag) =>
                    $"随包内置的 {FontCatalog.EmbeddedFamily}，本机装没装都不影响",
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
            await InstallGitAsync();

        private async void InstallNode_Click(object sender, RoutedEventArgs e) =>
            await InstallNodeAsync();

        private async void InstallCaddy_Click(object sender, RoutedEventArgs e) =>
            await InstallAsync("Caddy", "v2.10.0", "Caddy");

        private async void InstallPm2_Click(object sender, RoutedEventArgs e) =>
            await InstallAsync("PM2", "6.0.5", "Pm2");

        private async void InstallWebView2_Click(object sender, RoutedEventArgs e) =>
            await InstallAsync("WebView2 运行时", "Evergreen Standalone", "WebView2");

        /// <summary>
        /// 小白版的「修复」：把缺失的必要组件逐个补齐。
        /// 顺序上先 Node.js —— PM2、npm 包都挂在它下面；每装完一步都重新探测，
        /// 上一步被取消或失败时，别再拿旧快照继续往下装。
        /// </summary>
        private async void RepairRuntime_Click(object sender, RoutedEventArgs e)
        {
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
                    await InstallAsync("WebView2 运行时", "Evergreen Standalone", "WebView2");
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

        private async Task InstallAsync(string name, string version, string field)
        {
            // Git 已经接了真实安装链路（镜像 → 直连 → 日志落盘），不再走下面的模拟弹窗
            if (field == "Git")
            {
                await InstallGitAsync();
                return;
            }

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
                $"GET {GithubAccel.Apply("https://github.com/")}…/{name} · 12.4 MB",
                "校验 SHA-256 与发布清单一致",
                $"解压到 {Path.Combine(AppPaths.RoamingRoot, "lib")}",
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
                phaseText.Text = "安装完成，正在重新检测…";
                logText.Text = string.Join(Environment.NewLine, lines);
                logScroll.ChangeView(null, logScroll.ScrollableHeight, null);

                // 装完不做任何「假装已安装」的写入：重新探测一次，行上显示什么由真实磁盘状态决定
                await RefreshEnvironmentAsync(force: true);

                string? detected = CurrentEnvironment?.VersionOf(field);
                phaseText.Text = detected is null ? "安装完成，但未能检测到" : "安装完成";
                lines.Add(detected is null
                    ? $"[{DateTime.Now:HH:mm:ss}] 未检测到 {name}，请检查安装目录与上方下载日志"
                    : $"[{DateTime.Now:HH:mm:ss}] 检测到 {name} {detected}");

                logText.Text = string.Join(Environment.NewLine, lines);
                logScroll.ChangeView(null, logScroll.ScrollableHeight, null);
                close.Content = "完成";
            };

            await dialog.ShowAsync();
        }

        // ==================== 内置环境真实安装 ====================

        private Task InstallGitAsync() => InstallComponentAsync(MingitInstaller.Spec, "\uE896");

        private Task InstallNodeAsync() => InstallComponentAsync(NodejsInstaller.Spec, "\uE943");

        /// <summary>
        /// 内置环境的真实安装弹窗：下载（镜像优先、直连兜底）→ 解压 → 换成正式目录。
        /// Git / Node.js 共用这一套，组件之间的差异全部在 <see cref="InstallSpec"/> 里。
        ///
        /// 界面只做两件事：把 <see cref="InstallProgress"/> 画成进度，把 <see cref="InstallResult"/>
        /// 翻成一句能操作的话。换源细节、HTTP 状态、异常堆栈全部进日志文件——
        /// 弹窗里糊三百行日志，用户看不懂，反馈时也没法整段贴出来。
        /// </summary>
        private async Task InstallComponentAsync(InstallSpec spec, string glyph)
        {
            var accent = Color.FromArgb(0xFF, 0x2B, 0x88, 0xD8);
            var (dialog, body, footer) = CreateShell(
                glyph,
                accent,
                $"安装 {spec.DisplayName}",
                $"{spec.VersionLabel} · 镜像优先，失败回退直连",
                580);

            var phaseText = new TextBlock
            {
                Text = "准备中…",
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

            var progressHead = new Grid { ColumnSpacing = 12 };
            progressHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            progressHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            progressHead.Children.Add(phaseText);
            Grid.SetColumn(percentText, 1);
            progressHead.Children.Add(percentText);

            var bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 4, Value = 0 };

            var detailText = new TextBlock
            {
                Text = "正在检查安装目录…",
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

            var logButton = ShellButton("查看日志");
            logButton.Visibility = Visibility.Collapsed;
            var retryButton = ShellButton("重试", accent: true);
            retryButton.Visibility = Visibility.Collapsed;
            var close = ShellButton("取消");

            // 右对齐底栏里「查看日志」放最左：它不是主操作，不该挨着关闭
            footer.Children.Add(logButton);
            footer.Children.Add(retryButton);
            footer.Children.Add(close);

            var lines = new List<string>();
            var attempt = new CancellationTokenSource();
            var closed = false;
            string? lastMessage = null;
            string? logPath = null;

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

                phaseText.Text = p.Message;
                phaseText.Foreground = Primary();
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
                        detailText.Text = "解压到内置环境…";
                        break;
                    case InstallStage.Done:
                        bar.IsIndeterminate = false;
                        bar.Value = 100;
                        percentText.Text = "100%";
                        detailText.Text = $"正在重新检测 {spec.DisplayName}…";
                        break;
                    default:
                        bar.IsIndeterminate = false;
                        break;
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

                bar.IsIndeterminate = false;
                bar.Value = 0;
                percentText.Text = "0%";
                phaseText.Text = "准备中…";
                phaseText.Foreground = Primary();
                detailText.Text = "正在检查安装目录…";
                hintText.Visibility = Visibility.Collapsed;
                logButton.Visibility = Visibility.Collapsed;
                retryButton.Visibility = Visibility.Collapsed;
                close.Content = "取消";
                lines.Clear();
                logText.Text = string.Empty;
                lastMessage = null;

                InstallResult result = await ComponentInstaller.InstallAsync(spec, progress, attempt.Token);
                logPath = result.LogPath;

                if (result.Error == InstallError.Cancelled)
                {
                    return;
                }

                if (closed)
                {
                    // 弹窗已被关掉：只把探测结果刷新掉，别再碰已卸载的控件
                    await RefreshEnvironmentAsync(force: true);
                    return;
                }

                if (result.Success)
                {
                    bar.IsIndeterminate = false;
                    bar.Value = 100;
                    percentText.Text = "100%";
                    phaseText.Text = "安装完成";
                    detailText.Text = $"正在重新检测 {spec.DisplayName}…";

                    await RefreshEnvironmentAsync(force: true);

                    string? detected = CurrentEnvironment?.VersionOf(spec.ProbeKey);
                    detailText.Text = detected is null
                        ? $"已解压到 {spec.InstallRoot}，但没检测到可执行文件，请查看日志。"
                        : spec.SuccessDetail(detected);
                    Append(detected is null
                        ? "安装完成，但没检测到可执行文件"
                        : $"检测到 {spec.DisplayName} {detected}");
                }
                else
                {
                    bar.IsIndeterminate = false;
                    bar.Value = 0;
                    percentText.Text = string.Empty;
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
                        hints.Add($"详细信息见日志 {result.LogPath}");
                    }

                    hintText.Text = string.Join(Environment.NewLine, hints);
                    hintText.Visibility = hints.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

                    logButton.Visibility = result.LogPath is null ? Visibility.Collapsed : Visibility.Visible;
                    retryButton.Visibility = Visibility.Visible;
                }

                close.Content = "关闭";
            }

            close.Click += (_, _) => dialog.Hide();

            retryButton.Click += async (_, _) => await RunAsync();

            logButton.Click += (_, _) => RevealFile(logPath);

            // Hide() / Esc / 关窗统一走这里：安装线程收到取消信号后自己清理暂存目录
            dialog.Closing += (_, _) =>
            {
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
                ? $"已下载 {SizeText(progress.Received)}"
                : "正在等待服务器响应…";
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
                "GitHub 连通性测试",
                "真的去访问网页、API、压缩包与 Raw；延迟是这次请求实测出来的，不是预置数字。",
                640);

            body.Children.Add(new TextBlock
            {
                Text = GithubAccel.Enabled
                    ? $"当前出口：{GithubConnectivity.DescribeExit()}；GitHub 域名经 {GithubAccel.Prefix} 中转，失败会退回直连。"
                    : $"当前出口：{GithubConnectivity.DescribeExit()}；加速服务未开启。",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Tertiary(),
            });

            IReadOnlyList<GithubProbe> probes = GithubConnectivity.BuildProbes();
            var statuses = new List<TextBlock>();

            foreach (GithubProbe probe in probes)
            {
                var status = CreateStatus("等待");
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

            var close = ShellButton("关闭");
            var retest = ShellButton("重新测试", accent: true);
            footer.Children.Add(close);
            footer.Children.Add(retest);

            close.Click += (_, _) => dialog.Hide();
            retest.Click += async (_, _) => await ProbeGithubAsync(probes, statuses, summary, retest);

            dialog.Opened += async (_, _) => await ProbeGithubAsync(probes, statuses, summary, retest);
            await dialog.ShowAsync();
        }

        /// <summary>
        /// 并发探测所有节点：各自计时、各自回填那一行，最后汇总。
        /// 并发而不是逐个来，是因为最坏的超时是 8 秒一条——串行六条能让人等到关掉弹窗。
        /// </summary>
        private async Task ProbeGithubAsync(
            IReadOnlyList<GithubProbe> probes,
            List<TextBlock> statuses,
            TextBlock summary,
            Button retest)
        {
            retest.IsEnabled = false;
            summary.Text = "正在探测…";
            summary.Foreground = Secondary();

            foreach (TextBlock status in statuses)
            {
                status.Text = "探测中…";
                status.Foreground = Secondary();
            }

            GithubProbeResult[] results = await Task.WhenAll(
                probes.Select(async (probe, index) =>
                {
                    GithubProbeResult result = await GithubConnectivity.RunAsync(probe, CancellationToken.None);
                    ApplyProbeResult(statuses[index], result);
                    return result;
                }));

            SummarizeProbe(results, summary);
            retest.IsEnabled = true;
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
                status.Text = $"{latency} ms";
                status.Foreground = latency <= 300 ? Success() : Caution();
                return;
            }

            status.Text = "—";
        }

        private void SummarizeProbe(IReadOnlyList<GithubProbeResult> results, TextBlock summary)
        {
            // 只拿 GitHub 自己的域名算「最快」：中转和 git 通道不是同一维度的数字
            List<GithubProbeResult> measured = results
                .Where(item => item.Ok && !item.Probe.IsRelay && !item.Probe.IsGit && item.LatencyMs is not null)
                .ToList();

            if (measured.Count == 0)
            {
                summary.Text = "没有一个 GitHub 域名能连通。"
                    + (GithubAccel.Enabled
                        ? "试试关掉加速服务改用直连，或检查代理设置。"
                        : "检查代理设置，或打开「GitHub 加速服务」换一条出口。");
                summary.Foreground = Critical();
                return;
            }

            GithubProbeResult fastest = measured.OrderBy(item => item.LatencyMs).First();
            LauncherSettings.SetGithubTestResult(fastest.LatencyMs!.Value);
            UpdateGithubTestUi();

            var notes = new List<string>();

            if (results.Any(item => item.Probe.Key == "codeload" && !item.Ok))
            {
                notes.Add("压缩包不通：取源码请走镜像通道。");
            }

            if (results.Any(item => item.Probe.Key == "api" && !item.Ok))
            {
                notes.Add("API 不通：版本列表只能用缓存。");
            }

            if (results.Any(item => item.Probe.Key == "raw" && !item.Ok))
            {
                notes.Add("Raw 不通：blob 转 raw 的下载会失败。");
            }

            if (results.FirstOrDefault(item => item.Probe.IsRelay) is { Ok: false })
            {
                notes.Add("加速中转不可用：建议关掉加速开关，或换个时间再试。");
            }

            if (results.FirstOrDefault(item => item.Probe.IsGit) is { Ok: false, Error: not null })
            {
                notes.Add("git 拉取失败：clone、pull 与酒馆更新都会受影响。");
            }

            summary.Text = $"最快 {fastest.LatencyMs} ms（{fastest.Probe.Name}）。{string.Join(" ", notes)}";
            summary.Foreground = notes.Count == 0 ? Success() : Caution();
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

                // 「恢复默认」是一次性决策，不等防抖：万一用户立刻关窗口也要落盘
                SettingsStore.Flush();
            };

            await dialog.ShowAsync();
        }
    }
}
