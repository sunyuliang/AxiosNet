namespace AxiosNet.WebAPI;

/// <summary>
/// 声明级静态请求头：类 / 接口上是整份声明共用的静态头，接口方法上是该方法独有的静态头（覆盖接口级同名项）。
/// 每个字符串形如 <c>"Name: Value"</c>；只有名字（不含冒号）表示删除该头，冒号后为空则写入空值头。
/// 同名项后声明的覆盖先声明的：继承链上派生类 / 派生接口覆盖基类 / 基接口，接口方法覆盖接口级。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class HeadersAttribute : Attribute
{
    /// <summary>声明一组静态头。</summary>
    /// <param name="headers">头声明（<c>"Name: Value"</c>）；null 视为空数组。</param>
    public HeadersAttribute(params string[] headers)
    {
        Headers = headers ?? [];
    }

    /// <summary>本组头声明（原样保留，解析与校验发生在构建绑定元数据时）。</summary>
    public IReadOnlyList<string> Headers { get; }
}

/// <summary>把属性 / 参数里的字典逐项写进请求头；类型须为字符串键值字典（<c>IDictionary&lt;string, string&gt;</c> 或 <c>IReadOnlyDictionary&lt;string, string&gt;</c>，构建期校验）。
/// 字典为 <c>null</c> 或空时不写头，值为 <c>null</c> 的项跳过；不能与 <see cref="HeaderAttribute"/> 标在同一成员上。</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, Inherited = true, AllowMultiple = false)]
public sealed class HeaderCollectionAttribute : Attribute
{
}
