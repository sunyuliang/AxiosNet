namespace AxiosNet;

/// <summary>URL 语法辅助：库只自动编码 <see cref="AxiosConfig.Params"/> 与 urlencoded 表单的键值，<see cref="AxiosConfig.Url"/> 原样参与拼接。</summary>
/// <remarks>路径段需自行调用 <see cref="EncodePathSegment"/>。</remarks>
public static class AxiosUrl
{
    /// <summary>把值转义成可放进单个路径段的文本：<c>/</c> → <c>%2F</c>、空格 → <c>%20</c>、中文 → UTF-8 百分号编码，<c>?</c> / <c>#</c> / <c>&amp;</c> / <c>+</c> 一并转义。</summary>
    /// <param name="value">要放进路径里的值（如路由参数、文件 id）。</param>
    /// <example>
    /// <code>
    /// var id = "a/b c";
    /// await http.GetAsync("items/" + AxiosUrl.EncodePathSegment(id));   // GET items/a%2Fb%20c
    /// </code>
    /// </example>
    /// <remarks>
    /// 服务端通常不会把 <c>%2F</c> 还原成 <c>/</c>；要传层级路径请直接拼 <c>/</c>。
    /// </remarks>
    public static string EncodePathSegment(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Uri.EscapeDataString(value);
    }
}
