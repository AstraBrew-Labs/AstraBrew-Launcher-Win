using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 把「外观主题 / 界面字体 / 界面缩放」三项设置落到当前窗口上。
    ///
    /// 三种手段各不相同，原因如下：
    ///
    /// 1. 主题 —— 给内容根元素设 <see cref="FrameworkElement.RequestedTheme"/>。
    ///    Application.RequestedTheme 只能在启动前设一次，运行期切换必须走元素级。
    ///    主题变了要顺带回调一次，让标题栏的窗口按钮配色跟着换（见 MainWindow）。
    ///
    /// 2. 字体 —— 替换 <c>ContentControlThemeFontFamily</c>。
    ///    控件和 TextBlock 的默认 FontFamily 都取自这个主题资源，改它等于全局换字，
    ///    不需要逐个控件去写（也会漏掉代码里 new 出来的控件）。
    ///    坑在于：改 Application.Resources 不会刷新已经解析过的 ThemeResource，
    ///    框架只在「主题变化」时重走资源查找，所以要把 RequestedTheme 拨到反主题再拨回来。
    ///    两次赋值在同一个调用栈里完成，中间不会插入渲染帧，看不到闪烁。
    ///
    /// 3. 缩放 —— 内容层套 ScaleTransform + 尺寸补偿。
    ///    WinUI 没有整窗缩放的 API；把内容按 1/scale 的逻辑尺寸排版、再乘回去，
    ///    视觉上等于整体放大，滚动区域与自适应布局仍按逻辑像素工作。
    ///    标题栏不参与缩放：它和系统绘制的窗口按钮挨着，缩了会错位。
    ///
    /// 所有失败只降级不抛：外观出问题不该让界面起不来。
    /// </summary>
    internal sealed class AppearanceController : IDisposable
    {
        /// <summary>控件默认字体的主题资源键。</summary>
        private const string FontFamilyKey = "ContentControlThemeFontFamily";

        private readonly FrameworkElement _root;
        private readonly FrameworkElement _scaleHost;
        private readonly FrameworkElement _scaleContent;
        private readonly ScaleTransform _scaleTransform = new();
        private readonly Action _afterThemeApplied;

        private AppTheme _theme;
        private string? _font;
        private double _scale;

        /// <summary>框架原始字体族（XamlAutoFontFamily），「系统默认」时要还原成它。</summary>
        private readonly FontFamily? _defaultFont;

        /// <summary>当前目标字体，树加载完成后按它铺一遍。</summary>
        private FontFamily? _targetFont;

        private bool _disposed;

        /// <param name="root">承载主题的内容根（窗口最外层元素）。</param>
        /// <param name="scaleHost">提供可用区域的容器，尺寸变化时重新排布缩放内容。</param>
        /// <param name="scaleContent">被缩放的内容，其宽高会按 1/scale 反向补偿。</param>
        /// <param name="afterThemeApplied">主题应用后的回调，用于刷新窗口按钮配色。</param>
        public AppearanceController(
            FrameworkElement root,
            FrameworkElement scaleHost,
            FrameworkElement scaleContent,
            Action afterThemeApplied)
        {
            _root = root;
            _scaleHost = scaleHost;
            _scaleContent = scaleContent;
            _afterThemeApplied = afterThemeApplied;

            // 必须在任何覆盖之前取：这是「系统默认」的还原目标。
            // 取不到时给一份框架同款回退列表，否则「切回系统默认」会因为 target 为 null 而不刷树，
            // 已经改成别的字体的控件就再也变不回来了。
            _defaultFont = ReadDefaultFont() ?? new FontFamily("Segoe UI Variable Text, Segoe UI");

            _scaleContent.RenderTransformOrigin = new Point(0, 0);
            _scaleContent.RenderTransform = _scaleTransform;
            _scaleHost.SizeChanged += OnScaleHostSizeChanged;

            // 构造函数里树还没加载完，第一次铺字体必然扑空；加载完成后再补一次
            _root.Loaded += OnRootLoaded;

            LauncherSettings.Changed += OnSettingsChanged;

            ApplyTheme(LauncherSettings.Theme);
            ApplyFont(LauncherSettings.FontFamily);
            ApplyScale(LauncherSettings.UiScale);
        }

        private void OnRootLoaded(object sender, RoutedEventArgs e) => ApplyFontToTree();

        // ==================== 主题 ====================

        private void ApplyTheme(AppTheme theme)
        {
            _theme = theme;

            var element = theme switch
            {
                AppTheme.Light => ElementTheme.Light,
                AppTheme.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };

            try
            {
                if (_root.RequestedTheme != element)
                {
                    _root.RequestedTheme = element;
                }

                _afterThemeApplied();
            }
            catch (Exception ex)
            {
                LogOnce(ex);
            }
        }

        // ==================== 字体 ====================

        /// <summary>
        /// 换界面字体。
        ///
        /// 两条腿走路，缺一条就有一半界面不跟着变（都是实测踩出来的）：
        ///
        /// 1. 改 <c>ThemeDictionaries</c> 里的 <c>ContentControlThemeFontFamily</c> ——
        ///    管「之后新建」的元素。必须写进主题字典而不是 Application.Resources 顶层：
        ///    框架把该键定义在 generic.xaml 的主题字典里，主题字典优先级高于普通条目，
        ///    写在顶层等于被它盖掉。
        /// 2. 遍历现有可视化树，逐个设置 FontFamily —— 管「已经建好」的元素。
        ///    ThemeResource 不会因为字典内容变了就回刷，标题栏那几个 TextBlock
        ///    在改字体时早就解析完了（诊断日志里它们一直停在 Segoe UI）。
        ///
        /// 不动的两类：图标字体（Fluent Icons / MDL2）和 MonoTextStyle 钉死的 Consolas。
        /// </summary>
        private void ApplyFont(string? family)
        {
            _font = family;

            try
            {
                // 三种来源：内置字体 / 本机字体 / 跟随系统。
                // 配置里写了个本机没有的字体时，Resolve 已经把结论降级成内置字体了。
                var choice = FontCatalog.Resolve(family);
                _targetFont = choice.Kind switch
                {
                    FontChoiceKind.Embedded => FontCatalog.CreateEmbeddedFont(),
                    FontChoiceKind.SystemFamily when choice.Family is { Length: > 0 } name => new FontFamily(name),
                    _ => _defaultFont,
                };

                // ① 主题字典：影响之后新建的元素
                foreach (var entry in Application.Current.Resources.ThemeDictionaries)
                {
                    if (entry.Value is not ResourceDictionary dictionary)
                    {
                        continue;
                    }

                    if (_targetFont is null)
                    {
                        // 移除即回落到框架自带的 XamlAutoFontFamily（系统 UI 字体）
                        dictionary.Remove(FontFamilyKey);
                    }
                    else
                    {
                        dictionary[FontFamilyKey] = _targetFont;
                    }
                }

                // ② 现有元素：资源变更不回刷，只能自己覆盖
                ApplyFontToTree();
            }
            catch (Exception ex)
            {
                LogOnce(ex);
            }
        }

        /// <summary>把当前目标字体铺到整棵树上（仅 UI 线程、仅在根元素已加载后有效）。</summary>
        private void ApplyFontToTree()
        {
            if (_targetFont is null || !_root.IsLoaded)
            {
                // 还没上树：等 Loaded 再来一次，构造函数里第一次调用必然走到这里
                return;
            }

            try
            {
                ApplyFontToNode(_root, _targetFont);

                foreach (var node in Walk(_root))
                {
                    ApplyFontToNode(node, _targetFont);
                }
            }
            catch (Exception ex)
            {
                LogOnce(ex);
            }
        }

        private static void ApplyFontToNode(DependencyObject node, FontFamily target)
        {
            switch (node)
            {
                // 下拉项可能有自己的 FontFamily（字体选择器用每项自己的字做预览），
                // 这里跳过，让它们从 ComboBox 继承或在本地保留预览字体。
                case ComboBoxItem:
                    break;

                case TextBlock text when ShouldReplace(text.FontFamily):
                    text.FontFamily = target;
                    break;

                // Control 覆盖按钮 / 输入框 / 下拉框等；FontIcon 属于 IconElement 不在此列，图标不受影响
                case Control control when ShouldReplace(control.FontFamily):
                    control.FontFamily = target;
                    break;
            }
        }

        /// <summary>图标字体与等宽钉死的元素保持原样，其余一律换成目标字体。</summary>
        private static bool ShouldReplace(FontFamily? current)
        {
            var source = current?.Source;
            if (string.IsNullOrEmpty(source))
            {
                return true;
            }

            return !source.Contains("Icons", StringComparison.OrdinalIgnoreCase)
                && !source.Contains("MDL2", StringComparison.OrdinalIgnoreCase)
                && !source.Contains("Symbol", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(source, "Consolas", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>从框架主题字典里取原始字体族（通常是 XamlAutoFontFamily），用于「系统默认」时还原。</summary>
        private static FontFamily? ReadDefaultFont()
        {
            foreach (var entry in Application.Current.Resources.ThemeDictionaries)
            {
                if (entry.Value is ResourceDictionary dictionary
                    && dictionary.TryGetValue(FontFamilyKey, out var value)
                    && value is FontFamily family)
                {
                    return family;
                }
            }

            return null;
        }

        // ==================== 缩放 ====================

        private void ApplyScale(double scale)
        {
            _scale = scale;

            try
            {
                _scaleTransform.ScaleX = scale;
                _scaleTransform.ScaleY = scale;
                RelayoutScaledContent();
            }
            catch (Exception ex)
            {
                LogOnce(ex);
            }
        }

        /// <summary>
        /// RenderTransform 不参与布局，所以必须自己把内容尺寸算成「容器尺寸 ÷ 缩放」，
        /// 否则放大后右下角会被裁掉、滚动条也滚不到底。
        /// </summary>
        private void RelayoutScaledContent()
        {
            if (_scale <= 0)
            {
                return;
            }

            var width = _scaleHost.ActualWidth / _scale;
            var height = _scaleHost.ActualHeight / _scale;

            if (double.IsNaN(width) || double.IsNaN(height) || width < 0 || height < 0)
            {
                return;
            }

            if (Math.Abs(_scaleContent.Width - width) > 0.5 || double.IsNaN(_scaleContent.Width))
            {
                _scaleContent.Width = width;
            }

            if (Math.Abs(_scaleContent.Height - height) > 0.5 || double.IsNaN(_scaleContent.Height))
            {
                _scaleContent.Height = height;
            }
        }

        private void OnScaleHostSizeChanged(object sender, SizeChangedEventArgs e) => RelayoutScaledContent();

        // ==================== 变更分发 ====================

        private void OnSettingsChanged(object? sender, EventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            if (_theme != LauncherSettings.Theme)
            {
                ApplyTheme(LauncherSettings.Theme);
            }

            if (!string.Equals(_font, LauncherSettings.FontFamily, StringComparison.OrdinalIgnoreCase))
            {
                ApplyFont(LauncherSettings.FontFamily);
            }

            if (Math.Abs(_scale - LauncherSettings.UiScale) > 0.0001)
            {
                ApplyScale(LauncherSettings.UiScale);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            LauncherSettings.Changed -= OnSettingsChanged;
            _root.Loaded -= OnRootLoaded;
            _scaleHost.SizeChanged -= OnScaleHostSizeChanged;

            if (ReferenceEquals(_scaleContent.RenderTransform, _scaleTransform))
            {
                _scaleContent.RenderTransform = null;
            }
        }

        // ==================== 工具 ====================

        /// <summary>深度优先遍历可视化树。</summary>
        private static System.Collections.Generic.IEnumerable<DependencyObject> Walk(DependencyObject node)
        {
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                yield return child;

                foreach (var nested in Walk(child))
                {
                    yield return nested;
                }
            }
        }

        private static int _logCount;

        private static void LogOnce(Exception ex)
        {
            if (_logCount >= 8)
            {
                return;
            }

            _logCount++;
            try
            {
                Directory.CreateDirectory(AppPaths.TempRoot);
                File.AppendAllText(
                    Path.Combine(AppPaths.TempRoot, "appearance.log"),
                    $"[{DateTimeOffset.Now:HH:mm:ss.fff}] {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
            }
            catch
            {
                // 日志写不进去也不能反过来影响外观逻辑
            }
        }
    }
}
