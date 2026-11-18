using System.Globalization;
using System.Reflection;

namespace AstraBrewSetup;

/// <summary>
/// 入口：解析参数 → 把内嵌 MSI 释放到临时目录 → 开向导（或静默安装）→ 收拾临时文件。
///
/// 支持的命令行：
///   （无参数）        交互式安装向导
///   /S 或 /silent    静默安装（默认仅当前用户，装到 %LocalAppData%\Programs）
///   /ALLUSERS        配合 /S：为所有人安装（默认落 %ProgramData%）
///   /D=&lt;路径&gt;        指定安装目录
///   /uninstall /x    交互式卸载
///   /purge           卸载并删除配置与缓存（不弹询问，给批量部署用）
/// </summary>
internal static class Program
{
    private const string DefaultProduct = "AstraBrew Launcher";

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        bool silent = Has(args, "/S") || Has(args, "/silent") || Has(args, "/quiet") || Has(args, "/qn");
        bool uninstall = Has(args, "/uninstall") || Has(args, "/x") || Has(args, "/purge");
        bool purge = Has(args, "/purge");
        bool allUsers = Has(args, "/ALLUSERS");
        string? directory = ReadValue(args, "/D=") ?? ReadValue(args, "/DIR=") ?? ReadValue(args, "/INSTALLDIR=");

        string tempDir = Path.Combine(Path.GetTempPath(), "astrabrew-setup-" + Guid.NewGuid().ToString("N"));
        string msiPath = Path.Combine(tempDir, "AstraBrewLauncher.msi");

        try
        {
            Directory.CreateDirectory(tempDir);

            if (!MsiEngine.ExtractPayload(msiPath))
            {
                Fail("安装包损坏：找不到内嵌的 MSI。", "Corrupted package: embedded MSI not found.");
                return 2;
            }

            if (silent)
            {
                return RunSilent(msiPath, uninstall, purge, allUsers, directory);
            }

            using var wizard = new SetupWizard(msiPath, ProductName, ProductVersion, uninstall, purge);
            Application.Run(wizard);
            return 0;
        }
        catch (Exception ex)
        {
            Fail(ex.ToString(), ex.Message);
            return 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch (Exception)
            {
                // 临时目录清不掉无所谓，系统会回收
            }
        }
    }

    private static int RunSilent(string msiPath, bool uninstall, bool purge, bool allUsers, string? directory)
    {
        var state = new MsiEngine.ProgressState();

        if (uninstall)
        {
            int code = MsiEngine.Uninstall(msiPath, state, Zh);

            if (purge && code is 0 or 3010)
            {
                MsiEngine.RemoveUserData();
            }

            return code;
        }

        string target = directory ?? DefaultDirectory(allUsers);
        Directory.CreateDirectory(target);

        return MsiEngine.Install(msiPath, target, allUsers, state, Zh);
    }

    private static string DefaultDirectory(bool allUsers) => allUsers
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AstraBrew Launcher")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AstraBrew Launcher");

    private static bool Zh => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
        .Equals("zh", StringComparison.OrdinalIgnoreCase);

    private static bool Has(string[] args, string name) =>
        args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? ReadValue(string[] args, string prefix)
    {
        foreach (string arg in args)
        {
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string value = arg[prefix.Length..].Trim('"');
                return value.Length > 0 ? value : null;
            }
        }

        return null;
    }

    /// <summary>产品名与版本由 package.ps1 通过 AssemblyMetadata 写进来（渠道后缀会带上）。</summary>
    private static string ProductName =>
        Metadata("ProductDisplayName") ?? DefaultProduct;

    private static string ProductVersion =>
        Metadata("ProductVersionLabel") ?? "1.0.0";

    private static string? Metadata(string key) => typeof(Program).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == key)?.Value;

    private static void Fail(string log, string message)
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "astrabrew-launcher");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "setup.log"),
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {log}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 日志写不了就算了
        }

        MessageBox.Show(
            message,
            DefaultProduct,
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
