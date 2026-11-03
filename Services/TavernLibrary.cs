using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Services
{
    /// <summary>角色卡内嵌世界书的一条，与世界书文件里的条目共用。</summary>
    public sealed record LibraryEntry(string Title, string Keywords, string Body, bool Enabled);

    /// <summary>一张真实角色卡：从 PNG 的 <c>chara</c>/<c>ccv3</c> 文本块或同名 .json 解析出来。</summary>
    public sealed record CharacterCard(
        string FileName,
        string FullPath,
        long Bytes,
        DateTime Modified,
        string Name,
        string Creator,
        string Version,
        string Description,
        string Personality,
        string Scenario,
        string FirstMessage,
        IReadOnlyList<string> Tags,
        string Spec,
        int PixelWidth,
        int PixelHeight,
        string? EmbeddedWorldName,
        IReadOnlyList<LibraryEntry> EmbeddedEntries,
        bool Unreadable);

    /// <summary>一本世界书（<c>worlds/*.json</c>）。</summary>
    public sealed record WorldBook(
        string FileName,
        string FullPath,
        long Bytes,
        DateTime Modified,
        string Name,
        int EntryCount,
        int EnabledCount,
        IReadOnlyList<LibraryEntry> Entries);

    /// <summary>对话预览里的一行。</summary>
    public sealed record ChatLine(string Speaker, bool IsUser, string Time, string Body);

    /// <summary>一份对话存档（<c>chats/&lt;名字&gt;/*.jsonl</c>）。</summary>
    public sealed record ChatArchive(
        string Group,
        string FileName,
        string FullPath,
        long Bytes,
        DateTime Modified,
        int MessageCount,
        int UserCount,
        IReadOnlyList<ChatLine> Preview);

    /// <summary>预设里的一条提示词。</summary>
    public sealed record PresetPrompt(string Name, string Role, string Position, bool Enabled);

    /// <summary>一个预设文件（<c>* Settings/*.json</c>）。</summary>
    public sealed record PresetFile(
        string Folder,
        string FileName,
        string FullPath,
        long Bytes,
        DateTime Modified,
        string Name,
        string Model,
        string Api,
        bool Stream,
        int MaxContext,
        int MaxTokens,
        string Format,
        IReadOnlyList<PresetPrompt> Prompts,
        int EnabledCount);

    /// <summary>一次扫描的全部结果。</summary>
    public sealed record LibrarySnapshot(
        IReadOnlyList<CharacterCard> Characters,
        IReadOnlyList<WorldBook> Worlds,
        IReadOnlyList<ChatArchive> Chats,
        IReadOnlyList<PresetFile> Presets,
        string UserRoot,
        string? Error);

    /// <summary>
    /// 资源管理页的真实数据来源：角色卡、世界书、对话、预设，全部直接从酒馆的数据目录读。
    ///
    /// 以前这里是写死的十几张示例卡与几本示例世界书，界面好看，但和磁盘上有什么毫无关系——
    /// 用户新建一张卡、删一个存档，页面上看不出任何变化。这里只读不改，
    /// 导入/删除由页面调用 <see cref="ImportAsync"/> 与 <see cref="ShellFile"/>。
    /// </summary>
    /// <summary>导入时被跳过的文件与原因。</summary>
    public sealed record ImportFailure(string FileName, string Reason);

    /// <summary>一次导入的结果：成功几个、哪些被跳过（附原因）。</summary>
    public sealed record ImportReport(int Imported, IReadOnlyList<ImportFailure> Failed, string? Error);

    public static class TavernLibrary
    {
        private static readonly JsonDocumentOptions JsonOptions = new()
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        };

        public static string UserDirectory() => TavernRuntime.ResolveDefaultUser();

        /// <summary>角色卡目录；默认用户目录不存在时退回数据根上的同名目录（老布局）。</summary>
        public static string CharactersDirectory() => Prefer(Path.Combine(UserDirectory(), "characters"));

        public static string WorldsDirectory() => Prefer(Path.Combine(UserDirectory(), "worlds"));

        public static string ChatsDirectory() => Prefer(Path.Combine(UserDirectory(), "chats"));

        /// <summary>
        /// 预设按来源分目录（OpenAI / TextGen / KoboldAI / NovelAI …），页面上统称「预设」。
        /// 排序把 OpenAI 放最前：那是绝大多数人在用的那套，其余按字母序跟在后面。
        /// </summary>
        public static IReadOnlyList<string> PresetDirectories()
        {
            var roots = new List<string>();
            string userDir = UserDirectory();

            try
            {
                if (Directory.Exists(userDir))
                {
                    roots.AddRange(Directory
                        .GetDirectories(userDir)
                        .Where(dir => System.IO.Path.GetFileName(dir).EndsWith(" Settings", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(dir => Rank(System.IO.Path.GetFileName(dir)))
                        .ThenBy(dir => dir, StringComparer.OrdinalIgnoreCase));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 目录读不到就当没有预设
            }

            return roots;

            static int Rank(string name) => name.Equals("OpenAI Settings", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        }

        /// <summary>数据根上有没有同名目录，有就说明是老布局（没有 default-user 那一层）。</summary>
        private static string Prefer(string defaultUserPath)
        {
            if (Directory.Exists(defaultUserPath))
            {
                return defaultUserPath;
            }

            string legacy = Path.Combine(
                TavernRuntime.ResolveUserRoot(),
                System.IO.Path.GetFileName(defaultUserPath));

            return Directory.Exists(legacy) ? legacy : defaultUserPath;
        }

        // ==================== 扫描 ====================

        /// <summary>
        /// 扫一遍全部资源。文件 IO 与 JSON 解析都在后台线程跑，
        /// 角色卡动辄几 MB、聊天记录上千行，不能压 UI 线程。
        /// </summary>
        public static Task<LibrarySnapshot> LoadAsync()
        {
            return Task.Run(() =>
            {
                string userRoot = UserDirectory();

                try
                {
                    var characters = LoadCharacters();
                    var worlds = LoadWorldBooks();
                    var chats = LoadChats();
                    var presets = LoadPresets();

                    return new LibrarySnapshot(characters, worlds, chats, presets, userRoot, null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new LibrarySnapshot(
                        Array.Empty<CharacterCard>(),
                        Array.Empty<WorldBook>(),
                        Array.Empty<ChatArchive>(),
                        Array.Empty<PresetFile>(),
                        userRoot,
                        ex.Message);
                }
            });
        }

        private static IReadOnlyList<CharacterCard> LoadCharacters()
        {
            var cards = new List<CharacterCard>();
            string root = CharactersDirectory();

            if (!Directory.Exists(root))
            {
                return cards;
            }

            foreach (string file in SafeFiles(root, "*.png", "*.json"))
            {
                CharacterCard? card = ReadCharacter(file);
                if (card is not null)
                {
                    cards.Add(card);
                }
            }

            // 与酒馆一致：最近改过的排在最前面
            return cards.OrderByDescending(card => card.Modified).ToList();
        }

        /// <summary>读一张角色卡：PNG 走文本块，JSON 直接按 chara_card_v2/v3 结构读。</summary>
        public static CharacterCard? ReadCharacter(string file)
        {
            long bytes;
            DateTime modified;

            try
            {
                var info = new FileInfo(file);
                bytes = info.Length;
                modified = info.LastWriteTime;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            bool isPng = file.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
            string payload = string.Empty;
            string spec = isPng ? "chara_card_v2" : "chara_card_v1";
            int width = 0;
            int height = 0;

            try
            {
                if (isPng)
                {
                    PngScan scan = ReadPng(file);

                    width = scan.Width;
                    height = scan.Height;

                    // ccv3 是 v3 规格的完整字段，chara 是兼容用的 v2 副本；两个都在时以 ccv3 为准
                    string? encoded = scan.Ccv3 ?? scan.Chara;
                    if (encoded is null)
                    {
                        return new CharacterCard(
                            System.IO.Path.GetFileName(file), file, bytes, modified,
                            System.IO.Path.GetFileNameWithoutExtension(file), string.Empty, string.Empty,
                            string.Empty, string.Empty, string.Empty, string.Empty,
                            Array.Empty<string>(), "无元数据", width, height,
                            null, Array.Empty<LibraryEntry>(), Unreadable: true);
                    }

                    payload = DecodeBase64(encoded);
                    spec = scan.Ccv3 is null ? "chara_card_v2" : "chara_card_v3";
                }
                else
                {
                    payload = File.ReadAllText(file, Encoding.UTF8);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return null;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(payload, JsonOptions);
                JsonElement root = document.RootElement;

                // v2/v3 把内容包在 data 里，v1 直接平铺在根上
                JsonElement data = root;
                if (root.TryGetProperty("data", out JsonElement nested) && nested.ValueKind == JsonValueKind.Object)
                {
                    data = nested;

                    if (root.TryGetProperty("spec", out JsonElement declared) && declared.ValueKind == JsonValueKind.String)
                    {
                        spec = declared.GetString() ?? spec;
                    }
                }
                else if (isPng)
                {
                    spec = "chara_card_v1";
                }

                var tags = new List<string>();
                if (data.TryGetProperty("tags", out JsonElement tagArray) && tagArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement tag in tagArray.EnumerateArray())
                    {
                        if (tag.ValueKind == JsonValueKind.String && tag.GetString() is { Length: > 0 } value)
                        {
                            tags.Add(value);
                        }
                    }
                }

                string? worldName = null;
                var entries = new List<LibraryEntry>();

                if (data.TryGetProperty("character_book", out JsonElement book) && book.ValueKind == JsonValueKind.Object)
                {
                    worldName = Text(book, "name");

                    if (book.TryGetProperty("entries", out JsonElement bookEntries))
                    {
                        foreach (JsonElement entry in EnumerateEntries(bookEntries))
                        {
                            entries.Add(ReadBookEntry(entry));
                        }
                    }
                }

                string name = Text(data, "name") ?? System.IO.Path.GetFileNameWithoutExtension(file);

                return new CharacterCard(
                    System.IO.Path.GetFileName(file),
                    file,
                    bytes,
                    modified,
                    name,
                    Text(data, "creator") ?? string.Empty,
                    Text(data, "character_version") ?? string.Empty,
                    Text(data, "description") ?? string.Empty,
                    Text(data, "personality") ?? string.Empty,
                    Text(data, "scenario") ?? string.Empty,
                    Text(data, "first_mes") ?? Text(data, "first_message") ?? string.Empty,
                    tags,
                    spec,
                    width,
                    height,
                    worldName,
                    entries,
                    Unreadable: false);
            }
            catch (JsonException)
            {
                return new CharacterCard(
                    System.IO.Path.GetFileName(file), file, bytes, modified,
                    System.IO.Path.GetFileNameWithoutExtension(file), string.Empty, string.Empty,
                    string.Empty, string.Empty, string.Empty, string.Empty,
                    Array.Empty<string>(), "解析失败", width, height,
                    null, Array.Empty<LibraryEntry>(), Unreadable: true);
            }
        }

        /// <summary>只读世界书（工作台绑定内嵌世界书时用得到，不必把角色卡与聊天也扫一遍）。</summary>
        public static IReadOnlyList<WorldBook> LoadWorldBooks()
        {
            var books = new List<WorldBook>();
            string root = WorldsDirectory();

            if (!Directory.Exists(root))
            {
                return books;
            }

            foreach (string file in SafeFiles(root, "*.json"))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8), JsonOptions);
                    JsonElement rootElement = document.RootElement;

                    JsonElement entriesElement = rootElement;
                    if (rootElement.ValueKind == JsonValueKind.Object
                        && rootElement.TryGetProperty("entries", out JsonElement nested))
                    {
                        entriesElement = nested;
                    }

                    var entries = new List<LibraryEntry>();
                    foreach (JsonElement entry in EnumerateEntries(entriesElement))
                    {
                        entries.Add(ReadBookEntry(entry));
                    }

                    var info = new FileInfo(file);

                    books.Add(new WorldBook(
                        System.IO.Path.GetFileName(file),
                        file,
                        info.Length,
                        info.LastWriteTime,
                        System.IO.Path.GetFileNameWithoutExtension(file),
                        entries.Count,
                        entries.Count(entry => entry.Enabled),
                        entries));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    // 单个文件坏了就跳过，不要让整页资源变空
                }
            }

            return books.OrderByDescending(book => book.Modified).ToList();
        }

        private static IReadOnlyList<ChatArchive> LoadChats()
        {
            var chats = new List<ChatArchive>();
            string root = ChatsDirectory();

            if (!Directory.Exists(root))
            {
                return chats;
            }

            string[] folders;
            try
            {
                folders = Directory.GetDirectories(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return chats;
            }

            foreach (string folder in folders)
            {
                string group = System.IO.Path.GetFileName(folder);

                foreach (string file in SafeFiles(folder, "*.jsonl"))
                {
                    ChatArchive? chat = ReadChat(group, file);
                    if (chat is not null)
                    {
                        chats.Add(chat);
                    }
                }
            }

            return chats
                .OrderBy(chat => chat.Group, StringComparer.CurrentCultureIgnoreCase)
                .ThenByDescending(chat => chat.Modified)
                .ToList();
        }

        private static ChatArchive? ReadChat(string group, string file)
        {
            try
            {
                var info = new FileInfo(file);
                var lines = new List<ChatLine>();
                int total = 0;
                int user = 0;

                foreach (string raw in File.ReadLines(file, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        continue;
                    }

                    total++;

                    ChatLine? line = ParseChatLine(raw);
                    if (line is null)
                    {
                        continue;
                    }

                    if (line.IsUser)
                    {
                        user++;
                    }

                    // 只留最后几条做预览：整份存档可能有几千行，全塞进内存没意义
                    lines.Add(line);
                    if (lines.Count > 8)
                    {
                        lines.RemoveAt(0);
                    }
                }

                return new ChatArchive(
                    group,
                    System.IO.Path.GetFileName(file),
                    file,
                    info.Length,
                    info.LastWriteTime,
                    total,
                    user,
                    lines);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static ChatLine? ParseChatLine(string raw)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(raw, JsonOptions);
                JsonElement root = document.RootElement;

                bool isUser = root.TryGetProperty("is_user", out JsonElement userFlag)
                    && userFlag.ValueKind == JsonValueKind.True;

                string speaker = Text(root, "name") ?? "未知";
                string body = Text(root, "mes") ?? string.Empty;

                if (body.Length == 0)
                {
                    return null;
                }

                string time = string.Empty;
                if (Text(root, "send_date") is { Length: > 0 } stamp
                    && DateTime.TryParse(stamp, CultureInfo.CurrentCulture, DateTimeStyles.None, out DateTime parsed))
                {
                    time = parsed.ToString("HH:mm");
                }

                return new ChatLine(speaker, isUser, time, body);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static IReadOnlyList<PresetFile> LoadPresets()
        {
            var presets = new List<PresetFile>();
            var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (string folder in PresetDirectories())
            {
                string label = System.IO.Path.GetFileName(folder);
                order[label] = order.Count;

                foreach (string file in SafeFiles(folder, "*.json"))
                {
                    PresetFile? preset = ReadPreset(label, file);
                    if (preset is not null)
                    {
                        presets.Add(preset);
                    }
                }
            }

            // 按来源目录分组（OpenAI 在最前），组内最近改过的排前面
            return presets
                .OrderBy(preset => order.TryGetValue(preset.Folder, out int index) ? index : 99)
                .ThenByDescending(preset => preset.Modified)
                .ToList();
        }

        private static PresetFile? ReadPreset(string folder, string file)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8), JsonOptions);
                JsonElement root = document.RootElement;
                var info = new FileInfo(file);

                // 结构化生成参数在各家接口上字段名不同，取第一个有值的。
                // 预设文件按来源分四种（OpenAI / TextGen / KoboldAI / NovelAI），
                // 只认 openai_* 会让另外三家全部显示成「模型 —、上下文 —」。
                string model = FirstText(root, "openai_model", "claude_model", "google_model",
                    "openrouter_model", "mistralai_model", "custom_model", "model") ?? "—";

                string api = Text(root, "chat_completion_source") ?? ApiOfFolder(folder);

                int maxContext = Int(root, "openai_max_context")
                    ?? Int(root, "max_context_length")
                    ?? Int(root, "max_context")
                    ?? 0;

                int maxTokens = Int(root, "openai_max_tokens")
                    ?? Int(root, "genamt")
                    ?? Int(root, "amount_gen")
                    ?? Int(root, "response_tokens")
                    ?? 0;

                bool stream = IsTrue(root, "stream_openai") || IsTrue(root, "stream");

                var prompts = new List<PresetPrompt>();
                var enabledMap = ReadPromptOrder(root);

                if (root.TryGetProperty("prompts", out JsonElement promptArray) && promptArray.ValueKind == JsonValueKind.Array)
                {
                    int index = 0;

                    foreach (JsonElement prompt in promptArray.EnumerateArray())
                    {
                        index++;

                        string identifier = Text(prompt, "identifier") ?? string.Empty;
                        bool enabled = !enabledMap.TryGetValue(identifier, out bool declared) || declared;

                        int position = Int(prompt, "injection_position") ?? 0;
                        int depth = Int(prompt, "injection_depth") ?? 0;

                        prompts.Add(new PresetPrompt(
                            Text(prompt, "name") ?? $"提示词 {index}",
                            Text(prompt, "role") ?? "system",
                            $"{(position == 1 ? "绝对" : "相对")}深度 {depth} · 序号 {index}",
                            enabled));
                    }
                }

                return new PresetFile(
                    folder,
                    System.IO.Path.GetFileName(file),
                    file,
                    info.Length,
                    info.LastWriteTime,
                    System.IO.Path.GetFileNameWithoutExtension(file),
                    model,
                    api,
                    stream,
                    maxContext,
                    maxTokens,
                    enabledMap.Count > 0 ? "完整" : "简化",
                    prompts,
                    prompts.Count(prompt => prompt.Enabled));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }

        /// <summary>预设目录 → 它对应的接口名，与酒馆 settings.json 里的 <c>main_api</c> 取值一致。</summary>
        private static string ApiOfFolder(string folder) => folder switch
        {
            "OpenAI Settings" => "openai",
            "TextGen Settings" => "textgenerationwebui",
            "KoboldAI Settings" => "kobold",
            "NovelAI Settings" => "novel",
            _ => "—",
        };

        /// <summary><c>prompt_order</c> 里声明的启用状态：形如
        /// <c>[{ "character_id": 100000, "order": [{ "identifier": "main", "enabled": true }] }]</c>。
        /// 100000 是「默认」那一组，没有就用第一组。
        /// </summary>
        private static Dictionary<string, bool> ReadPromptOrder(JsonElement root)
        {
            var map = new Dictionary<string, bool>(StringComparer.Ordinal);

            if (!root.TryGetProperty("prompt_order", out JsonElement orders) || orders.ValueKind != JsonValueKind.Array)
            {
                return map;
            }

            JsonElement target = default;
            bool found = false;

            foreach (JsonElement order in orders.EnumerateArray())
            {
                if (!found)
                {
                    target = order;
                    found = true;
                }

                if (Int(order, "character_id") == 100000)
                {
                    target = order;
                    break;
                }
            }

            if (!found || !target.TryGetProperty("order", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
            {
                return map;
            }

            foreach (JsonElement item in list.EnumerateArray())
            {
                string? identifier = Text(item, "identifier");
                if (identifier is null)
                {
                    continue;
                }

                map[identifier] = item.TryGetProperty("enabled", out JsonElement enabled)
                    && enabled.ValueKind == JsonValueKind.True;
            }

            return map;
        }

        // ==================== 导入 ====================

        /// <summary>
        /// 校验后把外部文件复制进分类目录。同名文件自动加后缀，绝不覆盖已有资源——
        /// 导入同名角色卡时覆盖掉的那一张，通常正是用户最舍不得丢的那一张。
        ///
        /// 结构检查在复制**之前**：把一张普通 PNG 当角色卡塞进 characters 目录，
        /// 列表里就多一条打不开的空卡，用户还得自己回去找是哪个文件。
        /// 单个文件不合格只跳过它，不影响同批其它文件。
        /// </summary>
        public static Task<ImportReport> ImportAsync(ResourceKind kind, string folder, IEnumerable<string> files)
        {
            return Task.Run(() =>
            {
                int imported = 0;
                var failed = new List<ImportFailure>();

                try
                {
                    Directory.CreateDirectory(folder);

                    foreach (string file in files)
                    {
                        string name = System.IO.Path.GetFileName(file);

                        if (!File.Exists(file))
                        {
                            failed.Add(new ImportFailure(name, "文件不存在"));
                            continue;
                        }

                        ResourceCheck check = ResourceValidator.Validate(kind, file);
                        if (!check.Ok)
                        {
                            failed.Add(new ImportFailure(name, check.Error ?? "结构检查未通过"));
                            continue;
                        }

                        File.Copy(file, UniquePath(folder, name), overwrite: false);
                        imported++;
                    }

                    return new ImportReport(imported, failed, null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new ImportReport(imported, failed, ex.Message);
                }
            });
        }

        private static string UniquePath(string folder, string fileName)
        {
            string target = Path.Combine(folder, fileName);
            if (!File.Exists(target))
            {
                return target;
            }

            string stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
            string extension = System.IO.Path.GetExtension(fileName);

            for (int i = 2; ; i++)
            {
                target = Path.Combine(folder, $"{stem} ({i}){extension}");
                if (!File.Exists(target))
                {
                    return target;
                }
            }
        }

        // ==================== PNG 文本块 ====================

        private sealed record PngScan(int Width, int Height, string? Chara, string? Ccv3);

        /// <summary>
        /// 从 PNG 里抠出 <c>tEXt</c>/<c>zTXt</c> 块。
        /// 角色卡把整份 JSON 以 base64 塞进 <c>chara</c>（v2 兼容）与 <c>ccv3</c>（v3）两个块里，
        /// 位置不固定（通常在图数据之后），所以必须顺序扫完整条块链，不能只读文件头。
        /// </summary>
        private static PngScan ReadPng(string file)
        {
            using FileStream stream = File.OpenRead(file);

            Span<byte> signature = stackalloc byte[8];
            if (stream.Read(signature) != 8
                || signature[0] != 0x89 || signature[1] != 0x50 || signature[2] != 0x4E || signature[3] != 0x47)
            {
                return new PngScan(0, 0, null, null);
            }

            int width = 0;
            int height = 0;
            string? chara = null;
            string? ccv3 = null;

            Span<byte> header = stackalloc byte[8];
            var buffer = new byte[64 * 1024];

            while (true)
            {
                if (stream.Read(header) != 8)
                {
                    break;
                }

                int length = BinaryPrimitives.ReadInt32BigEndian(header[..4]);
                string type = Encoding.ASCII.GetString(header[4..8]);

                if (length < 0)
                {
                    break;
                }

                if (type == "IEND")
                {
                    break;
                }

                bool wanted = type is "tEXt" or "zTXt" or "iTXt" or "IHDR";

                if (!wanted || length > 32 * 1024 * 1024)
                {
                    stream.Seek(length + 4L, SeekOrigin.Current);
                    continue;
                }

                if (buffer.Length < length)
                {
                    buffer = new byte[length];
                }

                int read = 0;
                while (read < length)
                {
                    int got = stream.Read(buffer, read, length - read);
                    if (got <= 0)
                    {
                        return new PngScan(width, height, chara, ccv3);
                    }

                    read += got;
                }

                stream.Seek(4, SeekOrigin.Current); // CRC

                ReadOnlySpan<byte> data = buffer.AsSpan(0, length);

                switch (type)
                {
                    case "IHDR" when length >= 8:
                        width = BinaryPrimitives.ReadInt32BigEndian(data[..4]);
                        height = BinaryPrimitives.ReadInt32BigEndian(data[4..8]);
                        break;

                    case "tEXt":
                        (string key, string value) = SplitKeyword(data);
                        if (key == "chara")
                        {
                            chara = value;
                        }
                        else if (key == "ccv3")
                        {
                            ccv3 = value;
                        }

                        break;

                    case "zTXt":
                        // 布局：keyword\0 + 压缩方法(1 字节) + zlib 数据
                        int separator = data.IndexOf((byte)0);
                        if (separator > 0)
                        {
                            string compressedKey = Encoding.ASCII.GetString(data[..separator]);
                            int valueStart = separator + 2;

                            if (compressedKey is "chara" or "ccv3" && valueStart < data.Length)
                            {
                                try
                                {
                                    using var input = new MemoryStream(data[valueStart..].ToArray());
                                    using var inflate = new ZLibStream(input, CompressionMode.Decompress);
                                    using var output = new MemoryStream();
                                    inflate.CopyTo(output);
                                    string text = Encoding.UTF8.GetString(output.ToArray());

                                    if (compressedKey == "chara")
                                    {
                                        chara = text;
                                    }
                                    else
                                    {
                                        ccv3 = text;
                                    }
                                }
                                catch (Exception ex) when (ex is InvalidDataException or IOException)
                                {
                                    // 压缩块坏了就当没有
                                }
                            }
                        }

                        break;
                }
            }

            return new PngScan(width, height, chara, ccv3);
        }

        /// <summary>PNG 文本块形如 <c>keyword\0text</c>。</summary>
        private static (string Key, string Value) SplitKeyword(ReadOnlySpan<byte> data)
        {
            int separator = data.IndexOf((byte)0);
            if (separator <= 0)
            {
                return (string.Empty, string.Empty);
            }

            string key = Encoding.ASCII.GetString(data[..separator]);
            string value = Encoding.UTF8.GetString(data[(separator + 1)..]);

            return (key, value);
        }

        /// <summary>角色卡里的 base64 可能是标准表，也可能带换行，解不开就返回空串。</summary>
        private static string DecodeBase64(string encoded)
        {
            try
            {
                string clean = new(encoded.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
                return Encoding.UTF8.GetString(Convert.FromBase64String(clean));
            }
            catch (FormatException)
            {
                return string.Empty;
            }
        }

        // ==================== 小工具 ====================

        /// <summary>世界书的 <c>entries</c> 是对象字典（新）也可能是数组（老），两种都认。</summary>
        private static IEnumerable<JsonElement> EnumerateEntries(JsonElement entries)
        {
            switch (entries.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (JsonElement entry in entries.EnumerateArray())
                    {
                        yield return entry;
                    }

                    break;

                case JsonValueKind.Object:
                    foreach (JsonProperty property in entries.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.Object)
                        {
                            yield return property.Value;
                        }
                    }

                    break;
            }
        }

        private static LibraryEntry ReadBookEntry(JsonElement entry)
        {
            string title = Text(entry, "comment") ?? Text(entry, "name") ?? string.Empty;

            var keys = new List<string>();
            if (entry.TryGetProperty("key", out JsonElement keyElement))
            {
                if (keyElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement key in keyElement.EnumerateArray())
                    {
                        if (key.ValueKind == JsonValueKind.String && key.GetString() is { Length: > 0 } value)
                        {
                            keys.Add(value);
                        }
                    }
                }
                else if (keyElement.ValueKind == JsonValueKind.String && keyElement.GetString() is { Length: > 0 } single)
                {
                    keys.Add(single);
                }
            }

            if (title.Length == 0)
            {
                title = keys.Count > 0 ? keys[0] : "未命名条目";
            }

            // 世界书用 disable，角色卡内嵌条目用 enabled，两边语义相反
            bool enabled = true;
            if (entry.TryGetProperty("disable", out JsonElement disabled) && disabled.ValueKind == JsonValueKind.True)
            {
                enabled = false;
            }
            else if (entry.TryGetProperty("enabled", out JsonElement flag) && flag.ValueKind == JsonValueKind.False)
            {
                enabled = false;
            }

            return new LibraryEntry(
                title,
                keys.Count > 0 ? "关键词：" + string.Join('、', keys) : "无关键词",
                Text(entry, "content") ?? string.Empty,
                enabled);
        }

        /// <summary>只枚举文件，目录不存在或没权限都返回空——页面按「没有资源」显示。</summary>
        private static IEnumerable<string> SafeFiles(string folder, params string[] patterns)
        {
            foreach (string pattern in patterns)
            {
                string[] files;

                try
                {
                    files = Directory.GetFiles(folder, pattern, SearchOption.TopDirectoryOnly);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (string file in files)
                {
                    yield return file;
                }
            }
        }

        private static string? Text(JsonElement element, string name)
            => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static string? FirstText(JsonElement element, params string[] names)
        {
            foreach (string name in names)
            {
                if (Text(element, name) is { Length: > 0 } value)
                {
                    return value;
                }
            }

            return null;
        }

        private static int? Int(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt32(out int number) => number,
                JsonValueKind.String when int.TryParse(value.GetString(), out int parsed) => parsed,
                _ => null,
            };
        }

        private static bool IsTrue(JsonElement element, string name)
            => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
    }
}
