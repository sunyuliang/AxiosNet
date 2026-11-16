namespace AxiosNet.Internal;

/// <summary>取消类异常的归属。</summary>
internal enum CancellationKind
{
    /// <summary>没有已知令牌取消：不是本库产生的取消；仍归一成 <c>ERR_CANCELED</c>，不能被误报成解析 / 网络失败。</summary>
    None,

    /// <summary>读取 / 写调用自备的令牌取消：优先级最高；最终同样归一成 <c>ERR_CANCELED</c>，原始 OCE 留在 <c>Cause</c>。</summary>
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
/// <remarks>
/// <b>已知契约（当前不可达，不要「修」）</b>：两层取消包装叠加时（外层带计时器、内层没有任何令牌源），
/// 内层收到的「调用方令牌」其实就是外层的超时令牌，于是外层的超时会被内层归成
/// <see cref="CancellationKind.Caller"/>、最终报 <c>ERR_CANCELED</c> 而不是 <c>ECONNABORTED</c>。
/// 现在不会发生：每条响应只包一层 <see cref="TimeoutStream"/>（见 <c>AxiosClient.Send.cs</c> 的流式包装处），
/// 内层不存在自己的计时源。将来若给同一条响应叠第二层包装，归属判定要在这里重新过一遍。
/// </remarks>
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
        // 调用方自己的令牌最具体，归属优先级最高；最终交给调用方的错误码仍统一由调用处翻译
        // （普通请求路径与流读路径都归一成 ERR_CANCELED）。
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
