using System.Net.Http;
using AxiosNet.Internal;

namespace AxiosNet.WebAPI.Internal;

/// <summary>
/// 请求模型的「声明数据」：由源生成器烘焙特性值，或由 JIT 反射读取器产出。
/// 它只是事实（路由 / 成员 / 特性），校验与 <see cref="BindingPlan"/> 构建统一交给 <see cref="RequestModelMetadata"/>。
/// 不可变：注册后不再修改；构建期的错误以 <see cref="Error"/> 形式携带，首次使用时才抛出。
/// </summary>
internal sealed class RequestModelDeclaration
{
    public Type ModelType { get; init; } = null!;

    /// <summary>HTTP 方法名（生成器写 "GET" / "POST"，反射读 <see cref="HttpMethod.Method"/>）；空白表示声明损坏。</summary>
    public string? MethodName { get; set; }

    /// <summary>路径模板（未归一化，构建期 Trim）。</summary>
    public string? Path { get; set; }

    /// <summary>注册时无法完成的构建（例如生成器 configure 抛错）；非 null 时替换整份声明。</summary>
    public string? Error { get; set; }

    public bool ImplementsApiRequest { get; set; }

    public bool Multipart { get; set; }

    public bool AllowAnonymous { get; set; }

    /// <summary>非 null 表示 [Authorize]，值为声明的 scheme（构建期归一化校验）；null 表示继承默认授权。</summary>
    public string? AuthorizeScheme { get; set; }

    /// <summary>类级静态头（已按「基类在前、派生类在后」排序；构建期解析 "Name: Value"）。</summary>
    public List<string> StaticHeaders { get; } = new();

    public List<RequestModelMemberDeclaration> Members { get; } = new();
}

/// <summary>请求模型成员（属性）上的绑定声明。</summary>
internal sealed class RequestModelMemberDeclaration
{
    public string Name { get; set; } = string.Empty;

    /// <summary>表单 / multipart 的默认字段名（[JsonPropertyName] 或属性名）；[Part] 未显式命名时用它。</summary>
    public string FieldName { get; set; } = string.Empty;

    public Type ValueType { get; set; } = typeof(object);

    public Func<object, object?>? GetValue { get; set; }

    /// <summary>错误消息里的定位文本（如 <c>UserRequest.Id</c>）。</summary>
    public string? Description { get; set; }

    public bool Ignore { get; set; }

    public bool Path { get; set; }

    public string? PathName { get; set; }

    public bool Query { get; set; }

    public string? QueryName { get; set; }

    public bool Header { get; set; }

    public string? HeaderName { get; set; }

    public bool HeaderCollection { get; set; }

    public bool Body { get; set; }

    public bool Part { get; set; }

    public string? PartName { get; set; }

    public string? PartFileName { get; set; }

    public string? PartContentType { get; set; }

    public bool HasAnyBinding => Path || Query || Header || HeaderCollection || Body || Part;
}

/// <summary>接口级声明的声明数据（全部方法的声明 + 接口级授权 / 静态头）。</summary>
internal sealed class InterfaceDeclarationData
{
    public Type InterfaceType { get; init; } = null!;

    public string? Error { get; set; }

    public bool AllowAnonymous { get; set; }

    public string? AuthorizeScheme { get; set; }

    /// <summary>接口链上的静态头（基接口在前、派生接口在后）。</summary>
    public List<string> StaticHeaders { get; } = new();

    /// <summary>方法顺序即生成代理里的方法索引，注册与代理必须一致。</summary>
    public List<InterfaceMethodDeclaration> Methods { get; } = new();
}

/// <summary>接口方法的声明数据。</summary>
internal sealed class InterfaceMethodDeclaration
{
    public string Name { get; set; } = string.Empty;

    public string? MethodName { get; set; }

    public string? Path { get; set; }

    public string? Error { get; set; }

    public bool Multipart { get; set; }

    public bool AllowAnonymous { get; set; }

    public string? AuthorizeScheme { get; set; }

    /// <summary>方法级静态头（覆盖接口级同名项）。</summary>
    public List<string> StaticHeaders { get; } = new();

    public List<InterfaceParameterDeclaration> Parameters { get; } = new();

    public int CancellationTokenIndex { get; set; } = -1;

    public InterfaceReturnShape ReturnShape { get; set; }

    /// <summary>正文类型；<see cref="InterfaceReturnShape.Void"/> 与非泛型响应为 null。</summary>
    public Type? ResponseType { get; set; }

    /// <summary>返回类型的展示文本（如 <c>Task&lt;AxiosResponse&lt;User&gt;&gt;</c>），只用于错误消息；反射路径填，生成路径留空。</summary>
    public string? ReturnTypeText { get; set; }

    /// <summary>[Stream] 是否出现；出现时 <see cref="StreamFormat"/> 为声明值。</summary>
    public bool HasStreamAttribute { get; set; }

    public AxiosStreamFormat StreamFormat { get; set; }
}

/// <summary>接口方法参数的绑定声明。</summary>
internal sealed class InterfaceParameterDeclaration
{
    public int Index { get; set; }

    public string Name { get; set; } = string.Empty;

    public Type ValueType { get; set; } = typeof(object);

    public string? Description { get; set; }

    public bool Ignore { get; set; }

    public bool Path { get; set; }

    public string? PathName { get; set; }

    public bool Query { get; set; }

    public string? QueryName { get; set; }

    public bool Header { get; set; }

    public string? HeaderName { get; set; }

    public bool HeaderCollection { get; set; }

    public bool Body { get; set; }

    public bool Part { get; set; }

    public string? PartName { get; set; }

    public string? PartFileName { get; set; }

    public string? PartContentType { get; set; }

    public bool HasAnyBinding => Path || Query || Header || HeaderCollection || Body || Part;
}

/// <summary>对象形状：params / 表单 / multipart 展平所需的属性清单（生成期烘焙或反射产出）。</summary>
internal sealed class ObjectShape
{
    public ObjectShape(Type type, ShapeMember[] members, string? error = null)
    {
        Type = type;
        Members = members;
        Error = error;
    }

    public Type Type { get; }

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

    public Type ValueType { get; }

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
