using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 每块盘上一次扫完时数出来「一共有多少个目录」。
    ///
    /// 为什么要记这个：全盘扫一遍之前，谁也不知道这块盘有多少目录，没有分母就没法画进度环、
    /// 也算不出还要多久。目录数是扫完才得到的副产品，顺手存下来，下次开扫就有分母了——
    /// 进度环从第一秒就是真的往前走，而不是先空转几分钟。
    ///
    /// 缓存只是「预估」，不是判据：磁盘增减目录都会让它偏，所以界面里它与实时发现数取大者，
    /// 扫完一律画成 100%。过期或读不出来都当没有，绝不影响扫描本身。
    /// </summary>
    public static partial class ScanStats
    {
        public static string StorePath { get; } =
            Path.Combine(AppPaths.RoamingRoot, "data", "scan_stats.json");

        private static readonly object Gate = new();
        private static Dictionary<string, long>? _cache;

        /// <summary>这块盘上次扫出多少个目录；没记录过返回 0。</summary>
        public static long Expected(string root)
        {
            Dictionary<string, long> cache = Load();
            lock (Gate)
            {
                return cache.TryGetValue(Key(root), out long count) ? count : 0;
            }
        }

        /// <summary>扫描正常结束时调用：把这次数出来的目录数记下来，给下次的分母用。</summary>
        public static void Remember(IReadOnlyList<string> roots, IReadOnlyList<long> counts)
        {
            if (roots.Count == 0)
            {
                return;
            }

            Dictionary<string, long> cache = Load();
            lock (Gate)
            {
                for (int i = 0; i < roots.Count && i < counts.Count; i++)
                {
                    if (counts[i] > 0)
                    {
                        cache[Key(roots[i])] = counts[i];
                    }
                }

                Save(cache);
            }
        }

        private static string Key(string root) =>
            root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();

        private static Dictionary<string, long> Load()
        {
            lock (Gate)
            {
                if (_cache is not null)
                {
                    return _cache;
                }

                _cache = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    if (!File.Exists(StorePath))
                    {
                        return _cache;
                    }

                    ScanStatsFile? file = JsonSerializer.Deserialize(
                        File.ReadAllText(StorePath, Encoding.UTF8),
                        ScanStatsJsonContext.Default.ScanStatsFile);

                    if (file?.Drives is null)
                    {
                        return _cache;
                    }

                    foreach (KeyValuePair<string, ScanStatsEntry> pair in file.Drives)
                    {
                        if (pair.Value.Directories > 0)
                        {
                            _cache[Key(pair.Key)] = pair.Value.Directories;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    // 缓存坏了当没有：下一次扫描结束会重写
                }

                return _cache;
            }
        }

        private static void Save(Dictionary<string, long> cache)
        {
            try
            {
                var file = new ScanStatsFile();
                foreach (KeyValuePair<string, long> pair in cache)
                {
                    file.Drives[pair.Key] = new ScanStatsEntry { Directories = pair.Value };
                }

                Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);

                string json = JsonSerializer.Serialize(file, ScanStatsJsonContext.Default.ScanStatsFile);

                // 先写临时文件再换名：写一半崩掉不会留下一个解析不了的缓存
                string temporary = StorePath + ".tmp";
                File.WriteAllText(temporary, json, new UTF8Encoding(false));
                File.Move(temporary, StorePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 存不下只影响下次的预估精度，不值得打扰用户
            }
        }

        internal sealed class ScanStatsFile
        {
            [JsonPropertyName("drives")]
            public Dictionary<string, ScanStatsEntry> Drives { get; set; } = new();
        }

        internal sealed class ScanStatsEntry
        {
            [JsonPropertyName("directories")]
            public long Directories { get; set; }
        }

        [JsonSourceGenerationOptions(WriteIndented = true)]
        [JsonSerializable(typeof(ScanStatsFile))]
        internal partial class ScanStatsJsonContext : JsonSerializerContext
        {
        }
    }
}
