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
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Activate();
        }
    }
}
