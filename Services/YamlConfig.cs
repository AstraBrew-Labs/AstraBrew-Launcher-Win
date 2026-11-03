using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// config.yaml 的定点读写：按「点分路径」取一个值、改一个值，**其余行原样不动**。
    ///
    /// 为什么不解析成对象再序列化：官方配置大半是注释说明，重新序列化会全部丢掉，
    /// 用户下次拿记事本打开就只剩一堆裸键值。这里只改命中的那一行，
    /// 注释、分组、缩进风格都保持原样。
    ///
    /// 路径形如 <c>port</c>、<c>listenAddress.ipv4</c>、<c>browserLaunch.enabled</c>；
    /// 数组用 <see cref="ReadList"/> / <see cref="SetList"/>，单独处理（<c>whitelist</c>）。
    /// </summary>
    public static class YamlConfig
    {
        /// <summary>形如 <c>  key: value</c>；列表项 <c>- x</c> 不算键。</summary>
        private static readonly Regex KeyLine = new(
            @"^(?<indent>[ ]*)(?<key>[^:#\s][^:]*?):(?<rest>\s.*|)$",
            RegexOptions.Compiled);

        private readonly record struct Entry(int Index, int Indent, string Path, string Value);

        /// <summary>读出全部标量（含嵌套路径），值已去掉引号与行尾注释。</summary>
        public static Dictionary<string, string> ReadScalars(string text)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (Entry entry in Scan(Split(text)))
            {
                result[entry.Path] = Unquote(entry.Value);
            }

            return result;
        }

        /// <summary>读一个数组（如 <c>whitelist</c>）的全部条目。</summary>
        public static List<string> ReadList(string text, string path)
        {
            string[] lines = Split(text);
            Entry? key = Find(lines, path);
            if (key is null)
            {
                return new List<string>();
            }

            var items = new List<string>();

            for (int index = key.Value.Index + 1; index < lines.Length; index++)
            {
                string trimmed = lines[index].TrimStart();
                if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed == "-")
                {
                    items.Add(Unquote(trimmed[1..].Trim()));
                    continue;
                }

                // 列表结束
                break;
            }

            return items;
        }

        /// <summary>改一个标量；路径不存在时原样返回（不擅自往配置里加东西）。</summary>
        public static string SetScalar(string text, string path, string value)
        {
            string[] lines = Split(text);
            Entry? key = Find(lines, path);
            if (key is null)
            {
                return text;
            }

            string indent = new(' ', key.Value.Indent);
            lines[key.Value.Index] = indent + LastSegment(path) + ": " + Format(value);
            return Join(lines, text);
        }

        /// <summary>整段替换一个数组的条目；原来没有条目就插在键的下一行。</summary>
        public static string SetList(string text, string path, IReadOnlyList<string> items)
        {
            string[] lines = Split(text);
            Entry? key = Find(lines, path);
            if (key is null)
            {
                return text;
            }

            var result = lines.ToList();
            string childIndent = new(' ', key.Value.Indent + 2);

            // 空列表写成行内的 `key: []`：留一个只有键名的孤行，YAML 会读成 null，
            // 而 SillyTavern 对 `hosts`、`origin` 这些字段期待的是数组
            if (items.Count == 0)
            {
                int removeFrom = key.Value.Index + 1;
                while (removeFrom < result.Count)
                {
                    string probe = result[removeFrom].TrimStart();
                    if (probe.StartsWith("- ", StringComparison.Ordinal) || probe == "-")
                    {
                        result.RemoveAt(removeFrom);
                        continue;
                    }

                    break;
                }

                result[key.Value.Index] = new string(' ', key.Value.Indent) + LastSegment(path) + ": []";
                return Join(result.ToArray(), text);
            }

            int first = -1;
            int last = -1;

            for (int index = key.Value.Index + 1; index < result.Count; index++)
            {
                string trimmed = result[index].TrimStart();
                if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed == "-")
                {
                    if (first < 0)
                    {
                        first = index;
                    }

                    last = index;
                    continue;
                }

                break;
            }

            for (int index = last; index >= first && first >= 0; index--)
            {
                result.RemoveAt(index);
            }

            int insertAt = first >= 0 ? first : key.Value.Index + 1;
            result.InsertRange(insertAt, items.Select(item => childIndent + "- " + Format(item)));

            return Join(result.ToArray(), text);
        }

        private static Entry? Find(string[] lines, string path)
        {
            foreach (Entry entry in Scan(lines))
            {
                if (entry.Path == path)
                {
                    return entry;
                }
            }

            return null;
        }

        /// <summary>逐行扫出「键 → 路径」，靠缩进栈还原嵌套关系。</summary>
        private static List<Entry> Scan(string[] lines)
        {
            var entries = new List<Entry>();
            var stack = new List<(int Indent, string Key)>();

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

                string key = match.Groups["key"].Value.Trim();
                string path = stack.Count == 0
                    ? key
                    : string.Join('.', stack.Select(item => item.Key).Append(key));

                entries.Add(new Entry(index, indent, path, match.Groups["rest"].Value.Trim()));
                stack.Add((indent, key));
            }

            return entries;
        }

        private static string LastSegment(string path)
        {
            int dot = path.LastIndexOf('.');
            return dot < 0 ? path : path[(dot + 1)..];
        }

        /// <summary>写进 YAML 的样子：布尔与数字裸写，其它按需加单引号（如 <c>'[::]'</c>）。</summary>
        private static string Format(string value)
        {
            if (value is "true" or "false")
            {
                return value;
            }

            if (long.TryParse(value, out _))
            {
                return value;
            }

            bool plain = value.Length > 0 && value.All(c =>
                char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '/' or '*');

            return plain ? value : "'" + value.Replace("'", "''") + "'";
        }

        private static string Unquote(string value)
        {
            string text = value.Trim();

            if (text.Length >= 2
                && ((text[0] == '\'' && text[^1] == '\'') || (text[0] == '"' && text[^1] == '"')))
            {
                return text[1..^1].Replace("''", "'");
            }

            // 行尾注释：`true # 说明`
            int comment = text.IndexOf(" #", StringComparison.Ordinal);
            return comment > 0 ? text[..comment].Trim() : text;
        }

        private static string[] Split(string text) => text.Replace("\r\n", "\n").Split('\n');

        private static string Join(string[] lines, string original) =>
            string.Join(original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n", lines);
    }
}
