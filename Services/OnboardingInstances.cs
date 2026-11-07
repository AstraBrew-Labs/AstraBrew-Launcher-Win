using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 内置实例的安装计划：装哪个版本、走哪条通道、镜像能不能用。
    /// <see cref="Note"/> / <see cref="Error"/> 都是给界面显示的一句话。
    /// </summary>
    public sealed record BuiltinSetupPlan(
        string Revision,
        string ChannelName,
        bool MirrorUsable,
        IReadOnlyList<SourceTarget> Targets,
        string? Note,
        string? Error);

    /// <summary>
    /// 引导第 4 步的「薄编排」：把已有的版本列表、镜像、安装器、实例表串成两条路——
    /// 本地实例（扫描 / 手动添加 / 补依赖 / 启用）与内置实例（装好并启用）。
    ///
    /// 这里不重复实现任何安装逻辑：克隆与依赖仍然是 <see cref="TavernInstaller"/>，
    /// 实例表仍然是 <see cref="TavernInstances"/>。引导只是决定「先做什么、做完怎么激活」。
    /// </summary>
    public static class OnboardingInstances
    {
        /// <summary>内置实例能不能装/跑：缺 Node 或 Git 都装不成。</summary>
        public static bool HasRuntimeForBuiltin(out string reason)
        {
            if (RuntimeProbe.LocateNode(LauncherSettings.Runtime) is null && RuntimeProbe.LocateNode() is null)
            {
                reason = "还没有可用的 Node.js，请先回上一步装好运行环境。";
                return false;
            }

            if (GitCommand.LocateGit() is null)
            {
                reason = "还没有可用的 Git，装不了内置实例，请先回上一步把 Git 装上。";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        /// <summary>
        /// 算出内置实例的安装计划：最新稳定版 + 当前下载通道 + 镜像是否可用。
        /// 取不到发布列表时退回内置实例自己的版本号，保证「能装」而不是卡在网络上。
        /// </summary>
        public static async Task<BuiltinSetupPlan> PlanBuiltinAsync(CancellationToken token)
        {
            ReleaseResult releases = await TavernReleases.GetAsync(force: false, token).ConfigureAwait(false);

            if (releases.Releases.Count > 0)
            {
                // 与版本页同一套：列表写进共享状态，后续页面不必再拉一次
                TavernInstances.SetReleases(releases.Releases);
            }

            TavernRelease? latest = releases.Releases.FirstOrDefault(release => !release.Prerelease);
            string revision = latest?.Version ?? TavernInstances.LatestStable;

            DownloadChannel channel = LauncherSettings.Channel;
            bool mirrorUsable = false;

            if (TavernMirrors.IsMirror(channel))
            {
                if (TavernMirrors.TryGetCached(channel, out MirrorRefs cached))
                {
                    mirrorUsable = cached.HasTag(revision);
                }
                else
                {
                    MirrorRefs refs = await TavernMirrors.GetAsync(channel, force: false, token).ConfigureAwait(false);
                    mirrorUsable = refs.HasTag(revision);
                }
            }

            IReadOnlyList<SourceTarget> targets = TavernSource.InstallTargets(channel, mirrorUsable);

            string note = mirrorUsable
                ? $"{LauncherSettings.ChannelName(channel)} 镜像可用（{revision}）"
                : channel == DownloadChannel.Github
                    ? GithubAccel.Enabled
                        ? "GitHub 通道：先走加速，失败回退直连"
                        : "GitHub 直连"
                    : $"镜像里还没有 {revision}，将回退 GitHub";

            return new BuiltinSetupPlan(
                revision,
                LauncherSettings.ChannelName(channel),
                mirrorUsable,
                targets,
                note,
                null);
        }

        /// <summary>装内置实例；成功后重新读盘并激活。</summary>
        public static async Task<TavernInstallResult> InstallBuiltinAsync(
            BuiltinSetupPlan plan,
            IProgress<TavernInstallProgress>? progress,
            CancellationToken token)
        {
            TavernInstallResult result = await TavernInstaller
                .InstallAsync(plan.Revision, plan.Targets, progress, token)
                .ConfigureAwait(false);

            // 版本必须以磁盘为准：安装器写完目录，实例表还不知道
            TavernInstances.ReloadBuiltin();

            if (result.Success && TavernInstances.BuiltinRunnable)
            {
                TavernInstances.Use(TavernInstances.Builtin);
            }

            return result;
        }

        /// <summary>给内置实例补依赖（代码已下载、只缺 node_modules 的情况）。</summary>
        public static async Task<TavernInstallResult> InstallBuiltinDependenciesAsync(
            IProgress<TavernInstallProgress>? progress,
            CancellationToken token)
        {
            TavernInstallResult result = await TavernInstaller
                .InstallDependenciesAsync(progress, token)
                .ConfigureAwait(false);

            TavernInstances.ReloadBuiltin();

            if (result.Success && TavernInstances.BuiltinRunnable)
            {
                TavernInstances.Use(TavernInstances.Builtin);
            }

            return result;
        }

        /// <summary>给某个本地实例补依赖，装完刷新它的状态。</summary>
        public static async Task<TavernInstallResult> InstallLocalDependenciesAsync(
            TavernInstance instance,
            IProgress<TavernInstallProgress>? progress,
            CancellationToken token)
        {
            TavernInstallResult result = await TavernInstaller
                .InstallDependenciesAsync(instance.Path, progress, token)
                .ConfigureAwait(false);

            TavernInstances.ReloadLocal(instance);
            return result;
        }

        /// <summary>从 <c>package.json</c> 所在目录添加一个本地实例。</summary>
        public static LocalAddStatus AddLocalAt(string packageJsonPath, out TavernInstance? instance)
        {
            instance = null;

            string? directory = File.Exists(packageJsonPath)
                ? Path.GetDirectoryName(packageJsonPath)
                : packageJsonPath;

            if (directory is null || !Directory.Exists(directory))
            {
                return LocalAddStatus.Missing;
            }

            return TavernInstances.AddLocal(directory, out instance);
        }

        /// <summary>
        /// 启用一个实例：先按磁盘现状刷新，再交给 <see cref="TavernInstances.Use"/>。
        /// 缺依赖时 <c>Use</c> 会拒绝——这里如实把结果返回，由界面提示「先补依赖」。
        /// </summary>
        public static bool Enable(TavernInstance instance)
        {
            if (!instance.IsBuiltin)
            {
                TavernInstances.ReloadLocal(instance);
            }

            TavernInstances.Use(instance);

            return ReferenceEquals(TavernInstances.Current, instance);
        }
    }
}
