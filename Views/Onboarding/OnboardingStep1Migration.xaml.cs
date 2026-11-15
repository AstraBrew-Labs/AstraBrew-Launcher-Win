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

        public string? BlockingReason => _busy ? L.Get("Setup.S1.Blocking.Busy") : null;

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
            GuiStatusText.Text = L.Get("Setup.S1.Gui.Searching");
            PickFolderButton.Visibility = Visibility.Collapsed;

            LegacyListCard.Visibility = Visibility.Collapsed;
            LegacyListHost.Children.Clear();
            LegacyRescanButton.Visibility = Visibility.Collapsed;
            LegacyRunButton.Visibility = Visibility.Collapsed;
            LegacyProgressPanel.Visibility = Visibility.Collapsed;
            LegacyStatusText.Text = L.Get("Setup.S1.Legacy.Searching");

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
                SetGuiPlan(OnboardingMigration.PlanGui(root), L.Get("Setup.S1.Gui.Found"), showPicker: false, path: root);
            }
            else
            {
                _guiPlan = null;

                GuiStatusText.Text = L.Get("Setup.S1.Gui.NotFound");
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
            GuiSummaryText.Text = L.F(
                "Setup.S1.Gui.Summary",
                plan.Entries.Count,
                plan.NewCount,
                plan.OverwriteCount,
                plan.SkipCount,
                FormatSize(plan.TotalBytes));

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
                    Text = L.F("Setup.S1.Gui.MoreRows", plan.Entries.Count - MaxRows),
                });
            }

            GuiRunButton.Visibility = plan.HasWork ? Visibility.Visible : Visibility.Collapsed;

            if (!plan.HasWork)
            {
                GuiSummaryText.Text += L.Get("Setup.S1.Gui.NothingToDo");
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
                Text = entry.Bytes > 0 ? FormatSize(entry.Bytes) : L.Get("Setup.S1.Folder"),
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
                        FileAction.New => L.Get("Setup.S1.Action.New"),
                        FileAction.Overwrite => L.Get("Setup.S1.Action.Overwrite"),
                        _ => L.Get("Setup.S1.Action.Same"),
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
                AppNotify.Error(L.Get("Setup.S1.Notify.PickerFailed.Title"), ex.Message);
                return;
            }

            if (file is null || Path.GetDirectoryName(file.Path) is not { Length: > 0 } directory)
            {
                return;
            }

            MigrationPlan plan = OnboardingMigration.PlanGui(directory);

            if (plan.Error is { Length: > 0 })
            {
                AppNotify.Warning(L.Get("Setup.S1.Notify.Unreadable.Title"), plan.Error);
                return;
            }

            if (!File.Exists(Path.Combine(directory, "config.yaml")))
            {
                AppNotify.Warning(
                    L.Get("Setup.S1.Notify.NotTavern.Title"),
                    L.Get("Setup.S1.Notify.NotTavern.Body"));
            }

            SetGuiPlan(plan, L.Get("Setup.S1.Gui.Picked"), showPicker: true, path: directory);
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
                    Title = L.F("Setup.S1.Confirm.Overwrite.Title", _guiPlan.OverwriteCount),
                    Content = L.Get("Setup.S1.Confirm.Overwrite.Body"),
                    PrimaryButtonText = L.Get("Setup.S1.Confirm.Overwrite.Primary"),
                    CloseButtonText = L.Get("Setup.S1.Confirm.Overwrite.Close"),
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
            GuiProgressText.Text = L.Get("Setup.S1.Progress.Preparing");
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
                    GuiProgressText.Text = L.Get("Setup.S1.Progress.Done");

                    AppNotify.Success(
                        L.Get("Setup.S1.Notify.Moved.Title"),
                        L.F("Setup.S1.Notify.Moved.Body", result.Copied, result.Overwritten));

                    RefreshGui();
                }
                else
                {
                    GuiProgressText.Text = result.Error ?? L.Get("Setup.S1.Progress.Failed");
                    AppNotify.Error(L.Get("Setup.S1.Notify.MoveFailed.Title"), result.Error ?? L.Get("Setup.Common.Unknown"));
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
                LegacyStatusText.Text = L.Get("Setup.S1.Legacy.NotFound");
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
                ? L.Get("Setup.S1.Legacy.AlreadyOkay")
                : L.F("Setup.S1.Legacy.Found", willMove);

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
                    : L.F("Setup.S1.Legacy.SkipRow", Shorten(move.From)),
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
                    Text = L.Get(move.Kind == "data" ? "Setup.S1.Legacy.Kind.Move" : "Setup.S1.Legacy.Kind.Rename"),
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
                Title = L.Get("Setup.S1.Legacy.Confirm.Title"),
                Content = L.Get("Setup.S1.Legacy.Confirm.Body"),
                PrimaryButtonText = L.Get("Setup.S1.Legacy.Confirm.Primary"),
                CloseButtonText = L.Get("Setup.S1.Legacy.Confirm.Close"),
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
            LegacyProgressText.Text = L.Get("Setup.S1.Progress.Preparing");
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
                    LegacyProgressText.Text = result.Error ?? L.Get("Setup.S1.Progress.Failed");
                    AppNotify.Error(L.Get("Setup.S1.Legacy.Notify.Failed.Title"), result.Error ?? L.Get("Setup.Common.Unknown"));
                    return;
                }

                LegacyProgressBar.Value = 100;
                AppNotify.Success(
                    L.Get("Setup.S1.Legacy.Notify.Done.Title"),
                    L.Get("Setup.S1.Legacy.Notify.Done.Body"));

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
                Title = L.Get("Setup.S1.NodeUpgrade.Title"),
                Content = version is null
                    ? L.F("Setup.S1.NodeUpgrade.BodyUnknown", NodejsInstaller.Version)
                    : L.F("Setup.S1.NodeUpgrade.Body", version, NodejsInstaller.Version),
                PrimaryButtonText = L.Get("Setup.S1.NodeUpgrade.Primary"),
                CloseButtonText = L.Get("Setup.S1.NodeUpgrade.Close"),
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
            LegacyProgressText.Text = L.Get("Setup.S1.NodeUpgrade.Progress");

            var progress = new Progress<InstallProgress>(value =>
            {
                LegacyProgressBar.Value = value.Percent;
                LegacyProgressText.Text = string.IsNullOrWhiteSpace(value.Message)
                    ? L.F("Setup.S1.NodeUpgrade.ProgressPercent", $"{value.Percent:0}")
                    : $"{value.Message} · {value.Percent:0}%";
            });

            try
            {
                InstallResult result = await OnboardingMigration.ReinstallNodeAsync(
                    progress,
                    _cts?.Token ?? CancellationToken.None);

                if (result.Success)
                {
                    AppNotify.Success(L.Get("Setup.S1.NodeUpgrade.Done.Title"), result.Message);
                }
                else
                {
                    AppNotify.Error(L.Get("Setup.S1.NodeUpgrade.Failed.Title"), result.Message + (result.Hint is { Length: > 0 } hint ? " " + hint : string.Empty));
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
