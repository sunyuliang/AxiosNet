namespace AxiosNet.Cache;

/// <summary>
/// 可选能力：让存储把<b>已过期但带校验器</b>（<c>ETag</c> / <c>Last-Modified</c>）的条目留给条件请求复用。
/// </summary>
/// <remarks>
/// <see cref="IAxiosCacheStore.GetAsync"/> 对过期条目返回 <c>null</c>；实现本接口后，库在「新鲜条目没命中」时
/// 再取一次可复用条目，把校验器写成 <c>If-None-Match</c> / <c>If-Modified-Since</c> 发出去：
/// 服务端回 <c>304 Not Modified</c> 时直接交付库里那份正文，只刷新响应头与存活时长。
/// <para>
/// 保留多久归存储自己定：没有校验器的过期条目可以照常清掉；带校验器的条目是条件请求的唯一依据，
/// 过早清掉只是退化成「每次完整下载」。内置 <see cref="MemoryCacheStore"/> 已实现 ——
/// 过期且带校验器的条目留到被覆盖、被容量压力清掉或 <see cref="IAxiosCacheStore.ClearAsync"/>。
/// </para>
/// <para>
/// 读取与 <see cref="IAxiosCacheStore.GetAsync"/> 同一口径：出错按「没有」处理，配了 <c>TotalTimeout</c> 时挂起受剩余总预算约束。
/// </para>
/// </remarks>
public interface IAxiosCacheRevalidationStore
{
    /// <summary>取一条可供条件请求复用的条目（通常已过期）；没有、或条目不带校验器时返回 <c>null</c>。</summary>
    ValueTask<AxiosShortCircuit?> GetRevalidationAsync(string key, CancellationToken cancellationToken);
}
