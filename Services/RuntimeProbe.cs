using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace AstraBrew_Launcher.Services
{
    /// <summary>一套运行环境的真实检测结果；为 null 的项表示该组件未安装。</summary>
    public sealed record RuntimeEnvironment(
        string? Git,
        string? Node,
        string? Npm,
        string? Caddy,
        string? Pm2,
        string? WebView2)
    {
        public static RuntimeEnvironment Empty { get; } = new(null, null, null, null, null, null);

        /// <summary>按安装弹窗里用的字段名取版本，装完回填日志时用。</summary>
        public string? VersionOf(string component) => component switch
        {
            "Git" => Git,
            "Node" => Node,
            "Npm" => Npm,
            "Caddy" => Caddy,
            "Pm2" => Pm2,
            _ => WebView2,
        };
    }

    /// <summary>
    /// 运行环境真实探测：内置环境查启动器自己的 bin 目录，系统环境按 PATH 与 npm 全局目录找。
    ///
    /// 约定：
    /// · 只读探测，不安装、不改配置；找不到就是 null，界面统一显示「未安装」。
    /// · 版本号一律取自工具自己的 <c>--version</c> 输出，不从目录名或文件名猜——
    ///   目录名经常和实际版本对不上，猜出来的版本比没有更糟。
    /// · 探测要起子进程，全部放线程池、带 5 秒超时；任何异常都吞成 null，绝不抛到界面。
    /// · 结果缓存 30 秒，来回切页签不会反复拉进程；安装完这类需要立刻生效的场景传 <c>force</c>。
    /// </summary>
    public static class RuntimeProbe
    {
        /// <summary>酒馆要求的 Node.js 最低主版本；低于它就在行上标「版本过低」。</summary>
        public const int MinimumNodeMajor = 18;

        /// <summary>WebView2 Evergreen 运行时在 EdgeUpdate 里的固定 Client ID。</summary>
        private const string WebView2ClientId = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);

        private static readonly Regex VersionPattern = new(@"\d+\.\d+\.\d+", RegexOptions.Compiled);

        private static readonly object CacheGate = new();
        private static readonly Dictionary<RuntimeSource, (RuntimeEnvironment Value, DateTime At)> Cache = new();

        /// <summary>内置环境可能落的两个根：现役是 <c>bin</c>，历史版本用过 <c>lib</c>，两个都认。</summary>
        private static readonly string[] BuiltinRoots =
        {
            Path.Combine(AppPaths.RoamingRoot, "bin"),
            Path.Combine(AppPaths.RoamingRoot, "lib"),
        };

        /// <summary>
        /// 找 Git 的可执行文件：内置目录优先，其次系统 PATH。
        ///
        /// 与 <see cref="ProbeAsync"/> 的区别是这里要的是「路径」而不是「版本」——
        /// 安装器与镜像比对都要起子进程，光知道装过没用。
        /// </summary>
        public static string? LocateGit() => FirstExisting(BuiltinGitCandidates()) ?? FindOnPath("git");

        /// <summary>找 Node.js 的可执行文件：内置目录优先，其次系统 PATH。</summary>
        public static string? LocateNode() => FirstExisting(BuiltinNodeCandidates()) ?? FindOnPath("node");

        /// <summary>
        /// 按设置里的环境来源找 Git：内置只看启动器目录，系统只看 PATH。
        ///
        /// 与无参版本的区别是「不跨来源回退」：选了内置环境却拿系统的 git 去跑，
        /// 用户以为在用一个自包含的便携环境，实际并没有——启动时的环境检查必须问得准。
        /// </summary>
        public static string? LocateGit(RuntimeSource source) => source == RuntimeSource.Builtin
            ? FirstExisting(BuiltinGitCandidates())
            : FindOnPath("git");

        /// <summary>按环境来源找 Node.js，规则同 <see cref="LocateGit(RuntimeSource)"/>。</summary>
        public static string? LocateNode(RuntimeSource source) => source == RuntimeSource.Builtin
            ? FirstExisting(BuiltinNodeCandidates())
            : FindOnPath("node");

        /// <summary>
        /// 找 npm 的入口脚本（<c>&lt;node 目录&gt;\node_modules\npm\bin\npm-cli.js</c>）。
        ///
        /// 不直接用 <c>npm.cmd</c>：.cmd 是批处理，重定向 stdout/stderr 时得先起 cmd.exe，
        /// 退出码与控制台代码页都会拐一道弯；用 node 跑这个 js 拿到的是干净的行流。
        /// </summary>
        public static string? LocateNpmCli()
        {
            var nodes = new List<string>(BuiltinNodeCandidates().SelectMany(group => group));

            if (FindOnPath("node") is { } pathNode)
            {
                nodes.Add(pathNode);
            }

            foreach (string node in nodes)
            {
                if (!File.Exists(node) || Path.GetDirectoryName(node) is not { Length: > 0 } dir)
                {
                    continue;
                }

                string cli = Path.Combine(dir, "node_modules", "npm", "bin", "npm-cli.js");
                if (File.Exists(cli))
                {
                    return cli;
                }
            }

            return null;
        }

        private static IEnumerable<string>[] BuiltinGitCandidates() => new[]
        {
            Under(BuiltinRoots, "mingit", "cmd", "git.exe"),
            Under(BuiltinRoots, "mingit", "bin", "git.exe"),
            Under(BuiltinRoots, "git", "cmd", "git.exe"),
            Under(BuiltinRoots, "git", "bin", "git.exe"),
        };

        private static IEnumerable<string>[] BuiltinNodeCandidates() => new[]
        {
            Under(BuiltinRoots, "nodejs", "node.exe"),
            Under(BuiltinRoots, "node", "node.exe"),
        };

        /// <summary>探测指定来源的一套环境。永不抛异常，失败按「全未安装」返回。</summary>
        public static Task<RuntimeEnvironment> ProbeAsync(RuntimeSource source, bool force = false)
        {
            if (!force && TryGetCached(source, out RuntimeEnvironment cached))
            {
                return Task.FromResult(cached);
            }

            return Task.Run(async () =>
            {
                RuntimeEnvironment value;
                try
                {
                    value = source == RuntimeSource.Builtin
                        ? await ProbeBuiltinAsync().ConfigureAwait(false)
                        : await ProbeSystemAsync().ConfigureAwait(false);
                }
                catch
                {
                    value = RuntimeEnvironment.Empty;
                }

                lock (CacheGate)
                {
                    Cache[source] = (value, DateTime.UtcNow);
                }

                return value;
            });
        }

        /// <summary>Node.js 主版本是否低于酒馆要求。</summary>
        public static bool IsNodeOutdated(string? version)
        {
            if (version is null)
            {
                return false;
            }

            string digits = version.TrimStart('v', 'V');
            int dot = digits.IndexOf('.');
            string major = dot > 0 ? digits[..dot] : digits;

            return int.TryParse(major, out int value) && value < MinimumNodeMajor;
        }

        private static bool TryGetCached(RuntimeSource source, out RuntimeEnvironment value)
        {
            lock (CacheGate)
            {
                if (Cache.TryGetValue(source, out var entry) && DateTime.UtcNow - entry.At < CacheTtl)
                {
                    value = entry.Value;
                    return true;
                }
            }

            value = RuntimeEnvironment.Empty;
            return false;
        }

        // ==================== 内置环境 ====================

        /// <summary>
        /// 内置环境：<c>%AppData%\AstraBrew Launcher\bin</c>（历史版本用的是 <c>lib</c>，两个都认）。
        /// </summary>
        private static async Task<RuntimeEnvironment> ProbeBuiltinAsync()
        {
            string[] roots = BuiltinRoots;

            string? gitExe = FirstExisting(BuiltinGitCandidates());
            string? nodeExe = FirstExisting(BuiltinNodeCandidates());

            string? caddyExe = FirstExisting(Under(roots, "caddy", "caddy.exe"));

            string? pm2 = ReadPackageVersion(
                Under(roots, "pm2", "node_modules", "pm2", "package.json")
                    .Concat(Under(roots, "nodejs", "node_modules", "pm2", "package.json"))
                    .Concat(Under(roots, "pm2", "package.json"))
                    .Concat(Under(roots, "node_modules", "pm2", "package.json")));

            // npm 跟 Node.js 一起装在同一个目录：<nodejs>\node_modules\npm\package.json。
            // 读 package.json 而不是跑 npm --version：.cmd 得起 cmd.exe，为一个版本号拉子进程不值
            string? npm = ReadPackageVersion(
                Under(roots, "nodejs", "node_modules", "npm", "package.json")
                    .Concat(Under(roots, "node", "node_modules", "npm", "package.json")));

            Task<string?> gitTask = QueryAsync(gitExe, "--version", ParseThreePart);
            Task<string?> nodeTask = QueryAsync(nodeExe, "--version", ParseNode);
            Task<string?> caddyTask = QueryAsync(caddyExe, "version", ParseThreePart);

            await Task.WhenAll(gitTask, nodeTask, caddyTask).ConfigureAwait(false);

            return new RuntimeEnvironment(
                gitTask.Result,
                nodeTask.Result,
                npm,
                caddyTask.Result,
                pm2,
                DetectWebView2());
        }

        // ==================== 系统环境 ====================

        /// <summary>系统环境：PATH 里第一个命中的可执行文件 + npm 全局目录里的 PM2。</summary>
        private static async Task<RuntimeEnvironment> ProbeSystemAsync()
        {
            string? gitExe = FindOnPath("git");
            string? nodeExe = FindOnPath("node");
            string? caddyExe = FindOnPath("caddy");

            Task<string?> gitTask = QueryAsync(gitExe, "--version", ParseThreePart);
            Task<string?> nodeTask = QueryAsync(nodeExe, "--version", ParseNode);
            Task<string?> caddyTask = QueryAsync(caddyExe, "version", ParseThreePart);

            await Task.WhenAll(gitTask, nodeTask, caddyTask).ConfigureAwait(false);

            var pm2Candidates = new List<string>
            {
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "npm", "node_modules", "pm2", "package.json"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "nodejs", "node_modules", "pm2", "package.json"),
            };

            var npmCandidates = new List<string>
            {
                // 官方 msi 装出来就长这样：npm 是 Node.js 目录下的一个普通包
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "nodejs", "node_modules", "npm", "package.json"),
            };

            if (nodeExe is not null && Path.GetDirectoryName(nodeExe) is { Length: > 0 } nodeDir)
            {
                pm2Candidates.Add(Path.Combine(nodeDir, "node_modules", "pm2", "package.json"));
                npmCandidates.Insert(0, Path.Combine(nodeDir, "node_modules", "npm", "package.json"));
            }

            return new RuntimeEnvironment(
                gitTask.Result,
                nodeTask.Result,
                ReadPackageVersion(npmCandidates),
                caddyTask.Result,
                ReadPackageVersion(pm2Candidates),
                DetectWebView2());
        }

        private static string? FindOnPath(string fileName)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

            foreach (string raw in path.Split(Path.PathSeparator))
            {
                string dir = raw.Trim().Trim('"');
                if (dir.Length == 0)
                {
                    continue;
                }

                string candidate = Path.Combine(dir, fileName + ".exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        // ==================== WebView2 ====================

        /// <summary>WebView2 运行时版本：读 EdgeUpdate 登记项，不依赖 Edge 的安装目录在哪。</summary>
        private static string? DetectWebView2()
        {
            (RegistryHive Hive, RegistryView View)[] probes =
            {
                (RegistryHive.LocalMachine, RegistryView.Registry64),
                (RegistryHive.LocalMachine, RegistryView.Registry32),
                (RegistryHive.CurrentUser, RegistryView.Registry64),
            };

            foreach ((RegistryHive hive, RegistryView view) in probes)
            {
                try
                {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using RegistryKey? key = baseKey.OpenSubKey(
                        $@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{WebView2ClientId}");

                    if (key?.GetValue("pv") is string pv && pv.Length > 0 && pv != "0.0.0.0")
                    {
                        return pv;
                    }
                }
                catch
                {
                    // 注册表被策略挡住也只是一项查不到，继续看下一种视图
                }
            }

            return null;
        }

        // ==================== 版本查询 ====================

        private static Task<string?> QueryAsync(string? executable, string arguments, Func<string?, string?> parse)
        {
            if (executable is null)
            {
                return Task.FromResult<string?>(null);
            }

            return Task.Run(() => parse(RunVersionQuery(executable, arguments)));
        }

        private static string? RunVersionQuery(string executable, string arguments)
        {
            try
            {
                var startInfo = new ProcessStartInfo(executable, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                using Process? process = Process.Start(startInfo);
                if (process is null)
                {
                    return null;
                }

                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                _ = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(QueryTimeout))
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // 进程已经自己退了
                    }

                    return null;
                }

                return stdout.GetAwaiter().GetResult();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Git / Caddy：输出形如 <c>git version 2.55.0.windows.3</c>、<c>v2.10.0 h1:…</c>。</summary>
        private static string? ParseThreePart(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            Match match = VersionPattern.Match(raw);
            return match.Success ? match.Value : null;
        }

        /// <summary>Node：输出形如 <c>v22.20.0</c>，保留 v 前缀与工具自己的写法一致。</summary>
        private static string? ParseNode(string? raw)
        {
            string? core = ParseThreePart(raw);
            return core is null ? null : "v" + core;
        }

        /// <summary>PM2 没有稳定可靠的短命令，直接读 npm 包里的 package.json。</summary>
        private static string? ReadPackageVersion(IEnumerable<string> candidates)
        {
            foreach (string path in candidates)
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                    if (document.RootElement.TryGetProperty("version", out JsonElement version)
                        && version.GetString() is { Length: > 0 } text)
                    {
                        return text;
                    }
                }
                catch
                {
                    // 包不完整或 JSON 坏了：当这一处没有，继续下一个候选
                }
            }

            return null;
        }

        // ==================== 路径拼装 ====================

        /// <summary>把「相对各 root 的尾部路径」展开成完整候选路径列表。</summary>
        private static IEnumerable<string> Under(string[] roots, params string[] tail)
        {
            foreach (string root in roots)
            {
                yield return Path.Combine(new[] { root }.Concat(tail).ToArray());
            }
        }

        private static string? FirstExisting(params IEnumerable<string>[] groups)
        {
            foreach (string path in groups.SelectMany(group => group))
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }
    }
}
