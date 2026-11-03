using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 用「设置里选的那个浏览器」打开酒馆。
    ///
    /// 以前这一步只播报一句「已在浏览器中打开」，什么也没发生。这里按 App Paths 找可执行文件，
    /// 找得到就带着网址启动它，找不到就退回系统默认浏览器——重点是**页面真的会打开**，
    /// 而不是让用户自己复制地址。
    /// </summary>
    public static class BrowserLauncher
    {
        /// <summary>
        /// 打开网址。<paramref name="detail"/> 说明实际用了哪个浏览器 / 为什么退回了默认。
        /// </summary>
        public static bool Open(string url, BrowserKind browser, out string detail)
        {
            // 「系统默认」不去找特定 exe，交给 ShellExecute
            string? executable = browser == BrowserKind.System ? null : Locate(browser);

            try
            {
                if (executable is not null)
                {
                    Process.Start(new ProcessStartInfo(executable)
                    {
                        ArgumentList = { url },
                        UseShellExecute = false,
                    });

                    detail = Path.GetFileName(executable);
                    return true;
                }

                // 没装选中的浏览器：让系统按默认处理，至少能把页面打开
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                detail = "系统默认浏览器";
                return true;
            }
            catch (Exception ex)
            {
                detail = ex.Message;
                return false;
            }
        }

        /// <summary>按浏览器种类找可执行文件；找不到返回 null。</summary>
        public static string? Locate(BrowserKind browser) => browser switch
        {
            BrowserKind.Edge => Find("msedge.exe", @"Microsoft\Edge\Application"),
            BrowserKind.Firefox => Find("firefox.exe", @"Mozilla Firefox"),
            BrowserKind.Chrome => Find("chrome.exe", @"Google\Chrome\Application"),
            _ => null,
        };

        /// <summary>
        /// 先查注册表 App Paths（这是各浏览器安装时自己写的准确路径），
        /// 再退到两个 Program Files 下的常规位置。
        /// </summary>
        private static string? Find(string executable, string relativeFolder)
        {
            foreach (string root in new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\",
            })
            {
                foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
                {
                    try
                    {
                        using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                        using RegistryKey? key = baseKey.OpenSubKey(root + executable);

                        if (key?.GetValue(null) is string path && File.Exists(path))
                        {
                            return path;
                        }
                    }
                    catch
                    {
                        // 注册表被策略挡住：继续看下一个位置
                    }
                }
            }

            foreach (Environment.SpecialFolder folder in new[]
            {
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86,
            })
            {
                string root = Environment.GetFolderPath(folder);
                if (root.Length == 0)
                {
                    continue;
                }

                string candidate = Path.Combine(root, relativeFolder, executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }
    }
}
