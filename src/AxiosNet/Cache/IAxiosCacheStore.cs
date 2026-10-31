namespace AxiosNet.Cache;

/// <summary>
/// 缓存条目的存储：只负责「按键存 / 取一条响应」，可以是内存、共享内存，也可以是 Redis 之类的外部存储。
/// 键规则、存活时长的计算、准入判定与并发合并（单飞）都在 <see cref="AxiosCache"/> 里；存储只按收到的键与 TTL
/// 写入，并自己执行过期（删除还是只做逻辑失效由存储决定）。
/// </summary>
/// <remarks>
/// <see cref="AxiosShortCircuit"/> 是自描述的（状态码 / 头 / 原始字节），可以直接序列化成任何后端的一条记录。
/// 存储出错不应让请求失败：取失败按「没命中」处理，写失败只是这次不入缓存 —— 由 <see cref="AxiosCache"/> 兜住。
/// 例外是配了 <c>TotalTimeout</c> 时，读取挂起超出本次请求的剩余总预算：按「总预算耗尽」报出，不会无限期等存储。
/// </remarks>
/// <example>
/// <code>
/// client.Defaults.Cache = new AxiosCache(new AxiosCacheOptions { Store = new RedisCacheStore(redis) });
/// </code>
/// </example>
public interface IAxiosCacheStore
{
    /// <summary>取一条；没有、已过期或已失效都返回 <c>null</c>。</summary>
    /// <remarks>
    /// 实现应当尊重 <paramref name="cancellationToken"/>：配了 <c>TotalTimeout</c> 时，挂起不返回会被库按
    /// 「总预算耗尽」放弃（并观察后续失败）；没配则库会一直等到实现返回。
    /// </remarks>
    ValueTask<AxiosShortCircuit?> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>存一条；<paramref name="timeToLive"/> 是这条应有的存活时长，由存储自己决定怎么实现。</summary>
    ValueTask SetAsync(string key, AxiosShortCircuit entry, TimeSpan timeToLive, CancellationToken cancellationToken);

    /// <summary>
    /// 存一条，并带上算这条存活时长用到的全部 HTTP 缓存信息（<see cref="AxiosCacheEntryPolicy"/>）：
    /// 生效 TTL、配置 TTL、HTTP 新鲜期、龄期、解析后的 <c>Cache-Control</c>、<c>Date</c> / <c>Expires</c> / <c>Vary</c>。
    /// </summary>
    /// <remarks>
    /// 库总是调用这个重载。默认实现直接转发到 <see cref="SetAsync(string, AxiosShortCircuit, TimeSpan, CancellationToken)"/>，
    /// 所以只实现四个方法的老存储不用改；需要按响应头做自己的策略（例如共享存储拒绝 <c>private</c>、按 <c>Vary</c> 分区、
    /// 用 <c>stale-while-revalidate</c> 延长可服务窗口）时再覆写它。
    /// </remarks>
    ValueTask SetAsync(string key, AxiosShortCircuit entry, AxiosCacheEntryPolicy policy, CancellationToken cancellationToken)
        => SetAsync(key, entry, policy.TimeToLive, cancellationToken);

    /// <summary>摘掉一条，返回是否真的摘到了。</summary>
    ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken);

    /// <summary>清空；批量删除代价高的后端可以只做标记失效。</summary>
    ValueTask ClearAsync(CancellationToken cancellationToken);
}
