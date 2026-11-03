using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 本机网络地址。仪表盘的主卡、控制台的「设备访问」、酒馆配置的访问说明都从这里取——
    /// 以前各处各写一份示例 IP，用户复制出去根本连不上。
    ///
    /// 只取「已连接网卡上的全局单播地址」：回环不算、<c>169.254.x.x</c> 不算
    /// （那是没拿到 DHCP 时的自分配地址）、隧道网卡不算。
    /// </summary>
    public static class NetworkInfo
    {
        /// <summary>本机的 IPv4 / IPv6 局域网地址；没有对应协议时为 null。</summary>
        public static (string? V4, string? V6) LocalAddresses()
        {
            string? v4 = null;
            string? v6 = null;

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

            return (v4, v6);
        }

        /// <summary>形如 <c>http://192.168.8.21:11451</c>；没有可用地址时回落到回环地址。</summary>
        public static string HttpUrl(int port)
        {
            (string? v4, _) = LocalAddresses();
            return $"http://{v4 ?? "127.0.0.1"}:{port}";
        }
    }
}
