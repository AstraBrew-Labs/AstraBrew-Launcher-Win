using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>
    /// 一个真的躺在磁盘上的扩展。
    ///
    /// 不是「示例数据」：<see cref="Path"/> 指向真实目录，<see cref="Enabled"/> 读自
    /// <c>default-user/settings.json</c> 的 <c>disabledExtensions</c>，界面上的开关改的也是它。
    /// </summary>
    public sealed class TavernExtension
    {
        public string Folder { get; init; } = string.Empty;

        /// <summary>扩展目录的绝对路径。</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>manifest.json 里的 display_name；没有清单时退回目录名。</summary>
        public string DisplayName { get; init; } = string.Empty;

        public string Version { get; init; } = string.Empty;

        public string Author { get; init; } = string.Empty;

        public string HomePage { get; init; } = string.Empty;

        /// <summary>manifest 里声明的依赖扩展名；缺了会在下面标出来。</summary>
        public IReadOnlyList<string> Requires { get; init; } = Array.Empty<string>();

        /// <summary>第三方扩展（third-party 或 data 下的 extensions）；否则是酒馆自带。</summary>
        public bool IsThirdParty { get; init; }

        /// <summary>没有 manifest.json，或者清单解析不了——这种扩展酒馆加载时会报错。</summary>
        public bool Broken { get; init; }

        /// <summary>是否在 settings.json 的启用列表里（没被 disabledExtensions 命中）。</summary>
        public bool Enabled { get; set; }

        /// <summary>目录里有 .git，说明是 clone 来的，可以查更新、可以 pull。</summary>
        public bool IsGitRepo { get; init; }

        /// <summary>检查更新后置位：远端 HEAD 与本地 HEAD 不一致。</summary>
        public bool HasUpdate { get; set; }

        public string VersionLabel => string.IsNullOrEmpty(Version) ? "—" : "v" + Version;
    }

    /// <summary>安装/克隆过程中的一步，界面据此推进度条与日志。</summary>
    public sealed record ExtensionInstallStep(double Percent, string Phase, string? LogLine = null);

    /// <summary>一次安装的结果。</summary>
    public sealed record ExtensionInstallResult(bool Ok, string Folder, string? Error);

    /// <summary>
    /// 一个待安装的离线包，以及**安装前**的预检结果。
    /// <see cref="Folder"/> 是装好后的目录名：压缩包里含 manifest.json 的那一层，或压缩包文件名。
    /// </summary>
    public sealed record OfflinePackage(string Path, string FileName, string? Folder, bool Valid, string? Error);

    /// <summary>批量安装里某一个包的失败。</summary>
    public sealed record OfflinePackageFailure(string FileName, string Error);

    /// <summary>批量安装的进度：<see cref="Percent"/> 是整批的百分比，不是单包的。</summary>
    public sealed record OfflineBatchStep(
        int Index,
        int Total,
        string FileName,
        double Percent,
        string Phase,
        string? LogLine = null);

    /// <summary>批量安装的结果：装成了哪些、哪些失败（失败不影响其余包继续装）。</summary>
    public sealed record OfflineBatchResult(
        IReadOnlyList<string> Installed,
        IReadOnlyList<OfflinePackageFailure> Failed);

    /// <summary>
    /// 扩展的真实读写。
    ///
    /// 判据全部来自磁盘与酒馆自己的配置：
    /// · 装在哪 —— <c>public/scripts/extensions</c>（自带）与三处 third-party 落点（不同版本用过不同位置）；
    /// · 开没开 —— <c>settings.json → extension_settings.disabledExtensions</c>，酒馆就是这么判的；
    /// · 有没有更新 —— <c>git ls-remote origin HEAD</c> 与本地 <c>rev-parse HEAD</c> 比对。
    /// </summary>
    public static class TavernExtensions
    {
        private static readonly JsonDocumentOptions JsonOptions = new()
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        };

        /// <summary>第三方扩展的写入位置：与酒馆「安装扩展」默认落点一致。</summary>
        public static string ThirdPartyRoot(TavernInstance instance) =>
            Path.Combine(instance.Path, "public", "scripts", "extensions", "third-party");

        /// <summary>酒馆自带的扩展目录。</summary>
        public static string BuiltinRoot(TavernInstance instance) =>
            Path.Combine(instance.Path, "public", "scripts", "extensions");

        /// <summary>用户级扩展目录（较新版本把第三方扩展放这）。</summary>
        public static string UserExtensionsRoot(TavernInstance instance) =>
            Path.Combine(instance.Path, "data", "default-user", "extensions");

        /// <summary>settings.json 的位置：扩展开关、聊天设置全在这一个文件里。</summary>
        public static string SettingsPath() =>
            Path.Combine(TavernRuntime.ResolveUserRoot(), "default-user", "settings.json");

        // ==================== 读取 ====================

        /// <summary>
        /// 扫出当前实例的全部扩展。
        ///
        /// 同一份扩展可能同时出现在两三个落点里（用户手动拷过、两版酒馆用过不同目录），
        /// 按目录名去重，先出现的位置优先——顺序就是酒馆自己的加载顺序：
        /// 安装目录的 third-party → 用户目录 → 自带。
        /// </summary>
        public static IReadOnlyList<TavernExtension> Load(TavernInstance instance)
        {
            var result = new List<TavernExtension>();

            if (instance is null || !Directory.Exists(instance.Path))
            {
                return result;
            }

            HashSet<string> disabled = ReadDisabledNames();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach ((string root, bool thirdParty, bool requireManifest) in Roots(instance))
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                string[] directories;
                try
                {
                    directories = Directory.GetDirectories(root);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (string dir in directories)
                {
                    string folder = System.IO.Path.GetFileName(dir);
                    if (!seen.Add(folder))
                    {
                        continue;
                    }

                    // 自带目录里躺着 assets 这类纯资源目录，没有清单就不是扩展
                    bool hasManifest = File.Exists(System.IO.Path.Combine(dir, "manifest.json"));
                    if (requireManifest && !hasManifest)
                    {
                        continue;
                    }

                    ManifestInfo manifest = ReadManifest(dir, hasManifest);
                    var extension = new TavernExtension
                    {
                        Folder = folder,
                        Path = dir,
                        DisplayName = manifest.DisplayName,
                        Version = manifest.Version,
                        Author = manifest.Author,
                        HomePage = manifest.HomePage,
                        Requires = manifest.Requires,
                        IsThirdParty = thirdParty,
                        Broken = !hasManifest || manifest.Failed,
                        IsGitRepo = Directory.Exists(System.IO.Path.Combine(dir, ".git")),
                        Enabled = !disabled.Contains(folder),
                    };

                    result.Add(extension);
                }
            }

            return result
                .OrderBy(item => item.IsThirdParty)
                .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>四处落点，顺序即优先级。</summary>
        private static IEnumerable<(string Root, bool ThirdParty, bool RequireManifest)> Roots(TavernInstance instance)
        {
            yield return (ThirdPartyRoot(instance), true, false);
            yield return (UserExtensionsRoot(instance), true, false);

            // 全局数据模式下 default-user 也可能在配置目录下面
            string userRoot = TavernRuntime.ResolveUserRoot();
            string alternate = System.IO.Path.Combine(userRoot, "extensions");
            if (!PathMatch.IsSame(alternate, UserExtensionsRoot(instance)))
            {
                yield return (alternate, true, false);
            }

            yield return (BuiltinRoot(instance), false, true);
        }

        /// <summary>
        /// <c>settings.json</c> 里被禁用的扩展名集合。
        /// 读不到（还没启动过酒馆、文件坏了）就当「全部启用」——酒馆的默认值也是空数组。
        /// </summary>
        public static HashSet<string> ReadDisabledNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                string path = SettingsPath();
                if (!File.Exists(path))
                {
                    return names;
                }

                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8), JsonOptions);

                if (!document.RootElement.TryGetProperty("extension_settings", out JsonElement settings)
                    || !settings.TryGetProperty("disabledExtensions", out JsonElement disabled)
                    || disabled.ValueKind != JsonValueKind.Array)
                {
                    return names;
                }

                foreach (JsonElement item in disabled.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } name)
                    {
                        names.Add(name);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // 配置暂时读不了：界面按「启用」显示，改开关时会重新写一次
            }

            return names;
        }

        // ==================== 启用 / 停用 ====================

        /// <summary>
        /// 改一个扩展的启用状态，写回 settings.json。
        ///
        /// 只动 <c>disabledExtensions</c> 这一个数组，其余字节原样保留——settings.json 里有
        /// 上百个字段（模型、代理、世界书规则…），整体反序列化再写出去，一个手滑就会把这些也改了样。
        /// 酒馆正在运行时它自己也持有这份配置，下次保存会覆盖我们写的值，所以调用方要提示用户。
        /// </summary>
        public static bool SetEnabled(TavernExtension extension, bool enabled, out string? error)
            => SetEnabledMany(new[] { (extension, enabled) }, out error);

        /// <summary>
        /// 一次改多个扩展的启用状态，只写一次 settings.json。
        ///
        /// 「全部停用」如果逐个调 <see cref="SetEnabled"/>，N 个扩展就要整份重写 N 次配置，
        /// 每次都是一次读盘 + 一次原子替换；中途失败还会留下半套状态。
        /// 这里先在内存里算好最终集合，再落一次盘。
        /// </summary>
        public static bool SetEnabledMany(
            IEnumerable<(TavernExtension Extension, bool Enabled)> changes,
            out string? error)
        {
            error = null;

            List<(TavernExtension Extension, bool Enabled)> list = changes.ToList();
            if (list.Count == 0)
            {
                return true;
            }

            try
            {
                string path = SettingsPath();
                string text = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "{}";

                List<string> names = ReadDisabledNames().ToList();
                bool changed = false;

                foreach ((TavernExtension extension, bool enabled) in list)
                {
                    bool disabled = names.Contains(extension.Folder, StringComparer.OrdinalIgnoreCase);

                    if (disabled == !enabled)
                    {
                        continue;
                    }

                    if (enabled)
                    {
                        names.RemoveAll(item => string.Equals(item, extension.Folder, StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        names.Add(extension.Folder);
                    }

                    changed = true;
                }

                if (!changed)
                {
                    foreach ((TavernExtension extension, bool enabled) in list)
                    {
                        extension.Enabled = enabled;
                    }

                    return true;
                }

                if (!JsonArrayEditor.TrySetStringArray(text, "disabledExtensions", names, out string updated))
                {
                    error = "settings.json 结构不认识，没能写回。请直接在酒馆的扩展面板里开关。";
                    return false;
                }

                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

                // 先写临时文件再换名：写一半被杀不会留下一个解析不了的 settings.json
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, updated, new UTF8Encoding(false));
                File.Move(temporary, path, overwrite: true);

                foreach ((TavernExtension extension, bool enabled) in list)
                {
                    extension.Enabled = enabled;
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
                return false;
            }
        }

        // ==================== 删除 ====================

        /// <summary>删除扩展目录（进回收站）。</summary>
        public static Task<(bool Ok, string? Error)> DeleteAsync(TavernExtension extension) =>
            ShellFile.MoveToRecycleBinAsync(extension.Path);

        // ==================== 更新检查 ====================

        /// <summary>
        /// 逐个比对本地与远端的 HEAD。只有 git clone 来的扩展才查得动；
        /// 离线包解压出来的目录没有 .git，一律跳过（它们本来就没有「上游」）。
        /// </summary>
        public static async Task<(int Checked, int Pending, string? Error)> CheckUpdatesAsync(
            IReadOnlyList<TavernExtension> extensions,
            IProgress<int>? progress = null)
        {
            string? git = RuntimeProbe.LocateGit(LauncherSettings.Runtime);
            if (git is null)
            {
                return (0, 0, "没有找到 Git，无法检查更新。可在「设置 → 运行环境」里补装。");
            }

            var repos = extensions.Where(item => item.IsGitRepo && !item.Broken).ToList();
            if (repos.Count == 0)
            {
                return (0, 0, null);
            }

            int done = 0;
            int pending = 0;

            // 并发三条：够快，又不至于把每个仓库都拉一次连接
            using var gate = new SemaphoreSlim(3);

            var tasks = repos.Select(async extension =>
            {
                await gate.WaitAsync().ConfigureAwait(false);

                try
                {
                    string? local = await RunGitAsync(git, extension.Path, new[] { "rev-parse", "HEAD" }, TimeSpan.FromSeconds(10))
                        .ConfigureAwait(false);

                    string? remote = await RunGitAsync(git, extension.Path, new[] { "ls-remote", "origin", "HEAD" }, TimeSpan.FromSeconds(25))
                        .ConfigureAwait(false);

                    extension.HasUpdate = local is { Length: > 0 }
                        && remote is { Length: > 0 }
                        && !remote.StartsWith(local, StringComparison.OrdinalIgnoreCase);

                    if (extension.HasUpdate)
                    {
                        Interlocked.Increment(ref pending);
                    }
                }
                finally
                {
                    gate.Release();
                    progress?.Report(Interlocked.Increment(ref done));
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);

            return (repos.Count, pending, null);
        }

        // ==================== 安装：Git ====================

        /// <summary>
        /// 列出远端分支，给「检测分支」用。
        ///
        /// 顺序有讲究：先放远端的默认分支（<c>--symref HEAD</c> 才能看出来），再 main / master，
        /// 其余按字母序。酒馆主仓库的默认分支是 release，只按字母排会把
        /// copilot/xxx 这种临时分支顶到第一项，用户一不留神就装错分支。
        /// </summary>
        public static async Task<IReadOnlyList<string>> ListBranchesAsync(string url)
        {
            string? git = RuntimeProbe.LocateGit(LauncherSettings.Runtime);
            if (git is null)
            {
                return Array.Empty<string>();
            }

            string remote = GithubAccel.Apply(url);
            string? output = await RunGitAsync(git, null, new[] { "ls-remote", "--symref", "--heads", remote }, TimeSpan.FromSeconds(25))
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(output))
            {
                return Array.Empty<string>();
            }

            var branches = new List<string>();
            string? defaultBranch = null;

            foreach (string line in output.Split('\n'))
            {
                // 默认分支那一行长这样：ref: refs/heads/release\tHEAD
                if (line.StartsWith("ref:", StringComparison.Ordinal))
                {
                    int headAt = line.IndexOf("refs/heads/", StringComparison.Ordinal);
                    if (headAt >= 0)
                    {
                        defaultBranch = line[(headAt + "refs/heads/".Length)..]
                            .Split('\t', ' ')
                            .FirstOrDefault(item => item.Length > 0);
                    }

                    continue;
                }

                int tab = line.IndexOf("refs/heads/", StringComparison.Ordinal);
                if (tab < 0)
                {
                    continue;
                }

                string name = line[(tab + "refs/heads/".Length)..].Trim();
                if (name.Length > 0)
                {
                    branches.Add(name);
                }
            }

            return branches
                .OrderBy(name => string.Equals(name, defaultBranch, StringComparison.Ordinal) ? 0
                    : name == "main" ? 1
                    : name == "master" ? 2
                    : 3)
                .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// 从 Git 仓库装一个扩展：浅克隆到 third-party。
        /// 目录名取仓库名，重名时加 -2、-3 后缀，不覆盖用户已有的目录——
        /// 覆盖安装等于把扩展配置一起删了，这种事必须由用户自己决定。
        /// </summary>
        public static async Task<ExtensionInstallResult> InstallFromGitAsync(
            string url,
            string branch,
            TavernInstance instance,
            IProgress<ExtensionInstallStep>? progress = null)
        {
            string? git = RuntimeProbe.LocateGit(LauncherSettings.Runtime);
            if (git is null)
            {
                return new ExtensionInstallResult(false, string.Empty, "没有找到 Git。可在「设置 → 运行环境」里补装，或改用离线包安装。");
            }

            string name = BaseName(url);
            if (name.Length == 0)
            {
                return new ExtensionInstallResult(false, string.Empty, "从地址里取不到仓库名。");
            }

            string root = ThirdPartyRoot(instance);
            Directory.CreateDirectory(root);

            string target = UniqueTarget(root, name);
            string cloneUrl = GithubAccel.Apply(url);

            progress?.Report(new ExtensionInstallStep(5, "正在克隆仓库…", $"$ git clone --depth 1 -b {branch} {cloneUrl}"));

            var result = await RunGitWithProgressAsync(
                git,
                new[] { "clone", "--depth", "1", "--branch", branch, "--progress", cloneUrl, target },
                line => progress?.Report(MapCloneLine(line)),
                TimeSpan.FromMinutes(5)).ConfigureAwait(false);

            if (!result.Ok)
            {
                SafeDelete(target);
                return new ExtensionInstallResult(false, string.Empty, result.Error ?? "克隆失败，请检查网络或仓库地址。");
            }

            progress?.Report(new ExtensionInstallStep(88, "正在校验 manifest.json…"));

            if (!File.Exists(System.IO.Path.Combine(target, "manifest.json")))
            {
                SafeDelete(target);

                // 有些仓库把扩展放在子目录里；这里不做猜测，直接说清楚
                return new ExtensionInstallResult(false, string.Empty, "仓库根目录里没有 manifest.json，这不像一个酒馆扩展。");
            }

            progress?.Report(new ExtensionInstallStep(95, "正在启用扩展…"));

            string folder = System.IO.Path.GetFileName(target);
            SetDisabledMember(folder, disabled: false, out _);

            progress?.Report(new ExtensionInstallStep(100, "安装完成", $"已创建 {target}"));

            return new ExtensionInstallResult(true, folder, null);
        }

        // ==================== 安装：离线包 ====================

        /// <summary>
        /// 从 .zip 离线包装一个扩展。压缩包可能把 manifest.json 放在根上，
        /// 也可能套了一层目录（GitHub 的源码包就是 <c>repo-main/…</c>），两种都认。
        /// </summary>
        public static async Task<ExtensionInstallResult> InstallFromZipAsync(
            string zipPath,
            TavernInstance instance,
            IProgress<ExtensionInstallStep>? progress = null)
        {
            if (!File.Exists(zipPath))
            {
                return new ExtensionInstallResult(false, string.Empty, "找不到这个离线包。");
            }

            string root = ThirdPartyRoot(instance);
            Directory.CreateDirectory(root);

            string staging = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "astrabrew-ext-" + Guid.NewGuid().ToString("N")[..8]);

            try
            {
                progress?.Report(new ExtensionInstallStep(10, "正在解压离线包…", $"$ unzip \"{zipPath}\""));

                await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true)).ConfigureAwait(false);

                progress?.Report(new ExtensionInstallStep(60, "正在定位扩展目录…"));

                string source = FindManifestFolder(staging);
                if (source.Length == 0)
                {
                    return new ExtensionInstallResult(false, string.Empty, "离线包里没有 manifest.json，这不像一个酒馆扩展。");
                }

                // 压缩包套了一层目录时用那一层的名字；清单就在根上时退回压缩包文件名
                string name = System.IO.Path.GetFileName(source);
                if (string.Equals(source, staging, StringComparison.OrdinalIgnoreCase))
                {
                    name = System.IO.Path.GetFileNameWithoutExtension(zipPath);
                }

                string target = UniqueTarget(root, name);

                progress?.Report(new ExtensionInstallStep(80, "正在写入 third-party…", $"→ {target}"));

                await Task.Run(() => MoveDirectory(source, target)).ConfigureAwait(false);

                string folder = System.IO.Path.GetFileName(target);
                SetDisabledMember(folder, disabled: false, out _);

                progress?.Report(new ExtensionInstallStep(100, "安装完成", $"已创建 {target}"));

                return new ExtensionInstallResult(true, folder, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return new ExtensionInstallResult(false, string.Empty, ex.Message);
            }
            finally
            {
                SafeDelete(staging);
            }
        }

        /// <summary>
        /// 预检一批离线包：只读压缩包的目录结构，不写任何东西。
        ///
        /// 为什么不直接装：一批里只要有一个不是扩展包，装到一半才发现就得回滚已装的几个；
        /// 而且「同一批里两个包都叫 x」时按「重名加后缀」的规则会得到 x 与 x-2，
        /// 用户根本分不清哪个是他要的——这种情况整组标为无效，让他自己删掉重复的。
        /// </summary>
        public static IReadOnlyList<OfflinePackage> InspectOfflinePackages(IEnumerable<string> paths)
        {
            var inspected = new List<OfflinePackage>();

            foreach (string path in paths)
            {
                string fileName = System.IO.Path.GetFileName(path);

                if (!File.Exists(path))
                {
                    inspected.Add(new OfflinePackage(path, fileName, null, false, "文件不存在"));
                    continue;
                }

                try
                {
                    using ZipArchive archive = ZipFile.OpenRead(path);
                    string? manifest = FindManifestEntry(archive);

                    if (manifest is null)
                    {
                        inspected.Add(new OfflinePackage(path, fileName, null, false,
                            "里面没有 manifest.json（最多向下找三层），不像酒馆扩展"));
                        continue;
                    }

                    inspected.Add(new OfflinePackage(path, fileName, FolderOfManifest(manifest, path), true, null));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    inspected.Add(new OfflinePackage(path, fileName, null, false, ex.Message));
                }
            }

            // 同名包整组作废：装哪一个、以谁为准都说不清
            var duplicates = inspected
                .Where(item => item.Valid && item.Folder is { Length: > 0 })
                .GroupBy(item => item.Folder!, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .SelectMany(group => group.Select(item => item.FileName))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (duplicates.Count == 0)
            {
                return inspected;
            }

            return inspected
                .Select(item => duplicates.Contains(item.FileName)
                    ? item with { Valid = false, Error = $"同一批里有多个包都叫「{item.Folder}」，先去掉重复的再装" }
                    : item)
                .ToList();
        }

        /// <summary>
        /// 依次安装一批离线包。单个失败不影响后面的包——批量安装最怕「第三个坏了，前两个白装」。
        /// </summary>
        public static async Task<OfflineBatchResult> InstallFromZipBatchAsync(
            IReadOnlyList<OfflinePackage> packages,
            TavernInstance instance,
            IProgress<OfflineBatchStep>? progress = null,
            CancellationToken token = default)
        {
            var installed = new List<string>();
            var failed = new List<OfflinePackageFailure>();

            if (packages.Count == 0)
            {
                return new OfflineBatchResult(installed, failed);
            }

            for (int index = 0; index < packages.Count; index++)
            {
                token.ThrowIfCancellationRequested();

                OfflinePackage package = packages[index];
                double start = 100.0 * index / packages.Count;
                double span = 100.0 / packages.Count;

                if (!package.Valid)
                {
                    failed.Add(new OfflinePackageFailure(package.FileName, package.Error ?? "预检未通过"));
                    progress?.Report(new OfflineBatchStep(index, packages.Count, package.FileName, start,
                        $"跳过 {package.FileName}", $"✗ {package.FileName}：{package.Error}"));
                    continue;
                }

                progress?.Report(new OfflineBatchStep(index, packages.Count, package.FileName, start,
                    $"正在安装 {package.FileName}（{index + 1}/{packages.Count}）",
                    $"▶ {package.FileName} → {package.Folder}"));

                // 单包进度换算成整批的百分比，进度条才不会一包一跳
                var inner = progress is null
                    ? null
                    : new Progress<ExtensionInstallStep>(step => progress.Report(new OfflineBatchStep(
                        index,
                        packages.Count,
                        package.FileName,
                        start + (span * Math.Clamp(step.Percent, 0, 100) / 100),
                        $"{step.Phase}（{index + 1}/{packages.Count}）",
                        step.LogLine)));

                ExtensionInstallResult result = await InstallFromZipAsync(package.Path, instance, inner)
                    .ConfigureAwait(false);

                if (result.Ok)
                {
                    installed.Add(result.Folder);
                }
                else
                {
                    failed.Add(new OfflinePackageFailure(package.FileName, result.Error ?? "安装失败"));
                }
            }

            progress?.Report(new OfflineBatchStep(packages.Count, packages.Count, string.Empty, 100,
                $"批量安装完成：成功 {installed.Count} 个，失败 {failed.Count} 个", null));

            return new OfflineBatchResult(installed, failed);
        }

        /// <summary>压缩包里最浅的那个 manifest.json；超过三层的不认（防止把依赖目录当扩展）。</summary>
        private static string? FindManifestEntry(ZipArchive archive)
        {
            string? best = null;
            int bestDepth = int.MaxValue;

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');

                if (!name.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int depth = name.Count(character => character == '/');
                if (depth > 3 || depth >= bestDepth)
                {
                    continue;
                }

                bestDepth = depth;
                best = name;
            }

            return best;
        }

        /// <summary>
        /// 清单所在的目录名＝装好后的扩展目录名。
        /// 清单在压缩包根上时没有目录名，退回压缩包文件名（与 <see cref="InstallFromZipAsync"/> 一致）。
        /// </summary>
        private static string FolderOfManifest(string manifestEntry, string zipPath)
        {
            string directory = manifestEntry[..^"manifest.json".Length].TrimEnd('/');

            if (directory.Length == 0)
            {
                return System.IO.Path.GetFileNameWithoutExtension(zipPath);
            }

            int slash = directory.LastIndexOf('/');
            return slash < 0 ? directory : directory[(slash + 1)..];
        }

        /// <summary>在解压结果里找「直接含 manifest.json」的那一层，最多下钻三层。</summary>
        private static string FindManifestFolder(string root, int depth = 0)
        {
            if (File.Exists(System.IO.Path.Combine(root, "manifest.json")))
            {
                return root;
            }

            if (depth >= 3)
            {
                return string.Empty;
            }

            foreach (string dir in Directory.GetDirectories(root))
            {
                string found = FindManifestFolder(dir, depth + 1);
                if (found.Length > 0)
                {
                    return found;
                }
            }

            return string.Empty;
        }

        private static void MoveDirectory(string source, string target)
        {
            if (string.Equals(source, System.IO.Path.GetDirectoryName(target), StringComparison.OrdinalIgnoreCase))
            {
                Directory.Move(source, target);
                return;
            }

            Directory.CreateDirectory(target);

            foreach (string file in Directory.GetFiles(source))
            {
                File.Move(file, System.IO.Path.Combine(target, System.IO.Path.GetFileName(file)));
            }

            foreach (string dir in Directory.GetDirectories(source))
            {
                Directory.Move(dir, System.IO.Path.Combine(target, System.IO.Path.GetFileName(dir)));
            }
        }

        // ==================== manifest ====================

        private sealed record ManifestInfo(
            string DisplayName,
            string Version,
            string Author,
            string HomePage,
            IReadOnlyList<string> Requires,
            bool Failed);

        private static ManifestInfo ReadManifest(string directory, bool hasManifest)
        {
            string fallback = System.IO.Path.GetFileName(directory);

            if (!hasManifest)
            {
                return new ManifestInfo(fallback, string.Empty, string.Empty, string.Empty, Array.Empty<string>(), true);
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(
                    File.ReadAllText(System.IO.Path.Combine(directory, "manifest.json"), Encoding.UTF8),
                    JsonOptions);

                JsonElement root = document.RootElement;

                var requires = new List<string>();
                if (root.TryGetProperty("requires", out JsonElement required) && required.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in required.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } name)
                        {
                            requires.Add(name);
                        }
                    }
                }

                return new ManifestInfo(
                    Text(root, "display_name") ?? fallback,
                    Text(root, "version") ?? string.Empty,
                    Text(root, "author") ?? string.Empty,
                    Text(root, "homePage") ?? Text(root, "homepage") ?? string.Empty,
                    requires,
                    false);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return new ManifestInfo(fallback, string.Empty, string.Empty, string.Empty, Array.Empty<string>(), true);
            }

            static string? Text(JsonElement root, string name) =>
                root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
        }

        // ==================== 工具 ====================

        private static void SetDisabledMember(string folder, bool disabled, out string? error)
        {
            var stub = new TavernExtension { Folder = folder };
            SetEnabled(stub, !disabled, out error);
        }

        private static string BaseName(string url)
        {
            string value = url.Trim().TrimEnd('/');
            if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                value = value[..^4];
            }

            int slash = value.LastIndexOfAny(new[] { '/', ':' });
            return (slash >= 0 ? value[(slash + 1)..] : value).Trim();
        }

        /// <summary>目标目录已存在时加后缀，绝不覆盖。</summary>
        private static string UniqueTarget(string root, string name)
        {
            string safe = Sanitize(name);
            string target = System.IO.Path.Combine(root, safe);

            for (int i = 2; Directory.Exists(target) || File.Exists(target); i++)
            {
                target = System.IO.Path.Combine(root, $"{safe}-{i}");
            }

            return target;
        }

        private static string Sanitize(string name)
        {
            var builder = new StringBuilder(name.Length);

            foreach (char ch in name)
            {
                builder.Append(System.IO.Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch);
            }

            string value = builder.ToString().Trim().TrimEnd('.');
            return value.Length == 0 ? "extension" : value;
        }

        private static void SafeDelete(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 半成品目录删不掉也不该让安装流程崩
            }
        }

        private static ExtensionInstallStep MapCloneLine(string line)
        {
            string text = line.Trim();

            if (text.StartsWith("remote:", StringComparison.OrdinalIgnoreCase))
            {
                text = text[7..].Trim();
            }

            // git 的进度写在 stderr：「Receiving objects:  47% (12/25)」
            int percentAt = text.IndexOf('%');
            if (percentAt > 0)
            {
                int start = percentAt;
                while (start > 0 && (char.IsDigit(text[start - 1]) || text[start - 1] == ' '))
                {
                    start--;
                }

                if (int.TryParse(text[start..percentAt].Trim(), out int value))
                {
                    // 克隆占 5–85，解析/检出再推到 88
                    return new ExtensionInstallStep(5 + value * 0.8, "正在克隆仓库…", text);
                }
            }

            return new ExtensionInstallStep(5, "正在克隆仓库…", text);
        }

        /// <summary>跑一条 git 命令并取 stdout；失败返回 null。</summary>
        private static Task<string?> RunGitAsync(string git, string? workingDirectory, string[] arguments, TimeSpan timeout)
        {
            return Task.Run(() =>
            {
                try
                {
                    ProcessStartInfo startInfo = BuildStartInfo(git, arguments, workingDirectory);

                    using Process? process = Process.Start(startInfo);
                    if (process is null)
                    {
                        return null;
                    }

                    Task<string> output = process.StandardOutput.ReadToEndAsync();
                    _ = process.StandardError.ReadToEndAsync();

                    if (!process.WaitForExit((int)timeout.TotalMilliseconds))
                    {
                        TryKill(process);
                        return null;
                    }

                    return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
                }
                catch
                {
                    return null;
                }
            });
        }

        private sealed record GitRun(bool Ok, string? Error);

        /// <summary>跑一条会输出进度的 git 命令（clone），逐行回调 stderr。</summary>
        private static Task<GitRun> RunGitWithProgressAsync(
            string git,
            string[] arguments,
            Action<string> onLine,
            TimeSpan timeout)
        {
            return Task.Run(() =>
            {
                try
                {
                    ProcessStartInfo startInfo = BuildStartInfo(git, arguments, null);
                    startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

                    using var process = new Process { StartInfo = startInfo };
                    var errorLines = new List<string>();

                    process.ErrorDataReceived += (_, e) =>
                    {
                        if (string.IsNullOrWhiteSpace(e.Data))
                        {
                            return;
                        }

                        lock (errorLines)
                        {
                            errorLines.Add(e.Data);
                        }

                        onLine(e.Data);
                    };

                    process.OutputDataReceived += (_, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data))
                        {
                            onLine(e.Data);
                        }
                    };

                    if (!process.Start())
                    {
                        return new GitRun(false, "无法启动 git 进程。");
                    }

                    process.BeginErrorReadLine();
                    process.BeginOutputReadLine();

                    if (!process.WaitForExit((int)timeout.TotalMilliseconds))
                    {
                        TryKill(process);
                        return new GitRun(false, "克隆超时。请检查网络，或改用离线包安装。");
                    }

                    if (process.ExitCode == 0)
                    {
                        return new GitRun(true, null);
                    }

                    string detail;
                    lock (errorLines)
                    {
                        detail = string.Join(" / ", errorLines.TakeLast(3).Select(line => line.Trim()));
                    }

                    return new GitRun(false, detail.Length > 0 ? detail : $"git 退出代码 {process.ExitCode}");
                }
                catch (Exception ex)
                {
                    return new GitRun(false, ex.Message);
                }
            });
        }

        private static ProcessStartInfo BuildStartInfo(string git, string[] arguments, string? workingDirectory)
        {
            var startInfo = new ProcessStartInfo(git)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            if (workingDirectory is { Length: > 0 })
            {
                startInfo.WorkingDirectory = workingDirectory;
            }

            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            // 内置 MinGit 的 git.exe 调用的辅助程序在相邻目录里
            if (System.IO.Path.GetDirectoryName(git) is { Length: > 0 } gitDir)
            {
                string root = System.IO.Path.GetDirectoryName(gitDir) ?? gitDir;
                startInfo.Environment["PATH"] = string.Join(
                    System.IO.Path.PathSeparator,
                    new[] { gitDir, System.IO.Path.Combine(root, "bin"), System.IO.Path.Combine(root, "mingw64", "bin"), startInfo.Environment["PATH"] });
            }

            return startInfo;
        }

        private static void TryKill(Process process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 已经自己退了
            }
        }
    }

    /// <summary>
    /// 只改 JSON 里一个字符串数组的工具。
    ///
    /// 为什么不 <c>JsonNode.Parse</c> 再整体写回：settings.json 里有一百多个字段，
    /// 反序列化会丢掉重复键的写法、把数字统一成浮点、把中文转成 \uXXXX，
    /// 一份用户配置被改得面目全非只为了加一个名字。这里按字符扫描，只替换目标数组那一段。
    /// </summary>
    internal static class JsonArrayEditor
    {
        public static bool TrySetStringArray(string json, string key, IEnumerable<string> values, out string result)
        {
            result = json;

            string literal = "\"" + key + "\"";
            int keyAt = json.IndexOf(literal, StringComparison.Ordinal);

            if (keyAt >= 0)
            {
                int colon = json.IndexOf(':', keyAt + literal.Length);
                if (colon < 0)
                {
                    return false;
                }

                int open = colon + 1;
                while (open < json.Length && char.IsWhiteSpace(json[open]))
                {
                    open++;
                }

                if (open >= json.Length)
                {
                    return false;
                }

                if (json[open] == '[')
                {
                    int close = FindArrayEnd(json, open);
                    if (close < 0)
                    {
                        return false;
                    }

                    result = json[..open] + BuildArray(values, IndentOf(json, keyAt)) + json[(close + 1)..];
                    return true;
                }

                return false;
            }

            // 键不存在：得插进 extension_settings 里（酒馆默认值是空数组，老配置可能还没有这个键）
            return TryInsertKey(json, "extension_settings", key, values, out result);
        }

        private static bool TryInsertKey(string json, string parent, string key, IEnumerable<string> values, out string result)
        {
            result = json;

            string literal = "\"" + parent + "\"";
            int at = json.IndexOf(literal, StringComparison.Ordinal);
            if (at < 0)
            {
                return false;
            }

            int colon = json.IndexOf(':', at + literal.Length);
            if (colon < 0)
            {
                return false;
            }

            int open = colon + 1;
            while (open < json.Length && char.IsWhiteSpace(json[open]))
            {
                open++;
            }

            if (open >= json.Length || json[open] != '{')
            {
                return false;
            }

            string indent = IndentOf(json, at) + "  ";
            string insert = Environment.NewLine + indent + "\"" + key + "\": " + BuildArray(values, indent) + ",";

            result = json[..(open + 1)] + insert + json[(open + 1)..];
            return true;
        }

        private static string BuildArray(IEnumerable<string> values, string indent)
        {
            List<string> items = values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")
                .ToList();

            if (items.Count == 0)
            {
                return "[]";
            }

            string inner = indent + "  ";
            return "[" + Environment.NewLine
                + string.Join("," + Environment.NewLine, items.Select(item => inner + item))
                + Environment.NewLine + indent + "]";
        }

        /// <summary>数组的右括号位置；跳过字符串里的括号与转义。</summary>
        private static int FindArrayEnd(string json, int open)
        {
            bool inString = false;
            bool escaped = false;

            for (int i = open + 1; i < json.Length; i++)
            {
                char ch = json[i];

                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (!inString && ch == ']')
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>某个位置所在行的缩进，用来把新数组的排版对齐到原来的风格。</summary>
        private static string IndentOf(string json, int position)
        {
            int lineStart = json.LastIndexOf('\n', Math.Max(0, position - 1));
            if (lineStart < 0)
            {
                return string.Empty;
            }

            // lineStart 指向那个 '\n' 本身：从它后面开始量，否则缩进里会多带一个换行
            int index = lineStart + 1;
            while (index < json.Length && (json[index] == ' ' || json[index] == '\t'))
            {
                index++;
            }

            return json[(lineStart + 1)..index];
        }
    }
}
