using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 内置 Caddy：一个 exe，解压即用，装在 <c>%AppData%\AstraBrew Launcher\bin\caddy</c>。
    ///
    /// 只服务「服务器模式 + 反向代理 / HTTPS」这一条路，不属于最基础运行环境：
    /// 装不上只影响反代，不该挡住酒馆启动，所以它在完整包里、失败也不阻塞引导。
    /// 下载源是「GitHub Release（加速前缀优先）+ 官方直连」，走 <see cref="GithubAccel.ExpandSources"/>；
    /// 资产按系统架构在 amd64 / arm64 之间自动选。
    /// </summary>
    public static class CaddyInstaller
    {
        /// <summary>目标版本。Release 资产名里带版本号，改这里就够了。</summary>
        public const string Version = "2.11.9";

        /// <summary>
        /// 该下哪个构建：看**系统**架构而不是进程架构。
        /// 启动器可能以 x64 跑在 ARM64 机器上（系统模拟），那时候照样该装 arm64 的原生 Caddy。
        /// </summary>
        public static string BuildArch =>
            RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "amd64";

        /// <summary>
        /// 官方 Release 直链。Caddy 只发 windows-amd64 / windows-arm64 两个包（没有 x86），
        /// 所以非 ARM64 一律按 amd64 处理。
        /// </summary>
        private static string ReleaseUrl =>
            $"https://github.com/caddyserver/caddy/releases/download/v{Version}/caddy_{Version}_windows_{BuildArch}.zip";

        /// <summary>内置 Caddy 的落盘位置。</summary>
        public static string InstallRoot { get; } = Path.Combine(AppPaths.RoamingRoot, "bin", "caddy");

        /// <summary>装好没：压缩包里只有一个 <c>caddy.exe</c>，它在就算数。</summary>
        public static bool IsInstalled => File.Exists(Path.Combine(InstallRoot, "caddy.exe"));

        /// <summary>安装参数；源列表每次重新构造，跟着加速开关走。</summary>
        internal static InstallSpec Spec => new()
        {
            DisplayName = "Caddy",
            VersionLabel = $"Caddy v{Version} · windows-{BuildArch}",
            ProbeKey = "Caddy",
            LogTask = "caddy",
            ArchiveName = $"caddy_{Version}_windows_{BuildArch}.zip",
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
