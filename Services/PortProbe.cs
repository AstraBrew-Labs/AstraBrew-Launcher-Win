using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>占用某个端口的进程。</summary>
    public sealed record PortOwnerInfo(int Pid, string Name);

    /// <summary>
    /// 端口占用探测。配置页的实时检测与启动前的预检共用这一份。
    ///
    /// 两个判据是分开的，别混用：
    /// · <see cref="IsFree"/> 靠**真的绑定一次**判断。这是唯一可靠的方式，
    ///   查 netstat 会漏掉「已经绑上但还没 LISTEN」的窗口期。
    /// · <see cref="FindOwners"/> 靠 netstat 解析，只用来**说明是谁占的**，
    ///   拿不到结果也不影响「是否被占用」的结论。
    /// </summary>
    public static class PortProbe
    {
        /// <summary>端口现在能不能绑上。</summary>
        public static bool IsFree(int port)
        {
            if (port is < 1 or > 65535)
            {
                return false;
            }

            try
            {
                var listener = new TcpListener(IPAddress.Any, port)
                {
                    // 不独占的话，别的进程用 SO_REUSEADDR 绑过的端口这里也能绑成功，
                    // 结果是「检测说可用、启动却被占」——正是要避免的那种误判
                    ExclusiveAddressUse = true,
                };

                listener.Start();
                listener.Stop();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
            catch (Exception)
            {
                // 权限之类的问题：不能断言可用，按占用处理（宁可误报也不能让启动撞车）
                return false;
            }
        }

        /// <summary>
        /// 等端口真正释放。**同步版**，只给窗口关闭路径用。
        ///
        /// 那里没有事件循环再转一圈的机会，异步收尾跑不完；用 Thread.Sleep 而不是 Task.Delay，
        /// 是因为退出阶段定时器的粒度不可靠。
        /// </summary>
        /// <returns>超时前是否释放。false 表示端口被别人占着，调用方只该记日志、不该继续等。</returns>
        public static bool WaitFree(int port, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;

            while (true)
            {
                if (IsFree(port))
                {
                    return true;
                }

                if (DateTime.UtcNow >= deadline)
                {
                    return false;
                }

                Thread.Sleep(80);
            }
        }

        /// <summary>等端口释放（异步版）：停止服务后的收尾用。</summary>
        public static async Task<bool> WaitFreeAsync(int port, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;

            while (true)
            {
                if (IsFree(port))
                {
                    return true;
                }

                if (DateTime.UtcNow >= deadline)
                {
                    return false;
                }

                await Task.Delay(100).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 同步兜底：结束占用该端口的「启动器目录下的 node」，再等端口放开。
        ///
        /// 只给关窗路径用：那时 PM2 的 delete 可能因为 daemon 通信失败没生效，
        /// 而用户的要求是「关掉启动器就把端口放开」——这里做最后一层强制清理。
        /// 外部程序仍然不碰。
        /// </summary>
        public static bool ReleaseOwn(int port, TimeSpan timeout)
        {
            if (port is < 1 or > 65535)
            {
                return true;
            }

            foreach (PortOwnerInfo owner in FindOwners(port))
            {
                if (!IsOwnNodeProcess(owner.Pid))
                {
                    continue;
                }

                try
                {
                    using Process process = Process.GetProcessById(owner.Pid);
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(1500);
                }
                catch (Exception)
                {
                    // 已经退了 / 没权限：交给后面的端口检测
                }
            }

            return WaitFree(port, timeout);
        }

        /// <summary>查找占用者（仅 LISTENING）。查不到返回空列表，不代表端口空闲。</summary>
        public static List<PortOwnerInfo> FindOwners(int port)
        {
            var owners = new List<PortOwnerInfo>();

            try
            {
                var startInfo = new ProcessStartInfo("netstat")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };

                startInfo.ArgumentList.Add("-ano");
                startInfo.ArgumentList.Add("-p");
                startInfo.ArgumentList.Add("TCP");

                using Process? process = Process.Start(startInfo);
                if (process is null)
                {
                    return owners;
                }

                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(4000);

                var seen = new HashSet<int>();

                foreach (string raw in output.Split('\n'))
                {
                    string line = raw.Trim();
                    if (!line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string[] columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (columns.Length < 5)
                    {
                        continue;
                    }

                    // 本地地址形如 0.0.0.0:11451 / [::]:11451，只认端口完全相同的
                    string local = columns[1];
                    int colon = local.LastIndexOf(':');
                    if (colon < 0
                        || !int.TryParse(local[(colon + 1)..], out int localPort)
                        || localPort != port)
                    {
                        continue;
                    }

                    if (!int.TryParse(columns[^1], out int pid) || pid <= 0 || !seen.Add(pid))
                    {
                        continue;
                    }

                    string name = L.Get("Run.UnknownProcess");
                    try
                    {
                        name = Process.GetProcessById(pid).ProcessName + ".exe";
                    }
                    catch
                    {
                        // 进程刚退出，或者没权限查名字：保留 PID 也够用户判断了
                    }

                    owners.Add(new PortOwnerInfo(pid, name));
                }
            }
            catch (Exception)
            {
                // netstat 不可用（极少数精简系统）：返回空，调用方按「端口不可用」处理
            }

            return owners;
        }

        /// <summary>
        /// 尝试解除占用：只结束**确定是启动器自己装的 node** 的进程。
        ///
        /// 边界是刻意画死的：外部程序（数据库、别的 Node 项目、用户自己的服务）一律不动——
        /// 端口冲突不该以「帮你关掉一个别人的程序」收场，那可能直接丢数据。
        ///
        /// 返回真正结束掉的进程数；端口仍然没释放时返回 0。
        /// </summary>
        /// <param name="skip">
        /// 额外豁免。PM2 托管的子进程同样是「启动器目录下的 node」，但**绝对不能杀**——
        /// PM2 会立刻 autorestart，形成「启动 → 杀掉 → 又占端口」的死循环。
        /// </param>
        public static async Task<int> ReleaseOwnAsync(
            IReadOnlyList<PortOwnerInfo> owners,
            int port,
            Func<int, bool>? skip = null)
        {
            if (owners.Count == 0)
            {
                return 0;
            }

            int released = 0;

            foreach (PortOwnerInfo owner in owners)
            {
                if (!IsOwnNodeProcess(owner.Pid))
                {
                    continue;
                }

                if (skip is not null && skip(owner.Pid))
                {
                    continue;
                }

                try
                {
                    using Process process = Process.GetProcessById(owner.Pid);

                    // 连子进程一起结束：node 常带一个 npm/扩展脚本的子进程，
                    // 只杀父进程的话端口照样被占着
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2000);
                    released++;
                }
                catch (Exception)
                {
                    // 没权限或已经退出：交给下面的端口检测判断
                }
            }

            if (released == 0)
            {
                return 0;
            }

            // 进程退出到端口真正释放之间有一小段延迟，别急着下结论
            return await WaitFreeAsync(port, TimeSpan.FromSeconds(2)).ConfigureAwait(false)
                ? released
                : 0;
        }

        /// <summary>
        /// 占用者是不是「我们自己装的 node」。
        ///
        /// 判据是**可执行文件落在启动器的数据目录里**，而不是看当前选的是内置还是系统环境：
        /// 用户可能已经切到系统环境，但上次用内置环境起的酒馆进程还挂在端口上，
        /// 那种残留同样得能清掉（按当前环境去找 node 会漏掉它）。
        ///
        /// 反过来说，路径不在启动器目录下的一律不碰——系统 node 很可能是用户自己的项目。
        /// </summary>
        private static bool IsOwnNodeProcess(int pid)
        {
            string? path = TryGetExecutablePath(pid);

            if (path is null)
            {
                return false;
            }

            string normalized = NormalizePath(path);

            if (!Path.GetFileName(normalized).StartsWith("node", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            foreach (string root in new[] { AppPaths.RoamingRoot, AppPaths.LocalRoot })
            {
                string prefix = NormalizePath(root) + Path.DirectorySeparatorChar;

                if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string? TryGetExecutablePath(int pid)
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                return process.MainModule?.FileName;
            }
            catch (Exception)
            {
                // 权限不足时读不到路径：当作「不是自己的进程」，宁可不杀
                return null;
            }
        }

        private static string NormalizePath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return path;
            }
        }
    }
}
