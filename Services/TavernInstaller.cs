using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>安装阶段；界面按它决定进度条与文案，不去解析 Git 的输出。</summary>
    public enum TavernInstallStage
    {
        Preparing,
        Cloning,
        Committing,
        InstallingDeps,
        Verifying,
        Done,
    }

    /// <summary>
    /// 一次进度回调。<see cref="Detail"/> 是要落到日志面板里的那一行（多为 Git / npm 的原始输出行），
    /// 可以为空——没新东西可写时不要往面板里塞重复行。
    /// </summary>
    public sealed record TavernInstallProgress(
        TavernInstallStage Stage,
        string Message,
        string? Detail,
        double Percent);

    /// <summary>安装结果；失败时的 <see cref="Message"/>/<see cref="Hint"/> 是可以直接读给用户的一句话。</summary>
    public sealed record TavernInstallResult(
        bool Success,
        string Message,
        string? Hint,
        string? LogPath,
        string? Version,
        string? SourceName)
    {
        /// <summary>
        /// 依赖（<c>node_modules</c>）是否就绪。代码装上了、依赖没装上时
        /// <see cref="Success"/> 为 false，但磁盘上确实留着一份可修的代码——
        /// 界面据此把按钮换成「安装依赖」，用户不必重下一百兆源码。
        /// </summary>
        public bool DepsReady { get; init; }
    }

    /// <summary>
    /// 内置实例的真实安装：<c>git clone</c> 一个版本到暂存目录，校验后换成正式目录，
    /// 再用 <c>npm install</c> 就地装依赖。
    ///
    /// 为什么是浅克隆：酒馆仓库的完整历史有两百多兆，而启动器只需要某一个版本的代码。
    /// <c>--depth 1 --single-branch --branch &lt;tag&gt;</c> 拿到的工作区就是那个版本，
    /// 装完可以直接跑，也省掉一次「checkout 到一半」的中间态。
    ///
    /// 几条硬约定：
    /// · <b>先落暂存目录</b>：<c>.sillytavern-staging</c> 与正式目录同盘，最后一步是 rename。
    ///   中途失败不会留下「半个酒馆」——那种目录探测能过、一启动就报错。
    /// · <b>镜像失败退直连</b>：<paramref name="targets"/> 的顺序由调用方按镜像可用性排好，
    ///   这里只负责依次尝试，全部失败才给用户一句话。
    /// · <b>不弹终端</b>：凭据交互一律关掉。私库要密码时直接失败，比卡住等输入好。
    /// · <b>依赖装在代码就位之后</b>：<c>node_modules</c> 有几万个文件，放暂存目录再整体改名
    ///   只会让换名变慢；而且依赖失败时留下的是「有代码、缺依赖」——正好能修，不用重下源码。
    /// · <b>npm 源只临时传参</b>：<c>--registry</c> 只作用于这一次进程，不动用户的 <c>.npmrc</c>。
    /// · <b>界面只要一句话</b>：完整过程（选源、每条 Git / npm 输出、耗时）进 <see cref="InstallLog"/>。
    /// </summary>
    public static class TavernInstaller
    {
        /// <summary>克隆阶段占整条进度条的份额；剩余留给落位与依赖。</summary>
        private const double CloneShare = 70;

        /// <summary>依赖阶段在进度条上从这里开始；这一段没有可信百分比，界面用不确定态表现。</summary>
        private const double DepsShare = 76;

        /// <summary>
        /// 浅克隆约 100 MB、<c>node_modules</c> 约 320 MB；换版时新旧两份会短暂并存，
        /// 所以留到 1 GB 才动手。
        /// </summary>
        private const long RequiredFreeBytes = 1024L * 1024 * 1024;

        /// <summary>正式安装目录：<c>%AppData%\AstraBrew Launcher\sillytavern</c>。</summary>
        public static string InstallRoot => TavernInstances.BuiltinInstallRoot;

        /// <summary>暂存目录：与正式目录同盘，最后一步是改名而不是复制。</summary>
        public static string StagingRoot { get; } =
            Path.Combine(AppPaths.RoamingRoot, ".sillytavern-staging");

        /// <summary>旧版本的临时容身处：换版时先把它改名挪到这里，删不掉也不会影响新版本。</summary>
        private static string RetiredRoot { get; } =
            Path.Combine(AppPaths.RoamingRoot, ".sillytavern-old");

        /// <summary>
        /// 安装某个版本。<paramref name="revision"/> 可以是标签（<c>1.19.0</c>）也可以是分支（<c>staging</c>）。
        /// 永不抛异常，失败一律折成 <see cref="TavernInstallResult"/>。
        /// </summary>
        public static Task<TavernInstallResult> InstallAsync(
            string revision,
            IReadOnlyList<SourceTarget> targets,
            IProgress<TavernInstallProgress>? progress,
            CancellationToken token) =>
            Task.Run(() => GuardedAsync(() => RunAsync(revision, targets, progress, token)));

        /// <summary>
        /// 只补依赖：代码已经躺在安装目录里，缺的是 <c>node_modules</c>。
        ///
        /// 这条路是给「克隆成功、npm 失败」准备的——再拉一遍源码既慢又没意义，
        /// 而且用户会以为整个安装要从头再来。
        /// </summary>
        public static Task<TavernInstallResult> InstallDependenciesAsync(
            IProgress<TavernInstallProgress>? progress,
            CancellationToken token) =>
            Task.Run(() => GuardedAsync(() => RunDepsOnlyAsync(InstallRoot, progress, token)));

        /// <summary>
        /// 给任意一份酒馆目录补依赖。本地实例的「安装依赖」走这条：
        /// 依赖装在它自己的目录里，不碰内置实例那一份。
        /// </summary>
        public static Task<TavernInstallResult> InstallDependenciesAsync(
            string root,
            IProgress<TavernInstallProgress>? progress,
            CancellationToken token) =>
            Task.Run(() => GuardedAsync(() => RunDepsOnlyAsync(root, progress, token)));

        /// <summary>
        /// 是否有实例安装正在进行。两个入口（版本页 / 引导页）都能触发安装，
        /// 共用同一份安装目录，撞在一起会把暂存目录搅坏——所以这里做个全局闸门。
        /// </summary>
        public static bool IsRunning => Volatile.Read(ref _running) != 0;

        private static int _running;

        /// <summary>同一时刻只允许一个实例安装；重复进入直接返回一句人话。</summary>
        private static async Task<TavernInstallResult> GuardedAsync(Func<Task<TavernInstallResult>> work)
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            {
                return new TavernInstallResult(
                    false,
                    "另一个安装任务正在进行",
                    "等它结束再试，或者先到控制台看看进度。",
                    null,
                    null,
                    null);
            }

            try
            {
                return await work().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }

        private static async Task<TavernInstallResult> RunDepsOnlyAsync(
            string root,
            IProgress<TavernInstallProgress>? progress,
            CancellationToken token)
        {
            using InstallLog log = InstallLog.Create("sillytavern-deps");
            var reporter = new Reporter(progress);
            var clock = Stopwatch.StartNew();

            log.Info($"依赖安装开始 · 目录 {root}");
            log.Info($"npm 源 {LauncherSettings.NpmRegistryName} {LauncherSettings.NpmRegistryUrl}");

            if (!File.Exists(Path.Combine(root, "package.json")))
            {
                log.Fail("目录里没有 package.json");
                return Failure("这个目录里没有 package.json。", "确认一下选的是酒馆代码目录，再补依赖。", log);
            }

            try
            {
                DepsOutcome deps = await InstallDepsAsync(root, reporter, log, token).ConfigureAwait(false);

                if (!deps.Ok)
                {
                    log.Fail($"依赖安装失败：{deps.Reason}");
                    return new TavernInstallResult(
                        false,
                        "依赖没有装好，酒馆还起不来。",
                        deps.Reason,
                        log.FilePath,
                        null,
                        null)
                    {
                        DepsReady = false,
                    };
                }

                reporter.Send(TavernInstallStage.Done, "依赖已装好", null, 100, force: true);
                log.Info($"依赖安装完成 · 耗时 {clock.Elapsed.TotalSeconds:0.0} 秒");

                return new TavernInstallResult(
                    true,
                    "Node 依赖已装好",
                    $"耗时 {clock.Elapsed.TotalSeconds:0.0} 秒，现在可以直接启动。",
                    log.FilePath,
                    null,
                    null)
                {
                    DepsReady = true,
                };
            }
            catch (OperationCanceledException)
            {
                log.Warn("用户取消依赖安装");
                return Failure("已取消安装依赖。", "代码还在，随时可以再点一次「安装依赖」。", log);
            }
            catch (Exception ex)
            {
                log.Fail("依赖安装出错", ex);
                return Failure("安装依赖时出错，请重试。", ex.Message, log) with { DepsReady = false };
            }
        }

        private static async Task<TavernInstallResult> RunAsync(
            string revision,
            IReadOnlyList<SourceTarget> targets,
            IProgress<TavernInstallProgress>? progress,
            CancellationToken token)
        {
            using InstallLog log = InstallLog.Create("sillytavern");
            var reporter = new Reporter(progress);
            var clock = Stopwatch.StartNew();

            log.Info($"内置实例安装开始 · 目标版本 {revision}");
            log.Info($"安装目录 {InstallRoot}");
            log.Info($"暂存目录 {StagingRoot}");

            string? git = RuntimeProbe.LocateGit();
            if (git is null)
            {
                log.Fail("没有找到 Git 可执行文件");
                return Failure("没有找到 Git，无法拉取源码。", "请先在「设置 → 运行环境」里安装 Git。", log);
            }

            log.Info($"Git {git}");

            try
            {
                reporter.Send(TavernInstallStage.Preparing, "检查磁盘空间…", null, 0, force: true);

                if (FreeSpace(AppPaths.RoamingRoot) is { } free && free < RequiredFreeBytes)
                {
                    log.Fail($"磁盘剩余 {ComponentInstaller.FormatSize(free)}，不足 {ComponentInstaller.FormatSize(RequiredFreeBytes)}");
                    return Failure(
                        $"磁盘空间不足，至少需要 {ComponentInstaller.FormatSize(RequiredFreeBytes)} 可用空间。",
                        "清理系统盘后再点一次安装。",
                        log);
                }

                Directory.CreateDirectory(AppPaths.RoamingRoot);

                var failures = new List<string>();
                var used = default(SourceTarget);
                bool installed = false;

                foreach (SourceTarget target in targets)
                {
                    token.ThrowIfCancellationRequested();

                    reporter.Send(
                        TavernInstallStage.Cloning,
                        $"正在从{target.Name}拉取 {revision}…",
                        $"$ git clone --depth 1 --branch {revision} {target.Url}",
                        1,
                        force: true);

                    log.Info($"尝试 {target.Name} {target.Url}");
                    CleanStaging(log);

                    CloneOutcome outcome = await CloneAsync(git, target, revision, reporter, log, token)
                        .ConfigureAwait(false);

                    if (outcome.Ok)
                    {
                        used = target;
                        installed = true;
                        break;
                    }

                    failures.Add($"{target.Name}：{outcome.Reason}");
                    log.Warn($"{target.Name} 失败：{outcome.Reason}");
                }

                if (!installed)
                {
                    CleanStaging(log);
                    log.Fail($"所有源都失败：{string.Join(" / ", failures)}");
                    return Failure(
                        "拉取源码失败，没有拿到完整的代码。",
                        "已依次尝试 " + string.Join("、", Describe(targets)) + "；请检查网络或代理后重试。",
                        log);
                }

                // 校验：酒馆的 package.json 是判断「这份代码是不是酒馆」最稳的锚点
                if (!File.Exists(Path.Combine(StagingRoot, "package.json")))
                {
                    CleanStaging(log);
                    log.Fail("暂存目录里没有 package.json");
                    return Failure("下载到的内容不像是酒馆源码，已清理。", "请稍后重试，或换一条下载通道。", log);
                }

                reporter.Send(TavernInstallStage.Committing, "正在写入安装目录…", null, CloneShare + 4, force: true);

                bool hadInstall = Directory.Exists(InstallRoot);

                try
                {
                    Commit(log, reporter);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    CleanStaging(log);
                    log.Fail("覆盖安装目录失败", ex);
                    return Failure(
                        hadInstall ? "换版失败，原来的版本已保留。" : "安装失败，安装目录没有写入。",
                        "请先在仪表盘停止正在运行的酒馆，再试一次。",
                        log);
                }

                reporter.Send(TavernInstallStage.Verifying, "正在读取版本…", null, DepsShare - 2, force: true);
                string? version = ReadInstalledVersion() ?? revision;
                WriteStamp(used, revision, version, log);

                // 依赖装完才算装完：代码在、依赖缺，酒馆一样起不来
                DepsOutcome deps = await InstallDepsAsync(InstallRoot, reporter, log, token).ConfigureAwait(false);

                if (!deps.Ok)
                {
                    log.Fail($"依赖安装失败：{deps.Reason}");
                    log.Info($"安装结束（依赖未就绪）· {version} · 耗时 {clock.Elapsed.TotalSeconds:0.0} 秒");

                    return new TavernInstallResult(
                        false,
                        $"代码已就位（{version}），但依赖没有装好。",
                        $"在版本管理页点「安装依赖」重试。{(deps.Reason is { Length: > 0 } reason ? reason : string.Empty)}",
                        log.FilePath,
                        version,
                        used.Name)
                    {
                        DepsReady = false,
                    };
                }

                reporter.Send(TavernInstallStage.Done, "安装完成", null, 100, force: true);
                log.Info($"安装完成 · {version} · 来自 {used.Name} · 耗时 {clock.Elapsed.TotalSeconds:0.0} 秒");

                return new TavernInstallResult(
                    true,
                    $"SillyTavern {version} 已安装到 {InstallRoot}",
                    $"来自 {used.Name}，耗时 {clock.Elapsed.TotalSeconds:0.0} 秒。",
                    log.FilePath,
                    version,
                    used.Name)
                {
                    DepsReady = true,
                };
            }
            catch (OperationCanceledException)
            {
                CleanStaging(log);
                log.Warn("用户取消，暂存目录已清理");
                return Failure("已取消安装。", "没有改动现有版本。", log);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CleanStaging(log);
                log.Fail("安装失败", ex);
                return Failure("安装失败：磁盘写入被拒绝。", "关闭杀毒软件的实时拦截后重试。", log);
            }
            catch (Exception ex)
            {
                CleanStaging(log);
                log.Fail("安装失败", ex);
                return Failure("安装时出错，请重试。", ex.Message, log);
            }
        }

        // ==================== 克隆 ====================

        private readonly record struct CloneOutcome(bool Ok, string? Reason);

        /// <summary>
        /// 跑一次浅克隆。<paramref name="reporter"/> 会把 Git 的 <c>Receiving objects</c> /
        /// <c>Resolving deltas</c> / <c>Updating files</c> 三段的百分比映射成整条进度。
        /// </summary>
        private static async Task<CloneOutcome> CloneAsync(
            string git,
            SourceTarget target,
            string revision,
            Reporter reporter,
            InstallLog log,
            CancellationToken token)
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

            // 长路径：酒馆的 data/ 目录名经常很深，Windows 默认 260 字符会中途报错
            foreach (string arg in new[]
            {
                "-c", "core.longpaths=true",
                "-c", "advice.detachedHead=false",
                "-c", "credential.interactive=false",
                "-c", "http.lowSpeedLimit=1024",
                "-c", "http.lowSpeedTime=30",
                "clone", "--depth", "1", "--single-branch", "--branch", revision, "--progress",
                target.Url, StagingRoot,
            })
            {
                startInfo.ArgumentList.Add(arg);
            }

            // 任何人都不想看到「卡在这里等你输密码」的安装进度
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            startInfo.Environment["GIT_ASKPASS"] = string.Empty;
            startInfo.Environment["LC_ALL"] = "C";

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return new CloneOutcome(false, "无法启动 Git");
            }

            string? lastError = null;

            using CancellationTokenRegistration registration = token.Register(() => TryKill(process));

            process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data))
                {
                    return;
                }

                string line = e.Data.Trim();

                if (TryMapProgress(line, out double percent, out string detail))
                {
                    // 百分比已经画在进度条上、文案在 message 里了：这一行再进日志面板，
                    // 一秒几十条只会把真正的错误挤出去
                    reporter.Send(TavernInstallStage.Cloning, detail, null, percent);
                    return;
                }

                // 远端自己的进度条（Counting / Compressing objects）一秒几十行，
                // 与本地进度无关，进日志只会把真正有用的那几行冲掉
                if (line.StartsWith("remote:", StringComparison.Ordinal))
                {
                    if (!line.Contains('%'))
                    {
                        log.Info($"git: {line}");
                    }

                    return;
                }

                log.Info($"git: {line}");
                lastError = line;
            };

            process.BeginErrorReadLine();

            Task<string> stdout = process.StandardOutput.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            process.CancelErrorRead();
            await stdout.ConfigureAwait(false);

            if (process.ExitCode == 0)
            {
                reporter.Send(TavernInstallStage.Cloning, "代码已拉取，正在校验…", null, CloneShare, force: true);
                return new CloneOutcome(true, null);
            }

            return new CloneOutcome(false, lastError ?? $"Git 退出码 {process.ExitCode}");
        }

        /// <summary>
        /// 把 Git 的进度行映射成整条进度。
        /// 三段是顺序发生的：收对象（下载）→ 解压缩增量 → 检出到工作区。
        /// </summary>
        private static bool TryMapProgress(string line, out double percent, out string detail)
        {
            percent = 0;
            detail = string.Empty;

            if (line.StartsWith("Cloning into", StringComparison.Ordinal))
            {
                percent = 1;
                detail = "正在连接仓库…";
                return true;
            }

            int mark = line.IndexOf('%');
            if (mark <= 0)
            {
                return false;
            }

            // 取 % 前面那段数字
            int begin = mark - 1;
            while (begin >= 0 && char.IsDigit(line[begin]))
            {
                begin--;
            }

            if (!int.TryParse(line[(begin + 1)..mark], out int value))
            {
                return false;
            }

            double stage;

            if (line.StartsWith("Receiving objects", StringComparison.Ordinal))
            {
                stage = 0.62 * value / 100;
                detail = $"正在下载代码 · {value}%";
            }
            else if (line.StartsWith("Resolving deltas", StringComparison.Ordinal))
            {
                stage = 0.62 + 0.10 * value / 100;
                detail = $"正在解包 · {value}%";
            }
            else if (line.StartsWith("Updating files", StringComparison.Ordinal)
                || line.StartsWith("Checking out files", StringComparison.Ordinal))
            {
                stage = 0.72 + 0.20 * value / 100;
                detail = $"正在写入文件 · {value}%";
            }
            else
            {
                return false;
            }

            percent = stage * CloneShare;
            return true;
        }

        // ==================== 依赖 ====================

        private readonly record struct DepsOutcome(bool Ok, string? Reason);

        /// <summary>
        /// 在 <paramref name="root"/> 里跑一次 <c>npm install</c>。
        ///
        /// 源用 <c>--registry</c> 传参：只作用于这一个进程，不写回用户的 <c>.npmrc</c>，
        /// 用户在别处跑 npm 不会被启动器改过的东西影响。
        ///
        /// 输出按 <c>--loglevel=http</c> 收：包索引请求（几千行、没有信息量）直接丢掉，
        /// 真正下载的 tar 包一行一条推给界面，既是进度也是「到底在干什么」的证据。
        /// </summary>
        private static async Task<DepsOutcome> InstallDepsAsync(
            string root,
            Reporter reporter,
            InstallLog log,
            CancellationToken token)
        {
            string? node = RuntimeProbe.LocateNode();
            if (node is null)
            {
                return new DepsOutcome(false, "没有找到 Node.js，请先在「设置 → 运行环境」里安装。");
            }

            string? npmCli = RuntimeProbe.LocateNpmCli();
            if (npmCli is null)
            {
                return new DepsOutcome(false, "没有找到 npm，Node.js 可能没有装完整。");
            }

            string registry = LauncherSettings.NpmRegistryUrl;

            reporter.Send(
                TavernInstallStage.InstallingDeps,
                $"正在用{LauncherSettings.NpmRegistryName}源安装依赖…",
                $"$ npm install --omit=dev --registry={registry}",
                DepsShare,
                force: true);

            log.Info($"npm {npmCli}");
            log.Info($"npm install · 源 {LauncherSettings.NpmRegistryName} {registry} · 目录 {root}");

            var startInfo = new ProcessStartInfo(node)
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            foreach (string arg in new[]
            {
                npmCli,
                "install",
                "--omit=dev",
                "--no-audit",
                "--no-fund",
                "--no-progress",
                "--loglevel=http",
                "--registry=" + registry,
            })
            {
                startInfo.ArgumentList.Add(arg);
            }

            // 依赖里有原生模块的安装脚本，它们会自己调 node / npm：
            // 不把 node 目录塞进 PATH，这些脚本在系统没装 Node 的机器上会直接失败
            if (Path.GetDirectoryName(node) is { Length: > 0 } nodeDir)
            {
                startInfo.Environment["PATH"] = nodeDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            }

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return new DepsOutcome(false, "无法启动 npm。");
            }

            string? lastError = null;
            int fetched = 0;
            int resolved = 0;
            DateTime lastPush = DateTime.MinValue;
            var clock = Stopwatch.StartNew();

            // npm 在「解包 / 生成 bin / 跑安装脚本」这几步是完全哑的：
            // 缓存命中时几百个包的行两秒内刷完，之后可能几分钟没有任何输出。
            // 光靠行驱动会让界面一直停在「已处理 1 个包」，所以另起一个秒表把「还在动」说出来。
            string StatusText() => fetched > 0
                ? $"正在安装依赖 · 已处理 {fetched} 个包 · {clock.Elapsed.TotalSeconds:0} 秒"
                : resolved > 0
                    ? $"正在解析依赖 · 已查询 {resolved} 个包 · {clock.Elapsed.TotalSeconds:0} 秒"
                    : $"正在准备依赖 · {clock.Elapsed.TotalSeconds:0} 秒";

            void Push(string? detail)
            {
                DateTime now = DateTime.UtcNow;

                // npm 一秒能吐几十行：界面只吃节流后的一份，日志文件里是全量
                if ((now - lastPush).TotalMilliseconds < 120)
                {
                    return;
                }

                lastPush = now;
                reporter.Send(TavernInstallStage.InstallingDeps, StatusText(), detail, DepsShare, force: true);
            }

            void Handle(string? raw)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return;
                }

                string line = raw.Trim();

                // 两种形状都是「一个包在落地」：
                //   npm http fetch GET 200 https://…/x/-/x-1.0.0.tgz 42ms (cache miss)
                //   npm http cache x@https://…/x/-/x-1.0.0.tgz 0ms (cache hit)
                if (line.Contains("http cache ", StringComparison.Ordinal)
                    || line.Contains("http fetch ", StringComparison.Ordinal))
                {
                    if (FindUrl(line) is not { } url)
                    {
                        return;
                    }

                    // 没有 "/-/" 的是包索引（packument）请求，一个安装几百上千条，纯噪音；
                    // 但它至少说明 npm 还在解析依赖树，计数后只用于状态行
                    if (!url.Contains("/-/", StringComparison.Ordinal))
                    {
                        resolved++;
                        return;
                    }

                    fetched++;
                    string name = url[(url.LastIndexOf('/') + 1)..];
                    log.Info($"npm: 取回 {name}");
                    Push($"取回 {name}（{fetched}）");
                    return;
                }

                log.Info($"npm: {line}");

                // 进度条看不出 npm 在等什么时，这些行就是唯一的解释
                if (!line.StartsWith("npm warn", StringComparison.Ordinal)
                    && !line.StartsWith("npm error", StringComparison.Ordinal)
                    && !line.StartsWith("npm ERR", StringComparison.Ordinal)
                    && !line.StartsWith("added", StringComparison.Ordinal)
                    && !line.StartsWith("changed", StringComparison.Ordinal)
                    && !line.StartsWith("removed", StringComparison.Ordinal))
                {
                    return;
                }

                if (line.StartsWith("npm error", StringComparison.Ordinal)
                    || line.StartsWith("npm ERR", StringComparison.Ordinal))
                {
                    lastError = line;
                }

                Push(line);
            }

            process.OutputDataReceived += (_, e) => Handle(e.Data);
            process.ErrorDataReceived += (_, e) => Handle(e.Data);

            using CancellationTokenRegistration registration = token.Register(() => TryKill(process));

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // 每秒把「已经花了多久」推一次：npm 长时间不出声时，这是唯一能证明还活着的东西
            using var heartbeat = new CancellationTokenSource();
            Task ticker = Task.Run(async () =>
            {
                try
                {
                    while (!heartbeat.IsCancellationRequested)
                    {
                        await Task.Delay(1000, heartbeat.Token).ConfigureAwait(false);
                        reporter.Send(TavernInstallStage.InstallingDeps, StatusText(), null, DepsShare, force: true);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 装完了，正常退出
                }
            });

            try
            {
                await process.WaitForExitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }
            finally
            {
                heartbeat.Cancel();
                try
                {
                    await ticker.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 同上
                }
            }

            // 不等这两处排空就判结果，最后几行（往往正是错误原因）会丢
            process.CancelOutputRead();
            process.CancelErrorRead();

            if (process.ExitCode != 0)
            {
                return new DepsOutcome(false, lastError ?? $"npm 退出码 {process.ExitCode}。");
            }

            if (!Directory.Exists(Path.Combine(root, "node_modules")))
            {
                return new DepsOutcome(false, "npm 结束了，但没有生成 node_modules。");
            }

            log.Info($"npm install 完成 · 共取回 {fetched} 个包");
            return new DepsOutcome(true, null);
        }

        /// <summary>从一行 npm 日志里挑出 URL；没有就返回 null。</summary>
        private static string? FindUrl(string line)
        {
            int start = line.IndexOf("https://", StringComparison.Ordinal);
            if (start < 0)
            {
                start = line.IndexOf("http://", StringComparison.Ordinal);
            }

            if (start < 0)
            {
                return null;
            }

            int end = line.IndexOf(' ', start);
            return end < 0 ? line[start..] : line[start..end];
        }

        // ==================== 落位 ====================

        private static void Commit(InstallLog log, Reporter reporter)
        {
            // 旧版本先改名挪开、再让新代码就位，最后才删旧的。
            // 直接「删了再换」有个很难看的中间态：删到一半碰上只读文件（.git 里的 pack 就是只读的），
            // 用户手里既没有新版本也没有旧版本。
            string? retired = null;

            if (Directory.Exists(InstallRoot))
            {
                retired = RetiredRoot;

                try
                {
                    DeleteTree(retired);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log.Warn($"上一次留下的旧目录清理失败：{ex.Message}");
                }

                reporter.Send(TavernInstallStage.Committing, "正在移走旧版本…", null, CloneShare + 4.5, force: true);
                log.Info($"旧版本移到 {retired}");
                Directory.Move(InstallRoot, retired);
            }

            try
            {
                Directory.Move(StagingRoot, InstallRoot);
            }
            catch
            {
                // 新代码没能就位：把旧版本原样挪回去，不让用户平白丢一份能跑的代码
                if (retired is not null && !Directory.Exists(InstallRoot))
                {
                    Directory.Move(retired, InstallRoot);
                    log.Warn("新版本就位失败，已回滚到原版本");
                }

                throw;
            }

            reporter.Send(TavernInstallStage.Committing, "正在清理旧版本…", null, CloneShare + 5, force: true);
            log.Info($"已就位 {InstallRoot}");

            if (retired is not null)
            {
                // 旧目录里有 node_modules 时是十万级文件，删除要好一会儿：
                // 说清楚在删什么，否则进度条会像卡死
                try
                {
                    DeleteTree(retired);
                    log.Info($"旧版本已删除 {retired}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log.Warn($"旧版本目录删除失败，可手动清理 {retired}：{ex.Message}");
                }
            }
        }

        /// <summary>
        /// 递归删除一个目录，先把只读属性清掉再动手。
        ///
        /// Git 会把 <c>.git/objects/pack</c> 里的包文件设成只读，直接
        /// <c>Directory.Delete(recursive:true)</c> 会在半途抛 UnauthorizedAccessException，
        /// 留下一个删了一半的目录——换版失败大半是这个原因。
        /// </summary>
        private static void DeleteTree(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    FileAttributes attributes = File.GetAttributes(file);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 单个文件改不动就继续删别的，最后由 Directory.Delete 报出真正卡住的位置
                }
            }

            Directory.Delete(path, recursive: true);
        }

        private static void CleanStaging(InstallLog log)
        {
            try
            {
                DeleteTree(StagingRoot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Warn($"暂存目录清理失败：{ex.Message}");
            }
        }

        /// <summary>读刚装好的这份代码自己声明的版本，而不是相信我们请求的那个标签。</summary>
        private static string? ReadInstalledVersion()
        {
            try
            {
                string manifest = Path.Combine(InstallRoot, "package.json");
                if (!File.Exists(manifest))
                {
                    return null;
                }

                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest));
                if (document.RootElement.TryGetProperty("version", out JsonElement version)
                    && version.GetString() is { Length: > 0 } text)
                {
                    return text.Trim().TrimStart('v', 'V');
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // 版本读不到不影响「装好了」这个事实，界面退回使用请求的标签
            }

            return null;
        }

        /// <summary>写一份安装标记：排查时能看出这一份是谁、什么时候、从哪装的。</summary>
        private static void WriteStamp(SourceTarget target, string revision, string version, InstallLog log)
        {
            try
            {
                string content = string.Join(
                    Environment.NewLine,
                    $"component = SillyTavern",
                    $"revision = {revision}",
                    $"version = {version}",
                    $"source = {target.Name}",
                    $"url = {target.Url}",
                    $"channel = {LauncherSettings.ChannelName(LauncherSettings.Channel)}",
                    $"installed_at = {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

                File.WriteAllText(
                    Path.Combine(InstallRoot, "astrabrew-install.txt"),
                    content,
                    new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Warn($"安装标记写入失败：{ex.Message}");
            }
        }

        // ==================== 杂项 ====================

        private static IEnumerable<string> Describe(IReadOnlyList<SourceTarget> targets)
        {
            foreach (SourceTarget target in targets)
            {
                yield return target.Name;
            }
        }

        private static TavernInstallResult Failure(string message, string? hint, InstallLog log)
        {
            log.Fail(message);
            return new TavernInstallResult(false, message, hint, log.FilePath, null, null);
        }

        private static long? FreeSpace(string path)
        {
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(path));
                return root is { Length: > 0 } ? new DriveInfo(root).AvailableFreeSpace : null;
            }
            catch
            {
                return null;
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

        /// <summary>
        /// 进度节流：Git 一秒能吐几十行，每行都推给界面会让 UI 线程一直在排版。
        /// 只放行「涨了 1% 以上」或强制发送的那几帧，进度也不会倒着走。
        /// </summary>
        private sealed class Reporter
        {
            private readonly IProgress<TavernInstallProgress>? _progress;
            private double _last = -1;

            public Reporter(IProgress<TavernInstallProgress>? progress) => _progress = progress;

            public void Send(
                TavernInstallStage stage,
                string message,
                string? detail,
                double percent,
                bool force = false)
            {
                if (_progress is null)
                {
                    return;
                }

                if (!force && percent - _last < 0.9)
                {
                    return;
                }

                _last = Math.Max(_last, percent);
                _progress.Report(new TavernInstallProgress(stage, message, detail, Math.Round(percent, 1)));
            }
        }
    }
}
