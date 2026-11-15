using System;
using AstraBrew_Launcher.Services;
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

            // 换语言时自己把 key 重取一遍：页面不必逐行记着哪些行有文案。
            // Loaded 里补一次重取：行被移出可视树时 Unloaded 会退订，这期间的语言
            // 切换收不到，再显示出来就会停在上一种语言。
            Loaded += (_, _) =>
            {
                LanguageSwitch.LanguageChanged -= OnLanguageChanged;
                LanguageSwitch.LanguageChanged += OnLanguageChanged;
                ApplyKeys();
            };

            Unloaded += (_, _) => LanguageSwitch.LanguageChanged -= OnLanguageChanged;

            SizeChanged += (_, _) => UpdateLayoutMode();
        }

        private void OnLanguageChanged(object? sender, EventArgs e) => ApplyKeys();

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

        /// <summary>
        /// 标题的资源 key。**设了它就以它为准**，<see cref="Title"/> 的字面量只是设计时占位。
        /// 这样换语言时控件自己能重取，不必让页面维护一张「哪行文案在哪儿」的对照表。
        /// </summary>
        public static readonly DependencyProperty TitleKeyProperty = DependencyProperty.Register(
            nameof(TitleKey),
            typeof(string),
            typeof(SettingRow),
            new PropertyMetadata(string.Empty, (d, e) => ((SettingRow)d).ApplyKeys()));

        public string TitleKey
        {
            get => (string)GetValue(TitleKeyProperty);
            set => SetValue(TitleKeyProperty, value);
        }

        /// <summary>标题。</summary>
        public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
            nameof(Title),
            typeof(string),
            typeof(SettingRow),
            new PropertyMetadata(string.Empty, (d, e) =>
            {
                var row = (SettingRow)d;

                // XAML 里 TitleKey 与 Title 的生效顺序不固定：有 key 时一律以 key 为准
                row.TitleText.Text = row.TitleKey is { Length: > 0 } key ? L.Get(key) : (string)e.NewValue;
            }));

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        /// <summary>
        /// 说明的资源 key。**只给「文案固定」的行用**；
        /// 说明随运行状态变化的行（字体、代理状态、Git 版本……）不要设，由页面按状态自己填。
        /// </summary>
        public static readonly DependencyProperty DescriptionKeyProperty = DependencyProperty.Register(
            nameof(DescriptionKey),
            typeof(string),
            typeof(SettingRow),
            new PropertyMetadata(string.Empty, (d, e) => ((SettingRow)d).ApplyKeys()));

        public string DescriptionKey
        {
            get => (string)GetValue(DescriptionKeyProperty);
            set => SetValue(DescriptionKeyProperty, value);
        }

        /// <summary>说明；留空则该行只有标题。</summary>
        public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
            nameof(Description),
            typeof(string),
            typeof(SettingRow),
            new PropertyMetadata(string.Empty, (d, e) =>
            {
                var row = (SettingRow)d;

                // 有 key 时以 key 为准；**不要在这里再给 Description 赋值**——
                // 值相同 DP 不会再回调，界面就永远刷不出来（踩过：说明整行空白）
                row.ApplyDescription(row.DescriptionKey is { Length: > 0 } key
                    ? L.Get(key)
                    : (string)e.NewValue);
            }));

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

        /// <summary>按 key 重取标题与说明；没有 key 的那一项保持原样（可能由页面按状态填）。</summary>
        private void ApplyKeys()
        {
            if (TitleKey is { Length: > 0 } titleKey)
            {
                Title = L.Get(titleKey);
            }

            if (DescriptionKey is { Length: > 0 } descriptionKey)
            {
                ApplyDescription(L.Get(descriptionKey));
            }
        }

        private void ApplyDescription() => ApplyDescription(Description ?? string.Empty);

        private void ApplyDescription(string text)
        {
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
