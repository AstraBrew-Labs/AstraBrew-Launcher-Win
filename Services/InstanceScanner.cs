using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>扫描命中的一份酒馆代码目录；<see cref="Path"/> 已归一（绝对路径、无尾分隔符）。</summary>
    public sealed record ScanHit(string Path, string Version);

    /// <summary>
    /// 某一块磁盘此刻的进度。
    ///
    /// <para><see cref="Directories"/> 是已经查过的目录数（准确值），
    /// <see cref="Expected"/> 是这块盘一共有多少目录的预估（上次扫完记下来的，或者本次已经发现的）。
    /// 没有 <see cref="Expected"/> 就没有分母，界面上只能显示「已检查 N 个」而不能画进度环。</para>
    ///
    /// <para><see cref="Pending"/> 是排队里还剩多少条——它归零就等于这块盘扫完了。
    /// <see cref="Rate"/> 是每秒查多少个目录的滑动平均，用来算还要多久。
    /// <see cref="Estimating"/> 为真表示分母自己还在变，这时候给的时间不作数。
    /// <see cref="Threads"/> 是开局分到的线程数，<see cref="Workers"/> 是此刻真正在这块盘上
    /// 干活的线程数——别的盘扫完会来支援，所以后者可以比前者大。</para>
    /// </summary>
    public sealed record DriveProgress(
        string Root,
        string Current,
        long Directories,
        long Pending,
        long Expected,
        double Rate,
        bool Estimating,
        DriveKind Kind,
        int Threads,
        int Workers)
    {
        /// <summary>这块盘扫完了（队列清空）。</summary>
        public bool IsComplete => Pending == 0;

        /// <summary>0–1 的完成比例；没有预估总数时为 null，界面据此退回「只报数量」的写法。</summary>
        public double? Ratio
        {
            get
            {
                if (Expected <= 0)
                {
                    return null;
                }

                double ratio = (double)Directories / Expected;
                return ratio > 1 ? 1 : ratio;
            }
        }

        /// <summary>预计还要多少秒；速度还没测出来、或者预估总数已经追平时为 null。</summary>
        public double? RemainingSeconds
        {
            get
            {
                if (Rate < 1 || Expected <= Directories)
                {
                    return null;
                }

                return (Expected - Directories) / Rate;
            }
        }
    }

    /// <summary>扫描过程中的一次进度快照。</summary>
    public sealed record ScanProgress(
        long Directories,
        IReadOnlyList<ScanHit> Hits,
        IReadOnlyList<DriveProgress> Drives,
        TimeSpan Elapsed);

    /// <summary>一次扫描的最终结果。</summary>
    public sealed record ScanReport(
        IReadOnlyList<string> Roots,
        IReadOnlyList<string> Unavailable,
        IReadOnlyList<DriveProgress> Drives,
        long Directories,
        IReadOnlyList<ScanHit> Hits,
        int Threads,
        TimeSpan Elapsed,
        bool Cancelled);

    /// <summary>
    /// 全盘找酒馆代码目录。
    ///
    /// 判据只有一个：目录里的 <c>package.json</c> 的 <c>name</c> 必须等于 <c>sillytavern</c>。
    /// 目录叫什么、有没有 <c>config.yaml</c> 都不作数——改过名的目录照样是酒馆，
    /// 叫 SillyTavern 的空壳目录反而跑不起来。
    ///
    /// 并发按盘分配：每块固定磁盘至少一条线程，总线程数取自「设置 → 占用 CPU 核心」
    /// （自动 / 一半 = 逻辑核心的一半，全部 = 全部），按盘数均分。硬盘是 IO 密集型，
    /// 一块盘上的线程再多也只是在队列里排队，所以不是线程越多越快。
    ///
    /// 跳过规则见 <see cref="SkipNames"/> 与 <see cref="BuildSkipRoots"/>：系统目录、回收站、
    /// <c>node_modules</c>、各种缓存占掉全盘九成以上的目录数，跳过它们才可能几十秒扫完。
    /// 重解析点（junction / symlink）一律不跟进去，否则会绕回来无限深。
    /// </summary>
    public static class InstanceScanner
    {
        /// <summary>准入的包名：<c>package.json</c> 里 <c>name</c> 必须等于它（不区分大小写）。</summary>
        public const string PackageName = "sillytavern";

        private const string ManifestName = "package.json";

        /// <summary>进度上报间隔：目录计数一秒能涨几万，不节流会把 UI 线程压满。</summary>
        private const int ReportIntervalMs = 250;

        /// <summary>
        /// 按名字跳过的目录。命中就在枚举时剪掉整棵子树，连排队都不排。
        /// 只放「里面绝不可能有一份独立酒馆代码」的目录——跳过错了就是用户看不见自己的实例，
        /// 宁可少跳几个也不能多跳。
        /// </summary>
        private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
        {
            // 系统自己的目录：系统文件、还原点、更新暂存、卷影副本
            "Windows",
            "Windows.old",
            "WinSxS",
            "System Volume Information",
            "Recovery",
            "PerfLogs",
            "Config.Msi",
            "$Recycle.Bin",
            "$WinREAgent",
            "$Windows.~BT",
            "$Windows.~WS",

            // 依赖与版本库：一份 node_modules 就有几万个文件，而酒馆代码本身不在里面
            "node_modules",
            ".git",
            ".svn",
            ".hg",

            // 开发工具的包缓存
            ".npm",
            ".yarn",
            ".pnpm-store",
            ".nuget",
            ".gradle",
            ".cargo",

            // 浏览器 / Electron / 各种应用的缓存
            ".cache",
            "cache",
            "caches",
            "cachestorage",
            "code cache",
            "gpucache",
            "shadercache",
            "service worker",
            "crashpad",
            "crashdumps",
            "temp",
            "tmp",
            ".tmp",
            "tempstate",
        };

        /// <summary>
        /// 可扫描的固定磁盘根目录（<c>C:\</c> 这种形式）。
        ///
        /// 两层过滤：<see cref="DriveType"/> 只认 <c>Fixed</c>（U 盘、光驱、网络盘直接出局），
        /// 再问一遍总线类型——外接硬盘常常被系统报成「固定磁盘」，插着不动就跟内置盘一样，
        /// 但拔掉就没了，扫描它既慢又不可靠，所以总线是 USB / 1394 / 读卡器的一律不扫。
        /// </summary>
        public static List<string> ReadyRoots(out List<string> unavailable)
        {
            var roots = new List<string>();
            unavailable = new List<string>();

            DriveInfo[] drives;
            try
            {
                drives = DriveInfo.GetDrives();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return roots;
            }

            foreach (DriveInfo drive in drives)
            {
                string name;
                try
                {
                    if (drive.DriveType != DriveType.Fixed)
                    {
                        continue;
                    }

                    name = drive.Name;
                    if (!drive.IsReady)
                    {
                        unavailable.Add(name);
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 读不到状态的盘当它不可用：可能是刚拔掉的移动硬盘挂成了固定盘
                    unavailable.Add(SafeName(drive));
                    continue;
                }

                if (PhysicalDrive.Describe(name).External)
                {
                    continue;
                }

                roots.Add(name);
            }

            return roots;
        }

        /// <summary>
        /// 本次扫描一共开几条线程，由「设置 → 占用 CPU 核心」决定。三档的区别是**给系统留多少**：
        /// <list type="bullet">
        /// <item>自动：留一点给系统（8 核以上留 2，小机器留 1），剩下全用来扫——优先保证系统流畅；</item>
        /// <item>一半：扫描一半、系统一半，端平；</item>
        /// <item>全部：逻辑核心全给扫描——IO 密集型任务不会把系统抢卡，优先扫描速度。</item>
        /// </list>
        /// </summary>
        public static int ResolveBudget()
        {
            int logical = Math.Max(1, Environment.ProcessorCount);
            return LauncherSettings.CpuCores switch
            {
                CpuCorePreference.Auto => Math.Max(1, logical - (logical >= 8 ? 2 : 1)),
                CpuCorePreference.Half => Math.Max(1, logical / 2),
                _ => logical,
            };
        }

        /// <summary>给界面看的一句话，例如「自动 · 16 核留 2 给系统」，让人看得出设置确实生效了。</summary>
        public static string DescribeBudget()
        {
            int logical = Math.Max(1, Environment.ProcessorCount);
            return LauncherSettings.CpuCores switch
            {
                CpuCorePreference.Auto => $"自动 · {logical} 核留 {(logical >= 8 ? 2 : 1)} 给系统",
                CpuCorePreference.Half => $"一半 · {logical} 核用 {Math.Max(1, logical / 2)}",
                _ => $"全部 · {logical} 核全开",
            };
        }

        /// <summary>
        /// 把总预算分到每块盘：先均分，余数从前面的盘开始各加一条。
        /// 总数严格等于预算——向上取整的均分会让 4 块盘 × 4 条 = 16 条，超出「自动」的 14 条预算。
        /// 每盘至少一条：盘比预算还多也只能超一点，不然那块盘根本没人扫。
        /// </summary>
        public static int[] AllocateThreads(int driveCount)
        {
            int drives = Math.Max(1, driveCount);
            int budget = ResolveBudget();
            int share = budget / drives;
            int remainder = budget % drives;

            var result = new int[drives];
            for (int i = 0; i < drives; i++)
            {
                result[i] = Math.Clamp(share + (i < remainder ? 1 : 0), 1, 16);
            }

            return result;
        }

        /// <summary>
        /// 从根目录开始扫，返回命中的全部酒馆目录。
        ///
        /// <paramref name="onHit"/> 在**工作线程**上回调（发现一个就报一个，界面自己切线程），
        /// 这样扫描期间用户就能看到列表在长；进度按 <see cref="ReportIntervalMs"/> 节流。
        /// 取消不抛异常，结果里 <see cref="ScanReport.Cancelled"/> 为 true。
        /// </summary>
        public static Task<ScanReport> ScanAsync(
            IReadOnlyList<string> extraSkipRoots,
            IProgress<ScanProgress>? progress,
            Action<ScanHit>? onHit,
            CancellationToken token) =>
            Task.Run(() => Run(extraSkipRoots, progress, onHit, token), CancellationToken.None);

        /// <summary>
        /// 读目录里的 <c>package.json</c>，判断它是不是一份酒馆代码。
        ///
        /// 全应用只有这一处判断「是不是酒馆」：扫描、手动导入、从磁盘恢复实例都走它，
        /// 避免三处各写一套、判据慢慢走偏。文件不在 / 不是合法 JSON / 没有 name 都算不是。
        /// </summary>
        public static bool TryReadManifest(string root, out string version)
        {
            version = string.Empty;

            try
            {
                string manifest = Path.Combine(root, ManifestName);
                if (!File.Exists(manifest))
                {
                    return false;
                }

                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest, Encoding.UTF8));
                JsonElement element = document.RootElement;
                if (element.ValueKind != JsonValueKind.Object
                    || !element.TryGetProperty("name", out JsonElement name)
                    || name.ValueKind != JsonValueKind.String
                    || !string.Equals(name.GetString()?.Trim(), PackageName, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (element.TryGetProperty("version", out JsonElement declared)
                    && declared.ValueKind == JsonValueKind.String)
                {
                    // 与 TavernInstances 里的版本写法对齐：去 v 前缀，界面上统一自己加
                    version = declared.GetString()?.Trim().TrimStart('v', 'V') ?? string.Empty;
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or JsonException
                or ArgumentException
                or PathTooLongException
                or NotSupportedException)
            {
                // 读不动就当它不存在：一份文件坏了不该让整个扫描停下来
                return false;
            }
        }

        // ==================== 扫描主体 ====================

        private static ScanReport Run(
            IReadOnlyList<string> extraSkipRoots,
            IProgress<ScanProgress>? progress,
            Action<ScanHit>? onHit,
            CancellationToken token)
        {
            var clock = Stopwatch.StartNew();

            List<string> roots = ReadyRoots(out List<string> unavailable);
            int[] allocation = AllocateThreads(roots.Count);
            List<string> skipRoots = BuildSkipRoots(extraSkipRoots);

            var states = new List<DriveState>(roots.Count);
            var hits = new List<ScanHit>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var gate = new object();

            long reportedAt = Environment.TickCount64;

            // 每发现一个目录都要比一遍，写成手写循环：lambda 版每次调用都要新建一个闭包
            bool Skip(string directory)
            {
                foreach (string root in skipRoots)
                {
                    if (PathMatch.IsUnderRaw(directory, root))
                    {
                        return true;
                    }
                }

                return false;
            }

            ScanProgress Snapshot()
            {
                ScanHit[] hitCopy;
                lock (gate)
                {
                    hitCopy = hits.ToArray();
                }

                long total = 0;
                long now = Environment.TickCount64;
                var rows = new List<DriveProgress>(states.Count);
                foreach (DriveState state in states)
                {
                    long count = Interlocked.Read(ref state.Directories);
                    total += count;

                    // 预估总数最近三秒还在涨 = 分母没定，剩余时间先别报；
                    // 用上次扫完记下的目录数时它一直是 0，第一秒就能报出时间
                    bool estimating = now - Interlocked.Read(ref state.ExpectedChangedAt) < 3000;

                    rows.Add(new DriveProgress(
                        state.Root,
                        Volatile.Read(ref state.Current) ?? state.Root,
                        count,
                        Volatile.Read(ref state.Pending),
                        Interlocked.Read(ref state.Expected),
                        Volatile.Read(ref state.Rate),
                        estimating,
                        state.Kind,
                        state.Threads,
                        Volatile.Read(ref state.ActiveWorkers)));
                }

                return new ScanProgress(total, hitCopy, rows, clock.Elapsed);
            }

            void MaybeReport()
            {
                long now = Environment.TickCount64;
                long last = Interlocked.Read(ref reportedAt);
                if (now - last < ReportIntervalMs)
                {
                    return;
                }

                // 抢到窗口的那条线程负责上报，其余线程直接走开：
                // 否则一秒几万个目录会各推一份快照，UI 线程根本没有喘息的机会
                if (Interlocked.CompareExchange(ref reportedAt, now, last) != last)
                {
                    return;
                }

                progress?.Report(Snapshot());
            }

            void Record(string directory)
            {
                if (!TryReadManifest(directory, out string version))
                {
                    return;
                }

                string path = PathMatch.Normalize(directory);
                ScanHit hit;
                lock (gate)
                {
                    if (!seen.Add(path))
                    {
                        return;
                    }

                    hit = new ScanHit(path, version);
                    hits.Add(hit);
                }

                onHit?.Invoke(hit);
                progress?.Report(Snapshot());
            }

            void Process(DriveState state, string directory)
            {
                // 「现在有几条线程在这块盘上干活」含来支援的线程，界面上能直接看到
                Interlocked.Increment(ref state.ActiveWorkers);
                try
                {
                    // 跳过判断放在出队之后：根目录也是排队进来的，孩子不再入队就等于剪掉整棵子树
                    if (Skip(directory))
                    {
                        return;
                    }

                    Volatile.Write(ref state.Current, directory);

                    bool hasManifest = false;
                    var info = new DirectoryInfo(directory);

                    // 一次枚举同时拿到「有没有 package.json」和孩子节点，
                    // 比先 Directory.GetDirectories 再 File.Exists 少一轮系统调用
                    foreach (FileSystemInfo entry in info.EnumerateFileSystemInfos())
                    {
                        FileAttributes attributes = entry.Attributes;

                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            // 重解析点（junction / symlink）不跟：跟进去可能绕回上层，也可能连到别的盘
                            if ((attributes & FileAttributes.ReparsePoint) != 0)
                            {
                                continue;
                            }

                            if (IsSkippedName(entry.Name))
                            {
                                continue;
                            }

                            // 孩子进本盘自己的队列：一条盘上的目录只由这条盘的线程处理，
                            // 这样「这块盘扫到哪了」才是真的，也不会四块盘互相偷活
                            state.Enqueue(entry.FullName);
                        }
                        else if (string.Equals(entry.Name, ManifestName, StringComparison.OrdinalIgnoreCase))
                        {
                            hasManifest = true;
                        }
                    }

                    long done = Interlocked.Increment(ref state.Directories);

                    // 每 64 个目录采一次速度：一个目录一个样本太贵，太稀又跟不上速度变化
                    if ((done & 63) == 0)
                    {
                        state.SampleRate();
                    }

                    if (hasManifest)
                    {
                        Record(directory);
                    }

                    MaybeReport();
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException
                    or IOException
                    or System.Security.SecurityException
                    or PathTooLongException
                    or ArgumentException
                    or NotSupportedException)
                {
                    // 没权限、枚举到一半目录被删、路径过长：跳过这一层，其它目录照扫
                }
                finally
                {
                    Interlocked.Decrement(ref state.ActiveWorkers);
                    if (Interlocked.Decrement(ref state.Pending) == 0)
                    {
                        state.Signal.Set();
                    }
                }
            }

            /// <summary>本盘扫完了，去帮谁：挑积压最多的那块盘，让它多几条线程一起啃。</summary>
            DriveState? FindVictim()
            {
                DriveState? best = null;
                int bestBacklog = 0;

                foreach (DriveState state in states)
                {
                    int backlog = Volatile.Read(ref state.Pending);
                    if (backlog > bestBacklog)
                    {
                        best = state;
                        bestBacklog = backlog;
                    }
                }

                return bestBacklog > 0 ? best : null;
            }

            bool AllDone()
            {
                foreach (DriveState state in states)
                {
                    if (Volatile.Read(ref state.Pending) != 0 || !state.Queue.IsEmpty)
                    {
                        return false;
                    }
                }

                return true;
            }

            void Work(DriveState home)
            {
                while (!token.IsCancellationRequested)
                {
                    if (home.Queue.TryDequeue(out string? directory))
                    {
                        Process(home, directory!);
                        continue;
                    }

                    if (Volatile.Read(ref home.Pending) > 0)
                    {
                        // 本盘队列空了但还有在飞的目录：等它们把孩子放进来，本盘还没扫完
                        home.Signal.Reset();
                        if (!home.Queue.IsEmpty)
                        {
                            home.Signal.Set();
                            continue;
                        }

                        home.Signal.Wait(20);
                        continue;
                    }

                    // 本盘扫完了：线程不直接退出，去帮还没扫完的盘——
                    // 固态盘通常先完事，把它的核心让给还在啃的机械盘，整轮才能早结束
                    DriveState? victim = FindVictim();
                    if (victim is not null && victim.Queue.TryDequeue(out string? borrowed))
                    {
                        Process(victim, borrowed!);
                        continue;
                    }

                    if (AllDone())
                    {
                        return;
                    }

                    // 别的盘此刻也拿不出目录（都在枚举中），稍等再来，别空转
                    Thread.Sleep(20);
                }
            }

            for (int index = 0; index < roots.Count; index++)
            {
                var state = new DriveState(roots[index], PhysicalDrive.Describe(roots[index]).Kind, allocation[index]);
                states.Add(state);
                state.Enqueue(roots[index]);
            }

            progress?.Report(Snapshot());

            var workers = new List<Task>(allocation.Sum());
            foreach (DriveState state in states)
            {
                DriveState captured = state;
                for (int i = 0; i < captured.Threads; i++)
                {
                    // 长跑阻塞循环：用专用线程，别占着线程池的线程不还
                    workers.Add(Task.Factory.StartNew(
                        () => Work(captured),
                        CancellationToken.None,
                        TaskCreationOptions.LongRunning,
                        TaskScheduler.Default));
                }
            }

            try
            {
                Task.WaitAll(workers.ToArray());
            }
            catch (AggregateException)
            {
                // Work 自己不抛：这里只是兜底，避免把扫描线程的意外冒到界面上
            }

            foreach (DriveState state in states)
            {
                state.Signal.Dispose();
            }

            ScanProgress final = Snapshot();
            bool cancelled = token.IsCancellationRequested;

            // 只有完整扫完的目录数才是「这块盘有多少个目录」；中途取消的数字偏小，
            // 拿它当下次的分母会让进度环一直顶在 100%
            if (!cancelled)
            {
                var counts = new List<long>(states.Count);
                foreach (DriveState state in states)
                {
                    counts.Add(Interlocked.Read(ref state.Discovered));
                }

                ScanStats.Remember(roots, counts);
            }

            return new ScanReport(
                roots,
                unavailable,
                final.Drives,
                final.Directories,
                final.Hits,
                allocation.Sum(),
                clock.Elapsed,
                cancelled);
        }

        /// <summary>根级别的排除项：系统缓存、启动器自己的临时目录，以及调用方点名的目录。</summary>
        private static List<string> BuildSkipRoots(IReadOnlyList<string> extra)
        {
            var list = new List<string>();

            void Add(string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                try
                {
                    list.Add(PathMatch.Normalize(path));
                }
                catch (Exception ex) when (ex is ArgumentException
                    or NotSupportedException
                    or PathTooLongException)
                {
                }
            }

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            Add(AppPaths.TempRoot);
            Add(Path.Combine(local, "Temp"));
            Add(Path.Combine(local, "Packages"));
            Add(Path.Combine(local, "Microsoft"));

            if (Environment.GetEnvironmentVariable("SystemRoot") is { Length: > 0 } windows)
            {
                Add(Path.Combine(windows, "WinSxS"));
            }

            foreach (string path in extra)
            {
                Add(path);
            }

            return list;
        }

        private static bool IsSkippedName(string name) =>
            name.Length == 0 || name[0] == '$' || SkipNames.Contains(name);

        private static string SafeName(DriveInfo drive)
        {
            try
            {
                return drive.Name;
            }
            catch (Exception)
            {
                return "?";
            }
        }

        /// <summary>
        /// 一块盘的扫描状态：自己的待扫队列、自己的线程、自己的计数。
        ///
        /// 必须是「一盘一队列」而不是共用一条队列——共用的话 C 盘的线程会去处理 D 盘的目录，
        /// 界面上「这块盘扫到哪了」就全是别人的路径，四块盘会显示成一模一样。
        /// 计数用 Interlocked / Volatile 读写，别的线程随时来取快照。
        /// </summary>
        private sealed class DriveState
        {
            public DriveState(string root, DriveKind kind, int threads)
            {
                Root = root;
                Kind = kind;
                Threads = threads;

                // 分母优先用上次扫完记下来的目录数；没有就从这次发现的开始往上长
                Expected = ScanStats.Expected(root);
                _rateAt = Environment.TickCount64;
            }

            public string Root { get; }

            public DriveKind Kind { get; }

            /// <summary>这块盘分到几条线程，来自 <see cref="AllocateThreads"/>。</summary>
            public int Threads { get; }

            /// <summary>此刻正在这块盘上干活的线程数，包含别的盘扫完后来支援的。</summary>
            public int ActiveWorkers;

            public long Directories;

            public string? Current;

            /// <summary>本盘排队里还剩多少条（含正在处理的那条）。</summary>
            public int Pending;

            /// <summary>本盘一共发现过多少个目录（= 已查过的 + 排队中的）。</summary>
            public long Discovered;

            /// <summary>本盘目录总数的预估；只增不减。</summary>
            public long Expected;

            /// <summary>预估总数上次变大的时刻；用来判断「现在还只是在估算」。</summary>
            public long ExpectedChangedAt;

            /// <summary>每秒查多少个目录的滑动平均。</summary>
            public double Rate;

            /// <summary>上次算速度时的目录数 / 时刻，只给算速度用。</summary>
            private long _rateMark;

            private long _rateAt;

            /// <summary>本盘待扫目录。只在同一条盘的线程之间共享。</summary>
            public ConcurrentQueue<string> Queue { get; } = new();

            /// <summary>队列有东西 / 全部扫完时唤醒等待中的线程。</summary>
            public ManualResetEventSlim Signal { get; } = new(false);

            public void Enqueue(string directory)
            {
                Queue.Enqueue(directory);
                Interlocked.Increment(ref Pending);
                Interlocked.Increment(ref Discovered);

                // 分母至少要跟上已经发现的量：不然超过预估之后进度环会一直顶在 100%
                RaiseExpected(Interlocked.Read(ref Discovered));
                Signal.Set();
            }

            private void RaiseExpected(long discovered)
            {
                while (true)
                {
                    long current = Interlocked.Read(ref Expected);
                    if (discovered <= current)
                    {
                        return;
                    }

                    if (Interlocked.CompareExchange(ref Expected, discovered, current) == current)
                    {
                        Volatile.Write(ref ExpectedChangedAt, Environment.TickCount64);
                        return;
                    }
                }
            }

            /// <summary>
            /// 顺手更新速度：每查若干个目录采一次样，越新的样本权重越高。
            /// 采样的门槛放在调用方（每 64 个目录一次），这里只做滑动平均。
            /// </summary>
            public void SampleRate()
            {
                long now = Environment.TickCount64;
                long count = Interlocked.Read(ref Directories);
                long mark = Interlocked.Read(ref _rateMark);
                long at = Interlocked.Read(ref _rateAt);

                if (now - at < 400 || count <= mark)
                {
                    return;
                }

                Interlocked.Exchange(ref _rateMark, count);
                Interlocked.Exchange(ref _rateAt, now);

                double instant = (count - mark) * 1000.0 / (now - at);
                double previous = Volatile.Read(ref Rate);
                Volatile.Write(ref Rate, previous <= 0 ? instant : (previous * 0.6) + (instant * 0.4));
            }
        }
    }

    /// <summary>
    /// 路径比较：只做「是不是同一个目录」「在不在它下面」两件事。
    ///
    /// Windows 的路径比较不能直接用字符串相等：大小写不敏感、尾分隔符可有可无、
    /// 还有 <c>.</c> 与 <c>..</c> 这种。归一一次之后一律用 OrdinalIgnoreCase 比。
    /// </summary>
    internal static class PathMatch
    {
        /// <summary>绝对路径 + 去掉尾分隔符（<c>C:\</c> 会变成 <c>C:</c>，与它自己永远相等）。</summary>
        public static string Normalize(string path) =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        /// <summary>两个路径是否指向同一个目录。</summary>
        public static bool IsSame(string left, string right) =>
            string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

        /// <summary>归一之后判断包含关系。</summary>
        public static bool IsUnder(string path, string root) =>
            IsUnderRaw(Normalize(path), Normalize(root));

        /// <summary>
        /// 纯字符串的包含判断，要求两个参数都已 <see cref="Normalize"/>——
        /// 扫描时每发现一个目录都要比一次，那里不能再做一次 <c>GetFullPath</c>。
        /// </summary>
        public static bool IsUnderRaw(string normalizedPath, string normalizedRoot)
        {
            if (normalizedPath.Length < normalizedRoot.Length)
            {
                return false;
            }

            if (normalizedPath.Length == normalizedRoot.Length)
            {
                return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase);
            }

            return normalizedPath[normalizedRoot.Length] == Path.DirectorySeparatorChar
                && normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }
    }
}
