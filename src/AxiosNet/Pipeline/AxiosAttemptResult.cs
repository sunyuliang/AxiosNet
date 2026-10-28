using AxiosNet.Internal;

namespace AxiosNet.Pipeline;

/// <summary>
/// 一次尝试的结果：响应本体 + 流式正文的所有权凭证。结构体，逐次尝试零分配。
/// </summary>
public readonly struct AxiosAttemptResult
{
    /// <summary>用非流式响应建结果（没有所有权凭证）。</summary>
    public AxiosAttemptResult(AxiosResponse response)
        : this(response, null)
    {
    }

    /// <summary>完整构造。</summary>
    /// <param name="response">本次尝试得到的响应。</param>
    /// <param name="streamOwner">
    /// 流式正文的所有权凭证（<c>null</c> = 非流式，或流式但无需外部释放）。
    /// 它同时持有连接租约与并发槽位：<b>丢弃即泄漏</b>，没有终结器兜底。
    /// 中间件换掉响应时把旧凭证带进新结果 —— <c>new AxiosAttemptResult(newResponse, old.StreamOwner)</c>：
    /// 外层发现新响应的 <see cref="AxiosResponse.Data"/> 不是这份凭证就会释放它；传 <c>null</c> 则旧凭证再无人引用，
    /// 租约与槽位一直挂到进程退出（闸门的兜底只能还并发槽位，还不了连接租约）。
    /// 只有库自己发出的流式凭证（<see cref="AxiosResponse"/> 的流式正文）能让并发槽位继续随正文走；
    /// 中间件自建的凭证只是在响应侧被交给调用方，闸门照常归还槽位 —— 自建凭证不占并发额度。
    /// 释放幂等，所以「中间件先 Dispose 旧凭证、再把同一份带进新结果」也是安全的：外层的补释放条件是
    /// 「新响应的 Data 不是这份凭证」，而重复释放由凭证本身兜住。
    /// </param>
    public AxiosAttemptResult(AxiosResponse response, IDisposable? streamOwner)
        : this(response, streamOwner, slotTransferred: streamOwner is ResponseStreamOwner)
    {
    }

    /// <summary>终端专用：流式成功时槽位随正文流转交出去，需要告诉闸门「别再归还它」。</summary>
    internal AxiosAttemptResult(AxiosResponse response, IDisposable? streamOwner, bool slotTransferred)
    {
        // 构造一个没有响应的结果会让外层在拿 Data 时空引用；这里拦下比让它在几层之外炸掉好定位。
        ArgumentNullException.ThrowIfNull(response);

        Response = response;
        StreamOwner = streamOwner;
        SlotTransferred = slotTransferred;
    }

    /// <summary>本次尝试得到的响应。</summary>
    public AxiosResponse Response { get; }

    /// <summary>
    /// 流式正文的所有权凭证：<c>null</c> 表示本次尝试没有需要外部释放的资源。
    /// 它封装了正文流、响应消息、连接租约与并发槽位，<see cref="IDisposable.Dispose"/> 时一并归还。
    /// </summary>
    /// <remarks>
    /// 中间件替换响应时若不同时处置它，租约与槽位会一直挂到进程退出 ——
    /// 外层只在「响应对象被换掉」这一个条件下兜底释放，换掉响应的人是唯一知道旧值还有没有引用的地方。
    /// </remarks>
    public IDisposable? StreamOwner { get; }

    /// <summary>
    /// 并发槽位是否已随流式正文转交给 <see cref="StreamOwner"/>：<c>true</c> 时闸门不能再归还它，
    /// 否则正文还在下载、该请求却已不占并发额度。
    /// 终端在流式成功时置它；公共构造器按「StreamOwner 是不是库内流式凭证」推断
    /// （中间件自建的凭证不算：闸门仍会归还自己的槽位）。
    /// </summary>
    internal bool SlotTransferred { get; }

    /// <summary>
    /// 挡住 <c>default(AxiosAttemptResult)</c>：结构体的 default 绕得过构造器校验，中间件忘了返回正经结果时，
    /// 在这里报一个能定位的错，而不是让空引用在几层之外炸出来。
    /// </summary>
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
