using System.Runtime.InteropServices;

namespace AstraBrewSetup;

/// <summary>
/// 真正干活的一层：把内嵌的 MSI 落盘，然后用 <c>MsiInstallProduct</c> 在**本进程内**装，
/// 配合 <c>MsiSetExternalUI</c> 拿到 MSI 自己的进度消息。
///
/// 为什么不直接 <c>msiexec /i</c>：
/// 那样 MSI 会在**另一个进程**里跑，进度、动作名都拿不到，界面只能挂一个假的滚动条。
/// 同进程调用能拿到 <c>INSTALLMESSAGE_PROGRESS</c>（真实的百分比）和
/// <c>INSTALLMESSAGE_ACTIONSTART</c>（正在复制文件 / 写注册表 / 建快捷方式），
/// 界面上的进度条才有意义。
///
/// 提权由 app.manifest 的 requireAdministrator 完成，MSI 不会再弹第二次 UAC。
/// </summary>
internal static class MsiEngine
{
    public const int Success = 0;
    public const int RebootRequired = 3010;

    /// <summary>MSI 找不到这个产品（拿旧包去卸新装的版本时会这样）。</summary>
    public const int UnknownProduct = 1605;

    private const uint InstallMessageProgress = 0x0A000000;
    private const uint InstallMessageActionStart = 0x09000000;
    private const uint InstallMessageFatalExit = 0x02000000;
    private const uint InstallMessageError = 0x01000000;

    // CharSet 必须显式写 Unicode：委托默认按 ANSI 解 LPWSTR，
    // 宽字符串会被读成 "1"（第一个字符后就是 0 字节），进度消息全变废纸。
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    private delegate int ExternalUiHandler(IntPtr context, uint messageType, string message);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr MsiSetExternalUI(ExternalUiHandler handler, uint filter, IntPtr context);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern int MsiInstallProduct(string product, string commandLine);

    /// <summary>
    /// 关掉 Windows Installer 自己的界面（等价于 msiexec 的 /qn）。
    ///
    /// 只设 MsiSetExternalUI 是不够的：那只把**我们筛选的消息**转过来，
    /// MSI 仍会自己弹一个「正在安装…」的进度窗口——用户看到的就是向导后面又冒出一个框。
    /// 这两个 API 是配套的：内部 UI 设 NONE，消息全部走外部回调。
    /// </summary>
    [DllImport("msi.dll")]
    private static extern int MsiSetInternalUI(int level, IntPtr hwnd);

    /// <summary>INSTALLUILEVEL_NONE：不显示任何 MSI 自带对话框。</summary>
    private const int InstallUiLevelNone = 2;

    /// <summary>安装进度快照。MSI 的回调跑在安装线程上，界面用定时器来读，不跨线程直接改控件。</summary>
    internal sealed class ProgressState
    {
        private readonly object _gate = new();

        public int Percent { get; private set; }

        /// <summary>本次安装到达过的最高百分比：写日志用，判断「外部 UI 到底有没有收到进度」。</summary>
        public int PeakPercent { get; private set; }

        /// <summary>收到过多少条消息（诊断用：0 说明外部 UI 没接上）。</summary>
        public int MessageCount { get; private set; }

        public string Action { get; private set; } = string.Empty;

        public string? Error { get; private set; }

        public void Reset()
        {
            lock (_gate)
            {
                Percent = 0;
                PeakPercent = 0;
                MessageCount = 0;
                Action = string.Empty;
                Error = null;
            }
        }

        public void CountMessage()
        {
            lock (_gate)
            {
                MessageCount++;
            }
        }

        public void SetProgress(int percent)
        {
            lock (_gate)
            {
                // 进度只涨不跌：MSI 的「回滚」阶段会往回报，界面看着像倒退
                if (percent > Percent)
                {
                    Percent = Math.Min(percent, 100);

                    if (Percent > PeakPercent)
                    {
                        PeakPercent = Percent;
                    }
                }
            }
        }

        public void SetAction(string action)
        {
            lock (_gate)
            {
                Action = action;
            }
        }

