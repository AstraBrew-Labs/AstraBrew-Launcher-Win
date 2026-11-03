using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 内置 Node.js：声明「装什么、装到哪、什么才算装对」，安装流程交给 <see cref="ComponentInstaller"/>。
    ///
    /// 源顺序是「华为云 → npmmirror → 阿里云 → 官方直连」。官方源在国内经常只有几十 KB/s，
    /// 放在最后当兜底；三个镜像的内容都是同一份官方 zip，大小一模一样。
    ///
    /// 装的是官方 zip 而不是 msi：msi 会往注册表、系统 PATH、Program Files 里写东西，
    /// 卸载不干净还会和用户自己装的 Node.js 打架。zip 解出来就是一个目录，
    /// 删掉即卸载，也符合「随启动器一起搬走」的定位。
    /// </summary>
    public static class NodejsInstaller
    {
        /// <summary>本节安装的 Node.js 版本，与 <see cref="Spec"/> 里的 URL 保持一致。</summary>
        public const string Version = "v22.20.0";

        /// <summary>内置 Node.js 的落盘位置：<c>%AppData%\AstraBrew Launcher\bin\nodejs</c>。</summary>
        public static string InstallRoot { get; } = Path.Combine(AppPaths.RoamingRoot, "bin", "nodejs");

        /// <summary>下载源名字，供界面在失败时说明「都试过了」。</summary>
        public static string SourceNames => Spec.SourceNames;

        /// <summary>已经装好：<c>node.exe</c> 与 <c>npm.cmd</c> 都在才算数——缺 npm 装不了 PM2。</summary>
        public static bool IsInstalled => ExistsAt(InstallRoot);

        /// <summary>安装参数；界面拿它渲染弹窗标题与成功文案。</summary>
        internal static InstallSpec Spec { get; } = new()
        {
            DisplayName = "Node.js",
            VersionLabel = $"Node.js {Version}",
            ProbeKey = "Node",
            LogTask = "nodejs",
            // 官方压缩包名里带 v：node-v22.20.0-win-x64.zip，少了这个 v 四个源全 404
            ArchiveName = $"node-{Version}-win-x64.zip",
            InstallRoot = InstallRoot,
            StagingRoot = Path.Combine(AppPaths.RoamingRoot, "bin", ".nodejs-staging"),
            // 解压后约 110 MB，留足余量再动手，免得解到一半才报磁盘满
            RequiredFreeBytes = 500L * 1024 * 1024,
            Sources = new (string, string)[]
            {
                ("华为云", $"https://mirrors.huaweicloud.com/nodejs/{Version}/node-{Version}-win-x64.zip"),
                ("npmmirror", $"https://registry.npmmirror.com/-/binary/node/{Version}/node-{Version}-win-x64.zip"),
                ("阿里云", $"https://mirrors.aliyun.com/nodejs-release/{Version}/node-{Version}-win-x64.zip"),
                ("官方直连", $"https://nodejs.org/download/release/{Version}/node-{Version}-win-x64.zip"),
            },
            ExistsAt = ExistsAt,
            IncompleteMessage = "解压出来的 Node.js 不完整，请重试。",
            IncompleteHint = "若反复出现，请先清理磁盘再重试。",
            BusyMessage = "Node.js 正在被使用，请关闭正在运行 Node.js 的程序后重试。",
            BusyHint = "例如酒馆控制台、PM2 守护进程，或终端里正在跑的 node。",
            SuccessDetail = detected => $"Node.js {detected} 已就位，npm 与 PM2 现在可以安装。",
        };

        /// <summary>安装内置 Node.js；取消后不留半成品，失败一律折成 <see cref="InstallResult"/>。</summary>
        public static Task<InstallResult> InstallAsync(
            IProgress<InstallProgress>? progress,
            CancellationToken token) =>
            ComponentInstaller.InstallAsync(Spec, progress, token);

        private static bool ExistsAt(string root) =>
            File.Exists(Path.Combine(root, "node.exe"))
            && File.Exists(Path.Combine(root, "npm.cmd"));
    }
}
