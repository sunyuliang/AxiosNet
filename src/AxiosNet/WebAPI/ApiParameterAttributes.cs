namespace AxiosNet.WebAPI;

/// <summary>
/// 把属性从绑定中完全排除：不进路径、不进查询串、不进请求头、也不作为请求体，
/// 亦不参与「默认 Body」的唯一未标注属性判定（内部状态 / 敏感数据属性用它摘出去）。
/// <para>仅 [ApiIgnore] 一个属性标注时生效；与 Path / Query / Header / Body 同用直接报错（二义性）。</para>
/// <para>属性值若要进请求体内部序列化，是请求体类型自己的事（那是 STJ 的 [JsonIgnore]），与本特性无关。</para>
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class ApiIgnoreAttribute : Attribute
{
}

/// <summary>
/// 把属性值填进路径模板的 <c>{name}</c> 占位符（值做 URL 转义）。
/// <para>占位符名称与绑定的匹配大小写不敏感（属性名回退 <c>Id</c> ↔ 模板 <c>{"{id}"}</c> 可直接对上）。</para>
/// <para>不传名称时使用属性名；值为 <c>null</c> 时抛 <see cref="InvalidOperationException"/>（路径缺段无法成行）。</para>
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class PathAttribute : Attribute
{
    /// <summary>占位符名称；空串表示使用属性名。</summary>
    public string Name { get; }

    /// <summary>绑定到路径模板占位符。</summary>
    /// <param name="name">占位符名称（不含花括号）；省略时使用属性名。</param>
    public PathAttribute(string name = "")
    {
        Name = name;
    }
}

/// <summary>
/// 把属性值序列化为 query string 参数（走 AxiosNet 的 params 规则：数组 <c>a[]=v</c>、嵌套对象 <c>a[b]=v</c>、null 跳过）。
/// <para>
/// 不传名称时使用属性名；<b>未标注任何参数特性的属性默认也是本语义</b>——
/// 唯一例外：POST/PUT/PATCH 且模型恰好有一个未标注的<b>非标量</b>属性时，该属性默认按 <see cref="BodyAttribute"/> 绑定。
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
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

/// <summary>
/// 把属性值写进请求头。不传名称时使用属性名；值为 <c>null</c> 时跳过。
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
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
/// 把属性值作为请求体发送（序列化规则与 AxiosNet 的 <c>data</c> 完全一致：对象按 Content-Type 编码，
/// string / byte[] / Stream / HttpContent 原样发送；含 FormFile 的对象默认按 multipart 编码）。
/// <para>
/// 每个请求模型最多一个 <see cref="BodyAttribute"/> 属性；值为 <c>null</c> 时不发送请求体。
/// </para>
/// <para>
/// 本特性在<b>单负载模型</b>上可以省略：POST/PUT/PATCH 且模型恰好有一个未标注的<b>非标量</b>属性
/// （基元类型、枚举、string、decimal、DateTime/DateTimeOffset、TimeSpan、Guid 及其可空形态除外）时，
/// 该属性默认按 Body 绑定。其余一切场景（标量属性、多个未标注属性、GET/DELETE 等方法）
/// 未标注属性一律按 <see cref="QueryAttribute"/> 绑定，需要 Body 时显式标注本特性。
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class BodyAttribute : Attribute
{
}
