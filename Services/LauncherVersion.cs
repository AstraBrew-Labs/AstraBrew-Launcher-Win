using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 启动器版本号：<c>0.3.0</c> / <c>0.3.0-Debug.0</c> / <c>beta-v0.3.0</c> 都能吃。
    ///
    /// 为什么不直接用 <see cref="Version"/>：它不认字母后缀，而发布 tag 里渠道信息就写在前缀上
    /// （<c>beta-v0.3.0</c>）；也不直接按字符串比：<c>0.10.0</c> 会排到 <c>0.9.0</c> 前面去。
    ///
    /// **迭代号**（<see cref="Iteration"/>）是本项目特有的概念：同一版本里快速修 bug 时，
    /// csproj 的 <c>ChannelIteration</c> +1，版本变成 <c>0.3.0-Beta.1</c>。
    /// 它可能写在第四段（<c>0.3.0.1</c>），也可能写在预发布后缀的末尾（<c>0.3.0-Beta.1</c>），
    /// 两种写法必须解析成同一个值，否则「发一版就永远提示更新」。
    ///
    /// 比较顺序：主 → 次 → 修订 → 迭代号 → 预发布后缀。**渠道词（alpha/beta/debug）不参与比较**，
    /// 比较前用 <see cref="WithoutChannel"/> 剥掉——渠道是独立维度，同渠道内的 0.3.0-Beta.0
    /// 和 tag <c>beta-v0.3.0</c> 指的是同一个东西。
    /// </summary>
    public readonly struct LauncherVersion : IComparable<LauncherVersion>, IEquatable<LauncherVersion>
    {
        /// <summary>抓 a[.b[.c[.d]]] 与紧跟的 -/+ 后缀；前缀里的 beta- / v 由调用方另行处理。</summary>
        private static readonly Regex Pattern = new(
            @"(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?([-+~][0-9A-Za-z][0-9A-Za-z.\-]*)?",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>预发布后缀里的渠道词。比较前会被剥掉。</summary>
        private static readonly Dictionary<ReleaseChannel, string> ChannelWords = new()
        {
            [ReleaseChannel.Alpha] = "alpha",
            [ReleaseChannel.Beta] = "beta",
            [ReleaseChannel.Debug] = "debug",
        };

        public int Major { get; }

        public int Minor { get; }

        public int Patch { get; }

        /// <summary>同一版本下的迭代次数（<c>0.3.0-Beta.1</c> 的 1，或第四段 <c>0.3.0.1</c> 的 1）。</summary>
        public int Iteration { get; }

        /// <summary>预发布后缀里**剩下的字母段**（渠道词与迭代号都已剥离），没有就是 <c>null</c>。</summary>
        public string? Prerelease { get; }

        /// <summary>原始字符串，显示时用它（保留 <c>beta-v0.3.0</c> 这种写法）。</summary>
        public string Raw { get; }

        private LauncherVersion(int major, int minor, int patch, int iteration, string? prerelease, string raw)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
            Iteration = iteration;
            Prerelease = prerelease;
            Raw = raw;
        }

        public static bool TryParse(string? text, out LauncherVersion version)
        {
            version = default;

            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            Match match = Pattern.Match(text);
            if (!match.Success)
            {
                return false;
            }

            int major = int.Parse(match.Groups[1].Value);
            int minor = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;
            int patch = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
            int iteration = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 0;

            if (match.Groups[5].Success)
            {
                // 后缀拆段：纯数字段算迭代号（0.3.0-Beta.1 → 1），其余字母段留在 Prerelease
                var letters = new List<string>();
                foreach (string part in match.Groups[5].Value.TrimStart('-', '+', '~').Split('.'))
                {
                    if (part.Length == 0)
                    {
                        continue;
                    }

                    if (int.TryParse(part, out int number))
                    {
                        // 第四段已经给过迭代号时不再覆盖（0.3.0.1-rc.2 这种极端写法以第四段为准）
                        if (!match.Groups[4].Success)
                        {
                            iteration = number;
                        }
                    }
                    else
                    {
                        letters.Add(part.ToLowerInvariant());
                    }
                }

                string? prerelease = letters.Count == 0 ? null : string.Join('.', letters);
                version = new LauncherVersion(major, minor, patch, iteration, prerelease, text.Trim());
                return true;
            }

            version = new LauncherVersion(major, minor, patch, iteration, null, text.Trim());
            return true;
        }

        /// <summary>解析失败时返回 0.0.0，只用在「总能拿到一个值」的显示场景。</summary>
        public static LauncherVersion ParseOrZero(string? text) =>
            TryParse(text, out LauncherVersion version) ? version : default;

        /// <summary>
        /// 剥掉渠道词与迭代号，只留「版本 + 真正的预发布标记」（如 <c>rc</c>）。
        /// 同渠道内比较前必须调一次：<c>0.3.0-Beta.0</c> 与 tag <c>beta-v0.3.0</c> 是同一个版本。
        /// </summary>
        public LauncherVersion WithoutChannel(ReleaseChannel channel)
        {
            if (Prerelease is not { Length: > 0 } prerelease)
            {
                return this;
            }

            if (!ChannelWords.TryGetValue(channel, out string? word))
            {
                return this;
            }

            string[] kept = prerelease
                .Split('.')
                .Where(part => !string.Equals(part, word, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            string? rest = kept.Length == 0 ? null : string.Join('.', kept);
            return rest == Prerelease
                ? this
                : new LauncherVersion(Major, Minor, Patch, Iteration, rest, Raw);
        }

        public int CompareTo(LauncherVersion other)
        {
            int result = Major.CompareTo(other.Major);
            if (result != 0)
            {
                return result;
            }

            result = Minor.CompareTo(other.Minor);
            if (result != 0)
            {
                return result;
            }

            result = Patch.CompareTo(other.Patch);
            if (result != 0)
            {
                return result;
            }

            result = Iteration.CompareTo(other.Iteration);
            if (result != 0)
            {
                return result;
            }

            // 主次修与迭代号都相同：没有预发布标记的更大（0.3.0 > 0.3.0-rc）
            bool selfPre = Prerelease is { Length: > 0 };
            bool otherPre = other.Prerelease is { Length: > 0 };

            if (!selfPre && !otherPre)
            {
                return 0;
            }

            if (!selfPre)
            {
                return 1;
            }

            if (!otherPre)
            {
                return -1;
            }

            return string.Compare(Prerelease, other.Prerelease, StringComparison.OrdinalIgnoreCase);
        }

        public bool Equals(LauncherVersion other) => CompareTo(other) == 0;

        public override bool Equals(object? obj) => obj is LauncherVersion other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Iteration, Prerelease);

        public static bool operator >(LauncherVersion left, LauncherVersion right) => left.CompareTo(right) > 0;

        public static bool operator <(LauncherVersion left, LauncherVersion right) => left.CompareTo(right) < 0;

        public override string ToString() => Raw.Length > 0 ? Raw : $"{Major}.{Minor}.{Patch}";
    }
}
