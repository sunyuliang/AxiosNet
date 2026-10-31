using System.ComponentModel;
using AxiosNet.WebAPI.Internal;

namespace AxiosNet.WebAPI.Metadata;

/// <summary>接口声明构建器：由源生成器或 <see cref="AxiosApiMetadata.RegisterInterface"/> 使用。</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class InterfaceBuilder
{
    internal InterfaceBuilder(Type interfaceType)
    {
        Declaration = new InterfaceDeclarationData { InterfaceType = interfaceType };
    }

    internal InterfaceDeclarationData Declaration { get; }

    /// <summary>接口级静态头（基接口在前、派生接口在后由生成器排好序）。</summary>
    public InterfaceBuilder StaticHeaders(params string[] headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        Declaration.StaticHeaders.AddRange(headers);
        return this;
    }

    /// <summary>接口级 <c>[Authorize(scheme)]</c>（已被方法级声明覆盖时无需写入）。</summary>
    public InterfaceBuilder Authorize(string scheme)
    {
        Declaration.AuthorizeScheme = scheme;
        return this;
    }

    /// <summary>接口级 <c>[AllowAnonymous]</c>。</summary>
    public InterfaceBuilder Anonymous()
    {
        Declaration.AllowAnonymous = true;
        return this;
    }

    /// <summary>声明一个方法；顺序即生成代理里的方法索引，必须与接口方法顺序一致。</summary>
    public InterfaceMethodBuilder Method(string name, HttpMethod method, string path)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(method);
        return AddMethod(name, method.Method, path);
    }

    /// <summary>声明一个方法（HTTP 方法名文本形式）。</summary>
    public InterfaceMethodBuilder Method(string name, string method, string path)
    {
        ArgumentNullException.ThrowIfNull(name);
        return AddMethod(name, method, path);
    }

    private InterfaceMethodBuilder AddMethod(string name, string method, string path)
    {
        var declaration = new InterfaceMethodDeclaration
        {
            Name = name,
            MethodName = method,
            Path = path
        };

        Declaration.Methods.Add(declaration);
        return new InterfaceMethodBuilder(this, declaration);
    }
}

/// <summary>接口方法构建器：绑定参数返回自身；<c>ReturnsResponse</c> / <c>ReturnsData</c> / <c>ReturnsVoid</c> 与 <c>[Stream]</c> 是方法声明的最后一笔，返回接口构建器。</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class InterfaceMethodBuilder
{
    private readonly InterfaceBuilder _owner;
    private readonly InterfaceMethodDeclaration _method;

    internal InterfaceMethodBuilder(InterfaceBuilder owner, InterfaceMethodDeclaration method)
    {
        _owner = owner;
        _method = method;
    }

    /// <summary>声明一个参数（索引必须与接口方法签名一致）。</summary>
    public InterfaceParameterBuilder Parameter(int index, string name, Type valueType)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(valueType);

        var declaration = new InterfaceParameterDeclaration
        {
            Index = index,
            Name = name,
            ValueType = valueType,
            Description = $"{_owner.Declaration.InterfaceType.Name}.{_method.Name}.{name}"
        };

        _method.Parameters.Add(declaration);
        return new InterfaceParameterBuilder(this, declaration);
    }

    /// <summary>标注取消令牌参数的位置（不参与绑定）；声明里 <see cref="CancellationToken"/> 类型的参数也会被自动跳过并记录位置。</summary>
    public InterfaceMethodBuilder CancellationToken(int index)
    {
        _method.CancellationTokenIndex = index;
        return this;
    }

    /// <summary>方法级 <c>[Multipart]</c>：无标注参数默认按部件绑定。</summary>
    public InterfaceMethodBuilder Multipart()
    {
        _method.Multipart = true;
        return this;
    }

    /// <summary>方法级静态头（覆盖接口级同名项）。</summary>
    public InterfaceMethodBuilder StaticHeaders(params string[] headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        _method.StaticHeaders.AddRange(headers);
        return this;
    }

    /// <summary>方法级 <c>[Authorize(scheme)]</c>。</summary>
    public InterfaceMethodBuilder Authorize(string scheme)
    {
        _method.AuthorizeScheme = scheme;
        return this;
    }

    /// <summary>方法级 <c>[AllowAnonymous]</c>。</summary>
    public InterfaceMethodBuilder Anonymous()
    {
        _method.AllowAnonymous = true;
        return this;
    }

    /// <summary><c>[Stream]</c>：只用于 <c>Task&lt;AxiosResponse&lt;IAsyncEnumerable&lt;T&gt;&gt;&gt;</c>。</summary>
    public InterfaceMethodBuilder Stream(AxiosStreamFormat format = AxiosStreamFormat.NewlineDelimitedJson)
    {
        _method.HasStreamAttribute = true;
        _method.StreamFormat = format;
        return this;
    }

    /// <summary><c>Task&lt;AxiosResponse&lt;T&gt;&gt;</c>；<paramref name="responseType"/> 为 null（或省略）表示非泛型 <c>AxiosResponse</c>。</summary>
    public InterfaceBuilder ReturnsResponse(Type? responseType = null)
    {
        _method.ReturnShape = InterfaceReturnShape.Response;
        _method.ResponseType = responseType;
        return _owner;
    }

    /// <summary><c>Task&lt;T&gt;</c>：只交付正文。</summary>
    public InterfaceBuilder ReturnsData(Type responseType)
    {
        ArgumentNullException.ThrowIfNull(responseType);
        _method.ReturnShape = InterfaceReturnShape.Data;
        _method.ResponseType = responseType;
        return _owner;
    }

    /// <summary><c>Task</c>：只完成请求。</summary>
    public InterfaceBuilder ReturnsVoid()
    {
        _method.ReturnShape = InterfaceReturnShape.Void;
        _method.ResponseType = null;
        return _owner;
    }
}

