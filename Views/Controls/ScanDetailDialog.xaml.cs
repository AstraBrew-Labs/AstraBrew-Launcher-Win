using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AstraBrew_Launcher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AstraBrew_Launcher.Views.Controls
{
    /// <summary>
    /// 扫描里的一块盘：一个进度环、还要多久、此刻翻到哪个目录。
    ///
    /// 属性都是可变 + 变更通知，因为它每 250ms 就被原地改一次；
    /// 换新对象会让 <see cref="ProgressRing"/> 每次重建，环的动画会一直从头开始。
    /// </summary>
    public sealed class ScanDriveRow : INotifyPropertyChanged
    {
        private double _percent;
        private string _percentText = "0%";
        private string _dirsText = string.Empty;
        private string _stateText = string.Empty;
        private string _current = string.Empty;
        private string _kindText = string.Empty;
        private Visibility _kindVisibility = Visibility.Collapsed;
        private bool _indeterminate;

        public ScanDriveRow(string root) => Root = root;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Root { get; }

        /// <summary>
        /// 这一轮扫描里显示过的最大百分比。
        /// 分母还在长的时候比例会往回掉，环跟着倒退会像卡住了又退回去，所以只让它涨。
        /// </summary>
        internal double Floor { get; set; }

        /// <summary>
        /// 还没拿到分母（本次扫描第一次跑）：环转圈而不是画弧。
        /// 这时候的比例是拿「已发现」当分母算的，一会儿就会被打回原形——
        /// 与其让环冲高再冻住，不如老实转圈，数字从「已查多少个」里看。
        /// </summary>
        public bool IsIndeterminate
        {
            get => _indeterminate;
            set => Set(ref _indeterminate, value);
        }

        /// <summary>0–100，直接喂给进度环。</summary>
        public double Percent
        {
            get => _percent;
            set => Set(ref _percent, value);
        }

        /// <summary>环心里的百分比文字。</summary>
        public string PercentText
        {
            get => _percentText;
            set => Set(ref _percentText, value);
        }

        /// <summary>形如「已查 12,345 个」。</summary>
        public string DirsText
        {
            get => _dirsText;
            set => Set(ref _dirsText, value);
        }

        /// <summary>形如「预计剩余 1 分 12 秒」/「已完成」。</summary>
        public string StateText
        {
            get => _stateText;
            set => Set(ref _stateText, value);
        }

        /// <summary>此刻扫到的目录，卡住不动时用来判断它是在啃大目录还是死了。</summary>
        public string Current
        {
            get => _current;
            set => Set(ref _current, value);
        }

        /// <summary>「机械」/「固态」；问不出来时为空。</summary>
        public string KindText
        {
            get => _kindText;
            set => Set(ref _kindText, value);
        }

        public Visibility KindVisibility
        {
            get => _kindVisibility;
            set => Set(ref _kindVisibility, value);
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>扫描详情里「找到的实例」的一行。</summary>
    public sealed class ScanHitRow
    {
        public string Version { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;
    }

    /// <summary>
    /// 扫描详情的观察窗口。
    ///
    /// 它不驱动扫描，也不持有进度：数据全部来自 <see cref="InstanceScan.State"/>，
    /// 每次状态变化重画一遍。因此关掉弹窗、重开弹窗都只是「换一个观察者」，
    /// 扫描本身在后台任务里继续跑——这也是「点扫描就是后台扫描」这条要求的落点。
    ///
    /// 这里只放右下角常驻消息里没有的东西：每块盘各自的完成度、剩余时间、当前目录。
    /// 「扫了多少、找到几个、用了多久」那句话已经在消息里了，不再抄一份。
    /// </summary>
    public sealed partial class ScanDetailDialog : ContentDialog
    {
        public ScanDetailDialog()
        {
            InitializeComponent();

            Opened += (_, _) =>
            {
                InstanceScan.Changed -= OnScanChanged;
                InstanceScan.Changed += OnScanChanged;
                Render();
            };

            Closed += (_, _) => InstanceScan.Changed -= OnScanChanged;
        }

        /// <summary>「扫描进度」的数据源；按盘复用行对象，只改数值。</summary>
        public ObservableCollection<ScanDriveRow> Drives { get; } = new();

        /// <summary>「找到的实例」的数据源。</summary>
        public ObservableCollection<ScanHitRow> Hits { get; } = new();

        private void OnScanChanged(object? sender, EventArgs e) => Render();

        private void CancelScan_Click(object sender, RoutedEventArgs e) => InstanceScan.Cancel();

        /// <summary>把当前快照整块画出来。状态只有一个来源，这里不做任何增量推断。</summary>
        private void Render()
        {
            ScanSnapshot state = InstanceScan.State;
            bool running = state.IsRunning;

            PhaseText.Text = state.Phase switch
            {
                ScanPhase.Scanning => "正在扫描本地磁盘…",
                ScanPhase.Done => "扫描完成",
                ScanPhase.Cancelled => "已停止扫描",
                ScanPhase.Failed => "扫描失败",
                _ => "尚未开始扫描",
            };

            PhaseHintText.Text = state.Phase switch
            {
                ScanPhase.Scanning => $"已用时 {state.ElapsedLabel}",
                ScanPhase.Idle => "正在列出磁盘…",
                _ => $"用时 {state.ElapsedLabel}",
            };

            RangeText.Text = state.Drives.Count == 0
                ? string.Empty
                : $"{state.Threads} 条线程 · {InstanceScanner.DescribeBudget()}";

            CancelScanBtn.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

            // 关窗文案跟着状态走：扫完再写「后台运行」会让人以为关掉还有事在跑
            CloseButtonText = running ? "后台运行" : "关闭";

            // 「新增」只有扫完才算得准（列表在扫的过程中一直在长），扫的时候只报已发现数
            AddedText.Text = running
                ? state.Found == 0 ? string.Empty : $"已发现 {state.Found} 个"
                : state.Added > 0 ? $"本次新增 {state.Added} 个" : "没有新增";

            // 只有机械盘才需要那句「要多等一会儿」：固态盘十几秒就完了，
            // 一提示反而让人以为出了什么问题
            HddNotice.IsOpen = running && HasHdd(state);

            // 新一轮扫描的头一秒：把「只涨不跌」的地板清掉，环要能从头再长一遍
            if (running && state.Elapsed < TimeSpan.FromSeconds(1))
            {
                foreach (ScanDriveRow row in Drives)
                {
                    row.Floor = 0;
                }
            }

            RebuildDrives(state, running);
            RebuildHits(state);

            HitEmptyText.Visibility = Hits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HitEmptyText.Text = state.Phase switch
            {
                ScanPhase.Scanning => "还没有找到酒馆目录，继续扫描中…",
                ScanPhase.Failed => "扫描没有完成，没有拿到结果。",
                _ => "没有找到 package.json 里 name 为 sillytavern 的目录。",
            };

            FootHintText.Text = state.Phase switch
            {
                ScanPhase.Scanning =>
                    "关掉这个窗口不会中断扫描：进度一直显示在右下角的消息里，点它的「查看详情」就能回到这里。",
                ScanPhase.Done =>
                    "找到的目录已加入「本地实例」列表；关掉目录或删掉文件的实例会在下次刷新时自动移除。",
                ScanPhase.Cancelled =>
                    "已停止扫描，已经找到的实例保留在列表中。重新点「扫描」会从头再扫一遍。",
                ScanPhase.Failed =>
                    string.IsNullOrWhiteSpace(state.Error) ? "扫描时出错，请重试。" : state.Error!,
                _ => string.Empty,
            };
        }

        private static bool HasHdd(ScanSnapshot state)
        {
            foreach (DriveProgress drive in state.Drives)
            {
                if (drive.Kind == DriveKind.Hdd)
                {
                    return true;
                }
            }

            return false;
        }

        private void RebuildDrives(ScanSnapshot state, bool running)
        {
            if (Drives.Count != state.Drives.Count || !SameRoots(state))
            {
                Drives.Clear();
                foreach (DriveProgress drive in state.Drives)
                {
                    Drives.Add(new ScanDriveRow(drive.Root));
                }
            }

            for (int i = 0; i < state.Drives.Count; i++)
            {
                Apply(Drives[i], state.Drives[i], state.Phase);
            }
        }

        private bool SameRoots(ScanSnapshot state)
        {
            for (int i = 0; i < state.Drives.Count; i++)
            {
                if (!string.Equals(Drives[i].Root, state.Drives[i].Root, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>把一块盘的当前状态写进它的行。</summary>
        private static void Apply(ScanDriveRow row, DriveProgress drive, ScanPhase phase)
        {
            bool running = phase == ScanPhase.Scanning;
            bool finished = drive.IsComplete;

            // 分母还没有（本次第一次扫）：环转圈，别用「已发现」这种会一直长的分母画弧
            bool guessing = running && !finished && drive.Estimating;

            // 扫完一律画满：预估分母可能偏大，让环停在 87% 会像是没扫干净。
            // 但「取消 / 失败」时只能画已经扫到的比例——那块盘确实没扫完，
            // 给个满环再配「已停止」是自相矛盾的。
            bool full = finished || phase == ScanPhase.Done;
            double percent = full
                ? 100
                : Math.Min(99, (drive.Ratio ?? 0) * 100);

            if (!full && !guessing)
            {
                percent = Math.Max(percent, row.Floor);
                row.Floor = percent;
            }

            row.IsIndeterminate = guessing;
            row.Percent = percent;
            row.PercentText = guessing ? "…" : $"{percent:0}%";
            // 还在扫的盘顺带给出「几条线程在扫」：固态盘扫完后它的线程会过来支援，
            // 这儿的数字会涨上去，用户看得见空闲核心没有闲着
            row.DirsText = running && !finished && drive.Workers > 0
                ? $"已查 {drive.Directories:N0} 个 · {drive.Workers} 线程"
                : $"已查 {drive.Directories:N0} 个";
            row.Current = drive.Current;

            row.StateText = phase switch
            {
                ScanPhase.Scanning when finished => "已完成",
                ScanPhase.Scanning when guessing => "正在估算时间…",
                ScanPhase.Scanning when drive.RemainingSeconds is { } seconds =>
                    "预计剩余 " + ScanSnapshot.DurationLabel(seconds),
                ScanPhase.Scanning when drive.Rate < 1 => "正在估算…",
                ScanPhase.Scanning => string.Empty,
                ScanPhase.Cancelled => "已停止",
                _ => "已完成",
            };

            (row.KindText, row.KindVisibility) = drive.Kind switch
            {
                DriveKind.Hdd => ("机械硬盘", Visibility.Visible),
                DriveKind.Ssd => ("固态硬盘", Visibility.Visible),
                _ => (string.Empty, Visibility.Collapsed),
            };
        }

        private void RebuildHits(ScanSnapshot state)
        {
            if (Hits.Count == state.Hits.Count)
            {
                bool same = true;
                for (int i = 0; i < state.Hits.Count; i++)
                {
                    if (Hits[i].Path != state.Hits[i].Path)
                    {
                        same = false;
                        break;
                    }
                }

                if (same)
                {
                    return;
                }
            }

            Hits.Clear();
            foreach (ScanHit hit in state.Hits)
            {
                Hits.Add(new ScanHitRow
                {
                    Version = hit.Version.Length == 0 ? "版本未知" : "v" + hit.Version,
                    Path = hit.Path,
                });
            }
        }
    }
}
