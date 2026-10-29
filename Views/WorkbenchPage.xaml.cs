using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;
using Microsoft.UI.Text;
using AstraBrew_Launcher.Views.Controls;

namespace AstraBrew_Launcher.Views
{
    /// <summary>工作台当前在编辑哪一类资源。</summary>
    public enum WorkbenchKind
    {
        Character,
        WorldBook,
        Preset,
    }

    /// <summary>
    /// 资源工作台：一个页面对应一份资源，左侧改资料、右侧写长文本，改完自动保存。
    /// 参考 AstraBrew-Launcher-Win 的 resource_manage/workbench.rs。
    /// </summary>
    public sealed partial class WorkbenchPage : Page
    {
        private const int PresetPageSize = 6;

        // ==================== 原型数据 ====================

        private sealed record EntrySeed(
            string Comment,
            string[] Keys,
            string[] SecondaryKeys,
            string Content,
            bool Enabled,
            int Position);

        private sealed record PromptSeed(
            string Identifier,
            string Name,
            int Role,
            bool Enabled,
            bool Marker,
            string Content);

        private static readonly EntrySeed[] ShopEntries =
        {
            new(
                "便利店 · 夜班",
                new[] { "便利店", "夜班", "值班" },
                Array.Empty<string>(),
                "星野澪在城南十字路口的便利店上夜班，从二十二点到早上六点。\n收银台在门口左手边，关东煮的锅整夜开着，汤面浮着一层薄油。",
                true,
                1),
            new(
                "电车 · 末班",
                new[] { "电车", "末班", "站台" },
                new[] { "错过" },
                "最后一班电车在零点十七分经过店门前的站台，车厢里通常只有两三个人。\n错过这班，下一趟要等到五点四十。",
                true,
                1),
            new(
                "常客 · 老陈",
                new[] { "老陈", "常客" },
                Array.Empty<string>(),
                "老陈开夜班出租，每天凌晨两点进来买一罐黑咖啡和一个茶叶蛋。\n他从不说话，付完钱就坐在窗边的高脚凳上抽半支烟。",
                false,
                1),
            new(
                "店里的猫 · 阿墨",
                new[] { "猫", "阿墨" },
                new[] { "纸箱" },
                "一只黑猫在入冬后住进了后门的纸箱，星野澪给它取名阿墨。\n它只在没有客人的时候进来，蹲在暖柜旁边看关东煮冒烟。",
                false,
                1),
            new(
                "雨夜规则",
                new[] { "雨", "打烊" },
                Array.Empty<string>(),
                "下雨的夜里，星野澪会把门口那把旧伞留在伞桶外面。\n如果有人站在屋檐下等雨停超过十分钟，她会把伞推过去，说「明天顺路还我就行」。",
                false,
                4),
            new(
                "打烊流程",
                new[] { "打烊", "关灯" },
                Array.Empty<string>(),
                "打烊顺序：先收关东煮，再清点烟柜，最后关掉招牌灯。\n招牌灯一灭，整条街就只剩站台的灯还亮着。",
                false,
                1),
        };

        private static readonly EntrySeed[] RainEntries =
        {
            new(
                "旧唱片店",
                new[] { "唱片", "唱片店", "黑胶" },
                Array.Empty<string>(),
                "巷子深处的唱片店没有招牌，只在门上贴了一张手写的营业时间。\n雨天老板会把唱机搬到门口，放一张谁都不认识的爵士。",
                true,
                1),
            new(
                "雨里的和声",
                new[] { "雨", "哼唱" },
                new[] { "旋律" },
                "雨声盖过唱机的时候，老板会跟着哼两句，调子总是差半个音。\n他说这样雨才听得懂。",
                true,
                4),
            new(
                "闭店之后",
                new[] { "闭店", "打烊" },
                Array.Empty<string>(),
                "闭店之后他不急着走，会重新放一遍当天最后一张唱片，把针从外圈走到内圈。",
                false,
                1),
        };

