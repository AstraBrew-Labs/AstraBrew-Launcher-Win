using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 一条 GitHub 发布。字段只留界面真正用得上的：版本、发布说明、时间与体积。
    /// 版本号已去掉 <c>v</c> 前缀，和 <see cref="TavernInstance.Version"/> 同一套写法。
    /// </summary>
    public sealed record TavernRelease(
        string Version,
        string Tag,
        string? Name,
        DateTimeOffset? PublishedAt,
        bool Prerelease,
        long Size,
        string? Notes)
    {
        /// <summary>发布日期；拿不到时明说，不猜一个看起来合理的日期。</summary>
        public string DateLabel => PublishedAt is { } at
            ? at.ToLocalTime().ToString("yyyy-MM-dd")
            : "日期未知";

        public string ChannelLabel => Prerelease ? "预发布" : "稳定版";

        public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);
    }

    /// <summary>一次取列表的结果。<see cref="Error"/> 非空表示这次没请求成功（可能仍带着旧缓存）。</summary>
    public sealed record ReleaseResult(
        IReadOnlyList<TavernRelease> Releases,
        DateTimeOffset FetchedAt,
        string Source,
        bool FromCache,
        string? Error)
    {
        public int StableCount => Releases.Count(release => !release.Prerelease);
    }

    /// <summary>
    /// 内置实例的版本列表：GitHub Releases API + 本地缓存。
    ///
    /// 约定：
    /// · <b>缓存优先</b>：只要本地有缓存就直接用，不请求网络——未登录的 GitHub API
    ///   每小时只有 60 次额度，页面每进一次就拉一次很快就会 403。
    /// · <b>强制刷新</b>只由用户点「刷新版本列表」触发，成功后覆盖缓存。
    /// · <b>加速跟随开关</b>：请求经 <see cref="GithubAccel.Targets"/> 排地址，开着先走
    ///   <c>gh-proxy.org</c> 再退回直连；关掉就只连直连。与下载源选的是 Gitee / GitCode /
    ///   GitHub 无关——加速针对的是 github 域名本身，不是某一条下载通道。
    /// · <b>失败不空手</b>：强制刷新失败时继续显示旧缓存，并把原因折成一句话给界面。
    /// </summary>
    public static class TavernReleases
    {
        /// <summary>GitHub 发布列表接口；<c>per_page</c> 取 30 足够覆盖下拉框要用的历史。</summary>
        public const string ApiUrl =
            "https://api.github.com/repos/SillyTavern/SillyTavern/releases?per_page=30";

        /// <summary>发布说明保留多少字进缓存。完整正文常有十几 KB，界面上也看不完。</summary>
        private const int NotesLimit = 12000;

        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

        private static readonly SemaphoreSlim Gate = new(1, 1);

        private static ReleaseResult? _memory;

        /// <summary>缓存文件；删掉只会让下次多请求一次，不影响任何配置。</summary>
        public static string CachePath { get; } =
            Path.Combine(AppPaths.CacheRoot, "sillytavern-releases.json");

        /// <summary>当前内存里的列表，没加载过时为空。</summary>
        public static IReadOnlyList<TavernRelease> Current => _memory?.Releases ?? Array.Empty<TavernRelease>();

        /// <summary>
        /// 取发布列表。<paramref name="force"/> 为 true 时跳过缓存重新请求；
        /// 请求失败但手里还有缓存时返回旧数据并带上 <see cref="ReleaseResult.Error"/>。
        /// 永不抛异常（取消除外）。
        /// </summary>
        public static async Task<ReleaseResult> GetAsync(bool force, CancellationToken token)
        {
            await Gate.WaitAsync(token).ConfigureAwait(false);

            try
            {
                // 内存缓存比磁盘更近：同一进程里切页签不需要反复读文件
                if (!force && _memory is not null)
                {
                    return _memory with { FromCache = true };
                }

                if (!force)
                {
                    ReleaseResult? cached = ReadCache();
                    if (cached is not null)
                    {
                        _memory = cached;
                        return cached;
                    }
                }

                try
                {
                    (IReadOnlyList<TavernRelease> releases, string source) =
                        await FetchAsync(token).ConfigureAwait(false);

                    var fresh = new ReleaseResult(releases, DateTimeOffset.Now, source, false, null);
                    WriteCache(fresh);
                    _memory = fresh;
                    return fresh;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 刷新失败时不要把手里的旧列表也弄丢：用户至少还能看到上一次的结果
                    ReleaseResult? fallback = _memory ?? ReadCache();
                    if (fallback is not null)
                    {
                        return fallback with
                        {
                            FromCache = true,
                            Error = $"这次没能刷新，显示的是上次缓存的版本列表。{Describe(ex)}",
                        };
                    }

                    return new ReleaseResult(
                        Array.Empty<TavernRelease>(),
                        DateTimeOffset.MinValue,
                        string.Empty,
                        false,
                        Describe(ex));
                }
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>
        /// 比较两个版本号，&gt; 0 表示 <paramref name="left"/> 更新。
        /// 解析不了就退回字符串比较，至少保证结果稳定。
        /// </summary>
        public static int Compare(string? left, string? right)
        {
            string a = Numeric(left);
            string b = Numeric(right);

            if (Version.TryParse(a, out Version? va) && Version.TryParse(b, out Version? vb))
            {
                return va.CompareTo(vb);
            }

            return string.CompareOrdinal(a, b);
        }

        // ==================== 网络 ====================

        private static async Task<(IReadOnlyList<TavernRelease> Releases, string Source)> FetchAsync(
            CancellationToken token)
        {
            var failures = new List<string>();

            foreach (SourceTarget target in GithubAccel.Targets(ApiUrl))
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    using HttpClient client = ProxyHttpClient.Create(RequestTimeout);
                    using var request = new HttpRequestMessage(HttpMethod.Get, target.Url);
                    request.Headers.Accept.ParseAdd("application/vnd.github+json");
                    request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

                    using HttpResponseMessage response =
                        await client.SendAsync(request, token).ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        failures.Add($"{target.Name} HTTP {(int)response.StatusCode}");

                        // 403 / 429 是限流，换直连也一样，早点说清楚比逐个试一遍更快
                        if ((int)response.StatusCode is 403 or 429)
                        {
                            throw new FetchException(
                                "GitHub 接口被限流（403）。等一小时后再试，或切换「GitHub 加速服务」开关换一条出口。");
                        }

                        continue;
                    }

                    string json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    ApiRelease[]? payload = JsonSerializer.Deserialize(
                        json,
                        ReleaseJsonContext.Default.ApiReleaseArray);

                    if (payload is null)
                    {
                        failures.Add($"{target.Name} 返回的内容无法解析");
                        continue;
                    }

                    IReadOnlyList<TavernRelease> releases = Map(payload);
                    if (releases.Count == 0)
                    {
                        failures.Add($"{target.Name} 返回的发布列表为空");
                        continue;
                    }

                    return (releases, target.Name);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (FetchException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
                {
                    failures.Add($"{target.Name} 连接失败");
                }
            }

            throw new FetchException(
                failures.Count > 0
                    ? $"拿不到版本列表（{string.Join("；", failures)}）。"
                    : "拿不到版本列表。");
        }

        private static List<TavernRelease> Map(ApiRelease[] payload)
        {
            var releases = new List<TavernRelease>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ApiRelease item in payload)
            {
                if (item.Draft || string.IsNullOrWhiteSpace(item.TagName))
                {
                    continue;
                }

                string tag = item.TagName.Trim();
                string version = Numeric(tag);
                if (version.Length == 0 || !seen.Add(version))
                {
                    continue;
                }

                string? notes = item.Body;
                if (notes is { Length: > NotesLimit })
                {
                    notes = notes[..NotesLimit] + "\n\n…（发布说明过长，已截断）";
                }

                releases.Add(new TavernRelease(
                    version,
                    tag,
                    item.Name,
                    item.PublishedAt,
                    item.Prerelease,
                    item.Assets?.Sum(asset => asset.Size) ?? 0,
                    notes));
            }

            // API 已按发布时间倒序，这里再排一次，避免不同镜像返回的顺序不一致
            releases.Sort((a, b) =>
            {
                int byDate = Nullable.Compare(b.PublishedAt, a.PublishedAt);
                return byDate != 0 ? byDate : Compare(b.Version, a.Version);
            });

            return releases;
        }

        // ==================== 缓存 ====================

        private static ReleaseResult? ReadCache()
        {
            try
            {
                if (!File.Exists(CachePath))
                {
                    return null;
                }

                string json = File.ReadAllText(CachePath, Encoding.UTF8);
                ReleaseCacheFile? file = JsonSerializer.Deserialize(
                    json,
                    ReleaseJsonContext.Default.ReleaseCacheFile);

                if (file is null || file.Releases is null || file.Releases.Count == 0)
                {
                    return null;
                }

                var releases = file.Releases
                    .Select(entry => new TavernRelease(
                        Numeric(entry.Tag),
                        entry.Tag,
                        entry.Name,
                        entry.PublishedAt,
                        entry.Prerelease,
                        entry.Size,
                        entry.Notes))
                    .Where(release => release.Version.Length > 0)
                    .ToList();

                if (releases.Count == 0)
                {
                    return null;
                }

                return new ReleaseResult(
                    releases,
                    file.FetchedAt,
                    string.IsNullOrWhiteSpace(file.Source) ? "本地缓存" : file.Source,
                    true,
                    null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // 缓存坏了就当没有：下一次请求会重新写一份
                return null;
            }
        }

        private static void WriteCache(ReleaseResult result)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.CacheRoot);

                var file = new ReleaseCacheFile
                {
                    FetchedAt = result.FetchedAt,
                    Source = result.Source,
                };

                foreach (TavernRelease release in result.Releases)
                {
                    file.Releases.Add(new ReleaseCacheEntry
                    {
                        Tag = release.Tag,
                        Name = release.Name,
                        PublishedAt = release.PublishedAt,
                        Prerelease = release.Prerelease,
                        Size = release.Size,
                        Notes = release.Notes,
                    });
                }

                string json = JsonSerializer.Serialize(
                    file,
                    ReleaseJsonContext.Default.ReleaseCacheFile);

                // 先写临时文件再换名：写一半崩掉时不会留下一个解析不了的缓存
                string temp = CachePath + ".tmp";
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                File.Move(temp, CachePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 缓存写不进去只影响下次要不要重新请求，不该让整个页面失败
            }
        }

        // ==================== 杂项 ====================

        /// <summary>去掉 <c>v</c> 前缀与 <c>-beta.1</c> 之类的后缀，留下可比较的数字部分。</summary>
        private static string Numeric(string? value)
        {
            string trimmed = (value ?? string.Empty).Trim().TrimStart('v', 'V', ' ');
            int dash = trimmed.IndexOf('-');
            return dash >= 0 ? trimmed[..dash] : trimmed;
        }

        private static string Describe(Exception ex) => ex switch
        {
            FetchException fetch => fetch.Message,
            HttpRequestException => "连不上 GitHub，请检查网络或代理后重试。",
            TaskCanceledException => "连接 GitHub 超时，请检查网络或代理后重试。",
            _ => "读取版本列表时出错，请稍后重试。",
        };

        private sealed class FetchException : Exception
        {
            public FetchException(string message, Exception? inner = null)
                : base(message, inner)
            {
            }
        }
    }

    /// <summary>GitHub Releases API 的响应条目，只声明用得上的字段。</summary>
    internal sealed class ApiRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("assets")]
        public List<ApiAsset>? Assets { get; set; }
    }

    internal sealed class ApiAsset
    {
        [JsonPropertyName("size")]
        public long Size { get; set; }
    }

    internal sealed class ReleaseCacheFile
    {
        [JsonPropertyName("fetched_at")]
        public DateTimeOffset FetchedAt { get; set; }

        [JsonPropertyName("source")]
        public string? Source { get; set; }

        [JsonPropertyName("releases")]
        public List<ReleaseCacheEntry> Releases { get; set; } = new();
    }

    internal sealed class ReleaseCacheEntry
    {
        [JsonPropertyName("tag")]
        public string Tag { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("notes")]
        public string? Notes { get; set; }
    }

    /// <summary>
    /// 发布列表的序列化上下文。用源生成而不是反射：Release 构建会裁剪程序集，
    /// 反射版 <c>JsonSerializer</c> 在裁剪后会在运行时找不到元数据。
    /// </summary>
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(ApiRelease[]))]
    [JsonSerializable(typeof(ReleaseCacheFile))]
    internal partial class ReleaseJsonContext : JsonSerializerContext
    {
    }
}
