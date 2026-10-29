using System;
using System.IO;
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
            InitializeComponent();

            // WinUI 3 的启动期异常常常表现为「进程直接消失」，事件日志里只有 0xc000027b，
            // 看不到托管栈。这里兜底落一份日志，方便定位到底是哪一层抛的。
            UnhandledException += (_, e) => LogCrash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
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
