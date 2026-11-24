using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AstraBrew_Launcher.Services;
using AstraBrew_Launcher.Views;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;
using WinRT.Interop;

namespace AstraBrew_Launcher
{
    /// <summary>
    /// 应用主窗口：自定义标题栏 + 左侧导航 + 内容 Frame。
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        /// <summary>导航项 Tag 与页面类型的映射。</summary>
        private static readonly Dictionary<string, Type> PageMap = new()
        {
            ["dashboard"] = typeof(DashboardPage),
            ["tavern"] = typeof(TavernConfigPage),
            ["versions"] = typeof(VersionPage),
            ["extensions"] = typeof(ExtensionsPage),
            ["resources"] = typeof(ResourcesPage),
            ["workbench"] = typeof(WorkbenchPage),
            ["console"] = typeof(ConsolePage),
            ["logs"] = typeof(LogsPage),
            ["settings"] = typeof(SettingsPage),
            ["about"] = typeof(AboutPage),
        };

        private string _currentTag = string.Empty;

        /// <summary>程序化同步导航选中项时，屏蔽 SelectionChanged 里的一次跳转。</summary>
        private bool _syncingSelection;

        /// <summary>窗口规范执行者：禁最大化 / DPI 尺寸 / 多屏归属 / 断屏回迁。</summary>
        private WindowPlacement? _placement;

        /// <summary>外观执行者：主题 / 界面字体 / 界面缩放。</summary>
        private AppearanceController? _appearance;

        /// <summary>
        /// 交给 <see cref="InstanceScan.OpenDetail"/> 的委托。留着是为了关窗时能确认那个静态槽
        /// 还指向自己——换语言重建会新建一个窗口，旧窗口不能把新窗口的挂载点清掉。
        /// </summary>
        private readonly Action _openDetailHandler;

        public MainWindow()
        {
            InitializeComponent();

            // 先定窗口外观与尺寸，再接管标题栏：
            // presenter 的 IsMaximizable 要在 ExtendsContentIntoTitleBar 之前设好，
            // 否则等于在「已自定义标题栏」的状态下改窗口能力，部分版本会抛 E_UNEXPECTED。
            SetupWindow();
            SetupTitleBar();

            // 外观三项统一交给 AppearanceController：它监听 LauncherSettings.Changed，
            // 设置页改完立刻生效，本窗口不再自己算 DPI / 字体 / 主题。
            _appearance = new AppearanceController(Root, ContentHost, ContentRoot, ApplyTitleBarColors);

            _openDetailHandler = OpenScanDetail;

            Closed += OnClosed;

            // 标题栏的实例徽章是全局状态，切换实例后立刻更新。
            // 必须用具名方法：lambda 捕获的是 this，退不掉；换语言重建窗口会一次留下一个死钩子。
            TavernInstances.Changed -= OnInstancesChanged;
            TavernInstances.Changed += OnInstancesChanged;
            ApplyInstanceChip();
            ApplyChannelBadge();

            // 全局消息：页面只管投递「装好了 / 装失败了」，弹出的位置与样式由窗口统一决定
            AppNotify.Raised += OnNotifyRaised;

            // 扫描进度卡的「查看详情」：服务层不认页面，这里把两边接起来
            InstanceScan.OpenDetail = _openDetailHandler;

            // 默认选中「仪表盘」：这里只同步侧栏高亮，真正的页面创建推迟到 StartShell()
            if (NavView.MenuItems.Count > 0 && NavView.MenuItems[0] is NavigationViewItem first)
            {
                NavView.SelectedItem = first;
            }

            Onboarding.Finished += Onboarding_Finished;

            // 首次启动（没有「已完成引导」标记）才展示引导
            if (!OnboardingState.IsCompleted)
            {
                ShowOnboarding();
            }

            // 前置条件：先用磁盘上能立刻算出来的两项打底，运行环境稍后再探
            AppReadiness.Changed -= OnReadinessChanged;
            AppReadiness.Changed += OnReadinessChanged;
            AppReadiness.RefreshLocal();
            UpdatePrerequisites();

        }

