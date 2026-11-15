using System;
using AstraBrew_Launcher.Views.Controls;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 切换界面语言并让它**实时生效**。
    ///
    /// 做法：换掉资源上下文的语言限定符（<see cref="L.SetLanguage"/>）→ 把界面上所有
    /// <c>loc:Loc.*</c> 绑过的元素按新语言重刷（<see cref="Loc.RefreshAll"/>）→
    /// 通知各页面刷新自己用代码赋值的文案（<see cref="LanguageChanged"/>）。
    /// 窗口、页面实例、运行中的酒馆全都原地不动，没有「关掉再开一个」的观感。
    ///
    /// 早先的实现是「新建窗口 + 关旧窗口」，因为当时用 <c>x:Uid</c>、元素建好后无法重新解析。
    /// 现在文案走自建的 <see cref="Loc"/>，可以直接重刷，那条路只在资源上下文切不动时兜底。
    /// </summary>
    public static class LanguageSwitch
    {
        /// <summary>语言已换好、文案已重刷。页面在这里刷新自己用代码赋值的文案。</summary>
        public static event EventHandler? LanguageChanged;

        /// <summary>正在重建窗口（仅兜底路径）。主窗口关窗清理逻辑据此跳过「停酒馆 / 关桌面窗口」。</summary>
        public static bool IsRebuilding { get; private set; }

        /// <summary>设置页的语言选择器只调这一个方法。</summary>
        public static void Apply(AppLanguage choice)
        {
            if (LauncherSettings.Language == choice)
            {
                return;
            }

            string tag = LanguageResolver.Resolve(choice);

            // 「跟随系统」解析出来可能就是当前语言：只写设置，不必刷界面。
            if (tag == L.CurrentTag)
            {
                LauncherSettings.SetLanguage(choice);
                return;
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();

            if (!L.SetLanguage(tag))
            {
                // 资源上下文换不动（极少见）：设置照存，退回重建窗口那条老路让新语言生效
                LauncherSettings.SetLanguage(choice);
                Rebuild(tag);
                return;
            }

            LauncherSettings.SetLanguage(choice);

            // ① XAML 侧：登记过的元素逐个重取
            int refreshed = Loc.RefreshAll();

            // ② 代码侧：页面自己赋值的文案（SettingRow.Title、实例胶囊提示等）
            App.MainWindow?.RefreshLocalizedText();
            LanguageChanged?.Invoke(null, EventArgs.Empty);

            // DEBUG 落一行到 i18n.log：切换耗时与刷到的元素数，出问题时先看这两个数
            Probe($"language → {tag} in {watch.ElapsedMilliseconds} ms, refreshed {refreshed} elements");
        }

        [System.Diagnostics.Conditional("DEBUG")]
        private static void Probe(string message)
        {
            try
            {
                string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "astrabrew-launcher");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(dir, "i18n.log"),
                    $"[{DateTimeOffset.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
            catch (Exception)
            {
                // 探针失败不影响功能
            }
        }

        /// <summary>
        /// 兜底：资源上下文切不动时重建窗口。
        /// 正常路径永远不会走到这里——用户看到的是原地换字，不是窗口闪一下。
        /// </summary>
        private static void Rebuild(string tag)
        {
            MainWindow? previous = App.MainWindow;

            // 引导层盖着的时候重建会把步骤状态一起丢掉，退化成「重启生效」
            if (previous is null || previous.IsOnboardingVisible)
            {
                AppNotify.Info(
                    L.Get("Notify.Language.Restart.Title"),
                    L.Get("Notify.Language.Restart.Body"));
                return;
            }

            string current = previous.CurrentTag;
            previous.PersistGeometryForReload();

            IsRebuilding = true;
            try
            {
                var next = new MainWindow();
                App.SetMainWindow(next);
                next.Activate();
                next.NavigateTo(current, force: true);
            }
            catch (Exception ex)
            {
                IsRebuilding = false;
                AppNotify.Warning(
                    L.Get("Notify.Language.Failed.Title"),
                    L.Get("Notify.Language.Failed.Body") + $" ({ex.GetType().Name})");
                return;
            }

            previous.DetachContent();
            previous.Close();
            IsRebuilding = false;
        }
    }
}
