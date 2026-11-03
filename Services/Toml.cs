using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AstraBrew_Launcher.Services
{
    /// <summary>TOML 值的种类，只覆盖设置文件用得到的几种。</summary>
    internal enum TomlKind
    {
        String,
        Bool,
        Number,
        DateTime,
    }

    /// <summary>一条已解析的键值对；<see cref="Text"/> 是去掉引号与转义后的内容。</summary>
    internal readonly struct TomlEntry
    {
        public TomlEntry(TomlKind kind, string text)
        {
            Kind = kind;
            Text = text;
        }

        public TomlKind Kind { get; }

        public string Text { get; }
    }

    /// <summary>
    /// 极简 TOML 读取器：只认「<c>[section]</c> + <c>key = value</c>」这一层扁平结构。
    ///
    /// 为什么不用 Tomlyn 之类的完整实现：
    ///   1. 设置文件的 schema 完全由本程序掌握，没有数组、表数组、内联表；
    ///   2. Release 构建开了 <c>PublishTrimmed</c>，反射式反序列化在裁剪后会静默失效；
    ///   3. 自己写才能在写回时保留分组注释 —— 通用库会把注释全部丢掉。
    ///
    /// 因此这里只实现设置文件用得到的子集：注释、节、基本字符串（含转义）、字面量字符串、
    /// 布尔、数字、日期时间。读不懂的行直接跳过，保证「手改坏了一个键」不会让启动失败。
    /// </summary>
    internal sealed class TomlDocument
    {
        /// <summary>节名 → 键 → 值；根层（第一个 <c>[section]</c> 之前）用空串作节名。</summary>
        private readonly Dictionary<string, Dictionary<string, TomlEntry>> _sections =
            new(StringComparer.OrdinalIgnoreCase);

        public static TomlDocument Parse(string? text)
        {
            var document = new TomlDocument();
            if (string.IsNullOrWhiteSpace(text))
            {
                return document;
            }

            var section = string.Empty;
            foreach (var rawLine in text.TrimStart('\uFEFF').Split('\n'))
            {
                var line = rawLine.TrimEnd('\r').Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                if (line[0] == '[')
                {
                    // 表数组 [[x]] 用不到，同样按节处理即可，多余的值会被忽略
                    var close = line.IndexOf(']');
                    if (close > 1)
                    {
                        section = line.Substring(1, close - 1).Trim('[', ' ', '\t');
                    }

                    continue;
                }

                var separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, separator).Trim().Trim('"', '\'');
                if (key.Length == 0)
                {
                    continue;
                }

                if (ParseValue(line.Substring(separator + 1)) is not { } entry)
                {
                    continue;
                }

                if (!document._sections.TryGetValue(section, out var bucket))
                {
                    bucket = new Dictionary<string, TomlEntry>(StringComparer.OrdinalIgnoreCase);
                    document._sections[section] = bucket;
                }

                bucket[key] = entry;
            }

            return document;
        }

        public string? String(string section, string key) =>
            TryGet(section, key, TomlKind.String, out var entry) ? entry.Text : null;

        public bool? Bool(string section, string key) =>
            TryGet(section, key, TomlKind.Bool, out var entry)
                ? string.Equals(entry.Text, "true", StringComparison.OrdinalIgnoreCase)
                : null;

        public double? Number(string section, string key) =>
            TryGet(section, key, TomlKind.Number, out var entry)
                && double.TryParse(entry.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : null;

        public int? Int(string section, string key) =>
            Number(section, key) is { } value ? (int)Math.Round(value) : null;

        public DateTimeOffset? Date(string section, string key)
        {
            if (!TryGet(section, key, TomlKind.DateTime, out var entry))
            {
                return null;
            }

            return DateTimeOffset.TryParse(
                entry.Text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var value)
                ? value
                : null;
        }

        private bool TryGet(string section, string key, TomlKind kind, out TomlEntry entry)
        {
            entry = default;

            if (!_sections.TryGetValue(section, out var bucket) || !bucket.TryGetValue(key, out var found))
            {
                return false;
            }

            // 类型不符时当作没读到：宁可回落到默认值，也不要拿字符串去冒充数字
            if (found.Kind != kind)
            {
                return false;
            }

            entry = found;
            return true;
        }

        private static TomlEntry? ParseValue(string raw)
        {
            var text = raw.TrimStart();
            if (text.Length == 0)
            {
                return null;
            }

            if (text[0] == '"')
            {
                return TryReadBasicString(text, out var quoted) ? new TomlEntry(TomlKind.String, quoted) : null;
            }

            if (text[0] == '\'')
            {
                return TryReadLiteralString(text, out var literal) ? new TomlEntry(TomlKind.String, literal) : null;
            }

            var token = StripComment(text).Trim();
            if (token.Length == 0)
            {
                return null;
            }

            if (token is "true" or "false")
            {
                return new TomlEntry(TomlKind.Bool, token);
            }

            var numeric = token.Replace("_", string.Empty, StringComparison.Ordinal);
            if (double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                return new TomlEntry(TomlKind.Number, numeric);
            }

            return new TomlEntry(TomlKind.DateTime, token);
        }

        /// <summary>基本字符串：支持 TOML 的转义序列，未知转义按整行作废。</summary>
        private static bool TryReadBasicString(string text, out string value)
        {
            var builder = new StringBuilder(text.Length);
            value = string.Empty;

            for (var i = 1; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '"')
                {
                    value = builder.ToString();
                    return true;
                }

                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }

                if (++i >= text.Length)
                {
                    return false;
                }

                switch (text[i])
                {
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 't':
                        builder.Append('\t');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 'b':
                        builder.Append('\b');
                        break;
                    case 'f':
                        builder.Append('\f');
                        break;
                    case '"':
                        builder.Append('"');
                        break;
                    case '\\':
                        builder.Append('\\');
                        break;
                    case 'u':
                        if (!TryReadHex(text, i + 1, 4, out var shortCode))
                        {
                            return false;
                        }

                        builder.Append((char)shortCode);
                        i += 4;
                        break;
                    case 'U':
                        if (!TryReadHex(text, i + 1, 8, out var longCode))
                        {
                            return false;
                        }

                        builder.Append(char.ConvertFromUtf32((int)longCode));
                        i += 8;
                        break;
                    default:
                        return false;
                }
            }

            return false;
        }

        /// <summary>字面量字符串：单引号内不做任何转义（Windows 路径手写起来更省事）。</summary>
        private static bool TryReadLiteralString(string text, out string value)
        {
            var close = text.IndexOf('\'', 1);
            value = close > 0 ? text.Substring(1, close - 1) : string.Empty;
            return close > 0;
        }

        private static bool TryReadHex(string text, int start, int length, out long value)
        {
            value = 0;
            if (start + length > text.Length)
            {
                return false;
            }

            return long.TryParse(
                text.Substring(start, length),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out value);
        }

        private static string StripComment(string text)
        {
            var quote = '\0';
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (c is '"' or '\'')
                {
                    quote = c;
                    continue;
                }

                if (c == '#')
                {
                    return text.Substring(0, i);
                }
            }

            return text;
        }
    }

    /// <summary>
    /// TOML 写入器：只负责把键值对排成「等宽键 + 值 + 行尾注释」的形态。
    ///
    /// 键宽度固定，注释列随之对齐，手写编辑时一眼能看出值和注释的分界。
    /// </summary>
    internal sealed class TomlWriter
    {
        private const int KeyWidth = 20;
        private const int ValueWidth = 24;

        private readonly StringBuilder _builder = new(2048);

        public TomlWriter Raw(string line)
        {
            _builder.Append(line).Append('\n');
            return this;
        }

        public TomlWriter Blank()
        {
            _builder.Append('\n');
            return this;
        }

        public TomlWriter Section(string name)
        {
            _builder.Append('\n').Append('[').Append(name).Append(']').Append('\n');
            return this;
        }

        public TomlWriter String(string key, string value, string? comment = null) =>
            Pair(key, Quote(value), comment);

        public TomlWriter Bool(string key, bool value, string? comment = null) =>
            Pair(key, value ? "true" : "false", comment);

        public TomlWriter Int(string key, long value, string? comment = null) =>
            Pair(key, value.ToString(CultureInfo.InvariantCulture), comment);

        public TomlWriter Double(string key, double value, string? comment = null) =>
            Pair(key, value.ToString("0.00", CultureInfo.InvariantCulture), comment);

        public override string ToString() => _builder.ToString();

        private TomlWriter Pair(string key, string value, string? comment)
        {
            _builder.Append(key.PadRight(KeyWidth)).Append(" = ").Append(value);

            // 注释列对齐：值短就补齐，值长（路径、URL）就紧跟其后，不硬撑
            if (!string.IsNullOrEmpty(comment))
            {
                _builder.Append(new string(' ', Math.Max(2, ValueWidth - value.Length)))
                    .Append("# ")
                    .Append(comment);
            }

            _builder.Append('\n');
            return this;
        }

        /// <summary>写入基本字符串：反斜杠与引号必须转义，否则读回来是另一个值。</summary>
        private static string Quote(string value)
        {
            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');

            foreach (var c in value)
            {
                switch (c)
                {
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (char.IsControl(c))
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            builder.Append('"');
            return builder.ToString();
        }
    }
}
