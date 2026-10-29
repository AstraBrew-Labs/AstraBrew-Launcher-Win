using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AstraBrew_Launcher.Views.Controls
{
    /// <summary>
    /// 一条设置项：左侧图标座 + 标题/说明，右侧是调用方给的控件。
    ///
    /// 控件槽的摆放由控件自己决定：行宽小于 <see cref="WrapAt"/> 时落到下一行，
    /// 并左对齐到说明文字的左边缘。设置页里三十多行长度各不相同，
    /// 把这段自适应下沉到这里，页面就不必为每一行写一份 VisualState。
    /// </summary>
    [ContentProperty(Name = nameof(RowContent))]
    public sealed partial class SettingRow : UserControl
    {
        private bool _stacked;

        public SettingRow()
        {
            InitializeComponent();
            ApplyAccent();
            ApplyDescription();
            SizeChanged += (_, _) => UpdateLayoutMode();
        }

        /// <summary>右侧控件。</summary>
        public static readonly DependencyProperty RowContentProperty = DependencyProperty.Register(
            nameof(RowContent),
            typeof(object),
            typeof(SettingRow),
            new PropertyMetadata(null, (d, e) => ((SettingRow)d).ApplyRowContent(e.NewValue)));

        public object? RowContent
        {
            get => GetValue(RowContentProperty);
            set => SetValue(RowContentProperty, value);
        }

        /// <summary>图标码点。</summary>
        public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
            nameof(Glyph),
            typeof(string),
            typeof(SettingRow),
            new PropertyMetadata("\uE946", (d, e) => ((SettingRow)d).IconGlyph.Glyph = (string)e.NewValue));

        public string Glyph
        {
            get => (string)GetValue(GlyphProperty);
            set => SetValue(GlyphProperty, value);
        }

        /// <summary>标题。</summary>
        public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
            nameof(Title),
            typeof(string),
            typeof(SettingRow),
            new PropertyMetadata(string.Empty, (d, e) => ((SettingRow)d).TitleText.Text = (string)e.NewValue));

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        /// <summary>说明；留空则该行只有标题。</summary>
        public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
            nameof(Description),
            typeof(string),
            typeof(SettingRow),
            new PropertyMetadata(string.Empty, (d, e) => ((SettingRow)d).ApplyDescription()));

        public string Description
        {
            get => (string)GetValue(DescriptionProperty);
            set => SetValue(DescriptionProperty, value);
        }

        /// <summary>
        /// 行宽低于该值时控件转到下一行；0 表示永不换行（开关、小按钮这类窄控件）。
        /// </summary>
        public static readonly DependencyProperty WrapAtProperty = DependencyProperty.Register(
            nameof(WrapAt),
            typeof(double),
            typeof(SettingRow),
            new PropertyMetadata(560.0, (d, e) => ((SettingRow)d).UpdateLayoutMode()));

        public double WrapAt
        {
            get => (double)GetValue(WrapAtProperty);
            set => SetValue(WrapAtProperty, value);
        }

        /// <summary>图标强调色，十六进制 RGB（不含 #）。底色自动取同色 12% 透明度。</summary>
        public static readonly DependencyProperty AccentHexProperty = DependencyProperty.Register(
            nameof(AccentHex),
            typeof(string),
            typeof(SettingRow),
            new PropertyMetadata("2B88D8", (d, e) => ((SettingRow)d).ApplyAccent()));

        public string AccentHex
        {
            get => (string)GetValue(AccentHexProperty);
            set => SetValue(AccentHexProperty, value);
        }

        private void ApplyRowContent(object? content)
        {
            if (ControlHost is not null)
            {
                ControlHost.Content = content;
            }
        }

        private void ApplyDescription()
        {
            var text = Description ?? string.Empty;
            DescriptionText.Text = text;
            DescriptionText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ApplyAccent()
        {
            var hex = string.IsNullOrWhiteSpace(AccentHex) ? "2B88D8" : AccentHex.TrimStart('#');
            if (hex.Length == 6 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            {
                var r = (byte)((rgb >> 16) & 0xFF);
                var g = (byte)((rgb >> 8) & 0xFF);
                var b = (byte)(rgb & 0xFF);
                IconTile.Background = new SolidColorBrush(Color.FromArgb(0x1F, r, g, b));
                IconGlyph.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, r, g, b));
            }
        }

        private void UpdateLayoutMode()
        {
            if (WrapAt <= 0 || ActualWidth <= 0)
            {
                if (WrapAt <= 0 && _stacked)
                {
                    SetStacked(false);
                }

                return;
            }

            SetStacked(ActualWidth < WrapAt);
        }

        private void SetStacked(bool stacked)
        {
            if (_stacked == stacked)
            {
                return;
            }

            _stacked = stacked;

            if (stacked)
            {
                Grid.SetRow(ControlHost, 1);
                Grid.SetColumn(ControlHost, 1);
                Grid.SetColumnSpan(ControlHost, 2);
                ControlHost.HorizontalAlignment = HorizontalAlignment.Left;
                Root.RowSpacing = 10;
            }
            else
            {
                Grid.SetRow(ControlHost, 0);
                Grid.SetColumn(ControlHost, 2);
                Grid.SetColumnSpan(ControlHost, 1);
                ControlHost.HorizontalAlignment = HorizontalAlignment.Right;
                Root.RowSpacing = 0;
            }
        }
    }
}
