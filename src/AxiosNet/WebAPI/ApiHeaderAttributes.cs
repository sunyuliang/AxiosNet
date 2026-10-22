namespace AxiosNet.WebAPI;

/// <summary>
/// 请求模型的<b>静态请求头</b>：整类请求共用的固定头，一次声明。
/// <para>
/// 每个字符串形如 <c>"Name: Value"</c>；<b>只有名字、冒号后为空</b>（如 <c>"X-Internal"</c>）表示
/// <b>删除</b>该头（摘掉实例默认头），与 Refit 的 <c>[Headers]</c> 语义一致。
/// </para>
/// <para>
/// 优先级：本特性先写入，属性上的 <see cref="HeaderAttribute"/> 覆盖同名项；
/// <see cref="IApiRequestConfigurable.Configure"/>、按请求 <c>configure</c> 回调与授权提供方再逐层覆盖。
/// 基类上的声明会被派生类继承，派生类同名声明优先。
/// 「只有名字」的删除在合并完成后生效，因此它也能摘掉属性头与实例默认头。
/// </para>
/// </summary>
/// <example>
/// <code>
/// [Get("users/{id}")]
/// [Headers("Accept: application/json", "X-Api-Version: 2")]
/// public sealed class GetUserRequest : IApiRequest&lt;User&gt; { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class HeadersAttribute : Attribute
{
    /// <summary>声明一组静态头。</summary>
    /// <param name="headers">头声明（<c>"Name: Value"</c>）；null 视为空数组。</param>
    public HeadersAttribute(params string[] headers)
    {
        Headers = headers ?? [];
    }

    /// <summary>本组头声明（原样保留，解析与校验发生在构建请求模型元数据时）。</summary>
    public IReadOnlyList<string> Headers { get; }
}

/// <summary>
/// 把属性里的<b>字典</b>逐项写进请求头（Refit 的 <c>[HeaderCollection]</c> 等价能力）。
/// <para>
/// 属性类型须为 <c>IDictionary&lt;string, string&gt;</c>（<c>Dictionary&lt;string, string&gt;</c> /
/// <c>SortedDictionary&lt;string, string&gt;</c> 等均可，构建期校验）；字典为 <c>null</c> 或空时不写头，
/// 值为 <c>null</c> 的项跳过。
/// </para>
/// <para>不能与 <see cref="HeaderAttribute"/> 标在同一个属性上（一个是逐项、一个是单值，二义）。</para>
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class HeaderCollectionAttribute : Attribute
{
}
