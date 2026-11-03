using System;
using System.Collections.Generic;
using System.IO;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;

namespace AstraBrew_Launcher.Services
{
    /// <summary>启动模式：决定端口怎么暴露、以及是否走浏览器。</summary>
    public enum LaunchMode
    {
        Normal,
        Desktop,
        Server,
    }

    /// <summary>服务器模式下的可访问范围。</summary>
    public enum ServiceScope
    {
        Lan,
        Wan,
    }

    /// <summary>默认浏览器。<see cref="System"/> 对应官方配置里的 <c>default</c>（跟随系统）。</summary>
    public enum BrowserKind
    {
        Chrome,
        Edge,
        Firefox,
        System,
    }

    /// <summary>
    /// 跨页面共享的酒馆启动设置。
    ///
    /// 仪表盘、酒馆配置页、设置页读写的都是这一份：任一页面修改都会通过 <see cref="Changed"/>
    /// 通知其它页面刷新，启动器也按它决定怎么拉起酒馆——不存在「设置里选了桌面模式，
    /// 仪表盘还显示普通模式，启动时又按另一份来」。
    ///
    /// 启动模式与浏览器/服务范围的落点不同：
    /// · 启动模式存在 <c>settings.toml</c>（<see cref="LauncherSettings.StartMode"/>），
    ///   它是启动器自己的偏好；
    /// · 浏览器与服务范围属于酒馆配置，改动会写回 <c>config.yaml</c>。
    /// </summary>
    public static class TavernSettings
    {
        /// <summary>启动模式 / 默认浏览器 / 服务范围发生变化。</summary>
        public static event EventHandler? Changed;

        /// <summary>白名单发生增删。</summary>
        public static event EventHandler? WhitelistChanged;

        static TavernSettings()
        {
            // 设置页改「默认启动方式」写的是 LauncherSettings；这里补一次通知，
            // 让仪表盘与酒馆配置页的模式单选项跟着动
            LauncherSettings.Changed += (_, _) => Changed?.Invoke(null, EventArgs.Empty);

            LoadFromConfig();
        }

