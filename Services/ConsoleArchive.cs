using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 控制台自己的存档：把控制台显示过的每一行单独存一份，下次打开启动器时原样载回来。
    ///
    /// 为什么不复用 <c>tavern-*.log</c>：
    /// · 那份受「只保留当天」与容量上限约束，跨天就被清理，昨天下班前的内容第二天看不到；
    /// · 它是「酒馆输出的流水」，而控制台是「这台机器的操作记录」——两者生命周期不一样。
    ///
    /// 为什么不去 PM2 的 out 文件重放：
    /// 重放拿不到我们自己的行（<c>$ pm2 start …</c>、启动耗时、端口…），时间戳也全变成「现在」，
    /// 内容结构每次都不一样。存档 + 载入才能做到「跟上次一模一样」。
    ///
    /// 文件名故意不叫 <c>console-yyyyMMdd.log</c>：<see cref="RuntimeLog.Maintain"/> 只认
    /// 「前缀-日期」这种名字，取个不带日期的名字，它就永远不会被当成运行日志清掉。
    /// </summary>
    public static class ConsoleArchive
    {
        /// <summary>存档路径：<c>logs\runtime\console.log</c>（固定一份，不按天切）。</summary>
        public static string FilePath { get; } = Path.Combine(RuntimeLog.FolderPath, "console.log");

        /// <summary>超过这个大小就在会话开始时裁到 <see cref="KeepBytes"/>：只留最近的一段。</summary>
        private const long TrimThresholdBytes = 2L * 1024 * 1024;

        private const long KeepBytes = 1L * 1024 * 1024;

        /// <summary>行格式与运行日志一致：<c>yyyy-MM-dd HH:mm:ss.fff TAG 文字</c>。</summary>
        private static readonly Regex LinePattern = new(
            @"^(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\s+(?<tag>[A-Z]+)\s+(?<text>.*)$",
            RegexOptions.Compiled);

        private static readonly object Gate = new();

        /// <summary>
        /// 追加一行（由 <see cref="RuntimeLog"/> 的落盘线程调用，天然串行）。
        /// 写不进去就静默停写——存档不是功能前置条件。
        /// </summary>
        public static void Append(string tag, string text)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(RuntimeLog.FolderPath);

                    File.AppendAllText(
                        FilePath,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {tag,-5} {text}{Environment.NewLine}",
                        new UTF8Encoding(false));
                }
            }
            catch (Exception)
            {
                // 目录不可写 / 文件被独占：放弃这一行，不影响任何功能
            }
        }

        /// <summary>
        /// 读出最近若干行，用于启动时填回控制台。
        ///
        /// 只读文件尾部（<see cref="MaxScanBytes"/>）：存档可能有 1MB 上下，
        /// 一次全读进内存再解析没有必要，也拖慢首屏。
        /// </summary>
        public static IReadOnlyList<TavernLogEntry> Load(int maxLines)
        {
            try
            {
                if (!File.Exists(FilePath) || maxLines <= 0)
                {
                    return Array.Empty<TavernLogEntry>();
                }

                string text;

                using (var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long start = Math.Max(0, stream.Length - MaxScanBytes);
                    stream.Seek(start, SeekOrigin.Begin);

                    using var reader = new StreamReader(stream, Encoding.UTF8);

                    if (start > 0)
                    {
                        // 从中间开始必然有一条半截行：丢掉
                        reader.ReadLine();
                    }

                    text = reader.ReadToEnd();
                }

                var entries = new List<TavernLogEntry>();

                foreach (string line in text.Split('\n'))
                {
                    Match match = LinePattern.Match(line.TrimEnd('\r'));

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

                    entries.Add(new TavernLogEntry(time, ToKind(match.Groups["tag"].Value), match.Groups["text"].Value));
                }

                if (entries.Count > maxLines)
                {
                    entries.RemoveRange(0, entries.Count - maxLines);
                }

                return entries;
            }
            catch (Exception)
            {
                return Array.Empty<TavernLogEntry>();
            }
        }

        /// <summary>
        /// 会话开始时裁一次：只保留最后 <see cref="KeepBytes"/> 字节，并从行首对齐，
        /// 免得留下一条半截行。
        /// </summary>
        public static void Trim()
        {
            try
            {
                lock (Gate)
                {
                    if (!File.Exists(FilePath))
                    {
                        return;
                    }

                    var info = new FileInfo(FilePath);

                    if (info.Length <= TrimThresholdBytes)
                    {
                        return;
                    }

                    string tail;

                    using (var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        stream.Seek(info.Length - KeepBytes, SeekOrigin.Begin);

                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        reader.ReadLine();
                        tail = reader.ReadToEnd();
                    }

                    string temporary = FilePath + ".tmp";
                    File.WriteAllText(temporary, tail, new UTF8Encoding(false));
                    File.Move(temporary, FilePath, overwrite: true);
                }
            }
            catch (Exception)
            {
                // 裁不掉就让它继续长：宁可占点磁盘，也不能因为清理失败影响启动
            }
        }

        /// <summary>存档里的 TAG 只有五种（与运行日志同一套），解析回来即可。</summary>
        private static TavernLogKind ToKind(string tag) => tag switch
        {
            "SYS" => TavernLogKind.System,
            "OK" => TavernLogKind.Success,
            "WARN" => TavernLogKind.Warning,
            "ERROR" => TavernLogKind.Error,
            "OUT" => TavernLogKind.Output,
            _ => TavernLogKind.Info,
        };

        /// <summary>单次最多往回读多少字节。</summary>
        private const long MaxScanBytes = 512 * 1024;
    }
}
