using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace AstraBrew_Launcher.Views
{
    /// <summary>资源行上用到的一组色相工具，避免每处重复拆 hex。</summary>
    internal static class ResourcePalette
    {
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

        /// <summary>人类可读的体积；与列表、详情里的口径保持一致。</summary>
        public static string Size(long bytes) => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.0} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):0.00} GB",
        };
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

        public string Title { get; set; } = string.Empty;

        public string Subtitle { get; set; } = string.Empty;

        public string Meta { get; set; } = string.Empty;

        public string Glyph { get; set; } = "\uE8F1";

        /// <summary>封面占位里显示的首字（仅角色卡）。</summary>
        public string Monogram { get; set; } = string.Empty;

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

        public string Role { get; set; } = "角色";

        public string Time { get; set; } = string.Empty;

        public string Body { get; set; } = string.Empty;

        /// <summary>用户消息左侧多一道强调色竖条；角色文案本身就是「用户」。</summary>
        public Visibility UserVisibility => Role == "用户" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>角色卡详情。</summary>
    public sealed class CharacterDetail
    {
        public string Name { get; set; } = string.Empty;

        public string FileName { get; set; } = string.Empty;

        public long Bytes { get; set; }

        /// <summary>体积文案由字节数推导，避免种子数据里两处数字对不上。</summary>
        public string SizeLabel => ResourcePalette.Size(Bytes);

        public string Creator { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public string Spec { get; set; } = "chara_card_v2";

        public string ImageSize { get; set; } = string.Empty;

        public string WorldLabel { get; set; } = "无内嵌";

        public string Description { get; set; } = string.Empty;

        public string Personality { get; set; } = string.Empty;

        public string Scenario { get; set; } = string.Empty;

        public string FirstMessage { get; set; } = string.Empty;

        public string Monogram { get; set; } = string.Empty;

        public string AccentHex { get; set; } = "#2B88D8";

        public string ModifiedLabel { get; set; } = string.Empty;

        public List<string> Tags { get; set; } = new();

        public List<WorldEntryItem> Entries { get; set; } = new();

        public SolidColorBrush CoverBackground => ResourcePalette.Tint(AccentHex, 0x2E);

        public SolidColorBrush CoverForeground => ResourcePalette.Brush(AccentHex);

        /// <summary>规格块由重复器排版，宽窄窗只影响列数。</summary>
        public List<InfoCell> InfoCells => new()
        {
            new InfoCell("作者", Creator),
            new InfoCell("版本", Version),
            new InfoCell("规格", Spec),
            new InfoCell("内嵌世界书", WorldLabel),
        };

        public Visibility TagsVisibility => Tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        public Visibility EntriesVisibility => Entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>世界书详情。</summary>
    public sealed class WorldBookDetail
    {
        public string Name { get; set; } = string.Empty;

        public string FileName { get; set; } = string.Empty;

        public long Bytes { get; set; }

        /// <summary>体积文案由字节数推导，避免种子数据里两处数字对不上。</summary>
        public string SizeLabel => ResourcePalette.Size(Bytes);

        public string Author { get; set; } = string.Empty;

        public string TriggerLabel { get; set; } = string.Empty;

        public string ModifiedLabel { get; set; } = string.Empty;

        public string HintLabel { get; set; } = string.Empty;

        public string EntryCount { get; set; } = "0";

        public string EnabledCount { get; set; } = "0";

        public List<InfoCell> InfoCells => new()
        {
            new InfoCell("作者", Author),
            new InfoCell("关键词触发", TriggerLabel),
            new InfoCell("最后修改", ModifiedLabel),
        };

        public List<WorldEntryItem> Entries { get; set; } = new();
    }

    /// <summary>一次对话存档。</summary>
    public sealed class ChatDetail
    {
        /// <summary>所属角色（chats 下的子目录名）。</summary>
        public string Group { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string FileName { get; set; } = string.Empty;

        public long Bytes { get; set; }

        /// <summary>体积文案由字节数推导，避免种子数据里两处数字对不上。</summary>
        public string SizeLabel => ResourcePalette.Size(Bytes);

        public string HintLabel { get; set; } = string.Empty;

        public string MessageCount { get; set; } = "0";

        public string UserCount { get; set; } = "0";

        public List<ChatMessageItem> Messages { get; set; } = new();
    }

    /// <summary>预设详情。</summary>
    public sealed class PresetDetail
    {
        public string Name { get; set; } = string.Empty;

        public string FileName { get; set; } = string.Empty;

        public long Bytes { get; set; }

        /// <summary>体积文案由字节数推导，避免种子数据里两处数字对不上。</summary>
        public string SizeLabel => ResourcePalette.Size(Bytes);

        public string PromptCount { get; set; } = "0";

        public string EnabledCount { get; set; } = "0";

        public string Format { get; set; } = "标准";

        public string HelperLabel { get; set; } = "否";

        public string Source { get; set; } = string.Empty;

        public string Model { get; set; } = string.Empty;

        public string StreamLabel { get; set; } = "开";

        public string ContextLabel { get; set; } = string.Empty;

        public string MaxTokensLabel { get; set; } = string.Empty;

        public string HintLabel { get; set; } = string.Empty;

        public List<InfoCell> InfoCells => new()
        {
            new InfoCell("来源", Source),
            new InfoCell("模型", Model),
            new InfoCell("流式输出", StreamLabel),
            new InfoCell("上下文上限", ContextLabel),
            new InfoCell("单次回复上限", MaxTokensLabel),
            new InfoCell("文件体积", SizeLabel),
        };

        public List<PromptItem> Prompts { get; set; } = new();
    }

    /// <summary>
    /// 资源管理：角色卡、世界书、对话记录与预设。
    /// 页面四段——页头（资源总量与导入）、数据目录（这批资源在哪）、分类 Tab、
    /// 主从区（左列表挑，右详情看）。
    /// </summary>
    public sealed partial class ResourcesPage : Page
    {
        private const string DataRoot =
            @"C:\Users\al01\AppData\Roaming\AstraBrew Launcher\sillytavern\data\default-user";

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
        private ResourceRow? _selectedRow;

        private int? _selectedCharacter;
        private int? _selectedWorldBook;
        private int? _selectedChat;
        private int? _selectedPreset;

        /// <summary>目录里待导入的角色卡，演示导入动作用；导入后清空。</summary>
        private CharacterDetail? _pendingImport;

        public ResourcesPage()
        {
            InitializeComponent();
            Seed();

            _ready = true;
            TabCharacters.IsChecked = true;
        }

        /// <summary>列表数据源；切分类、搜索、删除后整表重建。</summary>
        public ObservableCollection<ResourceRow> Rows { get; } = new();

        // ==================== 种子数据 ====================

        private void Seed()
        {
            _characters.AddRange(new[]
            {
                new CharacterDetail
                {
                    Name = "星野澪",
                    FileName = "星野澪.png",
                    Bytes = 2_202_009,
                    Creator = "星野",
                    Version = "v1.4",
                    ImageSize = "864 × 1216",
                    WorldLabel = "星见町 · 2 条",
                    AccentHex = "#2B88D8",
                    Monogram = "澪",
                    ModifiedLabel = "2026-10-26",
                    Tags = new List<string> { "治愈", "日常", "温柔", "长篇" },
                    Description = "星见町街角旧书店的店主，二十三岁。习惯把书页折出小角当书签，也习惯替人记住他们自己忘掉的事。",
                    Personality = "说话慢，情绪淡，先听对方讲完再开口。对谁都不设防，但也不轻易把心里的事倒出来；被认真道谢时会手足无措地转去整理书脊。",
                    Scenario = "深秋的傍晚，书店门口的风铃响了一下。你为了避雨推门进来，她正把一摞旧书从纸箱里往外搬，抬头看了你一眼。",
                    FirstMessage = "「雨挺大的吧。先坐，我把这几本放上去就给你倒水。」",
                    Entries = new List<WorldEntryItem>
                    {
                        new()
                        {
                            Title = "星见町",
                            Keywords = "关键词：星见町、商店街、坡道",
                            Body = "沿海的小镇，主街是一条缓坡，坡顶有座废弃的观测台。镇上的人习惯在傍晚把店门开到很晚，说是「留着灯，晚归的人好认路」。",
                        },
                        new()
                        {
                            Title = "旧书店「灯下」",
                            Keywords = "关键词：旧书店、灯下、店规",
                            Body = "店规只有两条：书可以随便翻，但翻完要放回原位；下雨天进店的人，热水免费。二楼靠窗的位置常年空着，据说是留给某个再没回来的人。",
                            Enabled = false,
                        },
                    },
                },
                new CharacterDetail
                {
                    Name = "白鹭千夏",
                    FileName = "白鹭千夏.png",
                    Bytes = 1_677_722,
                    Creator = "月见",
                    Version = "v0.9",
                    ImageSize = "768 × 1152",
                    AccentHex = "#12A5A5",
                    Monogram = "夏",
                    ModifiedLabel = "2026-10-22",
                    Tags = new List<string> { "校园", "青春" },
                    Description = "转学第三个星期的二年级生，坐在靠窗倒数第二排。成绩中游，社团是濒临解散的文学部。",
                    Personality = "看似自来熟，其实每句话都在观察对方的反应。兴致上来会把话题拐到很远的地方，被拆穿就笑着装傻。",
                    Scenario = "放学后的文学部活动室，只有她一个人趴在桌上改社刊的排版，窗外是田径部的口号声。",
                    FirstMessage = "「你来得正好——帮我看看这段是不是太啰嗦了？我自己读了三遍已经读不出味道了。」",
                },
                new CharacterDetail
                {
                    Name = "银月骑士",
                    FileName = "银月骑士.png",
                    Bytes = 3_565_158,
                    Creator = "无衣",
                    Version = "v2.0",
                    ImageSize = "1024 × 1536",
                    WorldLabel = "银月大陆 · 3 条",
                    AccentHex = "#8E44DC",
                    Monogram = "银",
                    ModifiedLabel = "2026-10-19",
                    Tags = new List<string> { "奇幻", "冒险", "战斗" },
                    Description = "银月骑士团最后一任团长，在王国陷落那夜失去了名字。盔甲上的纹章被磨平，只剩一道月牙形的划痕。",
                    Personality = "寡言，礼节刻在骨子里。对敌人不留情，对平民会单膝跪下说话。提到「誓约」二字时会沉默很久。",
                    Scenario = "边境的雪夜，你在一座半塌的哨塔里生火，门外传来马蹄踏雪的声音，随后是三下叩门——不快不慢，极有分寸。",
                    FirstMessage = "「借个火。天亮前我就走。」",
                    Entries = new List<WorldEntryItem>
                    {
                        new()
                        {
                            Title = "银月骑士团",
                            Keywords = "关键词：骑士团、誓约、月牙纹章",
                            Body = "建国时的十二骑士之一创立，全盛期三百人。入团誓约只有一句：护住身后的人，不问姓名。",
                        },
                        new()
                        {
                            Title = "王国陷落之夜",
                            Keywords = "关键词：陷落、王都、灰烬",
                            Body = "王都陷落于霜月十七，火从神殿烧到城门。骑士团奉命护送平民出城，无人回头。",
                        },
                        new()
                        {
                            Title = "无名之誓",
                            Keywords = "关键词：无名、盔甲、划痕",
                            Body = "放弃姓氏的骑士会把纹章磨平，只留一道划痕。教会的说法是「让神认不出你，你才好替别人挡灾」。",
                        },
                    },
                },
                new CharacterDetail
                {
                    Name = "雨宫铃",
                    FileName = "雨宫铃.png",
                    Bytes = 1_258_291,
                    Creator = "空青",
                    Version = "v1.1",
                    ImageSize = "640 × 960",
                    AccentHex = "#C08A00",
                    Monogram = "铃",
                    ModifiedLabel = "2026-10-14",
                    Tags = new List<string> { "悬疑", "都市" },
                    Description = "在一栋老公寓里做夜间广播的独居女性。节目只有半小时，听众据说不超过十个人。",
                    Personality = "警惕，语速快，习惯用玩笑挡问题。真正紧张时会不自觉地数东西——台阶、窗格、呼吸。",
                    Scenario = "凌晨一点二十，你在调频里听到一个不该存在的频道。对面的人在念你今天的行程，一字不差。",
                    FirstMessage = "「……喂？这个频段不该有人。」",
                },
                new CharacterDetail
                {
                    Name = "洛水",
                    FileName = "洛水.png",
                    Bytes = 2_936_013,
                    Creator = "拾光",
                    Version = "v1.0",
                    ImageSize = "896 × 1280",
                    WorldLabel = "长乐宫 · 2 条",
                    AccentHex = "#8E44DC",
                    Monogram = "洛",
                    ModifiedLabel = "2026-10-08",
                    Tags = new List<string> { "古风", "宫廷" },
                    Description = "长乐宫掌乐的女官，十七岁入宫，如今第十一年。能听出任何一把琴的弦是哪年换的。",
                    Personality = "温和但不软弱，说话留三分余地。最擅长用一支曲子把话题带过去，让人忘了原本要问什么。",
                    Scenario = "宫宴散后，你被留在偏殿。她把琴横在膝上，指尖压着弦不让它响，等一个解释。",
                    FirstMessage = "「殿下今日在席上，听了三遍《长乐》，一次也没抬头。」",
                    Entries = new List<WorldEntryItem>
                    {
                        new()
                        {
                            Title = "长乐宫",
                            Keywords = "关键词：长乐宫、女官、乐班",
                            Body = "掌宫廷礼乐，编制四十八人。女官不入品，但宫宴座次排在九嫔之后——这是先帝留下的旧例。",
                        },
                        new()
                        {
                            Title = "《长乐》",
                            Keywords = "关键词：长乐、曲子、禁曲",
                            Body = "开国时的宴乐曲，共七段。第七段自第三代起禁奏，乐谱仍存于长乐宫，无人敢翻。",
                            Enabled = false,
                        },
                    },
                },
                new CharacterDetail
                {
                    Name = "维拉",
                    FileName = "维拉.png",
                    Bytes = 2_516_582,
                    Creator = "星酿工坊",
                    Version = "v1.2",
                    ImageSize = "832 × 1216",
                    AccentHex = "#2B88D8",
                    Monogram = "维",
                    ModifiedLabel = "2026-09-30",
                    Tags = new List<string> { "科幻", "机甲", "冷静" },
                    Description = "轨道工厂的机甲整备师，左臂是工作事故后换的义肢。说话像在读检查单，一个多余的字都没有。",
                    Personality = "理性到近乎冷淡，但会把你的机体检修记录记得比你自己还清楚。不擅长接受道谢，通常回一句「这是流程」。",
                    Scenario = "出勤前四十分钟，机库只开了应急灯。她咬着螺丝刀，半个身子探进你机甲的胸腔，听见脚步声也没回头。",
                    FirstMessage = "「站那儿别动。第三根液压管漏了，我不想再修一次你的腿。」",
                },
            });

            _worldBooks.AddRange(new[]
            {
                new WorldBookDetail
                {
                    Name = "赛博都市",
                    FileName = "赛博都市.json",
                    Bytes = 700_416,
                    Author = "白露",
                    TriggerLabel = "关键词 312 个",
                    ModifiedLabel = "2026-10-24",
                    HintLabel = "按关键词触发",
                    EntryCount = "128",
                    EnabledCount = "121",
                    Entries = new List<WorldEntryItem>
                    {
                        new()
                        {
                            Title = "新九龙 · 城市概览",
                            Keywords = "关键词：新九龙、上城、下城、霓虹",
                            Body = "垂直分层的巨型都市：上城是玻璃与阳光，下城常年只有霓虹照明。两城之间靠三百部公共升降机连接，票价抵下城人半日工资。",
                        },
                        new()
                        {
                            Title = "义体黑市",
                            Keywords = "关键词：义体、黑市、改装、医生",
                            Body = "藏在旧地铁七号线的岔道里，只做熟客。价格是明面诊所的三成，风险是术后没人告诉你排异期有多长。",
                        },
                        new()
                        {
                            Title = "数据游侠",
                            Keywords = "关键词：游侠、潜入、神经接驳",
                            Body = "拿钱进别人脑机网络的自由职业者。行规三条：不碰记忆区、不接同行单、留一条退路给自己。",
                            Enabled = false,
                        },
                    },
                },
                new WorldBookDetail
                {
                    Name = "星海纪元",
                    FileName = "星海纪元.json",
                    Bytes = 524_288,
                    Author = "无衣",
                    TriggerLabel = "关键词 208 个",
                    ModifiedLabel = "2026-10-18",
                    HintLabel = "按关键词触发",
                    EntryCount = "86",
                    EnabledCount = "86",
                    Entries = new List<WorldEntryItem>
                    {
                        new()
                        {
                            Title = "跃迁航道",
                            Keywords = "关键词：跃迁、航道、星门",
                            Body = "旧帝国的航道网仍在使用，但星门一半失修。走未登记的暗航道能省三十天，代价是没人知道你会不会到。",
                        },
                        new()
                        {
                            Title = "拾荒船队",
                            Keywords = "关键词：拾荒、船队、残骸带",
                            Body = "在战场残骸里拆零件为生的家族船队，用三代人拼出一艘能跃迁的船。他们信奉一句话：战场上没有垃圾。",
                        },
                        new()
                        {
                            Title = "静默区",
                            Keywords = "关键词：静默区、失联、坐标",
                            Body = "所有通讯都会在这里消失的星域，官方标注为「设备故障高发区」。进去的船，十艘里回来七艘，回来的都记不清里面发生了什么。",
                        },
                    },
                },
                new WorldBookDetail
                {
                    Name = "雾隐村",
                    FileName = "雾隐村.json",
                    Bytes = 212_992,
                    Author = "月见",
                    TriggerLabel = "关键词 96 个",
                    ModifiedLabel = "2026-10-11",
                    HintLabel = "按关键词触发",
                    EntryCount = "42",
                    EnabledCount = "38",
                    Entries = new List<WorldEntryItem>
                    {
                        new()
                        {
                            Title = "雾的规律",
                            Keywords = "关键词：雾、清晨、界线",
                            Body = "村子每天清晨起雾，正午散尽。雾从不越过溪上的木桥——老人说那是「界线」，年轻人当迷信。",
                        },
                        new()
                        {
                            Title = "守雾人",
                            Keywords = "关键词：守雾人、祠堂、铃",
                            Body = "每代一人，住在祠堂后面。职责只有一条：雾起时敲三下铃。上一任守雾人在雾里失踪，铃留在桥头。",
                        },
                    },
                },
                new WorldBookDetail
                {
                    Name = "长乐宫",
                    FileName = "长乐宫.json",
                    Bytes = 344_064,
                    Author = "拾光",
                    TriggerLabel = "关键词 174 个",
                    ModifiedLabel = "2026-10-05",
                    HintLabel = "按关键词触发",
                    EntryCount = "67",
                    EnabledCount = "60",
                    Entries = new List<WorldEntryItem>
                    {
                        new()
                        {
                            Title = "宫规 · 乐班",
                            Keywords = "关键词：乐班、宫规、禁奏",
                            Body = "乐班不得私奏、不得改谱、不得在宫宴之外奏《长乐》。违者削籍出宫，永不叙用。",
                        },
                        new()
                        {
                            Title = "偏殿",
                            Keywords = "关键词：偏殿、旧琴、窗",
                            Body = "长乐宫西侧的偏殿，摆着一张无人认领的旧琴。琴身有三道裂纹，音却比新琴准——据说是先帝亲手调过的那一张。",
                        },
                        new()
                        {
                            Title = "第七段",
                            Keywords = "关键词：第七段、禁曲、乐谱",
                            Body = "《长乐》第七段的乐谱锁在长乐宫的樟木柜里，钥匙由掌乐女官保管。没有人奏过，也没有人敢烧。",
                            Enabled = false,
                        },
                    },
                },
                new WorldBookDetail
                {
                    Name = "银月大陆",
                    FileName = "银月大陆.json",
                    Bytes = 126_976,
                    Author = "无衣",
                    TriggerLabel = "关键词 58 个",
                    ModifiedLabel = "2026-09-27",
                    HintLabel = "按关键词触发",
                    EntryCount = "24",
                    EnabledCount = "24",
                    Entries = new List<WorldEntryItem>
                    {
                        new()
                        {
                            Title = "霜月历",
                            Keywords = "关键词：霜月、历法、冬季",
                            Body = "一年十个月，以霜月为岁首。霜月十七是王国陷落的日子，此后每到此日，全境禁乐。",
                        },
                        new()
                        {
                            Title = "边境哨塔",
                            Keywords = "关键词：边境、哨塔、雪",
                            Body = "沿北境每隔三十里一座，常年只有两人驻守。塔内有一本交接册，第一页写着：取暖不锁门。",
                        },
                    },
                },
            });

            _chats.AddRange(new[]
            {
                new ChatDetail
                {
                    Group = "星野澪",
                    Name = "雨夜的书店",
                    FileName = "星野澪 - 2026-10-26@22-14-03.jsonl",
                    Bytes = 96_256,
                    HintLabel = "最近 4 条",
                    MessageCount = "46",
                    UserCount = "21",
                    Messages = new List<ChatMessageItem>
                    {
                        new() { Speaker = "星野澪", Role = "角色", Time = "22:14", Body = "「雨挺大的吧。先坐，我把这几本放上去就给你倒水。」" },
                        new() { Speaker = "我", Role = "用户", Time = "22:14", Body = "谢谢。我看门口灯还亮着，就进来了。" },
                        new() { Speaker = "星野澪", Role = "角色", Time = "22:15", Body = "「灯一直亮着。」她把纸箱推到墙边，「反正也不会有人来收电费。」\n「热水在里屋，你自己拿杯子——第二个柜子，别拿最上面那个。」" },
                        new() { Speaker = "我", Role = "用户", Time = "22:16", Body = "最上面那个有什么讲究吗？" },
                    },
                },
                new ChatDetail
                {
                    Group = "星野澪",
                    Name = "关于那本没还的书",
                    FileName = "星野澪 - 2026-10-24@19-02-41.jsonl",
                    Bytes = 72_704,
                    HintLabel = "更早的存档",
                    MessageCount = "32",
                    UserCount = "15",
                    Messages = new List<ChatMessageItem>
                    {
                        new() { Speaker = "星野澪", Role = "角色", Time = "19:02", Body = "「你上次借的那本，还没还。」" },
                        new() { Speaker = "我", Role = "用户", Time = "19:03", Body = "我记得还了啊，就放在柜台上了。" },
                        new() { Speaker = "星野澪", Role = "角色", Time = "19:03", Body = "「放在柜台上的是另一本。」她把书推过来，「这本你翻到第 47 页就停了，书签还夹在那儿。」" },
                    },
                },
                new ChatDetail
                {
                    Group = "白鹭千夏",
                    Name = "社刊排版夜",
                    FileName = "白鹭千夏 - 2026-10-22@18-40-12.jsonl",
                    Bytes = 41_984,
                    HintLabel = "更早的存档",
                    MessageCount = "18",
                    UserCount = "9",
                    Messages = new List<ChatMessageItem>
                    {
                        new() { Speaker = "白鹭千夏", Role = "角色", Time = "18:40", Body = "「你来得正好——帮我看看这段是不是太啰嗦了？」" },
                        new() { Speaker = "我", Role = "用户", Time = "18:41", Body = "第三行开始就可以删了。" },
                        new() { Speaker = "白鹭千夏", Role = "角色", Time = "18:41", Body = "「……你说得对，但我不想承认。」她把稿子翻过去，「那结尾怎么办，光留着不解释吗？」" },
                    },
                },
                new ChatDetail
                {
                    Group = "银月骑士",
                    Name = "哨塔的雪夜",
                    FileName = "银月骑士 - 2026-10-19@23-58-30.jsonl",
                    Bytes = 158_720,
                    HintLabel = "最近 3 条",
                    MessageCount = "74",
                    UserCount = "36",
                    Messages = new List<ChatMessageItem>
                    {
                        new() { Speaker = "银月骑士", Role = "角色", Time = "23:58", Body = "「借个火。天亮前我就走。」" },
                        new() { Speaker = "我", Role = "用户", Time = "23:59", Body = "外面雪这么大，你确定？" },
                        new() { Speaker = "银月骑士", Role = "角色", Time = "23:59", Body = "「……确定。」他把剑靠在门边，坐下时盔甲发出一声很轻的响，「但我可以等到雪小一点。」" },
                    },
                },
                new ChatDetail
                {
                    Group = "银月骑士",
                    Name = "边境的第三个夜晚",
                    FileName = "银月骑士 - 2026-10-16@21-12-05.jsonl",
                    Bytes = 121_344,
                    HintLabel = "更早的存档",
                    MessageCount = "58",
                    UserCount = "29",
                    Messages = new List<ChatMessageItem>
                    {
                        new() { Speaker = "银月骑士", Role = "角色", Time = "21:12", Body = "「你的伤还没好，别碰剑。」" },
                        new() { Speaker = "我", Role = "用户", Time = "21:13", Body = "那你替我拿着。" },
                        new() { Speaker = "银月骑士", Role = "角色", Time = "21:13", Body = "「……好。」他接过剑，动作比平时慢了半拍，「这是我今年接过最重的一把。」" },
                    },
                },
                new ChatDetail
                {
                    Group = "维拉",
                    Name = "出勤前的四十分钟",
                    FileName = "维拉 - 2026-09-30@06-05-22.jsonl",
                    Bytes = 34_816,
                    HintLabel = "更早的存档",
                    MessageCount = "12",
                    UserCount = "6",
                    Messages = new List<ChatMessageItem>
                    {
                        new() { Speaker = "维拉", Role = "角色", Time = "06:05", Body = "「站那儿别动。第三根液压管漏了，我不想再修一次你的腿。」" },
                        new() { Speaker = "我", Role = "用户", Time = "06:06", Body = "昨天刚换的新的。" },
                        new() { Speaker = "维拉", Role = "角色", Time = "06:06", Body = "「新的也会漏。」她把螺丝刀从嘴里拿下来，「记录上写着，你上周超载了两分四十秒。」" },
                    },
                },
            });

            _presets.AddRange(new[]
            {
                new PresetDetail
                {
                    Name = "长上下文预设",
                    FileName = "长上下文预设.json",
                    Bytes = 47_104,
                    Format = "标准",
                    HelperLabel = "否",
                    Source = "本地整理",
                    Model = "claude-sonnet-4.5",
                    StreamLabel = "开",
                    ContextLabel = "200,000 token",
                    MaxTokensLabel = "8,192 token",
                    HintLabel = "12 条 · 8 条启用",
                    PromptCount = "12",
                    EnabledCount = "8",
                    Prompts = new List<PromptItem>
                    {
                        new() { Index = "01", Name = "主提示词", Role = "system", Position = "注入位置 0 · 相对深度 4" },
                        new() { Index = "02", Name = "角色扮演守则", Role = "system", Position = "注入位置 0 · 相对深度 3" },
                        new() { Index = "03", Name = "世界观摘要", Role = "system", Position = "注入位置 1 · 绝对深度 12", Enabled = false },
                        new() { Index = "04", Name = "长程记忆提示", Role = "system", Position = "注入位置 1 · 绝对深度 8" },
                    },
                },
                new PresetDetail
                {
                    Name = "角色扮演增强",
                    FileName = "角色扮演增强.json",
                    Bytes = 79_872,
                    Format = "SPreset",
                    HelperLabel = "是",
                    Source = "社区搬运",
                    Model = "gpt-4.1",
                    StreamLabel = "开",
                    ContextLabel = "128,000 token",
                    MaxTokensLabel = "4,096 token",
                    HintLabel = "18 条 · 16 条启用",
                    PromptCount = "18",
                    EnabledCount = "16",
                    Prompts = new List<PromptItem>
                    {
                        new() { Index = "01", Name = "导演指令", Role = "system", Position = "注入位置 0 · 相对深度 2" },
                        new() { Index = "02", Name = "输出格式约束", Role = "system", Position = "注入位置 0 · 相对深度 1" },
                        new() { Index = "03", Name = "变量守则", Role = "system", Position = "注入位置 1 · 绝对深度 10" },
                        new() { Index = "04", Name = "情绪提示", Role = "system", Position = "注入位置 2 · 按楼层", Enabled = false },
                    },
                },
                new PresetDetail
                {
                    Name = "简洁对话",
                    FileName = "简洁对话.json",
                    Bytes = 12_288,
                    Format = "标准",
                    HelperLabel = "否",
                    Source = "本地整理",
                    Model = "deepseek-v4",
                    StreamLabel = "开",
                    ContextLabel = "64,000 token",
                    MaxTokensLabel = "2,048 token",
                    HintLabel = "6 条 · 全部启用",
                    PromptCount = "6",
                    EnabledCount = "6",
                    Prompts = new List<PromptItem>
                    {
                        new() { Index = "01", Name = "简短回答", Role = "system", Position = "注入位置 0 · 相对深度 2" },
                        new() { Index = "02", Name = "禁用列表式输出", Role = "system", Position = "注入位置 0 · 相对深度 1" },
                        new() { Index = "03", Name = "角色口吻", Role = "system", Position = "注入位置 1 · 绝对深度 4" },
                    },
                },
                new PresetDetail
                {
                    Name = "创意写作",
                    FileName = "创意写作.json",
                    Bytes = 94_208,
                    Format = "标准",
                    HelperLabel = "否",
                    Source = "自建",
                    Model = "claude-opus-4.1",
                    StreamLabel = "开",
                    ContextLabel = "200,000 token",
                    MaxTokensLabel = "16,384 token",
                    HintLabel = "21 条 · 14 条启用",
                    PromptCount = "21",
                    EnabledCount = "14",
                    Prompts = new List<PromptItem>
                    {
                        new() { Index = "01", Name = "文风锚点", Role = "system", Position = "注入位置 0 · 相对深度 4" },
                        new() { Index = "02", Name = "禁用陈词滥调", Role = "system", Position = "注入位置 0 · 相对深度 3" },
                        new() { Index = "03", Name = "场景三段式", Role = "system", Position = "注入位置 1 · 绝对深度 16", Enabled = false },
                        new() { Index = "04", Name = "对白节奏", Role = "system", Position = "注入位置 1 · 绝对深度 12" },
                    },
                },
            });

            _pendingImport = new CharacterDetail
            {
                Name = "月见雫",
                FileName = "月见雫.png",
                Bytes = 1_887_437,
                Creator = "白露",
                Version = "v0.8",
                ImageSize = "800 × 1200",
                AccentHex = "#12A5A5",
                Monogram = "雫",
                ModifiedLabel = "2026-10-28",
                Tags = new List<string> { "治愈", "海边" },
                Description = "海边小镇气象站的观测员，负责记录每一场台风的路径。写作用的笔名只有三个人知道。",
                Personality = "话不多，但一旦聊起云就会停不下来。喜欢把「大概」「可能」挂在嘴边，唯独报天气时从不用这些词。",
                Scenario = "台风过境前的傍晚，观测站的窗户被风吹得嗡嗡响。她把记录本压在水杯下面，抬头看你。",
                FirstMessage = "「明天别出门。……我是说，风速七级，出门不太划算。」",
            };
        }

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
            TabFolderChip.Text = FolderOf(_tab);
            DataPathText.Text = DataRoot + @"\" + FolderOf(_tab);
            ListFootText.Text = FootOf(_tab);

            ImportButton.Visibility = _tab == TabKind.Chats ? Visibility.Collapsed : Visibility.Visible;
            ImportButtonText.Text = _tab switch
            {
                TabKind.Characters => "导入角色卡",
                TabKind.WorldBooks => "导入世界书",
                _ => "导入预设",
            };

            SearchBox.PlaceholderText = _tab switch
            {
                TabKind.Characters => "搜索角色名、作者或标签…",
                TabKind.WorldBooks => "搜索世界书名称或作者…",
                TabKind.Chats => "搜索角色名或存档文件名…",
                _ => "搜索预设名称或模型…",
            };

            ListEmptyTitleText.Text = "没有匹配的资源";
            ListEmptyHintText.Text = "换个关键词，或清空搜索框后重试。";

            DetailEmptyGlyph.Glyph = glyph;
            DetailEmptyTitleText.Text = "还没有选中资源";
            DetailEmptyHintText.Text = _tab switch
            {
                TabKind.Characters => "从左侧挑一张角色卡，这里会展开简介、开场白与内嵌世界书。",
                TabKind.WorldBooks => "从左侧挑一本世界书，这里会列出它的全部条目。",
                TabKind.Chats => "从左侧挑一份对话存档，这里会还原最近的对话内容。",
                _ => "从左侧挑一个预设，这里会展开生成参数与提示词结构。",
            };
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
                if (!Matches(item.Name, item.Creator, string.Join(' ', item.Tags)))
                {
                    continue;
                }

                Rows.Add(new ResourceRow(item.AccentHex)
                {
                    Index = i,
                    Title = item.Name,
                    Subtitle = string.IsNullOrEmpty(item.Creator) ? "未署名" : item.Creator,
                    Meta = $"{item.Version} · {item.SizeLabel} · {item.ImageSize} · {item.WorldLabel}",
                    Monogram = item.Monogram,
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
                if (!Matches(item.Name, item.Author, item.FileName))
                {
                    continue;
                }

                Rows.Add(new ResourceRow("#8E44DC")
                {
                    Index = i,
                    Title = item.Name,
                    Subtitle = string.IsNullOrEmpty(item.Author) ? "未署名" : item.Author,
                    Meta = $"{item.EntryCount} 条条目 · {item.SizeLabel} · {item.ModifiedLabel}",
                    Glyph = "\uE8F1",
                    Selected = _selectedWorldBook == i,
                });
            }
        }

        private void BuildChatRows()
        {
            string? lastGroup = null;
            int groupCount = 0;

            for (int i = 0; i < _chats.Count; i++)
            {
                ChatDetail item = _chats[i];
                bool groupMatches = Matches(item.Group, item.Name, item.FileName);
                if (!groupMatches)
                {
                    continue;
                }

                if (item.Group != lastGroup)
                {
                    lastGroup = item.Group;
                    groupCount = _chats.Count(chat => chat.Group == item.Group);
                    Rows.Add(new ResourceRow("#12A5A5")
                    {
                        IsGroup = true,
                        Title = item.Group,
                        Meta = $"{groupCount} 个会话",
                    });
                }

                Rows.Add(new ResourceRow("#12A5A5")
                {
                    Index = i,
                    Title = item.Name,
                    Subtitle = item.FileName,
                    Meta = $"{item.MessageCount} 条消息 · {item.SizeLabel}",
                    Glyph = "\uE8BD",
                    Selected = _selectedChat == i,
                });
            }
        }

        private void BuildPresetRows()
        {
            for (int i = 0; i < _presets.Count; i++)
            {
                PresetDetail item = _presets[i];
                if (!Matches(item.Name, item.Model, item.FileName))
                {
                    continue;
                }

                Rows.Add(new ResourceRow("#C08A00")
                {
                    Index = i,
                    Title = item.Name,
                    Subtitle = item.Model,
                    Meta = $"{item.PromptCount} 条提示词 · {item.Format} · {item.SizeLabel}",
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
            HeaderSummaryText.Text = $"共 {total} 个资源";

            CountCharacters.Text = _characters.Count.ToString();
            CountWorldBooks.Text = _worldBooks.Count.ToString();
            CountChats.Text = _chats.Count.ToString();
            CountPresets.Text = _presets.Count.ToString();

            ListSummaryText.Text = _tab switch
            {
                TabKind.Characters => $"共 {_characters.Count} 个 · {SizeOf(_characters.Sum(item => item.Bytes))}",
                TabKind.WorldBooks => $"共 {_worldBooks.Count} 本 · {SizeOf(_worldBooks.Sum(item => item.Bytes))}",
                TabKind.Chats => $"共 {_chats.Count} 个会话 · {_chats.Select(item => item.Group).Distinct().Count()} 个角色",
                _ => $"共 {_presets.Count} 个 · {SizeOf(_presets.Sum(item => item.Bytes))}",
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
                        subtitle = $"{character.FileName} · {character.SizeLabel} · 修改于 {character.ModifiedLabel}";
                    }

                    break;

                case TabKind.WorldBooks:
                    if (Pick(_worldBooks, _selectedWorldBook) is { } worldBook)
                    {
                        model = worldBook;
                        template = DetailTemplate("WorldBookDetailTemplate");
                        title = worldBook.Name;
                        subtitle = $"{worldBook.FileName} · {worldBook.SizeLabel} · 修改于 {worldBook.ModifiedLabel}";
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
            DetailTitleText.Text = "选择左侧的资源";
            DetailSubText.Text = "文件名、体积与修改时间会显示在这里";

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

            if (_pendingImport is null || _tab != TabKind.Characters)
            {
                ShowBar("没有可导入的文件", $"{FolderOf(_tab)} 目录里没有新的文件，把资源放进去后再试。", InfoBarSeverity.Warning);
                return;
            }

            _busy = true;
            ImportButton.IsEnabled = false;
            ShowBar("正在校验", $"读取 {_pendingImport.FileName} 的角色卡字段…", InfoBarSeverity.Informational);

            await Task.Delay(900);

            CharacterDetail imported = _pendingImport;
            _pendingImport = null;
            _characters.Insert(0, imported);

            _busy = false;
            ImportButton.IsEnabled = true;
            _query = string.Empty;
            SearchBox.Text = string.Empty;

            // 进分类时默认落在第一项上，右侧不留一整块空白。
            EnsureSelection();

            ApplyTabChrome();
            RefreshRows();
            SelectNewest(0);
            ShowBar("导入完成", $"{imported.Name}（{imported.Version}）已写入 characters 目录。", InfoBarSeverity.Success);
        }

        private void SelectNewest(int index)
        {
            _selectedCharacter = index;
            RefreshRows();

            ResourceRow? row = Rows.FirstOrDefault(item => !item.IsGroup && item.Index == index);
            if (row is not null)
            {
                row.Selected = true;
                _selectedRow = row;
            }

            ShowDetailForSelection();
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

            await Task.Delay(650);

            _busy = false;
            if (sender is Button done)
            {
                done.IsEnabled = true;
            }

            RefreshRows();
            ShowBar("已重新扫描", $"{FolderOf(_tab)} 目录共 {CountOf(_tab)} 个资源。", InfoBarSeverity.Informational);
        }

        private void CopyPath_Click(object sender, RoutedEventArgs e)
        {
            string path = DataRoot + @"\" + FolderOf(_tab);
            var package = new DataPackage();
            package.SetText(path);
            Clipboard.SetContent(package);
            ShowBar("路径已复制", path, InfoBarSeverity.Success);
        }

        private void OpenDirectory_Click(object sender, RoutedEventArgs e)
        {
            ShowBar("资源目录", DataRoot + @"\" + FolderOf(_tab), InfoBarSeverity.Informational);
        }

        private void OpenWorkbench_Click(object sender, RoutedEventArgs e)
        {
            WorkbenchPage.PendingKind = _tab switch
            {
                TabKind.WorldBooks => WorkbenchKind.WorldBook,
                TabKind.Presets => WorkbenchKind.Preset,
                _ => WorkbenchKind.Character,
            };
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

        private async void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                return;
            }

            string name = DetailTitleText.Text;
            string target = FolderOf(_tab);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "删除资源",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"将从 {target} 目录删除「{name}」，文件不可恢复。",
                },
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
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

            RemoveSelected();
            ShowBar("已删除", $"{name} 已从 {target} 目录中移除。", InfoBarSeverity.Success);
        }

        private void RemoveSelected()
        {
            switch (_tab)
            {
                case TabKind.Characters when _selectedCharacter is int character:
                    _characters.RemoveAt(character);
                    _selectedCharacter = _characters.Count == 0 ? null : Math.Min(character, _characters.Count - 1);
                    break;
                case TabKind.WorldBooks when _selectedWorldBook is int worldBook:
                    _worldBooks.RemoveAt(worldBook);
                    _selectedWorldBook = _worldBooks.Count == 0 ? null : Math.Min(worldBook, _worldBooks.Count - 1);
                    break;
                case TabKind.Chats when _selectedChat is int chat:
                    _chats.RemoveAt(chat);
                    _selectedChat = _chats.Count == 0 ? null : Math.Min(chat, _chats.Count - 1);
                    break;
                case TabKind.Presets when _selectedPreset is int preset:
                    _presets.RemoveAt(preset);
                    _selectedPreset = _presets.Count == 0 ? null : Math.Min(preset, _presets.Count - 1);
                    break;
            }

            RefreshRows();
            ShowDetailForSelection();
        }

        // ==================== 文案与工具 ====================

        private static string TitleOf(TabKind tab) => tab switch
        {
            TabKind.Characters => "角色卡",
            TabKind.WorldBooks => "世界书",
            TabKind.Chats => "对话",
            _ => "预设",
        };

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

        private static string FootOf(TabKind tab) => tab switch
        {
            TabKind.Characters => "按最近修改排序 · 直接读 characters 目录",
            TabKind.WorldBooks => "按最近修改排序 · 直接读 worlds 目录",
            TabKind.Chats => "按角色分文件夹 · 直接读 chats 目录",
            _ => "按最近修改排序 · 直接读 OpenAI Settings 目录",
        };

        private int CountOf(TabKind tab) => tab switch
        {
            TabKind.Characters => _characters.Count,
            TabKind.WorldBooks => _worldBooks.Count,
            TabKind.Chats => _chats.Count,
            _ => _presets.Count,
        };

        private static string SizeOf(long bytes) => ResourcePalette.Size(bytes);

        private void ShowBar(string title, string message, InfoBarSeverity severity)
        {
            ActionInfoBar.Title = title;
            ActionInfoBar.Message = message;
            ActionInfoBar.Severity = severity;
            ActionInfoBar.IsOpen = true;
        }
    }
}
