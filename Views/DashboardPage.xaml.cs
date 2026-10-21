using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AstraBrew_Launcher.Views
{
    /// <summary>仪表盘：服务状态、运行指标与快捷入口。</summary>
    public sealed partial class DashboardPage : Page
    {
        public DashboardPage()
        {
            InitializeComponent();
        }

        private void OpenConsole_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            App.MainWindow?.NavigateTo("console");
        }
    }
}
