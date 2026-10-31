namespace AxiosNet.WebAPI;

/// <summary>
/// 指定接口方法请求体的媒体类型（写入 <c>config.ContentType</c>）：用于 JSON 以外的正文，例如
/// <c>application/x-www-form-urlencoded</c>、XML 或自定义媒体类型。媒体类型决定正文编码，只写 Content-Type 头不会改变编码。
/// </summary>
/// <remarks>
/// 与 <see cref="MultipartAttribute"/> 互斥：multipart 的正文由全部参数装配，媒体类型固定为 <c>multipart/form-data</c>。
/// 取值在构建接口契约时校验（空白、控制字符、与 <see cref="MultipartAttribute"/> 冲突），错误信息带接口与方法名；
/// 属性对象本身只保留原文。
/// </remarks>
[AttributeUsage(AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class BodyContentTypeAttribute : Attribute
{
    /// <summary>声明请求体媒体类型。</summary>
    /// <param name="contentType">媒体类型，可带参数（如 <c>application/x-www-form-urlencoded; charset=utf-8</c>）；不能为空白、不能含控制字符。</param>
    public BodyContentTypeAttribute(string contentType)
    {
        // 属性构造期不做校验：反射实例化属性时抛异常，调用方只能得到没有接口 / 方法上下文的错误；
        // 交给契约构建统一校验，与 [Stream] 等声明级特性走同一条错误通道。
        ContentType = contentType ?? string.Empty;
    }

    /// <summary>媒体类型原文；构建接口契约时去除首尾空白并校验。</summary>
    public string ContentType { get; }
}

/// <summary>
/// 指定接口方法查询串里集合值的编码格式（写入 <c>config.Serialization.CollectionFormat</c>），
/// 覆盖实例级默认值与配置。典型场景是 OpenAPI 的 <c>explode: true</c>：对应 <see cref="AxiosCollectionFormat.Multi"/>（<c>a=1&amp;a=2</c>）。
/// </summary>
/// <remarks>
/// 取值在构建接口契约时校验是否属于 <see cref="AxiosCollectionFormat"/> 的已定义值，错误信息带接口与方法名；属性对象本身只保留原值。
/// 实例配置了 <see cref="AxiosConfig.SerializationSettings.ParamsSerializer"/> 时内置集合编码不参与（与手动设置 <see cref="AxiosConfig.SerializationSettings.CollectionFormat"/> 一致）。
/// </remarks>
[AttributeUsage(AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class QueryCollectionFormatAttribute : Attribute
{
    /// <summary>声明查询集合格式。</summary>
    /// <param name="format">集合格式（Brackets / Multi / Csv / Ssv / Tsv / Pipes）。</param>
    public QueryCollectionFormatAttribute(AxiosCollectionFormat format) => Format = format;

    /// <summary>集合格式原值；构建接口契约时校验。</summary>
    public AxiosCollectionFormat Format { get; }
}
