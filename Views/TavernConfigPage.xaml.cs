using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AstraBrew_Launcher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

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

        /// <summary>正在下载默认配置：防止连点出两个下载任务。</summary>
        private bool _fetching;

        /// <summary>正在恢复默认配置：本地覆盖通常瞬间完成，联网时才需要等。</summary>
        private bool _resetting;

        /// <summary>正在把文件读进表单：这段时间的控件变化不算「用户改动」，不触发保存。</summary>
        private bool _loading;

        /// <summary>自动保存的防抖计时器：停手 700ms 才落盘，边打字边写文件没有意义。</summary>
        private readonly DispatcherTimer _saveTimer = new();

        /// <summary>高手版字段的变更事件只挂一次（页面可能被复用）。</summary>
        private bool _fieldsHooked;

        public TavernConfigPage()
        {
            InitializeComponent();

            _saveTimer.Interval = TimeSpan.FromMilliseconds(700);
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                SaveToConfig();
            };

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

            HookFieldEvents();
            RefreshConfigMask();
            LoadFromConfig();
            MarkClean();
        }

        /// <summary>
        /// 离开页面：防抖可能还没到点，这里立刻补一次。
        /// 放在导航事件而不是 Unloaded 上——Unloaded 要等导航动画结束，那之前窗口就可能已经关了。
        /// </summary>
        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            _saveTimer.Stop();
            SaveToConfig();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            TavernSettings.Changed -= Shared_Changed;
            TavernSettings.WhitelistChanged -= Shared_WhitelistChanged;

            // 还没到防抖时间就切走了：这里补一次，别把改动丢了
            _saveTimer.Stop();
            SaveToConfig();
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
                ? "只放最常用的几项，其余按官方推荐值跑就好。"
                : "官方 config.yaml 的全部选项，逐项标注对应字段。";
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

            if (!_syncing && !_loading)
            {
                MarkDirty();
            }
        }

        // ==================== 页头 / 底部操作 ====================

        /// <summary>
        /// 把表单里的值写回全局 config.yaml（只改这些键，注释与其它选项不动）。
        /// 由防抖计时器调用：停手后才落盘；页面切走时也会补一次。
        /// </summary>
        private void SaveToConfig()
        {
            if (!_dirty || !GlobalTavernConfig.HasGlobalConfig)
            {
                return;
            }

            double rawPort = SimplePortBox.Value;
            int port = double.IsNaN(rawPort) ? (int)DefaultPort : (int)rawPort;
            bool lan = SimpleLanToggle.IsOn;
            bool autoBrowser = SimpleAutoBrowserToggle.IsOn;
            int browserIndex = SimpleBrowserCombo.SelectedIndex < 0 ? 0 : SimpleBrowserCombo.SelectedIndex;
            double heartbeat = double.IsNaN(HeartbeatBox.Value) ? 0 : HeartbeatBox.Value;

            string ipv4 = ListenIpv4Box.Text.Trim();
            string ipv6 = ListenIpv6Box.Text.Trim();

            try
            {
                string text = GlobalTavernConfig.ReadGlobal();

                text = YamlConfig.SetScalar(text, "port", port.ToString());
                text = YamlConfig.SetScalar(text, "listen", lan ? "true" : "false");
                text = YamlConfig.SetScalar(
                    text,
                    "listenAddress.ipv4",
                    ipv4.Length > 0 ? ipv4 : lan ? "0.0.0.0" : "127.0.0.1");
                text = YamlConfig.SetScalar(text, "listenAddress.ipv6", ipv6.Length > 0 ? ipv6 : "[::]");
                text = YamlConfig.SetScalar(text, "heartbeatInterval", ((long)heartbeat).ToString());
                text = YamlConfig.SetScalar(text, "browserLaunch.enabled", autoBrowser ? "true" : "false");
                text = YamlConfig.SetScalar(text, "browserLaunch.browser", BrowserKey(browserIndex));
                text = YamlConfig.SetList(
                    text,
                    "whitelist",
                    System.Linq.Enumerable.ToList(System.Linq.Enumerable.Select(TavernSettings.Whitelist, item => item.Value)));

                text = SaveExtraFields(text);

                GlobalTavernConfig.SaveGlobal(text);
                MarkClean();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Notify("保存失败", ex.Message, NotifySeverity.Error);
            }
        }

        /// <summary>从全局配置读到表单上；文件不在就保持现状（遮罩会挡住这种情况）。</summary>
        private void LoadFromConfig()
        {
            if (!GlobalTavernConfig.HasGlobalConfig)
            {
                return;
            }

            string text;
            Dictionary<string, string> scalars;
            List<string> whitelist;

            try
            {
                text = GlobalTavernConfig.ReadGlobal();
                scalars = YamlConfig.ReadScalars(text);
                whitelist = YamlConfig.ReadList(text, "whitelist");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            // 接下来往控件里填值，这些变化不是用户改的，别触发保存
            _loading = true;

            try
            {
                int port = scalars.TryGetValue("port", out string? portText) && int.TryParse(portText, out int parsedPort)
                    ? parsedPort
                    : (int)DefaultPort;

                bool lan = scalars.TryGetValue("listen", out string? listenText)
                    && bool.TryParse(listenText, out bool listenValue)
                    && listenValue;

                bool autoBrowser = !scalars.TryGetValue("browserLaunch.enabled", out string? autoText)
                    || !bool.TryParse(autoText, out bool autoValue)
                    || autoValue;

                int browserIndex = scalars.TryGetValue("browserLaunch.browser", out string? browserText)
                    ? BrowserIndex(browserText)
                    : 0;

                double heartbeat = scalars.TryGetValue("heartbeatInterval", out string? beatText)
                    && double.TryParse(beatText, out double beatValue)
                    ? beatValue
                    : 0;

                // SyncPorts 自己会处理 _syncing，先单独调
                SyncPorts(port);

                _syncing = true;
                SimpleLanToggle.IsOn = lan;
                ListenIpv4Box.Text = scalars.TryGetValue("listenAddress.ipv4", out string? ipv4) && ipv4.Length > 0
                    ? ipv4
                    : lan ? "0.0.0.0" : "127.0.0.1";
                ListenIpv6Box.Text = scalars.TryGetValue("listenAddress.ipv6", out string? ipv6) && ipv6.Length > 0
                    ? ipv6
                    : "[::]";
                HeartbeatBox.Value = heartbeat;
                SimpleAutoBrowserToggle.IsOn = autoBrowser;
                AdvAutoBrowserToggle.IsOn = autoBrowser;
                SimpleBrowserCombo.SelectedIndex = browserIndex;
                AdvBrowserCombo.SelectedIndex = browserIndex;

                // 服务范围同理由文件决定：仪表盘的局域网/互联网 chip 读的是这一份
                TavernSettings.Scope = lan ? ServiceScope.Lan : ServiceScope.Wan;

                // 让仪表盘与启动器读到同一份浏览器选择（含官方的 default）
                TavernSettings.Browser = browserIndex switch
                {
                    1 => BrowserKind.Edge,
                    2 => BrowserKind.Firefox,
                    3 => BrowserKind.System,
                    _ => BrowserKind.Chrome,
                };

                LoadExtraFields(scalars, text);
                _syncing = false;

                if (whitelist.Count > 0)
                {
                    TavernSettings.SetWhitelist(whitelist);
                }
            }
            finally
            {
                _syncing = false;
                _loading = false;
                _saveTimer.Stop();
            }
        }

        // ==================== 高手版字段（命名约定驱动） ====================
        //
        // XAML 里给控件起名 `Field_<路径>` 就自动接上配置文件：
        //   层级用 `__` 分隔（Field_ssl__certPath → ssl.certPath）；
        //   `FieldArray_` 前缀按多行文本读写数组；`FieldEnum_` 前缀用 Tag 里的逗号分隔值表映射下拉框。
        // 类型由控件决定：CheckBox / ToggleSwitch → 布尔，NumberBox → 数字，TextBox / PasswordBox → 字符串。

        private const string ScalarPrefix = "Field_";
        private const string ArrayPrefix = "FieldArray_";
        private const string EnumPrefix = "FieldEnum_";

        /// <summary>遍历视觉树收集所有按约定命名的字段控件（折叠起来的分组也在树里）。</summary>
        private List<FrameworkElement> EnumerateFields()
        {
            var result = new List<FrameworkElement>();
            var pending = new Stack<DependencyObject>();
            pending.Push(this);

            while (pending.Count > 0)
            {
                DependencyObject node = pending.Pop();
                int count = VisualTreeHelper.GetChildrenCount(node);

                for (int index = 0; index < count; index++)
                {
                    DependencyObject child = VisualTreeHelper.GetChild(node, index);

                    if (child is FrameworkElement element && element.Name.StartsWith("Field", StringComparison.Ordinal))
                    {
                        result.Add(element);
                    }

                    pending.Push(child);
                }
            }

            return result;
        }

        private static string FieldPath(string name)
        {
            string remainder = name.StartsWith(ArrayPrefix, StringComparison.Ordinal) ? name[ArrayPrefix.Length..]
                : name.StartsWith(EnumPrefix, StringComparison.Ordinal) ? name[EnumPrefix.Length..]
                : name[ScalarPrefix.Length..];

            return remainder.Replace("__", ".");
        }

        private static bool IsTrue(string value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 多行文本框 → 数组条目。
        ///
        /// 必须同时按 <c>\r</c> 与 <c>\n</c> 切：WinUI 的 TextBox 里回车可能只留下 <c>\r</c>，
        /// 只按 <c>\n</c> 切会把整段当成一条，写回 YAML 就变成
        /// <c>- 'localhost\ncdn…'</c> 这种一段带引号的多行标量——酒馆解析直接报错、起不来。
        /// </summary>
        private static List<string> SplitLines(string text) =>
            System.Linq.Enumerable.ToList(
                System.Linq.Enumerable.Where(
                    System.Linq.Enumerable.Select(
                        text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries),
                        line => line.Trim()),
                    line => line.Length > 0));

        private void LoadExtraFields(Dictionary<string, string> scalars, string text)
        {
            foreach (FrameworkElement element in EnumerateFields())
            {
                string path = FieldPath(element.Name);

                if (element.Name.StartsWith(ArrayPrefix, StringComparison.Ordinal))
                {
                    if (element is TextBox box)
                    {
                        box.Text = string.Join(Environment.NewLine, YamlConfig.ReadList(text, path));
                    }

                    continue;
                }

                if (!scalars.TryGetValue(path, out string? value))
                {
                    continue;
                }

                if (element.Name.StartsWith(EnumPrefix, StringComparison.Ordinal))
                {
                    if (element is ComboBox combo && combo.Tag is string options)
                    {
                        string[] values = options.Split(',');
                        int index = Array.FindIndex(values, item => string.Equals(item.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase));

                        if (index >= 0)
                        {
                            combo.SelectedIndex = index;
                        }
                    }

                    continue;
                }

                switch (element)
                {
                    case CheckBox check:
                        check.IsChecked = IsTrue(value);
                        break;
                    case ToggleSwitch toggle:
                        toggle.IsOn = IsTrue(value);
                        break;
                    case PasswordBox password:
                        password.Password = value;
                        break;
                    case TextBox textBox:
                        textBox.Text = value;
                        break;
                    case NumberBox number:
                        number.Value = double.TryParse(value, out double parsed) ? parsed : double.NaN;
                        break;
                }
            }
        }

        private string SaveExtraFields(string text)
        {
            foreach (FrameworkElement element in EnumerateFields())
            {
                string path = FieldPath(element.Name);

                if (element.Name.StartsWith(ArrayPrefix, StringComparison.Ordinal))
                {
                    if (element is TextBox arrayBox)
                    {
                        text = YamlConfig.SetList(text, path, SplitLines(arrayBox.Text));
                    }

                    continue;
                }

                string? value = null;

                if (element.Name.StartsWith(EnumPrefix, StringComparison.Ordinal))
                {
                    if (element is ComboBox combo && combo.Tag is string options && combo.SelectedIndex >= 0)
                    {
                        string[] values = options.Split(',');
                        if (combo.SelectedIndex < values.Length)
                        {
                            value = values[combo.SelectedIndex].Trim();
                        }
                    }
                }
                else
                {
                    value = element switch
                    {
                        CheckBox check => check.IsChecked == true ? "true" : "false",
                        ToggleSwitch toggle => toggle.IsOn ? "true" : "false",
                        PasswordBox password => password.Password,
                        TextBox textBox => textBox.Text,
                        // 空数字框（NaN）保留文件里的原值，不把 null 写成 0
                        NumberBox number => double.IsNaN(number.Value) ? null : ((long)number.Value).ToString(),
                        _ => null,
                    };
                }

                if (value is not null)
                {
                    text = YamlConfig.SetScalar(text, path, value);
                }
            }

            return text;
        }

        /// <summary>给字段控件挂变更事件：这些控件原本没有任何事件，不挂就不会触发自动保存。</summary>
        private void HookFieldEvents()
        {
            if (_fieldsHooked)
            {
                return;
            }

            _fieldsHooked = true;

            foreach (FrameworkElement element in EnumerateFields())
            {
                switch (element)
                {
                    case CheckBox check:
                        check.Checked += Field_Changed;
                        check.Unchecked += Field_Changed;
                        break;
                    case ToggleSwitch toggle:
                        toggle.Toggled += Field_Changed;
                        break;
                    case TextBox box:
                        box.TextChanged += Field_TextChanged;
                        break;
                    case PasswordBox password:
                        password.PasswordChanged += Field_Changed;
                        break;
                    case NumberBox number:
                        number.ValueChanged += Field_NumberChanged;
                        break;
                    case ComboBox combo:
                        combo.SelectionChanged += Field_Changed;
                        break;
                }
            }
        }

        private void Field_Changed(object sender, RoutedEventArgs e)
        {
            if (!_syncing && !_loading)
            {
                MarkDirty();
            }
        }

        private void Field_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_syncing && !_loading)
            {
                MarkDirty();
            }
        }

        private void Field_NumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (!_syncing && !_loading)
            {
                MarkDirty();
            }
        }

        /// <summary>
        /// 配置里存的是浏览器关键字，界面上是四个选项。
        ///
        /// 官方默认值是 <c>default</c>（跟随系统）。以前这里只认三种浏览器，
        /// 于是「打开页面 → 随手改个别的东西 → 自动保存」就把用户的 default 悄悄写成 chrome。
        /// </summary>
        private static string BrowserKey(int index) => index switch
        {
            1 => "edge",
            2 => "firefox",
            3 => "default",
            _ => "chrome",
        };

        private static int BrowserIndex(string value) => value.ToLowerInvariant() switch
        {
            "edge" => 1,
            "firefox" => 2,
            "chrome" => 0,
            _ => 3,
        };

        private void ListenAddress_Changed(object sender, TextChangedEventArgs e)
        {
            if (_syncing || _loading)
            {
                return;
            }

            MarkDirty();
        }

        private void Heartbeat_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_syncing || _loading)
            {
                return;
            }

            MarkDirty();
        }

        /// <summary>
        /// 恢复默认配置：有本地底稿就直接覆盖全局，没有才联网取——不弹遮罩，
        /// 这是一次用户主动发起的动作，进度与结果都在全局消息里说。
        /// </summary>
        private async void Reset_Click(object sender, RoutedEventArgs e)
        {
            if (_resetting)
            {
                return;
            }

            _resetting = true;
            _saveTimer.Stop();
            ResetBtn.IsEnabled = false;
            StateText.Text = "正在恢复默认配置…";

            try
            {
                GlobalConfigFetchResult result = await GlobalTavernConfig.ResetAsync(CancellationToken.None);

                if (result.Success)
                {
                    ApplyFetchedDefaults();

                    Notify(
                        "已恢复默认配置",
                        result.Downloaded
                            ? $"本地没有默认配置，已通过{result.Source}下载并覆盖 {result.Path}；"
                                + "端口 11451、IPv6 与局域网访问已开启，白名单只保留本机。"
                            : $"已用本地默认配置覆盖 {result.Path}；"
                                + "端口 11451、IPv6 与局域网访问已开启，白名单只保留本机。",
                        NotifySeverity.Success);
                }
                else
                {
                    Notify("恢复默认配置失败", result.Error ?? "请检查网络后重试。", NotifySeverity.Error);
                }
            }
            finally
            {
                _resetting = false;
                ResetBtn.IsEnabled = true;
                StateText.Text = _dirty ? "有未保存修改" : "已同步";
            }
        }

        private void OpenConfigFile_Click(object sender, RoutedEventArgs e)
        {
            Notify(
                "已定位配置文件",
                GlobalTavernConfig.ActivePath,
                NotifySeverity.Informational);
        }

        // ==================== 首次使用：获取默认配置 ====================

        /// <summary>
        /// 全局配置不存在时盖住整页。
        /// 但先用默认底稿尝试恢复一份——底稿在就不需要联网，也不需要打扰用户。
        /// </summary>
        private void RefreshConfigMask()
        {
            GlobalTavernConfig.RestoreFromDefault();

            ConfigMask.Visibility = GlobalTavernConfig.HasGlobalConfig
                ? Visibility.Collapsed
                : Visibility.Visible;
            MaskPathText.Text = GlobalTavernConfig.GlobalPath;
        }

        private async void FetchConfig_Click(object sender, RoutedEventArgs e)
        {
            if (_fetching)
            {
                return;
            }

            _fetching = true;
            FetchConfigBtn.IsEnabled = false;
            FetchProgress.Visibility = Visibility.Visible;
            FetchProgress.IsActive = true;
            MaskStatusText.Text = GithubAccel.Enabled
                ? "正在通过 GitHub 加速下载默认配置，失败会自动回退直连…"
                : "正在从 GitHub 直连下载默认配置…";

            try
            {
                GlobalConfigFetchResult result = await GlobalTavernConfig.FetchAsync(CancellationToken.None);

                if (result.Success)
                {
                    RefreshConfigMask();
                    ApplyFetchedDefaults();
                    MaskStatusText.Text = string.Empty;

                    Notify(
                        "默认配置已就绪",
                        $"已通过{result.Source}获取并保存到 {result.Path}；"
                            + "端口 11451、IPv6 与局域网访问已开启，白名单只保留本机。",
                        NotifySeverity.Success);
                }
                else
                {
                    MaskStatusText.Text = "获取失败：" + result.Error;
                    Notify("获取默认配置失败", result.Error ?? "请检查网络后重试。", NotifySeverity.Error);
                }
            }
            finally
            {
                _fetching = false;
                FetchConfigBtn.IsEnabled = true;
                FetchProgress.IsActive = false;
                FetchProgress.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>文件刚被写好（首次获取 / 恢复默认）：按文件内容重画表单。</summary>
        private void ApplyFetchedDefaults()
        {
            LoadFromConfig();
            MarkClean();
        }

        /// <summary>
        /// 导入配置：选好文件先问「覆盖还是合并」——
        /// 覆盖是整份替换（现有改动全丢），合并是保留当前、只补导入里多出来的项。
        /// </summary>
        private async void ImportConfig_Click(object sender, RoutedEventArgs e)
        {
            string? picked = await PickConfigAsync();
            if (string.IsNullOrEmpty(picked))
            {
                return;
            }

            string imported;
            try
            {
                imported = File.ReadAllText(picked, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Notify("读取配置失败", ex.Message, NotifySeverity.Error);
                return;
            }

            if (!GlobalTavernConfig.LooksLikeConfig(imported))
            {
                Notify(
                    "这不是酒馆配置",
                    "文件里找不到 port 与 whitelistMode 这两项，看起来不是 SillyTavern 的 config.yaml。",
                    NotifySeverity.Warning);
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "导入配置",
                Content = new TextBlock
                {
                    Text = $"将 {Path.GetFileName(picked)} 导入到：\n{GlobalTavernConfig.GlobalPath}\n\n"
                        + "覆盖：当前配置整份替换成它，现有改动全部丢弃。\n"
                        + "合并：保留当前配置，只把它里面多出来的项补进来。",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "覆盖",
                SecondaryButtonText = "合并",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };

            ContentDialogResult choice = await dialog.ShowAsync();
            string fileName = Path.GetFileName(picked);

            _saveTimer.Stop();

            try
            {
                if (choice == ContentDialogResult.Primary)
                {
                    GlobalTavernConfig.OverwriteGlobal(imported);
                    LoadFromConfig();
                    MarkClean();
                    Notify(
                        "配置已覆盖",
                        $"已用 {fileName} 覆盖 {GlobalTavernConfig.GlobalPath}。",
                        NotifySeverity.Success);
                }
                else if (choice == ContentDialogResult.Secondary)
                {
                    YamlMergeResult merged = GlobalTavernConfig.MergeIntoGlobal(imported);
                    LoadFromConfig();
                    MarkClean();

                    Notify(
                        "配置已合并",
                        merged.AddedKeys == 0
                            ? $"{fileName} 里没有当前缺少的项，配置保持不变。"
                            : $"从 {fileName} 补入 {merged.AddedKeys} 项，当前已有的设置一个都没动。",
                        merged.AddedKeys == 0 ? NotifySeverity.Informational : NotifySeverity.Success);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Notify("导入失败", ex.Message, NotifySeverity.Error);
            }
        }

        private static async Task<string?> PickConfigAsync()
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };

            picker.FileTypeFilter.Add(".yaml");
            picker.FileTypeFilter.Add(".yml");

            if (App.MainWindow is not null)
            {
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            }

            StorageFile? file = await picker.PickSingleFileAsync();
            return file?.Path;
        }

        // ==================== 保存状态 ====================

        private void MarkDirty()
        {
            // 加载期间控件被填值：那不是用户改动，不许置脏。
            // 置脏的后果不只是状态点变色——离开页面会触发一次整份保存，
            // 于是「只是打开看一眼」也会把 config.yaml 重写一遍。
            if (_loading)
            {
                return;
            }

            if (!_dirty)
            {
                _dirty = true;
                StateDotClean.Visibility = Visibility.Collapsed;
                StateDotDirty.Visibility = Visibility.Visible;
                StateText.Text = "正在编辑";
                DirtyHint.Text = "停手后会自动保存";
            }

            // 防抖：每改一次就重新计时，停手 700ms 才真正写盘
            _saveTimer.Stop();
            _saveTimer.Start();
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
            StateText.Text = "已保存";
            DirtyHint.Text = $"已保存 · {DateTime.Now:HH:mm}，下次启动服务时生效";
        }

        /// <summary>页面播报统一走全局消息（窗口右下角），页面里不再挂 InfoBar。</summary>
        private static void Notify(string title, string message, NotifySeverity severity)
            => AppNotify.Post(severity, title, message);
    }
}
