using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>一次「获取默认配置」的结果。</summary>
    public sealed record GlobalConfigFetchResult(bool Success, string Path, string? Source, string? Error)
    {
        /// <summary>这份配置是联网取来的（加速或直连），还是本地底稿直接恢复的。</summary>
        public bool Downloaded => Source is GithubAccel.AccelName or GithubAccel.DirectName;
    }

    /// <summary>
    /// 全局酒馆配置的落点与首次获取。
    ///
    /// **生效与操作的只有全局那一份**：<c>data/sillytavern/config.yaml</c>。
    /// 读取、修改、覆盖、删除都针对它，不存在「拿默认路径当配置用」这种状态。
    ///
    /// 默认位置 <c>data/default/sillytavern/config.yaml</c> 只是**恢复默认用的底稿**：
    /// 全局缺失时先拿它恢复一份到全局（<see cref="RestoreFromDefault"/>），
    /// 底稿也没有时才联网取一份——取回先落默认位置、做几项开箱优化，再复制成全局配置。
    /// 此后用户改的都是全局那份，默认位置始终保持一份干净的底稿。
    /// </summary>
    public static class GlobalTavernConfig
    {
        /// <summary>启动器给用户的端口：不与 SillyTavern 官方默认 8000 撞车。</summary>
        public const int Port = 11451;

        /// <summary>官方默认配置（release 分支）。</summary>
        public const string SourceUrl =
            "https://raw.githubusercontent.com/SillyTavern/SillyTavern/refs/heads/release/default/config.yaml";

        /// <summary>全局配置：唯一生效、唯一被修改 / 覆盖 / 删除的那份。</summary>
        public static string GlobalPath { get; } =
            Path.Combine(AppPaths.RoamingRoot, "data", "sillytavern", "config.yaml");

        /// <summary>默认配置：恢复默认用的底稿，不作为生效配置使用。</summary>
        public static string DefaultPath { get; } =
            Path.Combine(AppPaths.RoamingRoot, "data", "default", "sillytavern", "config.yaml");

        /// <summary>操作配置时永远用这个路径，不要因为全局缺失就退到默认路径。</summary>
        public static string ActivePath => GlobalPath;

        /// <summary>全局配置是否存在（决定界面要不要出「先取一份」的遮罩）。</summary>
        public static bool HasGlobalConfig => File.Exists(GlobalPath);

        /// <summary>默认底稿是否存在。</summary>
        public static bool HasDefaultConfig => File.Exists(DefaultPath);

        /// <summary>
        /// 全局缺失时拿默认底稿恢复一份到全局；返回是否真的做了恢复。
        /// 底稿不存在或全局已存在都直接返回 false，不覆盖用户已有的配置。
        /// </summary>
        public static bool RestoreFromDefault()
        {
            if (HasGlobalConfig || !HasDefaultConfig)
            {
                return false;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(GlobalPath)!);
                File.Copy(DefaultPath, GlobalPath, overwrite: false);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 恢复不了就当没有底稿：让界面走「在线获取」那条路，而不是卡在这里
                return false;
            }
        }

        /// <summary>
        /// 下载默认配置 → 优化 → 写默认位置 → 复制成全局配置。
        /// 下载顺序由 <see cref="GithubAccel.Targets"/> 决定：开着加速就先加速、失败回退直连，
        /// 关着加速就只有直连，与设置页那个开关联动。
        /// </summary>
        public static async Task<GlobalConfigFetchResult> FetchAsync(CancellationToken token)
        {
            var errors = new List<string>();

            foreach (SourceTarget target in GithubAccel.Targets(SourceUrl))
            {
                try
                {
                    string yaml = await DownloadAsync(target.Url, token).ConfigureAwait(false);
                    string tuned = ApplyLaunchDefaults(yaml);

                    WriteAtomic(DefaultPath, tuned);

                    Directory.CreateDirectory(Path.GetDirectoryName(GlobalPath)!);
                    File.Copy(DefaultPath, GlobalPath, overwrite: true);

                    return new GlobalConfigFetchResult(true, GlobalPath, target.Name, null);
                }
                catch (Exception ex) when (ex is HttpRequestException
                    or InvalidDataException
                    or IOException
                    or UnauthorizedAccessException
                    or TaskCanceledException)
                {
                    // 加速挂了还有直连，直连挂了就把两个原因一起告诉用户
                    errors.Add($"{target.Name}：{ex.Message}");
                }
            }

            return new GlobalConfigFetchResult(false, GlobalPath, null, string.Join("；", errors));
        }

        /// <summary>
        /// 「恢复默认配置」：优先拿本地底稿覆盖全局，底稿不在就直接联网取一份。
        /// 与 <see cref="RestoreFromDefault"/> 的区别是**强制覆盖**——
        /// 用户要的就是把当前改动抹掉、回到默认，所以不存在「保留现有配置」这回事。
        /// </summary>
        public static async Task<GlobalConfigFetchResult> ResetAsync(CancellationToken token)
        {
            if (HasDefaultConfig)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(GlobalPath)!);
                    File.Copy(DefaultPath, GlobalPath, overwrite: true);
                    return new GlobalConfigFetchResult(true, GlobalPath, "默认配置", null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 本地覆盖不动（文件被占用等）就退到联网取一份，别卡死在这条路上
                }
            }

            return await FetchAsync(token).ConfigureAwait(false);
        }

        /// <summary>读出全局配置的全文。</summary>
        public static string ReadGlobal() => File.ReadAllText(GlobalPath, System.Text.Encoding.UTF8);

        /// <summary>写回全局配置（原子写，保留调用方给的整份内容）。</summary>
        public static void SaveGlobal(string yaml) => WriteAtomic(GlobalPath, yaml);

        /// <summary>看起来像不像一份酒馆配置（防止用户误选了别的 YAML）。</summary>
        public static bool LooksLikeConfig(string text) =>
            text.Length > 200
            && text.Contains("whitelistMode", StringComparison.Ordinal)
            && text.Contains("port:", StringComparison.Ordinal);

        /// <summary>用导入的内容整体覆盖全局配置（覆盖语义，不碰默认底稿）。</summary>
        public static void OverwriteGlobal(string yaml) => WriteAtomic(GlobalPath, yaml);

        /// <summary>
        /// 把导入配置里「当前没有的项」补进全局配置；当前已有的值一律保留。
        /// 没有全局配置时抛 <see cref="InvalidOperationException"/>——这时候该先取一份默认配置。
        /// </summary>
        public static YamlMergeResult MergeIntoGlobal(string imported)
        {
            if (!HasGlobalConfig)
            {
                throw new InvalidOperationException("还没有全局配置，请先点「获取默认配置」。");
            }

            string current = File.ReadAllText(GlobalPath, System.Text.Encoding.UTF8);
            YamlMergeResult result = YamlMerge.Merge(current, imported);

            if (result.AddedKeys > 0)
            {
                WriteAtomic(GlobalPath, result.Text);
            }

            return result;
        }

        /// <summary>取回文本并确认它真的是一份酒馆配置，而不是代理塞回来的错误页。</summary>
        private static async Task<string> DownloadAsync(string url, CancellationToken token)
        {
            using HttpClient client = ProxyHttpClient.Create(TimeSpan.FromSeconds(45));
            using HttpResponseMessage response = await client.GetAsync(url, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            string text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (text.Length < 500 || text.Length > 1024 * 1024
                || !text.Contains("whitelistMode", StringComparison.Ordinal)
                || !text.Contains("port:", StringComparison.Ordinal))
            {
                throw new InvalidDataException("下载到的内容不是 SillyTavern 的配置文件。");
            }

            return text;
        }

        /// <summary>
        /// 对官方默认配置做开箱优化：端口 <see cref="Port"/>、允许局域网访问（<c>listen</c>）、
        /// 打开 IPv6（<c>protocol.ipv6</c>）、白名单只留本机（<c>127.0.0.1</c> 与 <c>localhost</c>）。
        ///
        /// 逐行改而不是整份重写：注释、分组、其它选项一律原样保留，
        /// 用户拿到的仍是官方那份配置，只是这四项已经调好了。
        /// </summary>
        public static string ApplyLaunchDefaults(string yaml)
        {
            bool crlf = yaml.Contains("\r\n", StringComparison.Ordinal);
            string[] lines = yaml.Replace("\r\n", "\n").Split('\n');
            var output = new List<string>(lines.Length + 2);

            string? section = null;
            bool portDone = false;
            bool whitelistWritten = false;

            foreach (string line in lines)
            {
                // 顶格的 key 才算段首：browserLaunch.port、whitelistMode 这些都不能误伤
                bool topLevel = line.Length > 0 && !char.IsWhiteSpace(line[0]);

                if (topLevel)
                {
                    section = line.StartsWith("protocol:", StringComparison.Ordinal) ? "protocol"
                        : line.StartsWith("whitelist:", StringComparison.Ordinal) ? "whitelist"
                        : null;

                    if (line.StartsWith("port:", StringComparison.Ordinal))
                    {
                        output.Add("port: " + Port);
                        portDone = true;
                        continue;
                    }

                    if (line.StartsWith("listen:", StringComparison.Ordinal))
                    {
                        output.Add("listen: true");
                        continue;
                    }
                }

                if (section == "protocol")
                {
                    string trimmed = line.TrimStart();
                    if (trimmed.StartsWith("ipv6:", StringComparison.Ordinal))
                    {
                        output.Add(line[..(line.Length - trimmed.Length)] + "ipv6: true");
                        continue;
                    }
                }

                if (section == "whitelist" && line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
                {
                    if (!whitelistWritten)
                    {
                        output.Add("  - 127.0.0.1");
                        output.Add("  - localhost");
                        whitelistWritten = true;
                    }

                    // 官方默认的 ::1 等条目不再保留：白名单只留本机这两项
                    continue;
                }

                output.Add(line);
            }

            if (!portDone)
            {
                throw new InvalidDataException("默认配置里找不到 port 字段，官方配置结构可能变了。");
            }

            return string.Join(crlf ? "\r\n" : "\n", output);
        }

        /// <summary>先写临时文件再换名：写一半断掉不会留下半份 YAML。</summary>
        private static void WriteAtomic(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, content, new System.Text.UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
    }
}
