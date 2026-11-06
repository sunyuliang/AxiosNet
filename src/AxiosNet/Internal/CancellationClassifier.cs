namespace AxiosNet.Internal;

/// <summary>取消类异常的归属。</summary>
internal enum CancellationKind
{
    /// <summary>没有已知令牌取消：异常不是本库产生的取消，调用处应原样抛出。</summary>
    None,

    /// <summary>读取 / 写调用自备的令牌取消：调用方自己的令牌，原样抛出。</summary>
    Caller,

    /// <summary>本次请求的外部令牌取消：统一映射成 <c>ERR_CANCELED</c>。</summary>
    Canceled,

    /// <summary>本次尝试的超时令牌取消：统一映射成 <c>ECONNABORTED</c>。</summary>
    Timeout,
}

/// <summary>
/// 取消分类的唯一判定：读取令牌 → 外部令牌 → 超时。
/// 顺序固定，避免调用方取消与超时同时触发时各路径给出不同归属。
/// </summary>
internal static class CancellationClassifier
{
    /// <param name="externalToken">本次请求的外部取消令牌。</param>
    /// <param name="timeoutToken">本次尝试的超时令牌；不限时为 <c>null</c>。</param>
    /// <param name="readToken">当前 I/O 调用自备的令牌；没有则传 <see cref="CancellationToken.None"/>。</param>
    public static CancellationKind Classify(
        CancellationToken externalToken,
        CancellationToken? timeoutToken,
        CancellationToken readToken)
    {
        // 调用方自己的令牌最具体：它取消时由调用方处理异常，库不改写错误码。
        if (readToken.IsCancellationRequested)
        {
            return CancellationKind.Caller;
        }

        if (externalToken.IsCancellationRequested)
        {
            return CancellationKind.Canceled;
        }

        if (timeoutToken is { IsCancellationRequested: true })
        {
            return CancellationKind.Timeout;
        }

        return CancellationKind.None;
    }
}
