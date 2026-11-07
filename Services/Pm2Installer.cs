using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 内置 PM2：它是 npm 包，不是 zip，所以不走 <see cref="ComponentInstaller"/>，
    /// 而是用内置 Node 的 npm 装到 <c>%AppData%\AstraBrew Launcher\bin\pm2</c>
    /// （<c>--prefix</c> 指到这里，pm2 就会落在 <c>bin\pm2\node_modules\pm2</c>，
    /// 与 <see cref="RuntimeProbe"/> 的探测路径一致）。
    ///
    /// 属于完整包：没有它酒馆照样能跑，只是关窗就停服、没有后台常驻。
    /// </summary>
    public static class Pm2Installer
    {
        public const string Version = "6.0.5";

        /// <summary>安装位置；<c>--prefix</c> 指向它。</summary>
        public static string InstallRoot { get; } = Path.Combine(AppPaths.RoamingRoot, "bin", "pm2");

        /// <summary>装好没：<c>node_modules\pm2\package.json</c> 在就算数。</summary>
        public static bool IsInstalled =>
            File.Exists(Path.Combine(InstallRoot, "node_modules", "pm2", "package.json"));

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

                progress?.Report(new InstallProgress(
                    InstallStage.Downloading,
                    "正在通过 npm 安装 PM2…",
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
