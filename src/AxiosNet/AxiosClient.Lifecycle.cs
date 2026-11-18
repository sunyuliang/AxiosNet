namespace AxiosNet;

using AxiosNet.Internal;

// AxiosClient 的预热、释放与在途请求计数。
public sealed partial class AxiosClient
{
    // 释放后发起新请求的错误文案。
    private const string DisposedMessage = "AxiosClient 已释放，不能发起新请求";

    // 预热标记（0/1）：1 = 已完成；预热抛异常时回退 0，让下一次调用重新尝试。
    private int _warmedUp;

    /// <summary>
    /// 预热：提前完成静态首用（JSON 选项校验、响应解码器、状态文案、媒体类型判定）与默认基地址所属 origin 的连接池节点，
    /// 不建立 TCP 连接。
    /// </summary>
    /// <returns>本次是否真的做了预热；已预热过的实例返回 <c>false</c>。</returns>
    /// <remarks>
    /// 实例建好后、第一条业务请求之前调用一次。预热目标取 <see cref="Defaults"/> 的 <see cref="AxiosConfig.BaseAddress"/>；
    /// 没配基地址或注入自定义 Handler 时只做静态首用；基地址不是绝对 URI 时抛 <c>ERR_BAD_OPTION_VALUE</c>。
    /// 预热抛异常时不置「已完成」：异常照常外抛，下一次调用重新尝试；成功之后恒返回 <c>false</c>。
    /// 并发竞争只有一条真的执行，其余按「已经预热过」返回。
    /// 预热会冻结全局 JSON 选项实例，之后要改请换一个新的 <see cref="AxiosJson.Options"/>。
    /// </remarks>
    public bool Warmup()
    {
        // 已释放的实例不做事：与请求同一口径，不漏裸 ObjectDisposedException。
        if (Volatile.Read(ref _disposed) == 1)
        {
            throw DisposedError(Defaults);
        }

        if (Volatile.Read(ref _warmedUp) == 1)
        {
            return false;
        }

        // 先把预热目标解析出来：非法基地址要在置标记之前抛，否则后续调用会因为标记已置位而静默跳过。
        var origin = ResolveWarmupOrigin();

        // 并发竞争只让一条真的执行，其余按「已经预热过」返回。
        if (Interlocked.Exchange(ref _warmedUp, 1) != 0)
        {
            return false;
        }

        try
        {
            StaticWarmup.Run();

            // 注入 Handler 的实例没有池：除静态首用外无可预热。
            if (origin is not null)
            {
                _pool?.Prewarm(origin, Defaults.ProfileOrNull);
            }
        }
        catch
        {
            // 预热未完成：标记退回「未执行」，下一次调用重新尝试。
            Volatile.Write(ref _warmedUp, 0);
            throw;
        }

        return true;
    }

    /// <summary>预热目标：<see cref="AxiosConfig.BaseAddress"/> 解析出的绝对 URI；没有配置时为 <c>null</c>。</summary>
    private Uri? ResolveWarmupOrigin()
    {
        if (Defaults.BaseAddress is not { Length: > 0 } baseAddress)
        {
            return null;
        }

        if (Uri.TryCreate(baseAddress, UriKind.Absolute, out var origin))
        {
            return origin;
        }

        throw AxiosException.InvalidOption(
            $"Warmup() 只能用绝对基地址预建连接池节点（当前 BaseAddress = '{baseAddress}'）："
            + "请写成 https://host[:port][/path] 的形式，或改用 Pool.Prewarm(new Uri(具体 origin)) 指定要预热的站点。", Defaults);
    }

    /// <summary>最后一个在途请求结束时，补做 <see cref="Dispose"/> 推迟的资源释放。</summary>
    private void ReleaseInFlight()
    {
        if (Interlocked.Decrement(ref _inFlight) == 0 && Volatile.Read(ref _disposed) == 1)
        {
            DisposeResources();
        }
    }

    /// <summary>阶段 0 —— 入口守卫：先占一个在途名额，再复核释放标记；必须在调用方的 <c>try</c> 之外（它抛时名额还没占上，不该归还）。</summary>
    private void EnterInFlight(AxiosConfig config)
    {
        // net6.0 目标无 ObjectDisposedException.ThrowIf，手写检查。
        if (Volatile.Read(ref _disposed) == 1)
        {
            throw DisposedError(config);
        }

        Interlocked.Increment(ref _inFlight);

        if (Volatile.Read(ref _disposed) == 1)
        {
            ReleaseInFlight();
            throw DisposedError(config);
        }
    }

    /// <summary>已释放实例上的请求统一报 <c>ERR_CANCELED</c>，不漏裸 <see cref="ObjectDisposedException"/>。</summary>
    private static AxiosException DisposedError(AxiosConfig config)
        => AxiosException.Canceled(config, null, DisposedMessage, new ObjectDisposedException(nameof(AxiosClient)));

    /// <summary>释放实例：不打断在途请求，资源释放推迟到最后一个请求结束；此后新请求抛 <c>ERR_CANCELED</c>。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Volatile.Read(ref _inFlight) > 0)
        {
            // 在途请求结束时由 ReleaseInFlight 补做释放
            return;
        }

        DisposeResources();
    }

    /// <summary>资源释放本体（幂等）：注入的 HttpClient/Handler 与本实例自建的池。</summary>
    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
        {
            return;
        }

        _customClient?.Dispose();

        if (_ownsCustomHandler)
        {
            _customHandler?.Dispose();
        }

        if (_ownsPool)
        {
            // 优雅关闭：空闲节点立即释放，在途请求的最后一条租约归还时才回收节点。
            _pool?.Dispose();
        }
    }

    /// <summary>与 <see cref="Dispose"/> 等价，供 <c>await using</c> 使用。</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }
}
