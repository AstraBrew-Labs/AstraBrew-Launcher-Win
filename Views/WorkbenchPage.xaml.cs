using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AstraBrew_Launcher.Services;
using AstraBrew_Launcher.Views.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using WinRT.Interop;
using LibraryWorldBook = AstraBrew_Launcher.Services.WorldBook;

namespace AstraBrew_Launcher.Views
{
    /// <summary>工作台在编辑哪一类资源。打开时确定，页面内不再切换。</summary>
    public enum WorkbenchKind
    {
        Character,
        WorldBook,
        Preset,
    }

    /// <summary>
    /// 资源工作台：一个页面对应**一个真实文件**，左侧改资料、右侧写长文本，改完自动写回。
    ///
    /// 与原型期的区别：
    /// · 数据来自磁盘（角色卡读 PNG 的 chara/ccv3 块或 .json，世界书读 entries，预设读 prompts），
    ///   不再是写死的「星野澪 / 便利店与电车末班」；
    /// · 保存真的落盘（`WorkbenchDocument`），保存态反映成功与否；
    /// · 类型在打开时由资源管理页决定，页面内没有切换入口——角色卡工作台就是角色卡，
    ///   预设工作台就是预设，避免「在角色卡里点到预设」这种事。
    /// </summary>
    public sealed partial class WorkbenchPage : Page
    {
        private const int PresetPageSize = 6;

        /// <summary>世界书条目每页几条。展开后的条目卡有十来个输入框，铺满一屏就会卡。</summary>
        private const int EntryPageSize = 6;

        private readonly EditLog _characterLog = new();
        private readonly EditLog _worldLog = new();
        private readonly EditLog _presetLog = new();

        private WorkbenchDocument? _document;
        private WorkbenchKind _kind = WorkbenchKind.Character;
        private string _path = string.Empty;

        /// <summary>加载失败的原因；非空时页面只读，保存会直接失败并提示。</summary>
        private string? _loadError;

        /// <summary>世界书的 entries 原本是数组还是对象字典——保存时按原形状写回。</summary>
        private bool _entriesAreArray = true;

        /// <summary>角色卡里开场白用的键名（first_mes 或 first_message），保存时沿用原来的。</summary>
        private string _firstMessageKey = "first_mes";

        private int _presetPage;
        private int _entryPage;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _saveTimer;
        private bool _ready;

        /// <summary>有未落盘的改动。打开就看一眼再关掉不该重写文件（会改变字节与修改时间）。</summary>
        private bool _dirty;

        // ==================== 对外绑定 ====================

        public CharacterForm Character { get; } = new();

        public WorldBookForm WorldBook { get; } = new();

        public WorldBookForm EmbeddedWorld { get; } = new();

        public ObservableCollection<PromptForm> Prompts { get; } = new();

        public ObservableCollection<PromptForm> VisiblePrompts { get; } = new();

        /// <summary>当前页的世界书条目；<see cref="WorldBook"/> 里始终是完整列表。</summary>
        public ObservableCollection<EntryForm> VisibleEntries { get; } = new();

        public WorkbenchPage()
        {
            InitializeComponent();

            _kind = PendingKind;
            _path = PendingPath;

            LoadResource();
            AttachLogs();

            _saveTimer = DispatcherQueue.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(700);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => CommitSave();

            SetupAccelerators();

            ApplyKind(_kind);
            ApplyWorldState();

            // 首帧只画框架：预设卡片每张要塞 ComboBox/开关/输入框，171 条的预设
            // 一次性铺 6 张要 150 ms 上下，页面会「先白一下再出现」。这里先给个占位文案，
            // 等首帧画完再铺内容。
            if (_kind == WorkbenchKind.Preset)
            {
                PresetFootText.Text = "正在准备提示词…";
                PromptCountChip.Text = $"{Prompts.Count} 条";
            }

            UpdateCounts();
            SetSaveState(_document is null ? SaveState.Failed : SaveState.Saved);

            if (_loadError is { Length: > 0 } error)
            {
                Notify("打不开这份资源", error, NotifySeverity.Error);
            }

            Loaded += (_, _) =>
            {
                UpdateGridColumns();
                UpdateHistoryButtons();

                if (_kind == WorkbenchKind.Preset)
                {
                    RefreshPresetPage();
                }
            };

            // 每个网格自己盯着宽度：只在页面 SizeChanged 时算会拿到旧值，导致窄窗仍是三列。
            EntryList.SizeChanged += (_, _) => UpdateGridColumns();
            PromptList.SizeChanged += (_, _) => UpdateGridColumns();
            EmbeddedEntryList.SizeChanged += (_, _) => UpdateGridColumns();
            SizeChanged += (_, _) => UpdateGridColumns();
        }

        // ==================== 加载 ====================

