using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
    /// 第 1 步：数据迁移。
    ///
    /// 两条来源并列——「另一个酒馆启动器的数据」和「本启动器旧版本的数据」——
    /// 各自先**预演**（只读）再让用户确认，确认前磁盘状态完全不变。
    /// 不迁移也可以直接下一步：这一步永远是「可选加分项」，不是门槛。
    /// </summary>
    public sealed partial class OnboardingStep1Migration : UserControl, IOnboardingStep
    {
        /// <summary>文件清单最多画这么多行：真实数据目录可能有上千个文件，全画出来只会卡住 UI。</summary>
        private const int MaxRows = 200;

        public event EventHandler? StateChanged;

        private MigrationPlan? _guiPlan;

        private MigrationPlan? _legacyPlan;

        private CancellationTokenSource? _cts;

        private bool _busy;

        public OnboardingStep1Migration()
        {
            InitializeComponent();

            SourcePicker.SelectedIndex = 0;
        }

        public bool CanContinue => !_busy;

        public bool IsBusy => _busy;

        public string? BlockingReason => _busy ? "正在搬数据，等一下" : null;

        // ==================== 生命周期 ====================

        public Task EnterAsync(CancellationToken token)
        {
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);

            RefreshGui();
            RefreshLegacy();

            return Task.CompletedTask;
        }

        public void Leave()
        {
            _cts?.Cancel();
        }

        public void Reset()
        {
            _guiPlan = null;
            _legacyPlan = null;
            _busy = false;

            SourcePicker.SelectedIndex = 0;
            ShowPanel(gui: true);

            GuiSummaryText.Visibility = Visibility.Collapsed;
            GuiListCard.Visibility = Visibility.Collapsed;
            GuiListHost.Children.Clear();
            GuiRescanButton.Visibility = Visibility.Collapsed;
            GuiRunButton.Visibility = Visibility.Collapsed;
            GuiProgressPanel.Visibility = Visibility.Collapsed;
            GuiStatusText.Text = "正在找旧数据…";
            PickFolderButton.Visibility = Visibility.Collapsed;

            LegacyListCard.Visibility = Visibility.Collapsed;
            LegacyListHost.Children.Clear();
            LegacyRescanButton.Visibility = Visibility.Collapsed;
            LegacyRunButton.Visibility = Visibility.Collapsed;
            LegacyProgressPanel.Visibility = Visibility.Collapsed;
            LegacyStatusText.Text = "正在找旧版留下的东西…";

            RaiseState();
        }

        private void RaiseState() => StateChanged?.Invoke(this, EventArgs.Empty);

        private void SetBusy(bool busy)
        {
            _busy = busy;
            RaiseState();
        }

        // ==================== 来源切换 ====================

        private void SourcePicker_SelectionChanged(object sender, EventArgs e)
        {
            ShowPanel(gui: SourcePicker.SelectedIndex == 0);
        }

        private void ShowPanel(bool gui)
        {
            GuiPanel.Visibility = gui ? Visibility.Visible : Visibility.Collapsed;
            LegacyPanel.Visibility = gui ? Visibility.Collapsed : Visibility.Visible;
        }

        // ==================== GUI 迁移 ====================

        private void RefreshGui()
        {
            if (OnboardingMigration.TryFindGuiData(out string root))
            {
                SetGuiPlan(OnboardingMigration.PlanGui(root), "找到这里的数据了", showPicker: false, path: root);
            }
            else
            {
                _guiPlan = null;

                GuiStatusText.Text = "常见位置没找到。可以自己选文件。";
                SetMeta(GuiMetaText, OnboardingMigration.GuiDataRoot);
                PickFolderButton.Visibility = Visibility.Visible;
                GuiSummaryText.Visibility = Visibility.Collapsed;
                GuiListCard.Visibility = Visibility.Collapsed;
                GuiListHost.Children.Clear();
                GuiRescanButton.Visibility = Visibility.Visible;
                GuiRunButton.Visibility = Visibility.Collapsed;
            }
        }

        private void SetGuiPlan(MigrationPlan plan, string status, bool showPicker, string? path = null)
        {
            _guiPlan = plan;

            GuiStatusText.Text = plan.Error is { Length: > 0 } error
                ? $"{status}（{error}）"
                : status;

            SetMeta(GuiMetaText, path);

            PickFolderButton.Visibility = showPicker ? Visibility.Visible : Visibility.Collapsed;
            GuiRescanButton.Visibility = Visibility.Visible;

            if (plan.Error is { Length: > 0 })
            {
                GuiSummaryText.Visibility = Visibility.Collapsed;
                GuiListCard.Visibility = Visibility.Collapsed;
                GuiRunButton.Visibility = Visibility.Collapsed;
                return;
            }

            GuiSummaryText.Visibility = Visibility.Visible;
            GuiSummaryText.Text = $"共 {plan.Entries.Count} 个文件 · 新加 {plan.NewCount} · 替换 {plan.OverwriteCount} · 一样 {plan.SkipCount} · {FormatSize(plan.TotalBytes)}";

            GuiListCard.Visibility = Visibility.Visible;
            GuiListHost.Children.Clear();

            foreach (MigrationEntry entry in plan.Entries.Take(MaxRows))
            {
                GuiListHost.Children.Add(BuildFileRow(entry));
            }

            if (plan.Entries.Count > MaxRows)
            {
                GuiListHost.Children.Add(new TextBlock
                {
                    Margin = new Thickness(8, 4, 8, 4),
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                    Text = $"还有 {plan.Entries.Count - MaxRows} 个没列出来，照样会搬",
                });
            }

            GuiRunButton.Visibility = plan.HasWork ? Visibility.Visible : Visibility.Collapsed;

            if (!plan.HasWork)
            {
                GuiSummaryText.Text += " · 已经一样了，不用搬";
            }
        }

        private FrameworkElement BuildFileRow(MigrationEntry entry)
        {
            var grid = new Grid
            {
                Padding = new Thickness(8, 5, 8, 5),
                ColumnSpacing = 8,
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            grid.Children.Add(new TextBlock
            {
                Text = entry.RelativePath.TrimEnd(Path.DirectorySeparatorChar),
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var size = new TextBlock
            {
                Text = entry.Bytes > 0 ? FormatSize(entry.Bytes) : "文件夹",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            };
            Grid.SetColumn(size, 1);
            grid.Children.Add(size);

            var pill = new Border
            {
                Padding = new Thickness(7, 1, 7, 2),
                CornerRadius = new CornerRadius(8),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(entry.Action switch
                {
                    FileAction.New => Windows.UI.Color.FromArgb(0x1F, 0x2B, 0x88, 0xD8),
                    FileAction.Overwrite => Windows.UI.Color.FromArgb(0x1F, 0xD8, 0x9B, 0x2B),
                    _ => Windows.UI.Color.FromArgb(0x14, 0x80, 0x80, 0x80),
                }),
                Child = new TextBlock
                {
                    Text = entry.Action switch
                    {
                        FileAction.New => "新加",
                        FileAction.Overwrite => "替换",
                        _ => "一样",
                    },
                    FontSize = 10.5,
                },
            };
            Grid.SetColumn(pill, 2);
            grid.Children.Add(pill);

            return grid;
        }

        private async void PickConfig_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                ViewMode = PickerViewMode.List,
            };

            picker.FileTypeFilter.Add(".yaml");
            picker.FileTypeFilter.Add(".yml");

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

            if (file is null || Path.GetDirectoryName(file.Path) is not { Length: > 0 } directory)
            {
                return;
            }

            MigrationPlan plan = OnboardingMigration.PlanGui(directory);

            if (plan.Error is { Length: > 0 })
            {
                AppNotify.Warning("这个文件夹读不了", plan.Error);
                return;
            }

            if (!File.Exists(Path.Combine(directory, "config.yaml")))
            {
                AppNotify.Warning("这里好像不是酒馆的数据", "请选里面有 default-user、characters 文件夹的那一层。");
            }

            SetGuiPlan(plan, "用的是你选的这个文件夹", showPicker: true, path: directory);
        }

        private void GuiRescan_Click(object sender, RoutedEventArgs e) => RefreshGui();

        private async void GuiRun_Click(object sender, RoutedEventArgs e)
        {
            if (_guiPlan is null || _busy)
            {
                return;
            }

            if (_guiPlan.OverwriteCount > 0)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = $"有 {_guiPlan.OverwriteCount} 个文件会被替换",
                    Content = "目标位置已经有同名文件，搬过去会换成新的。旧的先备份，成功后再删。",
                    PrimaryButtonText = "替换并继续",
                    CloseButtonText = "先不搬",
                    DefaultButton = ContentDialogButton.Close,
                };
                dialog.Resources["ControlCornerRadius"] = new CornerRadius(8);

                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }
            }

            SetBusy(true);
            GuiProgressPanel.Visibility = Visibility.Visible;
            GuiProgressBar.Value = 0;
            GuiProgressText.Text = "准备开始…";
            GuiRunButton.IsEnabled = false;

            var progress = new Progress<MigrationProgress>(value =>
            {
                GuiProgressBar.Value = value.Total > 0 ? value.Done * 100.0 / value.Total : 0;
                GuiProgressText.Text = $"{value.Detail} · {value.Done}/{value.Total}";
            });

            try
            {
                MigrationResult result = await OnboardingMigration.RunGuiAsync(
                    _guiPlan,
                    progress,
                    _cts?.Token ?? CancellationToken.None);

                if (result.Success)
                {
                    // 数据搬到了全局目录；仍留在「跟随实例」模式等于白搬
                    LauncherSettings.SetDataLocation(DataLocationMode.Global);

                    GuiProgressBar.Value = 100;
                    GuiProgressText.Text = "搬完了";

                    AppNotify.Success(
                        "搬好了",
                        $"新加 {result.Copied} 个，替换 {result.Overwritten} 个。以后数据都放同一处。");

                    RefreshGui();
                }
                else
                {
                    GuiProgressText.Text = result.Error ?? "没搬成功";
                    AppNotify.Error("没搬成功", result.Error ?? "未知原因");
                }
            }
            finally
            {
                SetBusy(false);
                GuiRunButton.IsEnabled = true;
            }
        }

        // ==================== 旧版本迁移 ====================

        private void RefreshLegacy()
        {
            MigrationPlan plan = OnboardingMigration.PlanLegacy();
            _legacyPlan = plan;

            if (!plan.Detected || plan.Error is { Length: > 0 })
            {
                LegacyStatusText.Text = "没找到旧版留下的数据";
                SetMeta(LegacyMetaText, AppPaths.RoamingRoot);
                LegacyListCard.Visibility = Visibility.Collapsed;
                LegacyListHost.Children.Clear();
                LegacyRescanButton.Visibility = Visibility.Visible;
                LegacyRunButton.Visibility = Visibility.Collapsed;
                return;
            }

            List<LegacyMove> moves = plan.Moves.ToList();
            int willMove = moves.Count(move => move.WillMove);

            LegacyStatusText.Text = willMove == 0 && plan.Notices.Count == 0
                ? "旧版的目录已经是新的了，不用再动。"
                : $"找到 {willMove} 项旧数据，要挪个位置。";

            SetMeta(LegacyMetaText, AppPaths.RoamingRoot);

            LegacyListCard.Visibility = moves.Count > 0 || plan.Notices.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            LegacyListHost.Children.Clear();

            foreach (LegacyMove move in moves.Take(MaxRows))
            {
                LegacyListHost.Children.Add(BuildMoveRow(move));
            }

            foreach (string notice in plan.Notices)
            {
                LegacyListHost.Children.Add(new TextBlock
                {
                    Margin = new Thickness(8, 3, 8, 3),
                    FontSize = 11.5,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    Text = "· " + notice,
                });
            }

            LegacyRescanButton.Visibility = Visibility.Visible;
            LegacyRunButton.Visibility = willMove > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private FrameworkElement BuildMoveRow(LegacyMove move)
        {
            var text = new TextBlock
            {
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Text = move.WillMove
                    ? $"{Shorten(move.From)}  →  {Shorten(move.To)}"
                    : $"{Shorten(move.From)}  →  已经有了，跳过",
            };

            var pill = new Border
            {
                Padding = new Thickness(7, 1, 7, 2),
                CornerRadius = new CornerRadius(8),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(move.WillMove
                    ? Windows.UI.Color.FromArgb(0x1F, 0x2B, 0x88, 0xD8)
                    : Windows.UI.Color.FromArgb(0x14, 0x80, 0x80, 0x80)),
                Child = new TextBlock
                {
                    FontSize = 10.5,
                    Text = move.Kind == "data" ? "挪上层" : "改名字",
                },
            };

            var grid = new Grid
            {
                Padding = new Thickness(8, 5, 8, 5),
                ColumnSpacing = 8,
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            grid.Children.Add(text);
            Grid.SetColumn(pill, 1);
            grid.Children.Add(pill);

            return grid;
        }

        private void LegacyRescan_Click(object sender, RoutedEventArgs e) => RefreshLegacy();

        private async void LegacyRun_Click(object sender, RoutedEventArgs e)
        {
            if (_legacyPlan is null || _busy)
            {
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "把旧数据挪到新位置？",
                Content = "只改名和挪位置，不复制也不删文件。撞名字的先放进备份文件夹。",
                PrimaryButtonText = "开始整理",
                CloseButtonText = "先不弄",
                DefaultButton = ContentDialogButton.Primary,
            };
            dialog.Resources["ControlCornerRadius"] = new CornerRadius(8);

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            SetBusy(true);
            LegacyProgressPanel.Visibility = Visibility.Visible;
            LegacyProgressBar.Value = 0;
            LegacyProgressText.Text = "准备开始…";
            LegacyRunButton.IsEnabled = false;

            var progress = new Progress<MigrationProgress>(value =>
            {
                LegacyProgressBar.Value = value.Total > 0 ? value.Done * 100.0 / value.Total : 0;
                LegacyProgressText.Text = $"{value.Detail} · {value.Done}/{value.Total}";
            });

            try
            {
                MigrationResult result = await OnboardingMigration.RunLegacyAsync(
                    _legacyPlan,
                    progress,
                    _cts?.Token ?? CancellationToken.None);

                if (!result.Success)
                {
                    LegacyProgressText.Text = result.Error ?? "没整理成功";
                    AppNotify.Error("没整理成功", result.Error ?? "未知原因");
                    return;
                }

                LegacyProgressBar.Value = 100;
                AppNotify.Success("旧数据整理好了", "内置工具挪进 bin 文件夹，数据目录也理顺了。");

                RefreshLegacy();
                await OfferNodeUpgradeAsync();
            }
            finally
            {
                SetBusy(false);
                LegacyRunButton.IsEnabled = true;
            }
        }

        /// <summary>旧版内置 Node 是 v24，当前规范是 v22：迁移完顺手把这件事处理掉。</summary>
        private async Task OfferNodeUpgradeAsync()
        {
            (bool needed, string? version) = await OnboardingMigration
                .CheckLegacyNodeAsync(_cts?.Token ?? CancellationToken.None);

            if (!needed)
            {
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "有个小工具要重装",
                Content = (version is null ? "旧版带的小工具已经无法运行。" : $"旧版带的是 Node.js {version}，")
                    + $"现在要用 {NodejsInstaller.Version}。混着用在有些电脑上会出怪问题。要现在重装吗？"
                    + "旧的先留着，装失败自动还原。",
                PrimaryButtonText = "现在重装",
                CloseButtonText = "稍后再说",
                DefaultButton = ContentDialogButton.Primary,
            };
            dialog.Resources["ControlCornerRadius"] = new CornerRadius(8);

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            SetBusy(true);
            LegacyProgressPanel.Visibility = Visibility.Visible;
            LegacyProgressBar.Value = 0;
            LegacyProgressText.Text = "正在重装小工具…";

            var progress = new Progress<InstallProgress>(value =>
            {
                LegacyProgressBar.Value = value.Percent;
                LegacyProgressText.Text = string.IsNullOrWhiteSpace(value.Message)
                    ? $"正在重装小工具… {value.Percent:0}%"
                    : $"{value.Message} · {value.Percent:0}%";
            });

            try
            {
                InstallResult result = await OnboardingMigration.ReinstallNodeAsync(
                    progress,
                    _cts?.Token ?? CancellationToken.None);

                if (result.Success)
                {
                    AppNotify.Success("小工具重装好了", result.Message);
                }
                else
                {
                    AppNotify.Error("小工具没装上", result.Message + (result.Hint is { Length: > 0 } hint ? " " + hint : string.Empty));
                }
            }
            finally
            {
                SetBusy(false);
            }
        }

        // ==================== 杂项 ====================

        /// <summary>
        /// 路径单独一行灰字：主句说人话，路径该多长就多长。传 null 收起。
        /// </summary>
        private static void SetMeta(TextBlock text, string? value)
        {
            text.Text = value ?? string.Empty;
            text.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
            ToolTipService.SetToolTip(text, string.IsNullOrEmpty(value) ? null : value);
        }

        private static string Shorten(string path)
        {
            string root = AppPaths.RoamingRoot;

            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? path[root.Length..].TrimStart(Path.DirectorySeparatorChar)
                : path;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0)
            {
                return "0 B";
            }

            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes;
            int unit = 0;

            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
        }
    }
}
