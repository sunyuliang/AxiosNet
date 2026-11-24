using System.ComponentModel;
using AxiosNet.Metadata;

namespace AxiosNet.WebAPI.Metadata;

/// <summary>请求模型声明构建器：由源生成器或 <see cref="AxiosApiMetadata.RegisterRequestModel"/> 使用。</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class RequestModelBuilder
{
    internal RequestModelBuilder(Type modelType)
    {
        Declaration = new RequestModelDeclaration { ModelType = modelType };
    }

    /// <summary>构建中的请求模型声明；<c>Build</c> 后交给注册表。</summary>
    internal RequestModelDeclaration Declaration { get; }

    /// <summary>路由；方法名会被归一化（大写）。</summary>
    public RequestModelBuilder Route(HttpMethod method, string path)
    {
        ArgumentNullException.ThrowIfNull(method);
        Declaration.MethodName = method.Method;
        Declaration.Path = path;
        return this;
    }

    /// <summary>路由（方法名文本形式，归一化后与 <see cref="Route(HttpMethod, string)"/> 等价）。</summary>
    public RequestModelBuilder Route(string method, string path)
    {
        Declaration.MethodName = method;
        Declaration.Path = path;
        return this;
    }

    /// <summary>类级静态头：<c>"Name: Value"</c> 固定值，<c>"Name"</c> 摘除同名头；按参数顺序生效。</summary>
    public RequestModelBuilder StaticHeaders(params string[] headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        Declaration.StaticHeaders.AddRange(headers);
        return this;
    }

    /// <summary>声明 multipart：无标注成员默认按部件绑定。</summary>
    public RequestModelBuilder Multipart()
    {
        Declaration.Multipart = true;
        return this;
    }

    /// <summary>类级 <c>[BodyContentType]</c>：请求体媒体类型（与 <see cref="Multipart"/> 互斥）。</summary>
    public RequestModelBuilder ContentType(string contentType)
    {
        Declaration.ContentType = contentType;
        return this;
    }

    /// <summary>类级 <c>[QueryCollectionFormat]</c>：查询集合的默认编码。</summary>
    public RequestModelBuilder QueryCollectionFormat(AxiosCollectionFormat format)
    {
        Declaration.QueryCollectionFormat = format;
        return this;
    }

    /// <summary>类级 <c>[Stream]</c>：流式响应的默认帧格式。</summary>
    public RequestModelBuilder Stream(AxiosStreamFormat format = AxiosStreamFormat.NewlineDelimitedJson)
    {
        Declaration.HasStreamAttribute = true;
        Declaration.StreamFormat = format;
        return this;
    }

    /// <summary>声明 <c>[Authorize(scheme)]</c>。</summary>
    public RequestModelBuilder Authorize(string scheme)
    {
        Declaration.AuthorizeScheme = scheme;
        return this;
    }

    /// <summary>声明 <c>[AllowAnonymous]</c>。</summary>
    public RequestModelBuilder Anonymous()
    {
        Declaration.AllowAnonymous = true;
        return this;
    }

    /// <summary>标记模型实现 <c>IApiRequest&lt;T&gt;</c>（启动期校验用；生成器静态判定，运行时不再反射接口）。</summary>
    public RequestModelBuilder ImplementsApiRequest()
    {
        Declaration.ImplementsApiRequest = true;
        return this;
    }

    /// <summary>声明一个成员：<paramref name="getValue"/> 是生成期写死的直接 getter（不经过 <c>PropertyInfo</c>）。</summary>
    public RequestModelMemberBuilder Member(string name, Type valueType, Func<object, object?> getValue)
        => AddMember(name, valueType, getValue, null, null);

    /// <summary>
    /// 声明一个成员并一次性配置它的全部绑定（同一属性可同时绑定路径 + 头 + 查询 + 请求体）。
    /// </summary>
    /// <param name="name">成员名（CLR 属性名）。</param>
    /// <param name="valueType">成员类型。</param>
    /// <param name="getValue">直接取值的委托。</param>
    /// <param name="configure">绑定配置；<c>null</c> 表示不标注任何参数特性。</param>
    /// <param name="description">错误消息里的定位文本；<c>null</c> 时用 <c>{模型名}.{成员名}</c>。</param>
    public RequestModelBuilder Member(
        string name,
        Type valueType,
        Func<object, object?> getValue,
        Action<RequestModelMemberBuilder>? configure,
        string? description = null)
    {
        AddMember(name, valueType, getValue, configure, description);
        return this;
    }

    /// <summary>追加一个成员声明并返回它的构建器；参数校验在这里做。</summary>
    private RequestModelMemberBuilder AddMember(
        string name,
        Type valueType,
        Func<object, object?> getValue,
        Action<RequestModelMemberBuilder>? configure,
        string? description)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(valueType);
        ArgumentNullException.ThrowIfNull(getValue);

        var member = new RequestModelMemberDeclaration
        {
            Name = name,
            FieldName = name,
            ValueType = valueType,
            GetValue = getValue,
            Description = description ?? $"{Declaration.ModelType.Name}.{name}"
        };

        Declaration.Members.Add(member);
        var builder = new RequestModelMemberBuilder(this, member);
        configure?.Invoke(builder);
        return builder;
    }
}

