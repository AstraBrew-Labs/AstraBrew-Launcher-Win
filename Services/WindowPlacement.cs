using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 窗口规范执行者。集中处理四件事，避免散落在 MainWindow 里各写一套：
    ///
    /// 1. 禁止最大化 —— 去掉 WS_MAXIMIZEBOX，并在窗口过程里拦掉 SC_MAXIMIZE，
    ///    双击标题栏 / Win+↑ / 拖到屏幕顶端 Aero Snap 都不会把窗口顶成最大化。
    /// 2. 高清屏适配 —— 尺寸一律以 DIP 描述，落成像素时乘以当前显示器 DPI；
    ///    窗口在屏间移动导致 DPI 变化时，最小尺寸按新 DPI 重新计算。
    /// 3. 每个缩放都能正常显示 —— 初始尺寸、最小尺寸都夹在当前显示器工作区内，
    ///    1366×768@150% 这类「逻辑分辨率很小」的屏上也不会出现窗口比屏幕还大。
    /// 4. 多屏切换 / 副屏断开 —— 监听显示器拓扑，窗口所在的那块屏消失时自动回到主屏居中。
    ///
    /// 两条硬性约束（曾导致启动闪退，改动时务必保持）：
    ///   A. 窗口过程里只调 Win32，不调 WinRT。WM_GETMINMAXINFO 在窗口创建/激活过程中会被反复
    ///      命中，此时重入 WinRT 窗口层会抛异常；而托管异常穿透 native 边界会变成
    ///      stowed exception（0xc000027b，崩在 CoreMessagingXP.dll）直接杀进程。
    ///   B. 所有回调（窗口过程、AppWindow.Changed、Dispatcher 队列）都必须自带 try/catch。
    ///      窗口装饰是「锦上添花」，任何失败都只该降级，不该让启动失败。
    ///
    /// 尺寸单位：Win32 的 WM_GETMINMAXINFO 与 AppWindow 的 MoveAndResize 都是物理像素，
    /// XAML 布局是 DIP。换算只发生在本类，上层不需要关心 DPI。
    /// </summary>
    internal sealed class WindowPlacement : IDisposable
    {
        // ==================== 逻辑尺寸（DIP）====================
        // 与显示器缩放无关：100% 下 1180×780 像素，200% 下 2360×1560 像素，视觉大小始终一致。
        private const int PreferredWidth = 1180;
        private const int PreferredHeight = 780;
        private const int MinimumWidth = 900;
        private const int MinimumHeight = 620;

        /// <summary>标题栏高度（DIP），与 MainWindow.xaml 首行 48 保持一致，用于判断标题栏是否可达。</summary>
        private const int TitleBarHeight = 48;

        /// <summary>窗口最多占工作区的比例：开窗即铺满整屏在观感上等同于最大化，这里留出余量。</summary>
        private const double WorkAreaRatio = 0.92;

        /// <summary>最小尺寸的绝对下限（物理像素），防止极端 DPI 下算出无法操作的小窗。</summary>
        private const int AbsoluteMinimumWidth = 320;
        private const int AbsoluteMinimumHeight = 240;

        // ==================== Win32 消息与常量 ====================
        private const uint WM_GETMINMAXINFO = 0x0024;
        private const uint WM_SYSCOMMAND = 0x0112;
        private const uint WM_DPICHANGED = 0x02E0;
        private const uint WM_DISPLAYCHANGE = 0x007E;
        private const uint WM_SETTINGCHANGE = 0x001A;
        private const int SC_MAXIMIZE = 0xF030;
        private const int SPI_SETWORKAREA = 0x002F;
        private const uint ScCommandMask = 0xFFF0;

        private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

        /// <summary>子类化标识，固定值即可，保证同一窗口只挂一次。</summary>
        private static readonly UIntPtr SubclassId = new(0xA57B);

        private readonly AppWindow _appWindow;
        private readonly IntPtr _hwnd;
        private readonly OverlappedPresenter? _presenter;
        private readonly DispatcherQueue? _dispatcher;

        /// <summary>必须持有委托实例，否则 GC 回收后窗口过程会调到已释放的桩上。</summary>
        private readonly SubclassProc _subclassProc;
        private readonly MonitorEnumProc _monitorEnumProc;

        /// <summary>EnumDisplayMonitors 的结果落点；仅 UI 线程使用。</summary>
        private List<RectInt32>? _enumTarget;

        private bool _syncQueued;
        private bool _disposed;

        public WindowPlacement(Window window)
        {
            _appWindow = window.AppWindow;
            _hwnd = WindowNative.GetWindowHandle(window);

            // 缓存 dispatcher：窗口过程里要投递任务时，避免再去碰 WinRT 属性
            _dispatcher = window.DispatcherQueue;
            _presenter = _appWindow.Presenter as OverlappedPresenter;

            _subclassProc = WndProc;
            _monitorEnumProc = OnMonitorEnum;

            TryConfigurePresenter();

            // 子类化要尽早：晚于首帧就可能漏掉早期的 WM_GETMINMAXINFO
            SetWindowSubclass(_hwnd, _subclassProc, SubclassId, UIntPtr.Zero);
            _appWindow.Changed += OnAppWindowChanged;

            TryApplyInitialPlacement();
        }

        /// <summary>把窗口放回可见区域：显示器拓扑变化、副屏断开、DPI 变化时调用（仅 UI 线程）。</summary>
        public void EnsureOnScreen()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var areas = CollectWorkAreas();
                if (areas.Count == 0)
                {
                    // 枚举失败时宁可不动，也不要凭猜测把窗口挪走
                    return;
                }

                var rect = WindowRect();

                // 1. 找窗口当前所在的显示器：取交集面积最大的那块
                RectInt32 host = default;
                long bestOverlap = 0;
                foreach (var area in areas)
                {
                    var hit = Intersect(rect, area);
                    long overlap = (long)hit.Width * hit.Height;
                    if (overlap > bestOverlap)
                    {
                        bestOverlap = overlap;
                        host = area;
                    }
                }

                // 2. 交集为 0：窗口所在的副屏已经断开（或坐标被系统留在了不存在的区域）→ 回主屏居中
                if (bestOverlap == 0)
                {
                    ApplyRect(CenterIn(rect, PrimaryWorkArea(areas)));
                    return;
                }

                // 3. 仍在同一块屏上：只处理「窗口比工作区还大」和「标题栏不可达」两种情况，
                //    用户故意把窗口拖出半边屏幕时不去纠正，避免变成和用户抢鼠标。
                var fitted = FitInside(rect, host);
                if (!fitted.Equals(rect))
                {
                    ApplyRect(fitted);
                }
            }
            catch (Exception ex)
            {
                LogOnce(ex);
            }
        }

        // ==================== 初始化 ====================

        /// <summary>
        /// 窗口外观约束：不可最大化，可最小化、可调整大小。
        /// 失败不致命 —— 即便这里抛了，窗口过程仍会拦掉 SC_MAXIMIZE。
        /// </summary>
        private void TryConfigurePresenter()
        {
            if (_presenter is null)
            {
                return;
            }

            try
            {
                _presenter.IsMaximizable = false;
                _presenter.IsMinimizable = true;
                _presenter.IsResizable = true;

                // 刻意不清空 PreferredMinimum*：该属性的单位（物理像素 / DIP）在 SDK 版本间不一致，
                // 但无论按哪种解释，它与 WM_GETMINMAXINFO 里的像素级约束取「较大者」
                // 都等于想要的 900×620 DIP，保留框架自己的下限更安全。
                _presenter.PreferredMinimumWidth = MinimumWidth;
                _presenter.PreferredMinimumHeight = MinimumHeight;
            }
            catch (Exception ex)
            {
                LogOnce(ex);
            }
        }

        /// <summary>
        /// 首屏定位：以当前显示器 DPI 把 DIP 尺寸换成像素，夹进工作区后居中。
        /// 失败时保持系统默认位置，不影响启动。
        /// </summary>
        private void TryApplyInitialPlacement()
        {
            try
            {
                if (_presenter is { State: OverlappedPresenterState.Maximized })
                {
                    _presenter.Restore();
                }

                if (!TryGetWorkArea(_hwnd, out var work))
                {
                    return;
                }

                double scale = CurrentScale();

                // 小屏 / 高缩放：默认尺寸与最小尺寸都不能超过工作区的 92%，
                // 否则 1366×768@150% 这类屏上「开窗即满屏」，观感上等同最大化。
                int capWidth = (int)Math.Round(work.Width * WorkAreaRatio);
                int capHeight = (int)Math.Round(work.Height * WorkAreaRatio);

                int width = Math.Clamp(
                    (int)Math.Round(PreferredWidth * scale),
                    Math.Min((int)Math.Round(MinimumWidth * scale), capWidth),
                    capWidth);
                int height = Math.Clamp(
                    (int)Math.Round(PreferredHeight * scale),
                    Math.Min((int)Math.Round(MinimumHeight * scale), capHeight),
                    capHeight);

                ApplyRect(CenterIn(new RectInt32(0, 0, width, height), work));
            }
            catch (Exception ex)
            {
                LogOnce(ex);
            }
        }

        // ==================== 事件 ====================

        private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                // 兜底：任何路径（系统快捷键、Shell 消息、外部工具）把窗口顶成最大化，立刻还原
                if (_presenter is not null && _presenter.State == OverlappedPresenterState.Maximized)
                {
                    _presenter.Restore();
                    return;
                }

                // 尺寸变化里也包含「跨屏后 DPI 变了，系统按新比例重算窗口」：
                // 新显示器的工作区装不下时收回来，避免窗口一半落在屏外。
                if (args.DidSizeChange)
                {
                    ShrinkToWorkArea();
                }
            }
            catch (Exception ex)
            {
                LogOnce(ex);
            }
        }

        /// <summary>窗口比当前显示器工作区还大时缩回工作区内（DPI 变化、分辨率变小都会触发）。</summary>
        private void ShrinkToWorkArea()
        {
            if (_disposed || !TryGetWorkArea(_hwnd, out var work))
            {
                return;
            }

            var rect = WindowRect();
            if (rect.Width <= work.Width && rect.Height <= work.Height)
            {
                return;
            }

            int width = Math.Min(rect.Width, work.Width);
            int height = Math.Min(rect.Height, work.Height);

            ApplyRect(new RectInt32(
                Math.Clamp(rect.X, work.X, work.X + Math.Max(0, work.Width - width)),
                Math.Clamp(rect.Y, work.Y, work.Y + Math.Max(0, work.Height - height)),
                width,
                height));
        }

        /// <summary>
        /// 把校验推迟到下一轮消息：绝不在窗口过程里同步挪窗。
        /// 拓扑变化往往连续抛出多条通知，用 _syncQueued 合并成一次。
        /// </summary>
        private void ScheduleSync()
        {
            if (_disposed || _syncQueued || _dispatcher is null)
            {
                return;
            }

            _syncQueued = true;
            if (!_dispatcher.TryEnqueue(() =>
            {
                _syncQueued = false;
                EnsureOnScreen();
            }))
            {
                _syncQueued = false;
            }
        }

        // ==================== 窗口过程 ====================

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
        {
            // 跨越 native 边界的回调，异常一旦逃出去就是 0xc000027b 直接杀进程。
            // 这里只做 Win32 判断 + 投递任务，不碰 WinRT。
            try
            {
                switch (msg)
                {
                    case WM_GETMINMAXINFO:
                        return HandleGetMinMaxInfo(hWnd, wParam, lParam);

                    case WM_SYSCOMMAND:
                        // 吞掉最大化命令：即便窗口样式里还残留 WS_MAXIMIZEBOX，
                        // 双击标题栏、Win+↑、拖到顶端都不会真的最大化。
                        if (((int)wParam & (int)ScCommandMask) == SC_MAXIMIZE)
                        {
                            return IntPtr.Zero;
                        }

                        break;

                    case WM_DPICHANGED:
                    case WM_DISPLAYCHANGE:
                        ScheduleSync();
                        break;

                    case WM_SETTINGCHANGE:
                        // SPI_SETWORKAREA：任务栏位置 / 工作区变化，窗口可能已经越界
                        if (wParam.ToInt32() == SPI_SETWORKAREA)
                        {
                            ScheduleSync();
                        }

                        break;
                }
            }
            catch (Exception ex)
            {
                LogOnce(ex);
            }

            return DefSubclassProc(hWnd, msg, wParam, lParam);
        }

        private IntPtr HandleGetMinMaxInfo(IntPtr hWnd, IntPtr wParam, IntPtr lParam)
        {
            // 先让默认处理填好 ptMaxSize / ptMaxPosition（WinUI 的自定义标题栏要靠它算最大化尺寸），
            // 再覆盖我们关心的两项，避免破坏框架自身的计算。
            var result = DefSubclassProc(hWnd, WM_GETMINMAXINFO, wParam, lParam);

            try
            {
                var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                var minimum = GetTrackMinimum(hWnd);
                info.MinTrackSize = new NativePoint { X = minimum.Width, Y = minimum.Height };

                if (TryGetWorkArea(hWnd, out var work))
                {
                    // 上限锁在工作区：窗口再大也不会盖住任务栏，也不会跨屏溢出
                    info.MaxTrackSize = new NativePoint { X = work.Width, Y = work.Height };
                }

                Marshal.StructureToPtr(info, lParam, false);
            }
            catch (Exception ex)
            {
                LogOnce(ex);
            }

            return result;
        }

        /// <summary>
        /// 最小尺寸（物理像素）：DIP 值 × 当前 DPI。
        /// 同时夹在 92% 工作区内 —— 小屏上必须允许窗口比「理想最小尺寸」更小，否则永远出界。
        /// </summary>
        private (int Width, int Height) GetTrackMinimum(IntPtr hWnd)
        {
            double scale = GetDpiForWindow(hWnd) / 96.0;
            int width = (int)Math.Round(MinimumWidth * scale);
            int height = (int)Math.Round(MinimumHeight * scale);

            if (TryGetWorkArea(hWnd, out var work))
            {
                width = Math.Min(width, (int)Math.Round(work.Width * WorkAreaRatio));
                height = Math.Min(height, (int)Math.Round(work.Height * WorkAreaRatio));
            }

            return (Math.Max(AbsoluteMinimumWidth, width), Math.Max(AbsoluteMinimumHeight, height));
        }

        // ==================== 几何工具 ====================

        /// <summary>当前显示器缩放系数（1.0 = 100%）。</summary>
        private double CurrentScale() => GetDpiForWindow(_hwnd) / 96.0;

        private RectInt32 WindowRect() =>
            new(_appWindow.Position.X, _appWindow.Position.Y, _appWindow.Size.Width, _appWindow.Size.Height);

        private void ApplyRect(RectInt32 rect)
        {
            if (_appWindow.Position.X == rect.X
                && _appWindow.Position.Y == rect.Y
                && _appWindow.Size.Width == rect.Width
                && _appWindow.Size.Height == rect.Height)
            {
                return;
            }

            _appWindow.MoveAndResize(rect);
        }

        /// <summary>枚举所有显示器的工作区（Win32，UI 线程调用）。</summary>
        private List<RectInt32> CollectWorkAreas()
        {
            var areas = new List<RectInt32>();
            _enumTarget = areas;
            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, _monitorEnumProc, IntPtr.Zero);
            }
            finally
            {
                _enumTarget = null;
            }

            return areas;
        }

        private bool OnMonitorEnum(IntPtr hMonitor, IntPtr hdc, ref NativeRect rect, IntPtr data)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                _enumTarget?.Add(info.Work.ToRect());
            }

            return true;
        }

        private static RectInt32 PrimaryWorkArea(List<RectInt32> fallback)
        {
            var primary = MonitorFromPoint(new NativePoint { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
            if (primary != IntPtr.Zero)
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(primary, ref info))
                {
                    return info.Work.ToRect();
                }
            }

            return fallback.Count > 0 ? fallback[0] : default;
        }

        /// <summary>取窗口所在显示器的工作区（物理像素）。</summary>
        private static bool TryGetWorkArea(IntPtr hWnd, out RectInt32 work)
        {
            work = default;

            var monitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
            {
                return false;
            }

            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info))
            {
                return false;
            }

            work = info.Work.ToRect();
            return true;
        }

        /// <summary>在工作区内居中，尺寸保持传入值（调用方已夹过）。</summary>
        private static RectInt32 CenterIn(RectInt32 rect, RectInt32 work)
        {
            if (work.Width <= 0 || work.Height <= 0)
            {
                return rect;
            }

            int width = Math.Min(rect.Width, work.Width);
            int height = Math.Min(rect.Height, work.Height);

            return new RectInt32(
                work.X + Math.Max(0, (work.Width - width) / 2),
                work.Y + Math.Max(0, (work.Height - height) / 2),
                width,
                height);
        }

        /// <summary>
        /// 把窗口收进工作区：先缩到能放下，再看标题栏是否可达，不可达才平移。
        /// 用户把窗口故意拖出半边的场景不纠正。
        /// </summary>
        private RectInt32 FitInside(RectInt32 rect, RectInt32 work)
        {
            int width = Math.Min(rect.Width, work.Width);
            int height = Math.Min(rect.Height, work.Height);

            if (IsTitleBarReachable(rect, work))
            {
                return new RectInt32(rect.X, rect.Y, width, height);
            }

            return new RectInt32(
                Math.Clamp(rect.X, work.X, work.X + Math.Max(0, work.Width - width)),
                Math.Clamp(rect.Y, work.Y, work.Y + Math.Max(0, work.Height - height)),
                width,
                height);
        }

        /// <summary>标题栏（顶部一条）是否与工作区有足够重叠 —— 只要有得拖，就不算丢失。</summary>
        private bool IsTitleBarReachable(RectInt32 rect, RectInt32 work)
        {
            double scale = CurrentScale();
            int stripHeight = Math.Min(rect.Height, (int)Math.Round(TitleBarHeight * scale));
            var strip = new RectInt32(rect.X, rect.Y, rect.Width, stripHeight);
            var hit = Intersect(strip, work);

            // 至少 64 DIP 宽、一半高度落在工作区内，才算「抓得住」
            return hit.Width >= (int)Math.Round(64 * scale) && hit.Height >= stripHeight / 2;
        }

        private static RectInt32 Intersect(RectInt32 a, RectInt32 b)
        {
            int left = Math.Max(a.X, b.X);
            int top = Math.Max(a.Y, b.Y);
            int right = Math.Min(a.X + a.Width, b.X + b.Width);
            int bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);

            return right <= left || bottom <= top
                ? default
                : new RectInt32(left, top, right - left, bottom - top);
        }

        // ==================== 诊断 ====================

        /// <summary>
        /// 兜底日志：本类所有失败都只降级不抛，但必须留下痕迹，
        /// 否则「窗口位置不对」会变成无从下手的悬案。写到 %Temp%/astrabrew-launcher/window.log。
        /// </summary>
        private static void LogOnce(Exception ex)
        {
            if (_logCount >= 8)
            {
                return;
            }

            _logCount++;
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "astrabrew-launcher");
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, "window.log"),
                    $"[{DateTimeOffset.Now:HH:mm:ss.fff}] {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}{Environment.NewLine}");
            }
            catch
            {
                // 日志写不进去也不能反过来影响窗口逻辑
            }
        }

        private static int _logCount;

        // ==================== 释放 ====================

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                _appWindow.Changed -= OnAppWindowChanged;
                RemoveWindowSubclass(_hwnd, _subclassProc, SubclassId);
            }
            catch (Exception ex)
            {
                LogOnce(ex);
            }
        }

        // ==================== P/Invoke ====================

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public readonly RectInt32 ToRect() => new(Left, Top, Right - Left, Bottom - Top);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MinMaxInfo
        {
            public NativePoint Reserved;
            public NativePoint MaxSize;
            public NativePoint MaxPosition;
            public NativePoint MinTrackSize;
            public NativePoint MaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        private delegate IntPtr SubclassProc(
            IntPtr hWnd,
            uint msg,
            IntPtr wParam,
            IntPtr lParam,
            UIntPtr id,
            UIntPtr data);

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref NativeRect rect, IntPtr data);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id, UIntPtr data);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(
            IntPtr hdc,
            IntPtr clip,
            MonitorEnumProc callback,
            IntPtr data);
    }
}
