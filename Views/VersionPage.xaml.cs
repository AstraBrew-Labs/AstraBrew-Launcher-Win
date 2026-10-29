using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using AstraBrew_Launcher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace AstraBrew_Launcher.Views
{
    /// <summary>
    /// 本地实例列表的行视图数据。
    /// 列表状态变化时整表重建（<see cref="VersionPage.Rows"/> 清空后重填），
    /// 因此这里只暴露只读派生属性，不需要实现变更通知。
    /// </summary>
    public sealed class LocalInstanceRow
    {
        public string Version { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public string NodeText { get; set; } = string.Empty;

        public bool IsCurrent { get; set; }

        public bool DepsReady { get; set; } = true;

        public string VersionLabel => "v" + Version;

        public string UseText => IsCurrent ? "使用中" : "使用";

        public bool UseEnabled => !IsCurrent && DepsReady;

        public Visibility CurrentVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;

        public Visibility ReadyVisibility => DepsReady ? Visibility.Visible : Visibility.Collapsed;

        public Visibility MissingVisibility => DepsReady ? Visibility.Collapsed : Visibility.Visible;

        public Visibility FixVisibility => DepsReady ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>缺少依赖时，「使用」按钮让位给「安装依赖」，避免两个动作并列。</summary>
        public Visibility UseVisibility => DepsReady ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 版本管理：两个独立页签。
    /// 「本地实例」是列表——用户已有的多个酒馆目录可以并存；
    /// 「内置实例」是单张管理卡——启动器只托管一份代码，支持切换版本但不支持版本共存。
    /// </summary>
    public sealed partial class VersionPage : Page
    {
        private const string LatestStable = "1.19.2";

        /// <summary>可在内置实例中切换的稳定版；索引 0 恒为最新。</summary>
        private static readonly (string Version, string Date, string Size, string Files)[] StableReleases =
        {
            ("1.19.2", "2026-10-20", "186 MB", "1,284 个文件"),
            ("1.19.0", "2026-09-28", "184 MB", "1,271 个文件"),
            ("1.18.0", "2026-08-30", "181 MB", "1,246 个文件"),
            ("1.17.2", "2026-07-18", "178 MB", "1,208 个文件"),
        };

        /// <summary>本地实例的可变状态；列表行由它派生，交互只改这里。</summary>
        private sealed class LocalInstanceData
        {
            public string Version = string.Empty;
            public string FolderPath = string.Empty;
            public string Node = string.Empty;
            public bool IsCurrent;
            public bool DepsReady = true;
        }

        private readonly List<LocalInstanceData> _localData = new()
        {
            new LocalInstanceData
            {
                Version = "1.19.0",
                FolderPath = @"C:\Users\al01\Documents\SillyTavern-1.19.0",
                Node = "Node 20.11.0",
            },
            new LocalInstanceData
            {
                Version = "1.18.0",
                FolderPath = @"D:\AstraBrew\tavern-1.18.0",
                Node = "Node 20.11.0",
            },
            new LocalInstanceData
            {
                Version = "1.17.2",
                FolderPath = @"C:\Users\al01\Documents\SillyTavern-legacy",
                Node = "Node 18.20.4",
                DepsReady = false,
            },
        };

        private readonly DispatcherTimer _scanTimer = new();
        private readonly DispatcherTimer _switchTimer = new();

        private string _builtinVersion = LatestStable;
        private bool _currentSourceIsLocal;
        private string _currentLocalVersion = string.Empty;

        private bool _syncingPicker;
        private bool _switching;
        private bool _secondPhase;
        private double _switchProgress;
        private string _switchTarget = string.Empty;

        public VersionPage()
        {
            InitializeComponent();

            _scanTimer.Interval = TimeSpan.FromMilliseconds(2200);
            _scanTimer.Tick += ScanTimer_Tick;

            _switchTimer.Interval = TimeSpan.FromMilliseconds(40);
            _switchTimer.Tick += SwitchTimer_Tick;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        /// <summary>本地实例列表的数据源。</summary>
        public ObservableCollection<LocalInstanceRow> Rows { get; } = new();

        private bool IsStaging => StagingChip is not null && StagingChip.IsChecked == true;

        // ==================== 生命周期 ====================

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplyTab();
            RefreshLocalRows();
            ApplyBuiltinCurrent();
            ReloadVersions();
            UpdateHeaderCurrent();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _scanTimer.Stop();
            _switchTimer.Stop();
        }

        // ==================== 页签 ====================

        private void TabStrip_Checked(object sender, RoutedEventArgs e)
        {
            // 初始化期间 Checked 早于另一个 Tab 构造完成，这里直接忽略。
            if (LocalPanel is null || BuiltinPanel is null)
            {
                return;
            }

            ApplyTab();
        }

        private void ApplyTab()
        {
            bool local = LocalTab.IsChecked == true;

            LocalPanel.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
            BuiltinPanel.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateHeaderCurrent()
        {
            if (HeaderCurrentText is null)
            {
                return;
            }

            HeaderCurrentText.Text = _currentSourceIsLocal
                ? $"当前 · 本地 v{_currentLocalVersion}"
                : $"当前 · 内置 {DisplayVersion(_builtinVersion)}";

            // 关于页标题旁的实例徽章读同一份状态，两处永远一致
            if (_currentSourceIsLocal && _currentLocalVersion.Length > 0)
            {
                ActiveInstance.Set(TavernInstanceKind.Local, _currentLocalVersion);
            }
            else
            {
                ActiveInstance.Set(
                    TavernInstanceKind.Builtin,
                    _builtinVersion == "staging" ? "开发版" : _builtinVersion);
            }
        }

        // ==================== 本地实例：列表 ====================

        private void RefreshLocalRows()
        {
            Rows.Clear();

            foreach (var data in _localData)
            {
                Rows.Add(new LocalInstanceRow
                {
                    Version = data.Version,
                    Path = data.FolderPath,
                    NodeText = data.Node,
                    IsCurrent = data.IsCurrent,
                    DepsReady = data.DepsReady,
                });
            }

            int missing = _localData.Count(item => !item.DepsReady);

            LocalTabCount.Text = _localData.Count.ToString();
            LocalSummaryText.Text = _localData.Count == 0
                ? "尚未扫描到实例"
                : missing == 0
                    ? $"已发现 {_localData.Count} 个实例 · 依赖全部就绪"
                    : $"已发现 {_localData.Count} 个实例 · {missing} 个缺少依赖";

            LocalList.Visibility = _localData.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            LocalEmpty.Visibility = _localData.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void LocalUse_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: LocalInstanceRow row })
            {
                return;
            }

            var target = _localData.FirstOrDefault(item => item.Version == row.Version);
            if (target is null || !target.DepsReady)
            {
                return;
            }

            foreach (var item in _localData)
            {
                item.IsCurrent = ReferenceEquals(item, target);
            }

            _currentSourceIsLocal = true;
            _currentLocalVersion = target.Version;

            RefreshLocalRows();
            UpdateHeaderCurrent();

            ShowBar(
                "已切换实例",
                $"后续启动将使用本地 v{target.Version}，目录：{target.FolderPath}",
                InfoBarSeverity.Success);
        }

        private void LocalFixDeps_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: LocalInstanceRow row })
            {
                return;
            }

            var target = _localData.FirstOrDefault(item => item.Version == row.Version);
            if (target is null)
            {
                return;
            }

            target.DepsReady = true;
            RefreshLocalRows();

            ShowBar(
                "依赖已安装",
                $"已为 v{target.Version} 安装 Node 依赖（{target.Node} · npm 10.5.2），现在可以切换使用。",
                InfoBarSeverity.Success);
        }

        private async void LocalRemove_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: LocalInstanceRow row })
            {
                return;
            }

            var target = _localData.FirstOrDefault(item => item.Version == row.Version);
            if (target is null)
            {
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"从列表移除 v{target.Version}？",
                Content = "只从启动器列表中移除，不会删除磁盘上的目录。",
                PrimaryButtonText = "移除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            _localData.Remove(target);

            if (target.IsCurrent)
            {
                _currentSourceIsLocal = false;
                UpdateHeaderCurrent();
            }

            RefreshLocalRows();

            ShowBar(
                "已移除实例",
                $"v{target.Version} 不再出现在列表中，目录 {target.FolderPath} 仍然保留。",
                InfoBarSeverity.Informational);
        }

        // ==================== 本地实例：扫描 / 导入 ====================

        private void Scan_Click(object sender, RoutedEventArgs e)
        {
            if (_scanTimer.IsEnabled)
            {
                return;
            }

            ScanPanel.Visibility = Visibility.Visible;
            ScanStatusText.Text = "正在扫描本地磁盘…";
            ScanPathText.Text = @"C:\Users\al01\Documents";
            ScanBar.IsIndeterminate = true;
            _scanTimer.Start();
        }

        private void ScanTimer_Tick(object? sender, object e)
        {
            _scanTimer.Stop();
            ScanPanel.Visibility = Visibility.Collapsed;

            ShowBar(
                "扫描完成",
                $"已检查 18,432 个文件夹，识别到 {_localData.Count} 个实例（跳过 2 个缺少 config.yaml 的目录）。",
                InfoBarSeverity.Informational);
        }

        private void CancelScan_Click(object sender, RoutedEventArgs e)
        {
            _scanTimer.Stop();
            ScanPanel.Visibility = Visibility.Collapsed;
            ShowBar("已取消扫描", "本次没有新增实例，已发现的列表保持不变。", InfoBarSeverity.Informational);
        }

        private void ScanLog_Click(object sender, RoutedEventArgs e)
        {
            ShowBar(
                "扫描日志",
                "2026-10-28 00:52 · 命中 3 个目录 · 跳过 2 个无 config.yaml 的目录 · 耗时 4.1 秒。",
                InfoBarSeverity.Informational);
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            ShowBar(
                "请选择酒馆目录",
                "选择包含 package.json 与 config.yaml 的目录，启动器会检查 Node 依赖后加入列表。",
                InfoBarSeverity.Informational);
        }

        // ==================== 内置实例：版本切换 ====================

        private void ReloadVersions()
        {
            if (VersionPicker is null || SwitchVersionBtn is null)
            {
                return;
            }

            _syncingPicker = true;
            VersionPicker.Items.Clear();

            if (IsStaging)
            {
                VersionPicker.Items.Add(new ComboBoxItem
                {
                    Content = "staging · 4c1f9ab（开发分支最新）",
                    Tag = "staging",
                });
            }
            else
            {
                foreach (var release in StableReleases)
                {
                    string label = $"v{release.Version} · {release.Date}";
                    if (release.Version == _builtinVersion)
                    {
                        // 当前版本不再重复标「最新」，避免「最新 · 当前」两个标签挤在一起。
                        label += " · 当前";
                    }
                    else if (release.Version == LatestStable)
                    {
                        label += " · 最新";
                    }

                    VersionPicker.Items.Add(new ComboBoxItem
                    {
                        Content = label,
                        Tag = release.Version,
                    });
                }
            }

            VersionPicker.SelectedIndex = 0;
            _syncingPicker = false;

            UpdateSwitchButton();
        }

        private void Channel_Checked(object sender, RoutedEventArgs e)
        {
            if (StagingNote is null || VersionPicker is null)
            {
                return;
            }

            StagingNote.IsOpen = IsStaging;

            // 开发版只有一个目标（staging 分支），选稳定版才需要列版本。
            ReloadVersions();
        }

        private void VersionPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingPicker)
            {
                return;
            }

            UpdateSwitchButton();
        }

        private void UpdateSwitchButton()
        {
            if (SwitchVersionBtn is null || SwitchHintText is null)
            {
                return;
            }

            string target = (VersionPicker.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
            bool same = target.Length > 0 && target == _builtinVersion;

            SwitchVersionBtn.IsEnabled = !_switching && target.Length > 0 && !same;
            SwitchVersionBtn.Content = _switching
                ? "正在切换…"
                : same
                    ? "已是当前版本"
                    : target.Length > 0
                        ? $"切换到 {DisplayVersion(target)}"
                        : "选择要切换的版本";

            SwitchHintText.Text = IsStaging
                ? "开发版跟随 staging 分支，切换同样覆盖当前代码。"
                : "切换会覆盖当前版本，旧版本不会保留。";
        }

        private void ApplyBuiltinCurrent()
        {
            if (BuiltinVersionText is null)
            {
                return;
            }

            if (_builtinVersion == "staging")
            {
                BuiltinVersionText.Text = "staging";
                MetaChannelText.Text = "开发版";
                MetaDateText.Text = "更新于 2026-10-27";
                MetaSizeText.Text = "192 MB";
                MetaFilesText.Text = "1,312 个文件";
                LatestPill.Visibility = Visibility.Collapsed;
                return;
            }

            BuiltinVersionText.Text = "v" + _builtinVersion;
            MetaChannelText.Text = "稳定版";

            int index = Array.FindIndex(StableReleases, item => item.Version == _builtinVersion);
            MetaDateText.Text = index >= 0 ? "发布于 " + StableReleases[index].Date : "发布日期未知";
            MetaSizeText.Text = index >= 0 ? StableReleases[index].Size : "—";
            MetaFilesText.Text = index >= 0 ? StableReleases[index].Files : "—";

            LatestPill.Visibility = _builtinVersion == LatestStable
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void SwitchVersion_Click(object sender, RoutedEventArgs e)
        {
            if (_switching)
            {
                return;
            }

            if (VersionPicker.SelectedItem is not ComboBoxItem { Tag: string target } || string.IsNullOrEmpty(target))
            {
                return;
            }

            if (target == _builtinVersion)
            {
                return;
            }

            _switchTarget = target;
            _switchProgress = 0;
            _secondPhase = false;
            _switching = true;

            SwitchLog.Text = string.Empty;
            SwitchBar.Value = 0;
            SwitchPercentText.Text = "0%";
            SwitchPhaseText.Text = $"第 1/2 步 · 正在下载 {DisplayVersion(target)} 源码";
            AppendSwitchLog($"$ git -C sillytavern fetch --depth 1 origin {target}");
            AppendSwitchLog($"正在从镜像下载 {DisplayVersion(target)}，已存在的旧版本目录将被覆盖…");

            SwitchProgressPanel.Visibility = Visibility.Visible;
            UpdateSwitchButton();
            _switchTimer.Start();
        }

        private void SwitchTimer_Tick(object? sender, object e)
        {
            _switchProgress += 2.5;

            if (_switchProgress >= 100)
            {
                _switchTimer.Stop();
                FinishSwitch();
                return;
            }

            SwitchBar.Value = _switchProgress;
            SwitchPercentText.Text = $"{(int)_switchProgress}%";

            if (_switchProgress >= 55 && !_secondPhase)
            {
                _secondPhase = true;
                SwitchPhaseText.Text = "第 2/2 步 · 正在安装依赖（npm install）";
                AppendSwitchLog("检出完成，开始执行 npm install --omit=dev");
            }
        }

        private void FinishSwitch()
        {
            _switching = false;
            SwitchBar.Value = 100;
            SwitchPercentText.Text = "100%";
            SwitchPhaseText.Text = "第 2/2 步 · 依赖安装完成";
            AppendSwitchLog($"完成：代码已检出 {DisplayVersion(_switchTarget)}，npm install 结束。");

            _builtinVersion = _switchTarget;

            ApplyBuiltinCurrent();
            SwitchProgressPanel.Visibility = Visibility.Collapsed;
            ReloadVersions();
            UpdateHeaderCurrent();

            ShowBar(
                "版本已切换",
                $"内置实例现在使用 {DisplayVersion(_builtinVersion)}，旧版本代码已被覆盖，需要时可重新下载。",
                InfoBarSeverity.Success);
        }

        private void AppendSwitchLog(string line)
        {
            SwitchLog.Text = string.IsNullOrEmpty(SwitchLog.Text)
                ? line
                : SwitchLog.Text + Environment.NewLine + line;
        }

        private static string DisplayVersion(string version)
            => version == "staging" ? "开发版" : "v" + version;

        // ==================== 内置实例：其它操作 ====================

        private void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_builtinVersion == LatestStable)
            {
                ShowBar("已是最新版本", "内置实例 v1.19.2 是当前最新的稳定版。", InfoBarSeverity.Success);
                return;
            }

            ShowBar(
                "发现新版本 v1.19.2",
                "在右侧「切换版本」中选择 v1.19.2 即可更新，旧版本会被覆盖。",
                InfoBarSeverity.Informational);
        }

        private void OpenBuiltinDir_Click(object sender, RoutedEventArgs e)
            => ShowBar("安装目录", BuiltinPathText.Text, InfoBarSeverity.Informational);

        private void CopyBuiltinPath_Click(object sender, RoutedEventArgs e)
        {
            var package = new DataPackage();
            package.SetText(BuiltinPathText.Text);
            Clipboard.SetContent(package);

            ShowBar("路径已复制", BuiltinPathText.Text, InfoBarSeverity.Success);
        }

        private void ViewChangelog_Click(object sender, RoutedEventArgs e)
        {
            ShowBar(
                "v1.19.2 更新日志",
                "稳定性改进 · 修复扩展加载顺序 · 依赖升级；完整日志见发布页。",
                InfoBarSeverity.Informational);
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshLocalRows();
            ApplyBuiltinCurrent();

            string current = _currentSourceIsLocal
                ? $"本地 v{_currentLocalVersion}"
                : $"内置 {DisplayVersion(_builtinVersion)}";

            ShowBar(
                "实例状态已刷新",
                $"本地 {_localData.Count} 个实例 · 内置 1 个实例 · 当前使用 {current}。",
                InfoBarSeverity.Informational);
        }

        // ==================== 反馈 ====================

        private void ShowBar(string title, string message, InfoBarSeverity severity)
        {
            ActionInfoBar.Title = title;
            ActionInfoBar.Message = message;
            ActionInfoBar.Severity = severity;
            ActionInfoBar.IsOpen = true;
        }
    }
}
