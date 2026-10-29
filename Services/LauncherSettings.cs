using System;

namespace AstraBrew_Launcher.Services
{
    /// <summary>启动器的明暗外观。</summary>
    public enum AppTheme
    {
        System,
        Light,
        Dark,
    }

    /// <summary>界面语言。</summary>
    public enum AppLanguage
    {
        System,
        SimplifiedChinese,
        English,
    }

    /// <summary>启动酒馆时允许占用的 CPU 核心范围。</summary>
    public enum CpuCorePreference
    {
        Auto,
        Half,
        All,
    }

    /// <summary>用户点「启动」后的默认行为。</summary>
    public enum QuickStartMode
    {
        Normal,
        Desktop,
    }

    /// <summary>局域网内可见，还是经反代暴露到公网。</summary>
    public enum ServiceScopeSetting
    {
        Lan,
        Internet,
    }

    /// <summary>酒馆数据放在全局目录，还是跟着实例走。</summary>
    public enum DataLocationMode
    {
        Global,
        CurrentInstance,
    }

    /// <summary>依赖装在哪里：启动器自带的便携环境，还是系统里已有的那套。</summary>
    public enum RuntimeSource
    {
        Builtin,
        System,
    }

    /// <summary>npm 包源。</summary>
    public enum NpmRegistrySource
    {
        Npmmirror,
        HuaweiCloud,
        Tencent,
        Official,
    }

    /// <summary>下载 GitHub 资源时走的通道。</summary>
    public enum DownloadChannel
    {
        Auto,
        GhProxy,
        GhFast,
        Official,
    }

    /// <summary>访问 GitHub 时的代理方式。</summary>
    public enum ProxyModeSetting
    {
        None,
        System,
        Custom,
    }

    /// <summary>
    /// 启动器自身的偏好设置（原型状态，不做持久化）。
    ///
    /// 与 <see cref="TavernSettings"/> 同一套路：静态类持有一份状态，
    /// 任何字段变更都通过 <see cref="Changed"/> 通知，页面只负责把状态画出来。
    /// 这样设置页导航离开再回来，改动仍然在，而不是每次都被重置成硬编码值。
    /// </summary>
    public static class LauncherSettings
    {
        /// <summary>任一偏好发生变化。</summary>
        public static event EventHandler? Changed;

        private const string DefaultExportPath = @"%APPDATA%\AstraBrew Launcher\exports";
        private const string DefaultDataPath = @"%APPDATA%\AstraBrew Launcher\data";

        /// <summary>外观。</summary>
        public static AppTheme Theme => _theme;

        /// <summary>界面语言。</summary>
        public static AppLanguage Language => _language;

        /// <summary>界面缩放，1.0 表示 100%。</summary>
        public static double UiScale => _uiScale;

        /// <summary>字体方案在候选列表中的下标。</summary>
        public static int FontIndex => _fontIndex;

        /// <summary>退出时记住窗口大小与位置。</summary>
        public static bool RememberWindow => _rememberWindow;

        /// <summary>登录 Windows 后自动启动。</summary>
        public static bool AutoLaunch => _autoLaunch;

        /// <summary>扫盘 / 构建时的核心数策略。</summary>
        public static CpuCorePreference CpuCores => _cpuCores;

        /// <summary>默认启动方式。</summary>
        public static QuickStartMode StartMode => _startMode;

        /// <summary>关闭窗口时顺带停止酒馆服务。</summary>
        public static bool StopTavernOnWindowClose => _stopTavernOnWindowClose;

        /// <summary>资源导出目录。</summary>
        public static string ExportPath => _exportPath;

        /// <summary>服务器模式：允许局域网 / 公网访问。</summary>
        public static bool ServerMode => _serverMode;

        /// <summary>服务器模式下的可见范围。</summary>
        public static ServiceScopeSetting ServiceScope => _serviceScope;

        /// <summary>关闭窗口后酒馆继续在后台跑（依赖 PM2）。</summary>
        public static bool AllowBackground => _allowBackground;

        /// <summary>数据存放策略。</summary>
        public static DataLocationMode DataLocation => _dataLocation;

        /// <summary>全局数据目录。</summary>
        public static string GlobalDataPath => _globalDataPath;

        /// <summary>控制台里回显实际执行的启动命令。</summary>
        public static bool ShowStartupCommand => _showStartupCommand;

