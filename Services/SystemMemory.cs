using System;
using System.Runtime.InteropServices;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 本机物理内存总量。
    ///
    /// 只有一个用途：给 WebView 的内存上限算出「最大能开到多少」。
    /// 写死 8 GB / 16 GB 这种上限在 8 GB 机器上等于允许把系统拖进交换分区，
    /// 所以这个数必须按真实内存推，并给系统留出余量。
    /// </summary>
    internal static class SystemMemory
    {
        /// <summary>给系统预留的绝对下限。</summary>
        private const long ReserveMb = 2048;

        /// <summary>给系统预留的比例：内存越大，按比例预留得越多。</summary>
        private const long ReservePercent = 20;

        /// <summary>读不到真实值时按 8 GB 兜底，保证滑块仍可用而不是卡在 0。</summary>
        private const int FallbackMb = 8192;

        /// <summary>物理内存总量（MB）。读不到时为 0。</summary>
        public static long TotalMb { get; } = ReadTotal();

        /// <summary>
        /// WebView 内存上限的最大可取值（MB）：总量减去系统预留，向下取整到整 GB，
        /// 且不低于 1 GB —— 小内存机器上也要留一个能设置的值。
        /// </summary>
        public static int MaxLimitMb { get; } = ComputeMaxLimit();

        /// <summary>给界面用的一行说明：本机 16 GB，上限可到 12 GB。</summary>
        public static string DescribeGb(long mb) => mb > 0 ? $"{mb / 1024.0:0.#} GB" : "未知";

        private static int ComputeMaxLimit()
        {
            long total = TotalMb;

            if (total <= 0)
            {
                return FallbackMb;
            }

            long reserve = Math.Max(ReserveMb, total * ReservePercent / 100);
            long max = total - reserve;

            // 整 GB：滑杆步进 512 MB，上限给个整数更好对
            max = max / 1024 * 1024;

            return (int)Math.Max(1024, max);
        }

        private static long ReadTotal()
        {
            try
            {
                var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };

                if (GlobalMemoryStatusEx(ref status))
                {
                    return (long)(status.TotalPhys / (1024 * 1024));
                }
            }
            catch (Exception)
            {
                // 取不到就当未知，交给上限的兜底值
            }

            return 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
    }
}
