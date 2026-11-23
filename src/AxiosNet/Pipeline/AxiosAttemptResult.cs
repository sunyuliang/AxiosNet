
namespace AxiosNet.Pipeline;

/// <summary>一次尝试的结果：响应本体 + 流式正文的所有权凭证。</summary>
public readonly struct AxiosAttemptResult
{
    /// <summary>用非流式响应建结果（没有所有权凭证）。</summary>
    public AxiosAttemptResult(AxiosResponse response)
        : this(response, null)
    {
    }

    /// <summary>带所有权凭证构造：<paramref name="streamOwner"/> 非 null 时由结果接管释放责任。</summary>
    /// <param name="response">本次尝试得到的响应。</param>
    /// <param name="streamOwner">
    /// 流式正文的所有权凭证（<c>null</c> = 非流式，或流式但无需外部释放）；同时持有连接租约与上下文登记的全部跨终端租约，释放幂等。
    /// 中间件换掉响应时须把旧凭证带进新结果（<c>new AxiosAttemptResult(newResponse, old.StreamOwner)</c>）或显式释放，丢弃即泄漏。
    /// </param>
    public AxiosAttemptResult(AxiosResponse response, IDisposable? streamOwner)
        : this(response, streamOwner, leasesTransferred: streamOwner is ResponseStreamOwner)
    {
    }

    /// <summary>终端专用：流式成功时上下文里的租约随正文一起交出去，需要告诉闸门「别再归还它」。</summary>
    internal AxiosAttemptResult(AxiosResponse response, IDisposable? streamOwner, bool leasesTransferred)
    {
        // 拦下空响应。
        ArgumentNullException.ThrowIfNull(response);

        Response = response;
        StreamOwner = streamOwner;
        LeasesTransferred = leasesTransferred;
    }

    /// <summary>本次尝试得到的响应。</summary>
    public AxiosResponse Response { get; }

    /// <summary>流式正文的所有权凭证：<c>null</c> 表示本次尝试没有需要外部释放的资源。</summary>
    /// <remarks>封装正文流、响应消息、连接租约与上下文登记的跨终端租约，<see cref="IDisposable.Dispose"/> 时一并归还；替换响应时须处置它或带进新结果。</remarks>
    public IDisposable? StreamOwner { get; }

    /// <summary>上下文里登记的租约是否已随流式正文转交：<c>true</c> 时闸门不能再归还自己的槽位。</summary>
    /// <remarks>终端在流式成功时置它；公共构造器按 <see cref="StreamOwner"/> 是否为库内流式凭证推断（中间件自建的凭证不算）。</remarks>
    internal bool LeasesTransferred { get; }

    /// <summary>挡住 <c>default(AxiosAttemptResult)</c>（结构体的 default 绕得过构造器校验）。</summary>
    internal static AxiosAttemptResult Ensure(AxiosAttemptResult attempt, AxiosConfig? config)
    {
        if (attempt.Response is null)
        {
            throw AxiosException.InvalidOption(
                "尝试中间件返回了 default(AxiosAttemptResult)（没有响应）。"
                + "不调用 next 时请返回一份正经的结果，例如 new AxiosAttemptResult(response)；"
                + "替换流式响应时请把旧的 StreamOwner 一并处置或带进新结果。",
                config);
        }

        return attempt;
    }
}
