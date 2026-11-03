using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace AstraBrew_Launcher.Services
{
    /// <summary>酒馆子进程的运行状态。</summary>
    public enum TavernRunState
    {
        NotStarted,
        Starting,
        Running,
        Stopping,
        Stopped,
        Failed,
    }

    /// <summary>控制台一行日志的级别；决定等宽行里那个标签与颜色。</summary>
    public enum TavernLogKind
    {
        System,
        Info,
        Success,
        Warning,
        Error,
        Output,
    }

    public sealed record TavernLogEntry(DateTime Time, TavernLogKind Kind, string Text);

    /// <summary>运行时的对外快照，控制台与仪表盘读同一份。</summary>
    public sealed record TavernProcessState(
        TavernRunState State,
        int? Pid,
        int Port,
        string Url,
        string Mode,
        string VersionLabel,
        string? Error,
        DateTime? StartedAt,
        double? StartupSeconds)
    {
        public static TavernProcessState None { get; } = new(
            TavernRunState.NotStarted,
            null,
            0,
            string.Empty,
            "—",
            string.Empty,
            null,
            null,
            null);

        public bool IsRunning => State == TavernRunState.Running;

        public bool IsBusy => State is TavernRunState.Starting or TavernRunState.Stopping;

        public bool HasProcess => Pid is not null;
    }

    /// <summary>
    /// 酒馆子进程的真实生命周期：环境检查、启动、停止、日志采集。
    ///
    /// 为什么状态挂在服务层而不是页面上：用户点完启动很可能立刻翻去看别的页面，
    /// 挂在页面上的状态一卸载就没了，回来只能看到一个「未启动」——而进程其实还在跑。
    /// 这里让进程活在静态状态里，控制台只是它的一个观察窗口：切走再回来，
    /// 历史日志与实时状态都还在；仪表盘的运行徽标读的也是同一份。
    ///
    /// 启动参数照旧版 <c>core/tavern_process.rs</c>：
    /// <c>node server.js</c>，全局数据模式再补 <c>--configPath / --dataRoot</c>，
    /// 桌面模式补 <c>--browserLaunchEnabled false</c>（浏览器由启动器自己开），
    /// 有代理时补 <c>--requestProxy*</c> 并注入 <c>HTTP(S)_PROXY</c>。
    ///
    /// 线程约定：公开方法都在 UI 线程调用；<see cref="Changed"/> 与 <see cref="Logged"/>
    /// 一律在 UI 线程触发，订阅者可以直接改控件。
    /// </summary>
    public static class TavernRuntime
    {
        /// <summary>日志上限：再多也不会有人往上翻，留着只是占内存。</summary>
        private const int MaxLogLines = 2000;

        /// <summary>启动后等端口就绪的上限；超了不判失败，只提醒去看输出。</summary>
        private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(90);

        /// <summary>正常停止时等进程自己退出的时间，超了才升级成强制结束。</summary>
        private static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(3);

        /// <summary>ANSI 颜色码：Node 侧的日志默认带 chalk 着色，进界面就是一堆方块。</summary>
        private static readonly Regex AnsiPattern = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

        private static readonly object Gate = new();
        private static readonly List<TavernLogEntry> _logs = new();

        static TavernRuntime()
        {
            // 一进来就有一行说明：空白日志区会让人以为「输出没接上」
            _logs.Add(new TavernLogEntry(DateTime.Now, TavernLogKind.System, "控制台就绪，等待启动指令"));
        }

        private static Process? _process;
        private static DispatcherQueue? _queue;
        private static CancellationTokenSource? _readyWatch;
        private static TavernProcessState _state = TavernProcessState.None;

        /// <summary>本次停止是用户要求的（停止 / 重启 / 退出）：进程退出不该再报「异常」。</summary>
        private static bool _stopRequested;

        /// <summary>运行状态变化，始终在 UI 线程触发。</summary>
        public static event EventHandler? Changed;

        /// <summary>新增一行日志，始终在 UI 线程触发。</summary>
        public static event EventHandler<TavernLogEntry>? Logged;

        public static TavernProcessState State => _state;

        /// <summary>本次会话的全部日志（最多 <see cref="MaxLogLines"/> 行）。</summary>
        public static IReadOnlyList<TavernLogEntry> Logs => _logs;

        public static bool IsRunning => _state.IsRunning;

        /// <summary>
        /// 当前持有子进程时把它交出去：仪表盘要采样 CPU / 内存，得拿到 <see cref="Process"/>。
        /// 只读，别在调用方 Dispose 或杀进程——生命周期归这里。
        /// </summary>
        public static Process? LiveProcess
        {
            get
            {
                lock (Gate)
                {
                    return _process;
                }
            }
        }

        /// <summary>
        /// 当前生效的端口。
        ///
        /// 只有「正在启动 / 正在运行」才用快照里的端口——那是进程真正监听的；
        /// 停下之后必须回读配置文件，否则用户在配置页把端口改成 8080，
        /// 仪表盘还会一直显示上次跑的 11451。停止时快照里的 Port 不清空，
        /// 所以要靠状态判断，不能只看 Port 有没有值。
        /// </summary>
        public static int CurrentPort => _state.State is TavernRunState.Starting or TavernRunState.Running
            && _state.Port > 0
                ? _state.Port
                : ReadPort(ResolveConfigPath());

        /// <summary>当前生效的配置文件路径（全局模式是 <c>data/sillytavern/config.yaml</c>）。</summary>
        public static string ResolveConfigPath()
        {
            TavernInstance instance = TavernInstances.Current;

            return LauncherSettings.DataLocation == DataLocationMode.Global
                ? GlobalTavernConfig.ActivePath
                : Path.Combine(instance.Path, "config.yaml");
        }

        /// <summary>
        /// 当前生效的数据目录：角色卡、扩展、聊天记录都在这下面。
        /// 全局模式是配置文件所在的那层（酒馆的 <c>--dataRoot</c>），实例模式是实例目录。
        /// </summary>
        public static string ResolveDataRoot()
        {
            TavernInstance instance = TavernInstances.Current;

            return LauncherSettings.DataLocation == DataLocationMode.Global
                ? Path.GetDirectoryName(GlobalTavernConfig.ActivePath) ?? AppPaths.RoamingRoot
                : instance.Path;
        }

        /// <summary>
        /// 酒馆真正读写的 user 数据层：<c>default-user</c>、<c>settings.json</c>、角色卡、扩展都在这一层下面。
        ///
        /// 与 <see cref="ResolveDataRoot"/> 的差别在实例模式：那个方法返回的是代码目录，
        /// 而酒馆没传 <c>--dataRoot</c> 时用的是「代码目录下 data」。
        ///
        /// 还有一层现实：刚把数据位置从实例切到全局时，全局目录里只有一个空的
        /// <c>_storage</c>，全部资源仍在实例的 <c>data</c> 下。拿「有没有 default-user」
        /// 当判据，没有就回退另一处——否则资源页会突然变空，看起来像数据被删了。
        /// </summary>
        public static string ResolveUserRoot()
        {
            bool global = LauncherSettings.DataLocation == DataLocationMode.Global;

            string preferred = global
                ? Path.GetDirectoryName(GlobalTavernConfig.ActivePath) ?? AppPaths.RoamingRoot
                : Path.Combine(TavernInstances.Current.Path, "data");

            string alternate = global
                ? Path.Combine(TavernInstances.Current.Path, "data")
                : Path.GetDirectoryName(GlobalTavernConfig.ActivePath) ?? AppPaths.RoamingRoot;

            if (HasUserLayer(preferred))
            {
                return preferred;
            }

            return HasUserLayer(alternate) ? alternate : preferred;
        }

        /// <summary>
        /// 这一层算不算「被酒馆用过」：有 <c>default-user</c>，或者干脆就有一只 settings.json。
        /// 只看目录存不存在不够——酒馆建了空壳目录就走了，里面什么都没有。
        /// </summary>
        private static bool HasUserLayer(string root)
        {
            try
            {
                return Directory.Exists(Path.Combine(root, "default-user"))
                    || File.Exists(Path.Combine(root, "settings.json"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>user 数据层里的 <c>default-user</c>；酒馆的多用户布局里它就是主用户目录。</summary>
        public static string ResolveDefaultUser() => Path.Combine(ResolveUserRoot(), "default-user");

        // ==================== 启动 ====================

        /// <summary>
        /// 启动酒馆。已经在跑或正在启停时直接返回——重复点「启动」不该起第二个 node，
        /// 那会立刻因为端口被自己占住而失败，看起来像是启动坏了。
        /// </summary>
        public static async Task StartAsync()
        {
            lock (Gate)
            {
                if (_state.State is TavernRunState.Starting or TavernRunState.Running or TavernRunState.Stopping)
                {
                    return;
                }

                _queue ??= DispatcherQueue.GetForCurrentThread();
                _stopRequested = false;
                _state = _state with
                {
                    State = TavernRunState.Starting,
                    Error = null,
                    StartedAt = null,
                };
            }

            Raise();

            try
            {
                await Task.Run(StartCoreAsync).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Fail("启动时发生意外：" + ex.Message);
            }
        }

        private static async Task StartCoreAsync()
        {
            var clock = Stopwatch.StartNew();
            bool builtin = LauncherSettings.Runtime == RuntimeSource.Builtin;
            string envLabel = builtin ? "内置环境" : "系统环境";

            TavernInstance instance = TavernInstances.Current;
            AddLog(TavernLogKind.System, $"正在启动酒馆 · {envLabel} · {instance.DisplayLabel}");

            // ---------- 1. Node.js ----------
            string? node = RuntimeProbe.LocateNode(LauncherSettings.Runtime);
            if (node is null)
            {
                Fail(
                    builtin
                        ? "内置环境里没有找到 Node.js。请到「设置 → 运行环境」点「修复」，或把环境来源切换成系统环境。"
                        : "系统环境里没有找到 Node.js。请先安装 Node.js，或在设置里改用内置环境。");
                return;
            }

            string? nodeVersion = await RunVersionAsync(node, "--version").ConfigureAwait(false);
            if (nodeVersion is null)
            {
                Fail($"Node.js 在 {node}，但查询版本失败——文件可能已损坏。");
                return;
            }

            AddLog(TavernLogKind.Success, $"Node.js {nodeVersion} · {node}");

            if (RuntimeProbe.IsNodeOutdated(nodeVersion))
            {
                AddLog(
                    TavernLogKind.Warning,
                    $"Node.js 版本低于酒馆要求的 v{RuntimeProbe.MinimumNodeMajor}，可能启动失败。");
            }

            // ---------- 2. Git ----------
            // Git 不是启动的硬条件（酒馆本体不依赖它），但装扩展要用；
            // 缺了照样起，只是把后果说清楚，别让人以为一切正常。
            string? git = RuntimeProbe.LocateGit(LauncherSettings.Runtime);
            if (git is null)
            {
                AddLog(
                    TavernLogKind.Warning,
                    builtin
                        ? "没有找到 Git：酒馆能启动，但「安装扩展」会失败。可在「设置 → 运行环境」里补装。"
                        : "没有找到 Git：酒馆能启动，但「安装扩展」会失败。请在系统里装 Git，或改用内置环境。");
            }
            else
            {
                string? gitVersion = await RunVersionAsync(git, "--version").ConfigureAwait(false);

                // git --version 自己就带前缀（git version 2.55.0.windows.2），
                // 直接拼「Git 」会变成「Git git version …」
                if (gitVersion is not null)
                {
                    gitVersion = gitVersion.Replace("git version ", string.Empty, StringComparison.OrdinalIgnoreCase);
                }

                AddLog(
                    TavernLogKind.Success,
                    $"Git {gitVersion ?? "版本未知"} · {git}");
            }

            // ---------- 3. 实例与依赖 ----------
            if (!Directory.Exists(instance.Path))
            {
                Fail($"实例目录不存在：{instance.Path}");
                return;
            }

            string server = Path.Combine(instance.Path, "server.js");
            if (!File.Exists(server))
            {
                Fail($"{instance.Path} 里没有 server.js，这不像一份完整的酒馆代码。");
                return;
            }

            if (!instance.DepsReady)
            {
                Fail("这份实例缺少 Node 依赖（node_modules）。到「版本管理 → 本地实例」点「安装依赖」后再启动。");
                return;
            }

            AddLog(TavernLogKind.Info, $"实例目录 {instance.Path}");

            // ---------- 4. 配置与数据目录 ----------
            (string configPath, string? dataRoot) = ResolveData(instance);
            int port = ReadPort(configPath);

            AddLog(TavernLogKind.Info, $"配置 {configPath}");
            if (dataRoot is { Length: > 0 })
            {
                AddLog(TavernLogKind.Info, $"数据目录 {dataRoot}");

                // 旧配置里白名单写着 localhost：酒馆会忽略它，浏览器按 IPv6 回环打开就被挡。
                // 起服务前顺手修正，比让用户对着「无法访问」自己猜要好。
                if (GlobalTavernConfig.NormalizeWhitelist())
                {
                    AddLog(
                        TavernLogKind.Warning,
                        "白名单里的 localhost 不是合法 IP（酒馆只认 IP/CIDR），已换成 ::1 并补齐回环地址。");
                }
            }

            // ---------- 5. 端口 ----------
            List<PortOwner> owners = FindPortOwners(port);
            if (owners.Count > 0)
            {
                string detail = string.Join("、", owners.Select(item => $"PID {item.Pid} {item.Name}"));
                AddLog(TavernLogKind.Error, $"端口 {port} 已被占用：{detail}");

                lock (Gate)
                {
                    _state = _state with { State = TavernRunState.Failed, Port = port, Error = $"端口 {port} 被占用" };
                }

                Raise();
                AppNotify.Error(
                    "端口被占用，没能启动",
                    $"端口 {port} 正被 {detail} 使用。可以关掉那个进程，或点这里强制结束它再启动。",
                    "强制释放并启动",
                    () => _ = ReleasePortAndStartAsync(owners, port));
                return;
            }

            // ---------- 6. 组命令 ----------
            var arguments = new List<string> { "server.js" };

            if (dataRoot is { Length: > 0 })
            {
                arguments.Add("--configPath");
                arguments.Add(configPath);
                arguments.Add("--dataRoot");
                arguments.Add(dataRoot);
            }

            // 桌面 / 服务器模式都不让酒馆自己开浏览器：
            // 桌面模式由启动器开窗口，服务器模式本来就不该在本机弹页面
            if (TavernSettings.Mode != LaunchMode.Normal)
            {
                arguments.Add("--browserLaunchEnabled");
                arguments.Add("false");
            }

            string? proxy = ResolveProxy();
            if (proxy is not null)
            {
                arguments.Add("--requestProxyEnabled");
                arguments.Add("true");
                arguments.Add("--requestProxyUrl");
                arguments.Add(proxy);
                arguments.Add("--requestProxyBypass");
                arguments.Add("localhost 127.0.0.1 ::1");
            }

            if (LauncherSettings.ShowStartupCommand)
            {
                AddLog(TavernLogKind.System, "$ " + BuildCommandText(node, arguments));
            }

            // ---------- 7. 起进程 ----------
            var startInfo = new ProcessStartInfo(node)
            {
                WorkingDirectory = instance.Path,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };

            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            // 内置 node 不在 PATH 里：不显式前置，酒馆自己的子进程（npm、扩展脚本）会找不到 node
            startInfo.Environment["PATH"] = BuildPath(node, git);

            if (proxy is not null)
            {
                foreach (string key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy" })
                {
                    startInfo.Environment[key] = proxy;
                }
            }

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true,
            };

            process.OutputDataReceived += (_, e) => OnOutput(e.Data);
            process.ErrorDataReceived += (_, e) => OnOutput(e.Data);
            process.Exited += (_, _) => OnExited(process);

            if (!process.Start())
            {
                process.Dispose();
                Fail("无法启动 Node.js 进程。");
                return;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var watch = new CancellationTokenSource();

            lock (Gate)
            {
                _readyWatch?.Dispose();
                _readyWatch = watch;
                _process = process;
                _state = _state with
                {
                    State = TavernRunState.Starting,
                    Pid = process.Id,
                    Port = port,
                    Url = $"http://127.0.0.1:{port}",
                    Mode = $"Direct · {envLabel}",
                    VersionLabel = instance.KernelLabel,
                };
            }

            Raise();
            AddLog(TavernLogKind.Info, $"进程已启动（PID {process.Id}），等待端口 {port} 就绪…");

            await WaitReadyAsync(process, port, clock, watch.Token).ConfigureAwait(false);
        }

        /// <summary>轮询端口，通了就算启动完成。子进程一句「listening」都不打的时候也照样成立。</summary>
        private static async Task WaitReadyAsync(Process process, int port, Stopwatch clock, CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow + ReadyTimeout;
            bool warned = false;

            while (!token.IsCancellationRequested)
            {
                if (SafeHasExited(process))
                {
                    // 退出的说明由 Exited 事件负责，这里直接收工
                    return;
                }

                if (IsPortListening(port))
                {
                    Post(() => MarkRunning(port, clock.Elapsed));
                    return;
                }

                if (!warned && DateTime.UtcNow > deadline)
                {
                    warned = true;
                    AddLog(
                        TavernLogKind.Warning,
                        $"启动超过 {ReadyTimeout.TotalSeconds:0} 秒仍未监听端口 {port}，请查看上方输出。");
                }

                try
                {
                    await Task.Delay(400, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private static void MarkRunning(int port, TimeSpan elapsed)
        {
            lock (Gate)
            {
                if (_state.State is not (TavernRunState.Starting or TavernRunState.Running))
                {
                    return;
                }

                _state = _state with
                {
                    State = TavernRunState.Running,
                    Port = port,
                    Url = $"http://127.0.0.1:{port}",
                    StartedAt = _state.StartedAt ?? DateTime.Now,
                    StartupSeconds = elapsed.TotalSeconds,
                };
            }

            Raise();
            AddLog(TavernLogKind.Success, $"启动完成，用时 {elapsed.TotalSeconds:0.0} 秒 · http://127.0.0.1:{port}");
            AppNotify.Success(
                "酒馆已启动",
                $"{_state.VersionLabel} · http://127.0.0.1:{port}。运行日志可在控制台查看。");
        }

        // ==================== 停止 ====================

        /// <summary>
        /// 停止酒馆。<paramref name="force"/> 为 false 时先请求进程树自己退出（taskkill /T），
        /// 超时才升级成 /F：Node 通常不响应优雅关闭，硬杀是常态而不是异常。
        /// </summary>
        public static async Task StopAsync(bool force = false)
        {
            Process? process;
            int pid;

            lock (Gate)
            {
                process = _process;

                if (process is null || _state.State is TavernRunState.Stopping)
                {
                    return;
                }

                if (_state.Pid is not { } value)
                {
                    return;
                }

                pid = value;
                _stopRequested = true;
                _state = _state with { State = TavernRunState.Stopping };

                // 就绪轮询停掉：进程没了还继续探端口只会把「已停止」又改回运行中
                _readyWatch?.Cancel();
            }

            Raise();
            AddLog(TavernLogKind.Info, force ? "正在强制结束进程树…" : $"正在停止服务（PID {pid}）…");

            if (force)
            {
                KillTree(pid, force: true);
                await WaitExitAsync(process, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            else if (!TryKillTree(pid, force: false))
            {
                // 控制台程序没有窗口，taskkill 不带 /F 会直接报「无法终止」：
                // 那就别再空等一轮超时，立刻升级成强制结束
                AddLog(TavernLogKind.Info, "进程不响应关闭请求（控制台程序通常如此），改为强制结束。");
                KillTree(pid, force: true);
                await WaitExitAsync(process, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            else
            {
                await WaitExitAsync(process, GracefulStopTimeout).ConfigureAwait(false);

                if (!SafeHasExited(process))
                {
                    AddLog(TavernLogKind.Warning, $"进程没有在 {GracefulStopTimeout.TotalSeconds:0} 秒内退出，改为强制结束。");
                    KillTree(pid, force: true);
                    await WaitExitAsync(process, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
            }

            // Exited 事件有时比这里晚到：补一次收尾，别让界面停在「停止中」
            Post(() =>
            {
                if (_state.State == TavernRunState.Stopping)
                {
                    FinishStopped();
                }
            });
        }

        /// <summary>重启：先停干净再启动。停止没完成就启动只会撞端口。</summary>
        public static async Task RestartAsync()
        {
            await StopAsync().ConfigureAwait(false);

            if (_process is { } process)
            {
                await WaitExitAsync(process, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }

            await StartAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// 退出启动器时调用：同步结束进程树，不等 Exited 事件。
        /// 窗口一关事件循环就没了，异步收尾根本跑不完，进程会变成孤儿继续占着端口。
        /// </summary>
        public static void Shutdown()
        {
            int? pid;
            lock (Gate)
            {
                pid = _state.Pid;
                _stopRequested = true;
                _readyWatch?.Cancel();
            }

            if (pid is { } value)
            {
                KillTree(value, force: true);
            }
        }

        private static async Task WaitExitAsync(Process process, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                if (SafeHasExited(process))
                {
                    return;
                }

                await Task.Delay(100).ConfigureAwait(false);
            }
        }

        private static void KillTree(int pid, bool force) => TryKillTree(pid, force);

        /// <summary>结束进程树；返回 taskkill 是否成功（进程已退、或者本来就没了）。</summary>
        private static bool TryKillTree(int pid, bool force)
        {
            try
            {
                var startInfo = new ProcessStartInfo("taskkill")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                if (force)
                {
                    startInfo.ArgumentList.Add("/F");
                }

                startInfo.ArgumentList.Add("/T");
                startInfo.ArgumentList.Add("/PID");
                startInfo.ArgumentList.Add(pid.ToString());

                using Process? killer = Process.Start(startInfo);
                if (killer is null)
                {
                    return false;
                }

                killer.WaitForExit(4000);
                return killer.HasExited && killer.ExitCode == 0;
            }
            catch
            {
                // 进程可能已经自己退了；杀不掉也没有更好的办法，交给后面的超时收尾
                return false;
            }
        }

        // ==================== 进程事件 ====================

        private static void OnOutput(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            string line = AnsiPattern.Replace(raw, string.Empty).TrimEnd();

            // 酒馆自己的输出按「有内容的行」入日志；错误级别靠关键字粗判，
            // 这里不做颜色回放——终端里的色块进界面只会变成噪声
            TavernLogKind kind = line.Contains("error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("ERR!", StringComparison.Ordinal)
                ? TavernLogKind.Error
                : TavernLogKind.Output;

            AddLog(kind, line);
        }

        private static void OnExited(Process process)
        {
            int code;
            try
            {
                code = process.ExitCode;
            }
            catch
            {
                code = -1;
            }

            bool intentional;
            lock (Gate)
            {
                intentional = _stopRequested;

                if (ReferenceEquals(_process, process))
                {
                    _process = null;
                }

                _readyWatch?.Cancel();
            }

            process.Dispose();

            Post(() =>
            {
                if (intentional || code == 0)
                {
                    FinishStopped();
                    AddLog(TavernLogKind.Success, intentional ? "服务已停止，端口已释放" : "酒馆进程已退出（代码 0）");
                    return;
                }

                lock (Gate)
                {
                    _state = _state with
                    {
                        State = TavernRunState.Failed,
                        Pid = null,
                        Error = $"进程退出（代码 {code}）",
                        StartedAt = null,
                    };
                }

                Raise();
                AddLog(TavernLogKind.Error, $"酒馆进程异常退出（代码 {code}），向上翻看最后几行输出。");
                AppNotify.Error(
                    "酒馆异常退出",
                    $"进程以代码 {code} 结束。控制台里有完整输出，常见原因是端口冲突或依赖缺失。");
            });
        }

        private static void FinishStopped()
        {
            lock (Gate)
            {
                _stopRequested = false;
                _state = _state with
                {
                    State = TavernRunState.Stopped,
                    Pid = null,
                    Url = string.Empty,
                    Error = null,
                    StartedAt = null,
                };
            }

            Raise();
        }

        private static void Fail(string message)
        {
            lock (Gate)
            {
                _queue ??= DispatcherQueue.GetForCurrentThread();
                _state = _state with
                {
                    State = TavernRunState.Failed,
                    Pid = null,
                    Error = message,
                    StartedAt = null,
                };
            }

            Raise();
            AddLog(TavernLogKind.Error, message);
            AppNotify.Error("酒馆没能启动", message);
        }

        // ==================== 端口 ====================

        private sealed record PortOwner(int Pid, string Name);

        private static async Task ReleasePortAndStartAsync(List<PortOwner> owners, int port)
        {
            foreach (PortOwner owner in owners)
            {
                AddLog(TavernLogKind.Warning, $"强制结束占用 {port} 的进程 PID {owner.Pid}（{owner.Name}）");
                KillTree(owner.Pid, force: true);
            }

            await Task.Delay(600).ConfigureAwait(false);
            await StartAsync().ConfigureAwait(false);
        }

        private static List<PortOwner> FindPortOwners(int port)
        {
            var owners = new List<PortOwner>();

            try
            {
                var startInfo = new ProcessStartInfo("netstat")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };

                startInfo.ArgumentList.Add("-ano");
                startInfo.ArgumentList.Add("-p");
                startInfo.ArgumentList.Add("TCP");

                using Process? process = Process.Start(startInfo);
                if (process is null)
                {
                    return owners;
                }

                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(4000);

                var seen = new HashSet<int>();

                foreach (string raw in output.Split('\n'))
                {
                    string line = raw.Trim();
                    if (!line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string[] columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (columns.Length < 5)
                    {
                        continue;
                    }

                    // 本地地址形如 0.0.0.0:11451 / [::]:11451，只认端口完全相同的
                    string local = columns[1];
                    int colon = local.LastIndexOf(':');
                    if (colon < 0
                        || !int.TryParse(local[(colon + 1)..], out int localPort)
                        || localPort != port)
                    {
                        continue;
                    }

                    if (!int.TryParse(columns[^1], out int pid) || pid <= 0 || !seen.Add(pid))
                    {
                        continue;
                    }

                    string name = "未知进程";
                    try
                    {
                        name = Process.GetProcessById(pid).ProcessName + ".exe";
                    }
                    catch
                    {
                        // 进程刚退出，或者没权限查名字：保留 PID 也够用户判断了
                    }

                    owners.Add(new PortOwner(pid, name));
                }
            }
            catch
            {
                // netstat 不可用就当没冲突，交给启动自己去撞
            }

            return owners;
        }

        /// <summary>端口上有没有人监听。用连一下的方式判断，比查表可靠（也不依赖 netstat）。</summary>
        private static bool IsPortListening(int port)
        {
            try
            {
                using var client = new TcpClient();
                Task connect = client.ConnectAsync(IPAddress.Loopback, port);

                if (!connect.Wait(400))
                {
                    return false;
                }

                return client.Connected;
            }
            catch
            {
                return false;
            }
        }

        // ==================== 配置 / 命令 ====================

        /// <summary>
        /// 生效的配置文件与数据目录。
        /// 全局模式用 <c>data/sillytavern/config.yaml</c> 与它所在的目录（酒馆的 user 数据也落在这里）；
        /// 实例模式不传参数，让酒馆按 cwd 读自己的 config.yaml。
        /// </summary>
        private static (string ConfigPath, string? DataRoot) ResolveData(TavernInstance instance)
        {
            if (LauncherSettings.DataLocation == DataLocationMode.Global)
            {
                string config = GlobalTavernConfig.ActivePath;
                return (config, Path.GetDirectoryName(config) ?? AppPaths.RoamingRoot);
            }

            return (Path.Combine(instance.Path, "config.yaml"), null);
        }

        /// <summary>从配置里读端口；读不到（没配置文件、字段不在）就用启动器的默认端口。</summary>
        public static int ReadPort(string configPath)
        {
            try
            {
                if (!File.Exists(configPath))
                {
                    return GlobalTavernConfig.Port;
                }

                string text = File.ReadAllText(configPath, Encoding.UTF8);
                if (YamlConfig.ReadScalars(text).TryGetValue("port", out string? value)
                    && int.TryParse(value, out int port)
                    && port is > 0 and < 65536)
                {
                    return port;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 读不了配置不是致命问题：用默认端口先把服务起起来
            }

            return GlobalTavernConfig.Port;
        }

        /// <summary>
        /// 要注入给 Node 的代理地址：自定义优先；系统模式读 Windows 的 WinINET 设置。
        /// 这两条都不成立就返回 null——不要凭空给一个可能连不上的代理。
        /// </summary>
        private static string? ResolveProxy()
        {
            if (LauncherSettings.Proxy == ProxyModeSetting.Custom)
            {
                string custom = LauncherSettings.CustomProxy.Trim();
                return custom.Length > 0 ? custom : null;
            }

            if (LauncherSettings.Proxy != ProxyModeSetting.System)
            {
                return null;
            }

            // 与设置页显示的是同一份判断（SystemProxy 只读一处注册表）。
            // PAC 脚本与「自动检测」拿不到固定地址，这里返回 null，交给系统栈自己处理。
            return SystemProxy.ResolveEffectiveUrl();
        }

        /// <summary>回显用的命令行。参数里有空格就加引号，粘到终端里能直接跑。</summary>
        private static string BuildCommandText(string executable, IEnumerable<string> arguments)
        {
            var parts = new List<string> { Quote(executable) };
            parts.AddRange(arguments.Select(Quote));
            return string.Join(' ', parts);

            static string Quote(string value) =>
                value.Contains(' ') ? "\"" + value + "\"" : value;
        }

        /// <summary>node 目录与 git 目录前置到 PATH：酒馆的子进程（npm、扩展脚本）要靠它找工具。</summary>
        private static string BuildPath(string node, string? git)
        {
            var parts = new List<string>();

            if (Path.GetDirectoryName(node) is { Length: > 0 } nodeDir)
            {
                parts.Add(nodeDir);
            }

            if (git is not null)
            {
                if (Path.GetDirectoryName(git) is { Length: > 0 } gitDir)
                {
                    parts.Add(gitDir);

                    // MinGit 的 git.exe 在 cmd 下，但它调用的工具在 bin、mingw64\bin
                    string root = Path.GetDirectoryName(gitDir) ?? gitDir;
                    parts.Add(Path.Combine(root, "bin"));
                    parts.Add(Path.Combine(root, "mingw64", "bin"));
                }
            }

            string? existing = Environment.GetEnvironmentVariable("PATH");
            if (existing is { Length: > 0 })
            {
                parts.Add(existing);
            }

            return string.Join(Path.PathSeparator, parts);
        }

        private static Task<string?> RunVersionAsync(string executable, string arguments)
        {
            return Task.Run(() =>
            {
                try
                {
                    var startInfo = new ProcessStartInfo(executable, arguments)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8,
                    };

                    using Process? process = Process.Start(startInfo);
                    if (process is null)
                    {
                        return null;
                    }

                    Task<string> output = process.StandardOutput.ReadToEndAsync();
                    _ = process.StandardError.ReadToEndAsync();

                    if (!process.WaitForExit(5000))
                    {
                        try
                        {
                            process.Kill(entireProcessTree: true);
                        }
                        catch
                        {
                            // 已经自己退了
                        }

                        return null;
                    }

                    string text = output.GetAwaiter().GetResult().Trim();
                    if (text.Length == 0)
                    {
                        return null;
                    }

                    // node 输出「v24.14.0」，git 输出「git version 2.55.0.windows.2」：
                    // 保留工具自己的写法，只取第一行
                    int breakAt = text.IndexOfAny(new[] { '\r', '\n' });
                    return breakAt > 0 ? text[..breakAt].Trim() : text;
                }
                catch
                {
                    return null;
                }
            });
        }

        private static bool SafeHasExited(Process process)
        {
            try
            {
                return process.HasExited;
            }
            catch
            {
                return true;
            }
        }

        // ==================== 日志 ====================

        /// <summary>清空本次会话日志（不动进程）。</summary>
        public static void ClearLogs()
        {
            _logs.Clear();
            Raise();
            AddLog(TavernLogKind.System, "已清空本次会话日志");
        }

        private static void AddLog(TavernLogKind kind, string text)
        {
            var entry = new TavernLogEntry(DateTime.Now, kind, text);

            Post(() =>
            {
                _logs.Add(entry);

                if (_logs.Count > MaxLogLines)
                {
                    _logs.RemoveRange(0, _logs.Count - MaxLogLines);
                }

                Logged?.Invoke(null, entry);
            });
        }

        private static void Post(Action action)
        {
            DispatcherQueue? queue = _queue;

            if (queue is null || queue.HasThreadAccess)
            {
                action();
                return;
            }

            queue.TryEnqueue(() => action());
        }

        /// <summary>
        /// 状态变化一律转投 UI 线程：启动 / 进程退出都发生在别的线程上，
        /// 直接 Invoke 就是跨线程改控件（会抛 COMException，而且抛在事件里没人接得住，
        /// 表现成「状态一直停在启动中、PID 永远是 —」）。
        /// </summary>
        private static void Raise() => Post(() => Changed?.Invoke(null, EventArgs.Empty));
    }
}
