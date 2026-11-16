using AxiosNet.Circuit;
using AxiosNet.Pipeline;
using AxiosNet.Retry;

namespace AxiosNet.Telemetry;

/// <summary>
/// 观测派发的护栏：把当前实现包一层，<b>任何成员抛出的异常都在这里结束</b>，不会外溢到请求路径。
/// </summary>
/// <remarks>
/// <para>
/// 可替换实现是第三方代码（<see cref="AxiosTelemetry.Sink"/> 公开可写）。它的异常一旦外溢，
/// 后果不是「观测丢了」而是「请求坏了」：<c>finally</c> 里的收尾异常会盖掉请求结果与真正的失败原因，
/// 缓存 / 闸门 / 池 的事件异常会把一条能成功的请求变成失败。所以派发统一走
/// <see cref="AxiosTelemetry.Dispatch"/>，违约实现只毁掉自己的观测。
/// </para>
/// <para>
/// 库内没有绕开护栏的旁路：连「进」要不要配对「出」这种判定（<see cref="TryEnterRequest"/>）也由护栏
/// 在<b>自己那份快照</b>上完成，否则两次读 <see cref="AxiosTelemetry.Sink"/> 之间的替换会把一条请求劈成两家。
/// </para>
/// <para>
/// 每个成员都内联 try/catch 而不是共用一个吃 <see cref="Action"/> 的辅助方法：委托会为
/// <see cref="IAxiosTelemetrySink.RecordLease"/> / <see cref="IAxiosTelemetrySink.EnterLease"/> 这类
/// 热路径每次派发多分配一个闭包，而本库的分配不变量是「不随请求条数增长」。
/// </para>
/// <para>
/// <see cref="OutOfMemoryException"/> 不吞：那不是「观测写错了」，进程状态已经不可信，按常规失败处理。
/// 吞掉之后没有二次上报渠道（库不写日志），需要诊断请在自己的实现内部 try/catch。
/// </para>
/// </remarks>
internal sealed class GuardedTelemetrySink : IAxiosTelemetrySink
{
    /// <summary>被包装的实现。</summary>
    private readonly IAxiosTelemetrySink _inner;

    /// <summary>包住指定实现。</summary>
    /// <param name="inner">真正的观测实现。</param>
    public GuardedTelemetrySink(IAxiosTelemetrySink inner) => _inner = inner;

