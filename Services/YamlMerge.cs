using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AstraBrew_Launcher.Services
{
    /// <summary>一次合并的结果：合并后的全文 + 补进去的新项数量。</summary>
    public sealed record YamlMergeResult(string Text, int AddedKeys);

    /// <summary>
    /// 两份 config.yaml 的合并：**以当前配置为基座，只把导入配置里「当前没有的项」补进来**。
    ///
    /// 规则：
    /// <list type="bullet">
    /// <item>当前有的键，一律保留当前的值——导入只是补缺，不是改设置；</item>
    /// <item>导入多出来的顶层段，整段追加到文件末尾；</item>
    /// <item>导入多出来的子键，插到对应父段的末尾，缩进对齐父段现有的子键；</item>
    /// <item>类型对不上（当前是标量 `key: value`、导入是块，或反过来）就保留当前，宁可少合也不产出坏 YAML。</item>
    /// </list>
    ///
    /// 为什么不解析成对象再序列化：官方 config.yaml 里大半是注释说明，重新序列化会把它们全丢掉，
    /// 用户拿到的就不再是一份能读的配置了。这里只做「整块搬运」，注释与顺序原样保留。
    /// </summary>
    public static class YamlMerge
    {
        /// <summary>形如 <c>  key: value</c>；列表项 <c>- x</c> 不算键。</summary>
        private static readonly Regex KeyLine = new(
            @"^(?<indent>[ ]*)(?<key>[^:#\s][^:]*?):(?<rest>\s.*|)$",
            RegexOptions.Compiled);

        /// <summary>一个键（连同它下面的缩进子树）。</summary>
        private sealed class Block
        {
            public required string Key { get; init; }

            public required int Indent { get; init; }

            public required int Start { get; init; }

            /// <summary>键后面直接跟了值（<c>port: 8000</c>），而不是一段子树。</summary>
            public required bool IsScalar { get; init; }

            /// <summary>块结束位置（不含）：下一个同级或更浅的键所在行。</summary>
            public int End { get; set; }

            public List<Block> Children { get; } = new();
        }

        public static YamlMergeResult Merge(string current, string imported)
        {
            string newline = current.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            string[] currentLines = Split(current);
            string[] importedLines = Split(imported);

            List<Block> currentTop = Parse(currentLines);
            List<Block> importedTop = Parse(importedLines);

            var insertions = new List<(int After, List<string> Lines)>();
            int added = 0;

            foreach (Block importedBlock in importedTop)
            {
                Block? existing = currentTop.FirstOrDefault(block => block.Key == importedBlock.Key);

                if (existing is null)
                {
                    // 整个顶层段都是新的：追加到文件末尾，前面留一个空行分段
                    List<string> lines = Slice(importedLines, importedBlock, 0);
                    if (currentLines.Length > 0 && currentLines[^1].Trim().Length > 0)
                    {
                        lines.Insert(0, string.Empty);
                    }

                    insertions.Add((currentLines.Length - 1, lines));
                    added++;
                    continue;
                }

                Collect(currentLines, existing, importedBlock, importedLines, insertions, ref added);
            }

            if (insertions.Count == 0)
            {
                return new YamlMergeResult(current, 0);
            }

            var merged = new List<string>(currentLines);

            // 从后往前插：先动靠后的插入点，前面的行号才不会失效
            foreach (var group in insertions.GroupBy(item => item.After).OrderByDescending(group => group.Key))
            {
                merged.InsertRange(group.Key + 1, group.SelectMany(item => item.Lines));
            }

            return new YamlMergeResult(string.Join(newline, merged), added);
        }

        /// <summary>在当前块里补导入块多出来的子键；同名的继续往下比。</summary>
        private static void Collect(
            string[] currentLines,
            Block existing,
            Block imported,
            string[] importedLines,
            List<(int After, List<string> Lines)> insertions,
            ref int added)
        {
            // 类型不一致时保留当前：往标量底下塞子键会写出坏 YAML
            if (existing.IsScalar || imported.IsScalar)
            {
                return;
            }

            int childIndent = existing.Children.Count > 0 ? existing.Children[0].Indent : existing.Indent + 2;

            foreach (Block importedChild in imported.Children)
            {
                Block? match = existing.Children.FirstOrDefault(child => child.Key == importedChild.Key);

                if (match is null)
                {
                    List<string> lines = Slice(importedLines, importedChild, childIndent - importedChild.Indent);
                    insertions.Add((InsertAfter(existing, currentLines), lines));
                    added++;
                    continue;
                }

                Collect(currentLines, match, importedChild, importedLines, insertions, ref added);
            }
        }

        /// <summary>
        /// 新子键插在哪：段内最后一个**内容行**之后，而不是段尾。
        /// 段尾往往是一串给下一个段做说明的注释（比如 logging 后面那行
        /// 「-- RATE LIMITING CONFIGURATION --」），插在它后面会让人以为注释在解释新键。
        /// </summary>
        private static int InsertAfter(Block block, string[] lines)
        {
            for (int index = block.End - 1; index > block.Start; index--)
            {
                string trimmed = lines[index].Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                return index;
            }

            return block.Start;
        }

        /// <summary>按缩进解析出顶层块与嵌套子树。</summary>
        private static List<Block> Parse(string[] lines)
        {
            var top = new List<Block>();
            var stack = new List<Block>();

            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index];
                if (line.Length == 0)
                {
                    continue;
                }

                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("#", StringComparison.Ordinal)
                    || trimmed.StartsWith("- ", StringComparison.Ordinal)
                    || trimmed == "-")
                {
                    // 注释与列表项不是键，属于所在块的内容
                    continue;
                }

                Match match = KeyLine.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                int indent = match.Groups["indent"].Length;
                while (stack.Count > 0 && stack[^1].Indent >= indent)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                string rest = match.Groups["rest"].Value.Trim();
                var block = new Block
                {
                    Key = match.Groups["key"].Value.Trim(),
                    Indent = indent,
                    Start = index,
                    IsScalar = rest.Length > 0 && !rest.StartsWith("#", StringComparison.Ordinal),
                };

                if (stack.Count > 0)
                {
                    stack[^1].Children.Add(block);
                }
                else
                {
                    top.Add(block);
                }

                stack.Add(block);
            }

            AssignEnds(top, lines.Length);
            return top;
        }

        /// <summary>块的结束位置：下一个同级（或更浅）的键，没有就是文件尾。</summary>
        private static void AssignEnds(List<Block> top, int total)
        {
            for (int index = 0; index < top.Count; index++)
            {
                top[index].End = index + 1 < top.Count ? top[index + 1].Start : total;
            }

            foreach (Block block in top)
            {
                AssignChildEnds(block);
            }
        }

        private static void AssignChildEnds(Block parent)
        {
            for (int index = 0; index < parent.Children.Count; index++)
            {
                Block child = parent.Children[index];
                child.End = index + 1 < parent.Children.Count ? parent.Children[index + 1].Start : parent.End;
                AssignChildEnds(child);
            }
        }

        private static List<string> Slice(string[] lines, Block block, int indentDelta)
        {
            var result = new List<string>(Math.Max(1, block.End - block.Start));

            for (int index = block.Start; index < block.End; index++)
            {
                result.Add(indentDelta == 0 ? lines[index] : Reindent(lines[index], indentDelta));
            }

            return result;
        }

        /// <summary>按目标父级的缩进平移整块；负数时最多删到行首。</summary>
        private static string Reindent(string line, int delta)
        {
            if (line.Length == 0)
            {
                return line;
            }

            if (delta > 0)
            {
                return new string(' ', delta) + line;
            }

            return line[Math.Min(-delta, LeadingSpaces(line))..];
        }

        private static int LeadingSpaces(string line)
        {
            int count = 0;
            while (count < line.Length && line[count] == ' ')
            {
                count++;
            }

            return count;
        }

        private static string[] Split(string text) => text.Replace("\r\n", "\n").Split('\n');
    }
}
