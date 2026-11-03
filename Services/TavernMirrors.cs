using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 一个镜像仓库里「有什么」：分支与标签。
    ///
    /// 只为一件事服务——判断「这个镜像有没有我要的那个版本」。
    /// 比对结果决定安装时走镜像还是退回 GitHub 直连，所以宁可报「查不到」，
    /// 也不能猜：猜错的结果是用户点了安装，卡在一个根本没有该标签的仓库上。
    /// </summary>
    public sealed class MirrorRefs
    {
        private readonly HashSet<string> _tags;
        private readonly HashSet<string> _branches;

        private MirrorRefs(
            DownloadChannel channel,
            HashSet<string> tags,
            HashSet<string> branches,
            DateTimeOffset fetchedAt,
            string? error)
        {
            Channel = channel;
            _tags = tags;
            _branches = branches;
            FetchedAt = fetchedAt;
            Error = error;
        }

        public DownloadChannel Channel { get; }

        /// <summary>非空表示这次没查到（没装 Git、连不上、超时），界面要说明原因并退回直连。</summary>
        public string? Error { get; }

        public DateTimeOffset FetchedAt { get; }

        public int TagCount => _tags.Count;

        /// <summary>镜像里有没有这个版本的标签。<c>v1.19.0</c> 与 <c>1.19.0</c> 视为同一个。</summary>
        public bool HasTag(string? version) =>
            Error is null && version is { Length: > 0 } && _tags.Contains(Normalize(version));

        /// <summary>镜像里有没有这个分支（开发版走 staging 分支，不是标签）。</summary>
        public bool HasBranch(string name) =>
            Error is null && _branches.Contains(name);

        internal static MirrorRefs Failed(DownloadChannel channel, string error) =>
            new(channel, new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase), DateTimeOffset.UtcNow, error);

        internal static MirrorRefs Create(
            DownloadChannel channel,
            HashSet<string> tags,
            HashSet<string> branches) =>
            new(channel, tags, branches, DateTimeOffset.UtcNow, null);

        /// <summary>GitHub 上是 <c>v1.19.0</c>，Gitee 上常常是 <c>1.19.0</c>：只比数字部分。</summary>
        internal static string Normalize(string value) => value.Trim().TrimStart('v', 'V');
    }

    /// <summary>
    /// 镜像仓库的引用清单，一次会话只取一次。
    ///
    /// 为什么不是「按版本逐个探测」：发布列表里有二三十个版本，逐个问就是二三十次网络往返。
    /// 把镜像的引用一次取回来（<c>git ls-remote --refs</c> 只回几百行），
    /// 之后所有版本判断都在本地集合里做，切版本不产生任何请求。
    ///
    /// 为什么用 Git 而不是 HTTP 探测：安装本身就是 <c>git clone</c>，
    /// 用同一套协议、同一份凭据判断「能不能拉」，结论才和真装的时候一致。
    /// </summary>
    public static class TavernMirrors
    {
        /// <summary>成功结果的缓存时长；镜像同步以天计，半小时都算过于频繁。</summary>
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

        /// <summary>失败只缓存一分钟：网络抖动后用户切个版本就能重试，不会被一次失败钉死半小时。</summary>
        private static readonly TimeSpan ErrorTtl = TimeSpan.FromSeconds(60);

        private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(25);

        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static readonly Dictionary<DownloadChannel, MirrorRefs> Cache = new();

        /// <summary>当前下载通道是不是镜像（GitHub 直连没有「镜像可用」这回事）。</summary>
        public static bool IsMirror(DownloadChannel channel) => channel != DownloadChannel.Github;

        /// <summary>
        /// 取某个镜像通道的引用清单。命中缓存直接返回，<paramref name="force"/> 时重新请求。
        /// 永不抛异常：查不到就返回带 <see cref="MirrorRefs.Error"/> 的结果。
        /// </summary>
        public static async Task<MirrorRefs> GetAsync(
            DownloadChannel channel,
            bool force,
            CancellationToken token)
        {
            if (!IsMirror(channel))
            {
                return MirrorRefs.Failed(channel, "当前是 GitHub 直连，没有镜像需要比对");
            }

            if (!force && TryGetCached(channel, out MirrorRefs cached))
            {
                return cached;
            }

            await Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                // 排队期间别人可能已经取过了，再验一次，避免同一时刻打两遍
                if (!force && TryGetCached(channel, out MirrorRefs racing))
                {
                    return racing;
                }

                MirrorRefs refs = await QueryAsync(channel, token).ConfigureAwait(false);
                Cache[channel] = refs;
                return refs;
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>
        /// 命中缓存就拿出来（不发起任何网络请求）；界面用它决定「要不要先显示上次的结论」。
        /// </summary>
        public static bool TryGetCached(DownloadChannel channel, out MirrorRefs refs)
        {
            if (Cache.TryGetValue(channel, out MirrorRefs? entry))
            {
                TimeSpan age = DateTimeOffset.UtcNow - entry.FetchedAt;
                if (age < (entry.Error is null ? CacheTtl : ErrorTtl))
                {
                    refs = entry;
                    return true;
                }
            }

            refs = null!;
            return false;
        }

        private static async Task<MirrorRefs> QueryAsync(DownloadChannel channel, CancellationToken token)
        {
            string? git = RuntimeProbe.LocateGit();
            if (git is null)
            {
                return MirrorRefs.Failed(channel, "尚未安装 Git，无法比对镜像");
            }

            string url = TavernSource.RepoUrl(channel);
            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var branches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

                // 不要 peeled 行（^{}）；-c 三项与安装时的 clone 保持一致的网络行为
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add("credential.interactive=false");
                startInfo.ArgumentList.Add("ls-remote");
                startInfo.ArgumentList.Add("--refs");
                startInfo.ArgumentList.Add(url);

                // 私库或代理要求凭据时，绝不能弹出终端等人输入：直接失败，退回直连
                startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
                startInfo.Environment["GIT_ASKPASS"] = string.Empty;
                startInfo.Environment["LC_ALL"] = "C";

                using Process? process = Process.Start(startInfo);
                if (process is null)
                {
                    return MirrorRefs.Failed(channel, "无法启动 Git");
                }

                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(QueryTimeout);

                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    TryKill(process);
                    return MirrorRefs.Failed(channel, token.IsCancellationRequested ? "已取消" : "比对镜像超时");
                }

                string output = await stdout.ConfigureAwait(false);
                string errors = await stderr.ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    return MirrorRefs.Failed(channel, FirstLine(errors) ?? $"Git 退出码 {process.ExitCode}");
                }

                foreach (string line in output.Split('\n'))
                {
                    // 格式：<sha>\t<ref>
                    int tab = line.IndexOf('\t');
                    if (tab < 0)
                    {
                        continue;
                    }

                    string reference = line[(tab + 1)..].Trim();
                    if (reference.StartsWith("refs/tags/", StringComparison.Ordinal))
                    {
                        tags.Add(MirrorRefs.Normalize(reference["refs/tags/".Length..]));
                    }
                    else if (reference.StartsWith("refs/heads/", StringComparison.Ordinal))
                    {
                        branches.Add(reference["refs/heads/".Length..]);
                    }
                }

                return MirrorRefs.Create(channel, tags, branches);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return MirrorRefs.Failed(channel, FirstLine(ex.Message) ?? "无法读取镜像仓库");
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // 已经自己退了
            }
        }

        private static string? FirstLine(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            foreach (string line in text.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    return trimmed;
                }
            }

            return null;
        }
    }
}
