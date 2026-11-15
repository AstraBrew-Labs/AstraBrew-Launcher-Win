using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>运行环境的两侧：启动器自带的一套，或系统 PATH 里那一套。</summary>
    public enum RuntimeSide
    {
        Builtin,
        System,
    }

    /// <summary>装多少：只装能跑酒馆的最小集合，还是把常用工具补齐。</summary>
    public enum RuntimePackage
    {
        /// <summary>基础包：Node.js + npm（普通模式跑酒馆只需要这些）。</summary>
        Basic,

        /// <summary>完整包：再加 Git、Caddy、PM2。</summary>
        Full,
    }

    /// <summary>环境里的一个组件。</summary>
    public enum RuntimeComponentKey
    {
        Node,
        Npm,
        Git,
        Caddy,
        Pm2,
        WebView2,
    }

    /// <summary>
    /// 一个组件在当前环境里的状态。<see cref="Purpose"/> / <see cref="Consequence"/> 就是
    /// 界面那个 (i) 图标里的两句话——用户最想知道「它是干什么的、缺了会怎样」。
    /// </summary>
    public sealed record RuntimeComponentInfo(
        RuntimeComponentKey Key,
        string Name,
        string Glyph,
        string? Version,
        bool Ready,
        bool RequiredForBasic,
        string Purpose,
        string Consequence);

    /// <summary>一侧环境的完整画像。</summary>
    public sealed record RuntimeProfile(
        RuntimeSide Side,
        IReadOnlyList<RuntimeComponentInfo> Components,
        bool BasicReady,
        bool FullReady,
        string Summary)
    {
        public RuntimeComponentInfo? this[RuntimeComponentKey key] =>
            Components.FirstOrDefault(component => component.Key == key);
    }

    /// <summary>安装弹窗里的一行任务。</summary>
    public sealed record RuntimeTask(
        string Key,
        string Name,
        string Detail,
        string Glyph,
        double Weight);

    /// <summary>一行任务的进度：百分比是这一行自己的，总进度由界面按 weight 合成。</summary>
    /// <summary>
    /// 一条安装任务此刻处在哪一步。
    /// **判断状态一律看这个枚举，不要拿 <see cref="RuntimeTaskUpdate.Message"/> 的文字去比**——
    /// 界面文案要翻译，文字一比就断（原来写的是 <c>Message == "完成"</c>，切英文后进度条就停在原地）。
    /// </summary>
    public enum RuntimeTaskState
    {
        /// <summary>还没轮到 / 被取消，界面显示「等着」。</summary>
        Pending,

        /// <summary>轮到它了，正在准备（还没开始下载）。</summary>
        Preparing,

        /// <summary>正在下载 / 解压 / 校验。</summary>
        Running,

        /// <summary>装好了。</summary>
        Completed,

        /// <summary>装失败了。</summary>
        Failed,
    }

    public sealed record RuntimeTaskUpdate(
        string Key,
        string Message,
        double Percent,
        bool Indeterminate,
        string? LogLine,
        RuntimeTaskState State = RuntimeTaskState.Pending)
    {
        /// <summary>失败。保留这个属性是为了不惊动老的读取方，真身是 <see cref="State"/>。</summary>
        public bool Failed => State == RuntimeTaskState.Failed;
    }

    /// <summary>一批安装的结果。<see cref="Cancelled"/> 与失败分开，界面文案不一样。</summary>
    public sealed record RuntimeBatchResult(
        bool Success,
        bool Cancelled,
        IReadOnlyList<(string Key, InstallResult Result)> Results,
        string? Error = null);

    /// <summary>
    /// 运行环境的「画像 + 安装清单 + 批量安装」。
    ///
    /// 为什么清单要分「基础包 / 完整包」：普通模式跑酒馆只要 Node.js + npm，
    /// 把 Git / Caddy / PM2 一起算进门槛，等于逼用户为了点下一步多等两分钟。
    /// 完整包是「顺便一次装齐」，装不完也不挡路。
    ///
    /// 系统环境**不代装**：写 Program Files 要提权、msi 会弹交互，静默安装既不现实也不礼貌。
    /// 所以 <see cref="Plan"/> 对系统侧永远返回空清单，界面给的是「打开下载页 + 重新检测」。
    /// </summary>
    public static class RuntimeSetup
    {
        /// <summary>同一时刻只允许一批安装：双击按钮不该装两遍。</summary>
        private static readonly SemaphoreSlim Gate = new(1, 1);

        public static async Task<RuntimeProfile> DescribeAsync(RuntimeSide side, bool force = false)
        {
            RuntimeSource source = side == RuntimeSide.Builtin ? RuntimeSource.Builtin : RuntimeSource.System;
            RuntimeEnvironment env = await RuntimeProbe.ProbeAsync(source, force).ConfigureAwait(false);

            bool builtin = side == RuntimeSide.Builtin;

            var components = new List<RuntimeComponentInfo>
            {
                new(
                    RuntimeComponentKey.Node,
                    "Node.js",
                    "\uE7F4",
                    env.Node,
                    env.Node is not null,
                    RequiredForBasic: true,
                    L.Get("Rts.Node.Why"),
                    L.Get("Rts.Node.Need")),

                new(
                    RuntimeComponentKey.Npm,
                    "npm",
                    "\uE8B7",
                    env.Npm,
                    env.Npm is not null,
                    RequiredForBasic: true,
                    L.Get("Rts.Npm.Why"),
                    L.Get("Rts.Npm.Need")),

                new(
                    RuntimeComponentKey.Git,
                    builtin ? L.Get("Rts.Git.Builtin") : "Git",
                    "\uE8E5",
                    env.Git,
                    env.Git is not null,
                    RequiredForBasic: false,
                    L.Get("Rts.Git.Why"),
                    L.Get("Rts.Git.Need")),

                new(
                    RuntimeComponentKey.Caddy,
                    "Caddy",
                    "\uE774",
                    env.Caddy,
                    env.Caddy is not null,
                    RequiredForBasic: false,
                    L.Get("Rts.Caddy.Why"),
                    L.Get("Rts.Caddy.Need")),

                new(
                    RuntimeComponentKey.Pm2,
                    "PM2",
                    "\uE950",
                    env.Pm2,
                    env.Pm2 is not null,
                    RequiredForBasic: false,
                    L.Get("Rts.Pm2.Why"),
                    L.Get("Rts.Pm2.Need")),

                new(
                    RuntimeComponentKey.WebView2,
                    "WebView2",
                    "\uE8A7",
                    env.WebView2,
                    env.WebView2 is not null,
                    RequiredForBasic: false,
                    L.Get("Rts.Desktop.Why"),
                    L.Get("Rts.Desktop.Need")),
            };

            bool basic = components
                .Where(component => component.RequiredForBasic)
                .All(component => component.Ready);

            bool full = components
                .Where(component => component.Key != RuntimeComponentKey.WebView2)
                .All(component => component.Ready);

            string summary = basic
                ? full ? L.Get("Rts.Status.Complete") : L.Get("Rts.Status.CanRun")
                : L.Get("Rts.Status.Missing");

            return new RuntimeProfile(side, components, basic, full, summary);
        }

        /// <summary>按包大小与当前画像，算出要装哪几行（系统侧永远是空）。</summary>
        public static IReadOnlyList<RuntimeTask> Plan(
            RuntimeSide side,
            RuntimePackage package,
            RuntimeProfile profile)
        {
            if (side == RuntimeSide.System)
            {
                return Array.Empty<RuntimeTask>();
            }

            var tasks = new List<RuntimeTask>();

            RuntimeComponentInfo? node = profile[RuntimeComponentKey.Node];
            RuntimeComponentInfo? npm = profile[RuntimeComponentKey.Npm];

            // Node 在但 npm 不在，也按「重装 Node」处理：官方 zip 里两者是一体的
            if (node is not { Ready: true } || npm is not { Ready: true })
            {
                tasks.Add(new RuntimeTask(
                    "Node",
                    "Node.js",
                    node is { Ready: true }
                        ? L.F("Rts.Node.MissingNpm", NodejsInstaller.Version)
                        : $"Node.js {NodejsInstaller.Version}",
                    "\uE7F4",
                    40));
            }

            if (package == RuntimePackage.Full)
            {
                if (profile[RuntimeComponentKey.Git] is not { Ready: true })
                {
                    tasks.Add(new RuntimeTask(
                        "Git",
                        "Git（MinGit）",
                        MingitInstaller.Version,
                        "\uE8E5",
                        30));
                }

                if (profile[RuntimeComponentKey.Caddy] is not { Ready: true })
                {
                    tasks.Add(new RuntimeTask(
                        "Caddy",
                        "Caddy",
                        $"v{CaddyInstaller.Version}",
                        "\uE774",
                        20));
                }

                if (profile[RuntimeComponentKey.Pm2] is not { Ready: true })
                {
                    tasks.Add(new RuntimeTask(
                        "Pm2",
                        "PM2",
                        $"v{Pm2Installer.Version}",
                        "\uE950",
                        10));
                }
            }

            return tasks;
        }

        /// <summary>
        /// 顺序安装。串行而不是并发，理由很实在：
        /// · PM2 必须等 Node 就位（它就是用 npm 装的）；
        /// · 四条并发下载会互相抢带宽、并发解压抢磁盘，总时长几乎不变，
        ///   却让每一行都慢得像卡死——不如让用户清楚看到「现在在装哪个」。
        /// </summary>
        public static async Task<RuntimeBatchResult> InstallAsync(
            IReadOnlyList<RuntimeTask> tasks,
            IProgress<RuntimeTaskUpdate>? progress,
            CancellationToken token)
        {
            var results = new List<(string Key, InstallResult Result)>();

            if (tasks.Count == 0)
            {
                return new RuntimeBatchResult(true, false, results);
            }

            if (!await Gate.WaitAsync(0, token).ConfigureAwait(false))
            {
                return new RuntimeBatchResult(false, false, results, L.Get("Rts.Busy"));
            }

            try
            {
                foreach (RuntimeTask task in tasks)
                {
                    if (token.IsCancellationRequested)
                    {
                        return new RuntimeBatchResult(false, true, results);
                    }

                    progress?.Report(new RuntimeTaskUpdate(
                        task.Key,
                        L.Get("Rts.Stage.Preparing"),
                        0,
                        false,
                        null,
                        RuntimeTaskState.Preparing));

                    IProgress<InstallProgress>? adapter = progress is null
                        ? null
                        : new Progress<InstallProgress>(value => progress.Report(new RuntimeTaskUpdate(
                            task.Key,
                            StageLabel(value.Stage, value.Message),
                            value.Percent,
                            value.Percent <= 0,
                            value.Message,
                            RuntimeTaskState.Running)));

                    InstallResult result = await InstallOneAsync(task.Key, adapter, token).ConfigureAwait(false);
                    results.Add((task.Key, result));

                    progress?.Report(new RuntimeTaskUpdate(
                        task.Key,
                        result.Success ? L.Get("Rts.Stage.Done") : result.Message,
                        result.Success ? 100 : 0,
                        false,
                        result.Message,
                        result.Success
                            ? RuntimeTaskState.Completed
                            : result.Error == InstallError.Cancelled
                                ? RuntimeTaskState.Pending   // 主动取消不算失败，界面上仍是「等着」
                                : RuntimeTaskState.Failed));

                    if (result.Error == InstallError.Cancelled)
                    {
                        return new RuntimeBatchResult(false, true, results);
                    }

                    // Node 失败后面全都没意义：PM2 靠 npm，依赖也靠它
                    if (!result.Success && task.Key == "Node")
                    {
                        return new RuntimeBatchResult(false, false, results, L.Get("Rts.SkipAfterNode"));
                    }
                }

                bool ok = results.All(item => item.Result.Success);
                return new RuntimeBatchResult(ok, false, results);
            }
            finally
            {
                Gate.Release();
            }
        }

        private static Task<InstallResult> InstallOneAsync(
            string key,
            IProgress<InstallProgress>? progress,
            CancellationToken token) => key switch
            {
                "Node" => NodejsInstaller.InstallAsync(progress, token),
                "Git" => MingitInstaller.InstallAsync(progress, token),
                "Caddy" => CaddyInstaller.InstallAsync(progress, token),
                "Pm2" => Pm2Installer.InstallAsync(progress, token),
                _ => Task.FromResult(new InstallResult(false, InstallError.Unknown, L.Get("Rts.UnknownComponent"), key, null)),
            };

        private static string StageLabel(InstallStage stage, string message) => stage switch
        {
            InstallStage.Preparing => L.Get("Rts.Stage.Preparing"),
            InstallStage.Downloading => message.Length > 0 ? message : L.Get("Rts.Stage.Downloading"),
            InstallStage.Extracting => L.Get("Rts.Stage.Extracting"),
            InstallStage.Verifying => L.Get("Rts.Stage.Verifying"),
            InstallStage.Done => L.Get("Rts.Stage.Done"),
            _ => message,
        };
    }
}
