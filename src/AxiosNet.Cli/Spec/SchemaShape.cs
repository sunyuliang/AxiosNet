 namespace AxiosNet.Cli.Spec;

/// <summary>schema 的形态类别：决定它会被映射成什么类型，以及能否落成可序列化的标量参数。</summary>
internal enum SchemaShapeKind
{
    /// <summary>枚举（<c>enum</c> 非空）：映射成枚举或基础类型，一定是标量。</summary>
    Enum,

    /// <summary>联合（<c>oneOf</c> / <c>anyOf</c> 非空）：一律降级成 <c>JsonElement</c>，不是标量。</summary>
    Union,

    /// <summary>有非空 <c>properties</c> 或 <c>allOf</c>：生成命名类，不是标量。</summary>
    ClassLike,

    /// <summary><c>type: array</c>：映射成集合。</summary>
    Array,

    /// <summary><c>string</c> / <c>integer</c> / <c>number</c> / <c>boolean</c> / <c>file</c>。</summary>
    Primitive,

    /// <summary>其余：未知类型、多值 <c>type</c>、<c>object</c>、<c>null</c>，按 <see cref="SchemaShape.Type"/> 再分。</summary>
    Unknown
}

/// <summary>
/// 一个 schema 的形态判定结果。<see cref="Of"/> 是把「<c>$ref</c> → enum → oneOf/anyOf → 对象 → 数组 / 原始类型」这条
/// 分支顺序写成代码的唯一位置：类型映射（<c>SchemaMapper</c>）与参数能否落成标量（<c>ApiReader</c>）都只消费它，不再各判一遍。
/// </summary>
/// <remarks>调用方须先解引用并确认 <see cref="SpecNode.IsObject"/>；本类型不自带引用解析，也不产生警告。</remarks>
internal readonly struct SchemaShape
{
    private SchemaShape(SchemaShapeKind kind, string? type, string? format, IReadOnlyList<string>? conflictingTypes)
    {
        Kind = kind;
        Type = type;
        Format = format;
        ConflictingTypes = conflictingTypes;
    }

    /// <summary>形态类别。</summary>
    internal SchemaShapeKind Kind { get; }

    /// <summary>规范化后的 <c>type</c>：<c>type</c> 为数组时剔除 <c>null</c> 后剩下的那一种；多值或未知为 <c>null</c>。</summary>
    internal string? Type { get; }

    /// <summary>原始类型声明的 <c>format</c>；只有 <see cref="SchemaShapeKind.Primitive"/> / <see cref="SchemaShapeKind.Array"/> 有意义。</summary>
    internal string? Format { get; }

    /// <summary>多值 <c>type</c> 剔除 <c>null</c> 后剩下的类型名（至少两个）；非多值为 <c>null</c>，此时 <see cref="Kind"/> 恒为 <see cref="SchemaShapeKind.Unknown"/>。</summary>
    internal IReadOnlyList<string>? ConflictingTypes { get; }

    /// <summary>判定形态；多值 <c>type</c> 先于其余分支判定，与映射顺序一致。</summary>
    internal static SchemaShape Of(SpecNode schema)
    {
        var (type, conflicting) = ReadType(schema);
        if (conflicting is not null)
        {
            return new SchemaShape(SchemaShapeKind.Unknown, type: null, format: null, conflicting);
        }

        if (schema["enum"] is { IsArray: true } enumNode && enumNode.Items.Count > 0)
        {
            return new SchemaShape(SchemaShapeKind.Enum, type, format: null, conflictingTypes: null);
        }

        if (schema["oneOf"] is { IsArray: true } oneOf && oneOf.Items.Count > 0)
        {
            return new SchemaShape(SchemaShapeKind.Union, type, format: null, conflictingTypes: null);
        }

        if (schema["anyOf"] is { IsArray: true } anyOf && anyOf.Items.Count > 0)
        {
            return new SchemaShape(SchemaShapeKind.Union, type, format: null, conflictingTypes: null);
        }

        if (IsClassLike(schema))
        {
            return new SchemaShape(SchemaShapeKind.ClassLike, type, format: null, conflictingTypes: null);
        }

        var format = schema["format"]?.AsString();

        return type switch
        {
            "array" => new SchemaShape(SchemaShapeKind.Array, type, format, conflictingTypes: null),
            "string" or "integer" or "number" or "boolean" or "file"
                => new SchemaShape(SchemaShapeKind.Primitive, type, format, conflictingTypes: null),
            _ => new SchemaShape(SchemaShapeKind.Unknown, type, format, conflictingTypes: null)
        };
    }

    /// <summary>判断 schema 是否应该生成命名类：有非空 <c>properties</c> 或非空 <c>allOf</c>。</summary>
    internal static bool IsClassLike(SpecNode schema)
        => (schema["properties"] is { IsObject: true } properties && properties.Object.Count > 0)
           || (schema["allOf"] is { IsArray: true } allOf && allOf.Items.Count > 0);

    /// <summary>读 <c>type</c>：数组形式剔除 <c>null</c> 后只剩一种才算明确；多种返回冲突列表（并置 <c>Type</c> 为 <c>null</c>）。</summary>
    private static (string? Type, IReadOnlyList<string>? Conflicting) ReadType(SpecNode schema)
    {
        var node = schema["type"];
        if (node is null)
        {
            return (null, null);
        }

        if (!node.IsArray)
        {
            return (node.AsString(), null);
        }

        // 3.1 允许 type 是数组；null 只表达可空，不算真正的第二类型。
        var types = new List<string>();
        foreach (var item in node.Items)
        {
            if (item.AsString() is { Length: > 0 } type && type != "null")
            {
                types.Add(type);
            }
        }

        if (types.Count == 0)
        {
            return ("null", null);
        }

        return types.Count == 1 ? (types[0], null) : (null, types);
    }
}
