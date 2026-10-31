namespace AxiosNet.Internal;

/// <summary>对象形状：params / 表单 / multipart 展平所需的属性清单（生成期烘焙或反射产出）。</summary>
internal sealed class ObjectShape
{
    public ObjectShape(Type type, ShapeMember[] members, string? error = null)
    {
        Type = type;
        Members = members;
        Error = error;
    }

    /// <summary>形状对应的 CLR 类型。</summary>
    public Type Type { get; }

    /// <summary>参与展平的成员清单，顺序即输出顺序。</summary>
    public ShapeMember[] Members { get; }

    /// <summary>注册期无法完成的形状（例如 configure 抛错）；非 null 时在首次展平该类型时抛出。</summary>
    public string? Error { get; }
}

/// <summary>对象形状里的单个成员：取值委托 + 各种展平形态下的键名。</summary>
internal readonly struct ShapeMember
{
    public ShapeMember(
        string name,
        string fieldName,
        bool ignored,
        Type valueType,
        Func<object, object?> getValue)
    {
        Name = name;
        FieldName = fieldName;
        Ignored = ignored;
        ValueType = valueType;
        GetValue = getValue;
        EscapedName = ParamsSerializer.EscapeKey(name);
        BracketSuffix = "[" + EscapedName + "]";
    }

    /// <summary>CLR 成员名（错误消息 / 诊断用）。</summary>
    public string Name { get; }

    /// <summary>表单 / multipart 字段名（[JsonPropertyName] 或属性名）。</summary>
    public string FieldName { get; }

    /// <summary>query 展平的转义名（根层级）。</summary>
    public string EscapedName { get; }

    /// <summary>query 展平的「[转义名]」后缀（嵌套层级）。</summary>
    public string BracketSuffix { get; }

    /// <summary>是否整体忽略（[JsonIgnore(Always)]）。</summary>
    public bool Ignored { get; }

    /// <summary>成员值的声明类型（展平前）。</summary>
    public Type ValueType { get; }

    /// <summary>从实例取成员值：生成期烘焙的强类型委托，或反射构造的委托。</summary>
    public Func<object, object?> GetValue { get; }
}

/// <summary>自定义只读字典的枚举访问器（生成期烘焙；反射路径见 <see cref="QueryMap"/>）。</summary>
internal sealed class MapAccessor
{
    public MapAccessor(Type type, Func<object, IEnumerable<KeyValuePair<string, object?>>> enumerate)
    {
        Type = type;
        Enumerate = enumerate;
    }

    public Type Type { get; }

    public Func<object, IEnumerable<KeyValuePair<string, object?>>> Enumerate { get; }
}
