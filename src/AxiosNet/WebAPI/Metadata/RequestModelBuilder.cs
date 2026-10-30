using System.ComponentModel;
using AxiosNet.WebAPI.Internal;

namespace AxiosNet.WebAPI.Metadata;

/// <summary>请求模型声明构建器：由源生成器或 <see cref="AxiosMetadata.RegisterRequestModel"/> 使用。</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class RequestModelBuilder
{
    internal RequestModelBuilder(Type modelType)
    {
        Declaration = new RequestModelDeclaration { ModelType = modelType };
    }

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
            Description = $"{Declaration.ModelType.Name}.{name}"
        };

        Declaration.Members.Add(member);
        return new RequestModelMemberBuilder(this, member);
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

    /// <summary>表单 / multipart 的字段名（默认与成员名相同；生成器写入 <c>[JsonPropertyName]</c> 时调用）。</summary>
    public RequestModelMemberBuilder FieldName(string fieldName)
    {
        _member.FieldName = fieldName;
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

    /// <summary><c>[Body]</c>。</summary>
    public RequestModelBuilder Body()
    {
        _member.Body = true;
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

    /// <summary><c>[ApiIgnore]</c>：完全不绑定（也不参与默认 Body 判定）。</summary>
    public RequestModelBuilder Ignore()
    {
        _member.Ignore = true;
        return _owner;
    }

    /// <summary>不标注任何参数特性：按默认规则绑定（GET 走 Query，POST / PUT / PATCH 的单一非标量走 Body，multipart 走部件）。</summary>
    public RequestModelBuilder Unannotated() => _owner;
}
