using System;
using System.Collections.Generic;
using System.Globalization;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 操作记录里那些「人话」的映射：设置项的写入入口、安装任务名 → 资源键。
    ///
    /// 单独放一个文件是为了让 <see cref="LauncherSettings"/> 与 <see cref="InstallLog"/> 只多一行调用，
    /// 而不是各自揣一张几十条的对照表。
    /// </summary>
    internal static class ActivityLabels
    {
        /// <summary>
        /// 设置写入方法名 → 现成的设置行标题键。
        /// 复用行标题而不是另建一套：两处文案一旦分叉，同一样东西在设置页叫一个名字、在操作记录里叫另一个。
        /// </summary>
        private static readonly Dictionary<string, string> SettingTitles = new(StringComparer.Ordinal)
        {
            ["SetTheme"] = "Settings.Row.Theme.Title",
            ["SetLanguage"] = "Settings.Row.Language.Title",
            ["SetLevel"] = "Activity.Item.Level",
            ["SetUiScale"] = "Settings.Row.Scale.Title",
            ["SetFontFamily"] = "Settings.Row.Font.Title",
            ["SetRememberWindow"] = "Settings.Row.WindowPlacement.Title",
            ["SetAutoLaunch"] = "Settings.Row.AutoStart.Title",
            ["SetCpuCores"] = "Settings.Row.CpuCores.Title",
            ["SetStartMode"] = "Settings.Row.StartMode.Title",
            ["SetRememberTavernWindow"] = "Settings.Row.TavernPlacement.Title",
            ["SetWebViewMemoryMb"] = "Settings.Row.WebViewMemory.Title",
            ["SetExportPath"] = "Settings.Row.ExportPath.Title",
            ["SetServiceScope"] = "Settings.Row.BindScope.Title",
            ["SetUserWhitelist"] = "Cfg.G2.Whitelist.Title",
            ["SetAllowBackground"] = "Settings.Row.KeepRunning.Title",
            ["SetDataLocation"] = "Settings.Row.DataLocation.Title",
            ["SetShowStartupCommand"] = "Settings.Row.EchoCommands.Title",
            ["SetKeepLatestLogsOnly"] = "Settings.Row.KeepLatestLogs.Title",
            ["SetLogMaxSizeMb"] = "Settings.Row.LogMaxSize.Title",
            ["SetRuntime"] = "Settings.Row.RuntimeSource.Title",
            ["SetNpmRegistry"] = "Settings.Row.NpmRegistry.Title",
            ["SetChannel"] = "Settings.Row.DownloadChannel.Title",
            ["SetGithubAcceleration"] = "Settings.Row.GithubAccel.Title",
            ["SetUpdateChannel"] = "Activity.Item.UpdateChannel",
            ["SetProxy"] = "Settings.Row.ProxyMode.Title",
            ["SetCustomProxy"] = "Settings.Row.ProxyAddress.Title",
            ["SetGithubBestLatency"] = "Settings.Row.GithubAccel.Title",
        };

        /// <summary>设置项名字：认识的取标题，不认识的退化成方法名（去掉 Set 前缀），总比不记强。</summary>
        public static string SettingTitle(string member)
        {
            if (SettingTitles.TryGetValue(member, out string? key))
            {
                return L.Get(key);
            }

            return member.StartsWith("Set", StringComparison.Ordinal) && member.Length > 3
                ? member[3..]
                : member;
        }

        /// <summary>
        /// 旧值 → 新值。只给能说人话的类型：布尔说「开/关」，数字直接写；
        /// 枚举与字符串一律不写（内部值如 <c>Npmmirror</c>、代理地址都不适合进日志）。
        /// </summary>
        public static string? ValueDetail(object? previous, object? current) => current switch
        {
            bool value => $"{OnOff(previous is true)} → {OnOff(value)}",
            int value => $"{previous} → {value.ToString(CultureInfo.InvariantCulture)}",
            long value => $"{previous} → {value.ToString(CultureInfo.InvariantCulture)}",
            double value => $"{previous} → {value.ToString("0.##", CultureInfo.InvariantCulture)}",
            _ => null,
        };

        private static string OnOff(bool value) => L.Get(value ? "Activity.Value.On" : "Activity.Value.Off");

        /// <summary>安装任务（<see cref="InstallLog"/> 的文件前缀）→ 界面上的名字。</summary>
        public static string InstallTask(string task) => task switch
        {
            "sillytavern" => L.Get("Activity.Install.Tavern"),
            "sillytavern-deps" => L.Get("Activity.Install.Deps"),
            "mingit" => L.Get("Activity.Install.Git"),
            "nodejs" => L.Get("Activity.Install.Node"),
            "pm2" => L.Get("Activity.Install.Pm2"),
            "caddy" => L.Get("Activity.Install.Caddy"),
            _ => L.F("Activity.Install.Other", task),
        };
    }
}
