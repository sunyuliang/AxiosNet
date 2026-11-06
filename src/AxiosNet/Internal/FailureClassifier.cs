namespace AxiosNet.Internal;

/// <summary>
/// 失败分类的唯一判定：重试「是否瞬态」与熔断「是否计入故障」共用同一张表，避免两处名单漂移。
/// 纯类型判定，无委托 / 装箱 / 分配。
/// </summary>
internal static class FailureClassifier
{
    /// <summary>
    /// 网络层故障（换一次发送可能好）：库错误码 <c>ERR_NETWORK</c> / <c>ECONNABORTED</c>，
    /// 或底层 <see cref="HttpRequestException"/> / <see cref="IOException"/>。
    /// </summary>
    public static bool IsNetworkFailure(Exception? exception) => exception switch
    {
        AxiosException { Code: AxiosException.ErrNetwork or AxiosException.ErrConnAborted } => true,
        HttpRequestException => true,
        IOException => true,
        _ => false
    };

    /// <summary>取消的两种形态都认：<see cref="OperationCanceledException"/> 与 <c>ERR_CANCELED</c>。</summary>
    public static bool IsCancellation(Exception exception) =>
        exception is OperationCanceledException
        || exception is AxiosException { Code: AxiosException.ErrCanceled };
}
