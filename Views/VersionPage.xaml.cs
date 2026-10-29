using System;
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
        /// <summary>行对应的共享实例对象，交互时直接回写它，不再按版本号反查。</summary>
        public TavernInstance? Instance { get; set; }

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
    ///
    /// 本页不持有任何实例状态：清单与「当前使用哪一份」都在
    /// <see cref="TavernInstances"/>，这里只负责渲染与派发动作。
    /// </summary>
    public sealed partial class VersionPage : Page
    {
        private readonly DispatcherTimer _scanTimer = new();
        private readonly DispatcherTimer _switchTimer = new();

        /// <summary>最近一次同步到界面上的内置版本，用来判断是否需要重置通道单选项。</summary>
        private string _lastBuiltinVersion = string.Empty;

        private bool _syncingPicker;
        private bool _syncingChannel;
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
            // 先减后加：避免同一页面重复 Loaded 时累积多份订阅
            TavernInstances.Changed -= Catalog_Changed;
            TavernInstances.Changed += Catalog_Changed;

            ApplyTab();
            SyncFromCatalog();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            TavernInstances.Changed -= Catalog_Changed;

            _scanTimer.Stop();
            _switchTimer.Stop();
        }

        /// <summary>
        /// 清单或当前实例变化后重画整页。所有动作只改 <see cref="TavernInstances"/>，
        /// 界面统一在这里跟上，因此仪表盘 / 标题栏 / 关于页与本页不会出现两套状态。
        /// </summary>
        private void Catalog_Changed(object? sender, EventArgs e)
        {
            SyncFromCatalog();
        }

        private void SyncFromCatalog()
        {
            if (LocalList is null)
            {
                return;
            }

            string builtinVersion = TavernInstances.Builtin.Version;
            if (!string.Equals(_lastBuiltinVersion, builtinVersion, StringComparison.Ordinal))
            {
                _lastBuiltinVersion = builtinVersion;
                SyncChannelChips();
            }

            RefreshLocalRows();
            ApplyBuiltinCurrent();
            ReloadVersions();
            SyncHeaderCurrent();
        }

        /// <summary>
        /// 通道单选项跟随内置实例的实际版本：否则从仪表盘或别处换过版后回到本页，
        /// 单选项会停在「稳定版」而实际代码是开发版。
        /// </summary>
        private void SyncChannelChips()
        {
            if (StableChip is null || StagingChip is null)
            {
                return;
            }

            bool staging = TavernInstances.Builtin.Version == TavernInstances.StagingTag;

            _syncingChannel = true;
            StagingChip.IsChecked = staging;
            StableChip.IsChecked = !staging;
            _syncingChannel = false;

            if (StagingNote is not null)
            {
                StagingNote.IsOpen = staging;
            }
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

        /// <summary>页头状态胶囊与内置卡状态：读同一份 <see cref="TavernInstances.Current"/>。</summary>
        private void SyncHeaderCurrent()
        {
            if (HeaderCurrentText is null)
            {
                return;
            }

            var current = TavernInstances.Current;

            HeaderCurrentText.Text = current.IsBuiltin
                ? $"当前 · 内置 {current.KernelLabel}"
                : $"当前 · 本地 {current.KernelLabel}";

            UpdateBuiltinActiveState();
        }

        /// <summary>
        /// 内置卡头部的状态：使用中 / 已安装，并决定「使用内置实例」按钮是否出现。
        /// </summary>
        private void UpdateBuiltinActiveState()
        {
            if (BuiltinActivePill is null || BuiltinIdlePill is null || UseBuiltinBtn is null)
            {
                return;
            }

            bool builtinActive = TavernInstances.IsBuiltinActive;

            BuiltinActivePill.Visibility = builtinActive ? Visibility.Visible : Visibility.Collapsed;
            BuiltinIdlePill.Visibility = builtinActive ? Visibility.Collapsed : Visibility.Visible;
            UseBuiltinBtn.Visibility = builtinActive ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UseBuiltin_Click(object sender, RoutedEventArgs e)
        {
            if (TavernInstances.IsBuiltinActive)
            {
                return;
            }

            TavernInstances.Use(TavernInstances.Builtin);

            ShowBar(
                "已切换实例",
                $"后续启动将使用内置实例 {TavernInstances.Builtin.KernelLabel}。本地实例仍保留在列表中，随时可以切回。",
                InfoBarSeverity.Success);
        }

        // ==================== 本地实例：列表 ====================

        private void RefreshLocalRows()
        {
            var locals = TavernInstances.Locals;
            var current = TavernInstances.Current;

            Rows.Clear();

            foreach (var instance in locals)
            {
                Rows.Add(new LocalInstanceRow
                {
                    Instance = instance,
                    Version = instance.Version,
                    Path = instance.Path,
                    NodeText = instance.Node,
                    IsCurrent = ReferenceEquals(instance, current),
                    DepsReady = instance.DepsReady,
                });
            }

            int missing = locals.Count(item => !item.DepsReady);

            LocalTabCount.Text = locals.Count.ToString();
            LocalSummaryText.Text = locals.Count == 0
                ? "尚未扫描到实例"
                : missing == 0
                    ? $"已发现 {locals.Count} 个实例 · 依赖全部就绪"
                    : $"已发现 {locals.Count} 个实例 · {missing} 个缺少依赖";

            LocalList.Visibility = locals.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            LocalEmpty.Visibility = locals.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void LocalUse_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: LocalInstanceRow row } || row.Instance is null)
            {
                return;
            }

            var target = row.Instance;
            if (!target.DepsReady)
            {
                return;
            }

            // 只改共享状态；列表与页头由 Catalog_Changed 统一刷新
            TavernInstances.Use(target);

            ShowBar(
                "已切换实例",
                $"后续启动将使用本地 {target.KernelLabel}，目录：{target.Path}",
                InfoBarSeverity.Success);
        }

        private void LocalFixDeps_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: LocalInstanceRow row } || row.Instance is null)
            {
                return;
            }

            var target = row.Instance;
            TavernInstances.SetDepsReady(target, true);

            ShowBar(
                "依赖已安装",
                $"已为 {target.KernelLabel} 安装 Node 依赖（{target.Node} · npm 10.5.2），现在可以切换使用。",
                InfoBarSeverity.Success);
        }

        private async void LocalRemove_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: LocalInstanceRow row } || row.Instance is null)
            {
                return;
            }

            var target = row.Instance;
            bool wasCurrent = ReferenceEquals(target, TavernInstances.Current);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"从列表移除 {target.KernelLabel}？",
                Content = "只从启动器列表中移除，不会删除磁盘上的目录。",
                PrimaryButtonText = "移除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            TavernInstances.RemoveLocal(target);

            ShowBar(
                "已移除实例",
                wasCurrent
                    ? $"{target.KernelLabel} 不再出现在列表中，目录 {target.Path} 仍然保留；当前已回落到内置实例 {TavernInstances.Builtin.KernelLabel}。"
                    : $"{target.KernelLabel} 不再出现在列表中，目录 {target.Path} 仍然保留。",
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
                $"已检查 18,432 个文件夹，识别到 {TavernInstances.Locals.Count} 个实例（跳过 2 个缺少 config.yaml 的目录）。",
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

            string builtinVersion = TavernInstances.Builtin.Version;

            _syncingPicker = true;
            VersionPicker.Items.Clear();

            if (IsStaging)
            {
                VersionPicker.Items.Add(new ComboBoxItem
                {
                    Content = "staging · 4c1f9ab（开发分支最新）",
                    Tag = TavernInstances.StagingTag,
                });
            }
            else
            {
                foreach (var release in TavernInstances.StableReleases)
                {
                    string label = $"v{release.Version} · {release.Date}";
                    if (release.Version == builtinVersion)
                    {
                        // 当前版本不再重复标「最新」，避免「最新 · 当前」两个标签挤在一起。
                        label += " · 当前";
                    }
                    else if (release.Version == TavernInstances.LatestStable)
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
            if (StagingNote is null || VersionPicker is null || _syncingChannel)
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
            if (SwitchVersionBtn is null || SwitchHintText is null || VersionPicker is null)
            {
                return;
            }

            string target = (VersionPicker.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
            bool same = target.Length > 0 && target == TavernInstances.Builtin.Version;

            SwitchVersionBtn.IsEnabled = !_switching && target.Length > 0 && !same;
            SwitchVersionBtn.Content = _switching
                ? "正在切换…"
                : same
                    ? "已是当前版本"
                    : target.Length > 0
                        ? $"切换到 {DisplayVersion(target)}"
                        : "选择要切换的版本";

            // 当前在用本地实例时，「换版」只改内置代码，不改变使用来源，必须写清楚，
            // 否则用户会以为换版就等于切回内置。
            SwitchHintText.Text = !TavernInstances.IsBuiltinActive
                ? "换版只更新内置实例的代码，不改变当前使用的实例；换版后点右上角「使用内置实例」即可启用。"
                : IsStaging
                    ? "开发版跟随 staging 分支，切换同样覆盖当前代码。"
                    : "切换会覆盖当前版本，旧版本不会保留。";
        }

        private void ApplyBuiltinCurrent()
        {
            if (BuiltinVersionText is null)
            {
                return;
            }

            var builtin = TavernInstances.Builtin;
            string version = builtin.Version;

            // 安装目录来自共享清单，不再写死一份样例路径
            BuiltinPathText.Text = builtin.Path;

            if (version == TavernInstances.StagingTag)
            {
                BuiltinVersionText.Text = TavernInstances.StagingTag;
                MetaChannelText.Text = "开发版";
                MetaDateText.Text = "更新于 2026-10-27";
                MetaSizeText.Text = builtin.Size;
                MetaFilesText.Text = "1,312 个文件";
                LatestPill.Visibility = Visibility.Collapsed;
                return;
            }

            BuiltinVersionText.Text = "v" + version;
            MetaChannelText.Text = "稳定版";

            int index = Array.FindIndex(
                TavernInstances.StableReleases,
                item => item.Version == version);
            MetaDateText.Text = index >= 0 ? "发布于 " + TavernInstances.StableReleases[index].Date : "发布日期未知";
            MetaSizeText.Text = index >= 0 ? TavernInstances.StableReleases[index].Size : "—";
            MetaFilesText.Text = index >= 0 ? TavernInstances.StableReleases[index].Files : "—";

            LatestPill.Visibility = version == TavernInstances.LatestStable
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

            if (target == TavernInstances.Builtin.Version)
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

            bool builtinActive = TavernInstances.IsBuiltinActive;

            // 只改共享状态：卡片、通道单选项与下拉框由 Catalog_Changed 统一重画
            TavernInstances.SetBuiltinVersion(_switchTarget);

            SwitchProgressPanel.Visibility = Visibility.Collapsed;

            ShowBar(
                "版本已切换",
                builtinActive
                    ? $"内置实例现在使用 {TavernInstances.Builtin.KernelLabel}，旧版本代码已被覆盖，需要时可重新下载。"
                    : $"内置实例的代码已更新为 {TavernInstances.Builtin.KernelLabel}；当前仍在使用本地 {TavernInstances.Current.KernelLabel}，点右上角「使用内置实例」即可启用新版本。",
                InfoBarSeverity.Success);
        }

        private void AppendSwitchLog(string line)
        {
            SwitchLog.Text = string.IsNullOrEmpty(SwitchLog.Text)
                ? line
                : SwitchLog.Text + Environment.NewLine + line;
        }

        private static string DisplayVersion(string version)
            => version == TavernInstances.StagingTag ? "开发版" : "v" + version;

        // ==================== 内置实例：其它操作 ====================

        private void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (TavernInstances.Builtin.Version == TavernInstances.LatestStable)
            {
                ShowBar(
                    "已是最新版本",
                    $"内置实例 v{TavernInstances.LatestStable} 是当前最新的稳定版。",
                    InfoBarSeverity.Success);
                return;
            }

            ShowBar(
                $"发现新版本 v{TavernInstances.LatestStable}",
                $"在右侧「切换版本」中选择 v{TavernInstances.LatestStable} 即可更新，旧版本会被覆盖。",
                InfoBarSeverity.Informational);
        }

        private void OpenBuiltinDir_Click(object sender, RoutedEventArgs e)
            => ShowBar("安装目录", TavernInstances.Builtin.Path, InfoBarSeverity.Informational);

        private void CopyBuiltinPath_Click(object sender, RoutedEventArgs e)
        {
            var package = new DataPackage();
            package.SetText(TavernInstances.Builtin.Path);
            Clipboard.SetContent(package);

            ShowBar("路径已复制", TavernInstances.Builtin.Path, InfoBarSeverity.Success);
        }

        private void ViewChangelog_Click(object sender, RoutedEventArgs e)
        {
            ShowBar(
                $"v{TavernInstances.LatestStable} 更新日志",
                "稳定性改进 · 修复扩展加载顺序 · 依赖升级；完整日志见发布页。",
                InfoBarSeverity.Informational);
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            SyncFromCatalog();

            var current = TavernInstances.Current;

            ShowBar(
                "实例状态已刷新",
                $"本地 {TavernInstances.Locals.Count} 个实例 · 内置 1 个实例 · 当前使用 {(current.IsBuiltin ? "内置" : "本地")} {current.KernelLabel}。",
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
