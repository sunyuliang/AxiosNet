namespace AxiosNet.Internal;

/// <summary>
/// 媒体类型的文本 / 二进制判定（请求与响应共用同一份规则）。
/// <para>
/// 用途：
/// <list type="bullet">
/// <item>请求侧：文本类型在显式设置 <c>RequestEncoding</c> 时补 charset（<c>RequestBodyFactory.ComposeMediaType</c>）；</item>
/// <item>响应侧：决定响应体交给序列化器的哪个入口 —— 文本走 <c>Deserialize(string)</c>，
/// 二进制走 <c>Deserialize(ReadOnlyMemory&lt;byte&gt;)</c>，且不做 UTF-8 预解码。</item>
/// </list>
/// </para>
/// <para>
/// 判定只看媒体类型主值（忽略 <c>charset</c> / <c>boundary</c> 等参数）。
/// 没有 Content-Type 时按<b>文本</b>处理：绝大多数 HTTP API 是文本协议，
/// 真正的二进制端点（下载、protobuf）都会声明 <c>application/octet-stream</c> 之类的类型。
/// </para>
/// </summary>
internal static class MediaTypes
{
    /// <summary>
    /// 媒体类型主值归一：去掉 <c>; charset=...</c> 等参数、两端空白，并转小写。
    /// <c>null</c> / 空白 / 只剩参数时返回 <c>null</c>。
    /// </summary>
    public static string? Normalize(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return null;
        }

        var end = mediaType.IndexOf(';');
        var main = (end < 0 ? mediaType : mediaType[..end]).Trim();
        return main.Length == 0 ? null : main.ToLowerInvariant();
    }

    /// <summary>是否为文本类媒体类型（<c>null</c> / 空视为文本）。</summary>
    public static bool IsTextual(string? mediaType)
    {
        var main = Normalize(mediaType);
        if (main is null)
        {
            return true;
        }

        return main.StartsWith("text/", StringComparison.Ordinal)
            || main.EndsWith("+json", StringComparison.Ordinal)
            || main.EndsWith("+xml", StringComparison.Ordinal)
            || main.EndsWith("+yaml", StringComparison.Ordinal)
            || main.Equals(AxiosContentType.Json, StringComparison.Ordinal)
            || main.Equals(AxiosContentType.Xml, StringComparison.Ordinal)
            || main.Equals(AxiosContentType.FormUrlEncoded, StringComparison.Ordinal)
            || main.Equals("application/javascript", StringComparison.Ordinal)
            || main.Equals("application/yaml", StringComparison.Ordinal)
            || main.Equals("application/x-yaml", StringComparison.Ordinal)
            || main.Equals("application/csv", StringComparison.Ordinal)
            || main.Equals("application/graphql", StringComparison.Ordinal)
            || main.Equals("application/sql", StringComparison.Ordinal);
    }
}
