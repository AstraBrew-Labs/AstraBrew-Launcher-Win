using System;
using System.Collections.Generic;
using AstraBrew_Launcher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AstraBrew_Launcher.Views.Controls
{
    /// <summary>
    /// XAML 侧的文案绑定：<c>loc:Loc.Text="Shell.Nav.Dashboard.Content"</c>。
    ///
    /// **为什么不用 <c>x:Uid</c>**：WinUI 的 x:Uid 解析依赖「PRI 索引名 ↔ 应用身份」对齐。
    /// 本项目的 PRI 索引名是包标识（4f424de3-…，来自 AppxManifest），
    /// 而以 exe 直接运行时进程没有包身份，运行时按进程名去找索引 —— 对不上，
    /// 结果是 x:Uid 静默失效（元素上文字为空，日志里也没有任何报错）。
    /// 自建附加属性走 <see cref="L"/>，两种运行方式下行为一致，缺 key 时还会打出 key 全名便于定位。
    ///
    /// 文案仍然存放在 <c>Strings/&lt;语言&gt;/Resources.resw</c>，翻译流程不变。
    ///
    /// **实时切换**：每个被套上 <c>Loc.*</c> 的元素都会在 <see cref="Tracked"/> 里留一条弱引用。
    /// 换语言时 <see cref="RefreshAll"/> 把它们挨个重取一遍即可原位换字，
    /// 不需要重建页面，更不需要关窗重开。
    /// </summary>
    public static class Loc
    {
        /// <summary>
        /// 登记过的元素（弱引用，不拖住已卸载的页面）。
        /// 只存元素不存 key：key 就在附加属性上，重刷时读回来即可，省得两处状态不同步。
        /// </summary>
        private static readonly List<WeakReference<DependencyObject>> Tracked = new();

        /// <summary>TextBlock.Text / TextBox.Header 等「纯文本」属性。</summary>
        public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
            "Text",
            typeof(string),
            typeof(Loc),
            new PropertyMetadata(null, (d, e) => Apply(d, e, AssignText)));

        public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

        public static void SetText(DependencyObject element, string value) => element.SetValue(TextProperty, value);

        /// <summary>ContentControl.Content / NavigationViewItem.Content 等「内容」属性。</summary>
        public static readonly DependencyProperty ContentProperty = DependencyProperty.RegisterAttached(
            "Content",
            typeof(string),
            typeof(Loc),
            new PropertyMetadata(null, (d, e) => Apply(d, e, AssignContent)));

        public static string? GetContent(DependencyObject element) => (string?)element.GetValue(ContentProperty);

        public static void SetContent(DependencyObject element, string value) => element.SetValue(ContentProperty, value);

        /// <summary>输入框占位符。</summary>
        /// <remarks>
        /// <see cref="TextBox"/> 与 <see cref="AutoSuggestBox"/> 各有一个同名属性但互不继承，
        /// 两个都要写：只认 TextBox 的话，搜索框会静默留空（英文界面下尤其明显）。
        /// </remarks>
        public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
            "Placeholder",
            typeof(string),
            typeof(Loc),
            new PropertyMetadata(null, (d, e) => Apply(d, e, static (target, value) =>
            {
                switch (target)
                {
                    case TextBox box:
                        box.PlaceholderText = value;
                        break;
                    case AutoSuggestBox suggest:
                        suggest.PlaceholderText = value;
                        break;
                }
            })));

        public static string? GetPlaceholder(DependencyObject element) => (string?)element.GetValue(PlaceholderProperty);

        public static void SetPlaceholder(DependencyObject element, string value) => element.SetValue(PlaceholderProperty, value);

        /// <summary>悬浮提示。</summary>
        public static readonly DependencyProperty ToolTipProperty = DependencyProperty.RegisterAttached(
            "ToolTip",
            typeof(string),
            typeof(Loc),
            new PropertyMetadata(null, (d, e) => Apply(d, e, (target, value) =>
                ToolTipService.SetToolTip(target, value))));

        public static string? GetToolTip(DependencyObject element) => (string?)element.GetValue(ToolTipProperty);

        public static void SetToolTip(DependencyObject element, string value) => element.SetValue(ToolTipProperty, value);

        /// <summary>无障碍名字（读屏软件念的那句）。图标按钮没有可见文字时全靠它。</summary>
        public static readonly DependencyProperty NameProperty = DependencyProperty.RegisterAttached(
            "Name",
            typeof(string),
            typeof(Loc),
            new PropertyMetadata(null, (d, e) => Apply(d, e, static (target, value) =>
                AutomationProperties.SetName(target, value))));

        public static string? GetName(DependencyObject element) => (string?)element.GetValue(NameProperty);

        public static void SetName(DependencyObject element, string value) => element.SetValue(NameProperty, value);

        /// <summary>
        /// 任意「字符串型依赖属性」的通用入口：
        /// <c>loc:Loc.Property="Title:Settings.Row.Language.Title"</c> 这种自定控件属性用它，
        /// 值形如 <c>属性名:资源键</c>。
        /// </summary>
        public static readonly DependencyProperty PropertyProperty = DependencyProperty.RegisterAttached(
            "Property",
            typeof(string),
            typeof(Loc),
            new PropertyMetadata(null, (d, e) => Apply(d, e, AssignProperty)));

        public static string? GetProperty(DependencyObject element) => (string?)element.GetValue(PropertyProperty);

        public static void SetProperty(DependencyObject element, string value) => element.SetValue(PropertyProperty, value);

        /// <summary>
        /// 换语言后把所有登记过的元素按新语言重刷一遍。
        /// 返回实际重刷的元素数，方便在日志里确认「真的刷到了东西」。
        /// </summary>
        /// <remarks>
        /// 必须在 UI 线程调用：这里会直接写控件属性。
        /// 元素被卸载后只留下一条弱引用，下一轮顺手清理掉，不会攒住整棵旧页面树。
        /// </remarks>
        public static int RefreshAll()
        {
            var alive = new List<DependencyObject>();

            lock (Tracked)
            {
                for (int i = Tracked.Count - 1; i >= 0; i--)
                {
                    if (Tracked[i].TryGetTarget(out DependencyObject? target))
                    {
                        alive.Add(target);
                    }
                    else
                    {
                        Tracked.RemoveAt(i);
                    }
                }
            }

            int count = 0;
            foreach (DependencyObject element in alive)
            {
                try
                {
                    count += Reapply(element);
                }
                catch (Exception)
                {
                    // 单个元素刷不动（已被移除、类型变了）不该拖垮整次切换
                }
            }

            return count;
        }

        /// <summary>把一个元素身上所有 <c>Loc.*</c> 重新取一遍。返回命中的条数。</summary>
        private static int Reapply(DependencyObject element)
        {
            int applied = 0;

            if (GetText(element) is { Length: > 0 } text)
            {
                AssignText(element, L.Get(text));
                applied++;
            }

            if (GetContent(element) is { Length: > 0 } content)
            {
                AssignContent(element, L.Get(content));
                applied++;
            }

            if (GetPlaceholder(element) is { Length: > 0 } placeholder)
            {
                switch (element)
                {
                    case TextBox box:
                        box.PlaceholderText = L.Get(placeholder);
                        break;
                    case AutoSuggestBox suggest:
                        suggest.PlaceholderText = L.Get(placeholder);
                        break;
                }

                applied++;
            }

            if (GetToolTip(element) is { Length: > 0 } tooltip)
            {
                ToolTipService.SetToolTip(element, L.Get(tooltip));
                applied++;
            }

            if (GetName(element) is { Length: > 0 } name)
            {
                AutomationProperties.SetName(element, L.Get(name));
                applied++;
            }

            if (GetProperty(element) is { Length: > 0 } property)
            {
                AssignProperty(element, property);
                applied++;
            }

            return applied;
        }

        private static void AssignText(DependencyObject target, string value)
        {
            switch (target)
            {
                case TextBlock text:
                    text.Text = value;
                    break;
                case TextBox box:
                    box.Header = value;
                    break;
            }
        }

        private static void AssignContent(DependencyObject target, string value)
        {
            // NavigationViewItem / ToolTip 都是 ContentControl 的子类，一处即可
            if (target is ContentControl control)
            {
                control.Content = value;
            }
        }

        /// <summary>值形如 <c>属性名:资源键</c>，反射写入字符串型属性。</summary>
        private static void AssignProperty(DependencyObject target, string value)
        {
            int split = value.IndexOf(':');
            if (split <= 0 || split == value.Length - 1)
            {
                return;
            }

            string propertyName = value[..split];
            string key = value[(split + 1)..];

            System.Reflection.PropertyInfo? property = target.GetType().GetProperty(propertyName);
            if (property is { CanWrite: true } && property.PropertyType == typeof(string))
            {
                property.SetValue(target, L.Get(key));
            }
        }

        /// <summary>
        /// 统一的取值与容错：<c>null</c> / 空串表示「这条不参与本地化」，
        /// 控件保持 XAML 里的原样。取不到 key 时由 <see cref="L"/> 返回 key 本身。
        /// </summary>
        private static void Apply(DependencyObject target, DependencyPropertyChangedEventArgs e, Action<DependencyObject, string> assign)
        {
            if (e.NewValue is not string key || key.Length == 0)
            {
                return;
            }

            Track(target);
            assign(target, L.Get(key));
        }

        /// <summary>登记这个元素，换语言时才能找到它。同一元素重复登记只留一条。</summary>
        private static void Track(DependencyObject target)
        {
            lock (Tracked)
            {
                for (int i = Tracked.Count - 1; i >= 0; i--)
                {
                    if (!Tracked[i].TryGetTarget(out DependencyObject? existing))
                    {
                        Tracked.RemoveAt(i);
                        continue;
                    }

                    if (ReferenceEquals(existing, target))
                    {
                        return;
                    }
                }

                Tracked.Add(new WeakReference<DependencyObject>(target));
            }
        }
    }
}
