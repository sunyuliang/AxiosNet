namespace AxiosNet;

/// <summary>
/// 流式响应（<see cref="AxiosResponse{TData}.Data"/> 为 <c>IAsyncEnumerable&lt;T&gt;</c>）的帧格式。
/// </summary>
public enum AxiosStreamFormat
{
    /// <summary>
    /// 行分隔 JSON（NDJSON / JSON Lines）：每行一个完整的 JSON 值，空行跳过。
    /// </summary>
    NewlineDelimitedJson = 0,

    /// <summary>
    /// Server-Sent Events（<c>text/event-stream</c>）：空行结束一帧，多行 <c>data:</c> 以换行拼接，
    /// 冒号开头的注释与 <c>event:</c> / <c>id:</c> / <c>retry:</c> 字段忽略，<c>[DONE]</c> 视为流结束。
    /// </summary>
    ServerSentEvents = 1,

    /// <summary>
    /// 单个 JSON 数组（<c>[{...},{...}]</c>）：按<b>顶层元素</b>逐帧交付，元素之间的空白与换行随意。
    /// <para>
    /// 分帧用 <c>Utf8JsonReader</c> 增量做（不先把整个数组读进内存）：字符串里的 <c>,</c> / <c>]</c>、
    /// 转义序列、嵌套对象与数组都由解析器自己跨过，因此数组多大都不影响内存占用，
    /// 单个元素上限与 NDJSON / SSE 一致（8 MB）。
    /// </para>
    /// <para>
    /// 与另外两种格式的差异：JSON 必须是 <b>UTF-8</b>（宽字符编码与 gbk 之类非 UTF-8 单字节编码在取流时
    /// 直接报 <c>ERR_BAD_RESPONSE</c>）；顶层必须是数组，标量元素（<c>[1,2,3]</c>）同样逐帧交付；
    /// 见到收尾的 <c>]</c> 立即结束，不等连接关闭、也不校验尾部字节；没等到 <c>]</c> 就断流同样报
    /// <c>ERR_BAD_RESPONSE</c>（已交付的元素保留）。
    /// </para>
    /// </summary>
    JsonArray = 2
}