        private static readonly PromptSeed[] PresetSeeds =
        {
            new("main", "主提示", 0, true, false, "以下对话由 {{char}} 与 {{user}} 进行。请始终以 {{char}} 的身份、口吻与记忆回应，不要跳出角色，也不要替 {{user}} 说话。"),
            new("worldInfoBefore", "世界书 · 前置", 0, true, false, "在角色设定之前插入命中的世界书条目，用于交代世界观与地点。"),
            new("personaDescription", "用户人设", 0, true, false, "{{user}} 的信息以第三人称陈述，只用于理解关系，不要在回复中直接引用。"),
            new("charDescription", "角色描述", 0, true, false, "把角色的外貌、身份与当前处境作为叙述基础，避免复述原文。"),
            new("charPersonality", "角色性格", 0, true, false, "性格要点：克制、慢热、会照顾人但不擅长接受照顾。"),
            new("scenario", "场景", 0, true, false, "场景决定时间、地点与正在发生的事；没有新信息时不要推进时间。"),
            new("enhanceDefinitions", "增强定义", 0, false, false, "补充名词解释与专有名词映射（该预设当前关闭，交给世界书处理）。"),
            new("nsfw", "越狱", 0, true, false, "允许描写细腻的情绪与身体感受，但不脱离角色设定；遇到不适内容以角色的方式回避，而不是出戏解释。"),
            new("worldInfoAfter", "世界书 · 后置", 0, true, false, "在角色设定之后插入剩余条目，用于补充细节。"),
            new("dialogueExamples", "对话示例", 0, true, false, "示例只用于校准语气与句子长度，不要照抄台词。"),
            new("chatHistory", "聊天记录", 0, true, true, string.Empty),
            new("jailbreak", "系统提示", 0, true, false, "保持句子短、留白多。一次回复不超过四段。"),
            new("summary", "总结", 0, false, false, "每十轮把发生的事压缩成一段不超过一百字的摘要（该预设当前关闭）。"),
            new("assistMe", "助手引导", 1, true, false, "当 {{user}} 输入 /help 时，用角色口吻简短说明可以做什么。"),
        };

        // ==================== 状态 ====================

        private readonly EditLog _characterLog = new();
        private readonly EditLog _worldLog = new();
        private readonly EditLog _presetLog = new();

        private readonly Dictionary<WorkbenchKind, (string Title, string Glyph, string Accent, string Folder, string File, string Size)> _kindInfo = new()
        {
            [WorkbenchKind.Character] = ("角色卡工作台", "\uE77B", "#2B88D8", "characters", "星野澪.json", "8.4 KB"),
            [WorkbenchKind.WorldBook] = ("世界书工作台", "\uE8F1", "#8E44DC", "worlds", "便利店与电车末班.json", "42.6 KB"),
            [WorkbenchKind.Preset] = ("预设工作台", "\uE9E9", "#C08A00", "OpenAI Settings", "夜谈 · 慢热叙事.json", "18.2 KB"),
        };

        private WorkbenchKind _kind = WorkbenchKind.Character;
        private int _presetPage;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _saveTimer;
        private bool _applyingKind;

        // ==================== 对外绑定 ====================

        public CharacterForm Character { get; } = new();

        public WorldBookForm WorldBook { get; } = new();

        public WorldBookForm EmbeddedWorld { get; } = new();

        public ObservableCollection<PromptForm> Prompts { get; } = new();

        public ObservableCollection<PromptForm> VisiblePrompts { get; } = new();

