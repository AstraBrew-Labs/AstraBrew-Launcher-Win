using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AstraBrew_Launcher.Services;
using AstraBrew_Launcher.Views.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

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

        /// <summary>这一行的依赖正在装（同一时刻只允许一处，见 <see cref="VersionPage"/>）。</summary>
        public bool IsFixing { get; set; }

        public string VersionLabel => Version.Length == 0 ? L.Get("Version.Row.VersionUnknown") : "v" + Version;

        public string FixText => IsFixing ? L.Get("Version.Row.Fixing") : L.Get("Version.Row.Fix");

        public bool FixEnabled => !IsFixing;

        public string UseText => IsCurrent ? L.Get("Version.Row.InUseLabel") : L.Get("Version.Row.Use");

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
        /// <summary>已经开着的扫描详情弹窗。ContentDialog 同一时刻只能有一个。</summary>
        private ScanDetailDialog? _scanDialog;

        /// <summary>「查看详情」点进来时页面还没挂进视觉树，先记下来等 Loaded 再开弹窗。</summary>
        private bool _pendingScanDetail;

        /// <summary>正在补依赖的那份本地实例：同一时刻只跑一份 npm。</summary>
        private TavernInstance? _fixingDeps;

        /// <summary>最近一次同步到界面上的内置版本，用来判断是否需要重置通道单选项。</summary>
        private string _lastBuiltinVersion = string.Empty;

        private bool _syncingPicker;
        private bool _syncingChannel;
        private bool _switching;
        private bool _loadingReleases;
        private bool _checkingMirror;

        /// <summary>版本列表处在哪一步：换语言时按钮上的「刷新 / 正在刷新…」按它重写。</summary>
        private ReleasesStage _releasesStage = ReleasesStage.Idle;

        /// <summary>最近一次取回的版本列表，换语言时用它重写「缓存于 …」那一行。</summary>
        private ReleaseResult? _releaseCache;

        private enum ReleasesStage
        {
            Idle,
            Loading,
            Refreshing,
        }

        public VersionPage()
        {
            InitializeComponent();

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

            // 扫描在自己的后台任务里跑，页面只是它的一个观察者：
            // 翻走再翻回来要能立刻接上当前进度
            InstanceScan.Changed -= Scan_Changed;
            InstanceScan.Changed += Scan_Changed;

            // 页面里有一半文字是「按状态拼出来的」（版本胶囊、按钮文案、来源说明），
            // Loc 刷不到，换语言时由这里按当前状态重画一遍
            LanguageSwitch.LanguageChanged -= OnLanguageChanged;
            LanguageSwitch.LanguageChanged += OnLanguageChanged;


            ApplyLocalizedChrome();
            ApplyTab();
            SyncFromCatalog();
            RefreshScanEntry();

            // 有缓存就直接画出来，没有才去请求；失败只影响下拉框，不影响其它内容
            _ = LoadReleasesAsync(force: false);
            _ = LoadRuntimeLabelAsync();

            // 从全局消息里的「查看详情」点过来时，页面刚导航完还没挂进视觉树，
            // 那时开不了弹窗，只能在这里补一次
            if (_pendingScanDetail)
            {
                _pendingScanDetail = false;
                _ = ShowScanDialogAsync();
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            TavernInstances.Changed -= Catalog_Changed;
            InstanceScan.Changed -= Scan_Changed;
            LanguageSwitch.LanguageChanged -= OnLanguageChanged;
        }

        /// <summary>
        /// 换语言：静态文字由 <c>Loc</c> 重刷，这里只重画由状态拼出来的那几处。
        /// 一律「按当前状态重新渲染」，不改任何设置、不重置用户选中的版本。
        /// </summary>
        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            ApplyLocalizedChrome();
            SyncFromCatalog();
        }

        /// <summary>「整块控件一句话」的地方：这些属性是 <c>object</c> 型，Loc 的反射写法够不着。</summary>
        private void ApplyLocalizedChrome()
        {
            if (SwitchLogExpander is not null)
            {
                SwitchLogExpander.Header = L.Get("Version.Builtin.Progress.Log.Header");
            }

            if (StagingNote is not null)
            {
                StagingNote.Title = L.Get("Version.Builtin.Note.Staging.Title");
                StagingNote.Message = L.Get("Version.Builtin.Note.Staging.Body");
            }

            if (RefreshReleasesText is not null)
            {
                RefreshReleasesText.Text = L.Get(_releasesStage switch
                {
                    ReleasesStage.Refreshing => "Version.Builtin.Action.Refreshing",
                    ReleasesStage.Loading => "Version.Builtin.Action.Loading",
                    _ => "Version.Builtin.Action.RefreshLabel",
                });
            }

            if (_releaseCache is { } cache)
            {
                ShowReleaseCache(cache);
            }
        }

        private void Scan_Changed(object? sender, EventArgs e) => RefreshScanEntry();

        /// <summary>
        /// 各行显示的 Node.js 是「启动这份实例时用的那一套」，属于全应用共用的运行环境，
        /// 不是每个目录各自的——所以统一探测一次，再铺到所有行上。
        /// </summary>
        private async Task LoadRuntimeLabelAsync()
        {
            try
            {
                RuntimeEnvironment env = await RuntimeProbe.ProbeAsync(LauncherSettings.Runtime);
                TavernInstances.SetRuntimeLabel(env.Node);
            }
            catch (Exception)
            {
                // 探不到就保持「Node 未检测」，不影响实例本身的增删
            }
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
                ? L.F("Version.Header.Current.Builtin", current.KernelLabel)
                : L.F("Version.Header.Current.Local", current.KernelLabel);

            UpdateBuiltinActiveState();
        }

        /// <summary>
        /// 内置卡头部的状态：使用中 / 已安装 / 待装依赖 / 未安装，并决定「使用内置实例」按钮是否出现。
        ///
        /// 四种状态互斥：没装的时候既不能「使用」，也不该显示「已安装」——
        /// 那会让用户以为点一下就能启动。缺依赖同理：代码在，但起不来。
        /// </summary>
        private void UpdateBuiltinActiveState()
        {
            if (BuiltinActivePill is null || BuiltinIdlePill is null || UseBuiltinBtn is null)
            {
                return;
            }

            bool installed = TavernInstances.BuiltinInstalled;
            bool runnable = TavernInstances.BuiltinRunnable;

            // 没装（或缺依赖）就没有「正在使用」可言：此时 Current 虽然还指着内置实例，
            // 但磁盘上那份代码跑不起来，显示「使用中」会误导
            bool builtinActive = TavernInstances.IsBuiltinActive && runnable;

            BuiltinActivePill.Visibility = builtinActive ? Visibility.Visible : Visibility.Collapsed;
            BuiltinDepsPill.Visibility = installed && !TavernInstances.BuiltinDepsReady
                ? Visibility.Visible
                : Visibility.Collapsed;
            BuiltinIdlePill.Visibility = runnable && !builtinActive ? Visibility.Visible : Visibility.Collapsed;
            BuiltinMissingPill.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;

            // 缺依赖时不给「使用内置实例」：切过去也启动不了，先让它把依赖装上
            UseBuiltinBtn.Visibility = runnable && !builtinActive ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UseBuiltin_Click(object sender, RoutedEventArgs e)
        {
            if (TavernInstances.IsBuiltinActive)
            {
                return;
            }

            TavernInstances.Use(TavernInstances.Builtin);

            Notify(
                L.Get("Version.Local.Notify.Switched.Title"),
                L.F("Version.Local.Notify.Switched.Builtin", TavernInstances.Builtin.KernelLabel),
                NotifySeverity.Success);
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
                    IsFixing = ReferenceEquals(instance, _fixingDeps),
                });
            }

            int missing = locals.Count(item => !item.DepsReady);

            LocalTabCount.Text = locals.Count.ToString();
            LocalSummaryText.Text = locals.Count == 0
                ? L.Get("Version.Local.Summary.Empty")
                : missing == 0
                    ? L.F("Version.Local.Summary.DepsOk", locals.Count)
                    : L.F("Version.Local.Summary.DepsMissing", locals.Count, missing);

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

            Notify(
                L.Get("Version.Local.Notify.Switched.Title"),
                L.F("Version.Local.Notify.Switched.Local", target.KernelLabel, target.Path),
                NotifySeverity.Success);
        }

        /// <summary>
        /// 给本地实例补 Node 依赖：在它自己的目录里跑一次 npm install，
        /// 依赖落在它自己的 <c>node_modules</c> 里，不碰内置实例那一份。
        /// </summary>
        private async void LocalFixDeps_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: LocalInstanceRow row } || row.Instance is null)
            {
                return;
            }

            if (_fixingDeps is not null)
            {
                Notify(
                    L.Get("Version.Local.Notify.Fixing.Title"),
                    L.F("Version.Local.Notify.Fixing.Busy", _fixingDeps.KernelLabel),
                    NotifySeverity.Warning);
                return;
            }

            TavernInstance target = row.Instance;
            if (!await ConfirmLocalDepsAsync(target))
            {
                return;
            }

            _fixingDeps = target;
            RefreshLocalRows();

            Notify(
                L.Get("Version.Local.Notify.Fixing.Title"),
                L.F("Version.Local.Notify.Fixing.Body", target.KernelLabel, LauncherSettings.NpmRegistryName),
                NotifySeverity.Informational);

            TavernInstallResult result;
            try
            {
                result = await TavernInstaller.InstallDependenciesAsync(target.Path, null, CancellationToken.None);
            }
            catch (Exception ex)
            {
                result = new TavernInstallResult(false, L.Get("Version.Progress.ErrorDeps"), ex.Message, null, null, null);
            }
            finally
            {
                _fixingDeps = null;
            }

            // 装没装好以磁盘为准：重新读一遍 node_modules，不拿结果里的标记当真
            TavernInstances.ReloadLocal(target);

            if (result.Success && target.DepsReady)
            {
                Notify(
                    L.Get("Version.Local.Notify.DepsOk.Title"),
                    L.F("Version.Local.Notify.DepsOk.Body", target.KernelLabel),
                    NotifySeverity.Success);
                return;
            }

            string detail = result.Message
                + (result.Hint is { Length: > 0 } hint ? " " + hint : string.Empty);

            AppNotify.Post(
                NotifySeverity.Error,
                L.Get("Version.Local.Notify.DepsFail.Title"),
                detail,
                result.LogPath is { Length: > 0 } ? L.Get("Version.Done.ViewLog") : null,
                result.LogPath is { Length: > 0 } logPath ? () => OpenPath(logPath) : null);
        }

        /// <summary>
        /// 补依赖前的确认：说清楚装在哪、走哪个源、装多久，以及只对这一次生效——
        /// 用户最怕启动器偷偷改了 npm 的全局配置。
        /// </summary>
        private async Task<bool> ConfirmLocalDepsAsync(TavernInstance target)
        {
            var body = new StackPanel { Spacing = 8 };
            body.Children.Add(new TextBlock
            {
                Text = L.Get("Version.Local.Dialog.Deps.Body"),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 22,
            });
            body.Children.Add(HintText(L.F("Version.Local.Dialog.Deps.Location", target.Path)));
            body.Children.Add(HintText(L.F(
                "Version.Local.Dialog.Deps.Registry",
                LauncherSettings.NpmRegistryName,
                LauncherSettings.NpmRegistryUrl)));
            body.Children.Add(HintText(L.Get("Version.Local.Dialog.Deps.Note")));

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = L.F("Version.Local.Dialog.Deps.Title", target.KernelLabel),
                Content = body,
                PrimaryButtonText = L.Get("Version.Local.Dialog.Deps.Start"),
                CloseButtonText = L.Get("Common.Action.Cancel.Content"),
                DefaultButton = ContentDialogButton.Primary,
            };

            // 与页面按钮同一个圆角
            dialog.Resources["ControlCornerRadius"] = new CornerRadius(8);

            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        private static TextBlock HintText(string text) => new()
        {
            Text = text,
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        };

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
                Title = L.F("Version.Local.Dialog.Remove.Title", target.KernelLabel),
                Content = L.Get("Version.Local.Dialog.Remove.Body"),
                PrimaryButtonText = L.Get("Version.Local.Dialog.Remove.Confirm"),
                CloseButtonText = L.Get("Common.Action.Cancel.Content"),
                DefaultButton = ContentDialogButton.Close,
            };

            // DefaultButton 会被模板套上框架 AccentButtonStyle，圆角取自 ControlCornerRadius（4）。
            // 在弹窗作用域里把它收到 8，让「取消」这颗强调按钮跟页面按钮同一个圆角。
            dialog.Resources["ControlCornerRadius"] = new CornerRadius(8);

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            TavernInstances.RemoveLocal(target);

            Notify(
                L.Get("Version.Local.Notify.Removed.Title"),
                wasCurrent
                    ? L.F("Version.Local.Notify.Removed.Current", target.KernelLabel, target.Path, TavernInstances.Builtin.KernelLabel)
                    : L.F("Version.Local.Notify.Removed.Body", target.KernelLabel, target.Path),
                NotifySeverity.Informational);
        }

        // ==================== 本地实例：扫描 / 导入 ====================

        /// <summary>
        /// 扫描是后台任务：点一次就开跑，进度落在右下角的常驻消息里（切页面也看得见），
        /// 同时把详情弹窗打开——用户点了「扫描」，总得当场知道它开始动了。
        /// 已经在扫时再点，只把弹窗重新打开，不再起第二遍。
        /// </summary>
        private async void Scan_Click(object sender, RoutedEventArgs e)
        {
            if (!InstanceScan.IsRunning)
            {
                InstanceScan.Start();
            }

            await ShowScanDialogAsync();
        }

        /// <summary>供全局消息的「查看详情」调用：页面还没挂进视觉树时先记一笔，Loaded 再弹。</summary>
        public void OpenScanDetail()
        {
            if (XamlRoot is null)
            {
                _pendingScanDetail = true;
                return;
            }

            _ = ShowScanDialogAsync();
        }

        private async void ScanDetail_Click(object sender, RoutedEventArgs e) => await ShowScanDialogAsync();

        private async Task ShowScanDialogAsync()
        {
            if (_scanDialog is not null || XamlRoot is null)
            {
                return;
            }

            var dialog = new ScanDetailDialog { XamlRoot = XamlRoot };
            _scanDialog = dialog;

            try
            {
                await dialog.ShowAsync();
            }
            catch (Exception)
            {
                // 同一 XamlRoot 上已经有别的弹窗：这次不打开，摘要条本身仍是实时的
            }
            finally
            {
                _scanDialog = null;
            }
        }

        /// <summary>
        /// 详情按钮只在扫过之后出现。扫了多少、找到几个这些话已经由右下角的常驻消息
        /// 一直说着，列表顶部不再单占一条摘要，只留一个进详情的入口。
        /// </summary>
        private void RefreshScanEntry()
        {
            if (ScanDetailBtn is null)
            {
                return;
            }

            ScanDetailBtn.Visibility = InstanceScan.State.HasRun
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        /// <summary>
        /// 手动导入：选一个 <c>package.json</c>，校验 <c>name</c> 是不是 sillytavern。
        ///
        /// 选文件而不是选目录，是因为判据就在这个文件里：让人指一个目录，还得替他猜
        /// package.json 在哪一层；指到文件就等于把判据直接摆出来了。
        /// 校验统一走 <see cref="TavernInstances.AddLocal"/>，与扫描同一套标准。
        /// </summary>
        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            string? picked = await PickManifestAsync();
            if (string.IsNullOrEmpty(picked))
            {
                return;
            }

            if (!string.Equals(Path.GetFileName(picked), "package.json", StringComparison.OrdinalIgnoreCase))
            {
                Notify(
                    L.Get("Version.Import.WrongFile.Title"),
                    L.Get("Version.Import.WrongFile.Body"),
                    NotifySeverity.Warning);
                return;
            }

            // 实例路径是 package.json 所在的那一层
            string root = Path.GetDirectoryName(picked) ?? picked;

            switch (TavernInstances.AddLocal(root, out TavernInstance? added))
            {
                case LocalAddStatus.Added:
                    Notify(
                        L.Get("Version.Import.Added.Title"),
                        added!.DepsReady
                            ? L.F("Version.Import.Added.Ready", added.KernelLabel, added.Path)
                            : L.F("Version.Import.Added.Missing", added.KernelLabel, added.Path),
                        NotifySeverity.Success);
                    break;

                case LocalAddStatus.Duplicate:
                    Notify(
                        L.Get("Version.Import.Duplicate.Title"),
                        L.F("Version.Import.Duplicate.Body", root),
                        NotifySeverity.Informational);
                    break;

                case LocalAddStatus.Builtin:
                    Notify(
                        L.Get("Version.Import.Builtin.Title"),
                        L.Get("Version.Import.Builtin.Body"),
                        NotifySeverity.Warning);
                    break;

                case LocalAddStatus.NotTavern:
                    Notify(
                        L.Get("Version.Import.NotTavern.Title"),
                        L.F("Version.Import.NotTavern.Body", InstanceScanner.PackageName),
                        NotifySeverity.Error);
                    break;

                default:
                    Notify(
                        L.Get("Version.Import.Unreadable.Title"),
                        L.F("Version.Import.Unreadable.Body", root),
                        NotifySeverity.Error);
                    break;
            }
        }

        private static async Task<string?> PickManifestAsync()
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };

            picker.FileTypeFilter.Add(".json");

            if (App.MainWindow is not null)
            {
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            }

            StorageFile? file = await picker.PickSingleFileAsync();
            return file?.Path;
        }

        // ==================== 内置实例：版本切换 ====================

        private void ReloadVersions()
        {
            if (VersionPicker is null || SwitchVersionBtn is null)
            {
                return;
            }

            string builtinVersion = TavernInstances.Builtin.Version;
            bool installed = TavernInstances.BuiltinInstalled;

            // 换语言会走一遍重建：先记住用户手上选的是哪一版，重建后还能选回去
            string? keepTag = (VersionPicker.SelectedItem as ComboBoxItem)?.Tag as string;

            _syncingPicker = true;
            VersionPicker.Items.Clear();

            if (IsStaging)
            {
                VersionPicker.Items.Add(new ComboBoxItem
                {
                    Content = L.Get("Version.Builtin.Picker.Staging"),
                    Tag = TavernInstances.StagingTag,
                });
            }
            else
            {
                foreach (TavernRelease release in TavernInstances.Releases)
                {
                    if (release.Prerelease)
                    {
                        // 稳定版通道只列正式版；预发布留给开发版，不混进同一个下拉框
                        continue;
                    }

                    string label = $"v{release.Version} · {release.DateLabel}";
                    if (installed && release.Version == builtinVersion)
                    {
                        // 当前版本不再重复标「最新」，避免「最新 · 当前」两个标签挤在一起。
                        label += L.Get("Version.Builtin.Picker.Current");
                    }
                    else if (release.Version == TavernInstances.LatestStable)
                    {
                        label += L.Get("Version.Builtin.Picker.Latest");
                    }

                    VersionPicker.Items.Add(new ComboBoxItem
                    {
                        Content = label,
                        Tag = release.Version,
                    });
                }

                if (VersionPicker.Items.Count == 0)
                {
                    // 没缓存又没网时的兜底：不假装有版本可选，也不留一个空下拉框
                    VersionPicker.Items.Add(new ComboBoxItem
                    {
                        Content = L.Get("Version.Builtin.Picker.Empty"),
                        Tag = null,
                        IsEnabled = false,
                    });
                }
            }

            // 默认选中「已经装着的那个版本」：否则用户看到的是「切换」而不是
            // 「安装依赖」，明明只差一步却以为要从头再装一遍
            int selectIndex = 0;
            if (installed)
            {
                for (int i = 0; i < VersionPicker.Items.Count; i++)
                {
                    if ((VersionPicker.Items[i] as ComboBoxItem)?.Tag as string == builtinVersion)
                    {
                        selectIndex = i;
                        break;
                    }
                }
            }

            if (keepTag is { Length: > 0 })
            {
                for (int i = 0; i < VersionPicker.Items.Count; i++)
                {
                    if ((VersionPicker.Items[i] as ComboBoxItem)?.Tag as string == keepTag)
                    {
                        selectIndex = i;
                        break;
                    }
                }
            }

            VersionPicker.SelectedIndex = selectIndex;
            _syncingPicker = false;

            UpdateSwitchButton();
            _ = RefreshSourceRouteAsync();
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
            _ = RefreshSourceRouteAsync();
        }

        /// <summary>
        /// 一颗按钮承担「安装」「换版」「装依赖」：前两者在磁盘上是同一件事（把某个版本拉到安装目录），
        /// 第三个只补 <c>node_modules</c>。没装时不显示「切换」——那会让用户以为已经有东西可以切。
        /// </summary>
        private void UpdateSwitchButton()
        {
            if (SwitchVersionBtn is null || SwitchHintText is null || VersionPicker is null)
            {
                return;
            }

            string target = (VersionPicker.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
            bool installed = TavernInstances.BuiltinInstalled;
            bool depsReady = TavernInstances.BuiltinDepsReady;
            bool same = installed && target.Length > 0 && target == TavernInstances.Builtin.Version;

            // 代码就是这一份、只是缺依赖：按钮只干装依赖这一件事
            bool depsOnly = same && !depsReady;

            SwitchVersionBtn.IsEnabled = !_switching && target.Length > 0 && (!same || depsOnly);
            SwitchVersionBtn.Content = _switching
                ? depsOnly
                    ? L.Get("Version.Builtin.SwitchBtn.InstallingDeps")
                    : installed
                        ? L.Get("Version.Builtin.SwitchBtn.Switching")
                        : L.Get("Version.Builtin.SwitchBtn.Installing")
                : depsOnly
                    ? L.Get("Version.Builtin.SwitchBtn.Deps")
                    : same
                        ? L.Get("Version.Builtin.SwitchBtn.Same")
                        : target.Length > 0
                            ? L.F(
                                installed ? "Version.Builtin.SwitchBtn.Switch" : "Version.Builtin.SwitchBtn.Install",
                                DisplayVersion(target))
                            : installed
                                ? L.Get("Version.Builtin.SwitchBtn.PickSwitch")
                                : L.Get("Version.Builtin.SwitchBtn.PickInstall");

            SwitchHintText.Text = depsOnly
                ? L.F("Version.Builtin.Hint.DepsOnly", LauncherSettings.NpmRegistryName)
                : !installed
                    ? L.Get("Version.Builtin.Hint.Fresh")
                    : !TavernInstances.IsBuiltinActive
                        ? L.Get("Version.Builtin.Hint.NotActive")
                        : IsStaging
                            ? L.Get("Version.Builtin.Hint.Staging")
                            : L.Get("Version.Builtin.Hint.Switch");
        }

        // ==================== 安装来源：镜像可用 / 直连 ====================

        /// <summary>
        /// 判断这次安装「能不能走镜像」并写进界面。
        ///
        /// 规则很直白：把镜像仓库的引用清单（一次取回、缓存半小时）与当前选的版本比一比，
        /// 有同一个标签就显示「镜像可用」，没有就显示「直连安装」——直连就是官方 GitHub。
        /// 选的是 GitHub 直连通道时不存在「镜像可用」，只显示「直连安装」。
        /// </summary>
        private async Task RefreshSourceRouteAsync()
        {
            if (SourceRouteRow is null || _switching)
            {
                return;
            }

            DownloadChannel channel = LauncherSettings.Channel;
            string revision = (VersionPicker?.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;

            if (channel == DownloadChannel.Github)
            {
                ShowRoute(
                    mirror: false,
                    L.Get(LauncherSettings.GithubAcceleration
                        ? "Version.Builtin.Route.GithubAccel"
                        : "Version.Builtin.Route.GithubPlain"));
                return;
            }

            if (revision.Length == 0)
            {
                ShowRoute(mirror: false, L.Get("Version.Builtin.Route.NoRevision"));
                return;
            }

            // 已经有这台镜像的清单且通道没变：直接判定，不再请求
            if (TavernMirrors.TryGetCached(channel, out MirrorRefs known))
            {
                ApplyRoute(channel, revision, known);
                return;
            }

            if (_checkingMirror)
            {
                return;
            }

            _checkingMirror = true;
            SourceRouteText.Text = L.F("Version.Builtin.Route.Checking", LauncherSettings.ChannelName(channel));

            try
            {
                MirrorRefs refs = await TavernMirrors.GetAsync(channel, force: false, CancellationToken.None);
                ApplyRoute(channel, revision, refs);
            }
            finally
            {
                _checkingMirror = false;
            }
        }

        private void ApplyRoute(DownloadChannel channel, string revision, MirrorRefs refs)
        {
            if (refs.Channel != channel)
            {
                return;
            }

            bool staging = revision == TavernInstances.StagingTag;
            bool usable = staging ? refs.HasBranch(TavernInstances.StagingTag) : refs.HasTag(revision);

            if (refs.Error is { Length: > 0 } error)
            {
                ShowRoute(mirror: false, L.F("Version.Builtin.Route.MirrorError", error));
                return;
            }

            ShowRoute(
                usable,
                usable
                    ? L.F("Version.Builtin.Route.MirrorOk", LauncherSettings.ChannelName(channel), DisplayVersion(revision))
                    : LauncherSettings.GithubAcceleration
                        ? L.F("Version.Builtin.Route.MirrorMissAccel", LauncherSettings.ChannelName(channel), DisplayVersion(revision))
                        : L.F("Version.Builtin.Route.MirrorMiss", LauncherSettings.ChannelName(channel), DisplayVersion(revision)));
        }

        private void ShowRoute(bool mirror, string detail)
        {
            if (SourceRouteRow is null)
            {
                return;
            }

            MirrorOkChip.Visibility = mirror ? Visibility.Visible : Visibility.Collapsed;
            DirectChip.Visibility = mirror ? Visibility.Collapsed : Visibility.Visible;
            SourceRouteText.Text = detail;
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

            // 没装：不显示版本号，也不留三枚空胶囊——版本号是「当前装的这一份」的属性
            if (!TavernInstances.BuiltinInstalled)
            {
                BuiltinVersionText.Text = L.Get("Version.Builtin.Current.NotInstalled");
                MetaRow.Visibility = Visibility.Collapsed;
                LatestPill.Visibility = Visibility.Collapsed;
                return;
            }

            MetaRow.Visibility = Visibility.Visible;

            if (version == TavernInstances.StagingTag)
            {
                BuiltinVersionText.Text = TavernInstances.StagingTag;
                MetaChannelText.Text = L.Get("Version.Builtin.Channel.StagingLabel");
                MetaDateText.Text = L.Get("Version.Builtin.Current.StagingDate");
                MetaTagText.Text = L.Get("Version.Builtin.Current.StagingTag");
                LatestPill.Visibility = Visibility.Collapsed;
                return;
            }

            BuiltinVersionText.Text = "v" + version;

            // 元信息只写发布列表里真有的事实：不编体积、不编文件数
            TavernRelease? release = TavernInstances.FindRelease(version);
            MetaChannelText.Text = release?.ChannelLabel ?? L.Get("Version.Builtin.Channel.StableLabel");
            MetaDateText.Text = release?.PublishedAt is null
                ? L.Get("Version.Builtin.Current.NoDate")
                : L.F("Version.Builtin.Current.Published", release.DateLabel);
            MetaTagText.Text = release is null
                ? L.Get("Version.Builtin.Current.NoTag")
                : L.F("Version.Builtin.Current.Tag", release.Tag);

            LatestPill.Visibility = version == TavernInstances.LatestStable
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        /// <summary>
        /// 安装 / 换版 / 补依赖：先确认 Git 与 Node.js 就位，再按「镜像可用性」决定从哪拉代码。
        /// 缺环境时不硬装——装完了也启动不了，反而让人以为是代码的问题。
        /// </summary>
        private async void SwitchVersion_Click(object sender, RoutedEventArgs e)
        {
            if (_switching)
            {
                return;
            }

            if (VersionPicker.SelectedItem is not ComboBoxItem { Tag: string target } || string.IsNullOrEmpty(target))
            {
                return;
            }

            bool installed = TavernInstances.BuiltinInstalled;
            bool same = installed && target == TavernInstances.Builtin.Version;

            if (same && TavernInstances.BuiltinDepsReady)
            {
                return;
            }

            // 代码已经是对的那一份，缺的只是 node_modules：不重下源码
            if (same)
            {
                if (!await EnsureRuntimeAsync(needGit: false))
                {
                    return;
                }

                if (!await ConfirmDepsInstallAsync())
                {
                    return;
                }

                await RunDepsInstallAsync();
                return;
            }

            if (!await EnsureRuntimeAsync(needGit: true))
            {
                return;
            }

            await RunInstallAsync(target, installed);
        }

        /// <summary>
        /// 装之前的两道门：Git（拉源码）与 Node.js（跑酒馆，顺带提供 npm）。
        /// 缺任意一个就弹窗说清楚缺什么，并给一个直接落到「运行环境」分组的按钮。
        /// 只补依赖时不需要 Git，此时只查 Node.js。
        /// </summary>
        private async Task<bool> EnsureRuntimeAsync(bool needGit)
        {
            SwitchVersionBtn.IsEnabled = false;

            RuntimeEnvironment env;
            try
            {
                env = await RuntimeProbe.ProbeAsync(LauncherSettings.Runtime, force: true);
            }
            catch
            {
                env = RuntimeEnvironment.Empty;
            }

            var missing = new List<string>();
            if (needGit && env.Git is null)
            {
                missing.Add("Git");
            }

            if (env.Node is null)
            {
                missing.Add("Node.js");
            }

            if (missing.Count == 0)
            {
                return true;
            }

            UpdateSwitchButton();

            string what = string.Join(L.Get("Common.List.And"), missing);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = L.F("Version.Runtime.Missing.Title", what),
                Content = new TextBlock
                {
                    Text = L.F("Version.Runtime.Missing.Body", what),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 22,
                },
                PrimaryButtonText = L.Get("Version.Runtime.Missing.Go"),
                CloseButtonText = L.Get("Version.Runtime.Missing.Later"),
                DefaultButton = ContentDialogButton.Primary,
            };

            // 与页面按钮同一个圆角
            dialog.Resources["ControlCornerRadius"] = new CornerRadius(8);

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                SettingsPage.OpenRuntimeSection();
                App.MainWindow?.NavigateTo("settings");
            }

            return false;
        }

        /// <summary>
        /// 补依赖前的确认：说清楚装在哪、走哪个源、装多久，
        /// 以及「只对这一次生效」——用户最怕启动器偷偷改了 npm 的全局配置。
        /// </summary>
        private async Task<bool> ConfirmDepsInstallAsync()
        {
            string root = TavernInstances.BuiltinInstallRoot;

            var body = new StackPanel { Spacing = 8 };
            body.Children.Add(new TextBlock
            {
                Text = L.Get("Version.Local.Dialog.Deps.BodyBuiltin"),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 22,
            });
            body.Children.Add(HintText(L.F("Version.Local.Dialog.Deps.Location", root)));
            body.Children.Add(HintText(L.F(
                "Version.Local.Dialog.Deps.Registry",
                LauncherSettings.NpmRegistryName,
                LauncherSettings.NpmRegistryUrl)));
            body.Children.Add(HintText(L.Get("Version.Local.Dialog.Deps.Note")));

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = L.Get("Version.Local.Dialog.Deps.TitlePlain"),
                Content = body,
                PrimaryButtonText = L.Get("Version.Local.Dialog.Deps.Start"),
                CloseButtonText = L.Get("Common.Action.Cancel.Content"),
                DefaultButton = ContentDialogButton.Primary,
            };

            dialog.Resources["ControlCornerRadius"] = new CornerRadius(8);

            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        /// <summary>
        /// 把安装进度画到卡片里。依赖阶段没有可信百分比，改用不确定态 + 「已处理 N 个包」，
        /// 比编一个匀速涨的数字诚实。
        /// </summary>
        private Progress<TavernInstallProgress> CreateProgress()
        {
            return new Progress<TavernInstallProgress>(p =>
            {
                SwitchPhaseText.Text = p.Message;

                bool waiting = p.Stage == TavernInstallStage.InstallingDeps;
                SwitchBar.IsIndeterminate = waiting;

                if (waiting)
                {
                    SwitchPercentText.Text = L.Get("Version.Progress.InstallingDeps");
                }
                else
                {
                    SwitchBar.Value = p.Percent;
                    SwitchPercentText.Text = $"{(int)p.Percent}%";
                }

                // 百分比细节已经画在进度条上了，日志只留阶段性的那几句
                if (p.Detail is { Length: > 0 } detail && !detail.EndsWith('%'))
                {
                    AppendSwitchLog(detail);
                }
            });
        }

        /// <summary>
        /// 真正落盘的一步：<see cref="TavernInstaller"/> 克隆 → 校验 → 换名 → 装依赖，
        /// 本方法只负责把进度画到卡片里、把结果翻成一句话。
        /// </summary>
        private async Task RunInstallAsync(string revision, bool installed)
        {
            _switching = true;

            DownloadChannel channel = LauncherSettings.Channel;
            bool mirrorUsable = TavernMirrors.TryGetCached(channel, out MirrorRefs refs)
                && refs.Error is null
                && (revision == TavernInstances.StagingTag ? refs.HasBranch(revision) : refs.HasTag(revision));

            IReadOnlyList<SourceTarget> targets = TavernSource.InstallTargets(channel, mirrorUsable);

            SwitchLog.Text = string.Empty;
            AppendSwitchLog($"$ git clone --depth 1 --branch {revision} {targets[0].Url}");
            SwitchBar.IsIndeterminate = false;
            SwitchBar.Value = 0;
            SwitchPercentText.Text = "0%";
            SwitchPhaseText.Text = L.F(
                "Version.Progress.Cloning",
                targets[0].Name,
                DisplayVersion(revision));
            SwitchProgressPanel.Visibility = Visibility.Visible;

            // 安装过程有十来分钟，日志默认摊开：让人看得见在下载什么、卡在哪
            SwitchLogExpander.IsExpanded = true;
            UpdateSwitchButton();

            TavernInstallResult result;
            try
            {
                result = await TavernInstaller.InstallAsync(revision, targets, CreateProgress(), CancellationToken.None);
            }
            catch (Exception ex)
            {
                result = new TavernInstallResult(false, L.Get("Version.Progress.Error"), ex.Message, null, null, null);
            }

            FinishInstall(result, installed, depsOnly: false);
        }

        /// <summary>只补依赖：不碰代码，装的是 <c>node_modules</c>。</summary>
        private async Task RunDepsInstallAsync()
        {
            _switching = true;

            SwitchLog.Text = string.Empty;
            AppendSwitchLog($"$ npm install --omit=dev --registry={LauncherSettings.NpmRegistryUrl}");
            SwitchBar.IsIndeterminate = false;
            SwitchBar.Value = 0;
            SwitchPercentText.Text = "0%";
            SwitchPhaseText.Text = L.F("Version.Progress.NpmPhase", LauncherSettings.NpmRegistryName);
            SwitchProgressPanel.Visibility = Visibility.Visible;
            SwitchLogExpander.IsExpanded = true;
            UpdateSwitchButton();

            TavernInstallResult result;
            try
            {
                result = await TavernInstaller.InstallDependenciesAsync(CreateProgress(), CancellationToken.None);
            }
            catch (Exception ex)
            {
                result = new TavernInstallResult(false, L.Get("Version.Progress.ErrorDeps"), ex.Message, null, null, null);
            }

            FinishInstall(result, installed: true, depsOnly: true);
        }

        /// <summary>
        /// 把安装结果收尾到界面：失败按磁盘现状重画，成功刷新实例状态。
        /// <paramref name="depsOnly"/> 区分「补依赖」与「装代码」——两者成功后的说法不一样，
        /// 补依赖说成「版本已切换」会让人以为代码被换过。
        /// </summary>
        private void FinishInstall(TavernInstallResult result, bool installed, bool depsOnly)
        {
            _switching = false;
            SwitchBar.IsIndeterminate = false;

            if (!result.Success)
            {
                // 失败也可能动过磁盘（回滚过、清过暂存、装了一半依赖），界面一律按磁盘现状重画
                TavernInstances.ReloadBuiltin();

                SwitchPhaseText.Text = TavernInstances.BuiltinInstalled
                    ? L.Get("Version.Progress.Incomplete")
                    : L.Get("Version.Progress.Failed");
                AppendSwitchLog(result.Message);
                if (result.Hint is { Length: > 0 } hint)
                {
                    AppendSwitchLog(hint);
                }

                if (result.LogPath is { Length: > 0 } path)
                {
                    AppendSwitchLog(L.F("Version.Progress.LogPath", path));
                }

                UpdateSwitchButton();

                // 代码在、只缺依赖：这不是「装不上」，换个说法与颜色，别让人以为得从头再来
                bool needsDeps = TavernInstances.BuiltinInstalled && !TavernInstances.BuiltinDepsReady;

                string failTitle = needsDeps
                    ? L.Get("Version.Done.Fail.Deps")
                    : installed
                        ? L.Get("Version.Done.Fail.Switch")
                        : L.Get("Version.Done.Fail.Install");
                string failBody = result.Message
                    + (result.Hint is { Length: > 0 } failHint ? " " + failHint : string.Empty);

                // 结果走全局消息：安装要跑一两分钟，用户很可能已经翻到别的页面，
                // 写在页面里的提示会跟着页面一起消失。
                string? failActionText = null;
                Action? failAction = null;
                if (result.LogPath is { Length: > 0 } logPath)
                {
                    failActionText = L.Get("Version.Done.ViewLog");
                    failAction = () => OpenPath(logPath);
                }

                AppNotify.Post(
                    needsDeps ? NotifySeverity.Warning : NotifySeverity.Error,
                    failTitle,
                    failBody,
                    failActionText,
                    failAction);
                return;
            }

            // 装完了以磁盘为准：版本号现读 package.json，依赖状态现看 node_modules
            TavernInstances.ReloadBuiltin();

            SwitchBar.Value = 100;
            SwitchPercentText.Text = "100%";
            SwitchPhaseText.Text = depsOnly
                ? L.Get("Version.Progress.DepsOk")
                : installed
                    ? L.Get("Version.Progress.Switched")
                    : L.Get("Version.Progress.Done");
            AppendSwitchLog(result.Message);

            bool builtinActive = TavernInstances.IsBuiltinActive;

            string title = depsOnly
                ? L.Get("Version.Done.Deps.Title")
                : installed
                    ? L.Get("Version.Done.Switch.Title")
                    : L.Get("Version.Done.Install.Title");

            // 正文按「一眼读完」写：耗时、路径这类细节留在页面日志里，
            // 全局消息只负责告诉用户「成了、下一步点哪儿」。
            string body;
            if (depsOnly)
            {
                body = builtinActive
                    ? L.F("Version.Done.Deps.Active", TavernInstances.Builtin.KernelLabel)
                    : L.F("Version.Done.Deps.Inactive", TavernInstances.Builtin.KernelLabel);
            }
            else if (installed)
            {
                body = builtinActive
                    ? L.F("Version.Done.Switch.Active", TavernInstances.Builtin.KernelLabel)
                    : L.F("Version.Done.Switch.Inactive", TavernInstances.Builtin.KernelLabel);
            }
            else
            {
                body = builtinActive
                    ? L.F("Version.Done.Install.Active", result.Message)
                    : L.F("Version.Done.Install.Inactive", result.Message);
            }

            AppNotify.Success(title, body);

            // 装完就收：结果已经进了系统通知，页面里这块再挂着只会挡住下面的版本列表。
            // 以前这里什么都不做——面板与摊开的日志会一直留在页面上，用户得切走再回来才消失。
            SwitchLogExpander.IsExpanded = false;
            _ = DismissSwitchPanelAsync();
        }

        /// <summary>
        /// 成功后过一会儿撤掉进度面板：留两秒半让人看见 100%，再收起来。
        ///
        /// 用延时而不是立刻收起，是因为「点完就没了」会让人怀疑到底装没装成功；
        /// 也不做成常驻（那正是要修的 bug）。期间又发起新安装则直接跳过，别把新一轮的面板收掉。
        /// </summary>
        private async Task DismissSwitchPanelAsync()
        {
            await Task.Delay(TimeSpan.FromSeconds(2.5));

            if (_switching || SwitchProgressPanel is null)
            {
                return;
            }

            // 明确回 UI 线程再动控件：这里是 await 之后的续体，不该指望上下文一定还在
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_switching)
                {
                    return;
                }

                SwitchProgressPanel.Visibility = Visibility.Collapsed;
                SwitchLogExpander.IsExpanded = false;
                SwitchLog.Text = string.Empty;
                SwitchBar.Value = 0;
                SwitchPercentText.Text = "0%";
            });
        }

        /// <summary>
        /// 用系统默认程序打开文件（日志通常是 .log，装了编辑器就交给编辑器）。
        /// 打不开也不提示：这只是个便利入口，日志路径本来也写在页面日志里。
        /// </summary>
        private static void OpenPath(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception)
            {
                // 便利动作失败不影响安装结果本身
            }
        }

        private void AppendSwitchLog(string line)
        {
            SwitchLog.Text = string.IsNullOrEmpty(SwitchLog.Text)
                ? line
                : SwitchLog.Text + Environment.NewLine + line;

            // 面板只有 120 像素高：留最后 60 行够看清失败原因，也不让 TextBlock 无限膨胀
            string[] lines = SwitchLog.Text.Split(Environment.NewLine);
            if (lines.Length > 60)
            {
                SwitchLog.Text = string.Join(Environment.NewLine, lines[^60..]);
            }

            // 日志是往前滚的：不跟到底部，用户看到的永远是最早那几行
            // （ChangeView 自己会把超大偏移夹到内容的实际高度）
            SwitchLogScroll?.ChangeView(null, double.MaxValue, null);
        }

        private static string DisplayVersion(string version)
            => version == TavernInstances.StagingTag ? L.Get("Version.Builtin.Channel.StagingLabel") : "v" + version;

        // ==================== 内置实例：版本列表缓存 ====================

        /// <summary>
        /// 取版本列表：有缓存就直接用，没有才请求；<paramref name="force"/> 由用户点刷新触发。
        /// 整个方法不抛异常到 UI 线程，失败折成一句话。
        /// </summary>
        private async Task LoadReleasesAsync(bool force)
        {
            if (_loadingReleases || RefreshReleasesBtn is null)
            {
                return;
            }

            _loadingReleases = true;
            RefreshReleasesBtn.IsEnabled = false;
            _releasesStage = force ? ReleasesStage.Refreshing : ReleasesStage.Loading;
            RefreshReleasesText.Text = L.Get(force
                ? "Version.Builtin.Action.Refreshing"
                : "Version.Builtin.Action.Loading");

            if (force)
            {
                ReleaseCacheText.Text = L.Get("Version.Builtin.Cache.Refreshing");
            }

            try
            {
                ReleaseResult result = await TavernReleases.GetAsync(force, CancellationToken.None);

                // 只在拿到列表时写共享状态；空结果说明连缓存都没有，保持「尚未获取」
                TavernInstances.SetReleases(result.Releases);
                ShowReleaseCache(result);

                if (result.Error is { Length: > 0 })
                {
                    // 手里还有列表时只是「没刷新成功」，别写成「不可用」吓人
                    Notify(
                        L.Get(result.Releases.Count > 0 ? "Version.Releases.RefreshFailed" : "Version.Releases.Unavailable"),
                        result.Error,
                        NotifySeverity.Warning);
                }
                else if (force)
                {
                    Notify(
                        L.Get("Version.Releases.Refreshed.Title"),
                        L.F("Version.Releases.Refreshed.Body", result.StableCount, result.Source),
                        NotifySeverity.Success);
                }
            }
            catch (Exception)
            {
                ReleaseCacheText.Text = L.Get("Version.Builtin.Cache.Failed");
                Notify(
                    L.Get("Version.Releases.Unavailable"),
                    L.Get("Version.Releases.ReadFailed"),
                    NotifySeverity.Error);
            }
            finally
            {
                _loadingReleases = false;
                RefreshReleasesBtn.IsEnabled = true;
                _releasesStage = ReleasesStage.Idle;
                RefreshReleasesText.Text = L.Get("Version.Builtin.Action.RefreshLabel");
            }
        }

        private void ShowReleaseCache(ReleaseResult result)
        {
            _releaseCache = result;

            if (result.Releases.Count == 0 || result.FetchedAt == DateTimeOffset.MinValue)
            {
                ReleaseCacheText.Text = L.Get("Version.Builtin.Cache.Empty");
                return;
            }

            string when = result.FromCache
                ? L.F("Version.Builtin.Cache.Cached", result.FetchedAt.ToLocalTime().ToString("MM-dd HH:mm"))
                : L.F("Version.Builtin.Cache.Updated", result.FetchedAt.ToLocalTime().ToString("HH:mm"));

            ReleaseCacheText.Text = L.F("Version.Builtin.Cache.Summary", when, result.Source, result.StableCount);
        }

        private async void RefreshReleases_Click(object sender, RoutedEventArgs e) =>
            await LoadReleasesAsync(force: true);

        /// <summary>
        /// 发布说明的排版交给 <see cref="ReleaseNotes"/>：关于页显示的启动器更新日志用的是同一份实现，
        /// 两处对同一段 Markdown 出字必须一致。
        /// </summary>
        private static string FormatNotes(string? notes) =>
            ReleaseNotes.Format(notes, L.Get("Version.Notes.Empty"));

        // ==================== 内置实例：其它操作 ====================

        private void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (TavernInstances.Releases.Count == 0)
            {
                Notify(
                    L.Get("Version.Check.NoList.Title"),
                    L.Get("Version.Check.NoList.Body"),
                    NotifySeverity.Informational);
                return;
            }

            TavernRelease? latest = TavernInstances.LatestRelease;
            if (latest is null)
            {
                Notify(
                    L.Get("Version.Check.NoStable.Title"),
                    L.Get("Version.Check.NoStable.Body"),
                    NotifySeverity.Warning);
                return;
            }

            if (TavernInstances.Builtin.Version == TavernInstances.StagingTag)
            {
                Notify(
                    L.F("Version.Check.Staging.Title", latest.Version),
                    L.F("Version.Check.Staging.Body", latest.Version),
                    NotifySeverity.Informational);
                return;
            }

            int delta = TavernReleases.Compare(latest.Version, TavernInstances.Builtin.Version);

            if (delta == 0)
            {
                Notify(
                    L.Get("Version.Check.UpToDate.Title"),
                    L.F("Version.Check.UpToDate.Body", TavernInstances.Builtin.Version),
                    NotifySeverity.Success);
                return;
            }

            if (delta > 0)
            {
                Notify(
                    L.F("Version.Check.New.Title", latest.Version),
                    L.F("Version.Check.New.Body", latest.Version),
                    NotifySeverity.Informational);
                return;
            }

            // 内置版本比列表还新：可能是本地改过 package.json 或用了未发布的构建，如实说明
            Notify(
                L.Get("Version.Check.Ahead.Title"),
                L.F("Version.Check.Ahead.Body", TavernInstances.Builtin.Version, latest.Version),
                NotifySeverity.Informational);
        }

        private void OpenBuiltinDir_Click(object sender, RoutedEventArgs e)
            => Notify(
                L.Get("Version.Dir.Title"),
                TavernInstances.Builtin.Path,
                NotifySeverity.Informational);

        private void CopyBuiltinPath_Click(object sender, RoutedEventArgs e)
        {
            var package = new DataPackage();
            package.SetText(TavernInstances.Builtin.Path);
            Clipboard.SetContent(package);

            Notify(
                L.Get("Version.CopyOk.Title"),
                TavernInstances.Builtin.Path,
                NotifySeverity.Success);
        }

        private async void ViewChangelog_Click(object sender, RoutedEventArgs e)
        {
            string target = (VersionPicker.SelectedItem as ComboBoxItem)?.Tag as string
                ?? TavernInstances.LatestStable;

            if (target == TavernInstances.StagingTag)
            {
                Notify(
                    L.Get("Version.Changelog.Staging.Title"),
                    L.Get("Version.Changelog.Staging.Body"),
                    NotifySeverity.Informational);
                return;
            }

            TavernRelease? release = TavernInstances.FindRelease(target) ?? TavernInstances.LatestRelease;
            if (release is null)
            {
                Notify(
                    L.Get("Version.Changelog.None.Title"),
                    L.Get("Version.Changelog.None.Body"),
                    NotifySeverity.Informational);
                return;
            }

            var body = new ScrollViewer
            {
                MaxHeight = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    Text = FormatNotes(release.Notes),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    FontSize = 13,
                    LineHeight = 21,
                },
            };

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = L.F("Version.Changelog.Title", release.Version),
                Content = body,
                CloseButtonText = L.Get("Common.Action.Close.Content"),
            };

            // 与页面按钮同一个圆角；发布说明较长，弹窗放宽一点
            dialog.Resources["ControlCornerRadius"] = new CornerRadius(8);
            dialog.Resources["ContentDialogMaxWidth"] = 720.0;

            await dialog.ShowAsync();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            // 磁盘才是真相：目录被删、在目录里 git pull 过，都要在这里对齐
            TavernInstances.DropMissingLocals();
            foreach (TavernInstance instance in TavernInstances.Locals.ToArray())
            {
                TavernInstances.ReloadLocal(instance);
            }

            SyncFromCatalog();

            var current = TavernInstances.Current;

            Notify(
                L.Get("Version.Refresh.Done.Title"),
                L.F(
                    "Version.Refresh.Done.Body",
                    TavernInstances.Locals.Count,
                    L.Get(current.IsBuiltin ? "Version.Scope.Builtin" : "Version.Scope.Local"),
                    current.KernelLabel),
                NotifySeverity.Informational);
        }

        // ==================== 反馈 ====================

        /// <summary>页面播报统一走全局消息（窗口右下角），页面里不再挂 InfoBar。</summary>
        private static void Notify(string title, string message, NotifySeverity severity)
            => AppNotify.Post(severity, title, message);
    }
}
