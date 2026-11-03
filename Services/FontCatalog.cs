using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;

namespace AstraBrew_Launcher.Services
{
    /// <summary>字体配置最终落到哪一类来源上。</summary>
    internal enum FontChoiceKind
    {
        /// <summary>跟随 Windows（框架自带的系统 UI 字体）。</summary>
        System,

        /// <summary>随包分发的内置字体，不依赖本机安装。</summary>
        Embedded,

        /// <summary>本机安装的某个字体族。</summary>
        SystemFamily,
    }

    /// <summary>配置值解析后的结论。</summary>
    internal readonly struct FontChoice
    {
        public FontChoiceKind Kind { get; init; }

        /// <summary>仅 <see cref="FontChoiceKind.SystemFamily"/> 时有效。</summary>
        public string? Family { get; init; }

        /// <summary>配置里原本写的字体族在本机找不到，已回退。</summary>
        public string? Missing { get; init; }
    }

    /// <summary>
    /// 本机字体族的枚举、筛选与「配置值 ↔ 真实字体族」的换算。
    ///
    /// 为什么用 GDI 的 <c>EnumFontFamiliesEx</c> 而不是翻字体目录：
    /// 目录里的文件名（msyh.ttc、SourceHanSansSC-Regular.otf）和 XAML 要的字体族名
    /// （Microsoft YaHei UI、Source Han Sans SC）不是一回事，靠文件名猜会错。
    /// GDI 回调里拿到的 <c>lfFaceName</c> 就是 FontFamily 能直接用的那个名字。
    ///
    /// 筛选规则（「符合」的定义）：
    ///   - 只要 TrueType / OpenType 轮廓字体，点阵字体（Terminal、Fixedsys 之类）排除；
    ///   - 排除 SYMBOL_CHARSET 与 @ 开头的竖排副本；
    ///   - 排除图标 / 符号字体（Segoe MDL2 Assets、Wingdings 等）——选它会让整个界面变符号；
    ///   - 同名去重（同一族会在不同 charset 上回调多次）。
    ///
    /// 排序：能显示中文的排在前面，其余按名称。中文用户在一百多个字体里找中文太费劲。
    ///
    /// 另有一份随包分发的内置字体（<see cref="EmbeddedFamily"/>），作为软件默认：
    /// 用户没装字体也有一致的观感，这是「软件默认」与「系统默认」的区别。
    /// </summary>
    internal static class FontCatalog
    {
        /// <summary>配置文件里表示「不覆盖，用框架自带的系统 UI 字体」的取值。</summary>
        public const string SystemId = "system";

        /// <summary>配置文件里表示「用随包分发的内置字体」的取值，也是出厂默认。</summary>
        public const string EmbeddedId = "embedded";

        /// <summary>
        /// 内置字体的族名，必须和 ttf 的 <c>name</c> 表逐字一致（这里用的是繁体字形，
        /// 不是文件名里的简体）。对不上时 XAML 不报错，只是静默回退到系统字体——
        /// 改动前请先用 name 表核对。
        /// </summary>
        public const string EmbeddedFamily = "有爱魔兽圆体 CN";

        /// <summary>
        /// 同一款字体在繁体语言下登记的族名。ttf 的 name 表里 zh-TW / zh-HK 走这条，
        /// 简体系统按 <see cref="EmbeddedFamily"/> 匹配，两个都要认。
        /// </summary>
        private const string EmbeddedFamilyTraditional = "有愛魔獸圓體 CN";

        /// <summary>界面上的名字（用户可读）。</summary>
        public const string EmbeddedLabel = "软件默认";

        /// <summary>内置字体文件相对输出目录的位置。</summary>
        private const string EmbeddedRelativePath = @"Assets\Fonts\Nowar-Warcraft-Rounded-CN-Regular.ttf";

        /// <summary>旧版配置里的「软件默认」写法，读到一并映射。</summary>
        private static readonly string[] LegacyEmbeddedIds = { "app", "default", "embedded-font" };

        /// <summary>旧版配置里的方案 id，读到时映射到具体字体族（迁移用）。</summary>
        private static readonly Dictionary<string, string[]> LegacyIds = new(StringComparer.OrdinalIgnoreCase)
        {
            ["rounded"] = new[]
            {
                "Source Han Sans SC", "Source Han Sans CN", "Noto Sans SC", "Noto Sans CJK SC", "Microsoft YaHei UI",
            },
            ["mono"] = new[] { "Cascadia Mono", "Cascadia Code", "Consolas" },
        };

