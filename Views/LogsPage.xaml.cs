using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AstraBrew_Launcher.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AstraBrew_Launcher.Views
{
    /// <summary>
    /// 一行日志的展示投影。
    ///
    /// 为什么不直接绑 <see cref="LogLine"/>：颜色是 UI 的事，放进 Services 的模型会把
    /// 读写层和 WinUI 绑死；这里只在页面内部做一层投影。
    /// </summary>
    public sealed class LogRow
    {
        public string TimeText { get; init; } = string.Empty;

        public string LevelTag { get; init; } = string.Empty;

        public string Text { get; init; } = string.Empty;

        public string FileName { get; init; } = string.Empty;

        /// <summary>原始时间：操作记录跳转时要靠它找到「那一刻」的那一行。</summary>
        public DateTime Time { get; init; }

        public Brush LevelBrush { get; init; } = new SolidColorBrush(Colors.Gray);

        /// <summary>
        /// 给 UI 自动化与朗读用。不重写的话 Name 会是类型全名（<c>AstraBrew_Launcher.Views.LogRow</c>），
        /// 屏幕阅读器念出来没有任何意义。
        /// </summary>
        public override string ToString() => $"{TimeText} {LevelTag} {Text}";
    }

    /// <summary>启动器页签左侧导航的一项（运行日志 / 下载日志 / 操作记录）。</summary>
    public sealed class SourceNavItem
    {
        public string Glyph { get; init; } = "\uE9D9";

        public string Label { get; init; } = string.Empty;

        public override string ToString() => Label;
    }

    /// <summary>操作记录的一行：动作 + 详情 + 点击后跳到哪份日志。</summary>
    public sealed class ActivityRow
    {
        public string TimeText { get; init; } = string.Empty;

        public string Text { get; init; } = string.Empty;

        public string Detail { get; init; } = string.Empty;

        public Visibility DetailVisibility { get; init; } = Visibility.Collapsed;

        public string Glyph { get; init; } = "\uE946";

        public Brush KindBrush { get; init; } = new SolidColorBrush(Colors.Gray);

        /// <summary>悬停说明：点这行会跳到哪儿。说不清就别给，别让人点了个寂寞。</summary>
        public string Hint { get; init; } = string.Empty;

        public ActivityEntry Entry { get; init; } = default!;

        public override string ToString() => $"{TimeText} {Text} {Detail}";
    }

    /// <summary>
    /// 日志页：启动器与酒馆两条线，可按时段与级别筛选。
    ///
    /// 数据来自磁盘（<see cref="LogCatalog"/>），不是内存——切走再回来、重启应用都还在。
    /// **不要**把 <see cref="TavernRuntime.Cleared"/> 接进来：那是内存缓冲的清空，
    /// 磁盘上的历史不该跟着消失。
    /// </summary>
    public sealed partial class LogsPage : Page
    {
        // ===== 筛选状态：放 static，切页返回后保留（页面每次导航都会重建） =====
        private static LogArea _area = LogArea.Launcher;
        private static int _rangeIndex;
        private static DateTimeOffset? _fromDate;
        private static DateTimeOffset? _toDate;
        private static bool _showInfo = true;
        private static bool _showWarning = true;
        private static bool _showError = true;
        /// <summary>启动器页签的内容来源：0 运行日志 / 1 下载日志 / 2 操作记录。</summary>
        private static int _source;

        /// <summary>只看这一份日志文件（操作记录里点「下载」类条目时设上）。</summary>
        private static string? _exactFile;

        /// <summary>
        /// 每种来源各自的时间范围（0 今天 / 1 近 7 天 / 2 近 30 天 / 3 全部）。
        ///
        /// 分开记是因为两类日志的分布完全不同：运行日志看今天，下载日志可能几个月才有一次——
        /// 共用一个范围，切到「下载日志」十有八九是一片空白，用户会以为功能坏了。
        /// </summary>
        private static readonly int[] RangeBySource = { 0, 3, 0 };
        private static bool _follow = true;

        private static readonly Brush SystemBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x2B, 0x88, 0xD8));
        private static readonly Brush InfoBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x6E, 0x76, 0x81));
        private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x16, 0xA3, 0x4A));
        private static readonly Brush WarningBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B));
        private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xD1, 0x34, 0x38));
        private static readonly Brush OutputBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A));

        private readonly ObservableCollection<LogRow> _rows = new();
        private readonly ObservableCollection<ActivityRow> _activities = new();
        private readonly List<SourceNavItem> _sourceItems = new();
        private readonly Queue<LogLine> _pending = new();

        private HashSet<LogLevel> _levels = new();
        private CancellationTokenSource? _loadCts;
        private DispatcherTimer? _flushTimer;
        private DispatcherTimer? _debounce;
        private bool _syncing;
        private bool _loaded;

        public LogsPage()
        {
            InitializeComponent();

            LogList.ItemsSource = _rows;
            ActivityList.ItemsSource = _activities;
            ActualThemeChanged += (_, _) => UpdateFollowLabel();
        }

        // ==================== 生命周期 ====================

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _loaded = true;

            ApplyStaticState();

            RuntimeLog.Written -= OnLogWritten;
            RuntimeLog.Written += OnLogWritten;
            RuntimeLog.Rolled -= OnLogRolled;
            RuntimeLog.Rolled += OnLogRolled;

            ActivityLog.Recorded -= OnActivityRecorded;
            ActivityLog.Recorded += OnActivityRecorded;

            LanguageSwitch.LanguageChanged -= OnLanguageChanged;
            LanguageSwitch.LanguageChanged += OnLanguageChanged;

            ApplyPlaceholders();

            BuildSourceNav();

            _ = LoadAsync();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _loaded = false;

            RuntimeLog.Written -= OnLogWritten;
            RuntimeLog.Rolled -= OnLogRolled;
            ActivityLog.Recorded -= OnActivityRecorded;
            LanguageSwitch.LanguageChanged -= OnLanguageChanged;

            _loadCts?.Cancel();
            _loadCts = null;

            _flushTimer?.Stop();
            _flushTimer = null;

            _debounce?.Stop();
            _debounce = null;

            _pending.Clear();
        }

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            ApplyPlaceholders();
            BuildSourceNav();
            UpdateCount();
            UpdateFollowLabel();
        }

        /// <summary>
        /// 日期框的占位文案只能走代码：<see cref="Controls.Loc"/> 的 Placeholder 只认
        /// TextBox / AutoSuggestBox，CalendarDatePicker 不在它的处理范围里。
        /// </summary>
        private void ApplyPlaceholders()
        {
            FromDate.PlaceholderText = L.Get("Logs.Range.Custom.From");
            ToDate.PlaceholderText = L.Get("Logs.Range.Custom.To");
            UpdateFollowLabel();
        }

        private void UpdateFollowLabel()
        {
            if (FollowLabel is null)
            {
                return;
            }

            FollowLabel.Foreground = FollowSwitch.IsOn
                ? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
                : (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        }

        // ==================== 筛选状态 <-> 控件 ====================

        private void ApplyStaticState()
        {
            _syncing = true;

            try
            {
                RangePicker.SelectedIndex = _rangeIndex;
                FromDate.Date = _fromDate;
                ToDate.Date = _toDate;
                InfoChip.IsChecked = _showInfo;
                WarningChip.IsChecked = _showWarning;
                ErrorChip.IsChecked = _showError;
                if (_source >= 0 && _source < _sourceItems.Count)
                {
                    SourceList.SelectedIndex = _source;
                }
                FollowSwitch.IsOn = _follow;

                LauncherTab.IsChecked = _area == LogArea.Launcher;
                TavernTab.IsChecked = _area == LogArea.Tavern;
                Pm2Tab.IsChecked = _area == LogArea.Pm2;
            }
            finally
            {
                _syncing = false;
            }

            _levels = BuildLevels();

            SyncSourceUi();

            ClearDatesButton.Visibility = _fromDate is null && _toDate is null
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (_syncing || !_loaded)
            {
                return;
            }

            _area = ReferenceEquals(sender, TavernTab) ? LogArea.Tavern
                : ReferenceEquals(sender, Pm2Tab) ? LogArea.Pm2
                : LogArea.Launcher;

            // 换页签就不再锁定某一份安装日志：那是「启动器 → 下载日志」里的临时视图
            if (_area != LogArea.Launcher)
            {
                _exactFile = null;
            }

            // 只有启动器页签分「运行 / 下载 / 操作」，其余页签跟着收起
            SyncSourceUi();

            _ = LoadAsync();
        }

        /// <summary>按当前语言重建左侧导航的三项。</summary>
        private void BuildSourceNav()
        {
            if (SourceList is null)
            {
                return;
            }

            int selected = _source;

            _sourceItems.Clear();
            _sourceItems.Add(new SourceNavItem { Glyph = "\uE9D9", Label = L.Get("Logs.Source.0") });
            _sourceItems.Add(new SourceNavItem { Glyph = "\uE896", Label = L.Get("Logs.Source.1") });
            _sourceItems.Add(new SourceNavItem { Glyph = "\uE81C", Label = L.Get("Logs.Source.2") });

            SourceList.ItemsSource = null;
            SourceList.ItemsSource = _sourceItems;

            if (selected >= 0 && selected < _sourceItems.Count)
            {
                SourceList.SelectedIndex = selected;
            }
        }

        /// <summary>左侧导航换了类别：清掉「只看这一份」，否则会莫名其妙只看一个文件。</summary>
        private void Source_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || !_loaded || SourceList.SelectedIndex < 0)
            {
                return;
            }

            int previous = _source;
            int next = SourceList.SelectedIndex;

            if (previous >= 0 && previous < RangeBySource.Length)
            {
                RangeBySource[previous] = _rangeIndex;
            }

            _source = next;
            _exactFile = null;

            if (next >= 0 && next < RangeBySource.Length)
            {
                _rangeIndex = RangeBySource[next];
                _fromDate = null;
                _toDate = null;

                _syncing = true;
                try
                {
                    RangePicker.SelectedIndex = _rangeIndex;
                    FromDate.Date = null;
                    ToDate.Date = null;
                }
                finally
                {
                    _syncing = false;
                }
            }

            SyncSourceUi();
            _ = LoadAsync();
        }

        /// <summary>按页签与来源决定显示哪张列表、哪些筛选能用。</summary>
        private void SyncSourceUi()
        {
            if (SourceSidebar is null || ActivityList is null)
            {
                return;
            }

            bool launcher = _area == LogArea.Launcher;

            SourceSidebar.Visibility = launcher ? Visibility.Visible : Visibility.Collapsed;

            // 酒馆与 PM2 页签只有运行日志，来源标签收起来时按「运行」算
            bool activity = launcher && _source == 2;

            ActivityList.Visibility = activity ? Visibility.Visible : Visibility.Collapsed;
            LogList.Visibility = activity ? Visibility.Collapsed : Visibility.Visible;

            // 级别与跟随只对日志行有意义；操作记录没有级别，整行收起（留一条空带子更难看）
            LevelChipsRow.Visibility = activity ? Visibility.Collapsed : Visibility.Visible;
            var chips = activity ? Visibility.Collapsed : Visibility.Visible;
            InfoChip.Visibility = chips;
            WarningChip.Visibility = chips;
            ErrorChip.Visibility = chips;

            FollowSwitch.Visibility = chips;
            FollowLabel.Visibility = chips;

            UpdateScopeChip();
            UpdateCount();
            UpdateEmptyState();
        }

        /// <summary>「只看 xxx.log」胶囊：显示当前锁定的是哪份文件，点掉就回到按日期浏览。</summary>
        private void UpdateScopeChip()
        {
            if (ScopeChip is null)
            {
                return;
            }

            if (_exactFile is { Length: > 0 } file)
            {
                ScopeChip.Content = L.F("Logs.Scope.File", Path.GetFileName(file));
                ScopeChip.Visibility = Visibility.Visible;
            }
            else
            {
                ScopeChip.Visibility = Visibility.Collapsed;
            }
        }

        private void ClearScope_Click(object sender, RoutedEventArgs e)
        {
            _exactFile = null;
            UpdateScopeChip();
            _ = LoadAsync();
        }

        /// <summary>时间预设（SegmentedPicker 用的是 EventHandler，不是 RoutedEventHandler）。</summary>
        private void Range_Changed(object? sender, EventArgs e) => OnFilterChanged();

        private void Level_Changed(object sender, RoutedEventArgs e) => OnFilterChanged();

        private void OnFilterChanged()
        {
            if (_syncing || !_loaded)
            {
                return;
            }

            CaptureFilterState();
            _levels = BuildLevels();
            ScheduleReload();
        }

        private void Date_Changed(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
        {
            if (_syncing || !_loaded)
            {
                return;
            }

            CaptureFilterState();
            _levels = BuildLevels();
            ScheduleReload();
        }

        private void ClearDates_Click(object sender, RoutedEventArgs e)
        {
            _syncing = true;
            FromDate.Date = null;
            ToDate.Date = null;
            _syncing = false;

            CaptureFilterState();
            ScheduleReload();
        }

        private void Follow_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncing)
            {
                return;
            }

            _follow = FollowSwitch.IsOn;
            UpdateFollowLabel();

            if (_follow && _rows.Count > 0)
            {
                LogList.ScrollIntoView(_rows[^1]);
            }
        }

        private void CaptureFilterState()
        {
            _rangeIndex = RangePicker.SelectedIndex < 0 ? 0 : RangePicker.SelectedIndex;
            _fromDate = FromDate.Date;
            _toDate = ToDate.Date;

            _showInfo = InfoChip.IsChecked == true;
            _showWarning = WarningChip.IsChecked == true;
            _showError = ErrorChip.IsChecked == true;

            _source = SourceList.SelectedIndex >= 0 ? SourceList.SelectedIndex : _source;

            ClearDatesButton.Visibility = _fromDate is null && _toDate is null
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void ScheduleReload()
        {
            _debounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _debounce.Stop();
            _debounce.Tick -= Debounce_Tick;
            _debounce.Tick += Debounce_Tick;
            _debounce.Start();
        }

        private void Debounce_Tick(object? sender, object e)
        {
            _debounce?.Stop();
            _ = LoadAsync();
        }

        // ==================== 查询构建 ====================

        /// <summary>
        /// 级别筛选归一：三个 chip 全选等于「不筛」（传空集），
        /// 单勾「信息」时要连 SYS / OK / OUT 一起放出来，否则用户会觉得丢了行。
        /// </summary>
        private HashSet<LogLevel> BuildLevels()
        {
            if (_showInfo && _showWarning && _showError)
            {
                return new HashSet<LogLevel>();
            }

            var levels = new HashSet<LogLevel>();

            if (_showInfo)
            {
                levels.Add(LogLevel.Info);
                levels.Add(LogLevel.System);
                levels.Add(LogLevel.Success);
                levels.Add(LogLevel.Output);
            }

            if (_showWarning)
            {
                levels.Add(LogLevel.Warning);
            }

            if (_showError)
            {
                levels.Add(LogLevel.Error);
            }

            return levels;
        }

        /// <summary>时间范围：自定义日期优先，其次才是预设区间。</summary>
        private (DateTime? From, DateTime? To) ResolveRange()
        {
            if (_fromDate is not null || _toDate is not null)
            {
                DateTime? from = _fromDate?.Date;
                DateTime? to = _toDate is { } end ? end.Date.AddDays(1).AddTicks(-1) : null;
                return (from, to);
            }

            return _rangeIndex switch
            {
                0 => (DateTime.Today, null),
                1 => (DateTime.Today.AddDays(-6), null),
                2 => (DateTime.Today.AddDays(-29), null),
                _ => (null, null),
            };
        }

        private LogQuery BuildQuery()
        {
            (DateTime? from, DateTime? to) = ResolveRange();

            return new LogQuery(
                _area,
                from,
                to,
                _levels,
                _area != LogArea.Launcher || _source == 0,
                _area == LogArea.Launcher && _source == 1)
            {
                // 只有「启动器 → 下载日志」才认这份锁定；切到酒馆 / PM2 页签时它必须失效，
                // 否则会出现「酒馆日志页签里显示 caddy 安装日志」这种串台
                ExactFile = _area == LogArea.Launcher && _source == 1 ? _exactFile : null,
            };
        }

        // ==================== 加载 ====================

        private async Task LoadAsync()
        {
            if (!_loaded)
            {
                return;
            }

            // 操作记录是另一套数据（JSONL + 可点击），走自己的加载路径
            if (_area == LogArea.Launcher && _source == 2)
            {
                await LoadActivityAsync();
                return;
            }

            _loadCts?.Cancel();

            var cts = new CancellationTokenSource();
            _loadCts = cts;

            LoadingRing.IsActive = true;
            EmptyState.Visibility = Visibility.Collapsed;

            LogQuery query = BuildQuery();

            try
            {
                LogLoadResult result = await Task.Run(() => LogCatalog.Load(query, cts.Token), cts.Token);

                if (cts.IsCancellationRequested)
                {
                    return;
                }

                _pending.Clear();
                _rows.Clear();

                foreach (LogLine line in result.Lines)
                {
                    _rows.Add(Project(line));
                }

                UpdateCount();
                UpdateEmptyState();
                UpdateFootnote(result.Truncated);

                if (_follow && _rows.Count > 0)
                {
                    LogList.ScrollIntoView(_rows[^1]);
                }
            }
            catch (OperationCanceledException)
            {
                // 被新的筛选条件顶掉：结果丢弃即可
            }
            catch (Exception)
            {
                // 读盘失败不该让整页白掉：留空状态，用户可重试
                UpdateEmptyState();
            }
            finally
            {
                if (!cts.IsCancellationRequested)
                {
                    LoadingRing.IsActive = false;
                }
            }
        }

        /// <summary>
        /// 载入操作记录。与日志行不同，记录**新的在前**：用户翻操作记录是想看「刚才干了什么」。
        /// </summary>
        private async Task LoadActivityAsync()
        {
            if (!_loaded)
            {
                return;
            }

            _loadCts?.Cancel();

            var cts = new CancellationTokenSource();
            _loadCts = cts;

            LoadingRing.IsActive = true;
            EmptyState.Visibility = Visibility.Collapsed;

            (DateTime? from, DateTime? to) = ResolveRange();

            // 没选范围时给个够用的默认：操作记录保留 90 天，一次读太多也没意义
            DateTime start = from ?? DateTime.Today.AddDays(-89);
            DateTime end = to ?? DateTime.Today.AddDays(1).AddTicks(-1);

            try
            {
                IReadOnlyList<ActivityEntry> entries = await Task.Run(
                    () => ActivityLog.Load(start, end, 800),
                    cts.Token);

                if (cts.IsCancellationRequested)
                {
                    return;
                }

                _activities.Clear();

                foreach (ActivityEntry entry in entries)
                {
                    _activities.Add(ProjectActivity(entry));
                }

                UpdateCount();
                UpdateEmptyState();
                UpdateFootnote(false);
            }
            catch (OperationCanceledException)
            {
                // 被新的筛选顶掉
            }
            catch (Exception)
            {
                UpdateEmptyState();
            }
            finally
            {
                if (!cts.IsCancellationRequested)
                {
                    LoadingRing.IsActive = false;
                }
            }
        }

        /// <summary>
        /// 点一条操作记录：跳到它对应的日志。
        /// · 下载 / 安装类有具体文件 → 直接只显示那份安装日志；
        /// · 其余（改设置、启停）→ 回到运行日志并把视图滚到当时那一行。
        /// </summary>
        private async void Activity_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not ActivityRow row)
            {
                return;
            }

            ActivityEntry entry = row.Entry;

            _area = LogArea.Launcher;

            if (entry.LogFile is { Length: > 0 } file && File.Exists(file))
            {
                _source = 1;
                _exactFile = file;
                _fromDate = null;
                _toDate = null;
                _rangeIndex = 3;
            }
            else
            {
                _source = 0;
                _exactFile = null;

                // 定位到那一天：日期框是「天」粒度，精确到秒只能靠滚
                _fromDate = entry.LogAt.Date;
                _toDate = entry.LogAt.Date;
                _rangeIndex = 3;
            }

            ApplyStaticState();

            await LoadAsync();

            if (_source == 0)
            {
                ScrollToTime(entry.LogAt);
            }
        }

        /// <summary>把列表滚到「第一行晚于这个时刻」的前一行。找不到就滚到末尾。</summary>
        private void ScrollToTime(DateTime at)
        {
            if (_rows.Count == 0)
            {
                return;
            }

            LogRow target = _rows[0];

            foreach (LogRow row in _rows)
            {
                if (row.Time <= at)
                {
                    target = row;
                }
                else
                {
                    break;
                }
            }

            LogList.ScrollIntoView(target);
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.IO.Directory.CreateDirectory(RuntimeLog.FolderPath);
                ShellFile.Reveal(RuntimeLog.FolderPath);
            }
            catch (Exception ex)
            {
                AppNotify.Error(L.Get("Logs.Action.OpenFolder"), ex.Message);
            }
        }

        // ==================== 实时追加 ====================

        private void OnLogWritten(object? sender, LogLine line)
        {
            // 事件已在 UI 线程（RuntimeLog 内部投递），但仍要保证页面还活着
            if (!_loaded || (_area == LogArea.Launcher && _source == 2) || !MatchesCurrent(line))
            {
                return;
            }

            _pending.Enqueue(line);

            if (_flushTimer is null)
            {
                _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                _flushTimer.Tick += (_, _) => FlushPending();
                _flushTimer.Start();
            }
        }

        /// <summary>跨天 / 分卷：行来源变了，整屏重读比继续追加可靠。</summary>
        private void OnLogRolled(object? sender, EventArgs e)
        {
            if (_loaded)
            {
                _ = LoadAsync();
            }
        }

        private void FlushPending()
        {
            if (_pending.Count == 0)
            {
                _flushTimer?.Stop();
                _flushTimer = null;
                return;
            }

            LogRow? last = null;

            while (_pending.Count > 0)
            {
                last = Project(_pending.Dequeue());
                _rows.Add(last);
            }

            // 与读取上限保持一致：页面滚动区不该无限长
            while (_rows.Count > LogCatalog.MaxLinesPerArea)
            {
                _rows.RemoveAt(0);
            }

            UpdateCount();
            UpdateEmptyState();

            if (_follow && last is not null)
            {
                LogList.ScrollIntoView(last);
            }
        }

        private bool MatchesCurrent(LogLine line)
        {
            if (line.Area != _area)
            {
                return false;
            }

            // 启动器页签按来源分流：运行日志与下载日志不会同时出现
            if (_area == LogArea.Launcher
                && ((line.Origin == LogOriginKind.Runtime && _source != 0)
                    || (line.Origin == LogOriginKind.Install && _source != 1)))
            {
                return false;
            }

            (DateTime? from, DateTime? to) = ResolveRange();

            if (from is { } start && line.Time < start)
            {
                return false;
            }

            if (to is { } end && line.Time > end)
            {
                return false;
            }

            return _levels.Count == 0 || _levels.Contains(line.Level);
        }

        // ==================== 展示 ====================

        private static LogRow Project(LogLine line) => new()
        {
            Time = line.Time,
            TimeText = line.TimeText,
            LevelTag = line.LevelTag,
            Text = line.Text,
            FileName = line.FileName,
            LevelBrush = BrushFor(line.Level),
        };

        private static ActivityRow ProjectActivity(ActivityEntry entry)
        {
            (string glyph, Brush brush, string hintKey) = entry.Kind switch
            {
                ActivityCategory.Setting => ("\uE713", SystemBrush, "Logs.Activity.Hint.Setting"),
                ActivityCategory.Service => ("\uE768", SuccessBrush, "Logs.Activity.Hint.Service"),
                ActivityCategory.Install => ("\uE896", InfoBrush, "Logs.Activity.Hint.Install"),
                ActivityCategory.Scan => ("\uE721", WarningBrush, "Logs.Activity.Hint.Scan"),
                _ => ("\uE946", InfoBrush, "Logs.Activity.Hint.Setting"),
            };

            string hint = entry.LogFile is { Length: > 0 } file
                ? L.F("Logs.Activity.Hint.File", Path.GetFileName(file))
                : L.Get(hintKey);

            return new ActivityRow
            {
                TimeText = entry.Time.ToString("MM-dd HH:mm:ss"),
                Text = entry.Text,
                Detail = entry.Detail ?? string.Empty,
                DetailVisibility = string.IsNullOrWhiteSpace(entry.Detail)
                    ? Visibility.Collapsed
                    : Visibility.Visible,
                Glyph = glyph,
                KindBrush = brush,
                Hint = hint,
                Entry = entry,
            };
        }

        /// <summary>新记录实时插到最前面：用户刚改完设置就会来看这条。</summary>
        private void OnActivityRecorded(object? sender, ActivityEntry entry)
        {
            if (!_loaded || _area != LogArea.Launcher || _source != 2)
            {
                return;
            }

            (DateTime? from, DateTime? to) = ResolveRange();

            if ((from is { } start && entry.Time < start) || (to is { } end && entry.Time > end))
            {
                return;
            }

            _activities.Insert(0, ProjectActivity(entry));

            while (_activities.Count > 800)
            {
                _activities.RemoveAt(_activities.Count - 1);
            }

            UpdateCount();
            UpdateEmptyState();
        }

        private static Brush BrushFor(LogLevel level) => level switch
        {
            LogLevel.System => SystemBrush,
            LogLevel.Success => SuccessBrush,
            LogLevel.Warning => WarningBrush,
            LogLevel.Error => ErrorBrush,
            LogLevel.Output => OutputBrush,
            _ => InfoBrush,
        };

        private void UpdateCount()
        {
            if (CountText is null)
            {
                return;
            }

            bool activity = _area == LogArea.Launcher && _source == 2;

            CountText.Text = activity
                ? L.F("Logs.Count.Activity", _activities.Count)
                : L.F("Logs.Count.Format", _rows.Count);
        }

        private void UpdateEmptyState()
        {
            if (EmptyState is null)
            {
                return;
            }

            bool activity = _area == LogArea.Launcher && _source == 2;
            int count = activity ? _activities.Count : _rows.Count;

            EmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateFootnote(bool truncated)
        {
            if (FootnoteText is null)
            {
                return;
            }

            FootnoteText.Text = truncated
                ? L.F("Logs.Footnote.Limit", LogCatalog.MaxLinesPerArea)
                : string.Empty;

            FootnoteText.Visibility = truncated ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
