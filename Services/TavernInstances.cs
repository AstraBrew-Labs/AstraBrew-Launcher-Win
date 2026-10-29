using System;
using System.Collections.Generic;

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

        /// <summary>形如 "v1.19.2"；开发版通道显示为 "开发版"。</summary>
        public string KernelLabel => Version == TavernInstances.StagingTag ? "开发版" : "v" + Version;

        /// <summary>下拉框里的名字，形如 "内置酒馆" / "本地-1.19.0"。</summary>
        public string DisplayName => IsBuiltin ? "内置酒馆" : $"本地-{Version}";

        /// <summary>标题栏徽章文案，形如 "内置-1.19.2" / "本地-1.19.0"。</summary>
        public string DisplayLabel => $"{KindLabel}-{Version}";
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
        /// <summary>内置实例可切换的最新稳定版。</summary>
        public const string LatestStable = "1.19.2";

        /// <summary>开发版通道在版本号里的标记值。</summary>
        public const string StagingTag = "staging";

        /// <summary>可在内置实例中切换的稳定版；索引 0 恒为最新。</summary>
        public static readonly (string Version, string Date, string Size, string Files)[] StableReleases =
        {
            ("1.19.2", "2026-10-20", "186 MB", "1,284 个文件"),
            ("1.19.0", "2026-09-28", "184 MB", "1,271 个文件"),
            ("1.18.0", "2026-08-30", "181 MB", "1,246 个文件"),
            ("1.17.2", "2026-07-18", "178 MB", "1,208 个文件"),
        };

        /// <summary>清单或当前选择发生变化。</summary>
        public static event EventHandler? Changed;

        private static readonly TavernInstance BuiltinInstance = new(
            TavernInstanceKind.Builtin,
            LatestStable,
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AstraBrew Launcher",
                "sillytavern"),
            "Node 20.11.0",
            SizeOf(LatestStable),
            depsReady: true);

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

            _current = instance;
            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>更换内置实例的代码版本（覆盖式换版）。</summary>
        public static void SetBuiltinVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version) || BuiltinInstance.Version == version)
            {
                return;
            }

            BuiltinInstance.Version = version;
            BuiltinInstance.Size = SizeOf(version);
            Changed?.Invoke(null, EventArgs.Empty);
        }

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

            int index = Array.FindIndex(StableReleases, item => item.Version == version);
            return index >= 0 ? StableReleases[index].Size : "—";
        }
    }
}
