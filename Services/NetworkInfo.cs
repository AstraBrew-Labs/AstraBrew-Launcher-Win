using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 本机网络地址。仪表盘的主卡、控制台的「设备访问」、酒馆配置的访问说明都从这里取——
    /// 以前各处各写一份示例 IP，用户复制出去根本连不上。
    ///
    /// 只取「已连接网卡上的全局单播地址」：回环不算、<c>169.254.x.x</c> 不算
    /// （那是没拿到 DHCP 时的自分配地址）、隧道网卡不算。
    ///
    /// <b>结果带缓存</b>：一次枚举在 Windows 上要 100 ms 出头，而仪表盘切换服务范围时会连着
    /// 刷两次地址——不缓存就会在点击后卡住半秒。缓存过期后先返回旧值，再在后台线程重新枚举，
    /// 界面线程不再为拿地址停下。
    /// </summary>
    public static class NetworkInfo
    {
        /// <summary>多久重新枚举一次网卡。地址只在换网络时才会变，没必要每次读都枚举。</summary>
        private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(30);

        private static readonly object Gate = new();

        private static (string? V4, string? V6, string? Subnet) _value;
        private static bool _hasValue;
        private static bool _refreshing;
        private static DateTime _readAt;

        /// <summary>本机的 IPv4 / IPv6 局域网地址；没有对应协议时为 null。</summary>
        public static (string? V4, string? V6) LocalAddresses()
        {
            (string? v4, string? v6, _) = Snapshot();
            return (v4, v6);
        }

        /// <summary>
        /// 本机所在网段，形如 <c>192.168.8.0/24</c>（按网卡上的前缀长度算，拿不到就按 /24）。
        /// 白名单要放行「整个局域网」时用它——只放行单个 IP 的话，同一 Wi-Fi 下换台设备就连不上。
        /// </summary>
        public static string? LocalSubnet()
        {
            (_, _, string? subnet) = Snapshot();
            return subnet;
        }

        private static (string? V4, string? V6, string? Subnet) Snapshot()
        {
            lock (Gate)
            {
                // 第一次必须同步算出来：调用方要据它拼地址，不能给空
                if (!_hasValue)
                {
                    _value = Read();
                    _hasValue = true;
                    _readAt = DateTime.UtcNow;
                    return _value;
                }

                if (DateTime.UtcNow - _readAt < Fresh)
                {
                    return _value;
                }

                // 过了保鲜期：这次先给旧值，后台重新枚举一遍，下次读就是新的
                if (!_refreshing)
                {
                    _refreshing = true;
                    _ = Task.Run(Refresh);
                }

                return _value;
            }
        }

        private static void Refresh()
        {
            try
            {
                (string? v4, string? v6, string? subnet) = Read();

                lock (Gate)
                {
                    _value = (v4, v6, subnet);
                    _hasValue = true;
                    _readAt = DateTime.UtcNow;
                }
            }
            catch
            {
                // 读不到就继续用旧值，下次再试
            }
            finally
            {
                lock (Gate)
                {
                    _refreshing = false;
                }
            }
        }

        /// <summary>形如 <c>http://192.168.8.21:11451</c>；没有可用地址时回落到回环地址。</summary>
        public static string HttpUrl(int port)
        {
            (string? v4, _) = LocalAddresses();
            return HttpUrl(port, v4);
        }

        /// <summary>已经有了 IPv4 地址时用这个重载，省得再枚举一次网卡。</summary>
        public static string HttpUrl(int port, string? v4) => $"http://{v4 ?? "127.0.0.1"}:{port}";

        private static (string? V4, string? V6, string? Subnet) Read()
        {
            string? v4 = null;
            string? v6 = null;
            string? subnet = null;

            try
            {
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up
                        || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback
                        || adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    {
                        continue;
                    }

                    foreach (UnicastIPAddressInformation info in adapter.GetIPProperties().UnicastAddresses)
                    {
                        string address = info.Address.ToString();

                        if (info.Address.AddressFamily == AddressFamily.InterNetwork
                            && !address.StartsWith("169.254.", StringComparison.Ordinal)
                            && v4 is null)
                        {
                            v4 = address;
                            subnet = SubnetOf(info.Address, info.PrefixLength);
                        }
                        else if (info.Address.AddressFamily == AddressFamily.InterNetworkV6
                            && !info.Address.IsIPv6LinkLocal
                            && v6 is null)
                        {
                            v6 = address;
                        }
                    }

                    if (v4 is not null && v6 is not null)
                    {
                        break;
                    }
                }
            }
            catch
            {
                // 网卡信息读不到：调用方按「没有可用地址」处理，不要弹异常
            }

            return (v4, v6, subnet);
        }

        /// <summary>把「地址 + 前缀长度」折成网段写法；前缀异常时按 /24 处理（家用网络最常见）。</summary>
        private static string SubnetOf(IPAddress address, int prefixLength)
        {
            byte[] bytes = address.GetAddressBytes();

            if (prefixLength < 8 || prefixLength > 32)
            {
                prefixLength = 24;
            }

            uint value = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
            uint mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
            uint network = value & mask;

            return $"{network >> 24 & 0xFF}.{network >> 16 & 0xFF}.{network >> 8 & 0xFF}.{network & 0xFF}/{prefixLength}";
        }
    }
}
