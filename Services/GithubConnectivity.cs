using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 一个要探测的节点。地址是真实的——探测发的是真请求，延迟也是真的量出来的。
    /// </summary>
    /// <param name="Accelerable">
    /// 这条地址在加速开关打开时是否**拼中转前缀**。API / Raw / git 走前缀才有效；
    /// 官网首页是 HTML 页面，中转只会 301 回 github.com（实测），拼了反而多绕一跳。
    /// </param>
    public sealed record GithubProbe(
        string Key,
        string Name,
        string Hint,
        string Url,
        bool IsGit = false,
        bool Accelerable = true);

    /// <summary>
    /// 一次探测的结果。<see cref="Note"/> 是「能用但不理想」的说明，<see cref="Error"/> 是失败原因，
    /// <see cref="Status"/> 是 HTTP 状态码（拿不到响应头时为空）——判断 403 限流靠它，不解析文案。
    /// </summary>
    public sealed record GithubProbeResult(
        GithubProbe Probe,
        bool Ok,
        int? LatencyMs,
        string? Error,
        string? Note,
        int? Status = null,
        string? Route = null)
    {
        /// <summary>没测（例如没装 Git）。</summary>
        public static GithubProbeResult Skipped(GithubProbe probe, string note) =>
            new(probe, false, null, null, note);

        /// <summary>API 被限流（403）：能连上，但拿不到数据。</summary>
        public bool IsRateLimited => Status == 403;
    }

    /// <summary>克隆探测的实时进度：这一条走的是哪条线路 + 百分比 + 速度。</summary>
    public sealed record GithubCloneProgress(int Percent, string Phase, string? Speed, string Route);

    /// <summary>
    /// GitHub 连通性测试：真的去访问官网、API、Raw，再把仓库**完整**克隆一份下来。
    ///
    /// 以前这里是四条写死的延迟数字（412 / 268 / 0 / 168），点「重新测试」只是让它们
    /// 按固定节奏再出现一遍——用户换了代理、断了网，界面照样一片绿。
    ///
    /// 现在的规则按「代理 × 加速」四种组合走，测的就是真实出口：
    /// <list type="bullet">
    /// <item>都没开：不走代理、不拼前缀，直接连 GitHub；</item>
    /// <item>只开代理：请求经代理发出，不拼前缀；</item>
    /// <item>只开加速：**不走代理**，API / Raw / git 的地址前面拼 <see cref="GithubAccel.Prefix"/>；</item>
    /// <item>两个都开：既走代理，也拼前缀。</item>
    /// </list>
    ///
    /// 四条探测并发跑（HTTP 三条 + git 一条在线程池里跑），谁先回来谁先填那一行。
    /// 克隆与「从哪下载最快」用同一套参数（<c>--depth 1 --single-branch --progress</c>），
    /// 真把约 100 MB 的文件传下来，并解析进度——只有真下过文件，才能说明安装时能不能用；
    /// 测完（无论成败、包括超时）都把临时目录删掉。
    /// </summary>
    public static class GithubConnectivity
    {
        private const string RepositoryUrl = "https://github.com/SillyTavern/SillyTavern";

        private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// 单条克隆线路最多等多久。拉的是完整工作区（约 100 MB），45 秒意味着
        /// 低于约 2.3 MB/s 的通道直接判超时——**装酒馆时也是这点文件，再慢就等于不能用**，
        /// 与其让用户以为「通了、只是慢」，不如老实报超时。
        /// </summary>
        private static readonly TimeSpan CloneRouteTimeout = TimeSpan.FromSeconds(45);

        /// <summary>
        /// 整个克隆探测的总预算（加速 → 直连最多两条线路共用）。
        /// 没有这个上限时，两条线路各等一遍最坏要 90 秒以上，弹窗一直转，用户只会以为卡死。
        /// </summary>
        private static readonly TimeSpan CloneBudget = TimeSpan.FromSeconds(90);

        /// <summary>剩余预算少于这个数就别再开下一条线路了——刚启动就要超时，纯属浪费。</summary>
        private static readonly TimeSpan MinCloneWindow = TimeSpan.FromSeconds(8);

        /// <summary>克隆探测的总时限（秒）。界面文案直接引用它，免得改了常量忘了改说明。</summary>
        public static int CloneBudgetSeconds => (int)CloneBudget.TotalSeconds;

        /// <summary>按当前设置列出四条要测的项（顺序就是界面上的顺序）。</summary>
        public static IReadOnlyList<GithubProbe> BuildProbes() => new[]
        {
            new GithubProbe(
                "web",
                "官网访问",
                "https://github.com（HTML 页面，加速不代理页面）",
                "https://github.com",
                Accelerable: false),
            new GithubProbe(
                "api",
                "API 访问",
                "仓库 Release 元数据",
                "https://api.github.com/repos/SillyTavern/SillyTavern/releases"),
            new GithubProbe(
                "raw",
                "Raw 访问",
                "仓库内的 default/config.yaml",
                "https://raw.githubusercontent.com/SillyTavern/SillyTavern/refs/heads/release/default/config.yaml"),
            new GithubProbe(
                "clone",
                "克隆仓库",
                "真拉完整仓库（约 100 MB）到临时目录，测完自动删除",
                RepositoryUrl,
                IsGit: true),
        };

        /// <summary>当前实际走的那条出口，直接写进弹窗，省得用户猜「到底用没用代理」。</summary>
        public static string DescribeExit()
        {
            string accel = GithubAccel.Enabled
                ? $"加速开启（API / Raw / 克隆走 {GithubAccel.Prefix}）"
                : "加速关闭";

            return $"当前出口：{ProxyRoute.Resolve().Describe()} · {accel}";
        }

        /// <summary>
        /// 探测一个节点。永不抛异常，失败折成结果返回。
        /// <paramref name="progress"/> 只对克隆那一条有意义：拉取过程中持续报百分比与速度。
        /// </summary>
        public static Task<GithubProbeResult> RunAsync(
            GithubProbe probe,
            CancellationToken token,
            IProgress<GithubCloneProgress>? progress = null)
            => probe.IsGit ? RunCloneAsync(probe, token, progress) : RunHttpAsync(probe, token);

        // ==================== 出口策略 ====================

        /// <summary>
        /// 探测用的 HttpClient。代理是**显式**配的，不交给系统默认行为：
        /// 「只开加速」时必须真的不走代理，否则测出来的绿是代理给的——用户关掉代理就装不上。
        /// </summary>
        private static HttpClient CreateClient(TimeSpan timeout, ProxyRoute route)
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = true };

            if (!route.UseProxy)
            {
                handler.UseProxy = false;
            }
            else if (route.Address is { Length: > 0 } address)
            {
                handler.UseProxy = true;
                handler.Proxy = new System.Net.WebProxy(address);
            }
            else
            {
                // PAC / 自动检测：地址由脚本决定，交给 Windows 自己解析
                handler.UseProxy = true;
            }

            var client = new HttpClient(handler) { Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AstraBrew-Launcher/1.0");
            return client;
        }

        /// <summary>
        /// <summary>
        /// 一条探测这次要依次试的地址。加速开着时是「加速 → 直连」两条：
        /// 中转侧自己也会被限流（实测 403），这时候直连往往还能用；
        /// 反过来直连被墙时加速能救。**只试一条就下结论，等于把另一条的能力白白丢掉。**
        /// 加速关着就只有直连——没让用户开的线路不该偷偷用。
        /// 只拼前缀、不改路径：中转端自己会做规范化。
        /// </summary>
        private static IReadOnlyList<(string Route, string Url)> ResolveUrls(GithubProbe probe)
        {
            if (!probe.Accelerable || !GithubAccel.Enabled || !GithubAccel.IsGithubUrl(probe.Url))
            {
                return new[] { ("直连", probe.Url) };
            }

            return new[]
            {
                ("加速", GithubAccel.Prefix + probe.Url.Trim()),
                ("直连", probe.Url),
            };
        }

        // ==================== HTTP ====================

        private static async Task<GithubProbeResult> RunHttpAsync(GithubProbe probe, CancellationToken token)
        {
            ProxyRoute route = ProxyRoute.Resolve();
            var attempts = new List<GithubProbeResult>();

            foreach ((string routeName, string url) in ResolveUrls(probe))
            {
                GithubProbeResult attempt = await TryHttpAsync(probe, route, url, routeName, token)
                    .ConfigureAwait(false);

                if (attempt.Ok)
                {
                    return attempt;
                }

                attempts.Add(attempt);

                if (token.IsCancellationRequested)
                {
                    break;
                }
            }

            return MergeAttempts(probe, attempts);
        }

        /// <summary>真正发一次请求。永不抛（除取消）。</summary>
        private static async Task<GithubProbeResult> TryHttpAsync(
            GithubProbe probe,
            ProxyRoute route,
            string url,
            string routeName,
            CancellationToken token)
        {
            var clock = Stopwatch.StartNew();

            try
            {
                using HttpClient client = CreateClient(HttpTimeout, route);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);

                // 只要响应头与开头几百字节：连通性不需要整个文件
                request.Headers.Range = new RangeHeaderValue(0, 2048);
                request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

                using HttpResponseMessage response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);

                int latency = Elapsed(clock);
                int code = (int)response.StatusCode;

                if (response.IsSuccessStatusCode)
                {
                    return new GithubProbeResult(probe, true, latency, null, null, code, routeName);
                }

                return new GithubProbeResult(probe, false, latency, $"HTTP {code}", null, code, routeName);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException)
            {
                return new GithubProbeResult(
                    probe, false, null, $"超时（{HttpTimeout.TotalSeconds:0} 秒）", null, null, routeName);
            }
            catch (HttpRequestException ex)
            {
                return new GithubProbeResult(
                    probe, false, null, DescribeHttpError(ex), null, null, routeName);
            }
        }

        /// <summary>
        /// 一条通道所有线路都失败之后的结论。挑法有讲究：
        /// 两条都 403 → 是真限流（换线路也没用，只能等）；
        /// 只有一条 403、另一条别的错 → 报 403（用户还能靠换线路解决）；
        /// 都不是 403 → 把两条的原因都写出来，别只留最后一条让人以为另一条没试过。
        /// </summary>
        private static GithubProbeResult MergeAttempts(GithubProbe probe, List<GithubProbeResult> attempts)
        {
            if (attempts.Count == 0)
            {
                return new GithubProbeResult(probe, false, null, "没有可用线路", null);
            }

            if (attempts.Count == 1)
            {
                return attempts[0];
            }

            if (attempts.All(attempt => attempt.IsRateLimited))
            {
                return attempts[^1] with { Error = "两条线路都返回 403", Route = null };
            }

            GithubProbeResult? limited = attempts.FirstOrDefault(attempt => attempt.IsRateLimited);
            if (limited is not null)
            {
                return limited with { Error = $"HTTP 403（{limited.Route}）" };
            }

            string detail = string.Join(" / ", attempts.Select(
                attempt => $"{attempt.Route}：{attempt.Error ?? "失败"}"));

            return attempts[^1] with { Error = detail };
        }

        // ==================== 克隆 ====================

        /// <summary>
        /// 真的拉一次仓库。加速开着时先走加速线路，失败再回落直连——和 HTTP 那三条一个规矩：
        /// 中转侧自己也会被限流（实测 403），只试一条就下结论，等于把另一条的能力白白丢掉。
        /// </summary>
        private static Task<GithubProbeResult> RunCloneAsync(
            GithubProbe probe,
            CancellationToken token,
            IProgress<GithubCloneProgress>? progress)
        {
            return Task.Run(() =>
            {
                // 上次被强关时没删干净的工作目录，开测前顺手收掉
                TempDir.ClearStale("gh-connect", TimeSpan.FromHours(2));

                string? git = GitCommand.LocateGit();
                if (git is null)
                {
                    return GithubProbeResult.Skipped(probe, "未安装 Git，跳过");
                }

                // 代理是全局开关：这条测试测的是「当前设置能不能拉」，所以照着设置走，
                // 不在这里另外拆「走 / 不走」两条（那件事是「从哪下载最快」的职责）。
                bool proxyOn = ProxyRoute.Resolve().UseProxy;
                var attempts = new List<GithubProbeResult>();

                // 两条线路共用一份预算：第一条把时间吃光，就不该再开第二条熬着
                var budget = Stopwatch.StartNew();

                foreach ((string routeName, string url) in ResolveUrls(probe))
                {
                    TimeSpan left = CloneBudget - budget.Elapsed;

                    if (left < MinCloneWindow)
                    {
                        break;
                    }

                    TimeSpan window = left < CloneRouteTimeout ? left : CloneRouteTimeout;

                    GithubProbeResult attempt = RunOneClone(
                        probe, git, url, routeName, proxyOn, window, progress, token);

                    if (attempt.Ok)
                    {
                        return attempt;
                    }

                    attempts.Add(attempt);

                    if (token.IsCancellationRequested)
                    {
                        break;
                    }
                }

                return MergeAttempts(probe, attempts);
            }, token);
        }

        /// <summary>
        /// 跑一次完整克隆（<c>--depth 1 --single-branch --progress</c>，约 100 MB），
        /// 超过 <paramref name="timeout"/> 直接杀掉进程并判超时——「能下完但很慢」同样算不可用。
        ///
        /// 以前这里用 <c>--filter=blob:none --no-checkout</c> 只取元数据：一两秒就绿，
        /// 可安装时下的是完整仓库——那条通道到底能不能把文件传下来，测的根本不是同一件事
        /// （镜像 / 中转常见「元数据秒回、拉文件很慢」）。现在与「从哪下载最快」用同一套参数，
        /// 并把 git 的进度行解析出来给界面：用户能看见「下载 45% · 1.2 MB/s」，而不是干等一个「探测中」。
        ///
        /// **必须隔离全局 git 配置**：加速开关会往 <c>~/.gitconfig</c> 写 <c>insteadOf</c>，
        /// 不隔离的话「直连」会被 git 自己改写成加速地址，测出来的绿是错的那条线给的。
        /// </summary>
        private static GithubProbeResult RunOneClone(
            GithubProbe probe,
            string git,
            string url,
            string routeName,
            bool proxyOn,
            TimeSpan timeout,
            IProgress<GithubCloneProgress>? progress,
            CancellationToken token)
        {
            string label = proxyOn ? $"{routeName} · 走代理" : routeName;
            string workDir = TempDir.Create("gh-connect");
            var clock = Stopwatch.StartNew();

            // 失败时要报的是 git 的那句话，进度行不算错，单独收起来
            var tail = new Queue<string>();
            var gate = new object();
            int lastPercent = -1;

            try
            {
                var startInfo = GitCommand.CreateSpeedTestClone(
                    git,
                    url,
                    workDir,
                    isolateGlobalConfig: true,
                    useProxy: proxyOn);

                using var process = new Process { StartInfo = startInfo };

                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is not { Length: > 0 } line)
                    {
                        return;
                    }

                    if (!GitProgressParser.TryParse(line, out GitProgress parsed))
                    {
                        lock (gate)
                        {
                            tail.Enqueue(line);
                            while (tail.Count > 8)
                            {
                                tail.Dequeue();
                            }
                        }

                        return;
                    }

                    // 节流：git 每秒能刷十几次，只按整数百分比变化往外发
                    lock (gate)
                    {
                        if (parsed.Percent <= lastPercent)
                        {
                            return;
                        }

                        lastPercent = parsed.Percent;
                    }

                    progress?.Report(new GithubCloneProgress(
                        parsed.Percent, parsed.Phase, parsed.Speed, label));
                };

                // stdout 也读掉：只重定向不读，管道写满会把 git 卡死
                process.OutputDataReceived += (_, _) => { };

                if (!process.Start())
                {
                    return new GithubProbeResult(probe, false, null, "无法启动 git", null, null, label);
                }

                process.BeginErrorReadLine();
                process.BeginOutputReadLine();

                while (!process.HasExited)
                {
                    if (token.IsCancellationRequested)
                    {
                        GitCommand.Kill(process);
                        return new GithubProbeResult(probe, false, null, "已取消", null, null, label);
                    }

                    // 到点就掐：慢到这种程度的通道，装上酒馆也是这个速度，判定为不可用
                    if (clock.Elapsed > timeout)
                    {
                        GitCommand.Kill(process);
                        return new GithubProbeResult(
                            probe,
                            false,
                            null,
                            $"超时（{timeout.TotalSeconds:0} 秒没拉完，通道太慢）",
                            null,
                            null,
                            label);
                    }

                    Thread.Sleep(50);
                }

                // 等异步读取把最后几行吐干净，否则错误信息可能只拿到半句
                process.WaitForExit(2000);

                int latency = Elapsed(clock);

                if (process.ExitCode == 0)
                {
                    return new GithubProbeResult(probe, true, latency, null, null, null, label);
                }

                string detail = LastMeaningful(tail, gate);

                return new GithubProbeResult(
                    probe,
                    false,
                    latency,
                    detail.Length > 0 ? detail : $"git 退出代码 {process.ExitCode}",
                    null,
                    null,
                    label);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return new GithubProbeResult(
                    probe, false, null, GitCommand.FirstLine(ex.Message), null, null, label);
            }
            finally
            {
                TempDir.Delete(workDir);
            }
        }

        /// <summary>从 stderr 末尾挑一句能给人看的：跳过空行，去掉 <c>fatal:</c> 前缀。</summary>
        private static string LastMeaningful(Queue<string> tail, object gate)
        {
            string[] lines;

            lock (gate)
            {
                lines = tail.ToArray();
            }

            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i].Trim();

                if (line.Length == 0)
                {
                    continue;
                }

                // "fatal: unable to access ..." 前面那截对用户没意义
                return line.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase)
                    ? line[6..].Trim()
                    : line;
            }

            return string.Empty;
        }

        // ==================== 杂项 ====================

        private static int Elapsed(Stopwatch clock)
        {
            clock.Stop();
            return (int)Math.Clamp(clock.ElapsedMilliseconds, 1, 999999);
        }

        /// <summary>
        /// 把 .NET 的网络异常翻成一句人话。
        /// 原始信息形如「The SSL connection could not be established, see inner exception.」，
        /// 摆在界面上既看不懂也判断不出该怎么办。
        /// </summary>
        private static string DescribeHttpError(HttpRequestException ex)
        {
            string raw = ex.Message;
            string lower = raw.ToLowerInvariant();

            if (lower.Contains("ssl") || lower.Contains("tls") || lower.Contains("authentication failed"))
            {
                return "TLS 握手失败（域名可能被阻断）";
            }

            if (lower.Contains("no such host") || lower.Contains("name or service not known") || lower.Contains("nodename"))
            {
                return "域名解析失败";
            }

            if (lower.Contains("refused"))
            {
                return "连接被拒绝";
            }

            if (lower.Contains("reset"))
            {
                return "连接被重置";
            }

            if (lower.Contains("timed out") || lower.Contains("timeout"))
            {
                return "连接超时";
            }

            return GitCommand.FirstLine(raw);
        }
    }
}
