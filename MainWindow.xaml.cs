using System;
using System.Collections.Generic;
using System.IO;
using AstraBrew_Launcher.Services;
using AstraBrew_Launcher.Views;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

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

            Closed += OnClosed;

            // 标题栏的实例徽章是全局状态，切换实例后立刻更新
            TavernInstances.Changed += (_, _) => ApplyInstanceChip();
            ApplyInstanceChip();

            // 全局消息：页面只管投递「装好了 / 装失败了」，弹出的位置与样式由窗口统一决定
            AppNotify.Raised += OnNotifyRaised;

            // 扫描进度卡的「查看详情」：服务层不认页面，这里把两边接起来
            InstanceScan.OpenDetail = OpenScanDetail;

            // 默认选中「仪表盘」
            if (NavView.MenuItems.Count > 0 && NavView.MenuItems[0] is NavigationViewItem first)
            {
                NavView.SelectedItem = first;
            }

            if (string.IsNullOrEmpty(_currentTag))
            {
                NavigateTo("dashboard");
            }

            Onboarding.Finished += Onboarding_Finished;

            // 首次启动（没有「已完成引导」标记）才展示引导
            if (!OnboardingState.IsCompleted)
            {
                ShowOnboarding();
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
        public void NavigateTo(string tag)
        {
            if (!PageMap.TryGetValue(tag, out var pageType))
            {
                return;
            }

            if (_currentTag == tag && ContentFrame.Content?.GetType() == pageType)
            {
                return;
            }

            _currentTag = tag;
            ContentFrame.Navigate(pageType, null, new EntranceNavigationTransitionInfo());

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
        /// 窗口按钮（最小化 / 关闭）的悬停配色要跟着界面主题走：
        /// 用应用启动时的主题会让「系统深色 + 界面浅色」这类组合露出黑块。
        /// 主题一变由 <see cref="AppearanceController"/> 回来调一次。
        /// </summary>
        private void ApplyTitleBarColors()
        {
            try
            {
                var dark = Root.ActualTheme == ElementTheme.Dark;
                var titleBar = AppWindow.TitleBar;

                if (dark)
                {
                    titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF);
                    titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF);
                    titleBar.ButtonHoverForegroundColor = Colors.White;
                }
                else
                {
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
        private void OnClosed(object sender, WindowEventArgs args)
        {
            AppNotify.Raised -= OnNotifyRaised;

            _placement?.PersistBounds();

            // 关窗口要不要连酒馆一起停：运行时活在静态状态里，这里不收它就会变成孤儿进程，
            // 端口一直被占着，下次启动只会看到「端口被占用」。同步杀进程树——窗口一关事件循环就没了。
            if (LauncherSettings.StopTavernOnWindowClose)
            {
                TavernRuntime.Shutdown();
            }

            // 窗口位置是静默写入的（不进保存队列），必须整份写一次；
            // 顺带把还在防抖里的改动一起落盘，进程结束后定时器不会再触发。
            SettingsStore.SaveNow();
            SettingsStore.Flush();

            _placement?.Dispose();
            _appearance?.Dispose();
        }

        /// <summary>
        /// 标题栏徽章：显示当前选中的酒馆实例（内置-1.19.2 / 本地-1.19.0）。
        /// 常驻标题栏，用户在任意页面都能确认启动时用的是哪一份实例。
        /// </summary>
        private void ApplyInstanceChip()
        {
            if (InstanceChipText is null)
            {
                return;
            }

            InstanceChipText.Text = TavernInstances.Current.DisplayLabel;
            ToolTipService.SetToolTip(
                InstanceChip,
                $"当前酒馆实例 · {TavernInstances.Current.KindLabel} {TavernInstances.Current.KernelLabel}　在「版本管理」中切换");        }

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
