using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 尾随 PM2 的 out / err 文件，把新增的完整行回灌进 <see cref="TavernRuntime"/>。
    ///
    /// 为什么需要它：PM2 托管后，酒馆的 stdout 归 PM2 管，我们手里没有 <see cref="System.Diagnostics.Process"/>，
    /// 原本 OnOutput 那条管道断了——控制台、日志页、磁盘三处会同时哑掉。
    /// 回灌之后行仍走 <see cref="TavernRuntime"/> 的 AddLog，三处看到的是同一份。
    ///
    /// **按前缀扫目录，而不是盯死两个路径**：PM2 在 cluster / 多实例 / 重启过的情形下会把日志写成
    /// <c>&lt;app&gt;-out-0.log</c>、<c>-out-1.log</c>，只盯 <c>-out.log</c> 会永远读到空文件
    /// （这正是「托管后控制台没有酒馆输出」的直接原因）。
    ///
    /// 为什么是轮询而不是 <c>FileSystemWatcher</c>：PM2 一次可能写几百行，
    /// watcher 的 Change 会合并、会丢事件；按偏移量读反而准，代价只是 400ms 的延迟。
    /// </summary>
    internal sealed class Pm2LogTail : IDisposable
    {
        private readonly Dictionary<string, Cursor> _cursors = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _appName;
        private readonly Action<string, bool> _onLine;
        private readonly Timer _timer;

        private int _busy;
        private int _warned;
        private bool _disposed;
        private bool _replayHistory = true;
        private long _replayBytes = long.MaxValue;
        private string? _historyNotice;
        private bool _noticeSent;

        /// <param name="onLine">收到一行：参数是文本与「是否来自 stderr」。</param>
        public Pm2LogTail(string appName, Action<string, bool> onLine)
        {
            _appName = appName;
            _onLine = onLine;
            _timer = new Timer(_ => Pump(), null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>接管时回放多少字节：够看到启动横幅与最近的报错，又不会把控制台刷满。</summary>
        public const long AdoptReplayBytes = 64 * 1024;

        /// <summary>
        /// 开始尾随。
        /// </summary>
        /// <param name="replayHistory">
        /// 是否回放文件里已有的内容。**两条路径都必须回放**：
        /// · 本次新启动：文件刚被清空，从 0 读就是本次输出；
        /// · 接管上一轮遗留的 app：这份文件是酒馆输出**唯一**的存放处，不回放的话控制台、
        ///   日志页都是空的——用户只能看到「接管成功」这一行，等于把日志区废掉。
        /// </param>
        /// <param name="replayBytes">回放上限，超出就只读尾部（避免一次读几十兆）。</param>
        /// <param name="historyNotice">
        /// 回放前插的一行提示（接管时传，说明下面的内容是接管前记录的）。
        /// 传 <c>null</c> 就不插。
        /// </param>
        public void Start(bool replayHistory, long replayBytes = long.MaxValue, string? historyNotice = null)
        {
            _replayHistory = replayHistory;
            _replayBytes = replayBytes;
            _historyNotice = historyNotice;
            _timer.Change(300, 400);
        }

        private void Pump()
        {
            if (_disposed || Interlocked.Exchange(ref _busy, 1) == 1)
            {
                return;
            }

            try
            {
                foreach ((string path, bool isError) in Discover())
                {
                    if (!_cursors.TryGetValue(path, out Cursor? cursor))
                    {
                        cursor = new Cursor(path, isError);

                        if (_replayHistory)
                        {
                            cursor.SeekStart(_replayBytes);
                        }
                        else
                        {
                            cursor.SeekEnd();
                        }

                        _cursors[path] = cursor;

                        // 提示只插一次，且要插在真正有内容可看的那份文件之前：
                        // 空文件前顶一行「以下是接管前的输出」只会让人以为日志丢了
                        if (_historyNotice is { Length: > 0 } && !_noticeSent && cursor.HasPending)
                        {
                            _noticeSent = true;
                            _onLine(_historyNotice, false);
                        }
                    }

                    cursor.Read(_onLine);
                }
            }
            catch (Exception ex)
            {
                // 只记第一次：磁盘抖动会连着抛，刷屏等于没有日志
                if (Interlocked.Exchange(ref _warned, 1) == 0)
                {
                    RuntimeLog.Warn($"pm2 log tail failed: {ex.Message}");
                }
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }

        /// <summary>枚举本 app 的全部 out / err 文件（含 <c>-0</c>、<c>-1</c> 这些实例后缀）。</summary>
        private IEnumerable<(string Path, bool IsError)> Discover() => Enumerate(_appName);

        private static IEnumerable<(string Path, bool IsError)> Enumerate(string appName)
        {
            if (!Directory.Exists(Pm2Service.LogFolder))
            {
                yield break;
            }

            foreach (string path in Directory.EnumerateFiles(Pm2Service.LogFolder, appName + "-out*.log"))
            {
                yield return (path, false);
            }

            foreach (string path in Directory.EnumerateFiles(Pm2Service.LogFolder, appName + "-err*.log"))
            {
                yield return (path, true);
            }
        }

        /// <summary>
        /// 一次性读尾部若干字节：接管时用它把「启动器没开时酒馆吐出来的输出」补回日志页。
        ///
        /// 与尾随器分开的两个理由：这里是**同步、一次读完**（接管只发生一次），
        /// 而且补进来的行只进日志页、不进控制台——控制台由存档负责。
        /// </summary>
        public static IReadOnlyList<(string Text, bool IsError)> ReadTail(string appName, long maxBytesPerFile)
        {
            var lines = new List<(string, bool)>();

            foreach ((string path, bool isError) in Enumerate(appName))
            {
                try
                {
                    using var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);

                    long start = Math.Max(0, stream.Length - maxBytesPerFile);
                    stream.Seek(start, SeekOrigin.Begin);

                    using var reader = new StreamReader(stream, Encoding.UTF8);

                    // 从中间开始必然有一条半截行
                    if (start > 0)
                    {
                        reader.ReadLine();
                    }

                    string? line;

                    while ((line = reader.ReadLine()) is not null)
                    {
                        if (line.Length > 0)
                        {
                            lines.Add((line, isError));
                        }
                    }
                }
                catch (Exception)
                {
                    // 文件被删 / 正在轮转：跳过这份，其余照读
                }
            }

            return lines;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Dispose();

            // 收掉最后半行：进程刚好退出时最后一行常常没有换行符
            try
            {
                foreach (Cursor cursor in _cursors.Values)
                {
                    cursor.Flush(_onLine);
                }
            }
            catch (Exception)
            {
                // 退出路径上不再纠缠
            }
        }

        /// <summary>一个文件的读取游标。UTF-8 解码器与半行缓冲都要跨轮次保留。</summary>
        private sealed class Cursor
        {
            /// <summary>单次回放上限：超过就只看尾部。</summary>
            private const long MaxReplayBytes = 2L * 1024 * 1024;

            private readonly string _path;
            private readonly bool _isError;
            private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
            private readonly StringBuilder _partial = new();

            private long _offset;
            private bool _skipFirstLine;
            private int _idleRounds;

            public Cursor(string path, bool isError)
            {
                _path = path;
                _isError = isError;
            }

            /// <summary>还有没读过的内容（决定要不要在那份文件前插「以下是接管前的输出」）。</summary>
            public bool HasPending
            {
                get
                {
                    try
                    {
                        return File.Exists(_path) && new FileInfo(_path).Length > _offset;
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                }
            }

            /// <summary>
            /// 回到起点。文件超过 <paramref name="maxBytes"/> 时从尾部截一段，
            /// 并标记「第一行是半截」——不标的话用户会看到一条从中间开始的乱码行。
            /// </summary>
            public void SeekStart(long maxBytes = MaxReplayBytes)
            {
                try
                {
                    if (!File.Exists(_path))
                    {
                        _offset = 0;
                        return;
                    }

                    long length = new FileInfo(_path).Length;
                    _offset = length > maxBytes ? length - maxBytes : 0;
                    _skipFirstLine = _offset > 0;
                }
                catch (Exception)
                {
                    _offset = 0;
                    _skipFirstLine = false;
                }
            }

            /// <summary>从文件当前末尾开始（只看新增内容）。</summary>
            public void SeekEnd()
            {
                try
                {
                    _offset = File.Exists(_path) ? new FileInfo(_path).Length : 0;
                }
                catch (Exception)
                {
                    _offset = 0;
                }
            }

            public void Read(Action<string, bool> onLine)
            {
                if (!File.Exists(_path))
                {
                    return;
                }

                using var stream = new FileStream(
                    _path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                // 文件被清空（我们启动前会清）或重建：从头再来
                if (stream.Length < _offset)
                {
                    _offset = 0;
                    _partial.Clear();
                }

                if (stream.Length == _offset)
                {
                    // 连续几轮没新内容：如果还压着半行（进度输出常常长期不带换行），把它吐出来
                    if (++_idleRounds >= 5 && _partial.Length > 0)
                    {
                        Flush(onLine);
                    }

                    return;
                }

                _idleRounds = 0;
                stream.Seek(_offset, SeekOrigin.Begin);

                var buffer = new byte[8192];
                int read;

                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    _offset += read;
                    Append(buffer, read, onLine);
                }
            }

            private void Append(byte[] buffer, int count, Action<string, bool> onLine)
            {
                char[] chars = new char[_decoder.GetCharCount(buffer, 0, count)];
                int written = _decoder.GetChars(buffer, 0, count, chars, 0);

                _partial.Append(chars, 0, written);

                string all = _partial.ToString();
                int start = 0;
                int index;

                while ((index = all.IndexOf('\n', start)) >= 0)
                {
                    string line = all[start..index].TrimEnd('\r');

                    // 从文件中间起读时，第一条必然是被截断的半行：丢掉
                    if (_skipFirstLine)
                    {
                        _skipFirstLine = false;
                    }
                    else if (line.Length > 0)
                    {
                        onLine(line, _isError);
                    }

                    start = index + 1;
                }

                // 剩下的半行留到下一轮：PM2 也会写不带换行的进度输出
                _partial.Clear();

                if (start < all.Length)
                {
                    _partial.Append(all[start..]);
                }
            }

            public void Flush(Action<string, bool> onLine)
            {
                if (_partial.Length == 0)
                {
                    return;
                }

                string line = _partial.ToString();
                _partial.Clear();

                if (line.Length > 0)
                {
                    onLine(line, _isError);
                }
            }
        }
    }
}
