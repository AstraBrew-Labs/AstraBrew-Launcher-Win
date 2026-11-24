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

    /// <summary>
    /// 控制台里的一行。
    ///
    /// <para>
    /// 启动器自己写的日志**不存成品文字**，只存资源键与实参（<see cref="Key"/> / <see cref="Args"/>），
    /// 显示时按当前界面语言现取。理由：日志是「记录」不是「快照」——同一份日志在切到英文后
    /// 应该整屏跟着变，而不是上面几行中文、下面几行英文。
    /// </para>
    /// <para>
    /// 酒馆进程自己的 stdout 没有键可存（那是 Node 打印的），走 <see cref="Text"/> 原样显示。
    /// </para>
    /// </summary>
    public sealed record TavernLogEntry(DateTime Time, TavernLogKind Kind, string Text)
    {
        /// <summary>资源键；为空表示这行是外部输出，<see cref="Text"/> 就是最终文字。</summary>
        public string? Key { get; init; }

        /// <summary>与 <see cref="Key"/> 配套的占位符实参。</summary>
        public object?[]? Args { get; init; }

        /// <summary>界面上该显示的文字：有键就按当前语言取，没有就原样。</summary>
        public string Display => Key is { Length: > 0 } key
            ? Args is { Length: > 0 } args ? L.F(key, args) : L.Get(key)
            : Text;
    }

    /// <summary>运行时的对外快照，控制台与仪表盘读同一份。</summary>
    public sealed record TavernProcessState(
        TavernRunState State,
        int? Pid,
        int Port,
        string Url,
        string VersionLabel,
        string? Error,
        DateTime? StartedAt,
        double? StartupSeconds)
    {
        /// <summary>
        /// 启动模式与运行环境的**资源键**（不是成品文字）。
        /// 控制台状态条要在显示时按当前语言拼，换语言才会跟着变。
        /// </summary>
        public string ModeKey { get; init; } = string.Empty;

        /// <summary>运行环境（内置 / 系统）的资源键，与 <see cref="ModeKey"/> 配套。</summary>
        public string EnvKey { get; init; } = string.Empty;

        /// <summary>
        /// 这次是不是 PM2 在管。关启动器要不要保活、停止走哪条路，全看这一个字段。
        /// 勾了「继续运行」但 PM2 起不来时是 false（已回退直连），此时关窗照样会杀。
        /// </summary>
        public bool ManagedByPm2 { get; init; }

        public static TavernProcessState None { get; } = new(
            TavernRunState.NotStarted,
            null,
            0,
            string.Empty,
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
            _logs.Add(new TavernLogEntry(DateTime.Now, TavernLogKind.System, string.Empty) { Key = "Run.Log.Ready" });
        }

        private static Process? _process;
        private static DispatcherQueue? _queue;
        private static CancellationTokenSource? _readyWatch;
        private static TavernProcessState _state = TavernProcessState.None;

        /// <summary>本次停止是用户要求的（停止 / 重启 / 退出）：进程退出不该再报「异常」。</summary>
        private static bool _stopRequested;

        /// <summary>PM2 托管的 app 名；null 表示本次是直连启动。</summary>
        private static string? _pm2AppName;

        /// <summary>PM2 输出尾随器；只有托管时才有。</summary>
        private static Pm2LogTail? _tail;

        /// <summary>控制台存档是否已经载回内存：决定接管时还要不要从 PM2 兜底回放。</summary>
        private static bool _consoleHistoryLoaded;

        /// <summary>
        /// 本次启动的输出里出现过 ESM 路径格式错误。
        /// 这种退出和「酒馆代码坏了」不是一回事，报错要指到真正的原因上。
        /// </summary>
        private static bool _esmSchemeSeen;

        /// <summary>运行状态变化，始终在 UI 线程触发。</summary>
        public static event EventHandler? Changed;

        /// <summary>新增一行日志，始终在 UI 线程触发。</summary>
        public static event EventHandler<TavernLogEntry>? Logged;

        /// <summary>日志被整体清空（每次启动前都会清），界面要重建列表而不是追加一行。</summary>
        public static event EventHandler? Cleared;

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
            bool alreadyRunning;

            lock (Gate)
            {
                alreadyRunning = _state.State is TavernRunState.Starting or TavernRunState.Running or TavernRunState.Stopping;

                if (alreadyRunning)
                {
                    return;
                }

                ActivityLog.Record(
                    ActivityCategory.Service,
                    L.F("Activity.Service.Start", TavernSettings.ModeName));

                _queue ??= DispatcherQueue.GetForCurrentThread();
                _stopRequested = false;
                _esmSchemeSeen = false;
                _state = _state with
                {
                    State = TavernRunState.Starting,
                    Error = null,
                    StartedAt = null,
                };
            }

            // 先清掉上一次的日志再起：两次启动的输出接在一起，出错时根本分不清哪一行是这次的。
            // 必须在 StartCoreAsync 之前做——那边一进去就开始写日志了。
            ResetLogs();

            Raise();

            try
            {
                await Task.Run(StartCoreAsync).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Fail("Run.Log.Unexpected", ex.Message);
            }
        }

        private static async Task StartCoreAsync()
        {
            var clock = Stopwatch.StartNew();
            bool builtin = LauncherSettings.Runtime == RuntimeSource.Builtin;
            string envLabel = builtin ? L.Get("Run.Env.Builtin") : L.Get("Run.Env.System");

            TavernInstance instance = TavernInstances.Current;
            AddLogKey(TavernLogKind.System, "Run.Log.Starting", envLabel, instance.DisplayLabel);

            // ---------- 1. Node.js ----------
            string? node = RuntimeProbe.LocateNode(LauncherSettings.Runtime);
            if (node is null)
            {
                Fail(builtin ? "Run.NoNode.Builtin" : "Run.NoNode.System");
                return;
            }

            string? nodeVersion = await RunVersionAsync(node, "--version").ConfigureAwait(false);
            if (nodeVersion is null)
            {
                Fail("Run.Log.NodeBroken", node);
                return;
            }

            AddLogKey(TavernLogKind.Success, "Run.Log.Node", nodeVersion, node);

            if (RuntimeProbe.IsNodeOutdated(nodeVersion))
            {
                AddLogKey(TavernLogKind.Warning, "Run.Log.NodeOld", RuntimeProbe.MinimumNodeMajor);
            }

            // ---------- 2. Git ----------
            // Git 不是启动的硬条件（酒馆本体不依赖它），但装扩展要用；
            // 缺了照样起，只是把后果说清楚，别让人以为一切正常。
            string? git = RuntimeProbe.LocateGit(LauncherSettings.Runtime);
            if (git is null)
            {
                AddLogKey(
                    TavernLogKind.Warning,
                    builtin ? "Run.NoGit.Builtin" : "Run.NoGit.System");
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

                AddLogKey(
                    TavernLogKind.Success,
                    "Run.Log.Git",
                    gitVersion ?? L.Get("Cmp.Unknown"),
                    git);
            }

            // ---------- 3. 实例与依赖 ----------
            if (!Directory.Exists(instance.Path))
            {
                Fail("Run.Log.NoInstance", instance.Path);
                return;
            }

            string server = Path.Combine(instance.Path, "server.js");
            if (!File.Exists(server))
            {
                Fail("Run.Log.NoServer", instance.Path);
                return;
            }

            if (!instance.DepsReady)
            {
                Fail("Run.Log.NoDeps");
                return;
            }

            AddLogKey(TavernLogKind.Info, "Run.Log.InstanceDir", instance.Path);

            // ---------- 4. 配置与数据目录 ----------
            (string configPath, string? dataRoot) = ResolveData(instance);
            int port = ReadPort(configPath);

            AddLogKey(TavernLogKind.Info, "Run.Log.Config", configPath);
            if (dataRoot is { Length: > 0 })
            {
                AddLogKey(TavernLogKind.Info, "Run.Log.DataDir", dataRoot);

                // 白名单跟着服务范围走：局域网放行本机网段，互联网全开放。
                // 每次启动都对一遍，换过 Wi-Fi、改过服务范围之后不会留下过期的名单。
                //
                // 两张表一起写：whitelist 管「谁能连进来」，privateAddressWhitelist.allowedRanges
                // 管「酒馆能连向哪些私有地址」。只写前者时，listen 打开后酒馆会一直刷
                // 「请配置 privateAddressWhitelist」的警告，局域网里也连不通。
                IReadOnlyList<string> whitelistHosts = TavernSettings.WhitelistHosts;
                IReadOnlyList<string> privateRanges = TavernSettings.BuildPrivateRanges(whitelistHosts);
                bool whitelistWritten = GlobalTavernConfig.ApplyWhitelist(whitelistHosts, privateRanges);

                // 每次都打一行：白名单决定了谁能连上来，出问题时要能一眼看到当时放行了哪些
                AddLogKey(
                    TavernLogKind.Info,
                    whitelistWritten ? "Run.Log.WhitelistDone" : "Run.Log.Whitelist",
                    TavernSettings.ModeName,
                    string.Join(L.Get("Common.List.And"), whitelistHosts));

                // 另一张表单独打：它管的是出站，与「谁能连进来」是两件事，混在一行里看不清
                AddLogKey(
                    TavernLogKind.Info,
                    "Run.Log.PrivateRanges",
                    string.Join(L.Get("Common.List.And"), privateRanges));
            }

            // 后面所有 Raise/日志都要投递到 UI 线程。PM2 分支不经过 Process.Start 那条路，
            // 一旦 _queue 为 null，Post 会在尾随器的计时器线程直接执行 → 控制台动视觉树 → 异常被吞。
            _queue ??= DispatcherQueue.GetForCurrentThread();

            // ---------- 4.5 PM2：能不能托管、要不要接管 ----------
            // 判定必须放在端口预检之前：预检要豁免 PM2 托管的进程，否则杀掉它的子进程后
            // PM2 会 autorestart，形成「启动 → 杀掉 → 又占端口」的死循环。
            string pm2Name = Pm2Service.AppNameFor(instance);
            bool pm2Wanted = LauncherSettings.AllowBackground && Pm2Service.IsAvailable;
            bool pm2Ready = false;

            if (Pm2Service.IsAvailable)
            {
                Pm2App? existing = await Pm2Service.FindAsync(pm2Name).ConfigureAwait(false);

                // 上一轮关掉启动器后仍在跑：接管，绝不重启——重启会掐断用户正在用的会话
                if (pm2Wanted && existing is { IsOnline: true } && IsPortListening(port))
                {
                    AdoptPm2(existing, port, instance, builtin, configPath);
                    return;
                }

                // 其余情况（半死 / 换端口 / 用户关了继续运行 / 切换了实例）一律清场。
                // 不传 keep：同名 app 也要删，否则 start 会撞上旧定义直接失败。
                int stale = await Pm2Service.DeleteSiblingsAsync(null).ConfigureAwait(false);

                if (stale > 0)
                {
                    AddLogKey(TavernLogKind.Info, "Run.Log.Pm2Cleanup", stale);
                }

                pm2Ready = pm2Wanted;
            }
            else if (LauncherSettings.AllowBackground)
            {
                // 用户勾了「继续运行」但 PM2 不在：这次关启动器就会停，提前说清楚，别让他以为还在后台
                AddLogKey(TavernLogKind.Warning, "Run.Log.Pm2Unavailable");
            }

            // ---------- 5. 端口 ----------
            // 先自己动手：占用者是「我们内置的那份 node」时直接结束掉（多半是上次没退干净的残留），
            // 用户不必为这种小事去改配置。别的程序一律不动——端口冲突不该以关掉别人的程序收场。
            if (!PortProbe.IsFree(port))
            {
                List<PortOwnerInfo> owners = PortProbe.FindOwners(port);
                string detail = owners.Count > 0
                    ? string.Join("、", owners.Select(item => $"PID {item.Pid} {item.Name}"))
                    : L.Get("Run.PortBusy.UnknownOwner");

                AddLogKey(TavernLogKind.Warning, "Run.Log.PortBusyFound", port, detail);

                // PM2 管的进程同样是「启动器目录下的 node」，但绝不能杀：
                // 杀了它会立刻被 autorestart 拉回来，端口又占上，白忙一场
                IReadOnlySet<int> pm2Pids = await Pm2Service.ManagedPidsAsync().ConfigureAwait(false);

                int released = await PortProbe
                    .ReleaseOwnAsync(owners, port, pid => pm2Pids.Contains(pid))
                    .ConfigureAwait(false);

                if (released == 0)
                {
                    AddLogKey(TavernLogKind.Error, "Run.Log.PortBusy", port, detail);

                    lock (Gate)
                    {
                        _state = _state with { State = TavernRunState.Failed, Port = port, Error = L.F("Run.PortBusy.Short", port) };
                    }

                    Raise();
                    RequestPortFix(port, detail);
                    return;
                }

                AddLogKey(TavernLogKind.Success, "Run.Log.PortReleased", port, released);
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
            if (pm2Ready)
            {
                var pm2Watch = new CancellationTokenSource();

                lock (Gate)
                {
                    _readyWatch?.Dispose();
                    _readyWatch = pm2Watch;
                    _process = null;
                    _pm2AppName = pm2Name;
                    _state = _state with
                    {
                        State = TavernRunState.Starting,
                        Pid = null,
                        Port = port,
                        Url = $"http://127.0.0.1:{port}",
                        ModeKey = TavernSettings.ModeKey,
                        EnvKey = builtin ? "Run.Env.Builtin" : "Run.Env.System",
                        VersionLabel = instance.KernelLabel,
                        ManagedByPm2 = true,
                    };
                }

                Raise();

                if (LauncherSettings.ShowStartupCommand)
                {
                    AddLog(TavernLogKind.System, $"$ pm2 start {pm2Name}");
                }

                Pm2Result launch = await Pm2Service
                    .StartAsync(
                        new Pm2LaunchSpec(
                            pm2Name,
                            instance.Path,
                            "server.js",

                            // ecosystem 的 script 已经是 server.js，args 里不能再来一个：
                            // 变成 `node server.js server.js` 会直接把酒馆顶掉
                            arguments.Skip(1).ToList(),
                            node,
                            BuildPm2Environment(node, git, proxy)),
                        pm2Watch.Token)
                    .ConfigureAwait(false);

                if (!launch.Ok)
                {
                    // pm2 start 在 daemon 冷启动时会挂住 CLI（超时收尾）：这不代表没起来。
                    // 先查表；查不到就给端口 8 秒——端口在听就说明 app 其实已经被 PM2 拉起来了，
                    // 只是 CLI 与 daemon 的通信没回来。只看 jlist 会在这种环境里误判成失败，
                    // 进而「回退直连」与已经在跑的那个抢端口。
                    Pm2App? probe = await Pm2Service.FindAsync(pm2Name, pm2Watch.Token).ConfigureAwait(false);

                    if (probe is not null || await WaitListeningAsync(port, TimeSpan.FromSeconds(6)).ConfigureAwait(false))
                    {
                        launch = new Pm2Result(true, 0, string.Empty, string.Empty);
                    }
                }

                if (launch.Ok)
                {
                    // 尾随器放在这里而不是更早：StartAsync 内部会清空 out/err 文件，
                    // 先接上的话读到的偏移是旧文件的长度，启动 banner 会整段丢掉
                    StartTail(pm2Name, adopted: false);

                    // pm2 start 的退出码只代表「注册成功」，进程可能随后 errored：还要看 jlist
                    Pm2App? app = await Pm2Service.FindAsync(pm2Name, pm2Watch.Token).ConfigureAwait(false);

                    lock (Gate)
                    {
                        _state = _state with { Pid = app?.Pid, StartedAt = app?.StartedAt ?? DateTime.Now };
                    }

                    Raise();
                    AddLogKey(TavernLogKind.Info, "Run.Log.Pm2Started", pm2Name, app?.Pid ?? 0, port);

                    await WaitReadyPm2Async(pm2Name, port, clock, pm2Watch.Token).ConfigureAwait(false);
                    return;
                }

                // 起不来就回退直连：宁可这次没托管，也不能让酒馆打不开
                AddLogKey(TavernLogKind.Warning, "Run.Log.Pm2Fallback", launch.Detail);
                AppNotify.Warning(L.Get("Run.Pm2Fallback.Title"), L.F("Run.Pm2Fallback.Body", launch.Detail));

                StopTail();
                pm2Watch.Dispose();

                lock (Gate)
                {
                    _readyWatch = null;
                    _pm2AppName = null;
                    _state = _state with
                    {
                        State = TavernRunState.Starting,
                        Pid = null,
                        StartedAt = null,
                        ManagedByPm2 = false,
                    };
                }

                Raise();
            }

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

            // 酒馆内部会 spawn git（更新本体、装扩展）。把专属配置传给 node，
            // 它派生出去的所有 git 进程都能继承——用户自己的 ~/.gitconfig 不受影响。
            TavernGitConfig.Apply(startInfo);

            ApplyNodeOptions(startInfo);

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
                Fail("Run.Log.NodeStartFail");
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
                _pm2AppName = null;
                _state = _state with
                {
                    State = TavernRunState.Starting,
                    Pid = process.Id,
                    ManagedByPm2 = false,
                    Port = port,
                    Url = $"http://127.0.0.1:{port}",
                    ModeKey = TavernSettings.ModeKey,
                    EnvKey = builtin ? "Run.Env.Builtin" : "Run.Env.System",
                    VersionLabel = instance.KernelLabel,
                };
            }

            Raise();
            AddLogKey(TavernLogKind.Info, "Run.Log.Started", process.Id, port);

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
                    AddLogKey(
                        TavernLogKind.Warning,
                        "Run.Log.ReadyTimeout",
                        ReadyTimeout.TotalSeconds.ToString("0"),
                        port);
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

        /// <summary>
        /// 这条错误在老版本上把用户困死过：报错本身只说「协议不对」，
        /// 看不出是环境变量里的 --import 在捣鬼，这里补一句能照做的说明。
        /// 同一条错误会连着刷好几行（throw 行、Error 行、code 行），说明只补一次。
        /// </summary>
        private static void CheckEsmHint(string line)
        {
            if (_esmSchemeSeen
                || !line.Contains("ERR_UNSUPPORTED_ESM_URL_SCHEME", StringComparison.Ordinal))
            {
                return;
            }

            _esmSchemeSeen = true;
            AddLogKey(TavernLogKind.Error, "Run.Esm.Hint");
        }

        /// <summary>
        /// 判断一行输出算什么级别。直连与 PM2 两条来源共用，级别口径才不会分叉。
        ///
        /// **stderr 不等于错误**：Node 连 <c>console.warn</c> 都走 stderr，一律标红的话，
        /// 启动时每句提示都是红的，真正的错误反而淹在里面（例如代理与私有地址过滤同时开启时，
        /// 酒馆会打两条「请求代理已启用」的提示——它们是警告，不是故障）。
        /// 所以：有错误关键字才算错误，其余 stderr 降为警告。
        /// </summary>
        private static TavernLogKind ClassifyLine(string line, bool fromError)
        {
            if (line.Contains("error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("ERR!", StringComparison.Ordinal))
            {
                return TavernLogKind.Error;
            }

            return fromError ? TavernLogKind.Warning : TavernLogKind.Output;
        }

        // ==================== PM2 托管 ====================

        /// <summary>
        /// PM2 模式下等端口就绪。主信号仍是端口（唯一可靠的就绪判据），
        /// 额外每 1.5 秒看一眼 jlist：状态变 errored 就别再干等满 90 秒。
        /// </summary>
        private static async Task WaitReadyPm2Async(string appName, int port, Stopwatch clock, CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow + ReadyTimeout;
            DateTime nextPoll = DateTime.UtcNow;
            bool warned = false;
            int? knownPid = null;

            while (!token.IsCancellationRequested)
            {
                if (IsPortListening(port))
                {
                    int? readyPid = knownPid;
                    Post(() => MarkRunning(port, clock.Elapsed, readyPid));
                    return;
                }

                if (DateTime.UtcNow >= nextPoll)
                {
                    nextPoll = DateTime.UtcNow.AddSeconds(1.5);

                    Pm2App? app = await Pm2Service.FindAsync(appName, token).ConfigureAwait(false);

                    // app 为 null 时**不能判死**：PM2 在部分 Windows 环境里读不到进程表
                    // （它依赖 wmic 之类的系统工具，被组策略或安全软件拦掉就是空表）。
                    // 只有明确拿到「已退出」的状态才提前收工，否则继续等端口、由超时兜底。
                    if (app is not null && app.Status is "errored" or "stopped" or "stopping")
                    {
                        // 不清掉的话 PM2 会把它留在表里反复重启（autorestart），
                        // 端口与日志文件一直被占，用户再点启动还会撞上同名 app
                        _ = Pm2Service.DeleteAsync(appName);

                        Fail("Run.Log.Pm2Exited", app.Status);
                        return;
                    }

                    // pm2 start 返回时 app 常常还在 launching（pid=0），那一次拿不到 pid。
                    // 这里每轮补写：只写一次，避免每 1.5 秒刷一次 UI。
                    if (app?.Pid is > 0 and var pid && pid != knownPid)
                    {
                        knownPid = pid;
                        ApplyPm2Pid(pid);
                    }
                }

                if (!warned && DateTime.UtcNow > deadline)
                {
                    warned = true;
                    AddLogKey(
                        TavernLogKind.Warning,
                        "Run.Log.ReadyTimeout",
                        ReadyTimeout.TotalSeconds.ToString("0"),
                        port);
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

        /// <summary>等端口开始监听（不是「空闲」）。给「CLI 超时但进程其实已起」的判定用。</summary>
        private static async Task<bool> WaitListeningAsync(int port, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                if (IsPortListening(port))
                {
                    return true;
                }

                try
                {
                    await Task.Delay(300).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }

            return IsPortListening(port);
        }

        /// <summary>
        /// 补写 PM2 的 pid。启动初期 app 还是 <c>launching</c>，那一次拿到的 pid 是 null；
        /// 不补的话 <see cref="TavernProcessState.Pid"/> 会整个会话停在 null，
        /// 仪表盘的内存 / CPU 与「PID」三处就都是「—」。
        /// </summary>
        private static void ApplyPm2Pid(int pid)
        {
            lock (Gate)
            {
                if (_state.Pid == pid)
                {
                    return;
                }

                _state = _state with { Pid = pid };
            }

            Raise();
        }

        /// <summary>
        /// 接管一个已经在跑的 PM2 app：不重启、不改配置，只把状态与日志接回来。
        /// 重启会掐断用户正在用的会话，必须认领而不是重来。
        /// </summary>
        private static void AdoptPm2(Pm2App app, int port, TavernInstance instance, bool builtin, string configPath)
        {
            _queue ??= DispatcherQueue.GetForCurrentThread();

            lock (Gate)
            {
                _process = null;
                _pm2AppName = app.Name;
                _state = _state with
                {
                    State = TavernRunState.Running,
                    Pid = app.Pid,
                    Port = port,
                    Url = $"http://127.0.0.1:{port}",
                    ModeKey = TavernSettings.ModeKey,
                    EnvKey = builtin ? "Run.Env.Builtin" : "Run.Env.System",
                    VersionLabel = instance.KernelLabel,
                    Error = null,
                    StartedAt = app.StartedAt ?? DateTime.Now,
                    StartupSeconds = 0,
                    ManagedByPm2 = true,
                };
            }

            // 启动器没开的时候，酒馆的输出只写在 PM2 的 out / err 里：
            // 把它截一段补进日志页，否则「酒馆」页签只有启动器自己那几行
            int imported = ImportPm2History(app.Name);

            StartTail(app.Name, adopted: true);
            Raise();
            AddLogKey(TavernLogKind.Info, "Run.Log.Config", configPath);
            AddLogKey(TavernLogKind.Success, "Run.Log.Pm2Adopted", app.Name, port);

            if (imported > 0)
            {
                AddLogKey(TavernLogKind.Info, "Run.Log.Pm2History", imported);
            }

            // PM2 页签里也留一条：接管不执行任何命令（只查 jlist，而它是轮询、不记），
            // 不写这一行的话，重启后打开「PM2」看到的是上一轮的旧记录，像是没生效
            RuntimeLog.Pm2(
                $"adopt {app.Name} · {app.Status} · pid {app.Pid?.ToString() ?? "-"} · port {port}");

            ActivityLog.Record(ActivityCategory.Service, L.F("Activity.Service.Adopt", port));
        }

        /// <summary>
        /// 启动器刚开起来时，把上一轮关掉后 PM2 还在跑的 app 认领回来。
        /// 不认领的话，控制台显示「未启动」而端口却被占着，用户点启动只会撞端口。
        /// </summary>
        public static async Task AdoptExistingAsync()
        {
            if (!Pm2Service.IsAvailable || !LauncherSettings.AllowBackground)
            {
                return;
            }

            _queue ??= DispatcherQueue.GetForCurrentThread();

            try
            {
                TavernInstance instance = TavernInstances.Current;

                if (!instance.IsInstalled)
                {
                    return;
                }

                string appName = Pm2Service.AppNameFor(instance);
                Pm2App? app = await Pm2Service.FindAsync(appName).ConfigureAwait(false);

                if (app is not { IsOnline: true })
                {
                    return;
                }

                (string configPath, _) = ResolveData(instance);
                int port = ReadPort(configPath);

                if (!IsPortListening(port))
                {
                    // 进程在跑、端口却没监听：半死状态。留着只会让用户点启动撞端口
                    await Pm2Service.DeleteAsync(appName).ConfigureAwait(false);
                    return;
                }

                AdoptPm2(app, port, instance, LauncherSettings.Runtime == RuntimeSource.Builtin, configPath);
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn($"pm2 adopt failed: {ex.Message}");
            }
        }

        /// <summary>把 PM2 的输出文件接上：行会回灌进 <see cref="IngestExternalLine"/>，三处日志同步。</summary>
        /// <param name="adopted">本次是接管上一轮遗留的 app（而不是刚启动）。</param>
        private static void StartTail(string appName, bool adopted)
        {
            StopTail();

            var tail = new Pm2LogTail(appName, IngestExternalLine);
            _tail = tail;

            if (!adopted)
            {
                // 文件刚被 PrepareLogFiles 清空，读到什么就是本次启动的输出
                tail.Start(replayHistory: true);
                return;
            }

            // 接管时只跟新行：历史已经由 ImportPm2History 补进日志页（去重后），
            // 控制台那一侧由 ConsoleArchive 负责。再回放一遍只会重复。
            tail.Start(replayHistory: false);
        }

        /// <summary>
        /// 启动时把控制台存档载回内存。**只做一次**，且必须在 UI 线程调用。
        ///
        /// 控制台显示的就是上一轮保存下来的那些行，格式、顺序、内容都和关掉时一样；
        /// 这也顺带解决了「接管时从 PM2 重放、结构全变了」的问题。
        /// </summary>
        public static void RestoreConsoleHistory()
        {
            if (_consoleHistoryLoaded)
            {
                return;
            }

            _consoleHistoryLoaded = true;

            IReadOnlyList<TavernLogEntry> entries = ConsoleArchive.Load(MaxLogLines);

            if (entries.Count == 0)
            {
                // 首次运行 / 存档被删：保留「可以回放兜底」这条路
                _consoleHistoryLoaded = false;
                return;
            }

            Post(() =>
            {
                _logs.Clear();
                _logs.AddRange(entries);
                Cleared?.Invoke(null, EventArgs.Empty);
            });
        }

        private static void StopTail()
        {
            try
            {
                _tail?.Dispose();
            }
            catch (Exception)
            {
                // 退出路径上不再纠缠
            }

            _tail = null;
        }

        /// <summary>
        /// 把 PM2 记录的那段输出补进**日志页**（不进控制台）。
        ///
        /// 去重靠「今天的酒馆日志里已经有的行跳过」：启动器开着时尾随器已经写过一遍，
        /// 补的时候再把它们抄一遍，就成了每条都重复两次。用最近 600 行做对照，
        /// 命中即跳过（同一条一模一样的输出连发两次会被误判，但日志里这无关紧要）。
        /// </summary>
        /// <returns>实际补进来的行数。</returns>
        private static int ImportPm2History(string appName)
        {
            Dictionary<string, int>? seen = RecentTavernTexts(600);
            int imported = 0;

            foreach ((string line, bool fromError) in Pm2LogTail.ReadTail(appName, Pm2LogTail.AdoptReplayBytes))
            {
                string text = AnsiPattern.Replace(line, string.Empty).TrimEnd();

                if (text.Length == 0)
                {
                    continue;
                }

                // 按次数扣减而不是「见过就跳过」：分隔线、重复横幅这类同一句会出现多次，
                // 用集合会把第二次以后的新行也当成重复吞掉
                if (seen is not null && seen.TryGetValue(text, out int count) && count > 0)
                {
                    seen[text] = count - 1;
                    continue;
                }

                RuntimeLog.Tavern(
                    new TavernLogEntry(DateTime.Now, ClassifyLine(text, fromError), text),
                    mirrorToConsole: false);

                imported++;
            }

            return imported;
        }

        /// <summary>今天酒馆日志里最近若干行的正文（去掉时间与级别前缀）及其出现次数，用于补历史时去重。</summary>
        private static Dictionary<string, int>? RecentTavernTexts(int maxLines)
        {
            try
            {
                var texts = new List<string>();

                foreach (string path in Directory.EnumerateFiles(
                    RuntimeLog.FolderPath,
                    $"tavern-{DateTime.Today:yyyyMMdd}*.log"))
                {
                    foreach (string line in File.ReadLines(path))
                    {
                        Match match = RuntimeLinePattern.Match(line);

                        if (match.Success)
                        {
                            texts.Add(match.Groups["text"].Value);
                        }
                    }
                }

                var counts = new Dictionary<string, int>(StringComparer.Ordinal);

                foreach (string text in texts.Skip(Math.Max(0, texts.Count - maxLines)))
                {
                    counts[text] = counts.TryGetValue(text, out int count) ? count + 1 : 1;
                }

                return counts;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>与 <see cref="LogCatalog"/> 同一套行格式：时间、5 字符标签、正文。</summary>
        private static readonly Regex RuntimeLinePattern = new(
            @"^(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\s+[A-Z]+\s+(?<text>.*)$",
            RegexOptions.Compiled);

        /// <summary>
        /// PM2 输出的一行。来源不是我们的 <see cref="Process"/>，但走同一条日志管线——
        /// 控制台、日志页、磁盘三处看到的才是同一份。
        /// </summary>
        internal static void IngestExternalLine(string line, bool fromError)
        {
            string text = AnsiPattern.Replace(line, string.Empty).TrimEnd();

            if (text.Length == 0)
            {
                return;
            }

            CheckEsmHint(text);
            AddLog(ClassifyLine(text, fromError), text);
        }

        /// <summary>
        /// 交给 PM2 拉起的进程要继承的环境。命令行参数传不了环境变量，
        /// 只能走 ecosystem 的 <c>env</c> 字段（见 <see cref="Pm2Service"/>）。
        /// </summary>
        private static IReadOnlyDictionary<string, string> BuildPm2Environment(string node, string? git, string? proxy)
        {
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PATH"] = BuildPm2Path(node, git),
            };

            if (File.Exists(TavernGitConfig.FilePath))
            {
                env["GIT_CONFIG_GLOBAL"] = TavernGitConfig.FilePath;
            }

            if (ComposeNodeOptions() is { Length: > 0 } nodeOptions)
            {
                env["NODE_OPTIONS"] = nodeOptions;
            }

            if (proxy is not null)
            {
                foreach (string key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy" })
                {
                    env[key] = proxy;
                }
            }

            return env;
        }

        private static void MarkRunning(int port, TimeSpan elapsed, int? pid = null)
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
                    Pid = pid ?? _state.Pid,
                    Port = port,
                    Url = $"http://127.0.0.1:{port}",
                    StartedAt = _state.StartedAt ?? DateTime.Now,
                    StartupSeconds = elapsed.TotalSeconds,
                };
            }

            Raise();
            AddLogKey(TavernLogKind.Success, "Run.Log.Up", elapsed.TotalSeconds.ToString("0.0"), port);
            AppNotify.Success(
                L.Get("Run.Up.Title"),
                L.F("Run.Up.Body", _state.VersionLabel, port));

            // 桌面模式：启动完成后自动开窗。建窗必须在 UI 线程，这里只负责投递。
            // 普通模式不做这一步——酒馆自己会拉起浏览器，再开一次就成两个页面了。
            if (TavernSettings.Mode == LaunchMode.Desktop)
            {
                string url = $"http://127.0.0.1:{port}";

                Post(() =>
                {
                    if (!DesktopWindow.Open(url, out string detail))
                    {
                        AppNotify.Warning(
                            L.Get("Run.DesktopFail.Title"),
                            L.F("Run.DesktopFail.Body", detail));
                    }
                });
            }
        }

        // ==================== 停止 ====================

        /// <summary>
        /// 停止酒馆。<paramref name="force"/> 为 false 时先请求进程树自己退出（taskkill /T），
        /// 超时才升级成 /F：Node 通常不响应优雅关闭，硬杀是常态而不是异常。
        /// </summary>
        public static async Task StopAsync(bool force = false)
        {
            ActivityLog.Record(ActivityCategory.Service, L.Get("Activity.Service.Stop"));

            // PM2 分支必须放在最前：下面的实现第一个判断就是「_process 为空就返回」，
            // 托管模式下根本没有 Process，放在后面等于「点停止没反应」
            if (_state.ManagedByPm2 && _pm2AppName is { Length: > 0 } pm2Name)
            {
                await StopPm2Async(pm2Name, force).ConfigureAwait(false);
                return;
            }

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
            if (force)
            {
                AddLogKey(TavernLogKind.Info, "Run.Log.KillingTree");
            }
            else
            {
                AddLogKey(TavernLogKind.Info, "Run.Log.Stopping", pid);
            }

            if (force)
            {
                KillTree(pid, force: true);
                await WaitExitAsync(process, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            else if (!TryKillTree(pid, force: false))
            {
                // 控制台程序没有窗口，taskkill 不带 /F 会直接报「无法终止」：
                // 那就别再空等一轮超时，立刻升级成强制结束
                AddLogKey(TavernLogKind.Info, "Run.Log.NoCloseResponse");
                KillTree(pid, force: true);
                await WaitExitAsync(process, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            else
            {
                await WaitExitAsync(process, GracefulStopTimeout).ConfigureAwait(false);

                if (!SafeHasExited(process))
                {
                    AddLogKey(TavernLogKind.Warning, "Run.Log.StopTimeout", GracefulStopTimeout.TotalSeconds.ToString("0"));
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

            // 停止的收尾（FinishStopped）是投递到 UI 线程执行的，而这里跑在后台线程上。
            // 不等它落地，下面 StartAsync 会看到状态还是「停止中」并直接返回——
            // 表现成「点了重启，日志停在已停止，服务没起来」。
            await WaitSettledAsync().ConfigureAwait(false);

            if (_process is { } process)
            {
                await WaitExitAsync(process, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }

            await StartAsync().ConfigureAwait(false);
        }

        /// <summary>等「停止中」这个过渡状态结束；超时就自己补一次收尾。</summary>
        private static async Task WaitSettledAsync()
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

            while (DateTime.UtcNow < deadline)
            {
                if (_state.State is not TavernRunState.Stopping)
                {
                    return;
                }

                await Task.Delay(50).ConfigureAwait(false);
            }

            // 进程已经等过退出了，还卡在「停止中」只会让重启一直失败
            Post(() =>
            {
                if (_state.State == TavernRunState.Stopping)
                {
                    FinishStopped();
                }
            });

            await Task.Delay(120).ConfigureAwait(false);
        }

        /// <summary>
        /// 退出启动器时调用：**同步**把酒馆停干净，并等到端口真的释放。
        ///
        /// 窗口一关事件循环就没了，异步收尾根本跑不完，进程会变成孤儿继续占着端口；
        /// 所以这里用同步路径，并且把「等到端口释放」也算进职责——只杀进程不校验端口，
        /// 下次启动就可能撞上 TIME_WAIT 或杀失败的残留。
        /// </summary>
        /// <param name="portWait">最多等多久（默认 3 秒）。超时不阻断退出，只记日志。</param>
        /// <returns>端口是否已释放。false 表示还被别的进程占着。</returns>
        public static bool Shutdown(TimeSpan? portWait = null)
        {
            TimeSpan budget = portWait ?? TimeSpan.FromSeconds(3);
            var clock = Stopwatch.StartNew();

            int? pid;
            bool managed;
            string? appName;
            int port;

            lock (Gate)
            {
                pid = _state.Pid;
                managed = _state.ManagedByPm2;
                appName = _pm2AppName;
                port = _state.Port;
                _stopRequested = true;
                _readyWatch?.Cancel();
            }

            StopTail();

            if (managed && appName is { Length: > 0 } name)
            {
                // PM2 的 app 只能走 delete：对它的子进程用 taskkill 会被 autorestart 拉回来
                Pm2Service.Delete(name, TimeSpan.FromMilliseconds(1500));
            }
            else if (pid is { } value)
            {
                KillTree(value, force: true);
            }
            else
            {
                return true;
            }

            TimeSpan left = budget - clock.Elapsed;
            bool free = port <= 0 || (left > TimeSpan.Zero && PortProbe.WaitFree(port, left));

            if (!free && port > 0)
            {
                // 兜底：PM2 的 delete 在 daemon 通信失败时不会真的结束进程。
                // 用户的要求是「关掉启动器就把端口放开」，这里强制清一次占用者。
                free = PortProbe.ReleaseOwn(port, TimeSpan.FromMilliseconds(1200));
            }

            if (!free)
            {
                RuntimeLog.Warn($"shutdown: port {port} still busy after {clock.ElapsedMilliseconds}ms");
            }

            return free;
        }

        /// <summary>PM2 模式的停止：delete 之后还要等端口放开，否则「已停止」是假的。</summary>
        private static async Task StopPm2Async(string appName, bool force)
        {
            int port;

            lock (Gate)
            {
                if (_state.State is TavernRunState.Stopping)
                {
                    return;
                }

                port = _state.Port;
                _stopRequested = true;
                _state = _state with { State = TavernRunState.Stopping };
                _readyWatch?.Cancel();
            }

            Raise();
            AddLogKey(TavernLogKind.Info, "Run.Log.Pm2Stop", appName);

            // PM2 模式没有「优雅 / 强制」两档：delete 本身就是强杀，日志如实写一句即可
            if (force)
            {
                AddLogKey(TavernLogKind.Info, "Run.Log.KillingTree");
            }

            await Pm2Service.DeleteAsync(appName).ConfigureAwait(false);

            if (!await PortProbe.WaitFreeAsync(port, TimeSpan.FromSeconds(5)).ConfigureAwait(false))
            {
                AddLogKey(TavernLogKind.Warning, "Run.Log.Pm2PortStuck", port);
            }

            StopTail();
            Post(FinishStopped);
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

                                // 压到 2.5 秒：窗口关闭路径给整个停止流程的预算只有 3 秒
                killer.WaitForExit(2500);
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

            CheckEsmHint(line);
            AddLog(ClassifyLine(line, fromError: false), line);
        }

        /// <summary>ESM 路径格式出错时给的说明：说清原因、来源与怎么改。</summary>
        private static string EsmSchemeHint => L.Get("Run.Esm.Hint");

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
                    AddLogKey(
                        TavernLogKind.Success,
                        intentional ? "Run.Log.Stopped" : "Run.Log.ExitedZero");
                    return;
                }

                lock (Gate)
                {
                    _state = _state with
                    {
                        State = TavernRunState.Failed,
                        Pid = null,
                        Error = L.F("Run.Exit.Error", code),
                        StartedAt = null,
                    };
                }

                DesktopWindow.Close();

                Raise();
                AddLogKey(TavernLogKind.Error, "Run.Log.ExitedAbnormal", code);

                // 抓不到的成因就别硬猜：能识别的按真实原因给建议，其余保持通用说明
                if (_esmSchemeSeen)
                {
                    AppNotify.Error(
                        L.Get("Run.Esm.Title"),
                        L.Get("Run.Esm.Body"));
                    return;
                }

                AppNotify.Error(
                    L.Get("Run.Crash.Title"),
                    L.F("Run.Crash.Body", code));
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

                    // 托管标记必须跟着清：不清的话仪表盘会显示「已停止 · PM2」这种假标记，
                    // 还会让关窗逻辑误以为「有 PM2 保活」而跳过停服
                    ManagedByPm2 = false,
                };

                _pm2AppName = null;
            }

            StopTail();

            // 酒馆没了，桌面窗口里的页面也活不了：一起关掉，别留个白屏窗口
            DesktopWindow.Close();

            Raise();
        }

        /// <summary>
        /// 启动失败：状态、日志、弹出提示三处一起写。
        /// 参数是**资源键**而不是成品文字——日志里那一行要能跟着语言变。
        /// </summary>
        private static void Fail(string key, params object?[] args)
        {
            string message = args.Length > 0 ? L.F(key, args) : L.Get(key);

            lock (Gate)
            {
                _queue ??= DispatcherQueue.GetForCurrentThread();
                _state = _state with
                {
                    State = TavernRunState.Failed,
                    Pid = null,
                    Error = message,
                    StartedAt = null,

                    // 启动都失败了，不可能还有 PM2 在管：不清的话关窗会误判成「有保活」
                    ManagedByPm2 = false,
                };

                _pm2AppName = null;
            }

            StopTail();

            Raise();
            AddEntry(new TavernLogEntry(DateTime.Now, TavernLogKind.Error, message) { Key = key, Args = args });
            AppNotify.Error(L.Get("Run.StartFail.Title"), message);
        }

        // ==================== 端口 ====================

        /// <summary>
        /// 端口被别的程序占着、又解不掉：把用户送到配置页，并把那一行标出来。
        ///
        /// 走 force 导航：配置页可能正开着（用户刚改完端口就点了启动），
        /// 不重建的话新页面不会重新读配置，高亮也没有地方挂。
        /// </summary>
        private static void RequestPortFix(int port, string detail)
        {
            PortAttention.Request(port);

            DispatcherQueue? queue = App.MainWindow?.DispatcherQueue ?? _queue;

            queue?.TryEnqueue(() =>
            {
                AppNotify.Error(
                    L.Get("Run.PortBusy.Title"),
                    L.F("Run.PortBusy.GoFix", port, detail));

                App.MainWindow?.NavigateTo("tavern", force: true);
            });
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

        /// <summary>
        /// 修正继承来的 <c>NODE_OPTIONS</c>。
        ///
        /// 用户机器上装了别的 Node 工具时，系统环境变量里常会留下
        /// <c>--import C:\some\hook.mjs</c>。子进程会原样继承它，Node 的 ESM 加载器又把
        /// 盘符当协议名，于是酒馆刚起来就以 ERR_UNSUPPORTED_ESM_URL_SCHEME 退出——
        /// 在用户的视角里就是「什么都没干，酒馆启动不了」。
        /// 这里把裸路径改写成 file:// URL，并如实记一行日志。
        /// </summary>
        private static void ApplyNodeOptions(ProcessStartInfo startInfo)
        {
            if (ComposeNodeOptions() is { Length: > 0 } options)
            {
                startInfo.Environment["NODE_OPTIONS"] = options;
            }
        }

        /// <summary>
        /// 最终交给 node 的 NODE_OPTIONS：继承值（规整过） + 隐藏子进程窗口的补丁。
        ///
        /// 直连与 PM2 两条通道都走这里，否则会出现「托管时还在闪窗、直连却好了」这种分叉。
        /// 补丁文件写不出来时**只保留继承值**：宁可闪一下，也不能让酒馆因为
        /// <c>--require</c> 指向一个不存在的文件而起不来。
        /// </summary>
        private static string? ComposeNodeOptions()
        {
            string? inherited = SanitizeInheritedNodeOptions();
            string? shim = NodeShim.RequireOption();

            if (shim is null)
            {
                return inherited;
            }

            if (inherited is null)
            {
                return shim;
            }

            // 用户自己的 NODE_OPTIONS 里可能已经引了同一个补丁：别叠两层
            if (inherited.Contains("winhide.cjs", StringComparison.OrdinalIgnoreCase))
            {
                return inherited;
            }

            return inherited + " " + shim;
        }

        /// <summary>
        /// 把继承来的 NODE_OPTIONS 规整成能用的值（裸路径改写成 file://），并如实记日志。
        /// 直连与 PM2 两条路径共用：PM2 那边要把同样的值写进 ecosystem 的 env。
        /// </summary>
        private static string? SanitizeInheritedNodeOptions()
        {
            string? inherited = Environment.GetEnvironmentVariable("NODE_OPTIONS");

            if (string.IsNullOrWhiteSpace(inherited))
            {
                return null;
            }

            AddLogKey(TavernLogKind.Info, "Run.Log.InheritedNodeOptions", inherited);

            (string? fixedOptions, string? note) = NodeEnvironment.SanitizeNodeOptions(inherited);

            if (note is null || fixedOptions is null)
            {
                return null;
            }

            AddLogKey(TavernLogKind.Warning, "Miss.NodeEnvNote");
            AddLogKey(TavernLogKind.Info, "Run.Log.Rewritten", fixedOptions);

            return fixedOptions;
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
        /// <summary>
        /// 拼给子进程的 PATH：node 与 git 的目录前置，内置环境不在系统 PATH 里也能跑。
        /// internal：PM2 服务起 pm2 子进程时要用同一份，别各拼一套。
        /// </summary>
        internal static string BuildPath(string node, string? git)
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

        /// <summary>
        /// 给 PM2 拉起的酒馆进程的 PATH：只放它真正需要的目录。
        ///
        /// 不能照抄 <see cref="BuildPath"/>——那会把整个宿主 PATH 写进 ecosystem 文件，
        /// 一个 json 能撑到几十万字符，还把这些与本项目无关的目录带进常驻进程。
        /// 酒馆真正要的只有：node、git，以及 cmd / powershell 所在的系统目录。
        /// </summary>
        private static string BuildPm2Path(string node, string? git)
        {
            var parts = new List<string>();

            if (Path.GetDirectoryName(node) is { Length: > 0 } nodeDir)
            {
                parts.Add(nodeDir);
            }

            if (git is not null && Path.GetDirectoryName(git) is { Length: > 0 } gitDir)
            {
                parts.Add(gitDir);

                string root = Path.GetDirectoryName(gitDir) ?? gitDir;
                parts.Add(Path.Combine(root, "bin"));
                parts.Add(Path.Combine(root, "mingw64", "bin"));
            }

            foreach (string system in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            })
            {
                if (system is { Length: > 0 })
                {
                    parts.Add(system);
                }
            }

            return string.Join(Path.PathSeparator, parts.Where(item => item.Length > 0));
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
            AddLogKey(TavernLogKind.System, "Run.Log.Cleared");
        }

        /// <summary>
        /// 每次启动前重置日志：清空后留一行时间分隔，既不残留上一次的输出，
        /// 也能一眼看出这是第几次启动、什么时候起的。
        /// </summary>
        private static void ResetLogs()
        {
            // 走 Post 而不是直接改：重启流程是在后台线程上推进的，
            // 直接清表并触发 Cleared（页面会重画日志列表）就是跨线程改控件，会抛异常，
            // 表现成「点了重启，停在已停止，什么也没发生」。
            Post(() =>
            {
                _logs.Clear();
                _logs.Add(new TavernLogEntry(DateTime.Now, TavernLogKind.System, string.Empty)
                {
                    Key = "Run.Log.NewSession",
                    Args = new object?[] { DateTime.Now.ToString("HH:mm:ss") },
                });

                Cleared?.Invoke(null, EventArgs.Empty);
            });
        }

        /// <summary>外部输出（酒馆进程的 stdout）：文字就是最终文字，不随语言变。</summary>
        private static void AddLog(TavernLogKind kind, string text)
            => AddEntry(new TavernLogEntry(DateTime.Now, kind, text));

        /// <summary>
        /// 启动器自己写的一行：只记 key 与实参，显示时按当前语言取。
        /// 换语言后历史日志会跟着重排，不会「上半屏中文下半屏英文」。
        /// </summary>
        private static void AddLogKey(TavernLogKind kind, string key, params object?[] args)
            => AddEntry(new TavernLogEntry(DateTime.Now, kind, string.Empty) { Key = key, Args = args });

        private static void AddEntry(TavernLogEntry entry)
        {
            Post(() =>
            {
                _logs.Add(entry);

                // 内存一份、磁盘一份：内存只留最近 2000 行给控制台，磁盘按天滚动给日志页。
                // Tavern() 只是入队（有界队列 + 后台写），stdout 刷屏也不会卡住 UI 线程。
                RuntimeLog.Tavern(entry);

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
