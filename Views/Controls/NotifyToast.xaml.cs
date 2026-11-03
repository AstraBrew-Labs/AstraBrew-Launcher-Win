using System;
using AstraBrew_Launcher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace AstraBrew_Launcher.Views.Controls
{
    /// <summary>
    /// 一条全局消息。自己管进场动画、倒计时条与自动消失；
    /// 宿主 <see cref="NotifyHost"/> 只负责排队，并在消失后把它摘掉。
    /// </summary>
    public sealed partial class NotifyToast : UserControl
    {
        private readonly DispatcherTimer _timer = new();
        private Action? _action;
        private bool _closing;

        public NotifyToast()
        {
            InitializeComponent();

            _timer.Tick += (_, _) =>
            {
                _timer.Stop();
                Dismiss();
            };
        }

        /// <summary>消失动画跑完后触发：宿主收到后把这张卡从栈里移除。</summary>
        public event EventHandler? Dismissed;

        /// <summary>
        /// 填入一条消息并播放入场动画。调用前请先挂进视觉树——
        /// 图标配色要看 <see cref="FrameworkElement.ActualTheme"/>，离开树就取不到真实主题。
        /// </summary>
        public void Present(NotifyMessage message)
        {
            TitleText.Text = message.Title;

            bool hasBody = !string.IsNullOrWhiteSpace(message.Message);
            MessageText.Text = message.Message;
            MessageText.Visibility = hasBody ? Visibility.Visible : Visibility.Collapsed;

            var (glyph, accent) = Look(message.Severity, IsDark);
            SeverityIcon.Glyph = glyph;
            SeverityIcon.Foreground = new SolidColorBrush(accent);

            if (!string.IsNullOrWhiteSpace(message.ActionText) && message.Action is not null)
            {
                _action = message.Action;
                ActionButton.Content = message.ActionText;
                ActionButton.Visibility = Visibility.Visible;
            }
            else
            {
                ActionButton.Visibility = Visibility.Collapsed;
            }

            // 倒计时条用同色半透明：既指出「还会停留多久」，又不抢标题的注意力
            CountdownBar.Background = new SolidColorBrush(Color.FromArgb(0x80, accent.R, accent.G, accent.B));

            PlayEnter();

            if (message.Seconds > 0)
            {
                CountdownTrack.Visibility = Visibility.Visible;
                StartCountdown(message.Seconds);

                _timer.Interval = TimeSpan.FromSeconds(message.Seconds);
                _timer.Start();
            }
            else
            {
                // 常驻消息（失败类）不给倒计时条，避免暗示它会自己走
                CountdownTrack.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>播放淡出动画，结束后由宿主移除。</summary>
        public void Dismiss()
        {
            if (_closing)
            {
                return;
            }

            _closing = true;
            _timer.Stop();

            var story = new Storyboard();
            story.Children.Add(Tween(Card, "Opacity", Card.Opacity, 0, 140));
            if (Card.RenderTransform is TranslateTransform offset)
            {
                story.Children.Add(Tween(offset, "Y", offset.Y, 8, 140));
            }

            story.Completed += (_, _) => Dismissed?.Invoke(this, EventArgs.Empty);
            story.Begin();
        }

        /// <summary>直接停掉计时器（宿主容量满、要立刻腾位置时用）。</summary>
        public void Stop() => _timer.Stop();

        /// <summary>当前是否处于深色外观；<c>Default</c> 时跟随应用级主题。</summary>
        private bool IsDark => ActualTheme == ElementTheme.Dark
            || (ActualTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);

        private void PlayEnter()
        {
            // 自下而上 10px + 淡入：右下角新冒出来的东西，位移比纯淡入更容易被注意到
            var offset = new TranslateTransform { Y = 10 };
            Card.RenderTransform = offset;
            Card.Opacity = 0;

            var story = new Storyboard();
            story.Children.Add(Tween(Card, "Opacity", 0, 1, 160));
            story.Children.Add(Tween(offset, "Y", 10, 0, 220));
            story.Begin();
        }

        private void StartCountdown(double seconds)
        {
            var scale = new ScaleTransform { ScaleX = 1 };
            CountdownBar.RenderTransform = scale;

            var anim = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromSeconds(seconds),
                EnableDependentAnimation = true,
            };

            Storyboard.SetTarget(anim, scale);
            Storyboard.SetTargetProperty(anim, "ScaleX");

            var story = new Storyboard();
            story.Children.Add(anim);
            story.Begin();
        }

        private static DoubleAnimation Tween(DependencyObject target, string path, double from, double to, double milliseconds)
        {
            var anim = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = TimeSpan.FromMilliseconds(milliseconds),
                EnableDependentAnimation = true,
            };

            Storyboard.SetTarget(anim, target);
            Storyboard.SetTargetProperty(anim, path);
            return anim;
        }

        /// <summary>
        /// 严重程度 → 图标与颜色。这里写死两套色值而不是取框架的
        /// <c>SystemFillColor*</c> 主题资源：运行时按消息种类切换前景色，
        /// 走代码取主题字典拿不到元素级的 <c>RequestedTheme</c>，颜色会和当前外观对不上。
        /// </summary>
        private static (string Glyph, Color Accent) Look(NotifySeverity severity, bool dark) => severity switch
        {
            NotifySeverity.Success => ("\uE930", dark ? Rgb(0x6C, 0xCB, 0x5F) : Rgb(0x0F, 0x7B, 0x0F)),
            NotifySeverity.Warning => ("\uE7BA", dark ? Rgb(0xF9, 0xC1, 0x5C) : Rgb(0x9D, 0x5D, 0x00)),
            NotifySeverity.Error => ("\uEA39", dark ? Rgb(0xFF, 0x99, 0xA4) : Rgb(0xC4, 0x2B, 0x1C)),
            _ => ("\uE946", dark ? Rgb(0x60, 0xCD, 0xFF) : Rgb(0x00, 0x67, 0xC0)),
        };

        private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(0xFF, r, g, b);

        private void Close_Click(object sender, RoutedEventArgs e) => Dismiss();

        private void Action_Click(object sender, RoutedEventArgs e)
        {
            // 先收卡片再执行动作：动作可能打开文件、跳转页面，卡片不该再挡在那里
            Action? action = _action;
            Dismiss();

            try
            {
                action?.Invoke();
            }
            catch (Exception)
            {
                // 动作本身失败不该把消息系统也带崩
            }
        }
    }
}
