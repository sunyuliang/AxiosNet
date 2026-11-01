using System.Globalization;
using AxiosNet.Cli.Model;
using AxiosNet.Cli.Spec;

namespace AxiosNet.Cli.Generation;

/// <summary>
/// OpenAPI schema → C# 类型。对象/枚举会注册成可复用模型，原始类型直接映射；
/// 无法表达的结构（oneOf / anyOf / 元组 / 多类型）降级为 <c>JsonElement</c> 并记录缺口。
/// </summary>
internal sealed class SchemaMapper
{
    /// <summary>指针解析器（同时负责非本地引用的警告）。</summary>
    private readonly RefResolver _refs;

    /// <summary>命令行选项，唯一影响映射结果的是 <c>--integer-long</c>。</summary>
    private readonly CliOptions _options;

    /// <summary>降级 / 缺口警告收集器。</summary>
    private readonly WarningCollector _warnings;

    /// <summary>本次映射累计注册的可复用类型（类 / 枚举 / 常量类）。</summary>
    private readonly List<ApiType> _types = new();

    /// <summary>$ref 原文 → 已映射类型，避免同一引用重复生成，也让循环引用提前收敛。</summary>
    private readonly Dictionary<string, TypeRef> _refTypes = new(StringComparer.Ordinal);

    /// <summary>已占用的类型名（运行库保留名 + 调用方预留给定 + 本次已生成的名字）。</summary>
    private readonly HashSet<string> _usedNames = new(StringComparer.Ordinal);

    /// <summary>schema 递归 / 展开深度上限，防止病态文档把栈打爆（<see cref="Map"/> 与 <see cref="Collect"/> 共用）。</summary>
    private const int MaxDepth = 64;

    /// <summary>当前递归深度，防止病态 schema 把栈打爆。</summary>
    private int _depth;

    /// <summary>构造映射器；<paramref name="reservedNames"/> 来自读取阶段，用来避免模型与请求类 / 接口重名。</summary>
    public SchemaMapper(SpecNode root, IEnumerable<string> reservedNames, CliOptions options, WarningCollector warnings)
    {
        _refs = new RefResolver(root, warnings);
        _options = options;
        _warnings = warnings;

        foreach (var name in Naming.ReservedTypeNames)
        {
            _usedNames.Add(name);
        }

        foreach (var name in reservedNames)
        {
            _usedNames.Add(name);
        }
    }

    /// <summary>映射过程中累计的可复用类型，最后转交给 <see cref="ApiSpec.Types"/>。</summary>
    public IReadOnlyList<ApiType> Types => _types;

    /// <summary>映射入口：非对象直接降级为 <c>JsonElement</c>，并负责递归深度上限的计数与还原。</summary>
    public TypeRef Map(SpecNode? schema, string suggestedName, string location)
    {
        if (schema is not { IsObject: true })
        {
            return TypeRef.JsonElement;
        }

        if (_depth >= MaxDepth)
        {
            _warnings.Add("SCHEMA_DEPTH", location, "schema 嵌套过深，按 JsonElement 处理。");
            return TypeRef.JsonElement;
        }

        _depth++;
        try
        {
            return MapCore(schema, suggestedName, location);
        }
        finally
        {
            _depth--;
        }
    }

    /// <summary>把对象 schema（含 allOf 展开）描述成属性列表，供 multipart 部件逐个展开。</summary>
    public bool TryDescribeObject(SpecNode? schema, string location, out List<ApiObjectProperty> properties)
    {
        properties = new List<ApiObjectProperty>();

        // 顶层是 $ref 时先解引用，再判断能否按类展开。
        var resolved = schema is null ? null : schema["$ref"] is null ? schema : _refs.Resolve(schema, location);
        if (resolved is not { IsObject: true } || !IsClassLike(resolved))
        {
            return false;
        }

        // 属性声明顺序 / 线上名 → schema / 必填集合 / 是否含 additionalProperties。
        var order = new List<string>();
        var schemas = new Dictionary<string, SpecNode>(StringComparer.Ordinal);
        var required = new HashSet<string>(StringComparer.Ordinal);
        var hasExtension = false;
        Collect(
            resolved, order, schemas, required, ref hasExtension,
            new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), location, 0);

