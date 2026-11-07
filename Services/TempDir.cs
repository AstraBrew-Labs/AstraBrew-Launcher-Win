using System;
using System.IO;
using System.Threading;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 临时工作目录：创建在 <c>%TEMP%\astrabrew-launcher\&lt;分类&gt;\&lt;guid&gt;</c>，用完即删。
    ///
    /// 删除必须「尽力而为」：克隆出来的仓库里常有只读文件、git 也可能还握着句柄，
    /// 一次删不掉就重试。删不掉也只是留了一份临时数据，不该让调用方失败。
    /// </summary>
    internal static class TempDir
    {
        /// <summary>建一个空的临时目录（已存在同名时由系统生成的新 guid 保证不冲突）。</summary>
        public static string Create(string category)
        {
            string root = Path.Combine(AppPaths.TempRoot, category);
            string dir = Path.Combine(root, Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception)
            {
                // 建不出来交给调用方：git / 下载器会报出更具体的错误
            }

            return dir;
        }

        /// <summary>
        /// 清掉这个分类下早于 <paramref name="olderThan"/> 的残留目录。
        /// 应用被强关时克隆来不及删干净，下次开测之前顺手收一次，别让 %TEMP% 越堆越大。
        /// </summary>
        public static void ClearStale(string category, TimeSpan olderThan)
        {
            string root = Path.Combine(AppPaths.TempRoot, category);
            if (!Directory.Exists(root))
            {
                return;
            }

            try
            {
                foreach (string dir in Directory.EnumerateDirectories(root))
                {
                    try
                    {
                        if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) >= olderThan)
                        {
                            Delete(dir);
                        }
                    }
                    catch (Exception)
                    {
                        // 单个目录清不掉不影响其他的
                    }
                }
            }
            catch (Exception)
            {
                // 目录列举失败就算了：这是清理，不是功能
            }
        }

        /// <summary>递归删除，先去掉只读属性，失败重试若干次。</summary>
        public static void Delete(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            for (int attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    if (!Directory.Exists(path))
                    {
                        return;
                    }

                    foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            File.SetAttributes(file, FileAttributes.Normal);
                        }
                        catch (Exception)
                        {
                            // 单个文件改不了属性不影响整体删除
                        }
                    }

                    Directory.Delete(path, recursive: true);
                    return;
                }
                catch (Exception)
                {
                    Thread.Sleep(200);
                }
            }
        }
    }
}
