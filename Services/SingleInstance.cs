using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 单实例 + 唤醒通道。
    ///
    /// 从浏览器点 <c>launcher-astrabrew://</c> 时，Windows 会**再起一个我们的进程**并把链接当参数传进来。
    /// 已经有一个实例在跑的话，第二个实例不该真的开第二个窗口（两个窗口抢同一份设置、同一条端口），
    /// 它把链接丢给第一个实例，然后自己退出。
    ///
    /// 用「命名互斥量 + 命名管道」，不用 WinAppSDK 的 <c>AppInstance</c>：
    /// 后者的激活重定向对**没有包身份**的 unpackaged 构建并不可靠，而本项目四个渠道都是 unpackaged。
    ///
    /// 注意只在**带链接启动**时才做转发；不带参数的第二次启动仍然照旧开新窗口（保持原有行为）。
    /// </summary>
    internal static class SingleInstance
    {
        private const string MutexName = @"Local\AstraBrewLauncher.SingleInstance";
        private const string PipeName = "AstraBrewLauncher.WakeUp";

        /// <summary>进程活多久就持有多久，别让它被 GC 回收掉（回收等于释放所有权）。</summary>
        private static Mutex? _mutex;

        /// <summary>自己是不是第一个实例。拿不到互斥量就是「已经有人在跑」。</summary>
        public static bool TryAcquire()
        {
            try
            {
                _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
                if (createdNew)
                {
                    return true;
                }

                _mutex.Dispose();
                _mutex = null;
                return false;
            }
            catch (Exception)
            {
                // 互斥量建不出来（权限异常之类）：按主实例处理，至少保证能启动
                return true;
            }
        }

        /// <summary>
        /// 把链接交给正在跑的那个实例。主实例可能刚启动还没开始监听，
        /// 所以在 <paramref name="timeoutMs"/> 内反复重试，别一次连不上就放弃。
        /// </summary>
        public static bool TrySend(string payload, int timeoutMs = 2500)
        {
            long deadline = Environment.TickCount64 + timeoutMs;

            while (Environment.TickCount64 < deadline)
            {
                try
                {
                    using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                    client.Connect(500);

                    using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
                    writer.WriteLine(payload);
                    return true;
                }
                catch (Exception)
                {
                    Thread.Sleep(120);
                }
            }

            return false;
        }

        /// <summary>
        /// 主实例侧：后台线程蹲着收消息，收到就回调（回调在**后台线程**上执行，
        /// 调用方自己切回 UI 线程）。
        /// </summary>
        public static void Listen(Action<string> onMessage)
        {
            var thread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(
                            PipeName,
                            PipeDirection.In,
                            1,
                            PipeTransmissionMode.Byte,
                            PipeOptions.None);

                        server.WaitForConnection();

                        using var reader = new StreamReader(server, new UTF8Encoding(false));
                        string? line = reader.ReadLine();

                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            onMessage(line);
                        }
                    }
                    catch (Exception)
                    {
                        // 管道被占用 / 进程正在退出：喘一口气再来，别把线程烧了
                        Thread.Sleep(200);
                    }
                }
            })
            {
                IsBackground = true,
                Name = "AstraBrew deep-link listener",
            };

            thread.Start();
        }
    }
}
