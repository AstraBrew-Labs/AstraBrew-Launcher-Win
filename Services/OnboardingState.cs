using System;
using System.IO;

namespace AstraBrew_Launcher.Services
{
    /// <summary>引导的结束方式，仅用于记录。</summary>
    public enum OnboardingResult
    {
        /// <summary>走完全部三步。</summary>
        Completed,

        /// <summary>中途主动点击「跳过引导」。</summary>
        Skipped,

        /// <summary>第 1 步选择「用过」，直接结束。</summary>
        Experienced,
    }

    /// <summary>
    /// 「已完成引导」标记的持久化（原型实现）。
    ///
    /// 存储位置：%LOCALAPPDATA%\AstraBrew Launcher\onboarding.state
    /// 写入时机只有三处，都在 <c>OnboardingView.Finish</c> 里：
    /// 1. 第 3 步点「完成」；
    /// 2. 任意一步点「跳过引导」；
    /// 3. 第 1 步选择「用过」后点「下一步」。
    ///
    /// 用纯文本 key=value 而非 JSON，避免 Release 下 PublishTrimmed 反射序列化的坑。
    /// </summary>
    public static class OnboardingState
    {
        private const string CompletedKey = "completed";

        private static string StateDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AstraBrew Launcher");

        /// <summary>标记文件路径，设置页会展示给用户，方便原型阶段重置。</summary>
        public static string FilePath => Path.Combine(StateDirectory, "onboarding.state");

        /// <summary>是否已完成引导（文件缺失 / 内容异常一律视为未完成）。</summary>
        public static bool IsCompleted
        {
            get
            {
                try
                {
                    if (!File.Exists(FilePath))
                    {
                        return false;
                    }

                    foreach (var line in File.ReadAllLines(FilePath))
                    {
                        var parts = line.Split('=', 2);
                        if (parts.Length == 2
                            && parts[0].Trim().Equals(CompletedKey, StringComparison.OrdinalIgnoreCase))
                        {
                            return parts[1].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                        }
                    }
                }
                catch (IOException)
                {
                    // 读不到就当没完成，下次启动再引导一次，不影响使用。
                }
                catch (UnauthorizedAccessException)
                {
                }

                return false;
            }
        }

        /// <summary>写入「已完成引导」标记。失败不抛出，避免引导流程卡死。</summary>
        public static void MarkCompleted(OnboardingResult result)
        {
            try
            {
                Directory.CreateDirectory(StateDirectory);

                var lines = new[]
                {
                    $"{CompletedKey}=true",
                    $"reason={result.ToString().ToLowerInvariant()}",
                    $"completedAt={DateTimeOffset.Now:yyyy-MM-ddTHH:mm:sszzz}",
                };
                File.WriteAllLines(FilePath, lines);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        /// <summary>清除标记，让引导可以重新演示。</summary>
        public static void Reset()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    File.Delete(FilePath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
