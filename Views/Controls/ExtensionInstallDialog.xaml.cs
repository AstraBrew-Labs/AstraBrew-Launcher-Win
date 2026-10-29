using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AstraBrew_Launcher.Views.Controls
{
    /// <summary>
    /// 安装扩展：Git 仓库与离线包两条路径，安装过程内嵌在弹窗里而不是另开一层。
    /// 这里只做界面与流程演示，真实克隆与解压由后续的安装服务接管。
    /// </summary>
    public sealed partial class ExtensionInstallDialog : ContentDialog
    {
        private readonly DispatcherTimer _detectTimer = new();
        private readonly DispatcherTimer _installTimer = new();

        private bool _installing;
        private bool _offlinePicked;
        private double _progress;

        public ExtensionInstallDialog()
        {
            InitializeComponent();

            _detectTimer.Interval = TimeSpan.FromMilliseconds(850);
            _detectTimer.Tick += DetectTimer_Tick;

            _installTimer.Interval = TimeSpan.FromMilliseconds(70);
            _installTimer.Tick += InstallTimer_Tick;
        }

        /// <summary>安装成功后写入的扩展目录名；为空表示没有完成安装。</summary>
        public string InstalledName { get; private set; } = string.Empty;

        /// <summary>安装成功时抛给页面，用于插入新行。</summary>
        public event EventHandler? Installed;

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
        }

        // ==================== Git ====================

        private void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // 地址一变，之前检测出来的分支就不可信了。
            BranchBox.Items.Clear();
            BranchBox.SelectedIndex = -1;
            BranchBox.PlaceholderText = "先检测分支，再选择要安装的分支";
            HideError();
        }

        private void Detect_Click(object sender, RoutedEventArgs e)
        {
            if (_detectTimer.IsEnabled || _installing)
            {
                return;
            }

            if (!LooksLikeRepository(UrlBox.Text))
            {
                ShowError("地址不像一个 Git 仓库，需要形如 https://host/owner/repo.git");
                return;
            }

            HideError();
            DetectButton.IsEnabled = false;
            DetectButton.Content = "检测中…";
            _detectTimer.Start();
        }

        private void DetectTimer_Tick(object? sender, object e)
        {
            _detectTimer.Stop();
            DetectButton.IsEnabled = true;
            DetectButton.Content = "检测分支";

            string? branch = SuggestDefaultBranch();
            BranchBox.Items.Clear();
            BranchBox.Items.Add(branch ?? "main");
            BranchBox.Items.Add("dev");
            BranchBox.Items.Add("staging");
            BranchBox.SelectedIndex = 0;
        }

        // ==================== 离线包 ====================

        private void PickOffline_Click(object sender, RoutedEventArgs e)
        {
            // 原型阶段不做真实文件选择，直接给出一个可继续的状态。
            _offlinePicked = true;
            OfflineText.Text = "已选择 1 个离线包：long-term-memory-2.2.0.zip · 待校验";
            HideError();
        }

        // ==================== 安装 ====================

        private void Dialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
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
                    ShowError("先填写一个可用的仓库地址。");
                    args.Cancel = true;
                    return;
                }

                if (BranchBox.SelectedItem is null)
                {
                    ShowError("先点「检测分支」，再选择要安装的分支。");
                    args.Cancel = true;
                    return;
                }
            }

            if (OfflineTab.IsChecked == true && !_offlinePicked)
            {
                ShowError("先选择一个 .zip 离线包。");
                args.Cancel = true;
                return;
            }

            // 安装不结束弹窗：切到进度面板，完成后由代码关闭。
            args.Cancel = true;
            StartInstall();
        }

        private void StartInstall()
        {
            _installing = true;
            _progress = 0;

            FormPanel.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Visible;
            InstallBar.Value = 0;
            PercentText.Text = "0%";

            // 安装期间不给「关闭」入口，避免半安装目录被留在磁盘上。
            PrimaryButtonText = string.Empty;
            CloseButtonText = string.Empty;

            bool git = GitTab.IsChecked == true;
            PhaseText.Text = git ? "正在克隆仓库…" : "正在解压离线包…";
            PhaseHintText.Text = "第 1/2 步 · 下载扩展代码";
            LogText.Text = git
                ? $"$ git clone --depth 1 -b {BranchBox.SelectedItem} {UrlBox.Text.Trim()}"
                : "$ unzip long-term-memory-2.2.0.zip -d third-party";

            _installTimer.Start();
        }

        private void InstallTimer_Tick(object? sender, object e)
        {
            _progress = Math.Min(100, _progress + 2.4 + Random.Shared.NextDouble() * 2.6);
            InstallBar.Value = _progress;
            PercentText.Text = $"{(int)_progress}%";

            if (_progress < 50)
            {
                return;
            }

            PhaseText.Text = "正在安装依赖…";
            PhaseHintText.Text = "第 2/2 步 · npm install";

            if (_progress < 99)
            {
                return;
            }

            _installTimer.Stop();
            InstallBar.Value = 100;
            PercentText.Text = "100%";
            PhaseText.Text = "安装完成";
            PhaseHintText.Text = "扩展已启用，可在列表中关闭";
            LogText.Text += Environment.NewLine + $"已在 third-party 下创建目录：{RepositoryName(UrlBox.Text)}";
            LogText.Text += Environment.NewLine + "manifest.json 校验通过，扩展已启用。";

            InstalledName = GitTab.IsChecked == true ? RepositoryName(UrlBox.Text) : "长期记忆";
            Installed?.Invoke(this, EventArgs.Empty);
            Hide();
        }

        // ==================== 辅助 ====================

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

        private string? SuggestDefaultBranch()
        {
            string name = RepositoryName(UrlBox.Text);
            return string.IsNullOrEmpty(name) ? null : "main";
        }

        private static string RepositoryName(string value)
        {
            string url = value.Trim().TrimEnd('/');
            if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                url = url[..^4];
            }

            int slash = url.LastIndexOfAny(new[] { '/', ':' });
            string name = slash >= 0 ? url[(slash + 1)..] : url;
            return name.Trim();
        }
    }
}
