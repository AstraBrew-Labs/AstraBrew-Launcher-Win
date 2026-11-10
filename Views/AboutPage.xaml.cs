using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using AstraBrew_Launcher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

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
            // 版本号带渠道后缀（0.3.0-Alpha.0），正式版没有后缀——统一由 BuildChannelInfo 给出
            string versionText = BuildChannelInfo.DisplayVersion;

            VersionValue.Text = versionText;
            HeroVersionChipText.Text = versionText;

            ApplyChannel();

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

        /// <summary>
        /// 渠道标记。构建配置（Alpha / Beta / Debug）已经编进程序集，这里只负责画：
        /// · 正式版：胶囊保持 MetaChipStyle 的中性灰，写「稳定通道」，不出现任何「预览 / 测试」字样；
        /// · 其余三种：胶囊写「预览版 / 测试版 / 开发版」，文字与底色都换成该渠道的强调色。
        /// 「更新通道」那一行跟着换名字——预览版还写「稳定通道」会自相矛盾。
        /// </summary>
        private void ApplyChannel()
        {
            ChannelValue.Text = BuildChannelInfo.ChannelName;

            if (BuildChannelInfo.Badge is not { Length: > 0 } badge)
            {
                return;
            }

            ChannelChipText.Text = badge;

            var (r, g, b) = BuildChannelInfo.Accent;
            ChannelChipText.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, r, g, b));
            ChannelChip.Background = new SolidColorBrush(Color.FromArgb(0x1F, r, g, b));
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

            // 正式版不写这一行：拿到反馈时，没有渠道字样就代表是稳定版
            if (BuildChannelInfo.Badge is { Length: > 0 })
            {
                text.AppendLine($"渠道       {BuildChannelInfo.Describe}");
            }

            text.AppendLine($"酒馆实例   {TavernInstances.Current.DisplayLabel}");
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
