using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 公网出口 IP。
    ///
    /// 服务器模式选「互联网」时，用户真正要填进端口映射、告诉别人怎么连的是**公网地址**；
    /// 本机网卡上的 192.168.x.x 在公网没有任何意义。这里问一下外面看到的是谁。
    ///
    /// 两个端点分开问、各自失败互不影响：家用宽带大多只有 IPv4，硬等 IPv6 超时只会让弹窗卡住。
    /// 只回**能拿到且看起来是公网**的地址——代理或运营商 NAT 有时会返回内网地址。
    /// </summary>
    internal static class PublicIp
    {
        private const string V4Endpoint = "https://api-ipv4.ip.sb/ip";

        private const string V6Endpoint = "https://api-ipv6.ip.sb/ip";

        /// <summary>单次请求上限：超过这个时间宁可当作「取不到」，也不让弹窗干等。</summary>
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

        /// <summary>拿到结果就缓存 5 分钟：反复开关弹窗不该每次都打接口。</summary>
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

        /// <summary>全部失败只压 30 秒：网络刚起来时用户会再点一次，别让人等满 5 分钟。</summary>
        private static readonly TimeSpan FailureLifetime = TimeSpan.FromSeconds(30);

        private static readonly object Gate = new();

        private static (string? V4, string? V6) _cached;

        private static DateTime _cachedAt = DateTime.MinValue;

        /// <summary>取公网 IPv4 / IPv6；取不到的为空。</summary>
        public static async Task<(string? V4, string? V6)> ResolveAsync()
        {
            lock (Gate)
            {
                TimeSpan age = DateTime.UtcNow - _cachedAt;
                TimeSpan lifetime = _cached.V4 is null && _cached.V6 is null ? FailureLifetime : CacheLifetime;

                if (age < lifetime)
                {
                    return _cached;
                }
            }

            Task<string?> v4 = QueryAsync(V4Endpoint);
            Task<string?> v6 = QueryAsync(V6Endpoint);

            await Task.WhenAll(v4, v6).ConfigureAwait(false);

            var result = (V4: v4.Result, V6: v6.Result);

            lock (Gate)
            {
                _cached = result;
                _cachedAt = DateTime.UtcNow;
            }

            return result;
        }

        private static async Task<string?> QueryAsync(string endpoint)
        {
            try
            {
                // 和下载走同一套代理策略：设置里固定了代理的用户，这里也得走同一个出口
                using var client = ProxyHttpClient.Create(RequestTimeout);

                string text = await client.GetStringAsync(endpoint).ConfigureAwait(false);
                string candidate = text.Trim().Trim('"').Trim();

                return IPAddress.TryParse(candidate, out IPAddress? address) && IsPublic(address)
                    ? candidate
                    : null;
            }
            catch (Exception)
            {
                // 超时、DNS 失败、没 IPv6 出口、接口抽风——都只是「这个版本拿不到」
                return null;
            }
        }

        /// <summary>排掉内网 / 回环 / 链路本地地址：这些拿到手机上也连不进来。</summary>
        private static bool IsPublic(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            if (IPAddress.IsLoopback(address)
                || address.Equals(IPAddress.Any)
                || address.Equals(IPAddress.IPv6Any))
            {
                return false;
            }

            byte[] bytes = address.GetAddressBytes();

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                return bytes[0] switch
                {
                    10 => false,                                     // 10.0.0.0/8
                    127 => false,                                    // 回环
                    0 => false,
                    169 when bytes[1] == 254 => false,               // 链路本地
                    172 when bytes[1] is >= 16 and <= 31 => false,   // 172.16.0.0/12
                    192 when bytes[1] == 168 => false,               // 192.168.0.0/16
                    100 when bytes[1] is >= 64 and <= 127 => false,  // 运营商级 NAT
                    _ => true,
                };
            }

            // IPv6：fe80::/10（链路本地）、fc00::/7（唯一本地）
            return !((bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
                || (bytes[0] & 0xFE) == 0xFC);
        }
    }
}
