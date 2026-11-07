using System;
using System.Text.RegularExpressions;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// git clone 输出的一行进度：阶段 + **折算后的**总体百分比 + 瞬时速度。
    /// </summary>
    public readonly record struct GitProgress(string Phase, int Percent, string? Speed);

    /// <summary>
    /// 解析 <c>git clone --progress</c> 往 stderr 吐的进度行。
    ///
    /// 只有带 <c>--progress</c> 时才会有这些行；stderr 一旦被重定向，git 默认是不输出的。
    /// 两个地方（连通性测试的克隆、下载通道测速）现在都要「真拉一次并给用户看进度」，
    /// 所以把正则收在这里一份——两处各写一份的结果必然是有一边先改、另一边显示不同的数字。
    /// </summary>
    public static class GitProgressParser
    {
        /// <summary>
        /// 一条进度行，形如：
        /// <c>Receiving objects:  45% (1234/2743), 12.34 MiB | 1.20 MiB/s</c>。
        /// 速度那段可能缺席（刚开跑的那几行）。
        /// </summary>
        private static readonly Regex Pattern = new(
            @"(?<phase>Receiving objects|Resolving deltas|Updating files|Counting objects|Compressing objects):\s+(?<percent>\d+)%(?:[^|]*\|\s*(?<speed>[\d.]+\s*[KMGT]?i?B/s))?",
            RegexOptions.Compiled);

        /// <summary>
        /// 把一个阶段内的百分比折算成整次拉取的 0~100：
        /// 下载 3~85、整理 85~95、写入 95~99。界面上要的是一个单调递增的数，
        /// 否则「下载 100%」之后又跳到「整理 20%」，看起来像退回去了。
        /// </summary>
        /// <returns>不是进度行时返回 false（调用方把这一行当错误信息收着）。</returns>
        public static bool TryParse(string? line, out GitProgress progress)
        {
            progress = default;

            Match match = Pattern.Match(line ?? string.Empty);
            if (!match.Success || !int.TryParse(match.Groups["percent"].Value, out int percent))
            {
                return false;
            }

            (string Label, int From, int To) range = match.Groups["phase"].Value switch
            {
                "Counting objects" or "Compressing objects" => ("准备", 0, 3),
                "Receiving objects" => ("下载", 3, 85),
                "Resolving deltas" => ("整理", 85, 95),
                "Updating files" => ("写入", 95, 99),
                _ => ("下载", 3, 85),
            };

            int overall = range.From + (int)Math.Round(percent * (range.To - range.From) / 100.0);
            string? speed = match.Groups["speed"].Success
                ? match.Groups["speed"].Value.Trim()
                : null;

            progress = new GitProgress(range.Label, overall, speed);
            return true;
        }
    }
}
