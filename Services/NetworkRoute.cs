using System;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 「这次请求走不走代理、走哪条」的唯一判断处。
    ///
    /// 规则（引导的网络检查、GitHub 连通性、镜像测速共用同一套）：
    /// · 设置里选「不使用代理」→ 不走；
    /// · 选了自定义 → 用那个地址；
    /// · 跟随系统 → 看 Windows 那边到底开没开，没开就是纯直连。
    ///
    /// <see cref="Address"/> 为空但 <see cref="UseProxy"/> 为真，表示「有代理但没有固定地址」
    /// （PAC 脚本 / 自动检测）：HttpClient 能跟着走，git 不能，只能直连。
    /// </summary>
    internal readonly record struct ProxyRoute(bool UseProxy, string? Address)
    {
        public static ProxyRoute Resolve()
        {
            switch (LauncherSettings.Proxy)
            {
                case ProxyModeSetting.None:
                    return new ProxyRoute(false, null);

                case ProxyModeSetting.Custom:
                    return Uri.TryCreate(LauncherSettings.CustomProxy, UriKind.Absolute, out _)
                        ? new ProxyRoute(true, LauncherSettings.CustomProxy)
                        : new ProxyRoute(false, null);

                default:
                    SystemProxyState system = SystemProxy.Read();
                    return system.Enabled
                        ? new ProxyRoute(true, system.EffectiveUrl)
                        : new ProxyRoute(false, null);
            }
        }

        /// <summary>给人看的一句话，弹窗顶部与「当前出口」都用它。</summary>
        public string Describe() => this switch
        {
            { UseProxy: false } => L.Get("Miss.NoProxy"),
            { Address: { Length: > 0 } address } => L.F("Miss.SystemProxy", address),
            _ => L.Get("Miss.SystemProxyAuto"),
        };
    }
}
