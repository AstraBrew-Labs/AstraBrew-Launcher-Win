using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AstraBrew_Launcher.Services;
using AstraBrew_Launcher.Views.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace AstraBrew_Launcher.Views
{
    /// <summary>
    /// 扩展列表的行视图数据。
    /// 每行背后都有一份真实目录（<see cref="Source"/>），开关状态来自酒馆的 settings.json。
    /// 行的增删与筛选由页面整表重建，只有启用开关会就地改状态，
    /// 因此与开关相关的展示属性都走变更通知（XAML 里绑定为 OneWay）。
    /// </summary>
    public sealed class ExtensionRow : INotifyPropertyChanged
    {
        private readonly SolidColorBrush _tileBackground;
        private readonly SolidColorBrush _tileForeground;
        private bool _enabled = true;

        public ExtensionRow(string accent)
        {
            Color color = ParseColor(accent);
            _tileForeground = new SolidColorBrush(color);
            _tileBackground = new SolidColorBrush(Color.FromArgb(0x24, color.R, color.G, color.B));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>这行指向的磁盘实体。</summary>
        public TavernExtension Source { get; set; } = new();

        public string Name { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        /// <summary>manifest 里声明的依赖扩展；为空表示不依赖别的扩展。</summary>
        public string RequiresLabel { get; set; } = string.Empty;

        public string Author { get; set; } = string.Empty;

        /// <summary>third-party 下的目录名。</summary>
        public string Folder { get; set; } = string.Empty;

        /// <summary>扩展目录的完整路径，鼠标停到图标座上显示。</summary>
        public string FolderTip { get; set; } = string.Empty;

        public string HomePage { get; set; } = string.Empty;

        public string Glyph { get; set; } = "\uE8F1";

        /// <summary>酒馆自带（不可删除、不可开关）。</summary>
        public bool IsSystem { get; set; }

        public bool HasUpdate { get; set; }

        public bool Broken { get; set; }

        public bool HasHomepage => !string.IsNullOrEmpty(HomePage);

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value)
                {
                    return;
                }

                _enabled = value;
                Raise(nameof(Enabled));
                Raise(nameof(TitleOnVisibility));
                Raise(nameof(TitleOffVisibility));
                Raise(nameof(EnabledLabelVisibility));
                Raise(nameof(DisabledLabelVisibility));
            }
        }

        public SolidColorBrush TileBackground => _tileBackground;

        public SolidColorBrush TileForeground => _tileForeground;

        public string VersionLabel => string.IsNullOrEmpty(Version) ? "—" : "v" + Version;

        public string AuthorLabel => string.IsNullOrEmpty(Author) ? "未署名" : Author;

        public Visibility TitleOnVisibility => Show(Enabled);

        public Visibility TitleOffVisibility => Show(!Enabled);

        public Visibility EnabledLabelVisibility => Show(Enabled && !IsSystem);

        public Visibility DisabledLabelVisibility => Show(!Enabled);

        public Visibility RequiresVisibility => Show(!string.IsNullOrEmpty(RequiresLabel));

        public Visibility SystemVisibility => Show(IsSystem);

        public Visibility UpdateVisibility => Show(HasUpdate && !IsSystem);

        public Visibility BrokenVisibility => Show(Broken);

        public Visibility HomepageVisibility => Show(HasHomepage && !IsSystem);

        public Visibility SwitchVisibility => Show(!IsSystem);

        public Visibility SystemNoteVisibility => Show(IsSystem);

        public Visibility DeleteVisibility => Show(!IsSystem);

        /// <summary>系统扩展不可开关，行内只保留说明。</summary>
        private static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

        private static Color ParseColor(string hex)
        {
            string value = hex.TrimStart('#');
            byte r = Convert.ToByte(value[..2], 16);
            byte g = Convert.ToByte(value.Substring(2, 2), 16);
            byte b = Convert.ToByte(value.Substring(4, 2), 16);
            return Color.FromArgb(0xFF, r, g, b);
        }

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// 扩展管理：管当前实例的 third-party 目录，以及酒馆自带的那批扩展。
    ///
    /// 页面上每个数字都对得上磁盘：列表来自真实目录，开关写回
    /// <c>default-user/settings.json</c> 的 <c>disabledExtensions</c>，
    /// 删除走回收站，检查更新真的去比对远端 HEAD。切换实例时整页跟着换目标。
    /// </summary>
    public sealed partial class ExtensionsPage : Page
    {
        private readonly List<ExtensionRow> _all = new();

        private TavernInstance _instance = TavernInstances.Current;
        private string _query = string.Empty;
        private bool _showSystem;
        private bool _busy;
        private bool _hooked;

        public ExtensionsPage()
        {
            InitializeComponent();

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        /// <summary>列表数据源；筛选后整表重建。</summary>
        public ObservableCollection<ExtensionRow> Rows { get; } = new();

        private string TargetRoot => TavernExtensions.ThirdPartyRoot(_instance);

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_hooked)
            {
                TavernInstances.Changed += OnInstancesChanged;
                TavernRuntime.Changed += OnRuntimeChanged;
                _hooked = true;
            }

            UpdateTargetChrome();
            _ = ReloadAsync();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (!_hooked)
            {
                return;
            }

            TavernInstances.Changed -= OnInstancesChanged;
            TavernRuntime.Changed -= OnRuntimeChanged;
            _hooked = false;
        }

        private void OnInstancesChanged(object? sender, EventArgs e)
        {
            if (ReferenceEquals(_instance, TavernInstances.Current))
            {
                return;
            }

            UpdateTargetChrome();
            _ = ReloadAsync();
        }

        /// <summary>酒馆启停不影响扩展目录，但会影响「改了会不会被覆盖」的提示，所以只刷新说明。</summary>
        private void OnRuntimeChanged(object? sender, EventArgs e) => UpdateTargetChrome();

        // ==================== 数据 ====================

        private void UpdateTargetChrome()
        {
            TavernInstance instance = TavernInstances.Current;
            _instance = instance;

            TargetPathText.Text = TavernExtensions.ThirdPartyRoot(instance);
            TargetInstanceChipText.Text = $"{instance.KindLabel} · {instance.KernelLabel}";
        }

        /// <summary>重扫磁盘并整表重建。扫描在后台线程跑，几十个目录也不至于卡一下。</summary>
        private async Task ReloadAsync()
        {
            TavernInstance instance = _instance;
            IReadOnlyList<TavernExtension> items = await Task.Run(() => TavernExtensions.Load(instance));

            // 扫描期间用户可能又换了实例，这批结果就过期了
            if (!ReferenceEquals(instance, TavernInstances.Current))
            {
                return;
            }

            _all.Clear();
            foreach (TavernExtension item in items)
            {
                _all.Add(ToRow(item));
            }

            RefreshRows();
        }

        private static ExtensionRow ToRow(TavernExtension item)
        {
            var row = new ExtensionRow(item.IsThirdParty ? "#8E44DC" : "#8A8A8A")
            {
                Source = item,
                Name = item.DisplayName,
                Version = item.Version,
                Author = item.Author,
                Folder = item.Folder,
                FolderTip = item.Path,
                HomePage = item.HomePage,
                Glyph = item.IsThirdParty ? "\uE8F1" : "\uE713",
                IsSystem = !item.IsThirdParty,
                Broken = item.Broken,
                HasUpdate = item.HasUpdate,
                Enabled = item.Enabled,
                RequiresLabel = item.Requires.Count == 0
                    ? string.Empty
                    : "依赖 " + string.Join("、", item.Requires),
            };

            return row;
        }

        // ==================== 列表 ====================

        private void RefreshRows()
        {
            var visible = _all
                .Where(row => _showSystem || !row.IsSystem)
                .Where(Matches)
                .ToList();

            Rows.Clear();
            foreach (var row in visible)
            {
                Rows.Add(row);
            }

            bool empty = visible.Count == 0;
            ExtensionList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            EmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

            if (empty)
            {
                bool nothingAtAll = _all.Count == 0;
                bool onlySystem = !nothingAtAll && _all.All(row => row.IsSystem);

                EmptyTitleText.Text = nothingAtAll
                    ? "这个实例还没有扩展"
                    : onlySystem
                        ? "这里只有酒馆自带的扩展"
                        : "没有匹配的扩展";

                EmptyHintText.Text = nothingAtAll
                    ? "点右上角「安装扩展」，从 Git 仓库或离线包装一个。"
                    : onlySystem
                        ? $"自带的 {_all.Count} 个扩展不在 third-party 里，打开右上角「显示系统扩展」就能看到。"
                        : "换个关键词，或清空搜索框后重试。";
            }

            UpdateSummary();
        }

        private bool Matches(ExtensionRow row)
        {
            if (string.IsNullOrEmpty(_query))
            {
                return true;
            }

            return row.Name.Contains(_query, StringComparison.OrdinalIgnoreCase)
                || row.Author.Contains(_query, StringComparison.OrdinalIgnoreCase)
                || row.Folder.Contains(_query, StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateSummary()
        {
            // 顶部的「已启用 x / y」只算第三方：自带扩展不受这里控制，算进去分母会骗人
            var scope = _all.Where(row => _showSystem || !row.IsSystem).ToList();
            var controllable = _all.Where(row => !row.IsSystem).ToList();
            int total = scope.Count;
            int enabled = controllable.Count(row => row.Enabled);
            int pending = _all.Count(row => row.HasUpdate && !row.IsSystem);

            HeaderSummaryText.Text = $"已启用 {enabled} / {controllable.Count}";

            string summary = $"共 {total} 个 · 第三方 {controllable.Count} 个 · {enabled} 个已启用";
            if (pending > 0)
            {
                summary += $" · {pending} 个待更新";
            }

            ListSummaryText.Text = summary;
            // 右侧按钮已经写着「全部停用 / 全部启用」，这里只说停用数量，免得两个词并排重复
            int disabled = controllable.Count - enabled;
            FilterHintText.Text = disabled > 0 ? $"{disabled} 个已停用" : string.Empty;

            // 批量开关的方向由「现在有没有开着的」决定：有开着的就给「全部停用」
            // 数据没到之前不要先把按钮亮出来：XAML 里的默认文字会是一个错误的方向
            if (ToggleAllText is not null)
            {
                ToggleAllText.Text = enabled > 0 ? "全部停用" : "全部启用";
                ToggleAllButton.IsEnabled = controllable.Count > 0;
                ToggleAllButton.Visibility = controllable.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            // 侧栏角标跟着真实待更新数走，没有就整颗隐藏
            App.MainWindow?.SetExtensionsBadge(pending);
        }

        private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            {
                return;
            }

            _query = sender.Text.Trim();
            RefreshRows();
        }

        private void SystemToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (ExtensionList is null)
            {
                return;
            }

            _showSystem = SystemToggle.IsOn;
            RefreshRows();
        }

        // ==================== 行内动作 ====================

        private void RowToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleSwitch { DataContext: ExtensionRow row } toggle || _busy)
            {
                return;
            }

            // 模板实例化时 IsOn 由绑定写入也会触发 Toggled，值没变就当作噪音丢掉。
            if (row.Enabled == toggle.IsOn || row.IsSystem)
            {
                return;
            }

            bool target = toggle.IsOn;

            if (!TavernExtensions.SetEnabled(row.Source, target, out string? error))
            {
                // 写不进去就把开关拨回去，别让界面显示一个磁盘上不存在的状态
                _busy = true;
                toggle.IsOn = row.Enabled;
                _busy = false;

                Notify("没能保存扩展开关", error ?? "settings.json 写入失败。", NotifySeverity.Error);
                return;
            }

            row.Enabled = target;
            UpdateSummary();

            string title = target ? "已启用扩展" : "已停用扩展";
            string message = target
                ? $"{row.Name} 会在下次酒馆启动时加载。"
                : $"{row.Name} 的目录保留，下次启动不再加载。";

            if (TavernRuntime.IsRunning)
            {
                message += " 酒馆正在运行，它保存设置时会覆盖这次改动，建议重启后确认。";
            }

            Notify(title, message, NotifySeverity.Informational);
        }

        private async void RowDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: ExtensionRow row } || _busy)
            {
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "删除扩展",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"将删除整个目录：\n{row.FolderTip}\n\n目录会先进回收站，但扩展自己的配置不保留。",
                },
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };

            // DefaultButton 会被模板套上框架 AccentButtonStyle，圆角取自 ControlCornerRadius（4）。
            // 在弹窗作用域里把它收到 8，让「取消」这颗强调按钮跟页面按钮同一个圆角。
            dialog.Resources["ControlCornerRadius"] = new CornerRadius(8);

            if (Application.Current.Resources.TryGetValue("DangerDialogButtonStyle", out object dangerStyle)
                && dangerStyle is Style style)
            {
                dialog.PrimaryButtonStyle = style;
            }

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            _busy = true;
            (bool ok, string? error) = await TavernExtensions.DeleteAsync(row.Source);
            _busy = false;

            if (!ok)
            {
                Notify("没能删除扩展", error ?? "目录可能正被酒馆占用，先停掉服务再试。", NotifySeverity.Error);
                return;
            }

            _all.Remove(row);
            RefreshRows();
            Notify("已删除扩展", $"{row.Name} 已移入回收站。", NotifySeverity.Success);
        }

        private void RowHomepage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not HyperlinkButton { DataContext: ExtensionRow row } || !row.HasHomepage)
            {
                return;
            }

            if (BrowserLauncher.Open(row.HomePage, TavernSettings.Browser, out string detail))
            {
                Notify("已在浏览器中打开", $"{row.Name} · {detail}", NotifySeverity.Informational);
            }
            else
            {
                Notify("打不开主页", detail, NotifySeverity.Warning);
            }
        }

        private void RowOpenDir_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not HyperlinkButton { DataContext: ExtensionRow row })
            {
                return;
            }

            if (!Directory.Exists(row.FolderTip))
            {
                Notify("目录已不存在", row.FolderTip, NotifySeverity.Warning);
                _ = ReloadAsync();
                return;
            }

            ShellFile.Reveal(row.FolderTip);
        }

        // ==================== 页级动作 ====================

        private async void Install_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ExtensionInstallDialog
            {
                XamlRoot = XamlRoot,
                Instance = _instance,
            };

            dialog.Installed += (_, _) =>
            {
                _query = string.Empty;
                SearchBox.Text = string.Empty;
                _ = ReloadAsync();
            };

            await dialog.ShowAsync();
        }

        /// <summary>
        /// 一键开关所有第三方扩展。写法是「一次算好、一次落盘」——
        /// 逐个调 SetEnabled 会把 settings.json 整份重写 N 次，中途失败还会留下半套状态。
        /// </summary>
        private void ToggleAll_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                return;
            }

            List<ExtensionRow> controllable = _all.Where(row => !row.IsSystem).ToList();

            if (controllable.Count == 0)
            {
                Notify("没有可切换的扩展", "自带扩展不受启动器控制，只能开关第三方扩展。", NotifySeverity.Informational);
                return;
            }

            // 只要还有开着的，就整体停用；全停了才反过来
            bool disable = controllable.Any(row => row.Enabled);
            List<(TavernExtension Extension, bool Enabled)> changes = controllable
                .Select(row => (row.Source, !disable))
                .ToList();

            if (!TavernExtensions.SetEnabledMany(changes, out string? error))
            {
                Notify("没能保存扩展开关", error ?? "settings.json 写入失败。", NotifySeverity.Error);
                return;
            }

            // 行里的 Enabled 是加载时抄下来的一份副本，写盘成功要同步回来，
            // 否则开关与「已启用 x/y」会停在旧状态（批量接口改的是 TavernExtension）
            foreach (ExtensionRow row in controllable)
            {
                row.Enabled = !disable;
            }

            RefreshRows();
            UpdateSummary();

            string message = disable
                ? $"{controllable.Count} 个第三方扩展已全部停用：目录保留，下次启动不再加载。"
                : $"{controllable.Count} 个第三方扩展已全部启用，下次启动生效。";

            if (TavernRuntime.IsRunning)
            {
                message += " 酒馆正在运行，它保存设置时会覆盖这次改动，建议重启后确认。";
            }

            Notify(disable ? "已全部停用" : "已全部启用", message, NotifySeverity.Informational);
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                return;
            }

            _busy = true;
            if (sender is Button button)
            {
                button.IsEnabled = false;
            }

            await ReloadAsync();

            _busy = false;
            if (sender is Button done)
            {
                done.IsEnabled = true;
            }

            int thirdParty = _all.Count(row => !row.IsSystem);
            Notify("已重新扫描", $"{TargetRoot} 下共 {thirdParty} 个第三方扩展。", NotifySeverity.Informational);
        }

        private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                return;
            }

            _busy = true;
            if (sender is Button button)
            {
                button.IsEnabled = false;
            }

            var targets = _all.Select(row => row.Source).ToList();
            (int checkedCount, int pending, string? error) = await TavernExtensions.CheckUpdatesAsync(targets);

            _busy = false;
            if (sender is Button done)
            {
                done.IsEnabled = true;
            }

            if (error is not null)
            {
                Notify("检查更新失败", error, NotifySeverity.Error);
                return;
            }

            if (checkedCount == 0)
            {
                Notify(
                    "没有可检查的扩展",
                    "只有用 Git 克隆的扩展才能比对远端版本；离线包装的需要重新下载新版本。",
                    NotifySeverity.Informational);
                return;
            }

            await ReloadAsync();

            Notify(
                pending > 0 ? "发现新版本" : "已是最新",
                pending > 0
                    ? $"{checkedCount} 个 Git 扩展里 {pending} 个的远端有新提交。"
                    : $"{checkedCount} 个 Git 扩展都与远端一致。",
                pending > 0 ? NotifySeverity.Warning : NotifySeverity.Informational);
        }

        private void CopyTargetPath_Click(object sender, RoutedEventArgs e)
        {
            var package = new DataPackage();
            package.SetText(TargetRoot);
            Clipboard.SetContent(package);
            Notify("路径已复制", TargetRoot, NotifySeverity.Success);
        }

        private void OpenRoot_Click(object sender, RoutedEventArgs e)
        {
            string root = TargetRoot;

            if (!Directory.Exists(root))
            {
                Notify("目录还不存在", $"{root} 会在安装第一个扩展时创建。", NotifySeverity.Warning);
                return;
            }

            ShellFile.Reveal(root);
        }

        /// <summary>页面播报统一走全局消息（窗口右下角），页面里不再挂 InfoBar。</summary>
        private static void Notify(string title, string message, NotifySeverity severity)
            => AppNotify.Post(severity, title, message);
    }
}
