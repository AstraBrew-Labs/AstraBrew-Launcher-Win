using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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

        public static ReleaseResult Empty { get; } = new(
            Array.Empty<TavernRelease>(),
            DateTimeOffset.MinValue,
            string.Empty,
            false,
            null);
    }

    /// <summary>
    /// 内置实例的版本列表：GitHub Releases API + 本地缓存。
    ///
    /// 关于限流与缓存的关系（这里以前想错过一次）：
    /// · <b>加速是一条独立出口，不是同一条线路的备胎</b>。中转端自己有 Token 池，
    ///   直连的 60 次/小时与它无关。所以某条出口返回 403/429 时**不能直接放弃**，
    ///   要继续试下一条——这正是「加速绕开 QPS 限制」的实际含义。
    /// · <b>缓存与节流只为「不要频繁请求」</b>，不是「永远不请求」：缓存 15 分钟内直接用，
    ///   过期就重新校验一次；失败后 5 分钟内不再重试，避免每次翻页都去撞墙。
    /// · <b>条件请求</b>：带上上一次的 <c>ETag</c>（<c>If-None-Match</c>），GitHub 回 304 时
    ///   不消耗额度——所以「经常校验」和「省额度」可以同时成立。
    /// · <b>失败不空手</b>：刷新失败时继续显示旧缓存，并把原因折成一句话给界面。
    /// </summary>
    public static class TavernReleases
    {
        /// <summary>GitHub 发布列表接口；<c>per_page</c> 取 30 足够覆盖下拉框要用的历史。</summary>
        public const string ApiUrl =
            "https://api.github.com/repos/SillyTavern/SillyTavern/releases?per_page=30";

        /// <summary>发布说明保留多少字进缓存。完整正文常有十几 KB，界面上也看不完。</summary>
        private const int NotesLimit = 12000;

        /// <summary>缓存的新鲜期：这段时间内直接用，不碰网络。</summary>
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

        /// <summary>一次请求失败后的冷却期，期间不再自动重试（手动刷新不受限制）。</summary>
        private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(5);

        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

        private static readonly SemaphoreSlim Gate = new(1, 1);

        private static ReleaseResult? _memory;

        /// <summary>上一次成功响应带的 ETag；条件请求用它换 304，不消耗额度。</summary>
        private static string? _etag;

        /// <summary>
        /// 这个 ETag 是哪条出口给的。
        /// 中转端（前面挂着 CDN）与 GitHub 直连给出的 ETag 是两套，混着用只会换来无意义的
        /// 200 全量响应——所以只把 ETag 发回给当初那条出口。
        /// </summary>
        private static string? _etagSource;

        private static DateTimeOffset _lastFailureAt = DateTimeOffset.MinValue;
        private static string? _lastFailure;

        /// <summary>上次失败时的出口配置（开关 / 代理）。配置变了，冷却期就该作废。</summary>
        private static string? _lastFailureExit;

        /// <summary>缓存文件；删掉只会让下次多请求一次，不影响任何配置。</summary>
        public static string CachePath { get; } =
            Path.Combine(AppPaths.CacheRoot, "sillytavern-releases.json");

        /// <summary>当前内存里的列表，没加载过时为空。</summary>
        public static IReadOnlyList<TavernRelease> Current => _memory?.Releases ?? Array.Empty<TavernRelease>();

        /// <summary>
        /// 取发布列表。<paramref name="force"/> 为 true 时无视缓存与冷却，直接请求；
        /// 请求失败但手里还有缓存时返回旧数据并带上 <see cref="ReleaseResult.Error"/>。
        /// 永不抛异常（取消除外）。
        /// </summary>
        public static async Task<ReleaseResult> GetAsync(bool force, CancellationToken token)
        {
            await Gate.WaitAsync(token).ConfigureAwait(false);

            try
            {
                // 内存缓存比磁盘更近：同一进程里切页签不需要反复读文件
                ReleaseResult? cached = _memory ?? ReadCache();
                if (cached is not null)
                {
                    _memory = cached;
                }

                if (!force && cached is not null && IsFresh(cached))
                {
                    return cached with { FromCache = true, Error = null };
                }

                // 冷却期只在「手里有列表可看」时才生效：没有缓存还压着不请求，
                // 用户看到的就是一个既要不到数据、又不会去要的死页面。
                // 出口配置变了也要立即作废——刚打开加速却还在吃上一次直连失败的冷却，
                // 表现就是「开了加速还是那个 403」。
                string exit = ExitSignature();
                if (!force
                    && cached is not null
                    && _lastFailure is { Length: > 0 }
                    && string.Equals(_lastFailureExit, exit, StringComparison.Ordinal)
                    && DateTimeOffset.Now - _lastFailureAt < FailureCooldown)
                {
                    return cached with
                    {
                        FromCache = true,
                        Error = $"刚才没能刷新，先看缓存的列表。{_lastFailure}",
                    };
                }

                try
                {
                    (IReadOnlyList<TavernRelease> releases, string source, bool notModified) =
                        await FetchAsync(cached?.Releases, token).ConfigureAwait(false);

                    if (notModified && cached is not null)
                    {
                        // 304：内容没变，但「校验过」本身就说明缓存是最新的
                        ReleaseResult verified = cached with
                        {
                            FetchedAt = DateTimeOffset.Now,
                            Source = source,
                            FromCache = false,
                            Error = null,
                        };

                        WriteCache(verified);
                        _memory = verified;
                        _lastFailure = null;
                        return verified;
                    }

                    var fresh = new ReleaseResult(releases, DateTimeOffset.Now, source, false, null);
                    WriteCache(fresh);
                    _memory = fresh;
                    _lastFailure = null;
                    return fresh;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _lastFailure = Describe(ex);
                    _lastFailureAt = DateTimeOffset.Now;
                    _lastFailureExit = exit;

                    // 刷新失败时不要把手里的旧列表也弄丢：用户至少还能看到上一次的结果
                    ReleaseResult? fallback = cached ?? _memory;
                    if (fallback is not null)
                    {
                        return fallback with
                        {
                            FromCache = true,
                            Error = $"这次没能刷新，显示的是上次缓存的版本列表。{_lastFailure}",
                        };
                    }

                    return ReleaseResult.Empty with { Error = _lastFailure };
                }
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>比较两个版本号，&gt; 0 表示 <paramref name="left"/> 更新。</summary>
        /// <remarks>解析不了就退回字符串比较，至少保证结果稳定。</remarks>
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

        private static bool IsFresh(ReleaseResult result)
            => result.Releases.Count > 0
                && result.FetchedAt != DateTimeOffset.MinValue
                && DateTimeOffset.Now - result.FetchedAt < CacheTtl;

        // ==================== 网络 ====================

        private static async Task<(IReadOnlyList<TavernRelease> Releases, string Source, bool NotModified)> FetchAsync(
            IReadOnlyList<TavernRelease>? known,
            CancellationToken token)
        {
            var failures = new List<string>();
            var throttled = new List<string>();
            string? resumeHint = null;

            foreach (SourceTarget target in GithubAccel.Targets(ApiUrl))
            {
                token.ThrowIfCancellationRequested();

                // 中转端偶发的 403 值得再来一次：它前面挂着 CDN（响应带 60 秒缓存），
                // 换个时间点常常就命中缓存直接成了。
                // 直连不信这一套——GitHub 的 403 是「这一小时的 60 次用完了」，重试只是再花一次额度。
                int attempts = target.Name == GithubAccel.AccelName ? 2 : 1;

                for (int attempt = 1; attempt <= attempts; attempt++)
                {
                    if (attempt > 1)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(1200), token).ConfigureAwait(false);
                    }

                    // ETag 只发回给当初那条出口：中转端和直连给的 ETag 是两套
                    bool conditional = known is { Count: > 0 }
                        && _etag is { Length: > 0 }
                        && string.Equals(_etagSource, target.Name, StringComparison.Ordinal);

                    try
                    {
                        using HttpClient client = ProxyHttpClient.Create(RequestTimeout);
                        using var request = new HttpRequestMessage(HttpMethod.Get, target.Url);
                        request.Headers.Accept.ParseAdd("application/vnd.github+json");
                        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

                        if (conditional)
                        {
                            request.Headers.TryAddWithoutValidation("If-None-Match", _etag);
                        }

                        using HttpResponseMessage response =
                            await client.SendAsync(request, token).ConfigureAwait(false);

                        if (response.StatusCode == HttpStatusCode.NotModified)
                        {
                            // 304 按 GitHub 的规定不计入额度：这次「刷新」几乎不花钱
                            return (known!, target.Name, true);
                        }

                        if ((int)response.StatusCode is 403 or 429)
                        {
                            // 限流只针对这一条出口：中转端有自己的 Token 池，直连是 60 次/小时。
                            // 一条被拒不代表另一条也不行，所以继续试，绝不直接放弃。
                            if (attempt < attempts)
                            {
                                continue;
                            }

                            throttled.Add(target.Name);
                            resumeHint ??= ResumeHint(response);
                            failures.Add($"{target.Name} 被限流（HTTP {(int)response.StatusCode}）");
                            break;
                        }

                        if (!response.IsSuccessStatusCode)
                        {
                            failures.Add($"{target.Name} HTTP {(int)response.StatusCode}");
                            break;
                        }

                        string json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                        ApiRelease[]? payload = JsonSerializer.Deserialize(
                            json,
                            ReleaseJsonContext.Default.ApiReleaseArray);

                        if (payload is null)
                        {
                            failures.Add($"{target.Name} 返回的内容无法解析");
                            break;
                        }

                        IReadOnlyList<TavernRelease> releases = Map(payload);
                        if (releases.Count == 0)
                        {
                            failures.Add($"{target.Name} 返回的发布列表为空");
                            break;
                        }

                        _etag = ReadEtag(response);
                        _etagSource = target.Name;
                        return (releases, target.Name, false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
                    {
                        if (attempt < attempts)
                        {
                            continue;
                        }

                        failures.Add($"{target.Name} 连接失败");
                        break;
                    }
                }
            }

            if (throttled.Count > 0)
            {
                // 只有真的存在加速出口时才说「两条都试过了」：
                // 开关开着但链接不是 GitHub 的（或开关关着）时，那样说是不符合事实的
                bool accelerated = GithubAccel.Targets(ApiUrl)
                    .Any(target => target.Name == GithubAccel.AccelName);

                throw new FetchException(BuildThrottleMessage(throttled, failures, resumeHint, accelerated));
            }

            throw new FetchException(
                failures.Count > 0
                    ? $"拿不到版本列表（{string.Join("；", failures)}）。"
                    : "拿不到版本列表。");
        }

        /// <summary>
        /// 出口特征：加速开关 + 代理方式。
        /// 用它判断「上次的失败还作不作数」——用户刚打开加速，就不该继续吃直连失败的冷却。
        /// </summary>
        private static string ExitSignature()
            => $"{(GithubAccel.Enabled ? "accel" : "direct")}|{(int)LauncherSettings.Proxy}|{LauncherSettings.CustomProxy}";

        private static string BuildThrottleMessage(
            List<string> throttled,
            List<string> failures,
            string? resumeHint,
            bool accelerated)
        {
            string when = resumeHint is null ? string.Empty : $"额度大约 {resumeHint}恢复。";

            // 另一条出口可能是因为别的原因失败的（连不上、502），如实带上，别一律说成「被限流」
            List<string> others = failures
                .Where(item => !item.Contains("被限流", StringComparison.Ordinal))
                .ToList();

            string detail = others.Count > 0 ? $"另外{string.Join("；", others)}。" : string.Empty;
            // 中转端的 403 往往是它自己的频率限制（响应带 60 秒 CDN 缓存），
            // 和「配置错了」不是一回事，别让用户去翻设置
            string advice = accelerated
                ? "加速与直连都试过了：中转端对同一 IP 有频率限制，隔一两分钟再点「刷新版本列表」通常就能过；"
                    + "直连那边则是 GitHub 的每小时 60 次。"
                : "打开「设置 → 网络 → GitHub 加速服务」可以换一条出口——中转端有自己的配额，不用等直连那 60 次/小时恢复。";

            return $"GitHub 接口限流（{string.Join("、", throttled)} 被拒）。{when}{detail}{advice}";
        }

        /// <summary>从限流响应里读「什么时候恢复」：优先 Retry-After，其次 X-RateLimit-Reset。</summary>
        private static string? ResumeHint(HttpResponseMessage response)
        {
            if (response.Headers.RetryAfter is { } retry)
            {
                if (retry.Delta is { } delta)
                {
                    int minutes = (int)Math.Ceiling(delta.TotalMinutes);
                    return minutes <= 1 ? "在 1 分钟内" : $"在 {minutes} 分钟内";
                }

                if (retry.Date is { } date)
                {
                    return $"在 {date.ToLocalTime():HH:mm} 之后";
                }
            }

            if (response.Headers.TryGetValues("X-RateLimit-Reset", out IEnumerable<string>? values)
                && long.TryParse(values.FirstOrDefault(), out long epoch)
                && epoch > 0 && epoch < 4102444800)
            {
                return $"在 {DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime():HH:mm} 之后";
            }

            return null;
        }

        private static string? ReadEtag(HttpResponseMessage response)
        {
            string? tag = response.Headers.ETag?.Tag;
            return string.IsNullOrWhiteSpace(tag) ? null : tag;
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

                _etag = string.IsNullOrWhiteSpace(file.Etag) ? null : file.Etag;
                _etagSource = string.IsNullOrWhiteSpace(file.Source) ? null : file.Source;

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
                    Etag = _etag,
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

        /// <summary>条件请求用的 ETag；镜像与直连偶尔会给不同的值，以最近一次成功为准。</summary>
        [JsonPropertyName("etag")]
        public string? Etag { get; set; }

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
