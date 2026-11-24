using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 运行日志的落盘与清理：启动器自己写的那一份，加上酒馆进程的输出。
    ///
    /// 与 <see cref="InstallLog"/> 分工明确：
    /// · <see cref="InstallLog"/> 是「一个安装任务一个文件」，落 <c>logs\</c> 根，只增不删，供排错时翻；
    /// · 这里是「按天滚动的运行日志」，落 <c>logs\runtime\</c>，受「只保留最新」与容量上限约束。
    ///
    /// 为什么不复用安装日志那套：文件名规则、生命周期、清理策略全都不一样，
    /// 混在一个目录里既没法自动清理（会误删安装记录），解析也会互相污染。
    ///
    /// 三条硬约束：
    /// · **不阻塞 UI**：所有写入进有界队列，后台单线程落盘；队列满了丢最旧的行，不涨内存；
    /// · **失败必须静默**：日志不是功能前置条件，目录不可写时直接停写，绝不影响启动；
    /// · **清理绝不碰安装日志**：只枚举 <see cref="FolderPath"/>，父目录的 <c>logs\*.log</c> 一个都不碰。
    /// </summary>
    public static class RuntimeLog
    {
        /// <summary>运行日志目录：<c>%AppData%\AstraBrew Launcher\logs\runtime</c>。</summary>
        public static string FolderPath { get; } = Path.Combine(InstallLog.FolderPath, "runtime");

        /// <summary>队列容量：够撑住一次 stdout 刷屏，又不至于吃掉内存。</summary>
        private const int QueueCapacity = 4096;

        /// <summary>单文件下限：再小就只剩分卷、看不出上下文了。</summary>
        private const long MinSingleFileBytes = 512 * 1024;

        private static readonly BlockingCollection<LogLine> Queue =
            new(new ConcurrentQueue<LogLine>(), QueueCapacity);

        private static readonly object Gate = new();

        private static readonly LogSink LauncherSink = new("launcher");
        private static readonly LogSink TavernSink = new("tavern");

        /// <summary>PM2 控制面：我们发起的每条命令、退出码、jlist 状态变化。</summary>
        private static readonly LogSink Pm2Sink = new("pm2");

        private static DispatcherQueue? _uiQueue;
        private static Task? _pump;
        private static Timer? _maintenance;
        private static long _dropped;
        private static bool _started;

        /// <summary>上一次已知的日志设置，用来判断「真的改了」才清理。</summary>
        private static bool _lastKeepLatestOnly = true;
        private static int _lastMaxSizeMb = LauncherSettings.DefaultLogMaxSizeMb;

        /// <summary>新行已落盘。**在 UI 线程触发**，页面可以直接往列表里加。</summary>
        public static event EventHandler<LogLine>? Written;

        /// <summary>跨天或分卷：页面应整屏重读，别再用增量。</summary>
        public static event EventHandler? Rolled;

        /// <summary>因为队列满被丢掉的行数（页脚可提示，别当错误）。</summary>
        public static long DroppedCount => Interlocked.Read(ref _dropped);

        // ==================== 生命周期 ====================

        /// <summary>
        /// 启动一次会话：先清理、再打头部、最后开始接活。必须在 UI 线程调用（要捕获 DispatcherQueue）。
        /// </summary>
        public static void StartSession()
        {
            lock (Gate)
            {
                if (_started)
                {
                    return;
                }

                _started = true;
                _uiQueue ??= DispatcherQueue.GetForCurrentThread();
                _lastKeepLatestOnly = LauncherSettings.KeepLatestLogsOnly;
                _lastMaxSizeMb = LauncherSettings.LogMaxSizeMb;
            }

            // 先清理再开写：把过期文件收掉，今天的文件才有位置
            Maintain();

            // 控制台存档是固定文件名、不参与按天清理，所以得自己管大小
            ConsoleArchive.Trim();

            // 操作记录另有一套按天清理（90 天），与运行日志分开
            ActivityLog.Maintain();

            _pump ??= Task.Run(PumpLoop);

            Info($"session start · {BuildChannelInfo.DisplayVersion} · {BuildChannelInfo.Channel}");
            Info($"os {Environment.OSVersion.VersionString} · {(Environment.Is64BitProcess ? "x64" : "x86")} · .NET {Environment.Version}");
            Info($"ui language={LauncherSettings.Language} theme={LauncherSettings.Theme} level={LauncherSettings.Level} runtime={LauncherSettings.Runtime}");
            Info($"log policy keep-latest-only={LauncherSettings.KeepLatestLogsOnly} cap={LauncherSettings.LogMaxSizeMb}MB dir={FolderPath}");

            // 单次运行内也可能把日志刷爆（跑一晚的 stdout），十分钟兜底扫一次
            _maintenance = new Timer(_ => Maintain(), null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));

            LauncherSettings.Changed -= OnSettingsChanged;
            LauncherSettings.Changed += OnSettingsChanged;
        }

        /// <summary>收尾：写完最后一行、排空队列、放掉文件句柄（否则记事本打不开）。</summary>
        public static void EndSession()
        {
            lock (Gate)
            {
                if (!_started)
                {
                    return;
                }

                _started = false;
            }

            Info("session end");

            LauncherSettings.Changed -= OnSettingsChanged;

            _maintenance?.Dispose();
            _maintenance = null;

            Queue.CompleteAdding();

            try
            {
                _pump?.Wait(TimeSpan.FromSeconds(3));
            }
            catch (Exception)
            {
                // 排空失败也只影响最后几行，不阻断退出
            }

            LauncherSink.Dispose();
            TavernSink.Dispose();
        }

        private static void OnSettingsChanged(object? sender, EventArgs e)
        {
            bool keep = LauncherSettings.KeepLatestLogsOnly;
            int max = LauncherSettings.LogMaxSizeMb;

            // 设置变了就重新清理；别的设置变化不扫盘（滑一下滑杆就扫一次太浪费）
            if (keep == _lastKeepLatestOnly && max == _lastMaxSizeMb)
            {
                return;
            }

            _lastKeepLatestOnly = keep;
            _lastMaxSizeMb = max;

            _ = Task.Run(Maintain);
        }

        // ==================== 写入入口 ====================

        public static void Info(string message) => Enqueue(LogLevel.Info, message, LogArea.Launcher);

        public static void Warn(string message) => Enqueue(LogLevel.Warning, message, LogArea.Launcher);

        public static void Error(string message) => Enqueue(LogLevel.Error, message, LogArea.Launcher);

        /// <summary>
        /// 酒馆的一行输出。由 <see cref="TavernRuntime"/> 在 UI 线程的追加回调里调用——
        /// 那里已经有一份内存副本，这里只负责落盘，不改变内存行为。
        ///
        /// 写盘用的是 <see cref="TavernLogEntry.Display"/> 的**成品文字**（写盘瞬间的语言），
        /// 历史行不会随界面语言变化——换来的是「记事本打开就能读、反馈附件能直接看」。
        /// </summary>
        public static void Tavern(TavernLogEntry entry, bool mirrorToConsole = true)
        {
            LogLevel level = entry.Kind switch
            {
                TavernLogKind.System => LogLevel.System,
                TavernLogKind.Success => LogLevel.Success,
                TavernLogKind.Warning => LogLevel.Warning,
                TavernLogKind.Error => LogLevel.Error,
                TavernLogKind.Output => LogLevel.Output,
                _ => LogLevel.Info,
            };

            Enqueue(level, entry.Display, LogArea.Tavern, entry.Time, mirrorToConsole);
        }

        /// <summary>
        /// PM2 控制面的一行：进了哪条命令、退出码多少、jlist 看到的进程状态。
        ///
        /// 只落 <c>pm2-*.log</c>（日志页「PM2」页签），**不进控制台**：
        /// 控制台是给用户看的操作记录，pm2 的调用细节属于排错用的后台细节。
        /// </summary>
        public static void Pm2(string message) => Enqueue(LogLevel.Info, message, LogArea.Pm2);

        private static void Enqueue(
            LogLevel level,
            string text,
            LogArea area,
            DateTime? time = null,
            bool mirrorToConsole = true)
        {
            if (string.IsNullOrEmpty(text) || Queue.IsAddingCompleted)
            {
                return;
            }

            var line = new LogLine(time ?? DateTime.Now, area, LogOriginKind.Runtime, level, text, string.Empty)
            {
                MirrorToConsole = mirrorToConsole,
            };

            if (!Queue.TryAdd(line))
            {
                Interlocked.Increment(ref _dropped);
            }
        }

        private static void PumpLoop()
        {
            foreach (LogLine line in Queue.GetConsumingEnumerable())
            {
                LogSink sink = line.Area switch
                {
                    LogArea.Tavern => TavernSink,
                    LogArea.Pm2 => Pm2Sink,
                    _ => LauncherSink,
                };

                string? path = sink.Write(line);

                if (path is null)
                {
                    continue;
                }

                // 控制台单独存一份：下次打开启动器原样载回来，不靠 PM2 的输出重放。
                // 只有酒馆那一类行会进控制台，所以只归档它。
                if (line.Area == LogArea.Tavern && line.MirrorToConsole)
                {
                    ConsoleArchive.Append(line.LevelTag, line.Text);
                }

                RaiseWritten(line with { FilePath = path });
            }
        }

        private static void RaiseWritten(LogLine line)
        {
            EventHandler<LogLine>? handler = Written;
            if (handler is null)
            {
                return;
            }

            DispatcherQueue? queue = _uiQueue;
            if (queue is null || queue.HasThreadAccess)
            {
                handler(null, line);
                return;
            }

            queue.TryEnqueue(() => handler(null, line));
        }

        private static void RaiseRolled()
        {
            EventHandler? handler = Rolled;
            if (handler is null)
            {
                return;
            }

            DispatcherQueue? queue = _uiQueue;
            if (queue is null || queue.HasThreadAccess)
            {
                handler(null, EventArgs.Empty);
                return;
            }

            queue.TryEnqueue(() => handler(null, EventArgs.Empty));
        }

        // ==================== 清理 ====================

        /// <summary>
        /// 按当前策略清理：先处理「只保留当天」，再看容量上限。
        /// 失败静默——清理只影响磁盘占用，不该弹任何东西。
        /// </summary>
        public static void Maintain()
        {
            try
            {
                if (!Directory.Exists(FolderPath))
                {
                    return;
                }

                var files = new List<FileEntry>();

                foreach (string path in Directory.EnumerateFiles(FolderPath, "*.log"))
                {
                    if (!TryParseName(Path.GetFileNameWithoutExtension(path), out DateTime day, out int part))
                    {
                        continue;
                    }

                    var info = new FileInfo(path);
                    if (info.Exists)
                    {
                        files.Add(new FileEntry(info, day, part));
                    }
                }

                if (files.Count == 0)
                {
                    return;
                }

                var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (LauncherSink.CurrentPath is { Length: > 0 } launcher)
                {
                    active.Add(launcher);
                }

                if (TavernSink.CurrentPath is { Length: > 0 } tavern)
                {
                    active.Add(tavern);
                }

                if (Pm2Sink.CurrentPath is { Length: > 0 } pm2)
                {
                    active.Add(pm2);
                }

                files.Sort((x, y) => x.Day != y.Day ? x.Day.CompareTo(y.Day) : x.Part.CompareTo(y.Part));

                // 1) 只保留当天：把更早的日期整批删掉
                if (LauncherSettings.KeepLatestLogsOnly)
                {
                    DateTime today = DateTime.Today;

                    foreach (FileEntry file in files.Where(item => item.Day < today).ToList())
                    {
                        if (TryDelete(file, active))
                        {
                            files.Remove(file);
                        }
                    }
                }

                // 2) 容量：从最旧删起，直到不超上限
                long limit = LauncherSettings.LogMaxSizeMb * 1024L * 1024L;
                long total = files.Sum(item => item.Info.Length);

                foreach (FileEntry file in files)
                {
                    if (total <= limit)
                    {
                        break;
                    }

                    // 先取大小再删：FileInfo 的属性是缓存的，Delete() 之后再读 Length 会抛异常，
                    // 那个异常会被外层 catch 吞掉，导致「只删一个文件就停手」
                    long size = file.Info.Length;

                    if (TryDelete(file, active))
                    {
                        total -= size;
                    }
                }
            }
            catch (Exception)
            {
                // 目录被占用、权限不足：下次再说
            }
        }

        private static bool TryDelete(FileEntry file, HashSet<string> active)
        {
            if (active.Contains(file.Info.FullName))
            {
                return false;
            }

            try
            {
                // 轮转刚开出来的新文件可能还不在 active 里：两秒内动过的先放过
                if (DateTime.UtcNow - file.Info.LastWriteTimeUtc < TimeSpan.FromSeconds(2))
                {
                    return false;
                }

                file.Info.Refresh();
                file.Info.Delete();
                return true;
            }
            catch (Exception)
            {
                // 被别的程序占着（用户正拿记事本开着它）：跳过
                return false;
            }
        }

        /// <summary>从文件名解析日期与分卷号：<c>launcher-20261122.log</c> / <c>launcher-20261122-2.log</c>。</summary>
        private static bool TryParseName(string name, out DateTime day, out int part)
        {
            day = default;
            part = 1;

            int dash = name.IndexOf('-', StringComparison.Ordinal);
            if (dash < 0)
            {
                return false;
            }

            string rest = name[(dash + 1)..];
            string datePart = rest;

            int second = rest.IndexOf('-', StringComparison.Ordinal);
            if (second > 0)
            {
                datePart = rest[..second];

                if (!int.TryParse(rest[(second + 1)..], out part))
                {
                    part = 1;
                }
            }

            return datePart.Length == 8
                && DateTime.TryParseExact(
                    datePart,
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out day);
        }

        private readonly record struct FileEntry(FileInfo Info, DateTime Day, int Part);

        // ==================== 单个文件 ====================

        /// <summary>一类日志的写句柄。跨天或超限时自己换文件。</summary>
        private sealed class LogSink : IDisposable
        {
            private readonly string _prefix;

            private StreamWriter? _writer;
            private DateTime _day;
            private int _part = 1;
            private long _bytes;

            public LogSink(string prefix)
            {
                _prefix = prefix;
            }

            /// <summary>当前正在写的文件（清理要避开它）。</summary>
            public string? CurrentPath { get; private set; }

            /// <summary>写一行，返回落到的文件路径；写不进去返回 null。</summary>
            public string? Write(LogLine line)
            {
                try
                {
                    if (_writer is null || line.Time.Date != _day)
                    {
                        Open(line.Time.Date, part: 1);
                    }

                    if (_writer is null || CurrentPath is null)
                    {
                        return null;
                    }

                    string text = $"{line.Time:yyyy-MM-dd HH:mm:ss.fff} {line.LevelTag,-5} {line.Text}";

                    _writer.WriteLine(text);
                    _bytes += Encoding.UTF8.GetByteCount(text) + Environment.NewLine.Length;

                    if (_bytes >= SingleFileLimitBytes)
                    {
                        // 单日写太大：换个卷继续写，旧卷才有机会被清理掉
                        _part++;
                        Open(line.Time.Date, _part);
                        RaiseRolled();
                    }

                    return CurrentPath;
                }
                catch (Exception)
                {
                    // 目录不可写、文件被独占：停写，不影响任何功能
                    Dispose();
                    return null;
                }
            }

            private void Open(DateTime day, int part)
            {
                CloseWriter();

                try
                {
                    Directory.CreateDirectory(FolderPath);

                    string name = part <= 1
                        ? $"{_prefix}-{day:yyyyMMdd}.log"
                        : $"{_prefix}-{day:yyyyMMdd}-{part}.log";

                    string path = Path.Combine(FolderPath, name);

                    // 共享读写与删除：页面要能读、清理要能删，都不该被写句柄挡住
                    var stream = new FileStream(
                        path,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete);

                    _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                    _day = day;
                    _part = part;
                    _bytes = stream.Length;
                    CurrentPath = path;
                }
                catch (Exception)
                {
                    CloseWriter();
                    CurrentPath = null;
                }
            }

            private void CloseWriter()
            {
                try
                {
                    _writer?.Dispose();
                }
                catch (Exception)
                {
                    // 关不掉也无所谓，进程退出时系统会收
                }

                _writer = null;
            }

            public void Dispose()
            {
                CloseWriter();
                CurrentPath = null;
            }

            /// <summary>
            /// 单文件上限：总配额的一半（下限 512 KB）。
            /// 不设成整个配额，是为了始终留出「可删的旧卷」——不然一个文件吃满上限，
            /// 清理时它自己正在写、删不掉，就永远降不下来。
            /// </summary>
            private static long SingleFileLimitBytes
            {
                get
                {
                    long half = LauncherSettings.LogMaxSizeMb * 1024L * 1024L / 2;
                    return Math.Max(MinSingleFileBytes, half);
                }
            }
        }
    }
}
