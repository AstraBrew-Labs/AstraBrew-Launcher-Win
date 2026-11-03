using System;
using System.Collections.Generic;

namespace AstraBrew_Launcher.Services
{
    /// <summary>一次源码获取尝试：名字进日志，地址可直接给 <c>git clone</c> 或压缩包下载。</summary>
    public readonly record struct SourceTarget(string Name, string Url);

    /// <summary>
    /// GitHub 加速服务：全应用唯一的「要不要经中转、怎么拼地址」判断处。
    ///
    /// 规则来自 GH-Proxy 官方文档（gh-proxy.com/docs/github-accelerator）：
    /// · <b>用法只有一种</b>：把完整的 GitHub 资源 URL 原样拼在中转域名后面，路径、查询串都不改。
    ///   例：<c>https://gh-proxy.org/https://github.com/owner/repo/releases/download/v1/x.zip</c>。
    /// · <b>只认 GitHub 自己的域名</b>。中转端本质是「按 github 地址反代」，别的站拼上前缀只会得到
    ///   一个连不上的 URL（镜像、npmmirror、华为云、nodejs.org、npm 源全都如此）。所以这里是
    ///   **主机白名单**，不在名单里的一律原样返回，不做任何推断、不拼前缀。
    /// · <b>只由开关决定，与下载源无关</b>。版本列表接口、Git 安装包、扩展克隆都看开关；
    ///   选的是 Gitee 还是 GitHub 不影响这一点。
    /// · <b>加速地址永远是候选而非唯一</b>：<see cref="Targets"/> 先给加速再给直连，
    ///   中转挂了还能落到直连，不会因为开了开关反而装不上。
    /// · <b>唯一例外是内置实例克隆仓库</b>：那里要下载源明确选了「GitHub 直连」才走加速，
    ///   规则落在 <see cref="TavernSource.InstallTargets"/>，不在这里。
    /// </summary>
    /// <remarks>
    /// 关于两个中转域名：文档里 <c>gh-proxy.org</c> 是资源代理（文件、API、git 协议），
    /// <c>gh-proxy.com</c> 是主站 Web 转换器，只在「用浏览器打开仓库主页 / Releases 页面、
    /// 由它列出全部资产」时才用。本项目只做程序化下载与克隆（API 取版本列表、git clone 取代码、
    /// 直链取安装包），全部属于 .org 的资源代理范围，所以统一用 <see cref="Prefix"/>；
    /// 仓库页解析那条路径没有落地场景，不实现。
    /// </remarks>
    public static class GithubAccel
    {
        /// <summary>加速前缀：把原链接整段拼在它后面。</summary>
        public const string Prefix = "https://gh-proxy.org/";

        /// <summary>中转站主站（Web 转换器）；只用于浏览器打开仓库页，本项目的下载/克隆不走它。</summary>
        public const string WebPrefix = "https://gh-proxy.com/";

        /// <summary>走加速时给用户看的名字。</summary>
        public const string AccelName = "GitHub 加速";

        /// <summary>不经中转时给用户看的名字。</summary>
        public const string DirectName = "GitHub 直连";

        /// <summary>
        /// 已经带过中转前缀的地址（两个中转域名都算）——这些不要再套一层。
        /// 用户可能在扩展地址里直接粘了一条已经加速过的链接。
        /// </summary>
        private static readonly string[] KnownPrefixes = { Prefix, WebPrefix };

        /// <summary>
        /// 唯一允许拼加速前缀的主机名，逐个对应文档「支持的资源格式」清单。
        /// **白名单，不是后缀匹配**——后缀匹配早晚会把某个长得像 GitHub 的域名放进来，
        /// 而这个错误的表现是「用户装不上」。新增一条之前先确认文档里写了它。
        /// </summary>
        private static readonly HashSet<string> AccelerableHosts = new(StringComparer.OrdinalIgnoreCase)
        {
            // ── 主站：仓库主页、Releases、archive、raw 路径、blob、Actions 产物、clone、patch/diff、atom
            "github.com",
            // ── REST API（文档第 9 节，中转端带 Token 轮询池，避免 60 次/小时限流）
            "api.github.com",
            // ── 源码归档实际落点（github.com/…/archive 会 302 到这里）
            "codeload.github.com",
            // ── Raw（第 3.1 节）与历史域名（第 3.2 节，中转端会规范化为 raw.githubusercontent.com）
            "raw.githubusercontent.com",
            "raw.github.com",
            // ── Gist（第 7 节，gist.github.com 同样由中转端规范化）
            "gist.githubusercontent.com",
            "gist.github.com",
            // ── 官方短链（第 8 节，http / https 均可）
            "gh.io",
            // ── 静态资产（第 10 节）：头像、仓库封面、站点资源、GitHub Desktop 安装包
            "avatars.githubusercontent.com",
            "opengraph.githubassets.com",
            "github.githubassets.com",
            "desktop.githubusercontent.com",
            // ── 发布资产的 302 落点：客户端一般拿不到，直连这些地址时也能走中转
            "objects.githubusercontent.com",
            "release-assets.githubusercontent.com",
        };

