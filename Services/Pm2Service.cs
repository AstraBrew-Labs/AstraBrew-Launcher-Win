using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>jlist 里我们关心的字段。</summary>
    public sealed record Pm2App(
        string Name,
        int PmId,
        string Status,
        int? Pid,
        string? Cwd,
        string? Script,
        DateTime? StartedAt)
    {
        /// <summary>真的在跑：online 且拿得到 pid。launching / errored / stopped 都不算。</summary>
        public bool IsOnline => Pid is > 0 && string.Equals(Status, "online", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>一次 pm2 命令的结果。失败时 <see cref="Detail"/> 可直接进日志。</summary>
    public readonly record struct Pm2Result(bool Ok, int ExitCode, string StdOut, string StdErr)
    {
        public string Detail
        {
            get
            {
                string raw = StdErr.Length > 0 ? StdErr : StdOut;
                string first = raw
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault() ?? string.Empty;

                return first.Length > 0 ? first : $"exit {ExitCode}";
            }
        }
    }

    /// <summary>交给 <c>pm2 start</c> 的全部输入。用记录承载，调用点一眼能看清少了什么。</summary>
/// <param name="Environment">
/// 要传给**酒馆进程**的环境变量（PATH / GIT_CONFIG_GLOBAL / NODE_OPTIONS / 代理）。
///
/// 命令行参数传不了这些：PM2 拉起的进程继承的是 **daemon** 的环境，
/// 不是我们这次调 pm2 的子进程环境。所以只能写进 ecosystem 的 <c>env</c> 字段。
/// </param>
public sealed record Pm2LaunchSpec(
    string AppName,
    string WorkingDirectory,
    string Script,
    IReadOnlyList<string> AppArguments,
    string NodePath,
    IReadOnlyDictionary<string, string> Environment);

    /// <summary>
    /// PM2 的运行时控制面：起 / 停 / 删 / 列。安装归 <see cref="Pm2Installer"/>，这里只管已经装好的那份。
    ///
    /// 几条硬约定：
    /// · **用 node 跑 <c>node_modules\pm2\bin\pm2</c>，不走 <c>pm2.cmd</c>**：
    ///   后者要先起 cmd.exe，退出码与输出编码都会拐一道弯，拿不到真实的失败原因；
    /// · **`pm2 start` 的退出码不可信**（注册成功就返回 0，进程随后可能 errored），
    ///   调用方必须再查 <c>jlist</c> 的 status；
    /// · **停进程只用 <c>pm2 delete</c>，绝不对 PM2 的子进程用 taskkill**：
    ///   PM2 会发现进程没了并 autorestart，形成「杀掉又占端口」的死循环。
    /// </summary>
    public static class Pm2Service
    {
        /// <summary>app 名前缀：切换实例后靠它识别并清理旧 app。</summary>
        public const string NamePrefix = "astrabrew-tavern-";

        /// <summary>PM2 自己的输出落到这里（不与我们按天滚动的运行日志混在一起）。</summary>
        public static string LogFolder { get; } = Path.Combine(RuntimeLog.FolderPath, "pm2");

        /// <summary>能用：包装好了，且有 node 可以拿来跑 pm2。</summary>
        public static bool IsAvailable =>
            Pm2Installer.IsInstalled && RuntimeProbe.LocateNode() is not null;

        // ==================== 命名与路径 ====================

        /// <summary>
        /// app 名：内置实例固定一个，本地实例按路径哈希。
        ///
        /// **故意不带版本号**：换版本不该换名字，否则每次换版都会在 PM2 表里留一个删不掉的旧 app。
        /// 本地实例用哈希而不是路径原文，是避免中文路径进命令行后的编码与引号问题。
        /// </summary>
        public static string AppNameFor(TavernInstance instance) => instance.IsBuiltin
            ? NamePrefix + "builtin"
            : NamePrefix + "local-" + ShortHash(instance.Path);

        public static string OutPath(string appName) => Path.Combine(LogFolder, appName + "-out.log");

        public static string ErrPath(string appName) => Path.Combine(LogFolder, appName + "-err.log");

        private static string ShortHash(string path)
        {
            byte[] bytes = SHA1.HashData(Encoding.UTF8.GetBytes(path.TrimEnd('\\', '/').ToLowerInvariant()));
            return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
        }

        // ==================== 查询 ====================

        /// <summary>
        /// 列出 PM2 表里的 app。失败返回空列表并把原因写进运行日志，**不抛**——
        /// PM2 出问题只该让「后台常驻」失效，不该让启动流程崩掉。
        /// </summary>
        public static async Task<IReadOnlyList<Pm2App>> ListAsync(CancellationToken token = default)
        {
            if (!IsAvailable)
            {
                return Array.Empty<Pm2App>();
            }

            Pm2Result result = await RunAsync(new[] { "jlist" }, TimeSpan.FromSeconds(10), token)
                .ConfigureAwait(false);

            if (!result.Ok)
            {
                RuntimeLog.Warn($"pm2 jlist failed: {result.Detail}");
                return Array.Empty<Pm2App>();
            }

            return Parse(result.StdOut);
        }

        public static async Task<Pm2App?> FindAsync(string appName, CancellationToken token = default)
        {
            IReadOnlyList<Pm2App> apps = await ListAsync(token).ConfigureAwait(false);

            return apps.FirstOrDefault(app => string.Equals(app.Name, appName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>PM2 正在管的所有 pid。端口预检拿它做豁免，避免杀掉会被 autorestart 拉回来的进程。</summary>
        public static async Task<IReadOnlySet<int>> ManagedPidsAsync(CancellationToken token = default)
        {
            IReadOnlyList<Pm2App> apps = await ListAsync(token).ConfigureAwait(false);

            return apps
                .Where(app => app.Pid is > 0)
                .Select(app => app.Pid!.Value)
                .ToHashSet();
        }

        // ==================== 启动 / 停止 ====================

        /// <summary>
        /// 起一个 app。返回前只保证「已注册」，端口就绪由调用方继续轮询（PM2 是异步拉起）。
        /// </summary>
        public static async Task<Pm2Result> StartAsync(Pm2LaunchSpec spec, CancellationToken token = default)
        {
            if (!IsAvailable)
            {
                return new Pm2Result(false, -1, string.Empty, "PM2 not available");
            }

            // 此刻同名 app 已被删掉，两个日志文件没有句柄持有，可以直接清空
            PrepareLogFiles(spec.AppName);

            string ecosystem = WriteEcosystem(spec);

            if (ecosystem.Length == 0)
            {
                return new Pm2Result(false, -1, string.Empty, "cannot write ecosystem file");
            }

            // 用 ecosystem 而不是一长串命令行参数：只有 env 字段能把环境变量交给 PM2 拉起的进程
            var args = new List<string> { "start", ecosystem, "--no-color" };

            // 20 秒：真实冷启（拉 daemon + 版本自检）2~8 秒足够，留出余量但不至于让用户干等。
            // 超时后调用方会用「端口是否在听」再判一次，所以不必为极端情况把超时拉长。
            return await RunAsync(args, TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
        }

        /// <summary>
        /// 删除（不是 stop）：连带结束进程树，且不会被 autorestart 拉回来。幂等。
        /// 「app 不存在」也按成功处理，否则关窗路径会一直报错。
        /// </summary>
        public static async Task<Pm2Result> DeleteAsync(string appName, CancellationToken token = default)
        {
            Pm2Result result = await RunAsync(new[] { "delete", appName }, TimeSpan.FromSeconds(15), token)
                .ConfigureAwait(false);

            return NormalizeDelete(result);
        }

        /// <summary>同步删除：只给窗口关闭路径用（那里没有事件循环再转一圈的机会）。</summary>
        public static Pm2Result Delete(string appName, TimeSpan timeout)
        {
            Pm2Result result = RunSync(new[] { "delete", appName }, timeout);

            return NormalizeDelete(result);
        }

        /// <summary>
        /// 删掉除 <paramref name="keep"/> 之外所有本应用的 app（切换实例后的残留）。
        /// <paramref name="keep"/> 传 <c>null</c> 表示「连同名一起删」——启动前清场用。
        /// 返回真正删掉的数量。
        /// </summary>
        public static async Task<int> DeleteSiblingsAsync(string? keep, CancellationToken token = default)
        {
            IReadOnlyList<Pm2App> apps = await ListAsync(token).ConfigureAwait(false);

            int removed = 0;

            foreach (Pm2App app in apps)
            {
                if (!app.Name.StartsWith(NamePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (keep is not null && string.Equals(app.Name, keep, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if ((await DeleteAsync(app.Name, token).ConfigureAwait(false)).Ok)
                {
                    removed++;
                }
            }

            return removed;
        }

        /// <summary>表里一个 app 都没有时结束 daemon（省一份常驻内存）。表非空一律不动。</summary>
        public static async Task<bool> KillDaemonIfIdleAsync(CancellationToken token = default)
        {
            if (!IsAvailable)
            {
                return false;
            }

            IReadOnlyList<Pm2App> apps = await ListAsync(token).ConfigureAwait(false);

            if (apps.Count > 0)
            {
                return false;
            }

            Pm2Result result = await RunAsync(new[] { "kill" }, TimeSpan.FromSeconds(10), token)
                .ConfigureAwait(false);

            return result.Ok;
        }

        // ==================== 内部 ====================

        private static Pm2Result NormalizeDelete(Pm2Result result)
        {
            // 退出码 1 + "not found" 等于「本来就没有」，按成功算
            if (result.Ok)
            {
                return result;
            }

            bool notFound = result.StdErr.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || result.StdOut.Contains("not found", StringComparison.OrdinalIgnoreCase);

            return notFound ? result with { Ok = true } : result;
        }

        /// <summary>ecosystem 文件路径：每次启动重写一份，放在日志目录里便于排错时查看。</summary>
        public static string EcosystemPath(string appName) => Path.Combine(LogFolder, appName + ".ecosystem.json");

        /// <summary>
        /// 写 ecosystem 配置。字段含义：
        /// · <c>interpreter</c> 钉死 node 绝对路径 —— daemon 的 PATH 停留在它自己第一次被拉起时，
        ///   用户后来切换内置/系统 node，daemon 不会跟着变；
        /// · <c>out_file</c> / <c>error_file</c> 分开两个文件，否则无法区分级别；
        /// · <c>kill_timeout</c> 3000 —— PM2 默认 1.6 秒，酒馆在 Windows 上经常来不及响应；
        /// · <c>env</c> 是唯一能把环境变量交给 app 的通道（命令行参数做不到）。
        ///
        /// 失败返回空串，调用方按启动失败处理。
        /// </summary>
        private static string WriteEcosystem(Pm2LaunchSpec spec)
        {
            try
            {
                Directory.CreateDirectory(LogFolder);

                var payload = new
                {
                    apps = new[]
                    {
                        new
                        {
                            name = spec.AppName,
                            script = spec.Script,
                            args = spec.AppArguments,
                            cwd = spec.WorkingDirectory,
                            interpreter = spec.NodePath,
                            out_file = OutPath(spec.AppName),
                            error_file = ErrPath(spec.AppName),
                            merge_logs = false,
                            autorestart = true,
                            kill_timeout = 3000,

                            // PM2 6.0.5 的默认值就是 true，显式写死是为了防它以后改默认；
                            // 也让「为什么这里不该有黑框」的意图留在文件里
                            windowsHide = true,
                            env = spec.Environment,
                        },
                    },
                };

                string path = EcosystemPath(spec.AppName);
                string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });

                File.WriteAllText(path, json, new UTF8Encoding(false));

                return path;
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn($"pm2 ecosystem write failed: {ex.Message}");

                return string.Empty;
            }
        }

        private static void PrepareLogFiles(string appName)
        {
            try
            {
                Directory.CreateDirectory(LogFolder);

                // 通配清空：PM2 会按实例编号写 -out-0.log / -err-1.log 这类变体，
                // 只清 -out.log 的话，旧的实例日志会在下次启动时被尾随器整段回放出来
                foreach (string pattern in new[] { appName + "-out*.log", appName + "-err*.log" })
                {
                    foreach (string path in Directory.EnumerateFiles(LogFolder, pattern))
                    {
                        try
                        {
                            // 用户可能正拿记事本开着它：清不掉就继续追加，不影响启动
                            using var stream = new FileStream(
                                path,
                                FileMode.Create,
                                FileAccess.Write,
                                FileShare.ReadWrite);
                        }
                        catch (Exception)
                        {
                            // 单个文件清不掉不该挡住启动
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 目录建不出来同样不挡启动：日志缺失比启动失败轻得多
            }
        }

        private static IReadOnlyList<Pm2App> Parse(string stdout)
        {
            // PM2 会在 JSON 前后夹 MOTD、升级提示之类，不能假设整段都是 JSON
            int start = stdout.IndexOf('[');
            int end = stdout.LastIndexOf(']');

            if (start < 0 || end <= start)
            {
                return Array.Empty<Pm2App>();
            }

            var apps = new List<Pm2App>();

            try
            {
                using JsonDocument document = JsonDocument.Parse(stdout[start..(end + 1)]);

                foreach (JsonElement item in document.RootElement.EnumerateArray())
                {
                    string name = item.TryGetProperty("name", out JsonElement nameElement)
                        ? nameElement.GetString() ?? string.Empty
                        : string.Empty;

                    if (name.Length == 0)
                    {
                        continue;
                    }

                    int pmId = item.TryGetProperty("pm_id", out JsonElement idElement) && idElement.TryGetInt32(out int id)
                        ? id
                        : -1;

                    int? pid = item.TryGetProperty("pid", out JsonElement pidElement) && pidElement.TryGetInt32(out int topPid) && topPid > 0
                        ? topPid
                        : null;

                    string status = string.Empty;
                    string? cwd = null;
                    string? script = null;
                    DateTime? startedAt = null;

                    if (item.TryGetProperty("pm2_env", out JsonElement env) && env.ValueKind == JsonValueKind.Object)
                    {
                        if (env.TryGetProperty("status", out JsonElement statusElement))
                        {
                            status = statusElement.GetString() ?? string.Empty;
                        }

                        if (pid is null
                            && env.TryGetProperty("pid", out JsonElement envPid)
                            && envPid.TryGetInt32(out int envPidValue)
                            && envPidValue > 0)
                        {
                            pid = envPidValue;
                        }

                        if (env.TryGetProperty("pm_cwd", out JsonElement cwdElement))
                        {
                            cwd = cwdElement.GetString();
                        }

                        if (env.TryGetProperty("pm_exec_path", out JsonElement execElement))
                        {
                            script = execElement.GetString();
                        }

                        // pm_uptime 是毫秒时间戳：接管已有进程时，Uptime 才是真值
                        if (env.TryGetProperty("pm_uptime", out JsonElement upElement)
                            && upElement.TryGetInt64(out long uptimeMs)
                            && uptimeMs > 0)
                        {
                            startedAt = DateTimeOffset.FromUnixTimeMilliseconds(uptimeMs).LocalDateTime;
                        }
                    }

                    apps.Add(new Pm2App(name, pmId, status, pid, cwd, script, startedAt));
                }
            }
            catch (JsonException)
            {
                // 版本差异导致字段缺失：当作空表，让调用方走回退
                return Array.Empty<Pm2App>();
            }

            return apps;
        }

        private static ProcessStartInfo CreateStartInfo(string node, string? git)
        {
            var startInfo = new ProcessStartInfo(node)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };

            // 顺序讲究：先拼好 node/git 的 PATH，再让 Pm2Installer 往前面插 InstallRoot——
            // 它只在前者里没有 InstallRoot 时插入，顺序反了会丢掉 node/git 路径
            startInfo.Environment["PATH"] = TavernRuntime.BuildPath(node, git);
            Pm2Installer.ApplyEnvironment(startInfo);

            TavernGitConfig.Apply(startInfo);

            return startInfo;
        }

        private static async Task<Pm2Result> RunAsync(
            IReadOnlyList<string> args,
            TimeSpan timeout,
            CancellationToken token)
        {
            string? node = RuntimeProbe.LocateNode();

            if (node is null)
            {
                return new Pm2Result(false, -1, string.Empty, "node not found");
            }

            ProcessStartInfo startInfo = CreateStartInfo(node, RuntimeProbe.LocateGit());
            startInfo.ArgumentList.Add(CliPath);

            foreach (string arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            var clock = Stopwatch.StartNew();

            try
            {
                using Process? process = Process.Start(startInfo);

                if (process is null)
                {
                    return LogCommand(args, new Pm2Result(false, -1, string.Empty, "cannot start pm2"), clock);
                }

                Task<string> stdout = process.StandardOutput.ReadToEndAsync(token);
                Task<string> stderr = process.StandardError.ReadToEndAsync(token);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(timeout);

                try
                {
                    await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception)
                    {
                        // 进程已经退了
                    }

                    return LogCommand(
                        args,
                        new Pm2Result(false, -1, string.Empty, $"pm2 timed out after {timeout.TotalSeconds:0}s"),
                        clock);
                }

                return LogCommand(
                    args,
                    new Pm2Result(
                        process.ExitCode == 0,
                        process.ExitCode,
                        (await stdout.ConfigureAwait(false)).Trim(),
                        (await stderr.ConfigureAwait(false)).Trim()),
                    clock);
            }
            catch (Exception ex)
            {
                return LogCommand(args, new Pm2Result(false, -1, string.Empty, $"{ex.GetType().Name}: {ex.Message}"), clock);
            }
        }

        private static Pm2Result RunSync(IReadOnlyList<string> args, TimeSpan timeout)
        {
            string? node = RuntimeProbe.LocateNode();

            if (node is null)
            {
                return new Pm2Result(false, -1, string.Empty, "node not found");
            }

            ProcessStartInfo startInfo = CreateStartInfo(node, RuntimeProbe.LocateGit());
            startInfo.ArgumentList.Add(CliPath);

            foreach (string arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            var clock = Stopwatch.StartNew();

            try
            {
                using Process? process = Process.Start(startInfo);

                if (process is null)
                {
                    return LogCommand(args, new Pm2Result(false, -1, string.Empty, "cannot start pm2"), clock);
                }

                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit((int)timeout.TotalMilliseconds))
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception)
                    {
                        // 已经退了
                    }

                    return LogCommand(
                        args,
                        new Pm2Result(false, -1, string.Empty, $"pm2 timed out after {timeout.TotalSeconds:0}s"),
                        clock);
                }

                return LogCommand(
                    args,
                    new Pm2Result(
                        process.ExitCode == 0,
                        process.ExitCode,
                        stdout.GetAwaiter().GetResult().Trim(),
                        stderr.GetAwaiter().GetResult().Trim()),
                    clock);
            }
            catch (Exception ex)
            {
                return LogCommand(args, new Pm2Result(false, -1, string.Empty, $"{ex.GetType().Name}: {ex.Message}"), clock);
            }
        }

        /// <summary>
        /// 每条 pm2 命令记一行到「PM2」页签：命令、退出码、耗时，失败时带上原因。
        ///
        /// <c>jlist</c> 例外：启动等待期间每 1.5 秒查一次，记下来只会把真信息淹掉。
        /// </summary>
        private static Pm2Result LogCommand(IReadOnlyList<string> args, Pm2Result result, Stopwatch clock)
        {
            clock.Stop();

            if (args.Count == 0
                || string.Equals(args[0], "jlist", StringComparison.OrdinalIgnoreCase))
            {
                return result;
            }

            string command = "pm2 " + string.Join(' ', args);

            RuntimeLog.Pm2(result.Ok
                ? $"{command} → exit {result.ExitCode} ({clock.ElapsedMilliseconds} ms)"
                : $"{command} → exit {result.ExitCode} ({clock.ElapsedMilliseconds} ms)：{result.Detail}");

            return result;
        }

        /// <summary>pm2 的 Node 入口脚本：<c>&lt;InstallRoot&gt;\node_modules\pm2\bin\pm2</c>（无扩展名）。</summary>
        private static string CliPath => Path.Combine(Pm2Installer.InstallRoot, "node_modules", "pm2", "bin", "pm2");
    }
}
