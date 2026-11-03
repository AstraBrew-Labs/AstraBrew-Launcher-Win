using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 删除与定位文件的那点系统调用。
    ///
    /// 资源页、扩展页删的都是用户自己的文件（角色卡、聊天记录、扩展目录），
    /// <c>Directory.Delete</c> 一按就没了，误删只能靠备份。这里统一走回收站
    /// （<c>SHFileOperation</c> + <c>FOF_ALLOWUNDO</c>），删错了还能从回收站捡回来；
    /// 只有回收站本身失败（网络盘、权限）才退回直接删除，并把结果如实返回给界面。
    /// </summary>
    public static class ShellFile
    {
        private const uint FOF_SILENT = 0x0004;
        private const uint FOF_NOCONFIRMATION = 0x0010;
        private const uint FOF_ALLOWUNDO = 0x0040;
        private const uint FOF_NOERRORUI = 0x0400;

        /// <summary>
        /// 把文件或目录送进回收站。返回是否成功；<paramref name="error"/> 里是失败原因。
        /// </summary>
        public static Task<(bool Ok, string? Error)> MoveToRecycleBinAsync(string path)
        {
            return Task.Run(() => MoveToRecycleBin(path));
        }

        /// <summary>同步版：调用方在后台线程时用。</summary>
        public static (bool Ok, string? Error) MoveToRecycleBin(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return (false, "路径为空");
            }

            bool exists = File.Exists(path) || Directory.Exists(path);
            if (!exists)
            {
                return (false, "文件已经不存在了");
            }

            try
            {
                // SHFileOperation 要双 null 结尾的路径，且不能带 \\?\ 前缀
                var operation = new ShFileOpStruct
                {
                    wFunc = 3, // FO_DELETE
                    pFrom = path + "\0\0",
                    fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI),
                };

                int code = SHFileOperation(ref operation);

                if (code == 0 && !operation.fAnyOperationsAborted)
                {
                    return (true, null);
                }

                return (false, code == 0 ? "操作被取消" : $"系统返回错误 0x{code:X}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException)
            {
                return (false, ex.Message);
            }
        }

        /// <summary>
        /// 在资源管理器里显示某个文件或目录。
        /// 目录直接打开；文件用 <c>/select,</c> 定位并选中它，省得用户自己在一堆文件里翻。
        /// </summary>
        public static void Reveal(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                    {
                        UseShellExecute = true,
                    });

                    return;
                }

                if (Directory.Exists(path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"")
                    {
                        UseShellExecute = true,
                    });
                }
            }
            catch
            {
                // 资源管理器起不来（被策略挡、系统精简过）也没别的办法，调用方已经给了提示
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ShFileOpStruct
        {
            public IntPtr hwnd;

            public uint wFunc;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string pFrom;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string? pTo;

            public ushort fFlags;

            [MarshalAs(UnmanagedType.Bool)]
            public bool fAnyOperationsAborted;

            public IntPtr hNameMappings;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string? lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref ShFileOpStruct fileOp);
    }
}
