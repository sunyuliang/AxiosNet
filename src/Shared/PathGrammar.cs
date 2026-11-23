namespace AxiosNet.Internal.Spec;

/// <summary>
/// OpenAPI 路径模板的占位符文法，三个消费方共用同一份源文件（运行期绑定、源生成器、CLI 读取器）：
/// 名字由 ASCII 字母或下划线开头，后续为字母、数字或下划线。
/// </summary>
internal static class PathGrammar
{
    /// <summary>占位符名的正则片段。</summary>
    public const string Name = "[A-Za-z_][A-Za-z0-9_]*";

    /// <summary>匹配整个占位符的正则模式（<c>{名}</c>），捕获组 1 为名字。</summary>
    public const string Placeholder = "\\{(" + Name + ")\\}";

    /// <summary>能否作为占位符名的首字符。</summary>
    /// <param name="c">待判定的字符。</param>
    public static bool IsNameStart(char c) => c == '_' || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

    /// <summary>能否作为占位符名的后续字符（首字符集合加数字）。</summary>
    /// <param name="c">待判定的字符。</param>
    public static bool IsNameChar(char c) => IsNameStart(c) || (c >= '0' && c <= '9');
}