    /// <summary>
    /// 是否要观测。违约实现抛异常时按「不观测」处理：观测坏掉不该挡住请求，
    /// 而返回 <c>true</c> 会让后续每一步都再撞一次同一个异常。
    /// </summary>
    /// <param name="config">本次请求的生效配置。</param>
    /// <returns>实现声明的开关；抛异常时为 <c>false</c>。</returns>
    public bool IsEnabled(AxiosConfig? config)
    {
        try
        {
            return _inner.IsEnabled(config);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>开观测；违约实现抛异常时退化成「本次没有观测」。</summary>
    /// <param name="config">本次请求的生效配置。</param>
    /// <param name="uri">最终 URL。</param>
    /// <returns>观测句柄；抛异常时为 <c>default</c>。</returns>
    public AxiosRequestTrace StartRequest(AxiosConfig config, Uri uri)
    {
        try
        {
            return _inner.StartRequest(config, uri);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return default;
        }
    }

    /// <inheritdoc />
    public void StopRequest(in AxiosRequestTrace trace)
    {
        try
        {
            _inner.StopRequest(in trace);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    /// <inheritdoc />
    public void SetRequestBodySize(in AxiosRequestTrace trace, long? size)
    {
        try
        {
            _inner.SetRequestBodySize(in trace, size);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    /// <summary>
    /// 开观测并自己吞掉违约异常，返回「收尾要不要配对一次 <see cref="ExitRequest"/>」。
    /// </summary>
    /// <remarks>
    /// 库代码用它而不是 <see cref="EnterRequest"/>：收尾要不要补一次「出」，取决于「进」有没有真的成功。
    /// 这个判定放在护栏里做，才能保证「进」和「出」都落在<b>同一份派发快照</b>上——若调用方先取
    /// <see cref="AxiosTelemetry.Dispatch"/> 判开关、再去读 <see cref="AxiosTelemetry.Sink"/> 记「进」，
    /// 两次读之间换掉实现就会「进记在新实现、出记在旧实现」，新实现的在途数永久多一条。
    /// </remarks>
    /// <returns><c>true</c> = 已计入水位，收尾必须配一次 <see cref="ExitRequest"/>；<c>false</c> = 本次不观测。</returns>
    public bool TryEnterRequest()
    {
        try
        {
            _inner.EnterRequest();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void EnterRequest() => TryEnterRequest();

    /// <inheritdoc />
    public void ExitRequest()
    {
        try
        {
            _inner.ExitRequest();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    /// <inheritdoc />
    public void EnterLease()
    {
        try
        {
            _inner.EnterLease();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    /// <inheritdoc />
    public void ExitLease()
    {
        try
        {
            _inner.ExitLease();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    /// <inheritdoc />
    public void EnterPool()
    {
        try
        {
            _inner.EnterPool();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    /// <inheritdoc />
    public void ExitPool()
    {
        try
        {
            _inner.ExitPool();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    /// <inheritdoc />
    public void RecordLease(bool reused)
    {
        try
        {
            _inner.RecordLease(reused);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    /// <inheritdoc />
    public void RecordEndpointCreated()
    {
        try
        {
            _inner.RecordEndpointCreated();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    /// <inheritdoc />
    public void RecordEndpointEvicted(string reason)
    {
        try
        {
            _inner.RecordEndpointEvicted(reason);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    /// <inheritdoc />
    public void TagPoolReuse(bool reused)
    {
        try
        {
            _inner.TagPoolReuse(reused);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    /// <inheritdoc />
    public void CircuitUntrackedHost()
    {
        try
        {
            _inner.CircuitUntrackedHost();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    /// <inheritdoc />
    public void RequestSucceeded(AxiosConfig config, Uri uri, AxiosResponse response, long startTimestamp, in AxiosRequestTrace trace)
    {
        try
        {
            _inner.RequestSucceeded(config, uri, response, startTimestamp, in trace);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    /// <inheritdoc />
    public void RequestFailed(AxiosConfig? config, Uri? uri, Exception exception, long startTimestamp, in AxiosRequestTrace trace)
    {
        try
        {
            _inner.RequestFailed(config, uri, exception, startTimestamp, in trace);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    /// <inheritdoc />
    public void GateRejected(AxiosConfig config, string reason)
    {
        try
        {
            _inner.GateRejected(config, reason);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    /// <inheritdoc />
    public void RequestRetried(in AxiosRetryContext context, Uri uri)
    {
        try
        {
            _inner.RequestRetried(in context, uri);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    /// <inheritdoc />
    public void RetryBudgetRefused(AxiosConfig? config)
    {
        try
        {
            _inner.RetryBudgetRefused(config);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    /// <inheritdoc />
    public void CacheHit(AxiosConfig? config)
    {
        try
        {
            _inner.CacheHit(config);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    /// <inheritdoc />
    public void CacheMiss(AxiosConfig? config)
    {
        try
        {
            _inner.CacheMiss(config);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    /// <inheritdoc />
    public void CacheStored(AxiosConfig? config)
    {
        try
        {
            _inner.CacheStored(config);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    /// <inheritdoc />
    public void CacheStoreFailed(AxiosConfig? config)
    {
        try
        {
            _inner.CacheStoreFailed(config);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    /// <inheritdoc />
    public void CacheRevalidated(AxiosConfig? config, in AxiosRequestTrace trace)
    {
        try
        {
            _inner.CacheRevalidated(config, in trace);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    /// <summary>是否要收尝试级事件；违约实现抛异常时按「不收」处理。</summary>
    /// <param name="config">本次请求的生效配置。</param>
    /// <returns>实现声明的开关；抛异常时为 <c>false</c>。</returns>
    public bool IsAttemptAuditEnabled(AxiosConfig? config)
    {
        try
        {
            return _inner.IsAttemptAuditEnabled(config);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void AttemptSucceeded(in AxiosAttemptContext context, int? status, long startTimestamp)
    {
        try
        {
            _inner.AttemptSucceeded(in context, status, startTimestamp);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    /// <inheritdoc />
    public void AttemptFailed(in AxiosAttemptContext context, Exception exception, long startTimestamp)
    {
        try
        {
            _inner.AttemptFailed(in context, exception, startTimestamp);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }
}
