namespace AxiosNet.Cache;

/// <summary>
/// 一条响应里与缓存相关的 HTTP 信息（解析结果）：新鲜期、剩余新鲜期、龄期、<c>Cache-Control</c>、<c>Date</c> / <c>Expires</c> / <c>Vary</c>。
/// 只在需要时解析 —— 解析模式算 TTL 时，或自建存储读取 <see cref="AxiosCacheEntryPolicy"/> 的 HTTP 字段时。
/// </summary>
internal sealed class HttpCacheInfo
{
    internal HttpCacheInfo(
        TimeSpan? freshnessLifetime,
        TimeSpan age,
        CacheControlDirectives cacheControl,
        DateTimeOffset? date,
        DateTimeOffset? expires,
        IReadOnlyList<string> vary)
    {
        FreshnessLifetime = freshnessLifetime;
        Age = age;
        CacheControl = cacheControl;
        Date = date;
        Expires = expires;
        Vary = vary;

        Remaining = freshnessLifetime is { } lifetime
            ? (lifetime > age ? lifetime - age : TimeSpan.Zero)
            : (TimeSpan?)null;
    }

    /// <summary>响应声明的新鲜期；没有声明时为 <c>null</c>，已过期或非法的 <c>Expires</c> 为零。</summary>
    internal TimeSpan? FreshnessLifetime { get; }

    internal TimeSpan Age { get; }

    /// <summary>剩余新鲜期（新鲜期减龄期）；没有声明新鲜期时为 <c>null</c>，用尽时为零。</summary>
    internal TimeSpan? Remaining { get; }

    internal CacheControlDirectives CacheControl { get; }

    internal DateTimeOffset? Date { get; }

    internal DateTimeOffset? Expires { get; }

    internal IReadOnlyList<string> Vary { get; }
}
