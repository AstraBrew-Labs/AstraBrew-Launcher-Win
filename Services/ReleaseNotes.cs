using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 把 Release 简介（Markdown）整理成能直接读的纯文本。
    ///
    /// 不引 Markdown 渲染库：一段更新日志不值得为此加依赖，WinUI 里渲染富文本还要自己拼
    /// <c>RichTextBlock</c> 的 Inline，成本高于收益。这里只处理更新日志实际会用到的几种写法：
    /// 标题、无序列表、引用、加粗、行内代码、链接。
    ///
    /// 关于页（启动器自己的更新）与版本页（酒馆版本的发布说明）共用这一份，
    /// **不要各写一套**——两处对同一段 Markdown 的出字不一样，用户会以为是两个东西。
    /// </summary>
    public static class ReleaseNotes
    {
        /// <summary>
        /// 整理成纯文本。没有内容时返回 <paramref name="emptyText"/>（调用方给本地化文案）。
        /// </summary>
        public static string Format(string? notes, string? emptyText = null)
        {
            if (string.IsNullOrWhiteSpace(notes))
            {
                return emptyText ?? string.Empty;
            }

            var lines = new List<string>();

            foreach (string raw in notes.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();

                if (line.StartsWith('#'))
                {
                    // ## 更新内容 → 更新内容（标题在界面上另有排版，正文里不必再留井号）
                    line = line.TrimStart('#').Trim();
                }
                else if (line.StartsWith("* ") || line.StartsWith("- "))
                {
                    line = "• " + line[2..].TrimStart();
                }
                else if (line.StartsWith("> "))
                {
                    line = line[2..].TrimStart();
                }

                line = StripInline(line);

                // 压掉连续空行：发布说明里常有三四个空行，界面上就是一大片空白
                if (line.Length == 0 && lines.Count > 0 && lines[^1].Length == 0)
                {
                    continue;
                }

                lines.Add(line);
            }

            return string.Join(Environment.NewLine, lines).Trim();
        }

        /// <summary>行内标记：链接、加粗、行内代码、分隔线。</summary>
        private static string StripInline(string line)
        {
            if (line is "---" or "***" or "___")
            {
                return string.Empty;
            }

            // [文字](地址) → 文字（地址）：链接本身点不了，但地址得留着，
            // 「完整的更新日志：…」这类内容全靠它
            line = Regex.Replace(line, @"\[([^\]]+)\]\(([^)\s]+)\)", "$1（$2）");

            line = line.Replace("**", string.Empty).Replace("__", string.Empty);
            line = Regex.Replace(line, "`([^`]*)`", "$1");

            return line.Trim();
        }
    }
}
