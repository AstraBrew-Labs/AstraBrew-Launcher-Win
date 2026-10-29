using System;

namespace AstraBrew_Launcher.Services
{
    /// <summary>酒馆实例的来源：启动器自带的一份，或用户自己在磁盘上的目录。</summary>
    public enum TavernInstanceKind
    {
        Builtin,
        Local,
    }

    /// <summary>
    /// 当前选中的酒馆实例（原型状态，不做持久化）。
    ///
    /// 版本管理页决定用哪一份实例、关于页标题旁展示它的来源与版本号，
    /// 两边读写同一份数据，避免出现「版本页说用本地、关于页说用内置」。
    /// </summary>
    public static class ActiveInstance
    {
        // 与版本管理页的初始状态保持一致：默认用内置实例的最新稳定版
        private static TavernInstanceKind _kind = TavernInstanceKind.Builtin;
        private static string _version = "1.19.2";

        public static event EventHandler? Changed;

        public static TavernInstanceKind Kind => _kind;

        /// <summary>纯版本号，不带 v 前缀，例如 "1.19.0"。</summary>
        public static string Version => _version;

        /// <summary>形如「内置-1.19.0」「本地-1.19.0」。</summary>
        public static string DisplayLabel => $"{KindLabel}-{_version}";

        public static string KindLabel => _kind == TavernInstanceKind.Builtin ? "内置" : "本地";

        /// <summary>切到某个实例；版本相同也允许切换来源。</summary>
        public static void Set(TavernInstanceKind kind, string version)
        {
            if (_kind == kind && string.Equals(_version, version, StringComparison.Ordinal))
            {
                return;
            }

            _kind = kind;
            _version = version;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }
}