/// <summary>接口参数构建器：每个绑定方法返回所属方法构建器。</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class InterfaceParameterBuilder
{
    private readonly InterfaceMethodBuilder _owner;
    private readonly InterfaceParameterDeclaration _parameter;

    internal InterfaceParameterBuilder(InterfaceMethodBuilder owner, InterfaceParameterDeclaration parameter)
    {
        _owner = owner;
        _parameter = parameter;
    }

    /// <summary><c>[Path]</c>；省略名字时用参数名。</summary>
    public InterfaceMethodBuilder Path(string? name = null)
    {
        _parameter.Path = true;
        _parameter.PathName = name;
        return _owner;
    }

    /// <summary><c>[Query]</c>；省略名字时用参数名。</summary>
    public InterfaceMethodBuilder Query(string? name = null)
    {
        _parameter.Query = true;
        _parameter.QueryName = name;
        return _owner;
    }

    /// <summary><c>[Header]</c>；省略名字时用参数名。</summary>
    public InterfaceMethodBuilder Header(string? name = null)
    {
        _parameter.Header = true;
        _parameter.HeaderName = name;
        return _owner;
    }

    /// <summary><c>[HeaderCollection]</c>。</summary>
    public InterfaceMethodBuilder HeaderCollection()
    {
        _parameter.HeaderCollection = true;
        return _owner;
    }

    /// <summary><c>[Body]</c>。</summary>
    public InterfaceMethodBuilder Body()
    {
        _parameter.Body = true;
        return _owner;
    }

    /// <summary><c>[Part]</c>；省略名字时用参数名。</summary>
    public InterfaceMethodBuilder Part(string? name = null, string? fileName = null, string? contentType = null)
    {
        _parameter.Part = true;
        _parameter.PartName = name;
        _parameter.PartFileName = fileName;
        _parameter.PartContentType = contentType;
        return _owner;
    }

    /// <summary><c>[ApiIgnore]</c>：完全不绑定。</summary>
    public InterfaceMethodBuilder Ignore()
    {
        _parameter.Ignore = true;
        return _owner;
    }

    /// <summary>不标注任何参数特性：按默认规则绑定（GET 走 Query，POST / PUT / PATCH 的单一非标量走 Body，multipart 走部件）。</summary>
    public InterfaceMethodBuilder Unannotated() => _owner;
}
