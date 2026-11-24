using System.Text;

namespace AxiosNet.Cli.Generation;

/// <summary>C# 标识符与名字的规范化；生成代码里的名字全部经过这里，保证确定性与合法性。</summary>
internal static class Naming
{
    /// <summary>与框架 / 生成代码同命名空间的类型重名会遮蔽重要类型，生成模型名避开这些名字。</summary>
    public static readonly string[] ReservedTypeNames =
    {
        "Task", "CancellationToken", "AxiosResponse", "AxiosCollectionFormat", "AxiosStreamFormat",
        "IAsyncEnumerable", "JsonElement", "JsonConverter", "JsonStringEnumConverter", "JsonPropertyName",
        "JsonExtensionData", "Dictionary", "List", "Stream", "DateTime", "DateTimeOffset", "TimeSpan",
        "Guid", "String", "Boolean", "Int32", "Int64", "Double", "Decimal",
        "ApiRequest", "IApiRequest", "IApiRequestConfigurable", "AxiosConfig",

        // 生成代码里用到的特性类：同名模型会遮蔽它们，导致 [Get] / [Authorize] 等解析成 CS0616。
        "GetAttribute", "PostAttribute", "PutAttribute", "PatchAttribute", "DeleteAttribute",
        "HeadAttribute", "OptionsAttribute", "HttpApiAttribute", "AuthorizeAttribute",
        "AllowAnonymousAttribute", "BodyContentTypeAttribute", "MultipartAttribute", "StreamAttribute",
        "QueryCollectionFormatAttribute", "BodyAttribute", "PartAttribute", "PathAttribute",
        "QueryAttribute", "HeaderAttribute", "JsonConverterAttribute", "ObsoleteAttribute"
    };

    /// <summary>C# 保留关键字（不含上下文关键字）：做标识符时必须加 <c>@</c> 转义。</summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class",
        "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event",
        "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if",
        "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new",
        "null", "object", "operator", "out", "override", "params", "private", "protected", "public",
        "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static",
        "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong",
        "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while"
    };

    /// <summary>把任意名字切成 PascalCase：按非字母数字切块，每块首字母大写、其余原样保留。</summary>
    public static string Pascal(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "Value";
        }

        // 最终输出与「当前正在累积的一段连续字母数字」。
        var builder = new StringBuilder(raw.Length);
        var chunk = new StringBuilder();

        // 按码位遍历：代理对不会被拆成两个 UTF-16 单元、各算一次分隔符。
        foreach (var rune in raw.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                AppendLetter(chunk, rune);
            }
            else
            {
                Flush(chunk, builder);
            }
        }

        Flush(chunk, builder);

        if (builder.Length == 0)
        {
            return "Value";
        }

        var result = builder.ToString();
        return char.IsDigit(result[0]) ? "_" + result : result;
    }

    /// <summary>
    /// 把一个字母 / 数字附加到当前块：BMP 内原样附加，BMP 外写成 <c>_u</c> + <c>X4</c> 格式的十六进制码位（如 <c>_u20000</c>）。
    /// </summary>
    /// <param name="chunk">当前累积的字母数字块。</param>
    /// <param name="rune">待附加的码位。</param>
    /// <remarks>C# 标识符只接受 BMP 码位；同一码位每次得到同一个名字，原文仍由调用方另行保留。</remarks>
    private static void AppendLetter(StringBuilder chunk, Rune rune)
    {
        if (rune.IsBmp)
        {
            chunk.Append((char)rune.Value);
            return;
        }

        chunk.Append("_u").Append(rune.Value.ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>把累积的一段字母数字写入输出：首字符大写、其余保持原样，然后清空累积缓冲。</summary>
    private static void Flush(StringBuilder chunk, StringBuilder target)
    {
        if (chunk.Length == 0)
        {
            return;
        }

        var text = chunk.ToString();
        target.Append(char.ToUpperInvariant(text[0]));
        if (text.Length > 1)
        {
            target.Append(text, 1, text.Length - 1);
        }

        chunk.Clear();
    }

    /// <summary>PascalCase 转 camelCase，并对 C# 关键字加 <c>@</c>。</summary>
    public static string Camel(string? raw)
    {
        // Pascal 一定非空且只含 BMP 字符，取首字符转小写即可得到 camelCase。
        var pascal = Pascal(raw);
        var name = char.ToLowerInvariant(pascal[0]) + pascal[1..];
        return Escape(name);
    }

    /// <summary>关键字加 <c>@</c> 前缀转义，其余标识符原样返回。</summary>
    public static string Escape(string identifier)
        => Keywords.Contains(identifier) ? "@" + identifier : identifier;

    /// <summary>判断是否是不加转义的合法 C# 标识符（非关键字、首字符为字母或下划线、后续仅字母数字下划线；按 UTF-16 单元判定，代理对不算字母）。</summary>
    public static bool IsIdentifier(string value)
    {
        if (value.Length == 0 || Keywords.Contains(value))
        {
            return false;
        }

        if (!char.IsLetter(value[0]) && value[0] != '_')
        {
            return false;
        }

        for (var i = 1; i < value.Length; i++)
        {
            if (!char.IsLetterOrDigit(value[i]) && value[i] != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>tag → 接口名：<c>users</c> → <c>IUsersApi</c>，已以 Api 结尾时不重复追加。</summary>
    public static string InterfaceNameForTag(string tag)
    {
        // tag 已以 Api 结尾时只加前缀 I，避免出现 IUsersApiApi。
        var pascal = Pascal(tag);
        return pascal.EndsWith("Api", StringComparison.OrdinalIgnoreCase)
            ? "I" + pascal
            : "I" + pascal + "Api";
    }

    /// <summary>在集合里取唯一名字，冲突时追加 2、3…（集合会被更新）。</summary>
    public static string Unique(string baseName, ISet<string> used)
    {
        if (TryAddUnique(baseName, used))
        {
            return baseName;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = baseName + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (TryAddUnique(candidate, used))
            {
                return candidate;
            }
        }
    }

    /// <summary>把候选名加入集合；比较时忽略关键字转义前缀 <c>@</c>（<c>@class2</c> 与 <c>class2</c> 视为同名），规则沿用集合自身的比较器。</summary>
    private static bool TryAddUnique(string candidate, ISet<string> used)
    {
        var comparer = used is HashSet<string> set ? set.Comparer : StringComparer.Ordinal;
        var plain = StripEscape(candidate);
        foreach (var existing in used)
        {
            if (comparer.Equals(StripEscape(existing), plain))
            {
                return false;
            }
        }

        used.Add(candidate);
        return true;
    }

    /// <summary>去掉关键字转义前缀 <c>@</c>，得到成员在 C# 里的实际名字。</summary>
    private static string StripEscape(string name)
        => name.Length > 0 && name[0] == '@' ? name[1..] : name;
}
