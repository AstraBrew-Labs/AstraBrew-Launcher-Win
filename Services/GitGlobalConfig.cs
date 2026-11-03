using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>一次全局 git 配置改动的结果：失败时 <see cref="Message"/> 可直接给用户看。</summary>
    public readonly record struct GitConfigResult(bool Ok, string Message);

    /// <summary>
    /// Git 全局加速：把 gh-proxy 的 <c>insteadOf</c> 规则写进**用户的全局 git 配置**，
    /// 让终端、酒馆自动更新、酒馆里装的扩展等「命令行 git」也走加速。
    ///
    /// 它是 <see cref="GithubAccel"/> 那个总开关的另一半，不是一个独立开关：
    /// · 应用内请求（版本列表 API、安装包下载、扩展克隆）由 <see cref="GithubAccel"/> 拼前缀；
    /// · 命令行 git 拿到的地址是干净的 <c>https://github.com/…</c>，拼不了前缀，
    ///   只能靠这条 <c>insteadOf</c> 让 git 自己改写。
    /// 两者必须同开同关，否则会出现「界面里显示已加速、酒馆更新却还是直连」的错位。
    ///
    /// 写的是 <c>~/.gitconfig</c>，关掉应用也还在——这是「全局」的应有之义，
    /// 关闭加速服务时要把它撤干净，不能留一条规则让用户以为已经恢复直连。
    ///
    /// 写法照 GH-Proxy 文档「Git 全局自动替换 (insteadOf)」：
    /// <code>
    /// git config --global url."https://gh-proxy.org/https://github.com/".insteadOf "https://github.com/"
    /// git config --global --unset url."https://gh-proxy.org/https://github.com/".insteadOf
    /// </code>
    /// 键名里带完整 URL，所以它天然只命中 github.com —— 相当于一张白名单，
    /// Gitee / GitCode / 自建 Git 服务的地址不会被改写。
    /// </summary>
    public static class GitGlobalConfig
    {
        /// <summary>被替换的原始前缀：只接管 github.com，别的远端一律不动。</summary>
        public const string Target = "https://github.com/";

        /// <summary>
        /// 配置键：<c>url.&lt;加速前缀&gt;&lt;原前缀&gt;.insteadOf</c>。
        /// 键名里带完整 URL，所以它只匹配 GitHub，不会误伤 Gitee / GitCode / 自建 Git 服务。
        /// </summary>
        public static string Key => $"url.{GithubAccel.Prefix}{Target}.insteadOf";

        /// <summary>
        /// 按加速服务的开关把全局 git 配置对齐：开着保证规则在，关着保证规则不在。
        /// 已经是目标状态就什么都不做（返回 <c>null</c>）——开关每次进设置页都会同步一次，
        /// 不该每次都去写一遍 <c>.gitconfig</c>。
        /// </summary>
        /// <returns>需要提醒用户的失败；<c>null</c> 表示已经一致或已处理成功。</returns>
        public static async Task<GitConfigResult?> SyncAsync(bool enabled)
        {
            bool? applied = await IsAppliedAsync().ConfigureAwait(false);

            if (applied is null)
            {
                // 没有 Git：开着时规则写不上，得说一声；关着时本来就没东西可撤
                return enabled
                    ? new GitConfigResult(false, "没有找到 Git，全局加速规则没能写入；命令行与酒馆更新里的 GitHub 拉取仍是直连。")
                    : null;
            }

            if (applied == enabled)
            {
                return null;
            }

            return await (enabled ? ApplyAsync() : RevertAsync()).ConfigureAwait(false);
        }

        /// <summary>写入全局配置。已存在同键的值时整体替换，避免多次开关攒出一串重复项。</summary>
        public static async Task<GitConfigResult> ApplyAsync()
        {
            if (RuntimeProbe.LocateGit() is not { Length: > 0 } git)
            {
                return new GitConfigResult(false, "没有找到 Git，请先在「运行环境」里安装后再打开这个开关。");
            }

            (int code, _, string stderr) = await RunAsync(
                git,
                "config",
                "--global",
                "--replace-all",
                Key,
                Target).ConfigureAwait(false);

            if (code != 0)
            {
                return new GitConfigResult(false, Explain(stderr, "写入全局 git 配置失败。"));
            }

            // 写完复核一次：git config 偶尔会因文件被占用而静默失败，只看退出码不够
            return await IsAppliedAsync().ConfigureAwait(false) == true
                ? new GitConfigResult(true, string.Empty)
                : new GitConfigResult(false, "配置写完后没能读到，可能是 .gitconfig 被其它程序占用，请重试。");
        }

        /// <summary>移除这条规则，恢复到直连。</summary>
        public static async Task<GitConfigResult> RevertAsync()
        {
            if (RuntimeProbe.LocateGit() is not { Length: > 0 } git)
            {
                // 没有 Git 时全局配置就没有意义；按「已恢复」处理，让开关能正常回到关闭
                return new GitConfigResult(true, string.Empty);
            }

            (int code, _, string stderr) = await RunAsync(
                git,
                "config",
                "--global",
                "--unset-all",
                Key).ConfigureAwait(false);

            // git config --unset 在键不存在时返回 5：本来就没写，等于已经是目标状态
            if (code != 0 && code != 5)
            {
                return new GitConfigResult(false, Explain(stderr, "移除全局 git 配置失败。"));
            }

            return new GitConfigResult(true, string.Empty);
        }

        /// <summary>
        /// 全局配置里现在有没有这条规则。读的是 git 本身，不是应用设置——
        /// 用户可能在终端里手动改过，界面得以实际配置为准。
        /// 返回 <c>null</c> 表示没有 Git、无法判断。
        /// </summary>
        public static async Task<bool?> IsAppliedAsync()
        {
            if (RuntimeProbe.LocateGit() is not { Length: > 0 } git)
            {
                return null;
            }

            (int code, string stdout, _) = await RunAsync(
                git,
                "config",
                "--global",
                "--get-all",
                Key).ConfigureAwait(false);

            // 退出码 1 = 没有这个键
            if (code != 0)
            {
                return false;
            }

            return stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Any(line => string.Equals(line.Trim(), Target, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>把 git 的 stderr 压成一行，前面拼一句人话。</summary>
        private static string Explain(string stderr, string fallback)
        {
            string detail = string.Join(
                " ",
                stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            return detail.Length > 0 ? $"{fallback}（{detail}）" : fallback;
        }

        /// <summary>
        /// 跑一条 git 命令。参数走 <c>ArgumentList</c> 逐个传，键名里带 <c>://</c> 与引号也不会被拆坏。
        /// </summary>
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
                return (-1, string.Empty, "无法启动 git 进程。");
            }

            // 两个流必须同时读，只读一个会在另一个写满管道时互锁
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);

            return (process.ExitCode, (await stdout.ConfigureAwait(false)).Trim(), (await stderr.ConfigureAwait(false)).Trim());
        }
    }
}
