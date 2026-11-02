using AxiosNet.Cache;

namespace AxiosNet.Tests.Infrastructure;

/// <summary>把最近一次落库的条目留在手边，用来比较条目正文与响应正文是不是同一块内存。</summary>
/// <remarks>没实现 <see cref="IAxiosCacheRevalidationStore"/>：用它的用例不会发条件请求。</remarks>
public sealed class RecordingCacheStore : IAxiosCacheStore
{
    private readonly MemoryCacheStore _inner = new();

    public AxiosShortCircuit? LastEntry { get; private set; }

    public ValueTask<AxiosShortCircuit?> GetAsync(string key, CancellationToken cancellationToken)
        => _inner.GetAsync(key, cancellationToken);

    public ValueTask SetAsync(string key, AxiosShortCircuit entry, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        LastEntry = entry;
        return _inner.SetAsync(key, entry, timeToLive, cancellationToken);
    }

    public ValueTask SetAsync(string key, AxiosShortCircuit entry, AxiosCacheEntryPolicy policy, CancellationToken cancellationToken)
    {
        LastEntry = entry;
        return _inner.SetAsync(key, entry, policy, cancellationToken);
    }

    public ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken)
        => _inner.RemoveAsync(key, cancellationToken);

    public ValueTask ClearAsync(CancellationToken cancellationToken)
        => _inner.ClearAsync(cancellationToken);
}
