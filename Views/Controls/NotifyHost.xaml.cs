using System;
using System.Collections.Generic;
using System.Linq;
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

        /// <summary>带钥匙的常驻消息（扫描进度这类），再次投递同一把钥匙就原地更新。</summary>
        private readonly Dictionary<string, NotifyToast> _keyed = new(StringComparer.Ordinal);

        public NotifyHost()
        {
            InitializeComponent();

            // 常驻消息由发起方撤走（任务结束），页面和用户都不该操心它的生命周期
            AppNotify.Closed += OnNotifyClosed;
        }

        public void Show(NotifyMessage message)
        {
            if (message.Key is { Length: > 0 } key && _keyed.TryGetValue(key, out NotifyToast? live))
            {
                // 原地改字：进度每 250ms 来一次，重新排队会闪、也会把用户正在读的卡片顶走
                live.Update(message);
                return;
            }

            Trim();

            var toast = new NotifyToast();
            toast.Dismissed += Toast_Dismissed;
            ToastStack.Children.Add(toast);

            if (message.Key is { Length: > 0 } newKey)
            {
                _keyed[newKey] = toast;
            }

            toast.Present(message);
        }

        /// <summary>撤掉某把钥匙的消息；没有这条就什么都不做。</summary>
        private void OnNotifyClosed(object? sender, string key)
        {
            if (!_keyed.Remove(key, out NotifyToast? toast))
            {
                return;
            }

            toast.Dismiss();
        }

        /// <summary>
        /// 腾位置。挑最早的一条可关闭消息，跳过常驻的——
        /// 常驻消息代表「有任务正在跑」，被挤掉等于骗用户说任务没了。
        /// </summary>
        private void Trim()
        {
            while (ToastStack.Children.Count >= MaxVisible)
            {
                NotifyToast? victim = ToastStack.Children.OfType<NotifyToast>().FirstOrDefault(item => !item.IsPinned);
                if (victim is null)
                {
                    return;
                }

                // 容量满时不做退场动画：直接把最老的一条摘掉，保证新消息立刻可见
                victim.Stop();
                victim.Dismissed -= Toast_Dismissed;
                ToastStack.Children.Remove(victim);
                Forget(victim);
            }
        }

        private void Toast_Dismissed(object? sender, EventArgs e)
        {
            if (sender is not NotifyToast toast)
            {
                return;
            }

            toast.Dismissed -= Toast_Dismissed;
            ToastStack.Children.Remove(toast);
            Forget(toast);
        }

        private void Forget(NotifyToast toast)
        {
            if (toast.Key is { Length: > 0 } key && _keyed.TryGetValue(key, out NotifyToast? known) && ReferenceEquals(known, toast))
            {
                _keyed.Remove(key);
            }
        }
    }
}
