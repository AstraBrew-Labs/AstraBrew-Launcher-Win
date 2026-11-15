using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.Globalization;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 界面文案的唯一入口（代码侧）。
    ///
    /// XAML 里的静态文字走 <c>x:Uid</c> + <c>Strings/&lt;语言&gt;/Resources.resw</c>；
    /// 代码里拼出来的句子（通知、弹窗标题、进度、错误）一律从这里取，**不要再写字面量**。
    /// 两套 key 共用同一个命名空间（<c>模块.元素.属性</c>），审计脚本才好查。
    ///
    /// 缺 key 时不静默：返回 key 原文（界面上会刺眼地显示 <c>Notify.Foo.Title</c>），
    /// DEBUG 下再写一行 i18n.log。宁可难看也不能让拼错藏在暗处。
    /// </summary>
    public static class L
    {
        /// <summary>resw 文件会被 MRT 加上「文件名/」前缀，所以真实 key 是 Resources.xxx。</summary>
        private const string MapPrefix = "Resources/";

        private static readonly object Gate = new();

        private static ResourceMap? _map;

        /// <summary>资源管理器留着，切换语言时要拿它重新建上下文。</summary>
        private static ResourceManager? _manager;

        /// <summary>
        /// 本轮解析用的资源上下文。**语言就挂在这个上下文上**：
        /// <c>QualifierValues["Language"]</c> 改掉之后，下一次 <c>GetValue</c> 立刻走新语言，
        /// 不用重启进程、不用重建窗口，界面自己重刷就能实时换过来。
        /// </summary>
        private static ResourceContext? _context;

        /// <summary>当前实际生效的语言标签（zh-CN / en-US）。</summary>
        public static string CurrentTag { get; private set; } = LanguageResolver.Chinese;

        /// <summary>英文界面下做长度/字号微调时用。</summary>
        public static bool IsEnglish => CurrentTag.StartsWith("en", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 按设置里的语言重建资源上下文。**必须在任何 XAML 解析之前调用**，
        /// 否则首帧会用上一次的语言（x:Uid 在元素创建时就解析掉了）。
        /// </summary>
        /// <returns>成功为 true；失败时保持上一次的语言，调用方应提示「重启后生效」。</returns>
        public static bool Initialize()
        {
            string tag = LanguageResolver.Resolve(LauncherSettings.Language);
            return Initialize(tag);
        }

        /// <summary>指定语言初始化。进程启动时调一次即可。</summary>
        public static bool Initialize(string tag) => ApplyTag(tag, forceNewContext: true);

        /// <summary>
        /// 运行中换语言：只改资源上下文的语言限定符，**不重建任何东西**。
        /// 调用方随后重刷界面（<see cref="Views.Controls.Loc.RefreshAll"/>）即可实时切换。
        /// </summary>
        /// <returns>失败时保持原语言，界面仍是旧文案（调用方自行决定怎么兜底）。</returns>
        public static bool SetLanguage(string tag) => ApplyTag(tag, forceNewContext: false);

        private static bool ApplyTag(string tag, bool forceNewContext)
        {
            lock (Gate)
            {
                try
                {
                    // 系统对话框（文件选择器、ContentDialog 的默认按钮）也会跟着换。
                    // 无包身份下这里可能抛，抛了也不影响下面自己控制的那条路径。
                    try
                    {
                        ApplicationLanguages.PrimaryLanguageOverride = tag;
                    }
                    catch (Exception)
                    {
                        // 忽略：资源解析不依赖它
                    }

                    _manager ??= CreateManager();
                    _map = _manager.MainResourceMap;

                    if (forceNewContext || _context is null)
                    {
                        _context = _manager.CreateResourceContext();
                    }

                    // 这就是「实时切换」的全部秘密：改限定符，下一次取值就是新语言。
                    _context.QualifierValues["Language"] = tag;
                    CurrentTag = tag;

                    // 日期、数字、货币格式跟着界面语言走：英文界面里不该出现「2026/9/14」。
                    var culture = new CultureInfo(tag);
                    CultureInfo.DefaultThreadCurrentCulture = culture;
                    CultureInfo.DefaultThreadCurrentUICulture = culture;
                    CultureInfo.CurrentCulture = culture;
                    CultureInfo.CurrentUICulture = culture;

                    Probe($"apply tag={tag} resources={_map.ResourceCount}");
                    return true;
                }
                catch (Exception ex)
                {
                    Probe($"apply failed tag={tag}: {ex.GetType().Name}: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// 取一条文案。**永不抛、永不返回 null**：这是界面线程与后台线程共用的路径，
        /// 一条文案缺失不该让启动器崩掉。
        /// </summary>
        public static string Get(string key)
        {
            string? value = Lookup(key);
            if (value is not null)
            {
                return value;
            }

            Probe($"missing key: {key} (lang={CurrentTag})");
            return key;
        }

        /// <summary>可选条目：缺失时返回空串，交给控件自己隐藏（例如 <see cref="Views.Controls.SettingRow.Description"/>）。</summary>
        public static string GetOrEmpty(string key) => Lookup(key) ?? string.Empty;

        /// <summary>
        /// 带参数的句式：resw 里写完整的一句话（<c>{0}</c> / <c>{1}</c>），
        /// 不要在代码里做「前半句 + 变量 + 后半句」的拼接——英文语序不一样，拼出来一定是错的。
        /// </summary>
        public static string F(string key, params object?[] args)
        {
            string format = Get(key);

            try
            {
                return string.Format(CultureInfo.CurrentCulture, format, args);
            }
            catch (FormatException ex)
            {
                // 占位符写错（数量对不上）：保留原文，别让界面变成异常
                Probe($"bad format: {key} ({ex.Message})");
                return format;
            }
        }

        private static ResourceManager CreateManager()
        {
            try
            {
                return new ResourceManager();
            }
            catch (Exception)
            {
                // unpackaged / 非默认输出布局下找不到同名索引时，显式指到 exe 旁边的 PRI
                string pri = Path.Combine(AppContext.BaseDirectory, "resources.pri");
                return new ResourceManager(pri);
            }
        }

        /// <summary>查一条资源；找不到返回 null。加锁：AppNotify 会从后台续体调用。</summary>
        private static string? Lookup(string key)
        {
            if (key.Length == 0)
            {
                return null;
            }

            lock (Gate)
            {
                if (_map is null)
                {
                    return null;
                }

                // MRT 在编 PRI 时把 key 里的点当作层级分隔（dump 出来是 Shell/Nav/About），
                // 所以点写法与斜杠写法都要试一遍，别猜哪个能命中。
                string slash = key.Replace('.', '/');

                foreach (string candidate in new[] { MapPrefix + key, MapPrefix + slash, key, slash })
                {
                    try
                    {
                        // 用 GetValue + 捕获：键不存在时 MRT 抛 COMException，
                        // 这里的 try/catch 就是「找不到」的判定，别指望返回 null。
                        // 带上下文的那次调用才认 QualifierValues 里的语言。
                        ResourceCandidate hit = _context is null
                            ? _map.GetValue(candidate)
                            : _map.GetValue(candidate, _context);
                        string value = hit.ValueAsString;
                        if (value.Length > 0)
                        {
                            return value;
                        }
                    }
                    catch (Exception)
                    {
                        // 单条查询失败不该中断整个界面：换下一个候选
                    }
                }
            }

            return null;
        }

        /// <summary>调试探针：只在 DEBUG 落一行到 %Temp%\astrabrew-launcher\i18n.log。</summary>
        [Conditional("DEBUG")]
        private static void Probe(string message)
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "astrabrew-launcher");
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, "i18n.log"),
                    $"[{DateTimeOffset.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
            catch
            {
                // 探针失败不影响功能
            }
        }
    }
}
