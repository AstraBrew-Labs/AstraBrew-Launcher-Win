using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AstraBrew_Launcher.Services
{
    /// <summary>发布渠道。与 <see cref="BuildChannel"/> 一一对应，用来「只收自己这条线的更新」。</summary>
    public enum ReleaseChannel
    {
        Release,
        Alpha,
        Beta,
        Debug,
    }

    /// <summary>发布里的一个资产（安装包 / 免安装包 / 校验文件）。</summary>
    public sealed record UpdateAsset(string Name, string Url, long Size)
    {
        /// <summary>给人看的大小，例如「123.5 MB」。</summary>
        public string DisplaySize => Size <= 0
            ? string.Empty
            : Size >= 1024L * 1024L
                ? $"{Size / 1024d / 1024d:0.0} MB"
                : $"{Size / 1024d:0} KB";
    }

    /// <summary>
    /// 一条发布记录。GitHub 与 GitCode 的字段名不完全一样（<c>browser_download_url</c> / <c>url</c>），
    /// 这里统一成一份，上层不用管是从哪个源拿到的。
    /// </summary>
    public sealed class LauncherRelease
    {
        public required string Tag { get; init; }

        public required string Title { get; init; }

        public required LauncherVersion Version { get; init; }

        public required ReleaseChannel Channel { get; init; }

        public string Notes { get; init; } = string.Empty;

        public DateTimeOffset? PublishedAt { get; init; }

        public bool Prerelease { get; init; }

        public IReadOnlyList<UpdateAsset> Assets { get; init; } = Array.Empty<UpdateAsset>();

        /// <summary>显示用的版本号：优先用 tag 里那段（保留 <c>beta-v0.0.5</c> 的原貌）。</summary>
        public string DisplayVersion => Version.ToString();

        /// <summary>
        /// 解析一条 release。两个源的字段基本同名，差异只在下载地址：
        /// GitHub 是 <c>browser_download_url</c>，GitCode 有时给 <c>url</c>，两个都认。
        /// 解析不出可用的版本号就返回 null——宁可不提示更新，也不要提示一个假版本。
        /// </summary>
        public static LauncherRelease? Parse(JsonElement element)
        {
            string tag = ReadString(element, "tag_name") ?? ReadString(element, "tag") ?? string.Empty;
            string title = ReadString(element, "name") ?? tag;

            if (!LauncherVersion.TryParse(tag, out LauncherVersion version) &&
                !LauncherVersion.TryParse(title, out version))
            {
                return null;
            }

            var assets = new List<UpdateAsset>();
            if (element.TryGetProperty("assets", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in list.EnumerateArray())
                {
                    // GitCode 会在 assets 里塞四个源码归档（type = source：本 tag 的 zip / tar.gz 快照），
                    // 挑包时绝不能选中它们——那是源码，不是可执行的发布包。
                    string? assetType = ReadString(item, "type");
                    if (string.Equals(assetType, "source", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string name = ReadString(item, "name") ?? string.Empty;
                    string url = ReadString(item, "browser_download_url")
                        ?? ReadString(item, "download_url")
                        ?? ReadString(item, "url")
                        ?? string.Empty;

                    if (name.Length == 0 || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    long size = 0;
                    if (item.TryGetProperty("size", out JsonElement sizeElement) &&
                        sizeElement.ValueKind == JsonValueKind.Number)
                    {
                        sizeElement.TryGetInt64(out size);
                    }

                    assets.Add(new UpdateAsset(name, url, size));
                }
            }

            DateTimeOffset? published = null;
            string? publishedText = ReadString(element, "published_at") ?? ReadString(element, "created_at");
            if (publishedText is { Length: > 0 } &&
                DateTimeOffset.TryParse(publishedText, out DateTimeOffset parsed))
            {
                published = parsed;
            }

            bool prerelease = element.TryGetProperty("prerelease", out JsonElement pre) &&
                pre.ValueKind == JsonValueKind.True;

            return new LauncherRelease
            {
                Tag = tag,
                Title = title,
                Version = version,
                Channel = DetectChannel(tag, title),
                Notes = ReadString(element, "body") ?? string.Empty,
                PublishedAt = published,
                Prerelease = prerelease,
                Assets = assets,
            };
        }

        /// <summary>
        /// 从 tag / 标题里认渠道。两种命名都要认（实测仓库里两种都出现过）：
        /// <list type="bullet">
        ///   <item><c>[type]-[版本号]</c>：<c>Beta-v0.0.0</c>、<c>beta-v0.3.0</c>；</item>
        ///   <item><c>v[版本号]-[type]</c>：<c>v0.0.0-Beta.0</c>、<c>v0.3.0-Beta.0</c>。</item>
        /// </list>
        ///
        /// 做法是按非字母字符切词再比关键字，而不是看开头/结尾——版本号里不可能出现 alpha/beta，
        /// 所以扫全串是安全的，也顺手覆盖了 <c>stable-v1.2.3</c> 这类写法。
        /// 一个渠道词都没有就是正式发布（稳定版）。
        /// </summary>
        public static ReleaseChannel DetectChannel(string tag, string title)
        {
            ReleaseChannel fromTag = ChannelFromText(tag);
            return fromTag != ReleaseChannel.Release ? fromTag : ChannelFromText(title);
        }

        private static ReleaseChannel ChannelFromText(string text)
        {
            foreach (string raw in text.Split(ChannelSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                // v0 / v1 这种前缀只影响版本号，不影响渠道词
                string token = raw.Length > 1 && (raw[0] is 'v' or 'V') && char.IsDigit(raw[1])
                    ? raw[1..]
                    : raw;

                switch (token.ToLowerInvariant())
                {
                    case "alpha":
                    case "preview":
                        return ReleaseChannel.Alpha;

                    case "beta":
                        return ReleaseChannel.Beta;

                    case "debug":
                        return ReleaseChannel.Debug;
                }
            }

            return ReleaseChannel.Release;
        }

        private static readonly char[] ChannelSeparators = { '-', '_', '.', '+', '~', ' ', '/', '\\' };

        /// <summary>
        /// 挑免安装包（zip），自动更新就用它。命名规范变过两轮，所以分三档退化：
        /// 新规范 <c>AstraBrewLauncher-0.3.0-win-x64.zip</c> → 老规范 <c>…portable.zip</c> → 任意 zip。
        /// 同一档里有多个时优先含当前架构（<c>win-x64</c>）的那个。
        /// </summary>
        public UpdateAsset? PickPortableZip()
        {
            string rid = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "x86",
                _ => "x64",
            };

            IEnumerable<UpdateAsset> zips = Assets.Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

            UpdateAsset? Pick(Func<UpdateAsset, bool> filter)
            {
                List<UpdateAsset> pool = zips.Where(filter).ToList();
                if (pool.Count == 0)
                {
                    return null;
                }

                return pool.FirstOrDefault(a => a.Name.Contains(rid, StringComparison.OrdinalIgnoreCase))
                    ?? pool[0];
            }

            return Pick(a => a.Name.StartsWith("AstraBrewLauncher-", StringComparison.OrdinalIgnoreCase))
                ?? Pick(a => a.Name.Contains("launcher", StringComparison.OrdinalIgnoreCase))
                ?? Pick(_ => true);
        }

        /// <summary>挑安装包（setup.exe 优先，其次 msi）。当前装的是 MSI 版时用它做升级。</summary>
        public UpdateAsset? PickInstaller()
        {
            return Assets.FirstOrDefault(a =>
                    a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                    a.Name.Contains("setup", StringComparison.OrdinalIgnoreCase))
                ?? Assets.FirstOrDefault(a => a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>挑校验文件（<c>SHA256SUMS.txt</c>）。没有就不校验，但不影响安装。</summary>
        public UpdateAsset? FindChecksums()
        {
            return Assets.FirstOrDefault(a =>
                a.Name.Contains("SHA256", StringComparison.OrdinalIgnoreCase) &&
                !a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }

        private static string? ReadString(JsonElement element, string name)
        {
            return element.TryGetProperty(name, out JsonElement value) &&
                value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        /// <summary>
        /// 从一整个 release 列表里挑「当前渠道、版本最高」的一条。
        /// 跨渠道的一律不选：Debug 版收到 beta 包会把渠道标记改掉，那种「更新」不如不要。
        /// </summary>
        public static LauncherRelease? PickLatest(IEnumerable<LauncherRelease> releases, ReleaseChannel channel)
        {
            return releases
                .Where(r => r.Channel == channel)
                .OrderByDescending(r => r.Version)
                .ThenByDescending(r => r.PublishedAt ?? DateTimeOffset.MinValue)
                .FirstOrDefault();
        }
    }
}
