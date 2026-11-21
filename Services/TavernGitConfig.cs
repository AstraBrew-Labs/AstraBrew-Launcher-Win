using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>一次 git 配置改动的结果：失败时 <see cref="Message"/> 可直接给用户看。</summary>
    public readonly record struct GitConfigResult(bool Ok, string Message);

    /// <summary>
    /// 酒馆专属的 Git 加速配置：把 <c>insteadOf</c> 规则写进**启动器自己管的一份 gitconfig**，
    /// 再通过 <c>GIT_CONFIG_GLOBAL</c> 让酒馆相关的 git 命令读它。
    ///
    /// 为什么不写 <c>~/.gitconfig</c>：那是用户的东西。以前这条规则写进全局，
    /// 用户在终端里 clone 任何仓库都会被改写成中转地址，而且关掉应用也还在——
    /// 加速服务没有资格改别人电脑上的全局行为。
    ///
    /// 现在的语义：
    /// · 文件落在 <see cref="FilePath"/>（<c>%AppData%\AstraBrew Launcher\data\git\gitconfig</c>）；
    /// · 所有「为酒馆服务」的 git 进程都通过 <see cref="Apply"/> 注入 <c>GIT_CONFIG_GLOBAL</c>：
    ///   启动器自己拉实例 / 装扩展 / 查更新，以及启动酒馆时传给 node（它再 spawn 出去的 git 会继承）；
    /// · 用户的 <c>~/.gitconfig</c> 一个字节都不动。
    ///
    /// 代价是：这个 git 进程读不到用户全局配置里的用户名、凭据、代理等设置。
    /// 拉公共仓库不需要这些；确实需要时，用户可以把自己的配置 <c>include</c> 进来
    /// （<see cref="FilePath"/> 就是个普通 gitconfig，可以随手编辑）。
    /// </summary>
    public static class TavernGitConfig
    {
        /// <summary>被替换的原始前缀：只接管 github.com，别的远端一律不动。</summary>
        public const string Target = "https://github.com/";

        /// <summary>
        /// 配置键：<c>url.&lt;加速前缀&gt;&lt;原前缀&gt;.insteadOf</c>。
        /// 键名里带完整 URL，所以它只匹配 GitHub，不会误伤 Gitee / GitCode / 自建 Git 服务。
        /// </summary>
        public static string Key => $"url.{GithubAccel.Prefix}{Target}.insteadOf";

        /// <summary>专属配置文件的路径。</summary>
        public static string FilePath { get; } = Path.Combine(AppPaths.RoamingRoot, "data", "git", "gitconfig");

        /// <summary>旧版写进用户全局配置的痕迹清理完了没有。</summary>
        private static string LegacyMarker => Path.Combine(AppPaths.RoamingRoot, "data", "git", ".global-cleaned");

        /// <summary>
        /// 给一个即将启动的 git 进程注入 <c>GIT_CONFIG_GLOBAL</c>。
        ///
        /// 文件不存在时**什么都不设**：没有专属配置就按 git 默认走用户全局，行为与不加速时一致，
        /// 不会因为「指了一个空文件」把用户自己的用户名 / 凭据也一起弄丢。
        /// </summary>
        public static void Apply(ProcessStartInfo startInfo)
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            startInfo.Environment["GIT_CONFIG_GLOBAL"] = FilePath;
        }

        /// <summary>
        /// 按开关把专属配置对齐：开着保证规则在，关着保证规则不在。
        /// 已经是目标状态就返回 <c>null</c>——进设置页会同步一次，不该每次都写盘。
        /// </summary>
        public static async Task<GitConfigResult?> SyncAsync(bool enabled)
        {
            bool? applied = await IsAppliedAsync().ConfigureAwait(false);

            if (applied is null)
            {
                return enabled
                    ? new GitConfigResult(false, L.Get("Miss.GitCfg.NoGitSoft"))
                    : null;
            }

            if (applied == enabled)
            {
                return null;
            }

            return await (enabled ? ApplyAsync() : RevertAsync()).ConfigureAwait(false);
        }

        /// <summary>写入专属配置。同键已有值时整体替换，避免反复开关攒出一串重复项。</summary>
        public static async Task<GitConfigResult> ApplyAsync()
        {
            if (RuntimeProbe.LocateGit() is not { Length: > 0 } git)
            {
                return new GitConfigResult(false, L.Get("Miss.GitCfg.NoGitHard"));
            }

            try
            {
                // git 会创建文件，但不会替我们建父目录
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            }
            catch (Exception ex)
            {
                return new GitConfigResult(false, $"{L.Get("Miss.GitCfg.WriteFail")}（{ex.Message}）");
            }

            (int code, _, string stderr) = await RunAsync(
                git,
                "config",
                "--file",
                FilePath,
                "--replace-all",
                Key,
                Target).ConfigureAwait(false);

            if (code != 0)
            {
                return new GitConfigResult(false, Explain(stderr, L.Get("Miss.GitCfg.WriteFail")));
            }

            // 写完复核一次：只看退出码不够，文件被占用时 git 可能静默失败
            return await IsAppliedAsync().ConfigureAwait(false) == true
                ? new GitConfigResult(true, string.Empty)
                : new GitConfigResult(false, L.Get("Miss.GitCfg.ReadBack"));
        }

        /// <summary>移除这条规则，恢复到直连。文件留着，用户可能往里面加了别的配置。</summary>
        public static async Task<GitConfigResult> RevertAsync()
        {
            if (!File.Exists(FilePath))
            {
                // 没有专属配置就等于没有规则，开关可以直接回到关闭
                return new GitConfigResult(true, string.Empty);
            }

            if (RuntimeProbe.LocateGit() is not { Length: > 0 } git)
            {
                return new GitConfigResult(true, string.Empty);
            }

            (int code, _, string stderr) = await RunAsync(
                git,
                "config",
                "--file",
                FilePath,
                "--unset-all",
                Key).ConfigureAwait(false);

            // 退出码 5 = 键不存在，本来就没写，等于已经是目标状态
            if (code != 0 && code != 5)
            {
                return new GitConfigResult(false, Explain(stderr, L.Get("Miss.GitCfg.RemoveFail")));
            }

            return new GitConfigResult(true, string.Empty);
        }

        /// <summary>
        /// 专属配置里现在有没有这条规则。读的是 git 本身，不是应用设置——用户可能手改过文件。
        /// 返回 <c>null</c> 表示没有 Git，无法判断。
        /// </summary>
        public static async Task<bool?> IsAppliedAsync()
        {
            if (!File.Exists(FilePath))
            {
                return false;
            }

            if (RuntimeProbe.LocateGit() is not { Length: > 0 } git)
            {
                return null;
            }

            (int code, string stdout, _) = await RunAsync(
                git,
                "config",
                "--file",
                FilePath,
                "--get-all",
                Key).ConfigureAwait(false);

            if (code != 0)
            {
                return false;
            }

            return stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Any(line => string.Equals(line.Trim(), Target, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 清掉旧版本写进用户全局配置的那条规则。
        ///
        /// 0.3.0-Beta.2 之前加速服务是「全局」的，升级后必须撤掉——否则用户升级完，
        /// 终端里 clone 任何仓库仍然被改写成中转地址，而界面上已经显示「不影响全局配置」。
        /// 只做一次，做完写标记文件；没装 Git 时不写标记，下次启动再试。
        /// </summary>
        public static async Task CleanLegacyGlobalAsync()
        {
            if (File.Exists(LegacyMarker))
            {
                return;
            }

            if (RuntimeProbe.LocateGit() is not { Length: > 0 } git)
            {
                return;
            }

            try
            {
                // --global 指向用户的 ~/.gitconfig；键不存在返回 5，忽略即可
                await RunAsync(git, "config", "--global", "--unset-all", Key).ConfigureAwait(false);

                Directory.CreateDirectory(Path.GetDirectoryName(LegacyMarker)!);
                File.WriteAllText(LegacyMarker, DateTimeOffset.Now.ToString("O"));
            }
            catch (Exception)
            {
                // 清不掉就下次启动再试，不影响功能
            }
        }

        /// <summary>把 git 的 stderr 压成一行，前面拼一句人话。</summary>
        private static string Explain(string stderr, string fallback)
        {
            string detail = string.Join(
                " ",
                stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            return detail.Length > 0 ? $"{fallback}（{detail}）" : fallback;
        }

        /// <summary>跑一条 git 命令。参数走 <c>ArgumentList</c>，键名里带 <c>://</c> 也不会被拆坏。</summary>
        private static async Task<(int Code, string Stdout, string Stderr)> RunAsync(string git, params string[] args)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = git,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (string arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return (-1, string.Empty, L.Get("Miss.GitCfg.CannotStart"));
            }

            // 两个流必须同时读，只读一个会在另一个写满管道时互锁
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);

            return (process.ExitCode, (await stdout.ConfigureAwait(false)).Trim(), (await stderr.ConfigureAwait(false)).Trim());
        }
    }
}
