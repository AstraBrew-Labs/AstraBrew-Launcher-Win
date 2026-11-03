using System;
using System.IO;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 面向用户的路径取值点。
    ///
    /// 界面、ToolTip、反馈条、控制台日志、剪贴板里出现的路径一律是展开后的真实路径
    /// （<c>C:\Users\…\AppData\Roaming\AstraBrew Launcher\…</c>），不出现
    /// <c>%AppData%</c> / <c>%Temp%</c> 这类变量名——用户没法把变量粘进资源管理器。
    ///
    /// 变量形式只保留在存储值（配置文件里记的默认目录）与注释 / 文档示意中，
    /// 这类值读出后必须先经 <see cref="Expand"/> 再交给界面。
    /// </summary>
    public static class AppPaths
    {
        /// <summary>根目录：<c>%AppData%\AstraBrew Launcher</c>。</summary>
        public static string RoamingRoot { get; } =
            Combine(Environment.SpecialFolder.ApplicationData, "AstraBrew Launcher");

        /// <summary>本地程序目录：<c>%LocalAppData%\AstraBrew Launcher</c>。</summary>
        public static string LocalRoot { get; } =
            Combine(Environment.SpecialFolder.LocalApplicationData, "AstraBrew Launcher");

        /// <summary>临时目录：<c>%Temp%\astrabrew-launcher</c>。</summary>
        public static string TempRoot { get; } =
            Path.Combine(Path.GetTempPath(), "astrabrew-launcher");

        /// <summary>
        /// 可重建的缓存目录：<c>%Temp%\astrabrew-launcher\caches</c>。
        ///
        /// 放发布列表这类「删了只会多请求一次」的数据，属于中间产物而不是用户资产：
        /// 和临时下载放在一起，被磁盘清理或重启清掉也只是下次重新请求一次，
        /// 不让它在 AppData 里占着用户的备份与同步体积。
        /// </summary>
        public static string CacheRoot { get; } = Path.Combine(TempRoot, "caches");

        /// <summary>把 <c>%APPDATA%</c> 之类的变量换成实际目录，供显示与文件操作使用。</summary>
        public static string Expand(string path) => Environment.ExpandEnvironmentVariables(path);

        private static string Combine(Environment.SpecialFolder folder, string child)
        {
            string root = Environment.GetFolderPath(folder);
            return root.Length > 0 ? Path.Combine(root, child) : AppContext.BaseDirectory;
        }
    }
}
