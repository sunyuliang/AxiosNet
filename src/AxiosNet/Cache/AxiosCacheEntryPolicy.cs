namespace AxiosNet.Cache;

/// <summary>一条缓存条目的写入策略：最终存活时长，以及算它时用到的 HTTP 缓存信息。</summary>
/// <remarks>
/// <see cref="TimeToLive"/> 是库的结论，存储照做即可；其余字段是依据，自建存储可据此换策略（如按 <c>private</c> 拒绝共享、按 <see cref="Vary"/> 分区）。
/// Configured 模式下 HTTP 相关字段在首次读取时才解析。
/// </remarks>
public sealed class AxiosCacheEntryPolicy
{
    private readonly IReadOnlyDictionary<string, IEnumerable<string>>? _headers;
    private readonly DateTimeOffset _receivedAt;
    // 缓存的 HTTP 缓存语义（懒解析一次）：null 表示响应头里没有任何缓存指令。
    private HttpCacheInfo? _http;

    /// <summary>解析模式直接带上算好的 HTTP 信息；<see cref="CacheFreshnessMode.Configured"/> 只带原始响应头，等被读取时再解析。</summary>
    internal AxiosCacheEntryPolicy(
        TimeSpan timeToLive,
        TimeSpan configuredTimeToLive,
        CacheFreshnessMode mode,
        IReadOnlyDictionary<string, IEnumerable<string>>? headers,
        DateTimeOffset receivedAt,
        HttpCacheInfo? http)
    {
        TimeToLive = timeToLive;
        ConfiguredTimeToLive = configuredTimeToLive;
        Mode = mode;
        _headers = headers;
        _receivedAt = receivedAt;
        _http = http;
    }

    /// <summary>最终生效的存活时长：Configured 取配置值，Server 取剩余新鲜期，Intersection 取两者较小值；请求带 <c>max-age</c> 时再以它为上限。</summary>
    public TimeSpan TimeToLive { get; }

    /// <summary><see cref="AxiosCacheOptions.TimeToLive"/> 的配置值（未经 HTTP 语义调整）。</summary>
    public TimeSpan ConfiguredTimeToLive { get; }

    /// <summary>响应声明的 HTTP 新鲜期：<c>max-age</c>，否则 <c>Expires - Date</c>；都没有时为 <c>null</c>，非法 <c>Expires</c> 算零。</summary>
    public TimeSpan? HttpFreshnessLifetime => Http.FreshnessLifetime;

    /// <summary>落库时龄期：<c>Age</c> 与「收到时刻 - Date」取较大值，无缓存头时为零。</summary>
    public TimeSpan Age => Http.Age;

    /// <summary>这次写入采用的取值方式（取自 <see cref="AxiosCacheOptions.FreshnessMode"/>）。</summary>
    public CacheFreshnessMode Mode { get; }

    /// <summary>解析后的 <c>Cache-Control</c>；响应没带这个头时各项都是「未出现」。</summary>
    public CacheControlDirectives CacheControl => Http.CacheControl;

    /// <summary>解析后的 <c>Date</c> 头；缺失或非法时为 <c>null</c>。</summary>
    public DateTimeOffset? Date => Http.Date;

    /// <summary>解析后的 <c>Expires</c> 头；缺失或非法时为 <c>null</c>（非法值按「已过期」参与计算，见 <see cref="HttpFreshnessLifetime"/>）。</summary>
    public DateTimeOffset? Expires => Http.Expires;

    /// <summary>解析后的 <c>Vary</c> 头（逗号分隔，含 <c>*</c>）；没有再分区维度时为空。</summary>
    public IReadOnlyList<string> Vary => Http.Vary;

    /// <summary>HTTP 信息按需解析（与 <c>ResponseHeadersView</c> 同一套发布方式：并发首次访问可能各解析一次，只发布一份）。</summary>
    private HttpCacheInfo Http => Volatile.Read(ref _http) ?? ResolveHttp();

    /// <summary>首次访问时解析 HTTP 缓存信息，并按「只发布一份」写回（并发各解析一次也不会互相覆盖）。</summary>
    private HttpCacheInfo ResolveHttp()
    {
        // _headers 只在 http 为 null（Configured 模式）时才有值。
        var parsed = AxiosCache.ReadHttpCacheInfo(_headers!, _receivedAt);
        return Interlocked.CompareExchange(ref _http, parsed, null) ?? parsed;
    }
}
