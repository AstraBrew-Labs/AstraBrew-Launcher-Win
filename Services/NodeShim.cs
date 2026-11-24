using System;
using System.IO;
using System.Text;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 给酒馆进程注入的一小段 node 预加载脚本：把 <c>child_process</c> 的所有启动入口
    /// 默认补上 <c>windowsHide: true</c>。
    ///
    /// 为什么需要它：酒馆每次启动都会在 <c>preSetupTasks</c> 里跑
    /// <c>commandExistsSync('git')</c>，而 <c>command-exists</c> 用的是
    /// <c>execSync('where git', { stdio: [] })</c> —— 没设 <c>windowsHide</c>。
    /// 父进程（PM2 用 CREATE_NO_WINDOW 拉起）本身没有控制台，Windows 只能给这个
    /// <c>cmd.exe</c> 新分配一个，于是「黑框一闪」。直连模式下同样会闪，只是以前没注意。
    ///
    /// 为什么打补丁而不是改酒馆源码：酒馆一更新就覆盖，改了等于白改；
    /// 也不去改 <c>node_modules</c> —— 重装依赖同样会丢。
    ///
    /// 三条安全约定：
    /// · 脚本整段包在 <c>try/catch</c> 里：补丁自己出错也绝不能拖垮酒馆；
    /// · 文件写不出来就**不注入** `--require`（宁可闪一下，不能让酒馆起不来）；
    /// · 内容变了才重写，升级后自动更新，平时不碰磁盘。
    /// </summary>
    public static class NodeShim
    {
        private const string FileName = "winhide.cjs";

        /// <summary>脚本落点：<c>%AppData%\AstraBrew Launcher\data\winhide.cjs</c>。</summary>
        public static string FilePath { get; } = Path.Combine(AppPaths.RoamingRoot, "data", FileName);

        /// <summary>写成功过的路径；null 表示这次进程内写不出来，别再反复试。</summary>
        private static string? _ready;

        private static bool _attempted;

        /// <summary>
        /// 确保脚本已落盘，返回可用路径；失败返回 <c>null</c>（调用方据此不注入）。
        /// </summary>
        public static string? Ensure()
        {
            if (_ready is not null)
            {
                return _ready;
            }

            if (_attempted)
            {
                return null;
            }

            _attempted = true;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

                // 只在内容不同时重写：每次启动都写一遍会和杀毒软件打架，也没必要
                if (!File.Exists(FilePath)
                    || !string.Equals(File.ReadAllText(FilePath, Encoding.UTF8), Script, StringComparison.Ordinal))
                {
                    File.WriteAllText(FilePath, Script, new UTF8Encoding(false));
                }

                _ready = FilePath;
                return _ready;
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn($"node shim unavailable: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 拼进 NODE_OPTIONS 的 <c>--require</c> 片段。脚本不可用时返回 <c>null</c>。
        ///
        /// **路径必须用正斜杠**：NODE_OPTIONS 是由 node 自己再解析一遍的字符串，
        /// Windows 反斜杠在这里会被吃掉（实测变成 <c>C:UsersY_ALAAppData...</c>，
        /// node 直接报 Cannot find module）。空格用引号包住。
        /// </summary>
        public static string? RequireOption()
        {
            string? path = Ensure();

            return path is null ? null : $"--require=\"{path.Replace('\\', '/')}\"";
        }

        /// <summary>
        /// 预加载脚本本体。保持极简：只做一件事，出错静默。
        ///
        /// 参数定位逻辑：跳过第 0 个（命令/文件），找第一个「非数组、非 Buffer、非函数」的对象当 options；
        /// 找不到就在回调之前插一个。这样 <c>exec(cmd, cb)</c> 与 <c>spawn(cmd, args, opts)</c>
        /// 两种形态都能覆盖。
        /// </summary>
        private const string Script = """
            // AstraBrew Launcher · 隐藏酒馆子进程的控制台窗口
            // 通过 NODE_OPTIONS=--require 注入。补丁自身出错绝不影响宿主进程。
            try {
              var child = require('child_process');
              var names = ['spawn', 'spawnSync', 'exec', 'execSync', 'execFile', 'execFileSync'];

              for (var i = 0; i < names.length; i++) {
                (function (name) {
                  var original = child[name];
                  if (typeof original !== 'function') return;

                  child[name] = function () {
                    var args = Array.prototype.slice.call(arguments);
                    var index = -1;

                    for (var j = 1; j < args.length; j++) {
                      var value = args[j];
                      if (value && typeof value === 'object' && !Array.isArray(value) && !Buffer.isBuffer(value)) {
                        index = j;
                        break;
                      }
                    }

                    if (index >= 0) {
                      var merged = {};
                      for (var key in args[index]) merged[key] = args[index][key];
                      merged.windowsHide = true;
                      args[index] = merged;
                    } else {
                      var last = args[args.length - 1];
                      var at = typeof last === 'function' ? args.length - 1 : args.length;
                      args.splice(at, 0, { windowsHide: true });
                    }

                    return original.apply(this, args);
                  };
                })(names[i]);
              }
            } catch (e) {
              // 静默：补丁失败只意味着窗口可能闪一下，不影响功能
            }
            """;
    }
}
