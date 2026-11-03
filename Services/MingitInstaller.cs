using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 内置 MinGit：声明「装什么、装到哪、什么才算装对」，安装流程交给 <see cref="ComponentInstaller"/>。
    ///
    /// 源顺序是「华为云 → npmmirror → GitHub」：国内直连 GitHub 十次有八次超时，
    /// 放第一位等于让每个用户先等半分钟再失败。GitHub 那一条只要加速开关打开，
    /// 就由 <see cref="GithubAccel"/> 补一条 <c>gh-proxy.org</c> 地址排到直连前面。
    /// </summary>
    public static class MingitInstaller
    {
        /// <summary>本节安装的 MinGit 版本，与 <see cref="Spec"/> 里的 URL 保持一致。</summary>
        public const string Version = "2.55.0.windows.2";

        /// <summary>GitHub 上的安装包；加速开着时会额外生成一条经中转的地址。</summary>
        private const string GithubUrl =
            "https://github.com/git-for-windows/git/releases/download/v2.55.0.windows.2/MinGit-2.55.0.2-64-bit.zip";

        /// <summary>镜像在前、GitHub 兜底；GitHub 项在安装时按开关展开。</summary>
        private static readonly (string Name, string Url)[] BaseSources =
        {
            ("华为云", "https://mirrors.huaweicloud.com/git-for-windows/v2.55.0.windows.2/MinGit-2.55.0.2-64-bit.zip"),
            ("npmmirror", "https://registry.npmmirror.com/-/binary/git-for-windows/v2.55.0.windows.2/MinGit-2.55.0.2-64-bit.zip"),
            (GithubAccel.DirectName, GithubUrl),
        };

        /// <summary>内置 Git 的落盘位置：<c>%AppData%\AstraBrew Launcher\bin\mingit</c>。</summary>
        public static string InstallRoot { get; } = Path.Combine(AppPaths.RoamingRoot, "bin", "mingit");

        /// <summary>下载源名字，供界面在失败时说明「都试过了」。</summary>
        public static string SourceNames => Spec.SourceNames;

        /// <summary>已经装好：<c>cmd\git.exe</c> 或 <c>bin\git.exe</c> 任一存在。</summary>
        public static bool IsInstalled => ExistsAt(InstallRoot);

        /// <summary>
        /// 安装参数；界面拿它渲染弹窗标题与成功文案。
        ///
        /// 每次取都重新构造：源列表要跟着「加速开关」走，缓成单例的话，
        /// 用户在设置里拨了开关，下一次安装还会用旧的地址。
        /// </summary>
        internal static InstallSpec Spec => new()
        {
            DisplayName = "Git",
            VersionLabel = $"MinGit {Version}",
            ProbeKey = "Git",
            LogTask = "mingit",
            ArchiveName = "MinGit-2.55.0.2-64-bit.zip",
            InstallRoot = InstallRoot,
            StagingRoot = Path.Combine(AppPaths.RoamingRoot, "bin", ".mingit-staging"),
            // 解压后约 130 MB，留足余量再动手，免得解到一半才报磁盘满
            RequiredFreeBytes = 400L * 1024 * 1024,
            Sources = GithubAccel.ExpandSources(BaseSources),
            ExistsAt = ExistsAt,
            IncompleteMessage = "解压出来的文件不完整，请重试。",
            IncompleteHint = "若反复出现，请先清理磁盘再重试。",
            BusyMessage = "旧版 Git 正在被使用，请关闭正在运行 Git 的程序后重试。",
            BusyHint = "例如酒馆控制台、终端或编辑器里的 Git 进程。",
            SuccessDetail = detected => $"Git {detected} 已就位，可以拉取与更新酒馆源码。",
        };

        /// <summary>安装内置 MinGit；取消后不留半成品，失败一律折成 <see cref="InstallResult"/>。</summary>
        public static Task<InstallResult> InstallAsync(
            IProgress<InstallProgress>? progress,
            CancellationToken token) =>
            ComponentInstaller.InstallAsync(Spec, progress, token);

        private static bool ExistsAt(string root) =>
            File.Exists(Path.Combine(root, "cmd", "git.exe"))
            || File.Exists(Path.Combine(root, "bin", "git.exe"));
    }
}
