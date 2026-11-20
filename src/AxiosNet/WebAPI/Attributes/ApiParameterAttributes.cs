namespace AxiosNet.WebAPI.Attributes;

/// <summary>把属性 / 参数从绑定中完全排除：不进路径、不进查询串、不进请求头、也不作为请求体。与 Path / Query / Header / HeaderCollection / Part / Body / RootBody 同用直接报错。</summary>
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
/// 不传名称时用属性名 / 参数名。未标注特性的成员默认也是本语义，例外见 <see cref="BodyAttribute"/> 的默认绑定规则。
/// 字符串键字典且没有显式命名时键直接进查询串，显式命名则保留嵌套形态。
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
/// 把属性 / 参数值作为请求体对象的一个键发送：正文为 <c>{ "键": 值 }</c>，可与 <see cref="QueryAttribute"/> / <see cref="HeaderAttribute"/> / <see cref="PathAttribute"/> 同用。
/// 键名取 <c>[JsonPropertyName]</c>，否则成员名（再套 JSON 命名策略）；可标注多个，重复键在构建期报错。
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, Inherited = true, AllowMultiple = false)]
public sealed class BodyAttribute : Attribute
{
}

/// <summary>
/// 把属性 / 参数值并入请求体：单独占据正文时原样发送（不加壳），与 <see cref="BodyAttribute"/> / 未标注成员同时存在时合并到同一层
/// （结构化值展开属性，其余值按键写入，重复键报错）。与 <see cref="MultipartAttribute"/> 互斥。
/// 编码格式由 <see cref="AxiosConfig.ContentType"/> 决定，含 <see cref="FormFile"/> 时须显式设为 <c>multipart/form-data</c>。
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, Inherited = true, AllowMultiple = false)]
public sealed class RootBodyAttribute : Attribute
{
}
