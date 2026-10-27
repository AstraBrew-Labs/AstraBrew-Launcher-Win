using System;
using System.Collections.ObjectModel;
using AstraBrew_Launcher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.ApplicationModel.DataTransfer;

namespace AstraBrew_Launcher.Views
{
    /// <summary>
    /// 酒馆配置：同一份配置的两种呈现——
    /// 小白版只暴露端口、访问范围、浏览器与 IP 白名单；
    /// 高手版保留旧版一致的 9 组完整选项，分组可折叠。
    /// 两版共用同一批状态，切换版本后端口、局域网、浏览器与白名单都保持一致。
    /// </summary>
    public sealed partial class TavernConfigPage : Page
    {
        private const double DefaultPort = 8000;

        /// <summary>初始化 / 程序赋值期间屏蔽回写，避免控件互相触发。</summary>
        private bool _syncing = true;

        private bool _dirty;

        public TavernConfigPage()
        {
            InitializeComponent();

            // 端口默认值统一在代码里写入，避免 XAML 的 Value 早于事件挂载
            SimplePortBox.Value = DefaultPort;
            AdvPortBox.Value = DefaultPort;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        /// <summary>x:Bind 数据源：两个版本的白名单列表指向同一个集合。</summary>
        public ObservableCollection<WhitelistEntry> Whitelist => TavernSettings.Whitelist;

        // ==================== 生命周期 ====================

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            TavernSettings.Changed += Shared_Changed;
            TavernSettings.WhitelistChanged += Shared_WhitelistChanged;

            _syncing = true;
            ApplyVersion();
            SyncBrowserRows();
            SyncBrowserSelection();
            SyncPorts(SimplePortBox.Value);
            SyncWhitelistMeta();
            _syncing = false;

            MarkClean();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            TavernSettings.Changed -= Shared_Changed;
            TavernSettings.WhitelistChanged -= Shared_WhitelistChanged;
        }

        // ==================== 版本切换 ====================

        private void Version_Checked(object sender, RoutedEventArgs e)
        {
            if (SimplePanel is null)
            {
                return;
            }

            ApplyVersion();
        }

        private void ApplyVersion()
        {
            bool simple = SimpleRadio.IsChecked == true;

            SimplePanel.Visibility = simple ? Visibility.Visible : Visibility.Collapsed;
            AdvancedPanel.Visibility = simple ? Visibility.Collapsed : Visibility.Visible;

            VersionTitle.Text = simple ? "小白版 · 常用设置" : "高手版 · 全部选项";
            VersionHint.Text = simple
                ? "只保留端口、访问范围与浏览器等日常选项，其余按推荐值运行。"
                : "完整覆盖网络、账号、SSL、CORS、备份、缩略图、性能、日志与会话共 9 组配置。";
            VersionCount.Text = simple ? "5 项" : "9 组";
        }

        // ==================== 分组折叠 ====================

        /// <summary>
        /// Tag 指向分组前缀（如 Net），约定伴生元素为 NetBody / NetChevron，
        /// 9 个分组因此共用一个处理器。
        /// </summary>
        private void Section_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton toggle || toggle.Tag is not string key)
            {
                return;
            }

            bool expanded = toggle.IsChecked == true;

            if (FindName($"{key}Body") is FrameworkElement body)
            {
                body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            }

