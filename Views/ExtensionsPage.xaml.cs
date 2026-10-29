using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
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

        public string Name { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        /// <summary>最低酒馆版本，例如 1.12.0；为空表示不限制。</summary>
        public string MinClient { get; set; } = string.Empty;

        public string Author { get; set; } = string.Empty;

        /// <summary>third-party 下的目录名。</summary>
        public string Folder { get; set; } = string.Empty;

        /// <summary>目录提示：行内不再单独占一列，鼠标停到图标座上显示。</summary>
        public string FolderTip => string.IsNullOrEmpty(Folder) ? string.Empty : "third-party\\" + Folder;

        public string Glyph { get; set; } = "\uE8F1";

        public bool IsSystem { get; set; }

        public bool HasUpdate { get; set; }

        public bool Broken { get; set; }

        public bool GitRepairable { get; set; }

        public bool HasHomepage { get; set; } = true;

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

        public string MinClientLabel => string.IsNullOrEmpty(MinClient) ? string.Empty : "ST ≥ " + MinClient;

        public string AuthorLabel => string.IsNullOrEmpty(Author) ? "未署名" : Author;

        public Visibility TitleOnVisibility => Show(Enabled);

        public Visibility TitleOffVisibility => Show(!Enabled);

        public Visibility EnabledLabelVisibility => Show(Enabled && !IsSystem);

        public Visibility DisabledLabelVisibility => Show(!Enabled);

        public Visibility MinClientVisibility => Show(!string.IsNullOrEmpty(MinClient));

        public Visibility SystemVisibility => Show(IsSystem);

        public Visibility UpdateVisibility => Show(HasUpdate && !IsSystem);

        public Visibility BrokenVisibility => Show(Broken);

        public Visibility HomepageVisibility => Show(HasHomepage && !IsSystem);

        public Visibility RepairVisibility => Show(GitRepairable);

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
    /// 扩展管理：绑定到一个酒馆实例的 third-party 目录，管理启用、更新与删除。
    /// 页面分三层——页头（全局状态与安装入口）、目标实例条（这批扩展装在哪）、
    /// 已安装列表（每行一个扩展，行内完成开关与清理）。
    /// </summary>
    public sealed partial class ExtensionsPage : Page
    {
        private const string TargetPath =
            @"C:\Users\al01\AppData\Roaming\AstraBrew Launcher\sillytavern\public\scripts\extensions\third-party";

        private readonly List<ExtensionRow> _all = new();

        private string _query = string.Empty;
        private bool _showSystem;
        private bool _busy;

        public ExtensionsPage()
        {
            InitializeComponent();
            Seed();
            Loaded += (_, _) => RefreshRows();
        }

        /// <summary>列表数据源；筛选后整表重建。</summary>
        public ObservableCollection<ExtensionRow> Rows { get; } = new();

        // ==================== 数据 ====================

        private void Seed()
        {
            _all.AddRange(new[]
            {
                new ExtensionRow("#8E44DC")
                {
                    Name = "长期记忆",
                    Version = "2.1.0",
                    Author = "星酿工坊",
                    Folder = "long-term-memory",
                    HasUpdate = true,
                    Glyph = "\uE8F1",
                },
                new ExtensionRow("#2B88D8")
                {
                    Name = "语音合成",
                    Version = "1.0.4",
                    MinClient = "1.12.0",
                    Author = "月见",
                    Folder = "speech-synthesis",
                    Glyph = "\uE767",
                },
                new ExtensionRow("#12A5A5")
                {
                    Name = "变量脚本",
                    Version = "0.9.2",
                    Author = "白露",
                    Folder = "var-scripts",
                    Enabled = false,
                    Glyph = "\uE943",
                },
                new ExtensionRow("#C08A00")
                {
                    Name = "界面主题包",
                    Version = "3.2.0",
                    Author = "星野",
                    Folder = "theme-pack",
                    Glyph = "\uE790",
                },
                new ExtensionRow("#8E44DC")
                {
                    Name = "快捷回复",
                    Version = "1.4.1",
                    MinClient = "1.12.0",
                    Author = "空青",
                    Folder = "quick-reply",
                    Glyph = "\uE8AC",
                },
                new ExtensionRow("#2B88D8")
                {
                    Name = "正则助手",
                    Version = "0.6.0",
                    Author = "无衣",
                    Folder = "regex-helper",
                    Enabled = false,
                    Broken = true,
                    Glyph = "\uE943",
                },
                new ExtensionRow("#12A5A5")
                {
                    Name = "翻译桥",
                    Version = "2.0.3",
                    Author = "星酿工坊",
                    Folder = "translate-bridge",
                    Glyph = "\uE774",
                },
                new ExtensionRow("#C08A00")
                {
                    Name = "角色卡导出",
                    Version = "1.1.0",
                    Author = "拾光",
                    Folder = "card-export",
                    Enabled = false,
                    GitRepairable = true,
                    HasHomepage = false,
                    Glyph = "\uE8B5",
                },
                new ExtensionRow("#8E44DC")
                {
                    Name = "群聊增强",
                    Version = "0.4.2",
                    Author = "空青",
                    Folder = "group-enhance",
                    Glyph = "\uE902",
                },
                new ExtensionRow("#8A8A8A")
                {
                    Name = "扩展管理器",
                    Version = "1.0.0",
                    Author = "SillyTavern",
                    Folder = "extensions-manager",
                    IsSystem = true,
                    HasHomepage = false,
                    Glyph = "\uE713",
                },
                new ExtensionRow("#8A8A8A")
                {
                    Name = "向量存储",
                    Version = "1.2.0",
                    Author = "SillyTavern",
                    Folder = "vector-storage",
                    IsSystem = true,
                    HasHomepage = false,
                    Glyph = "\uE9F5",
                },
            });
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
                EmptyTitleText.Text = _all.Count == 0 ? "这个实例还没有扩展" : "没有匹配的扩展";
                EmptyHintText.Text = _all.Count == 0
                    ? "点右上角「安装扩展」，从 Git 仓库或离线包装一个。"
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
            var scope = _all.Where(row => _showSystem || !row.IsSystem).ToList();
            int total = scope.Count;
            int enabled = scope.Count(row => row.Enabled);
            int pending = scope.Count(row => row.HasUpdate);
            int disabled = total - enabled;

            HeaderSummaryText.Text = $"已启用 {enabled} / {total}";

            string summary = $"共 {total} 个 · {enabled} 个已启用";
            if (pending > 0)
            {
                summary += $" · {pending} 个待更新";
            }

            ListSummaryText.Text = summary;
            FilterHintText.Text = disabled > 0 ? $"{disabled} 个已停用" : "全部启用";
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
            if (row.Enabled == toggle.IsOn)
            {
                return;
            }

            row.Enabled = toggle.IsOn;
            UpdateSummary();
            ShowBar(
                toggle.IsOn ? "已启用扩展" : "已停用扩展",
                toggle.IsOn ? $"{row.Name} 会在下次酒馆启动时加载。" : $"{row.Name} 的配置保留，下次启动不再加载。",
                InfoBarSeverity.Informational);
        }

        private async void RowDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: ExtensionRow row })
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
                    Text = $"将删除 third-party\\{row.Folder} 整个目录，扩展配置一并移除，且无法撤销。",
                },
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };

            if (Application.Current.Resources.TryGetValue("DangerDialogButtonStyle", out object dangerStyle)
                && dangerStyle is Style style)
            {
                dialog.PrimaryButtonStyle = style;
            }

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            _all.Remove(row);
            RefreshRows();
            ShowBar("已删除扩展", $"{row.Name} 及其目录已被移除。", InfoBarSeverity.Success);
        }

        private void RowHomepage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is HyperlinkButton { DataContext: ExtensionRow row })
            {
                ShowBar("扩展主页", $"{row.Name} 的主页会在浏览器中打开。", InfoBarSeverity.Informational);
            }
        }

        private void RowOpenDir_Click(object sender, RoutedEventArgs e)
        {
            if (sender is HyperlinkButton { DataContext: ExtensionRow row })
            {
                ShowBar("扩展目录", System.IO.Path.Combine(TargetPath, row.Folder), InfoBarSeverity.Informational);
            }
        }

        private void RowRepairGit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not HyperlinkButton { DataContext: ExtensionRow row })
            {
                return;
            }

            row.GitRepairable = false;
            row.Broken = false;
            RefreshRows();
            ShowBar("Git 仓库已修复", $"{row.Name} 的远端地址已重新写入，可以正常更新。", InfoBarSeverity.Success);
        }

        // ==================== 页级动作 ====================

        private async void Install_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ExtensionInstallDialog { XamlRoot = XamlRoot };
            dialog.Installed += (_, _) =>
            {
                string name = string.IsNullOrWhiteSpace(dialog.InstalledName) ? "新扩展" : dialog.InstalledName;
                _all.Insert(0, new ExtensionRow("#8E44DC")
                {
                    Name = name,
                    Version = "1.0.0",
                    Author = "本地安装",
                    Folder = name,
                    HasUpdate = false,
                    HasHomepage = false,
                    Glyph = "\uE8F1",
                });
                _query = string.Empty;
                SearchBox.Text = string.Empty;
                RefreshRows();
                ShowBar("安装完成", $"{name} 已启用，出现在列表最上方。", InfoBarSeverity.Success);
            };

            await dialog.ShowAsync();
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

            await Task.Delay(650);

            _busy = false;
            if (sender is Button done)
            {
                done.IsEnabled = true;
            }

            RefreshRows();
            ShowBar("已重新扫描", $"third-party 下共发现 {_all.Count(row => !row.IsSystem)} 个扩展。", InfoBarSeverity.Informational);
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

            await Task.Delay(900);

            _busy = false;
            if (sender is Button done)
            {
                done.IsEnabled = true;
            }

            foreach (var row in _all.Where(row => !row.IsSystem && !row.Broken).Take(1))
            {
                row.HasUpdate = true;
            }

            RefreshRows();
            int pending = _all.Count(row => row.HasUpdate && !row.IsSystem);
            ShowBar(
                pending > 0 ? "检查完成" : "已是最新",
                pending > 0 ? $"{pending} 个扩展有新版本，可逐个更新。" : "所有扩展都已是最新版本。",
                InfoBarSeverity.Informational);
        }

        private void CopyTargetPath_Click(object sender, RoutedEventArgs e)
        {
            var package = new DataPackage();
            package.SetText(TargetPath);
            Clipboard.SetContent(package);
            ShowBar("路径已复制", TargetPath, InfoBarSeverity.Success);
        }

        private void OpenRoot_Click(object sender, RoutedEventArgs e)
        {
            ShowBar("扩展目录", TargetPath, InfoBarSeverity.Informational);
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
