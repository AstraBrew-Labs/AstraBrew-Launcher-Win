using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AstraBrew_Launcher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace AstraBrew_Launcher.Views
{
    /// <summary>资源行上用到的一组色相工具，避免每处重复拆 hex。</summary>
    internal static class ResourcePalette
    {
        /// <summary>按名字取一个稳定的强调色：同一个角色每次进来颜色都一样。</summary>
        private static readonly string[] Accents = { "#2B88D8", "#8E44DC", "#12A5A5", "#C08A00", "#C23934", "#4F6BED" };

        public static Color Parse(string hex)
        {
            string value = hex.TrimStart('#');
            return Color.FromArgb(
                0xFF,
                Convert.ToByte(value.Substring(0, 2), 16),
                Convert.ToByte(value.Substring(2, 2), 16),
                Convert.ToByte(value.Substring(4, 2), 16));
        }

        public static SolidColorBrush Brush(string hex) => new(Parse(hex));

        public static SolidColorBrush Tint(string hex, byte alpha)
        {
            Color color = Parse(hex);
            return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        }

        /// <summary>名字 → 强调色；空名字给第一个。</summary>
        public static string AccentFor(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return Accents[0];
            }

            int hash = 0;
            foreach (char ch in name)
            {
                hash = (hash * 31 + ch) & 0x7FFFFFFF;
            }

            return Accents[hash % Accents.Length];
        }

        /// <summary>人类可读的体积；与列表、详情里的口径保持一致。</summary>
        public static string Size(long bytes) => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.0} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):0.00} GB",
        };

        /// <summary>
        /// 从磁盘上的图片取一张缩略图。按解码宽度缩放，不把 5 MB 的原图整个读进内存——
        /// 列表里同时有二十张卡时，这个差别是「秒开」和「卡三秒」的区别。
        /// </summary>
        public static ImageSource? Thumbnail(string path, int decodeWidth, bool ignoreCache = false)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return null;
                }

                var image = new BitmapImage
                {
                    DecodePixelWidth = decodeWidth,
                    DecodePixelType = DecodePixelType.Logical,
                };

                // 换过卡面后同一路径指向新内容：不忽略缓存的话界面还是旧图
                if (ignoreCache)
                {
                    image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                }

                image.UriSource = new Uri(path);

                return image;
            }
            catch (Exception ex) when (ex is IOException or UriFormatException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 列表行。行只承载展示字段，选中态是唯一可变项——选中时改两个描边层的可见性，
    /// 不重建集合，点击反馈不会被打断。
    /// </summary>
    public sealed class ResourceRow : INotifyPropertyChanged
    {
        private readonly SolidColorBrush _tileBackground;
        private readonly SolidColorBrush _tileForeground;
        private bool _selected;

        public ResourceRow(string accent)
        {
            _tileForeground = ResourcePalette.Brush(accent);
            _tileBackground = ResourcePalette.Tint(accent, 0x24);
            CoverBackground = ResourcePalette.Tint(accent, 0x2E);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>在所属分类数据列表里的下标；对话行指向扁平会话列表。</summary>
        public int Index { get; set; }

        /// <summary>真实文件的完整路径；分组行没有。</summary>
        public string FullPath { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string Subtitle { get; set; } = string.Empty;

        public string Meta { get; set; } = string.Empty;

        public string Glyph { get; set; } = "\uE8F1";

        /// <summary>封面占位里显示的首字（仅角色卡）。</summary>
        public string Monogram { get; set; } = string.Empty;

        /// <summary>角色卡缩略图；取不到就退回首字色块。</summary>
        public ImageSource? CoverSource { get; set; }

        /// <summary>对话列表里的角色分组头，不是可选中的资源。</summary>
        public bool IsGroup { get; set; }

        public bool HasCover { get; set; }

        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value)
                {
                    return;
                }

                _selected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedVisibility)));
            }
        }

        public SolidColorBrush TileBackground => _tileBackground;

        public SolidColorBrush TileForeground => _tileForeground;

        public SolidColorBrush CoverBackground { get; }

        public SolidColorBrush CoverForeground => _tileForeground;

        public Visibility SelectedVisibility => Show(Selected);

        public Visibility CoverVisibility => Show(HasCover);

        public Visibility CoverImageVisibility => Show(CoverSource is not null);

        public Visibility MonogramVisibility => Show(CoverSource is null);

        public Visibility IconVisibility => Show(!HasCover && !IsGroup);

        public Visibility GroupVisibility => Show(IsGroup);

        public Visibility ItemVisibility => Show(!IsGroup);

        private static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>键值信息格里的一对「标签 + 值」。</summary>
    public sealed class InfoCell
    {
        public InfoCell(string label, string value)
        {
            Label = label;
            Value = value;
        }

        public string Label { get; }

        public string Value { get; }
    }

    /// <summary>世界书条目：角色卡内嵌条目与世界书条目共用。</summary>
    public sealed class WorldEntryItem
    {
        public string Title { get; set; } = string.Empty;

        public string Keywords { get; set; } = string.Empty;

        public string Body { get; set; } = string.Empty;

        public bool Enabled { get; set; } = true;

        public Visibility EnabledVisibility => Enabled ? Visibility.Visible : Visibility.Collapsed;

        public Visibility DisabledVisibility => Enabled ? Visibility.Collapsed : Visibility.Visible;

        public static WorldEntryItem From(LibraryEntry entry) => new()
        {
            Title = entry.Title,
            Keywords = entry.Keywords,
            Body = entry.Body,
            Enabled = entry.Enabled,
        };
    }

    /// <summary>预设里的一条提示词。</summary>
    public sealed class PromptItem
    {
        public string Index { get; set; } = "01";

        public string Name { get; set; } = string.Empty;

        /// <summary>system / user / assistant。</summary>
        public string Role { get; set; } = "system";

        public string Position { get; set; } = string.Empty;

        public bool Enabled { get; set; } = true;

        public Visibility EnabledVisibility => Enabled ? Visibility.Visible : Visibility.Collapsed;

        public Visibility DisabledVisibility => Enabled ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>对话预览里的一条消息。</summary>
    public sealed class ChatMessageItem
    {
        public string Speaker { get; set; } = string.Empty;

        /// <summary>显示用的角色名（「用户 / 角色」）。**别拿它做逻辑判断**，它会被本地化。</summary>
        public string Role { get; set; } = L.Get("Res.Value.RoleCharacter");

        /// <summary>这条是不是用户自己说的。竖条、对齐、头像都看它，不看 <see cref="Role"/> 的文字。</summary>
        public bool IsUser { get; set; }

        public string Time { get; set; } = string.Empty;

        public string Body { get; set; } = string.Empty;

        /// <summary>用户消息左侧多一道强调色竖条。</summary>
        public Visibility UserVisibility => IsUser ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>角色卡详情。</summary>
    public sealed class CharacterDetail
    {
        /// <summary>磁盘上的完整路径，删除与打开目录都靠它。</summary>
        public string FullPath { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string FileName { get; set; } = string.Empty;

        public long Bytes { get; set; }

        /// <summary>体积文案由字节数推导，避免两处数字对不上。</summary>
        public string SizeLabel => ResourcePalette.Size(Bytes);

        public string Creator { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public string Spec { get; set; } = "chara_card_v2";

        public string ImageSize { get; set; } = string.Empty;

        /// <summary>内嵌世界书的名字；空表示没有具名世界书，只有条目。</summary>
        public string WorldName { get; set; } = string.Empty;

        /// <summary>内嵌条目数；0 表示没有内嵌世界书。</summary>
        public int EmbeddedCount { get; set; }

        /// <summary>
        /// 「世界书名 · 3 条」这类说明。**每次读都现拼**：
        /// 换语言时页面会重读磁盘，但列表与详情里的这句都从这里取，不会被缓存成旧语言。
        /// </summary>
        public string WorldLabel => EmbeddedCount == 0
            ? L.Get("Res.Value.NoEmbed")
            : WorldName.Length > 0
                ? L.F("Res.Value.EmbeddedNamed", WorldName, EmbeddedCount)
                : L.F("Res.Value.EmbeddedAnon", EmbeddedCount);

        public string Description { get; set; } = string.Empty;

        public string Personality { get; set; } = string.Empty;

        public string Scenario { get; set; } = string.Empty;

        public string FirstMessage { get; set; } = string.Empty;

        public string Monogram { get; set; } = string.Empty;

        public string AccentHex { get; set; } = "#2B88D8";

        public string ModifiedLabel { get; set; } = string.Empty;

        /// <summary>封面：有 png 就显示真图，没有就留首字色块。</summary>
        public ImageSource? CoverSource { get; set; }

        public Visibility CoverImageVisibility => CoverSource is null ? Visibility.Collapsed : Visibility.Visible;

        public Visibility MonogramVisibility => CoverSource is null ? Visibility.Visible : Visibility.Collapsed;

        public List<string> Tags { get; set; } = new();

        public List<WorldEntryItem> Entries { get; set; } = new();

        public SolidColorBrush CoverBackground => ResourcePalette.Tint(AccentHex, 0x2E);

        public SolidColorBrush CoverForeground => ResourcePalette.Brush(AccentHex);

        /// <summary>规格块由重复器排版，宽窄窗只影响列数。</summary>
        public List<InfoCell> InfoCells => new()
        {
            new InfoCell(L.Get("Res.Info.Creator"), string.IsNullOrEmpty(Creator) ? L.Get("Res.Info.Unsigned") : Creator),
            new InfoCell(L.Get("Res.Info.Version"), string.IsNullOrEmpty(Version) ? "—" : Version),
            new InfoCell(L.Get("Res.Info.Spec"), Spec),
            new InfoCell(L.Get("Res.Info.EmbeddedWorld"), WorldLabel),
        };

        public Visibility TagsVisibility => Tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        public Visibility EntriesVisibility => Entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>正文小节为空时显示一句占位，避免整块空白看起来像加载失败。</summary>
        public string DescriptionText => string.IsNullOrWhiteSpace(Description) ? L.Get("Res.Card.NoDescription") : Description;

        public string PersonalityText => string.IsNullOrWhiteSpace(Personality) ? L.Get("Res.Card.NoPersonality") : Personality;

        public string ScenarioText => string.IsNullOrWhiteSpace(Scenario) ? L.Get("Res.Card.NoScenario") : Scenario;

        public string FirstMessageText => string.IsNullOrWhiteSpace(FirstMessage) ? L.Get("Res.Card.NoFirstMessage") : FirstMessage;
    }

    /// <summary>世界书详情。</summary>
    public sealed class WorldBookDetail
    {
        public string FullPath { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string FileName { get; set; } = string.Empty;

        public long Bytes { get; set; }

        public string SizeLabel => ResourcePalette.Size(Bytes);

        public string Author { get; set; } = string.Empty;

        public string TriggerLabel { get; set; } = string.Empty;

        /// <summary>相对数据目录的位置，例如 default-user\worlds。</summary>
        public string Location { get; set; } = string.Empty;

        public string ModifiedLabel { get; set; } = string.Empty;

        public string HintLabel { get; set; } = string.Empty;

        public string EntryCount { get; set; } = "0";

        public string EnabledCount { get; set; } = "0";

        public List<InfoCell> InfoCells => new()
        {
            new InfoCell(L.Get("Res.Info.Folder"), Location),
            new InfoCell(L.Get("Res.Info.Trigger"), TriggerLabel),
            new InfoCell(L.Get("Res.Info.Modified"), ModifiedLabel),
        };

        public List<WorldEntryItem> Entries { get; set; } = new();
    }

    /// <summary>一次对话存档。</summary>
    public sealed class ChatDetail
    {
        public string FullPath { get; set; } = string.Empty;

        /// <summary>所属角色（chats 下的子目录名）。</summary>
        public string Group { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string FileName { get; set; } = string.Empty;

        public long Bytes { get; set; }

        public string SizeLabel => ResourcePalette.Size(Bytes);

        public string HintLabel { get; set; } = string.Empty;

        public string MessageCount { get; set; } = "0";

        public string UserCount { get; set; } = "0";

        public List<ChatMessageItem> Messages { get; set; } = new();
    }

    /// <summary>预设详情。</summary>
    public sealed class PresetDetail
    {
        public string FullPath { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string FileName { get; set; } = string.Empty;

        public long Bytes { get; set; }

        public string SizeLabel => ResourcePalette.Size(Bytes);

        public string PromptCount { get; set; } = "0";

        public string EnabledCount { get; set; } = "0";

        public string Format { get; set; } = L.Get("Res.Value.FormatStandard");

        /// <summary>接口来源，例如 openai / claude / google。</summary>
        public string ApiSource { get; set; } = "—";

        public string Source { get; set; } = string.Empty;

        public string Model { get; set; } = string.Empty;

        public string StreamLabel { get; set; } = L.Get("Res.Value.StreamOn");

        public string ContextLabel { get; set; } = string.Empty;

        public string MaxTokensLabel { get; set; } = string.Empty;

        public string HintLabel { get; set; } = string.Empty;

        /// <summary>「12 条提示词」或「无提示词结构」——TextGen / NovelAI 的预设本来就没有提示词表。</summary>
        public string PromptSummary => Prompts.Count > 0
            ? L.F("Res.Value.PromptsCount", Prompts.Count)
            : L.Get("Res.Value.NoPromptStructure");

        public List<InfoCell> InfoCells => new()
        {
            new InfoCell(L.Get("Res.Info.SourceFolder"), Source),
            new InfoCell(L.Get("Res.Info.Model"), Model),
            new InfoCell(L.Get("Res.Info.Stream"), StreamLabel),
            new InfoCell(L.Get("Res.Info.Context"), ContextLabel),
            new InfoCell(L.Get("Res.Info.MaxTokens"), MaxTokensLabel),
            new InfoCell(L.Get("Res.Info.Size"), SizeLabel),
        };

        public List<PromptItem> Prompts { get; set; } = new();
    }

    /// <summary>
    /// 资源管理：角色卡、世界书、对话记录与预设。
    /// 页面四段——页头（资源总量与导入）、数据目录（这批资源在哪）、分类 Tab、
    /// 主从区（左列表挑，右详情看）。
    ///
    /// 全部内容来自酒馆的数据目录：角色卡读 PNG 里的 chara/ccv3 文本块，世界书读 entries，
    /// 对话逐行读 jsonl，预设读 prompt_order。导入复制真实文件，删除送回收站。
    /// </summary>
    public sealed partial class ResourcesPage : Page
    {
        private enum TabKind
        {
            Characters,
            WorldBooks,
            Chats,
            Presets,
        }

        private readonly List<CharacterDetail> _characters = new();
        private readonly List<WorldBookDetail> _worldBooks = new();
        private readonly List<ChatDetail> _chats = new();
        private readonly List<PresetDetail> _presets = new();

        private TabKind _tab = TabKind.Characters;
        private string _query = string.Empty;
        private bool _ready;
        private bool _busy;
        private bool _hooked;
        private string _userRoot = TavernLibrary.UserDirectory();
        private ResourceRow? _selectedRow;

        private int? _selectedCharacter;
        private int? _selectedWorldBook;
        private int? _selectedChat;
        private int? _selectedPreset;

        public ResourcesPage()
        {
            InitializeComponent();

            // 界面上直接给出展开后的真实路径
            UpdateTargetChrome();
            DataPathText.Text = CurrentDirectory();

            _ready = true;
            TabCharacters.IsChecked = true;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        /// <summary>列表数据源；切分类、搜索、删除后整表重建。</summary>
        public ObservableCollection<ResourceRow> Rows { get; } = new();

        // ==================== 加载 ====================

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_hooked)
            {
                TavernInstances.Changed += OnEnvironmentChanged;
                TavernRuntime.Changed += OnEnvironmentChanged;
                LanguageSwitch.LanguageChanged += OnLanguageChanged;
                _hooked = true;
            }

            UpdateTargetChrome();
            _ = ReloadAsync();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (!_hooked)
            {
                return;
            }

            TavernInstances.Changed -= OnEnvironmentChanged;
            TavernRuntime.Changed -= OnEnvironmentChanged;
            LanguageSwitch.LanguageChanged -= OnLanguageChanged;
            _hooked = false;
        }

        /// <summary>
        /// 换语言：页面上的行与详情都是「读磁盘时拼好的字符串」，
        /// <c>Loc</c> 刷不到，所以整页按当前分类重读一遍（保留选中项）。
        /// </summary>
        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            UpdateTargetChrome();
            _ = ReloadAsync(keepSelection: true);
        }

        /// <summary>换实例、改数据位置、酒馆启停都可能换掉数据目录，整页跟着重读。</summary>
        private void OnEnvironmentChanged(object? sender, EventArgs e)
        {
            UpdateTargetChrome();
            _ = ReloadAsync(keepSelection: true);
        }

        private void UpdateTargetChrome()
        {
            TavernInstance instance = TavernInstances.Current;
            TargetInstanceChipText.Text = $"{instance.KindLabel} · {instance.KernelLabel}";
        }

        /// <summary>把磁盘上的资源读进四张表。解析在后台线程，映射与重建留在 UI 线程。</summary>
        private async Task ReloadAsync(bool keepSelection = false)
        {
            LibrarySnapshot snapshot = await TavernLibrary.LoadAsync();

            _userRoot = snapshot.UserRoot;

            _characters.Clear();
            foreach (CharacterCard card in snapshot.Characters)
            {
                _characters.Add(ToDetail(card));
            }

            _worldBooks.Clear();
            foreach (WorldBook book in snapshot.Worlds)
            {
                _worldBooks.Add(ToDetail(book, _userRoot));
            }

            _chats.Clear();
            foreach (ChatArchive chat in snapshot.Chats)
            {
                _chats.Add(ToDetail(chat));
            }

            _presets.Clear();
            foreach (PresetFile preset in snapshot.Presets)
            {
                _presets.Add(ToDetail(preset));
            }

            if (!keepSelection)
            {
                _selectedCharacter = null;
                _selectedWorldBook = null;
                _selectedChat = null;
                _selectedPreset = null;
            }
            else
            {
                ClampSelection();
            }

            ApplyTabChrome();
            EnsureSelection();
            RefreshRows();
            ShowDetailForSelection();

            if (snapshot.Error is not null)
            {
                Notify(L.Get("Res.Notify.ReadFail"), snapshot.Error, NotifySeverity.Error);
            }
        }

        /// <summary>重读之后列表可能变短，旧下标要收回到合法范围。</summary>
        private void ClampSelection()
        {
            _selectedCharacter = Clamp(_selectedCharacter, _characters.Count);
            _selectedWorldBook = Clamp(_selectedWorldBook, _worldBooks.Count);
            _selectedChat = Clamp(_selectedChat, _chats.Count);
            _selectedPreset = Clamp(_selectedPreset, _presets.Count);

            static int? Clamp(int? value, int count) =>
                count == 0 ? null : Math.Clamp(value ?? 0, 0, count - 1);
        }

        // ==================== 映射 ====================

        private static CharacterDetail ToDetail(CharacterCard card)
        {
            string accent = ResourcePalette.AccentFor(card.Name);

            var detail = new CharacterDetail
            {
                FullPath = card.FullPath,
                Name = card.Name,
                FileName = card.FileName,
                Bytes = card.Bytes,
                Creator = card.Creator,
                Version = card.Version,
                Spec = card.Spec,
                ImageSize = card.PixelWidth > 0 ? $"{card.PixelWidth} × {card.PixelHeight}" : "—",
                WorldName = card.EmbeddedWorldName ?? string.Empty,
                EmbeddedCount = card.EmbeddedEntries.Count,
                Description = card.Description,
                Personality = card.Personality,
                Scenario = card.Scenario,
                FirstMessage = card.FirstMessage,
                Monogram = FirstChar(card.Name),
                AccentHex = accent,
                ModifiedLabel = card.Modified.ToString("yyyy-MM-dd"),
                CoverSource = card.FileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    ? ResourcePalette.Thumbnail(card.FullPath, 264)
                    : null,
                Tags = card.Tags.ToList(),
                Entries = card.EmbeddedEntries.Select(WorldEntryItem.From).ToList(),
            };

            return detail;
        }

        private static WorldBookDetail ToDetail(WorldBook book, string userRoot)
        {
            return new WorldBookDetail
            {
                Location = RelativeFolder(book.FullPath, userRoot),
                FullPath = book.FullPath,
                Name = book.Name,
                FileName = book.FileName,
                Bytes = book.Bytes,
                Author = "—",
                TriggerLabel = L.Get("Res.Value.TriggerKeyword"),
                ModifiedLabel = book.Modified.ToString("yyyy-MM-dd"),
                HintLabel = L.F("Res.Value.EntryHint", book.EntryCount, book.EnabledCount),
                EntryCount = book.EntryCount.ToString(),
                EnabledCount = book.EnabledCount.ToString(),
                Entries = book.Entries.Select(WorldEntryItem.From).ToList(),
            };
        }

        private static ChatDetail ToDetail(ChatArchive chat)
        {
            // 文件名形如「角色名 - 2026-11-03@22-14-03.jsonl」，标题只留前半段
            string stem = System.IO.Path.GetFileNameWithoutExtension(chat.FileName);
            int dash = stem.IndexOf(" - ", StringComparison.Ordinal);
            string name = dash > 0 ? stem[..dash] : stem;

            var messages = chat.Preview.Select(line => new ChatMessageItem
            {
                Speaker = line.IsUser ? L.Get("Res.Value.SpeakerMe") : line.Speaker,
                Role = L.Get(line.IsUser ? "Res.Value.RoleUser" : "Res.Value.RoleCharacter"),
                IsUser = line.IsUser,
                Time = line.Time,
                Body = line.Body,
            }).ToList();

            return new ChatDetail
            {
                FullPath = chat.FullPath,
                Group = chat.Group,
                Name = name,
                FileName = chat.FileName,
                Bytes = chat.Bytes,
                HintLabel = messages.Count > 0 ? L.F("Res.Value.Recent", messages.Count) : L.Get("Res.Value.EmptyArchive"),
                MessageCount = chat.MessageCount.ToString(),
                UserCount = chat.UserCount.ToString(),
                Messages = messages,
            };
        }

        private static PresetDetail ToDetail(PresetFile preset)
        {
            var prompts = preset.Prompts
                .Select((prompt, index) => new PromptItem
                {
                    Index = (index + 1).ToString("00"),
                    Name = prompt.Name,
                    Role = prompt.Role,
                    Position = prompt.Position,
                    Enabled = prompt.Enabled,
                })
                .ToList();

            return new PresetDetail
            {
                FullPath = preset.FullPath,
                Name = preset.Name,
                FileName = preset.FileName,
                Bytes = preset.Bytes,
                PromptCount = preset.Prompts.Count.ToString(),
                EnabledCount = preset.EnabledCount.ToString(),
                Format = preset.Format,
                ApiSource = preset.Api,
                Source = preset.Folder,
                Model = preset.Model,
                StreamLabel = L.Get(preset.Stream ? "Res.Value.StreamOn" : "Res.Value.StreamOff"),
                ContextLabel = preset.MaxContext > 0 ? $"{preset.MaxContext:N0} token" : "—",
                MaxTokensLabel = preset.MaxTokens > 0 ? $"{preset.MaxTokens:N0} token" : "—",
                HintLabel = preset.Prompts.Count > 0
                    ? L.F("Res.Value.PresetHint", preset.Prompts.Count, preset.EnabledCount)
                    : L.Get("Res.Value.OnlyParams"),
                Prompts = prompts,
            };
        }

        /// <summary>相对数据目录的位置，用于「所在目录」这一格。</summary>
        private static string RelativeFolder(string fullPath, string userRoot)
        {
            try
            {
                string? folder = System.IO.Path.GetDirectoryName(fullPath);
                return folder is null ? "—" : System.IO.Path.GetRelativePath(userRoot, folder);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                return "—";
            }
        }

        private static string FirstChar(string name) => string.IsNullOrEmpty(name) ? "?" : name[..1];

        // ==================== 分类切换 ====================

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (!_ready || sender is not RadioButton { Tag: string tag })
            {
                return;
            }

            _tab = tag switch
            {
                "characters" => TabKind.Characters,
                "worldbooks" => TabKind.WorldBooks,
                "chats" => TabKind.Chats,
                _ => TabKind.Presets,
            };

            _query = string.Empty;
            SearchBox.Text = string.Empty;

            // 进分类时默认落在第一项上，右侧不留一整块空白。
            EnsureSelection();

            ApplyTabChrome();
            RefreshRows();
            ShowDetailForSelection();
        }

        /// <summary>当前分类还没选中任何东西时，落到第一项。</summary>
        private void EnsureSelection()
        {
            switch (_tab)
            {
                case TabKind.Characters when _selectedCharacter is null && _characters.Count > 0:
                    _selectedCharacter = 0;
                    break;
                case TabKind.WorldBooks when _selectedWorldBook is null && _worldBooks.Count > 0:
                    _selectedWorldBook = 0;
                    break;
                case TabKind.Chats when _selectedChat is null && _chats.Count > 0:
                    _selectedChat = 0;
                    break;
                case TabKind.Presets when _selectedPreset is null && _presets.Count > 0:
                    _selectedPreset = 0;
                    break;
            }
        }

        /// <summary>把与分类相关的固定文案、图标与强调色一次性换掉。</summary>
        private void ApplyTabChrome()
        {
            string accent = AccentOf(_tab);
            string glyph = GlyphOf(_tab);
            Color color = ResourcePalette.Parse(accent);

            ListIconTile.Background = ResourcePalette.Tint(accent, 0x1F);
            ListIconGlyph.Glyph = glyph;
            ListIconGlyph.Foreground = new SolidColorBrush(color);

            ListTitleText.Text = TitleOf(_tab);
            TabFolderChip.Text = _tab == TabKind.Presets
                ? L.F("Res.Dir.Presets.Folders", TavernLibrary.PresetDirectories().Count)
                : FolderOf(_tab);
            DataPathText.Text = CurrentDirectory();
            ListFootText.Text = FootOf(_tab);

            ImportButton.Visibility = _tab == TabKind.Chats ? Visibility.Collapsed : Visibility.Visible;
            ImportButtonText.Text = L.Get(_tab switch
            {
                TabKind.Characters => "Res.Action.ImportCharacters",
                TabKind.WorldBooks => "Res.Action.ImportWorldBooks",
                _ => "Res.Action.ImportPresets",
            });

            SearchBox.PlaceholderText = L.Get(_tab switch
            {
                TabKind.Characters => "Res.Search.Characters",
                TabKind.WorldBooks => "Res.Search.WorldBooks",
                TabKind.Chats => "Res.Search.Chats",
                _ => "Res.Search.Presets",
            });

            bool empty = CountOf(_tab) == 0;
            ListEmptyTitleText.Text = empty
                ? L.F("Res.Empty.Title.None", TitleOf(_tab))
                : L.Get("Res.Empty.Title.Search");
            ListEmptyHintText.Text = empty
                ? L.Get(_tab switch
                {
                    TabKind.Characters => "Res.Empty.Hint.Characters",
                    TabKind.WorldBooks => "Res.Empty.Hint.WorldBooks",
                    TabKind.Chats => "Res.Empty.Hint.Chats",
                    _ => "Res.Empty.Hint.Presets",
                })
                : L.Get("Res.Empty.Search");

            DetailEmptyGlyph.Glyph = glyph;
            DetailEmptyTitleText.Text = L.Get("Res.Detail.Empty.Title");
            DetailEmptyHintText.Text = L.Get(_tab switch
            {
                TabKind.Characters => "Res.Detail.EmptyHint.Characters",
                TabKind.WorldBooks => "Res.Detail.EmptyHint.WorldBooks",
                TabKind.Chats => "Res.Detail.EmptyHint.Chats",
                _ => "Res.Detail.EmptyHint.Presets",
            });
        }

        // ==================== 列表 ====================

        private void RefreshRows()
        {
            Rows.Clear();
            _selectedRow = null;

            switch (_tab)
            {
                case TabKind.Characters:
                    BuildCharacterRows();
                    break;
                case TabKind.WorldBooks:
                    BuildWorldBookRows();
                    break;
                case TabKind.Chats:
                    BuildChatRows();
                    break;
                default:
                    BuildPresetRows();
                    break;
            }

            bool empty = Rows.Count == 0;
            ResourceList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            ListEmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

            // 行是重建的，选中态跟着新对象走；同步一次引用，下一次点击才取消得掉旧行的高亮。
            _selectedRow = Rows.FirstOrDefault(row => row.Selected);

            UpdateSummary();
        }

        private void BuildCharacterRows()
        {
            for (int i = 0; i < _characters.Count; i++)
            {
                CharacterDetail item = _characters[i];
                if (!Matches(item.Name, item.Creator, string.Join(' ', item.Tags), item.FileName))
                {
                    continue;
                }

                Rows.Add(new ResourceRow(item.AccentHex)
                {
                    Index = i,
                    FullPath = item.FullPath,
                    Title = item.Name,
                    Subtitle = string.IsNullOrEmpty(item.Creator) ? L.Get("Res.Info.Unsigned") : item.Creator,
                    Meta = $"{item.SizeLabel} · {item.ImageSize} · {item.WorldLabel}",
                    Monogram = item.Monogram,
                    CoverSource = ResourcePalette.Thumbnail(item.FullPath, 96),
                    HasCover = true,
                    Selected = _selectedCharacter == i,
                });
            }
        }

        private void BuildWorldBookRows()
        {
            for (int i = 0; i < _worldBooks.Count; i++)
            {
                WorldBookDetail item = _worldBooks[i];
                if (!Matches(item.Name, item.FileName))
                {
                    continue;
                }

                Rows.Add(new ResourceRow("#8E44DC")
                {
                    Index = i,
                    FullPath = item.FullPath,
                    Title = item.Name,
                    Subtitle = item.FileName,
                    Meta = L.F("Res.Row.EntryMeta", item.EntryCount, item.SizeLabel, item.ModifiedLabel),
                    Glyph = "\uE8F1",
                    Selected = _selectedWorldBook == i,
                });
            }
        }

        private void BuildChatRows()
        {
            string? lastGroup = null;

            for (int i = 0; i < _chats.Count; i++)
            {
                ChatDetail item = _chats[i];
                if (!Matches(item.Group, item.Name, item.FileName))
                {
                    continue;
                }

                if (item.Group != lastGroup)
                {
                    lastGroup = item.Group;
                    int groupCount = _chats.Count(chat => chat.Group == item.Group);
                    Rows.Add(new ResourceRow("#12A5A5")
                    {
                        IsGroup = true,
                        Title = item.Group,
                        Meta = L.F("Res.Row.Sessions", groupCount),
                    });
                }

                Rows.Add(new ResourceRow("#12A5A5")
                {
                    Index = i,
                    FullPath = item.FullPath,
                    Title = item.Name,
                    Subtitle = item.FileName,
                    Meta = L.F("Res.Row.ChatMeta", item.MessageCount, item.SizeLabel),
                    Glyph = "\uE8BD",
                    Selected = _selectedChat == i,
                });
            }
        }

        private void BuildPresetRows()
        {
            string? lastFolder = null;

            for (int i = 0; i < _presets.Count; i++)
            {
                PresetDetail item = _presets[i];
                if (!Matches(item.Name, item.Model, item.FileName, item.Source))
                {
                    continue;
                }

                // 四家接口的预设混在一张表里，按来源目录分组，否则「模型」「上下文」这些字段
                // 在不同来源下含义都不一样，看着像数据串了
                if (item.Source != lastFolder)
                {
                    lastFolder = item.Source;
                    int groupCount = _presets.Count(preset => preset.Source == item.Source);
                    Rows.Add(new ResourceRow("#C08A00")
                    {
                        IsGroup = true,
                        Title = item.Source,
                        Meta = L.F("Res.Row.Presets", groupCount),
                    });
                }

                Rows.Add(new ResourceRow("#C08A00")
                {
                    Index = i,
                    FullPath = item.FullPath,
                    Title = item.Name,
                    Subtitle = item.Model,
                    Meta = $"{item.PromptSummary} · {item.Format} · {item.SizeLabel}",
                    Glyph = "\uE9E9",
                    Selected = _selectedPreset == i,
                });
            }
        }

        private bool Matches(params string[] fields)
        {
            if (string.IsNullOrWhiteSpace(_query))
            {
                return true;
            }

            return fields.Any(field => !string.IsNullOrEmpty(field)
                && field.Contains(_query, StringComparison.OrdinalIgnoreCase));
        }

        private void UpdateSummary()
        {
            int total = _characters.Count + _worldBooks.Count + _chats.Count + _presets.Count;
            HeaderSummaryText.Text = L.F("Res.Header.Summary", total);

            CountCharacters.Text = _characters.Count.ToString();
            CountWorldBooks.Text = _worldBooks.Count.ToString();
            CountChats.Text = _chats.Count.ToString();
            CountPresets.Text = _presets.Count.ToString();

            ListSummaryText.Text = _tab switch
            {
                TabKind.Characters => L.F("Res.Summary.Characters", _characters.Count, SizeOf(_characters.Sum(item => item.Bytes))),
                TabKind.WorldBooks => L.F("Res.Summary.WorldBooks", _worldBooks.Count, SizeOf(_worldBooks.Sum(item => item.Bytes))),
                TabKind.Chats => _chats.Count == 0
                    ? L.Get("Res.Summary.ChatsEmpty")
                    : L.F(
                        "Res.Summary.Chats",
                        _chats.Count,
                        _chats.Select(item => item.Group).Distinct().Count()),
                _ => L.F("Res.Summary.Presets", _presets.Count, SizeOf(_presets.Sum(item => item.Bytes))),
            };
        }

        private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            {
                return;
            }

            _query = sender.Text.Trim();
            RefreshRows();
        }

        // ==================== 选中与详情 ====================

        private void Row_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: ResourceRow row } || row.IsGroup)
            {
                return;
            }

            if (_selectedRow is not null)
            {
                _selectedRow.Selected = false;
            }

            row.Selected = true;
            _selectedRow = row;

            switch (_tab)
            {
                case TabKind.Characters:
                    _selectedCharacter = row.Index;
                    break;
                case TabKind.WorldBooks:
                    _selectedWorldBook = row.Index;
                    break;
                case TabKind.Chats:
                    _selectedChat = row.Index;
                    break;
                default:
                    _selectedPreset = row.Index;
                    break;
            }

            ShowDetailForSelection();
        }

        private void ShowDetailForSelection()
        {
            string? title = null;
            string? subtitle = null;
            string glyph = GlyphOf(_tab);
            string accent = AccentOf(_tab);
            DataTemplate? template = null;
            object? model = null;
            bool canEdit = true;

            switch (_tab)
            {
                case TabKind.Characters:
                    if (Pick(_characters, _selectedCharacter) is { } character)
                    {
                        model = character;
                        template = DetailTemplate("CharacterDetailTemplate");
                        title = character.Name;
                        subtitle = $"{character.FileName} · {character.SizeLabel} · {L.F("Res.Detail.Modified", character.ModifiedLabel)}";
                    }

                    break;

                case TabKind.WorldBooks:
                    if (Pick(_worldBooks, _selectedWorldBook) is { } worldBook)
                    {
                        model = worldBook;
                        template = DetailTemplate("WorldBookDetailTemplate");
                        title = worldBook.Name;
                        subtitle = $"{worldBook.FileName} · {worldBook.SizeLabel} · {L.F("Res.Detail.Modified", worldBook.ModifiedLabel)}";
                    }

                    break;

                case TabKind.Chats:
                    if (Pick(_chats, _selectedChat) is { } chat)
                    {
                        model = chat;
                        template = DetailTemplate("ChatDetailTemplate");
                        title = chat.Name;
                        subtitle = $"{chat.FileName} · {chat.SizeLabel}";
                        canEdit = false;
                    }

                    break;

                default:
                    if (Pick(_presets, _selectedPreset) is { } preset)
                    {
                        model = preset;
                        template = DetailTemplate("PresetDetailTemplate");
                        title = preset.Name;
                        subtitle = $"{preset.FileName} · {preset.SizeLabel}";
                    }

                    break;
            }

            if (model is null || template is null)
            {
                ShowEmptyDetail();
                return;
            }

            Color color = ResourcePalette.Parse(accent);
            DetailIconTile.Background = ResourcePalette.Tint(accent, 0x1F);
            DetailIconGlyph.Glyph = glyph;
            DetailIconGlyph.Foreground = new SolidColorBrush(color);
            DetailTitleText.Text = title;
            DetailSubText.Text = subtitle;

            DetailBody.ContentTemplate = template;
            DetailBody.Content = model;

            DetailActions.Visibility = Visibility.Visible;
            WorkbenchButton.Visibility = canEdit ? Visibility.Visible : Visibility.Collapsed;
            DetailScroll.Visibility = Visibility.Visible;
            DetailScroll.ChangeView(null, 0, null, true);
            DetailEmptyPanel.Visibility = Visibility.Collapsed;
        }

        private void ShowEmptyDetail()
        {
            DetailIconTile.Background = new SolidColorBrush(Color.FromArgb(0x12, 0x80, 0x80, 0x80));
            DetailIconGlyph.Glyph = GlyphOf(_tab);
            DetailIconGlyph.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A));
            DetailTitleText.Text = L.Get("Res.Detail.Placeholder.Title");
            DetailSubText.Text = L.Get("Res.Detail.Placeholder.Sub");

            DetailBody.Content = null;
            DetailActions.Visibility = Visibility.Collapsed;
            DetailScroll.Visibility = Visibility.Collapsed;
            DetailEmptyPanel.Visibility = Visibility.Visible;
        }

        private DataTemplate? DetailTemplate(string key) => Resources.ContainsKey(key) ? Resources[key] as DataTemplate : null;

        private static T? Pick<T>(List<T> list, int? index)
            where T : class
            => index is int value && value >= 0 && value < list.Count ? list[value] : null;

        // ==================== 页级动作 ====================

        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                return;
            }

            string folder = ImportDirectory();

            if (!Directory.Exists(folder))
            {
                // 目录可能还没被酒馆创建（全新安装、还没聊过天）：先建出来，别让用户自己去找
                try
                {
                    Directory.CreateDirectory(folder);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Notify(L.Get("Res.Notify.DirNotWritable"), $"{folder}\n{ex.Message}", NotifySeverity.Error);
                    return;
                }
            }

            var picker = new FileOpenPicker();
            picker.SuggestedStartLocation = PickerLocationId.Downloads;

            switch (_tab)
            {
                case TabKind.Characters:
                    picker.FileTypeFilter.Add(".png");
                    picker.FileTypeFilter.Add(".json");
                    break;
                default:
                    picker.FileTypeFilter.Add(".json");
                    break;
            }

            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));

            IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
            if (files is null || files.Count == 0)
            {
                return;
            }

            await RunImportAsync(files.Select(file => file.Path).ToList());
        }

        /// <summary>选完文件之后的实际导入：结构检查 → 复制 → 报告。</summary>
        private async Task RunImportAsync(IReadOnlyList<string> paths)
        {
            string folder = ImportDirectory();

            _busy = true;
            ImportButton.IsEnabled = false;

            // 先按分类做结构检查再复制：一张普通 PNG 混进 characters 目录，
            // 列表里就多一条打不开的空卡，用户还得回头找是哪个文件
            ResourceKind kind = _tab switch
            {
                TabKind.WorldBooks => ResourceKind.WorldBook,
                TabKind.Presets => ResourceKind.Preset,
                _ => ResourceKind.Character,
            };

            ImportReport report = await TavernLibrary.ImportAsync(kind, folder, paths);

            _busy = false;
            ImportButton.IsEnabled = true;

            if (report.Imported > 0)
            {
                await ReloadAsync(keepSelection: true);
            }

            if (report.Error is { Length: > 0 } error)
            {
                Notify(L.Get("Res.Notify.ImportFail"), error, NotifySeverity.Error);
                return;
            }

            if (report.Failed.Count == 0)
            {
                Notify(
                    L.Get("Res.Notify.ImportOk"),
                    L.F("Res.Notify.ImportOkBody", report.Imported, FolderOf(_tab)),
                    NotifySeverity.Success);
                return;
            }

            await ShowImportReportAsync(report);
        }

        /// <summary>
        /// 有文件被跳过时列出来：只说「跳过了 2 个」等于让用户自己去猜哪里不对。
        /// 全部成功走全局消息，不弹窗打扰。
        /// </summary>
        private async Task ShowImportReportAsync(ImportReport report)
        {
            var body = new StackPanel { Spacing = 10 };

            body.Children.Add(new TextBlock
            {
                Text = report.Imported > 0
                    ? L.F("Res.Import.Partial", report.Imported, report.Failed.Count)
                    : L.F("Res.Import.None", report.Failed.Count, FolderOf(_tab)),
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
            });

            var list = new StackPanel { Spacing = 8 };
            foreach (ImportFailure failure in report.Failed)
            {
                var row = new StackPanel { Spacing = 1 };
                row.Children.Add(new TextBlock
                {
                    Text = failure.FileName,
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                row.Children.Add(new TextBlock
                {
                    Text = failure.Reason,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                });
                list.Children.Add(row);
            }

            body.Children.Add(new ScrollViewer
            {
                MaxHeight = 220,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = list,
            });

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = L.Get("Res.Import.Dialog.Title"),
                Content = body,
                CloseButtonText = L.Get("Res.Import.Dialog.Close"),
                DefaultButton = ContentDialogButton.Close,
            };

            await dialog.ShowAsync();
        }

        private async void Rescan_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                return;
            }

            _busy = true;
            if (sender is Button button)
            {
                button.IsEnabled = false;
            }

            await ReloadAsync(keepSelection: true);

            _busy = false;
            if (sender is Button done)
            {
                done.IsEnabled = true;
            }

            Notify(
                L.Get("Res.Notify.RescanOk"),
                L.F("Res.Notify.RescanBody", FolderOf(_tab), CountOf(_tab)),
                NotifySeverity.Informational);
        }

        private void CopyPath_Click(object sender, RoutedEventArgs e)
        {
            string path = SelectedFullPath ?? CurrentDirectory();
            var package = new DataPackage();
            package.SetText(path);
            Clipboard.SetContent(package);
            Notify(L.Get("Res.Notify.CopyOk"), path, NotifySeverity.Success);
        }

        private void OpenDirectory_Click(object sender, RoutedEventArgs e)
        {
            string folder = CurrentDirectory();

            if (!Directory.Exists(folder))
            {
                Notify(L.Get("Res.Notify.DirAbsent"), folder, NotifySeverity.Warning);
                return;
            }

            ShellFile.Reveal(folder);
        }

        private void OpenWorkbench_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedFullPath is not { Length: > 0 } path)
            {
                return;
            }

            // 打开哪一类由这里的分类决定；工作台里没有切换入口，进去就是这一类
            WorkbenchPage.PendingKind = _tab switch
            {
                TabKind.WorldBooks => WorkbenchKind.WorldBook,
                TabKind.Presets => WorkbenchKind.Preset,
                _ => WorkbenchKind.Character,
            };
            WorkbenchPage.PendingPath = path;
            WorkbenchPage.PendingFile = SelectedFileName ?? string.Empty;

            App.MainWindow?.NavigateTo("workbench");
        }

        /// <summary>当前选中资源的文件名，跳转工作台时带过去。</summary>
        private string? SelectedFileName => _tab switch
        {
            TabKind.Characters => Pick(_characters, _selectedCharacter)?.FileName,
            TabKind.WorldBooks => Pick(_worldBooks, _selectedWorldBook)?.FileName,
            TabKind.Presets => Pick(_presets, _selectedPreset)?.FileName,
            _ => null,
        };

        /// <summary>当前选中资源的完整路径；没选中时为空。</summary>
        private string? SelectedFullPath => _tab switch
        {
            TabKind.Characters => Pick(_characters, _selectedCharacter)?.FullPath,
            TabKind.WorldBooks => Pick(_worldBooks, _selectedWorldBook)?.FullPath,
            TabKind.Chats => Pick(_chats, _selectedChat)?.FullPath,
            TabKind.Presets => Pick(_presets, _selectedPreset)?.FullPath,
            _ => null,
        };

        private async void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || SelectedFullPath is not { Length: > 0 } path)
            {
                return;
            }

            string name = DetailTitleText.Text;

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = L.Get("Res.Delete.Dialog.Title"),
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = L.F("Res.Delete.Dialog.Body", path),
                },
                PrimaryButtonText = L.Get("Res.Delete.Confirm"),
                CloseButtonText = L.Get("Common.Action.Cancel.Content"),
                DefaultButton = ContentDialogButton.Close,
            };

            if (Application.Current.Resources.TryGetValue("DangerDialogButtonStyle", out object dangerStyle)
                && dangerStyle is Style style)
            {
                dialog.PrimaryButtonStyle = style;
            }

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            _busy = true;
            (bool ok, string? error) = await ShellFile.MoveToRecycleBinAsync(path);
            _busy = false;

            if (!ok)
            {
                Notify(
                    L.Get("Res.Delete.Fail.Title"),
                    error ?? L.Get("Res.Delete.Fail.Body"),
                    NotifySeverity.Error);
                return;
            }

            await ReloadAsync();

            Notify(
                L.Get("Res.Delete.Ok.Title"),
                L.F("Res.Delete.Ok.Body", name),
                NotifySeverity.Success);
        }

        // ==================== 文案与工具 ====================

        /// <summary>当前分类对应的真实目录。</summary>
        private string CurrentDirectory() => _tab switch
        {
            TabKind.Characters => TavernLibrary.CharactersDirectory(),
            TabKind.WorldBooks => TavernLibrary.WorldsDirectory(),
            TabKind.Chats => TavernLibrary.ChatsDirectory(),

            // 预设散在好几家 Settings 目录里，界面上给它们的共同父目录
            _ => TavernLibrary.UserDirectory(),
        };

        /// <summary>导入往哪个目录写：预设统一进 OpenAI Settings，别让用户猜该放哪一家。</summary>
        private string ImportDirectory() => _tab == TabKind.Presets
            ? TavernLibrary.PresetDirectories().FirstOrDefault()
                ?? System.IO.Path.Combine(TavernLibrary.UserDirectory(), "OpenAI Settings")
            : CurrentDirectory();

        private static string TitleOf(TabKind tab) => L.Get(tab switch
        {
            TabKind.Characters => "Res.Tab.Characters",
            TabKind.WorldBooks => "Res.Tab.WorldBooks",
            TabKind.Chats => "Res.Tab.Chats",
            _ => "Res.Tab.Presets",
        });

        private static string FolderOf(TabKind tab) => tab switch
        {
            TabKind.Characters => "characters",
            TabKind.WorldBooks => "worlds",
            TabKind.Chats => "chats",
            _ => "OpenAI Settings",
        };

        private static string GlyphOf(TabKind tab) => tab switch
        {
            TabKind.Characters => "\uE77B",
            TabKind.WorldBooks => "\uE8F1",
            TabKind.Chats => "\uE8BD",
            _ => "\uE9E9",
        };

        private static string AccentOf(TabKind tab) => tab switch
        {
            TabKind.Characters => "#2B88D8",
            TabKind.WorldBooks => "#8E44DC",
            TabKind.Chats => "#12A5A5",
            _ => "#C08A00",
        };

        private static string FootOf(TabKind tab) => L.Get(tab switch
        {
            TabKind.Characters => "Res.Foot.Characters",
            TabKind.WorldBooks => "Res.Foot.WorldBooks",
            TabKind.Chats => "Res.Foot.Chats",
            _ => "Res.Foot.Presets",
        });

        private int CountOf(TabKind tab) => tab switch
        {
            TabKind.Characters => _characters.Count,
            TabKind.WorldBooks => _worldBooks.Count,
            TabKind.Chats => _chats.Count,
            _ => _presets.Count,
        };

        private static string SizeOf(long bytes) => bytes == 0 ? "—" : ResourcePalette.Size(bytes);

        /// <summary>页面播报统一走全局消息（窗口右下角），页面里不再挂 InfoBar。</summary>
        private static void Notify(string title, string message, NotifySeverity severity)
            => AppNotify.Post(severity, title, message);
    }
}
