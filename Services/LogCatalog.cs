using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AstraBrew_Launcher.Services
{
    /// <summary>一次读取的结果：截断过的话页面要提示「只显示最新 N 行」。</summary>
    public sealed record LogLoadResult(IReadOnlyList<LogLine> Lines, bool Truncated);

    /// <summary>
    /// 把磁盘上的日志读成可筛选的行。三类数据源：
    /// · <c>logs\runtime\launcher-*.log</c> —— 启动器运行日志（本功能新增）；
    /// · <c>logs\runtime\tavern-*.log</c> —— 酒馆输出；
    /// · <c>logs\*.log</c> —— 安装日志（<see cref="InstallLog"/>），只读、不参与清理。
    ///
    /// 三条性能约束：**从新文件往旧文件读**、**累计行数够了就停**、**扫描字节封顶**。
    /// 用户把容量上限调到 200MB 时，也不能让页面读出一个卡死的进度条。
    /// </summary>
    public static class LogCatalog
    {
        /// <summary>单次读取最多返回多少行（ListView 虚拟化下这个量级滚动是流畅的）。</summary>
        public const int MaxLinesPerArea = 5000;

        /// <summary>单次读取最多扫多少字节：防止上限调大后一屏加载几十秒。</summary>
        public const long MaxScanBytes = 32L * 1024 * 1024;

        private static readonly Regex RuntimePattern = new(
            @"^(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\s+(?<tag>[A-Z]+)\s+(?<text>.*)$",
            RegexOptions.Compiled);

        private static readonly Regex InstallPattern = new(
            @"^\[(?<time>\d{2}:\d{2}:\d{2}\.\d{3})\]\s+(?<tag>[A-Z]+)\s+(?<text>.*)$",
            RegexOptions.Compiled);

        /// <summary>读取并筛选。**在后台线程调用**：会碰磁盘，行数也不少。</summary>
        public static LogLoadResult Load(LogQuery query, CancellationToken token)
        {
            var collected = new List<LogLine>();
            long scanned = 0;

            if (query.ExactFile is { Length: > 0 } exact)
            {
                token.ThrowIfCancellationRequested();
                ReadInstallFile(exact, query, collected, ref scanned);
            }
            else if (query.IncludeRuntime)
            {
                string prefix = query.Area switch
                {
                    LogArea.Tavern => "tavern",
                    LogArea.Pm2 => "pm2",
                    _ => "launcher",
                };

                foreach (string path in EnumerateRuntimeFiles(prefix, query.From, query.To))
                {
                    token.ThrowIfCancellationRequested();
                    ReadRuntimeFile(path, query, collected, ref scanned);

                    // 多收一倍再截断：跨文件时只看「已收够」会丢掉更早的边界行
                    if (collected.Count >= MaxLinesPerArea * 2 || scanned >= MaxScanBytes)
                    {
                        break;
                    }
                }
            }

            // 安装日志只属于「启动器」页签：它是启动器自己的安装记录，与酒馆输出无关
            if (query.ExactFile is not { Length: > 0 } && query.Area == LogArea.Launcher && query.IncludeInstall)
            {
                foreach (string path in EnumerateInstallFiles(query.From, query.To))
                {
                    token.ThrowIfCancellationRequested();
                    ReadInstallFile(path, query, collected, ref scanned);

                    if (collected.Count >= MaxLinesPerArea * 2 || scanned >= MaxScanBytes)
                    {
                        break;
                    }
                }
            }

            collected.Sort((left, right) => left.Time.CompareTo(right.Time));

            bool truncated = collected.Count > MaxLinesPerArea;

            if (truncated)
            {
                collected.RemoveRange(0, collected.Count - MaxLinesPerArea);
            }

            return new LogLoadResult(collected, truncated);
        }

        // ==================== 文件枚举 ====================

        private static IEnumerable<string> EnumerateRuntimeFiles(string prefix, DateTime? from, DateTime? to)
        {
            if (!Directory.Exists(RuntimeLog.FolderPath))
            {
                return Array.Empty<string>();
            }

            var files = new List<(string Path, DateTime Day, int Part)>();

            try
            {
                foreach (string path in Directory.EnumerateFiles(RuntimeLog.FolderPath, prefix + "-*.log"))
                {
                    if (!TryParseRuntimeName(Path.GetFileNameWithoutExtension(path), prefix, out DateTime day, out int part))
                    {
                        continue;
                    }

                    if (from is { } start && day < start.Date)
                    {
                        continue;
                    }

                    if (to is { } end && day > end.Date)
                    {
                        continue;
                    }

                    files.Add((path, day, part));
                }
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }

            // 从新到旧：最新的先读，凑够上限就不必再碰更早的文件
            return files
                .OrderByDescending(item => item.Day)
                .ThenByDescending(item => item.Part)
                .Select(item => item.Path)
                .ToList();
        }

        private static IEnumerable<string> EnumerateInstallFiles(DateTime? from, DateTime? to)
        {
            if (!Directory.Exists(InstallLog.FolderPath))
            {
                return Array.Empty<string>();
            }

            var files = new List<(string Path, DateTime Day)>();

            try
            {
                foreach (string path in Directory.EnumerateFiles(InstallLog.FolderPath, "*.log"))
                {
                    // 运行日志在子目录里，这层 glob 不会命中；但万一有别的子目录，这里也排除掉
                    if (!string.Equals(Path.GetDirectoryName(path), InstallLog.FolderPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    DateTime day = TryParseInstallDay(path, out DateTime parsed)
                        ? parsed
                        : File.GetLastWriteTime(path).Date;

                    if (from is { } start && day < start.Date)
                    {
                        continue;
                    }

                    if (to is { } end && day > end.Date)
                    {
                        continue;
                    }

                    files.Add((path, day));
                }
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }

            return files
                .OrderByDescending(item => item.Day)
                .Select(item => item.Path)
                .ToList();
        }

        // ==================== 文件解析 ====================

        private static void ReadRuntimeFile(string path, LogQuery query, List<LogLine> target, ref long scanned)
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

                string? raw;

                while ((raw = reader.ReadLine()) is not null)
                {
                    scanned += raw.Length + 2;

                    if (scanned > MaxScanBytes)
                    {
                        return;
                    }

                    Match match = RuntimePattern.Match(raw);
                    if (!match.Success)
                    {
                        continue;
                    }

                    if (!DateTime.TryParseExact(
                            match.Groups["time"].Value,
                            "yyyy-MM-dd HH:mm:ss.fff",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out DateTime time))
                    {
                        continue;
                    }

                    var line = new LogLine(
                        time,
                        query.Area,
                        LogOriginKind.Runtime,
                        ParseLevel(match.Groups["tag"].Value),
                        match.Groups["text"].Value,
                        path);

                    if (Matches(line, query))
                    {
                        target.Add(line);
                    }
                }
            }
            catch (Exception)
            {
                // 文件被删、被独占：跳过，页面显示其余部分
            }
        }

        private static void ReadInstallFile(string path, LogQuery query, List<LogLine> target, ref long scanned)
        {
            try
            {
                DateTime day = TryParseInstallDay(path, out DateTime parsed)
                    ? parsed
                    : File.GetLastWriteTime(path).Date;

                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

                // 头部几行没有时间戳（标题 / 系统信息 / 分隔线），用文件日期兜底，
                // 这样「哪个任务、什么环境失败」这类关键信息不会在页面上消失
                DateTime fallback = day;
                string? raw;

                while ((raw = reader.ReadLine()) is not null)
                {
                    scanned += raw.Length + 2;

                    if (scanned > MaxScanBytes)
                    {
                        return;
                    }

                    string text;
                    LogLevel level;
                    DateTime time;

                    Match match = InstallPattern.Match(raw);

                    if (match.Success)
                    {
                        text = match.Groups["text"].Value;
                        level = ParseLevel(match.Groups["tag"].Value);

                        time = TimeSpan.TryParseExact(
                            match.Groups["time"].Value,
                            @"hh\:mm\:ss\.fff",
                            CultureInfo.InvariantCulture,
                            out TimeSpan clock)
                            ? day.Add(clock)
                            : fallback;

                        fallback = time;
                    }
                    else
                    {
                        if (raw.Length == 0)
                        {
                            continue;
                        }

                        text = raw;
                        level = LogLevel.Info;
                        time = fallback;
                    }

                    var line = new LogLine(time, LogArea.Launcher, LogOriginKind.Install, level, text, path);

                    if (Matches(line, query))
                    {
                        target.Add(line);
                    }
                }
            }
            catch (Exception)
            {
                // 同上：跳过读不了的文件
            }
        }

        private static bool Matches(LogLine line, LogQuery query)
        {
            if (query.From is { } from && line.Time < from)
            {
                return false;
            }

            if (query.To is { } to && line.Time > to)
            {
                return false;
            }

            return query.Accepts(line.Level);
        }

        /// <summary>
        /// 标签映射。运行日志用统一标签；安装日志的 <c>FAIL</c> 归到错误，
        /// 酒馆的 <c>OUT</c> 归到输出。
        /// </summary>
        private static LogLevel ParseLevel(string tag) => tag.ToUpperInvariant() switch
        {
            "SYS" => LogLevel.System,
            "OK" => LogLevel.Success,
            "WARN" => LogLevel.Warning,
            "ERROR" or "ERR" or "FAIL" => LogLevel.Error,
            "OUT" => LogLevel.Output,
            _ => LogLevel.Info,
        };

        // ==================== 文件名 ====================

        /// <summary><c>launcher-20261122.log</c> / <c>launcher-20261122-2.log</c>。</summary>
        private static bool TryParseRuntimeName(string name, string prefix, out DateTime day, out int part)
        {
            day = default;
            part = 1;

            if (!name.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string rest = name[(prefix.Length + 1)..];
            string datePart = rest;

            int dash = rest.IndexOf('-', StringComparison.Ordinal);
            if (dash > 0)
            {
                datePart = rest[..dash];

                if (!int.TryParse(rest[(dash + 1)..], out part))
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

        /// <summary>
        /// 安装日志文件名：<c>{task}-{yyyyMMdd}-{HHmmss}.log</c>。
        /// task 自身可能带连字符（<c>sillytavern-deps</c>），所以**从右往左**取日期与时间。
        /// </summary>
        private static bool TryParseInstallDay(string path, out DateTime day)
        {
            day = default;

            string name = Path.GetFileNameWithoutExtension(path);
            string[] parts = name.Split('-');

            if (parts.Length < 3)
            {
                return false;
            }

            return DateTime.TryParseExact(
                parts[^2],
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out day);
        }
    }
}
