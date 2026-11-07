using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 内置 Caddy：一个 exe，解压即用，装在 <c>%AppData%\AstraBrew Launcher\bin\caddy</c>。
    ///
    /// 只服务「服务器模式 + 反向代理 / HTTPS」这一条路，不属于最基础运行环境：
    /// 装不上只影响反代，不该挡住酒馆启动，所以它在完整包里、失败也不阻塞引导。
    /// 下载源是「GitHub Release（加速前缀优先）+ 官方直连」，走 <see cref="GithubAccel.ExpandSources"/>。
    /// </summary>
    public static class CaddyInstaller
    {
        /// <summary>与设置页「运行环境」里显示的版本保持一致；换版本时这里与下面 URL 一起改。</summary>
        public const string Version = "2.10.0";

        private const string ReleaseUrl =
            "https://github.com/caddyserver/caddy/releases/download/v2.10.0/caddy_2.10.0_windows_amd64.zip";

        /// <summary>内置 Caddy 的落盘位置。</summary>
        public static string InstallRoot { get; } = Path.Combine(AppPaths.RoamingRoot, "bin", "caddy");

        /// <summary>装好没：压缩包里只有一个 <c>caddy.exe</c>，它在就算数。</summary>
        public static bool IsInstalled => File.Exists(Path.Combine(InstallRoot, "caddy.exe"));

        /// <summary>安装参数；源列表每次重新构造，跟着加速开关走。</summary>
        internal static InstallSpec Spec => new()
        {
            DisplayName = "Caddy",
            VersionLabel = $"Caddy v{Version}",
            ProbeKey = "Caddy",
            LogTask = "caddy",
            ArchiveName = $"caddy_{Version}_windows_amd64.zip",
            InstallRoot = InstallRoot,
            StagingRoot = Path.Combine(AppPaths.RoamingRoot, "bin", ".caddy-staging"),
            // 解压后约 45 MB，留足余量
            RequiredFreeBytes = 200L * 1024 * 1024,
            Sources = GithubAccel.ExpandSources(new[]
            {
                (GithubAccel.DirectName, ReleaseUrl),
            }),
            ExistsAt = root => File.Exists(Path.Combine(root, "caddy.exe")),
            IncompleteMessage = "解压出来的 Caddy 不完整，请重试。",
            IncompleteHint = "若反复出现，请先清理磁盘再重试。",
            BusyMessage = "Caddy 正在被使用，请先在控制台停止反向代理后重试。",
            BusyHint = "反向代理由酒馆内核托管，停掉酒馆再试即可。",
            SuccessDetail = detected => $"Caddy {detected} 已就位，可以配置反向代理与 HTTPS。",
        };

        /// <summary>安装内置 Caddy；取消或失败都不留半成品。</summary>
        public static Task<InstallResult> InstallAsync(
            IProgress<InstallProgress>? progress,
            CancellationToken token) =>
            ComponentInstaller.InstallAsync(Spec, progress, token);
    }
}
