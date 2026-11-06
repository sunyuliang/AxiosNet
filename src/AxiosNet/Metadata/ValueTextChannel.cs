namespace AxiosNet.Metadata;

/// <summary>
/// 值转线上文本时所在的<b>通道</b>：传给 <c>AxiosConfig.Serialization.ValueText</c> 钩子，让同一个钩子能按位置给出不同文本
/// （典型用法：请求头里的日期用 RFC 1123，查询串里用 ISO 8601）。
/// </summary>
/// <remarks>
/// 通道只影响两件事：日期时间族的默认形态（裸标量正文用往返格式保真，其余折 UTC 毫秒）与二进制值的去向
/// （<see cref="Multipart"/> 的二进制按文件部件处理，不经过值格式化）。
/// 成员级 <see cref="ValueFormat"/> 压过实例钩子，所以钩子收到的通道一定是「成员没声明」的那些值。
/// </remarks>
public enum ValueTextChannel
{
    /// <summary>路径占位符，如 <c>/users/{id}</c> 里的 <c>id</c>。钩子返回的文本随后仍会被 URL 转义。</summary>
    Path,

    /// <summary>查询串，含嵌套对象 / 字典 / JSON 树的标量叶子。钩子返回的文本随后仍会被百分号编码。</summary>
    Query,

    /// <summary>请求头（<c>[Header]</c> 属性 / 参数，以及 <c>[HeaderCollection]</c> 字典里的每个值）。文本原样写进头，不做转义。</summary>
    Header,

    /// <summary><c>application/x-www-form-urlencoded</c> 的字段值。钩子返回的文本随后仍会被百分号编码。</summary>
    Form,

    /// <summary>multipart 的文本部件。二进制值走文件部件，不经过值格式化，所以这里只会出现标量。</summary>
    Multipart,

    /// <summary>裸标量请求体（<c>data</c> 本身就是标量）。此通道的日期时间默认用往返格式保真，二进制默认按原始字节发送。</summary>
    ScalarBody
}
