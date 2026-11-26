using System.Net.Http;
using AxiosNet.Metadata;

namespace AxiosNet.WebAPI.Internal.Metadata;

/// <summary>
/// 请求模型的「声明数据」：由源生成器烘焙特性值，或由 JIT 反射读取器产出。
/// 只承载事实（路由 / 成员 / 特性），校验与 <see cref="BindingPlan"/> 构建交给 <see cref="RequestModelMetadata"/>。
/// 不可变；构建期错误以 <see cref="Error"/> 携带，首次使用时才抛出。
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

    /// <summary>类级 <c>[BodyContentType]</c> 原文；null 表示类上没有声明。</summary>
    public string? ContentType { get; set; }

    /// <summary>类级 <c>[QueryCollectionFormat]</c>；null 表示类上没有声明。</summary>
    public AxiosCollectionFormat? QueryCollectionFormat { get; set; }

    /// <summary>类级 <c>[Stream]</c> 是否出现；出现时 <see cref="StreamFormat"/> 为声明值。</summary>
    public bool HasStreamAttribute { get; set; }

    public AxiosStreamFormat StreamFormat { get; set; }

    public bool AllowAnonymous { get; set; }

    /// <summary>非 null 表示 [Authorize]，值为声明的 scheme（构建期归一化校验）；null 表示继承默认授权。</summary>
    public string? AuthorizeScheme { get; set; }

    /// <summary>类级静态头（已按「基类在前、派生类在后」排序；构建期解析 "Name: Value"）。</summary>
    public List<string> StaticHeaders { get; } = new();

    /// <summary>成员绑定声明（构建期按名字 / 类型 / 绑定目标校验）。</summary>
    public List<RequestModelMemberDeclaration> Members { get; } = new();
}

/// <summary>请求模型成员（属性）上的绑定声明；绑定通道字段见 <see cref="BindingMemberDeclaration"/>。</summary>
internal sealed class RequestModelMemberDeclaration : BindingMemberDeclaration
{
    /// <summary>表单 / multipart 的默认字段名（[JsonPropertyName] 或属性名）；[Part] 未显式命名时用它。</summary>
    public string FieldName { get; set; } = string.Empty;

    /// <summary><see cref="FieldName"/> 是否来自 <c>[JsonPropertyName]</c>（是则不套 JSON 命名策略）。</summary>
    public bool NameFromAttribute { get; set; }

    /// <summary>取值委托：构建期从属性元数据填好，绑定时只按位置取值；<c>null</c> 表示生成元数据不完整。</summary>
    public Func<object, object?>? GetValue { get; set; }

    /// <summary>属性级 <c>[BodyContentType]</c> 原文；null 表示属性上没有声明。</summary>
    public string? ContentType { get; set; }

    /// <summary>属性级 <c>[QueryCollectionFormat]</c>；null 表示属性上没有声明。</summary>
    public AxiosCollectionFormat? QueryCollectionFormat { get; set; }

    /// <summary>属性级 <c>[Stream]</c> 是否出现；出现时 <see cref="StreamFormat"/> 为声明值。</summary>
    public bool HasStreamAttribute { get; set; }

    public AxiosStreamFormat StreamFormat { get; set; }

    /// <summary><see cref="FieldName"/>，空白时回退属性名（部件名不能为空）。</summary>
    public override string DefaultName => string.IsNullOrWhiteSpace(FieldName) ? Name : FieldName;

    /// <inheritdoc />
    public override bool DefaultNameFromAttribute => NameFromAttribute;

    internal override IValueSource CreateSource(string fallbackDescription)
        => new DelegateValueSource(Name, ValueType, GetValue!, Description ?? fallbackDescription, Format);

    internal override void EnsureSourceAvailable(string memberPath)
    {
        if (GetValue is null)
        {
            throw new InvalidOperationException(
                $"请求模型 {memberPath} 缺少取值实现：生成元数据不完整，请检查源生成器版本与包是否匹配。");
        }
    }
}

/// <summary>接口级声明：全部方法的声明 + 接口级授权 / 静态头。</summary>
/// <remarks>与 <see cref="AxiosNet.WebAPI.Reflection.InterfaceDeclarationValidator"/> 区别：这里是数据，那里是校验器。</remarks>
internal sealed class InterfaceDeclaration
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

    /// <summary>[BodyContentType] 声明的请求体媒体类型；null 表示不覆盖库默认值。</summary>
    public string? ContentType { get; set; }

    /// <summary>[QueryCollectionFormat] 声明的查询集合格式；null 表示不覆盖实例配置。</summary>
    public AxiosCollectionFormat? QueryCollectionFormat { get; set; }

    public bool AllowAnonymous { get; set; }

    public string? AuthorizeScheme { get; set; }

    /// <summary>方法级静态头（覆盖接口级同名项）。</summary>
    public List<string> StaticHeaders { get; } = new();

    /// <summary>方法参数声明（含 CancellationToken 的位置）。</summary>
    public List<InterfaceParameterDeclaration> Parameters { get; } = new();

    public int CancellationTokenIndex { get; set; } = -1;

    public InterfaceReturnShape ReturnShape { get; set; }

    /// <summary>正文类型；<see cref="InterfaceReturnShape.Void"/> 与非泛型响应为 null。</summary>
    public Type? ResponseType { get; set; }

    /// <summary>返回类型的展示文本（如 <c>Task&lt;AxiosResponse&lt;User&gt;&gt;</c>），仅用于错误消息。</summary>
    public string? ReturnTypeText { get; set; }

    /// <summary>[Stream] 是否出现；出现时 <see cref="StreamFormat"/> 为声明值。</summary>
    public bool HasStreamAttribute { get; set; }

    public AxiosStreamFormat StreamFormat { get; set; }
}

/// <summary>接口方法参数的绑定声明；绑定通道字段见 <see cref="BindingMemberDeclaration"/>。</summary>
internal sealed class InterfaceParameterDeclaration : BindingMemberDeclaration
{
    public int Index { get; set; }

    internal override IValueSource CreateSource(string fallbackDescription)
        => new ArgumentValueSource(Index, Name, ValueType, Description ?? fallbackDescription, Format);
}
