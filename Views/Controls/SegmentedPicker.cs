using System;
using System.Collections.Generic;
using AstraBrew_Launcher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AstraBrew_Launcher.Views.Controls
{
    /// <summary>
    /// 分段选择器：一排互斥选项放在浅色轨道里，选中段用卡片底色「抬起」。
    ///
    /// 设置页里同一个形态出现十多次，与其在 XAML 里重复写「Border + 若干 RadioButton」，
    /// 不如把选项做成数据：<c>Options="跟随系统,浅色,深色"</c>，
    /// <c>Glyphs="E770,E706,E708"</c> 可选。样式仍然复用全局的
    /// <c>SegmentTrackStyle</c> / <c>SegmentRadioStyle</c>，外观与其他页面一致。
    /// </summary>
    public sealed class SegmentedPicker : UserControl
    {
        private readonly Border _track;
        private readonly StackPanel _host;
        private readonly List<RadioButton> _buttons = new();
        private readonly string _groupName = "segment-" + Guid.NewGuid().ToString("N");
        private bool _syncing;

        /// <summary>用户切换了选项。</summary>
        public event EventHandler? SelectionChanged;

        public SegmentedPicker()
        {
            _host = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            _track = new Border
            {
                Child = _host,
                VerticalAlignment = VerticalAlignment.Center,
            };

            if (Application.Current.Resources.TryGetValue("SegmentTrackStyle", out object trackStyle)
                && trackStyle is Style style)
            {
                _track.Style = style;
            }

            Content = _track;

            Rebuild();

            // 选项文案是按语言取的：换语言后自己重建一遍，页面不必逐个通知。
            //
            // Loaded 里还要**重建一次**：控件被折叠/移出可视树时 Unloaded 会退订，
            // 期间发生的语言切换它收不到；等它再显示出来，文案就还是上一种语言
            // （踩过：引导第 1 步的「从别的启动器导入 / 从旧版导入」在英文界面里仍是中文）。
            Loaded += (_, _) =>
            {
                LanguageSwitch.LanguageChanged -= OnLanguageChanged;
                LanguageSwitch.LanguageChanged += OnLanguageChanged;
                Rebuild();
            };

            Unloaded += (_, _) => LanguageSwitch.LanguageChanged -= OnLanguageChanged;
        }

        private void OnLanguageChanged(object? sender, EventArgs e) => Rebuild();

        /// <summary>选项文案，英文逗号分隔。</summary>
        public static readonly DependencyProperty OptionsProperty = DependencyProperty.Register(
            nameof(Options),
            typeof(string),
            typeof(SegmentedPicker),
            new PropertyMetadata(string.Empty, (d, e) => ((SegmentedPicker)d).Rebuild()));

        public string Options
        {
            get => (string)GetValue(OptionsProperty);
            set => SetValue(OptionsProperty, value);
        }

        /// <summary>
        /// 选项文案的资源 key 前缀（与 <see cref="Options"/> 二选一）。
        /// 值形如 <c>Settings.Language.Option</c>，实际取 <c>.Count</c> 与 <c>.0/.1/.2</c>——
        /// 用计数而不是分隔串解析，翻译里出现逗号也不会把一段拆成两段。
        /// </summary>
        public static readonly DependencyProperty OptionsKeyProperty = DependencyProperty.Register(
            nameof(OptionsKey),
            typeof(string),
            typeof(SegmentedPicker),
            new PropertyMetadata(string.Empty, (d, e) => ((SegmentedPicker)d).Rebuild()));

        public string OptionsKey
        {
            get => (string)GetValue(OptionsKeyProperty);
            set => SetValue(OptionsKeyProperty, value);
        }

        /// <summary>与选项一一对应的图标码点（十六进制，不含 &amp;#x），留空则不显示图标。</summary>
        public static readonly DependencyProperty GlyphsProperty = DependencyProperty.Register(
            nameof(Glyphs),
            typeof(string),
            typeof(SegmentedPicker),
            new PropertyMetadata(string.Empty, (d, e) => ((SegmentedPicker)d).Rebuild()));

        public string Glyphs
        {
            get => (string)GetValue(GlyphsProperty);
            set => SetValue(GlyphsProperty, value);
        }

        /// <summary>当前选中项下标。</summary>
        public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
            nameof(SelectedIndex),
            typeof(int),
            typeof(SegmentedPicker),
            new PropertyMetadata(0, (d, e) => ((SegmentedPicker)d).ApplySelection()));

        public int SelectedIndex
        {
            get => (int)GetValue(SelectedIndexProperty);
            set => SetValue(SelectedIndexProperty, value);
        }

        /// <summary>选项文案：优先按资源 key 取，没有 key 才走逗号分隔的字面量。</summary>
        private IReadOnlyList<string> ResolveLabels()
        {
            if (OptionsKey is not { Length: > 0 } key)
            {
                return Split(Options);
            }

            if (int.TryParse(L.GetOrEmpty(key + ".Count"), out int count) && count > 0)
            {
                var items = new List<string>(count);
                for (int i = 0; i < count; i++)
                {
                    items.Add(L.Get($"{key}.{i}"));
                }

                return items;
            }

            // 没写 .Count 时按单条处理，至少不会整块空掉
            return new[] { L.Get(key) };
        }

        /// <summary>
        /// 换语言后重刷选项文案。选中项保持不变（重建时会按 SelectedIndex 重新点亮），
        /// 所以不会因为换了个语言把用户的选择弄丢。
        /// </summary>
        public void RefreshText() => Rebuild();

        private void Rebuild()
        {
            _host.Children.Clear();
            _buttons.Clear();

            var labels = ResolveLabels();
            var glyphs = Split(Glyphs);

            for (var i = 0; i < labels.Count; i++)
            {
                var content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                };

                var glyph = i < glyphs.Count ? glyphs[i] : string.Empty;
                if (glyph.Length > 0
                    && int.TryParse(glyph, System.Globalization.NumberStyles.HexNumber, null, out var code))
                {
                    content.Children.Add(new FontIcon
                    {
                        FontSize = 13,
                        Glyph = char.ConvertFromUtf32(code),
                    });
                }

                content.Children.Add(new TextBlock { Text = labels[i], FontSize = 13 });

                var button = new RadioButton
                {
                    Content = content,
                    GroupName = _groupName,
                    Tag = i,
                };

                // Content 是面板（图标 + 文字）时，RadioButton 算不出可读名字，
                // 读屏软件与自动化都只能拿到一个空名字——这里补上
                AutomationProperties.SetName(button, labels[i]);

                if (Application.Current.Resources.TryGetValue("SegmentRadioStyle", out object radioStyle)
                    && radioStyle is Style style)
                {
                    button.Style = style;
                }

                button.Checked += OnOptionChecked;
                _buttons.Add(button);
                _host.Children.Add(button);
            }

            ApplySelection();
        }

        private void OnOptionChecked(object sender, RoutedEventArgs e)
        {
            if (_syncing || sender is not RadioButton button || button.Tag is not int index)
            {
                return;
            }

            if (SelectedIndex == index)
            {
                return;
            }

            _syncing = true;
            try
            {
                SelectedIndex = index;
            }
            finally
            {
                _syncing = false;
            }

            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ApplySelection()
        {
            if (_buttons.Count == 0)
            {
                return;
            }

            var index = Math.Clamp(SelectedIndex, 0, _buttons.Count - 1);

            _syncing = true;
            try
            {
                // 先点亮目标、再关掉其余：反过来会先让「另一个」短暂进入选中态，
                // 而 RadioButton 的 Checked 有可能晚一拍才派发——那时 _syncing 已经复位，
                // 就会被当成用户又点了一次，把刚选的值改回去（表现为「切换偶尔才生效」）
                _buttons[index].IsChecked = true;

                for (var i = 0; i < _buttons.Count; i++)
                {
                    if (i != index)
                    {
                        _buttons[i].IsChecked = false;
                    }
                }
            }
            finally
            {
                _syncing = false;
            }
        }

        private static List<string> Split(string? raw)
        {
            var items = new List<string>();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return items;
            }

            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                items.Add(part.Trim());
            }

            return items;
        }
    }
}
