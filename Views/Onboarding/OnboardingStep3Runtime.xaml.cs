using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AstraBrew_Launcher.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace AstraBrew_Launcher.Views.Onboarding
{
    /// <summary>
    /// 第 3 步：检查运行环境。
    ///
    /// 两侧并列（内置 / 系统），各自列出 Node / npm / Git / Caddy / PM2 / WebView2 的状态。
    /// 「哪边能跑酒馆就选哪边」是自动的，但用户随时可以改成另一侧——
    /// 选择会**落到 <see cref="LauncherSettings.Runtime"/>**，因为启动器全局只有一套运行环境，
    /// 引导里选的和仪表盘显示的不是同一个东西，那是 bug。
    ///
    /// 内置侧的补全**一次装满**：Node → Git → Caddy → PM2。以前还让人在
    /// 「基础包 / 完整包」之间选，可引导阶段用户根本无从判断该选哪个，
    /// 少装了 Git 到第 3 步测速时又会失败——不如直接全装。
    /// </summary>
    public sealed partial class OnboardingStep3Runtime : UserControl, IOnboardingStep
    {
        public event EventHandler? StateChanged;

        private RuntimeProfile? _builtin;

        private RuntimeProfile? _system;

        private RuntimeSide? _selected;

        private CancellationTokenSource? _cts;

        private bool _busy;

        private bool _syncing;

        public OnboardingStep3Runtime()
        {
            InitializeComponent();
        }

        public bool CanContinue => !_busy && SelectedProfile is { BasicReady: true };

        public bool IsBusy => _busy;

        /// <summary>
        /// 页脚主按钮是否该承担「补全」：内置侧还没到能跑的程度时，把「下一步」换成「补全」，
        /// 点一下就地开装，装完按钮自己变回「下一步」。
        /// 系统侧不代装，所以那边永远是「下一步」（缺失时是禁用 + 原因提示）。
        /// </summary>
        public bool WantsPrimaryAction =>
            !_busy &&
            _selected == RuntimeSide.Builtin &&
            SelectedProfile is { BasicReady: false };

        /// <summary>页脚点了「补全」：与卡里那颗按钮走同一条路。</summary>
        public Task PrimaryActionAsync() => InstallAsync();

        public string? BlockingReason
        {
            get
            {
                if (_busy)
                {
                    return L.Get("Setup.S3.Blocking.Busy");
                }

                if (_selected is null)
                {
                    return L.Get("Setup.S3.Blocking.NoSide");
                }

                return SelectedProfile is { BasicReady: true }
                    ? null
                    : L.Get("Setup.S3.Blocking.Missing");
            }
        }

        private RuntimeProfile? SelectedProfile => _selected switch
        {
            RuntimeSide.Builtin => _builtin,
            RuntimeSide.System => _system,
            _ => null,
        };

        // ==================== 生命周期 ====================

        public async Task EnterAsync(CancellationToken token)
        {
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);

            await RefreshAsync(force: false);
        }

        public void Leave()
        {
            _cts?.Cancel();
            _busy = false;
        }

        public void Reset()
        {
            _builtin = null;
            _system = null;
            _selected = null;
            _busy = false;

            BuiltinComponents.Children.Clear();
            SystemComponents.Children.Clear();

            _syncing = true;
            OptionBuiltin.IsChecked = false;
            OptionSystem.IsChecked = false;
            _syncing = false;

            RaiseState();
        }

        private void RaiseState() => StateChanged?.Invoke(this, EventArgs.Empty);

        // ==================== 检测 ====================

        private async Task RefreshAsync(bool force)
        {
            _busy = true;
            RaiseState();

            try
            {
                _builtin = await RuntimeSetup.DescribeAsync(RuntimeSide.Builtin, force);
                _system = await RuntimeSetup.DescribeAsync(RuntimeSide.System, force);

                RenderComponents(BuiltinComponents, _builtin);
                RenderComponents(SystemComponents, _system);

                ApplyPill(BuiltinPill, BuiltinPillText, _builtin);
                ApplyPill(SystemPill, SystemPillText, _system);

                AutoSelectSide();
                UpdateActionRow();
            }
            finally
            {
                _busy = false;

                // 上面那次 UpdateActionRow 是在 _busy 还开着的时候跑的，按钮会被算成禁用；
                // 探测结束必须再算一次，否则切到内置也一直是灰的
                UpdateActionRow();
                RaiseState();
            }
        }

        private void AutoSelectSide()
        {
            if (_builtin is null || _system is null)
            {
                return;
            }

            _syncing = true;

            // 只有一侧能跑就选它；都能跑就沿用用户原本的偏好；
            // 两边都跑不起来时**默认选内置**——内置是启动器能自己装好的那一侧，
            // 选空只会让用户对着两张红卡发呆，还得自己猜该点哪边。
            RuntimeSide choice = _builtin.BasicReady && _system.BasicReady
                ? LauncherSettings.Runtime == RuntimeSource.Builtin ? RuntimeSide.Builtin : RuntimeSide.System
                : _builtin.BasicReady
                    ? RuntimeSide.Builtin
                    : _system.BasicReady
                        ? RuntimeSide.System
                        : RuntimeSide.Builtin;

            OptionBuiltin.IsChecked = choice == RuntimeSide.Builtin;
            OptionSystem.IsChecked = choice == RuntimeSide.System;

            _selected = choice;
            _syncing = false;

            // 两边都缺时把来源也落成内置：后面补全装的就是内置那套
            LauncherSettings.SetRuntime(choice == RuntimeSide.Builtin ? RuntimeSource.Builtin : RuntimeSource.System);
        }

        private void RenderComponents(Panel host, RuntimeProfile profile)
        {
            host.Children.Clear();

            foreach (RuntimeComponentInfo component in profile.Components)
            {
                host.Children.Add(BuildComponentRow(component));
            }
        }

        /// <summary>
        /// 单行组件：小图标 + 名称 + 版本（灰字）+ (i) + 状态胶囊，整行 24 高。
        /// 文案变短之后两行摆法会撑高卡片，而卡片又要左右等高——一行到底最稳。
        /// 「必需」不再占一个胶囊，改成名称前的强调色小圆点。
        /// </summary>
        private UIElement BuildComponentRow(RuntimeComponentInfo component)
        {
            var grid = new Grid { ColumnSpacing = 6, Height = 22 };

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                VerticalAlignment = VerticalAlignment.Center,
            };

            if (component.RequiredForBasic)
            {
                left.Children.Add(new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    VerticalAlignment = VerticalAlignment.Center,
                    Fill = Res("AccentFillColorDefaultBrush"),
                });
            }

            left.Children.Add(new TextBlock
            {
                Text = component.Name,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            // 版本跟在名字后面：短名称 + 版本读起来是一句「Node.js 22.20.0」
            left.Children.Add(new TextBlock
            {
                Text = component.Version ?? L.Get(component.Ready ? "Setup.S3.Component.Ready" : "Setup.S3.Component.Missing"),
                FontSize = 10.5,
                Margin = new Thickness(2, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = component.Ready
                    ? Res("TextFillColorTertiaryBrush")
                    : Res("SystemFillColorCriticalBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            grid.Children.Add(left);

            var pill = new Border
            {
                Padding = new Thickness(6, 1, 6, 2),
                CornerRadius = new CornerRadius(7),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(component.Ready
                    ? Windows.UI.Color.FromArgb(0x1F, 0x0F, 0x7B, 0x0F)
                    : Windows.UI.Color.FromArgb(0x1F, 0xC4, 0x2B, 0x1C)),
                Child = new TextBlock
                {
                    Text = L.Get(component.Ready ? "Setup.S3.Pill.Ready" : "Setup.S3.Pill.Missing"),
                    FontSize = 9.5,
                    Foreground = component.Ready
                        ? Res("SystemFillColorSuccessBrush")
                        : Res("SystemFillColorCriticalBrush"),
                },
            };
            Grid.SetColumn(pill, 1);
            grid.Children.Add(pill);

            var info = new Button
            {
                Style = (Style)Application.Current.Resources["OnboardingInfoButtonStyle"],
                Content = new FontIcon { Glyph = "\uE946", FontSize = 11 },
            };
            AutomationProperties.SetName(info, L.F("Setup.S3.Component.Aria", component.Name));
            info.Click += (_, _) => ShowInfo(info, component);

            Grid.SetColumn(info, 2);
            grid.Children.Add(info);

            return grid;
        }

        private void ShowInfo(FrameworkElement anchor, RuntimeComponentInfo component)
        {
            var panel = new StackPanel { Spacing = 6, MaxWidth = 280 };
            panel.Children.Add(new TextBlock
            {
                Text = component.Purpose,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(new TextBlock
            {
                Text = component.Consequence,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Res("TextFillColorSecondaryBrush"),
            });

            if (component.Version is { Length: > 0 } version)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = L.F("Setup.S3.Component.Version", version),
                    FontSize = 11,
                    Foreground = Res("TextFillColorTertiaryBrush"),
                });
            }

            var flyout = new Flyout { Content = panel };
            flyout.ShowAt(anchor);
        }

        private void ApplyPill(Border pill, TextBlock text, RuntimeProfile profile)
        {
            text.Text = profile.BasicReady ? profile.Summary : L.Get("Setup.S3.Pill.StillMissing");

            pill.Background = new SolidColorBrush(profile.BasicReady
                ? Windows.UI.Color.FromArgb(0x1F, 0x0F, 0x7B, 0x0F)
                : Windows.UI.Color.FromArgb(0x1F, 0xD8, 0x9B, 0x2B));
        }

        // ==================== 交互 ====================

        private void Side_Checked(object sender, RoutedEventArgs e)
        {
            if (_syncing)
            {
                return;
            }

            RuntimeSide side = OptionBuiltin.IsChecked == true ? RuntimeSide.Builtin : RuntimeSide.System;

            _selected = side;
            LauncherSettings.SetRuntime(side == RuntimeSide.Builtin ? RuntimeSource.Builtin : RuntimeSource.System);

            // 切过来只刷新卡片与按钮，**不自动弹安装窗**：
            // 用户可能只是想看看这边缺什么，弹窗跳出来会像「已经动手装了」
            UpdateActionRow();
            RaiseState();
        }

        private void UpdateActionRow()
        {
            RuntimeProfile? profile = SelectedProfile;
            bool builtin = _selected == RuntimeSide.Builtin;

            // 系统侧不能代装：那颗按钮点了只会弹一张说明，摆出来纯属噪音
            InstallButton.Visibility = profile is null || !builtin ? Visibility.Collapsed : Visibility.Visible;

            if (profile is null)
            {
                InstallButtonText.Text = L.Get("Setup.S3.Action.FillAll");
                SystemHelpButton.Visibility = Visibility.Collapsed;
                return;
            }

            // 永装完整包：缺几项就排几项，全齐了就把按钮标成「已齐全」并禁掉
            int pending = builtin
                ? RuntimeSetup.Plan(RuntimeSide.Builtin, RuntimePackage.Full, profile).Count
                : 0;

            InstallButtonText.Text = pending == 0
                ? L.Get("Setup.S3.Action.AllSet")
                : L.Get("Setup.S3.Action.FillAll");
            InstallButton.IsEnabled = !_busy && builtin && pending > 0;

            ToolTipService.SetToolTip(
                InstallButton,
                builtin && pending == 0 ? L.Get("Setup.S3.Tip.Nothing") : null);

            SystemHelpButton.Visibility = builtin ? Visibility.Collapsed : Visibility.Visible;
            SystemHelpButton.IsEnabled = !_busy;
        }

        private async void Install_Click(object sender, RoutedEventArgs e)
        {
            await InstallAsync();
        }

        /// <summary>系统侧的「缺什么？」：弹缺件清单，告诉用户去官网装。</summary>
        private async void SystemHelp_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedProfile is { } profile)
            {
                await ShowSystemHelpAsync(profile);
            }
        }

        private async Task InstallAsync()
        {
            if (_selected is not { } side || _busy)
            {
                return;
            }

            RuntimeProfile? profile = SelectedProfile;

            if (profile is null)
            {
                return;
            }

            if (side == RuntimeSide.System)
            {
                await ShowSystemHelpAsync(profile);
                return;
            }

            IReadOnlyList<RuntimeTask> tasks = RuntimeSetup.Plan(side, RuntimePackage.Full, profile);

            if (tasks.Count == 0)
            {
                AppNotify.Success(
                    L.Get("Setup.S3.Notify.Ready.Title"),
                    L.Get("Setup.S3.Notify.Ready.Full"));
                return;
            }

            _busy = true;
            RaiseState();

            try
            {
                await InstallTaskDialog.ShowAsync(
                    XamlRoot,
                    tasks,
                    autoStart: true,
                    note: L.Get("Setup.S3.Note.Full"));
            }
            finally
            {
                _busy = false;
            }

            // 装完重新探一次：卡片上的版本号必须来自磁盘，不能凭猜
            await RefreshAsync(force: true);
        }

        /// <summary>系统侧不代装：给出缺什么、去哪下，装完回来重新检测。</summary>
        private async Task ShowSystemHelpAsync(RuntimeProfile profile)
        {
            List<RuntimeComponentInfo> missing = profile.Components
                .Where(component => !component.Ready && component.Key != RuntimeComponentKey.WebView2)
                .ToList();

            var body = new StackPanel { Spacing = 10 };

            body.Children.Add(new TextBlock
            {
                Text = L.Get(missing.Count == 0
                    ? "Setup.S3.System.AllSet"
                    : "Setup.S3.System.Missing"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });

            foreach (RuntimeComponentInfo component in missing)
            {
                body.Children.Add(new TextBlock
                {
                    Text = $"· {component.Name}：{component.Consequence}",
                    FontSize = 11.5,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Res("TextFillColorSecondaryBrush"),
                });
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = L.Get("Setup.S3.System.Dialog.Title"),
                Content = body,
                PrimaryButtonText = L.Get("Setup.S3.System.Dialog.Primary"),
                CloseButtonText = L.Get("Setup.S3.System.Dialog.Close"),
                DefaultButton = ContentDialogButton.Primary,
            };
            dialog.Resources["ControlCornerRadius"] = new CornerRadius(8);

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await RefreshAsync(force: true);
            }
        }

        private async void Recheck_Click(object sender, RoutedEventArgs e)
        {
            await RefreshAsync(force: true);
        }

        private static Brush Res(string key) =>
            Application.Current.Resources.TryGetValue(key, out object value) && value is Brush brush
                ? brush
                : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }
}
