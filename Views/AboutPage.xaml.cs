using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
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

        /// <summary>本次会话里查到的「最新发布」，下载与安装都从这里取资产。</summary>
        private LauncherRelease? _latest;

        /// <summary>服务层的结论，失败时要靠它翻译成人话。</summary>
        private UpdateStatus _lastStatus = UpdateStatus.Unknown;

        private UpdateFailure _lastFailure = UpdateFailure.None;

        /// <summary>失败详情（异常原文 / 无资产之类），有值就直接显示它。</summary>
        private string? _failureDetail;

        private UpdateSourceKind? _source;

        private DateTimeOffset _checkedAt;

        private double _downloadPercent;

        private CancellationTokenSource? _updateCts;

        /// <summary>正在把渠道从设置回填到选择器：此时的选择变更不是用户操作。</summary>
        private bool _syncingChannel;

        private enum UpdateCheckState
        {
            Never,
            Checking,
            UpToDate,
            Available,
            Downloading,
            Installing,
            Failed,
            NoChannelRelease,
        }

        public AboutPage()
        {
            InitializeComponent();

            // 渠道选择器按设置回填。先挂挡板：改 SelectedIndex 会触发 SelectionChanged，
            // 不挡的话会把「回填」当成用户切换，白跑一次强制检查。
            _syncingChannel = true;
            UpdateChannelSelect.SelectedIndex = (int)LauncherSettings.UpdateChannel;
            _syncingChannel = false;

            UpdateChannelHint();

            // 更新按钮与状态行是状态驱动的，XAML 里刻意没挂 Loc.Text——首帧文案在这里给
            ApplyUpdateState();

            Loaded += (_, _) =>
            {
                FillEnvironment();
                ApplyDocsLink();

                // 页面一进来就把上次的检查结论回填（服务层有 6 小时缓存，命中时不发请求）。
                // 启动时的静默检查与这里读的是同一份缓存，所以两处结论不会打架。
                // 开发版不做自动回填：那就是一次白打的接口调用，手动点按钮才查。
                if (LauncherUpdateService.AutoCheckEnabled)
                {
                    _ = CheckForUpdatesAsync(force: false);
                }
            };

            // 渠道标记、更新通道都是按当前语言取的：换语言后要重画一遍
            LanguageSwitch.LanguageChanged += OnLanguageChanged;
            Unloaded += (_, _) =>
            {
                _updateCts?.Cancel();
                LanguageSwitch.LanguageChanged -= OnLanguageChanged;
            };
        }

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            ApplyChannel();
            ApplyBrandLockup();
            ApplyUpdateState();
            ApplyDocsLink();
            UpdateChannelHint();
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

        // ==================== 更新 ====================

        private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_updateState is UpdateCheckState.Available)
            {
                await DownloadAndInstallAsync();
                return;
            }

            if (_updateState is UpdateCheckState.Checking or UpdateCheckState.Downloading or UpdateCheckState.Installing)
            {
                return;
            }

            await CheckForUpdatesAsync(force: true);
        }

        /// <summary>渠道档位的显示名（不含「跟随版本」，那一档没有具体线路）。</summary>
        private static string ChannelOptionName(UpdateChannelSetting channel) => channel switch
        {
            UpdateChannelSetting.Stable => L.Get("About.Channel.Option.0"),
            UpdateChannelSetting.Beta => L.Get("About.Channel.Option.1"),
            UpdateChannelSetting.Alpha => L.Get("About.Channel.Option.2"),
            _ => L.Get("About.Channel.Option.Auto"),
        };

        /// <summary>
        /// 下拉框的悬停说明：选「跟随版本」时要说清现在实际跟的是哪条线，
        /// 否则这四个字读起来像「什么都不做」，用户不知道会不会收到测试包。
        /// </summary>
        private void UpdateChannelHint()
        {
            if (UpdateChannelSelect is null)
            {
                return;
            }

            string effective = ChannelOptionName(LauncherSettings.EffectiveUpdateChannel);

            ToolTipService.SetToolTip(
                UpdateChannelSelect,
                LauncherSettings.UpdateChannel == UpdateChannelSetting.Auto
                    ? L.F("About.Channel.Tip.Auto", effective)
                    : L.F("About.Channel.Tip.Fixed", effective));
        }

        /// <summary>
        /// 切换更新渠道。落盘后**立刻强制重查**：用户切完最想看到的就是「这条线上有没有新版」，
        /// 让他再点一次「检查更新」属于多余的一步。缓存里带着渠道，换了渠道旧结论自动作废。
        /// </summary>
        private void UpdateChannel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingChannel)
            {
                return;
            }

            int index = UpdateChannelSelect.SelectedIndex;
            if (index < 0 || index > (int)UpdateChannelSetting.Alpha)
            {
                return;
            }

            var channel = (UpdateChannelSetting)index;
            if (channel == LauncherSettings.UpdateChannel)
            {
                return;
            }

            // SetUpdateChannel 会经 LauncherSettings.Changed 触发 SettingsStore 的防抖落盘；
            // 它同时打上「手动定过」标记，此后不再跟随构建渠道（选回「跟随版本」也是显式选择）
            LauncherSettings.SetUpdateChannel(channel);
            UpdateChannelHint();

            _latest = null;
            _lastStatus = UpdateStatus.Unknown;
            _failureDetail = null;
            _updateState = UpdateCheckState.Never;
            ApplyUpdateState();

            _ = CheckForUpdatesAsync(force: true);
        }

        /// <summary>
        /// 查一次更新。<paramref name="force"/> = true 是用户点按钮（不吃缓存、失败要报出来）；
        /// false 是页面加载时的静默回填（吃 6 小时缓存，失败就把上次的结论留着，不闪红字）。
        /// </summary>
        private async Task CheckForUpdatesAsync(bool force)
        {
            _updateCts?.Cancel();
            _updateCts = new CancellationTokenSource();

            _updateState = UpdateCheckState.Checking;
            ApplyUpdateState();

            try
            {
                UpdateCheckResult result = await LauncherUpdateService
                    .CheckAsync(force, _updateCts.Token);

                _lastStatus = result.State;
                _lastFailure = result.Failure;
                _latest = result.Latest;
                _checkedAt = result.CheckedAt;
                _source = result.Source;

                _updateState = result.State switch
                {
                    UpdateStatus.Available => UpdateCheckState.Available,
                    UpdateStatus.UpToDate => UpdateCheckState.UpToDate,
                    UpdateStatus.NoReleaseForChannel => UpdateCheckState.NoChannelRelease,
                    UpdateStatus.Failed => UpdateCheckState.Failed,
                    _ => UpdateCheckState.Never,
                };
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _updateState = UpdateCheckState.Failed;
                _lastFailure = UpdateFailure.Network;
                _failureDetail = ex.Message;
            }

            ApplyUpdateState();
        }

        /// <summary>
        /// 下载并安装。安装版取 setup.exe（让 MSI 自己升级，ARP 记录与快捷方式一起更新），
        /// 免安装版取 zip（解压后覆盖当前程序目录）。真正替换发生在进程退出之后，由外部脚本完成。
        /// </summary>
        private async Task DownloadAndInstallAsync()
        {
            LauncherRelease? release = _latest;
            if (release is null)
            {
                return;
            }

            UpdateAsset? asset = LauncherUpdateService.IsInstalledLayout
                ? release.PickInstaller()
                : release.PickPortableZip();

            if (asset is null)
            {
                _updateState = UpdateCheckState.Failed;
                _lastFailure = UpdateFailure.Payload;
                _failureDetail = L.Get("Update.Error.NoAsset");
                ApplyUpdateState();
                return;
            }

            _updateState = UpdateCheckState.Downloading;
            _downloadPercent = 0;
            ApplyUpdateState();

            var progress = new Progress<double>(percent =>
            {
                _downloadPercent = percent;
                if (_updateState == UpdateCheckState.Downloading)
                {
                    CheckUpdateText.Text = L.F("About.Update.Downloading", (int)percent);
                }
            });

            try
            {
                string path = await LauncherUpdateService
                    .DownloadAsync(release, asset, progress, CancellationToken.None);

                bool verified = await LauncherUpdateService
                    .VerifyAsync(release, asset, path, CancellationToken.None);

                if (!verified)
                {
                    File.Delete(path);
                    _updateState = UpdateCheckState.Failed;
                    _lastFailure = UpdateFailure.Payload;
                    _failureDetail = L.Get("Update.Error.Checksum");
                    ApplyUpdateState();
                    return;
                }

                _updateState = UpdateCheckState.Installing;
                ApplyUpdateState();

                if (!LauncherUpdateService.BeginInstall(path, release, out string error))
                {
                    _updateState = UpdateCheckState.Failed;
                    _failureDetail = error;
                    ApplyUpdateState();
                    return;
                }

                // 留一点时间把「正在安装」画出来，然后退出——脚本要等这个进程结束后才换文件
                await Task.Delay(800);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                _updateState = UpdateCheckState.Failed;
                _lastFailure = UpdateFailure.Network;
                _failureDetail = ex.Message;
                ApplyUpdateState();
            }
        }

        /// <summary>按当前检查状态刷按钮与右下角那行说明（换语言时也会走到这里）。</summary>
        private void ApplyUpdateState()
        {
            switch (_updateState)
            {
                case UpdateCheckState.Checking:
                    CheckUpdateButton.IsEnabled = false;
                    CheckUpdateText.Text = L.Get("About.Update.Checking");
                    LastCheckText.Text = L.Get("About.Update.Connecting");
                    break;

                case UpdateCheckState.UpToDate:
                    CheckUpdateButton.IsEnabled = true;
                    CheckUpdateText.Text = L.Get("About.Action.CheckUpdate");
                    LastCheckText.Text = L.Get("About.Update.LatestHint");
                    break;

                case UpdateCheckState.Available:
                    CheckUpdateButton.IsEnabled = true;
                    CheckUpdateText.Text = L.F("About.Update.Download", _latest?.DisplayVersion ?? string.Empty);
                    LastCheckText.Text = DescribeAvailable();
                    break;

                case UpdateCheckState.Downloading:
                    CheckUpdateButton.IsEnabled = false;
                    CheckUpdateText.Text = L.F("About.Update.Downloading", (int)_downloadPercent);
                    LastCheckText.Text = DescribeAvailable();
                    break;

                case UpdateCheckState.Installing:
                    CheckUpdateButton.IsEnabled = false;
                    CheckUpdateText.Text = L.Get("About.Update.Installing");
                    LastCheckText.Text = L.Get("About.Update.Restarting");
                    break;

                case UpdateCheckState.Failed:
                    CheckUpdateButton.IsEnabled = true;
                    CheckUpdateText.Text = L.Get("About.Action.CheckUpdate");
                    LastCheckText.Text = _failureDetail is { Length: > 0 }
                        ? _failureDetail
                        : L.F("About.Update.Failed", FailureText(_lastFailure));
                    break;

                case UpdateCheckState.NoChannelRelease:
                    CheckUpdateButton.IsEnabled = true;
                    CheckUpdateText.Text = L.Get("About.Action.CheckUpdate");
                    LastCheckText.Text = L.Get("About.Update.NoChannelRelease");
                    break;

                default:
                    CheckUpdateButton.IsEnabled = true;
                    CheckUpdateText.Text = L.Get("About.Action.CheckUpdate");
                    LastCheckText.Text = LauncherUpdateService.AutoCheckEnabled
                        ? L.Get("About.Update.Never")
                        : L.Get("About.Update.DevNoAutoCheck");
                    break;
            }

            ApplyVersionBadge();
            ApplyReleaseNotes();
        }

        /// <summary>
        /// 新版本的更新日志（Release 简介）。
        ///
        /// 只在「确实拿到了新版」的三种状态下显示：检查完发现没新版就该收起，
        /// 否则上一轮的日志会一直挂在页面上，看着像还有更新没装。
        /// </summary>
        private void ApplyReleaseNotes()
        {
            if (ReleaseNotesPanel is null || ReleaseNotesTitle is null || ReleaseNotesText is null)
            {
                return;
            }

            bool show = _updateState
                is UpdateCheckState.Available or UpdateCheckState.Downloading or UpdateCheckState.Installing;

            if (!show || _latest is null)
            {
                ReleaseNotesPanel.Visibility = Visibility.Collapsed;
                return;
            }

            ReleaseNotesTitle.Text = L.F("About.Update.Notes.Title", _latest.DisplayVersion);
            ReleaseNotesText.Text = ReleaseNotes.Format(_latest.Notes, L.Get("About.Update.Notes.Empty"));
            ReleaseNotesPanel.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// 「当前版本」右边那枚小胶囊。以前是写死的「已是最新」，等于一直替用户下结论；
        /// 现在只在真的查过之后才出现：确认最新 → 绿色「已是最新」，有新版 → 蓝色「有新版本」，
        /// 没查过 / 查失败 → 整颗藏起来（不知道就别装知道）。
        /// </summary>
        private void ApplyVersionBadge()
        {
            if (VersionBadge is null || VersionBadgeText is null)
            {
                return;
            }

            switch (_updateState)
            {
                case UpdateCheckState.UpToDate:
                    VersionBadge.Visibility = Visibility.Visible;
                    VersionBadgeText.Text = L.Get("About.Update.Latest");
                    SetBadgeColors("16A34A");
                    break;

                case UpdateCheckState.Available:
                case UpdateCheckState.Downloading:
                case UpdateCheckState.Installing:
                    VersionBadge.Visibility = Visibility.Visible;
                    VersionBadgeText.Text = L.Get("About.Update.Badge.Available");
                    SetBadgeColors("2B88D8");
                    break;

                default:
                    VersionBadge.Visibility = Visibility.Collapsed;
                    break;
            }
        }

        /// <summary>胶囊底色是强调色的 12% alpha，文字用原色，和关于页其它徽章同一套算法。</summary>
        private void SetBadgeColors(string hex)
        {
            Color color = Color.FromArgb(0xFF, Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..], 16));
            VersionBadgeText.Foreground = new SolidColorBrush(color);
            VersionBadge.Background = new SolidColorBrush(Color.FromArgb(0x1F, color.R, color.G, color.B));
        }

        /// <summary>「有新版」那行：版本 + 包大小 + 来源，一行说清楚。</summary>
        private string DescribeAvailable()
        {
            if (_latest is null)
            {
                return string.Empty;
            }

            UpdateAsset? asset = LauncherUpdateService.IsInstalledLayout
                ? _latest.PickInstaller()
                : _latest.PickPortableZip();

            string size = asset?.DisplaySize is { Length: > 0 } text ? $" · {text}" : string.Empty;
            string source = _source is { } kind ? $" · {LauncherUpdateSource.NameOf(kind)}" : string.Empty;

            return L.F("About.Update.AvailableHint", _latest.DisplayVersion, size, source);
        }

        private static string FailureText(UpdateFailure failure) => failure switch
        {
            UpdateFailure.Timeout => L.Get("Update.Failure.Timeout"),
            UpdateFailure.RateLimit => L.Get("Update.Failure.RateLimit"),
            UpdateFailure.Server => L.Get("Update.Failure.Server"),
            UpdateFailure.Payload => L.Get("Update.Failure.Payload"),
            UpdateFailure.Empty => L.Get("Update.Failure.Empty"),
            _ => L.Get("Update.Failure.Network"),
        };

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