/// <summary>请求模型成员构建器：每个绑定方法都返回所属模型构建器，便于连续声明下一个成员。</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class RequestModelMemberBuilder
{
    private readonly RequestModelBuilder _owner;
    private readonly RequestModelMemberDeclaration _member;

    internal RequestModelMemberBuilder(RequestModelBuilder owner, RequestModelMemberDeclaration member)
    {
        _owner = owner;
        _member = member;
    }

    /// <summary>字段名：聚合正文的键名兜底值，同时是表单 / multipart 的默认部件名；空白回退成员名，<paramref name="nameFromAttribute"/> 默认 <c>true</c>（视为显式命名，不再套 JSON 命名策略）。</summary>
    public RequestModelMemberBuilder FieldName(string fieldName, bool nameFromAttribute = true)
    {
        var blank = string.IsNullOrWhiteSpace(fieldName);
        _member.FieldName = blank ? _member.Name : fieldName;
        _member.NameFromAttribute = nameFromAttribute && !blank;
        return this;
    }

    /// <summary>属性级 <c>[BodyContentType]</c>：覆盖类级声明（两层都声明且不同则在构建期报错）。</summary>
    public RequestModelMemberBuilder ContentType(string contentType)
    {
        _member.ContentType = contentType;
        return this;
    }

    /// <summary>属性级 <c>[QueryCollectionFormat]</c>：覆盖类级声明（两层都声明且不同则在构建期报错）。</summary>
    public RequestModelMemberBuilder QueryCollectionFormat(AxiosCollectionFormat format)
    {
        _member.QueryCollectionFormat = format;
        return this;
    }

    /// <summary>属性级 <c>[Stream]</c>：覆盖类级声明（两层都声明且不同则在构建期报错）。</summary>
    public RequestModelMemberBuilder Stream(AxiosStreamFormat format = AxiosStreamFormat.NewlineDelimitedJson)
    {
        _member.HasStreamAttribute = true;
        _member.StreamFormat = format;
        return this;
    }

    /// <summary><c>[Format]</c>：成员级值格式化（只影响库转文本的通道，结构化正文内部归序列化器）。</summary>
    public RequestModelMemberBuilder Format(ValueFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        _member.Format = format;
        return this;
    }

    /// <summary><c>[Path]</c>；省略名字时用成员名。</summary>
    public RequestModelBuilder Path(string? name = null)
    {
        _member.Path = true;
        _member.PathName = name;
        return _owner;
    }

    /// <summary><c>[Query]</c>；省略名字时用成员名。</summary>
    public RequestModelBuilder Query(string? name = null)
    {
        _member.Query = true;
        _member.QueryName = name;
        return _owner;
    }

    /// <summary><c>[Header]</c>；省略名字时用成员名。</summary>
    public RequestModelBuilder Header(string? name = null)
    {
        _member.Header = true;
        _member.HeaderName = name;
        return _owner;
    }

    /// <summary><c>[HeaderCollection]</c>。</summary>
    public RequestModelBuilder HeaderCollection()
    {
        _member.HeaderCollection = true;
        return _owner;
    }

    /// <summary><c>[Body]</c>：省略名字时成员的值就是整份正文（原样发送、独占）；给了名字时作为聚合正文的一个键。</summary>
    public RequestModelBuilder Body(string? name = null)
    {
        _member.Body = true;
        _member.BodyName = name;
        return _owner;
    }

    /// <summary><c>[Part]</c>；省略名字时用字段名。</summary>
    public RequestModelBuilder Part(string? name = null, string? fileName = null, string? contentType = null)
    {
        _member.Part = true;
        _member.PartName = name;
        _member.PartFileName = fileName;
        _member.PartContentType = contentType;
        return _owner;
    }

    /// <summary><c>[ApiIgnore]</c>：完全不绑定（也不参与默认通道判定）。</summary>
    public RequestModelBuilder Ignore()
    {
        _member.Ignore = true;
        return _owner;
    }

    /// <summary>不标注任何参数特性：按默认规则绑定（GET 走 Query，POST / PUT / PATCH 的单一非标量走 Body，multipart 走部件）。</summary>
    public RequestModelBuilder Unannotated() => _owner;
}
