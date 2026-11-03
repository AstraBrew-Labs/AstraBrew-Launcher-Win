using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AstraBrew_Launcher.Services
{
    /// <summary>酒馆实例的来源：启动器自带的一份，或用户自己在磁盘上的目录。</summary>
    public enum TavernInstanceKind
    {
        Builtin,
        Local,
    }

    /// <summary>
    /// 一份可启动的酒馆代码目录。
    ///
    /// 只由 <see cref="TavernInstances"/> 构造与修改：页面拿到的是共享对象，
    /// 不能自己 new 一份，否则又会退化成「每页各存一份」。
    /// </summary>
    public sealed class TavernInstance
    {
        internal TavernInstance(
            TavernInstanceKind kind,
            string version,
            string path,
            string node,
            string size,
            bool depsReady)
        {
            Kind = kind;
            Version = version;
            Path = path;
            Node = node;
            Size = size;
            DepsReady = depsReady;
        }

        public TavernInstanceKind Kind { get; }

        /// <summary>纯版本号，不带 v 前缀；内置实例也可能是 "staging"。</summary>
        public string Version { get; internal set; }

        public string Path { get; }

        public string Node { get; }

        public string Size { get; internal set; }

        /// <summary>Node 依赖是否就绪；本地实例可能缺失，内置实例恒为 true。</summary>
        public bool DepsReady { get; internal set; }

        public bool IsBuiltin => Kind == TavernInstanceKind.Builtin;

        public string KindLabel => IsBuiltin ? "内置" : "本地";

        /// <summary>这份实例在磁盘上是否真的存在（内置实例看安装目录，本地实例恒为 true）。</summary>
        public bool IsInstalled => !IsBuiltin || TavernInstances.BuiltinInstalled;

        /// <summary>形如 "v1.19.2"；开发版通道显示为 "开发版"；还没装就是 "未安装"。</summary>
        public string KernelLabel => !IsInstalled
            ? "未安装"
            : Version == TavernInstances.StagingTag
                ? "开发版"
                : "v" + Version;

        /// <summary>下拉框里的名字，形如 "内置酒馆" / "本地-1.19.0"。</summary>
        public string DisplayName => IsBuiltin ? "内置酒馆" : $"本地-{Version}";

        /// <summary>标题栏徽章文案，形如 "内置-1.19.2" / "本地-1.19.0"；没装时是 "内置-未安装"。</summary>
        public string DisplayLabel => IsInstalled ? $"{KindLabel}-{Version}" : $"{KindLabel}-未安装";
    }

    /// <summary>
    /// 全应用唯一的实例清单与「当前使用哪一份」的状态（原型数据，不做持久化）。
    ///
    /// 仪表盘的版本下拉、版本管理页的两个页签、标题栏徽章、关于页的版本信息
    /// 全部读写这一份数据：任何一处切换实例都只改这里，再通过 <see cref="Changed"/>
    /// 让所有在界面的页面刷新，从结构上消除「两处各存一份、互相不知道」的同步问题。
    /// </summary>
    public static class TavernInstances
    {
        /// <summary>
        /// 还没读到真实安装版本、也没取到发布列表时的占位版本。
        /// 有 <c>sillytavern\package.json</c> 时一律以文件里的版本为准。
        /// </summary>
        public const string DefaultVersion = "1.19.0";

        /// <summary>开发版通道在版本号里的标记值。</summary>
        public const string StagingTag = "staging";

        /// <summary>
        /// GitHub 发布列表，按发布时间从新到旧；由版本页加载后写入。
        /// 空列表表示还没取过，UI 不要据此判断「没有版本」。
        /// </summary>
        private static IReadOnlyList<TavernRelease> _releases = Array.Empty<TavernRelease>();

        /// <summary>内置实例的代码目录（全应用唯一一份）。</summary>
        public static string BuiltinInstallRoot { get; } =
            Path.Combine(AppPaths.RoamingRoot, "sillytavern");

        /// <summary>
        /// 内置实例装好了没有：以 <c>sillytavern\package.json</c> 为准。
        /// 目录在但清单不在（用户手动清过、上次装一半被杀）一律算没装——
        /// 那种目录不但跑不起来，还会让「安装」按钮消失。
        /// </summary>
        public static bool BuiltinInstalled { get; private set; } =
            File.Exists(Path.Combine(BuiltinInstallRoot, "package.json"));

        /// <summary>
        /// 内置实例的 Node 依赖装好了没有。
        ///
        /// 以 <c>node_modules\.package-lock.json</c> 为准——那是 npm 在安装**结束时**
        /// 才写的隐藏锁文件，装一半被杀就不会有，比其他「目录存在」的判据可靠。
        /// 老版本 npm 不写它，退一步看两个运行时必备的包。
        /// </summary>
        public static bool BuiltinDepsReady { get; private set; } = DetectDepsReady();

        /// <summary>清单或当前选择发生变化。</summary>
        public static event EventHandler? Changed;

        private static readonly TavernInstance BuiltinInstance = new(
            TavernInstanceKind.Builtin,
            DetectInstalledVersion() ?? DefaultVersion,
            BuiltinInstallRoot,
            "Node 20.11.0",
            "—",
            depsReady: BuiltinDepsReady);

        private static readonly List<TavernInstance> LocalList = new()
        {
            new TavernInstance(
                TavernInstanceKind.Local,
                "1.19.0",
                @"C:\Users\al01\Documents\SillyTavern-1.19.0",
                "Node 20.11.0",
                "1.24 GB",
                depsReady: true),
            new TavernInstance(
                TavernInstanceKind.Local,
                "1.18.0",
                @"D:\AstraBrew\tavern-1.18.0",
                "Node 20.11.0",
                "1.18 GB",
                depsReady: true),
            new TavernInstance(
                TavernInstanceKind.Local,
                "1.17.2",
                @"C:\Users\al01\Documents\SillyTavern-legacy",
                "Node 18.20.4",
                "1.06 GB",
                depsReady: false),
        };

        /// <summary>内置实例恒在索引 0，与本地实例的先后顺序固定，下拉框顺序因此稳定。</summary>
        private static readonly List<TavernInstance> AllList = new() { BuiltinInstance };

        private static TavernInstance _current = BuiltinInstance;

        static TavernInstances()
        {
            AllList.AddRange(LocalList);
        }

        /// <summary>内置实例（唯一一份）。</summary>
        public static TavernInstance Builtin => BuiltinInstance;

        /// <summary>GitHub 发布列表，空列表表示还没取过（而不是「没有版本」）。</summary>
        public static IReadOnlyList<TavernRelease> Releases => _releases;

        /// <summary>发布列表里最新的一条稳定版；没取到列表时为 null。</summary>
        public static TavernRelease? LatestRelease =>
            _releases.FirstOrDefault(release => !release.Prerelease);

        /// <summary>
        /// 最新稳定版版本号。还没取到列表时退回内置实例自己的版本，
        /// 这样仪表盘与更新检查不会在启动瞬间误报「有新版本」。
        /// </summary>
        public static string LatestStable => LatestRelease?.Version ?? BuiltinInstance.Version;

        /// <summary>按版本号找发布记录；找不到返回 null（内置版本可能不在发布列表里）。</summary>
        public static TavernRelease? FindRelease(string version) =>
            _releases.FirstOrDefault(release =>
                string.Equals(release.Version, version, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// 写入刚取到的发布列表。内容与现有列表一致时不发通知，避免刷新后整页重建。
        /// </summary>
        public static void SetReleases(IReadOnlyList<TavernRelease> releases)
        {
            if (releases is null || releases.Count == 0)
            {
                return;
            }

            bool same = _releases.Count == releases.Count
                && _releases.Select(release => release.Tag)
                    .SequenceEqual(releases.Select(release => release.Tag), StringComparer.Ordinal);
            if (same)
            {
                return;
            }

            _releases = releases;

            // 体积这类派生值跟着新列表走；当前版本的卡片不必等下一次刷新
            if (FindRelease(BuiltinInstance.Version) is { } current)
            {
                BuiltinInstance.Size = SizeOf(current.Version);
            }

            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>本地实例，顺序即列表顺序。</summary>
        public static IReadOnlyList<TavernInstance> Locals => LocalList;

        /// <summary>全部实例，内置在前、本地在后。</summary>
        public static IReadOnlyList<TavernInstance> All => AllList;

        /// <summary>当前用于启动的实例；永远指向清单里的一份。</summary>
        public static TavernInstance Current => _current;

        public static bool IsBuiltinActive => _current.IsBuiltin;

        /// <summary>切换到某个实例；缺少依赖的本地实例不允许切换。</summary>
        public static void Use(TavernInstance instance)
        {
            if (instance is null || !AllList.Contains(instance))
            {
                return;
            }

            if (ReferenceEquals(_current, instance))
            {
                return;
            }

            if (!instance.IsBuiltin && !instance.DepsReady)
            {
                return;
            }

            // 没装就没有可启动的代码，别把「当前实例」指到一个空目录上；
            // 缺依赖同理——那种目录探测能过，一点启动就报错
            if (instance.IsBuiltin && !BuiltinRunnable)
            {
                return;
            }

            _current = instance;
            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>
        /// 安装/换版之后重新认一遍磁盘上的内置实例：装没装、什么版本。
        ///
        /// 版本一律现读 <c>sillytavern\package.json</c>，不用「刚才请求的那个标签」顶上——
        /// 镜像上的标签与代码里的 version 偶尔不一致，界面上该显示的是跑起来的那份代码。
        /// </summary>
        public static void ReloadBuiltin()
        {
            BuiltinInstalled = File.Exists(Path.Combine(BuiltinInstallRoot, "package.json"));
            BuiltinDepsReady = BuiltinInstalled && DetectDepsReady();
            BuiltinInstance.DepsReady = BuiltinDepsReady;

            string? detected = BuiltinInstalled ? DetectInstalledVersion() : null;
            if (detected is not null)
            {
                BuiltinInstance.Version = detected;
            }

            BuiltinInstance.Size = BuiltinInstalled ? SizeOf(BuiltinInstance.Version) : "—";
            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>
        /// 内置实例能不能跑起来（代码 + 依赖都齐）。
        /// 「装好了」与「能启动」不是一回事：缺依赖的那份代码在磁盘上是完整的，
        /// 但没有 <c>node_modules</c> 时启动只会立刻报错。
        /// </summary>
        public static bool BuiltinRunnable => BuiltinInstalled && BuiltinDepsReady;

        /// <summary>标记本地实例的 Node 依赖状态。</summary>
        public static void SetDepsReady(TavernInstance instance, bool ready)
        {
            if (instance is null || instance.IsBuiltin || instance.DepsReady == ready)
            {
                return;
            }

            instance.DepsReady = ready;
            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>从清单移除本地实例（不删除磁盘目录）；移除的正是当前实例时回落到内置实例。</summary>
        public static void RemoveLocal(TavernInstance instance)
        {
            if (instance is null || instance.IsBuiltin || !LocalList.Remove(instance))
            {
                return;
            }

            AllList.Remove(instance);

            if (ReferenceEquals(_current, instance))
            {
                _current = BuiltinInstance;
            }

            Changed?.Invoke(null, EventArgs.Empty);
        }

        private static string SizeOf(string version)
        {
            if (version == StagingTag)
            {
                return "192 MB";
            }

            TavernRelease? release = FindRelease(version);
            return release is { Size: > 0 } ? ComponentInstaller.FormatSize(release.Size) : "—";
        }

        /// <summary>
        /// <c>node_modules</c> 装好了没有。判据见 <see cref="BuiltinDepsReady"/>。
        /// </summary>
        private static bool DetectDepsReady()
        {
            try
            {
                string modules = Path.Combine(BuiltinInstallRoot, "node_modules");
                if (!Directory.Exists(modules))
                {
                    return false;
                }

                if (File.Exists(Path.Combine(modules, ".package-lock.json")))
                {
                    return true;
                }

                // 老 npm 不写隐藏锁文件：看两个运行时一定会用到的包
                return File.Exists(Path.Combine(modules, "express", "package.json"))
                    && File.Exists(Path.Combine(modules, "yaml", "package.json"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// 从内置目录的 <c>package.json</c> 读真实安装版本。
        /// 目录不存在、文件损坏、没有 version 字段都返回 null——宁可退回占位版本，
        /// 也不靠目录名猜一个可能对不上的数字。
        /// </summary>
        private static string? DetectInstalledVersion()
        {
            try
            {
                string manifest = Path.Combine(BuiltinInstallRoot, "package.json");
                if (!File.Exists(manifest))
                {
                    return null;
                }

                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest));
                if (!document.RootElement.TryGetProperty("version", out JsonElement version)
                    || version.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                string? value = version.GetString()?.Trim().TrimStart('v', 'V');
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }
    }
}
