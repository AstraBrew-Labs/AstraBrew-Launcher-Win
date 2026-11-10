using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AstraBrew_Launcher.Services;
using AstraBrew_Launcher.Views.Controls;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AstraBrew_Launcher.Views.Onboarding
{
    /// <summary>
    /// 第 2 步：检查网络环境。
    ///
    /// 顺序是有讲究的——先 PING 再 HTTPS，最后才问 GitHub：
    /// 「没网」和「网通了解析坏了」要分开告诉用户，否则后面测 GitHub 只会得到一堆看不懂的报错。
    ///
    /// API 返回 403 是**能连上但被限流**，与「连不上」完全是两件事：
    /// 这种情况给用户两个明确的选择（启用加速重试 / 过会再说），而不是让他卡在这一步。
    /// </summary>
    public sealed partial class OnboardingStep2Network : UserControl, IOnboardingStep
    {
        public event EventHandler? StateChanged;

        private readonly Dictionary<(DownloadChannel Channel, bool Accelerated, bool UseProxy), TextBlock> _mirrorRows = new();

        private CancellationTokenSource? _cts;

        /// <summary>
        /// 本轮检查（含克隆测速）专用的取消源。每次开新一轮都换一个，
        /// 上一轮连同它正在拉的那几条仓库一起作废。
        /// </summary>
        private CancellationTokenSource? _runCts;

        /// <summary>
        /// 第几轮检查。**只有最新一轮**能改界面和 <see cref="_busy"/>：
        /// 被取代的那一轮收尾得晚，让它写下去就会把新结果盖回旧环境的数据。
        /// </summary>
        private int _runId;

        private bool _busy;

        private bool _pingOk;

        private bool _dnsOk;

        private bool _apiOk;

        private bool _rawOk;

        private bool _githubSkipped;

        private bool _mirrorOk;

        private string? _blocking;

        /// <summary>403 时该往哪边修：开加速、关加速、还是只能等一会儿。</summary>
        private RateLimitFix _fix = RateLimitFix.EnableAccel;

        public OnboardingStep2Network()
        {
            InitializeComponent();
            BuildMirrorRows();
        }

        public bool CanContinue => !_busy && CanAdvance(out _);

        public bool IsBusy => _busy;

        public string? BlockingReason
        {
            get
            {
                CanAdvance(out string reason);
                return reason;
            }
        }

        // ==================== 生命周期 ====================

        public Task EnterAsync(CancellationToken token)
        {
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);

            if (!_busy)
            {
                _ = RunAsync();
            }

            return Task.CompletedTask;
        }

        public void Leave()
        {
            _runCts?.Cancel();
            _cts?.Cancel();
            _busy = false;
        }

        public void Reset()
        {
            _pingOk = false;
            _dnsOk = false;
            _apiOk = false;
            _rawOk = false;
            _githubSkipped = false;
            _mirrorOk = false;
            _busy = false;
            _blocking = null;

            RateLimitCard.Visibility = Visibility.Collapsed;
            _fix = RateLimitFix.EnableAccel;
            SummaryText.Text = string.Empty;

            foreach (CheckRow row in new[] { RowPing, RowDns, RowApi, RowRaw })
            {
                row.SetState(CheckState.Pending);
            }

            // 上一轮还在拉的话先掐掉：重置了状态却让它继续跑，行里会自己冒出旧进度
            AbortRun();

            ResetMirrorRows();

            RaiseState();
        }

        /// <summary>把测速那几行恢复成「等着」，并清掉上一轮留下的颜色与字重。</summary>
        private void ResetMirrorRows()
        {
            foreach (TextBlock status in _mirrorRows.Values)
            {
                status.Text = "等着";
                status.FontWeight = FontWeights.Normal;
                status.Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
            }
        }

        /// <summary>
        /// 立刻掐掉正在跑的那一轮，不等新一轮开始。
        /// 用在「点了开加速、但同步全局 git 还要花一两秒」这种空档里——
        /// 这一两秒里旧的那一轮仍在按旧出口拉仓库。
        /// </summary>
        private void AbortRun()
        {
            _runCts?.Cancel();
            _runId++;
        }

        /// <summary>
        /// 开一轮新的检查：**先把上一轮彻底掐掉**。
        /// 用户点「开加速再试」时，测速区很可能正在拉仓库（每条约 100 MB、几十秒），
        /// 不掐掉的话它会继续按**旧出口**跑，界面上的进度照样在爬——
        /// 看起来就像「点了没反应，还在没有加速的环境里测」。
        /// </summary>
        private (int RunId, CancellationToken Token) BeginRun()
        {
            _runCts?.Cancel();
            _runCts?.Dispose();
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(_cts?.Token ?? CancellationToken.None);

            int runId = ++_runId;
            return (runId, _runCts.Token);
        }

        private void RaiseState() => StateChanged?.Invoke(this, EventArgs.Empty);

        /// <summary>
        /// 「能不能继续」的唯一判据。**不要**为了方便把它放宽成永远 true：
        /// 网络不通就往下走，用户会在第 4 步对着装不完的内置实例发呆。
        /// </summary>
        private bool CanAdvance(out string reason)
        {
            reason = string.Empty;

            if (_busy)
            {
                reason = "正在检查，等一下";
                return false;
            }

            if (!_pingOk)
            {
                reason = "网没通，看看 Wi-Fi 或网线";
                return false;
            }

            if (!_dnsOk)
            {
                reason = "网通了但打不开网页，看看代理";
                return false;
            }

            if (!_apiOk && !_githubSkipped)
            {
                reason = _blocking ?? "连不上 GitHub 接口";
                return false;
            }

            if (!_rawOk && !_githubSkipped)
            {
                reason = _blocking ?? "取不到酒馆文件，开加速再试";
                return false;
            }

            if (!_mirrorOk)
            {
                reason = "四条线都不通，下不了酒馆";
                return false;
            }

            return true;
        }

        // ==================== 主流程 ====================

        private async Task RunAsync()
        {
            (int runId, CancellationToken token) = BeginRun();

            _busy = true;
            _githubSkipped = false;
            _blocking = null;
            RateLimitCard.Visibility = Visibility.Collapsed;
            SummaryText.Text = string.Empty;
            RaiseState();

            try
            {
                foreach (CheckRow row in new[] { RowPing, RowDns, RowApi, RowRaw })
                {
                    row.SetState(CheckState.Pending);
                }

                NetProbeResult ping = await NetworkProbe.PingAsync(token);
                if (runId != _runId)
                {
                    return;
                }

                _pingOk = ping.Ok;
                ApplyProbe(RowPing, ping.Ok, ping.LatencyMs, ping.Error, null);

                if (!_pingOk)
                {
                    _busy = false;
                    SummaryText.Text = "上不了网。检查 Wi-Fi、网线或代理，再点「再查一次」。";
                    RaiseState();
                    return;
                }

                NetProbeResult dns = await NetworkProbe.DnsAsync(token);
                if (runId != _runId)
                {
                    return;
                }

                _dnsOk = dns.Ok;
                ApplyProbe(RowDns, dns.Ok, dns.LatencyMs, dns.Error, null);

                if (!_dnsOk)
                {
                    _busy = false;
                    SummaryText.Text = "网络是通的，但网页打不开。看看代理，再查一次。";
                    RaiseState();
                    return;
                }

                IReadOnlyList<GithubProbe> probes = GithubConnectivity.BuildProbes();
                GithubProbe apiProbe = probes.First(probe => probe.Key == "api");
                GithubProbe rawProbe = probes.First(probe => probe.Key == "raw");

                Task<GithubProbeResult> apiTask = GithubConnectivity.RunAsync(apiProbe, token);
                Task<GithubProbeResult> rawTask = GithubConnectivity.RunAsync(rawProbe, token);

                GithubProbeResult api = await apiTask;
                GithubProbeResult raw = await rawTask;

                // 中途被新一轮取代（用户刚开了加速）：这一轮的数字只属于旧出口，不能落下来
                if (runId != _runId)
                {
                    return;
                }

                _apiOk = api.Ok;
                _rawOk = raw.Ok;

                ApplyProbe(RowApi, api.Ok, api.LatencyMs, api.Error, api.Route);
                ApplyProbe(RowRaw, raw.Ok, raw.LatencyMs, raw.Error, raw.Route);

                // API 被限流最常见，但 Raw 同样会 403——两条任何一条撞上都要给出口，
                // 以前只看 API，于是 Raw 那行写着「HTTP 403」却没有卡片，用户只能干瞪眼
                if (api.IsRateLimited || raw.IsRateLimited)
                {
                    ShowRateLimit(api.IsRateLimited ? api : raw);
                    _busy = false;
                    RaiseState();
                    return;
                }

                // 非 403 的失败多半是域名被阻断（TLS 握手失败）：这时候最该给的是「开加速」的出口，
                // 而不是只把那一行标红。测速照跑，用户还能顺便看到哪条镜像快。
                if (!raw.Ok)
                {
                    ShowBlocked(raw);
                }

                // 测速区在卡片下半屏，不滚过去的话用户只能看到两行在动
                MirrorHost.StartBringIntoView();

                await StartMirrorsAsync(token, runId);

                if (runId != _runId)
                {
                    return;
                }

                _busy = false;
                RaiseState();
            }
            catch (OperationCanceledException)
            {
                if (runId == _runId)
                {
                    _busy = false;
                    RaiseState();
                }
            }
            finally
            {
                // 被取代的那一轮什么都别碰：_busy 现在归新一轮管
                if (runId == _runId)
                {
                    if (!RateLimitCard.Visibility.Equals(Visibility.Visible))
                    {
                        _busy = false;
                    }

                    RaiseState();
                }
            }
        }

        private enum RateLimitFix
        {
            EnableAccel,
            DisableAccel,
            Recheck,
        }

        private void ApplyProbe(CheckRow row, bool ok, int? latency, string? error, string? route)
        {
            if (ok)
            {
                // 加速开着时把「实际走通的那条线」写出来：加速被限流、回落直连成功，
                // 用户得看得见，否则会以为加速在生效
                string via = GithubAccel.Enabled && route is { Length: > 0 } ? $" · {route}" : string.Empty;

                row.SetState(CheckState.Ready, latency is { } ms ? $"{ms} ms{via}" : $"正常{via}");
                return;
            }

            row.SetState(CheckState.Missing, error is { Length: > 0 } ? error : "失败");
        }

        /// <summary>
        /// 403 卡片：文案与按钮都跟着「是谁被限流」走。
        /// 明明加速开着却叫用户「开加速」是最招人烦的那种提示。
        /// </summary>
        private void ShowRateLimit(GithubProbeResult hit)
        {
            bool accelOn = GithubAccel.Enabled;

            if (hit.Route is null)
            {
                // 两条线路都 403：换线没用，只能等
                _fix = RateLimitFix.Recheck;
                _blocking = "GitHub 限流了，过一会儿再试";
                RateLimitText.Text = "两条线路都被 GitHub 限流了（403）。这不是你的网络问题，过一会儿再点「再查一次」。";
                FixRouteButton.Content = "再查一次";
            }
            else if (hit.Route == "加速")
            {
                // 加速线路被限流、直连也不通：关掉加速再试
                _fix = RateLimitFix.DisableAccel;
                _blocking = "加速线路被限流，关掉加速再试";
                RateLimitText.Text = "加速线路被限流了（403），直连也没通。可以关掉加速走直连，或者过一会儿再试。";
                FixRouteButton.Content = "关加速再试";
            }
            else
            {
                // 只试了直连（加速没开）：建议开加速
                _fix = RateLimitFix.EnableAccel;
                _blocking = "GitHub 限流了，开加速再试";
                RateLimitText.Text = accelOn
                    ? "GitHub 限流了（403）。过一会儿再试，或者先把加速打开换条线路。"
                    : "直连被 GitHub 限流了（403）。开加速换条线路就能继续。";
                FixRouteButton.Content = "开加速再试";
            }

            RateLimitCard.Visibility = Visibility.Visible;
            SummaryText.Text = "限流时拿不到列表，下载测速还能继续。";
        }

        /// <summary>
        /// 通道不通（TLS 握手失败 / 连接被重置 / 超时）时的提示。
        /// 与 403 共用一张卡片：用户要的是「怎么才能通」，不是错误分类；
        /// 加速已经开着时不再劝他开加速（那是最招人烦的一种提示），改成让他查代理。
        /// </summary>
        private void ShowBlocked(GithubProbeResult hit)
        {
            string reason = hit.Error is { Length: > 0 } error ? error : "连不上";

            if (GithubAccel.Enabled)
            {
                _fix = RateLimitFix.Recheck;
                _blocking = "GitHub 连不上，检查代理后再试";
                RateLimitText.Text =
                    $"GitHub 文件地址连不上：{reason}。加速已经开着，检查一下代理或网络，再点「再查一次」。";
                FixRouteButton.Content = "再查一次";
            }
            else
            {
                _fix = RateLimitFix.EnableAccel;
                _blocking = "GitHub 连不上，开加速再试";
                RateLimitText.Text =
                    $"GitHub 文件地址连不上：{reason}。打开加速换条线路就能继续。";
                FixRouteButton.Content = "开加速再试";
            }

            RateLimitCard.Visibility = Visibility.Visible;
            SummaryText.Text = "拿不到酒馆文件，先开加速换条线路。";
        }

        // ==================== 403 分支 ====================

        private async void FixRoute_Click(object sender, RoutedEventArgs e)
        {
            FixRouteButton.IsEnabled = false;

            // 先把正在跑的那一轮停掉：同步全局 git 配置要起子进程，
            // 这一两秒里旧的那一轮还在按旧出口拉仓库，进度也还在跳
            AbortRun();
            RateLimitCard.Visibility = Visibility.Collapsed;

            try
            {
                if (_fix is RateLimitFix.EnableAccel or RateLimitFix.DisableAccel)
                {
                    bool enable = _fix == RateLimitFix.EnableAccel;

                    LauncherSettings.SetGithubAcceleration(enable);

                    // 全局 git 的 insteadOf 也一起同步：不然「加速开着但 git 不加速」会让人以为开关没生效
                    GitConfigResult? sync = await GitGlobalConfig.SyncAsync(enable);

                    if (sync is { Ok: false } failed)
                    {
                        AppNotify.Warning(enable ? "加速开了，命令行那部分没设上" : "关加速时命令行没改回来", failed.Message);
                    }
                    else
                    {
                        AppNotify.Success(enable ? "加速已打开" : "加速已关闭", "正在重新检查…");
                    }
                }
            }
            catch (Exception ex)
            {
                AppNotify.Warning("切换线路时出错了", ex.Message);
            }
            finally
            {
                FixRouteButton.IsEnabled = true;
            }

            _ = RunAsync();
        }

        private async void SkipGithub_Click(object sender, RoutedEventArgs e)
        {
            _githubSkipped = true;
            RateLimitCard.Visibility = Visibility.Collapsed;
            SummaryText.Text = "跳过了 GitHub 检查，版本列表只能用旧的。";
            RaiseState();

            // 这里也开新一轮：跳过时测速区可能正有一轮在跑，不掐掉会出现两份结果互相覆盖
            (int runId, CancellationToken token) = BeginRun();
            await StartMirrorsAsync(token, runId);
        }

        // ==================== 镜像测速 ====================

        private void BuildMirrorRows()
        {
            foreach (MirrorChannel line in MirrorBenchmark.BuildChannels())
            {
                var grid = new Grid
                {
                    Padding = new Thickness(6, 6, 6, 6),
                    ColumnSpacing = 10,
                };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                grid.Children.Add(new TextBlock
                {
                    Text = line.Name,
                    FontSize = 12.5,
                    VerticalAlignment = VerticalAlignment.Center,
                });

                var status = new TextBlock
                {
                    Text = "等着",
                    MaxWidth = 240,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                };

                Grid.SetColumn(status, 1);
                grid.Children.Add(status);

                _mirrorRows[(line.Channel, line.Accelerated, line.UseProxy)] = status;
                MirrorHost.Children.Add(grid);
            }
        }

        private async Task StartMirrorsAsync(CancellationToken token, int runId)
        {
            _busy = true;
            RaiseState();

            // 先把行清回「等着」：上一轮的字和颜色留着，新的一开始看不出是在重测
            ResetMirrorRows();

            // 回调会排队进 UI 线程：上一轮被掐掉后仍可能有几条在路上，
            // 认一下轮次，别让旧进度写到新一轮的行里
            var progress = new Progress<MirrorTrial>(trial =>
            {
                if (runId == _runId)
                {
                    ApplyTrial(trial, selected: false);
                }
            });

            var live = new Progress<MirrorProgress>(update =>
            {
                if (runId == _runId)
                {
                    ApplyProgress(update);
                }
            });

            MirrorBenchmarkResult result = await MirrorBenchmark.RunAsync(progress, live, token);

            if (runId != _runId)
            {
                return;
            }

            foreach (MirrorTrial trial in result.Trials)
            {
                ApplyTrial(trial, trial == result.Best);
            }

            if (result.Best is { } best)
            {
                _mirrorOk = true;
                await MirrorBenchmark.ApplyAsync(best, token);

                string elapsed = MirrorBenchmark.DescribeElapsed(best.ElapsedMs);

                SummaryText.Text = $"以后从 {best.Name} 下载，拉完用了 {elapsed}";
                AppNotify.Success("已经选好最快的线路", $"{best.Name} · {elapsed}");
            }
            else
            {
                _mirrorOk = false;
                SummaryText.Text = result.Error is { Length: > 0 } error
                    ? $"测速没成功，还是从 {LauncherSettings.ChannelName(LauncherSettings.Channel)} 下载（{error}）"
                    : "测速没成功，下载线路没变。";
            }

            _busy = false;
            RaiseState();

            // 四行 + 结论一屏放不下：跑完把结论滚出来，用户不用自己找
            SummaryText.StartBringIntoView();
        }

        /// <summary>
        /// 拉取中的实时进度。git 每秒刷很多次，服务层已经按整数百分比节流过了。
        /// </summary>
        private void ApplyProgress(MirrorProgress update)
        {
            if (!_mirrorRows.TryGetValue((update.Channel, update.Accelerated, update.UseProxy), out TextBlock? status))
            {
                return;
            }

            status.Text = update.Speed is { Length: > 0 } speed
                ? $"{update.Phase} {update.Percent}% · {speed}"
                : $"{update.Phase} {update.Percent}%";

            status.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        }

        private void ApplyTrial(MirrorTrial trial, bool selected)
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(() => ApplyTrial(trial, selected));
                return;
            }

            if (!_mirrorRows.TryGetValue((trial.Channel, trial.Accelerated, trial.UseProxy), out TextBlock? status))
            {
                return;
            }

            if (trial.Error == "已取消")
            {
                // 别人先拉完了，这条是我们主动停的：写成「没等它拉完」，别让人以为它坏了
                status.Text = "没等它拉完";
                status.Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
                return;
            }

            if (!trial.Ok)
            {
                status.Text = trial.Error is { Length: > 0 } error ? error : "没通过";
                status.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
                return;
            }

            string elapsed = MirrorBenchmark.DescribeElapsed(trial.ElapsedMs);

            status.Text = selected ? $"{elapsed} · 用这条" : elapsed;

            status.Foreground = selected
                ? (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"]
                : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

            if (selected)
            {
                status.FontWeight = FontWeights.SemiBold;
            }
        }
    }
}
