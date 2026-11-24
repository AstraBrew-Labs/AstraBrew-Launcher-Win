using System;
using System.Collections.Generic;

namespace AstraBrew_Launcher.Services
{
    /// <summary>日志页的两个页签：启动器、酒馆。</summary>
    public enum LogArea
    {
        Launcher,
        Tavern,

        /// <summary>PM2 控制面：命令行、退出码、jlist 状态。它与酒馆输出分开，排错时才不会互相淹没。</summary>
        Pm2,
    }

    /// <summary>
    /// 日志的来源。安装日志（<see cref="InstallLog"/> 产出的 <c>logs\*.log</c>）只读不写，
    /// 也不参与清理；运行日志才是本功能新增的那一份。
    /// </summary>
    public enum LogOriginKind
    {
        Runtime,
        Install,
    }

    /// <summary>
    /// 日志级别。前四种给启动器用，后两种来自酒馆输出。
    ///
    /// 两边的原始标签并不一致（酒馆有 SYS / OK / OUT，安装日志是 INFO / WARN / FAIL），
    /// 读入时统一映射到这里，页面才能用同一套筛选条件。
    /// </summary>
    public enum LogLevel
    {
        System,
        Info,
        Success,
        Warning,
        Error,
        Output,
    }

    /// <summary>一行日志。时间已带日期，跨文件也能正确排序。</summary>
    public sealed record LogLine(
        DateTime Time,
        LogArea Area,
        LogOriginKind Origin,
        LogLevel Level,
        string Text,
        string FilePath)
    {
        /// <summary>
        /// 这行要不要同时进控制台存档。
        ///
        /// 从 PM2 补全的历史输出只进日志页：控制台是「本次会话」的视图，由存档负责，
        /// 补进来的历史再灌一遍，重开启动器就会看到成倍的重复行。
        /// </summary>
        public bool MirrorToConsole { get; init; } = true;

        /// <summary>落盘与界面共用的 5 字符标签（对齐用，右侧补空格）。</summary>
        public string LevelTag => Level switch
        {
            LogLevel.System => "SYS",
            LogLevel.Success => "OK",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERROR",
            LogLevel.Output => "OUT",
            _ => "INFO",
        };

        /// <summary>界面上显示的日期时间（跨天时不能只显示时分秒）。</summary>
        public string TimeText => Time.ToString("MM-dd HH:mm:ss");

        /// <summary>文件名，给行的 ToolTip 用。</summary>
        public string FileName => System.IO.Path.GetFileName(FilePath);
    }

    /// <summary>一次读取请求。级别集合为空视为「全部」。</summary>
    public sealed record LogQuery(
        LogArea Area,
        DateTime? From,
        DateTime? To,
        IReadOnlySet<LogLevel> Levels,
        bool IncludeRuntime,
        bool IncludeInstall)
    {
        /// <summary>
        /// 只看这一份安装日志（操作记录里点「下载了酒馆」时用）。
        /// 设了它就不再按前缀 / 日期枚举，直接读这份文件——用户点的是「那一次」的日志。
        /// </summary>
        public string? ExactFile { get; init; }

        /// <summary>某个级别是否被选中。空集合 = 不筛级别。</summary>
        public bool Accepts(LogLevel level) => Levels.Count == 0 || Levels.Contains(level);
    }
}
