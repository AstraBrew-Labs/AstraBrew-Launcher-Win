using System;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 一次性的「请去改端口」请求。
    ///
    /// 启动前发现端口被别的程序占着、又解不掉的场景：启动器要跳到酒馆配置页，
    /// 并且把端口那一行标出来。跳转会重建页面（<c>NavigateTo("tavern", force: true)</c>），
    /// 新页面在 <c>Loaded</c> 里消费这个请求——所以它必须是个静态信箱，
    /// 不能挂在某个页面实例上（那时页面还没建出来）。
    /// </summary>
    public static class PortAttention
    {
        private static readonly object Gate = new();
        private static int? _pending;

        /// <summary>登记一条请求。多次登记只保留最后一次的端口号。</summary>
        public static void Request(int port)
        {
            lock (Gate)
            {
                _pending = port;
            }
        }

        /// <summary>取走请求。没有就返回 false。</summary>
        public static bool TryConsume(out int port)
        {
            lock (Gate)
            {
                if (_pending is int value)
                {
                    _pending = null;
                    port = value;
                    return true;
                }
            }

            port = 0;
            return false;
        }

        /// <summary>丢弃未消费的请求（用户自己离开配置页之后就别再翻旧账）。</summary>
        public static void Clear()
        {
            lock (Gate)
            {
                _pending = null;
            }
        }
    }
}
