namespace AxiosNet.WebAPI;

/// <summary>请求模型的静态请求头：整类请求共用的固定头，一次声明。每个字符串形如 <c>"Name: Value"</c>；只有名字、冒号后为空表示删除该头。</summary>
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

/// <summary>把属性里的字典逐项写进请求头；属性类型须为 <c>IDictionary&lt;string, string&gt;</c>（构建期校验）。
/// 字典为 <c>null</c> 或空时不写头，值为 <c>null</c> 的项跳过；不能与 <see cref="HeaderAttribute"/> 标在同一个属性上。</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class HeaderCollectionAttribute : Attribute
{
}
