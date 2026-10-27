namespace AxiosNet;

/// <summary>
/// 常用 <c>Content-Type</c> 的预置常量，用于给 <see cref="AxiosConfig.ContentType"/> 赋值。
/// 用常量而非 <c>enum</c>：值可以带参数（如 <c>; boundary=----xxx</c>），也可直接用于 <c>switch</c> / 特性。
/// </summary>
public static class AxiosContentType
{
    /// <summary><c>application/json</c>：库默认值，结构化数据序列化为 JSON 文本。</summary>
    public const string Json = "application/json";

    /// <summary><c>application/x-www-form-urlencoded</c>：结构化数据转键值对表单（键值均自动编码）。</summary>
    public const string FormUrlEncoded = "application/x-www-form-urlencoded";

    /// <summary><c>multipart/form-data</c>：结构化数据转字段 + 文件部件；固定边界时在后面拼 <c>; boundary=----xxx</c>。</summary>
    public const string MultipartFormData = "multipart/form-data";

    /// <summary><c>application/octet-stream</c>：二进制载荷（<c>byte[]</c> / <c>Stream</c>）的常规选择。</summary>
    public const string OctetStream = "application/octet-stream";

    /// <summary><c>application/xml</c>：需配合 XML 序列化器，否则会发出「头部 XML、正文 JSON」的请求。</summary>
    public const string Xml = "application/xml";

    /// <summary><c>application/json-patch+json</c>：JSON Patch 文档（PATCH 常用）。</summary>
    public const string JsonPatch = "application/json-patch+json";

    /// <summary><c>application/problem+json</c>：RFC 7807 问题详情。</summary>
    public const string ProblemJson = "application/problem+json";

    /// <summary><c>text/plain</c>：纯文本；要发裸文本请把 <c>string</c> 直接作为 data。</summary>
    public const string TextPlain = "text/plain";

    /// <summary><c>text/html</c>：HTML 片段。</summary>
    public const string TextHtml = "text/html";
}
