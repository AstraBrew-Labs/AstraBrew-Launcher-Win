using System;
using System.Net;
using Microsoft.Win32;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// Windows 系统代理的当前状态（HKCU\…\Internet Settings）。
    ///
    /// 三种「开启」形态要分开看，否则界面会说出错误的话：
    /// · <b>手动代理</b>：<c>ProxyEnable=1</c> 且 <c>ProxyServer</c> 有值——能直接拿到地址；
    /// · <b>PAC 脚本</b>：<c>AutoConfigURL</c> 有值——即使 <c>ProxyEnable=0</c>，Windows 依然按脚本走代理；
    /// · <b>自动检测</b>：<c>AutoDetect=1</c>（WPAD）——有代理但没有固定地址。
    /// 只有三者都不成立才是真正的「未开启」。
    /// </summary>
    public sealed record SystemProxyState(
        bool Enabled,
        string Server,
        string? PacUrl,
        bool AutoDetect,
        string Detail)
    {
        public string Label => Enabled ? "已开启" : "未开启";

        /// <summary>给 node / git 之类只能接受固定地址的组件用；PAC 与自动检测返回 null。</summary>
        public string? EffectiveUrl => Server.Length > 0 ? Server : null;
    }

    /// <summary>读取与解析系统代理。全应用只此一处读注册表，避免两个页面的判断慢慢长歪。</summary>
    public static class SystemProxy
    {
        private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

        /// <summary>读一次当前状态。读不到（被策略挡住、注册表异常）按「未开启」处理并说明原因。</summary>
        public static SystemProxyState Read()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath);

                if (key is null)
                {
                    return new SystemProxyState(false, string.Empty, null, false,
                        "读不到 Windows 的 Internet 选项，按未开启代理处理。");
                }

                int enable = key.GetValue("ProxyEnable") is int value ? value : 0;
                string server = key.GetValue("ProxyServer") is string text ? text.Trim() : string.Empty;
                string? pac = key.GetValue("AutoConfigURL") is string url && url.Trim().Length > 0
                    ? url.Trim()
                    : null;
                bool autoDetect = key.GetValue("AutoDetect") is int detect && detect == 1;

                if (enable != 0 && server.Length > 0)
                {
                    string effective = Normalize(server);

                    return new SystemProxyState(
                        true,
                        effective,
                        pac,
                        autoDetect,
                        $"Windows 手动代理已开启并生效：{effective}");
                }

                if (pac is not null)
                {
                    return new SystemProxyState(
                        true,
                        string.Empty,
                        pac,
                        autoDetect,
                        $"Windows 使用 PAC 脚本：{pac}（地址由脚本决定，界面无法提前列出）");
                }

                if (autoDetect)
                {
                    return new SystemProxyState(
                        true,
                        string.Empty,
                        null,
                        true,
                        "Windows 开着「自动检测设置」（WPAD），有代理但没有固定地址。");
                }

                return new SystemProxyState(
                    false,
                    string.Empty,
                    pac,
                    autoDetect,
                    "Windows 当前没有配置代理，选这一项等于直连。");
            }
            catch (Exception ex) when (ex is System.Security.SecurityException
                or UnauthorizedAccessException
                or System.IO.IOException)
            {
                return new SystemProxyState(false, string.Empty, null, false,
                    $"读取系统代理失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 把 <c>ProxyServer</c> 的几种写法统一成一条可用的 URL：
        /// <c>127.0.0.1:7890</c>、<c>http://127.0.0.1:7890</c>、<c>http=…;https=…</c>、
        /// 以及 <c>socks=…</c> 混排。给 node / git 用时只认 http(s)，所以优先挑 http 那一段。
        /// </summary>
        private static string Normalize(string raw)
        {
            if (!raw.Contains('='))
            {
                return raw.Contains("://", StringComparison.Ordinal) ? raw : "http://" + raw;
            }

            string? http = null;
            string? https = null;
            string? socks = null;

            foreach (string part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int split = part.IndexOf('=');
                if (split <= 0)
                {
                    continue;
                }

                string scheme = part[..split].Trim().ToLowerInvariant();
                string value = part[(split + 1)..].Trim();

                if (value.Length == 0)
                {
                    continue;
                }

                switch (scheme)
                {
                    case "http":
                        http = value;
                        break;
                    case "https":
                        https = value;
                        break;
                    case "socks":
                    case "socks5":
                        socks = value;
                        break;
                }
            }

            string? chosen = http ?? https ?? socks;

            if (chosen is null)
            {
                return raw;
            }

            if (chosen.Contains("://", StringComparison.Ordinal))
            {
                return chosen;
            }

            // socks 段要带上协议头，否则 HttpWebRequest 会把它当 http 用
            string prefix = http is not null || https is not null ? "http://" : "socks5://";

            return prefix + chosen;
        }

        /// <summary>把状态压成一句话，供日志或 ToolTip 使用。</summary>
        public static string Describe()
        {
            SystemProxyState state = Read();
            return state.Enabled ? state.Detail : "系统代理未开启";
        }

        /// <summary>
        /// 当前是否真的能拿到一个固定代理地址。给 <see cref="WebProxy"/> 之类的组件用。
        /// </summary>
        public static string? ResolveEffectiveUrl() => Read().EffectiveUrl;
    }
}
