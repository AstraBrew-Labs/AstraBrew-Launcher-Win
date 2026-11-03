using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 启动器设置的落盘层：<c>%AppData%\AstraBrew Launcher\settings.toml</c>。
    ///
    /// 职责边界：
    ///   · 只有这里碰文件；<see cref="LauncherSettings"/> 是内存里的唯一状态，
    ///     页面改状态、这里监听变更并写回，页面不直接读写配置。
    ///   · 值一律以「人能读懂的字符串」存（theme = "dark"、mode = "desktop"），
    ///     不存枚举数字 —— 枚举顺序一变，配置文件就全错位了。
    ///   · 写盘走「先写 .tmp 再改名」，避免掉电/崩溃留下半截文件。
    ///   · 变更后 400ms 防抖：拖缩放滑杆时每 5% 都会触发一次通知，
    ///     即时落盘等于一秒内重写几十次文件。
    ///
    /// 读文件容错原则：任何一个键读不懂都只影响它自己，其余照常生效；
    /// 整份文件坏掉时按默认值启动，绝不让设置文件挡住启动。
    /// </summary>
    public static class SettingsStore
    {
        /// <summary>配置文件结构版本；将来字段有破坏性调整时用它做迁移。</summary>
        public const int SchemaVersion = 1;

        /// <summary>设置文件完整路径（界面上要显示时直接用它，不要显示 %AppData%）。</summary>
        public static string FilePath { get; } = Path.Combine(AppPaths.RoamingRoot, "settings.toml");

        /// <summary>一次写盘成功。</summary>
        public static event EventHandler? Saved;

        private const int SaveDelay = 400;

        private static readonly UTF8Encoding Utf8 = new(false);
        private static readonly object Gate = new();

        private static Timer? _timer;
        private static string? _pending;
        private static bool _attached;

        // ==================== 枚举 ↔ 文本 ====================
        // 顺序即设置页里分段选择器的顺序，改顺序会同时改掉旧配置的含义，别动。

        private static readonly (AppTheme Value, string Id)[] Themes =
        {
            (AppTheme.System, "system"),
            (AppTheme.Light, "light"),
            (AppTheme.Dark, "dark"),
        };

        private static readonly (AppLanguage Value, string Id)[] Languages =
        {
            (AppLanguage.System, "system"),
            (AppLanguage.SimplifiedChinese, "zh-CN"),
            (AppLanguage.English, "en-US"),
        };

        private static readonly (CpuCorePreference Value, string Id)[] CpuCores =
        {
            (CpuCorePreference.Auto, "auto"),
            (CpuCorePreference.Half, "half"),
            (CpuCorePreference.All, "all"),
        };

        private static readonly (QuickStartMode Value, string Id)[] StartModes =
        {
            (QuickStartMode.Normal, "browser"),
            (QuickStartMode.Desktop, "desktop"),
            (QuickStartMode.Server, "server"),
        };

        private static readonly (ServiceScopeSetting Value, string Id)[] ServiceScopes =
        {
            (ServiceScopeSetting.Lan, "lan"),
            (ServiceScopeSetting.Internet, "internet"),
        };

        private static readonly (DataLocationMode Value, string Id)[] DataLocations =
        {
            (DataLocationMode.Global, "global"),
            (DataLocationMode.CurrentInstance, "instance"),
        };

        private static readonly (RuntimeSource Value, string Id)[] RuntimeSources =
        {
            (RuntimeSource.Builtin, "builtin"),
            (RuntimeSource.System, "system"),
        };

        private static readonly (NpmRegistrySource Value, string Id)[] NpmRegistries =
        {
            (NpmRegistrySource.Npmmirror, "npmmirror"),
            (NpmRegistrySource.HuaweiCloud, "huawei"),
            (NpmRegistrySource.Tencent, "tencent"),
            (NpmRegistrySource.Official, "official"),
        };

        private static readonly (DownloadChannel Value, string Id)[] Channels =
        {
            (DownloadChannel.Gitee, "gitee"),
            (DownloadChannel.GitCode, "gitcode"),
            (DownloadChannel.Github, "github"),
        };

        private static readonly (ProxyModeSetting Value, string Id)[] Proxies =
        {
            (ProxyModeSetting.None, "none"),
            (ProxyModeSetting.System, "system"),
            (ProxyModeSetting.Custom, "custom"),
        };

        /// <summary>设置页版本。顺序即分段切换器里「小白 → 高手」的顺序。</summary>
        private static readonly (SettingsLevel Value, string Id)[] Levels =
        {
            (SettingsLevel.Simple, "simple"),
            (SettingsLevel.Advanced, "advanced"),
        };

        // ==================== 读 ====================

        /// <summary>
        /// 启动时调用一次：把 settings.toml 读进 <see cref="LauncherSettings"/>。
        /// 文件不存在时按默认值生成一份，之后监听变更自动写回。
        /// </summary>
        public static void Load()
        {
            if (!File.Exists(FilePath))
            {
                LauncherSettings.Reset();
                Attach();
                Write(Build());
                return;
            }

            TomlDocument document;
            try
            {
                document = TomlDocument.Parse(File.ReadAllText(FilePath, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                // 权限、被别的程序独占、编码坏了——都按「没有配置」处理
                Log(ex);
                document = TomlDocument.Parse(null);
            }

            LauncherSettings.BeginUpdate();
            try
            {
                LauncherSettings.Reset();
                ApplyUi(document);
                ApplyWindow(document);
                ApplyStartup(document);
                ApplyTavern(document);
                ApplyConsole(document);
                ApplyRuntime(document);
                ApplyNetwork(document);
                ApplyPaths(document);
            }
            finally
            {
                LauncherSettings.EndUpdate();
            }

            // 读完再挂监听：否则 EndUpdate 的那一次通知会立刻把文件重写一遍，
            // 把用户手写的注释和未知键抹掉。
            Attach();
        }

        private static void ApplyUi(TomlDocument document)
        {
            if (ParseId(Themes, document.String("ui", "theme")) is { } theme)
            {
                LauncherSettings.SetTheme(theme);
            }

            if (ParseId(Languages, document.String("ui", "language")) is { } language)
            {
                LauncherSettings.SetLanguage(language);
            }

            if (ParseId(Levels, document.String("ui", "level")) is { } level)
            {
                LauncherSettings.SetLevel(level);
            }

            if (document.Number("ui", "scale") is { } scale)
            {
                LauncherSettings.SetUiScale(scale);
            }

            // 字体存的是字体族名，不是下标：换机器或卸载字体后还能判断出「这个字体没了」
            LauncherSettings.SetFontFamily(FontCatalog.FromStorage(document.String("ui", "font")));

            if (document.Bool("ui", "remember_window") is { } remember)
            {
                LauncherSettings.SetRememberWindow(remember);
            }
        }

        private static void ApplyWindow(TomlDocument document)
        {
            var x = document.Int("window", "x");
            var y = document.Int("window", "y");
            var width = document.Int("window", "width");
            var height = document.Int("window", "height");
            var dpi = document.Int("window", "dpi");

            if (x is null || y is null || width is null || height is null || width <= 0 || height <= 0)
            {
                return;
            }

            LauncherSettings.SetWindowRect(new WindowBounds(
                x.Value,
                y.Value,
                width.Value,
                height.Value,
                dpi is > 0 ? dpi.Value : 96));
        }

        private static void ApplyStartup(TomlDocument document)
        {
            if (document.Bool("startup", "auto_launch") is { } autoLaunch)
            {
                LauncherSettings.SetAutoLaunch(autoLaunch);
            }

            if (ParseId(StartModes, document.String("startup", "mode")) is { } mode)
            {
                LauncherSettings.SetStartMode(mode);
            }

            if (document.Bool("startup", "stop_tavern_on_close") is { } stopOnClose)
            {
                LauncherSettings.SetStopTavernOnWindowClose(stopOnClose);
            }

            if (ParseId(CpuCores, document.String("startup", "cpu_cores")) is { } cores)
            {
                LauncherSettings.SetCpuCores(cores);
            }
        }

        private static void ApplyTavern(TomlDocument document)
        {
            if (document.Bool("tavern", "server_mode") is { } serverMode)
            {
                LauncherSettings.SetServerMode(serverMode);
            }

            if (ParseId(ServiceScopes, document.String("tavern", "scope")) is { } scope)
            {
                LauncherSettings.SetServiceScope(scope);
            }

            if (document.Bool("tavern", "allow_background") is { } background)
            {
                LauncherSettings.SetAllowBackground(background);
            }

            if (ParseId(DataLocations, document.String("tavern", "data_location")) is { } location)
            {
                LauncherSettings.SetDataLocation(location);
            }
        }

        private static void ApplyConsole(TomlDocument document)
        {
            if (document.Bool("console", "show_startup_command") is { } show)
            {
                LauncherSettings.SetShowStartupCommand(show);
            }
        }

        private static void ApplyRuntime(TomlDocument document)
        {
            if (ParseId(RuntimeSources, document.String("runtime", "source")) is { } runtime)
            {
                LauncherSettings.SetRuntime(runtime);
            }

            if (ParseId(NpmRegistries, document.String("runtime", "npm_registry")) is { } registry)
            {
                LauncherSettings.SetNpmRegistry(registry);
            }
        }

        private static void ApplyNetwork(TomlDocument document)
        {
            if (ParseId(Channels, document.String("network", "download_channel")) is { } channel)
            {
                LauncherSettings.SetChannel(channel);
            }

            if (document.Bool("network", "github_acceleration") is { } acceleration)
            {
                LauncherSettings.SetGithubAcceleration(acceleration);
            }

            if (ParseId(Proxies, document.String("network", "proxy")) is { } proxy)
            {
                LauncherSettings.SetProxy(proxy);
            }

            // 空字符串是合法值（用户清空了代理地址），只有「键不存在」才回落到默认
            if (document.String("network", "custom_proxy") is { } customProxy)
            {
                LauncherSettings.SetCustomProxy(customProxy);
            }

            if (document.Int("network", "github_best_latency") is { } latency)
            {
                LauncherSettings.SetGithubTestResult(latency);
            }
        }

        private static void ApplyPaths(TomlDocument document)
        {
            if (document.String("paths", "export") is { Length: > 0 } export)
            {
                LauncherSettings.SetExportPath(export);
            }

            if (document.String("paths", "data") is { Length: > 0 } data)
            {
                LauncherSettings.SetGlobalDataPath(data);
            }
        }

        // ==================== 写 ====================

        /// <summary>状态变更 → 防抖落盘。</summary>
        public static void Save()
        {
            string text;
            try
            {
                text = Build();
            }
            catch (Exception ex)
            {
                Log(ex);
                return;
            }

            lock (Gate)
            {
                _pending = text;
                _timer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
                _timer.Change(SaveDelay, Timeout.Infinite);
            }
        }

        /// <summary>
        /// 立刻把当前状态落盘（不等防抖）。退出窗口前必须调一次：
        /// 窗口位置是静默写入的，不会进保存队列，只能靠这里刷出去。
        /// </summary>
        public static void SaveNow()
        {
            try
            {
                Write(Build());
            }
            catch (Exception ex)
            {
                Log(ex);
            }
        }

        /// <summary>
        /// 把排队的待写内容落盘。进程一结束，防抖定时器里那次写入就永远丢了。
        /// </summary>
        public static void Flush()
        {
            string? text;
            lock (Gate)
            {
                text = _pending;
                _pending = null;
            }

            if (text is not null)
            {
                Write(text);
            }
        }

        private static void Attach()
        {
            if (_attached)
            {
                return;
            }

            _attached = true;
            LauncherSettings.Changed += (_, _) => Save();
        }

        private static string Build()
        {
            var writer = new TomlWriter();

            writer.Raw("# ============================================================");
            writer.Raw("#  AstraBrew Launcher · 用户设置");
            writer.Raw($"#  {FilePath}");
            writer.Raw("#");
            writer.Raw("#  由启动器维护：在「设置」页改动后自动写回本文件。");
            writer.Raw("#  可以手工编辑，下次启动时读取；删掉的键会回落到默认值。");
            writer.Raw("#  无法识别的行会被忽略，不会导致启动失败。");
            writer.Raw("# ============================================================");
            writer.Blank();
            writer.Int("schema_version", SchemaVersion, "配置结构版本");

            writer.Section("ui");
            writer.String("theme", IdOf(Themes, LauncherSettings.Theme), "system | light | dark");
            writer.Double("scale", LauncherSettings.UiScale, "0.90 - 1.40");
            writer.String("font", FontCatalog.ToStorage(LauncherSettings.FontFamily), "embedded（软件默认）| system | 字体族名");
            writer.String("language", IdOf(Languages, LauncherSettings.Language), "system | zh-CN | en-US");
            writer.String("level", IdOf(Levels, LauncherSettings.Level), "simple（小白版）| advanced（高手版）");
            writer.Bool("remember_window", LauncherSettings.RememberWindow);

            writer.Blank();
            if (LauncherSettings.WindowRect is { } bounds)
            {
                writer.Raw("# 上次退出时的窗口几何（物理像素，dpi = 96 即 100%）；");
                writer.Raw("# remember_window = false 时不读取这段。");
                writer.Section("window");
                writer.Int("x", bounds.X);
                writer.Int("y", bounds.Y);
                writer.Int("width", bounds.Width);
                writer.Int("height", bounds.Height);
                writer.Int("dpi", bounds.Dpi);
            }
            else
            {
                writer.Raw("# [window] 尚未记录：勾选「记住窗口位置」后正常退出一次即写入");
            }

            writer.Section("startup");
            writer.Bool("auto_launch", LauncherSettings.AutoLaunch, "登录 Windows 后自动启动");
            writer.String("mode", IdOf(StartModes, LauncherSettings.StartMode), "browser | desktop");
            writer.Bool("stop_tavern_on_close", LauncherSettings.StopTavernOnWindowClose);
            writer.String("cpu_cores", IdOf(CpuCores, LauncherSettings.CpuCores), "auto | half | all");

            writer.Section("tavern");
            writer.Bool("server_mode", LauncherSettings.ServerMode);
            writer.String("scope", IdOf(ServiceScopes, LauncherSettings.ServiceScope), "lan | internet");
            writer.Bool("allow_background", LauncherSettings.AllowBackground);
            writer.String(
                "data_location",
                IdOf(DataLocations, LauncherSettings.DataLocation),
                "global | instance");

            writer.Section("console");
            writer.Bool("show_startup_command", LauncherSettings.ShowStartupCommand);

            writer.Section("runtime");
            writer.String("source", IdOf(RuntimeSources, LauncherSettings.Runtime), "builtin | system");
            writer.String(
                "npm_registry",
                IdOf(NpmRegistries, LauncherSettings.NpmRegistry),
                "npmmirror | huawei | tencent | official");

            writer.Section("network");
            writer.String("download_channel", IdOf(Channels, LauncherSettings.Channel), "gitee | gitcode | github");
            writer.Bool("github_acceleration", LauncherSettings.GithubAcceleration, "所有 GitHub 链接是否走 gh-proxy.org 加速（同时写入全局 git insteadOf 规则）");

            if (LauncherSettings.GithubBestLatency is { } latency)
            {
                writer.Int("github_best_latency", latency, "毫秒");
            }

            writer.String("proxy", IdOf(Proxies, LauncherSettings.Proxy), "none | system | custom");
            writer.String("custom_proxy", LauncherSettings.CustomProxy);

            writer.Section("paths");
            writer.String("export", LauncherSettings.ExportPath, "资源导出目录（可用环境变量）");
            writer.String("data", LauncherSettings.GlobalDataPath, "全局数据目录（可用环境变量）");

            return writer.ToString();
        }

        private static void Write(string text)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.RoamingRoot);
                var temporary = FilePath + ".tmp";
                File.WriteAllText(temporary, text, Utf8);
                File.Move(temporary, FilePath, true);
                Saved?.Invoke(null, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                // 写不进去只降级：设置页会停在「保存中…」，但不该弹错误、更不该崩
                Log(ex);
            }
        }

        // ==================== 工具 ====================

        private static string IdOf<T>(IReadOnlyList<(T Value, string Id)> map, T value)
        {
            for (var i = 0; i < map.Count; i++)
            {
                if (EqualityComparer<T>.Default.Equals(map[i].Value, value))
                {
                    return map[i].Id;
                }
            }

            return map[0].Id;
        }

        private static T? ParseId<T>(IReadOnlyList<(T Value, string Id)> map, string? id)
            where T : struct
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            var trimmed = id.Trim();
            for (var i = 0; i < map.Count; i++)
            {
                if (string.Equals(map[i].Id, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return map[i].Value;
                }
            }

            return null;
        }

        /// <summary>落一份诊断日志到 %Temp%\astrabrew-launcher\settings.log（尽力而为）。</summary>
        private static void Log(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.TempRoot);
                File.AppendAllText(
                    Path.Combine(AppPaths.TempRoot, "settings.log"),
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
            }
            catch
            {
                // 日志写不进去也不能影响设置逻辑
            }
        }
    }
}
