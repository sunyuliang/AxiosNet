namespace AxiosNet.WebAPI.Attributes;

/// <summary>
/// 指定接口方法请求体的媒体类型（写入 <c>config.ContentType</c>）：用于 JSON 以外的正文，如 <c>application/x-www-form-urlencoded</c>、XML 或自定义媒体类型；媒体类型决定正文编码，只写 Content-Type 头不会改变编码。
/// </summary>
/// <remarks>
/// 可用于接口方法、请求模型类或模型属性：方法 / 属性级优先于类型级，两处显式声明且取值不同时构建期抛 <c>ERR_BAD_OPTION_VALUE</c>。
/// 与 <see cref="MultipartAttribute"/> 互斥（multipart 固定 <c>multipart/form-data</c>）；取值在构建契约时校验，属性对象本身只保留原文。
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class BodyContentTypeAttribute : Attribute
{
    /// <summary>声明请求体媒体类型；可带参数（如 <c>application/x-www-form-urlencoded; charset=utf-8</c>），构建契约时校验非空白且无控制字符。</summary>
    public BodyContentTypeAttribute(string contentType)
    {
        // 构造期不校验：反射实例化时抛异常拿不到接口 / 方法上下文，交给契约构建统一校验。
        ContentType = contentType ?? string.Empty;
    }

    /// <summary>媒体类型原文；构建接口契约时去除首尾空白并校验。</summary>
    public string ContentType { get; }
}

/// <summary>
/// 指定接口方法查询串里集合值的编码格式（写入 <c>config.Serialization.CollectionFormat</c>），覆盖实例级默认值；OpenAPI 的 <c>explode: true</c> 对应 <see cref="AxiosCollectionFormat.Multi"/>（<c>a=1&amp;a=2</c>）。
/// </summary>
/// <remarks>
/// 可用于接口方法、请求模型类或模型属性，方法 / 属性级优先；两处显式声明且取值不同时构建期抛 <c>ERR_BAD_OPTION_VALUE</c>。
/// 实例配置了 <see cref="AxiosConfig.SerializationSettings.QueryEncoder"/> 时内置集合编码不参与。
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class QueryCollectionFormatAttribute : Attribute
{
    /// <summary>声明查询集合格式（Brackets / Multi / Csv / Ssv / Tsv / Pipes），构建契约时校验。</summary>
    public QueryCollectionFormatAttribute(AxiosCollectionFormat format) => Format = format;

    /// <summary>集合格式原值；构建接口契约时校验。</summary>
    public AxiosCollectionFormat Format { get; }
}
