namespace AxiosNet.Internal;

/// <summary>
/// 媒体类型的文本 / 二进制判定（请求与响应共用）。
/// <para>请求侧决定是否补 charset；响应侧决定响应体交给序列化器的文本入口还是字节入口。</para>
/// <para>
/// 只看媒体类型主值（忽略 <c>charset</c> / <c>boundary</c> 等参数）；没有 Content-Type 时按<b>文本</b>处理
/// —— 绝大多数 HTTP API 是文本协议，真正的二进制端点都会声明 <c>application/octet-stream</c> 之类。
/// </para>
/// </summary>
internal static class MediaTypes
{
    private const string TextPrefix = "text/";
    private const string JsonSuffix = "+json";
    private const string XmlSuffix = "+xml";
    private const string YamlSuffix = "+yaml";
    private const string JavaScript = "application/javascript";
    private const string Yaml = "application/yaml";
    private const string XYaml = "application/x-yaml";
    private const string Csv = "application/csv";
    private const string GraphQl = "application/graphql";
    private const string Sql = "application/sql";

    /// <summary>
    /// 媒体类型主值归一：去掉参数与两端空白并转小写。<c>null</c> / 空白 / 只剩参数时返回 <c>null</c>。
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
        // 响应侧的每请求必经路径：直接在原串上切片比较，不切子串、不转小写。
        var main = MainValue(mediaType);
        if (main.Length == 0)
        {
            return true;
        }

        return main.StartsWith(TextPrefix, StringComparison.OrdinalIgnoreCase)
            || main.EndsWith(JsonSuffix, StringComparison.OrdinalIgnoreCase)
            || main.EndsWith(XmlSuffix, StringComparison.OrdinalIgnoreCase)
            || main.EndsWith(YamlSuffix, StringComparison.OrdinalIgnoreCase)
            || IsTextualApplicationType(main);
    }

    /// <summary>结构化文本的应用类型（<c>application/json</c>、<c>application/xml</c> 等）。</summary>
    private static bool IsTextualApplicationType(ReadOnlySpan<char> main)
        => main.Equals(AxiosContentType.Json, StringComparison.OrdinalIgnoreCase)
            || main.Equals(AxiosContentType.Xml, StringComparison.OrdinalIgnoreCase)
            || main.Equals(AxiosContentType.FormUrlEncoded, StringComparison.OrdinalIgnoreCase)
            || main.Equals(JavaScript, StringComparison.OrdinalIgnoreCase)
            || main.Equals(Yaml, StringComparison.OrdinalIgnoreCase)
            || main.Equals(XYaml, StringComparison.OrdinalIgnoreCase)
            || main.Equals(Csv, StringComparison.OrdinalIgnoreCase)
            || main.Equals(GraphQl, StringComparison.OrdinalIgnoreCase)
            || main.Equals(Sql, StringComparison.OrdinalIgnoreCase);

    /// <summary>媒体类型主值（去掉参数与两端空白）；不做任何分配，无需转小写。</summary>
    private static ReadOnlySpan<char> MainValue(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return default;
        }

        var end = mediaType.IndexOf(';');
        return (end < 0 ? mediaType.AsSpan() : mediaType.AsSpan(0, end)).Trim();
    }
}
