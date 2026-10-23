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
    ServerSentEvents = 1
}
