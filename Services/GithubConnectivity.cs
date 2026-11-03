using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 一个要探测的节点。地址是真实的——探测发的是真请求，延迟也是真的量出来的。
    /// </summary>
    public sealed record GithubProbe(
        string Key,
        string Name,
        string Hint,
        string Url,
        bool IsRelay = false,
        bool IsGit = false);

    /// <summary>一次探测的结果。<see cref="Note"/> 是「能用但不理想」的说明，<see cref="Error"/> 是失败原因。</summary>
    public sealed record GithubProbeResult(
        GithubProbe Probe,
        bool Ok,
        int? LatencyMs,
        string? Error,
        string? Note)
    {
        /// <summary>没测（例如没装 Git）。</summary>
        public static GithubProbeResult Skipped(GithubProbe probe, string note) =>
            new(probe, false, null, null, note);
    }

    /// <summary>
    /// GitHub 连通性测试：真的去访问 GitHub 的网页、API、压缩包与 Raw，再单独验证 git 通道。
    ///
    /// 以前这里是四条写死的延迟数字（412 / 268 / 0 / 168），点「重新测试」只是让它们
    /// 按固定节奏再出现一遍——用户换了代理、断了网，界面照样一片绿。
    ///
    /// 现在的规则：
    /// · 每条都用 <see cref="ProxyHttpClient"/>，代理方式（无 / 系统 / 自定义）与其它网络功能一致；
    /// · 只读到响应头就断开（<c>ResponseHeadersRead</c> + Range），不把压缩包整个拖下来；
    /// · 延迟 = 从发出请求到拿到响应头的时间；
    /// · 加速开着时多探一条中转（gh-proxy.org），它 4xx 就说明「加速地址本身不可用」，
    ///   这与「GitHub 挂了」是两件事，要分开告诉用户；
    /// · git 通道单独用 <c>git ls-remote</c> 验：网页能开不代表 clone 能跑。
    /// </summary>
    public static class GithubConnectivity
    {
        private const string RepositoryUrl = "https://github.com/SillyTavern/SillyTavern";

        private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(20);

        /// <summary>按当前设置列出要测的节点：加速开着才加中转，装了 Git 才加 git。</summary>
        public static IReadOnlyList<GithubProbe> BuildProbes()
        {
            var probes = new List<GithubProbe>
            {
                new("web", "github.com", "仓库页（网页与跳转）", RepositoryUrl),
                new("api", "api.github.com", "Release 元数据", "https://api.github.com/repos/SillyTavern/SillyTavern/releases?per_page=1"),
                new("codeload", "codeload.github.com", "源码压缩包", "https://codeload.github.com/SillyTavern/SillyTavern/tar.gz/refs/heads/release"),
                new("raw", "raw.githubusercontent.com", "Raw 文件", "https://raw.githubusercontent.com/SillyTavern/SillyTavern/release/start.sh"),
            };

            if (GithubAccel.Enabled)
            {
                probes.Add(new GithubProbe(
                    "relay",
                    "gh-proxy.org",
                    "加速中转（当前生效）",
                    GithubAccel.Prefix + "https://raw.githubusercontent.com/SillyTavern/SillyTavern/release/start.sh",
                    IsRelay: true));
            }

            if (LocateGit() is not null)
            {
                probes.Add(new GithubProbe(
                    "git",
                    "git ls-remote",
                    "克隆通道（命令行 git）",
                    RepositoryUrl,
                    IsGit: true));
            }

            return probes;
        }

        /// <summary>当前实际走的那条出口，直接写进弹窗，省得用户猜「到底用没用代理」。</summary>
        public static string DescribeExit() => LauncherSettings.Proxy switch
        {
            ProxyModeSetting.None => "直连（不使用代理）",
            ProxyModeSetting.Custom => $"自定义代理 {LauncherSettings.CustomProxy}",
            _ => "系统代理（跟随 Windows Internet 选项）",
        };

        /// <summary>探测一个节点。永不抛异常，失败折成结果返回。</summary>
        public static Task<GithubProbeResult> RunAsync(GithubProbe probe, CancellationToken token)
            => probe.IsGit ? RunGitAsync(probe, token) : RunHttpAsync(probe, token);

        // ==================== HTTP ====================

        private static async Task<GithubProbeResult> RunHttpAsync(GithubProbe probe, CancellationToken token)
        {
            var clock = Stopwatch.StartNew();

            try
            {
                using HttpClient client = ProxyHttpClient.Create(HttpTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, probe.Url);

                // 只要响应头与开头几百字节：压缩包是几十 MB，测连通性不该把它拖下来
                request.Headers.Range = new RangeHeaderValue(0, 2048);
                request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

                using HttpResponseMessage response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);

                int latency = Elapsed(clock);
                int code = (int)response.StatusCode;

                if (response.IsSuccessStatusCode)
                {
                    return new GithubProbeResult(probe, true, latency, null, null);
                }

                // 中转对 GitHub 地址返回 4xx，通常是「这条加速地址不可用」，不是 GitHub 不通
                if (probe.IsRelay && code is 403 or 404 or 502 or 503)
                {
                    return new GithubProbeResult(probe, false, latency, null, $"中转返回 HTTP {code}，这条加速地址不可用");
                }

                return new GithubProbeResult(probe, false, latency, $"HTTP {code}", null);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException)
            {
                return new GithubProbeResult(probe, false, null, $"超时（{HttpTimeout.TotalSeconds:0} 秒）", null);
            }
            catch (HttpRequestException ex)
            {
                return new GithubProbeResult(probe, false, null, DescribeHttpError(ex), null);
            }
        }

        // ==================== git ====================

        private static Task<GithubProbeResult> RunGitAsync(GithubProbe probe, CancellationToken token)
        {
            return Task.Run(() =>
            {
                string? git = LocateGit();
                if (git is null)
                {
                    return GithubProbeResult.Skipped(probe, "未安装 Git，跳过");
                }

                var clock = Stopwatch.StartNew();

                try
                {
                    var startInfo = new ProcessStartInfo(git)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8,
                    };

                    startInfo.ArgumentList.Add("ls-remote");
                    startInfo.ArgumentList.Add("--heads");
                    startInfo.ArgumentList.Add(probe.Url);

                    // 内置 MinGit 的辅助程序在相邻目录
                    if (System.IO.Path.GetDirectoryName(git) is { Length: > 0 } gitDir)
                    {
                        string root = System.IO.Path.GetDirectoryName(gitDir) ?? gitDir;
                        startInfo.Environment["PATH"] = string.Join(
                            System.IO.Path.PathSeparator,
                            new[] { gitDir, System.IO.Path.Combine(root, "bin"), System.IO.Path.Combine(root, "mingw64", "bin") });
                    }

                    using var process = new Process { StartInfo = startInfo };
                    if (!process.Start())
                    {
                        return new GithubProbeResult(probe, false, null, "无法启动 git", null);
                    }

                    Task<string> output = process.StandardOutput.ReadToEndAsync();
                    Task<string> error = process.StandardError.ReadToEndAsync();

                    while (!process.HasExited)
                    {
                        if (token.IsCancellationRequested)
                        {
                            TryKill(process);
                            return GithubProbeResult.Skipped(probe, "已取消");
                        }

                        if (clock.Elapsed > GitTimeout)
                        {
                            TryKill(process);
                            return new GithubProbeResult(probe, false, null, $"超时（{GitTimeout.TotalSeconds:0} 秒）", null);
                        }

                        Thread.Sleep(50);
                    }

                    int latency = Elapsed(clock);

                    if (process.ExitCode == 0)
                    {
                        bool hasRefs = output.GetAwaiter().GetResult()
                            .Split('\n')
                            .Any(line => line.Contains("refs/heads/", StringComparison.Ordinal));

                        return hasRefs
                            ? new GithubProbeResult(probe, true, latency, null, null)
                            : new GithubProbeResult(probe, false, latency, "仓库能连上，但没读到任何分支", null);
                    }

                    string detail = FirstLine(error.GetAwaiter().GetResult());
                    return new GithubProbeResult(probe, false, latency, detail.Length > 0 ? detail : $"git 退出代码 {process.ExitCode}", null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    return new GithubProbeResult(probe, false, null, FirstLine(ex.Message), null);
                }
            }, token);
        }

        private static string? LocateGit()
        {
            string? git = RuntimeProbe.LocateGit(LauncherSettings.Runtime);

            // 选「内置」但内置里没有时，回退系统 git：连通性测试关心的是「这台机器能不能拉」，
            // 与启动器用哪套运行时无关
            return git ?? RuntimeProbe.LocateGit();
        }

        private static void TryKill(Process process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 已经自己退了
            }
        }

        // ==================== 杂项 ====================

        private static int Elapsed(Stopwatch clock)
        {
            clock.Stop();
            return (int)Math.Clamp(clock.ElapsedMilliseconds, 1, 99999);
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

            return FirstLine(raw);
        }

        /// <summary>异常信息常常带一长串内层原因，界面上只留第一行。</summary>
        private static string FirstLine(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string value = text.Trim();
            int breakAt = value.IndexOfAny(new[] { '\r', '\n' });
            return breakAt > 0 ? value[..breakAt].Trim() : value;
        }
    }
}
