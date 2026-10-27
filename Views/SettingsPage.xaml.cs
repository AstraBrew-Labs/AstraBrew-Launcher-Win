using AstraBrew_Launcher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AstraBrew_Launcher.Views
{
    /// <summary>设置：启动器自身的通用、外观与更新选项。</summary>
    public sealed partial class SettingsPage : Page
    {
        public SettingsPage()
        {
            InitializeComponent();

            // 让原型阶段能看到标记落在哪个文件里
            OnboardingStatePath.Text = OnboardingState.FilePath;
        }

        private void ReplayOnboarding_Click(object sender, RoutedEventArgs e)
        {
            OnboardingState.Reset();
            App.MainWindow?.ShowOnboarding();
        }
    }
}
