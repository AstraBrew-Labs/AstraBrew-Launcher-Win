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

        public string VersionLabel => Version.Length == 0 ? "版本未知" : "v" + Version;

        public string FixText => IsFixing ? "正在安装…" : "安装依赖";

        public bool FixEnabled => !IsFixing;

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
                ? $"当前 · 内置 {current.KernelLabel}"
                : $"当前 · 本地 {current.KernelLabel}";

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
                "已切换实例",
                $"后续启动将使用内置实例 {TavernInstances.Builtin.KernelLabel}。本地实例仍保留在列表中，随时可以切回。",
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
                ? "尚未扫描到实例，点「扫描」全盘查找，或用「导入」指定 package.json"
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

            Notify(
                "已切换实例",
                $"后续启动将使用本地 {target.KernelLabel}，目录：{target.Path}",
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
                    "正在安装依赖",
                    $"先等 {_fixingDeps.KernelLabel} 装完，一次只跑一份 npm，同时跑两份会互相抢网络与磁盘。",
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
                "正在安装依赖",
                $"{target.KernelLabel} 的依赖装在它自己的目录里，源为{LauncherSettings.NpmRegistryName}；"
                    + "大约需要下载 300 MB，期间可以继续用启动器做别的事。",
                NotifySeverity.Informational);

            TavernInstallResult result;
            try
            {
                result = await TavernInstaller.InstallDependenciesAsync(target.Path, null, CancellationToken.None);
            }
            catch (Exception ex)
            {
                result = new TavernInstallResult(false, "安装依赖时出错，请重试。", ex.Message, null, null, null);
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
                    "依赖已安装",
                    $"{target.KernelLabel} 的 Node 依赖已就位，现在可以切换使用。",
                    NotifySeverity.Success);
                return;
            }

            string detail = result.Message
                + (result.Hint is { Length: > 0 } hint ? " " + hint : string.Empty);

            AppNotify.Post(
                NotifySeverity.Error,
                "依赖没有装好",
                detail,
                result.LogPath is { Length: > 0 } path ? "查看日志" : null,
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
                Text = "这份代码在，只是缺 Node 依赖。依赖装完才能启动，代码不会被改动。",
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 22,
            });
            body.Children.Add(HintText($"安装位置　{target.Path}"));
            body.Children.Add(HintText($"包　源　{LauncherSettings.NpmRegistryName}（{LauncherSettings.NpmRegistryUrl}）"));
            body.Children.Add(HintText("源以参数形式传给这一次 npm，不改动全局 .npmrc；大约需要下载 300 MB、一到三分钟。"));

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"为 {target.KernelLabel} 安装依赖",
                Content = body,
                PrimaryButtonText = "开始安装",
                CloseButtonText = "取消",
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
                Title = $"从列表移除 {target.KernelLabel}？",
                Content = "只从启动器列表中移除，不会删除磁盘上的目录。",
                PrimaryButtonText = "移除",
                CloseButtonText = "取消",
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
                "已移除实例",
                wasCurrent
                    ? $"{target.KernelLabel} 不再出现在列表中，目录 {target.Path} 仍然保留；当前已回落到内置实例 {TavernInstances.Builtin.KernelLabel}。"
                    : $"{target.KernelLabel} 不再出现在列表中，目录 {target.Path} 仍然保留。",
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
                    "请选择 package.json",
                    "启动器靠 package.json 里的 name 字段判断这是不是酒馆目录，选别的文件没法验证。",
                    NotifySeverity.Warning);
                return;
            }

            // 实例路径是 package.json 所在的那一层
            string root = Path.GetDirectoryName(picked) ?? picked;

            switch (TavernInstances.AddLocal(root, out TavernInstance? added))
            {
                case LocalAddStatus.Added:
                    Notify(
                        "已添加本地实例",
                        $"{added!.KernelLabel} · {added.Path}"
                            + (added.DepsReady
                                ? "；依赖已就绪，可以直接切换使用。"
                                : "；还缺 Node 依赖，点行里的「安装依赖」补齐后才能启动。"),
                        NotifySeverity.Success);
                    break;

                case LocalAddStatus.Duplicate:
                    Notify(
                        "这个实例已经在列表里",
                        $"{root} 无需重复添加；要启用它，直接点行里的「使用」。",
                        NotifySeverity.Informational);
                    break;

                case LocalAddStatus.Builtin:
                    Notify(
                        "这是内置实例，不能重复添加",
                        "内置实例已经由「内置实例」页签托管，再加一份会出现两个入口指向同一份代码。"
                            + "请选择其他目录下的 package.json。",
                        NotifySeverity.Warning);
                    break;

                case LocalAddStatus.NotTavern:
                    Notify(
                        "这不是酒馆目录",
                        $"package.json 里的 name 不是 \"{InstanceScanner.PackageName}\"，启动器不会把它当成酒馆代码。",
                        NotifySeverity.Error);
                    break;

                default:
                    Notify(
                        "路径读不到",
                        $"没有找到 {root}，或者当前账户没有读取它的权限。",
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

            _syncingPicker = true;
            VersionPicker.Items.Clear();

            if (IsStaging)
            {
                VersionPicker.Items.Add(new ComboBoxItem
                {
                    Content = "staging · 开发分支最新",
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

                if (VersionPicker.Items.Count == 0)
                {
                    // 没缓存又没网时的兜底：不假装有版本可选，也不留一个空下拉框
                    VersionPicker.Items.Add(new ComboBoxItem
                    {
                        Content = "版本列表尚未获取",
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
                ? depsOnly ? "正在安装依赖…" : installed ? "正在切换…" : "正在安装…"
                : depsOnly
                    ? "安装依赖"
                    : same
                        ? "已是当前版本"
                        : target.Length > 0
                            ? $"{(installed ? "切换到" : "安装")} {DisplayVersion(target)}"
                            : installed ? "选择要切换的版本" : "选择要安装的版本";

            SwitchHintText.Text = depsOnly
                ? $"代码已就位，还差 Node 依赖；安装会临时使用「设置 → 下载」里的 {LauncherSettings.NpmRegistryName} 源，不改动全局 npm 配置。"
                : !installed
                    ? "将从「设置 → 下载」里选的通道拉取源码，并接着安装 Node 依赖；需要 Git 与 Node.js 已就绪。"
                    : !TavernInstances.IsBuiltinActive
                        ? "换版会覆盖代码并重装依赖，不改变当前使用的实例；换版后点右上角「使用内置实例」即可启用。"
                        : IsStaging
                            ? "开发版跟随 staging 分支，切换同样覆盖当前代码。"
                            : "切换会覆盖当前版本并重装依赖，旧版本不会保留。";
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
                    LauncherSettings.GithubAcceleration
                        ? "从 github.com 拉取，已开启加速服务（失败自动直连）。"
                        : "从 github.com 直接拉取，未经加速。");
                return;
            }

            if (revision.Length == 0)
            {
                ShowRoute(mirror: false, "先在版本列表里选一个版本，再比对镜像。");
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
            SourceRouteText.Text = $"正在比对 {LauncherSettings.ChannelName(channel)} 的版本…";

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
                ShowRoute(
                    mirror: false,
                    $"没能比对镜像（{error}），这次直接从 GitHub 拉取。");
                return;
            }

            ShowRoute(
                usable,
                usable
                    ? $"{LauncherSettings.ChannelName(channel)} 已同步 {DisplayVersion(revision)}，从镜像下载。"
                    : LauncherSettings.GithubAcceleration
                        ? $"{LauncherSettings.ChannelName(channel)} 没有 {DisplayVersion(revision)}，改用 GitHub；"
                            + "加速服务已开启，git 拉取会自动经中转（全局配置生效）。"
                        : $"{LauncherSettings.ChannelName(channel)} 没有 {DisplayVersion(revision)}，改用 GitHub 直连拉取。");
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
                BuiltinVersionText.Text = "未安装";
                MetaRow.Visibility = Visibility.Collapsed;
                LatestPill.Visibility = Visibility.Collapsed;
                return;
            }

            MetaRow.Visibility = Visibility.Visible;

            if (version == TavernInstances.StagingTag)
            {
                BuiltinVersionText.Text = TavernInstances.StagingTag;
                MetaChannelText.Text = "开发版";
                MetaDateText.Text = "跟随 staging 分支";
                MetaTagText.Text = "分支 staging";
                LatestPill.Visibility = Visibility.Collapsed;
                return;
            }

            BuiltinVersionText.Text = "v" + version;

            // 元信息只写发布列表里真有的事实：不编体积、不编文件数
            TavernRelease? release = TavernInstances.FindRelease(version);
            MetaChannelText.Text = release?.ChannelLabel ?? "稳定版";
            MetaDateText.Text = release is null
                ? "发布日期未知"
                : release.PublishedAt is null
                    ? "发布日期未知"
                    : "发布于 " + release.DateLabel;
            MetaTagText.Text = release is null ? "未在发布列表中" : "标签 " + release.Tag;

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

            string what = string.Join(" 与 ", missing);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"需要先安装 {what}",
                Content = new TextBlock
                {
                    Text = $"{what} 还没装好，酒馆装完也启动不了。\n\n"
                        + "在「设置 → 运行环境」里点对应的「安装」，装完回到这里再点一次即可。",
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 22,
                },
                PrimaryButtonText = "前往设置",
                CloseButtonText = "稍后再说",
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
                Text = "代码已经在本地了，只缺 Node 依赖。依赖装完才算安装完成。",
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 22,
            });
            body.Children.Add(new TextBlock
            {
                Text = $"安装位置　{root}",
                FontSize = 12,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
            body.Children.Add(new TextBlock
            {
                Text = $"包　源　{LauncherSettings.NpmRegistryName}（{LauncherSettings.NpmRegistryUrl}）",
                FontSize = 12,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
            body.Children.Add(new TextBlock
            {
                Text = "源以参数形式传给这一次 npm，不改动全局 .npmrc；大约需要下载 300 MB、一到三分钟。",
                FontSize = 12,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                TextWrapping = TextWrapping.Wrap,
            });

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "安装 Node 依赖",
                Content = body,
                PrimaryButtonText = "开始安装",
                CloseButtonText = "取消",
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
                    SwitchPercentText.Text = "安装中";
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
            SwitchPhaseText.Text = $"第 1/3 步 · 正在从{targets[0].Name}拉取 {DisplayVersion(revision)}";
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
                result = new TavernInstallResult(false, "安装时出错，请重试。", ex.Message, null, null, null);
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
            SwitchPhaseText.Text = $"正在用{LauncherSettings.NpmRegistryName}源安装依赖…";
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
                result = new TavernInstallResult(false, "安装依赖时出错，请重试。", ex.Message, null, null, null);
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

                SwitchPhaseText.Text = TavernInstances.BuiltinInstalled ? "安装未完成" : "安装失败";
                AppendSwitchLog(result.Message);
                if (result.Hint is { Length: > 0 } hint)
                {
                    AppendSwitchLog(hint);
                }

                if (result.LogPath is { Length: > 0 } path)
                {
                    AppendSwitchLog("日志 " + path);
                }

                UpdateSwitchButton();

                // 代码在、只缺依赖：这不是「装不上」，换个说法与颜色，别让人以为得从头再来
                bool needsDeps = TavernInstances.BuiltinInstalled && !TavernInstances.BuiltinDepsReady;

                string failTitle = needsDeps ? "依赖没有装好" : installed ? "换版失败" : "安装失败";
                string failBody = result.Message
                    + (result.Hint is { Length: > 0 } failHint ? " " + failHint : string.Empty);

                // 结果走全局消息：安装要跑一两分钟，用户很可能已经翻到别的页面，
                // 写在页面里的提示会跟着页面一起消失。
                string? failActionText = null;
                Action? failAction = null;
                if (result.LogPath is { Length: > 0 } logPath)
                {
                    failActionText = "查看日志";
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
            SwitchPhaseText.Text = depsOnly ? "依赖已装好" : installed ? "版本已切换" : "安装完成";
            AppendSwitchLog(result.Message);

            bool builtinActive = TavernInstances.IsBuiltinActive;

            string title = depsOnly ? "依赖已安装" : installed ? "版本已切换" : "内置实例已安装";

            // 正文按「一眼读完」写：耗时、路径这类细节留在页面日志里，
            // 全局消息只负责告诉用户「成了、下一步点哪儿」。
            string body;
            if (depsOnly)
            {
                body = builtinActive
                    ? $"内置实例 {TavernInstances.Builtin.KernelLabel} 的 Node 依赖已就位，可以在仪表盘启动。"
                    : $"内置实例 {TavernInstances.Builtin.KernelLabel} 的依赖已就位；切到内置实例即可启用。";
            }
            else if (installed)
            {
                body = builtinActive
                    ? $"内置实例现在使用 {TavernInstances.Builtin.KernelLabel}，代码与依赖都已更新。"
                    : $"内置实例的代码已更新为 {TavernInstances.Builtin.KernelLabel}；切到内置实例即可启用。";
            }
            else
            {
                body = builtinActive
                    ? $"{result.Message}；依赖也已就位，可以在仪表盘启动。"
                    : $"{result.Message}；切到内置实例即可启用。";
            }

            AppNotify.Success(title, body);
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
            => version == TavernInstances.StagingTag ? "开发版" : "v" + version;

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
            RefreshReleasesText.Text = force ? "正在刷新…" : "正在获取…";

            if (force)
            {
                ReleaseCacheText.Text = "正在向 GitHub 请求最新列表…";
            }

            try
            {
                ReleaseResult result = await TavernReleases.GetAsync(force, CancellationToken.None);

                // 只在拿到列表时写共享状态；空结果说明连缓存都没有，保持「尚未获取」
                TavernInstances.SetReleases(result.Releases);
                ShowReleaseCache(result);

                if (result.Error is { Length: > 0 })
                {
                    Notify(force ? "刷新失败" : "版本列表不可用", result.Error, NotifySeverity.Warning);
                }
                else if (force)
                {
                    Notify(
                        "版本列表已刷新",
                        $"共 {result.StableCount} 个稳定版，来源 {result.Source}，已写入缓存。",
                        NotifySeverity.Success);
                }
            }
            catch (Exception)
            {
                ReleaseCacheText.Text = "读取版本列表失败";
                Notify("版本列表不可用", "读取版本列表时出错，请稍后重试。", NotifySeverity.Error);
            }
            finally
            {
                _loadingReleases = false;
                RefreshReleasesBtn.IsEnabled = true;
                RefreshReleasesText.Text = "刷新";
            }
        }

        private void ShowReleaseCache(ReleaseResult result)
        {
            if (result.Releases.Count == 0 || result.FetchedAt == DateTimeOffset.MinValue)
            {
                ReleaseCacheText.Text = "尚未获取版本列表";
                return;
            }

            string when = result.FromCache
                ? $"缓存于 {result.FetchedAt.ToLocalTime():MM-dd HH:mm}"
                : $"已更新于 {result.FetchedAt.ToLocalTime():HH:mm}";

            ReleaseCacheText.Text = $"{when} · {result.Source} · {result.StableCount} 个稳定版";
        }

        private async void RefreshReleases_Click(object sender, RoutedEventArgs e) =>
            await LoadReleasesAsync(force: true);

        /// <summary>
        /// 发布说明是 Markdown，这里只做最小整理：标题去掉 <c>#</c>、列表换成圆点、压掉连续空行。
        /// 不引 Markdown 渲染器——为一段更新日志加一个渲染库不划算，纯文本也算能读。
        /// </summary>
        private static string FormatNotes(string? notes)
        {
            if (string.IsNullOrWhiteSpace(notes))
            {
                return "这条发布没有填写说明。";
            }

            var lines = new System.Collections.Generic.List<string>();
            foreach (string raw in notes.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.TrimEnd();
                string trimmed = line.TrimStart();

                if (trimmed.StartsWith('#'))
                {
                    line = trimmed.TrimStart('#').Trim();
                }
                else if (trimmed.StartsWith("* ") || trimmed.StartsWith("- "))
                {
                    line = "• " + trimmed[2..].TrimStart();
                }

                if (line.Length == 0 && lines.Count > 0 && lines[^1].Length == 0)
                {
                    continue;
                }

                lines.Add(line);
            }

            return string.Join(Environment.NewLine, lines).Trim();
        }

        // ==================== 内置实例：其它操作 ====================

        private void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (TavernInstances.Releases.Count == 0)
            {
                Notify(
                    "还没有版本列表",
                    "先点底部的「刷新」从 GitHub 获取发布记录，再检查更新。",
                    NotifySeverity.Informational);
                return;
            }

            TavernRelease? latest = TavernInstances.LatestRelease;
            if (latest is null)
            {
                Notify("没有稳定版", "发布列表里暂时没有标记为稳定版的版本。", NotifySeverity.Warning);
                return;
            }

            if (TavernInstances.Builtin.Version == TavernInstances.StagingTag)
            {
                Notify(
                    $"最新稳定版 v{latest.Version}",
                    $"内置实例当前跟随 staging 开发分支；要切回稳定版，在上方「切换版本」里选 v{latest.Version}。",
                    NotifySeverity.Informational);
                return;
            }

            int delta = TavernReleases.Compare(latest.Version, TavernInstances.Builtin.Version);

            if (delta == 0)
            {
                Notify(
                    "已是最新版本",
                    $"内置实例 v{TavernInstances.Builtin.Version} 是发布列表里最新的稳定版。",
                    NotifySeverity.Success);
                return;
            }

            if (delta > 0)
            {
                Notify(
                    $"发现新版本 v{latest.Version}",
                    $"在右侧「切换版本」中选择 v{latest.Version} 即可更新，旧版本会被覆盖。",
                    NotifySeverity.Informational);
                return;
            }

            // 内置版本比列表还新：可能是本地改过 package.json 或用了未发布的构建，如实说明
            Notify(
                "当前版本比发布列表更新",
                $"内置实例是 v{TavernInstances.Builtin.Version}，发布列表里最新的稳定版是 v{latest.Version}；"
                    + "可能来自本地修改或未发布的构建。",
                NotifySeverity.Informational);
        }

        private void OpenBuiltinDir_Click(object sender, RoutedEventArgs e)
            => Notify("安装目录", TavernInstances.Builtin.Path, NotifySeverity.Informational);

        private void CopyBuiltinPath_Click(object sender, RoutedEventArgs e)
        {
            var package = new DataPackage();
            package.SetText(TavernInstances.Builtin.Path);
            Clipboard.SetContent(package);

            Notify("路径已复制", TavernInstances.Builtin.Path, NotifySeverity.Success);
        }

        private async void ViewChangelog_Click(object sender, RoutedEventArgs e)
        {
            string target = (VersionPicker.SelectedItem as ComboBoxItem)?.Tag as string
                ?? TavernInstances.LatestStable;

            if (target == TavernInstances.StagingTag)
            {
                Notify(
                    "开发版没有单独的更新日志",
                    "开发版直接跟随 staging 分支，改动都记在提交历史里；稳定版的说明见各个版本的发布记录。",
                    NotifySeverity.Informational);
                return;
            }

            TavernRelease? release = TavernInstances.FindRelease(target) ?? TavernInstances.LatestRelease;
            if (release is null)
            {
                Notify(
                    "还没有更新日志",
                    "先点底部的「刷新」从 GitHub 获取发布记录。",
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
                Title = $"v{release.Version} 更新日志",
                Content = body,
                CloseButtonText = "关闭",
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
                "实例状态已刷新",
                $"本地 {TavernInstances.Locals.Count} 个实例 · 内置 1 个实例 · 当前使用 {(current.IsBuiltin ? "内置" : "本地")} {current.KernelLabel}。",
                NotifySeverity.Informational);
        }

        // ==================== 反馈 ====================

        /// <summary>页面播报统一走全局消息（窗口右下角），页面里不再挂 InfoBar。</summary>
        private static void Notify(string title, string message, NotifySeverity severity)
            => AppNotify.Post(severity, title, message);
    }
}
