using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AstraBrew_Launcher.Services
{
    /// <summary>一条操作属于哪一类，决定列表里的图标与点击后的跳转目标。</summary>
    public enum ActivityCategory
    {
        /// <summary>改了设置。</summary>
        Setting,

        /// <summary>启动 / 停止 / 接管酒馆。</summary>
        Service,

        /// <summary>下载或安装（酒馆本体、依赖、Node、Git、PM2、Caddy）。</summary>
        Install,

        /// <summary>扫描本地实例。</summary>
        Scan,

        /// <summary>补全运行环境。</summary>
        Env,
    }

    /// <summary>
    /// 一条操作记录。
    /// </summary>
    /// <param name="Text">动作本身，例如「下载酒馆本体」。记录时已按当时的界面语言渲染。</param>
    /// <param name="Detail">补充说明：设置项的旧值 → 新值、安装结果、扫描数量。</param>
    /// <param name="LogFile">这次操作对应的日志文件（安装日志才有）；点条目时直接打开它。</param>
    /// <param name="LogAt">操作发生时刻，用来把运行日志定位到那一段。</param>
    public sealed record ActivityEntry(
        DateTime Time,
        ActivityCategory Kind,
        string Text,
        string? Detail,
        string? LogFile,
        DateTime LogAt);

    /// <summary>
    /// 操作记录：**用户做了什么、什么时候做的**。
    ///
    /// 与运行日志的关系：
    /// · 运行日志记「程序说了什么」，操作记录记「人做了什么」；
    /// · 每条记录都带时间与（可选）日志文件，点一下就能跳到对应的运行日志或那次安装的完整日志——
    ///   这就是「操作记录与运行日志配合」的全部含义，不做跨文件的行级关联（PM2 输出没有时间戳，做不到）。
    ///
    /// 存储：<c>logs\runtime\activity\activity-yyyyMMdd.jsonl</c>，一行一条 JSON。
    /// 用 JSON 而不是定长文本：正文里可能有换行与分隔符，拼字符串迟早会解析错。
    /// </summary>
    public static class ActivityLog
    {
        /// <summary>操作记录目录。</summary>
        public static string FolderPath { get; } = Path.Combine(RuntimeLog.FolderPath, "activity");

        /// <summary>保留多少天。比运行日志长得多：这是「我上周改过什么」的唯一线索。</summary>
        private const int KeepDays = 90;

        private static readonly object Gate = new();

        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

            // 中文原样写：这份文件是给人翻的（客户支持、自己排查），转成 \uXXXX 谁都不想读。
            // 它不进 HTML / JS，没有 XSS 面，放宽转义没有风险。
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>
        /// 是否开始记录。启动过程本身会写一堆设置（载入、白名单规范化），
        /// 那些不是用户操作，等界面起来之后才打开。
        /// </summary>
        public static bool Enabled { get; set; }

        /// <summary>新记录已写入。界面据此增量刷新，不用整表重读。</summary>
        public static event EventHandler<ActivityEntry>? Recorded;

        /// <summary>
        /// 记一条。**任何线程都可以调**：安装线程、日志线程都会用。
        /// 写不进去就静默丢弃——操作记录不是功能前置条件。
        /// </summary>
        public static void Record(
            ActivityCategory kind,
            string text,
            string? detail = null,
            string? logFile = null,
            DateTime? logAt = null)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            DateTime now = DateTime.Now;

            var entry = new ActivityEntry(now, kind, text, detail, logFile, logAt ?? now);

            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(FolderPath);

                    string path = Path.Combine(FolderPath, $"activity-{now:yyyyMMdd}.jsonl");

                    File.AppendAllText(
                        path,
                        JsonSerializer.Serialize(Row.From(entry), Json) + "\n",
                        new UTF8Encoding(false));
                }
            }
            catch (Exception)
            {
                return;
            }

            Recorded?.Invoke(null, entry);
        }

        /// <summary>
        /// 读区间内的记录（新的在前，最多 <paramref name="max"/> 条）。
        /// **在后台线程调用**：会碰磁盘。
        /// </summary>
        public static IReadOnlyList<ActivityEntry> Load(DateTime from, DateTime to, int max)
        {
            var entries = new List<ActivityEntry>();

            if (!Directory.Exists(FolderPath))
            {
                return entries;
            }

            try
            {
                foreach (string path in EnumerateFiles(from, to))
                {
                    foreach (string line in File.ReadLines(path, Encoding.UTF8))
                    {
                        if (line.Length == 0)
                        {
                            continue;
                        }

                        if (Row.TryParse(line, out ActivityEntry entry)
                            && entry.Time >= from
                            && entry.Time <= to)
                        {
                            entries.Add(entry);
                        }
                    }
                }
            }
            catch (Exception)
            {
                return entries;
            }

            entries.Sort((left, right) => right.Time.CompareTo(left.Time));

            if (entries.Count > max)
            {
                entries.RemoveRange(max, entries.Count - max);
            }

            return entries;
        }

        /// <summary>删掉过期的按天文件。由 <see cref="RuntimeLog.StartSession"/> 每次启动调一次。</summary>
        public static void Maintain()
        {
            try
            {
                if (!Directory.Exists(FolderPath))
                {
                    return;
                }

                DateTime cutoff = DateTime.Today.AddDays(-KeepDays);

                foreach (string path in Directory.EnumerateFiles(FolderPath, "activity-*.jsonl"))
                {
                    if (!TryParseDay(Path.GetFileNameWithoutExtension(path), out DateTime day))
                    {
                        continue;
                    }

                    if (day >= cutoff)
                    {
                        continue;
                    }

                    try
                    {
                        File.Delete(path);
                    }
                    catch (Exception)
                    {
                        // 正在被读/被占用：下次再说
                    }
                }
            }
            catch (Exception)
            {
                // 清理失败不影响任何功能
            }
        }

        private static IEnumerable<string> EnumerateFiles(DateTime from, DateTime to)
        {
            var files = new List<(string Path, DateTime Day)>();

            foreach (string path in Directory.EnumerateFiles(FolderPath, "activity-*.jsonl"))
            {
                if (TryParseDay(Path.GetFileNameWithoutExtension(path), out DateTime day)
                    && day >= from.Date
                    && day <= to.Date)
                {
                    files.Add((path, day));
                }
            }

            // 从旧到新读，最后按时间排序时「同毫秒」的顺序才稳定
            files.Sort((left, right) => left.Day.CompareTo(right.Day));

            return files.Select(item => item.Path);
        }

        private static bool TryParseDay(string name, out DateTime day)
        {
            day = default;
            int dash = name.IndexOf('-', StringComparison.Ordinal);

            return dash > 0
                && DateTime.TryParseExact(
                    name[(dash + 1)..],
                    "yyyyMMdd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out day);
        }

        /// <summary>落盘的形状。字段名压到一个字母：一天几千条时文件能小一半。</summary>
        private sealed record Row(string t, string k, string x, string? d, string? f, string? a)
        {
            public static Row From(ActivityEntry entry) => new(
                entry.Time.ToString("yyyy-MM-ddTHH:mm:ss.fff"),
                entry.Kind.ToString(),
                entry.Text,
                entry.Detail,
                entry.LogFile,
                entry.LogAt.ToString("yyyy-MM-ddTHH:mm:ss.fff"));

            public static bool TryParse(string line, out ActivityEntry entry)
            {
                entry = default!;

                try
                {
                    Row? row = JsonSerializer.Deserialize<Row>(line, Json);

                    if (row is null
                        || !DateTime.TryParse(row.t, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime time))
                    {
                        return false;
                    }

                    DateTime logAt = DateTime.TryParse(
                        row.a,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out DateTime parsed)
                        ? parsed
                        : time;

                    entry = new ActivityEntry(
                        time,
                        Enum.TryParse(row.k, out ActivityCategory kind) ? kind : ActivityCategory.Setting,
                        row.x,
                        row.d,
                        row.f,
                        logAt);

                    return true;
                }
                catch (JsonException)
                {
                    // 半行（写到一半被杀）或格式变动：跳过这一条，不影响其余
                    return false;
                }
            }
        }
    }
}
