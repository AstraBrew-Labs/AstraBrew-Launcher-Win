using System;
using System.Collections.Generic;
using System.Text;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 交给 Node 的「模块说明符」与继承来的 <c>NODE_OPTIONS</c> 的预处理。
    ///
    /// 存在的理由只有一个坑：Node 的 ESM 加载器只认 <c>file:</c> / <c>data:</c> / <c>node:</c>
    /// 协议。把 Windows 绝对路径（<c>C:\…</c>）直接交给 <c>--import</c> / <c>--loader</c>，
    /// Node 会把盘符当成协议名，进程立刻以退出码 1 结束：
    ///
    /// <code>
    /// Error [ERR_UNSUPPORTED_ESM_URL_SCHEME]: Only URLs with a scheme in: file, data, and node
    /// are supported by the default ESM loader. On Windows, absolute paths must be valid file://
    /// URLs. Received protocol 'c:'
    /// </code>
    ///
    /// 旧版本因此让用户「一启动就退出」。这里把路径统一转成 <c>file://</c> URL，
    /// 并在启动时顺手修正从父进程继承来的 <c>NODE_OPTIONS</c>。
    /// </summary>
    public static class NodeEnvironment
    {
        /// <summary>会把参数当作模块说明符、走 ESM 解析的开关。</summary>
        private static readonly string[] ModuleFlags = { "--import", "--loader", "--experimental-loader" };

        /// <summary>
        /// 把文件路径转成 Node 认的 <c>file://</c> URL。
        ///
        /// 规则照旧版实现的 <c>to_file_url</c>：反斜杠换正斜杠、去掉 <c>\\?\</c> 前缀、
        /// 盘符前补一个斜杠，再把非 URL 安全字符按字节转义——中文目录名不转义会拼出非法 URL。
        /// </summary>
        public static string ToFileUrl(string path)
        {
            string text = path.Replace('\\', '/');

            if (text.StartsWith("//?/", StringComparison.Ordinal))
            {
                text = text[4..];
            }
            else if (text.StartsWith("//./", StringComparison.Ordinal))
            {
                text = text[4..];
            }

            var url = new StringBuilder(text.Length + 8);
            url.Append("file://");

            if (!text.StartsWith('/'))
            {
                url.Append('/');
            }

            foreach (byte b in Encoding.UTF8.GetBytes(text))
            {
                bool safe = (b >= 'a' && b <= 'z')
                    || (b >= 'A' && b <= 'Z')
                    || (b >= '0' && b <= '9')
                    || b is (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~' or (byte)'/' or (byte)':';

                if (safe)
                {
                    url.Append((char)b);
                }
                else
                {
                    url.Append('%').Append(b.ToString("X2"));
                }
            }

            return url.ToString();
        }

        /// <summary>是不是一个「裸的」Windows 绝对路径（盘符开头或 UNC），不含协议前缀。</summary>
        public static bool IsAbsolutePath(string token)
        {
            if (token.Length >= 3
                && char.IsAsciiLetter(token[0])
                && token[1] == ':'
                && (token[2] == '\\' || token[2] == '/'))
            {
                return true;
            }

            return token.StartsWith(@"\\", StringComparison.Ordinal);
        }

        /// <summary>
        /// 修正 <c>NODE_OPTIONS</c> 里被当成模块说明符的裸路径。
        ///
        /// 返回修正后的值与一句人话说明（没改动时说明为 null）。拿不准的一律原样保留：
        /// 这不是我们的变量，删错了会连带毁掉用户自己装的工具链。
        /// </summary>
        public static (string? Value, string? Note) SanitizeNodeOptions(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return (raw, null);
            }

            List<string> tokens = Tokenize(raw);
            bool changed = false;

            for (int index = 0; index < tokens.Count; index++)
            {
                string token = tokens[index];

                // --import=C:\x.mjs 这种等号写法
                int equals = token.IndexOf('=');
                if (equals > 0)
                {
                    string flag = token[..equals];
                    string value = token[(equals + 1)..];

                    if (IsModuleFlag(flag) && IsAbsolutePath(value))
                    {
                        tokens[index] = flag + "=" + ToFileUrl(value);
                        changed = true;
                    }

                    continue;
                }

                if (!IsModuleFlag(token) || index + 1 >= tokens.Count)
                {
                    continue;
                }

                string next = tokens[index + 1];

                if (IsAbsolutePath(next))
                {
                    tokens[index + 1] = ToFileUrl(next);
                    changed = true;
                }
            }

            return changed
                ? (string.Join(' ', tokens),
                    "NODE_OPTIONS 传给 Node 的模块路径是文件路径，Node 的 ESM 加载器只认 file: 协议"
                    + "（否则报 ERR_UNSUPPORTED_ESM_URL_SCHEME，进程直接退出），启动前已改写成 file:// URL。")
                : (raw, null);
        }

        private static bool IsModuleFlag(string token)
        {
            foreach (string flag in ModuleFlags)
            {
                if (string.Equals(token, flag, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>按空格切分，双引号内的空格不切；引号本身保留在引号包裹的参数里，重建时再补。</summary>
        private static List<string> Tokenize(string raw)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            bool quoted = false;

            foreach (char character in raw)
            {
                if (character == '"')
                {
                    quoted = !quoted;
                    continue;
                }

                if (!quoted && char.IsWhiteSpace(character))
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }

                    continue;
                }

                current.Append(character);
            }

            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
            }

            // 含空格的参数（比如带空格的路径）重建时补回引号，否则会被拆成两个参数
            for (int index = 0; index < tokens.Count; index++)
            {
                if (tokens[index].Contains(' '))
                {
                    tokens[index] = "\"" + tokens[index] + "\"";
                }
            }

            return tokens;
        }
    }
}
