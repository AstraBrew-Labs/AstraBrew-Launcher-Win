using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AstraBrew_Launcher.Views
{
    /// <summary>
    /// 一次可撤销的编辑。同一个字段在 0.9 秒内的连续输入会合并成一步，
    /// 避免撤销时一个字一个字往回退。
    /// </summary>
    public sealed class EditLog
    {
        private sealed class Step
        {
            public string Key = string.Empty;
            public Action Undo = () => { };
            public Action Redo = () => { };
            public DateTime At;
        }

        private const int MaxSteps = 200;
        private static readonly TimeSpan MergeWindow = TimeSpan.FromMilliseconds(900);

        private readonly List<Step> _undo = new();
        private readonly List<Step> _redo = new();

        public event Action? Changed;

        public bool CanUndo => _undo.Count > 0;

        public bool CanRedo => _redo.Count > 0;

        public void Record(string key, Action undo, Action redo)
        {
            _redo.Clear();

            DateTime now = DateTime.Now;
            if (_undo.Count > 0)
            {
                Step last = _undo[^1];
                if (last.Key == key && now - last.At < MergeWindow)
                {
                    last.Redo = redo;
                    last.At = now;
                    Changed?.Invoke();
                    return;
                }
            }

            _undo.Add(new Step { Key = key, Undo = undo, Redo = redo, At = now });
            if (_undo.Count > MaxSteps)
            {
                _undo.RemoveAt(0);
            }

            Changed?.Invoke();
        }

        public void Undo()
        {
            if (_undo.Count == 0)
            {
                return;
            }

            Step step = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            step.Undo();
            _redo.Add(step);
            Changed?.Invoke();
        }

        public void Redo()
        {
            if (_redo.Count == 0)
            {
                return;
            }

            Step step = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            step.Redo();
            _undo.Add(step);
            Changed?.Invoke();
        }

        public void Clear()
        {
            _undo.Clear();
            _redo.Clear();
            Changed?.Invoke();
        }
    }

    /// <summary>可编辑标签：文字 + 一个「从哪个集合里删掉我」的回调。</summary>
    public sealed class EditTag
    {
        public EditTag(string text, Action<EditTag> remove)
        {
            Text = text;
            Remove = remove;
        }

        public string Text { get; }

        public Action<EditTag> Remove { get; }
    }

    /// <summary>
    /// 表单模型基类：所有字段改动都会写进 <see cref="EditLog"/>，撤销时静默回填。
    /// </summary>
    public abstract class EditModel : INotifyPropertyChanged
    {
        private readonly string _id = Guid.NewGuid().ToString("N")[..6];
        private bool _silent;

        public event PropertyChangedEventHandler? PropertyChanged;

        public EditLog? Log { get; set; }

        protected void Raise([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected void Set<T>(ref T field, T value, Action<T> apply, [CallerMemberName] string? key = null)
            where T : notnull
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            T old = field;
            field = value;
            Raise(key);

            if (_silent || Log is null)
            {
                return;
            }

            Log.Record($"{_id}:{key}", () => Apply(old, apply), () => Apply(value, apply));
        }

        private void Apply<T>(T value, Action<T> apply)
        {
            _silent = true;
            try
            {
                apply(value);
            }
            finally
            {
                _silent = false;
            }
        }
    }

    /// <summary>角色卡表单。</summary>
    public sealed class CharacterForm : EditModel
    {
        private static readonly Brush CoverTint = new SolidColorBrush(Color.FromArgb(0x26, 0x2B, 0x88, 0xD8));

        private string _name = string.Empty;
        private string _creator = string.Empty;
        private string _version = string.Empty;
        private string _description = string.Empty;
        private string _personality = string.Empty;
        private string _scenario = string.Empty;
        private string _firstMessage = string.Empty;
        private string _tagInput = string.Empty;
        private bool _hasCover = true;

        public string Name
        {
            get => _name;
            set
            {
                Set(ref _name, value, v => Name = v);
                Raise(nameof(Monogram));
            }
        }

        public string Creator
        {
            get => _creator;
            set => Set(ref _creator, value, v => Creator = v);
        }

        public string Version
        {
            get => _version;
            set => Set(ref _version, value, v => Version = v);
        }

        public string Description
        {
            get => _description;
            set
            {
                Set(ref _description, value, v => Description = v);
                Raise(nameof(DescriptionCount));
            }
        }

        public string Personality
        {
            get => _personality;
            set
            {
                Set(ref _personality, value, v => Personality = v);
                Raise(nameof(PersonalityCount));
            }
        }

        public string Scenario
        {
            get => _scenario;
            set
            {
                Set(ref _scenario, value, v => Scenario = v);
                Raise(nameof(ScenarioCount));
            }
        }

        public string FirstMessage
        {
            get => _firstMessage;
            set
            {
                Set(ref _firstMessage, value, v => FirstMessage = v);
                Raise(nameof(FirstMessageCount));
            }
        }

        public string TagInput
        {
            get => _tagInput;
            set => Set(ref _tagInput, value, v => TagInput = v);
        }

        public bool HasCover
        {
            get => _hasCover;
            set
            {
                Set(ref _hasCover, value, v => HasCover = v);
                Raise(nameof(CoverVisibility));
                Raise(nameof(CoverEmptyVisibility));
            }
        }

        public ObservableCollection<EditTag> Tags { get; } = new();

        /// <summary>角色卡是否携带内嵌世界书；解绑后变 false，卡片改显空态。</summary>
        public bool HasWorld { get; set; } = true;

        public string Monogram => _name.Length == 0 ? "?" : _name[..1];

        public Brush CoverBackground => CoverTint;

        public Visibility CoverVisibility => _hasCover ? Visibility.Visible : Visibility.Collapsed;

        public Visibility CoverEmptyVisibility => _hasCover ? Visibility.Collapsed : Visibility.Visible;

        public string TagSummary => Tags.Count == 0 ? "未添加标签" : $"{Tags.Count} 个";

        public string DescriptionCount => $"{_description.Length} 字";

        public string PersonalityCount => $"{_personality.Length} 字";

        public string ScenarioCount => $"{_scenario.Length} 字";

        public string FirstMessageCount => $"{_firstMessage.Length} 字";

        public void AddTag(string raw)
        {
            string text = raw.Trim();
            if (text.Length == 0 || Tags.Any(tag => tag.Text == text))
            {
                return;
            }

            EditTag item = new(text, RemoveTag);
            int index = Tags.Count;
            Tags.Insert(index, item);
            Raise(nameof(TagSummary));

            Log?.Record(
                $"tag:{Guid.NewGuid():N}",
                () =>
                {
                    Tags.Remove(item);
                    Raise(nameof(TagSummary));
                },
                () =>
                {
                    Tags.Insert(Math.Min(index, Tags.Count), item);
                    Raise(nameof(TagSummary));
                });
        }

        public void RemoveTag(EditTag tag)
        {
            int index = Tags.IndexOf(tag);
            if (index < 0)
            {
                return;
            }

            Tags.RemoveAt(index);
            Raise(nameof(TagSummary));

            Log?.Record(
                $"tag:{Guid.NewGuid():N}",
                () =>
                {
                    Tags.Insert(Math.Min(index, Tags.Count), tag);
                    Raise(nameof(TagSummary));
                },
                () =>
                {
                    Tags.Remove(tag);
                    Raise(nameof(TagSummary));
                });
        }
    }

    /// <summary>世界书表单：独立世界书与角色内嵌世界书共用。</summary>
    public sealed class WorldBookForm : EditModel
    {
        private string _name = string.Empty;
        private string _author = string.Empty;

        public string Name
        {
            get => _name;
            set => Set(ref _name, value, v => Name = v);
        }

        public string Author
        {
            get => _author;
            set => Set(ref _author, value, v => Author = v);
        }

        public ObservableCollection<EntryForm> Entries { get; } = new();

        public int EnabledCount => Entries.Count(entry => entry.Enabled);

        public void Load(string name, string author, IEnumerable<EntryForm> entries)
        {
            Name = name;
            Author = author;
            Entries.Clear();
            int number = 1;
            foreach (EntryForm entry in entries)
            {
                entry.Number = number++;
                Entries.Add(entry);
            }
        }

        public void Relabel()
        {
            int number = 1;
            foreach (EntryForm entry in Entries)
            {
                entry.Number = number++;
            }
        }
    }

    /// <summary>世界书条目的可编辑版本。</summary>
    public sealed class EntryForm : EditModel
    {
        private static readonly string[] Positions =
        {
            "角色定义之前",
            "角色定义之后",
            "示例消息之前",
            "示例消息之后",
            "作者注释之前",
            "作者注释之后",
            "深度 · 系统",
            "深度 · 用户",
            "深度 · 助手",
            "出口",
        };

        private string _comment = string.Empty;
        private string _content = string.Empty;
        private string _order = "100";
        private string _probability = "100";
        private string _depth = "4";
        private string _keysInput = string.Empty;
        private string _secondaryKeysInput = string.Empty;
        private bool _enabled = true;
        private int _position;
        private int _number = 1;

        public int Number
        {
            get => _number;
            set
            {
                if (_number == value)
                {
                    return;
                }

                _number = value;
                Raise(nameof(IndexLabel));
                Raise(nameof(ShortIndex));
            }
        }

        public string IndexLabel => $"条目 {_number}";

        public string ShortIndex => _number.ToString("00");

        public string Comment
        {
            get => _comment;
            set
            {
                Set(ref _comment, value, v => Comment = v);
                Raise(nameof(KeysLabel));
            }
        }

        public string Content
        {
            get => _content;
            set => Set(ref _content, value, v => Content = v);
        }

        public string Order
        {
            get => _order;
            set => Set(ref _order, value, v => Order = v);
        }

        public string Probability
        {
            get => _probability;
            set => Set(ref _probability, value, v => Probability = v);
        }

        public string Depth
        {
            get => _depth;
            set => Set(ref _depth, value, v => Depth = v);
        }

        public string KeysInput
        {
            get => _keysInput;
            set => Set(ref _keysInput, value, v => KeysInput = v);
        }

        public string SecondaryKeysInput
        {
            get => _secondaryKeysInput;
            set => Set(ref _secondaryKeysInput, value, v => SecondaryKeysInput = v);
        }

        public bool Enabled
        {
            get => _enabled;
            set
            {
                Set(ref _enabled, value, v => Enabled = v);
                Raise(nameof(EnabledVisibility));
                Raise(nameof(DisabledVisibility));
            }
        }

        public int Position
        {
            get => _position;
            set => Set(ref _position, value, v => Position = v);
        }

        public string[] PositionOptions => Positions;

        public ObservableCollection<EditTag> Keys { get; } = new();

        public ObservableCollection<EditTag> SecondaryKeys { get; } = new();

        public string KeysLabel => Keys.Count == 0 ? "无关键词" : string.Join(" · ", Keys.Select(tag => tag.Text));

        public string KeyHint => $"{Keys.Count} 关键词";

        public Visibility KeyHintVisibility => Keys.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        public Visibility EnabledVisibility => _enabled ? Visibility.Visible : Visibility.Collapsed;

        public Visibility DisabledVisibility => _enabled ? Visibility.Collapsed : Visibility.Visible;

        public void AddKey(bool secondary)
        {
            string text = (secondary ? SecondaryKeysInput : KeysInput).Trim();
            if (text.Length == 0)
            {
                return;
            }

            ObservableCollection<EditTag> target = secondary ? SecondaryKeys : Keys;
            if (target.Any(tag => tag.Text == text))
            {
                return;
            }

            EditTag item = new(text, tag => RemoveKey(tag, secondary));
            int index = target.Count;
            target.Insert(index, item);
            NotifyKeys();

            if (secondary)
            {
                SecondaryKeysInput = string.Empty;
            }
            else
            {
                KeysInput = string.Empty;
            }

            Log?.Record(
                $"key:{Guid.NewGuid():N}",
                () =>
                {
                    target.Remove(item);
                    NotifyKeys();
                },
                () =>
                {
                    target.Insert(Math.Min(index, target.Count), item);
                    NotifyKeys();
                });
        }

        public void RemoveKey(EditTag tag, bool secondary)
        {
            ObservableCollection<EditTag> target = secondary ? SecondaryKeys : Keys;
            int index = target.IndexOf(tag);
            if (index < 0)
            {
                return;
            }

            target.RemoveAt(index);
            NotifyKeys();

            Log?.Record(
                $"key:{Guid.NewGuid():N}",
                () =>
                {
                    target.Insert(Math.Min(index, target.Count), tag);
                    NotifyKeys();
                },
                () =>
                {
                    target.Remove(tag);
                    NotifyKeys();
                });
        }

        private void NotifyKeys()
        {
            Raise(nameof(KeysLabel));
            Raise(nameof(KeyHint));
            Raise(nameof(KeyHintVisibility));
        }
    }

    /// <summary>预设里的一条提示词。</summary>
    public sealed class PromptForm : EditModel
    {
        private static readonly string[] Roles = { "系统", "用户", "助手" };

        private string _name = string.Empty;
        private string _content = string.Empty;
        private bool _enabled = true;
        private int _role;

        public string Identifier { get; set; } = string.Empty;

        public bool IsMarker { get; set; }

        public string KindLabel => IsMarker ? "标记" : "提示词";

        public string Name
        {
            get => _name;
            set => Set(ref _name, value, v => Name = v);
        }

        public string Content
        {
            get => _content;
            set => Set(ref _content, value, v => Content = v);
        }

        public bool Enabled
        {
            get => _enabled;
            set
            {
                Set(ref _enabled, value, v => Enabled = v);
                Raise(nameof(EnabledVisibility));
                Raise(nameof(DisabledVisibility));
            }
        }

        public int Role
        {
            get => _role;
            set => Set(ref _role, value, v => Role = v);
        }

        public string[] RoleOptions => Roles;

        public Visibility EditorVisibility => IsMarker ? Visibility.Collapsed : Visibility.Visible;

        // 开着的时候不加胶囊——开关本身就是状态，胶囊只标记「停用 / 占位」这类例外。
        public Visibility EnabledVisibility => Visibility.Collapsed;

        public Visibility DisabledVisibility
            => !IsMarker && !_enabled ? Visibility.Visible : Visibility.Collapsed;

        public Visibility MarkerVisibility => IsMarker ? Visibility.Visible : Visibility.Collapsed;
    }
}
