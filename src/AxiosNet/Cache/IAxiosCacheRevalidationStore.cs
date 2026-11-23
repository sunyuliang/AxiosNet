namespace AxiosNet.Cache;

/// <summary>可选能力：让存储把已过期但带校验器（<c>ETag</c> / <c>Last-Modified</c>）的条目留给条件请求复用。</summary>
/// <remarks>
/// 新鲜条目没命中时再取一次可复用条目，带条件头发送，<c>304</c> 时交付库里正文。保留多久由存储决定；
/// 出错按「没有」处理。
/// </remarks>
public interface IAxiosCacheRevalidationStore
{
    /// <summary>取一条可供条件请求复用的条目（通常已过期）；没有、或条目不带校验器时返回 <c>null</c>。</summary>
    ValueTask<AxiosShortCircuit?> GetRevalidationAsync(string key, CancellationToken cancellationToken);
}