        public WorkbenchPage()
        {
            InitializeComponent();

            Character.Log = _characterLog;
            WorldBook.Log = _worldLog;

            // 从资源管理页跳进来时，用真实文件名替换种子里的名字。
            if (!string.IsNullOrEmpty(PendingFile))
            {
                (string title, string glyph, string accent, string folder, string file, string size) = _kindInfo[PendingKind];
                _kindInfo[PendingKind] = (title, glyph, accent, folder, PendingFile, size);
            }

            LoadSeed();

            foreach (EntryForm entry in WorldBook.Entries)
            {
                entry.Log = _worldLog;
            }

            foreach (PromptForm prompt in Prompts)
            {
                prompt.Log = _presetLog;
            }

            _characterLog.Changed += () => OnLogChanged(_characterLog);
            _worldLog.Changed += () => OnLogChanged(_worldLog);
            _presetLog.Changed += () => OnLogChanged(_presetLog);

            _saveTimer = DispatcherQueue.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(700);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => CommitSave();

            SetupAccelerators();

            ApplyKind(PendingKind);
            ApplyWorldState();
            RefreshPresetPage();
            UpdateCounts();
            SetSaveState(SaveState.Saved);

            Loaded += (_, _) =>
            {
                UpdateGridColumns();
                UpdateHistoryButtons();
            };
            // 每个网格自己盯着宽度：只在页面 SizeChanged 时算会拿到旧值，导致窄窗仍是三列。
            EntryList.SizeChanged += (_, _) => UpdateGridColumns();
            PromptList.SizeChanged += (_, _) => UpdateGridColumns();
            EmbeddedEntryList.SizeChanged += (_, _) => UpdateGridColumns();
            SizeChanged += (_, _) => UpdateGridColumns();
        }

        // ==================== 种子数据 ====================

        private void LoadSeed()
        {
            Character.Name = "星野澪";
            Character.Creator = "星酿工坊";
            Character.Version = "1.2.0";
            Character.Description =
                "二十四岁，城南十字路口便利店的夜班店员。个子不高，头发在脑后随手扎成一束，"
                + "工作服外面总罩着一件洗到发白的灰色开衫。\n"
                + "说话很慢，习惯先想清楚再开口；被夸的时候会低头整理货架。";
            Character.Personality =
                "克制、慢热，不擅长接受别人的照顾，却很会照顾人。\n"
                + "对熟客记得住每个人买什么，对陌生人只保持基本的礼貌。\n"
                + "讨厌被问「以后打算怎么办」，喜欢凌晨四点没有人来的那半小时。";
            Character.Scenario =
                "十二月的雨夜，凌晨一点四十。店里只剩暖柜的嗡嗡声和门口滴水的雨棚。\n"
                + "你推门进来时，她正蹲在关东煮的锅前换汤。";
            Character.FirstMessage =
                "「……欢迎光临。」\n"
                + "她直起身，手在围裙上擦了两下，才想起自己还没说完后半句，"
                + "「雨这么大，先坐一会儿吧，那边有热水。」";

            foreach (string tag in new[] { "现代", "治愈", "日常", "单向暗恋" })
            {
                Character.Tags.Add(new EditTag(tag, Character.RemoveTag));
            }

            EmbeddedWorld.Load("便利店与电车末班", "星酿工坊", ShopEntries.Select(MakeEntry));
            WorldBook.Load("便利店与电车末班", "星酿工坊", ShopEntries.Select(MakeEntry));

            foreach (PromptSeed seed in PresetSeeds)
            {
                Prompts.Add(new PromptForm
                {
                    Identifier = seed.Identifier,
                    Name = seed.Name,
                    Role = seed.Role,
                    Enabled = seed.Enabled,
                    IsMarker = seed.Marker,
                    Content = seed.Content,
                });
            }
        }

        private static EntryForm MakeEntry(EntrySeed seed, int index)
        {
            EntryForm entry = new()
            {
                Number = index + 1,
                Comment = seed.Comment,
                Content = seed.Content,
                Enabled = seed.Enabled,
                Position = seed.Position,
                Order = (100 + (index * 10)).ToString(),
                Probability = "100",
                Depth = "4",
            };

            foreach (string key in seed.Keys)
            {
                entry.Keys.Add(new EditTag(key, tag => entry.RemoveKey(tag, false)));
            }

            foreach (string key in seed.SecondaryKeys)
            {
                entry.SecondaryKeys.Add(new EditTag(key, tag => entry.RemoveKey(tag, true)));
            }

            return entry;
        }

        // ==================== 类型切换 ====================

        private EditLog CurrentLog => _kind switch
        {
            WorkbenchKind.WorldBook => _worldLog,
            WorkbenchKind.Preset => _presetLog,
            _ => _characterLog,
        };

