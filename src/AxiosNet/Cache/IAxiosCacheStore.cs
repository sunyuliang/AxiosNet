namespace AxiosNet.Cache;

/// <summary>缓存条目的存储：只负责「按键存 / 取一条响应」，可以是内存、共享内存或外部存储。</summary>
/// <remarks>
/// 键规则、TTL 计算、准入与单飞都在 <see cref="AxiosCache"/> 里，存储只按收到的键与 TTL 写入并自己执行过期。
/// 存储出错不让请求失败：取失败按「没命中」，写失败只是这次不入缓存。
/// </remarks>
/// <example>
/// <code>
/// client.Defaults.Caching = new AxiosCache(new AxiosCacheOptions { Store = new RedisCacheStore(redis) });
/// </code>
/// </example>
public interface IAxiosCacheStore
{
    /// <summary>取一条；没有、已过期或已失效都返回 <c>null</c>。</summary>
    /// <remarks>实现应尊重 <paramref name="cancellationToken"/>；挂起会被库按「总预算耗尽」放弃。</remarks>
    ValueTask<AxiosShortCircuit?> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>存一条；<paramref name="timeToLive"/> 是这条应有的存活时长，由存储自己决定怎么实现。</summary>
    ValueTask SetAsync(string key, AxiosShortCircuit entry, TimeSpan timeToLive, CancellationToken cancellationToken);

    /// <summary>存一条，并带上算存活时长用到的全部 HTTP 缓存信息（<see cref="AxiosCacheEntryPolicy"/>）。</summary>
    /// <remarks>库总是调用这个重载；默认实现转发到四参重载。</remarks>
#if NETSTANDARD2_0
    // 兼容资产没有默认接口实现：实现者须自己提供本重载（可只转发到四参重载）。
    ValueTask SetAsync(string key, AxiosShortCircuit entry, AxiosCacheEntryPolicy policy, CancellationToken cancellationToken);
#else
    ValueTask SetAsync(string key, AxiosShortCircuit entry, AxiosCacheEntryPolicy policy, CancellationToken cancellationToken)
        => SetAsync(key, entry, policy.TimeToLive, cancellationToken);
#endif

    /// <summary>摘掉一条，返回是否真的摘到了。</summary>
    ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken);

    /// <summary>清空；批量删除代价高的后端可以只做标记失效。</summary>
    ValueTask ClearAsync(CancellationToken cancellationToken);
}