        /// <summary>
        /// 由 <see cref="App"/> 在窗口 Activate 之后调用：建第一页并开始收遮罩。
        ///
        /// 顺序很关键——窗口先进屏幕、页面后建，低配机上「点开就有反应」的观感差别很明显。
        /// 深链唤起时页面可能已经被 HandleDeepLink 建好了，这里就不再覆盖它。
        /// </summary>
        public void StartShell()
        {
            if (string.IsNullOrEmpty(_currentTag))
            {
                NavigateTo("dashboard");
            }

            _ = RunBootSequenceAsync();
        }

        /// <summary>
        /// 启动时序：先出界面，再干活。
        ///
        /// 低配机上最难受的不是「慢」，而是「窗口已经出来了、点什么都没反应」——
        /// 用户在这两三秒里的点击全落在还没建好的页面上。所以遮罩会一直盖到首屏就绪，
        /// 期间所有点击都被它挡住；运行环境探测（要起 6 个子进程）与指标扫描（要数磁盘）
        /// 则推迟到遮罩撤掉之后，避免和首帧渲染抢 CPU。
        /// </summary>
        private static void BootLog(string message)
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "astrabrew-launcher");
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, "boot.log"),
                    $"[{DateTimeOffset.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
            catch (Exception)
            {
            }
        }

        private async Task RunBootSequenceAsync()
        {
            Stopwatch watch = Stopwatch.StartNew();
            BootLog("boot start");

            try
            {
                // 让首帧先落到屏幕上：遮罩比白屏好看
                await Task.Delay(120);

                // 等第一页真的 Loaded：低配机上「窗口出来了但页面还没铺完」是常态，
                // 按固定毫秒数撤遮罩会正好露一个空白页给用户点。
                await WaitForFirstPageAsync(TimeSpan.FromSeconds(6));

                // 遮罩至少停 420ms：一闪而过像闪屏，反而让人怀疑没加载
                int remaining = 420 - (int)watch.ElapsedMilliseconds;

                if (remaining > 0)
                {
                    await Task.Delay(remaining);
                }
            }
            catch (Exception)
            {
                // 时序出错也必须把遮罩撤掉，否则整个界面永远点不动
            }
            finally
            {
                BootRing.IsActive = false;
                BootOverlay.Visibility = Visibility.Collapsed;
                BootLog($"overlay hidden after {watch.ElapsedMilliseconds} ms");
            }

            // 首屏之后的活：探测运行环境 + 更新前置条件
            await Task.Delay(350);
            await AppReadiness.RefreshAsync();
            UpdatePrerequisites();

            // 启动阶段的临时对象（TOML、XAML 解析、字体枚举）一次性收掉，
            // 低内存机器上能立刻还回去十几 MB
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: true);

            // 再把工作集还给系统一次：启动时的 XAML 解析、字体枚举会在工作集里留下
            // 一大片已经用不上的页。低内存机器上这一步能明显减少后续换页压力；
            // 代价是首次交互时这些页要重新从磁盘映射回来（毫秒级）。
            TrimWorkingSet();

            BootLog($"post-boot work done at {watch.ElapsedMilliseconds} ms");
        }

        /// <summary>
        /// 等内容 Frame 里那一页触发 Loaded（页面自己的首次布局与数据回填都在这之前完成）。
        /// 超时也放行：宁可露出一个空页面，也不能让遮罩把界面永久锁死。
        /// </summary>
        private async Task WaitForFirstPageAsync(TimeSpan timeout)
        {
            if (ContentFrame.Content is not FrameworkElement page)
            {
                return;
            }

            if (page.IsLoaded)
            {
                return;
            }

            var loaded = new TaskCompletionSource<bool>();

            void OnLoaded(object sender, RoutedEventArgs e)
            {
                page.Loaded -= OnLoaded;
                loaded.TrySetResult(true);
            }

            page.Loaded += OnLoaded;

            Task finished = await Task.WhenAny(loaded.Task, Task.Delay(timeout));

            if (finished != loaded.Task)
            {
                page.Loaded -= OnLoaded;
            }
        }

        /// <summary>把工作集收缩到当前实际需要的大小（等价于任务管理器的「收缩工作集」）。</summary>
        private static void TrimWorkingSet()
        {
            try
            {
                EmptyWorkingSet(GetCurrentProcess());
            }
            catch (Exception)
            {
                // 系统不允许就跳过，不影响任何功能
            }
        }

        private void OnReadinessChanged(object? sender, EventArgs e)
        {
            if (Root.DispatcherQueue.HasThreadAccess)
            {
                UpdatePrerequisites();
                return;
            }

            Root.DispatcherQueue.TryEnqueue(UpdatePrerequisites);
        }

        /// <summary>
        /// 按前置条件开关导航项。
        ///
        /// 控制台与扩展管理都要求「当前实例真的能跑」：没有实例时点进去只会看到一堆
        /// 点了报错的按钮，或者干脆空白。禁掉并给出说明，比让人自己试错好。
        /// </summary>
        private void UpdatePrerequisites()
        {
            if (NavConsole is null || NavExtensions is null)
            {
                return;
            }

            bool ready = AppReadiness.InstanceReady;
            string? tip = ready ? null : L.Get("Shell.Nav.NeedInstance");

            NavConsole.IsEnabled = ready;
            NavExtensions.IsEnabled = ready;
            ToolTipService.SetToolTip(NavConsole, tip);
            ToolTipService.SetToolTip(NavExtensions, tip);

            // 正停在被禁用的页面上（比如深链直接跳进来）就退回仪表盘，别留一个死页面
            if (!ready && _currentTag is "console" or "extensions")
            {
                NavigateTo("dashboard");
            }
        }

        /// <summary>
        /// 展示一条全局消息。安装收尾是在后台任务的续体里跑的，
        /// 这里统一回到 UI 线程再动视觉树（同线程时直接走，避免多一次排队）。
        /// </summary>
        private void OnNotifyRaised(object? sender, NotifyMessage message)
        {
            if (Root.DispatcherQueue.HasThreadAccess)
            {
                Notifications.Show(message);
                return;
            }

            Root.DispatcherQueue.TryEnqueue(() => Notifications.Show(message));
        }

        /// <summary>展示首次引导浮层（设置页「重新演示」也会调用）。</summary>
        public void ShowOnboarding()
        {
            Onboarding.Restart();
            Onboarding.Visibility = Visibility.Visible;
        }

        private void Onboarding_Finished(object? sender, OnboardingResult result)
        {
            Onboarding.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// 打开扫描详情：先切到版本管理页，再让页面把弹窗打开。
        /// 页面刚导航过去时还没挂进视觉树（没有 XamlRoot），所以由页面自己记一笔、Loaded 时再弹。
        /// </summary>
        public void OpenScanDetail()
        {
            NavigateTo("versions");

            if (ContentFrame.Content is VersionPage page)
            {
                page.OpenScanDetail();
            }
        }

        /// <summary>当前导航到的路由标识。</summary>
        public string CurrentTag => _currentTag;

        /// <summary>可供页面调用的全局导航入口。</summary>
        public void NavigateTo(string tag) => NavigateTo(tag, force: false);

        /// <summary>
        /// <paramref name="force"/> 用于「换语言重建窗口后回到原来那一页」：
        /// 常规导航里同页会被短路掉（避免重复建页面），而重建场景必须真的 Navigate 一次。
        /// </summary>
        public void NavigateTo(string tag, bool force)
        {
            // 有前置条件的功能不允许被绕过：深链、页面内跳转、换语言重建都走这里，
            // 统一在这一层拦住，比在每个入口各写一遍可靠
            if (tag is "console" or "extensions" && !AppReadiness.InstanceReady)
            {
                tag = "dashboard";
            }

            if (!PageMap.TryGetValue(tag, out var pageType))
            {
                return;
            }

            if (!force && _currentTag == tag && ContentFrame.Content?.GetType() == pageType)
            {
                return;
            }

            _currentTag = tag;
            ContentFrame.Navigate(
                pageType,
                null,
                force ? new SuppressNavigationTransitionInfo() : new EntranceNavigationTransitionInfo());

            // 工作台是资源管理的子页面，导航高亮留在入口那一项
            SyncSelection(tag == "workbench" ? "resources" : tag);
        }

        /// <summary>
        /// 侧栏「扩展管理」上的待更新角标。
        ///
        /// 以前这里写死 `Value="3"`，看起来像有三项要更新，其实是假的——用户点进去
        /// 一个都找不到。现在只有真的比对过远端、确实有新提交时才显示，0 就整颗藏起来。
        /// </summary>
        public void SetExtensionsBadge(int pending)
        {
            if (ExtensionsBadge is null)
            {
                return;
            }

            if (pending <= 0)
            {
                ExtensionsBadge.Visibility = Visibility.Collapsed;
                return;
            }

            ExtensionsBadge.Value = pending;
            ExtensionsBadge.Visibility = Visibility.Visible;
        }

        /// <summary>引导浮层是否盖在上面（换语言时据此退化成「重启生效」）。</summary>
        public bool IsOnboardingVisible => Onboarding.Visibility == Visibility.Visible;

        /// <summary>换语言重建前调用：把当前窗口几何静默写进设置，新窗口照着同一位置打开。</summary>
        public void PersistGeometryForReload() => _placement?.PersistBounds();

        /// <summary>
        /// 换语言重建前调用：先卸掉内容树，触发页面的 Unloaded（页面在那里退订静态事件），
        /// 再关窗口。少了这一步，页面可能还挂在静态事件的委托链上，持有已关闭窗口的控件。
        /// </summary>
        public void DetachContent() => ContentFrame.Content = null;

        /// <summary>
        /// 处理 <c>launcher-astrabrew://</c> 唤起：先把窗口拎到前台，再按路由切页面。
        ///
        /// 路由就是导航项 Tag（<c>dashboard / tavern / versions / extensions / resources /
        /// console / settings / about</c>）。路由为空或不是已知页面时只前置窗口、不改当前页——
        /// 网页可以随便写一个链接把我们叫起来，但它没有资格把用户从正在做的事情上带走。
        /// </summary>
        public void HandleDeepLink(string uriText)
        {
            BringToFront();

            string route = UriScheme.Route(uriText);
            if (route is "open" or "home" or "index")
            {
                route = "dashboard";
            }

            if (route.Length > 0 && PageMap.ContainsKey(route))
            {
                NavigateTo(route);
            }
        }

        /// <summary>
        /// 把窗口拎到最前面：最小化先还原，再 Activate + SetForegroundWindow。
        /// 从浏览器唤起时我们是「被别的进程拉起来的」，光 Activate 经常只是任务栏闪一下，
        /// 必须补一次 SetForegroundWindow 才能真正拿到前台。
        /// </summary>
        public void BringToFront()
        {
            try
            {
                if (AppWindow.Presenter is OverlappedPresenter presenter &&
                    presenter.State == OverlappedPresenterState.Minimized)
                {
                    presenter.Restore();
                }

                Activate();
                SetForegroundWindow(WindowNative.GetWindowHandle(this));
            }
            catch (Exception)
            {
                // 窗口正在销毁：忽略，下一次唤起会重来
            }
        }

        [DllImport("psapi.dll")]
        private static extern bool EmptyWorkingSet(IntPtr process);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>让左侧导航的选中项与当前路由保持一致。</summary>
        private void SyncSelection(string tag)
        {
            foreach (var menuItem in NavView.MenuItems)
            {
                if (TrySelect(menuItem, tag))
                {
                    return;
                }
            }

            foreach (var menuItem in NavView.FooterMenuItems)
            {
                if (TrySelect(menuItem, tag))
                {
                    return;
                }
            }
        }

        private bool TrySelect(object menuItem, string tag)
        {
            if (menuItem is NavigationViewItem item && item.Tag is string itemTag && itemTag == tag)
            {
                if (!ReferenceEquals(NavView.SelectedItem, item))
                {
                    _syncingSelection = true;
                    try
                    {
                        NavView.SelectedItem = item;
                    }
                    finally
                    {
                        _syncingSelection = false;
                    }
                }

                return true;
            }

            return false;
        }

        private void SetupTitleBar()
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            var titleBar = AppWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

            ApplyTitleBarColors();
        }

        /// <summary>
        /// 窗口按钮（最小化 / 最大化 / 关闭）的配色要跟着界面主题走：
        /// 用应用启动时的主题会让「系统深色 + 界面浅色」这类组合露出黑块。
        /// 主题一变由 <see cref="AppearanceController"/> 回来调一次。
        ///
        /// **常态前景色也必须显式给**（ButtonForegroundColor）：系统标题栏的按钮图标颜色跟着
        /// Windows 的应用模式走，不是跟着我们的界面主题走。系统浅色 + 界面手动切深色时，
        /// 三个按钮会保持黑色图标，压在深色标题栏上等于隐身——虚拟机里就是这么发现的。
        /// </summary>
        private void ApplyTitleBarColors()
        {
            try
            {
                var dark = Root.ActualTheme == ElementTheme.Dark;
                var titleBar = AppWindow.TitleBar;

                if (dark)
                {
                    titleBar.ButtonForegroundColor = Colors.White;
                    titleBar.ButtonInactiveForegroundColor = Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF);
                    titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF);
                    titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF);
                    titleBar.ButtonHoverForegroundColor = Colors.White;
                }
                else
                {
                    titleBar.ButtonForegroundColor = Colors.Black;
                    titleBar.ButtonInactiveForegroundColor = Color.FromArgb(0x99, 0x00, 0x00, 0x00);
                    titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x0F, 0x00, 0x00, 0x00);
                    titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x0A, 0x00, 0x00, 0x00);
                    titleBar.ButtonHoverForegroundColor = Colors.Black;
                }
            }
            catch (Exception)
            {
                // 标题栏装饰失败只降级，不影响启动
            }
        }

        /// <summary>
        /// 关窗顺序有讲究：
        ///   1. 先记录窗口位置 —— 此时 AppWindow 还是正常态，坐标才有意义；
        ///   2. 再把待写的设置刷到磁盘 —— 进程结束后防抖定时器里的那次写入就没了。
        /// </summary>
        private static int _closing;

        private void OnClosed(object sender, WindowEventArgs args)
        {
            // 停服务并等端口的那几秒里用户可能再点一次关闭：只处理一遍
            if (Interlocked.Exchange(ref _closing, 1) == 1)
            {
                return;
            }

            AppNotify.Raised -= OnNotifyRaised;
            TavernInstances.Changed -= OnInstancesChanged;

            // 静态事件必须退订：换语言会重建窗口，不退订就让已关闭的窗口一直挂在事件链上
            AppReadiness.Changed -= OnReadinessChanged;

            // 只清自己挂上去的那一份，别把新窗口的挂载点抹掉
            if (ReferenceEquals(InstanceScan.OpenDetail, _openDetailHandler))
            {
                InstanceScan.OpenDetail = null;
            }

            // 换语言重建：新窗口已经起来了，这时停酒馆 / 关桌面窗口都是误伤
            if (LanguageSwitch.IsRebuilding)
            {
                _placement?.PersistBounds();
                SettingsStore.SaveNow();
                SettingsStore.Flush();
                _placement?.Dispose();
                _appearance?.Dispose();
                return;
            }

            // 桌面窗口是独立顶层窗口，主窗口关了它不会自己退：先收掉，免得留下孤儿窗口
            DesktopWindow.Close();

            _placement?.PersistBounds();

            // 「关闭启动器后继续运行」是唯一真值：
            //   · 关着 → 一定停（进程跟随启动器）
            //   · 开着但这次是直连（PM2 缺失或回退）→ 也停：没有 PM2 保活，留下只可能是占端口的孤儿
            bool keep = LauncherSettings.AllowBackground && TavernRuntime.State.ManagedByPm2;
            TavernProcessState state = TavernRuntime.State;

            if (keep)
            {
                RuntimeLog.Info("shutdown: tavern kept by PM2");
                ActivityLog.Record(ActivityCategory.Service, L.Get("Activity.Service.Keep"));
            }
            else if (state.HasProcess || state.ManagedByPm2)
            {
                // 顺序是硬要求：先停进程、再等端口真的放开，最后才允许退出。
                // 只杀进程不校验端口的话，下次启动可能撞上残留或 TIME_WAIT。
                ActivityLog.Record(ActivityCategory.Service, L.Get("Activity.Service.Shutdown"));

                bool free = TavernRuntime.Shutdown(TimeSpan.FromSeconds(3));

                if (!free && state.Port > 0)
                {
                    AppNotify.Warning(
                        L.Get("Run.ClosePortStuck.Title"),
                        L.F("Run.ClosePortStuck.Body", state.Port));
                }
            }

            // 窗口位置是静默写入的（不进保存队列），必须整份写一次；
            // 顺带把还在防抖里的改动一起落盘，进程结束后定时器不会再触发。
            // 放在停止之后：上面的「是否托管、端口有没有卡住」要能进日志再关会话。
            SettingsStore.SaveNow();
            SettingsStore.Flush();

            _placement?.Dispose();
            _appearance?.Dispose();

            // 最后一件事：写完 session end 并把日志队列排空、放掉文件句柄。
            // 放在所有持久化之后，这样前面的收尾万一抛异常也已经在日志里留痕。
            RuntimeLog.EndSession();
        }

        /// <summary>
        /// 标题栏徽章：显示当前选中的酒馆实例（内置-1.19.2 / 本地-1.19.0）。
        /// 常驻标题栏，用户在任意页面都能确认启动时用的是哪一份实例。
        /// </summary>
        private void OnInstancesChanged(object? sender, EventArgs e)
        {
            ApplyInstanceChip();
            AppReadiness.RefreshLocal();
        }

        private void ApplyInstanceChip()
        {
            if (InstanceChipText is null)
            {
                return;
            }

            InstanceChipText.Text = TavernInstances.Current.DisplayLabel;
            ToolTipService.SetToolTip(
                InstanceChip,
                L.F(
                    "Shell.Instance.ToolTip",
                    $"{TavernInstances.Current.KindLabel} {TavernInstances.Current.KernelLabel}"));
        }

        /// <summary>
        /// 标题栏左上角的渠道标记：预览版 / 测试版 / 开发版。
        /// **正式版整块收起**（Visibility 而不是空文字），标题栏不该多出一点空位；
        /// 非正式版常驻显示，用户截个图、报一句 bug，看左上角就知道是哪个渠道的构建，
        /// 不用再翻到关于页去确认。
        /// </summary>
        private void ApplyChannelBadge()
        {
            if (ChannelBadge is null || ChannelBadgeText is null)
            {
                return;
            }

            if (BuildChannelInfo.Badge is not { Length: > 0 } badge)
            {
                ChannelBadge.Visibility = Visibility.Collapsed;
                return;
            }

            var (r, g, b) = BuildChannelInfo.Accent;

            ChannelBadgeText.Text = badge;
            ChannelBadgeText.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, r, g, b));
            ChannelBadge.Background = new SolidColorBrush(Color.FromArgb(0x38, r, g, b));
            ChannelBadge.Visibility = Visibility.Visible;

            ToolTipService.SetToolTip(
                ChannelBadge,
                L.F("Channel.ToolTip", badge, BuildChannelInfo.Channel));
        }

        /// <summary>
        /// 换语言后刷新标题栏里**由代码写进去**的文案（XAML 里的 loc:Loc.* 由 Loc.RefreshAll 负责）。
        /// 实例胶囊、渠道标记的提示都在这里重取，否则切完语言它们还挂着旧语言的字。
        /// </summary>
        public void RefreshLocalizedText()
        {
            ApplyInstanceChip();
            ApplyChannelBadge();
        }

        private void SetupWindow()
        {
            AppWindow.Title = "AstraBrew Launcher";

            // 窗口 / 任务栏图标使用 Assets 的彩色版本（unpackaged 下默认是通用空白图标）
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }

            // 窗口规范统一交给 WindowPlacement：
            // 禁止最大化、按当前显示器 DPI 定尺寸并夹进工作区、跟随多屏切换、副屏断开自动回主屏。
            // 释放与「记录窗口位置」放在 OnClosed 里，保证顺序（先记录再 Dispose）。
            _placement = new WindowPlacement(this);
        }

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (_syncingSelection)
            {
                return;
            }

            if (args.IsSettingsSelected)
            {
                NavigateTo("settings");
                return;
            }

            if (args.SelectedItemContainer is NavigationViewItem item && item.Tag is string tag)
            {
                NavigateTo(tag);
            }
        }
    }
}