        /// <summary>判定「能显示中文」的族名特征（GDI 返回的是字体内部英文名）。</summary>
        private static readonly string[] CjkMarkers =
        {
            "yahei", "jhenghei", "simsun", "nsimsun", "simhei", "dengxian", "fangsong", "kaiti", "stkaiti",
            "stxihei", "stheiti", "ms gothic", "yu gothic", "yugothic", "yu mincho", "yumincho", "meiryo",
            "malgun", "batang", "gulim", "mingliu", "pmingliu", "source han", "noto sans sc", "noto serif sc",
            "noto sans cjk", "noto serif cjk", "sarasa", "lxgw", "harmonyos", "misans", "alibaba", "songti",
            "heiti", "yuanti",
        };

        /// <summary>图标 / 符号字体：选中会让界面全是方框和符号。</summary>
        private static readonly string[] ExcludedMarkers =
        {
            "mdl2", "fluent icons", "segoe ui emoji", "segoe ui symbol", "segoe ui historic", "webdings",
            "wingdings", "marlett", "lastresort", "bookshelf symbol", "ms reference specialty",
        };

        private static readonly Lazy<string[]> LazyFamilies = new(Enumerate, isThreadSafe: true);

        /// <summary>内置字体是否随包就位（发布时漏了 Assets\Fonts 就会是 false）。</summary>
        public static bool IsEmbeddedAvailable { get; } = File.Exists(EmbeddedPath);

        private static string EmbeddedPath => Path.Combine(AppContext.BaseDirectory, EmbeddedRelativePath);

        /// <summary>全部可用字体族，已排序。首次访问时枚举一次并缓存。</summary>
        public static IReadOnlyList<string> Families => LazyFamilies.Value;

        // ==================== 内置字体 ====================

        /// <summary>
        /// 取内置字体的 FontFamily。
        ///
        /// 走 ms-appx 而不是本机字体名：本机字体名只有用户装过才有，
        /// 而这里要的是「没装也能用」。URI 末尾 <c>#</c> 后面跟的是 ttf 里的族名，
        /// 不是文件名。
        /// </summary>
        public static FontFamily CreateEmbeddedFont() =>
            new($"ms-appx:///Assets/Fonts/{Path.GetFileName(EmbeddedRelativePath)}#{EmbeddedFamily}");

