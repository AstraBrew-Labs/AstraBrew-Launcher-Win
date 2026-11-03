using System;

namespace AstraBrew_Launcher.Services
{
    /// <summary>全局消息的严重程度：决定图标、配色与停留时长。</summary>
    public enum NotifySeverity
    {
        Success,
        Informational,
        Warning,
        Error,
    }

    /// <summary>
    /// 一条全局消息。标题是结论（「内置实例已安装」），正文是细节，
    /// 两者都按「一句话读完」来写——这里不是日志，长内容请留在页面或日志文件里。
    /// </summary>
    public sealed class NotifyMessage
    {
        public required NotifySeverity Severity { get; init; }

        public required string Title { get; init; }

        public string Message { get; init; } = string.Empty;

        /// <summary>可选操作按钮的文案；为空表示不显示按钮。</summary>
        public string? ActionText { get; init; }

        public Action? Action { get; init; }

        /// <summary>自动消失的秒数；0 表示常驻。</summary>
        public double Seconds { get; init; }

        /// <summary>同一把钥匙的消息会原地更新同一条卡片，而不是再叠一张。</summary>
        public string? Key { get; init; }

        /// <summary>
        /// 能不能由用户手动关掉。false 表示这条消息跟着某个后台任务走——
        /// 任务没结束它就该在，用户没有「关掉但任务还在跑」这种状态可选。
        /// </summary>
        public bool Dismissible { get; init; } = true;
    }

    /// <summary>
    /// 全局消息出口。页面把「装好了 / 装失败了」交给它，由主窗口右下角的
    /// <c>NotifyHost</c> 统一弹出：不打断操作，也不随页面一起消失。
    ///
    /// 为什么安装结果不走页面内的 InfoBar 或 ContentDialog：
    /// 装一次内置实例要跑一两分钟，这期间用户很可能已经翻到别的页面（甚至切回来看进度），
    /// 结果写在原页面里等于没播报——页面一卸载，提示就没了。
    ///
    /// 用法：
    /// <code>
    /// AppNotify.Success("内置实例已安装", "依赖也已就位，可以在仪表盘启动。");
    /// AppNotify.Error("安装失败", "连不上下载源。", "查看日志", () => OpenPath(logPath));
    /// </code>
    /// </summary>
    public static class AppNotify
    {
        /// <summary>请求展示一条消息；没有订阅者（窗口还没建好）时静默丢弃。</summary>
        public static event EventHandler<NotifyMessage>? Raised;

        /// <summary>请求撤掉某把钥匙对应的常驻消息（任务结束了）。</summary>
        public static event EventHandler<string>? Closed;

        public static void Success(string title, string message = "", string? actionText = null, Action? action = null)
            => Post(NotifySeverity.Success, title, message, actionText, action);

        public static void Info(string title, string message = "", string? actionText = null, Action? action = null)
            => Post(NotifySeverity.Informational, title, message, actionText, action);

        public static void Warning(string title, string message = "", string? actionText = null, Action? action = null)
            => Post(NotifySeverity.Warning, title, message, actionText, action);

        public static void Error(string title, string message = "", string? actionText = null, Action? action = null)
            => Post(NotifySeverity.Error, title, message, actionText, action);

        public static void Post(
            NotifySeverity severity,
            string title,
            string message = "",
            string? actionText = null,
            Action? action = null)
        {
            // 失败不自动消失：安装失败的提示要时间读，刚出现就滑走等于没说。
            // 警告稍长一点（还带着修复入口），成功/提示看个结论就够。
            double seconds = severity switch
            {
                NotifySeverity.Error => 0,
                NotifySeverity.Warning => 8,
                _ => 5,
            };

            Raised?.Invoke(
                null,
                new NotifyMessage
                {
                    Severity = severity,
                    Title = title,
                    Message = message,
                    ActionText = actionText,
                    Action = action,
                    Seconds = seconds,
                });
        }

        /// <summary>
        /// 一条「跟着后台任务走」的消息：常驻、不给关闭按钮、按 <paramref name="key"/> 原地更新。
        ///
        /// 为什么扫描进度要用它而不是弹窗：扫全盘要几十秒到几分钟，用户这时很可能在别的页面
        /// 甚至别的操作里。弹窗会挡住整个界面，逼他先处理这条提示；而页面内的小字一卸载就没了。
        /// 结果就是「扫描在跑，但谁都看不见」。挂在窗口上的常驻消息两个问题一起解决：
        /// 不打断、不随页面消失，任务结束由发起方 <see cref="Close"/> 收走。
        /// </summary>
        public static void Live(
            string key,
            string title,
            string message = "",
            string? actionText = null,
            Action? action = null)
        {
            Raised?.Invoke(
                null,
                new NotifyMessage
                {
                    Key = key,
                    Severity = NotifySeverity.Informational,
                    Title = title,
                    Message = message,
                    ActionText = actionText,
                    Action = action,
                    Seconds = 0,
                    Dismissible = false,
                });
        }

        /// <summary>撤掉一条常驻消息。不传 key 时什么都不做——调用方自己知道是哪一条。</summary>
        public static void Close(string key)
        {
            if (!string.IsNullOrEmpty(key))
            {
                Closed?.Invoke(null, key);
            }
        }
    }
}
