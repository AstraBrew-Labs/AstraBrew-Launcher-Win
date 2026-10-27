using System;
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

    /// <summary>默认浏览器。</summary>
    public enum BrowserKind
    {
        Chrome,
        Edge,
        Firefox,
    }

    /// <summary>
    /// 跨页面共享的酒馆启动设置（原型状态，不做持久化）。
    ///
    /// 仪表盘的「启动模式 / 默认浏览器」与酒馆配置页的对应选项读写同一份数据：
    /// 任一页面修改都会通过 <see cref="Changed"/> 通知另一页面刷新界面，
    /// 因此两处永远是同一状态，而不是各自维护一份。
    /// </summary>
    public static class TavernSettings
    {
        /// <summary>启动模式 / 默认浏览器 / 服务范围发生变化。</summary>
        public static event EventHandler? Changed;

        /// <summary>白名单发生增删。</summary>
        public static event EventHandler? WhitelistChanged;

        private static LaunchMode _mode = LaunchMode.Normal;
        private static BrowserKind _browser = BrowserKind.Chrome;
        private static ServiceScope _scope = ServiceScope.Lan;

        public static LaunchMode Mode
        {
            get => _mode;
            set
            {
                if (_mode == value)
                {
                    return;
                }

                _mode = value;
                Changed?.Invoke(null, EventArgs.Empty);
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
        public static bool IsNormalMode => _mode == LaunchMode.Normal;

        public static string ModeName => _mode switch
        {
            LaunchMode.Desktop => "桌面模式",
            LaunchMode.Server => "服务器模式",
            _ => "普通模式",
        };

        public static string BrowserName => _browser switch
        {
            BrowserKind.Edge => "Edge",
            BrowserKind.Firefox => "火狐浏览器",
            _ => "谷歌浏览器",
        };

        /// <summary>白名单：localhost 与 127.0.0.1 默认锁定，不可删除。</summary>
        public static ObservableCollection<WhitelistEntry> Whitelist { get; } = new()
        {
            new WhitelistEntry("localhost", true),
            new WhitelistEntry("127.0.0.1", true),
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
            Whitelist.Add(new WhitelistEntry("localhost", true));
            Whitelist.Add(new WhitelistEntry("127.0.0.1", true));
            Whitelist.Add(new WhitelistEntry("192.168.1.24", false));
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
