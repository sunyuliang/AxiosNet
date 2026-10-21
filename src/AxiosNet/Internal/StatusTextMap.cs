using System.Net;

namespace AxiosNet.Internal;

/// <summary>
/// HttpClient 在 .NET Core 上不暴露 reason phrase，这里维护与 axios 行为一致的常见状态码文本。
/// <para>
/// 用 <c>switch</c> 而不是字典：编译器把它编成对整数区间的二分查找，
/// 比一次委托到 <see cref="Dictionary{TKey,TValue}.TryGetValue"/> 的哈希查找更省，
/// 而且状态码与文案的对应关系在源码里一目了然。
/// </para>
/// </summary>
internal static class StatusTextMap
{
    public static string Get(int statusCode) => statusCode switch
    {
        200 => "OK",
        201 => "Created",
        202 => "Accepted",
        203 => "Non-Authoritative Information",
        204 => "No Content",
        205 => "Reset Content",
        206 => "Partial Content",
        301 => "Moved Permanently",
        302 => "Found",
        303 => "See Other",
        304 => "Not Modified",
        307 => "Temporary Redirect",
        308 => "Permanent Redirect",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        406 => "Not Acceptable",
        407 => "Proxy Authentication Required",
        408 => "Request Timeout",
        409 => "Conflict",
        410 => "Gone",
        412 => "Precondition Failed",
        413 => "Payload Too Large",
        414 => "URI Too Long",
        415 => "Unsupported Media Type",
        422 => "Unprocessable Entity",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        501 => "Not Implemented",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        _ => Fallback(statusCode)
    };

    /// <summary>表外的状态码：能映射到 <see cref="HttpStatusCode"/> 时用枚举名，其余留空。</summary>
    private static string Fallback(int statusCode)
        => Enum.IsDefined(typeof(HttpStatusCode), statusCode)
            ? ((HttpStatusCode)statusCode).ToString()
            : string.Empty;
}
