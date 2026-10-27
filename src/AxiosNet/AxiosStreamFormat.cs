namespace AxiosNet;

/// <summary>流式响应（<see cref="AxiosResponse{TData}.Data"/> 为 <c>IAsyncEnumerable&lt;T&gt;</c>）的帧格式。</summary>
public enum AxiosStreamFormat
{
    /// <summary>行分隔 JSON（NDJSON / JSON Lines）：每行一个完整的 JSON 值，空行跳过。</summary>
    NewlineDelimitedJson = 0,

    /// <summary>
    /// Server-Sent Events（<c>text/event-stream</c>）：空行结束一帧，多行 <c>data:</c> 以换行拼接，
    /// <c>[DONE]</c> 视为流结束。
    /// </summary>
    ServerSentEvents = 1,

    /// <summary>单个 JSON 数组（<c>[{...},{...}]</c>）：按顶层元素逐帧交付，必须是 UTF-8 且顶层是数组。</summary>
    JsonArray = 2
}
