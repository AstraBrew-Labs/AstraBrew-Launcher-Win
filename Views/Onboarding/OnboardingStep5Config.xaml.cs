using System;
using System.Threading;
using System.Threading.Tasks;
using AstraBrew_Launcher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AstraBrew_Launcher.Views.Onboarding
{
    /// <summary>
    /// 第 5 步：酒馆配置。
    ///
    /// 酒馆没有 <c>config.yaml</c> 起不来（端口、模型、扩展全从这份文件读），
    /// 而实例装完并不代表配置也有——所以这一步单独判定：
    /// · 已存在 → 显示路径与端口，「下一步」直接可点；
    /// · 不存在 → 页脚按钮变成「创建」，点一下下载一份官方默认配置，成功后自动变回「下一步」。
    ///
    /// 只判定「有没有」，不在这里做端口/模型编辑：那些在「酒馆配置」页里有完整表单，
    /// 引导里塞一套精简版只会让人以为两处设置不一样。
    /// </summary>
    public sealed partial class OnboardingStep5Config : UserControl, IOnboardingStep
    {
        public event EventHandler? StateChanged;

        private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x16, 0xA3, 0x4A));

        private static readonly Brush PendingBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B));

        private CancellationTokenSource? _cts;

        private bool _busy;

        private bool _hasConfig;

        public OnboardingStep5Config()
        {
            InitializeComponent();
        }

        public bool CanContinue => _hasConfig && !_busy;

        public bool IsBusy => _busy;

        public string? BlockingReason => _busy
            ? L.Get("Setup.S5.Blocking.Busy")
            : L.Get("Setup.S5.Blocking.Missing");

        // 没有配置时把页脚那颗「下一步」换成「创建」：这一步除了创建没有别的事可做，
        // 让它灰着再让人去页面里找按钮，是最没必要的一步
        public bool WantsPrimaryAction => !_busy && !_hasConfig;

        public string PrimaryActionLabelKey => "Setup.Action.CreateConfig";

        public string PrimaryActionTipKey => "Setup.Action.CreateConfigTip";

        // ==================== 生命周期 ====================

        public Task EnterAsync(CancellationToken token)
        {
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);

            Refresh();
            return Task.CompletedTask;
        }

        public void Leave()
        {
            _cts?.Cancel();
        }

        public void Reset()
        {
            _busy = false;
            _hasConfig = false;

            StateIcon.Glyph = "\uE9D9";
            StateTitle.Text = string.Empty;
            StateBody.Text = string.Empty;
            PathText.Text = string.Empty;
            PathText.Visibility = Visibility.Collapsed;
            NoteText.Text = string.Empty;

            Raise();
        }

        /// <summary>页脚主按钮：下载一份默认配置。</summary>
        public Task PrimaryActionAsync() => CreateAsync();

        // ==================== 状态 ====================

        private void Refresh()
        {
            _hasConfig = GlobalTavernConfig.HasGlobalConfig;

            if (_hasConfig)
            {
                StateIcon.Glyph = "\uE73E"; // CheckMark
                StateIcon.Foreground = SuccessBrush;
                StateTitle.Text = L.Get("Setup.S5.Found.Title");

                int port = TavernRuntime.ReadPort(GlobalTavernConfig.GlobalPath);
                StateBody.Text = L.F("Setup.S5.Found.Body", port);

                PathText.Text = GlobalTavernConfig.GlobalPath;
                PathText.Visibility = Visibility.Visible;
                NoteText.Text = L.Get("Setup.S5.Note.Found");
            }
            else
            {
                StateIcon.Glyph = "\uE9D9"; // Document
                StateIcon.Foreground = PendingBrush;
                StateTitle.Text = L.Get("Setup.S5.Missing.Title");
                StateBody.Text = L.Get("Setup.S5.Missing.Body");

                PathText.Visibility = Visibility.Collapsed;
                NoteText.Text = L.Get("Setup.S5.Note.Missing");
            }

            Raise();
        }

        private async Task CreateAsync()
        {
            if (_busy)
            {
                return;
            }

            _busy = true;
            Raise();

            CancellationToken token = _cts?.Token ?? CancellationToken.None;

            try
            {
                GlobalConfigFetchResult result = await GlobalTavernConfig.FetchAsync(token);

                if (result.Success)
                {
                    // 先刷新界面：它能立刻反映「已创建」，不必等通知消失
                    _busy = false;
                    Refresh();

                    AppNotify.Success(
                        L.Get("Setup.S5.Notify.Created.Title"),
                        L.F("Setup.S5.Notify.Created.Body", result.Source ?? L.Get("Miss.MirrorName")));
                    return;
                }

                AppNotify.Error(
                    L.Get("Setup.S5.Notify.Failed.Title"),
                    result.Error ?? L.Get("Setup.S5.Notify.Failed.Body"));

                StateBody.Text = L.Get("Setup.S5.Failed.Body");
            }
            catch (OperationCanceledException)
            {
                // 用户离开这一步：什么都不弹，页面下次进来会重新判定
            }
            catch (Exception ex)
            {
                AppNotify.Error(L.Get("Setup.S5.Notify.Failed.Title"), ex.Message);
                StateBody.Text = L.Get("Setup.S5.Failed.Body");
            }
            finally
            {
                _busy = false;
                Raise();
            }
        }

        private void Raise() => StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
