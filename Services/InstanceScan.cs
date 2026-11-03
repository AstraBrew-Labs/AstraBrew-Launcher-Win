using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace AstraBrew_Launcher.Services
{
    /// <summary>一次全盘扫描所处的阶段。</summary>
    public enum ScanPhase
    {
        /// <summary>本次运行还没扫过。</summary>
        Idle,

        Scanning,
        Done,
        Cancelled,
        Failed,
    }

    /// <summary>
    /// 扫描的对外状态（不可变快照）。
    ///
    /// 列表顶部的摘要与扫描详情弹窗读的是同一份：弹窗只是把 <see cref="Drives"/>、
    /// <see cref="Hits"/> 这些细节摊开，摘要把它们压成一句话，不存在两份进度各算各的。
    /// </summary>
    public sealed record ScanSnapshot(
        ScanPhase Phase,
        TimeSpan Elapsed,
        int Threads,
        IReadOnlyList<DriveProgress> Drives,
        long Directories,
        IReadOnlyList<ScanHit> Hits,
        int Added,
        string? Error)
    {
        public static ScanSnapshot None { get; } = new(
            ScanPhase.Idle,
            TimeSpan.Zero,
            0,
            Array.Empty<DriveProgress>(),
            0,
            Array.Empty<ScanHit>(),
            0,
            null);

        public bool IsRunning => Phase == ScanPhase.Scanning;

        /// <summary>本次运行是否扫过（成功、取消、失败都算扫过，界面据此决定要不要显示摘要条）。</summary>
        public bool HasRun => Phase != ScanPhase.Idle;

        public int Found => Hits.Count;

        /// <summary>形如 <c>C:\  ·  D:\</c>，列表顶部那一行「扫描范围」用。</summary>
        public string RootsText => Drives.Count == 0
            ? "尚未确定扫描范围"
            : string.Join("  ·  ", Drives.Select(drive => drive.Root));

        /// <summary>形如「42 秒」；不到一秒说「不到 1 秒」，不显示 0 秒。</summary>
        public string ElapsedLabel => DurationLabel(Elapsed.TotalSeconds);

        /// <summary>
        /// 还要多久：取最慢的那块盘。几块盘是并行扫的，所以整场扫描的结束时间由最慢的一块决定，
        /// 把所有盘的剩余时间加起来只会吓人。都算不出来时为 null。
        /// </summary>
        public double? RemainingSeconds
        {
            get
            {
                double? slowest = null;
                foreach (DriveProgress drive in Drives)
                {
                    // 分母还在长的那些盘算出来的时间是假的，不参与
                    if (drive.Estimating)
                    {
                        continue;
                    }

                    if (drive.RemainingSeconds is not { } seconds)
                    {
                        continue;
                    }

                    if (slowest is null || seconds > slowest)
                    {
                        slowest = seconds;
                    }
                }

                return slowest;
            }
        }

        /// <summary>形如「预计剩余 1 分 12 秒」；算不出来时给「正在估算…」。</summary>
        public string RemainingLabel => RemainingSeconds is { } seconds
            ? "预计剩余 " + DurationLabel(seconds)
            : "正在估算时间…";

        public static string DurationLabel(double seconds)
        {
            if (seconds < 1)
            {
                return "不到 1 秒";
            }

            if (seconds < 60)
            {
                return $"{seconds:0} 秒";
            }

            if (seconds < 3600)
            {
                return $"{(int)(seconds / 60)} 分 {(int)(seconds % 60)} 秒";
            }

            return $"{(int)(seconds / 3600)} 小时 {(int)(seconds % 3600 / 60)} 分";
        }
    }

    /// <summary>
    /// 后台扫描会话：全应用只有一次扫描在跑，与哪个页面在显示无关。
    ///
    /// 为什么把状态放在页面之外：扫一遍全盘要几十秒到几分钟，用户点完「扫描」很可能
    /// 立刻去做别的事。状态挂在页面上，页面一卸载扫描就断了；回来只能看到一个假的进度条。
    /// 这里让扫描活在自己的任务里，弹窗只是它的一个观察窗口：关掉不中断、随时可重开，
    /// 列表顶部的摘要条读的也是同一份快照。
    ///
    /// 线程约定：<see cref="Start"/> 与 <see cref="Cancel"/> 在 UI 线程调用；
    /// <see cref="Changed"/> 一律在 UI 线程触发，订阅者可以直接改控件。
    /// </summary>
    public static class InstanceScan
    {
        private static readonly object Gate = new();

        private static CancellationTokenSource? _cancellation;
        private static DispatcherQueue? _queue;
        private static ScanSnapshot _state = ScanSnapshot.None;

        /// <summary>本次扫描开始时列表里已有的实例数，用来算「新增几个」。</summary>
        private static int _baseline;

        /// <summary>状态变化（开始 / 进度 / 结束）。始终在 UI 线程触发。</summary>
        public static event EventHandler? Changed;

        /// <summary>
        /// 常驻进度消息的钥匙。同一条消息随进度原地改字，
        /// 不然后台扫一趟全盘能在右下角堆出几百张卡片。
        /// </summary>
        private const string LiveKey = "scan.live";

        /// <summary>
        /// 「查看详情」按钮的动作。扫描是服务层的事，不该认识页面；
        /// 由主窗口在启动时接上，点一下就是「切到版本管理 + 打开弹窗」。
        /// </summary>
        public static Action? OpenDetail { get; set; }

        /// <summary>当前状态快照；只读。</summary>
        public static ScanSnapshot State => _state;

        public static bool IsRunning => _state.IsRunning;

        /// <summary>
        /// 开始一次扫描。已经在扫就什么都不做——页面按钮这时应该去开详情弹窗，
        /// 而不是再起一遍：全盘扫两遍只会互相拖慢。
        /// </summary>
        public static void Start()
        {
            CancellationTokenSource cancellation;

            lock (Gate)
            {
                if (_cancellation is not null)
                {
                    return;
                }

                _queue ??= DispatcherQueue.GetForCurrentThread();
                _baseline = TavernInstances.Locals.Count;
                cancellation = new CancellationTokenSource();
                _cancellation = cancellation;

                List<string> drives = InstanceScanner.ReadyRoots(out _);
                int[] allocation = InstanceScanner.AllocateThreads(drives.Count);
                _state = new ScanSnapshot(
                    ScanPhase.Scanning,
                    TimeSpan.Zero,
                    allocation.Sum(),

                    // 盘先摆上、计数为 0：进度消息第一句就能报出扫描范围，
                    // 不用等第一条进度回来才知道在扫哪几块盘。
                    // 分母先取上次扫完记下来的目录数，这次扫出来会实时修正
                    drives.Select((root, index) => new DriveProgress(
                        root,
                        root,
                        0,
                        0,
                        ScanStats.Expected(root),
                        0,
                        ScanStats.Expected(root) == 0,
                        PhysicalDrive.KindOf(root),
                        allocation[index],
                        allocation[index])).ToArray(),
                    0,
                    Array.Empty<ScanHit>(),
                    0,
                    null);
            }

            Raise();
            PublishLive();

            // 令牌在锁内取好：下一轮扫描会换掉 _cancellation 字段，别让任务读到新的那个
            _ = Task.Run(() => RunAsync(cancellation.Token));
        }

        /// <summary>请求停止。已找到的实例留在列表里——扫到的东西没理由因为取消就丢掉。</summary>
        public static void Cancel()
        {
            lock (Gate)
            {
                _cancellation?.Cancel();
            }
        }

        private static async Task RunAsync(CancellationToken token)
        {
            // 启动器自己的目录一律不扫：内置实例归「内置实例」页签管，
            // 扫出来再拒绝一遍纯属浪费；暂存目录里还可能是半份代码
            var skip = new List<string>
            {
                TavernInstances.BuiltinInstallRoot,
                TavernInstaller.StagingRoot,
                AppPaths.TempRoot,
                AppPaths.RoamingRoot,
            };

            ScanReport report;
            try
            {
                report = await InstanceScanner.ScanAsync(skip, new ProgressProxy(), OnHit, token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Post(() => Finish(null, "扫描已取消。", ScanPhase.Cancelled));
                return;
            }
            catch (Exception ex)
            {
                Post(() => Finish(null, ex.Message, ScanPhase.Failed));
                return;
            }

            Post(() => Finish(report, null, report.Cancelled ? ScanPhase.Cancelled : ScanPhase.Done));
        }

        /// <summary>
        /// 每命中一个就立刻进列表：扫描期间翻回列表就能看到实例在长，
        /// 而不是等全盘扫完才一次性冒出来。命中在 UI 线程落到 <see cref="TavernInstances"/>。
        /// </summary>
        private static void OnHit(ScanHit hit) => Post(() => TavernInstances.AddLocal(hit.Path, out _));

        /// <summary>把一次进度快照落到状态上；只更新扫描中该动的字段。</summary>
        private static void Apply(ScanProgress progress)
        {
            if (!_state.IsRunning)
            {
                return;
            }

            _state = _state with
            {
                Elapsed = progress.Elapsed,
                Drives = progress.Drives,
                Directories = progress.Directories,
                Hits = progress.Hits,
            };

            Raise();
            PublishLive();
        }

        /// <summary>
        /// 把进度同步到右下角的常驻消息上。
        ///
        /// 这条消息挂在主窗口而不是页面上，所以用户翻到设置、装扩展、看日志时
        /// 「正在扫描、已经扫了多少」一直都在；也因为它不可关闭，不会出现
        /// 「关掉提示以为扫描停了」这种误解——要停只能按弹窗里的「取消扫描」。
        /// </summary>
        private static void PublishLive()
        {
            if (!_state.IsRunning)
            {
                return;
            }

            // 第二行给「还要多久」而不是「已经用了多久」：等的人关心的是还剩多少
            string tail = _state.RemainingSeconds is null
                ? $"用时 {_state.ElapsedLabel}"
                : _state.RemainingLabel;

            AppNotify.Live(
                LiveKey,
                "正在扫描本地实例",
                $"扫描范围　{_state.RootsText}{Environment.NewLine}"
                    + $"已检查 {_state.Directories:N0} 个 · 找到 {_state.Found} 个 · {tail}",
                OpenDetail is null ? null : "查看详情",
                OpenDetail);
        }

        private static void Finish(ScanReport? report, string? error, ScanPhase phase)
        {
            int added = Math.Max(0, TavernInstances.Locals.Count - _baseline);

            _state = report is null
                ? _state with { Phase = phase, Added = added, Error = error }
                : new ScanSnapshot(
                    phase,
                    report.Elapsed,
                    report.Threads,
                    report.Drives,
                    report.Directories,
                    report.Hits,
                    added,
                    error);

            lock (Gate)
            {
                _cancellation?.Dispose();
                _cancellation = null;
            }

            // 先撤进度卡再报结果：否则两条同时挂在右下角，一条说「正在扫描」一条说「扫描完成」
            AppNotify.Close(LiveKey);

            Raise();
            Announce(phase, added);
        }

        private static void Announce(ScanPhase phase, int added)
        {
            switch (phase)
            {
                case ScanPhase.Done when _state.Found == 0:
                    AppNotify.Info(
                        "扫描完成，没有找到酒馆目录",
                        $"已检查 {_state.Directories:N0} 个文件夹，用时 {_state.ElapsedLabel}。"
                            + "如果酒馆装在移动硬盘或网络盘上，请用「导入」直接指定它的 package.json。");
                    break;

                case ScanPhase.Done when added == 0:
                    AppNotify.Info(
                        "扫描完成，没有新增实例",
                        $"找到 {_state.Found} 个酒馆目录，它们都已经在列表里了；用时 {_state.ElapsedLabel}。");
                    break;

                case ScanPhase.Done:
                    AppNotify.Success(
                        "扫描完成",
                        $"找到 {_state.Found} 个酒馆目录，其中 {added} 个是新实例，已加入列表；"
                            + $"用时 {_state.ElapsedLabel}。");
                    break;

                case ScanPhase.Cancelled:
                    AppNotify.Info(
                        "已取消扫描",
                        $"已检查 {_state.Directories:N0} 个文件夹，找到的 {_state.Found} 个实例仍保留在列表中。");
                    break;

                case ScanPhase.Failed:
                    AppNotify.Error(
                        "扫描失败",
                        string.IsNullOrWhiteSpace(_state.Error) ? "扫描时出错，请重试。" : _state.Error);
                    break;
            }
        }

        private static void Post(Action action)
        {
            DispatcherQueue? queue = _queue;
            if (queue is null || queue.HasThreadAccess)
            {
                action();
                return;
            }

            queue.TryEnqueue(() => action());
        }

        private static void Raise() => Changed?.Invoke(null, EventArgs.Empty);

        /// <summary>扫描线程上的进度回调：只做转投，不在回调里碰状态。</summary>
        private sealed class ProgressProxy : IProgress<ScanProgress>
        {
            public void Report(ScanProgress value) => Post(() => Apply(value));
        }
    }
}