        private void LoadResource()
        {
            if (_path.Length == 0)
            {
                _loadError = "没有指定要编辑的文件。请从「资源管理」右侧点「打开工作台」进入。";
                return;
            }

            if (!File.Exists(_path))
            {
                _loadError = $"文件不存在：{_path}";
                return;
            }

            try
            {
                _document = WorkbenchDocument.Open(_path);

                switch (_kind)
                {
                    case WorkbenchKind.WorldBook:
                        LoadWorldBook();
                        break;

                    case WorkbenchKind.Preset:
                        LoadPreset();
                        break;

                    default:
                        LoadCharacter();
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or JsonException)
            {
                _document = null;
                _loadError = ex.Message;
            }
        }

        private void LoadCharacter()
        {
            JsonObject root = _document!.Root;

            Character.Name = Text(root, "name");
            Character.Creator = Text(root, "creator");
            Character.Version = Text(root, "character_version");
            Character.Description = Text(root, "description");
            Character.Personality = Text(root, "personality");
            Character.Scenario = Text(root, "scenario");

            // 老卡用的是 first_message，措辞不同但是同一个字段
            _firstMessageKey = root.ContainsKey("first_message") && !root.ContainsKey("first_mes")
                ? "first_message"
                : "first_mes";
            Character.FirstMessage = Text(root, _firstMessageKey);

            Character.Tags.Clear();
            if (root["tags"] is JsonArray tags)
            {
                foreach (JsonNode? tag in tags)
                {
                    if (tag is JsonValue value && value.TryGetValue(out string? text) && text is { Length: > 0 })
                    {
                        Character.Tags.Add(new EditTag(text, Character.RemoveTag));
                    }
                }
            }

            Character.CanReplaceCover = _document.CanReplaceCover;
            Character.CoverSource = _document.CanReplaceCover
                ? ResourcePalette.Thumbnail(_path, 208, ignoreCache: true)
                : null;

            // 内嵌世界书：character_book 里有条目才算「已绑定」
            JsonObject? book = root["character_book"] as JsonObject;
            List<EntryForm> embedded = ReadEntryForms(book?["entries"], characterBook: true);
            Character.HasWorld = embedded.Count > 0;
            EmbeddedWorld.Load(Text(book, "name"), string.Empty, embedded);
        }

        private void LoadWorldBook()
        {
            JsonObject root = _document!.Root;

            WorldBook.Name = System.IO.Path.GetFileNameWithoutExtension(_path);
            WorldBook.Author = Text(root, "author");

            List<EntryForm> entries = ReadEntryForms(root["entries"], characterBook: false);

            // 默认全部收起：展开的卡片要布局输入框，一张约 90 ms。
            // 收起时每行只有标题 + 正文预览，一眼能扫完，点一下才展开编辑。
            WorldBook.Load(WorldBook.Name, WorldBook.Author, entries);
            _entryPage = 0;
            RefreshEntryPage();
        }

        private void LoadPreset()
        {
            JsonObject root = _document!.Root;
            Dictionary<string, bool> order = ReadPromptOrder(root);

            Prompts.Clear();

            if (root["prompts"] is not JsonArray prompts)
            {
                return;
            }

            foreach (JsonNode? item in prompts)
            {
                if (item is not JsonObject prompt)
                {
                    continue;
                }

                string identifier = Text(prompt, "identifier");
                bool inOrder = order.TryGetValue(identifier, out bool enabled);

                Prompts.Add(new PromptForm
                {
                    Node = prompt,
                    Identifier = identifier,
                    Name = Text(prompt, "name"),
                    Content = Text(prompt, "content"),
                    Role = RoleIndex(Text(prompt, "role")),
                    IsMarker = IsTrue(prompt, "marker"),

                    // 没有 prompt_order（老预设）时按全开处理；有 order 就完全以 order 为准
                    InOrder = inOrder || order.Count == 0,
                    Enabled = order.Count == 0 || (inOrder && enabled),
                });
            }
        }

        /// <summary>加载期不挂日志（否则一次加载会灌进几百步撤销），加载完再挂上并清空。</summary>
        private void AttachLogs()
        {
            Character.Log = _characterLog;
            WorldBook.Log = _worldLog;

            foreach (EntryForm entry in WorldBook.Entries)
            {
                entry.Log = _worldLog;
            }

            foreach (EntryForm entry in EmbeddedWorld.Entries)
            {
                entry.Log = _worldLog;
            }

            foreach (PromptForm prompt in Prompts)
            {
                prompt.Log = _presetLog;
            }

            _characterLog.Clear();
            _worldLog.Clear();
            _presetLog.Clear();

            _characterLog.Changed += () => OnLogChanged(_characterLog);
            _worldLog.Changed += () => OnLogChanged(_worldLog);
            _presetLog.Changed += () => OnLogChanged(_presetLog);

            _ready = true;
        }

        // ==================== 页头 ====================

        private void ApplyKind(WorkbenchKind kind)
        {
            _kind = kind;

            CharacterView.Visibility = kind == WorkbenchKind.Character ? Visibility.Visible : Visibility.Collapsed;
            WorldBookView.Visibility = kind == WorkbenchKind.WorldBook ? Visibility.Visible : Visibility.Collapsed;
            PresetView.Visibility = kind == WorkbenchKind.Preset ? Visibility.Visible : Visibility.Collapsed;

            (string title, string glyph, string accent) = kind switch
            {
                WorkbenchKind.WorldBook => ("世界书工作台", "\uE8F1", "#8E44DC"),
                WorkbenchKind.Preset => ("预设工作台", "\uE9E9", "#C08A00"),
                _ => ("角色卡工作台", "\uE77B", "#2B88D8"),
            };

            Color color = ResourcePalette.Parse(accent);
            KindTitleText.Text = title;
            KindGlyph.Glyph = glyph;
            KindGlyph.Foreground = new SolidColorBrush(color);
            KindTile.Background = new SolidColorBrush(Color.FromArgb(0x1F, color.R, color.G, color.B));

            UpdateSubText();
            UpdateHistoryButtons();
            UpdateCounts();
            DispatcherQueue.TryEnqueue(UpdateGridColumns);
        }

        /// <summary>标题下面那行：目录/文件名 · 体积；加载失败时直接显示原因。</summary>
        private void UpdateSubText()
        {
            if (KindSubText is null)
            {
                return;
            }

            if (_loadError is { Length: > 0 } error)
            {
                KindSubText.Text = error;
                KindSubText.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C));
                return;
            }

