using System;
using System.IO;
using Microsoft.Win32;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 私有协议 <c>launcher-astrabrew://</c>：网页里点一个链接就能把启动器唤起来。
    ///
    /// 注册表结构与浏览器认的那套完全一致（HKCU 下，不需要管理员）：
    /// <code>
    /// HKCU\Software\Classes\launcher-astrabrew
    ///     (默认)            = URL:AstraBrew Launcher Protocol
    ///     URL Protocol      = ""                       ← 空字符串，浏览器靠它认这是协议
    ///     DefaultIcon\(默认) = "&lt;exe&gt;",0
    ///     shell\open\command\(默认) = "&lt;exe&gt;" "%1"
    /// </code>
    ///
    /// 两套注册方式共用这一份结构：
    /// · **安装包版**：由 MSI 写（<c>installer/Package.wxs</c> 的 <c>UriSchemeRegistration</c> 组件），
    ///   卸载时由 MSI 一并删掉，进程这边什么都不用做；
    /// · **免安装版**：没有安装程序，<see cref="EnsureRegistered"/> 在每次启动时自查一遍——
    ///   没注册、或者注册的路径是已经挪走的旧目录，就按当前 exe 补一份。
    ///
    /// 免安装版**不会抢已经有效的注册**：如果那条命令指向的 exe 还在（多半是装好的 MSI 版），
    /// 就原样留着，免得便携版一跑就把安装版的唤起劫走。
    /// </summary>
    public static class UriScheme
    {
        /// <summary>协议名，注册表里就是 <c>HKCU\Software\Classes\&lt;这个名字&gt;</c>。</summary>
        public const string Name = "launcher-astrabrew";

        /// <summary>带分隔符的前缀，给界面文案与日志用。</summary>
        public const string Prefix = Name + "://";

        private const string RootKeyPath = @"Software\Classes\" + Name;
        private const string CommandKeyPath = RootKeyPath + @"\shell\open\command";

        /// <summary>这条命令对应的就是这个 exe（注册信息已经是对的，不用重写）。</summary>
        private static bool IsSelf(string? command, string exePath)
        {
            return command is { Length: > 0 } &&
                string.Equals(ExtractExe(command), exePath, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>命令指向的 exe 还在盘上：说明另有一个「真的」安装在用这个协议。</summary>
        private static bool PointsToLiveExe(string command)
        {
            string path = ExtractExe(command);
            return path.Length > 0 && File.Exists(path);
        }

        /// <summary>从 <c>"C:\x\a.exe" "%1"</c> 里抠出 exe 路径。</summary>
        private static string ExtractExe(string command)
        {
            int open = command.IndexOf('"', StringComparison.Ordinal);
            if (open < 0)
            {
                // 没加引号的写法（罕见，但别的安装器可能写成 C:\x\a.exe %1）：
                // 取第一个空格之前那段
                int space = command.IndexOf(' ', StringComparison.Ordinal);
                return space > 0 ? command[..space] : command.Trim();
            }

            int close = command.IndexOf('"', open + 1);
            return close > open ? command[(open + 1)..close] : string.Empty;
        }

        /// <summary>
        /// 免安装版的自查注册。任何失败都静默放过——写不进 HKCU（组策略、只读配置）
        /// 只该让「浏览器唤起」这一个功能不可用，不该挡住启动。
        /// </summary>
        public static void EnsureRegistered()
        {
            try
            {
                string exe = Environment.ProcessPath ?? string.Empty;
                if (exe.Length == 0 || !File.Exists(exe))
                {
                    return;
                }

                string command = $"\"{exe}\" \"%1\"";

                using (RegistryKey? current = Registry.CurrentUser.OpenSubKey(CommandKeyPath))
                {
                    string? existing = current?.GetValue(null) as string;

                    // 已经指向自己：每次启动都重写一遍纯属浪费
                    if (IsSelf(existing, exe))
                    {
                        return;
                    }

                    // 指向别的、且那个 exe 还在：留给那个安装（通常是装好的 MSI 版）
                    if (existing is { Length: > 0 } && PointsToLiveExe(existing))
                    {
                        return;
                    }
                }

                using RegistryKey root = Registry.CurrentUser.CreateSubKey(RootKeyPath, writable: true);
                root.SetValue(null, "URL:AstraBrew Launcher Protocol");

                // 必须写成「空字符串」这个值，缺了它 Windows 不认为这是协议处理器
                root.SetValue("URL Protocol", string.Empty);

                using (RegistryKey icon = root.CreateSubKey("DefaultIcon"))
                {
                    icon.SetValue(null, $"\"{exe}\",0");
                }

                using (RegistryKey open = root.CreateSubKey(@"shell\open\command"))
                {
                    open.SetValue(null, command);
                }
            }
            catch (Exception)
            {
                // 见方法注释：静默失败
            }
        }

        /// <summary>从命令行里挑出协议链接（浏览器唤起时它是第一个参数）。</summary>
        public static string? FindInArgs(string[] args)
        {
            // args[0] 是 exe 自己，从 1 开始
            for (int i = 1; i < args.Length; i++)
            {
                if (LooksLike(args[i]))
                {
                    return args[i];
                }
            }

            return null;
        }

        /// <summary>是不是本协议的链接（也接受 <c>launcher-astrabrew:xxx</c> 这种不带 // 的写法）。</summary>
        public static bool LooksLike(string value)
        {
            return value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith(Name + ":", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 取路由段：<c>launcher-astrabrew://console</c> → <c>console</c>。
        /// 只认主机那一段，后面的路径 / 查询串一律不管（暂时没有要带的参数）。
        /// </summary>
        public static string Route(string uriText)
        {
            string rest = uriText;

            int colon = rest.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0)
            {
                rest = rest[(colon + 1)..];
            }

            rest = rest.TrimStart('/');

            int cut = rest.IndexOfAny(new[] { '/', '?', '#' });
            if (cut >= 0)
            {
                rest = rest[..cut];
            }

            return rest.Trim().ToLowerInvariant();
        }
    }
}
