namespace AxiosNet.Internal;

/// <summary>
/// multipart 的 field name / filename 落在 <c>Content-Disposition</c> 的 quoted-string 里，
/// 有几个字符会破坏这段语法。实测（net6.0 / net8.0 一致）：
/// <list type="bullet">
/// <item><c>"</c> → <c>ArgumentException</c>；<c>CR</c> / <c>LF</c> → <c>FormatException</c>（这两个至少会响）。</item>
/// <item><c>\</c>、<c>NUL</c>、<c>HTAB</c> 等控制字符**不报错**，但解析端会退回「带引号的原文」：
/// 服务端拿到的键名变成 <c>"a\b"</c>（连引号一起），字段静默对不上。</item>
/// </list>
/// 因此统一在组装前拦一道：要么给一个合法的字段名，要么在发请求前拿到指名道姓的错误。
/// </summary>
internal static class DispositionTokens
{
    /// <summary>返回第一个不能出现在 quoted-string 里的字符描述（如 <c>双引号 "</c>）；合法返回 null。</summary>
    public static string? FindInvalidCharacter(string value)
    {
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    return "双引号 \"";
                case '\\':
                    return "反斜杠 \\";
                case '\r':
                    return "CR (\\r)";
                case '\n':
                    return "LF (\\n)";
                case '\0':
                    return "NUL";
                default:
                    // quoted-string 只允许 HTAB / SP / obs-text 之外的可见字符：
                    // 其余 C0 控制字符（含 HTAB 之外的）与 DEL 会让解析端退回原始带引号形态。
                    if (c < 0x20 || c == 0x7F)
                    {
                        return $"控制字符 U+{(int)c:X4}";
                    }

                    break;
            }
        }

        return null;
    }
}

/// <summary>
/// multipart 字段名非法。只在本程序集内部流转，由 <see cref="RequestBodyFactory"/> 转成
/// 带配置上下文的 <see cref="AxiosException"/>（<c>ERR_BAD_OPTION_VALUE</c>）。
/// </summary>
internal sealed class InvalidFieldNameException : Exception
{
    public InvalidFieldNameException(string message)
        : base(message)
    {
    }
}