        /// <summary>该字体族是不是内置字体那一套（本机装没装都算）。</summary>
        public static bool IsEmbeddedFamily(string? family)
        {
            if (string.IsNullOrWhiteSpace(family))
            {
                return false;
            }

            var name = family.Trim();

            // 只认主族名。Light / Medium 在字体里是独立的族名，
            // 把它们也标成「软件默认」会让下拉里出现三条同名注释，反而更迷惑。
            return name.Equals(EmbeddedFamily, StringComparison.OrdinalIgnoreCase)
                || name.Equals(EmbeddedFamilyTraditional, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>该配置值是不是「软件默认」。</summary>
        public static bool IsEmbeddedId(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var trimmed = value.Trim();
            if (trimmed.Equals(EmbeddedId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (var legacy in LegacyEmbeddedIds)
            {
                if (trimmed.Equals(legacy, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        // ==================== 换算 ====================

        /// <summary>该字体族在本机是否存在（忽略大小写）。</summary>
        public static bool IsInstalled(string? family)
        {
            if (string.IsNullOrWhiteSpace(family))
            {
                return false;
            }

            var name = family.Trim();
            foreach (var installed in Families)
            {
                if (string.Equals(installed, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 配置文件里的原始值 → 保存到内存/写回文件用的规范值。
        ///
        /// 空值与 <c>system</c> 都归一成 null（跟随系统），<c>embedded</c> 归一成
        /// <see cref="EmbeddedId"/>，其余保留字体族名——哪怕本机没装也要留着，
        /// 否则用户临时卸载字体再装回来，设置就丢了。
        /// </summary>
        public static string? FromStorage(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                // 没写过这一项：出厂就是软件默认
                return EmbeddedId;
            }

            var value = raw.Trim();
            if (value.Equals(SystemId, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (IsEmbeddedId(value))
            {
                return EmbeddedId;
            }

            // 旧配置的 rounded / mono 映射到具体字体族
            if (LegacyIds.TryGetValue(value, out var candidates))
            {
                foreach (var candidate in candidates)
                {
                    if (IsInstalled(candidate))
                    {
                        return candidate;
                    }
                }

                return EmbeddedId;
            }

            return value;
        }

        /// <summary>写入配置用的值。</summary>
        public static string ToStorage(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return SystemId;
            }

            return IsEmbeddedId(value) ? EmbeddedId : value.Trim();
        }

        /// <summary>
        /// 决定实际该用哪套字体。
        ///
        /// 回退顺序：配置的字体没装 → 内置字体 → 系统默认。
        /// 内置字体是「一定能用」的那一档，所以排在系统默认前面：
        /// 出厂默认就是它，回退到它至少和用户预期的观感一致。
        /// </summary>
        public static FontChoice Resolve(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                // 明确选了「跟随系统」
                return new FontChoice { Kind = FontChoiceKind.System };
            }

            var trimmed = value.Trim();
            if (IsEmbeddedId(trimmed))
            {
                return IsEmbeddedAvailable
                    ? new FontChoice { Kind = FontChoiceKind.Embedded }
                    : new FontChoice { Kind = FontChoiceKind.System };
            }

            if (trimmed.Equals(SystemId, StringComparison.OrdinalIgnoreCase))
            {
                return new FontChoice { Kind = FontChoiceKind.System };
            }

            if (IsInstalled(trimmed))
            {
                return new FontChoice { Kind = FontChoiceKind.SystemFamily, Family = trimmed };
            }

            return new FontChoice
            {
                Kind = IsEmbeddedAvailable ? FontChoiceKind.Embedded : FontChoiceKind.System,
                Missing = trimmed,
            };
        }

        // ==================== 枚举 ====================

        private const byte DefaultCharset = 1;
        private const byte SymbolCharset = 2;
        private const int TrueTypeFontType = 0x4;
        private const int FaceNameSize = 32;

        private static string[] Enumerate()
        {
            var families = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var hdc = GetDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero)
            {
                return Array.Empty<string>();
            }

            try
            {
                var logFont = new LogFont { lfCharSet = DefaultCharset, lfFaceName = string.Empty };

                EnumFontProc callback = (elfe, _, fontType, _) =>
                {
                    if ((fontType & TrueTypeFontType) == 0)
                    {
                        return 1;
                    }

                    var face = Marshal.PtrToStructure<LogFont>(elfe);
                    var name = face.lfFaceName?.Trim();

                    if (string.IsNullOrEmpty(name)
                        || name[0] == '@'
                        || face.lfCharSet == SymbolCharset
                        || IsExcluded(name)
                        || !seen.Add(name))
                    {
                        return 1;
                    }

                    families.Add(name);
                    return 1;
                };

                EnumFontFamiliesEx(hdc, ref logFont, callback, IntPtr.Zero, 0);
                GC.KeepAlive(callback);
            }
            catch (Exception)
            {
                // 枚举失败就当没有可选字体：下拉框只剩「系统默认」，不影响启动
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, hdc);
            }

            families.Sort(CompareFamilies);
            return families.ToArray();
        }

        private static int CompareFamilies(string left, string right)
        {
            var leftCjk = SupportsChinese(left);
            var rightCjk = SupportsChinese(right);

            if (leftCjk != rightCjk)
            {
                return leftCjk ? -1 : 1;
            }

            return StringComparer.CurrentCultureIgnoreCase.Compare(left, right);
        }

        private static bool SupportsChinese(string name)
        {
            foreach (var marker in CjkMarkers)
            {
                if (name.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // 少数中文字体的族名本身就是中文（如「方正兰亭黑」）
            foreach (var ch in name)
            {
                if (ch >= '\u4E00' && ch <= '\u9FFF')
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsExcluded(string name)
        {
            foreach (var marker in ExcludedMarkers)
            {
                if (name.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct LogFont
        {
            public int lfHeight;
            public int lfWidth;
            public int lfEscapement;
            public int lfOrientation;
            public int lfWeight;
            public byte lfItalic;
            public byte lfUnderline;
            public byte lfStrikeOut;
            public byte lfCharSet;
            public byte lfOutPrecision;
            public byte lfClipPrecision;
            public byte lfQuality;
            public byte lfPitchAndFamily;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = FaceNameSize)]
            public string lfFaceName;
        }

        private delegate int EnumFontProc(IntPtr logFont, IntPtr textMetric, uint fontType, IntPtr param);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumFontFamiliesExW")]
        private static extern int EnumFontFamiliesEx(
            IntPtr hdc,
            ref LogFont logFont,
            EnumFontProc callback,
            IntPtr param,
            uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    }
}