        /// <summary>
        /// 浏览器与服务范围的真相在 <c>config.yaml</c> 里（<c>browserLaunch.browser</c> /
        /// <c>listen</c>）。启动时先读一遍：否则仪表盘会按「默认谷歌浏览器、局域网」
        /// 显示，而文件里写的其实是 <c>default</c> 或 <c>listen: false</c>。
        /// </summary>
        private static void LoadFromConfig()
        {
            try
            {
                if (!GlobalTavernConfig.HasGlobalConfig)
                {
                    return;
                }

                Dictionary<string, string> scalars = YamlConfig.ReadScalars(GlobalTavernConfig.ReadGlobal());

                if (scalars.TryGetValue("browserLaunch.browser", out string? browser))
                {
                    _browser = browser.ToLowerInvariant() switch
                    {
                        "chrome" => BrowserKind.Chrome,
                        "edge" => BrowserKind.Edge,
                        "firefox" => BrowserKind.Firefox,
                        _ => BrowserKind.System,
                    };
                }

                if (scalars.TryGetValue("listen", out string? listen)
                    && bool.TryParse(listen, out bool allowed))
                {
                    _scope = allowed ? ServiceScope.Lan : ServiceScope.Wan;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 读不到就按默认值走，不影响启动
            }
        }

        private static BrowserKind _browser = BrowserKind.Chrome;
        private static ServiceScope _scope = ServiceScope.Lan;

        /// <summary>
        /// 启动模式。读写的是 <see cref="LauncherSettings.StartMode"/>（那份会被存进 settings.toml），
        /// 本类只是它的一层视图——两个枚举顺序一致，直接按序号换算。
        /// </summary>
        public static LaunchMode Mode
        {
            get => (LaunchMode)(int)LauncherSettings.StartMode;
            set
            {
                if (Mode == value)
                {
                    return;
                }

                LauncherSettings.SetStartMode((QuickStartMode)(int)value);
            }
        }

        public static BrowserKind Browser
        {
            get => _browser;
            set
            {
                if (_browser == value)
                {
                    return;
                }

                _browser = value;
                Changed?.Invoke(null, EventArgs.Empty);
            }
        }

        public static ServiceScope Scope
        {
            get => _scope;
            set
            {
                if (_scope == value)
                {
                    return;
                }

                _scope = value;
                Changed?.Invoke(null, EventArgs.Empty);
            }
        }

        /// <summary>只有普通模式才和浏览器相关，其余模式相关选项应当隐藏。</summary>
        public static bool IsNormalMode => Mode == LaunchMode.Normal;

        public static string ModeName => Mode switch
        {
            LaunchMode.Desktop => "桌面模式",
            LaunchMode.Server => "服务器模式",
            _ => "普通模式",
        };

        public static string BrowserName => _browser switch
        {
            BrowserKind.Edge => "Edge",
            BrowserKind.Firefox => "火狐浏览器",
            BrowserKind.System => "系统默认浏览器",
            _ => "谷歌浏览器",
        };

        /// <summary>
        /// 白名单：127.0.0.1 与 ::1 默认锁定，不可删除。
        ///
        /// 不用 localhost：SillyTavern 的白名单只认 IP / CIDR，写主机名会被它判为
        /// 「Invalid IP」并忽略，于是浏览器用 IPv6 回环打开时直接被挡在门外。
        /// </summary>
        public static ObservableCollection<WhitelistEntry> Whitelist { get; } = new()
        {
            new WhitelistEntry("127.0.0.1", true),
            new WhitelistEntry("::1", true),
            new WhitelistEntry("192.168.1.24", false),
        };

        /// <summary>可用作白名单项的主机名（允许短横线，不允许首尾符号）。</summary>
        private static readonly Regex HostPattern = new(
            "^[a-zA-Z0-9]([a-zA-Z0-9\\-\\.]{0,62}[a-zA-Z0-9])?$",
            RegexOptions.Compiled);

        /// <summary>
        /// 添加白名单项。格式非法或与已有项重复时返回 false，并给出可直接展示的提示。
        /// </summary>
        public static bool TryAddWhitelist(string? raw, out string error)
        {
            var value = (raw ?? string.Empty).Trim();

            if (value.Length == 0)
            {
                error = "请输入 IP 或主机名后再添加。";
                return false;
            }

            var isIp = IPAddress.TryParse(value, out _);

            // 只由数字、点和冒号组成的串视为「想写 IP」，此时必须真能解析，
            // 否则 999.999.1.1 这类会被当成合法主机名放进来。
            var looksLikeIp = value.All(c => char.IsDigit(c) || c == '.' || c == ':');

            if (!isIp && (looksLikeIp || !HostPattern.IsMatch(value)))
            {
                error = looksLikeIp
                    ? $"「{value}」不是有效的 IP 地址。"
                    : $"「{value}」不是有效的 IP 或主机名。";
                return false;
            }

            foreach (var entry in Whitelist)
            {
                if (string.Equals(entry.Value, value, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"「{value}」已在白名单中，无需重复添加。";
                    return false;
                }
            }

            Whitelist.Add(new WhitelistEntry(value, false));
            error = string.Empty;
            WhitelistChanged?.Invoke(null, EventArgs.Empty);
            return true;
        }

        /// <summary>删除白名单项；锁定项会被忽略。</summary>
        public static bool RemoveWhitelist(WhitelistEntry? entry)
        {
            if (entry is null || entry.IsLocked || !Whitelist.Contains(entry))
            {
                return false;
            }

            Whitelist.Remove(entry);
            WhitelistChanged?.Invoke(null, EventArgs.Empty);
            return true;
        }

        /// <summary>恢复默认白名单（两项锁定 + 一条示例内网地址）。</summary>
        public static void ResetWhitelist()
        {
            Whitelist.Clear();
            Whitelist.Add(new WhitelistEntry("127.0.0.1", true));
            Whitelist.Add(new WhitelistEntry("::1", true));
            Whitelist.Add(new WhitelistEntry("192.168.1.24", false));
            WhitelistChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>
        /// 用配置文件里的白名单替换当前列表。127.0.0.1 与 ::1 永远保留且锁定，
        /// 它们不在文件里也要补上——否则界面会出现「没有锁定项」这种说不通的状态。
        /// </summary>
        public static void SetWhitelist(IEnumerable<string> hosts)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var next = new List<WhitelistEntry>();

            void Add(string value)
            {
                if (value.Length > 0 && seen.Add(value))
                {
                    next.Add(new WhitelistEntry(value, IsLockedHost(value)));
                }
            }

            Add("127.0.0.1");
            Add("::1");

            foreach (string host in hosts)
            {
                string value = host.Trim();

                // 旧配置里可能存着 localhost：它不是合法 IP，换成 IPv6 回环
                if (value.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                {
                    value = "::1";
                }

                Add(value);
            }

            Whitelist.Clear();
            foreach (WhitelistEntry entry in next)
            {
                Whitelist.Add(entry);
            }

            WhitelistChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>127.0.0.1 与 ::1 是内置项：界面上不可删除。</summary>
        private static bool IsLockedHost(string value) =>
            value == "127.0.0.1" || value == "::1";

        /// <summary>
        /// 只留本机两项（127.0.0.1 与 ::1），不带示例内网地址。
        /// 与首次获取默认配置时写进 config.yaml 的白名单保持一致。
        /// </summary>
        public static void ResetToLocalOnly()
        {
            Whitelist.Clear();
            Whitelist.Add(new WhitelistEntry("127.0.0.1", true));
            Whitelist.Add(new WhitelistEntry("::1", true));
            WhitelistChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>白名单条目视图模型：锁定项不提供删除入口，并显示「锁定」标记。</summary>
    public sealed class WhitelistEntry
    {
        public WhitelistEntry(string value, bool isLocked)
        {
            Value = value;
            IsLocked = isLocked;
        }

        public string Value { get; }

        public bool IsLocked { get; }

        public Visibility LockVisibility => IsLocked ? Visibility.Visible : Visibility.Collapsed;

        public Visibility DeleteVisibility => IsLocked ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>锁定项显示锁，自定义项显示地球，一眼区分默认与手动添加。</summary>
        public string Glyph => IsLocked ? "\uE72E" : "\uE774";

        public string AutomationName => IsLocked ? $"{Value}（已锁定）" : Value;
    }
}
