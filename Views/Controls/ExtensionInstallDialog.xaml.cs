using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AstraBrew_Launcher.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace AstraBrew_Launcher.Views.Controls
{
    /// <summary>
    /// 安装扩展：Git 仓库与离线包两条路径，安装过程内嵌在弹窗里而不是另开一层。
    ///
    /// 离线包支持**一次选多个**：先整批预检（有没有 manifest.json、装成什么目录名、有没有重名），
    /// 预检不过的不让装；通过后逐个解压落位，单个失败不影响其余包。
    /// Git 走 <c>git clone --depth 1</c>（GitHub 地址按加速开关中转）。
    /// 两条路径都不覆盖已有目录——重名时加 -2、-3 后缀另装一份。
    /// </summary>
    public sealed partial class ExtensionInstallDialog : ContentDialog
    {
        private readonly StringBuilder _log = new();
        private readonly List<string> _paths = new();

        private bool _installing;
        private IReadOnlyList<OfflinePackage> _packages = Array.Empty<OfflinePackage>();

        public ExtensionInstallDialog()
        {
            InitializeComponent();

            // Title 是 object 型，附加属性只认 string；两个按钮文案又是同一个附加属性，
            // 一个元素上只能挂一次，所以这三处一律在代码里给初值（后续由状态覆盖）。
            Title = L.Get("Ext.Action.Install");
            CloseButtonText = L.Get("Common.Action.Cancel.Content");
            PrimaryButtonText = L.Get("Eid.Action.Start");
        }

        /// <summary>装到哪个实例；页面打开弹窗时传入，默认取当前实例。</summary>
        public TavernInstance? Instance { get; set; }

        /// <summary>安装成功后的扩展目录名（批量时是多个）；为空表示没有完成安装。</summary>
        public IReadOnlyList<string> InstalledNames { get; private set; } = Array.Empty<string>();

        /// <summary>至少装成一个时抛给页面，用于刷新列表。</summary>
        public event EventHandler? Installed;

        private TavernInstance Target => Instance ?? TavernInstances.Current;

        // ==================== 安装方式 ====================

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (GitPanel is null || OfflinePanel is null)
            {
                return;
            }

            bool git = GitTab.IsChecked == true;
            GitPanel.Visibility = git ? Visibility.Visible : Visibility.Collapsed;
            OfflinePanel.Visibility = git ? Visibility.Collapsed : Visibility.Visible;
            HideError();
            UpdatePrimaryButton();
        }

        /// <summary>主按钮上的数量只在离线多选时有意义，Git 路径保持原样。</summary>
        private void UpdatePrimaryButton()
        {
            if (_installing)
            {
                return;
            }

            int ready = _packages.Count(package => package.Valid);
            bool blocked = _packages.Any(package => !package.Valid);

            // 有问题的包没移除时不显示数量：按钮文字说「装 2 个」却点了报错，很别扭
            PrimaryButtonText = OfflineTab.IsChecked == true && ready > 0 && !blocked
                ? L.F("Eid.ReadyCount", ready)
                : L.Get("Eid.Action.Start");
        }

        // ==================== Git ====================

        private void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // 地址一变，之前检测出来的分支就不可信了。
            BranchBox.Items.Clear();
            BranchBox.SelectedIndex = -1;
            BranchBox.PlaceholderText = L.Get("Eid.Branch.Placeholder");
            HideError();
        }

        private async void Detect_Click(object sender, RoutedEventArgs e)
        {
            if (_installing || !LooksLikeRepository(UrlBox.Text))
            {
                ShowError(L.Get("Eid.Error.NotRepo"));
                return;
            }

            HideError();
            DetectButton.IsEnabled = false;
            DetectButton.Content = L.Get("Eid.Action.Detecting");

            try
            {
                IReadOnlyList<string> branches = await TavernExtensions.ListBranchesAsync(UrlBox.Text.Trim());

                BranchBox.Items.Clear();

                if (branches.Count == 0)
                {
                    // 私有仓库、或者网络不通：给个 main 让用户还能继续试
                    BranchBox.Items.Add("main");
                    ShowError(L.Get("Eid.Error.BranchList"));
                }
                else
                {
                    foreach (string branch in branches)
                    {
                        BranchBox.Items.Add(branch);
                    }
                }

                BranchBox.SelectedIndex = 0;
            }
            finally
            {
                DetectButton.IsEnabled = true;
                DetectButton.Content = L.Get("Eid.Action.Detect");
            }
        }

        // ==================== 离线包 ====================

        private async void PickOffline_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".zip");
            picker.SuggestedStartLocation = PickerLocationId.Downloads;

            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));

            IReadOnlyList<StorageFile>? files = await picker.PickMultipleFilesAsync();
            if (files is null || files.Count == 0)
            {
                return;
            }

            _paths.Clear();
            foreach (StorageFile file in files)
            {
                if (!_paths.Contains(file.Path, StringComparer.OrdinalIgnoreCase))
                {
                    _paths.Add(file.Path);
                }
            }

            Inspect();
        }

        /// <summary>重新预检当前这批文件并按结果重画列表（移除一个包后也会走这里）。</summary>
        private void Inspect()
        {
            _packages = TavernExtensions.InspectOfflinePackages(_paths);
            RenderPackages();
            HideError();
        }

        private void RenderPackages()
        {
            OfflineHost.Children.Clear();
            OfflineListPanel.Visibility = _packages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (OfflinePackage package in _packages)
            {
                OfflineHost.Children.Add(BuildPackageRow(package));
            }

            int ready = _packages.Count(package => package.Valid);
            int broken = _packages.Count - ready;

            OfflineText.Text = _packages.Count == 0
                ? L.Get("Eid.Package.None")
                : broken == 0
                    ? L.F("Eid.Package.ReadyAll", _packages.Count)
                    : L.F("Eid.Package.Mixed", _packages.Count, ready, broken);

            UpdatePrimaryButton();
        }

        private Grid BuildPackageRow(OfflinePackage package)
        {
            var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };

            text.Children.Add(new TextBlock
            {
                Text = package.FileName,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            text.Children.Add(new TextBlock
            {
                Text = package.Valid ? L.F("Eid.Package.Ok", package.Folder) : package.Error ?? L.Get("ExtS.Package.PrecheckFailed"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Res(package.Valid ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush",
                    package.Valid ? Color.FromArgb(0xFF, 0x0F, 0x7B, 0x0F) : Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C)),
            });

            var remove = new Button
            {
                Content = new FontIcon { FontSize = 10, Glyph = "\uE711" },
                Tag = package.Path,
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
            };

            if (Application.Current.Resources.TryGetValue("SubtleIconButtonStyle", out object style)
                && style is Style subtle)
            {
                remove.Style = subtle;
            }

            ToolTipService.SetToolTip(remove, L.Get("Eid.Package.Remove.Tip"));
            AutomationProperties.SetName(remove, L.F("Eid.Package.Remove.Name", package.FileName));
            remove.Click += RemovePackage_Click;

            var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(text);
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);

            return row;
        }

        private void RemovePackage_Click(object sender, RoutedEventArgs e)
        {
            if (_installing || sender is not FrameworkElement { Tag: string path })
            {
                return;
            }

            _paths.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
            Inspect();
        }

        // ==================== 安装 ====================

        private async void Dialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            if (_installing)
            {
                args.Cancel = true;
                return;
            }

            if (GitTab.IsChecked == true)
            {
                if (!LooksLikeRepository(UrlBox.Text))
                {
                    ShowError(L.Get("Eid.Error.NoRepoUrl"));
                    args.Cancel = true;
                    return;
                }

                if (BranchBox.SelectedItem is null)
                {
                    ShowError(L.Get("Eid.Error.NoBranch"));
                    args.Cancel = true;
                    return;
                }
            }
            else
            {
                if (_packages.Count == 0)
                {
                    ShowError(L.Get("Eid.Error.NoPackage"));
                    args.Cancel = true;
                    return;
                }

                // 有一个包不合法就整批不动：半批装完再回滚，比直接拦住更难收拾
                if (_packages.Any(package => !package.Valid))
                {
                    ShowError(L.Get("Eid.Error.BrokenPackages"));
                    args.Cancel = true;
                    return;
                }
            }

            // 安装不结束弹窗：切到进度面板，完成后由代码关闭。
            args.Cancel = true;
            await StartInstallAsync();
        }

        private async Task StartInstallAsync()
        {
            _installing = true;
            _log.Clear();

            FormPanel.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Visible;
            InstallBar.Value = 0;
            PercentText.Text = "0%";
            LogText.Text = string.Empty;
            PhaseText.Text = L.Get("Eid.Phase.Preparing");
            PhaseHintText.Text = L.Get("Eid.Hint.Hold");

            // 安装期间不给「关闭」入口：半装完的目录留在磁盘上比失败更糟
            PrimaryButtonText = string.Empty;
            CloseButtonText = string.Empty;

            try
            {
                if (GitTab.IsChecked == true)
                {
                    await InstallFromGitAsync();
                }
                else
                {
                    await InstallOfflineBatchAsync();
                }
            }
            finally
            {
                _installing = false;
            }
        }

        private async Task InstallFromGitAsync()
        {
            var progress = new Progress<ExtensionInstallStep>(step =>
            {
                InstallBar.Value = step.Percent;
                PercentText.Text = $"{(int)Math.Round(step.Percent)}%";
                PhaseText.Text = step.Phase;
                PhaseHintText.Text = L.Get("Eid.Hint.Hold");

                if (step.LogLine is { Length: > 0 } line)
                {
                    AppendLog(line);
                }
            });

            ExtensionInstallResult result = await TavernExtensions.InstallFromGitAsync(
                UrlBox.Text.Trim(),
                BranchBox.SelectedItem?.ToString() ?? "main",
                Target,
                progress);

            if (!result.Ok)
            {
                Fail(L.Get("Eid.Fail.Title"), result.Error ?? L.Get("Eid.Error.Unknown"));
                return;
            }

            InstallBar.Value = 100;
            PercentText.Text = "100%";
            PhaseText.Text = L.Get("Eid.Phase.Done");
            PhaseHintText.Text = L.Get("Eid.Hint.Done");
            AppendLog(L.F("Eid.Log.Written", TavernExtensions.ThirdPartyRoot(Target)));

            Finish(new[] { result.Folder });
        }

        private async Task InstallOfflineBatchAsync()
        {
            int total = _packages.Count;
            var progress = new Progress<OfflineBatchStep>(step =>
            {
                InstallBar.Value = Math.Clamp(step.Percent, 0, 100);
                PercentText.Text = $"{(int)Math.Round(Math.Clamp(step.Percent, 0, 100))}%";
                PhaseText.Text = step.Phase;
                PhaseHintText.Text = total > 1
                    ? L.F("Eid.Phase.Batch", Math.Min(step.Index + 1, total), total)
                    : L.Get("Eid.Hint.Hold");

                if (step.LogLine is { Length: > 0 } line)
                {
                    AppendLog(line);
                }
            });

            OfflineBatchResult result = await TavernExtensions.InstallFromZipBatchAsync(
                _packages,
                Target,
                progress);

            foreach (OfflinePackageFailure failure in result.Failed)
            {
                AppendLog($"✗ {failure.FileName}：{failure.Error}");
            }

            if (result.Installed.Count == 0)
            {
                Fail(L.Get("Eid.Fail.None.Title"), result.Failed.Count > 0 ? result.Failed[0].Error : L.Get("Eid.Error.Unknown"));
                return;
            }

            InstallBar.Value = 100;
            PercentText.Text = "100%";
            PhaseText.Text = L.Get(result.Failed.Count == 0 ? "Eid.Phase.AllDone" : "Eid.Phase.Partial");
            PhaseHintText.Text = L.F("Eid.Hint.Summary", result.Installed.Count, result.Failed.Count);
            AppendLog(L.F("Eid.Log.Written", TavernExtensions.ThirdPartyRoot(Target)));

            if (result.Failed.Count > 0)
            {
                // 有失败就留在进度面板上：日志里有具体原因，用户可以直接对照重试
                PhaseText.Text = L.Get("Eid.Phase.Partial");
                CloseButtonText = L.Get("Common.Action.Close.Content");
                InstalledNames = result.Installed;
                Installed?.Invoke(this, EventArgs.Empty);
                return;
            }

            Finish(result.Installed);
        }

        private void Fail(string title, string reason)
        {
            InstallBar.Value = 0;
            PercentText.Text = "—";
            PhaseText.Text = title;
            PhaseHintText.Text = L.Get("Eid.Hint.FixRetry");
            AppendLog("✗ " + reason);

            // 失败时停在进度面板上：日志里有原始输出，比一句「失败」有用得多
            CloseButtonText = L.Get("Common.Action.Close.Content");
        }

        private void Finish(IReadOnlyList<string> installed)
        {
            InstalledNames = installed;
            Installed?.Invoke(this, EventArgs.Empty);
            Hide();
        }

        private void AppendLog(string line)
        {
            _log.AppendLine(line);

            // 克隆失败时 git 能吐几百行，留最后 200 行足够定位问题
            string[] lines = _log.ToString().Split('\n');
            if (lines.Length > 200)
            {
                _log.Clear();
                _log.Append(string.Join('\n', lines[^200..]));
            }

            LogText.Text = _log.ToString();
        }

        // ==================== 辅助 ====================

        private static Brush Res(string key, Color fallback)
            => Application.Current.Resources.TryGetValue(key, out object value) && value is Brush brush
                ? brush
                : new SolidColorBrush(fallback);

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }

        private void HideError()
        {
            ErrorText.Visibility = Visibility.Collapsed;
        }

        private static bool LooksLikeRepository(string value)
        {
            string url = value.Trim();
            if (url.Length < 8)
            {
                return false;
            }

            bool scheme = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("git@", StringComparison.OrdinalIgnoreCase);

            return scheme && url.Contains('/');
        }
    }
}
