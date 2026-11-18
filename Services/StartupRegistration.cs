using System;
using System.IO;
using Microsoft.Win32;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 开机自启动：往 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> 写一条值。
    ///
    /// 为什么不只用自己的配置项：Windows 的「设置 → 应用 → 启动」、任务管理器的「启动应用」
    /// 读的就是这个键。只把布尔值存进 settings.toml，系统那边什么都看不到，
    /// 用户会以为开关没生效（也确实没生效）。
    ///
    /// 写 HKCU 不需要管理员，也不进 MSI 的组件表：
    /// · **开关**：设置页拨一下立刻写 / 删；
    /// · **免安装版**：目录被挪走之后旧路径会失效，<see cref="EnsureMatchesSetting"/> 在每次启动时
    ///   按当前 exe 补一条，和私有协议的做法一致；
    /// · **卸载**：MSI 不管这个键（用户自己的偏好），但 exe 一旦不在盘上，Run 项会被系统跳过，
    ///   不影响使用；重装后再拨一次开关即可。
    /// </summary>
    public static class StartupRegistration
    {
        /// <summary>任务管理器里显示的名字，也是注册表值名。</summary>
        private const string ValueName = "AstraBrew Launcher";

        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>当前进程的 exe；单文件发布时 <c>ProcessPath</c> 才是真正的可执行文件。</summary>
        private static string ExePath => Environment.ProcessPath ?? string.Empty;

        /// <summary>命令行里要带的东西：加引号，路径有空格才不会把参数切成两段。</summary>
        private static string CommandLine => $"\"{ExePath}\"";

        /// <summary>按用户的选择写或删那条启动项。失败只返回 false，不抛——开关拨不动比崩溃强。</summary>
        public static bool Apply(bool enabled)
        {
            try
            {
                if (ExePath.Length == 0)
                {
                    return false;
                }

                using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

                if (!enabled)
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                    return true;
                }

                key.SetValue(ValueName, CommandLine, RegistryValueKind.String);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 启动时校准一次：设置里开着、而注册表里没有（或被指到旧目录），就补上；
        /// 设置里关着却残留了一条指向本 exe 的，就删掉。
        ///
        /// 只处理「指向自己」的那条：别的程序同名值不碰。
        /// </summary>
        public static void EnsureMatchesSetting()
        {
            try
            {
                string exe = ExePath;

                if (exe.Length == 0)
                {
                    return;
                }

                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);

                if (key is null)
                {
                    if (LauncherSettings.AutoLaunch)
                    {
                        Apply(true);
                    }

                    return;
                }

                string? current = key.GetValue(ValueName) as string;

                if (LauncherSettings.AutoLaunch)
                {
                    if (!string.Equals(current, CommandLine, StringComparison.OrdinalIgnoreCase))
                    {
                        Apply(true);
                    }

                    return;
                }

                // 关着但残留：仅当那条命令确实指向本 exe 时才删，避免误删别人的同名项
                if (current is { Length: > 0 } &&
                    current.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Apply(false);
                }
            }
            catch (Exception)
            {
                // 注册表被策略锁住时静默跳过：开关仍会记在配置里，下次启动再试
            }
        }

        /// <summary>当前是否真的在系统启动项里（设置页用来对齐开关的真实状态）。</summary>
        public static bool IsRegistered()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>exe 是否还在盘上：设置页给出「启动项已失效」的提示时用得上。</summary>
        public static bool TargetExists()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                string? value = key?.GetValue(ValueName) as string;

                if (value is not { Length: > 0 })
                {
                    return false;
                }

                string path = value.Trim();
                if (path.StartsWith('"'))
                {
                    int close = path.IndexOf('"', 1);
                    path = close > 1 ? path[1..close] : path.Trim('"');
                }

                return File.Exists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