            if (FindName($"{key}Chevron") is FontIcon chevron)
            {
                chevron.Glyph = expanded ? "\uE70E" : "\uE70D";
            }
        }

        /// <summary>开关联动子面板：Tag 指向要显隐的面板名。</summary>
        private void Conditional_Toggled(object sender, RoutedEventArgs e)
        {
            bool on = sender switch
            {
                ToggleSwitch toggle => toggle.IsOn,
                CheckBox check => check.IsChecked == true,
                _ => false,
            };

            if (sender is FrameworkElement { Tag: string panelName }
                && FindName(panelName) is FrameworkElement panel)
            {
                panel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            }

            if (!_syncing)
            {
                MarkDirty();
            }
        }

        // ==================== 端口 ====================

        private void Port_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_syncing)
            {
                return;
            }

            SyncPorts(args.NewValue);
            MarkDirty();
        }

        /// <summary>两个版本的端口输入框保持同值，并做范围校验。</summary>
        private void SyncPorts(double value)
        {
            double port = double.IsNaN(value) ? DefaultPort : value;

            _syncing = true;
            SimplePortBox.Value = port;
            AdvPortBox.Value = port;
            _syncing = false;

            bool invalid = double.IsNaN(value) || port < 1024 || port > 65535;
            var state = invalid ? Visibility.Visible : Visibility.Collapsed;
            SimplePortError.Visibility = state;
            AdvPortError.Visibility = state;

            UpdateEndpointPreview();
        }

        private void UpdateEndpointPreview()
        {
            if (SimpleEndpointText is null || SimplePortBox is null || SimpleLanToggle is null)
            {
                return;
            }

            double raw = SimplePortBox.Value;
            int port = double.IsNaN(raw) ? (int)DefaultPort : (int)raw;
            bool lan = SimpleLanToggle.IsOn;

            SimpleEndpointText.Text = lan
                ? $"http://192.168.1.24:{port}"
                : $"http://127.0.0.1:{port}";
        }

        // ==================== 局域网 / 浏览器 ====================

        private void Lan_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncing)
            {
                return;
            }

            bool on = sender is ToggleSwitch { IsOn: true };

            _syncing = true;
            SimpleLanToggle.IsOn = on;
            _syncing = false;

            ListenIpv4Box.Text = on ? "0.0.0.0" : "127.0.0.1";
            UpdateEndpointPreview();
            MarkDirty();
        }

        private void AutoBrowser_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncing)
            {
                return;
            }

            bool on = sender is ToggleSwitch { IsOn: true };

            _syncing = true;
            SimpleAutoBrowserToggle.IsOn = on;
            AdvAutoBrowserToggle.IsOn = on;
            _syncing = false;

            MarkDirty();
        }

        private void Browser_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || sender is not ComboBox combo || combo.SelectedIndex < 0)
            {
                return;
            }

            TavernSettings.Browser = combo.SelectedIndex switch
            {
                1 => BrowserKind.Edge,
                2 => BrowserKind.Firefox,
                _ => BrowserKind.Chrome,
            };

            MarkDirty();
        }

        private void SyncBrowserSelection()
        {
            int index = TavernSettings.Browser switch
            {
                BrowserKind.Edge => 1,
                BrowserKind.Firefox => 2,
                _ => 0,
            };

            _syncing = true;

            if (SimpleBrowserCombo is not null)
            {
                SimpleBrowserCombo.SelectedIndex = index;
            }

            if (AdvBrowserCombo is not null)
            {
                AdvBrowserCombo.SelectedIndex = index;
            }

            _syncing = false;
        }

        /// <summary>自动打开浏览器 / 默认浏览器只在普通模式下出现。</summary>
        private void SyncBrowserRows()
        {
            if (SimpleAutoBrowserRow is null)
            {
                return;
            }

            bool normal = TavernSettings.IsNormalMode;
            var visibility = normal ? Visibility.Visible : Visibility.Collapsed;

            SimpleAutoBrowserRow.Visibility = visibility;
            SimpleBrowserRow.Visibility = visibility;
            AdvAutoBrowserRow.Visibility = visibility;
            AdvBrowserRow.Visibility = visibility;

            BrowserModeHint.IsOpen = !normal;
            BrowserModeHint.Message = $"当前为{TavernSettings.ModeName}，浏览器相关选项已隐藏，切换回普通模式后自动恢复。";
        }

        // ==================== 白名单 ====================

        private void AddWhitelistSimple_Click(object sender, RoutedEventArgs e)
            => AddWhitelist(SimpleWhitelistInput, SimpleWhitelistError);

        private void AddWhitelistAdvanced_Click(object sender, RoutedEventArgs e)
            => AddWhitelist(AdvWhitelistInput, AdvWhitelistError);

        private void AddWhitelist(TextBox input, TextBlock error)
        {
            if (TavernSettings.TryAddWhitelist(input?.Text, out var message))
            {
                if (input is not null)
                {
                    input.Text = string.Empty;
                }

                error.Visibility = Visibility.Collapsed;
                MarkDirty();
            }
            else
            {
                error.Text = message;
                error.Visibility = Visibility.Visible;
            }
        }

        private void RemoveWhitelist_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: WhitelistEntry entry }
                && TavernSettings.RemoveWhitelist(entry))
            {
                MarkDirty();
            }
        }

        private void SyncWhitelistMeta()
        {
            if (SimpleWhitelistCount is null)
            {
                return;
            }

            SimpleWhitelistCount.Text = $"{TavernSettings.Whitelist.Count} 条";
        }

        // ==================== 共享状态 ====================

        private void Shared_Changed(object? sender, EventArgs e)
        {
            SyncBrowserRows();
            SyncBrowserSelection();
        }

        private void Shared_WhitelistChanged(object? sender, EventArgs e)
        {
            SyncWhitelistMeta();
            SimpleWhitelistError.Visibility = Visibility.Collapsed;
            AdvWhitelistError.Visibility = Visibility.Collapsed;
        }

        // ==================== 页头 / 底部操作 ====================

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            MarkClean();

            double raw = SimplePortBox.Value;
            int port = double.IsNaN(raw) ? (int)DefaultPort : (int)raw;
            string scope = SimpleLanToggle.IsOn ? "局域网可访问" : "仅本机可访问";

            ShowBar(
                "配置已保存",
                $"端口 {port} · {scope} · 白名单 {TavernSettings.Whitelist.Count} 条 · 默认浏览器 {TavernSettings.BrowserName}。",
                InfoBarSeverity.Success);
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            _syncing = true;
            SimpleLanToggle.IsOn = false;
            SimpleAutoBrowserToggle.IsOn = true;
            AdvAutoBrowserToggle.IsOn = true;
            ListenIpv4Box.Text = "127.0.0.1";
            _syncing = false;

            SyncPorts(DefaultPort);
            TavernSettings.ResetWhitelist();
            TavernSettings.Browser = BrowserKind.Chrome;

            MarkClean();
            ShowBar("已恢复默认配置", "端口 8000、仅本机访问、白名单恢复为默认两项锁定地址。", InfoBarSeverity.Informational);
        }

        private void OpenConfigFile_Click(object sender, RoutedEventArgs e)
        {
            ShowBar(
                "已定位配置文件",
                @"%AppData%\AstraBrew Launcher\data\config.yaml",
                InfoBarSeverity.Informational);
        }

        private void ImportConfig_Click(object sender, RoutedEventArgs e)
        {
            ShowBar(
                "请选择要导入的配置文件",
                "支持 config.yaml 与旧版 settings.json，导入后会覆盖当前表单。",
                InfoBarSeverity.Informational);
        }

        private void CopyEndpoint_Click(object sender, RoutedEventArgs e)
        {
            var package = new DataPackage();
            package.SetText(SimpleEndpointText.Text);
            Clipboard.SetContent(package);

            ShowBar("地址已复制", SimpleEndpointText.Text, InfoBarSeverity.Success);
        }

        // ==================== 保存状态 ====================

        private void MarkDirty()
        {
            if (_dirty)
            {
                return;
            }

            _dirty = true;
            StateDotClean.Visibility = Visibility.Collapsed;
            StateDotDirty.Visibility = Visibility.Visible;
            StateText.Text = "有未保存修改";
            DirtyHint.Text = "尚未保存的修改会在下次启动服务时生效";
        }

        private void MarkClean()
        {
            _dirty = false;

            if (StateDotClean is null)
            {
                return;
            }

            StateDotClean.Visibility = Visibility.Visible;
            StateDotDirty.Visibility = Visibility.Collapsed;
            StateText.Text = "已同步";
            DirtyHint.Text = "修改会在下次启动服务时生效";
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
