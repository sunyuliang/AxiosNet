namespace AxiosNet.Cache;

/// <summary><see cref="AxiosCache"/> 的策略选项。</summary>
public sealed class AxiosCacheOptions
{
    /// <summary>条目存活时长，默认 30 秒；必须大于零（零会让条目刚存进去就过期）。</summary>
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>缓存时长的取值方式，默认 <see cref="CacheFreshnessMode.Configured"/>：只信 <see cref="TimeToLive"/>，不解析响应头。</summary>
    /// <remarks>
    /// Server / Intersection 会解析 <c>Cache-Control</c> / <c>Expires</c> / <c>Age</c> 并取交集：请求 <c>no-store</c> 不读不写，
    /// <c>no-cache</c> 跳过新鲜查询（仍可落库），<c>max-age=N</c> 作落库上限，响应 <c>Vary: *</c> 不落库。
    /// </remarks>
    public CacheFreshnessMode FreshnessMode { get; set; }

    /// <summary>同键并发只发一次（单飞），默认 <c>true</c>；关掉后每个请求各自发送，只共享已落库的条目。</summary>
    /// <remarks>
    /// 请求侧要求绕开缓存时不查缓存、也不参与单飞。领导者拿到非 2xx 时只共享状态码 / 头 / 原始正文，跟随者用自己的
    /// <see cref="AxiosConfig.ValidateStatus"/> 判定；流式响应或正文超过 <see cref="MaxSharedFailureBodyBytes"/> 时退回各自补发。
    /// </remarks>
    public bool SingleFlight { get; set; } = true;

    /// <summary>非 2xx 响应在单飞内共享给跟随者的正文上限，默认 256 KB；<c>0</c> = 关闭共享。负值构造时抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
    /// <remarks>流式响应（<see cref="ResponseType.Stream"/>）无论大小都不共享。</remarks>
    public int MaxSharedFailureBodyBytes { get; set; } = 256 * 1024;

    /// <summary>条件请求（<c>ETag</c> / <c>Last-Modified</c> 复用），默认开启：新鲜条目没命中时用旧条目发条件请求，<c>304</c> 直接交付库里的正文并刷新头与存活时长。</summary>
    /// <remarks>只有实现 <see cref="IAxiosCacheRevalidationStore"/> 的存储才有旧条目；调用方自己写了条件头时以调用方为准。</remarks>
    public bool ConditionalRequests { get; set; } = true;

    /// <summary>条目存储，<c>null</c> = 内置 <see cref="MemoryCacheStore"/>；接 Redis / 共享缓存时换成自己的实现。</summary>
    /// <remarks>只在构造 <see cref="AxiosCache"/> 时读取一次（换存储请新建实例）。netstandard2.0 上是普通 <c>set</c>（跨程序集 <c>init</c> 无法写入）。</remarks>
#if NETSTANDARD2_0
    public IAxiosCacheStore? Store { get; set; }
#else
    public IAxiosCacheStore? Store { get; init; }
#endif

    /// <summary>内置内存存储的条目上限，默认 1024（须大于零）；自建 <see cref="Store"/> 时不生效。</summary>
    public int MaxEntries { get; set; } = 1024;

    /// <summary>内置内存存储的近似字节上限，默认 64 MB；<c>0</c> = 不限制。自建 <see cref="Store"/> 时不生效。</summary>
    /// <remarks>按「正文字节 + 头字符数 ×2」估算；超限与条目满同一口径：先清过期项，仍放不下就不收新条目。</remarks>
    public long MaxBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>单次写入缓存的超时，默认 2 秒；<c>null</c> 或 <see cref="TimeSpan.Zero"/> = 不限时（负值构造时抛 <see cref="ArgumentOutOfRangeException"/>）。</summary>
    /// <remarks>只约束自建存储；实际等待还受本次剩余总预算封顶，预算花完就不落库。内置 <see cref="MemoryCacheStore"/> 同步写入不受影响。</remarks>
    public TimeSpan? StoreTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>键选择器（参数是本次生效配置与最终 URL），默认 <see cref="AxiosCache.CreateKey"/>。</summary>
    /// <remarks>需要按租户 / 用户 / 语言区分缓存时在这里拼进键；返回空白视为配置错误。</remarks>
    public Func<AxiosConfig, Uri, string>? KeySelector { get; set; }

    /// <summary>
    /// 哪些请求参与缓存，默认 <see cref="AxiosCache.IsCacheable"/>：GET / HEAD、无短路响应、非流式、无请求体、非范围请求。
    /// </summary>
    /// <remarks>可以在默认口径上组合：<c>c =&gt; AxiosCache.IsCacheable(c) || c.Method == HttpMethod.Post</c>。</remarks>
    public Func<AxiosConfig, bool> ShouldCache { get; set; } = AxiosCache.IsCacheable;
}
