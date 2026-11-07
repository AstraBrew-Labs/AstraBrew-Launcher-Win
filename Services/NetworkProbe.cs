using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>两项基础连通性的类型。</summary>
    public enum NetProbeKind
    {
        /// <summary>能不能连上外网（PING 8.8.8.8）。</summary>
        Ping,

        /// <summary>域名能不能解析并完成一次 HTTPS（访问 www.baidu.com）。</summary>
        Dns,
    }

    /// <summary>一项基础连通性的结果。</summary>
    public sealed record NetProbeResult(
        NetProbeKind Kind,
        bool Ok,
        int? LatencyMs,
        string? Error,
        string? Detail);

    /// <summary>
    /// 引导第二步的两项「网络是否基本可用」检查。
    ///
    /// 为什么先 PING 再 HTTPS：
    /// · PING 通但网页打不开 → 多半是 DNS 或代理的问题，这时测 GitHub 只会得到一堆看不懂的报错；
    /// · 两项分开报，用户一眼能看出「是没网」还是「网通了解析坏了」。
    ///
    /// 这里刻意不并发 PING 与 HTTPS：先确认「有网」再问「能解析」，报错归因才准确，两项本身又快。
    /// </summary>
    public static class NetworkProbe
    {
        public const string PingTarget = "8.8.8.8";

        public const string DnsUrl = "https://www.baidu.com";

        private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(6);

        private static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(8);

        public static Task<NetProbeResult> PingAsync(CancellationToken token) => Task.Run(() =>
        {
            var clock = Stopwatch.StartNew();

            try
            {
                // -n 1 一次、-w 2000 等 2 秒：再久就是不通，没必要让用户干等
                var startInfo = new ProcessStartInfo("ping")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };

                startInfo.ArgumentList.Add("-n");
                startInfo.ArgumentList.Add("1");
                startInfo.ArgumentList.Add("-w");
                startInfo.ArgumentList.Add("2000");
                startInfo.ArgumentList.Add(PingTarget);

                using var process = new Process { StartInfo = startInfo };

                if (!process.Start())
                {
                    return new NetProbeResult(NetProbeKind.Ping, false, null, "无法启动 ping", null);
                }

                Task<string> output = process.StandardOutput.ReadToEndAsync();

                while (!process.HasExited)
                {
                    if (token.IsCancellationRequested)
                    {
                        GitCommand.Kill(process);
                        throw new OperationCanceledException(token);
                    }

                    if (clock.Elapsed > PingTimeout)
                    {
                        GitCommand.Kill(process);
                        return new NetProbeResult(
                            NetProbeKind.Ping,
                            false,
                            null,
                            $"超时（{PingTimeout.TotalSeconds:0} 秒）",
                            "系统自带的 ping 没有在预期时间内返回");
                    }

                    Thread.Sleep(50);
                }

                int latency = (int)Math.Clamp(clock.ElapsedMilliseconds, 1, 99999);
                string text = output.GetAwaiter().GetResult();

                // 退出码 0 但回了「无法访问目标主机」的情况在中文系统上很常见，靠 TTL 再确认一次
                bool ok = process.ExitCode == 0
                    && text.Contains("TTL=", StringComparison.OrdinalIgnoreCase);

                return ok
                    ? new NetProbeResult(NetProbeKind.Ping, true, latency, null, $"PING {PingTarget} 正常")
                    : new NetProbeResult(
                        NetProbeKind.Ping,
                        false,
                        latency,
                        "PING 不通",
                        GitCommand.FirstLine(text));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return new NetProbeResult(NetProbeKind.Ping, false, null, "无法启动 ping", ex.Message);
            }
        }, token);

        public static async Task<NetProbeResult> DnsAsync(CancellationToken token)
        {
            var clock = Stopwatch.StartNew();

            try
            {
                using HttpClient client = ProxyHttpClient.Create(DnsTimeout);

                // 只要响应头：这个请求只是想确认「域名能解析、TLS 能握上」
                using HttpResponseMessage response = await client
                    .GetAsync(DnsUrl, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);

                int latency = (int)Math.Clamp(clock.ElapsedMilliseconds, 1, 99999);

                return response.IsSuccessStatusCode
                    ? new NetProbeResult(NetProbeKind.Dns, true, latency, null, $"打开 {DnsUrl} 正常")
                    : new NetProbeResult(
                        NetProbeKind.Dns,
                        false,
                        latency,
                        $"HTTP {(int)response.StatusCode}",
                        $"{DnsUrl} 返回了异常状态码");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException)
            {
                return new NetProbeResult(NetProbeKind.Dns, false, null, $"超时（{DnsTimeout.TotalSeconds:0} 秒）", null);
            }
            catch (HttpRequestException ex)
            {
                return new NetProbeResult(NetProbeKind.Dns, false, null, Describe(ex), null);
            }
        }

        private static string Describe(HttpRequestException ex)
        {
            string lower = ex.Message.ToLowerInvariant();

            if (lower.Contains("no such host") || lower.Contains("name or service not known") || lower.Contains("nodename"))
            {
                return "DNS 解析失败";
            }

            if (lower.Contains("ssl") || lower.Contains("tls"))
            {
                return "TLS 握手失败";
            }

            if (lower.Contains("refused"))
            {
                return "连接被拒绝";
            }

            if (lower.Contains("reset"))
            {
                return "连接被重置";
            }

            return GitCommand.FirstLine(ex.Message);
        }
    }
}
