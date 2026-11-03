using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AstraBrew_Launcher.Services
{
    /// <summary>酒馆实例的来源：启动器自带的一份，或用户自己在磁盘上的目录。</summary>
    public enum TavernInstanceKind
    {
        Builtin,
        Local,
    }

    /// <summary>
    /// 把一份目录加进本地实例列表的结果。判定顺序：路径无效 → 是内置实例 →
    /// 目录不存在 → 已在列表里 → package.json 不认。
    ///
    /// 用枚举而不是 bool，是为了让界面按情况挑文案：选到内置实例和「这不是酒馆」
    /// 该说的话完全不同，解析异常字符串来分辨是个坏主意。
    /// </summary>
    public enum LocalAddStatus
    {
        Added,
        Duplicate,
        Builtin,
        NotTavern,
        Missing,
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

        /// <summary>纯版本号，不带 v 前缀；内置实例也可能是 "staging"；读不到时为空串。</summary>
        public string Version { get; internal set; }

        /// <summary>代码目录的绝对路径，永远指向 <c>package.json</c> 所在的那一层。</summary>
        public string Path { get; }

        /// <summary>启动这份实例时会用到的 Node.js。启动器只有一套运行环境，因此各行相同。</summary>
        public string Node { get; internal set; }

        public string Size { get; internal set; }

        /// <summary>Node 依赖是否就绪；本地实例可能缺失，内置实例恒为 true。</summary>
        public bool DepsReady { get; internal set; }

        public bool IsBuiltin => Kind == TavernInstanceKind.Builtin;

        public string KindLabel => IsBuiltin ? "内置" : "本地";

        /// <summary>这份实例在磁盘上是否真的存在（内置实例看安装目录，本地实例恒为 true）。</summary>
        public bool IsInstalled => !IsBuiltin || TavernInstances.BuiltinInstalled;

        /// <summary>形如 "v1.19.2"；开发版通道显示为 "开发版"；没装就是 "未安装"。</summary>
        public string KernelLabel => !IsInstalled
            ? "未安装"
            : Version == TavernInstances.StagingTag
                ? "开发版"
                : Version.Length == 0
                    ? "版本未知"
                    : "v" + Version;

        /// <summary>下拉框里的名字，形如 "内置酒馆" / "本地-1.19.0"。</summary>
        public string DisplayName => IsBuiltin
            ? "内置酒馆"
            : Version.Length == 0 ? "本地实例" : $"本地-{Version}";

        /// <summary>标题栏徽章文案，形如 "内置-1.19.2" / "本地-1.19.0"；没装时是 "内置-未安装"。</summary>
        public string DisplayLabel => IsInstalled
            ? $"{KindLabel}-{(Version.Length == 0 ? "版本未知" : Version)}"
            : $"{KindLabel}-未安装";
    }

    /// <summary>
    /// 全应用唯一的实例清单与「当前使用哪一份」的状态。
    ///
    /// 仪表盘的版本下拉、版本管理页的两个页签、标题栏徽章、关于页的版本信息
    /// 全部读写这一份数据：任何一处切换实例都只改这里，再通过 <see cref="Changed"/>
    /// 让所有在界面的页面刷新，从结构上消除「两处各存一份、互相不知道」的同步问题。
    ///
    /// 本地实例是**真实目录**：进列表的唯一条件是目录里的 <c>package.json</c> 的
    /// <c>name</c> 等于 <c>sillytavern</c>（判据只在 <see cref="InstanceScanner.TryReadManifest"/>），
    /// 版本与依赖状态每次都以磁盘为准，不缓存「上次看到的样子」。
    /// 清单落盘在 <c>data/local_instances.json</c>，只存路径——版本、依赖这些每次重读，
    /// 免得用户手动 <c>git pull</c> 之后界面还显示旧版本。
    ///
    /// 线程约定：除静态构造期间的读取外，所有修改方法都要在 UI 线程调用（会触发 <see cref="Changed"/>）。
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

        /// <summary>Node.js 还没探测出来时显示的占位文案。</summary>
        public const string PendingRuntime = "Node 未检测";

        /// <summary>本地实例清单的落盘位置：<c>%AppData%\AstraBrew Launcher\data\local_instances.json</c>。</summary>
        public static string LocalStorePath { get; } =
            Path.Combine(AppPaths.RoamingRoot, "data", "local_instances.json");

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
        public static bool BuiltinDepsReady { get; private set; } = DetectDeps(BuiltinInstallRoot);

        /// <summary>清单或当前选择发生变化。</summary>
        public static event EventHandler? Changed;

        private static readonly TavernInstance BuiltinInstance = new(
            TavernInstanceKind.Builtin,
            DetectInstalledVersion() ?? DefaultVersion,
            BuiltinInstallRoot,
            PendingRuntime,
            "—",
            depsReady: BuiltinDepsReady);

        /// <summary>本地实例，顺序即列表顺序（加入的先后）。</summary>
        private static readonly List<TavernInstance> LocalList = new();

        /// <summary>内置实例恒在索引 0，与本地实例的先后顺序固定，下拉框顺序因此稳定。</summary>
        private static readonly List<TavernInstance> AllList = new() { BuiltinInstance };

        private static TavernInstance _current = BuiltinInstance;

        static TavernInstances()
        {
            LoadLocals();
            AllList.AddRange(LocalList);
        }

        /// <summary>内置实例（唯一一份）。</summary>
        public static TavernInstance Builtin => BuiltinInstance;

        /// <summary>GitHub 发布列表，空列表表示还没取过（而不是「没有版本」）。</summary>
        public static IReadOnlyList<TavernRelease> Releases => _releases;

        /// <summary>发布列表里最新的一条稳定版；没取到列表时为 null。</summary>
        public static TavernRelease? LatestRelease
        {
            get
            {
                foreach (TavernRelease release in _releases)
                {
                    if (!release.Prerelease)
                    {
                        return release;
                    }
                }

                return null;
            }
        }

        /// <summary>
        /// 最新稳定版版本号。还没取到列表时退回内置实例自己的版本，
        /// 这样仪表盘与更新检查不会在启动瞬间误报「有新版本」。
        /// </summary>
        public static string LatestStable => LatestRelease?.Version ?? BuiltinInstance.Version;

        /// <summary>按版本号找发布记录；找不到返回 null（内置版本可能不在发布列表里）。</summary>
        public static TavernRelease? FindRelease(string version)
        {
            foreach (TavernRelease release in _releases)
            {
                if (string.Equals(release.Version, version, StringComparison.OrdinalIgnoreCase))
                {
                    return release;
                }
            }

            return null;
        }

        /// <summary>
        /// 写入刚取到的发布列表。内容与现有列表一致时不发通知，避免刷新后整页重建。
        /// </summary>
        public static void SetReleases(IReadOnlyList<TavernRelease> releases)
        {
            if (releases is null || releases.Count == 0)
            {
                return;
            }

            bool same = _releases.Count == releases.Count;
            if (same)
            {
                for (int i = 0; i < releases.Count; i++)
                {
                    if (!string.Equals(_releases[i].Tag, releases[i].Tag, StringComparison.Ordinal))
                    {
                        same = false;
                        break;
                    }
                }
            }

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

        // ==================== 本地实例的增删 ====================

        /// <summary>
        /// 把一个目录加进本地实例列表。
        ///
        /// 准入条件与扫描完全一致（<see cref="InstanceScanner.TryReadManifest"/>），
        /// 因此手动导入与全盘扫描不会出现「扫描能进、导入不能进」这种两套标准。
        /// 内置实例目录直接拒绝：它已经由「内置实例」页签托管，再加一份只会出现
        /// 两个入口指向同一份代码，切来切去自己也分不清当前用的是哪个。
        ///
        /// 必须在 UI 线程调用（会触发 <see cref="Changed"/>）。
        /// </summary>
        public static LocalAddStatus AddLocal(string path, out TavernInstance? added)
        {
            added = null;

            if (string.IsNullOrWhiteSpace(path))
            {
                return LocalAddStatus.Missing;
            }

            string full;
            try
            {
                full = PathMatch.Normalize(path);
            }
            catch (Exception ex) when (ex is ArgumentException
                or NotSupportedException
                or PathTooLongException
                or IOException)
            {
                return LocalAddStatus.Missing;
            }

            if (IsBuiltinPath(full))
            {
                return LocalAddStatus.Builtin;
            }

            if (!Directory.Exists(full))
            {
                return LocalAddStatus.Missing;
            }

            if (FindLocal(full) is not null)
            {
                return LocalAddStatus.Duplicate;
            }

            if (!InstanceScanner.TryReadManifest(full, out string version))
            {
                return LocalAddStatus.NotTavern;
            }

            TavernInstance instance = CreateLocal(full, version);
            LocalList.Add(instance);
            AllList.Add(instance);

            SaveLocal();
            added = instance;
            Changed?.Invoke(null, EventArgs.Empty);

            return LocalAddStatus.Added;
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

            SaveLocal();
            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>
        /// 按磁盘现状重读一份本地实例：版本、依赖状态。
        ///
        /// 用户可能刚在目录里 <c>git pull</c> 过、或跑过一次 npm install，界面上那份
        /// 「上次看到的样子」这时就是错的。目录已经不认了（被删、被换成别的东西）就顺手移除。
        /// </summary>
        public static void ReloadLocal(TavernInstance instance)
        {
            if (instance is null || instance.IsBuiltin)
            {
                return;
            }

            if (!InstanceScanner.TryReadManifest(instance.Path, out string version))
            {
                RemoveLocal(instance);
                return;
            }

            instance.Version = version;
            instance.DepsReady = DetectDeps(instance.Path);
            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>切换实例后确认「当前」是否还指向一个真实存在的目录；不存在就回落到内置实例。</summary>
        public static void DropMissingLocals()
        {
            bool removed = false;
            for (int i = LocalList.Count - 1; i >= 0; i--)
            {
                TavernInstance instance = LocalList[i];
                if (Directory.Exists(instance.Path)
                    && InstanceScanner.TryReadManifest(instance.Path, out _))
                {
                    continue;
                }

                LocalList.RemoveAt(i);
                AllList.Remove(instance);
                removed = true;
            }

            if (!removed)
            {
                return;
            }

            if (!_current.IsBuiltin && !LocalList.Contains(_current))
            {
                _current = BuiltinInstance;
            }

            SaveLocal();
            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>路径是否落在内置实例目录里（含它的子目录）。</summary>
        public static bool IsBuiltinPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                return PathMatch.IsUnder(path, BuiltinInstallRoot);
            }
            catch (Exception ex) when (ex is ArgumentException
                or NotSupportedException
                or PathTooLongException)
            {
                return false;
            }
        }

        /// <summary>
        /// 把探测到的 Node.js 版本铺到所有实例上（各行共用同一套运行环境）。
        /// 探测是异步的，页面拿到结果后调这里；探不到保持「Node 未检测」。
        /// </summary>
        public static void SetRuntimeLabel(string? version)
        {
            string label = string.IsNullOrWhiteSpace(version) ? PendingRuntime : "Node " + version.Trim();

            bool changed = !string.Equals(BuiltinInstance.Node, label, StringComparison.Ordinal);
            BuiltinInstance.Node = label;

            foreach (TavernInstance instance in LocalList)
            {
                if (string.Equals(instance.Node, label, StringComparison.Ordinal))
                {
                    continue;
                }

                instance.Node = label;
                changed = true;
            }

            if (changed)
            {
                Changed?.Invoke(null, EventArgs.Empty);
            }
        }

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
            BuiltinDepsReady = BuiltinInstalled && DetectDeps(BuiltinInstallRoot);
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

        // ==================== 内部 ====================

        private static TavernInstance? FindLocal(string normalizedPath)
        {
            foreach (TavernInstance instance in LocalList)
            {
                if (PathMatch.IsSame(instance.Path, normalizedPath))
                {
                    return instance;
                }
            }

            return null;
        }

        private static TavernInstance CreateLocal(string path, string version) =>
            new(
                TavernInstanceKind.Local,
                version,
                path,
                BuiltinInstance.Node,
                "—",
                DetectDeps(path));

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
        /// 某个目录的 <c>node_modules</c> 装好了没有。判据见 <see cref="BuiltinDepsReady"/>。
        /// </summary>
        private static bool DetectDeps(string root)
        {
            try
            {
                string modules = Path.Combine(root, "node_modules");
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

        // ==================== 落盘 ====================

        /// <summary>
        /// 读回上次的本地实例清单。只认路径：版本、依赖一律现读磁盘，
        /// 因此文件被改坏、目录被删、或在两版之间换了目录名，都不至于把界面带偏。
        /// </summary>
        private static void LoadLocals()
        {
            try
            {
                if (!File.Exists(LocalStorePath))
                {
                    return;
                }

                LocalInstanceFile? file = JsonSerializer.Deserialize(
                    File.ReadAllText(LocalStorePath, Encoding.UTF8),
                    LocalInstanceJsonContext.Default.LocalInstanceFile);

                if (file?.Instances is null)
                {
                    return;
                }

                foreach (LocalInstanceEntry entry in file.Instances)
                {
                    if (string.IsNullOrWhiteSpace(entry.Path))
                    {
                        continue;
                    }

                    string full;
                    try
                    {
                        full = PathMatch.Normalize(entry.Path);
                    }
                    catch (Exception ex) when (ex is ArgumentException
                        or NotSupportedException
                        or PathTooLongException
                        or IOException)
                    {
                        continue;
                    }

                    if (IsBuiltinPath(full) || FindLocal(full) is not null)
                    {
                        continue;
                    }

                    // 目录没了、或者被换成了别的东西：不进列表，也不报错——
                    // 用户自己移动过目录是很正常的事
                    if (!InstanceScanner.TryReadManifest(full, out string version))
                    {
                        continue;
                    }

                    LocalList.Add(CreateLocal(full, version));
                }
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or JsonException)
            {
                // 清单坏了就当没有：下次加入实例时会重写一份
            }
        }

        private static void SaveLocal()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LocalStorePath)!);

                var file = new LocalInstanceFile();
                foreach (TavernInstance instance in LocalList)
                {
                    file.Instances.Add(new LocalInstanceEntry { Path = instance.Path });
                }

                string json = JsonSerializer.Serialize(
                    file,
                    LocalInstanceJsonContext.Default.LocalInstanceFile);

                // 先写临时文件再换名：写一半崩掉不会留下一个解析不了的清单
                string temporary = LocalStorePath + ".tmp";
                File.WriteAllText(temporary, json, new UTF8Encoding(false));
                File.Move(temporary, LocalStorePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 写不进去只影响下次启动要不要重新扫描，不该让「加入列表」失败
            }
        }
    }

    internal sealed class LocalInstanceFile
    {
        [JsonPropertyName("instances")]
        public List<LocalInstanceEntry> Instances { get; set; } = new();
    }

    internal sealed class LocalInstanceEntry
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;
    }

    /// <summary>
    /// 本地实例清单的序列化上下文。用源生成而不是反射：Release 开了裁剪，
    /// 反射版 <c>JsonSerializer</c> 裁剪后会在运行时找不到元数据。
    /// </summary>
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(LocalInstanceFile))]
    internal partial class LocalInstanceJsonContext : JsonSerializerContext
    {
    }
}
