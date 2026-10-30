namespace AxiosNet.Cache;

/// <summary>
/// 缓存条目的存储：只负责「按 key 存 / 取一条响应」，可以是内存、共享内存，也可以是 Redis 之类的外部存储。
/// 键规则、存活时长、准入判定与并发合并（单飞）都在 <see cref="AxiosCache"/> 里，不归存储管。
/// </summary>
/// <remarks>
/// <see cref="AxiosShortCircuit"/> 是自描述的（状态码 / 头 / 原始字节），可以直接序列化成任何后端的一条记录。
/// 存储出错不应让请求失败：取失败按「没命中」处理，写失败只是这次不入缓存 —— 由 <see cref="AxiosCache"/> 兜住。
/// </remarks>
/// <example>
/// <code>
/// client.Defaults.Cache = new AxiosCache(new AxiosCacheOptions { Store = new RedisCacheStore(redis) });
/// </code>
/// </example>
public interface IAxiosCacheStore
{
    /// <summary>取一条；没有、已过期或已失效都返回 <c>null</c>。</summary>
    ValueTask<AxiosShortCircuit?> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>存一条；<paramref name="timeToLive"/> 是这条应有的存活时长，由存储自己决定怎么实现。</summary>
    ValueTask SetAsync(string key, AxiosShortCircuit entry, TimeSpan timeToLive, CancellationToken cancellationToken);

    /// <summary>摘掉一条，返回是否真的摘到了。</summary>
    ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken);

    /// <summary>清空；批量删除代价高的后端可以只做标记失效。</summary>
    ValueTask ClearAsync(CancellationToken cancellationToken);
}
