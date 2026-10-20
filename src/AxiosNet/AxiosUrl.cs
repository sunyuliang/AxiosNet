namespace AxiosNet;

/// <summary>
/// URL 语法位置的转义辅助。
/// <para>
/// 库只对「结构化输入」自动编码：<see cref="AxiosConfig.Params"/> 的键与值、
/// <see cref="AxiosConfig.ContentType"/> 为 <c>application/x-www-form-urlencoded</c> 时的表单键值。
/// <see cref="AxiosConfig.Url"/> 则按原样参与拼接——手写在 URL 里的路径段与查询串**不会再被语义编码**
/// （只有 <see cref="System.Uri"/> 按 RFC 3986 做的归一化：空格 → <c>%20</c>、中文 → UTF-8 百分号编码、
/// 裸 <c>%</c> → <c>%25</c>），因此值里的 <c>/</c>、<c>?</c>、<c>#</c>、<c>&amp;</c>、<c>+</c>
/// 会直接改变 URL 的结构，而不是被当成值的一部分。
/// </para>
/// <para>
/// 所以只有两类值需要手动转义：<b>路径段</b>（用 <see cref="EncodePathSegment"/>）和
/// <b>手写查询串</b>里的值（用 <see cref="Uri.EscapeDataString"/>；更好的做法是把它们交给
/// <see cref="AxiosConfig.Params"/>，库会自动编码）。
/// </para>
/// </summary>
public static class AxiosUrl
{
    /// <summary>
    /// 把值转义成可以安全放进**单个路径段**的文本：<c>/</c> → <c>%2F</c>、空格 → <c>%20</c>、
    /// 中文 → UTF-8 百分号编码，<c>?</c> / <c>#</c> / <c>&amp;</c> / <c>+</c> 一并转义。
    /// </summary>
    /// <param name="value">要放进路径里的值（如路由参数、文件 id）。</param>
    /// <example>
    /// <code>
    /// var id = "a/b c";
    /// await http.GetAsync("items/" + AxiosUrl.EncodePathSegment(id));   // GET items/a%2Fb%20c
    /// </code>
    /// </example>
    /// <remarks>
    /// 转义后的 <c>%2F</c> 在服务端不会自动还原成 <c>/</c>：多数框架把它当普通字符，
    /// 需要用「允许编码斜杠」的路由设置或自行解码。要传层级路径请直接拼 <c>/</c>，别用本方法。
    /// </remarks>
    public static string EncodePathSegment(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Uri.EscapeDataString(value);
    }
}
