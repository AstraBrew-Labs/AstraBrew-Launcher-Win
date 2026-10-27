using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using AstraBrew_Launcher.Services;
using AstraBrew_Launcher.Views;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace AstraBrew_Launcher
{
    /// <summary>
    /// 应用主窗口：自定义标题栏 + 左侧导航 + 内容 Frame。
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private const int DefaultWidth = 1180;
        private const int DefaultHeight = 780;

        /// <summary>导航项 Tag 与页面类型的映射。</summary>
        private static readonly Dictionary<string, Type> PageMap = new()
        {
            ["dashboard"] = typeof(DashboardPage),
            ["tavern"] = typeof(TavernConfigPage),
            ["versions"] = typeof(VersionPage),
            ["extensions"] = typeof(ExtensionsPage),
            ["resources"] = typeof(ResourcesPage),
            ["console"] = typeof(ConsolePage),
            ["settings"] = typeof(SettingsPage),
            ["about"] = typeof(AboutPage),
        };

        private string _currentTag = string.Empty;

        public MainWindow()
        {
            InitializeComponent();

            SetupTitleBar();
            SetupWindow();

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
            SyncSelection(tag);
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
                    NavView.SelectedItem = item;
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

        private void SetupWindow()
        {
            AppWindow.Title = "AstraBrew Launcher";

            // 窗口 / 任务栏图标使用 Assets 的彩色版本（unpackaged 下默认是通用空白图标）
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }

            // 保证界面在窄窗下仍使用紧凑布局，而不是被压垮
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.PreferredMinimumWidth = 900;
                presenter.PreferredMinimumHeight = 620;
            }

            var hwnd = WindowNative.GetWindowHandle(this);
            double scale = GetDpiForWindow(hwnd) / 96.0;

            AppWindow.Resize(new SizeInt32(
                (int)(DefaultWidth * scale),
                (int)(DefaultHeight * scale)));

            CenterOnScreen();
        }

        private void CenterOnScreen()
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            if (area is null)
            {
                return;
            }

            var size = AppWindow.Size;
            var work = area.WorkArea;

            AppWindow.Move(new PointInt32(
                work.X + Math.Max(0, (work.Width - size.Width) / 2),
                work.Y + Math.Max(0, (work.Height - size.Height) / 2)));
        }

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
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

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);
    }
}
