using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 仪表盘上那些数字的真实来源。
    ///
    /// 以前它们是写死的样例（312 MB、1,284 次请求、48 张角色卡……），页面上没有任何一处对得上磁盘。
    /// 这里只做「读」：装了多少扩展、存了多少角色卡、占了多少盘、今天开了几次页面，
    /// 全部现算——用户自己往目录里拷张卡、删个扩展，刷新一下就变了。
    /// </summary>
    public sealed record TavernMetrics(
        int BuiltinExtensions,
        int ThirdPartyExtensions,
        int CharacterCount,
        long CharacterBytes,
        long InstallBytes,
        long DataBytes,
        bool AccessLogEnabled,
        int TodayConnections,
        DateTime SampledAt)
    {
        public static TavernMetrics Empty { get; } = new(0, 0, 0, 0, 0, 0, false, 0, DateTime.MinValue);

        /// <summary>安装目录 + 数据目录的总占用。</summary>
        public long TotalBytes => InstallBytes + DataBytes;
    }

    /// <summary>
    /// 指标采集。磁盘遍历要几百毫秒到几秒（安装目录里躺着 node_modules），
    /// 所以只在后台线程跑，调用方拿到结果再回 UI。
    /// </summary>
    public static class TavernMetricsService
    {
        /// <summary>结果缓存：来回切页面不必把几万个文件再数一遍。</summary>
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(20);

        private static readonly object Gate = new();
        private static TavernMetrics _cached = TavernMetrics.Empty;
        private static DateTime _cachedAt = DateTime.MinValue;
        private static Task<TavernMetrics>? _running;

        // CPU 采样要两次采样的差值，这里记住上一次的时间点
        private static TimeSpan _lastCpuTime;
        private static long _lastCpuAt;

        /// <summary>采集一次；<paramref name="force"/> 为 true 时忽略缓存（刷新按钮）。</summary>
        public static Task<TavernMetrics> CollectAsync(bool force = false)
        {
            lock (Gate)
            {
                if (!force && DateTime.UtcNow - _cachedAt < CacheTtl)
                {
                    return Task.FromResult(_cached);
                }

                // 已经在采了就直接复用，别为一次刷新再遍历一遍磁盘
                if (_running is not null)
                {
                    return _running;
                }

                _running = Task.Run(() =>
                {
                    TavernMetrics value = Collect();

                    lock (Gate)
                    {
                        _cached = value;
                        _cachedAt = DateTime.UtcNow;
                        _running = null;
                    }

                    return value;
                });

                return _running;
            }
        }

        private static TavernMetrics Collect()
        {
            string installRoot = TavernInstances.Current.Path;

            // 资源页看的是同一层目录：<c>default-user</c> 就在它下面。
            // 用 ResolveDataRoot 在实例模式下会指到代码目录，角色卡数量永远读成 0。
            string dataRoot = TavernRuntime.ResolveUserRoot();

            int builtin = CountBuiltinExtensions(installRoot);
            int thirdParty = CountThirdPartyExtensions(installRoot, dataRoot);

            (int characters, long characterBytes) = CountCharacters(dataRoot);

            long dataBytes = DirectorySize(dataRoot);
            long installBytes = DirectorySize(installRoot);

            (bool accessLogEnabled, int today) = CountTodayConnections(dataRoot);

            return new TavernMetrics(
                builtin,
                thirdParty,
                characters,
                characterBytes,
                installBytes,
                dataBytes,
                accessLogEnabled,
                today,
                DateTime.Now);
        }

        // ==================== 扩展 ====================

        /// <summary>内置扩展：安装目录里带 <c>manifest.json</c> 的子目录。</summary>
        private static int CountBuiltinExtensions(string installRoot)
        {
            string root = Path.Combine(installRoot, "public", "scripts", "extensions");

            try
            {
                if (!Directory.Exists(root))
                {
                    return 0;
                }

                return Directory.EnumerateDirectories(root).Count(
                    dir => File.Exists(Path.Combine(dir, "manifest.json")));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        }

        /// <summary>
        /// 第三方扩展有三个可能的落点：安装目录的 <c>third-party</c>、
        /// 数据目录的 <c>extensions</c> 与 <c>default-user/extensions</c>（不同版本用过不同位置）。
        /// 同一份扩展可能同时存在于两处，按目录名去重。
        /// </summary>
        private static int CountThirdPartyExtensions(string installRoot, string dataRoot)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string root in new[]
            {
                Path.Combine(installRoot, "public", "scripts", "extensions", "third-party"),
                Path.Combine(dataRoot, "extensions"),
                Path.Combine(dataRoot, "default-user", "extensions"),
            })
            {
                try
                {
                    if (!Directory.Exists(root))
                    {
                        continue;
                    }

                    foreach (string dir in Directory.EnumerateDirectories(root))
                    {
                        names.Add(Path.GetFileName(dir));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 某个位置读不到就跳过，不要因此把整块指标判成 0
                }
            }

            return names.Count;
        }

        // ==================== 角色卡 ====================

        /// <summary>
        /// 角色卡数量与体积。酒馆把每张卡存成 <c>characters/&lt;名字&gt;.png</c>，
        /// 而与它同名的**目录**里放的是表情差分/资源，不算卡——
        /// 递归数会把一个角色的二十几张表情全算成角色卡，数字立刻失真。
        /// </summary>
        private static (int Count, long Bytes) CountCharacters(string dataRoot)
        {
            int count = 0;
            long bytes = 0;

            foreach (string root in new[]
            {
                Path.Combine(dataRoot, "default-user", "characters"),
                Path.Combine(dataRoot, "characters"),
            })
            {
                try
                {
                    if (!Directory.Exists(root))
                    {
                        continue;
                    }

                    foreach (string file in Directory.EnumerateFiles(root, "*.png", SearchOption.TopDirectoryOnly))
                    {
                        count++;

                        try
                        {
                            bytes += new FileInfo(file).Length;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // 文件刚好被酒馆改写：只算数量，不算体积
                        }
                    }

                    // 两张位置都算过就重复了，找到第一处就够
                    if (count > 0)
                    {
                        break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return (count, bytes);
                }
            }

            return (count, bytes);
        }

        // ==================== 目录体积 ====================

        /// <summary>目录总字节数；读不到的文件（被占用、没权限）直接跳过，不抛异常。</summary>
        public static long DirectorySize(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                return 0;
            }

            long total = 0;

            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                };

                foreach (FileInfo file in new DirectoryInfo(path).EnumerateFiles("*", options))
                {
                    total += file.Length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 整个目录读不了就返回已经数到的部分
            }

            return total;
        }

        // ==================== 访问日志 ====================

        /// <summary>
        /// 今天的连接次数，来自酒馆自己写的 <c>access.log</c>
        /// （每行形如 <c>2026-11-03T13:46:39.393Z 127.0.0.1 curl/8.16.0</c>，时间是 UTC）。
        ///
        /// 这个文件只在 <c>logging.enableAccessLog</c> 打开时才写，所以关着的时候
        /// 界面要显示「访问日志未开启」，而不是一个永远为零的数字。
        /// </summary>
        private static (bool Enabled, int Today) CountTodayConnections(string dataRoot)
        {
            string log = Path.Combine(dataRoot, "access.log");

            if (!File.Exists(log))
            {
                return (false, 0);
            }

            int today = 0;
            DateTime localToday = DateTime.Today;

            try
            {
                foreach (string line in File.ReadLines(log))
                {
                    int space = line.IndexOf(' ');
                    if (space <= 0)
                    {
                        continue;
                    }

                    if (!DateTime.TryParse(
                        line[..space],
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                        out DateTime stamp))
                    {
                        continue;
                    }

                    if (stamp.ToLocalTime().Date == localToday)
                    {
                        today++;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (false, 0);
            }

            return (true, today);
        }

        // ==================== 进程采样 ====================

        /// <summary>
        /// 采样子进程的内存与 CPU。<c>cpuPercent</c> 为 null 表示「还没有第二次采样」，
        /// 界面该显示「—」而不是 0%。
        ///
        /// 放在这里而不是页面：页面卸载会丢状态，两次采样之间隔多久也就没法算了。
        /// </summary>
        public static (long MemoryBytes, double? CpuPercent) SampleProcess()
        {
            Process? process = TavernRuntime.LiveProcess;

            if (process is null)
            {
                _lastCpuTime = TimeSpan.Zero;
                _lastCpuAt = 0;
                return (0, null);
            }

            try
            {
                process.Refresh();

                long memory = process.WorkingSet64;
                long now = Environment.TickCount64;
                TimeSpan cpu = process.TotalProcessorTime;

                double? percent = null;

                if (_lastCpuAt > 0 && now > _lastCpuAt)
                {
                    double elapsedMs = now - _lastCpuAt;
                    double usedMs = (cpu - _lastCpuTime).TotalMilliseconds;
                    int cores = Math.Max(1, Environment.ProcessorCount);

                    // 单核占比 = 用掉的 CPU 时间 / 经过的时间；再按核心数折算成整机占比
                    percent = Math.Clamp(usedMs / elapsedMs / cores * 100, 0, 100);
                }

                _lastCpuTime = cpu;
                _lastCpuAt = now;

                return (memory, percent);
            }
            catch
            {
                return (0, null);
            }
        }

        /// <summary>把字节数写成人看的大小（<c>312 MB</c> / <c>1.8 GB</c>）。</summary>
        public static string FormatSize(long bytes)
        {
            if (bytes <= 0)
            {
                return "—";
            }

            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;

            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return unit <= 1
                ? $"{value:0} {units[unit]}"
                : $"{value:0.#} {units[unit]}";
        }
    }
}
