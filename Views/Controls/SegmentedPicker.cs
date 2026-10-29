using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
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
        }

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

        private void Rebuild()
        {
            _host.Children.Clear();
            _buttons.Clear();

            var labels = Split(Options);
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
                for (var i = 0; i < _buttons.Count; i++)
                {
                    _buttons[i].IsChecked = i == index;
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
