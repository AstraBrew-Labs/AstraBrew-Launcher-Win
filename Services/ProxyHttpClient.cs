using System;
using System.Net;
using System.Net.Http;
using System.Threading;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 按设置页的「代理方式」创建 <see cref="HttpClient"/>。
    ///
    /// 安装器与版本列表共用这一处，免得两边的代理规则慢慢长歪：
    /// 「无」不走代理、「系统」跟随 Windows Internet 选项、「自定义」用填写的地址
    /// （地址解析失败时退回系统代理，而不是硬连一个连不上的值）。
    /// </summary>
    internal static class ProxyHttpClient
    {
        /// <summary>
        /// 超时默认交给调用方控制（无限），只有明确需要整体超时的短请求才传 <paramref name="timeout"/>。
        /// </summary>
        public static HttpClient Create(TimeSpan? timeout = null)
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
            };

            switch (LauncherSettings.Proxy)
            {
                case ProxyModeSetting.None:
                    handler.UseProxy = false;
                    break;
                case ProxyModeSetting.Custom when Uri.TryCreate(
                    LauncherSettings.CustomProxy,
                    UriKind.Absolute,
                    out Uri? proxy):
                    handler.UseProxy = true;
                    handler.Proxy = new WebProxy(proxy);
                    break;
                default:
                    // 跟随系统 Internet 选项（HttpClientHandler 的默认行为）
                    handler.UseProxy = true;
                    break;
            }

            var client = new HttpClient(handler)
            {
                Timeout = timeout ?? Timeout.InfiniteTimeSpan,
            };

            client.DefaultRequestHeaders.UserAgent.ParseAdd("AstraBrew-Launcher/1.0");
            return client;
        }
    }
}
