using System;
using System.IO;
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

        public App()
        {
            // 设置必须在任何 UI 创建之前读进来：主题、字体族、缩放都要用在首帧上，
            // 否则会先闪一帧系统默认外观再跳到用户选的那套。
            SettingsStore.Load();

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
            var window = new MainWindow();
            MainWindow = window;
            window.Activate();
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
