namespace AxiosNet.Cache;

/// <summary>
/// 一条缓存条目的写入策略：最终存活时长，以及算它时用到的 HTTP 缓存信息（解析模式下落库前就已解析，其余情况在读取时才解析）。
/// 随 <see cref="IAxiosCacheStore.SetAsync(string, AxiosShortCircuit, AxiosCacheEntryPolicy, CancellationToken)"/> 交给存储。
/// </summary>
/// <remarks>
/// <see cref="TimeToLive"/> 是库的结论，存储照做即可；其余字段是依据 —— 自建存储（共享 / 持久化）可以据此换一套自己的策略，
/// 例如按 <see cref="CacheControl"/> 的 <c>private</c> 拒绝写入共享缓存、或按 <see cref="Vary"/> 自己分区。
/// 响应完全没有缓存头时，除 <see cref="TimeToLive"/> / <see cref="ConfiguredTimeToLive"/> 外各项都取中性值（<c>null</c> / 零 / 空）。
/// <para>
/// <see cref="CacheFreshnessMode.Configured"/> 下落库前不解析响应头，HTTP 相关字段在第一次被读取时才解析 ——
/// 只关心 TTL 的存储（含内置 <see cref="MemoryCacheStore"/>）完全不会付出解析成本。
/// </para>
/// </remarks>
public sealed class AxiosCacheEntryPolicy
{
    private readonly IReadOnlyDictionary<string, IEnumerable<string>>? _headers;
    private readonly DateTimeOffset _receivedAt;
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

    /// <summary>最终生效的存活时长（库的结论）：<see cref="CacheFreshnessMode.Configured"/> 取配置值，
    /// <see cref="CacheFreshnessMode.Server"/> 取剩余新鲜期，<see cref="CacheFreshnessMode.Intersection"/> 取两者较小值；
    /// 请求带 <c>max-age</c> 时再以它为上限。</summary>
    public TimeSpan TimeToLive { get; }

    /// <summary><see cref="AxiosCacheOptions.TimeToLive"/> 的配置值（未经 HTTP 语义调整）。</summary>
    public TimeSpan ConfiguredTimeToLive { get; }

    /// <summary>
    /// 响应声明的 HTTP 新鲜期：<c>max-age</c>，没有则 <c>Expires - Date</c>（<c>Date</c> 缺失时用收到响应的时刻）；
    /// 两者都没有时为 <c>null</c>。已过期的 <c>Expires</c> 与非法 <c>Expires</c> 都算零。
    /// </summary>
    public TimeSpan? HttpFreshnessLifetime => Http.FreshnessLifetime;

    /// <summary>
    /// 落库时的当前龄期：取 <c>Age</c> 头与「收到响应时刻 - Date」的较大值（RFC 9111 的 apparent age），无缓存头时为零。
    /// </summary>
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

    private HttpCacheInfo ResolveHttp()
    {
        // _headers 只在 http 为 null 时才有值（Configured 模式），两者互为反证。
        var parsed = AxiosCache.ReadHttpCacheInfo(_headers!, _receivedAt);
        return Interlocked.CompareExchange(ref _http, parsed, null) ?? parsed;
    }
}