        // multipart 部件必须逐个声明：additionalProperties 里的未知字段无法展开，静默丢弃会让调用方以为已覆盖。
        if (hasExtension)
        {
            _warnings.Add(
                "MULTIPART_ADDITIONAL_PROPERTIES",
                location,
                "multipart 请求体声明了 additionalProperties，未知字段无法展开为部件，已忽略。");
        }

        foreach (var wire in order)
        {
            var propertySchema = schemas[wire];
            properties.Add(new ApiObjectProperty(
                wire, propertySchema, required.Contains(wire), Description(propertySchema)));
        }

        return properties.Count > 0;
    }

    /// <summary>真正的映射分支：$ref → enum → oneOf/anyOf → 对象 → 对象/数组/原始类型，未知一律降级。</summary>
    private TypeRef MapCore(SpecNode schema, string suggestedName, string location)
    {
        if (schema["$ref"]?.AsString() is { } reference)
        {
            var mapped = MapReference(reference, location);
            return IsNullable(schema) ? mapped with { Nullable = true } : mapped;
        }

        // 可空性在各分支之间传递；type 为多值时 ReadType 会置 conflict，直接降级。
        var nullable = IsNullable(schema);
        var (typeName, conflict) = ReadType(schema, location);
        if (conflict)
        {
            return new TypeRef("JsonElement", true, nullable);
        }

        if (schema["enum"] is { IsArray: true } enumNode && enumNode.Items.Count > 0)
        {
            return MapEnum(schema, enumNode, suggestedName, location, typeName, nullable);
        }

        if (schema["oneOf"] is { IsArray: true } oneOf && oneOf.Items.Count > 0)
        {
            RegisterUnionMembers(oneOf, suggestedName, location);
            _warnings.Add(
                "ONEOF_FALLBACK",
                location,
                $"oneOf 无法用声明式接口表达：类型按 JsonElement 生成，成员类型一并生成供调用方自行判别。");
            return new TypeRef("JsonElement", true, nullable);
        }

        if (schema["anyOf"] is { IsArray: true } anyOf && anyOf.Items.Count > 0)
        {
            RegisterUnionMembers(anyOf, suggestedName, location);
            _warnings.Add(
                "ANYOF_FALLBACK",
                location,
                $"anyOf 无法用声明式接口表达：类型按 JsonElement 生成，成员类型一并生成供调用方自行判别。");
            return new TypeRef("JsonElement", true, nullable);
        }

        if (IsClassLike(schema))
        {
            return MapInlineClass(schema, suggestedName, location, nullable);
        }

        switch (typeName)
        {
            case "object":
                return MapFreeFormObject(schema, suggestedName, location, nullable);

            case "array":
                return MapArray(schema, suggestedName, location, nullable);

            case "string":
            case "integer":
            case "number":
            case "boolean":
            case "file":
                return MapPrimitive(typeName, schema["format"]?.AsString(), nullable);

            case null:
                return schema["additionalProperties"] is not null
                    ? MapFreeFormObject(schema, suggestedName, location, nullable)
                    : new TypeRef("JsonElement", true, nullable);

            case "null":
                return new TypeRef("JsonElement", true, true);

            default:
                _warnings.Add("UNKNOWN_SCHEMA_TYPE", location, $"未知 schema 类型 {typeName}，按 JsonElement 处理。");
                return new TypeRef("JsonElement", true, nullable);
        }
    }

    /// <summary>oneOf / anyOf 的成员类型照常登记（对象、枚举、别名的引用都会生成），集合本身按 JsonElement 交付。</summary>
    private void RegisterUnionMembers(SpecNode members, string suggestedName, string location)
    {
        for (var index = 0; index < members.Items.Count; index++)
        {
            var member = members.Items[index];
            if (member is { IsObject: true })
            {
                Map(
                    member,
                    suggestedName + "Option" + (index + 1).ToString(CultureInfo.InvariantCulture),
                    location);
            }
        }
    }

    /// <summary>映射一个本地 $ref：对象生成命名类型并递归填充，别名直接映射目标，损坏引用降级。</summary>
    private TypeRef MapReference(string reference, string location)
    {
        if (_refTypes.TryGetValue(reference, out var cached))
        {
            return cached;
        }

        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            _warnings.Add("EXTERNAL_REF", location, $"不支持外部引用 {reference}，按 JsonElement 处理。");
            return TypeRef.JsonElement;
        }

        var target = _refs.ResolvePointer(reference);
        if (target is not { IsObject: true })
        {
            _warnings.Add("BROKEN_REF", location, $"引用不存在：{reference}。");
            return TypeRef.JsonElement;
        }

        var rawName = RefResolver.LastSegment(reference);
        var nullable = IsNullable(target);

        if (IsClassLike(target))
        {
            var name = UniqueTypeName(rawName);
            var type = new ApiType
            {
                Name = name,
                Kind = ApiTypeKind.Class,
                Description = Description(target)
            };

            // 先登记再填属性，循环引用（A 的字段是 B、B 的字段是 A）才能收敛。
            _refTypes[reference] = new TypeRef(name, false, nullable);
            _types.Add(type);
            FillClass(type, target);
            return _refTypes[reference];
        }

        // 别名（primitive / array / enum）：不额外生成包装类型，直接映射目标；占位先打断别名环。
        _refTypes[reference] = TypeRef.JsonElement;
        var mapped = MapCore(target, rawName, location);
        if (nullable)
        {
            mapped = mapped with { Nullable = true };
        }

        _refTypes[reference] = mapped;
        return mapped;
    }

    /// <summary>把内联对象 schema 注册成一个以建议名命名的类（重名自动加序号）。</summary>
    private TypeRef MapInlineClass(SpecNode schema, string suggestedName, string location, bool nullable)
    {
        var name = UniqueTypeName(suggestedName);
        var type = new ApiType
        {
            Name = name,
            Kind = ApiTypeKind.Class,
            Description = Description(schema)
        };

        _types.Add(type);
        FillClass(type, schema);
        return new TypeRef(name, false, nullable);
    }

    /// <summary>映射枚举：字符串且值都合法时生成枚举，否则生成常量类 + string，数字枚举按数值类型。</summary>
    private TypeRef MapEnum(
        SpecNode schema,
        SpecNode enumNode,
        string suggestedName,
        string location,
        string? typeName,
        bool nullable)
    {
        // 枚举字面量原文；非字符串项在这里变成 null，后续按非法值处理。
        var values = enumNode.Items.Select(item => item.AsString()).ToList();

        if (typeName is null)
        {
            typeName = values.All(value =>
                value is not null
                && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                ? "number"
                : "string";
        }

        if (typeName == "string")
        {
            // 判定能否直接生成 C# 枚举：值非空、是合法标识符且互不重复。
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            var valid = values.Count > 0;
            foreach (var value in values)
            {
                // value__ 是枚举的保留成员名（CS0076），不能生成成枚举成员，退回常量类。
                if (string.IsNullOrEmpty(value)
                    || string.Equals(value, "value__", StringComparison.Ordinal)
                    || !Naming.IsIdentifier(value)
                    || !distinct.Add(value))
                {
                    valid = false;
                    break;
                }
            }

            if (valid)
            {
                var name = UniqueTypeName(suggestedName);
                var type = new ApiType
                {
                    Name = name,
                    Kind = ApiTypeKind.Enum,
                    Description = Description(schema)
                };

                // 成员名与枚举类型同名会触发 CS0542，补 Value 后缀；补后缀后再撞名继续去重。
                var usedMembers = new HashSet<string>(StringComparer.Ordinal);
                foreach (var value in values)
                {
                    var memberName = value! == name ? value + "Value" : value!;
                    memberName = Naming.Unique(memberName, usedMembers);
                    type.EnumMembers.Add(new ApiEnumMember(memberName, value!));
                }

                _types.Add(type);
                return new TypeRef(name, true, nullable);
            }

            var constantsName = UniqueTypeName(suggestedName + "Values");
            var constants = new ApiType
            {
                Name = constantsName,
                Kind = ApiTypeKind.Constants,
                Description = Description(schema)
            };

            // 常量名也要去重（如 "foo-bar" 与 "foo_bar" 都会 Pascal 成 FooBar）。
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                if (value is null)
                {
                    continue;
                }

                var memberName = value.Length == 0 ? "Empty" : Naming.Pascal(value);

                // 成员名与所在常量类同名会触发 CS0542（如枚举值 "status-values" → StatusValues）：
                // 与 FillClass 的属性处理一致，补 Value 后缀回避。
                if (memberName == constantsName)
                {
                    memberName += "Value";
                }

                constants.Constants.Add(new ApiConstant(Naming.Unique(memberName, used), value));
            }

            _types.Add(constants);
            _warnings.Add(
                "ENUM_AS_STRING",
                location,
                $"枚举值不能直接作为 C# 成员名：已生成常量类 {constantsName}，属性类型使用 string。");
            return new TypeRef("string", false, nullable);
        }

        if (typeName is "integer" or "number")
        {
            _warnings.Add("NUMERIC_ENUM", location, "数字枚举按数值类型生成。");

            // 没有 type（推断成 number）或声明 type=number 且未写浮点 format 时，全为整数就落成整数：
            // 生成端更贴近契约，也避免 [JsonNumberHandling] 之外的浮点精度问题。
            if (typeName == "number" && schema["format"]?.AsString() is not ("float" or "double" or "decimal"))
            {
                var integralFormat = IntegralEnumFormat(values);
                if (integralFormat is not null)
                {
                    return MapPrimitive("integer", integralFormat, nullable);
                }

                _warnings.Add(
                    "NUMERIC_ENUM_FRACTION",
                    location,
                    "数字枚举含非整数或超出 64 位整数范围的值，按 double 生成。");
            }

            return MapPrimitive(typeName, schema["format"]?.AsString(), nullable);
        }

        _warnings.Add("ENUM_IGNORED", location, $"类型 {typeName} 上的 enum 未处理，按 {typeName} 生成。");
        return MapPrimitive(typeName, schema["format"]?.AsString(), nullable);
    }

    /// <summary>
    /// 数字枚举能否落成整数：全部值可解析为 64 位整数时返回对应的 format（int32 / int64），
    /// 否则返回 <c>null</c> 交由 double 兜底。
    /// </summary>
    private static string? IntegralEnumFormat(IReadOnlyList<string?> values)
    {
        // 全部值都落在 int32 范围内才用 int；更大的整数用 long。
        var withinInt32 = true;
        foreach (var value in values)
        {
            if (value is null
                || !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return null;
            }

            if (parsed is < int.MinValue or > int.MaxValue)
            {
                withinInt32 = false;
            }
        }

        return withinInt32 ? "int32" : "int64";
    }

    /// <summary>映射数组：元组（prefixItems）与缺失 items 降级为 <c>JsonElement[]</c>，否则按元素类型生成 <c>T[]</c>。</summary>
    private TypeRef MapArray(SpecNode schema, string suggestedName, string location, bool nullable)
    {
        if (schema["prefixItems"] is { IsArray: true } prefix && prefix.Items.Count > 0)
        {
            _warnings.Add("TUPLE_ARRAY", location, "元组数组（prefixItems）按 JsonElement[] 生成。");
            return new TypeRef("JsonElement[]", false, nullable);
        }

        var items = schema["items"];
        if (items is not { IsObject: true })
        {
            _warnings.Add("ARRAY_WITHOUT_ITEMS", location, "数组缺少对象形式的 items，按 JsonElement[] 生成。");
            return new TypeRef("JsonElement[]", false, nullable);
        }

        // 元素可空时类型名里带 ?，数组本身的可空由外层 nullable 决定。
        var item = Map(items, suggestedName + "Item", location);
        var itemText = item.Nullable ? item.Name + "?" : item.Name;
        return new TypeRef(itemText + "[]", false, nullable);
    }

    /// <summary>映射无固定属性的对象：additionalProperties 是对象时生成 <c>Dictionary&lt;string, T&gt;</c>，否则降级。</summary>
    private TypeRef MapFreeFormObject(SpecNode schema, string suggestedName, string location, bool nullable)
    {
        if (schema["additionalProperties"] is { IsObject: true } additional)
        {
            // 值类型可空时写成 T?，字典自身的可空仍由外层参数控制。
            var value = Map(additional, suggestedName + "Value", location);
            var valueText = value.Nullable ? value.Name + "?" : value.Name;
            return new TypeRef($"Dictionary<string, {valueText}>", false, nullable);
        }

        return new TypeRef("JsonElement", true, nullable);
    }

    /// <summary>OpenAPI 原始类型（含 format）→ C# 基础类型；未知组合降级为 <c>JsonElement</c>。</summary>
    private TypeRef MapPrimitive(string type, string? format, bool nullable)
    {
        switch (type)
        {
            case "string":
                return format switch
                {
                    "date-time" => new TypeRef("DateTimeOffset", true, nullable),
                    "date" => new TypeRef("DateTime", true, nullable),
                    "time" => new TypeRef("TimeSpan", true, nullable),
                    "uuid" or "guid" => new TypeRef("Guid", true, nullable),
                    "byte" or "binary" => new TypeRef("byte[]", false, nullable),
                    _ => new TypeRef("string", false, nullable)
                };

            case "integer":
                if (format == "int64")
                {
                    return new TypeRef("long", true, nullable);
                }

                if (format == "int32")
                {
                    return new TypeRef("int", true, nullable);
                }

                return _options.IntegerIsLong
                    ? new TypeRef("long", true, nullable)
                    : new TypeRef("int", true, nullable);

            case "number":
                return format switch
                {
                    "float" => new TypeRef("float", true, nullable),
                    "decimal" => new TypeRef("decimal", true, nullable),
                    _ => new TypeRef("double", true, nullable)
                };

            case "boolean":
                return new TypeRef("bool", true, nullable);

            case "file":
                return new TypeRef("byte[]", false, nullable);

            default:
                return new TypeRef("JsonElement", true, nullable);
        }
    }

    /// <summary>读 schema 的 <c>type</c>：数组形式时剔除 null 后只剩一种才算明确，多值返回 Conflict。</summary>
    private (string? Type, bool Conflict) ReadType(SpecNode schema, string location)
    {
        var node = schema["type"];
        if (node is null)
        {
            return (null, false);
        }

        if (!node.IsArray)
        {
            return (node.AsString(), false);
        }

        // 3.1 允许 type 是数组；null 只表达可空，不算真正的第二类型。
        var types = node.Items
            .Select(item => item.AsString())
            .Where(type => !string.IsNullOrEmpty(type) && type != "null")
            .ToList();

        if (types.Count == 0)
        {
            return ("null", false);
        }

        if (types.Count > 1)
        {
            _warnings.Add(
                "MULTIPLE_TYPES",
                location,
                $"type 同时声明了 {string.Join(" / ", types)}，按 JsonElement 处理。");
            return (null, true);
        }

        return (types[0], false);
    }

    /// <summary>把（可能含 allOf 的）对象 schema 展开成 <see cref="ApiType"/> 的属性列表。</summary>
    private void FillClass(ApiType type, SpecNode schema)
    {
        // 与 TryDescribeObject 同结构：属性顺序、线上名 → schema、必填集合、是否有兜底属性。
        var order = new List<string>();
        var schemas = new Dictionary<string, SpecNode>(StringComparer.Ordinal);
        var required = new HashSet<string>(StringComparer.Ordinal);
        var hasExtension = false;

        Collect(
            schema, order, schemas, required, ref hasExtension,
            new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal),
            $"schema {type.Name}", 0);

        // 属性名独立去重，避免 "foo-bar" 与 "foo_bar" 都变成 FooBar。
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var wire in order)
        {
            var propertySchema = schemas[wire];
            var propertyName = Naming.Pascal(wire);

            // 属性名与类名相同会触发 CS0542，补 Value 后缀回避。
            if (propertyName == type.Name)
            {
                propertyName += "Value";
            }

            propertyName = Naming.Unique(propertyName, usedNames);

            type.Properties.Add(new ApiProperty
            {
                Name = propertyName,
                WireName = wire,
                Type = Map(propertySchema, type.Name + propertyName, $"schema {type.Name}.{wire}"),
                Required = required.Contains(wire),
                Description = Description(propertySchema)
            });
        }

        // 有 additionalProperties 时用一个扩展数据属性兜底，保留未知字段；属性名不能与类名相同（CS0542）。
        if (hasExtension)
        {
            type.Properties.Add(new ApiProperty
            {
                Name = Naming.Unique(
                    type.Name == "AdditionalProperties" ? "AdditionalPropertiesValue" : "AdditionalProperties",
                    usedNames),
                WireName = "*",
                Type = new TypeRef("Dictionary<string, JsonElement>", false, true),
                Required = false,
                IsExtensionData = true
            });
        }

        if (type.Properties.Count == 0)
        {
            _warnings.Add("EMPTY_MODEL", $"schema {type.Name}", $"类型 {type.Name} 没有任何属性，生成空类。");
        }
    }

    /// <summary>递归展开 $ref / allOf / properties / required / additionalProperties，按首次出现顺序收集属性。</summary>
    private void Collect(
        SpecNode schema,
        List<string> order,
        Dictionary<string, SpecNode> schemas,
        HashSet<string> required,
        ref bool hasExtension,
        HashSet<string> active,
        HashSet<string> seen,
        string location,
        int depth)
    {
        // allOf 可以无限嵌套（不经过 $ref 环检测），这里必须单独限深，否则深层文档会打爆调用栈。
        if (depth >= MaxDepth)
        {
            _warnings.Add("SCHEMA_DEPTH", location, "allOf / $ref 展开过深，已停止收集属性。");
            return;
        }

        if (schema["$ref"]?.AsString() is { } reference)
        {
            if (!reference.StartsWith("#/", StringComparison.Ordinal))
            {
                _warnings.Add("EXTERNAL_REF", location, $"不支持外部引用 {reference}，已跳过。");
                return;
            }

            if (active.Contains(reference))
            {
                _warnings.Add("ALLOF_CYCLE", location, $"allOf 引用成环：{reference}。");
                return;
            }

            if (!seen.Add(reference))
            {
                return;
            }

            var target = _refs.ResolvePointer(reference);
            if (target is not { IsObject: true })
            {
                _warnings.Add("BROKEN_REF", location, $"引用不存在：{reference}。");
                return;
            }

            active.Add(reference);
            Collect(target, order, schemas, required, ref hasExtension, active, seen, location, depth + 1);
            active.Remove(reference);
            return;
        }

        if (schema["allOf"] is { IsArray: true } allOf)
        {
            foreach (var item in allOf.Items)
            {
                Collect(item, order, schemas, required, ref hasExtension, active, seen, location, depth + 1);
            }
        }

        if (schema["properties"] is { IsObject: true } properties)
        {
            foreach (var (wire, propertySchema) in properties.Properties)
            {
                if (!schemas.ContainsKey(wire))
                {
                    order.Add(wire);
                }

                schemas[wire] = propertySchema;
            }
        }

        if (schema["required"] is { IsArray: true } requiredNode)
        {
            foreach (var item in requiredNode.Items)
            {
                if (item.AsString() is { } name)
                {
                    required.Add(name);
                }
            }
        }

        if (schema["additionalProperties"] is { } additional
            && !(additional.IsBoolean && additional.AsBool() == false))
        {
            hasExtension = true;
        }
    }

    /// <summary>判断 schema 是否应该生成命名类：有非空 properties 或非空 allOf。</summary>
    private static bool IsClassLike(SpecNode schema)
        => (schema["properties"] is { IsObject: true } properties && properties.Object.Count > 0)
           || (schema["allOf"] is { IsArray: true } allOf && allOf.Items.Count > 0);

    /// <summary>判断 schema 是否可空：兼容 3.0 的 nullable / x-nullable 与 3.1 的 <c>type: [..., "null"]</c>。</summary>
    private static bool IsNullable(SpecNode schema)
    {
        if (schema["nullable"]?.AsBool() == true || schema["x-nullable"]?.AsBool() == true)
        {
            return true;
        }

        return schema["type"] is { IsArray: true } type
               && type.Items.Any(item => item.AsString() == "null");
    }

    /// <summary>取类型说明：优先 description，其次 title，都没有返回 null。</summary>
    private static string? Description(SpecNode schema)
    {
        var description = schema["description"]?.AsString();
        if (!string.IsNullOrWhiteSpace(description))
        {
            return description;
        }

        var title = schema["title"]?.AsString();
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }

    /// <summary>把建议名规范成 PascalCase 并在本次运行内取唯一（冲突时加 2、3…）。</summary>
    private string UniqueTypeName(string raw)
        => Naming.Unique(Naming.Pascal(raw), _usedNames);
}
