using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>迁移的数据来源。</summary>
    public enum MigrationSource
    {
        None,

        /// <summary>另一个酒馆启动器（SillyTavern Launcher GUI）的数据目录。</summary>
        LauncherGui,

        /// <summary>本启动器的旧版本目录（%AppData%\AstraBrew Launcher 的老布局）。</summary>
        LegacyAstraBrew,
    }

    /// <summary>迁移进度阶段。</summary>
    public enum MigrationStage
    {
        Scanning,
        Copying,
        Verifying,
        Done,
        Failed,
    }

    /// <summary>单个文件的处理方式。</summary>
    public enum FileAction
    {
        New,
        Overwrite,
        Skip,
    }

    /// <summary>GUI 迁移里的一条文件条目。<see cref="RelativePath"/> 相对源根。</summary>
    public sealed record MigrationEntry(string RelativePath, long Bytes, FileAction Action);

    /// <summary>旧版本迁移里的一条目录级动作。</summary>
    public sealed record LegacyMove(string From, string To, string Kind, bool WillMove, string Note);

    /// <summary>
    /// 一次迁移的「预演」。全是读出来的，不写盘——用户在看到清单之前，磁盘状态不会变。
    /// </summary>
    public sealed record MigrationPlan(
        MigrationSource Source,
        string SourceRoot,
        string TargetRoot,
        IReadOnlyList<MigrationEntry> Entries,
        IReadOnlyList<LegacyMove> Moves,
        IReadOnlyList<string> Notices,
        int NewCount,
        int OverwriteCount,
        int SkipCount,
        long TotalBytes,
        string? Error,
        bool Detected = true)
    {
        public static MigrationPlan NotFound(MigrationSource source, string targetRoot, string error) =>
            new(source, string.Empty, targetRoot, Array.Empty<MigrationEntry>(), Array.Empty<LegacyMove>(),
                Array.Empty<string>(), 0, 0, 0, 0, error, Detected: false);

        /// <summary>有没有真正要动的东西。</summary>
        public bool HasWork => NewCount + OverwriteCount + Moves.Count(move => move.WillMove) > 0;
    }

    /// <summary>一次进度回调。</summary>
    public sealed record MigrationProgress(
        MigrationStage Stage,
        string Detail,
        int Done,
        int Total,
        long BytesDone,
        long BytesTotal);

    /// <summary>迁移结果。</summary>
    public sealed record MigrationResult(
        bool Success,
        int Copied,
        int Overwritten,
        int Skipped,
        long BytesCopied,
        string? BackupRoot,
        string? Error);

    /// <summary>
    /// 「把旧数据搬到新家」的两条路：
    ///
    /// 1. **从酒馆启动器 GUI 迁移**：那边把酒馆数据放在
    ///    <c>%AppData%\SillyTavern Launcher GUI\data\st_data</c>，里面就是一份标准的酒馆数据目录
    ///    （config.yaml + default-user/…）。找不到默认位置时允许用户手动选一个 config.yaml。
    ///    确认后整目录复制到本启动器的全局数据目录，同名文件**覆盖**（覆盖前先备份）。
    ///
    /// 2. **从旧版本迁移**：旧版本的数据根就是 <c>%AppData%\AstraBrew Launcher</c>，
    ///    但布局与现在不同——内置环境在 <c>lib\</c>（现在是 <c>bin\</c>），
    ///    酒馆数据多套了一层 <c>data\sillytavern\data\</c>（现在只到 <c>data\sillytavern\</c>）。
    ///    这里按目录**内容**识别组件再改名移动，绝不按旧目录名猜；不认识的目录原样留着。
    ///
    /// 两条路都遵循同一条底线：**只搬、只复制，不删用户数据**。出错就回滚。
    /// </summary>
    public static class OnboardingMigration
    {
        private const string JournalName = ".migrate-journal.txt";

        private const string BackupPrefix = ".migrate-backup-";

        private const string LegacyBackupPrefix = ".migrate-old-";

        /// <summary>旧版启动器的数据根（也是一个酒馆启动器，目录名固定）。</summary>
        public static string GuiDataRoot
        {
            get
            {
                string appData = Path.GetDirectoryName(AppPaths.RoamingRoot) ?? AppPaths.RoamingRoot;
                return Path.Combine(appData, "SillyTavern Launcher GUI", "data", "st_data");
            }
        }

        /// <summary>旧版本的数据根：正好就是本启动器的根目录。</summary>
        public static string LegacyRoot => AppPaths.RoamingRoot;

        /// <summary>GUI 迁移的目标：设置里那个「全局数据目录」。</summary>
        public static string GuiTargetRoot => GlobalTavernConfig.GlobalDataRoot;

        // ==================== 计划 ====================

        /// <summary>默认位置有没有可迁移的数据。命中判据：目录在、且里面有 config.yaml。</summary>
        public static bool TryFindGuiData(out string root)
        {
            root = GuiDataRoot;

            return Directory.Exists(root) && File.Exists(Path.Combine(root, "config.yaml"));
        }

        /// <summary>
        /// 预演一次 GUI 迁移：逐文件比对源与目标，算出「新增 / 覆盖 / 相同」。
        /// 纯读，不改任何东西。
        /// </summary>
        public static MigrationPlan PlanGui(string sourceRoot)
        {
            string target = GuiTargetRoot;

            if (!Directory.Exists(sourceRoot))
            {
                return MigrationPlan.NotFound(MigrationSource.LauncherGui, target, "找不到这个目录");
            }

            string sourceFull = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar);
            string targetFull = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);

            if (string.Equals(sourceFull, targetFull, StringComparison.OrdinalIgnoreCase)
                || targetFull.StartsWith(sourceFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || sourceFull.StartsWith(targetFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return MigrationPlan.NotFound(
                    MigrationSource.LauncherGui,
                    target,
                    "源目录与目标目录重叠，不能从这里迁移");
            }

            var entries = new List<MigrationEntry>();
            var notices = new List<string>();

            int added = 0;
            int overwritten = 0;
            int skipped = 0;
            long bytes = 0;

            try
            {
                foreach (string file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(sourceRoot, file);

                    if (ShouldSkip(relative))
                    {
                        continue;
                    }

                    long size = new FileInfo(file).Length;
                    string destination = Path.Combine(target, relative);

                    FileAction action;

                    if (!File.Exists(destination))
                    {
                        action = FileAction.New;
                        added++;
                        bytes += size;
                    }
                    else
                    {
                        var existing = new FileInfo(destination);

                        if (existing.Length == size
                            && existing.LastWriteTimeUtc >= File.GetLastWriteTimeUtc(file))
                        {
                            action = FileAction.Skip;
                            skipped++;
                        }
                        else
                        {
                            action = FileAction.Overwrite;
                            overwritten++;
                            bytes += size;
                        }
                    }

                    entries.Add(new MigrationEntry(relative, size, action));
                }

                // 空目录（chats/、groups/ 刚建还没内容）也要建出来，否则酒馆会以为没有对应目录
                foreach (string dir in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(sourceRoot, dir);

                    if (!ShouldSkip(relative)
                        && !Directory.Exists(Path.Combine(target, relative))
                        && !Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        entries.Add(new MigrationEntry(relative + Path.DirectorySeparatorChar, 0, FileAction.New));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return MigrationPlan.NotFound(MigrationSource.LauncherGui, target, GitCommand.FirstLine(ex.Message));
            }

            entries.Sort((left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));

            if (overwritten > 0)
            {
                notices.Add($"目标目录里已有 {overwritten} 个同名文件，确认迁移后会被覆盖（覆盖前会先备份）。");
            }

            notices.Add($"迁移目标：{target}");

            return new MigrationPlan(
                MigrationSource.LauncherGui,
                sourceRoot,
                target,
                entries,
                Array.Empty<LegacyMove>(),
                notices,
                added,
                overwritten,
                skipped,
                bytes,
                null);
        }

        /// <summary>
        /// 预演一次旧版本迁移：识别 <c>lib\</c> 里的组件、检查 <c>data\sillytavern\data\</c> 那层多余的嵌套。
        /// 纯读。
        /// </summary>
        public static MigrationPlan PlanLegacy()
        {
            string root = LegacyRoot;
            string target = GuiTargetRoot;

            if (!Directory.Exists(root))
            {
                return MigrationPlan.NotFound(MigrationSource.LegacyAstraBrew, target, "未检测到旧版本数据");
            }

            string lib = Path.Combine(root, "lib");
            string nestedData = Path.Combine(root, "data", "sillytavern", "data");
            string legacyConfig = Path.Combine(root, "config.json");
            string bin = Path.Combine(root, "bin");

            bool hasLib = Directory.Exists(lib);
            bool hasNestedData = Directory.Exists(nestedData);
            bool hasLegacyConfig = File.Exists(legacyConfig);

            if (!hasLib && !hasNestedData && !hasLegacyConfig)
            {
                return MigrationPlan.NotFound(MigrationSource.LegacyAstraBrew, target, "未检测到旧版本数据");
            }

            var moves = new List<LegacyMove>();
            var notices = new List<string>();

            // ── lib\<组件> → bin\<组件>：按内容识别，不信任旧目录名 ──
            if (hasLib)
            {
                var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (string dir in Directory.EnumerateDirectories(lib))
                {
                    string name = ClassifyComponent(dir) ?? Path.GetFileName(dir);
                    string destination = Path.Combine(bin, name);

                    bool willMove = !claimed.Contains(name) && !Directory.Exists(destination);

                    if (!claimed.Add(name))
                    {
                        notices.Add($"{Path.GetFileName(dir)} 与另一个目录都被识别成 {name}，这一份会移入备份目录。");
                    }
                    else if (Directory.Exists(destination))
                    {
                        notices.Add($"bin\\{name} 已存在，旧目录会移入备份目录（不覆盖）。");
                    }

                    moves.Add(new LegacyMove(dir, destination, name, willMove, name));
                }
            }

            // ── data\sillytavern\data\* → data\sillytavern\*：旧版多套了一层 ──
            if (hasNestedData)
            {
                string outer = Path.Combine(root, "data", "sillytavern");

                foreach (string entry in Directory.EnumerateFileSystemEntries(nestedData))
                {
                    string name = Path.GetFileName(entry);
                    string destination = Path.Combine(outer, name);

                    bool willMove = !File.Exists(destination) && !Directory.Exists(destination);

                    if (!willMove)
                    {
                        notices.Add($"data\\sillytavern\\{name} 已存在，这一项不移动（旧数据留在原处）。");
                    }

                    moves.Add(new LegacyMove(entry, destination, "data", willMove, name));
                }
            }

            if (hasLegacyConfig)
            {
                notices.Add("旧版 config.json 不会被解析：启动器设置沿用当前默认值（数据与实例照常迁移）。");
            }

            string instanceRoot = Path.Combine(root, "sillytavern");
            if (Directory.Exists(instanceRoot) && !Directory.Exists(Path.Combine(instanceRoot, "node_modules")))
            {
                notices.Add("旧内置实例缺少 node_modules：迁移后需要在版本管理里补装依赖。");
            }

            notices.Add($"数据目录：{target}");

            return new MigrationPlan(
                MigrationSource.LegacyAstraBrew,
                root,
                target,
                Array.Empty<MigrationEntry>(),
                moves,
                notices,
                0,
                0,
                0,
                0,
                null);
        }

        /// <summary>
        /// 旧版内置 Node 是否需要重装：旧版用 v24、当前规范是 v22，主版本不等就重装。
        /// </summary>
        public static async Task<(bool Needed, string? Version)> CheckLegacyNodeAsync(CancellationToken token)
        {
            string root = Path.Combine(AppPaths.RoamingRoot, "bin", "nodejs");

            if (!Directory.Exists(root))
            {
                return (false, null);
            }

            RuntimeEnvironment env = await RuntimeProbe.ProbeAsync(RuntimeSource.Builtin, force: true)
                .ConfigureAwait(false);

            if (env.Node is null)
            {
                // 有目录但跑不起来：同样是坏的，需要重装
                return (true, null);
            }

            int major = MajorOf(env.Node);
            int expected = MajorOf(NodejsInstaller.Version);

            return (major != expected, env.Node);
        }

        // ==================== 执行：GUI 复制 ====================

        /// <summary>
        /// 执行 GUI 迁移。同名文件先移进 <c>.migrate-backup-&lt;时间戳&gt;</c> 再复制，
        /// 每处理一个文件写一行 journal；中途出错按 journal 逆序回滚。
        /// </summary>
        public static Task<MigrationResult> RunGuiAsync(
            MigrationPlan plan,
            IProgress<MigrationProgress>? progress,
            CancellationToken token) => Task.Run(() =>
        {
            var clock = Stopwatch.StartNew();

            string target = plan.TargetRoot;
            string backupRoot = Path.Combine(target, BackupPrefix + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            string journalPath = Path.Combine(target, JournalName);

            var journal = new List<(string Relative, bool Created, bool BackedUp)>();
            var pending = plan.Entries.Where(entry => entry.Action != FileAction.Skip).ToList();

            int done = 0;
            int copied = 0;
            int overwritten = 0;
            long bytesDone = 0;

            try
            {
                Directory.CreateDirectory(target);
                Directory.CreateDirectory(backupRoot);

                using var writer = new StreamWriter(journalPath, append: false, Encoding.UTF8);

                foreach (MigrationEntry entry in pending)
                {
                    token.ThrowIfCancellationRequested();

                    string destination = Path.Combine(target, entry.RelativePath);

                    if (entry.RelativePath.EndsWith(Path.DirectorySeparatorChar))
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }

                    string source = Path.Combine(plan.SourceRoot, entry.RelativePath);

                    if (Directory.Exists(Path.GetDirectoryName(destination)) is false)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    }

                    bool backed = false;

                    if (entry.Action == FileAction.Overwrite && File.Exists(destination))
                    {
                        string backup = Path.Combine(backupRoot, entry.RelativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                        File.Move(destination, backup, overwrite: true);
                        backed = true;
                    }

                    File.Copy(source, destination, overwrite: true);

                    if (entry.Action == FileAction.Overwrite)
                    {
                        overwritten++;
                    }
                    else
                    {
                        copied++;
                    }

                    journal.Add((entry.RelativePath, entry.Action == FileAction.New, backed));
                    writer.WriteLine($"{entry.Action}|{entry.RelativePath}");
                    writer.Flush();

                    done++;
                    bytesDone += entry.Bytes;

                    // 每 16 个文件或每 4 MB 报一次：刷太快会把 UI 线程按死
                    if (done % 16 == 0 || bytesDone % (4 * 1024 * 1024) < entry.Bytes)
                    {
                        progress?.Report(new MigrationProgress(
                            MigrationStage.Copying,
                            entry.RelativePath,
                            done,
                            pending.Count,
                            bytesDone,
                            plan.TotalBytes));
                    }
                }

                writer.Flush();
                writer.Close();

                // 成功：备份与 journal 都不再有意义
                TempDir.Delete(backupRoot);

                if (File.Exists(journalPath))
                {
                    File.Delete(journalPath);
                }

                progress?.Report(new MigrationProgress(
                    MigrationStage.Done,
                    "迁移完成",
                    done,
                    pending.Count,
                    bytesDone,
                    plan.TotalBytes));

                return new MigrationResult(true, copied, overwritten, plan.SkipCount, bytesDone, null, null);
            }
            catch (OperationCanceledException)
            {
                RollbackGui(target, backupRoot, journal, journalPath);
                return new MigrationResult(false, 0, 0, 0, 0, backupRoot, "已取消，已回滚");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RollbackGui(target, backupRoot, journal, journalPath);

                return new MigrationResult(
                    false,
                    0,
                    0,
                    0,
                    0,
                    backupRoot,
                    IsFileInUse(ex)
                        ? "有文件正被占用：请先在控制台停止酒馆服务，再重试迁移"
                        : GitCommand.FirstLine(ex.Message));
            }
        }, token);

        private static void RollbackGui(
            string target,
            string backupRoot,
            List<(string Relative, bool Created, bool BackedUp)> journal,
            string journalPath)
        {
            for (int index = journal.Count - 1; index >= 0; index--)
            {
                (string relative, bool created, bool backedUp) = journal[index];
                string destination = Path.Combine(target, relative);

                try
                {
                    if (created && File.Exists(destination))
                    {
                        File.Delete(destination);
                    }

                    if (backedUp)
                    {
                        string backup = Path.Combine(backupRoot, relative);

                        if (File.Exists(backup))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                            File.Move(backup, destination, overwrite: true);
                        }
                    }
                }
                catch (Exception)
                {
                    // 回滚尽量做，个别文件失败不该让「还剩下什么」更难看
                }
            }

            TempDir.Delete(backupRoot);

            try
            {
                if (File.Exists(journalPath))
                {
                    File.Delete(journalPath);
                }
            }
            catch (Exception)
            {
                // 忽略
            }
        }

        // ==================== 执行：旧版重组 ====================

        /// <summary>
        /// 执行旧版迁移：目录改名移动（同盘 Move，不复制），每一步记下来，
        /// 出错按逆序挪回去。不认识的目录原样不动。
        /// </summary>
        public static Task<MigrationResult> RunLegacyAsync(
            MigrationPlan plan,
            IProgress<MigrationProgress>? progress,
            CancellationToken token) => Task.Run(() =>
        {
            var moves = plan.Moves.Where(move => move.WillMove).ToList();
            var done = new List<(string From, string To)>();
            string backupRoot = Path.Combine(LegacyRoot, LegacyBackupPrefix + DateTime.Now.ToString("yyyyMMdd-HHmmss"));

            int index = 0;

            try
            {
                Directory.CreateDirectory(Path.Combine(LegacyRoot, "bin"));

                foreach (LegacyMove move in moves)
                {
                    token.ThrowIfCancellationRequested();

                    index++;
                    progress?.Report(new MigrationProgress(
                        MigrationStage.Copying,
                        Path.GetFileName(move.From),
                        index,
                        moves.Count,
                        index,
                        moves.Count));

                    // 目标已存在（混合态）：不覆盖，旧目录移进备份
                    if (File.Exists(move.To) || Directory.Exists(move.To))
                    {
                        Directory.CreateDirectory(backupRoot);
                        MoveInto(backupRoot, move.From);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(move.To)!);
                    MoveItem(move.From, move.To);
                    done.Add((move.From, move.To));
                }

                progress?.Report(new MigrationProgress(
                    MigrationStage.Done,
                    "旧数据已按新规范整理",
                    moves.Count,
                    moves.Count,
                    moves.Count,
                    moves.Count));

                return new MigrationResult(true, done.Count, 0, 0, 0, Directory.Exists(backupRoot) ? backupRoot : null, null);
            }
            catch (OperationCanceledException)
            {
                RollbackLegacy(done);
                return new MigrationResult(false, 0, 0, 0, 0, backupRoot, "已取消，已回滚");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RollbackLegacy(done);

                return new MigrationResult(
                    false,
                    0,
                    0,
                    0,
                    0,
                    backupRoot,
                    IsFileInUse(ex)
                        ? "有文件正被占用：请关闭正在运行的酒馆 / Node.js 进程后重试"
                        : GitCommand.FirstLine(ex.Message));
            }
        }, token);

        private static void RollbackLegacy(List<(string From, string To)> done)
        {
            for (int index = done.Count - 1; index >= 0; index--)
            {
                try
                {
                    MoveItem(done[index].To, done[index].From);
                }
                catch (Exception)
                {
                    // 同上：能回多少回多少
                }
            }
        }

        /// <summary>把一个文件或目录挪到备份目录下（保留原名）。</summary>
        private static void MoveInto(string backupRoot, string source)
        {
            string destination = Path.Combine(backupRoot, Path.GetFileName(source));
            int suffix = 1;

            while (Directory.Exists(destination) || File.Exists(destination))
            {
                destination = Path.Combine(backupRoot, $"{Path.GetFileName(source)}-{suffix++}");
            }

            MoveItem(source, destination);
        }

        private static void MoveItem(string from, string to)
        {
            if (Directory.Exists(from))
            {
                Directory.Move(from, to);
            }
            else if (File.Exists(from))
            {
                File.Move(from, to, overwrite: true);
            }
        }

        // ==================== 旧 Node 重装 ====================

        /// <summary>
        /// 把内置 Node 换成当前规范版本：旧目录先改名保住，装失败再挪回来。
        /// 旧版是 v24、当前规范是 v22，依赖与离线包都按 v22 校验过，混用会出只在部分机器复现的问题。
        /// </summary>
        public static async Task<InstallResult> ReinstallNodeAsync(
            IProgress<InstallProgress>? progress,
            CancellationToken token)
        {
            string current = NodejsInstaller.InstallRoot;
            string stash = Path.Combine(LegacyRoot, LegacyBackupPrefix + "nodejs-" + DateTime.Now.ToString("HHmmss"));

            bool stashed = false;

            try
            {
                if (Directory.Exists(current))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(stash)!);
                    Directory.Move(current, stash);
                    stashed = true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new InstallResult(
                    false,
                    InstallError.Verify,
                    "旧版 Node.js 正在使用，无法替换",
                    "请先在控制台停止酒馆服务，再重试迁移。",
                    null);
            }

            InstallResult result = await NodejsInstaller.InstallAsync(progress, token).ConfigureAwait(false);

            if (result.Success)
            {
                TempDir.Delete(stash);
                return result;
            }

            if (stashed)
            {
                try
                {
                    if (!Directory.Exists(current))
                    {
                        Directory.Move(stash, current);
                    }
                }
                catch (Exception)
                {
                    // 还原失败也要把路径告诉用户
                }
            }

            return result;
        }

        // ==================== 杂项 ====================

        /// <summary>这些目录出现在酒馆数据里属于异常，迁移它们只会拖慢流程。</summary>
        private static bool ShouldSkip(string relative)
        {
            string[] parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return parts.Any(part =>
                part.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                || part.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || part.Equals("__pycache__", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>按目录内容识别旧版内置组件；识别不了返回 null（原样保留）。</summary>
        private static string? ClassifyComponent(string directory)
        {
            if (File.Exists(Path.Combine(directory, "node.exe")))
            {
                return "nodejs";
            }

            if (File.Exists(Path.Combine(directory, "caddy.exe")))
            {
                return "caddy";
            }

            if (File.Exists(Path.Combine(directory, "cmd", "git.exe"))
                || File.Exists(Path.Combine(directory, "bin", "git.exe")))
            {
                return "mingit";
            }

            if (File.Exists(Path.Combine(directory, "node_modules", "pm2", "package.json")))
            {
                return "pm2";
            }

            return null;
        }

        private static int MajorOf(string? version)
        {
            if (version is not { Length: > 0 })
            {
                return -1;
            }

            string trimmed = version.Trim().TrimStart('v', 'V');
            int dot = trimmed.IndexOf('.');

            return int.TryParse(dot > 0 ? trimmed[..dot] : trimmed, out int major) ? major : -1;
        }

        private static bool IsFileInUse(Exception ex)
        {
            string message = ex.Message;
            return message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
                || message.Contains("另一个程序正在使用", StringComparison.Ordinal);
        }
    }
}