        public void SetError(string message)
        {
            lock (_gate)
            {
                Error = message;
            }
        }

        public (int Percent, string Action, string? Error) Snapshot()
        {
            lock (_gate)
            {
                return (Percent, Action, Error);
            }
        }
    }

    /// <summary>MSI 动作名 → 界面上那句人话。没列到的动作原样显示（总比空着强）。</summary>
    private static readonly Dictionary<string, (string Zh, string En)> Actions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["InstallValidate"] = ("正在检查安装条件", "Validating"),
        ["InstallInitialize"] = ("正在准备安装", "Preparing"),
        ["RemoveExistingProducts"] = ("正在移除旧版本", "Removing old version"),
        ["ProcessComponents"] = ("正在登记组件", "Processing components"),
        ["UnpublishFeatures"] = ("正在更新功能清单", "Updating features"),
        ["RemoveRegistryValues"] = ("正在清理旧注册表", "Cleaning registry"),
        ["RemoveShortcuts"] = ("正在清理旧快捷方式", "Cleaning shortcuts"),
        ["RemoveFiles"] = ("正在清理旧文件", "Removing old files"),
        ["InstallFiles"] = ("正在复制程序文件", "Copying files"),
        ["CreateShortcuts"] = ("正在创建快捷方式", "Creating shortcuts"),
        ["WriteRegistryValues"] = ("正在写入注册表", "Writing registry"),
        ["RegisterUser"] = ("正在注册用户信息", "Registering user"),
        ["RegisterProduct"] = ("正在注册产品", "Registering product"),
        ["PublishFeatures"] = ("正在发布功能", "Publishing features"),
        ["PublishProduct"] = ("正在完成注册", "Finalizing registration"),
        ["InstallFinalize"] = ("正在收尾", "Finishing up"),
        ["CostInitialize"] = ("正在计算安装大小", "Calculating size"),
        ["FileCost"] = ("正在计算安装大小", "Calculating size"),
        ["CostFinalize"] = ("正在计算安装大小", "Calculating size"),
        ["FindRelatedProducts"] = ("正在检查已安装版本", "Checking installed versions"),
    };

    /// <summary>
    /// 安装。同步阻塞（调用方放线程池里跑），返回 MSI 的退出码。
    /// </summary>
    public static int Install(
        string msiPath,
        string targetDir,
        bool forAllUsers,
        ProgressState state,
        bool chinese)
    {
        state.Reset();

        // ALLUSERS=1 → 所有人；ALLUSERS=2 + MSIINSTALLPERUSER=1 → 仅当前用户。
        // 两个值必须成对给，只写 ALLUSERS=2 会被当成「所有人」。
        // INSTALLLOCATION 也要给：MSI 里有 SetProperty 把 INSTALLFOLDER 改写成 [INSTALLLOCATION]
        // （那是给「静默升级沿用上次目录」用的），只传 INSTALLFOLDER 会被它盖掉，
        // 用户选的目录就不生效了——实测装到了旧路径。
        string args = $"INSTALLFOLDER=\"{targetDir}\" INSTALLLOCATION=\"{targetDir}\" REBOOT=ReallySuppress"
            + (forAllUsers ? " ALLUSERS=1" : " ALLUSERS=2 MSIINSTALLPERUSER=1");

        int code = Run(msiPath, args, state, chinese);

        // 装完必须真有 exe，否则不能报成功（否则用户点「完成」却发现没装）
        if (code is Success or RebootRequired &&
            !File.Exists(Path.Combine(targetDir, "AstraBrew Launcher.exe")))
        {
            state.SetError(chinese
                ? "安装程序没有把文件写到目标目录。"
                : "The installer did not write files to the target folder.");
            return 1603;
        }

        return code;
    }

    /// <summary>
    /// 卸载。原则：**以「目录里的 exe 真的消失」作为成功判据**，而不是看退出码。
    ///
    /// 这台机器上可能躺着多个同名产品记录（换过版本、装过渠道包都会留下），
    /// 逐个尝试：InstallLocation 对得上的先卸，其余按显示名匹配的后卸，最后才用 REMOVE=ALL。
    /// 每卸一个就检查 exe 还在不在，没了就停——卸掉别的残留也无妨，那是同一产品的旧记录。
    /// </summary>
    public static int Uninstall(string msiPath, ProgressState state, bool chinese)
    {
        state.Reset();

        string? installDir = ReadInstallLocation();
        string? exe = installDir is { Length: > 0 }
            ? Path.Combine(installDir, "AstraBrew Launcher.exe")
            : null;

        foreach (string code in FindCandidates(installDir))
        {
            int exit = RunMsiexec($"/x {code} /qn /norestart");

            if (exit is not (Success or RebootRequired) and not UnknownProduct)
            {
                // 1605 之类说明这条记录已经失效，继续试下一条
                continue;
            }

            if (exe is null || !File.Exists(exe))
            {
                return Success;
            }
        }

        // 兜底：直接按内嵌包卸
        int fallback = Run(msiPath, "REMOVE=ALL REBOOT=ReallySuppress", state, chinese);

        if (fallback is not (Success or RebootRequired))
        {
            return fallback;
        }

        if (exe is not null && File.Exists(exe))
        {
            state.SetError(chinese
                ? "卸载没有删除程序文件，可能是被其他进程占用，或这台机器上还有别的安装记录。"
                : "Uninstall did not remove the program files.");
            return 1603;
        }

        return Success;
    }

    private static int RunMsiexec(string arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo("msiexec.exe")
        {
            UseShellExecute = false,
            Arguments = arguments,
        };

        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(info)!;
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>安装时由 MSI 写进注册表的实际目录；两个作用域都会写一份到 HKCU。</summary>
    private static string? ReadInstallLocation()
    {
        foreach (Microsoft.Win32.RegistryKey root in new[]
        {
            Microsoft.Win32.Registry.CurrentUser,
            Microsoft.Win32.Registry.LocalMachine,
        })
        {
            try
            {
                using Microsoft.Win32.RegistryKey? key = root.OpenSubKey(@"Software\AstraBrew Labs\AstraBrew Launcher");

                if (key?.GetValue("InstallLocation") is string path && path.Length > 0)
                {
                    return path.TrimEnd('\\');
                }
            }
            catch (Exception)
            {
                // 读不到就试下一个
            }
        }

        return null;
    }

    private static int Run(string msiPath, string arguments, ProgressState state, bool chinese)
    {
        // 委托必须活到 MsiInstallProduct 返回：被 GC 掉就是访问违例
        ExternalUiHandler handler = (_, type, message) =>
        {
            try
            {
                state.CountMessage();

                switch (type)
                {
                    case InstallMessageProgress:
                        ApplyProgress(message, state);
                        break;

                    case InstallMessageActionStart:
                        ApplyAction(message, state, chinese);
                        break;

                    case InstallMessageError:
                    case InstallMessageFatalExit:
                        if (message is { Length: > 0 })
                        {
                            state.SetError(message.Trim());
                        }

                        break;
                }
            }
            catch (Exception)
            {
                // 回调里绝不能抛：MSI 会把它当成安装失败
            }

            return 0;
        };

        int previousLevel = MsiSetInternalUI(InstallUiLevelNone, IntPtr.Zero);
        MsiSetExternalUI(handler, 0xFFFFFFFF, IntPtr.Zero);

        try
        {
            return MsiInstallProduct(msiPath, arguments);
        }
        finally
        {
            MsiSetExternalUI((_, _, _) => 0, 0, IntPtr.Zero);
            MsiSetInternalUI(previousLevel, IntPtr.Zero);
            GC.KeepAlive(handler);
        }
    }

    /// <summary>
    /// 进度消息是四个整数：<c>总进度/单项进度/总量/当前</c>。
    /// 第一段 1 = 整个安装，2 = 当前这一项；第二段 2 表示「正在推进」。
    /// </summary>
    private static void ApplyProgress(string message, ProgressState state)
    {
        string[] parts = message.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 4 ||
            !int.TryParse(parts[0], out int kind) ||
            !int.TryParse(parts[1], out int direction) ||
            !int.TryParse(parts[2], out int total) ||
            !int.TryParse(parts[3], out int current))
        {
            return;
        }

        if (direction != 2 || total <= 0)
        {
            return;
        }

        // kind == 1 才是「整体进度」；kind == 2 是单个文件/组件，颗粒度太细
        if (kind == 1)
        {
            state.SetProgress(current * 100 / total);
            return;
        }

        if (kind == 2)
        {
            state.SetProgress(current * 100 / total);
        }
    }

    private static void ApplyAction(string message, ProgressState state, bool chinese)
    {
        string name = message.Split(':')[0].Trim();

        if (Actions.TryGetValue(name, out (string Zh, string En) text))
        {
            state.SetAction(chinese ? text.Zh : text.En);
            return;
        }

        // 认不出来的动作名不显示：MSI 有些消息是半截词（实测出现过只有一个「项」），
        // 摆上去比空着更让人困惑。保留上一句已知的动作即可。

    }

    /// <summary>
    /// 列出所有看起来是「这个产品」的卸载记录：先给 InstallLocation 对得上的，
    /// 再给只对得上显示名的（老版本没写 InstallLocation）。
    /// </summary>
    private static List<string> FindCandidates(string? installDir)
    {
        var exact = new List<string>();
        var byName = new List<string>();

        foreach (Microsoft.Win32.RegistryKey root in new[]
        {
            Microsoft.Win32.Registry.CurrentUser,
            Microsoft.Win32.Registry.LocalMachine,
        })
        {
            foreach (string view in new[]
            {
                "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
                "SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
            })
            {
                try
                {
                    using Microsoft.Win32.RegistryKey? key = root.OpenSubKey(view);
                    if (key is null)
                    {
                        continue;
                    }

                    foreach (string name in key.GetSubKeyNames())
                    {
                        using Microsoft.Win32.RegistryKey? entry = key.OpenSubKey(name);
                        if (entry?.GetValue("DisplayName") is not string display ||
                            !display.StartsWith("AstraBrew Launcher", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string? location = entry.GetValue("InstallLocation") as string;

                        if (installDir is { Length: > 0 } && location is { Length: > 0 } &&
                            location.TrimEnd('\\').Equals(installDir, StringComparison.OrdinalIgnoreCase))
                        {
                            exact.Add(name);
                        }
                        else if (!byName.Contains(name))
                        {
                            byName.Add(name);
                        }
                    }
                }
                catch (Exception)
                {
                    // 某个视图读不了就跳过
                }
            }
        }

        foreach (string name in byName)
        {
            if (!exact.Contains(name))
            {
                exact.Add(name);
            }
        }

        return exact;
    }

    /// <summary>把内嵌的 MSI 释放到临时目录。</summary>
    public static bool ExtractPayload(string destination)
    {
        using Stream? payload = typeof(MsiEngine).Assembly.GetManifestResourceStream("payload.msi");
        if (payload is null)
        {
            return false;
        }

        using FileStream file = File.Create(destination);
        payload.CopyTo(file);
        return true;
    }

    /// <summary>
    /// 删配置与缓存。**data 子目录整个跳过**——那里是酒馆实例、角色卡与会话记录，
    /// 一旦误删无法恢复，而它恰恰是用户最在乎的东西。
    /// </summary>
    public static void RemoveUserData()
    {
        string roaming = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AstraBrew Launcher");

        string local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AstraBrew Launcher");

        if (Directory.Exists(roaming))
        {
            foreach (string dir in Directory.GetDirectories(roaming))
            {
                if (Path.GetFileName(dir).Equals("data", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Try(() => Directory.Delete(dir, recursive: true));
            }

            foreach (string file in Directory.GetFiles(roaming))
            {
                Try(() => File.Delete(file));
            }
        }

        if (Directory.Exists(local))
        {
            Try(() => Directory.Delete(local, recursive: true));
        }
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // 单个文件被占用不影响其余
        }
    }
}
