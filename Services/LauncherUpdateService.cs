using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>检测结论。</summary>
    public enum UpdateStatus
    {
        /// <summary>还没查过。</summary>
        Unknown,

        /// <summary>已是最新。</summary>
        UpToDate,

        /// <summary>有新版本，可下载。</summary>
        Available,

        /// <summary>三个源都失败了。</summary>
        Failed,

        /// <summary>源都通，但没有当前渠道的发布（例如 Debug 版，仓库里只有 beta 包）。</summary>
        NoReleaseForChannel,
    }

    /// <summary>一次检测的完整结论，界面只读它。</summary>
    public sealed class UpdateCheckResult
    {
        public UpdateStatus State { get; init; }

        public LauncherRelease? Latest { get; init; }

        public UpdateSourceKind? Source { get; init; }

        public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.Now;

        public UpdateFailure Failure { get; init; }

        public IReadOnlyList<UpdateSourceProbe> Probes { get; init; } = Array.Empty<UpdateSourceProbe>();

        /// <summary>是不是从缓存读出来的（界面上可以写「上次检查」）。</summary>
        public bool FromCache { get; init; }
    }

    /// <summary>
    /// 启动器自身的更新：检测、下载、校验、替换、重启。
    ///
    /// 几处刻意的选择：
    /// · **渠道隔离**：只收自己这条线的发布。Debug 版拿到 beta 包会把渠道标记改掉，
    ///   那种「更新」比不更新更糟；同理正式版也不会被预发布版顶掉。
    /// · **检测有缓存**（6 小时）：GitHub 匿名访问是 60 次/小时，按 IP 计。每次启动都查会把配额吃光，
    ///   而启动器是天天开的。手动点「检查更新」永远是强制的，不吃缓存。
    /// · **不降级**：远端版本比本地低（仓库里还挂着旧 0.0.x）时按「已是最新」处理，绝不回退安装。
    /// · **先校验再落盘**：发布里带 <c>SHA256SUMS.txt</c> 就比对；没有就照装，但会在界面标注。
    /// </summary>
    public static class LauncherUpdateService
    {
        /// <summary>启动自动检查的缓存有效期。</summary>
        public static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(6);

        /// <summary>
        /// 这个构建要不要参与「自动检查」（启动后静默查一次、进关于页自动回填）。
        ///
        /// 开发版不参与：它跑的就是工作区里最新的代码，提示「有新版本」没有意义，
        /// 而且每次启动都白打一次接口。手动点「检查更新」仍然可用——
        /// 验证发布会话时就靠它，禁用整套更新反而更难测。
        /// </summary>
        public static bool AutoCheckEnabled => BuildChannelInfo.Channel != BuildChannel.Debug;

        /// <summary>
        /// 当前实际跟的渠道。取 <see cref="LauncherSettings.EffectiveUpdateChannel"/>：
        /// 用户没手动定过（Auto）时按构建渠道推导——测试版跟测试线、正式版跟正式线。
        /// </summary>
        public static ReleaseChannel LocalChannel => LauncherSettings.EffectiveUpdateChannel switch
        {
            UpdateChannelSetting.Stable => ReleaseChannel.Release,
            UpdateChannelSetting.Beta => ReleaseChannel.Beta,
            UpdateChannelSetting.Alpha => ReleaseChannel.Alpha,
            _ => ReleaseChannel.Release,
        };

        /// <summary>本地版本（<c>0.3.0-Debug.0</c> 这种，不含前导 v）。</summary>
        public static LauncherVersion LocalVersion => LauncherVersion.ParseOrZero(BuildChannelInfo.Version);

        /// <summary>启动器自己的 exe 路径。</summary>
        public static string ExePath => Environment.ProcessPath ?? string.Empty;

        private static string DataRoot
        {
            get
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(localAppData.Length > 0 ? localAppData : AppContext.BaseDirectory, "AstraBrew Launcher");
            }
        }

        private static string CacheFile => Path.Combine(DataRoot, "update-check.json");

        private static string UpdateRoot => Path.Combine(DataRoot, "update");

        /// <summary>
        /// 当前这份是不是「MSI 装出来的」：装到 <c>%LocalAppData%\Programs\AstraBrew Launcher</c>。
        /// 安装版用 setup.exe 升级（走 MSI，ARP 记录与快捷方式一并更新），免安装版直接覆盖文件。
        /// </summary>
        public static bool IsInstalledLayout
        {
            get
            {
                try
                {
                    string exe = ExePath;
                    if (exe.Length == 0)
                    {
                        return false;
                    }

                    string expected = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Programs",
                        "AstraBrew Launcher");

                    return exe.StartsWith(
                        expected.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        // ==================== 检测 ====================

        /// <summary>
        /// 查一次更新。<paramref name="force"/> = false 时优先吃 6 小时内的缓存，
        /// 那是「启动时自动检查」的路径；手动点按钮必须传 true。
        /// </summary>
        public static async Task<UpdateCheckResult> CheckAsync(bool force, CancellationToken cancellationToken)
        {
            if (!force)
            {
                UpdateCacheEntry? cached = ReadCache();
                if (cached is not null && DateTimeOffset.Now - cached.CheckedAt < CacheLifetime)
                {
                    return new UpdateCheckResult
                    {
                        State = Evaluate(cached.Latest),
                        Latest = cached.Latest,
                        Source = cached.Source,
                        CheckedAt = cached.CheckedAt,
                        FromCache = true,
                    };
                }
            }

            ReleaseQueryResult query = await LauncherUpdateSource
                .QueryAsync(cancellationToken)
                .ConfigureAwait(false);

            DateTimeOffset now = DateTimeOffset.Now;

            if (!query.Ok)
            {
                return new UpdateCheckResult
                {
                    State = UpdateStatus.Failed,
                    Failure = query.Failure,
                    Probes = query.Probes,
                    CheckedAt = now,
                };
            }

            LauncherRelease? latest = LauncherRelease.PickLatest(query.Releases, LocalChannel);
            if (latest is null)
            {
                return new UpdateCheckResult
                {
                    State = UpdateStatus.NoReleaseForChannel,
                    Source = query.Source,
                    Probes = query.Probes,
                    CheckedAt = now,
                };
            }

            WriteCache(latest, query.Source!.Value, now);

            return new UpdateCheckResult
            {
                State = Evaluate(latest),
                Latest = latest,
                Source = query.Source,
                Probes = query.Probes,
                CheckedAt = now,
            };
        }

        /// <summary>
        /// 远端版本必须**严格高于**本地才算有更新：相等、更低都按已是最新处理。
        ///
        /// 比较前两边都要剥掉渠道词：远端 tag 是 <c>beta-v0.3.0</c>（版本段不带 -Beta.0），
        /// 本地是 <c>0.3.0-Beta.0</c>。不剥的话 0.3.0 &gt; 0.3.0-Beta.0，装上之后还是同一个版本，
        /// 于是每次检查都提示更新——「越更越旧」的死循环。
        /// </summary>
        private static UpdateStatus Evaluate(LauncherRelease? latest)
        {
            if (latest is null)
            {
                return UpdateStatus.Unknown;
            }

            LauncherVersion remote = latest.Version.WithoutChannel(LocalChannel);
            LauncherVersion local = LocalVersion.WithoutChannel(LocalChannel);

            return remote > local
                ? UpdateStatus.Available
                : UpdateStatus.UpToDate;
        }

        // ==================== 缓存 ====================

        private sealed record UpdateCacheEntry(DateTimeOffset CheckedAt, UpdateSourceKind Source, LauncherRelease Latest);

        private static UpdateCacheEntry? ReadCache()
        {
            try
            {
                if (!File.Exists(CacheFile))
                {
                    return null;
                }

                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(CacheFile, Encoding.UTF8));
                JsonElement root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("checkedAt", out JsonElement checkedAt) ||
                    !DateTimeOffset.TryParse(checkedAt.GetString(), out DateTimeOffset stamp))
                {
                    return null;
                }

                if (!root.TryGetProperty("source", out JsonElement sourceElement) ||
                    !Enum.TryParse(sourceElement.GetString(), ignoreCase: true, out UpdateSourceKind source))
                {
                    return null;
                }

                // 缓存必须和当前渠道一致：用户刚把「稳定版」切成「测试版」，
                // 拿旧的稳定版结论去显示会让人以为切换没生效。对不上就当没有缓存。
                if (!root.TryGetProperty("channel", out JsonElement channelElement) ||
                    !Enum.TryParse(channelElement.GetString(), ignoreCase: true, out ReleaseChannel cachedChannel) ||
                    cachedChannel != LocalChannel)
                {
                    return null;
                }

                LauncherRelease? release = root.TryGetProperty("release", out JsonElement releaseElement)
                    ? LauncherRelease.Parse(releaseElement)
                    : null;

                return release is null ? null : new UpdateCacheEntry(stamp, source, release);
            }
            catch (Exception)
            {
                // 缓存坏了就当没有：重新查一次比让关于页打不开强
                return null;
            }
        }

        /// <summary>
        /// 缓存写的是一份**最小的 GitHub 形状** JSON（tag_name / name / body / assets…），
        /// 这样读的时候能直接复用 <see cref="LauncherRelease.Parse"/>，不用维护第二套反序列化。
        /// </summary>
        private static void WriteCache(LauncherRelease release, UpdateSourceKind source, DateTimeOffset checkedAt)
        {
            try
            {
                Directory.CreateDirectory(DataRoot);

                using var stream = File.Create(CacheFile);
                using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

                writer.WriteStartObject();
                writer.WriteString("checkedAt", checkedAt.ToString("O"));
                writer.WriteString("source", source.ToString());
                writer.WriteString("channel", LocalChannel.ToString());

                writer.WritePropertyName("release");
                writer.WriteStartObject();
                writer.WriteString("tag_name", release.Tag);
                writer.WriteString("name", release.Title);
                writer.WriteString("body", release.Notes);
                writer.WriteBoolean("prerelease", release.Prerelease);

                if (release.PublishedAt is { } published)
                {
                    writer.WriteString("published_at", published.ToString("O"));
                }

                writer.WritePropertyName("assets");
                writer.WriteStartArray();
                foreach (UpdateAsset asset in release.Assets)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", asset.Name);
                    writer.WriteString("browser_download_url", asset.Url);
                    writer.WriteNumber("size", asset.Size);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            catch (Exception)
            {
                // 缓存写不进去不影响这一次的结论
            }
        }

        // ==================== 下载 ====================

        /// <summary>
        /// 下载更新包到 <c>%LocalAppData%\AstraBrew Launcher\update\&lt;tag&gt;\</c>，返回本地路径。
        ///
        /// 下载地址按「原地址 → 加速 → 直连」的顺序试：GitCode 的资产直链只有一条，
        /// GitHub 的资产则可以走中转。断点续传不做——包在 100 MB 量级，重下比处理半截文件简单。
        /// </summary>
        public static async Task<string> DownloadAsync(
            LauncherRelease release,
            UpdateAsset asset,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            string folder = Path.Combine(UpdateRoot, SafeName(release.Version.ToString()));
            Directory.CreateDirectory(folder);

            string target = Path.Combine(folder, asset.Name);
            string partial = target + ".part";

            List<string> candidates = new() { asset.Url };

            // GitHub 的下载地址再补一条中转（不看开关，理由同检测）
            string accelerated = GithubAccel.ApplyAlways(asset.Url);
            if (!string.Equals(accelerated, asset.Url, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(accelerated);
            }

            Exception? lastError = null;

            foreach (string url in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    using HttpClient client = ProxyHttpClient.Create(TimeSpan.FromMinutes(30));
                    using HttpResponseMessage response = await client
                        .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                        .ConfigureAwait(false);

                    response.EnsureSuccessStatusCode();

                    long total = response.Content.Headers.ContentLength ?? asset.Size;

                    await using (Stream source = await response.Content
                        .ReadAsStreamAsync(cancellationToken)
                        .ConfigureAwait(false))
                    await using (var destination = new FileStream(
                        partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    {
                        byte[] buffer = new byte[81920];
                        long received = 0;
                        int read;

                        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                            received += read;

                            if (total > 0)
                            {
                                progress?.Report(Math.Min(100d, received * 100d / total));
                            }
                        }
                    }

                    if (File.Exists(target))
                    {
                        File.Delete(target);
                    }

                    File.Move(partial, target);
                    progress?.Report(100d);

                    return target;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
                finally
                {
                    TryDelete(partial);
                }
            }

            throw new IOException(
                L.Get("Update.Error.Download"),
                lastError ?? new IOException(asset.Url));
        }

        /// <summary>
        /// 校验下载到的包。发布里有 <c>SHA256SUMS.txt</c> 就比对，没有就返回 true（放行但不算校验过）。
        /// 返回 false 表示**校验不通过**，调用方必须删包并报错。
        /// </summary>
        public static async Task<bool> VerifyAsync(
            LauncherRelease release,
            UpdateAsset asset,
            string localPath,
            CancellationToken cancellationToken)
        {
            UpdateAsset? sums = release.FindChecksums();
            if (sums is null)
            {
                return true;
            }

            try
            {
                using HttpClient client = ProxyHttpClient.Create(TimeSpan.FromSeconds(20));
                string text = await client.GetStringAsync(sums.Url, cancellationToken).ConfigureAwait(false);

                string expected = ParseSha256(text, asset.Name);
                if (expected.Length == 0)
                {
                    // 清单里没写这个文件：按「没校验」处理，不因此拦下更新
                    return true;
                }

                await using FileStream stream = File.OpenRead(localPath);
                byte[] hash = await System.Security.Cryptography.SHA256
                    .HashDataAsync(stream, cancellationToken)
                    .ConfigureAwait(false);

                string actual = Convert.ToHexString(hash).ToLowerInvariant();
                return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // 清单拉不下来不该卡住更新：包本身还有 HTTPS，且 MSI/zip 供应链就在同一个 release 里
                return true;
            }
        }

        private static string ParseSha256(string text, string fileName)
        {
            foreach (string line in text.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                // 标准格式：<64 位 hash>  <文件名>（两个空格）
                int split = trimmed.IndexOf(' ');
                if (split <= 0)
                {
                    continue;
                }

                string hash = trimmed[..split].Trim();
                string name = trimmed[(split + 1)..].Trim().TrimStart('*');

                if (hash.Length == 64 && string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return hash.ToLowerInvariant();
                }
            }

            return string.Empty;
        }

        // ==================== 安装 ====================

        /// <summary>
        /// 准备好替换并启动外部执行器。**本进程随后必须立刻退出**（文件被占用就换不掉）。
        ///
        /// 便携版：把 zip 先解压到**程序目录旁边的 <c>&lt;目录&gt;.new</c>**，脚本在退出后
        /// 用「旧目录改名 → 新目录顶上 → 删旧目录」的方式整体换掉，不做原地增量覆盖。
        /// 这样解压/校验失败时程序目录分毫未动，最坏结果只是「没更新成功」，而不是半个程序。
        /// 安装版：脚本等在退出后静默跑 setup.exe，让 MSI 自己升级。
        /// 两条路最后都会把启动器重新拉起来。
        /// </summary>
        public static bool BeginInstall(string packagePath, LauncherRelease release, out string error)
        {
            error = string.Empty;

            try
            {
                string versionDir = Path.Combine(UpdateRoot, SafeName(release.Version.ToString()));
                Directory.CreateDirectory(versionDir);

                string exe = ExePath;
                string targetDir = (Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
                string scriptPath;
                string arguments;

                if (IsInstalledLayout && packagePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    // 安装版：直接跑安装包，让 MSI 处理文件、快捷方式与卸载记录
                    scriptPath = WriteInstallerScript(versionDir, packagePath, exe, Environment.ProcessId);
                }
                else
                {
                    // 解压到程序目录旁边：同卷，脚本里直接改名就能换过去，不用再复制一遍 300 MB
                    string stagingDir = targetDir + ".new";
                    if (Directory.Exists(stagingDir))
                    {
                        Directory.Delete(stagingDir, recursive: true);
                    }

                    System.IO.Compression.ZipFile.ExtractToDirectory(packagePath, stagingDir);

                    string stagedExe = Path.Combine(stagingDir, Path.GetFileName(exe));
                    if (!File.Exists(stagedExe))
                    {
                        throw new IOException(L.Get("Update.Error.NoAsset"));
                    }

                    scriptPath = WritePortableScript(versionDir, stagingDir, targetDir, exe, Environment.ProcessId);
                }

                arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"";

                var startInfo = new ProcessStartInfo("powershell.exe", arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = versionDir,
                };

                Process.Start(startInfo);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>PowerShell 单引号字符串：路径里出现单引号时按 PS 的规矩双写。</summary>
        private static string Ps(string value) => "'" + value.Replace("'", "''") + "'";

        /// <summary>
        /// 覆盖安装用的脚本：等本进程退出（最多 90 秒）→ 旧目录改名 <c>.old</c> → 新目录顶上 →
        /// 拉起新版本 → 删旧目录。
        ///
        /// 为什么是「整目录换」而不是 robocopy 覆盖程序目录：
        /// 发布包与本地构建输出的文件集合并不相同（build 目录里多出 xbf / appxrecipe / pdb），
        /// 增量覆盖会留下旧文件，新旧混装的结果是启动即崩（0xC000027B）且没有任何日志。
        /// 而先解压到旁边、改名交换，能保证「要么整个换过去，要么原样不动」。
        /// 脚本全程写日志到 %TEMP%\astrabrew-launcher\update.log，出问题能查。
        /// </summary>
        private static string WritePortableScript(
            string versionDir,
            string stagingDir,
            string targetDir,
            string exePath,
            int processId)
        {
            string path = Path.Combine(versionDir, "apply-portable.ps1");
            string backupDir = targetDir + ".old";
            string targetName = Path.GetFileName(targetDir);
            string logFile = Path.Combine(Path.GetTempPath(), "astrabrew-launcher", "update.log");

            var builder = new StringBuilder();
            builder.AppendLine("$ErrorActionPreference = 'Continue'");
            builder.AppendLine($"$log = {Ps(logFile)}");
            builder.AppendLine("New-Item -ItemType Directory -Force -Path (Split-Path -Parent $log) | Out-Null");
            builder.AppendLine("function Log($m) { Add-Content -LiteralPath $log -Value ('[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $m) }");
            builder.AppendLine("Log 'update start'");
            builder.AppendLine($"$targetPid = {processId}");
            builder.AppendLine("$deadline = (Get-Date).AddSeconds(90)");
            builder.AppendLine("while ((Get-Process -Id $targetPid -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 400 }");
            // 进程刚退出时 exe 句柄未必立刻释放，多等一下比换个半截强
            builder.AppendLine("Start-Sleep -Milliseconds 1500");
            builder.AppendLine("Log 'process exited'");

            // 1) 旧目录改名。被占用（杀软扫描、资源管理器打开着）时重试几轮
            builder.AppendLine($"if (Test-Path -LiteralPath {Ps(backupDir)}) {{ Remove-Item -LiteralPath {Ps(backupDir)} -Recurse -Force -ErrorAction SilentlyContinue }}");
            builder.AppendLine("$renamed = $false");
            builder.AppendLine("for ($i = 0; $i -lt 20 -and -not $renamed; $i++) {");
            builder.AppendLine($"    try {{ Rename-Item -LiteralPath {Ps(targetDir)} -NewName ($({Ps(targetName)}) + '.old') -ErrorAction Stop; $renamed = $true }}");
            builder.AppendLine("    catch { Start-Sleep -Milliseconds 500 }");
            builder.AppendLine("}");
            builder.AppendLine("Log ('rename old: ' + $renamed)");

            builder.AppendLine("if (-not $renamed) {");
            builder.AppendLine("    Log 'abort: cannot rename old dir'");
            // 旧目录还在，程序还能用：直接把旧版本拉起来
            builder.AppendLine($"    Start-Process -FilePath {Ps(exePath)} -WorkingDirectory {Ps(targetDir)}");
            builder.AppendLine("    exit 1");
            builder.AppendLine("}");

            // 2) 新目录顶上
            builder.AppendLine("$swapped = $false");
            builder.AppendLine("try {");
            builder.AppendLine($"    Rename-Item -LiteralPath {Ps(stagingDir)} -NewName {Ps(targetName)} -ErrorAction Stop");
            builder.AppendLine("    $swapped = $true");
            builder.AppendLine("} catch {");
            builder.AppendLine("    Log ('rename new failed: ' + $_.Exception.Message)");
            builder.AppendLine("}");
            builder.AppendLine("Log ('swap: ' + $swapped)");

            builder.AppendLine("if (-not $swapped) {");
            // 回滚：把旧目录改回去，别让用户手里没有程序
            builder.AppendLine($"    Rename-Item -LiteralPath {Ps(backupDir)} -NewName {Ps(targetName)} -ErrorAction SilentlyContinue");
            builder.AppendLine("    Log 'rolled back'");
            builder.AppendLine($"    Start-Process -FilePath {Ps(exePath)} -WorkingDirectory {Ps(targetDir)}");
            builder.AppendLine("    exit 1");
            builder.AppendLine("}");

            // 3) 拉起新版本，再清掉旧目录
            builder.AppendLine($"Start-Process -FilePath {Ps(exePath)} -WorkingDirectory {Ps(targetDir)}");
            builder.AppendLine("Log 'new version started'");
            builder.AppendLine("Start-Sleep -Seconds 3");
            builder.AppendLine($"Remove-Item -LiteralPath {Ps(backupDir)} -Recurse -Force -ErrorAction SilentlyContinue");
            builder.AppendLine("Log 'done'");

            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return path;
        }

        /// <summary>安装版升级用的脚本：等退出 → 静默跑安装包 → 拉起新版本。</summary>
        private static string WriteInstallerScript(
            string versionDir,
            string installerPath,
            string exePath,
            int processId)
        {
            string path = Path.Combine(versionDir, "apply-installer.ps1");
            string exeDir = Path.GetDirectoryName(exePath) ?? versionDir;
            string logFile = Path.Combine(Path.GetTempPath(), "astrabrew-launcher", "update.log");

            var builder = new StringBuilder();
            builder.AppendLine("$ErrorActionPreference = 'Continue'");
            builder.AppendLine($"$log = {Ps(logFile)}");
            builder.AppendLine("New-Item -ItemType Directory -Force -Path (Split-Path -Parent $log) | Out-Null");
            builder.AppendLine("function Log($m) { Add-Content -LiteralPath $log -Value ('[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $m) }");
            builder.AppendLine("Log 'installer update start'");
            builder.AppendLine($"$targetPid = {processId}");
            builder.AppendLine("$deadline = (Get-Date).AddSeconds(90)");
            builder.AppendLine("while ((Get-Process -Id $targetPid -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 400 }");
            builder.AppendLine("Start-Sleep -Milliseconds 1200");
            builder.AppendLine("Log 'process exited'");
            builder.AppendLine($"$p = Start-Process -FilePath {Ps(installerPath)} -ArgumentList '/quiet','/norestart' -Wait -PassThru");
            builder.AppendLine("Log ('installer exit: ' + $p.ExitCode)");
            // 工作目录必须给程序目录：Windows App SDK 的依赖解析按启动目录找会找空
            builder.AppendLine($"Start-Process -FilePath {Ps(exePath)} -WorkingDirectory {Ps(exeDir)}");
            builder.AppendLine("Log 'new version started'");

            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return path;
        }

        /// <summary>
        /// 清掉上次更新留下的残渣：<c>update\</c> 里的解压目录，以及程序目录旁边可能残留的
        /// <c>.old</c> / <c>.new</c>（更新脚本被杀、或删除旧目录时被占用）。
        /// 只删这两个约定后缀的目录，绝不碰程序目录本身。
        /// </summary>
        public static void CleanupStaging()
        {
            try
            {
                if (Directory.Exists(UpdateRoot))
                {
                    foreach (string folder in Directory.GetDirectories(UpdateRoot))
                    {
                        Directory.Delete(folder, recursive: true);
                    }
                }
            }
            catch (Exception)
            {
                // 正在被更新脚本使用：下次启动再清
            }

            try
            {
                string? exeDir = Path.GetDirectoryName(ExePath);
                if (exeDir is not { Length: > 0 })
                {
                    return;
                }

                foreach (string suffix in new[] { ".old", ".new" })
                {
                    string leftover = exeDir.TrimEnd(Path.DirectorySeparatorChar) + suffix;
                    if (Directory.Exists(leftover))
                    {
                        Directory.Delete(leftover, recursive: true);
                    }
                }
            }
            catch (Exception)
            {
                // 还在被占用：留着，下次再说
            }
        }

        private static string SafeName(string text)
        {
            var builder = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                builder.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
            }

            return builder.Length == 0 ? "update" : builder.ToString();
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // 临时文件删不掉不影响结果
            }
        }
    }
}
