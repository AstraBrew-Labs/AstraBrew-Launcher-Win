using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AstraBrew_Launcher.Services
{
    /// <summary>工作台打开的是哪种文件。</summary>
    public enum WorkbenchFormat
    {
        /// <summary>PNG 角色卡：内容在 tEXt 块里，图像本身不变。</summary>
        PngCard,

        /// <summary>普通 JSON：角色卡（.json）、世界书、预设。</summary>
        Json,
    }

    /// <summary>
    /// 工作台正在编辑的那份文件。
    ///
    /// 为什么不是「读出来一份对象、改完再整体写」：角色卡把 JSON 塞在 PNG 的文本块里，
    /// 而世界书与预设的字段远多于界面上露出来的那几个（insertion_order、use_regex、prompt_order…）。
    /// 整体重建会把这些看不见的字段抹掉。这里保留原始结构，只改界面上真的编辑过的键：
    /// · PNG：保留全部块与图像数据，只替换 <c>chara</c>/<c>ccv3</c> 块的文本并重算 CRC；
    /// · JSON：用 <see cref="JsonObject"/> 定点改键，其余原样写出。
    /// </summary>
    public sealed class WorkbenchDocument
    {
        /// <summary>一张 PNG 卡里的一个 JSON 文本块。</summary>
        private sealed class CardChunk
        {
            /// <summary>chara 或 ccv3。</summary>
            public string Keyword = string.Empty;

            /// <summary>整份 JSON（v2/v3 是带 spec 的外壳，v1 就是内容本身）。</summary>
            public JsonObject Whole = new();

            /// <summary>外壳里的 data；v1 时与 <see cref="Whole"/> 是同一个对象。</summary>
            public JsonObject Data = new();

            public bool Wrapped;
        }

        private sealed record PngChunk(string Type, byte[] Data);

        private readonly List<PngChunk>? _chunks;
        private readonly List<CardChunk> _cardChunks = new();
        private readonly JsonObject _jsonFile;

        private WorkbenchDocument(string path, WorkbenchFormat format, JsonObject root, JsonObject jsonFile, List<PngChunk>? chunks)
        {
            Path = path;
            Format = format;
            Root = root;
            _jsonFile = jsonFile;
            _chunks = chunks;
        }

        /// <summary>文件的绝对路径。</summary>
        public string Path { get; }

        public WorkbenchFormat Format { get; }

        /// <summary>编辑目标：卡片是 <c>data</c> 那一层，其余是整份 JSON。</summary>
        public JsonObject Root { get; }

        /// <summary>能改动图像内容的卡（PNG）才支持更换卡面。</summary>
        public bool CanReplaceCover => Format == WorkbenchFormat.PngCard;

        /// <summary>
        /// 卡数据来自 <c>ccv3</c> 块。
        /// 有些 v3 卡的块里是**平铺** JSON（没有 spec/data 外壳），只能靠块的种类判断版本。
        /// </summary>
        public bool IsV3Chunk { get; private set; }

        // ==================== 打开 ====================

        /// <summary>打开一份文件；文件不存在、不是合法 PNG/JSON 都抛异常，由调用方转成界面提示。</summary>
        public static WorkbenchDocument Open(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("文件不存在", path);
            }

            bool isPng = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

            if (isPng)
            {
                List<PngChunk> chunks = ReadChunks(File.ReadAllBytes(path));
                var document = new WorkbenchDocument(path, WorkbenchFormat.PngCard, new JsonObject(), new JsonObject(), chunks);

                foreach (string keyword in new[] { "ccv3", "chara" })
                {
                    if (document.TryReadCardChunk(keyword, out CardChunk chunk))
                    {
                        document._cardChunks.Add(chunk);
                    }
                }

                // 优先用 ccv3（v3 规格字段更全），没有就退回 chara，两者都没有就当空卡新建
                CardChunk? primary = document._cardChunks.FirstOrDefault(item => item.Keyword == "ccv3")
                    ?? document._cardChunks.FirstOrDefault();

                if (primary is not null)
                {
                    document.ReplaceRoot(primary.Data);
                    document.IsV3Chunk = primary.Keyword == "ccv3";
                }

                return document;
            }

            JsonNode? node = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));

            if (node is not JsonObject file)
            {
                throw new InvalidDataException("文件内容不是 JSON 对象");
            }

            // 角色卡可能包在 { spec, data } 里；世界书与预设是平铺的
            JsonObject root = file["data"] is JsonObject nested ? nested : file;

            return new WorkbenchDocument(path, WorkbenchFormat.Json, root, file, null);
        }

        /// <summary>编辑目标换成另一层（PNG 卡在 ccv3/chara 之间选一层）。</summary>
        private void ReplaceRoot(JsonObject root)
        {
            if (ReferenceEquals(Root, root))
            {
                return;
            }

            // Root 是只读属性，这里通过清空 + 搬字段的方式让外部持有的引用保持一致
            Root.Clear();
            foreach (KeyValuePair<string, JsonNode?> pair in root)
            {
                Root[pair.Key] = pair.Value?.DeepClone();
            }
        }

        private bool TryReadCardChunk(string keyword, out CardChunk chunk)
        {
            chunk = new CardChunk { Keyword = keyword };

            PngChunk? text = _chunks?.FirstOrDefault(item =>
                item.Type == "tEXt" && KeywordOf(item.Data) == keyword);

            if (text is null)
            {
                return false;
            }

            string base64 = Encoding.UTF8.GetString(text.Data[(keyword.Length + 1)..]);
            string json = DecodeBase64(base64);

            if (json.Length == 0)
            {
                return false;
            }

            try
            {
                if (JsonNode.Parse(json) is not JsonObject whole)
                {
                    return false;
                }

                chunk.Whole = whole;
                chunk.Wrapped = whole["data"] is JsonObject;
                chunk.Data = chunk.Wrapped ? (JsonObject)whole["data"]! : whole;

                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        // ==================== 保存 ====================

        /// <summary>把编辑目标写回文件（先写临时文件再换名，写一半崩掉不会毁掉原文件）。</summary>
        public void Save()
        {
            if (Format == WorkbenchFormat.PngCard)
            {
                SavePng();
                return;
            }

            WriteAtomic(Path, Encoding.UTF8.GetBytes(Serialize(_jsonFile, indented: true)));
        }

        private void SavePng()
        {
            List<PngChunk> chunks = _chunks ?? new List<PngChunk>();

            if (_cardChunks.Count == 0)
            {
                // 原本没有文本块的裸图：补一个 v1 平铺的 chara，酒馆能直接读
                chunks.Add(new PngChunk("tEXt", BuildText("chara", Serialize(Root, indented: false))));
            }
            else
            {
                foreach (CardChunk card in _cardChunks)
                {
                    string text = BuildCardText(card);
                    int index = chunks.FindIndex(item => item.Type == "tEXt" && KeywordOf(item.Data) == card.Keyword);

                    if (index >= 0)
                    {
                        chunks[index] = new PngChunk("tEXt", BuildText(card.Keyword, text));
                    }
                    else
                    {
                        // 只有其中一种块时，把缺的那种补在 IEND 之前，保持两种规格同步
                        chunks.Insert(Math.Max(0, chunks.Count - 1), new PngChunk("tEXt", BuildText(card.Keyword, text)));
                    }
                }
            }

            WriteAtomic(Path, BuildPng(chunks));
        }

        /// <summary>把编辑目标套回这个块原本的外壳形状，再序列化。</summary>
        private string BuildCardText(CardChunk card)
        {
            if (!card.Wrapped)
            {
                return Serialize(Root, indented: false);
            }

            var wrapper = (JsonObject)card.Whole.DeepClone();
            wrapper["data"] = Root.DeepClone();

            return Serialize(wrapper, indented: false);
        }

        /// <summary>
        /// 换一张卡面：用新图的像素数据，配上这张卡自己的文本块。
        /// 直接覆盖原 PNG 会让角色设定全部丢失，所以是「换图不换卡」。
        /// </summary>
        public void ReplaceCover(string newImagePath)
        {
            if (Format != WorkbenchFormat.PngCard)
            {
                throw new InvalidOperationException("只有 PNG 角色卡可以更换卡面");
            }

            List<PngChunk> image = ReadChunks(File.ReadAllBytes(newImagePath));

            // 新图可能自带 chara 块（比如拿另一张卡当封面），一律丢掉，只留我们的
            image.RemoveAll(item => item.Type == "tEXt" && KeywordOf(item.Data) is "chara" or "ccv3");

            var ours = new List<PngChunk>();
            foreach (CardChunk card in _cardChunks)
            {
                ours.Add(new PngChunk("tEXt", BuildText(card.Keyword, BuildCardText(card))));
            }

            if (ours.Count == 0)
            {
                ours.Add(new PngChunk("tEXt", BuildText("chara", Serialize(Root, indented: false))));
            }

            int end = image.FindIndex(item => item.Type == "IEND");
            image.InsertRange(end < 0 ? image.Count : end, ours);

            byte[] bytes = BuildPng(image);
            WriteAtomic(Path, bytes);

            // 块表也要跟着换，否则下一次保存会拿着旧图的结构去写
            _chunks!.Clear();
            _chunks.AddRange(image);
        }

        /// <summary>
        /// 把节点写成 JSON 文本。
        ///
        /// 不用 <c>JsonNode.ToJsonString(options)</c>：这个工程关掉了反射式序列化，
        /// 传进去的 options 一旦被标记为只读就会抛「must specify a TypeInfoResolver」。
        /// <see cref="Utf8JsonWriter"/> 直接吃节点，压根不走序列化器，也就没这问题。
        /// </summary>
        private static string Serialize(JsonNode node, bool indented)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Indented = indented,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
            {
                node.WriteTo(writer);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        // ==================== PNG 块读写 ====================

        private static List<PngChunk> ReadChunks(byte[] bytes)
        {
            if (bytes.Length < 8
                || bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47)
            {
                throw new InvalidDataException("这不是一个 PNG 文件");
            }

            var chunks = new List<PngChunk>();
            int offset = 8;

            while (offset + 12 <= bytes.Length)
            {
                int length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
                if (length < 0 || offset + 12 + length > bytes.Length)
                {
                    break;
                }

                string type = Encoding.ASCII.GetString(bytes, offset + 4, 4);
                byte[] data = bytes.AsSpan(offset + 8, length).ToArray();

                chunks.Add(new PngChunk(type, data));
                offset += 12 + length;

                if (type == "IEND")
                {
                    break;
                }
            }

            if (chunks.Count == 0)
            {
                throw new InvalidDataException("PNG 里没有可用的块");
            }

            return chunks;
        }

        private static byte[] BuildPng(List<PngChunk> chunks)
        {
            using var stream = new MemoryStream();
            stream.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

            // stackalloc 放在循环外：放在循环里每轮都会在栈上多占一格（CA2014）
            Span<byte> header = stackalloc byte[8];
            Span<byte> crc = stackalloc byte[4];

            foreach (PngChunk chunk in chunks)
            {
                BinaryPrimitives.WriteInt32BigEndian(header[..4], chunk.Data.Length);
                Encoding.ASCII.GetBytes(chunk.Type, header[4..8]);
                stream.Write(header);

                stream.Write(chunk.Data);

                byte[] crcInput = new byte[4 + chunk.Data.Length];
                Encoding.ASCII.GetBytes(chunk.Type, crcInput.AsSpan(0, 4));
                chunk.Data.CopyTo(crcInput, 4);

                BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(crcInput));
                stream.Write(crc);
            }

            return stream.ToArray();
        }

        private static string KeywordOf(byte[] data)
        {
            int separator = Array.IndexOf(data, (byte)0);
            return separator <= 0 ? string.Empty : Encoding.ASCII.GetString(data, 0, separator);
        }

        private static byte[] BuildText(string keyword, string text)
        {
            byte[] key = Encoding.ASCII.GetBytes(keyword);
            byte[] value = Encoding.UTF8.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(text)));

            byte[] data = new byte[key.Length + 1 + value.Length];
            key.CopyTo(data, 0);
            data[key.Length] = 0;
            value.CopyTo(data, key.Length + 1);

            return data;
        }

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

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];

            for (uint i = 0; i < 256; i++)
            {
                uint value = i;

                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                }

                table[i] = value;
            }

            return table;
        }

        private static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;

            foreach (byte value in data)
            {
                crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
            }

            return crc ^ 0xFFFFFFFF;
        }

        private static void WriteAtomic(string path, byte[] bytes)
        {
            string temporary = path + ".tmp";

            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
    }
}
