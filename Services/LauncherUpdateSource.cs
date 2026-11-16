using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>更新源。顺序即回退顺序，见 <see cref="LauncherUpdateSource.Order"/>。</summary>
    public enum UpdateSourceKind
    {
        /// <summary>GitCode 镜像仓库，国内直连最快，优先。</summary>
        GitCode,

        /// <summary>GitHub API 走加速中转。**不看用户开关**，更新永远先试这一档。</summary>
        GithubAccel,

        /// <summary>GitHub API 直连，最后的兜底。</summary>
        GithubDirect,
    }

    /// <summary>一个源这一次的结果，用来在界面上说清「为什么没查到」。</summary>
    public sealed record UpdateSourceProbe(
        UpdateSourceKind Kind,
        bool Ok,
        int Status,
        int Count,
        UpdateFailure Failure,
        TimeSpan Elapsed);

    /// <summary>失败原因。枚举而不是字符串：文案要跟着语言走，不能拿中文当逻辑。</summary>
    public enum UpdateFailure
    {
        None,

        /// <summary>连不上（DNS / 超时 / TLS）。</summary>
        Network,

        /// <summary>请求超时。</summary>
        Timeout,

        /// <summary>触发限流（GitHub 匿名 60 次/小时）。</summary>
        RateLimit,

        /// <summary>服务器返回了非 2xx。</summary>
        Server,

        /// <summary>返回内容不是预期的 JSON 结构。</summary>
        Payload,

        /// <summary>接口通了，但这个仓库一条发布都没有。</summary>
        Empty,
    }

    /// <summary>一次查询的汇总：拿到了哪些发布、最终用了哪个源、其余源为什么没用上。</summary>
    public sealed class ReleaseQueryResult
    {
        public IReadOnlyList<LauncherRelease> Releases { get; init; } = Array.Empty<LauncherRelease>();

        /// <summary>最终采信的源；全部失败时为 null。</summary>
        public UpdateSourceKind? Source { get; init; }

        public IReadOnlyList<UpdateSourceProbe> Probes { get; init; } = Array.Empty<UpdateSourceProbe>();

        public bool Ok => Source is not null;

        /// <summary>全部失败时最值得说的一条原因（取最后一个非 Empty 的失败，通常信息量最大）。</summary>
        public UpdateFailure Failure => Probes
            .Where(p => !p.Ok)
            .Select(p => p.Failure)
            .LastOrDefault(UpdateFailure.Empty);
    }

    /// <summary>
    /// 发布源查询：**GitCode → 加速 + GitHub → 直连 GitHub**，第一个拿到非空列表的就用。
    ///
    /// 为什么是固定三段、不看加速开关：
    /// · GitCode 是国内镜像，正常情况一条请求就够，省掉中转与限流；
    /// · 中转（gh-proxy）带 Token 轮询池，能避开 GitHub 匿名 60 次/小时的限制，
    ///   所以哪怕用户关掉了「GitHub 加速」，更新检测也照样先试它——
    ///   开关管的是日常下载走不走中转，不是「更新时不许兜底」；
    /// · 最后一档直连 GitHub：中转挂了、GitCode 没内容时的保底。
    ///
    /// 空列表（200 + <c>[]</c>）不算成功：GitCode 上镜像仓库常常还没同步 release，
    /// 这种情况必须继续往下试，而不是告诉用户「没有新版本」。
    /// </summary>
    public static class LauncherUpdateSource
    {
        public const string GitCodeApi =
            "https://api.gitcode.com/api/v5/repos/AstraBrew-Labs/AstraBrew-Launcher-Win/releases";

        public const string GithubApi =
            "https://api.github.com/repos/AstraBrew-Labs/AstraBrew-Launcher-Win/releases";

        /// <summary>回退顺序。改动这里就等于改动产品行为，别在别处另写一套顺序。</summary>
        public static IReadOnlyList<UpdateSourceKind> Order { get; } = new[]
        {
            UpdateSourceKind.GitCode,
            UpdateSourceKind.GithubAccel,
            UpdateSourceKind.GithubDirect,
        };

        /// <summary>每个源的实际请求地址。</summary>
        public static string UrlOf(UpdateSourceKind kind) => kind switch
        {
            UpdateSourceKind.GitCode => GitCodeApi,

            // 强制套中转：不看 LauncherSettings.GithubAcceleration
            UpdateSourceKind.GithubAccel => GithubAccel.ApplyAlways(GithubApi),

            _ => GithubApi,
        };

        /// <summary>源的名字（给界面用，走资源）。</summary>
        public static string NameOf(UpdateSourceKind kind) => kind switch
        {
            UpdateSourceKind.GitCode => L.Get("Update.Source.GitCode"),
            UpdateSourceKind.GithubAccel => L.Get("Update.Source.GithubAccel"),
            _ => L.Get("Update.Source.GithubDirect"),
        };

        /// <summary>按顺序试，第一个非空即返回；全都不行时把所有探针带回去。</summary>
        public static async Task<ReleaseQueryResult> QueryAsync(CancellationToken cancellationToken)
        {
            var probes = new List<UpdateSourceProbe>();

            foreach (UpdateSourceKind kind in Order)
            {
                cancellationToken.ThrowIfCancellationRequested();

                (UpdateSourceProbe probe, IReadOnlyList<LauncherRelease> releases) =
                    await FetchAsync(kind, cancellationToken).ConfigureAwait(false);

                probes.Add(probe);

                if (probe.Ok && releases.Count > 0)
                {
                    return new ReleaseQueryResult
                    {
                        Releases = releases,
                        Source = kind,
                        Probes = probes,
                    };
                }
            }

            return new ReleaseQueryResult { Probes = probes };
        }

        private static async Task<(UpdateSourceProbe Probe, IReadOnlyList<LauncherRelease> Releases)> FetchAsync(
            UpdateSourceKind kind,
            CancellationToken cancellationToken)
        {
            string url = UrlOf(kind);
            var watch = Stopwatch.StartNew();

            try
            {
                using HttpClient client = ProxyHttpClient.Create(TimeSpan.FromSeconds(12));
                client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

                using HttpResponseMessage response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false);

                string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                watch.Stop();

                if (!response.IsSuccessStatusCode)
                {
                    bool limited = response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests &&
                        (response.Headers.TryGetValues("X-RateLimit-Remaining", out IEnumerable<string>? rest) &&
                            rest.FirstOrDefault() == "0");

                    return (MakeProbe(kind, false, (int)response.StatusCode, 0,
                        limited ? UpdateFailure.RateLimit : UpdateFailure.Server, watch.Elapsed), Array.Empty<LauncherRelease>());
                }

                IReadOnlyList<LauncherRelease> releases = ParseList(body, out bool validJson);
                if (!validJson)
                {
                    return (MakeProbe(kind, false, (int)response.StatusCode, 0, UpdateFailure.Payload, watch.Elapsed),
                        Array.Empty<LauncherRelease>());
                }

                // 200 + 空数组：接口正常，只是这个仓库还没同步发布。标记成 Empty，让上层继续往下试。
                UpdateFailure failure = releases.Count == 0 ? UpdateFailure.Empty : UpdateFailure.None;
                return (MakeProbe(kind, true, (int)response.StatusCode, releases.Count, failure, watch.Elapsed), releases);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException)
            {
                watch.Stop();
                return (MakeProbe(kind, false, 0, 0, UpdateFailure.Timeout, watch.Elapsed), Array.Empty<LauncherRelease>());
            }
            catch (Exception)
            {
                // HttpRequestException / JsonException / TLS 握手失败都归到「连不上」，不值得细分给用户看
                watch.Stop();
                return (MakeProbe(kind, false, 0, 0, UpdateFailure.Network, watch.Elapsed), Array.Empty<LauncherRelease>());
            }
        }

        private static UpdateSourceProbe MakeProbe(
            UpdateSourceKind kind,
            bool ok,
            int status,
            int count,
            UpdateFailure failure,
            TimeSpan elapsed) => new(kind, ok, status, count, failure, elapsed);

        /// <summary>两个源的返回都是数组；解析单项失败就跳过那一项，不整批作废。</summary>
        private static IReadOnlyList<LauncherRelease> ParseList(string body, out bool validJson)
        {
            validJson = false;

            if (string.IsNullOrWhiteSpace(body))
            {
                return Array.Empty<LauncherRelease>();
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return Array.Empty<LauncherRelease>();
                }

                validJson = true;

                var list = new List<LauncherRelease>();
                foreach (JsonElement element in document.RootElement.EnumerateArray())
                {
                    LauncherRelease? release = LauncherRelease.Parse(element);
                    if (release is not null)
                    {
                        list.Add(release);
                    }
                }

                return list;
            }
            catch (JsonException)
            {
                return Array.Empty<LauncherRelease>();
            }
        }
    }
}
