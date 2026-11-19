using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AstraBrew_Launcher.Services;
using Microsoft.UI.Xaml;

namespace AstraBrew_Launcher
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        /// <summary>全局主窗口引用，供页面内跳转使用。</summary>
        public static MainWindow? MainWindow { get; private set; }

        /// <summary>换语言时重建窗口用：把全局引用指向新窗口，页面里的 App.MainWindow 自动跟着换。</summary>
        internal static void SetMainWindow(MainWindow window) => MainWindow = window;

        public App()
        {
            // 设置必须在任何 UI 创建之前读进来：主题、字体族、缩放都要用在首帧上，
            // 否则会先闪一帧系统默认外观再跳到用户选的那套。
            SettingsStore.Load();

            // 语言要排在 InitializeComponent 之前：x:Uid 在元素创建的那一刻就解析掉了，
            // 晚一步设置，首帧就会闪一下旧语言。
            L.Initialize();

            InitializeComponent();

            ApplyStartupAppearance();

            // WinUI 3 的启动期异常常常表现为「进程直接消失」，事件日志里只有 0xc000027b，
            // 看不到托管栈。这里兜底落一份日志，方便定位到底是哪一层抛的。
            UnhandledException += (_, e) => LogCrash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
        }

        /// <summary>
        /// 首帧主题。运行期切换主题走窗口内容根的 RequestedTheme（见 AppearanceController），
        /// 这里设应用级主题只是为了窗口材质（Mica）与系统绘制部分一开始就用对底色。
        ///
        /// 注意：字体不在这里改。Application.Resources 在窗口创建之前写入
        /// ContentControlThemeFontFamily 会让启动直接 fail-fast（实测无托管异常、无日志），
        /// 字体统一交给 AppearanceController 在窗口就位后处理。
        /// </summary>
        private void ApplyStartupAppearance()
        {
            try
            {
                if (LauncherSettings.Theme != AppTheme.System)
                {
                    RequestedTheme = LauncherSettings.Theme == AppTheme.Dark
                        ? ApplicationTheme.Dark
                        : ApplicationTheme.Light;
                }
            }
            catch (Exception)
            {
                // 应用级主题已经锁定（窗口创建之后就不能再改）：交给元素级主题兜底
            }
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            // 浏览器点 launcher-astrabrew:// 时，Windows 会把链接当命令行参数塞进来
            string? deepLink = UriScheme.FindInArgs(Environment.GetCommandLineArgs());

            // 免安装版没有安装程序，协议注册靠这里每次启动自查一遍（已安装版由 MSI 负责）
            UriScheme.EnsureRegistered();

            // 开机自启动：设置里开着就把 Run 项指到当前 exe（便携版换了目录也不会失效），
            // 关着则清掉残留。这一条必须在 UI 起来之前做，用户一开设置页看到的就是真实状态。
            StartupRegistration.EnsureMatchesSetting();

            // 带链接启动、而已经有一个实例在跑：把链接递过去，本进程直接退场——
            // 绝不能让浏览器点一下链接就多出一个窗口。
            if (!SingleInstance.TryAcquire() &&
                deepLink is { Length: > 0 } &&
                SingleInstance.TrySend(deepLink))
            {
                Environment.Exit(0);
                return;
            }

            var window = new MainWindow();
            MainWindow = window;
            window.Activate();

            // 主实例蹲在管道上收别人递来的链接；回调在后台线程，切回 UI 线程再动视觉树
            SingleInstance.Listen(message =>
                window.DispatcherQueue.TryEnqueue(() => window.HandleDeepLink(message)));

            if (deepLink is { Length: > 0 })
            {
                window.HandleDeepLink(deepLink);
            }

            // 窗口已经在屏幕上了，再建第一页：页面解析（仪表盘是全应用最重的一页）
            // 放到这之后，用户看到的是「窗口 + 加载遮罩」，而不是干等一秒才出窗口。
            window.StartShell();

            // 启动后的静默更新检查：不挡首屏，也不弹窗，有新版才在右下角冒一条
            _ = CheckUpdatesInBackgroundAsync();
        }

        /// <summary>
        /// 启动后延迟查一次更新。走的是带缓存的那条路（6 小时内直接读上次结论），
        /// 所以天天开也不会把 GitHub 的匿名配额吃光；有新版时只发一条可点击的通知，
        /// 装不装、什么时候装交给用户，绝不在启动阶段偷偷替换文件。
        /// </summary>
        private static async Task CheckUpdatesInBackgroundAsync()
        {
            try
            {
                // 上一次更新留在 update\ 里的解压目录，这时候已经没用了
                LauncherUpdateService.CleanupStaging();

                // 开发版不自动检查：跑的就是最新代码，没必要每次启动打一次接口
                if (!LauncherUpdateService.AutoCheckEnabled)
                {
                    return;
                }

                // 让主界面、实例扫描这些真正要紧的活儿先跑完
                await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);

                UpdateCheckResult result = await LauncherUpdateService
                    .CheckAsync(force: false, CancellationToken.None)
                    .ConfigureAwait(false);

                if (result.State != UpdateStatus.Available || result.Latest is null)
                {
                    return;
                }

                string latest = result.Latest.DisplayVersion;
                AppNotify.Info(
                    L.F("Update.Notify.Title", latest),
                    L.F("Update.Notify.Message", BuildChannelInfo.DisplayVersion, latest),
                    L.Get("Update.Notify.Action"),
                    () => MainWindow?.NavigateTo("about"));
            }
            catch (Exception)
            {
                // 网络抖动、JSON 变形之类一律吞掉：启动阶段的更新检查不该留下任何噪音
            }
        }

        /// <summary>把未处理异常写到 %Temp%/astrabrew-launcher/crash.log（尽力而为，绝不二次抛）。</summary>
        private static void LogCrash(Exception? ex)
        {
            if (ex is null)
            {
                return;
            }

            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "astrabrew-launcher");
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, "crash.log"),
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}{Environment.NewLine}");
            }
            catch
            {
                // 记录失败不能反过来影响退出流程
            }
        }
    }
}
