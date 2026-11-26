using AxiosNet.Circuit;
using AxiosNet.Pipeline;
using AxiosNet.Retry;

namespace AxiosNet.Telemetry;

/// <summary>观测派发的护栏：把当前实现包一层，任何成员抛出的异常都在这里结束，不会外溢到请求路径。</summary>
/// <remarks>
/// 实现抛异常只毁掉自己的观测，不改写请求结果；<see cref="OutOfMemoryException"/> 不吞。每个成员内联 try/catch，不共用吃委托的辅助方法。
/// </remarks>
internal sealed class GuardedTelemetrySink : IAxiosTelemetrySink
{
    /// <summary>被包装的实现。</summary>
    private readonly IAxiosTelemetrySink _inner;

    /// <summary>包住指定实现。</summary>
    /// <param name="inner">真正的观测实现。</param>
    public GuardedTelemetrySink(IAxiosTelemetrySink inner) => _inner = inner;

    /// <summary>是否要观测；违约实现抛异常时按「不观测」处理。</summary>
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
    /// <remarks>交出的句柄绑在护栏上，调用方直接走 <see cref="AxiosRequestTrace.Stop"/> / <see cref="AxiosRequestTrace.SetRequestBodySize"/> 也不绕过异常边界。</remarks>
    /// <param name="config">本次请求的生效配置。</param>
    /// <param name="uri">最终 URL。</param>
    /// <returns>观测句柄；抛异常时为 <c>default</c>。</returns>
    public AxiosRequestTrace StartRequest(AxiosConfig config, Uri uri)
    {
        try
        {
            var trace = _inner.StartRequest(config, uri);

            // 句柄里的实现换成护栏自己：收尾与正文大小同样走本类。
            return trace.IsActive ? new AxiosRequestTrace(this, trace.State) : default;
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

    /// <summary>开观测并吞掉违约异常，返回「收尾要不要配对一次 <see cref="ExitRequest"/>」。</summary>
    /// <remarks>库代码用它而不是 <see cref="EnterRequest"/>：进出须在同一份派发快照上判定。</remarks>
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
