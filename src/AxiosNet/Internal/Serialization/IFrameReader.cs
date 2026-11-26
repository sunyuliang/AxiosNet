namespace AxiosNet.Internal.Serialization;

/// <summary>分帧状态机：每次交出下一帧的载荷，流结束后恒返回 <c>null</c>。</summary>
/// <remarks>
/// 行分帧（NDJSON / SSE）与 JSON 数组分帧各一个实现；两者共用同一个 <see cref="SegmentedBuffer"/> 读窗口，
/// 自身只维护「怎么切」的状态，不碰流的读取细节。
/// </remarks>
internal interface IFrameReader
{
    /// <summary>读下一个可交付的帧。</summary>
    /// <param name="token">枚举令牌，用于取消挂起的读取。</param>
    /// <returns>帧载荷；<c>null</c> 表示流结束。</returns>
    ValueTask<FramePayload?> ReadAsync(CancellationToken token);
}
