namespace AxiosNet.Cache;

/// <summary>
/// 可选能力：让存储把已过期但带校验器（<c>ETag</c> / <c>Last-Modified</c>）的条目留给条件请求复用。
/// </summary>
/// <remarks>
/// 实现本接口后，库在新鲜条目没命中时再取一次可复用条目，把校验器写成 <c>If-None-Match</c> / <c>If-Modified-Since</c> 发出，
/// <c>304</c> 时直接交付库里正文，只刷新响应头与存活时长。带校验器的条目保留多久由存储决定，过早清掉只是退化成完整下载。
/// 读取口径同 <see cref="IAxiosCacheStore.GetAsync"/>：出错按「没有」处理，配了 <c>TotalTimeout</c> 时受剩余预算约束。
/// </remarks>
public interface IAxiosCacheRevalidationStore
{
    /// <summary>取一条可供条件请求复用的条目（通常已过期）；没有、或条目不带校验器时返回 <c>null</c>。</summary>
    ValueTask<AxiosShortCircuit?> GetRevalidationAsync(string key, CancellationToken cancellationToken);
}
