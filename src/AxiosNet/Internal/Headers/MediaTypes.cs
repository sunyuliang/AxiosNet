namespace AxiosNet.Internal.Headers;

/// <summary>
/// 媒体类型的文本 / 二进制判定（请求与响应共用）：只看媒体类型主值（忽略 <c>charset</c> / <c>boundary</c> 等参数），
/// 没有 Content-Type 时按<b>文本</b>处理。
/// </summary>
internal static class MediaTypes
{
    // 文本类型判定用的字面前缀 / 后缀与常见文本媒体类型。
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

    /// <summary>媒体类型主值归一：去掉参数与两端空白并转小写；<c>null</c> / 空白时返回 <c>null</c>。</summary>
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

    /// <summary>媒体类型主值是否等于 <paramref name="expected"/>（忽略参数、空白与大小写）；<paramref name="expected"/> 必须是主值本身。</summary>
    public static bool Is(string? mediaType, string expected)
        => SpanComparisons.EqualsIgnoreCase(MainValue(mediaType), expected);

    /// <summary>是否为 JSON 通道（<c>application/json</c> 或 <c>+json</c> 后缀）；<c>null</c> / 空白表示库默认的 JSON。</summary>
    public static bool IsJson(string? mediaType)
    {
        var main = MainValue(mediaType);
        return main.Length == 0
            || SpanComparisons.EqualsIgnoreCase(main, AxiosContentType.Json)
            || SpanComparisons.EndsWithIgnoreCase(main, JsonSuffix);
    }

    /// <summary>是否为文本类媒体类型（<c>null</c> / 空视为文本）。</summary>
    public static bool IsTextual(string? mediaType)
    {
        // 直接在原串上切片比较，不切子串、不转小写。
        var main = MainValue(mediaType);
        if (main.Length == 0)
        {
            return true;
        }

        return SpanComparisons.StartsWithIgnoreCase(main, TextPrefix)
            || SpanComparisons.EndsWithIgnoreCase(main, JsonSuffix)
            || SpanComparisons.EndsWithIgnoreCase(main, XmlSuffix)
            || SpanComparisons.EndsWithIgnoreCase(main, YamlSuffix)
            || IsTextualApplicationType(main);
    }

    /// <summary>结构化文本的应用类型（<c>application/json</c>、<c>application/xml</c> 等）。</summary>
    private static bool IsTextualApplicationType(ReadOnlySpan<char> main)
        => SpanComparisons.EqualsIgnoreCase(main, AxiosContentType.Json)
            || SpanComparisons.EqualsIgnoreCase(main, AxiosContentType.Xml)
            || SpanComparisons.EqualsIgnoreCase(main, AxiosContentType.FormUrlEncoded)
            || SpanComparisons.EqualsIgnoreCase(main, JavaScript)
            || SpanComparisons.EqualsIgnoreCase(main, Yaml)
            || SpanComparisons.EqualsIgnoreCase(main, XYaml)
            || SpanComparisons.EqualsIgnoreCase(main, Csv)
            || SpanComparisons.EqualsIgnoreCase(main, GraphQl)
            || SpanComparisons.EqualsIgnoreCase(main, Sql);

    /// <summary>媒体类型主值（去掉参数与两端空白，不做分配）。</summary>
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
