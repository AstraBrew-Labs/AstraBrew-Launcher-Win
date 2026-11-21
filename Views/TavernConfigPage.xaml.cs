using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AstraBrew_Launcher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage;
using Windows.System;
using Windows.UI;
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

        /// <summary>端口检测的取消源：连续改数字时只认最后一次。</summary>
        private CancellationTokenSource? _portCheckCts;

        /// <summary>端口可用的提示色（跟仪表盘的状态绿一致，深浅主题下都够看）。</summary>
        private static readonly Brush PortFreeBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x16, 0xA3, 0x4A));

        /// <summary>端口被占用的提示色。</summary>
        private static readonly Brush PortBusyBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xD1, 0x34, 0x38));

        /// <summary>检测中的提示色（次要灰）。</summary>
        private static readonly Brush PortCheckingBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A));

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

        /// <summary>上一次保存的时间，用来重拼「已保存 · 14:32」这句。</summary>
        private DateTime? _savedAt;

        /// <summary>x:Bind 数据源：两个版本的白名单列表指向同一个集合。</summary>
        public ObservableCollection<WhitelistEntry> Whitelist => TavernSettings.Whitelist;

        // ==================== 生命周期 ====================

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            TavernSettings.Changed += Shared_Changed;
            TavernSettings.WhitelistChanged += Shared_WhitelistChanged;

            // 页头状态、版本说明、白名单提示这些是代码拼出来的，Loc 刷不到
            LanguageSwitch.LanguageChanged -= OnLanguageChanged;
            LanguageSwitch.LanguageChanged += OnLanguageChanged;

            _syncing = true;
            ApplyVersion();
            SyncBrowserRows();
            SyncBrowserSelection();
            SyncPorts(SimplePortBox.Value);
            SyncWhitelistMeta();
            SyncWhitelistUi();
            _syncing = false;

            HookFieldEvents();
            RefreshConfigMask();
            LoadFromConfig();
            MarkClean();

            // 打开这一页最想确认的就是「端口能不能用」，进来就测
            SchedulePortCheck(immediate: true);

            // 启动前因端口被占被送到这里：把那一行标出来，用户不用自己找
            if (PortAttention.TryConsume(out int attentionPort))
            {
                HighlightPort(attentionPort);
            }
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
            // 检测还没回来就离开：取消它，并把输入框恢复成可编辑
            // （禁用状态是检测加上去的，取消了就没人负责撤销，页面复用时会是灰的）
            _portCheckCts?.Cancel();
            _portCheckCts = null;
            SetPortEditing(true);

            TavernSettings.Changed -= Shared_Changed;
            TavernSettings.WhitelistChanged -= Shared_WhitelistChanged;
            LanguageSwitch.LanguageChanged -= OnLanguageChanged;

            // 还没到防抖时间就切走了：这里补一次，别把改动丢了
            _saveTimer.Stop();
            SaveToConfig();
        }

        /// <summary>
        /// 换语言：静态文字由 <c>Loc</c> 重刷，这里重画由状态拼出来的那几处——
        /// 版本说明、白名单提示、保存状态与项数。
        /// </summary>
        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            ApplyVersion();
            SyncBrowserRows();
            SyncWhitelistMeta();
            SyncWhitelistUi();
            RefreshSaveState();
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

            VersionTitle.Text = L.Get(simple ? "Cfg.Version.Simple.Title" : "Cfg.Version.Advanced.Title");
            VersionHint.Text = L.Get(simple ? "Cfg.Version.Simple.Hint" : "Cfg.Version.Advanced.Hint");
            VersionCount.Text = simple
                ? L.F("Cfg.Version.Count", TavernSettings.IsNormalMode ? 5 : 3)
                : L.Get("Cfg.Version.Advanced.Count");
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

            // 用户开始改端口了：启动失败时留下的红框该撤掉，别一直挂着「请修改」
            ClearPortHighlight();

            MarkDirty();
            SchedulePortCheck();
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

        // ==================== 端口检测 ====================

        private enum PortStatus
        {
            Hidden,
            Checking,
            Free,
            Busy,
        }

        /// <summary>
        /// 调度一次端口检测。改数字时防抖 320ms：每敲一位都起一次绑定测试没有意义，
        /// 而且「检测期间输入框是禁用的」，抖得太频繁会让输入框一直闪。
        /// </summary>
        private void SchedulePortCheck(bool immediate = false)
        {
            if (_loading)
            {
                return;
            }

            double raw = SimplePortBox.Value;
            int port = double.IsNaN(raw) ? (int)DefaultPort : (int)raw;

            _ = CheckPortAsync(port, immediate);
        }

        /// <summary>
        /// 真去绑一次端口。**检测期间两个输入框都禁用**：不锁的话用户可以在
        /// 「已经发出去的检测」和「刚改的数字」之间错位——提示是旧端口的结论，
        /// 用户却以为在说新端口。检测只有毫秒级，锁一下不影响输入手感。
        /// </summary>
        private async Task CheckPortAsync(int port, bool immediate)
        {
            _portCheckCts?.Cancel();

            var cts = new CancellationTokenSource();
            _portCheckCts = cts;

            // 范围问题由「范围错误」那行提示，这里不叠第二句
            if (port is < 1024 or > 65535)
            {
                SetPortStatus(PortStatus.Hidden, port, null);
                SetPortEditing(true);
                cts.Dispose();
                return;
            }

            try
            {
                // 先等用户停手，这段**不禁用**输入框：防抖是「等人」，不是「在检测」，
                // 把 320ms 也算进禁用窗口的话，每敲一次数字手都会被锁住
                await Task.Delay(immediate ? 80 : 320, cts.Token);
                cts.Token.ThrowIfCancellationRequested();

                // 真要开始测了才锁：这一小段用户改不了值，提示与结论才对得上
                SetPortEditing(false);
                SetPortStatus(PortStatus.Checking, port, null);

                // 让 UI 把禁用态与「正在检测」画出来，再去做实际探测
                await Task.Yield();

                bool free = await Task.Run(() => PortProbe.IsFree(port), cts.Token);
                cts.Token.ThrowIfCancellationRequested();

                string? ownerName = null;

                if (!free)
                {
                    // 占用时才去查是谁（netstat 要一百多毫秒），有空闲路径上不付这个代价
                    List<PortOwnerInfo> owners = await Task.Run(() => PortProbe.FindOwners(port), cts.Token);
                    ownerName = owners.FirstOrDefault()?.Name;
                }

                cts.Token.ThrowIfCancellationRequested();
                SetPortStatus(free ? PortStatus.Free : PortStatus.Busy, port, ownerName);
            }
            catch (OperationCanceledException)
            {
                // 被下一次检测顶掉：状态与输入框都交给那一次处理
                return;
            }
            finally
            {
                if (!cts.IsCancellationRequested)
                {
                    SetPortEditing(true);
                }

                if (ReferenceEquals(_portCheckCts, cts))
                {
                    _portCheckCts = null;
                }

                cts.Dispose();
            }
        }

        private void SetPortStatus(PortStatus status, int port, string? ownerName)
        {
            (string text, Brush? brush) = status switch
            {
                PortStatus.Checking => (L.Get("Cfg.Port.Checking"), PortCheckingBrush),
                PortStatus.Free => (L.F("Cfg.Port.Free", port), PortFreeBrush),
                PortStatus.Busy => (
                    ownerName is { Length: > 0 }
                        ? L.F("Cfg.Port.BusyOwner", port, ownerName)
                        : L.F("Cfg.Port.Busy", port),
                    PortBusyBrush),
                _ => (string.Empty, null),
            };

            foreach (TextBlock block in new[] { SimplePortStatus, AdvPortStatus })
            {
                if (block is null)
                {
                    continue;
                }

                block.Text = text;
                block.Visibility = status == PortStatus.Hidden ? Visibility.Collapsed : Visibility.Visible;

                if (brush is not null)
                {
                    block.Foreground = brush;
                }
            }
        }

        private void SetPortEditing(bool enabled)
        {
            SimplePortBox.IsEnabled = enabled;
            AdvPortBox.IsEnabled = enabled;
        }

        /// <summary>
        /// 标出「端口被占用，请修改」：红框 + 提示 + 滚到可见位置。
        /// 启动失败被送到这里时用——用户点开页面就能看到该改哪一行。
        /// </summary>
        private void HighlightPort(int port)
        {
            SetPortStatus(PortStatus.Busy, port, null);

            foreach (NumberBox box in new[] { SimplePortBox, AdvPortBox })
            {
                if (box is null)
                {
                    continue;
                }

                box.BorderBrush = PortBusyBrush;
                box.BorderThickness = new Thickness(2);
            }

            SimplePortBox.StartBringIntoView();
            SimplePortBox.Focus(FocusState.Programmatic);
        }

        /// <summary>撤掉高亮，把边框还给主题默认值（不是写死一个颜色）。</summary>
        private void ClearPortHighlight()
        {
            foreach (NumberBox box in new[] { SimplePortBox, AdvPortBox })
            {
                if (box is null)
                {
                    continue;
                }

                box.ClearValue(Control.BorderBrushProperty);
                box.ClearValue(Control.BorderThicknessProperty);
            }
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

                // 第 4 项是「默认浏览器」：漏了这一条会把 System 悄悄存成 Chrome，
                // 仪表盘那边就永远显示不出「默认浏览器」被选中（两边同一份设置，坑的是对面）
                3 => BrowserKind.System,
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
                BrowserKind.System => 3,
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

        /// <summary>
        /// 自动打开浏览器 / 默认浏览器只在普通模式下出现：桌面模式由启动器自己开窗口，
        /// 服务器模式根本不弹页面，这两项对它们没有任何作用，摆出来只会让人以为要配。
        /// </summary>
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

            // 副标题与项数跟着变：选项都没了还说「含默认浏览器」「5 项」会显得自相矛盾
            if (NetworkSectionHint is not null)
            {
                NetworkSectionHint.Text = L.Get(normal ? "Cfg.G1.HintLabel" : "Cfg.G1.Hint.Client");
            }

            if (VersionCount is not null && LauncherSettings.IsSimpleLevel)
            {
                VersionCount.Text = L.F("Cfg.Version.Count", normal ? 5 : 3);
            }
        }

        // ==================== 白名单 ====================

        /// <summary>
        /// 服务器模式下白名单由服务范围推导并锁死：输入框、添加按钮与删除按钮全部收起；
        /// 普通 / 桌面模式下放开编辑，只保留两个回环地址不能删。
        /// </summary>
        private void SyncWhitelistUi()
        {
            if (SimpleWhitelistInputRow is null)
            {
                return;
            }

            var editable = TavernSettings.WhitelistLocked ? Visibility.Collapsed : Visibility.Visible;

            SimpleWhitelistInputRow.Visibility = editable;
            AdvWhitelistInputRow.Visibility = editable;

            SimpleWhitelistError.Visibility = Visibility.Collapsed;
            AdvWhitelistError.Visibility = Visibility.Collapsed;

            // 桌面模式的白名单固定为回环，别再说「按服务范围生成」——它没有服务范围
            bool desktop = TavernSettings.IsDesktopMode;
            bool locked = TavernSettings.WhitelistLocked;

            string note = desktop
                ? L.Get("Cfg.Whitelist.Note.Desktop")
                : locked
                    ? L.F("Cfg.Whitelist.Note.Scope", TavernSettings.ScopeName)
                    : L.Get("Cfg.Whitelist.Note.Editable");

            SimpleWhitelistNote.Text = note;
            AdvWhitelistNote.Text = note;

            if (SimpleWhitelistSubtitle is not null)
            {
                SimpleWhitelistSubtitle.Text = desktop
                    ? L.Get("Cfg.Whitelist.Hint.Desktop")
                    : locked
                        ? L.Get("Cfg.Whitelist.Hint.Server")
                        : L.Get("Cfg.Whitelist.Note.Editable");
            }

            if (AdvWhitelistSubtitle is not null)
            {
                AdvWhitelistSubtitle.Text = desktop
                    ? L.Get("Cfg.Whitelist.Adv.Desktop")
                    : locked
                        ? L.Get("Cfg.Whitelist.Adv.Scope")
                        : L.Get("Cfg.Whitelist.Adv.On");
            }
        }

        private void AddWhitelistSimple_Click(object sender, RoutedEventArgs e)
            => AddWhitelist(SimpleWhitelistInput, SimpleWhitelistError);

        private void AddWhitelistAdvanced_Click(object sender, RoutedEventArgs e)
            => AddWhitelist(AdvWhitelistInput, AdvWhitelistError);

        /// <summary>
        /// 输入框里按回车＝点「添加」。
        /// 电脑上的输入框本来就该这样：填完一个直接回车接着填下一个，不用每次去找按钮。
        /// </summary>
        private void WhitelistInput_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter || sender is not TextBox input)
            {
                return;
            }

            // 处理掉，别再冒到外层去触发别的默认动作
            e.Handled = true;

            AddWhitelist(
                input,
                ReferenceEquals(input, AdvWhitelistInput) ? AdvWhitelistError : SimpleWhitelistError);
        }

        private void AddWhitelist(TextBox input, TextBlock error)
        {
            if (TavernSettings.TryAddWhitelist(input?.Text, out string message))
            {
                if (input is not null)
                {
                    input.Text = string.Empty;

                    // 焦点留在输入框：连着加几条时不用再点回来
                    input.Focus(FocusState.Programmatic);
                }

                error.Visibility = Visibility.Collapsed;
                MarkDirty();

                // 新条目接在末尾，列表限高后会看不见——排到布局之后再滚到底
                ScrollViewer? scroll = ReferenceEquals(input, AdvWhitelistInput)
                    ? AdvWhitelistScroll
                    : SimpleWhitelistScroll;

                if (scroll is not null)
                {
                    DispatcherQueue.TryEnqueue(() =>
                        scroll.ChangeView(null, scroll.ScrollableHeight, null));
                }
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

            SimpleWhitelistCount.Text = L.F("Cfg.Whitelist.Count", TavernSettings.Whitelist.Count);
        }

        // ==================== 共享状态 ====================

        private void Shared_Changed(object? sender, EventArgs e)
        {
            SyncBrowserRows();
            SyncBrowserSelection();
            SyncWhitelistMeta();
            SyncWhitelistUi();
        }

        private void Shared_WhitelistChanged(object? sender, EventArgs e)
        {
            SyncWhitelistMeta();
            SyncWhitelistUi();

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
                Notify(L.Get("Cfg.Notify.SaveFail"), ex.Message, NotifySeverity.Error);
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

            try
            {
                text = GlobalTavernConfig.ReadGlobal();
                scalars = YamlConfig.ReadScalars(text);
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

                // 白名单由服务范围推导，文件里那份只是结果：这里重算一遍，
                // 顺带把「换过网络、网段变了」的情况补正
                TavernSettings.ApplyWhitelist(persist: false);
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
            StateText.Text = L.Get("Cfg.State.Reverting");

            try
            {
                GlobalConfigFetchResult result = await GlobalTavernConfig.ResetAsync(CancellationToken.None);

                if (result.Success)
                {
                    ApplyFetchedDefaults();

                    Notify(
                        L.Get("Cfg.Notify.ResetOkLabel"),
                        result.Downloaded
                            ? L.F("Cfg.Notify.ResetOk.Downloaded", result.Source, result.Path)
                            : L.F("Cfg.Notify.ResetOk.Local", result.Path),
                        NotifySeverity.Success);
                }
                else
                {
                    Notify(
                        L.Get("Cfg.Notify.ResetFail"),
                        result.Error ?? L.Get("Cfg.Notify.CheckNetwork"),
                        NotifySeverity.Error);
                }
            }
            finally
            {
                _resetting = false;
                ResetBtn.IsEnabled = true;
                StateText.Text = L.Get(_dirty ? "Cfg.State.Dirty" : "Cfg.State.Synced");
            }
        }

        private void OpenConfigFile_Click(object sender, RoutedEventArgs e)
        {
            Notify(
                L.Get("Cfg.Notify.LocateOk"),
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
            // 三条渠道的先后：镜像 → 加速 → 直连，状态文字直接说清从哪开始试
            MaskStatusText.Text = L.Get(GithubAccel.Enabled
                ? "Cfg.Download.Progress.Mirror"
                : "Cfg.Download.Progress.Direct");

            try
            {
                GlobalConfigFetchResult result = await GlobalTavernConfig.FetchAsync(CancellationToken.None);

                if (result.Success)
                {
                    RefreshConfigMask();
                    ApplyFetchedDefaults();
                    MaskStatusText.Text = string.Empty;

                    Notify(
                        L.Get("Cfg.Notify.DefaultOkLabel"),
                        L.F("Cfg.Notify.DefaultOk.Body", result.Source, result.Path),
                        NotifySeverity.Success);
                }
                else
                {
                    MaskStatusText.Text = L.F("Cfg.Mask.Failed", result.Error);
                    Notify(
                        L.Get("Cfg.Notify.FetchFail"),
                        result.Error ?? L.Get("Cfg.Notify.CheckNetwork"),
                        NotifySeverity.Error);
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
                Notify(L.Get("Cfg.Notify.ReadFail"), ex.Message, NotifySeverity.Error);
                return;
            }

            if (!GlobalTavernConfig.LooksLikeConfig(imported))
            {
                Notify(
                    L.Get("Cfg.Import.NotTavern.Title"),
                    L.Get("Cfg.Import.NotTavern.Body"),
                    NotifySeverity.Warning);
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = L.Get("Cfg.Import.Dialog.Title"),
                Content = new TextBlock
                {
                    Text = L.F("Cfg.Import.Dialog.Body", Path.GetFileName(picked), GlobalTavernConfig.GlobalPath),
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = L.Get("Cfg.Import.Overwrite"),
                SecondaryButtonText = L.Get("Cfg.Import.Merge"),
                CloseButtonText = L.Get("Common.Action.Cancel.Content"),
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
                        L.Get("Cfg.Notify.Imported.Title"),
                        L.F("Cfg.Notify.Imported.Body", fileName, GlobalTavernConfig.GlobalPath),
                        NotifySeverity.Success);
                }
                else if (choice == ContentDialogResult.Secondary)
                {
                    YamlMergeResult merged = GlobalTavernConfig.MergeIntoGlobal(imported);
                    LoadFromConfig();
                    MarkClean();

                    Notify(
                        L.Get("Cfg.Notify.Merged.Title"),
                        merged.AddedKeys == 0
                            ? L.F("Cfg.Notify.Merged.None", fileName)
                            : L.F("Cfg.Notify.Merged.Body", fileName, merged.AddedKeys),
                        merged.AddedKeys == 0 ? NotifySeverity.Informational : NotifySeverity.Success);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Notify(L.Get("Cfg.Notify.ImportFail"), ex.Message, NotifySeverity.Error);
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
                StateText.Text = L.Get("Cfg.State.Editing");
                DirtyHint.Text = L.Get("Cfg.DirtyHint.Editing");
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

            // 记下时间：换语言时这句要按新语言重拼，不能把「已保存 · 14:32」丢掉
            _savedAt = DateTime.Now;
            RefreshSaveState();
        }

        /// <summary>保存状态那两行按当前状态重写：改完、保存完、换语言都要走这里。</summary>
        private void RefreshSaveState()
        {
            if (StateText is null)
            {
                return;
            }

            if (_dirty)
            {
                StateText.Text = L.Get("Cfg.State.Editing");
                DirtyHint.Text = L.Get("Cfg.DirtyHint.Editing");
                return;
            }

            StateText.Text = L.Get("Cfg.State.Saved");
            DirtyHint.Text = _savedAt is { } at
                ? L.F("Cfg.DirtyHint.Saved", at.ToString("HH:mm"))
                : string.Empty;
        }

        /// <summary>页面播报统一走全局消息（窗口右下角），页面里不再挂 InfoBar。</summary>
        private static void Notify(string title, string message, NotifySeverity severity)
            => AppNotify.Post(severity, title, message);
    }
}
