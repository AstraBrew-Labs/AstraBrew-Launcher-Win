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

    /// <summary>
    /// 设置页的呈现版本：小白版只留日常选项，高手版是完整清单。
    ///
    /// 两版共用同一份状态——切版本只改可见性，不改任何值；因此在小版里看不到的项
    /// 仍按原有设定生效（例如隐藏的「数据存放」不会因此被重置）。
    /// </summary>
    public enum SettingsLevel
    {
        Simple,
        Advanced,
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

    /// <summary>
    /// 取酒馆源码时走的仓库。顺序即分段切换器里的顺序，不要随意调整。
    /// 两个镜像都在国内、直连 GitHub 十次有八次超时，所以默认是 GitCode。
    /// </summary>
    public enum DownloadChannel
    {
        Gitee,
        GitCode,
        Github,
    }

    /// <summary>访问 GitHub 时的代理方式。</summary>
    public enum ProxyModeSetting
    {
        None,
        System,
        Custom,
    }

    /// <summary>
    /// 一次窗口几何快照（物理像素），退出时记录、下次启动时还原。
    /// 单位为像素是因为「窗口在哪块屏」只能靠像素坐标判断；<see cref="Dpi"/> 用来在
    /// 显示器缩放变了之后按比例折算尺寸，避免 125% 存的窗口在 150% 下变得又小又远。
    /// </summary>
    public readonly record struct WindowBounds(int X, int Y, int Width, int Height, int Dpi);

    /// <summary>
    /// 启动器自身的偏好设置。
    ///
    /// 与 <see cref="TavernSettings"/> 同一套路：静态类持有一份状态，
    /// 任何字段变更都通过 <see cref="Changed"/> 通知，页面只负责把状态画出来。
    /// 落盘由 <see cref="SettingsStore"/> 统一负责（%AppData%\AstraBrew Launcher\settings.toml），
    /// 页面不直接读写文件。
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

        /// <summary>设置页版本：小白版 / 高手版。</summary>
        public static SettingsLevel Level => _level;

        /// <summary>是否处于小白版。</summary>
        public static bool IsSimpleLevel => _level == SettingsLevel.Simple;

        /// <summary>界面缩放，1.0 表示 100%。</summary>
        public static double UiScale => _uiScale;

        /// <summary>
        /// 界面字体：<see cref="FontCatalog.EmbeddedId"/> 表示随包分发的内置字体（出厂默认），
        /// null 表示跟随系统，其余为本机字体族名。
        /// </summary>
        public static string? FontFamily => _fontFamily;

        /// <summary>退出时记住窗口大小与位置。</summary>
        public static bool RememberWindow => _rememberWindow;

        /// <summary>上次退出时的窗口几何；null 表示还没记录过。</summary>
        public static WindowBounds? WindowRect => _windowRect;

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

        /// <summary>取酒馆源码的仓库。</summary>
        public static DownloadChannel Channel => _channel;

        /// <summary>
        /// GitHub 加速开关：打开后所有 GitHub 链接（版本列表接口、Git 安装包、扩展克隆、
        /// Raw 资源等）都经 <c>https://gh-proxy.org/</c> 中转，与下载源选的是镜像还是直连无关。
        /// 唯一的例外是内置实例克隆仓库——那里只有下载源选「GitHub 直连」才走加速。
        /// </summary>
        public static bool GithubAcceleration => _githubAcceleration;

        /// <summary>代理方式。</summary>
        public static ProxyModeSetting Proxy => _proxy;

        /// <summary>自定义代理地址。</summary>
        public static string CustomProxy => _customProxy;

        /// <summary>上次 GitHub 连通性测试的最佳延迟（毫秒），null 表示没测过。</summary>
        public static int? GithubBestLatency => _githubBestLatency;

        /// <summary>当前选择实际会连的仓库地址（算上加速开关）。</summary>
        public static string SourceUrl => TavernSource.ActiveUrl;

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
            DownloadChannel.Gitee => "Gitee 镜像",
            DownloadChannel.GitCode => "GitCode 镜像",
            _ => "GitHub 直连",
        };

        public static string ChannelHint(DownloadChannel channel) => channel switch
        {
            DownloadChannel.Gitee => "AstraBrew 自建镜像，国内直连，含完整提交历史",
            DownloadChannel.GitCode => "GitCode 同步镜像，国内直连；默认通道",
            _ => "官方仓库，国内建议同时打开「GitHub 加速服务」",
        };

        public static void SetTheme(AppTheme value) => Set(ref _theme, value);

        public static void SetLanguage(AppLanguage value) => Set(ref _language, value);

        public static void SetLevel(SettingsLevel value) => Set(ref _level, value);

        public static void SetUiScale(double value)
        {
            // 滑杆按 5% 步进，这里再夹一次，避免界面放大到 140% 之后行内控件被压扁
            var clamped = Math.Clamp(Math.Round(value, 2), 0.9, 1.4);
            Set(ref _uiScale, clamped);
        }

        public static void SetFontFamily(string? value)
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            Set(ref _fontFamily, normalized);
        }

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

        public static void SetGithubAcceleration(bool value) => Set(ref _githubAcceleration, value);

        public static void SetProxy(ProxyModeSetting value) => Set(ref _proxy, value);

        public static void SetCustomProxy(string value) => Set(ref _customProxy, value);

        /// <summary>记录一次 GitHub 连通性测试结果。</summary>
        public static void SetGithubTestResult(int bestLatency)
        {
            _githubBestLatency = bestLatency;
            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>
        /// 记录窗口几何。静默写入：这个值只在退出时更新，
        /// 通知出去只会让页面白重绘一遍，还会连带触发一次配置回写。
        /// </summary>
        public static void SetWindowRect(WindowBounds? value) => _windowRect = value;

        /// <summary>
        /// 批量写入开始：期间所有 Set 都不发通知。
        /// 读配置文件时用 —— 二十多个字段逐个通知会拖出一连串无意义的重绘。
        /// </summary>
        public static void BeginUpdate() => _batchDepth++;

        /// <summary>批量写入结束：合并成一次通知。</summary>
        public static void EndUpdate()
        {
            if (_batchDepth == 0)
            {
                return;
            }

            _batchDepth--;
            if (_batchDepth == 0)
            {
                Changed?.Invoke(null, EventArgs.Empty);
            }
        }

        /// <summary>恢复出厂值：所有偏好回到首次启动时的状态。</summary>
        public static void Reset()
        {
            _theme = AppTheme.System;
            _language = AppLanguage.System;
            _level = SettingsLevel.Simple;
            _uiScale = 1.0;
            _fontFamily = FontCatalog.EmbeddedId;
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
            _channel = DownloadChannel.GitCode;
            _githubAcceleration = true;
            _proxy = ProxyModeSetting.System;
            _customProxy = "http://127.0.0.1:7890";
            _githubBestLatency = null;
            _windowRect = null;

            Changed?.Invoke(null, EventArgs.Empty);
        }

        // 属性用私有字段承载，写入口一律走 Set<T>，保证「值没变就不通知」只写一次。
        private static AppTheme _theme = AppTheme.System;
        private static AppLanguage _language = AppLanguage.System;
        private static SettingsLevel _level = SettingsLevel.Simple;
        private static double _uiScale = 1.0;
        private static string? _fontFamily = FontCatalog.EmbeddedId;
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
        private static DownloadChannel _channel = DownloadChannel.GitCode;
        private static bool _githubAcceleration = true;
        private static ProxyModeSetting _proxy = ProxyModeSetting.System;
        private static string _customProxy = "http://127.0.0.1:7890";
        private static int? _githubBestLatency;
        private static WindowBounds? _windowRect;

        /// <summary>批量写入深度；大于 0 时 Set 只改值不通知。</summary>
        private static int _batchDepth;

        private static void Set<T>(ref T field, T value)
        {
            if (Equals(field, value))
            {
                return;
            }

            field = value;
            if (_batchDepth == 0)
            {
                Changed?.Invoke(null, EventArgs.Empty);
            }
        }
    }
}
