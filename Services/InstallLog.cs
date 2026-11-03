using System;
using System.IO;
using System.Text;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 安装日志：一次安装一个文件，落在 <c>%AppData%\AstraBrew Launcher\logs</c> 下。
    ///
    /// 分工必须分清：界面只给一句人话结论（「连不上下载源」），
    /// 尝试过哪些源、HTTP 状态码、耗时、异常堆栈全部进这里。
    /// 把 300 行日志糊到弹窗里，用户既看不懂也帮不上忙；留在文件里，
    /// 反馈问题时把路径发过来就够定位了。
    ///
    /// 写日志失败绝不阻断安装本身：日志是旁证，不是前置条件。
    /// </summary>
    public sealed class InstallLog : IDisposable
    {
        /// <summary>日志目录：<c>%AppData%\AstraBrew Launcher\logs</c>。</summary>
        public static string FolderPath { get; } = Path.Combine(AppPaths.RoamingRoot, "logs");

        private readonly object _gate = new();
        private readonly StreamWriter? _writer;
        private bool _disposed;

        /// <summary>本次安装的日志文件完整路径（即使文件没建成也给出预期路径）。</summary>
        public string FilePath { get; }

        private InstallLog(string filePath, StreamWriter? writer)
        {
            FilePath = filePath;
            _writer = writer;
        }

        /// <summary>
        /// 新建一份日志。<paramref name="task"/> 用作文件名前缀（如 <c>mingit</c>），
        /// 带上时间戳，多次安装不会互相覆盖——排查时往往要对比「上一次为什么成功」。
        /// </summary>
        public static InstallLog Create(string task)
        {
            string file = Path.Combine(FolderPath, $"{task}-{DateTime.Now:yyyyMMdd-HHmmss}.log");

            try
            {
                Directory.CreateDirectory(FolderPath);
                var stream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

                var log = new InstallLog(file, writer);
                log.Info($"AstraBrew Launcher · {task} 安装日志");
                log.Info($"时间 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                log.Info($"系统 {Environment.OSVersion.VersionString} · {(Environment.Is64BitProcess ? "64" : "32")} 位进程");
                log.Info($"日志文件 {file}");
                log.Info(new string('-', 64));
                return log;
            }
            catch
            {
                // 目录不可写（只读盘、权限被策略挡住）：安装照常进行，只是没有落盘记录
                return new InstallLog(file, null);
            }
        }

        /// <summary>普通过程记录。</summary>
        public void Info(string message) => Write("INFO", message);

        /// <summary>不影响成功、但值得记一笔的情况（换源、跳过的压缩包条目等）。</summary>
        public void Warn(string message) => Write("WARN", message);

        /// <summary>失败记录；带异常时把类型与堆栈一并落盘。</summary>
        public void Fail(string message, Exception? error = null)
        {
            Write("FAIL", message);
            if (error is null)
            {
                return;
            }

            Write("FAIL", $"  {error.GetType().Name}: {error.Message}");

            for (Exception? inner = error.InnerException; inner is not null; inner = inner.InnerException)
            {
                Write("FAIL", $"  由 {inner.GetType().Name} 引发: {inner.Message}");
            }

            if (error.StackTrace is { Length: > 0 } stack)
            {
                foreach (string line in stack.Split('\n'))
                {
                    Write("FAIL", "  " + line.TrimEnd('\r'));
                }
            }
        }

        /// <summary>写一行；多线程调用安全，落盘靠 AutoFlush，掉电最多丢最后一行。</summary>
        private void Write(string level, string message)
        {
            if (_writer is null || _disposed)
            {
                return;
            }

            lock (_gate)
            {
                try
                {
                    _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {level,-4} {message}");
                }
                catch (IOException)
                {
                    // 磁盘满或文件被别的进程锁住：不能因为日志把安装搞崩
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                try
                {
                    _writer?.Dispose();
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
