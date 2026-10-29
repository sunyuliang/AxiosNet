namespace AxiosNet.Internal;

/// <summary>
/// multipart 的 field name / filename 合法性检查：quoted-string 里不能出现引号 / 反斜杠 / CR / LF / NUL 及控制字符。
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
                    // 其余 C0 控制字符与 DEL。
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
/// multipart 的调用方选项非法（部件字段名，或文件名与 Content-Type），由 <see cref="MultipartBodyBuilder"/>
/// 转成 <see cref="AxiosException"/>（<c>ERR_BAD_OPTION_VALUE</c>）。
/// </summary>
internal sealed class InvalidMultipartOptionException : Exception
{
    public InvalidMultipartOptionException(string message)
        : base(message)
    {
    }

    public InvalidMultipartOptionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
