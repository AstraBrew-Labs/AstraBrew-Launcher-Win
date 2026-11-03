using System;
using AstraBrew_Launcher.Services;
using Microsoft.UI.Xaml.Controls;

namespace AstraBrew_Launcher.Views.Controls
{
    /// <summary>
    /// 全局消息栈。窗口里只有一份，由 <see cref="MainWindow"/> 挂在内容区右下角；
    /// 页面不直接引用它，一律通过 <see cref="AppNotify"/> 投递消息。
    /// </summary>
    public sealed partial class NotifyHost : UserControl
    {
        /// <summary>同屏最多三条：再多就是一堵挡视线的墙，新的进来先撤最早的。</summary>
        private const int MaxVisible = 3;

        public NotifyHost()
        {
            InitializeComponent();
        }

        public void Show(NotifyMessage message)
        {
            while (ToastStack.Children.Count >= MaxVisible && ToastStack.Children[0] is NotifyToast oldest)
            {
                // 容量满时不做退场动画：直接把最老的一条摘掉，保证新消息立刻可见
                oldest.Stop();
                oldest.Dismissed -= Toast_Dismissed;
                ToastStack.Children.Remove(oldest);
            }

            var toast = new NotifyToast();
            ToastStack.Children.Add(toast);
            toast.Dismissed += Toast_Dismissed;
            toast.Present(message);
        }

        private void Toast_Dismissed(object? sender, EventArgs e)
        {
            if (sender is not NotifyToast toast)
            {
                return;
            }

            toast.Dismissed -= Toast_Dismissed;
            ToastStack.Children.Remove(toast);
        }
    }
}
