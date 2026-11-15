using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AstraBrew_Launcher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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

        /// <summary>更新检查进行到哪一步。换语言时据此重画那两行字，而不是一律重置成「尚未检查」。</summary>
        private UpdateCheckState _updateState = UpdateCheckState.Never;

        private enum UpdateCheckState
        {
            Never,
            Checking,
            UpToDate,
        }

        public AboutPage()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                FillEnvironment();
                ApplyDocsLink();
            };

            // 渠道标记、更新通道都是按当前语言取的：换语言后要重画一遍
            LanguageSwitch.LanguageChanged += OnLanguageChanged;
            Unloaded += (_, _) => LanguageSwitch.LanguageChanged -= OnLanguageChanged;
        }

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            ApplyChannel();
            ApplyBrandLockup();
            ApplyUpdateState();
            ApplyDocsLink();
        }

        /// <summary>
        /// 品牌标识：中文用横版图片（图里就是中文），英文换成同名字的纯文字。
        /// 不这么做的话，英文界面上会挂着一张写着「星酿启动器」的图。
        /// </summary>
        private void ApplyBrandLockup()
        {
            if (HeroBrandImage is null || HeroBrandText is null)
            {
                return;
            }

            bool chinese = !L.IsEnglish;
            HeroBrandImage.Visibility = chinese ? Visibility.Visible : Visibility.Collapsed;
            HeroBrandText.Visibility = chinese ? Visibility.Collapsed : Visibility.Visible;
            HeroBrandText.Text = L.Get("Shell.AppName.Full");
        }

        private void FillEnvironment()
        {
            // 版本号带渠道后缀（0.3.0-Alpha.0），正式版没有后缀——统一由 BuildChannelInfo 给出
            string versionText = BuildChannelInfo.DisplayVersion;

            VersionValue.Text = versionText;
            HeroVersionChipText.Text = versionText;

            ApplyChannel();
            ApplyBrandLockup();

            // 构建号取 exe 的写入时间，重新构建后自动跟着变
            string exePath = Environment.ProcessPath ?? string.Empty;
            if (exePath.Length > 0 && File.Exists(exePath))
            {
                var built = File.GetLastWriteTime(exePath);
                BuildValue.Text = $"{built:yyyy-MM-dd} · #{built:yyMMdd}.1";
            }

            RuntimeValue.Text = RuntimeInformation.FrameworkDescription;

            var os = Environment.OSVersion.Version;
            OsValue.Text = L.F(
                "About.Os.Value",
                OsName(os.Build),
                $"{os.Build}.{os.Revision}");

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
        /// 文档站分中英两套（/zh/guide 与 /en/guide），链接跟着界面语言走；
        /// 站上没有对应语言时退回中文页——总比给一个 404 强。
        /// </summary>
        private void ApplyDocsLink()
        {
            if (DocsRowButton is null || DocsRowUrl is null)
            {
                return;
            }

            string path = L.IsEnglish ? "en/guide" : "zh/guide";
            DocsRowButton.Tag = $"https://launcher.astrabrew.cn/{path}";
            DocsRowUrl.Text = $"launcher.astrabrew.cn/{path}";
        }

        /// <summary>
        /// 渠道标记。构建配置（Alpha / Beta / Debug）已经编进程序集，这里只负责画：
        /// · 正式版：胶囊保持 MetaChipStyle 的中性灰，写「稳定通道」，不出现任何「预览 / 测试」字样；
        /// · 其余三种：胶囊写「预览版 / 测试版 / 开发版」，文字与底色都换成该渠道的强调色。
        /// </summary>
        private void ApplyChannel()
        {
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

        private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            CheckUpdateButton.IsEnabled = false;
            _updateState = UpdateCheckState.Checking;
            ApplyUpdateState();

            await Task.Delay(1200);
            _updateState = UpdateCheckState.UpToDate;
            ApplyUpdateState();

            await Task.Delay(1800);
            _updateState = UpdateCheckState.Never;
            ApplyUpdateState();
            CheckUpdateButton.IsEnabled = true;
        }

        /// <summary>按当前检查状态刷那两行文字（换语言时也会走到这里）。</summary>
        private void ApplyUpdateState()
        {
            switch (_updateState)
            {
                case UpdateCheckState.Checking:
                    CheckUpdateText.Text = L.Get("About.Update.Checking");
                    LastCheckText.Text = L.Get("About.Update.Connecting");
                    break;

                case UpdateCheckState.UpToDate:
                    CheckUpdateText.Text = L.Get("About.Update.Latest");
                    LastCheckText.Text = L.Get("About.Update.LatestHint");
                    break;

                default:
                    CheckUpdateText.Text = L.Get("About.Action.CheckUpdate");
                    LastCheckText.Text = L.Get("About.Update.Never");
                    break;
            }
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