        /// <summary>依赖来源。</summary>
        public static RuntimeSource Runtime => _runtime;

        /// <summary>npm 包源。</summary>
        public static NpmRegistrySource NpmRegistry => _npmRegistry;

        /// <summary>GitHub 下载通道。</summary>
        public static DownloadChannel Channel => _channel;

        /// <summary>自动通道测速后解析出的具体通道。</summary>
        public static DownloadChannel ResolvedChannel => _resolvedChannel;

        /// <summary>是否已做过通道测速。</summary>
        public static bool ChannelTested => _channelTested;

        /// <summary>上次测速时间。</summary>
        public static DateTimeOffset? ChannelTestedAt => _channelTestedAt;

        /// <summary>代理方式。</summary>
        public static ProxyModeSetting Proxy => _proxy;

        /// <summary>自定义代理地址。</summary>
        public static string CustomProxy => _customProxy;

        /// <summary>上次 GitHub 连通性测试的最佳延迟（毫秒），null 表示没测过。</summary>
        public static int? GithubBestLatency => _githubBestLatency;

        /// <summary>当前选择实际生效的下载通道（自动模式下解析成具体通道）。</summary>
        public static DownloadChannel EffectiveChannel =>
            Channel == DownloadChannel.Auto ? ResolvedChannel : Channel;

        public static string NpmRegistryUrl => NpmRegistry switch
        {
            NpmRegistrySource.HuaweiCloud => "https://repo.huaweicloud.com/repository/npm/",
            NpmRegistrySource.Tencent => "https://mirrors.cloud.tencent.com/npm/",
            NpmRegistrySource.Official => "https://registry.npmjs.org",
            _ => "https://registry.npmmirror.com",
        };

        public static string NpmRegistryName => NpmRegistry switch
        {
            NpmRegistrySource.HuaweiCloud => "华为云",
            NpmRegistrySource.Tencent => "腾讯云",
            NpmRegistrySource.Official => "npm 官方",
            _ => "npmmirror",
        };

        public static string ChannelName(DownloadChannel channel) => channel switch
        {
            DownloadChannel.GhProxy => "gh-proxy.org",
            DownloadChannel.GhFast => "ghfast.top",
            DownloadChannel.Official => "GitHub 直连",
            _ => "自动",
        };

        public static string ChannelHint(DownloadChannel channel) => channel switch
        {
            DownloadChannel.GhProxy => "公益反代，国内直连快，偶发 429 限流",
            DownloadChannel.GhFast => "备用反代，晚高峰略慢，限流更少",
            DownloadChannel.Official => "无中间节点，仅在有代理时可用",
            _ => "先测速，再挑当前最快的通道",
        };

        /// <summary>
        /// 测速结果有效期：15 分钟内视为有效。
        /// 过期不自动重测，只在设置页把状态标成「已过期」，由用户决定要不要重测。
        /// </summary>
        public static bool IsChannelCacheValid =>
            ChannelTested
            && ChannelTestedAt is { } tested
            && DateTimeOffset.Now - tested < TimeSpan.FromMinutes(15);

        public static void SetTheme(AppTheme value) => Set(ref _theme, value);

        public static void SetLanguage(AppLanguage value) => Set(ref _language, value);

        public static void SetUiScale(double value)
        {
            // 滑杆按 5% 步进，这里再夹一次，避免界面放大到 140% 之后行内控件被压扁
            var clamped = Math.Clamp(Math.Round(value, 2), 0.9, 1.4);
            Set(ref _uiScale, clamped);
        }

        public static void SetFontIndex(int value) => Set(ref _fontIndex, Math.Clamp(value, 0, 2));

        public static void SetRememberWindow(bool value) => Set(ref _rememberWindow, value);

        public static void SetAutoLaunch(bool value) => Set(ref _autoLaunch, value);

        public static void SetCpuCores(CpuCorePreference value) => Set(ref _cpuCores, value);

        public static void SetStartMode(QuickStartMode value) => Set(ref _startMode, value);

        public static void SetStopTavernOnWindowClose(bool value) => Set(ref _stopTavernOnWindowClose, value);

        public static void SetExportPath(string value) => Set(ref _exportPath, value);

        public static void SetServerMode(bool value) => Set(ref _serverMode, value);

