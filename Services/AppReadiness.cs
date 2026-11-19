using System;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 全局「能不能用某个功能」的判据中心。
    ///
    /// 以前每个页面各写各的判断（仪表盘看 `TavernInstances.Current.IsInstalled`、
    /// 控制台自己猜、扩展页干脆不判断），结果同一个状态在不同页面上有不同结论：
    /// 有的按钮点了报错，有的按钮干脆没反应。
    ///
    /// 这里只回答三个问题，界面按答案决定「禁用 + 说明」还是「可用」：
    ///   · <see cref="InstanceReady"/>：当前实例装好了、依赖也装齐（能真正跑起来）
    ///   · <see cref="RuntimeReady"/>：运行环境有 Node + npm（没有就起不来）
    ///   · <see cref="ConfigReady"/>：有酒馆配置文件（没有就没法改配置 / 装扩展）
    ///
    /// 约定：
    ///   · 只读，不改任何设置；刷新只由启动流程与实例 / 设置变更事件触发；
    ///   · 任何一项都可能先按「可用」显示，探测完再翻——首帧就画成灰色会让人以为坏了；
    ///   · 判定变了发 <see cref="Changed"/>，订阅方自己决定怎么画。
    /// </summary>
    public static class AppReadiness
    {
        /// <summary>实例可用：磁盘上有这一份，且 Node 依赖装齐了。</summary>
        public static bool InstanceReady { get; private set; } = true;

        /// <summary>运行环境可用：Node 与 npm 都在。</summary>
        public static bool RuntimeReady { get; private set; } = true;

        /// <summary>全局酒馆配置文件存在。</summary>
        public static bool ConfigReady { get; private set; } = true;

        /// <summary>任意一项变化。</summary>
        public static event EventHandler? Changed;

        /// <summary>实例 / 运行环境都齐了，「启动酒馆」这类操作才有意义。</summary>
        public static bool CanRunTavern => InstanceReady && RuntimeReady;

        /// <summary>
        /// 只刷新本机磁盘就能判断的两项（实例、配置文件）。不探测运行环境——
        /// 那要起子进程，不能挂在每次实例切换上。
        /// </summary>
        public static void RefreshLocal()
        {
            bool instance = TavernInstances.Current is { IsInstalled: true, DepsReady: true };
            bool config = GlobalTavernConfig.HasGlobalConfig;

            if (instance == InstanceReady && config == ConfigReady)
            {
                return;
            }

            InstanceReady = instance;
            ConfigReady = config;
            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>完整刷新一次：实例 + 配置 + 运行环境（环境探测有 30 秒缓存）。</summary>
        public static async Task RefreshAsync(bool force = false)
        {
            bool instance = TavernInstances.Current is { IsInstalled: true, DepsReady: true };
            bool config = GlobalTavernConfig.HasGlobalConfig;
            bool runtime = RuntimeReady;

            try
            {
                RuntimeEnvironment env = await RuntimeProbe.ProbeAsync(LauncherSettings.Runtime, force);
                runtime = env.Node is not null && env.Npm is not null;
            }
            catch (Exception)
            {
                // 探测失败按「可用」处理：宁可不拦，也不要因为探测本身出错把功能全锁死
            }

            if (instance == InstanceReady && config == ConfigReady && runtime == RuntimeReady)
            {
                return;
            }

            InstanceReady = instance;
            ConfigReady = config;
            RuntimeReady = runtime;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }
}
