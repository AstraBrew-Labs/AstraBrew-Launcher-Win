using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>安装阶段。</summary>
    public enum InstallStage
    {
        Preparing,
        Downloading,
        Extracting,
        Verifying,
        Done,
        Failed,
    }

    /// <summary>失败原因；<see cref="None"/> 表示成功。界面按它挑那句人话，不解析异常文本。</summary>
    public enum InstallError
    {
        None,
        Cancelled,
        Network,
        Download,
        Extract,
        Verify,
        DiskFull,
        Unknown,
    }

    /// <summary>
    /// 一次进度回调。
    /// <see cref="Percent"/> 是整条链路的百分比（下载 0–85、解压 85–96、替换 96–100），
    /// 换源重下会往回跳——这是实情，不该用假进度掩盖。
    /// </summary>
    public sealed record InstallProgress(
        InstallStage Stage,
        string Message,
        double Percent,
        long Received,
        long Total,
        double BytesPerSecond);

    /// <summary>
    /// 安装结果。<see cref="Message"/> 与 <see cref="Hint"/> 是给用户看的一两句话，
    /// 完整过程在 <see cref="LogPath"/> 指向的文件里。
    /// </summary>
    public sealed record InstallResult(
        bool Success,
        InstallError Error,
        string Message,
        string? Hint,
        string? LogPath);

    /// <summary>
    /// 一个可安装组件的全部差异项。
    ///
    /// 只有「怎么装」是共通的（换源下载 → 解压到暂存目录 → 校验 → 换名落位），
    /// 「装的是什么、装到哪、什么才算装对了、旧版被占用时怎么跟用户说」每个组件都不一样，
    /// 这些全部落在这里，安装引擎本身不含任何 Git / Node.js 的字眼。
    /// </summary>
    public sealed class InstallSpec
    {
        /// <summary>组件名，进日志与失败文案，如 <c>Git</c> / <c>Node.js</c>。</summary>
        public required string DisplayName { get; init; }

        /// <summary>副标题里的版本写法，如 <c>MinGit 2.55.0.windows.2</c> / <c>Node.js v22.20.0</c>。</summary>
        public required string VersionLabel { get; init; }

        /// <summary>装完后取版本用的键，对应 <see cref="RuntimeEnvironment.VersionOf"/>。</summary>
        public required string ProbeKey { get; init; }

        /// <summary>日志文件名前缀，如 <c>mingit</c> / <c>nodejs</c>。</summary>
        public required string LogTask { get; init; }

        /// <summary>下载到临时目录后的文件名。</summary>
        public required string ArchiveName { get; init; }

        /// <summary>正式安装目录，如 <c>bin\mingit</c>。</summary>
        public required string InstallRoot { get; init; }

        /// <summary>解压暂存目录，与正式目录同盘，最后一步是同盘改名。</summary>
        public required string StagingRoot { get; init; }

        /// <summary>解压后大致需要多少空闲空间（留过余量）。</summary>
        public required long RequiredFreeBytes { get; init; }

        /// <summary>下载源，顺序即尝试顺序：镜像在前，直连兜底。</summary>
        public required IReadOnlyList<(string Name, string Url)> Sources { get; init; }

        /// <summary>给定目录里是否已经是一个可用的安装（用于校验暂存目录）。</summary>
        public required Func<string, bool> ExistsAt { get; init; }

        /// <summary>解压出来缺文件时给用户的话。</summary>
        public required string IncompleteMessage { get; init; }

        public required string IncompleteHint { get; init; }

        /// <summary>旧版本删不掉时给用户的话（通常是进程占用）。</summary>
        public required string BusyMessage { get; init; }

        public required string BusyHint { get; init; }

        /// <summary>装完并探测到版本后给用户的一句话，参数是探测到的版本号。</summary>
        public required Func<string, string> SuccessDetail { get; init; }

        /// <summary>下载源名字串，供界面在失败时说明「都试过了」。</summary>
        public string SourceNames => string.Join("、", Sources.Select(source => source.Name));

        public string ArchivePath => Path.Combine(AppPaths.TempRoot, ArchiveName);
    }

    /// <summary>
    /// 内置环境安装引擎：下载 zip → 解压到暂存目录 → 校验 → 换名落位。
    ///
    /// 几条硬约定：
    /// · <b>镜像优先</b>：国内直连 GitHub / nodejs.org 十次有八次超时，
    ///   把直连放第一位等于让每个用户先等半分钟再失败。
    /// · <b>换源不提示升级</b>：某个源连不上是常态（镜像同步、限流、DNS 污染），
    ///   静默换下一个，只有全部失败才对用户说话。
    /// · <b>解压先落暂存目录</b>，校验通过再改名成正式目录，中途失败不会留下一个「半个环境」——
    ///   那比没装更糟：探测能过、一跑就报错。
    /// · <b>界面只给一句话</b>：换源细节、HTTP 状态、异常堆栈全进 <see cref="InstallLog"/>。
    /// · <b>尊重代理设置</b>：跟随设置页的「无 / 系统 / 自定义」，自定义代理用不上时别硬走。
    /// </summary>
    public static class ComponentInstaller
    {
        /// <summary>下载阶段占整条进度条的份额；解压到 <see cref="ExtractShare"/>，剩下的留给替换与校验。</summary>
        private const double DownloadShare = 85;
        private const double ExtractShare = 96;

        private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(25);
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(120);

        /// <summary>
        /// 安装一个组件。<paramref name="progress"/> 可为 null（后台静默安装）；
        /// 取消通过 <paramref name="token"/>，取消后不留半成品（暂存目录与 zip 都会被清掉）。
        /// 永不抛异常，失败一律折成 <see cref="InstallResult"/>。
        /// </summary>
        public static Task<InstallResult> InstallAsync(
            InstallSpec spec,
            IProgress<InstallProgress>? progress,
            CancellationToken token) =>
            Task.Run(() => RunAsync(spec, progress, token));

        private static async Task<InstallResult> RunAsync(
            InstallSpec spec,
            IProgress<InstallProgress>? progress,
            CancellationToken token)
        {
            using InstallLog log = InstallLog.Create(spec.LogTask);
            var reporter = new Reporter(progress);

            try
            {
                reporter.Send(InstallStage.Preparing, "检查安装目录与磁盘空间…", 0, force: true);
                log.Info($"{spec.VersionLabel} 安装开始");
                log.Info($"目标目录 {spec.InstallRoot}");
                log.Info($"暂存目录 {spec.StagingRoot}");
                log.Info($"下载文件 {spec.ArchivePath}");

                Directory.CreateDirectory(Path.GetDirectoryName(spec.InstallRoot)!);
                Directory.CreateDirectory(AppPaths.TempRoot);

                if (FreeSpace(AppPaths.RoamingRoot) is { } free && free < spec.RequiredFreeBytes)
                {
                    log.Fail($"磁盘可用空间不足：{FormatSize(free)}，至少需要 {FormatSize(spec.RequiredFreeBytes)}");
                    return Failure(
                        InstallError.DiskFull,
                        $"磁盘空间不足，至少需要 {FormatSize(spec.RequiredFreeBytes)} 可用空间。",
                        "清理系统盘后再点一次「安装」。",
                        log);
                }

                (string? sourceName, InstallError lastError) =
                    await DownloadFromFirstReachableAsync(spec, reporter, log, token).ConfigureAwait(false);

                if (sourceName is null)
                {
                    return lastError == InstallError.Download
                        ? Failure(
                            InstallError.Download,
                            "下载源都没有返回完整的安装包，请稍后重试。",
                            $"已依次尝试 {spec.SourceNames}。",
                            log)
                        : Failure(
                            InstallError.Network,
                            "连不上下载源，请检查网络后重试。",
                            $"已依次尝试 {spec.SourceNames}，均未连上。",
                            log);
                }

                token.ThrowIfCancellationRequested();

                await ExtractAsync(spec, reporter, log, token).ConfigureAwait(false);

                token.ThrowIfCancellationRequested();

                if (!spec.ExistsAt(spec.StagingRoot))
                {
                    log.Fail($"解压结果不完整，未在 {spec.StagingRoot} 找到可执行文件");
                    return Failure(
                        InstallError.Verify,
                        spec.IncompleteMessage,
                        spec.IncompleteHint,
                        log);
                }

                reporter.Send(InstallStage.Verifying, "正在替换到内置环境…", ExtractShare, force: true);
                Commit(spec, log);

                WriteStamp(spec, sourceName, log);
                TryDelete(spec.ArchivePath);
                TryDeleteDirectory(spec.StagingRoot);

                reporter.Send(InstallStage.Done, "安装完成", 100, force: true);
                log.Info($"安装完成，来源 {sourceName}");
                return new InstallResult(true, InstallError.None, "安装完成", null, log.FilePath);
            }
            catch (OperationCanceledException)
            {
                log.Warn("用户取消安装");
                TryDelete(spec.ArchivePath);
                TryDeleteDirectory(spec.StagingRoot);
                reporter.Send(InstallStage.Failed, "已取消", 0, force: true);
                return new InstallResult(false, InstallError.Cancelled, "已取消安装。", null, log.FilePath);
            }
            catch (ExtractException ex)
            {
                log.Fail("解压失败", ex.InnerException ?? ex);
                TryDelete(spec.ArchivePath);
                TryDeleteDirectory(spec.StagingRoot);
                reporter.Send(InstallStage.Failed, "解压失败", 0, force: true);
                return Failure(ex.Error, ex.Message, ex.Hint, log);
            }
            catch (Exception ex)
            {
                log.Fail("安装过程中出现未预期的错误", ex);
                TryDelete(spec.ArchivePath);
                TryDeleteDirectory(spec.StagingRoot);
                reporter.Send(InstallStage.Failed, "安装失败", 0, force: true);
                return Failure(
                    InstallError.Unknown,
                    "安装没能完成，请重试。",
                    "详细原因见日志。",
                    log);
            }
        }

        // ==================== 下载 ====================

        /// <summary>
        /// 依次尝试下载源，返回第一个成功的源名 + <see cref="InstallError.None"/>；
        /// 全部失败时名字为 null，错误按「有没有拿到过响应」区分网络不通与拿不到完整安装包。
        /// 单个源失败只记日志、清掉半截文件，继续下一个——不打断用户，也不弹窗。
        /// </summary>
        private static async Task<(string? Name, InstallError Error)> DownloadFromFirstReachableAsync(
            InstallSpec spec,
            Reporter reporter,
            InstallLog log,
            CancellationToken token)
        {
            var stoppedBy = InstallError.Network;
            var sawResponse = false;

            foreach ((string name, string url) in spec.Sources)
            {
                token.ThrowIfCancellationRequested();

                reporter.Send(InstallStage.Downloading, $"正在连接 {name}…", 0, force: true);
                log.Info($"尝试下载源 {name}：{url}");

                try
                {
                    await DownloadAsync(spec, name, url, reporter, log, token).ConfigureAwait(false);
                    return (name, InstallError.None);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (DownloadException ex)
                {
                    stoppedBy = ex.Error;
                    sawResponse |= ex.Error == InstallError.Download;
                    log.Fail($"下载源 {name} 不可用", ex.InnerException ?? ex);
                    TryDelete(spec.ArchivePath);
                    reporter.Send(
                        InstallStage.Downloading,
                        $"{name} 不可用，换下一个源…",
                        0,
                        force: true);
                }
            }

            log.Fail($"全部 {spec.Sources.Count} 个下载源均失败（最后原因 {stoppedBy}）");
            return (null, sawResponse ? InstallError.Download : InstallError.Network);
        }

        private static async Task DownloadAsync(
            InstallSpec spec,
            string name,
            string url,
            Reporter reporter,
            InstallLog log,
            CancellationToken token)
        {
            using HttpClient client = CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);

            stall.CancelAfter(ConnectTimeout);

            HttpResponseMessage response;
            try
            {
                response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw new DownloadException(
                    InstallError.Network,
                    $"{name} 连接超时");
            }
            catch (Exception ex)
            {
                // 任何传输层异常都只算「这个源不行」，换下一个；不能让单个源把整条链路打断
                throw new DownloadException(
                    InstallError.Network,
                    $"{name} 连接失败",
                    ex);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new DownloadException(
                        InstallError.Download,
                        $"{name} 返回 HTTP {(int)response.StatusCode}");
                }

                long total = response.Content.Headers.ContentLength ?? -1;
                log.Info($"{name} 响应 {(int)response.StatusCode} · 长度 {(total > 0 ? FormatSize(total) : "未知")}");

                long received = 0;
                var clock = Stopwatch.StartNew();
                byte[] buffer = new byte[81920];

                await using Stream input = await response.Content
                    .ReadAsStreamAsync(token)
                    .ConfigureAwait(false);
                await using var output = new FileStream(
                    spec.ArchivePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    useAsync: true);

                while (true)
                {
                    int read;
                    try
                    {
                        // 每次读到数据就重置超时：只要还在走，慢也能等到；彻底不动才判死
                        stall.CancelAfter(StallTimeout);
                        read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (OperationCanceledException)
                    {
                        throw new DownloadException(
                            InstallError.Network,
                            $"{name} 下载中断：{StallTimeout.TotalSeconds:0} 秒没有收到数据");
                    }
                    catch (Exception ex)
                    {
                        throw new DownloadException(InstallError.Network, $"{name} 下载中断", ex);
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    received += read;

                    double percent = total > 0
                        ? received * DownloadShare / total
                        : 0;
                    double speed = clock.Elapsed.TotalSeconds > 0.2
                        ? received / clock.Elapsed.TotalSeconds
                        : -1;

                    reporter.Send(
                        InstallStage.Downloading,
                        $"正在从 {name} 下载",
                        percent,
                        received,
                        total,
                        speed);
                }

                await output.FlushAsync(token).ConfigureAwait(false);

                if (total > 0 && received != total)
                {
                    throw new DownloadException(
                        InstallError.Download,
                        $"{name} 下载不完整（{FormatSize(received)} / {FormatSize(total)}）");
                }

                if (received < 1024 * 1024)
                {
                    // 正常安装包都几十兆；只有几百 K 基本是被劫持到了错误页
                    throw new DownloadException(
                        InstallError.Download,
                        $"{name} 返回的内容不像安装包（{FormatSize(received)}）");
                }

                log.Info($"{name} 下载完成：{FormatSize(received)}，耗时 {clock.Elapsed.TotalSeconds:0.0} 秒，"
                    + $"平均 {FormatSize((long)(received / Math.Max(clock.Elapsed.TotalSeconds, 0.1)))}/s");
            }
        }

        private static HttpClient CreateClient() => ProxyHttpClient.Create();

        // ==================== 解压 ====================

        private static Task ExtractAsync(
            InstallSpec spec,
            Reporter reporter,
            InstallLog log,
            CancellationToken token) =>
            Task.Run(
                () =>
                {
                    string stagingFull = Path.GetFullPath(spec.StagingRoot) + Path.DirectorySeparatorChar;

                    TryDeleteDirectory(spec.StagingRoot);
                    Directory.CreateDirectory(spec.StagingRoot);

                    using ZipArchive archive = ZipFile.OpenRead(spec.ArchivePath);
                    int total = archive.Entries.Count;

                    // 官方 Node.js 包把内容都塞在一层 node-v22.20.0-win-x64\ 下面，
                    // 不剥掉这层，node.exe 会落在 bin\nodejs\node-v22.20.0-win-x64\node.exe，
                    // 探测找不到、PATH 也拼不对。MinGit 是根目录平铺的，前缀为空，走同一条路也不会错。
                    string prefix = DetectRootPrefix(archive);
                    log.Info($"压缩包含 {total} 个条目，根目录前缀 [{(prefix.Length > 0 ? prefix : "无")}]，解压到 {spec.StagingRoot}");

                    reporter.Send(InstallStage.Extracting, "正在解压…", DownloadShare, force: true);

                    int done = 0;
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        token.ThrowIfCancellationRequested();
                        done++;

                        string relative = prefix.Length > 0 && entry.FullName.StartsWith(prefix, StringComparison.Ordinal)
                            ? entry.FullName[prefix.Length..]
                            : entry.FullName;

                        if (relative.Length == 0)
                        {
                            // 前缀目录本身那条记录，剥完就空了
                            continue;
                        }

                        string destination;
                        try
                        {
                            destination = Path.GetFullPath(Path.Combine(spec.StagingRoot, relative));
                        }
                        catch (ArgumentException)
                        {
                            // 名字里带非法字符（老 zip 里偶见），跳过即可
                            log.Warn($"跳过非法条目 {entry.FullName}");
                            continue;
                        }

                        // zip-slip 防护：解出来的路径必须还在暂存目录内
                        if (!destination.StartsWith(stagingFull, StringComparison.OrdinalIgnoreCase))
                        {
                            log.Warn($"跳过越界条目 {entry.FullName}");
                            continue;
                        }

                        if (entry.Name.Length == 0)
                        {
                            Directory.CreateDirectory(destination);
                            continue;
                        }

                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                            entry.ExtractToFile(destination, overwrite: true);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            throw new ExtractException(
                                InstallError.Extract,
                                "解压失败：磁盘空间不足或安装目录被占用。",
                                "清理磁盘、关闭杀毒软件的实时拦截后重试。",
                                ex);
                        }

                        // 解压只走到 ExtractShare：最后一步「换成正式目录」要留出可见的进度，
                        // 否则进度条先到 100% 再回跳，看起来像重装了一遍
                        double percent = DownloadShare
                            + (ExtractShare - DownloadShare) * done / Math.Max(total, 1);

                        reporter.Send(InstallStage.Extracting, "正在解压…", percent);
                    }

                    log.Info($"解压完成，共 {done} 个条目");
                },
                token);

        /// <summary>
        /// 找出「所有条目共用的那一层顶层目录」，返回带结尾斜杠的前缀；条目在根上平铺时返回空串。
        /// 只认一层：多层嵌套的包（很少见）宁可不解，也不猜错层级把文件摊平。
        /// </summary>
        private static string DetectRootPrefix(ZipArchive archive)
        {
            string? candidate = null;

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (entry.FullName.Length == 0)
                {
                    continue;
                }

                int slash = entry.FullName.IndexOf('/');
                if (slash < 0)
                {
                    return string.Empty;   // 根上有文件，说明没有统一前缀
                }

                string head = entry.FullName[..slash];
                if (candidate is null)
                {
                    candidate = head;
                }
                else if (!string.Equals(candidate, head, StringComparison.Ordinal))
                {
                    return string.Empty;   // 顶层有多个目录，不能剥
                }
            }

            return candidate is null ? string.Empty : candidate + "/";
        }

        /// <summary>
        /// 把暂存目录换到正式位置。同盘 rename，失败要么是旧文件被占用（工具正在跑），
        /// 要么是没权限——这两种都要给出能操作的建议，不能只说「安装失败」。
        /// </summary>
        private static void Commit(InstallSpec spec, InstallLog log)
        {
            if (Directory.Exists(spec.InstallRoot))
            {
                log.Info($"移除旧版本 {spec.InstallRoot}");
                try
                {
                    Directory.Delete(spec.InstallRoot, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new ExtractException(
                        InstallError.Extract,
                        spec.BusyMessage,
                        spec.BusyHint,
                        ex);
                }
            }

            Directory.Move(spec.StagingRoot, spec.InstallRoot);
            log.Info($"已就位 {spec.InstallRoot}");
        }

        /// <summary>写一份安装标记，排查时能看出这一份是谁、什么时候、从哪装的。</summary>
        private static void WriteStamp(InstallSpec spec, string source, InstallLog log)
        {
            try
            {
                string content = string.Join(
                    Environment.NewLine,
                    $"component = {spec.DisplayName}",
                    $"version = {spec.VersionLabel}",
                    $"source = {source}",
                    $"installed_at = {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

                File.WriteAllText(
                    Path.Combine(spec.InstallRoot, "astrabrew-install.txt"),
                    content,
                    new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Warn($"安装标记写入失败：{ex.Message}");
            }
        }

        // ==================== 杂项 ====================

        private static InstallResult Failure(InstallError error, string message, string? hint, InstallLog log) =>
            new(false, error, message, hint, log.FilePath);

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

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 临时文件删不掉不影响结果，下次安装会覆盖
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        internal static string FormatSize(long bytes) => bytes switch
        {
            >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.0} GB",
            >= 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.0} MB",
            >= 1024 => $"{bytes / 1024.0:0} KB",
            _ => $"{bytes} B",
        };

        /// <summary>单个源下载失败；带上是网络问题还是服务端问题，最后汇总时决定说哪句人话。</summary>
        private sealed class DownloadException : Exception
        {
            public DownloadException(InstallError error, string message, Exception? inner = null)
                : base(message, inner) => Error = error;

            public InstallError Error { get; }
        }

        /// <summary>解压 / 替换阶段的失败；<see cref="Exception.Message"/> 直接给用户看，所以必须是能操作的话。</summary>
        private sealed class ExtractException : Exception
        {
            public ExtractException(InstallError error, string message, string hint, Exception? inner = null)
                : base(message, inner)
            {
                Error = error;
                Hint = hint;
            }

            public InstallError Error { get; }

            public string Hint { get; }
        }

        /// <summary>
        /// 进度上报限流：81920 字节一个 chunk，全速时一秒能触发几百次，
        /// 不节流会把 UI 线程压满。规则是「阶段变了 / 文案变了 / 百分比涨了 1 且过了 120ms」。
        /// </summary>
        private sealed class Reporter
        {
            private readonly IProgress<InstallProgress>? _sink;
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private readonly object _gate = new();
            private double _lastPercent = -100;
            private string? _lastMessage;

            public Reporter(IProgress<InstallProgress>? sink) => _sink = sink;

            public void Send(
                InstallStage stage,
                string message,
                double percent,
                long received = 0,
                long total = -1,
                double speed = -1,
                bool force = false)
            {
                if (_sink is null)
                {
                    return;
                }

                lock (_gate)
                {
                    bool messageChanged = !string.Equals(message, _lastMessage, StringComparison.Ordinal);
                    bool percentMoved = Math.Abs(percent - _lastPercent) >= 1;

                    if (!force && !messageChanged && !(percentMoved && _clock.Elapsed >= ReportInterval))
                    {
                        return;
                    }

                    _lastPercent = percent;
                    _lastMessage = message;
                    _clock.Restart();
                }

                _sink.Report(new InstallProgress(
                    stage,
                    message,
                    Math.Clamp(percent, 0, 100),
                    received,
                    total,
                    speed));
            }
        }
    }
}
