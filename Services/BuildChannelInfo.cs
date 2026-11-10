using System;
using System.Linq;
using System.Reflection;

namespace AstraBrew_Launcher.Services
{
    /// <summary>发布渠道。构建配置决定当前是哪一个，运行时不会变。</summary>
    public enum BuildChannel
    {
        /// <summary>正式版：对外发布，关于页不显示任何渠道标记。</summary>
        Release,

        /// <summary>预览版：新功能先上这条线，随时可能坏。</summary>
        Alpha,

        /// <summary>测试版：功能定稿、找 bug 的阶段。</summary>
        Beta,

        /// <summary>开发版：本机开发构建。</summary>
        Debug,
    }

    /// <summary>
    /// 当前构建属于哪个渠道，以及它怎么显示。
    ///
    /// 渠道不靠 <c>#if</c> 判断：配置里的 <c>DefineConstants</c> 由 SDK 的 targets 追加，
    /// 项目体里读到的是空值，拼进去的常量有可能被后来的 <c>DEBUG;TRACE</c> 顶掉，
    /// 而且「没定义」和「定义成 Release」两种情况要分别处理，容易漏。
    /// 这里改读 csproj 里 <c>&lt;AssemblyMetadata Include="BuildChannel" .../&gt;</c> 生成到程序集的元数据：
    /// **没写就是正式版**，四个配置各写各的，一眼能对上是哪个构建。
    /// </summary>
    public static class BuildChannelInfo
    {
        /// <summary>元数据的键名，与 csproj 里的 Include 保持一致。</summary>
        private const string MetadataKey = "BuildChannel";

        /// <summary>当前构建的渠道。</summary>
        public static BuildChannel Channel { get; } = Resolve();

        /// <summary>是不是正式版（只有正式版不显示标记）。</summary>
        public static bool IsStable => Channel == BuildChannel.Release;

        /// <summary>
        /// 关于页头像旁那枚渠道标记。**正式版返回 null**——正式版不该带任何「预览 / 测试」的痕迹。
        /// </summary>
        public static string? Badge => Channel switch
        {
            BuildChannel.Alpha => "预览版",
            BuildChannel.Beta => "测试版",
            BuildChannel.Debug => "开发版",
            _ => null,
        };

        /// <summary>「版本与构建」里「更新通道」那一行 / 设置页版本行上的名字。</summary>
        public static string ChannelName => Channel switch
        {
            BuildChannel.Alpha => "预览通道",
            BuildChannel.Beta => "测试通道",
            BuildChannel.Debug => "开发通道",
            _ => "稳定通道",
        };

        /// <summary>渠道标记的强调色（文字色；底色由调用方按 12% 透明度兑出来）。</summary>
        public static (byte R, byte G, byte B) Accent => Channel switch
        {
            BuildChannel.Alpha => (0xF5, 0x9E, 0x0B),   // 琥珀：预览，看个新鲜
            BuildChannel.Beta => (0xA8, 0x55, 0xF7),    // 紫：测试，功能定了但还在挑错（亮一档，深色标题栏里才看得清）
            BuildChannel.Debug => (0xD1, 0x34, 0x38),   // 红：开发，随时会崩
            _ => (0x16, 0xA3, 0x4A),                    // 绿：正式
        };

        /// <summary>给日志 / 复制信息用的一行字，例如「预览版 · Alpha」。</summary>
        public static string Describe => Badge is { Length: > 0 } badge ? $"{badge}（{Channel}）" : "正式版";

        /// <summary>
        /// 完整版本号，含渠道后缀：<c>0.3.0-Alpha.0</c> / <c>0.3.0-Beta.0</c> / <c>0.3.0-Debug.0</c>，
        /// 正式版就是 <c>0.3.0</c>（没有后缀）。
        ///
        /// 取自 <see cref="AssemblyInformationalVersionAttribute"/>：csproj 里
        /// <c>VersionPrefix</c> + 按配置给的 <c>VersionSuffix</c> 生成，末尾的 <c>+&lt;git sha&gt;</c>
        /// 是 SDK 自动拼的构建来源，界面上不显示，这里截掉。
        /// </summary>
        public static string Version { get; } = ResolveVersion();

        /// <summary>界面上显示用的版本号（带 v 前缀），例如 <c>v0.3.0-Alpha.0</c>。</summary>
        public static string DisplayVersion => "v" + Version;

        private static string ResolveVersion()
        {
            try
            {
                string? informational = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion;

                if (informational is { Length: > 0 })
                {
                    // "0.3.0-Alpha.0+49cb9b4..." → "0.3.0-Alpha.0"
                    int plus = informational.IndexOf('+');
                    string value = plus >= 0 ? informational[..plus] : informational;

                    if (value.Length > 0)
                    {
                        return value;
                    }
                }
            }
            catch (Exception)
            {
                // 读不到就走下面的兜底
            }

            // 兜底：只有数字版本（例如老构建没写 InformationalVersion），后缀按渠道补上
            Version fallback = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 3, 0);
            string core = $"{fallback.Major}.{fallback.Minor}.{fallback.Build}";

            return Badge is { Length: > 0 } ? $"{core}-{Channel}.0" : core;
        }

        private static BuildChannel Resolve()
        {
            try
            {
                string? value = Assembly.GetExecutingAssembly()
                    .GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(item => string.Equals(item.Key, MetadataKey, StringComparison.Ordinal))
                    ?.Value;

                return Enum.TryParse(value, ignoreCase: true, out BuildChannel parsed)
                    ? parsed
                    : BuildChannel.Release;
            }
            catch (Exception)
            {
                // 读不到元数据只可能是构建方式异常：按正式版处理，绝不因此让关于页打不开
                return BuildChannel.Release;
            }
        }
    }
}
