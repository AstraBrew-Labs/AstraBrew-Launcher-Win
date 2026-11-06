using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 桌面模式：用一个独立窗口内嵌 WebView2 打开酒馆，像桌面应用一样用。
    ///
    /// 与普通模式的区别只在「页面开在哪」：普通模式交给系统浏览器，桌面模式由这里接管。
    /// 对齐旧版 <c>desktop_webview.rs</c> 的几件事：
    ///
    /// · **保真度**：保留右键菜单、缩放、开发者工具（Debug 构建），不做「精简浏览器」；
    /// · **导出不丢**：blob / 大 data URL 下载改走 IPC，由原生层写进「导出目录」，
    ///   并给文件名加后缀避免覆盖已有文件；普通下载同样落在这个目录；
    /// · **文件选择不被拦**：酒馆给 <c>&lt;input type="file"&gt;</c> 设了 accept，
    ///   Windows 文件框会按它过滤，角色卡（.png/.json）常被挡住，这里在选完后兜一层提示；
    /// · **坏了要能恢复**：渲染进程崩溃、导航失败都给出可重试的提示，而不是停在白屏。
    ///
    /// 与旧版的差别：这里不需要自建 Win32 消息循环——WinUI 3 的 <see cref="Window"/>
    /// 本来就是自己的线程/窗口，WebView2 控件直接挂进视觉树即可。
    /// </summary>
    public static class DesktopWindow
    {
        /// <summary>JS 侧投递导出数据用的 IPC 频道名。</summary>
        private const string DownloadChannel = "fileDownloader";

        /// <summary>超过这个体积的 blob 不走 base64，避免一次性占用过多内存。</summary>
        private const long MaxInlineBytes = 64L * 1024 * 1024;

        /// <summary>还原窗口时的最小尺寸（物理像素）：比这更小的记录多半是异常状态，按最小值开。</summary>
        private const int MinWidth = 560;

        private const int MinHeight = 400;

        private static Window? _window;
        private static WebView2? _view;
        private static string _url = string.Empty;
        private static bool _ready;
        private static bool _closing;
        private static int _retries;

        /// <summary>桌面窗口是否已经开着（用于「打开酒馆」时决定是新建还是唤到前台）。</summary>
        public static bool IsOpen => _window is not null;

        /// <summary>
        /// 窗口开了 / 关了。仪表盘与控制台据此更新「打开酒馆」上那句说明
        /// （「窗口已打开 · 点击唤到前台」/「窗口已关闭 · 点击重新打开」）。
        /// 一律在 UI 线程触发。
        /// </summary>
        public static event EventHandler? Changed;

        /// <summary>
        /// 打开（或唤到前台）桌面窗口。
        ///
        /// 同步返回是否已接管；WebView2 的初始化是异步的，失败会以全局消息告知。
        /// </summary>
        public static bool Open(string url, out string detail)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                detail = "还没有可打开的地址";
                return false;
            }

            if (_window is not null)
            {
                // 已经在跑：只切地址 + 唤到前台，绝不重复开窗（同一个酒馆开两个窗口只会互相抢登录态）
                if (!string.Equals(_url, url, StringComparison.OrdinalIgnoreCase))
                {
                    _url = url;

                    if (_ready)
                    {
                        _view!.CoreWebView2?.Navigate(url);
                    }
                }

                BringToFront();
                detail = "桌面窗口";
                return true;
            }

            try
            {
                CreateWindow(url);
                detail = "桌面窗口";
                return true;
            }
            catch (Exception ex)
            {
                detail = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 按当前启动模式打开酒馆：桌面模式走内嵌窗口，其余交给系统浏览器。
        ///
        /// 桌面窗口打不开（比如机器上没装 WebView2 运行时）时退回浏览器，
        /// 而不是留一个白窗口让用户以为酒馆坏了。
        /// </summary>
        public static bool OpenTavern(string url, out string detail)
        {
            if (TavernSettings.Mode == LaunchMode.Desktop)
            {
                if (Open(url, out string desktopDetail))
                {
                    detail = desktopDetail;
                    return true;
                }

                AppNotify.Warning("桌面窗口没能打开，改用浏览器", desktopDetail);
            }

            return BrowserLauncher.Open(url, TavernSettings.Browser, out detail);
        }

        /// <summary>把窗口唤到前台；最小化时先还原。</summary>
        public static void BringToFront()
        {
            if (_window is null)
            {
                return;
            }

            try
            {
                if (_window.AppWindow.Presenter is OverlappedPresenter presenter
                    && presenter.State == OverlappedPresenterState.Minimized)
                {
                    presenter.Restore();
                }

                _window.AppWindow.Show();
                _window.Activate();
            }
            catch (Exception)
            {
                // 窗口正在关闭：忽略，下一次打开会重新建
            }
        }

        /// <summary>重新加载页面（导航失败时的「重试」用）。</summary>
        public static void Reload()
        {
            _retries = 0;

            try
            {
                _view?.CoreWebView2?.Reload();
            }
            catch (Exception)
            {
                // 还没初始化完：初始化流程本身会导航
            }
        }

        /// <summary>关闭桌面窗口。酒馆进程退出、启动器退出时调用。</summary>
        public static void Close()
        {
            Window? window = _window;
            WebView2? view = _view;

            if (window is null || _closing)
            {
                return;
            }

            _closing = true;

            // 先记位置，再清引用：清完就读不到真实坐标了
            SaveBounds(window);

            // 先把引用清空，避免 window.Close() 触发的 Closed 回调又走一遍收尾
            _window = null;
            _view = null;
            _ready = false;
            _url = string.Empty;

            try
            {
                view?.Close();
            }
            catch (Exception)
            {
                // 控件已经不可用，继续关窗口
            }

            try
            {
                window.Close();
            }
            catch (Exception)
            {
                // 窗口已经关掉了：没有后续动作
            }

            _closing = false;
            RaiseChanged();
        }

        /// <summary>通知订阅者窗口状态变了；订阅方出错不该把建窗 / 关窗流程带崩。</summary>
        private static void RaiseChanged()
        {
            try
            {
                Changed?.Invoke(null, EventArgs.Empty);
            }
            catch (Exception)
            {
                // 只是刷新界面文案，失败无所谓
            }
        }

        // ==================== 建窗与初始化 ====================

        private static void CreateWindow(string url)
        {
            _url = url;
            _retries = 0;

            var view = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };

            var root = new Grid
            {
                Background = new SolidColorBrush(Colors.Black),
            };
            root.Children.Add(view);

            var window = new Window
            {
                Title = "酒馆 · AstraBrew",
                Content = root,
            };

            _window = window;
            _view = view;
            _ready = false;

            window.Closed += (_, _) => OnWindowClosed();

            PlaceWindow(window);
            ApplyIcon(window);

            window.Activate();

            RaiseChanged();

            _ = InitializeAsync(view, url);
        }

        /// <summary>
        /// 摆放窗口：开了「记住酒馆窗口的位置」就回到上次关闭前的位置，否则 1600×900 居中。
        /// 1600×900 是酒馆推荐的两栏布局宽度（手机端预览 + 聊天），比 1280×720 少折行；
        /// 小屏上按工作区收窄，不会超出屏幕。
        /// </summary>
        private static void PlaceWindow(Window window)
        {
            try
            {
                AppWindow appWindow = window.AppWindow;

                if (LauncherSettings.RememberTavernWindow && LauncherSettings.TavernWindowRect is { } saved)
                {
                    appWindow.MoveAndResize(ClampToWorkArea(saved));
                    return;
                }

                DisplayArea area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
                RectInt32 work = area.WorkArea;

                int width = Math.Min(1600, work.Width - 80);
                int height = Math.Min(900, work.Height - 80);
                int x = work.X + (work.Width - width) / 2;
                int y = work.Y + (work.Height - height) / 2;

                appWindow.MoveAndResize(new RectInt32(x, y, width, height));
            }
            catch (Exception)
            {
                // 拿不到显示器信息时保持系统默认尺寸，不影响打开
            }
        }

        /// <summary>
        /// 把记下来的矩形夹进某块显示器的工作区。
        ///
        /// 坐标是「上次那台机器 / 那次分辨率」的事实：用户可能拔了副屏、改了缩放，
        /// 也可能把窗口拖到一块现在已经不存在的屏上。这里取左上角最近的那块屏，
        /// 尺寸按工作区收窄、位置再夹一次——标题栏一定落在屏内，否则窗口没法再拖回来。
        /// </summary>
        private static RectInt32 ClampToWorkArea(TavernWindowRect saved)
        {
            var anchor = new PointInt32(saved.X + 40, saved.Y + 20);
            DisplayArea area = DisplayArea.GetFromPoint(anchor, DisplayAreaFallback.Nearest);
            RectInt32 work = area.WorkArea;

            int width = Math.Clamp(saved.Width, Math.Min(MinWidth, work.Width), work.Width);
            int height = Math.Clamp(saved.Height, Math.Min(MinHeight, work.Height), work.Height);

            int x = Math.Clamp(saved.X, work.X, work.X + work.Width - width);
            int y = Math.Clamp(saved.Y, work.Y, work.Y + work.Height - height);

            return new RectInt32(x, y, width, height);
        }

        /// <summary>
        /// 记下窗口此刻的位置与大小。必须在把引用清空、窗口真正销毁之前调用，
        /// 否则读到的是全 0/哨兵值。最小化时的坐标是 -32000 那种哨兵值，存下去下次会开到屏幕外。
        /// </summary>
        private static void SaveBounds(Window? window)
        {
            if (window is null || !LauncherSettings.RememberTavernWindow)
            {
                return;
            }

            try
            {
                if (window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
                {
                    return;
                }

                PointInt32 position = window.AppWindow.Position;
                SizeInt32 size = window.AppWindow.Size;

                // 尺寸小到不成样子说明窗口正在被拆：宁可保留上一次的记录
                if (size.Width < MinWidth / 2 || size.Height < MinHeight / 2)
                {
                    return;
                }

                LauncherSettings.SetTavernWindowRect(
                    new TavernWindowRect(position.X, position.Y, size.Width, size.Height));
            }
            catch (Exception)
            {
                // 关窗途中的窗口状态本来就不可靠：记不下来就下次居中开
            }
        }

        /// <summary>
        /// 窗口图标候选，按顺序试第一个存在的：酒馆图标 → 启动器图标。
        ///
        /// 必须给 .ico：`AppWindow.SetIcon` 收下 PNG 路径时**不会报错**，但窗口拿到的是一个
        /// 空白图标（实测标题栏只剩系统默认的页面图标），所以别图省事直接指 PNG。
        /// `Assets\Sillytavern.ico` 由同目录的 `Sillytavern.png` 生成（16/24/32/48/64/128/256
        /// 七档），换掉 PNG 之后要一起重新生成，否则窗口图标还是旧的那张。
        /// </summary>
        private static readonly string[] IconFiles = { "Sillytavern.ico", "app.ico" };

        private static void ApplyIcon(Window window)
        {
            foreach (string name in IconFiles)
            {
                string path = Path.Combine(AppContext.BaseDirectory, "Assets", name);

                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    window.AppWindow.SetIcon(path);
                    return;
                }
                catch (Exception)
                {
                    // 这个格式吃不下就试下一个：图标只是外观，不该影响开窗
                }
            }
        }

        private static async Task InitializeAsync(WebView2 view, string url)
        {
            try
            {
                // 用户数据目录必须可写：非打包应用默认会落到 exe 旁边，
                // 装在 Program Files 下时那是一次「只读目录写入失败」，窗口会一直白着
                string userData = Path.Combine(AppPaths.LocalRoot, "WebView2");
                Directory.CreateDirectory(userData);

                CoreWebView2Environment environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                    null,
                    userData,
                    new CoreWebView2EnvironmentOptions
                    {
                        // Chromium 没有「整体内存上限」这一个开关，能约束的是 V8 堆：
                        // 到这个量先强制回收，真撑不住时由渲染进程自己崩掉重载，
                        // 而不是让整台机器被拖进交换分区。取值由设置页夹在 1 GB ~ 物理内存-预留 之间。
                        AdditionalBrowserArguments =
                            $"--js-flags=--max-old-space-size={LauncherSettings.WebViewMemoryMb}",
                    });
                await view.EnsureCoreWebView2Async(environment);

                if (_view != view)
                {
                    // 初始化期间窗口已被关掉
                    return;
                }

                CoreWebView2 core = view.CoreWebView2;
                Configure(core);

                _ready = true;

                // 初始化期间可能已经换了地址（比如重启酒馆换了端口）
                string target = string.IsNullOrEmpty(_url) ? url : _url;
                core.Navigate(target);

                await core.AddScriptToExecuteOnDocumentCreatedAsync(BlobPatchScript());
                await core.AddScriptToExecuteOnDocumentCreatedAsync(FileInputFilterScript());
            }
            catch (Exception ex)
            {
                AppNotify.Error(
                    "桌面窗口打不开",
                    $"WebView2 初始化失败：{ex.Message}。可以改用普通模式（系统浏览器）打开。");

                Close();
            }
        }

        private static void Configure(CoreWebView2 core)
        {
            // 桌面模式按浏览器语义来：右键菜单、缩放、快捷键都保留
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = true;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;

#if DEBUG
            core.Settings.AreDevToolsEnabled = true;
#endif

            core.DocumentTitleChanged += (_, _) =>
            {
                string title = core.DocumentTitle;
                if (_window is not null && !string.IsNullOrWhiteSpace(title))
                {
                    _window.Title = title;
                }
            };

            core.NavigationCompleted += OnNavigationCompleted;

            core.DownloadStarting += OnDownloadStarting;

            core.WebMessageReceived += OnWebMessageReceived;

            core.ProcessFailed += (_, args) =>
            {
                // 浏览器进程退出是致命的：渲染进程、GPU 进程挂了 WebView2 会自己恢复，
                // 只有这一种需要重开页面
                if (args.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
                {
                    AppNotify.Warning(
                        "桌面窗口的浏览器进程已退出",
                        "页面需要重新加载。",
                        "重新加载",
                        Reload);
                }
            };
        }

        // ==================== 导航失败 ====================

        private static void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.IsSuccess)
            {
                _retries = 0;
                return;
            }

            // 酒馆刚启动时端口可能还没真正接客：自动重试一次，别立刻报错
            if (_retries < 1)
            {
                _retries++;
                _ = Task.Delay(1200).ContinueWith(_ =>
                {
                    if (_view is not null && _ready)
                    {
                        _view.DispatcherQueue.TryEnqueue(() => _view.CoreWebView2?.Reload());
                    }
                });
                return;
            }

            AppNotify.Warning(
                "页面没能加载",
                $"{_url} · {DescribeError(args.WebErrorStatus)}",
                "重新加载",
                Reload);
        }

        private static string DescribeError(CoreWebView2WebErrorStatus status) => status switch
        {
            CoreWebView2WebErrorStatus.CannotConnect => "连不上酒馆（可能还在启动或已停止）",
            CoreWebView2WebErrorStatus.ConnectionAborted => "连接被中断",
            CoreWebView2WebErrorStatus.ConnectionReset => "连接被重置",
            CoreWebView2WebErrorStatus.HostNameNotResolved => "域名解析失败",
            CoreWebView2WebErrorStatus.ServerUnreachable => "服务不可达",
            CoreWebView2WebErrorStatus.Timeout => "连接超时",
            _ => status.ToString(),
        };

        // ==================== 下载 ====================

        /// <summary>
        /// 文件下载：一律落到「导出目录」，同名加后缀。
        ///
        /// 不接管的话 WebView2 会自己弹一个保存框，路径由用户临时决定；
        /// 旧版把这里统一到设置的导出目录，导出的卡和图才好找。
        /// </summary>
        private static void OnDownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args)
        {
            string directory = ExportDirectory();

            try
            {
                Directory.CreateDirectory(directory);

                string name = System.IO.Path.GetFileName(args.ResultFilePath);
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = "download";
                }

                string target = UniquePath(directory, name);
                args.ResultFilePath = target;
                args.Handled = true;

                CoreWebView2DownloadOperation operation = args.DownloadOperation;

                operation.StateChanged += (_, _) =>
                {
                    if (operation.State == CoreWebView2DownloadState.Completed)
                    {
                        AppNotify.Success("已保存到导出目录", target, "打开所在文件夹", () => Reveal(target));
                    }
                    else if (operation.State == CoreWebView2DownloadState.Interrupted)
                    {
                        AppNotify.Warning(
                            "下载中断",
                            $"{name} · {operation.InterruptReason}",
                            "重试",
                            () => _view?.CoreWebView2?.Reload());
                    }
                };
            }
            catch (Exception ex)
            {
                // 目录不可写时别把下载吞掉：交回 WebView2 默认行为，至少能存下来
                args.Handled = false;
                AppNotify.Warning("导出目录不可用", $"{directory} · {ex.Message}");
            }
        }

        /// <summary>处理页面里 blob / 大 data URL 的导出：base64 传过来，这里落盘。</summary>
        private static void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            string raw;

            try
            {
                raw = args.TryGetWebMessageAsString();
            }
            catch (Exception)
            {
                // 结构化消息（非字符串）不是我们注入的脚本发的，忽略
                return;
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(raw);
                JsonElement root = document.RootElement;

                if (!root.TryGetProperty("channel", out JsonElement channel)
                    || channel.GetString() != DownloadChannel)
                {
                    return;
                }

                string name = root.TryGetProperty("name", out JsonElement nameElement)
                    ? nameElement.GetString() ?? "download"
                    : "download";

                string data = root.TryGetProperty("data", out JsonElement dataElement)
                    ? dataElement.GetString() ?? string.Empty
                    : string.Empty;

                if (name == "__error__")
                {
                    AppNotify.Warning("导出失败", string.IsNullOrEmpty(data) ? "页面没能读出内容" : data);
                    return;
                }

                if (data.Length == 0)
                {
                    AppNotify.Warning("导出失败", "页面返回了空内容");
                    return;
                }

                byte[] bytes = Convert.FromBase64String(data);
                string target = UniquePath(ExportDirectory(), Sanitize(name));
                File.WriteAllBytes(target, bytes);

                AppNotify.Success("已保存到导出目录", target, "打开所在文件夹", () => Reveal(target));
            }
            catch (FormatException)
            {
                AppNotify.Warning("导出失败", "页面返回的内容不是有效的 base64");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                AppNotify.Warning("导出失败", ex.Message);
            }
        }

        // ==================== 路径工具 ====================

        /// <summary>导出目录：设置为空或解析不出来时退回「下载」文件夹。</summary>
        private static string ExportDirectory()
        {
            string configured = AppPaths.Expand(LauncherSettings.ExportPath ?? string.Empty).Trim();

            if (configured.Length == 0)
            {
                return DownloadsDirectory();
            }

            try
            {
                return System.IO.Path.GetFullPath(configured);
            }
            catch (Exception)
            {
                return DownloadsDirectory();
            }
        }

        /// <summary>系统「下载」文件夹；取不到就退回程序本地目录，至少是可写的。</summary>
        private static string DownloadsDirectory()
        {
            try
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                if (profile.Length > 0)
                {
                    string downloads = System.IO.Path.Combine(profile, "Downloads");

                    if (Directory.Exists(downloads))
                    {
                        return downloads;
                    }
                }
            }
            catch (Exception)
            {
            }

            return AppPaths.LocalRoot;
        }

        /// <summary>同名文件加 <c>_1</c> 后缀，绝不覆盖用户已有的导出。</summary>
        private static string UniquePath(string directory, string fileName)
        {
            string safe = Sanitize(fileName);
            string candidate = System.IO.Path.Combine(directory, safe);

            if (!File.Exists(candidate))
            {
                return candidate;
            }

            string stem = System.IO.Path.GetFileNameWithoutExtension(safe);
            string extension = System.IO.Path.GetExtension(safe);

            for (int index = 1; index < 1000; index++)
            {
                candidate = System.IO.Path.Combine(directory, $"{stem}_{index}{extension}");

                if (!File.Exists(candidate))
                {
                    break;
                }
            }

            return candidate;
        }

        /// <summary>去掉路径分隔符等非法字符：名字来自页面，不能直接当路径用。</summary>
        private static string Sanitize(string name)
        {
            foreach (char invalid in System.IO.Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, '_');
            }

            name = name.Trim().TrimEnd('.');

            return name.Length == 0 ? "download" : name;
        }

        private static void Reveal(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception)
            {
                // 只是「方便一下」，打不开不影响文件已经保存
            }
        }

        private static void OnWindowClosed()
        {
            bool wasClosing = _closing;

            // 用户点右上角关的：这一刻窗口还没销毁，坐标仍然有效
            if (!wasClosing)
            {
                SaveBounds(_window);
            }

            _view?.Close();
            _view = null;
            _window = null;
            _ready = false;
            _url = string.Empty;

            if (!wasClosing)
            {
                // 用户自己关的窗口：按「关闭窗口时停止酒馆」决定是收工还是留后台。
                // 这里必须是**桌面窗口**的关闭，不是启动器的——以前只挂在主窗口关闭上，
                // 于是设置里明明开着，关掉酒馆窗口后进程还在后台占着端口。
                if (LauncherSettings.StopTavernOnWindowClose)
                {
                    if (TavernRuntime.State.HasProcess)
                    {
                        _ = TavernRuntime.StopAsync();

                        AppNotify.Info(
                            "桌面窗口已关闭",
                            "正在停止酒馆服务。想让窗口关掉后继续挂后台，可以在设置里关掉「关闭窗口时停止酒馆」。");
                    }
                }
                else if (TavernRuntime.IsRunning)
                {
                    AppNotify.Info(
                        "桌面窗口已关闭",
                        "酒馆仍在运行，可以从仪表盘或控制台点「访问酒馆」再打开窗口。",
                        "重新打开",
                        () => Open(TavernRuntime.State.Url, out _));
                }
            }

            if (!wasClosing)
            {
                // Close() 自己会发一次；用户手动关窗只有这里能发
                RaiseChanged();
            }
        }

        // ==================== 注入脚本 ====================

        /// <summary>
        /// 拦截 blob / 大体积 data URL 下载，改走 IPC 保存。
        /// WebView2 对 <c>a[download]</c> 指向 blob 的处理常常是静默失败或存成无意义文件名。
        /// </summary>
        private static string BlobPatchScript() => $$"""
            (function(){
            var MAX={{MaxInlineBytes}};
            function isDownload(url){
              if(!url){return false}
              url=String(url);
              if(url.indexOf('blob:')===0){return true}
              if(url.indexOf('data:')===0&&url.length>2000){return true}
              return false;
            }
            function post(name,data){
              try{window.chrome.webview.postMessage(JSON.stringify({channel:'{{DownloadChannel}}',name:name,data:data}))}catch(e){}
            }
            function save(url,name){
              try{
                fetch(url).then(function(r){return r.blob()}).then(function(b){
                  if(b.size>MAX){throw new Error('文件超过 64 MB，浏览器无法一次性导出')}
                  var reader=new FileReader();
                  reader.onload=function(){
                    var text=String(reader.result);
                    var comma=text.indexOf(',');
                    post(name||'download',comma>=0?text.slice(comma+1):text);
                  };
                  reader.readAsDataURL(b);
                }).catch(function(e){post('__error__',String(e))});
              }catch(e){post('__error__',String(e))}
            }
            document.addEventListener('click',function(e){
              var target=e.target;
              var link=(target&&target.closest)?target.closest('a[download]'):null;
              if(!link){return}
              var href=link.getAttribute('href')||'';
              if(!isDownload(href)){return}
              e.preventDefault();
              e.stopPropagation();
              save(href,link.getAttribute('download')||'download');
            },true);
            var open=window.open;
            window.open=function(url){
              if(url&&isDownload(url)){save(url,'download');return null}
              return open.apply(window,arguments)
            };
            })()
            """;

        /// <summary>
        /// 选完文件再检查一次扩展名。
        ///
        /// 酒馆给导入框设了 accept（角色卡允许 png/json），但 Windows 文件框的过滤有时
        /// 会把 <c>.json</c> 卡掉，用户选了却在页面里被静默拒绝。这里在 change 之后
        /// 按「角色卡 → .png/.json；其余 → .json」兜一层说明，避免「选了没反应」。
        /// </summary>
        private static string FileInputFilterScript() => """
            (function(){
            function allowed(input){
              var accept=(input.getAttribute('accept')||'').toLowerCase();
              if(accept.indexOf('png')>=0||accept.indexOf('image/')>=0){return '.png,.json'}
              if(accept.indexOf('json')>=0){return '.json'}
              return accept;
            }
            document.addEventListener('change',function(e){
              var input=e.target;
              if(!input||input.tagName!=='INPUT'||(input.type||'').toLowerCase()!=='file'){return}
              if(!input.files||!input.files.length){return}
              var accept=allowed(input);
              if(!accept){return}
              var extensions=[],any=false;
              accept.split(',').forEach(function(part){
                part=part.trim().toLowerCase();
                if(!part){return}
                if(part.charAt(0)==='.'){extensions.push(part.slice(1))}
                else if(part==='*/*'||part==='*'||part.indexOf('/*')>=0){any=true}
              });
              if(any||!extensions.length){return}
              var bad=[];
              for(var i=0;i<input.files.length;i++){
                var file=input.files[i];
                var name=(file.name||'').toLowerCase();
                var ok=extensions.some(function(ext){return name.lastIndexOf('.'+ext)===name.length-ext.length-1});
                if(!ok){bad.push(file.name)}
              }
              if(bad.length){
                input.value='';
                alert('以下文件的类型不在允许范围内：\n'+bad.join('\n')+'\n\n允许的类型：'+accept);
              }
            },true)
            })()
            """;
    }
}