        /// <summary>会走加速的主机名，供界面说明「到底哪些链接被加速」。</summary>
        public static IReadOnlyCollection<string> Hosts => AccelerableHosts;

        /// <summary>加速开关当前是否打开。</summary>
        public static bool Enabled => LauncherSettings.GithubAcceleration;

        /// <summary>这条链接是不是 GitHub 的（含 API、Raw、Gist、短链与静态资产）。</summary>
        /// <remarks>
        /// 只认 http/https：<c>git@github.com:owner/repo.git</c> 这种 scp 写法没法在中间插域名，
        /// 硬拼只会得到一个连不上的地址，不如老老实实直连。
        /// </remarks>
        public static bool IsGithubUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            string value = url.Trim();

            // 已经加过前缀的不要再套一层
            if (IsAccelerated(value))
            {
                return false;
            }

            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                return false;
            }

            return AccelerableHosts.Contains(uri.Host);
        }

        /// <summary>这条链接是不是已经带了中转前缀（<c>gh-proxy.org</c> 或 <c>gh-proxy.com</c>）。</summary>
        public static bool IsAccelerated(string? url)
        {
            if (url is not { Length: > 0 })
            {
                return false;
            }

            string value = url.Trim();
            foreach (string prefix in KnownPrefixes)
            {
                if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 按当前开关把一条链接换成「实际会连的地址」：**只有白名单里的 GitHub 主机**、
        /// 且开关打开时才加前缀，其余一律原样返回（包括 Gitee / GitCode / npmmirror / 华为云 /
        /// nodejs.org / 本地地址）。传给 <c>HttpClient</c>、<c>git clone</c> 之前都应该过一遍。
        /// </summary>
        /// <remarks>
        /// 只拼前缀、不改路径：<c>/blob/</c> 转 raw、<c>raw.github.com</c> 转 <c>raw.githubusercontent.com</c>
        /// 这类重写由中转端完成（文档第 3 节），我们改路径反而会和它的规范化打架。
        /// </remarks>
        public static string Apply(string url)
        {
            if (!Enabled || !IsGithubUrl(url))
            {
                return url;
            }

            return Prefix + url.Trim();
        }

        /// <summary>
        /// 一条 GitHub 链接的尝试顺序：开关打开时先加速、再直连；关掉就只有直连。
        /// 非 GitHub 链接不参与加速，只有直连一项。
        /// </summary>
        public static IReadOnlyList<SourceTarget> Targets(string url)
        {
            if (!Enabled || !IsGithubUrl(url))
            {
                return new[] { new SourceTarget(DirectName, url) };
            }

            string trimmed = url.Trim();
            return new[]
            {
                new SourceTarget(AccelName, Prefix + trimmed),
                new SourceTarget(DirectName, trimmed),
            };
        }

        /// <summary>
        /// 展开一组下载源：其中的 GitHub 项在开关打开时补一条加速地址放在自己前面，
        /// 直连留在后面兜底。镜像、npmmirror、华为云、nodejs.org 等非 GitHub 项
        /// 原样保留、顺序不变，绝不加前缀。
        /// </summary>
        public static IReadOnlyList<(string Name, string Url)> ExpandSources(
            IEnumerable<(string Name, string Url)> sources)
        {
            var expanded = new List<(string Name, string Url)>();

            foreach ((string name, string url) in sources)
            {
                if (!Enabled || !IsGithubUrl(url))
                {
                    expanded.Add((name, url));
                    continue;
                }

                expanded.Add((AccelName, Prefix + url.Trim()));
                expanded.Add((name, url));
            }

            return expanded;
        }
    }
}
