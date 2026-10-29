using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace AstraBrew_Launcher.Views
{
    /// <summary>
    /// 关于：品牌主卡 + 版本与构建、运行环境、安装位置、链接与支持、开源许可。
    /// 版本号、系统、架构、目录都取真机值，只有产品自身的版本/构建号是演示数据。
    /// </summary>
    public sealed partial class AboutPage : Page
    {
        private string _appDir = string.Empty;
        private string _dataDir = string.Empty;
        private string _logDir = string.Empty;
        private bool _copying;

        public AboutPage()
        {
            InitializeComponent();
            Loaded += (_, _) => FillEnvironment();
        }

        private void FillEnvironment()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version ?? new Version(1, 0, 0);
            VersionValue.Text = $"v{version.Major}.{version.Minor}.{version.Build}";

            // 构建号取 exe 的写入时间，重新构建后自动跟着变
            string exePath = Environment.ProcessPath ?? string.Empty;
            if (exePath.Length > 0 && File.Exists(exePath))
            {
                var built = File.GetLastWriteTime(exePath);
                BuildValue.Text = $"{built:yyyy-MM-dd} · #{built:yyMMdd}.1";
            }

            RuntimeValue.Text = RuntimeInformation.FrameworkDescription;

            var os = Environment.OSVersion.Version;
            OsValue.Text = $"{OsName(os.Build)}（内部版本 {os.Build}.{os.Revision}）";

            string arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.X86 => "x86",
                Architecture.Arm64 => "arm64",
                _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            };
            ArchValue.Text = arch;
            ArchChipText.Text = arch;

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            _appDir = localAppData.Length > 0
                ? Path.Combine(localAppData, "AstraBrew Launcher")
                : AppContext.BaseDirectory;
            _dataDir = roaming.Length > 0
                ? Path.Combine(roaming, "AstraBrew Launcher")
                : AppContext.BaseDirectory;
            _logDir = Path.Combine(_dataDir, "logs");

            AppDirValue.Text = _appDir;
            DataDirValue.Text = _dataDir;
            LogDirValue.Text = _logDir;
        }

        private static string OsName(int build) => build switch
        {
            >= 26100 => "Windows 11 24H2",
            >= 22631 => "Windows 11 23H2",
            >= 22621 => "Windows 11 22H2",
            >= 22000 => "Windows 11 21H2",
            >= 19045 => "Windows 10 22H2",
            >= 19044 => "Windows 10 21H2",
            _ => $"Windows {Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}",
        };

        // ==================== 交互 ====================

        private async void CopyVersionInfo_Click(object sender, RoutedEventArgs e)
        {
            if (_copying)
            {
                return;
            }

            _copying = true;

            var text = new StringBuilder();
            text.AppendLine("AstraBrew Launcher 星酿启动器");
            text.AppendLine($"版本       {VersionValue.Text}");
            text.AppendLine($"构建       {BuildValue.Text}");
            text.AppendLine($"运行时     {RuntimeValue.Text}");
            text.AppendLine($"操作系统   {OsValue.Text}");
            text.AppendLine($"进程架构   {ArchValue.Text}");
            text.AppendLine($"数据目录   {_dataDir}");

            try
            {
                var package = new DataPackage();
                package.SetText(text.ToString());
                Clipboard.SetContent(package);

                CopyIcon.Glyph = "\uE8FB";
                CopyLabel.Text = "已复制";
                await Task.Delay(1600);
            }
            catch
            {
                // 剪贴板被别的进程占用时静默跳过，不打断演示
            }

            CopyIcon.Glyph = "\uE8C8";
            CopyLabel.Text = "复制版本信息";
            _copying = false;
        }

        private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            CheckUpdateButton.IsEnabled = false;
            CheckUpdateText.Text = "检查中…";
            LastCheckText.Text = "正在连接更新服务…";

            await Task.Delay(1200);
            CheckUpdateText.Text = "已是最新";
            LastCheckText.Text = "刚刚检查 · 已是最新版本";

            await Task.Delay(1800);
            CheckUpdateText.Text = "检查更新";
            CheckUpdateButton.IsEnabled = true;
        }

        private async void OpenLink_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not string target || target.Length == 0)
            {
                return;
            }

            try
            {
                await Windows.System.Launcher.LaunchUriAsync(new Uri(target));
            }
            catch
            {
                // 没有默认浏览器 / 邮件客户端时静默跳过
            }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not string key)
            {
                return;
            }

            string path = key switch
            {
                "app" => _appDir,
                "logs" => _logDir,
                _ => _dataDir,
            };

            try
            {
                // 目录还没建出来时先建，免得演示点开是个报错框
                if (path.Length > 0 && !Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }

                if (path.Length > 0)
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                }
            }
            catch
            {
                // 打不开资源管理器就算了
            }
        }
    }
}
