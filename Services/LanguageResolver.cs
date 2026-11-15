using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Windows.Globalization;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 把「跟随系统 / 简体中文 / English」解析成一个具体的语言标签。
    ///
    /// 本项目只发两种语言，所以这里只返回 <c>zh-CN</c> 或 <c>en-US</c>：
    /// 中文系统给中文，其余一律英文。**没有第三种可能，也不做「跟随系统但系统是日语」的降级猜谜**——
    /// MRT 的默认回退会给中文，那等于让英文用户看到中文，比猜错更糟。
    /// </summary>
    internal static class LanguageResolver
    {
        public const string Chinese = "zh-CN";

        public const string English = "en-US";

        /// <summary>按设置里的选项解析出实际使用的标签。</summary>
        public static string Resolve(AppLanguage choice) => choice switch
        {
            AppLanguage.SimplifiedChinese => Chinese,
            AppLanguage.English => English,
            _ => Normalize(ProbeSystemTag()),
        };

        /// <summary>把任意语言标签收敛到本项目的两种。</summary>
        public static string Normalize(string? tag) =>
            tag is { Length: > 0 } && tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? Chinese
                : English;

        /// <summary>
        /// 系统当前的语言标签。
        ///
        /// 用 WinAppSDK 的 <see cref="ApplicationLanguages"/> 而不是 <c>Windows.Globalization.GlobalizationPreferences</c>：
        /// 后者在无包身份（本项目全是 unpackaged）下会抛，而且它返回的是「区域首选项」，
        /// 与 MRT 实际解析用的语言不是一回事。
        /// </summary>
        private static string ProbeSystemTag()
        {
            // ① 最可靠：Windows 的「显示语言」。不受我们自己的 PrimaryLanguageOverride 影响，
            //    也不受 PRI 里声明了哪些语言影响——「跟随系统」就该跟用户看到的系统语言一致。
            try
            {
                ushort langId = GetUserDefaultUILanguage();
                if (langId != 0)
                {
                    string name = new CultureInfo(langId).Name;
                    if (name.Length > 0)
                    {
                        return name;
                    }
                }
            }
            catch (Exception)
            {
                // 取不到就往下退
            }

            // ② .NET 的 UI 语言（线程启动时继承自系统）
            string ui = CultureInfo.CurrentUICulture.Name;
            if (ui.Length > 0)
            {
                return ui;
            }

            // ③ 最后才问 MRT：它返回的是「资源里声明过 + 用户偏好」的交集，
            //    如果 PRI 里只声明了一种语言，这里会给出那个语言，未必是系统语言。
            try
            {
                var languages = ApplicationLanguages.Languages;
                if (languages.Count > 0 && languages[0] is { Length: > 0 } first)
                {
                    return first;
                }
            }
            catch (Exception)
            {
                // 无包身份或被系统策略限制
            }

            return English;
        }

        [DllImport("kernel32.dll")]
        private static extern ushort GetUserDefaultUILanguage();
    }
}
