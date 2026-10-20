namespace AxiosNet;

/// <summary>
/// 常用 <c>Content-Type</c> 的预置常量，用于给 <see cref="AxiosConfig.ContentType"/> 赋值。
/// <para>
/// 为什么是常量而不是 <c>enum</c>：Content-Type 是「媒体类型 + 参数」的复合值，
/// 参数部分恰恰是库要支持的自定义点（<c>multipart/form-data; boundary=----xxx</c>、
/// <c>application/json; charset=gbk</c>），枚举表达不了。这与 BCL 的
/// <see cref="System.Net.Http.HttpMethod"/> 是同一个取舍——预置
/// <c>HttpMethod.Get</c> 之类，同时允许 <c>new HttpMethod("PROPFIND")</c> 自定义。
/// 常量能让 IDE 补全、避免拼错，也能直接用在 <c>switch</c> / 特性里（<c>const</c> 在编译期内联）。
/// </para>
/// <para>
/// 参数（charset / boundary）直接拼在本值后面即可；<see cref="AxiosConfig.RequestEncoding"/>
/// 非空时库会自动补 <c>charset=</c>，已经写了的不会重复追加。
/// </para>
/// </summary>
/// <example>
/// <code>
/// await http.PostAsync("login", form, c => c.ContentType = AxiosContentType.FormUrlEncoded);
/// await http.PostAsync("upload", files, c => c.ContentType = AxiosContentType.MultipartFormData);
/// await http.PostAsync("blob", bytes, c => c.ContentType = AxiosContentType.OctetStream);
///
/// // 带参数 / 私有类型：仍然是字符串，没有任何限制
/// c.ContentType = AxiosContentType.MultipartFormData + "; boundary=----axios-net";
/// c.ContentType = "application/vnd.api+json";
/// </code>
/// </example>
public static class AxiosContentType
{
    /// <summary><c>application/json</c>：库默认值，结构化数据序列化为 JSON 文本。</summary>
    public const string Json = "application/json";

    /// <summary><c>application/x-www-form-urlencoded</c>：结构化数据转键值对表单（键值均自动编码）。</summary>
    public const string FormUrlEncoded = "application/x-www-form-urlencoded";

    /// <summary>
    /// <c>multipart/form-data</c>：结构化数据转字段 + 文件部件。
    /// 需要固定边界时拼成 <c>MultipartFormData + "; boundary=----xxx"</c>。
    /// </summary>
    public const string MultipartFormData = "multipart/form-data";

    /// <summary><c>application/octet-stream</c>：二进制载荷（<c>byte[]</c> / <c>Stream</c>）的常规选择。</summary>
    public const string OctetStream = "application/octet-stream";

    /// <summary><c>application/xml</c>：结构化数据仍按 JSON 文本写入，只有头部不同。</summary>
    public const string Xml = "application/xml";

    /// <summary><c>application/json-patch+json</c>：JSON Patch 文档（PATCH 常用）。</summary>
    public const string JsonPatch = "application/json-patch+json";

    /// <summary><c>application/problem+json</c>：RFC 7807 问题详情。</summary>
    public const string ProblemJson = "application/problem+json";

    /// <summary><c>text/plain</c>：纯文本，结构化数据仍按 JSON 文本写入（要发裸文本请把 <c>string</c> 直接作为 data）。</summary>
    public const string TextPlain = "text/plain";

    /// <summary><c>text/html</c>：HTML 片段。</summary>
    public const string TextHtml = "text/html";
}
