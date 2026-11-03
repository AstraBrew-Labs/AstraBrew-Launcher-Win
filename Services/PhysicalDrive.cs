using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;

namespace AstraBrew_Launcher.Services
{
    /// <summary>一块盘是机械盘还是固态盘。只用于给用户一句解释，不参与扫描决策。</summary>
    public enum DriveKind
    {
        /// <summary>问不出来（虚拟盘、RAID、部分 USB 桥接）。不猜测、也不吓人。</summary>
        Unknown,

        Ssd,
        Hdd,
    }

    /// <summary>一块物理盘的自述：介质类型、是不是外接、型号串。</summary>
    public sealed record PhysicalDriveInfo(DriveKind Kind, bool External, string Model)
    {
        public static PhysicalDriveInfo Unknown { get; } = new(DriveKind.Unknown, false, string.Empty);

        /// <summary>型号串里出现这些词就当固态：问不到 seek penalty 时的兜底判据。</summary>
        private static readonly string[] SsdHints = { "SSD", "NVME", "SOLID STATE" };

        public static DriveKind GuessFromModel(string model)
        {
            foreach (string hint in SsdHints)
            {
                if (model.Contains(hint, StringComparison.OrdinalIgnoreCase))
                {
                    return DriveKind.Ssd;
                }
            }

            return DriveKind.Unknown;
        }
    }

    /// <summary>
    /// 问系统「这块盘是机械还是固态」「是不是外接」。
    ///
    /// 用的是 <c>IOCTL_STORAGE_QUERY_PROPERTY</c> 的两条属性：
    /// <list type="bullet">
    /// <item><c>StorageDeviceSeekPenaltyProperty</c>：机械盘寻道有代价，固态没有——这是系统自己的判据，
    /// 比看型号靠谱。</item>
    /// <item><c>StorageDeviceProperty</c>：总线类型。<c>USB</c> / <c>1394</c> / <c>SD</c> 一律算外接。</item>
    /// </list>
    ///
    /// 句柄用 0 权限打开：查属性不需要读数据，普通用户就能问，不必提权。
    /// 问不到就返回 <see cref="DriveKind.Unknown"/>——「不知道」比猜错好，
    /// 把机械盘当固态盘会让用户等得莫名其妙。
    /// </summary>
    public static class PhysicalDrive
    {
        private const uint IoctlStorageQueryProperty = 0x002D1400;
        private const uint StorageDeviceProperty = 0;
        private const uint StorageDeviceSeekPenaltyProperty = 7;

        /// <summary>总线类型里算「外接 / 可拔」的几种：U 盘、移动硬盘、读卡器。</summary>
        private const uint BusTypeUsb = 7;
        private const uint BusTypeFirewire = 4;
        private const uint BusTypeSd = 12;
        private const uint BusTypeMmc = 13;

        private static readonly IntPtr InvalidHandle = new(-1);

        private static readonly ConcurrentDictionary<string, PhysicalDriveInfo> Cache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>问一块盘（<c>C:\</c> 或 <c>C:</c> 都行）。结果按盘符缓存，扫描里会问很多次。</summary>
        public static PhysicalDriveInfo Describe(string root)
        {
            string volume = root.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            if (volume.Length == 0)
            {
                return PhysicalDriveInfo.Unknown;
            }

            return Cache.GetOrAdd(volume, Probe);
        }

        /// <summary>只是包装一下 <see cref="Describe"/>，读起来更像一句话。</summary>
        public static DriveKind KindOf(string root) => Describe(root).Kind;

        private static PhysicalDriveInfo Probe(string volume)
        {
            IntPtr handle;
            try
            {
                handle = CreateFileW(
                    @"\\.\" + volume,
                    0,
                    FileShare.Read | FileShare.Write,
                    IntPtr.Zero,
                    FileMode.Open,
                    FileAttributes.Normal,
                    IntPtr.Zero);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return PhysicalDriveInfo.Unknown;
            }

            if (handle == InvalidHandle || handle == IntPtr.Zero)
            {
                return PhysicalDriveInfo.Unknown;
            }

            try
            {
                string model = QueryModel(handle, out uint busType);
                bool external = busType is BusTypeUsb or BusTypeFirewire or BusTypeSd or BusTypeMmc;
                DriveKind kind = QueryKind(handle, model);
                return new PhysicalDriveInfo(kind, external, model);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return PhysicalDriveInfo.Unknown;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        /// <summary>先问 seek penalty；问不到再看型号里有没有 SSD / NVMe 字样。</summary>
        private static DriveKind QueryKind(IntPtr handle, string model)
        {
            byte[] query = BuildQuery(StorageDeviceSeekPenaltyProperty);
            var buffer = new byte[512];

            if (DeviceIoControl(handle, IoctlStorageQueryProperty, query, query.Length, buffer, buffer.Length, out int returned, IntPtr.Zero)
                && returned >= 12)
            {
                // DEVICE_SEEK_PENALTY_DESCRIPTOR：Version(4) Size(4) IncursSeekPenalty(1)
                return buffer[8] != 0 ? DriveKind.Hdd : DriveKind.Ssd;
            }

            return PhysicalDriveInfo.GuessFromModel(model);
        }

        /// <summary>读型号串与总线类型（<c>STORAGE_DEVICE_DESCRIPTOR</c> 的固定字段）。</summary>
        private static string QueryModel(IntPtr handle, out uint busType)
        {
            busType = 0;
            byte[] query = BuildQuery(StorageDeviceProperty);
            var buffer = new byte[1024];

            if (!DeviceIoControl(handle, IoctlStorageQueryProperty, query, query.Length, buffer, buffer.Length, out int returned, IntPtr.Zero)
                || returned < 36)
            {
                return string.Empty;
            }

            busType = BitConverter.ToUInt32(buffer, 28);

            uint offset = BitConverter.ToUInt32(buffer, 16);
            if (offset == 0 || offset >= returned)
            {
                return string.Empty;
            }

            int end = Array.IndexOf(buffer, (byte)0, (int)offset);
            if (end < 0 || end > returned)
            {
                end = returned;
            }

            return System.Text.Encoding.ASCII.GetString(buffer, (int)offset, end - (int)offset).Trim();
        }

        /// <summary><c>STORAGE_PROPERTY_QUERY</c>：PropertyId + QueryType(Standard) + 一字节占位。</summary>
        private static byte[] BuildQuery(uint propertyId)
        {
            var query = new byte[12];
            BitConverter.GetBytes(propertyId).CopyTo(query, 0);
            BitConverter.GetBytes(0u).CopyTo(query, 4);
            return query;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(
            string fileName,
            uint desiredAccess,
            FileShare shareMode,
            IntPtr securityAttributes,
            FileMode creationDisposition,
            FileAttributes flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr device,
            uint controlCode,
            byte[] inBuffer,
            int inBufferSize,
            byte[] outBuffer,
            int outBufferSize,
            out int bytesReturned,
            IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
