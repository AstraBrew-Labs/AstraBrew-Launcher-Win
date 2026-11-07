using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 跑 <c>git</c> 的统一入口：定位可执行文件、拼 <c>clone</c> 参数、注入代理与非交互环境变量。
    ///
    /// 三处「测一下能不能拉」的地方（GitHub 连通性、引导的镜像测速、后续的通道验证）都用这一份，
    /// 免得各自维护一套参数，改了一处漏了另一处。
    /// </summary>
    internal static class GitCommand
    {
        /// <summary>克隆下来的仓库目录名（放在调用方给的临时工作目录下）。</summary>
        public const string RepoFolderName = "repo";

        /// <summary>隔离用的空全局配置文件名（只在需要的场合创建）。</summary>
        private const string IsolatedConfigName = "empty.gitconfig";

        /// <summary>定位 git：先按设置里的运行时找内置，找不到再回退系统。</summary>
        public static string? LocateGit()
        {
            string? git = RuntimeProbe.LocateGit(LauncherSettings.Runtime);

            // 选「内置」但内置里没有时，回退系统 git：调用方（连通性 / 测速）关心的是
            // 「这台机器能不能拉」，与启动器用哪套运行时无关。
            return git ?? RuntimeProbe.LocateGit();
        }

        /// <summary>
        /// 构造一次「只取元数据」的浅克隆：<c>--depth 1 --single-branch --filter=blob:none --no-checkout</c>。
        /// 不下载文件内容、不落工作区，几秒到几十秒就能判断这条通道通不通。
        /// </summary>
        /// <param name="workDirectory">
        /// 临时工作目录（调用方负责事后删除）。仓库会克隆到其下的 <c>repo</c> 子目录。
        /// </param>
        /// <param name="isolateGlobalConfig">
        /// 是否用一份空的 GIT_CONFIG_GLOBAL 隔离用户的全局 git 配置。
        /// 镜像测速时**必须**为 true：加速开关会把 <c>~/.gitconfig</c> 写上 <c>insteadOf</c>，
        /// 不隔离的话「GitHub 直连」会被 git 自己改写成加速地址，四条通道只剩三条。
        /// </param>
        public static ProcessStartInfo CreateClone(
            string git,
            string url,
            string workDirectory,
            bool isolateGlobalConfig) =>
            CreateCloneCore(git, url, workDirectory, isolateGlobalConfig, withContent: false);

        /// <summary>
        /// 测速用的**真实**克隆：<c>--depth 1 --single-branch --progress</c>，会把工作区的文件全部拉下来。
        ///
        /// 和 <see cref="CreateClone"/> 的区别就在这一点：那个只取元数据（不下载文件内容），
        /// 一两秒就「成功」了，拿它给四条通道测速，测出来的快慢与真实安装速度毫无关系
        /// （镜像站常见的现象是元数据秒回、拉文件很慢）。
        /// </summary>
        /// <remarks>
        /// 必须带 <c>--progress</c>：stderr 被重定向之后 git 默认不输出进度，
        /// 界面就只能干等一个「等着」。
        /// </remarks>
        /// <param name="useProxy">
        /// 三态：null 跟随设置里的代理方式；true 强制走代理；false 强制不走代理。
        /// 测速要同时试「走代理」「不走代理」两条线，所以不能一律跟着全局设置走。
        /// </param>
        public static ProcessStartInfo CreateSpeedTestClone(
            string git,
            string url,
            string workDirectory,
            bool isolateGlobalConfig,
            bool? useProxy = null) =>
            CreateCloneCore(git, url, workDirectory, isolateGlobalConfig, withContent: true, useProxy);

        private static ProcessStartInfo CreateCloneCore(
            string git,
            string url,
            string workDirectory,
            bool isolateGlobalConfig,
            bool withContent,
            bool? useProxy = null)
        {
            var startInfo = CreateBase(git);

            // 代理：git 只认固定地址；PAC / 自动检测给不出来，就只能直连
            ProxyRoute route = ProxyRoute.Resolve();
            bool useProxyRoute = useProxy ?? route.UseProxy;

            if (useProxyRoute && route.Address is { Length: > 0 } address)
            {
                foreach (string key in new[] { "http.proxy", "https.proxy" })
                {
                    startInfo.ArgumentList.Add("-c");
                    startInfo.ArgumentList.Add($"{key}={address}");
                }
            }
            else if (useProxy is false)
            {
                // 明确要求「不走代理」：光不给 -c 还不够，环境变量里的代理也会被 git 捡走
                foreach (string key in new[] { "http.proxy", "https.proxy" })
                {
                    startInfo.ArgumentList.Add("-c");
                    startInfo.ArgumentList.Add($"{key}=");
                }

                foreach (string key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" })
                {
                    startInfo.Environment[key] = string.Empty;
                }
            }

            startInfo.ArgumentList.Add("clone");
            startInfo.ArgumentList.Add("--depth");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("--single-branch");
            startInfo.ArgumentList.Add("--no-tags");

            if (withContent)
            {
                // 测速：真下文件，带进度
                startInfo.ArgumentList.Add("--progress");
            }
            else
            {
                // 连通性：只取元数据，不落工作区，越快越好
                startInfo.ArgumentList.Add("--filter=blob:none");
                startInfo.ArgumentList.Add("--no-checkout");
                startInfo.ArgumentList.Add("--quiet");
            }

            startInfo.ArgumentList.Add(url);
            startInfo.ArgumentList.Add(Path.Combine(workDirectory, RepoFolderName));

            // 不弹凭据框、不拉 LFS：测连通性不该被任何交互卡住
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            startInfo.Environment["GIT_ASKPASS"] = "echo";
            startInfo.Environment["GIT_LFS_SKIP_SMUDGE"] = "1";
            startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";

            if (isolateGlobalConfig)
            {
                string isolated = Path.Combine(workDirectory, IsolatedConfigName);
                try
                {
                    Directory.CreateDirectory(workDirectory);
                    File.WriteAllText(isolated, string.Empty);
                    startInfo.Environment["GIT_CONFIG_GLOBAL"] = isolated;
                }
                catch (Exception)
                {
                    // 建不出空配置就退回用户全局配置：测速结果可能被 insteadOf 影响，但不至于跑不起来
                }
            }

            return startInfo;
        }

        /// <summary>通用的 <c>git &lt;命令&gt;</c> 起点（如 <c>config</c>、<c>ls-remote</c>）。</summary>
        public static ProcessStartInfo CreateBase(string git)
        {
            var startInfo = new ProcessStartInfo(git)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            // 内置 MinGit 的辅助程序在相邻目录（cmd\、mingw64\bin\），缺了 PATH 会找不到
            if (Path.GetDirectoryName(git) is { Length: > 0 } gitDir)
            {
                string root = Path.GetDirectoryName(gitDir) ?? gitDir;
                startInfo.Environment["PATH"] = string.Join(
                    Path.PathSeparator,
                    new[]
                    {
                        gitDir,
                        Path.Combine(root, "bin"),
                        Path.Combine(root, "mingw64", "bin"),
                        Path.Combine(root, "cmd"),
                    });
            }

            return startInfo;
        }

        /// <summary>杀掉整棵进程树并等它退干净（否则临时目录还占着句柄）。</summary>
        public static void Kill(Process process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (Exception)
            {
                // 已经自己退了
            }
        }

        /// <summary>把多行 git 输出压成一句能放进状态行的说明。</summary>
        public static string FirstLine(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string value = text.Trim();
            int breakAt = value.IndexOfAny(new[] { '\r', '\n' });
            return breakAt > 0 ? value[..breakAt].Trim() : value;
        }

        /// <summary>一次浅克隆需要的参数集合（供需要自定义参数的调用方拼装）。</summary>
        public static IReadOnlyList<string> ShallowCloneArguments(string url, string targetDirectory) => new[]
        {
            "clone",
            "--depth", "1",
            "--single-branch",
            "--filter=blob:none",
            "--no-checkout",
            "--quiet",
            url,
            targetDirectory,
        };
    }
}