            KindSubText.Foreground = Res("TextFillColorTertiaryBrush", Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A));

            string folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(_path) ?? string.Empty);
            string size = File.Exists(_path) ? ResourcePalette.Size(new FileInfo(_path).Length) : "—";
            KindSubText.Text = $"{folder}/{System.IO.Path.GetFileName(_path)} · {size}";

            if (WorldSizeText is not null)
            {
                WorldSizeText.Text = size;
            }
        }

        // ==================== 保存 ====================

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
            if (_saveTimer is null || !_ready)
            {
                return;
            }

            _dirty = true;
            _saveTimer.Stop();
            SetSaveState(SaveState.Saving);
            _saveTimer.Start();
        }

        private void CommitSave()
        {
            _saveTimer?.Stop();

            if (!_ready)
            {
                return;
            }

            if (!_dirty)
            {
                return;
            }

            if (_document is null)
            {
                SetSaveState(SaveState.Failed);
                return;
            }

            string? error = Validate();
            if (error is not null)
            {
                SetSaveState(SaveState.Failed);
                Notify("保存失败", error, NotifySeverity.Error);
                return;
            }

            try
            {
                SyncToDocument();
                _document.Save();

                _dirty = false;
                SetSaveState(SaveState.Saved);
                UpdateSubText();
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or JsonException)
            {
                SetSaveState(SaveState.Failed);
                Notify("保存失败", $"{System.IO.Path.GetFileName(_path)}：{ex.Message}", NotifySeverity.Error);
            }
        }

        private string? Validate() => _kind switch
        {
            WorkbenchKind.Character when string.IsNullOrWhiteSpace(Character.Name)
                => "角色卡缺少 name 字段，补上名称后会自动重新保存。",
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

        // ==================== 表单 → 文件 ====================

        private void SyncToDocument()
        {
            switch (_kind)
            {
                case WorkbenchKind.WorldBook:
                    SyncWorldBook();
                    break;

                case WorkbenchKind.Preset:
                    SyncPreset();
                    break;

                default:
                    SyncCharacter();
                    break;
            }
        }

        private void SyncCharacter()
        {
            JsonObject root = _document!.Root;

            root["name"] = Character.Name;
            PutText(root, "creator", Character.Creator);
            PutText(root, "character_version", Character.Version);
            PutText(root, "description", Character.Description);
            PutText(root, "personality", Character.Personality);
            PutText(root, "scenario", Character.Scenario);
            PutText(root, _firstMessageKey, Character.FirstMessage);

            var tags = new JsonArray();
            foreach (EditTag tag in Character.Tags)
            {
                tags.Add(tag.Text);
            }

            root["tags"] = tags;

            if (Character.HasWorld)
            {
                JsonObject book = root["character_book"] as JsonObject ?? new JsonObject();
                book["name"] = EmbeddedWorld.Name;

                var entries = new JsonArray();
                foreach (EntryForm entry in EmbeddedWorld.Entries)
                {
                    entries.Add(BuildEntryNode(entry, characterBook: true));
                }

                book["entries"] = entries;
                root["character_book"] = book;
            }
            else if (root.ContainsKey("character_book"))
            {
                root.Remove("character_book");
            }
        }

        private void SyncWorldBook()
        {
            JsonObject root = _document!.Root;

            if (WorldBook.Author.Length > 0 || root.ContainsKey("author"))
            {
                PutText(root, "author", WorldBook.Author);
            }

            if (_entriesAreArray)
            {
                var array = new JsonArray();
                foreach (EntryForm entry in WorldBook.Entries)
                {
                    array.Add(BuildEntryNode(entry, characterBook: false));
                }

                root["entries"] = array;
            }
            else
            {
                // 原文件是 {uid: {...}} 的字典：键必须沿用，否则条目顺序与引用都会变
                var map = new JsonObject();
                foreach (EntryForm entry in WorldBook.Entries)
                {
                    string key = entry.NodeKey ?? Guid.NewGuid().ToString("N")[..6];
                    map[key] = BuildEntryNode(entry, characterBook: false);
                    entry.NodeKey = key;
                }

                root["entries"] = map;
            }
        }

        private void SyncPreset()
        {
            JsonObject root = _document!.Root;

            var prompts = new JsonArray();
            foreach (PromptForm prompt in Prompts)
            {
                // 深拷贝一份再改：原节点还挂在旧数组上，直接塞进新数组会报「已有父节点」
                JsonObject node = prompt.Node?.DeepClone() as JsonObject ?? new JsonObject();
                node["identifier"] = prompt.Identifier;
                node["name"] = prompt.Name;

                // 标记（chatHistory / worldInfoBefore…）在酒馆里是占位符，本来就没有正文与角色，
                // 给它们补 content/role 等于往预设里塞脏字段
                if (!prompt.IsMarker)
                {
                    node["content"] = prompt.Content;
                    node["role"] = RoleName(prompt.Role);
                }

                prompt.Node = node;
                prompts.Add(node);
            }

            root["prompts"] = prompts;

            // prompt_order 决定顺序与开关；没有就补一份默认分组
            JsonObject? group = null;
            if (root["prompt_order"] is JsonArray orders)
            {
                group = orders.OfType<JsonObject>().FirstOrDefault(item => Int(item, "character_id") == 100000)
                    ?? orders.OfType<JsonObject>().FirstOrDefault();
            }

            if (group is null)
            {
                group = new JsonObject { ["character_id"] = 100000 };
                if (root["prompt_order"] is JsonArray existing)
                {
                    existing.Add(group);
                }
                else
                {
                    root["prompt_order"] = new JsonArray { group };
                }
            }

            // 保留原来的注入顺序：prompt_order 的顺序才是酒馆真正用的顺序，
            // 按 prompts 数组重排等于把用户排好的顺序洗了一遍。
            var byIdentifier = new Dictionary<string, PromptForm>(StringComparer.Ordinal);
            foreach (PromptForm prompt in Prompts)
            {
                byIdentifier.TryAdd(prompt.Identifier, prompt);
            }

            var order = new JsonArray();
            var written = new HashSet<string>(StringComparer.Ordinal);

            if (group["order"] is JsonArray previous)
            {
                foreach (JsonObject item in previous.OfType<JsonObject>())
                {
                    string identifier = Text(item, "identifier");

                    // 被删掉的提示词不再写回 order
                    if (!byIdentifier.TryGetValue(identifier, out PromptForm? form) || !written.Add(identifier))
                    {
                        continue;
                    }

                    order.Add(new JsonObject
                    {
                        ["identifier"] = identifier,
                        ["enabled"] = form.Enabled,
                    });
                }
            }

            // 新加的提示词接在末尾；原本不在 order 里的保持不在，不替用户做主
            foreach (PromptForm prompt in Prompts)
            {
                if (written.Contains(prompt.Identifier) || (!prompt.InOrder && prompt.Node is not null))
                {
                    continue;
                }

                written.Add(prompt.Identifier);
                prompt.InOrder = true;

                order.Add(new JsonObject
                {
                    ["identifier"] = prompt.Identifier,
                    ["enabled"] = prompt.Enabled,
                });
            }

            group["order"] = order;
        }

        /// <summary>
        /// 把表单写成条目节点。已有节点上只覆盖界面上能改的键，
        /// <c>use_regex</c>、<c>selective</c> 这些看不见的字段保持原样。
        /// </summary>
        private JsonObject BuildEntryNode(EntryForm form, bool characterBook)
        {
            JsonObject node = form.Node?.DeepClone() as JsonObject ?? new JsonObject();

            node["comment"] = form.Comment;
            if (node.ContainsKey("name") || characterBook)
            {
                node["name"] = form.Comment;
            }

            node["content"] = form.Content;

            string primaryKey = node.ContainsKey("keys") || characterBook ? "keys" : "key";
            string secondaryKey = node.ContainsKey("secondary_keys") || characterBook ? "secondary_keys" : "keysecondary";

            node[primaryKey] = ToJsonArray(form.Keys);
            node[secondaryKey] = ToJsonArray(form.SecondaryKeys);

            if (characterBook)
            {
                node["enabled"] = form.Enabled;
            }
            else
            {
                node["disable"] = !form.Enabled;
            }

            string orderKey = node.ContainsKey("insertion_order") ? "insertion_order" : "order";
            node[orderKey] = Int(form.Order, 100);
            node["position"] = form.Position;
            node["depth"] = Int(form.Depth, 4);

            if (!characterBook || node.ContainsKey("probability"))
            {
                node["probability"] = Int(form.Probability, 100);
            }

            form.Node = node;
            return node;
        }

        // ==================== 撤销 / 重做 ====================

        private EditLog CurrentLog => _kind switch
        {
            WorkbenchKind.WorldBook => _worldLog,
            WorkbenchKind.Preset => _presetLog,
            _ => _characterLog,
        };

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

        // ==================== 内嵌世界书 ====================

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

        /// <summary>从 worlds 目录里挑一本真实世界书，把条目整套拷进角色卡。</summary>
        private async Task PickWorldBookAsync()
        {
            IReadOnlyList<LibraryWorldBook> worlds = await Task.Run(TavernLibrary.LoadWorldBooks);

            if (worlds.Count == 0)
            {
                Notify(
                    "没有可绑定的世界书",
                    $"worlds 目录里还没有世界书文件：{TavernLibrary.WorldsDirectory()}",
                    NotifySeverity.Warning);
                return;
            }

            var list = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,
                Height = 260,
                Margin = new Thickness(0, 4, 0, 0),
            };

            foreach (LibraryWorldBook world in worlds)
            {
                list.Items.Add(new ListViewItem
                {
                    Tag = world.FullPath,
                    Content = new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = world.Name,
                                FontSize = 13,
                                FontWeight = FontWeights.SemiBold,
                            },
                            new TextBlock
                            {
                                Text = $"{world.EntryCount} 个条目 · {world.EnabledCount} 个启用 · {ResourcePalette.Size(world.Bytes)}",
                                FontSize = 11,
                                Foreground = Res("TextFillColorTertiaryBrush", Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A)),
                            },
                        },
                    },
                });
            }

            var dialog = new ContentDialog
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
                || item.Tag is not string path)
            {
                return;
            }

            try
            {
                (string name, List<EntryForm> entries) = await Task.Run(() => ReadWorldFile(path));

                foreach (EntryForm entry in entries)
                {
                    entry.Log = _worldLog;
                }

                EmbeddedWorld.Load(name, string.Empty, entries);
                Character.HasWorld = true;
                ApplyWorldState();
                UpdateCounts();
                MarkDirty();

                Notify("已绑定世界书", $"{name} 的 {entries.Count} 个条目已写入角色卡的内嵌世界书。", NotifySeverity.Success);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Notify("读取世界书失败", ex.Message, NotifySeverity.Error);
            }
        }

        private async void UnbindWorldBook_Click(object sender, RoutedEventArgs e)
        {
            if (!Character.HasWorld)
            {
                return;
            }

            var dialog = new ContentDialog
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
            UpdateCounts();
            MarkDirty();
            Notify("已解绑", "保存后角色卡里的 character_book 会被移除。", NotifySeverity.Success);
        }

        /// <summary>点标题或箭头：展开 / 收起这一条的编辑区。</summary>
        private void ToggleEntry_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is EntryForm entry)
            {
                entry.IsExpanded = !entry.IsExpanded;
            }
        }

        private void AddEntry_Click(object sender, RoutedEventArgs e)
        {
            var entry = new EntryForm
            {
                Number = WorldBook.Entries.Count + 1,
                Comment = "新条目",
                IsExpanded = true,
                Order = "100",
                Probability = "100",
                Depth = "4",
                Log = _worldLog,
            };

            WorldBook.Entries.Add(entry);
            WorldBook.Relabel();

            // 跳到新条目所在的那一页并展开，省得用户再翻一次
            _entryPage = (WorldBook.Entries.Count - 1) / EntryPageSize;
            RefreshEntryPage();
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

            Notify("已新增条目", "补上关键词与正文后即可被命中。", NotifySeverity.Informational);
        }

        // ==================== 卡面 ====================

        private async void ChangeCover_Click(object sender, RoutedEventArgs e)
        {
            if (_document is null || !_document.CanReplaceCover)
            {
                Notify(
                    "这份卡没有可更换的图片",
                    "只有 .png 角色卡才有卡面；.json 卡只是一份设定文件。",
                    NotifySeverity.Warning);
                return;
            }

            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".png");
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));

            StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            // 先把未落盘的改动写进去，再换图——换图用的是磁盘上的当前文本块
            CommitSave();

            try
            {
                _document.ReplaceCover(file.Path);
                Character.CoverSource = ResourcePalette.Thumbnail(_path, 208, ignoreCache: true);
                UpdateSubText();

                Notify("已更换卡面", $"{file.Name} 的图像已写入，原来的角色设定保持不变。", NotifySeverity.Success);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Notify("更换卡面失败", ex.Message, NotifySeverity.Error);
            }
        }

        /// <summary>
        /// 长文本放大编辑。
        ///
        /// 卡片里的输入框高度是固定的（内容 3000 字时也不该把整页顶长），
        /// 真要细改就开这个弹窗：大框、实时字数，关掉才写回模型——
        /// 边打字边写回会让防抖保存每 700ms 触发一次。
        /// </summary>
        private async void ExpandText_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string key })
            {
                return;
            }

            string title;
            string hint;
            Func<string> read;
            Action<string> write;

            switch (key)
            {
                case "personality":
                    title = "性格";
                    hint = "说话方式、脾气、在意的事…";
                    read = () => Character.Personality;
                    write = value => Character.Personality = value;
                    break;

                case "scenario":
                    title = "场景";
                    hint = "故事发生在哪、此刻正在做什么…";
                    read = () => Character.Scenario;
                    write = value => Character.Scenario = value;
                    break;

                case "firstMessage":
                    title = "开场白";
                    hint = "见面时说的第一句话…";
                    read = () => Character.FirstMessage;
                    write = value => Character.FirstMessage = value;
                    break;

                default:
                    title = "简介";
                    hint = "外貌、身份、与你的关系…";
                    read = () => Character.Description;
                    write = value => Character.Description = value;
                    break;
            }

            string? edited = await ShowTextEditorAsync(title, hint, read());
            if (edited is not null && edited != read())
            {
                write(edited);
            }
        }

        /// <summary>点预设卡片上的笔：编辑这条提示词的正文。</summary>
        private async void EditPrompt_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not PromptForm prompt)
            {
                return;
            }

            string? edited = await ShowTextEditorAsync(
                string.IsNullOrWhiteSpace(prompt.Name) ? "提示词正文" : prompt.Name,
                "这条提示词注入到上下文里的内容。",
                prompt.Content);

            if (edited is not null)
            {
                prompt.Content = edited;
            }
        }

        /// <summary>
        /// 长文本的放大编辑框，返回编辑后的文本；取消返回 null。
        ///
        /// 两个坑记在这：卡里的正文常带 CRLF，直接塞进 TextBox 会被截在第一个换行处；
        /// 而 TextBox 的 Text 必须在挂进可视树之后（Loaded）再赋，否则同样只剩第一段。
        /// </summary>
        private async Task<string?> ShowTextEditorAsync(string title, string hint, string original)
        {
            bool crlf = original.Contains("\r\n", StringComparison.Ordinal);
            string initial = original.Replace("\r\n", "\n").Replace('\r', '\n');
            bool seeded = false;

            var box = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalContentAlignment = VerticalAlignment.Top,
                Height = 420,
                FontSize = 13,
            };

            ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Disabled);

            var count = new TextBlock
            {
                Text = $"{initial.Length} 字",
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = Res("TextFillColorTertiaryBrush", Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A)),
            };

            void SyncCount() => count.Text = $"{box.Text.Length} 字";

            box.TextChanged += (_, _) => SyncCount();
            box.Loaded += (_, _) =>
            {
                if (!seeded)
                {
                    seeded = true;
                    box.Text = initial;
                }

                SyncCount();
                box.Focus(FocusState.Programmatic);
                box.SelectionStart = box.Text.Length;
            };

            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Res("TextFillColorTertiaryBrush", Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A)),
            });
            content.Children.Add(box);
            content.Children.Add(count);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = title,
                Content = content,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
            };
            dialog.Resources["ContentDialogMaxWidth"] = 760;

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return null;
            }

            return crlf ? box.Text.Replace("\n", "\r\n") : box.Text;
        }

        // ==================== 预设 ====================

        private void AddPrompt_Click(object sender, RoutedEventArgs e)
        {
            var prompt = new PromptForm
            {
                Identifier = $"custom-{Guid.NewGuid().ToString("N")[..8]}",
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
            MarkDirty();

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

            Notify("已添加提示词", "新条目的位置写在 prompt_order 末尾。", NotifySeverity.Informational);
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

            var dialog = new ContentDialog
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
            MarkDirty();

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

            Notify("已删除提示词", $"{prompt.Name} 已从预设中移除。", NotifySeverity.Success);
        }

        private void RefreshPresetPage()
        {
            if (PagerHost is null)
            {
                return;
            }

            int pageCount = Math.Max(1, (Prompts.Count + PresetPageSize - 1) / PresetPageSize);
            _presetPage = Math.Clamp(_presetPage, 0, pageCount - 1);

            VisiblePrompts.Clear();
            foreach (PromptForm prompt in Prompts.Skip(_presetPage * PresetPageSize).Take(PresetPageSize))
            {
                VisiblePrompts.Add(prompt);
            }

            BuildPager(PagerHost, pageCount, _presetPage, page =>
            {
                _presetPage = page;
                RefreshPresetPage();
            });

            PresetFootText.Text = pageCount > 1
                ? $"第 {_presetPage + 1} / {pageCount} 页 · 本页 {VisiblePrompts.Count} 条 · 共 {Prompts.Count} 条"
                : $"共 {Prompts.Count} 条提示词";
            PromptCountChip.Text = $"{Prompts.Count} 条";
        }

        /// <summary>世界书条目的分页。</summary>
        private void RefreshEntryPage()
        {
            if (EntryPagerHost is null)
            {
                return;
            }

            int pageCount = Math.Max(1, (WorldBook.Entries.Count + EntryPageSize - 1) / EntryPageSize);
            _entryPage = Math.Clamp(_entryPage, 0, pageCount - 1);

            VisibleEntries.Clear();
            foreach (EntryForm entry in WorldBook.Entries.Skip(_entryPage * EntryPageSize).Take(EntryPageSize))
            {
                VisibleEntries.Add(entry);
            }

            BuildPager(EntryPagerHost, pageCount, _entryPage, page =>
            {
                _entryPage = page;
                RefreshEntryPage();
            });

            EntryPageText.Text = pageCount > 1
                ? $"第 {_entryPage + 1} / {pageCount} 页 · 本页 {VisibleEntries.Count} 条 · 共 {WorldBook.Entries.Count} 条"
                : $"共 {WorldBook.Entries.Count} 个条目";
        }

        /// <summary>
        /// 页码条。同一个实现给预设和世界书条目用。
        ///
        /// 以前是「每页一个按钮」：171 条的预设会有 29 个按钮，光是把它们建出来就要 380 ms。
        /// 现在只在页数少的时候铺按钮，页数一多就收成一个「x / y」的按钮，点开才是页面列表
        /// （MenuFlyout 的项在展开时才创建）。
        /// </summary>
        private void BuildPager(StackPanel host, int pageCount, int current, Action<int> go)
        {
            host.Children.Clear();
            if (pageCount <= 1)
            {
                return;
            }

            host.Children.Add(MakePagerIcon("\uE76B", current > 0, () => go(current - 1)));

            if (pageCount <= 6)
            {
                for (int page = 0; page < pageCount; page++)
                {
                    int target = page;
                    host.Children.Add(MakePagerNumber(page + 1, page == current, () => go(target)));
                }
            }
            else
            {
                host.Children.Add(MakePagerSummary(pageCount, current, go));
            }

            host.Children.Add(MakePagerIcon("\uE76C", current < pageCount - 1, () => go(current + 1)));
        }

        /// <summary>页数多时的折叠页码：点开才生成页面列表。</summary>
        private Button MakePagerSummary(int pageCount, int current, Action<int> go)
        {
            var button = new Button
            {
                Height = 26,
                Padding = new Thickness(8, 0, 8, 0),
                Content = new TextBlock
                {
                    Text = $"{current + 1} / {pageCount}",
                    FontSize = 12,
                },
            };

            if (Application.Current.Resources.TryGetValue("SubtleIconButtonStyle", out object style)
                && style is Style subtle)
            {
                button.Style = subtle;
            }

            var flyout = new MenuFlyout();
            flyout.Opening += (_, _) =>
            {
                flyout.Items.Clear();

                for (int page = 0; page < pageCount; page++)
                {
                    int target = page;
                    var item = new MenuFlyoutItem { Text = $"第 {page + 1} 页" };
                    item.Click += (_, _) => go(target);
                    flyout.Items.Add(item);
                }
            };

            button.Flyout = flyout;
            return button;
        }

        private Button MakePagerIcon(string glyph, bool enabled, Action click)
        {
            var button = new Button
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
            var button = new Button
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
            EmbeddedSubText.Text = EmbeddedWorld.Name.Length > 0
                ? $"{EmbeddedWorld.Name} · {embeddedEnabled} 个启用"
                : "未绑定";

            EntryCountChip.Text = $"{WorldBook.Entries.Count} 个";
            EntryEnabledChip.Text = $"{WorldBook.Entries.Count(entry => entry.Enabled)} 个启用";
            PromptCountChip.Text = $"{Prompts.Count} 条";
        }

        // ==================== 导航 ====================

        private void Back_Click(object sender, RoutedEventArgs e) => Close();

        private void Done_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>离开前把未落盘的改动补写一次——防抖计时器不会在页面卸载后继续跑。</summary>
        private void Close()
        {
            CommitSave();
            App.MainWindow?.NavigateTo("resources");
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            CommitSave();
        }

        // ==================== 工具 ====================

        /// <summary>页面播报统一走全局消息（窗口右下角），页面里不再挂 InfoBar。</summary>
        private static void Notify(string title, string message, NotifySeverity severity)
            => AppNotify.Post(severity, title, message);

        private static Brush Res(string key, Color fallback)
            => Application.Current.Resources.TryGetValue(key, out object value) && value is Brush brush
                ? brush
                : new SolidColorBrush(fallback);

        // ==================== JSON 读写小工具 ====================

        private static string Text(JsonObject? node, string name)
            => node?[name] is JsonValue value && value.TryGetValue(out string? text) ? text ?? string.Empty : string.Empty;

        private static bool IsTrue(JsonObject node, string name)
            => node[name] is JsonValue value && value.TryGetValue(out bool flag) && flag;

        private static int Int(JsonObject? node, string name, int fallback = 0)
            => node?[name] is JsonValue value && value.TryGetValue(out int number) ? number : fallback;

        private static int Int(string text, int fallback)
            => int.TryParse(text, out int value) ? value : fallback;

        private static void PutText(JsonObject node, string name, string value)
        {
            if (value.Length == 0 && !node.ContainsKey(name))
            {
                return;
            }

            node[name] = value;
        }

        private static JsonArray ToJsonArray(IEnumerable<EditTag> tags)
        {
            var array = new JsonArray();
            foreach (EditTag tag in tags)
            {
                array.Add(tag.Text);
            }

            return array;
        }

        private static int RoleIndex(string role) => role switch
        {
            "user" => 1,
            "assistant" => 2,
            _ => 0,
        };

        private static string RoleName(int index) => index switch
        {
            1 => "user",
            2 => "assistant",
            _ => "system",
        };

        /// <summary>某个条目容器的形状（数组 / 字典）与内容。</summary>
        private List<EntryForm> ReadEntryForms(JsonNode? container, bool characterBook)
        {
            var forms = new List<EntryForm>();

            if (container is JsonArray array)
            {
                _entriesAreArray = true;

                foreach (JsonNode? item in array)
                {
                    if (item is JsonObject entry)
                    {
                        forms.Add(MakeEntryForm(entry, null, characterBook));
                    }
                }
            }
            else if (container is JsonObject map)
            {
                _entriesAreArray = false;

                foreach (KeyValuePair<string, JsonNode?> pair in map)
                {
                    if (pair.Value is JsonObject entry)
                    {
                        forms.Add(MakeEntryForm(entry, pair.Key, characterBook));
                    }
                }
            }

            return forms;
        }

        private static EntryForm MakeEntryForm(JsonObject node, string? key, bool characterBook)
        {
            string comment = Text(node, "comment");
            if (comment.Length == 0)
            {
                comment = Text(node, "name");
            }

            var form = new EntryForm
            {
                Node = node,
                NodeKey = key,
                Comment = comment,
                Content = Text(node, "content"),

                // 世界书文件用 disable（true = 停用），角色卡内嵌条目用 enabled
                Enabled = characterBook ? !node.ContainsKey("enabled") || IsTrue(node, "enabled") : !IsTrue(node, "disable"),
                Position = Math.Clamp(Int(node, "position"), 0, 6),
                Order = (node.ContainsKey("insertion_order") ? Int(node, "insertion_order") : Int(node, "order", 100)).ToString(),
                Probability = Int(node, "probability", 100).ToString(),
                Depth = Int(node, "depth", 4).ToString(),
            };

            string primaryKey = node.ContainsKey("keys") ? "keys" : "key";
            string secondaryKey = node.ContainsKey("secondary_keys") ? "secondary_keys" : "keysecondary";

            foreach (string keyword in Strings(node[primaryKey]))
            {
                form.Keys.Add(new EditTag(keyword, tag => form.RemoveKey(tag, false)));
            }

            foreach (string keyword in Strings(node[secondaryKey]))
            {
                form.SecondaryKeys.Add(new EditTag(keyword, tag => form.RemoveKey(tag, true)));
            }

            return form;
        }

        private static IEnumerable<string> Strings(JsonNode? node)
        {
            if (node is not JsonArray array)
            {
                yield break;
            }

            foreach (JsonNode? item in array)
            {
                if (item is JsonValue value && value.TryGetValue(out string? text) && text is { Length: > 0 })
                {
                    yield return text;
                }
            }
        }

        /// <summary>预设里 char 100000 那一组的 identifier → 启用状态。</summary>
        private static Dictionary<string, bool> ReadPromptOrder(JsonObject root)
        {
            var map = new Dictionary<string, bool>(StringComparer.Ordinal);

            if (root["prompt_order"] is not JsonArray orders)
            {
                return map;
            }

            JsonObject? group = orders.OfType<JsonObject>().FirstOrDefault(item => Int(item, "character_id") == 100000)
                ?? orders.OfType<JsonObject>().FirstOrDefault();

            if (group?["order"] is not JsonArray order)
            {
                return map;
            }

            foreach (JsonObject item in order.OfType<JsonObject>())
            {
                string identifier = Text(item, "identifier");
                if (identifier.Length > 0)
                {
                    map[identifier] = IsTrue(item, "enabled");
                }
            }

            return map;
        }

        /// <summary>读一本世界书文件，转成条目表单（Node 为空，写进角色卡时是全新副本）。</summary>
        private static (string Name, List<EntryForm> Entries) ReadWorldFile(string path)
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
            {
                throw new InvalidDataException("世界书不是 JSON 对象");
            }

            JsonNode? container = root["entries"] ?? root;
            var forms = new List<EntryForm>();

            foreach (JsonObject entry in EnumerateEntries(container))
            {
                string comment = Text(entry, "comment");
                if (comment.Length == 0)
                {
                    comment = Text(entry, "name");
                }

                var form = new EntryForm
                {
                    Comment = comment,
                    Content = Text(entry, "content"),

                    // 世界书用 disable：true 表示停用
                    Enabled = !IsTrue(entry, "disable"),
                    Position = Math.Clamp(Int(entry, "position"), 0, 6),
                    Order = Int(entry, "order", 100).ToString(),
                    Probability = Int(entry, "probability", 100).ToString(),
                    Depth = Int(entry, "depth", 4).ToString(),
                };

                foreach (string keyword in Strings(entry["key"] ?? entry["keys"]))
                {
                    form.Keys.Add(new EditTag(keyword, tag => form.RemoveKey(tag, false)));
                }

                foreach (string keyword in Strings(entry["keysecondary"] ?? entry["secondary_keys"]))
                {
                    form.SecondaryKeys.Add(new EditTag(keyword, tag => form.RemoveKey(tag, true)));
                }

                forms.Add(form);
            }

            return (System.IO.Path.GetFileNameWithoutExtension(path), forms);
        }

        private static IEnumerable<JsonObject> EnumerateEntries(JsonNode? node)
        {
            if (node is JsonArray array)
            {
                foreach (JsonNode? item in array)
                {
                    if (item is JsonObject entry)
                    {
                        yield return entry;
                    }
                }
            }
            else if (node is JsonObject map)
            {
                foreach (KeyValuePair<string, JsonNode?> pair in map)
                {
                    if (pair.Value is JsonObject entry)
                    {
                        yield return entry;
                    }
                }
            }
        }

        // ==================== 跳转入口 ====================

        /// <summary>由资源管理页在跳转前写入：编辑哪一类、哪个文件。打开后不再改变。</summary>
        public static WorkbenchKind PendingKind { get; set; } = WorkbenchKind.Character;

        /// <summary>被打开文件的完整路径。</summary>
        public static string PendingPath { get; set; } = string.Empty;

        /// <summary>文件名（标题副行与日志里用）。</summary>
        public static string PendingFile { get; set; } = string.Empty;
    }
}
