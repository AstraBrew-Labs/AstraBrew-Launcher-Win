using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// PM2：它是 npm 包，不是 zip，所以不走 <see cref="ComponentInstaller"/>，而是用当前运行时的 npm
    /// **全局安装**到 <c>%AppData%\AstraBrew Launcher\bin\pm2</c>
    /// （<c>install -g --prefix</c>：包落在 <c>bin\pm2\node_modules\pm2</c>，命令 shim 落在 <c>bin\pm2\pm2.cmd</c>）。
    ///
    /// **内置与系统共享同一份**：装的位置只看这个常量，不看用的是哪套 Node.js——
    /// 内置的 npm 装也好、系统的 npm 装也好，都落到同一个前缀；再加上固定成同一个
    /// <see cref="HomePath"/>（PM2_HOME），两边的 <c>pm2 ls</c> 看到的就是同一套进程，
    /// 不会出现「内置启动的进程，切到系统环境就看不见」。
    ///
    /// 属于完整包：没有它酒馆照样能跑，只是关窗就停服、没有后台常驻。
    /// </summary>
    public static class Pm2Installer
    {
        public const string Version = "6.0.5";

        /// <summary>安装目录（npm 的全局前缀）。两种运行时共用这一份，路径写死、不随运行时变化。</summary>
        public static string InstallRoot { get; } = Path.Combine(AppPaths.RoamingRoot, "bin", "pm2");

        /// <summary>npm 生成的全局命令。<c>-g</c> 装完才会有它。</summary>
        public static string CommandPath { get; } = Path.Combine(InstallRoot, "pm2.cmd");

        /// <summary>
        /// PM2 的运行数据（daemon、进程表、日志）。固定在这里而不是默认的 <c>%USERPROFILE%\.pm2</c>，
        /// 两个运行时共用一个——否则会在用户目录里各起一份 daemon。
        /// </summary>
        public static string HomePath { get; } = Path.Combine(AppPaths.RoamingRoot, "pm2");

        /// <summary>
        /// 装好没：包在、且**命令 shim 也在**才算数。
        /// 只认 package.json 的话，早期「非全局装」留下的目录也会被当成已装，命令却根本不存在。
        /// </summary>
        public static bool IsInstalled =>
            File.Exists(Path.Combine(InstallRoot, "node_modules", "pm2", "package.json"))
            && File.Exists(CommandPath);

        /// <summary>
        /// 给要跑 <c>pm2</c> 的子进程注入统一环境：PM2_HOME 指向共享目录、PATH 里能直接找到 pm2 命令。
        /// 任何地方起 pm2 子进程都应该先过一遍，否则两个运行时各自起 daemon。
        /// </summary>
        public static void ApplyEnvironment(ProcessStartInfo startInfo)
        {
            startInfo.Environment["PM2_HOME"] = HomePath;

            string path = startInfo.Environment.TryGetValue("PATH", out string? current)
                && current is { Length: > 0 }
                    ? current
                    : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

            bool present = path
                .Split(Path.PathSeparator)
                .Any(item => string.Equals(item.Trim().Trim('"'), InstallRoot, StringComparison.OrdinalIgnoreCase));

            if (!present)
            {
                startInfo.Environment["PATH"] = InstallRoot + Path.PathSeparator + path;
            }
        }

        /// <summary>
        /// 用 npm 安装 pm2。进度是「不确定」的（npm 只给百分比不给总量），
        /// 界面按 <see cref="InstallProgress.Percent"/> = 0 + 不断刷新的日志行来表现。
        /// </summary>
        public static Task<InstallResult> InstallAsync(
            IProgress<InstallProgress>? progress,
            CancellationToken token) => Task.Run(() =>
        {
            using InstallLog log = InstallLog.Create("pm2");
            var clock = Stopwatch.StartNew();

            string? node = RuntimeProbe.LocateNode();
            string? npmCli = RuntimeProbe.LocateNpmCli();

            if (node is null || npmCli is null)
            {
                return new InstallResult(
                    false,
                    InstallError.Verify,
                    "PM2 需要先装好 Node.js",
                    "PM2 是 Node.js 的包，请先安装 Node.js 再回来装它。",
                    log.FilePath);
            }

            try
            {
                Directory.CreateDirectory(InstallRoot);

                var startInfo = new ProcessStartInfo(node)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                startInfo.ArgumentList.Add(npmCli);
                startInfo.ArgumentList.Add("install");
                // 全局安装：带上 -g 才会在 prefix 根下生成 pm2.cmd，终端里也能直接用
                startInfo.ArgumentList.Add("--global");
                startInfo.ArgumentList.Add($"pm2@{Version}");
                startInfo.ArgumentList.Add("--prefix");
                startInfo.ArgumentList.Add(InstallRoot);
                startInfo.ArgumentList.Add("--no-audit");
                startInfo.ArgumentList.Add("--no-fund");
                startInfo.ArgumentList.Add("--loglevel=http");

                // npm 的缓存放到启动器自己的临时目录，别去动用户的 %AppData%\npm-cache
                startInfo.Environment["npm_config_cache"] = Path.Combine(AppPaths.TempRoot, "npm-cache");
                startInfo.Environment["npm_config_registry"] = LauncherSettings.NpmRegistryUrl;
                startInfo.Environment["npm_config_prefix"] = InstallRoot;

                if (Path.GetDirectoryName(node) is { Length: > 0 } nodeDir)
                {
                    startInfo.Environment["PATH"] = nodeDir + Path.PathSeparator
                        + Environment.GetEnvironmentVariable("PATH");
                }

                // 先把共享的 PM2_HOME 建好：两种运行时都读它，早建早统一
                Directory.CreateDirectory(HomePath);

                progress?.Report(new InstallProgress(
                    InstallStage.Downloading,
                    "正在全局安装 PM2…",
                    0,
                    0,
                    0,
                    0));

                using var process = new Process { StartInfo = startInfo };

                if (!process.Start())
                {
                    return new InstallResult(false, InstallError.Unknown, "无法启动 npm", null, log.FilePath);
                }

                var tail = new Queue<string>(8);

                void OnLine(string? line)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        return;
                    }

                    log.Info(line);

                    if (tail.Count == 8)
                    {
                        tail.Dequeue();
                    }

                    tail.Enqueue(line.Trim());

                    progress?.Report(new InstallProgress(
                        InstallStage.Downloading,
                        line.Trim(),
                        0,
                        0,
                        0,
                        0));
                }

                process.OutputDataReceived += (_, e) => OnLine(e.Data);
                process.ErrorDataReceived += (_, e) => OnLine(e.Data);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                while (!process.HasExited)
                {
                    if (token.IsCancellationRequested)
                    {
                        GitCommand.Kill(process);
                        return new InstallResult(false, InstallError.Cancelled, "已取消安装 PM2", null, log.FilePath);
                    }

                    // npm 偶尔会长时间没有输出：给一个上限，别让引导永远转圈
                    if (clock.Elapsed > TimeSpan.FromMinutes(5))
                    {
                        GitCommand.Kill(process);
                        return new InstallResult(
                            false,
                            InstallError.Network,
                            "PM2 安装超时",
                            "多半是 npm 源太慢，换一个 registry 再试。",
                            log.FilePath);
                    }

                    Thread.Sleep(120);
                }

                process.WaitForExit();

                if (process.ExitCode == 0 && IsInstalled)
                {
                    return new InstallResult(
                        true,
                        InstallError.None,
                        $"PM2 {Version} 已就位",
                        null,
                        log.FilePath);
                }

                // 包装上了但命令没生成：多半是 npm 的全局前缀没生效，单独说清楚
                if (process.ExitCode == 0 && !File.Exists(CommandPath))
                {
                    return new InstallResult(
                        false,
                        InstallError.Verify,
                        "PM2 装上了，但命令没生成",
                        $"没找到 {CommandPath}，可能是 npm 忽略了 --prefix。看日志后再试一次。",
                        log.FilePath);
                }

                string detail = tail.Count > 0 ? tail.Dequeue() : $"npm 退出代码 {process.ExitCode}";

                return new InstallResult(
                    false,
                    InstallError.Download,
                    "PM2 安装失败",
                    detail,
                    log.FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                log.Fail("PM2 安装异常", ex);

                return new InstallResult(
                    false,
                    InstallError.Unknown,
                    "PM2 安装失败",
                    GitCommand.FirstLine(ex.Message),
                    log.FilePath);
            }
        }, token);
    }
}
