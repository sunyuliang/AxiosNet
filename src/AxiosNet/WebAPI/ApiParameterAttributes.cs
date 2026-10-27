namespace AxiosNet.WebAPI;

/// <summary>把属性 / 参数从绑定中完全排除：不进路径、不进查询串、不进请求头、也不作为请求体。与 Path / Query / Header / Body 同用直接报错。</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, Inherited = true, AllowMultiple = false)]
public sealed class ApiIgnoreAttribute : Attribute
{
}

/// <summary>把属性 / 参数值填进路径模板的 <c>{name}</c> 占位符（值做 URL 转义）。名称匹配大小写不敏感；不传名称时使用属性名 / 参数名；值为 <c>null</c> 时抛 <see cref="InvalidOperationException"/>。</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, Inherited = true, AllowMultiple = false)]
public sealed class PathAttribute : Attribute
{
    /// <summary>占位符名称；空串表示使用属性名 / 参数名。</summary>
    public string Name { get; }

    /// <summary>绑定到路径模板占位符。</summary>
    /// <param name="name">占位符名称（不含花括号）；省略时使用属性名。</param>
    public PathAttribute(string name = "")
    {
        Name = name;
    }
}

/// <summary>
/// 把属性 / 参数值序列化为 query string 参数（数组 <c>a[]=v</c>、嵌套对象 <c>a[b]=v</c>、null 跳过）。
/// 不传名称时使用属性名 / 参数名；未标注参数特性的成员默认也是本语义，例外是 POST/PUT/PATCH 且声明只有一个未标注的非标量成员时按 <see cref="BodyAttribute"/> 绑定。
/// 字符串键字典且没有显式命名时键直接进查询串（<c>?a=1&amp;b=2</c>），显式命名则保留嵌套形态。
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, Inherited = true, AllowMultiple = false)]
public sealed class QueryAttribute : Attribute
{
    /// <summary>参数名；空串表示使用属性名。</summary>
    public string Name { get; }

    /// <summary>绑定到 query string。</summary>
    /// <param name="name">参数名；省略时使用属性名。</param>
    public QueryAttribute(string name = "")
    {
        Name = name;
    }
}

/// <summary>把属性 / 参数值写进请求头。不传名称时使用属性名 / 参数名；值为 <c>null</c> 时跳过。</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, Inherited = true, AllowMultiple = false)]
public sealed class HeaderAttribute : Attribute
{
    /// <summary>请求头名称；空串表示使用属性名。</summary>
    public string Name { get; }

    /// <summary>绑定到请求头。</summary>
    /// <param name="name">请求头名称；省略时使用属性名。</param>
    public HeaderAttribute(string name = "")
    {
        Name = name;
    }
}

/// <summary>
/// 把属性 / 参数值作为请求体发送（序列化规则与 <c>data</c> 一致）。
/// 编码格式只由 <see cref="AxiosConfig.ContentType"/> 决定，含 <see cref="FormFile"/> 时须显式设为 <c>multipart/form-data</c>；每份声明最多一个，值为 <c>null</c> 时不发送。
/// 单负载声明可省略本特性：POST/PUT/PATCH 且只有一个未标注的非标量成员时，该成员默认按 Body 绑定。
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, Inherited = true, AllowMultiple = false)]
public sealed class BodyAttribute : Attribute
{
}
