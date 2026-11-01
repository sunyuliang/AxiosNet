using System.Globalization;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace AxiosNet.Cli.Spec;

/// <summary>按内容而不是扩展名选择 JSON / YAML 解析器：以 <c>{</c> / <c>[</c> 开头先按 JSON 解析，失败再回退 YAML；其余交给 YAML。</summary>
internal static class SpecParser
{
    /// <summary>解析文档文本：去 BOM、空文档报错，再按首个非空白字符选择 JSON 或 YAML（JSON 失败时回退 YAML）。</summary>
    public static SpecNode Parse(string text, string sourceName)
    {
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        var trimmed = text.TrimStart();
        if (trimmed.Length == 0)
        {
            throw new GenerationException($"{sourceName} 是空文档。");
        }

        if (trimmed[0] is not ('{' or '['))
        {
            return ParseYaml(text, sourceName);
        }

        try
        {
            return ParseJson(text, sourceName);
        }
        catch (GenerationException jsonError)
        {
            // YAML 是 JSON 的超集：流式映射 / 序列同样以 { / [ 开头，JSON 解析失败时按 YAML 再试一次。
            // 两者都失败仍抛原始的 JSON 错误——文档写的就是 JSON 风格，那个消息更有指向性。
            try
            {
                return ParseYaml(text, sourceName);
            }
            catch (GenerationException)
            {
                throw jsonError;
            }
        }
    }

    /// <summary>解析 JSON：容忍尾随逗号与注释，失败时包装成 <see cref="GenerationException"/>。</summary>
    private static SpecNode ParseJson(string text, string sourceName)
    {
        try
        {
            using var document = JsonDocument.Parse(
                text,
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return Convert(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new GenerationException($"{sourceName} 不是合法的 JSON：{ex.Message}", ex);
        }
    }

    /// <summary>解析 YAML：启用重复键检查，反序列化出的 CLR 对象再统一转成 <see cref="SpecNode"/>。</summary>
    private static SpecNode ParseYaml(string text, string sourceName)
    {
        try
        {
            var deserializer = new DeserializerBuilder().WithDuplicateKeyChecking().Build();
            var plain = deserializer.Deserialize(new StringReader(text));
            return SpecNode.FromPlain(plain);
        }
        catch (YamlException ex)
        {
            throw new GenerationException($"{sourceName} 不是合法的 YAML：{ex.Message}", ex);
        }
    }

    /// <summary>把 <see cref="JsonElement"/> 递归转换成 <see cref="SpecNode"/>（对象键按序数比较，重复键报生成错误）。</summary>
    private static SpecNode Convert(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Object => ToObject(element),
            JsonValueKind.Array => SpecNode.Arr(element.EnumerateArray().Select(Convert).ToList()),
            JsonValueKind.String => SpecNode.Str(element.GetString()!),
            JsonValueKind.Number => Number(element),
            JsonValueKind.True => SpecNode.Bool(true),
            JsonValueKind.False => SpecNode.Bool(false),
            _ => SpecNode.Null
        };

    /// <summary>
    /// 逐个属性转换 JSON 对象：<see cref="JsonElement"/> 允许同一对象出现重复键，
    /// 直接用 <c>ToDictionary</c> 会抛原始 <see cref="ArgumentException"/>，这里改成带键名的生成错误。
    /// </summary>
    private static SpecNode ToObject(JsonElement element)
    {
        var result = new Dictionary<string, SpecNode>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (result.ContainsKey(property.Name))
            {
                throw new GenerationException($"JSON 对象包含重复键：{property.Name}。");
            }

            result[property.Name] = Convert(property.Value);
        }

        return SpecNode.Obj(result);
    }

    /// <summary>读取 JSON 数字：优先 double；超出范围时保留原文（避免精度丢失），仍不可解析则退回字符串。</summary>
    private static SpecNode Number(JsonElement element)
    {
        if (element.TryGetDouble(out var value))
        {
            return SpecNode.Num(value);
        }

        return double.TryParse(element.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            ? SpecNode.Num(value)
            : SpecNode.Str(element.GetRawText());
    }
}
