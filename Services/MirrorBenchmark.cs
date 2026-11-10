using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>一条线路的测速结果：下载源 + 是否加速 + 是否经代理。</summary>
    public sealed record MirrorTrial(
        DownloadChannel Channel,
        bool Accelerated,
        bool UseProxy,
        string Name,
        string Url,
        bool Ok,
        int? ElapsedMs,
        string? Error);

    /// <summary>一条要测的线路：下载源 + 是否加速 + 是否经代理，三者组合才算一条线路。</summary>
    public sealed record MirrorChannel(
        DownloadChannel Channel,
        bool Accelerated,
        bool UseProxy,
        string Name);

    /// <summary>测速过程中的实时进度：一行一条线路，界面拿它显示「下载 45% · 6.3 MB/s」。</summary>
    public sealed record MirrorProgress(
        DownloadChannel Channel,
        bool Accelerated,
        bool UseProxy,
        int Percent,
        string Phase,
        string? Speed);

    /// <summary>一轮测速的结果：所有记录 + 最快的那个（全失败时为空）。</summary>
    public sealed record MirrorBenchmarkResult(
        IReadOnlyList<MirrorTrial> Trials,
        MirrorTrial? Best,
        string? Error);

    /// <summary>
    /// 下载通道测速：**真的把仓库拉下来**，谁先拉完用谁。
    ///
    /// 为什么必须真拉：决定安装速度的是「文件能不能下下来」，不是元数据握手快不快。
    /// 之前用的是 `--filter=blob:none --no-checkout`（只取元数据、不下载文件），
    /// 四条通道一两秒就全绿，测出来的名次与真实安装速度毫无关系。
    /// 现在用 `--depth 1 --single-branch --progress`，拉的是完整工作区（约 100 MB），
    /// 并解析 stderr 里的 git 进度给界面看——用户能看见它在传，而不是盯着一个「等着」。
    ///
    /// 为什么代理开着时 GitHub 会多两条：代理开关是全局的，只测一条的话，
    /// 界面上写着「直连」的那条其实经着代理，名次与关掉代理之后的真实速度对不上。
    /// 所以代理可用时，直连与加速各拆成「不走代理 / 走代理」两条，共六条线路；
    /// 镜像（Gitee / GitCode）永远直连——中转方不认镜像站，套代理只会更慢。
    ///
    /// 四个实现要点：
    /// · **必须隔离全局 git 配置**（`GIT_CONFIG_GLOBAL` 指向空文件）：加速开关会把
    ///   `~/.gitconfig` 写上 `insteadOf`，不隔离的话「GitHub 直连」会被改写成加速地址；
    /// · 单条 90 秒超时；第一条拉完之后再等 6 秒，其余直接取消——为一条死线路挂满超时没有意义；
    /// · 进度按阶段折算成 0~100（下载 3~85、整理 85~95、写入 95~99），界面上是一个单调的数；
    /// · 每条线路一个临时目录，测完必删（含 clone 的进程被强杀的情况）。
    /// </summary>
    public static class MirrorBenchmark
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

        /// <summary>第一条拉完之后再给其余几条多少宽限。</summary>
        private static readonly TimeSpan GraceAfterFirstWin = TimeSpan.FromSeconds(6);

        /// <summary>这次要测的线路，顺序即界面顺序。</summary>
        public static IReadOnlyList<MirrorChannel> BuildChannels()
        {
            bool proxy = ProxyRoute.Resolve().UseProxy;

            var channels = new List<MirrorChannel>
            {
                // 镜像永远直连：中转方不认镜像站；代理只管 GitHub
                new(DownloadChannel.Gitee, false, false, "Gitee 镜像"),
                new(DownloadChannel.GitCode, false, false, "GitCode 镜像"),

                // 代理不可用时名字保持原样，免得每次都要读一遍「不走代理」
                new(DownloadChannel.Github, false, false, proxy ? "GitHub 直连 · 不走代理" : "GitHub 直连"),
            };

            if (proxy)
            {
                channels.Add(new(DownloadChannel.Github, false, true, "GitHub 直连 · 走代理"));
            }

            channels.Add(new(DownloadChannel.Github, true, false, proxy ? "GitHub 加速 · 不走代理" : "GitHub 加速"));

            if (proxy)
            {
                channels.Add(new(DownloadChannel.Github, true, true, "GitHub 加速 · 走代理"));
            }

            return channels;
        }

        /// <summary>某条线路实际要克隆的地址。加速那条**直接拼前缀**，不走 <see cref="GithubAccel.Apply"/>。</summary>
        public static string UrlOf(DownloadChannel channel, bool accelerated)
        {
            string url = TavernSource.RepoUrl(channel);
            return accelerated ? GithubAccel.Prefix + url : url;
        }

        /// <summary>
        /// 毫秒折成人话。测速动辄十几秒，界面上写「15200 ms」没人算得出来，
        /// 写「15.2 秒」一眼就懂——这也是这个数字唯一的用途。
        /// </summary>
        public static string DescribeElapsed(int? milliseconds) =>
            milliseconds is { } value ? $"{value / 1000.0:0.0} 秒" : "—";

        /// <summary>
        /// 并发测速。<paramref name="progress"/> 每回一条就报一次（界面逐行点亮），
        /// <paramref name="live"/> 在拉取过程中持续报百分比与速度。
        /// </summary>
        public static async Task<MirrorBenchmarkResult> RunAsync(
            IProgress<MirrorTrial>? progress,
            IProgress<MirrorProgress>? live,
            CancellationToken token)
        {
            // 上次被强关时没删干净的克隆，先收掉再开测
            TempDir.ClearStale("mirror-test", TimeSpan.FromHours(2));

            IReadOnlyList<MirrorChannel> channels = BuildChannels();

            string? git = GitCommand.LocateGit();
            if (git is null)
            {
                var skipped = channels
                    .Select(item => new MirrorTrial(
                        item.Channel,
                        item.Accelerated,
                        item.UseProxy,
                        item.Name,
                        UrlOf(item.Channel, item.Accelerated),
                        false,
                        null,
                        "未安装 Git，跳过"))
                    .ToList();

                return new MirrorBenchmarkResult(skipped, null, "未安装 Git，无法测速");
            }

            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(token);

            var pending = channels
                .Select(item => (
                    Item: item,
                    Task: Task.Run(() => RunOne(git, item, live, runCts.Token), runCts.Token)))
                .ToList();

            var trials = new List<MirrorTrial>(channels.Count);
            DateTime? firstWinAt = null;
            bool stopped = false;

            while (pending.Count > 0)
            {
                int index = await WaitForFirstAsync(pending.Select(item => item.Task)).ConfigureAwait(false);

                MirrorChannel channel = pending[index].Item;
                Task<MirrorTrial> task = pending[index].Task;
                pending.RemoveAt(index);

                MirrorTrial trial;

                try
                {
                    trial = await task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    trial = new MirrorTrial(
                        channel.Channel,
                        channel.Accelerated,
                        channel.UseProxy,
                        channel.Name,
                        UrlOf(channel.Channel, channel.Accelerated),
                        false,
                        null,
                        "已取消");
                }

                trials.Add(trial);
                progress?.Report(trial);

                if (trial.Ok)
                {
                    firstWinAt ??= DateTime.UtcNow;
                }

                // 已经有人拉完了：其余几条只再等几秒，剩下的直接掐掉
                if (!stopped && firstWinAt is { } winAt && DateTime.UtcNow - winAt > GraceAfterFirstWin)
                {
                    stopped = true;
                    runCts.Cancel();
                }
            }

            var order = channels
                .Select((item, index) => (item, index))
                .ToDictionary(
                    pair => (pair.item.Channel, pair.item.Accelerated, pair.item.UseProxy),
                    pair => pair.index);

            trials.Sort((left, right) =>
                order[(left.Channel, left.Accelerated, left.UseProxy)]
                    .CompareTo(order[(right.Channel, right.Accelerated, right.UseProxy)]));

            MirrorTrial? best = trials
                .Where(trial => trial.Ok && trial.ElapsedMs is not null)
                .OrderBy(trial => trial.ElapsedMs)
                .FirstOrDefault();

            return new MirrorBenchmarkResult(
                trials,
                best,
                best is null ? "所有线路都没测通" : null);
        }

        /// <summary>
        /// 把最快那条写进设置。
        ///
        /// **只有 GitHub 线路才去动加速与代理**：
        /// · 胜出的是 GitHub 加速线 → 打开加速并同步全局 <c>insteadOf</c>；
        /// · 胜出的是 GitHub 直连线 → 关掉加速（否则 git 仍会把直连地址改写成加速地址，测出来的最快等于白测）；
        /// · 胜出的是 Gitee / GitCode 镜像 → **只换下载通道，加速与代理一个都不碰**。
        ///   镜像本来就不走 GitHub，加速开关管的是 Raw / API / 安装包那些直链；
        ///   以前「镜像更快」顺手把加速关了，用户刚为了 Raw 打开的加速转头就被关掉，
        ///   到了下一步（装工具）又连不上 GitHub。
        /// </summary>
        public static async Task ApplyAsync(MirrorTrial best, CancellationToken token)
        {
            LauncherSettings.SetChannel(best.Channel);

            if (best.Channel != DownloadChannel.Github)
            {
                return;
            }

            if (LauncherSettings.GithubAcceleration != best.Accelerated)
            {
                LauncherSettings.SetGithubAcceleration(best.Accelerated);
                await GitGlobalConfig.SyncAsync(best.Accelerated).ConfigureAwait(false);
            }

            if (!best.UseProxy && ProxyRoute.Resolve().UseProxy)
            {
                LauncherSettings.SetProxy(ProxyModeSetting.None);
            }
        }

        /// <summary>等一组任务里最先完成的那个，返回它的下标（不抛异常）。</summary>
        private static async Task<int> WaitForFirstAsync(IEnumerable<Task<MirrorTrial>> tasks)
        {
            Task<MirrorTrial>[] all = tasks.ToArray();
            Task<MirrorTrial> finished = await Task.WhenAny(all).ConfigureAwait(false);
            return Array.IndexOf(all, finished);
        }

        private static MirrorTrial RunOne(
            string git,
            MirrorChannel channel,
            IProgress<MirrorProgress>? live,
            CancellationToken token)
        {
            string url = UrlOf(channel.Channel, channel.Accelerated);
            string workDir = TempDir.Create("mirror-test");
            var clock = Stopwatch.StartNew();

            // 失败时要报的是「git 的那句话」，进度行不算错，单独收起来
            var tail = new Queue<string>();
            var gate = new object();
            int lastPercent = -1;

            try
            {
                // isolateGlobalConfig: 加速开关写的 insteadOf 必须被挡在外面
                var startInfo = GitCommand.CreateSpeedTestClone(
                    git,
                    url,
                    workDir,
                    isolateGlobalConfig: true,
                    useProxy: channel.UseProxy);

                using var process = new Process { StartInfo = startInfo };

                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is not { Length: > 0 } line)
                    {
                        return;
                    }

                    if (!TryParseProgress(line, channel, out MirrorProgress update))
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
                        if (update.Percent <= lastPercent)
                        {
                            return;
                        }

                        lastPercent = update.Percent;
                    }

                    live?.Report(update);
                };

                // stdout 也读掉：只重定向不读，管道写满会把 git 卡死
                process.OutputDataReceived += (_, _) => { };

                if (!process.Start())
                {
                    return Trial(channel, url, false, null, "无法启动 git");
                }

                process.BeginErrorReadLine();
                process.BeginOutputReadLine();

                while (!process.HasExited)
                {
                    if (token.IsCancellationRequested)
                    {
                        GitCommand.Kill(process);
                        return Trial(channel, url, false, null, "已取消");
                    }

                    if (clock.Elapsed > Timeout)
                    {
                        GitCommand.Kill(process);
                        return Trial(channel, url, false, null, $"超时（{Timeout.TotalSeconds:0} 秒没拉完）");
                    }

                    Thread.Sleep(50);
                }

                // 等异步读取把最后几行吐干净，否则错误信息可能只拿到半句
                process.WaitForExit(2000);

                int elapsed = (int)Math.Clamp(clock.ElapsedMilliseconds, 1, 999999);

                if (process.ExitCode == 0)
                {
                    return Trial(channel, url, true, elapsed, null);
                }

                string detail = LastMeaningful(tail, gate);

                return Trial(
                    channel,
                    url,
                    false,
                    elapsed,
                    detail.Length > 0 ? detail : $"git 退出代码 {process.ExitCode}");
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return Trial(channel, url, false, null, GitCommand.FirstLine(ex.Message));
            }
            finally
            {
                TempDir.Delete(workDir);
            }
        }

        private static MirrorTrial Trial(
            MirrorChannel channel,
            string url,
            bool ok,
            int? elapsed,
            string? error) =>
            new(channel.Channel, channel.Accelerated, channel.UseProxy, channel.Name, url, ok, elapsed, error);

        /// <summary>把 git 的一行进度翻成「阶段 + 总体百分比」。不是进度行就返回 false。</summary>
        private static bool TryParseProgress(
            string line,
            MirrorChannel channel,
            out MirrorProgress progress)
        {
            progress = null!;

            if (!GitProgressParser.TryParse(line, out GitProgress parsed))
            {
                return false;
            }

            progress = new MirrorProgress(
                channel.Channel,
                channel.Accelerated,
                channel.UseProxy,
                parsed.Percent,
                parsed.Phase,
                parsed.Speed);

            return true;
        }

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
    }
}