        public static void SetServiceScope(ServiceScopeSetting value) => Set(ref _serviceScope, value);

        public static void SetAllowBackground(bool value) => Set(ref _allowBackground, value);

        public static void SetDataLocation(DataLocationMode value) => Set(ref _dataLocation, value);

        public static void SetGlobalDataPath(string value) => Set(ref _globalDataPath, value);

        public static void SetShowStartupCommand(bool value) => Set(ref _showStartupCommand, value);

        public static void SetRuntime(RuntimeSource value) => Set(ref _runtime, value);

        public static void SetNpmRegistry(NpmRegistrySource value) => Set(ref _npmRegistry, value);

        public static void SetChannel(DownloadChannel value) => Set(ref _channel, value);

        public static void SetProxy(ProxyModeSetting value) => Set(ref _proxy, value);

        public static void SetCustomProxy(string value) => Set(ref _customProxy, value);

        /// <summary>记录一次通道测速结果。</summary>
        public static void SetChannelTestResult(DownloadChannel resolved)
        {
            _resolvedChannel = resolved;
            _channelTested = true;
            _channelTestedAt = DateTimeOffset.Now;
            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>记录一次 GitHub 连通性测试结果。</summary>
        public static void SetGithubTestResult(int bestLatency)
        {
            _githubBestLatency = bestLatency;
            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>恢复出厂值：所有偏好回到首次启动时的状态。</summary>
        public static void Reset()
        {
            _theme = AppTheme.System;
            _language = AppLanguage.System;
            _uiScale = 1.0;
            _fontIndex = 0;
            _rememberWindow = true;
            _autoLaunch = false;
            _cpuCores = CpuCorePreference.Auto;
            _startMode = QuickStartMode.Normal;
            _stopTavernOnWindowClose = true;
            _exportPath = DefaultExportPath;
            _serverMode = false;
            _serviceScope = ServiceScopeSetting.Lan;
            _allowBackground = true;
            _dataLocation = DataLocationMode.CurrentInstance;
            _globalDataPath = DefaultDataPath;
            _showStartupCommand = true;
            _runtime = RuntimeSource.Builtin;
            _npmRegistry = NpmRegistrySource.Npmmirror;
            _channel = DownloadChannel.Auto;
            _resolvedChannel = DownloadChannel.GhProxy;
            _channelTested = false;
            _channelTestedAt = null;
            _proxy = ProxyModeSetting.System;
            _customProxy = "http://127.0.0.1:7890";
            _githubBestLatency = null;

            Changed?.Invoke(null, EventArgs.Empty);
        }

        // 属性用私有字段承载，写入口一律走 Set<T>，保证「值没变就不通知」只写一次。
        private static AppTheme _theme = AppTheme.System;
        private static AppLanguage _language = AppLanguage.System;
        private static double _uiScale = 1.0;
        private static int _fontIndex;
        private static bool _rememberWindow = true;
        private static bool _autoLaunch;
        private static CpuCorePreference _cpuCores = CpuCorePreference.Auto;
        private static QuickStartMode _startMode = QuickStartMode.Normal;
        private static bool _stopTavernOnWindowClose = true;
        private static string _exportPath = DefaultExportPath;
        private static bool _serverMode;
        private static ServiceScopeSetting _serviceScope = ServiceScopeSetting.Lan;
        private static bool _allowBackground = true;
        private static DataLocationMode _dataLocation = DataLocationMode.CurrentInstance;
        private static string _globalDataPath = DefaultDataPath;
        private static bool _showStartupCommand = true;
        private static RuntimeSource _runtime = RuntimeSource.Builtin;
        private static NpmRegistrySource _npmRegistry = NpmRegistrySource.Npmmirror;
        private static DownloadChannel _channel = DownloadChannel.Auto;
        private static DownloadChannel _resolvedChannel = DownloadChannel.GhProxy;
        private static bool _channelTested;
        private static DateTimeOffset? _channelTestedAt;
        private static ProxyModeSetting _proxy = ProxyModeSetting.System;
        private static string _customProxy = "http://127.0.0.1:7890";
        private static int? _githubBestLatency;

        private static void Set<T>(ref T field, T value)
        {
            if (Equals(field, value))
            {
                return;
            }

            field = value;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }
}
