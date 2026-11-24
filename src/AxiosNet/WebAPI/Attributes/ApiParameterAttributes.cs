namespace AxiosNet.WebAPI.Attributes;

/// <summary>把属性 / 参数从绑定中完全排除：不进路径、不进查询串、不进请求头、也不作为请求体。与 Path / Query / Header / HeaderCollection / Part / Body 同用直接报错。</summary>
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

    /// <summary>绑定到路径模板占位符；<paramref name="name"/> 省略时使用属性名。</summary>
    public PathAttribute(string name = "")
    {
        Name = name;
    }
}

/// <summary>
/// 把属性 / 参数值序列化为 query string 参数（数组 <c>a[]=v</c>、嵌套对象 <c>a[b]=v</c>、null 跳过）；不传名称时用属性名 / 参数名。
/// 未标注特性的成员默认也是本语义；字符串键字典没有显式命名时键直接进查询串，显式命名保留嵌套形态。
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, Inherited = true, AllowMultiple = false)]
public sealed class QueryAttribute : Attribute
{
    /// <summary>参数名；空串表示使用属性名。</summary>
    public string Name { get; }

    /// <summary>绑定到 query string；<paramref name="name"/> 省略时使用属性名。</summary>
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

    /// <summary>绑定到请求头；<paramref name="name"/> 省略时使用属性名。</summary>
    public HeaderAttribute(string name = "")
    {
        Name = name;
    }
}

/// <summary>
/// 声明请求正文：不带名字时该成员的值就是整份正文（原样发送、独占）；带名字时作为聚合正文里的一个键（可多个）。
/// </summary>
/// <remarks>
/// 无参形态下正文形态由值的类型决定：对象 / 数组 / 标量 / 流都直接下发，不拆不加壳；此时不能再有其它正文来源（其它 <see cref="BodyAttribute"/> 或 body 方法上改道进正文的未标注成员）。
/// 带名形态下正文是 <c>{ "键": 值, … }</c>，可与 <see cref="QueryAttribute"/> / <see cref="HeaderAttribute"/> / <see cref="PathAttribute"/> 同用。
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, Inherited = true, AllowMultiple = false)]
public sealed class BodyAttribute : Attribute
{
    /// <summary>正文键名；<c>null</c> 表示整份正文（无参构造），空串表示回退 <c>[JsonPropertyName]</c> 或成员名。</summary>
    public string? Name { get; }

    /// <summary>整份正文：原样发送，不与其它正文来源共存。</summary>
    public BodyAttribute()
    {
        Name = null;
    }

    /// <summary>聚合正文的一个键；<paramref name="name"/> 空白时回退 <c>[JsonPropertyName]</c>，再回退属性名 / 参数名。</summary>
    public BodyAttribute(string name)
    {
        Name = name;
    }
}
