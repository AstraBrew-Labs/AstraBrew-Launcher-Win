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
    /// 基础包只要求 Node + npm：普通模式跑酒馆就需要这些。Git / Caddy / PM2 属于完整包，
    /// 缺了不挡路，装不齐也能点下一步。
    /// </summary>
    public sealed partial class OnboardingStep3Runtime : UserControl, IOnboardingStep
    {
        public event EventHandler? StateChanged;

        private RuntimeProfile? _builtin;

        private RuntimeProfile? _system;

        private RuntimeSide? _selected;

        private RuntimePackage _package = RuntimePackage.Basic;

        private CancellationTokenSource? _cts;

        private bool _busy;

        private bool _syncing;

        public OnboardingStep3Runtime()
        {
            InitializeComponent();
        }

        public bool CanContinue => !_busy && SelectedProfile is { BasicReady: true };

        public bool IsBusy => _busy;

        public string? BlockingReason
        {
            get
            {
                if (_busy)
                {
                    return "正在检查或安装";
                }

                if (_selected is null)
                {
                    return "先选一边";
                }

                return SelectedProfile is { BasicReady: true }
                    ? null
                    : "这边还缺点东西，先点「开始安装」";
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
            _package = RuntimePackage.Basic;
            _busy = false;

            BuiltinComponents.Children.Clear();
            SystemComponents.Children.Clear();

            _syncing = true;
            PackageBasic.IsChecked = true;
            PackageFull.IsChecked = false;
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

            // 只有一侧能跑就选它；都能跑就沿用用户原本的偏好；都不能跑就都不选
            RuntimeSide? choice = _builtin.BasicReady && _system.BasicReady
                ? LauncherSettings.Runtime == RuntimeSource.Builtin ? RuntimeSide.Builtin : RuntimeSide.System
                : _builtin.BasicReady
                    ? RuntimeSide.Builtin
                    : _system.BasicReady
                        ? RuntimeSide.System
                        : null;

            OptionBuiltin.IsChecked = choice == RuntimeSide.Builtin;
            OptionSystem.IsChecked = choice == RuntimeSide.System;

            _selected = choice;
            _syncing = false;

            if (choice is { } side)
            {
                LauncherSettings.SetRuntime(side == RuntimeSide.Builtin ? RuntimeSource.Builtin : RuntimeSource.System);
            }
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
                Text = component.Version ?? (component.Ready ? "有了" : "没找到"),
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
                    Text = component.Ready ? "好了" : "缺",
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
            AutomationProperties.SetName(info, $"{component.Name} 是干什么的");
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
                    Text = $"版本 {version}",
                    FontSize = 11,
                    Foreground = Res("TextFillColorTertiaryBrush"),
                });
            }

            var flyout = new Flyout { Content = panel };
            flyout.ShowAt(anchor);
        }

        private void ApplyPill(Border pill, TextBlock text, RuntimeProfile profile)
        {
            text.Text = profile.BasicReady ? profile.Summary : "还缺东西";

            pill.Background = new SolidColorBrush(profile.BasicReady
                ? Windows.UI.Color.FromArgb(0x1F, 0x0F, 0x7B, 0x0F)
                : Windows.UI.Color.FromArgb(0x1F, 0xD8, 0x9B, 0x2B));
        }

        // ==================== 交互 ====================

        private void Package_Checked(object sender, RoutedEventArgs e)
        {
            if (_syncing)
            {
                return;
            }

            _package = PackageFull.IsChecked == true ? RuntimePackage.Full : RuntimePackage.Basic;
            PackageHint.Text = _package == RuntimePackage.Basic
                ? "只装跑酒馆最少的两样"
                : "再多装三样，装不齐也能继续";

            UpdateActionRow();
        }

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

            InstallButton.Visibility = profile is null ? Visibility.Collapsed : Visibility.Visible;

            if (profile is null)
            {
                InstallButtonText.Text = "开始安装";
                SystemHelpButton.Visibility = Visibility.Collapsed;
                return;
            }

            // 当前这一包到底还剩几项要装：只装必需且已经齐了就是 0 项，
            // 这时按钮必须禁掉——否则点下去只弹一句「这边已经齐了」，用户以为能补全
            int pending = builtin
                ? RuntimeSetup.Plan(RuntimeSide.Builtin, _package, profile).Count
                : 0;

            // 完整包全齐时留一个「重装一遍」的入口（换机器、装坏了要用），
            // 只装必需全齐就不给重装——那一步本来就没东西可装
            bool canReinstall = builtin && _package == RuntimePackage.Full && profile.FullReady;
            bool nothingToInstall = pending == 0 && !canReinstall;

            InstallButtonText.Text = !builtin
                ? profile.BasicReady && profile.FullReady
                    ? "重装一遍"
                    : profile.BasicReady
                        ? "补上缺的"
                        : "开始安装"
                : canReinstall
                    ? "重装一遍"
                    : nothingToInstall
                        ? "已经装齐了"
                        : profile.BasicReady
                            ? "补上缺的"
                            : "开始安装";

            // 自动安装只有内置侧支持：系统侧点它只会弹一张说明，摆成可点等于骗人
            InstallButton.IsEnabled = !_busy && builtin && !nothingToInstall;

            ToolTipService.SetToolTip(
                InstallButton,
                !builtin
                    ? "系统环境只能自己装，装完点「再查一次」"
                    : nothingToInstall
                        ? "当前这一包已经齐了，没有要装的"
                        : null);

            SystemHelpButton.Visibility = profile is null || builtin
                ? Visibility.Collapsed
                : Visibility.Visible;
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

            IReadOnlyList<RuntimeTask> tasks = RuntimeSetup.Plan(side, _package, profile);

            if (tasks.Count == 0)
            {
                AppNotify.Success("这边已经齐了", _package == RuntimePackage.Basic
                    ? "最少的两样都在，可以继续了。"
                    : "该有的都装好了。");
                return;
            }

            _busy = true;
            RaiseState();

            try
            {
                // 清单只有 Node.js 时别让人以为漏了：把「当前包的范围」写在弹窗顶上
                string? note = _package == RuntimePackage.Basic
                    ? "这次只装基础包（Node.js + npm）。Git、Caddy、PM2 在下面的「一次装齐」里。"
                    : null;

                await InstallTaskDialog.ShowAsync(XamlRoot, tasks, autoStart: true, note: note);
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
                Text = missing.Count == 0
                    ? "看起来都齐了，点「再查一次」刷新下。"
                    : "启动器不动你的系统，装完回来点「再查一次」。",
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
                Title = "这几样要你自己装",
                Content = body,
                PrimaryButtonText = "我装好了",
                CloseButtonText = "以后再说",
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
