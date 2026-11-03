using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AstraBrew_Launcher.Services
{
    /// <summary>资源分类；导入校验按它挑规则。</summary>
    public enum ResourceKind
    {
        Character,
        WorldBook,
        Preset,
    }

    /// <summary>一次结构检查的结论。<see cref="Format"/> 只在通过时有值，例如 v2 / v3 / 世界书。</summary>
    public sealed record ResourceCheck(bool Ok, string? Error, string Format)
    {
        public static ResourceCheck Pass(string format) => new(true, null, format);

        public static ResourceCheck Fail(string error) => new(false, error, string.Empty);
    }

    /// <summary>
    /// 导入前的结构检查。
    ///
    /// 参考旧版 <c>core/library</c> 的 <c>validate_character</c> / <c>validate_world_book</c> /
    /// <c>validate_preset</c>，但**刻意放松**：那套逐字段校验字段类型（每个数字、每个布尔），
    /// 拦得住「字段类型写错的卡」，也拦得住一大堆酒馆自己都能读的老文件。
    /// 这里只判断「这是不是这一类资源、能不能被酒馆认出来」：
    /// · 角色卡：PNG 里有 chara/ccv3 文本块且能解析；JSON 能解析；spec 在 v1/v2/v3 之列；name 非空；
    /// · 世界书：有 entries（对象或数组）且至少一条有正文；
    /// · 预设：有 prompts，或者有常见的生成参数字段（老预设没有 prompts 用的是 oai_settings 那一套）。
    /// 不检查大小、不检查每个字段的类型、不检查 extensions。
    /// </summary>
    public static class ResourceValidator
    {
        /// <summary>单文件上限：拦掉明显不是资源的巨型文件，正常卡与预设远小于此。</summary>
        private const long MaxBytes = 64L * 1024 * 1024;

        /// <summary>预设里出现任意一个就当作「这是预设」的常见字段。</summary>
        private static readonly string[] PresetSignatures =
        {
            "temperature",
            "top_p",
            "top_k",
            "openai_max_context",
            "openai_max_tokens",
            "openai_model",
            "chat_completion_source",
            "max_context_length",
            "max_context",
            "genamt",
            "amount_gen",
            "stream_openai",
            "wrap_in_quotes",
            "names_behavior",
            "instruct",
        };

        public static ResourceCheck Validate(ResourceKind kind, string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return ResourceCheck.Fail("文件不存在");
                }

                if (new FileInfo(path).Length > MaxBytes)
                {
                    return ResourceCheck.Fail("文件超过 64 MB，不像一份资源");
                }

                return kind switch
                {
                    ResourceKind.Character => ValidateCharacter(path),
                    ResourceKind.WorldBook => ValidateWorldBook(path),
                    _ => ValidatePreset(path),
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ResourceCheck.Fail(ex.Message);
            }
        }

        // ==================== 角色卡 ====================

        private static ResourceCheck ValidateCharacter(string path)
        {
            bool png = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

            if (!png && !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                return ResourceCheck.Fail("角色卡只能是 .png 或 .json");
            }

            JsonObject root;
            bool v3Chunk = false;

            if (png)
            {
                // 复用工作台那套 PNG 文本块解析：ccv3 优先，退回 chara
                WorkbenchDocument document;

                try
                {
                    document = WorkbenchDocument.Open(path);
                }
                catch (InvalidDataException)
                {
                    return ResourceCheck.Fail("不是有效的 PNG 文件");
                }

                root = document.Root;
                v3Chunk = document.IsV3Chunk;

                if (root.Count == 0)
                {
                    return ResourceCheck.Fail("PNG 里没有 chara / ccv3 数据块，不是酒馆角色卡（可能只是张图片）");
                }
            }
            else if (ParseObject(File.ReadAllText(path)) is { } parsed)
            {
                root = parsed;
            }
            else
            {
                return ResourceCheck.Fail("不是合法的 JSON 对象");
            }

            // v2/v3 把正文包在 data 里；v1 是平铺的
            JsonObject data = root["data"] as JsonObject ?? root;

            string spec = Text(root, "spec").ToLowerInvariant();
            string specVersion = Text(root, "spec_version");

            string format;
            if (spec.Contains("v3", StringComparison.Ordinal) || specVersion.StartsWith('3'))
            {
                format = "v3";
            }
            else if (spec.Contains("v2", StringComparison.Ordinal) || specVersion.StartsWith('2'))
            {
                format = "v2";
            }
            else if (spec.Length == 0 && specVersion.Length == 0)
            {
                // 没有 spec 字段：ccv3 块 ⇒ v3；有 data ⇒ v2 写法；否则 v1
                format = v3Chunk ? "v3" : ReferenceEquals(data, root) ? "v1" : "v2";
            }
            else
            {
                return ResourceCheck.Fail($"spec 认不出（{spec} {specVersion}），只支持 v1 / v2 / v3");
            }

            if (Text(data, "name").Trim().Length == 0)
            {
                return ResourceCheck.Fail("角色卡缺少 name 字段");
            }

            return ResourceCheck.Pass(format);
        }

        // ==================== 世界书 ====================

        private static ResourceCheck ValidateWorldBook(string path)
        {
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                return ResourceCheck.Fail("世界书必须是 .json");
            }

            if (ParseObject(File.ReadAllText(path)) is not { } root)
            {
                return ResourceCheck.Fail("不是合法的 JSON 对象");
            }

            JsonNode? container = root["entries"];

            if (container is null)
            {
                return ResourceCheck.Fail("没有 entries 字段，不是世界书");
            }

            List<JsonObject> entries = container switch
            {
                JsonArray array => array.OfType<JsonObject>().ToList(),
                JsonObject map => map.Select(pair => pair.Value).OfType<JsonObject>().ToList(),
                _ => new List<JsonObject>(),
            };

            if (entries.Count == 0)
            {
                return ResourceCheck.Fail("entries 是空的或格式不对（应为对象或数组）");
            }

            if (!entries.Any(entry => Text(entry, "content").Trim().Length > 0))
            {
                return ResourceCheck.Fail("所有条目都没有正文（content 为空）");
            }

            return ResourceCheck.Pass("世界书");
        }

        // ==================== 预设 ====================

        private static ResourceCheck ValidatePreset(string path)
        {
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                return ResourceCheck.Fail("预设必须是 .json");
            }

            if (ParseObject(File.ReadAllText(path)) is not { } root)
            {
                return ResourceCheck.Fail("不是合法的 JSON 对象");
            }

            int prompts = 0;
            if (root["prompts"] is JsonArray array)
            {
                prompts = array.OfType<JsonObject>().Count();
            }
            else if (root["prompts"] is not null)
            {
                return ResourceCheck.Fail("prompts 不是数组，不像酒馆预设");
            }

            bool hasSignature = PresetSignatures.Any(key => root.ContainsKey(key));

            if (prompts == 0 && !hasSignature)
            {
                return ResourceCheck.Fail("既没有 prompts，也没有常见生成参数（temperature / openai_model 等），不像预设");
            }

            return ResourceCheck.Pass(prompts > 0 ? $"{prompts} 条提示词" : "生成参数");
        }

        // ==================== 小工具 ====================

        private static JsonObject? ParseObject(string json)
        {
            try
            {
                return JsonNode.Parse(json) as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string Text(JsonObject? node, string name)
            => node?[name] is JsonValue value && value.TryGetValue(out string? text) ? text ?? string.Empty : string.Empty;
    }
}
