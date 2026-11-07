using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AstraBrew_Launcher.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AstraBrew_Launcher.Views.Onboarding
{
    /// <summary>
    /// 第 4 步：添加酒馆实例。
    ///
    /// 两条路互斥但殊途同归——**只要有一个可运行的实例被启用**，就能点「完成」结束引导。
    /// · 本地：扫描或手动添加，缺依赖的可以就地补装；
    /// · 内置：下载 + 装依赖 + 自动启用，复用 <see cref="TavernInstaller"/> 的全套进度事件。
    /// </summary>
    public sealed partial class OnboardingStep4Instance : UserControl, IOnboardingStep
    {
        public event EventHandler? StateChanged;

        /// <summary>用户点「返回第 3 步」时抛出，由外壳负责切步骤。</summary>
        public event EventHandler? BackToRuntimeRequested;

        private CancellationTokenSource? _cts;

        private bool _busy;

        private bool _enabled;

        private bool _scanHooked;

        private BuiltinSetupPlan? _plan;

        public OnboardingStep4Instance()
        {
            InitializeComponent();
        }

        public bool CanContinue => _enabled && !_busy;

        public bool IsBusy => _busy;

        public string? BlockingReason => _enabled
            ? _busy ? "正在装酒馆" : null
            : "先启用一个能用的酒馆";

        // ==================== 生命周期 ====================

        public async Task EnterAsync(CancellationToken token)
        {
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);

            HookScan();

            // 已经有一个能跑的实例（例如用户之前装过），直接算完成
            TavernInstances.ReloadBuiltin();
            _enabled = TavernInstances.Current.IsInstalled && TavernInstances.Current.DepsReady;
            RenderLocals();
            RaiseState();

            await Task.CompletedTask;
        }

        public void Leave()
        {
            _cts?.Cancel();
            UnhookScan();
            _busy = false;
        }

        public void Reset()
        {
            UnhookScan();

            _busy = false;
            _enabled = false;
            _plan = null;

            LocalPanel.Visibility = Visibility.Collapsed;
            BuiltinPanel.Visibility = Visibility.Collapsed;
            BuiltinProgressPanel.Visibility = Visibility.Collapsed;
            InstallDepsButton.Visibility = Visibility.Collapsed;
            BackToRuntimeButton.Visibility = Visibility.Collapsed;

            LocalListHost.Children.Clear();
            ScanStatusText.Text = "会翻遍所有硬盘，慢的话要等几分钟。";

            OptionLocal.IsChecked = false;
            OptionBuiltin.IsChecked = false;

            StateText.Text = "先选一种：用你有的，还是让启动器装。";
            RaiseState();
        }

        private void RaiseState() => StateChanged?.Invoke(this, EventArgs.Empty);

        private void SetBusy(bool busy)
        {
            _busy = busy;
            RaiseState();
        }

        // ==================== 模式选择 ====================

        private async void Mode_Checked(object sender, RoutedEventArgs e)
        {
            if (LocalPanel is null)
            {
                return;
            }

            bool local = OptionLocal.IsChecked == true;

            LocalPanel.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
            BuiltinPanel.Visibility = local ? Visibility.Collapsed : Visibility.Visible;

            if (local)
            {
                RenderLocals();
                StateText.Text = "找一找或自己选，找到后点「用它」。";
                return;
            }

            StateText.Text = "点「下载并启用」，剩下的交给启动器。";

            await LoadBuiltinPlanAsync();
        }

        // ==================== 本地实例 ====================

        private void HookScan()
        {
            if (_scanHooked)
            {
                return;
            }

            InstanceScan.Changed += Scan_Changed;
            _scanHooked = true;
        }

        private void UnhookScan()
        {
            if (!_scanHooked)
            {
                return;
            }

            InstanceScan.Changed -= Scan_Changed;
            _scanHooked = false;
        }

        private void Scan_Changed(object? sender, EventArgs e)
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(() => Scan_Changed(sender, e));
                return;
            }

            ScanSnapshot state = InstanceScan.State;

            ScanButton.Content = state.IsRunning ? "停下来" : "自动找一找";
            ScanStatusText.Text = state.IsRunning
                ? $"已看 {state.Directories} 个文件夹 · 找到 {state.Found} 个 · {state.ElapsedLabel}"
                : state.HasRun
                    ? $"找完了：共 {state.Found} 个 · 用时 {state.ElapsedLabel}"
                    : "扫描会遍历所有磁盘，机械硬盘可能需要几分钟。";

            RenderLocals();
        }

        private void Scan_Click(object sender, RoutedEventArgs e)
        {
            if (InstanceScan.IsRunning)
            {
                InstanceScan.Cancel();
                return;
            }

            HKickScan();
        }

        private void HKickScan()
        {
            HookScan();
            InstanceScan.Start();
            Scan_Changed(this, EventArgs.Empty);
        }

        private async void Add_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                ViewMode = PickerViewMode.List,
            };
            picker.FileTypeFilter.Add(".json");

            if (App.MainWindow is not null)
            {
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            }

            StorageFile? file;

            try
            {
                file = await picker.PickSingleFileAsync();
            }
            catch (Exception ex)
            {
                AppNotify.Error("没能打开选择窗口", ex.Message);
                return;
            }

            if (file is null)
            {
                return;
            }

            LocalAddStatus status = OnboardingInstances.AddLocalAt(file.Path, out TavernInstance? instance);

            switch (status)
            {
                case LocalAddStatus.Added:
                    AppNotify.Success("加进来了", instance?.Path ?? file.Path);
                    break;

                case LocalAddStatus.Duplicate:
                    AppNotify.Info("这个已经在列表里", instance?.DisplayName ?? string.Empty);
                    break;

                case LocalAddStatus.Builtin:
                    AppNotify.Info("这是内置那份", "到「我还没有」里管理。");
                    break;

                case LocalAddStatus.NotTavern:
                    AppNotify.Warning("这里不是酒馆", "请选酒馆根目录里的 package.json。");
                    break;

                default:
                    AppNotify.Error("这个文件夹读不了", file.Path);
                    break;
            }

            RenderLocals();
        }

        private void RenderLocals()
        {
            if (LocalListHost is null)
            {
                return;
            }

            LocalListHost.Children.Clear();

            IReadOnlyList<TavernInstance> locals = TavernInstances.Locals;

            if (locals.Count == 0)
            {
                LocalListHost.Children.Add(new TextBlock
                {
                    Margin = new Thickness(10, 12, 10, 12),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Res("TextFillColorTertiaryBrush"),
                    Text = InstanceScan.IsRunning
                        ? "正在找…"
                        : "还没找到。点「自动找一找」，或「自己选」一个 package.json。",
                });

                return;
            }

            foreach (TavernInstance instance in locals)
            {
                LocalListHost.Children.Add(BuildLocalRow(instance));
            }
        }

        private UIElement BuildLocalRow(TavernInstance instance)
        {
            var grid = new Grid
            {
                Padding = new Thickness(8, 4, 8, 4),
                ColumnSpacing = 8,
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = $"{instance.DisplayName}",
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
            });
            text.Children.Add(new TextBlock
            {
                Text = instance.Path,
                FontSize = 10.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Res("TextFillColorTertiaryBrush"),
            });
            grid.Children.Add(text);

            if (!instance.DepsReady)
            {
                var warn = new Border
                {
                    Padding = new Thickness(7, 2, 7, 3),
                    CornerRadius = new CornerRadius(8),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1F, 0xD8, 0x9B, 0x2B)),
                    Child = new TextBlock { Text = "缺零件", FontSize = 10.5 },
                };
                Grid.SetColumn(warn, 1);
                grid.Children.Add(warn);
            }

            var enable = new Button
            {
                Content = instance.DepsReady ? "用它" : "装好零件再用",
                VerticalAlignment = VerticalAlignment.Center,
            };

            enable.Click += async (_, _) => await EnableLocalAsync(instance);

            Grid.SetColumn(enable, 2);
            grid.Children.Add(enable);

            return grid;
        }

        private async Task EnableLocalAsync(TavernInstance instance)
        {
            if (_busy)
            {
                return;
            }

            if (!instance.DepsReady)
            {
                SetBusy(true);
                BuiltinProgressPanel.Visibility = Visibility.Visible;
                BuiltinProgressText.Text = "正在装零件…";

                var progress = new Progress<TavernInstallProgress>(value =>
                {
                    BuiltinBar.Value = value.Percent;
                    BuiltinProgressText.Text = string.IsNullOrWhiteSpace(value.Message)
                        ? $"{value.Percent:0}%"
                        : $"{value.Message} · {value.Percent:0}%";
                });

                try
                {
                    TavernInstallResult result = await OnboardingInstances.InstallLocalDependenciesAsync(
                        instance,
                        progress,
                        _cts?.Token ?? CancellationToken.None);

                    if (!result.Success)
                    {
                        AppNotify.Error("零件没装好", result.Message + (result.Hint is { Length: > 0 } hint ? " " + hint : string.Empty));
                        return;
                    }
                }
                finally
                {
                    SetBusy(false);
                    BuiltinProgressPanel.Visibility = Visibility.Collapsed;
                }
            }

            if (OnboardingInstances.Enable(instance))
            {
                _enabled = true;

                StateText.Text = $"已经用上 {instance.DisplayName}，点「完成」收工。";
                AppNotify.Success("已经用上了", instance.Path);
                RenderLocals();
                RaiseState();
            }
            else
            {
                AppNotify.Warning("这个还用不了", "零件没装全，或文件夹已经不在了。");
            }
        }

        // ==================== 内置实例 ====================

        private async Task LoadBuiltinPlanAsync()
        {
            if (!OnboardingInstances.HasRuntimeForBuiltin(out string reason))
            {
                BuiltinInfoText.Text = reason;
                InstallBuiltinButton.Visibility = Visibility.Collapsed;
                BackToRuntimeButton.Visibility = Visibility.Visible;
                StateText.Text = "装内置酒馆要先有第 3 步那两样。";
                return;
            }

            InstallBuiltinButton.Visibility = Visibility.Visible;
            BackToRuntimeButton.Visibility = Visibility.Collapsed;
            BuiltinInfoText.Text = "正在查版本…";

            if (TavernInstances.BuiltinInstalled)
            {
                RenderInstalledBuiltin();
                return;
            }

            try
            {
                _plan = await OnboardingInstances.PlanBuiltinAsync(_cts?.Token ?? CancellationToken.None);

                BuiltinTitleText.Text = $"内置酒馆 {_plan.Revision}";
                BuiltinInfoText.Text = _plan.Note is { Length: > 0 } note
                    ? $"准备装 {_plan.Revision} · {note}"
                    : $"准备装 {_plan.Revision}";
                InstallBuiltinButton.Content = "下载并启用";
            }
            catch (Exception ex)
            {
                BuiltinInfoText.Text = "查版本失败了：" + ex.Message;
            }
        }

        private void RenderInstalledBuiltin()
        {
            BuiltinTitleText.Text = $"内置酒馆 {TavernInstances.Builtin.Version}";
            BuiltinInfoText.Text = TavernInstances.BuiltinRunnable
                ? "已经装好了，直接用。"
                : "下载好了，但还缺零件。";

            InstallBuiltinButton.Content = TavernInstances.BuiltinRunnable ? "用它" : "下载并启用";
            InstallDepsButton.Visibility = TavernInstances.BuiltinRunnable ? Visibility.Collapsed : Visibility.Visible;
        }

        private async void InstallBuiltin_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                return;
            }

            // 已装好：只做启用
            if (TavernInstances.BuiltinInstalled && TavernInstances.BuiltinRunnable)
            {
                ActivateBuiltin();
                return;
            }

            if (TavernInstances.BuiltinInstalled && !TavernInstances.BuiltinRunnable)
            {
                await InstallDepsAsync();
                return;
            }

            if (_plan is null)
            {
                await LoadBuiltinPlanAsync();

                if (_plan is null)
                {
                    return;
                }
            }

            SetBusy(true);
            BuiltinProgressPanel.Visibility = Visibility.Visible;
            BuiltinBar.Value = 0;
            BuiltinProgressText.Text = "准备下载…";
            InstallBuiltinButton.IsEnabled = false;

            var progress = new Progress<TavernInstallProgress>(value =>
            {
                BuiltinBar.Value = Math.Clamp(value.Percent, 0, 100);
                BuiltinProgressText.Text = string.IsNullOrWhiteSpace(value.Detail)
                    ? $"{value.Message} · {value.Percent:0}%"
                    : $"{value.Message} · {value.Detail} · {value.Percent:0}%";
            });

            try
            {
                TavernInstallResult result = await OnboardingInstances.InstallBuiltinAsync(
                    _plan,
                    progress,
                    _cts?.Token ?? CancellationToken.None);

                if (result.Success && TavernInstances.BuiltinRunnable)
                {
                    BuiltinBar.Value = 100;
                    ActivateBuiltin();
                    return;
                }

                if (result.Success)
                {
                    // 克隆成功、依赖没装完：给一条更省事的补装路径
                    BuiltinProgressPanel.Visibility = Visibility.Collapsed;
                    RenderInstalledBuiltin();
                    AppNotify.Warning("下载好了，只缺零件", "点「补装零件」继续，不用重新下载。");
                    return;
                }

                BuiltinProgressText.Text = result.Message;
                AppNotify.Error("内置那份没装上", result.Message);
            }
            finally
            {
                SetBusy(false);
                InstallBuiltinButton.IsEnabled = true;
            }
        }

        private async void InstallDeps_Click(object sender, RoutedEventArgs e) => await InstallDepsAsync();

        private async Task InstallDepsAsync()
        {
            if (_busy)
            {
                return;
            }

            SetBusy(true);
            BuiltinProgressPanel.Visibility = Visibility.Visible;
            BuiltinBar.Value = 0;
            BuiltinProgressText.Text = "正在安装依赖…";

            var progress = new Progress<TavernInstallProgress>(value =>
            {
                BuiltinBar.Value = Math.Clamp(value.Percent, 0, 100);
                BuiltinProgressText.Text = $"{value.Message} · {value.Percent:0}%";
            });

            try
            {
                TavernInstallResult result = await OnboardingInstances.InstallBuiltinDependenciesAsync(
                    progress,
                    _cts?.Token ?? CancellationToken.None);

                if (result.Success && TavernInstances.BuiltinRunnable)
                {
                    ActivateBuiltin();
                }
                else
                {
                    AppNotify.Error("零件没装上", result.Message);
                }
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void ActivateBuiltin()
        {
            if (OnboardingInstances.Enable(TavernInstances.Builtin))
            {
                _enabled = true;
                InstallDepsButton.Visibility = Visibility.Collapsed;

                StateText.Text = "内置酒馆能用了，点「完成」收工。";
                AppNotify.Success("内置酒馆已经启用", TavernInstances.Builtin.Path);
                RenderInstalledBuiltin();
                RaiseState();
            }
            else
            {
                AppNotify.Warning("内置那份还用不了", "零件似乎没装全，点「补装零件」再试。");
            }
        }

        private void BackToRuntime_Click(object sender, RoutedEventArgs e)
        {
            BackToRuntimeRequested?.Invoke(this, EventArgs.Empty);
        }

        private static Brush Res(string key) =>
            Application.Current.Resources.TryGetValue(key, out object value) && value is Brush brush
                ? brush
                : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }
}