        private void Kind_Checked(object sender, RoutedEventArgs e)
        {
            if (_applyingKind || sender is not RadioButton button || button.Tag is not string tag)
            {
                return;
            }

            ApplyKind(tag switch
            {
                "worldbook" => WorkbenchKind.WorldBook,
                "preset" => WorkbenchKind.Preset,
                _ => WorkbenchKind.Character,
            });
        }

        private void ApplyKind(WorkbenchKind kind)
        {
            _kind = kind;

            _applyingKind = true;
            KindCharacterButton.IsChecked = kind == WorkbenchKind.Character;
            KindWorldBookButton.IsChecked = kind == WorkbenchKind.WorldBook;
            KindPresetButton.IsChecked = kind == WorkbenchKind.Preset;
            _applyingKind = false;

            CharacterView.Visibility = kind == WorkbenchKind.Character ? Visibility.Visible : Visibility.Collapsed;
            WorldBookView.Visibility = kind == WorkbenchKind.WorldBook ? Visibility.Visible : Visibility.Collapsed;
            PresetView.Visibility = kind == WorkbenchKind.Preset ? Visibility.Visible : Visibility.Collapsed;

            (string title, string glyph, string accent, string folder, string file, string size) = _kindInfo[kind];
            Color color = ResourcePalette.Parse(accent);
            KindTitleText.Text = title;
            KindSubText.Text = $"{folder}/{file} · {size}";
            KindGlyph.Glyph = glyph;
            KindGlyph.Foreground = new SolidColorBrush(color);
            KindTile.Background = new SolidColorBrush(Color.FromArgb(0x1F, color.R, color.G, color.B));

            SetSaveState(SaveState.Saved);
            UpdateHistoryButtons();
            UpdateCounts();
            DispatcherQueue.TryEnqueue(UpdateGridColumns);
        }

        // ==================== 保存状态 ====================

        private enum SaveState
        {
            Saved,
            Saving,
            Failed,
        }

        private void OnLogChanged(EditLog log)
        {
            if (!ReferenceEquals(log, CurrentLog))
            {
                return;
            }

            UpdateHistoryButtons();
            UpdateCounts();
            MarkDirty();
        }

        private void MarkDirty()
        {
            if (_saveTimer is null)
            {
                return;
            }

            _saveTimer.Stop();
            SetSaveState(SaveState.Saving);
            _saveTimer.Start();
        }

        private void CommitSave()
        {
            string? error = Validate();
            if (error is not null)
            {
                SetSaveState(SaveState.Failed);
                ShowBar("保存失败", error, InfoBarSeverity.Error);
                return;
            }

            SetSaveState(SaveState.Saved);
            RetryButton.Visibility = Visibility.Collapsed;
        }

        private string? Validate() => _kind switch
        {
            WorkbenchKind.Character when string.IsNullOrWhiteSpace(Character.Name)
                => "角色卡缺少 name 字段，补上名称后会自动重新保存。",
            WorkbenchKind.WorldBook when string.IsNullOrWhiteSpace(WorldBook.Name)
                => "世界书缺少 name 字段，补上名称后会自动重新保存。",
            WorkbenchKind.Preset when Prompts.Any(prompt => !prompt.IsMarker && string.IsNullOrWhiteSpace(prompt.Name))
                => "有提示词没有 name 字段，补上名称后会自动重新保存。",
            _ => null,
        };

        private void SetSaveState(SaveState state)
        {
            if (SaveRing is null)
            {
                return;
            }

            bool saving = state == SaveState.Saving;
            SaveRing.IsActive = saving;
            SaveRing.Visibility = saving ? Visibility.Visible : Visibility.Collapsed;
            SaveDot.Visibility = saving ? Visibility.Collapsed : Visibility.Visible;
            RetryButton.Visibility = state == SaveState.Failed ? Visibility.Visible : Visibility.Collapsed;

            switch (state)
            {
                case SaveState.Saving:
                    SaveStateText.Text = "保存中…";
                    SaveStateText.Foreground = Res("TextFillColorTertiaryBrush", Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A));
                    break;

                case SaveState.Failed:
                    SaveDot.Fill = Res("SystemFillColorCriticalBrush", Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C));
                    SaveStateText.Text = "保存失败";
                    SaveStateText.Foreground = Res("SystemFillColorCriticalBrush", Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C));
                    break;

