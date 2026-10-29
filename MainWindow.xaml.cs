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

        public MainWindow()
        {
            InitializeComponent();

            // 先定窗口外观与尺寸，再接管标题栏：
            // presenter 的 IsMaximizable 要在 ExtendsContentIntoTitleBar 之前设好，
            // 否则等于在「已自定义标题栏」的状态下改窗口能力，部分版本会抛 E_UNEXPECTED。
            SetupWindow();
            SetupTitleBar();

            // 标题栏的实例徽章是全局状态，切换实例后立刻更新
            ActiveInstance.Changed += (_, _) => ApplyInstanceChip();
            ApplyInstanceChip();

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

            // 让窗口按钮跟随 Fluent 的悬停/按下层次，而不是生硬的实色块
            if (Application.Current.RequestedTheme == ApplicationTheme.Dark)
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

            InstanceChipText.Text = ActiveInstance.DisplayLabel;
            ToolTipService.SetToolTip(
                InstanceChip,
                $"当前酒馆实例 · {ActiveInstance.KindLabel} v{ActiveInstance.Version}　在「版本管理」中切换");
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
            _placement = new WindowPlacement(this);
            Closed += (_, _) => _placement?.Dispose();
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
