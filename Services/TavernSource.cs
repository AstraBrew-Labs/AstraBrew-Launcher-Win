using System.Collections.Generic;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 酒馆源码的三个仓库地址，以及「加速开关」怎么落到具体链接上。
    ///
    /// 全应用只有这一处拼仓库地址：设置页显示、下载、更新都从这里取，
    /// 免得哪天改镜像域名时漏掉某一处。
    /// </summary>
    public static class TavernSource
    {
        /// <summary>AstraBrew 自建镜像，国内直连最稳。</summary>
        public const string GiteeRepo = "https://gitee.com/AstraBrew-Labs/SillyTavern";

        /// <summary>GitCode 上的同步镜像，Gitee 抽风时的备选。</summary>
        public const string GitCodeRepo = "https://gitcode.com/GitHub_Trending/si/SillyTavern";

        /// <summary>官方仓库。</summary>
        public const string GithubRepo = "https://github.com/SillyTavern/SillyTavern";

        /// <summary>某个通道对应的仓库地址，不含加速。</summary>
        public static string RepoUrl(DownloadChannel channel) => channel switch
        {
            DownloadChannel.Gitee => GiteeRepo,
            DownloadChannel.GitCode => GitCodeRepo,
            _ => GithubRepo,
        };

        /// <summary>
        /// 当前配置下真正会连的地址——设置页拿它显示，用户能一眼看出加速有没有生效。
        /// </summary>
        /// <remarks>
        /// 这里只描述「下载源」这一行：镜像通道显示镜像地址（**永不加前缀**，中转方不认镜像站），
        /// GitHub 通道交给 <see cref="GithubAccel.Apply"/>。
        /// 其它 GitHub 用途（版本列表、Git 安装包）的加速由 <see cref="GithubAccel"/> 单独决定。
        /// </remarks>
        public static string ActiveUrl =>
            LauncherSettings.Channel == DownloadChannel.Github
                ? GithubAccel.Apply(GithubRepo)
                : RepoUrl(LauncherSettings.Channel);

        /// <summary>
        /// 内置实例克隆仓库时的地址顺序——这是加速服务唯一的例外。
        ///
        /// 规则：
        /// · 下载源选「GitHub 直连」：加速开着就先走 <c>gh-proxy.org</c>，失败再直连。
        /// · 下载源选镜像且镜像里有这个版本：只走镜像，不掺 GitHub。
        /// · 下载源选镜像但镜像里没有：退回 GitHub <b>直连</b>，不加速。用户选的是镜像，
        ///   没选过直连，就不该替他决定要不要中转；想用加速得自己把下载源切过去。
        ///
        /// 但注意：加速开关打开时，<see cref="GitGlobalConfig"/> 已经往全局 git 配置里写了
        /// <c>insteadOf</c>，第三条里那个「直连」地址交到 git 手上会被它自己改写成加速地址。
        /// 这是开关打开后的预期行为，不是这里逻辑失效。
        /// </summary>
        public static IReadOnlyList<SourceTarget> InstallTargets(DownloadChannel channel, bool mirrorUsable)
        {
            if (channel != DownloadChannel.Github && mirrorUsable)
            {
                return new[] { new SourceTarget(LauncherSettings.ChannelName(channel), RepoUrl(channel)) };
            }

            return channel == DownloadChannel.Github
                ? GithubAccel.Targets(GithubRepo)
                : new[] { new SourceTarget(GithubAccel.DirectName, GithubRepo) };
        }
    }
}