                default:
                    SaveDot.Fill = Res("SystemFillColorSuccessBrush", Color.FromArgb(0xFF, 0x6C, 0xCB, 0x5F));
                    SaveStateText.Text = "已保存";
                    SaveStateText.Foreground = Res("TextFillColorSecondaryBrush", Color.FromArgb(0xFF, 0x5A, 0x5A, 0x5A));
                    break;
            }
        }

        private void Retry_Click(object sender, RoutedEventArgs e) => CommitSave();

        // ==================== 撤销 / 重做 ====================

        private void SetupAccelerators()
        {
            var undo = new KeyboardAccelerator { Key = VirtualKey.Z, Modifiers = VirtualKeyModifiers.Control };
            undo.Invoked += (_, args) =>
            {
                args.Handled = true;
                UndoStep();
            };

            var redo = new KeyboardAccelerator { Key = VirtualKey.Y, Modifiers = VirtualKeyModifiers.Control };
            redo.Invoked += (_, args) =>
            {
                args.Handled = true;
                RedoStep();
            };

            KeyboardAccelerators.Add(undo);
            KeyboardAccelerators.Add(redo);
        }

        private void Undo_Click(object sender, RoutedEventArgs e) => UndoStep();

        private void Redo_Click(object sender, RoutedEventArgs e) => RedoStep();

        private void UndoStep()
        {
            if (!CurrentLog.CanUndo)
            {
                return;
            }

            CurrentLog.Undo();
            UpdateHistoryButtons();
        }

        private void RedoStep()
        {
            if (!CurrentLog.CanRedo)
            {
                return;
            }

            CurrentLog.Redo();
            UpdateHistoryButtons();
        }

        private void UpdateHistoryButtons()
        {
            if (UndoButton is null)
            {
                return;
            }

            UndoButton.IsEnabled = CurrentLog.CanUndo;
            RedoButton.IsEnabled = CurrentLog.CanRedo;
        }

        // ==================== 标签 / 关键词 ====================

        private void TagRemove_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is EditTag tag)
            {
                tag.Remove(tag);
            }
        }

        private void AddCharTag_Click(object sender, RoutedEventArgs e) => AddCharacterTag();

        private void CharTagInput_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter)
            {
                return;
            }

            AddCharacterTag();
            e.Handled = true;
        }

        private void AddCharacterTag()
        {
            Character.AddTag(Character.TagInput);
            Character.TagInput = string.Empty;
        }

        private void AddKey_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is EntryForm entry)
            {
                entry.AddKey((element.Tag as string) == "secondary");
            }
        }

        private void KeyInput_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter)
            {
                return;
            }

            if (sender is FrameworkElement element && element.DataContext is EntryForm entry)
            {
                entry.AddKey((element.Tag as string) == "secondary");
                e.Handled = true;
            }
        }

        // ==================== 世界书 ====================

        private static readonly (string Name, string Author, EntrySeed[] Entries)[] WorldCatalog =
        {
            ("便利店与电车末班", "星酿工坊", ShopEntries),
            ("雨夜与旧唱片", "白鹭千夏", RainEntries),
        };

        private void ApplyWorldState()
        {
            bool bound = Character.HasWorld;
            EmbeddedEntriesHost.Visibility = bound ? Visibility.Visible : Visibility.Collapsed;
            EmbeddedFoot.Visibility = bound ? Visibility.Visible : Visibility.Collapsed;
            EmbeddedEmpty.Visibility = bound ? Visibility.Collapsed : Visibility.Visible;
            EmbeddedStatePill.Visibility = bound ? Visibility.Visible : Visibility.Collapsed;
            ChangeWorldText.Text = bound ? "更换" : "绑定";
            UnbindWorldButton.IsEnabled = bound;
        }

        private async void ChangeWorldBook_Click(object sender, RoutedEventArgs e) => await PickWorldBookAsync();

        private async void BindWorldBook_Click(object sender, RoutedEventArgs e) => await PickWorldBookAsync();

        private async Task PickWorldBookAsync()
        {
            ListView list = new()
            {
                SelectionMode = ListViewSelectionMode.Single,
                Height = 232,
                Margin = new Thickness(0, 4, 0, 0),
            };

            foreach ((string optionName, string optionAuthor, EntrySeed[] optionEntries) in WorldCatalog)
            {
                list.Items.Add(new ListViewItem
                {
                    Tag = optionName,
                    Content = new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = optionName,
                                FontSize = 13,
                                FontWeight = FontWeights.SemiBold,
                            },
                            new TextBlock
                            {
                                Text = $"{optionAuthor} · {optionEntries.Length} 个条目",
                                FontSize = 11,
                                Foreground = Res("TextFillColorTertiaryBrush", Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A)),
                            },
                        },
                    },
                });
            }

            ContentDialog dialog = new()
            {
                XamlRoot = XamlRoot,
                Title = Character.HasWorld ? "更换内嵌世界书" : "绑定世界书",
                Content = list,
                PrimaryButtonText = Character.HasWorld ? "更换" : "绑定",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                IsPrimaryButtonEnabled = false,
            };

            list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem is not null;

            if (await dialog.ShowAsync() != ContentDialogResult.Primary
                || list.SelectedItem is not ListViewItem item
                || item.Tag is not string picked)
            {
                return;
            }

            (string name, string author, EntrySeed[] entries) = WorldCatalog.First(option => option.Name == picked);
            EmbeddedWorld.Load(name, author, entries.Select(MakeEntry));
            Character.HasWorld = true;
            ApplyWorldState();
            UpdateCounts();
            ShowBar("已绑定世界书", $"{name} 已写入角色卡的内嵌世界书字段。", InfoBarSeverity.Success);
            MarkDirty();
        }

        private async void UnbindWorldBook_Click(object sender, RoutedEventArgs e)
        {
            if (!Character.HasWorld)
            {
                return;
            }

            ContentDialog dialog = new()
            {
                XamlRoot = XamlRoot,
                Title = "解绑内嵌世界书",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "角色卡里不会再携带这份世界书，worlds 目录里的文件本身不会被删除。",
                },
                PrimaryButtonText = "解绑",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };

            if (Application.Current.Resources.TryGetValue("DangerDialogButtonStyle", out object style)
                && style is Style dangerStyle)
            {
                dialog.PrimaryButtonStyle = dangerStyle;
            }

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            Character.HasWorld = false;
            ApplyWorldState();
            ShowBar("已解绑", "内嵌世界书已从角色卡中移除。", InfoBarSeverity.Success);
            MarkDirty();
        }

        private void GoWorldBook_Click(object sender, RoutedEventArgs e) => ApplyKind(WorkbenchKind.WorldBook);

        private void AddEntry_Click(object sender, RoutedEventArgs e)
        {
            EntryForm entry = new()
            {
                Number = WorldBook.Entries.Count + 1,
                Comment = "新条目",
                Order = "100",
                Probability = "100",
                Depth = "4",
                Log = _worldLog,
            };

            WorldBook.Entries.Add(entry);
            WorldBook.Relabel();
            UpdateCounts();

            _worldLog.Record(
                $"entry:{Guid.NewGuid():N}",
                () =>
                {
                    WorldBook.Entries.Remove(entry);
                    WorldBook.Relabel();
                    UpdateCounts();
                },
                () =>
                {
                    WorldBook.Entries.Add(entry);
                    WorldBook.Relabel();
                    UpdateCounts();
                });

            ShowBar("已新增条目", "补上关键词与正文后即可被命中。", InfoBarSeverity.Informational);
        }

        // ==================== 卡面 ====================

        private void ChangeCover_Click(object sender, RoutedEventArgs e)
        {
            ShowBar("更换卡面", "原型未接入文件选择器：正式版会在这里挑 PNG 并裁成 400 × 600。", InfoBarSeverity.Informational);
        }

        private void RemoveCover_Click(object sender, RoutedEventArgs e)
        {
            Character.HasCover = false;
            ShowBar("已移除卡面", "角色卡会用名称首字占位，随时可以再换回来。", InfoBarSeverity.Success);
        }

        // ==================== 预设 ====================

        private void AddPrompt_Click(object sender, RoutedEventArgs e)
        {
            PromptForm prompt = new()
            {
                Identifier = $"custom-{Prompts.Count + 1}",
                Name = "新的提示词",
                Role = 0,
                Content = string.Empty,
                Log = _presetLog,
            };

            int index = Math.Clamp((_presetPage + 1) * PresetPageSize, 0, Prompts.Count);
            Prompts.Insert(index, prompt);
            _presetPage = index / PresetPageSize;
            RefreshPresetPage();
            UpdateCounts();

            _presetLog.Record(
                $"prompt:{Guid.NewGuid():N}",
                () =>
                {
                    Prompts.Remove(prompt);
                    RefreshPresetPage();
                    UpdateCounts();
                },
                () =>
                {
                    Prompts.Insert(Math.Min(index, Prompts.Count), prompt);
                    RefreshPresetPage();
                    UpdateCounts();
                });

            ShowBar("已添加提示词", "顺序由 prompt_order 管理，新条目会先落在当前页末尾。", InfoBarSeverity.Informational);
        }

        private async void DeletePrompt_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not PromptForm prompt)
            {
                return;
            }

            int index = Prompts.IndexOf(prompt);
            if (index < 0)
            {
                return;
            }

            ContentDialog dialog = new()
            {
                XamlRoot = XamlRoot,
                Title = "删除提示词",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"从预设中移除「{prompt.Name}」，prompt_order 里对应的位置会一起清掉。",
                },
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };

            if (Application.Current.Resources.TryGetValue("DangerDialogButtonStyle", out object style)
                && style is Style dangerStyle)
            {
                dialog.PrimaryButtonStyle = dangerStyle;
            }

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            Prompts.RemoveAt(index);
            RefreshPresetPage();
            UpdateCounts();

            _presetLog.Record(
                $"prompt:{Guid.NewGuid():N}",
                () =>
                {
                    Prompts.Insert(Math.Min(index, Prompts.Count), prompt);
                    RefreshPresetPage();
                    UpdateCounts();
                },
                () =>
                {
                    Prompts.Remove(prompt);
                    RefreshPresetPage();
                    UpdateCounts();
                });

            ShowBar("已删除提示词", $"{prompt.Name} 已从预设中移除。", InfoBarSeverity.Success);
        }

        private void RefreshPresetPage()
        {
            int pageCount = Math.Max(1, (Prompts.Count + PresetPageSize - 1) / PresetPageSize);
            _presetPage = Math.Clamp(_presetPage, 0, pageCount - 1);

            VisiblePrompts.Clear();
            foreach (PromptForm prompt in Prompts.Skip(_presetPage * PresetPageSize).Take(PresetPageSize))
            {
                VisiblePrompts.Add(prompt);
            }

            BuildPager(pageCount);
            PresetFootText.Text = pageCount > 1
                ? $"第 {_presetPage + 1} / {pageCount} 页 · 本页 {VisiblePrompts.Count} 条 · 共 {Prompts.Count} 条"
                : $"共 {Prompts.Count} 条提示词";
            PromptCountChip.Text = $"{Prompts.Count} 条";
        }

        private void BuildPager(int pageCount)
        {
            PagerHost.Children.Clear();
            if (pageCount <= 1)
            {
                return;
            }

            PagerHost.Children.Add(MakePagerIcon("\uE76B", _presetPage > 0, () =>
            {
                _presetPage--;
                RefreshPresetPage();
            }));

            for (int page = 0; page < pageCount; page++)
            {
                int target = page;
                PagerHost.Children.Add(MakePagerNumber(page + 1, page == _presetPage, () =>
                {
                    _presetPage = target;
                    RefreshPresetPage();
                }));
            }

            PagerHost.Children.Add(MakePagerIcon("\uE76C", _presetPage < pageCount - 1, () =>
            {
                _presetPage++;
                RefreshPresetPage();
            }));
        }

        private Button MakePagerIcon(string glyph, bool enabled, Action click)
        {
            Button button = new()
            {
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                Content = new FontIcon { Glyph = glyph, FontSize = 9 },
                IsEnabled = enabled,
                Style = Application.Current.Resources.TryGetValue("SubtleIconButtonStyle", out object style) && style is Style subtle
                    ? subtle
                    : null,
            };

            button.Click += (_, _) => click();
            return button;
        }

        private Button MakePagerNumber(int number, bool current, Action click)
        {
            Button button = new()
            {
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                Content = new TextBlock { Text = number.ToString(), FontSize = 12 },
                FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
                Style = Application.Current.Resources.TryGetValue("SubtleIconButtonStyle", out object style) && style is Style subtle
                    ? subtle
                    : null,
            };

            if (current)
            {
                button.Background = new SolidColorBrush(Color.FromArgb(0x1A, 0x2B, 0x88, 0xD8));
                button.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x2B, 0x88, 0xD8));
            }

            button.Click += (_, _) => click();
            return button;
        }

        // ==================== 布局与计数 ====================

        private void UpdateGridColumns()
        {
            // 按「最小可读列宽」倒推列数，硬编码像素阈值在不同 DPI / 窗口宽度下会算错：
            // 条目卡里关键词区两列并排、数字字段四格并列，再窄于 420 就开始挤压；
            // 预设卡只有类型胶囊 + identifier + 正文，330 仍可读。
            SetColumns(EntryList, Fit(EntryList.ActualWidth, 420, 3, GridSpacing));
            SetColumns(EmbeddedEntryList, Fit(EmbeddedEntryList.ActualWidth, 420, 3, GridSpacing));
            SetColumns(PromptList, Fit(PromptList.ActualWidth, 330, 4, GridSpacing));
        }

        // 卡片间距由模板自身 Margin 承担（ItemsControl 用负 Margin 抵掉首尾），
        // 所以这里按 0 间距分列。
        private const double GridSpacing = 0;

        private static int Fit(double width, double minItem, int max, double spacing)
        {
            if (width <= 0)
            {
                return 1;
            }

            int columns = (int)((width + spacing) / (minItem + spacing));
            return Math.Clamp(columns, 1, max);
        }

        private static void SetColumns(ItemsControl host, int columns)
        {
            if (host.ItemsPanelRoot is ColumnGrid grid && grid.Columns != columns)
            {
                grid.Columns = columns;
            }
        }

        private void UpdateCounts()
        {
            if (EntryCountChip is null)
            {
                return;
            }

            int embeddedEnabled = EmbeddedWorld.Entries.Count(entry => entry.Enabled);
            EmbeddedCountChip.Text = $"{EmbeddedWorld.Entries.Count} 个条目";
            EmbeddedSubText.Text = $"{EmbeddedWorld.Name} · {EmbeddedWorld.Author} · {embeddedEnabled} 个启用";

            EntryCountChip.Text = $"{WorldBook.Entries.Count} 个";
            EntryEnabledChip.Text = $"{WorldBook.Entries.Count(entry => entry.Enabled)} 个启用";
            PromptCountChip.Text = $"{Prompts.Count} 条";
        }

        // ==================== 导航 ====================

        private void Back_Click(object sender, RoutedEventArgs e) => Close();

        private void Done_Click(object sender, RoutedEventArgs e) => Close();

        private void Close() => App.MainWindow?.NavigateTo("resources");

        // ==================== 工具 ====================

        private void ShowBar(string title, string message, InfoBarSeverity severity)
        {
            ActionInfoBar.Title = title;
            ActionInfoBar.Message = message;
            ActionInfoBar.Severity = severity;
            ActionInfoBar.IsOpen = true;
        }

        private static Brush Res(string key, Color fallback)
            => Application.Current.Resources.TryGetValue(key, out object value) && value is Brush brush
                ? brush
                : new SolidColorBrush(fallback);

        // ==================== 跳转入口 ====================

        /// <summary>由资源管理页在跳转前写入，工作台据此决定打开哪份资源。</summary>
        public static WorkbenchKind PendingKind { get; set; } = WorkbenchKind.Character;

        /// <summary>被打开资源的文件名（含扩展名）。</summary>
        public static string PendingFile { get; set; } = string.Empty;
    }
}
