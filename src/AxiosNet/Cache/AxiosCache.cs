using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AxiosNet.Internal;

namespace AxiosNet.Cache;

/// <summary><see cref="AxiosCache"/> 的策略选项。</summary>
public sealed class AxiosCacheOptions
{
    /// <summary>条目存活时长，默认 30 秒；必须大于零（零会让条目刚存进去就过期）。</summary>
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 缓存时长的取值方式，默认 <see cref="CacheFreshnessMode.Configured"/>：只信 <see cref="TimeToLive"/>，落库前不解析响应头。
    /// </summary>
    /// <remarks>
    /// <see cref="CacheFreshnessMode.Server"/> / <see cref="CacheFreshnessMode.Intersection"/> 会解析响应的
    /// <c>Cache-Control</c>（<c>max-age</c>）与 <c>Expires</c> / <c>Date</c> / <c>Age</c>，同时解析<b>请求</b>的
    /// <c>Cache-Control</c> / <c>Pragma</c>，两者取交集：请求 <c>no-store</c> 不读也不写；请求 <c>no-cache</c> /
    /// <c>max-age=0</c> 跳过新鲜查询（<c>no-cache</c> 仍可落库，开启 <see cref="ConditionalRequests"/> 时还会拿旧条目做条件请求；
    /// <c>max-age=0</c> 连落库也一并拒绝）；请求 <c>max-age=N</c> 作为落库 TTL 的上限；响应 <c>Vary: *</c> 不落库。
    /// <c>Configured</c> 是「强制按配置执行」，两侧的 HTTP 指令都不参与取舍。
    /// <c>s-maxage</c>（库按私有缓存解释）、<c>Vary</c> 分区、<c>stale-*</c> 服务不在库的取舍范围，
    /// 但这些信息都会随 <see cref="AxiosCacheEntryPolicy"/> 交给自建存储，由存储决定要不要用。
    /// </remarks>
    public CacheFreshnessMode FreshnessMode { get; set; }

    /// <summary>同键并发只发一次（单飞），默认 <c>true</c>；关掉后每个请求各自发送，只共享已落库的条目。</summary>
    /// <remarks>
    /// 请求侧要求绕开缓存（<c>no-store</c> / <c>no-cache</c> / <c>max-age=0</c>）时不查缓存、也不参与单飞，各自发送；
    /// 领导者正常结束但没拿到可共享的正文（非 2xx、不落库、存储没接住）时，跟随者也各自补发；
    /// 领导者抛错时跟随者拿到同一个错误（领导者自己被取消除外，跟随者会自己补发）。
    /// </remarks>
    public bool SingleFlight { get; set; } = true;

    /// <summary>
    /// 条件请求（<c>ETag</c> / <c>Last-Modified</c> 复用），默认开启：新鲜条目没命中时，若存储里还留着带校验器的旧条目，
    /// 就发 <c>If-None-Match</c> / <c>If-Modified-Since</c>，服务端回 <c>304 Not Modified</c> 时直接交付库里的正文，
    /// 只刷新响应头与存活时长。
    /// </summary>
    /// <remarks>
    /// 只有实现 <see cref="IAxiosCacheRevalidationStore"/> 的存储（内置 <see cref="MemoryCacheStore"/>）才有旧条目可取；
    /// 请求侧 <c>no-cache</c> / <c>max-age=0</c> 跳过新鲜查询时同样会尝试条件请求（<c>no-store</c> 不会）；
    /// 调用方自己写了条件头时以调用方为准，库不覆盖、也不接管 304。
    /// </remarks>
    public bool ConditionalRequests { get; set; } = true;

    /// <summary>条目存储，<c>null</c> = 内置 <see cref="MemoryCacheStore"/>；接 Redis / 共享缓存时换成自己的实现。</summary>
    /// <remarks>只在构造 <see cref="AxiosCache"/> 时读取一次（换存储请新建一个缓存实例），故为 <c>init</c>。</remarks>
    public IAxiosCacheStore? Store { get; init; }

    /// <summary>内置内存存储的条目上限，默认 1024（必须大于零）；自建 <see cref="Store"/> 时本项不生效（上限归存储自己管）。</summary>
    public int MaxEntries { get; set; } = 1024;

    /// <summary>
    /// 内置内存存储的近似字节上限，默认 64 MB；<c>0</c> = 不限制。自建 <see cref="Store"/> 时本项不生效。
    /// </summary>
    /// <remarks>
    /// 按「正文字节数 + 头名 / 头值字符数 ×2」估算，不是精确的托管堆占用；只用来兜住「1024 条大响应」这类失控。
    /// 超限与条目满同一口径：先清过期项，仍放不下就不收新条目（已存在的键照常覆盖）。
    /// </remarks>
    public long MaxBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>
    /// 单次落库的超时，默认 2 秒；<c>null</c> 或非正数 = 不限时。
    /// </summary>
    /// <remarks>
    /// 只约束自建存储（Redis 之类）的写入：存储挂起时不能把响应一起拖住，超时按「这次没缓存」处理。
    /// 实际等待还受本次请求剩余 <see cref="AxiosConfig.TotalTimeout"/> 封顶（两者取较短的一个），预算花完就不落库。
    /// 内置 <see cref="MemoryCacheStore"/> 是同步写入，不受影响。
    /// </remarks>
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

/// <summary>
/// 响应缓存：挂在 <see cref="AxiosConfig.Cache"/> 上，命中时不发请求，直接把上次的响应交付（走与短路响应同一条路径）。
/// 同一个键的并发请求只发一次，其余等这一次的结果。
/// </summary>
/// <remarks>
/// 一个实例就是一份策略：存活时长、键规则、准入判定都在实例上，要按请求区分就建多个实例。
/// 它自己不存条目 —— 存取交给 <see cref="IAxiosCacheStore"/>（默认 <see cref="MemoryCacheStore"/>），
/// 想接 Redis 之类的外部存储就实现那个接口。
/// 缓存的是<b>原始正文字节</b>，命中时按本次调用的 responseType 与泛型参数重新解析，所以 <c>GetAsync&lt;User&gt;</c> 与
/// <c>GetAsync&lt;string&gt;</c> 命中同一条目各自得到该有的形态。
/// 只存 2xx（206 与带 <c>Content-Range</c> 的除外）且拿到原始字节的响应，条目头里也不含 <c>Set-Cookie</c>；
/// 流式响应、响应拦截器换了响应对象、存储读写失败都不影响这次请求。
/// 条目过期后，若响应带 <c>ETag</c> / <c>Last-Modified</c> 且存储实现了 <see cref="IAxiosCacheRevalidationStore"/>，
/// 库会发条件请求：服务端回 <c>304</c> 时直接交付库里的正文（见 <see cref="AxiosCacheOptions.ConditionalRequests"/>）。
/// 命中不发请求，因此不占并发闸门、不产生遥测 span 与在途计数，<see cref="AxiosResponse.Request"/> 为 <c>null</c>；
/// 条件请求的 304 确实发出了一次请求，<see cref="AxiosResponse.Request"/> 不为 <c>null</c>。
/// </remarks>
/// <example>
/// <code>
/// client.Defaults.Cache = new AxiosCache(new AxiosCacheOptions { TimeToLive = TimeSpan.FromSeconds(30) });
/// </code>
/// </example>
public sealed class AxiosCache
{
    // 在飞登记：键 → 领导者那一轮。跟随者只是共享领导者的 Completion，完成 / 失败后按「键 + 实例」摘除。
    private readonly ConcurrentDictionary<string, AxiosCacheFlight> _flights = new(StringComparer.Ordinal);
    private readonly AxiosCacheOptions _options;
    private readonly IAxiosCacheStore _store;
    private readonly IAxiosCacheRevalidationStore? _revalidationStore;

    /// <summary>构造一份缓存策略；<paramref name="options"/> 为 <c>null</c> 时全用默认值。</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="AxiosCacheOptions.TimeToLive"/> 不大于零、<see cref="AxiosCacheOptions.StoreTimeout"/> 为负，
    /// 或用内置存储时 <see cref="AxiosCacheOptions.MaxBytes"/> 为负 / <see cref="AxiosCacheOptions.MaxEntries"/> 不大于零。
    /// </exception>
    public AxiosCache(AxiosCacheOptions? options = null)
    {
        _options = options ?? new AxiosCacheOptions();

        if (_options.TimeToLive <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AxiosCacheOptions.TimeToLive),
                _options.TimeToLive,
                "TimeToLive 必须大于零：零会让每个条目刚存进去就过期。");
        }

        if (_options.StoreTimeout is { } storeTimeout && storeTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AxiosCacheOptions.StoreTimeout),
                storeTimeout,
                "StoreTimeout 不能为负：不限时请设为 null。");
        }

        // 容量两项只在用内置存储时才校验：自建存储的上限归它自己管。
        if (_options.Store is null && _options.MaxBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AxiosCacheOptions.MaxBytes),
                _options.MaxBytes,
                "MaxBytes 不能为负：不限制请设为 0。");
        }

        _store = _options.Store ?? new MemoryCacheStore(_options.MaxEntries, _options.MaxBytes);
        _revalidationStore = _store as IAxiosCacheRevalidationStore;
    }

    /// <summary>这份缓存的策略。</summary>
    public AxiosCacheOptions Options => _options;

    /// <summary>条目存储：<see cref="AxiosCacheOptions.Store"/> 未配置时就是内置的 <see cref="MemoryCacheStore"/>，构造后不再变化。</summary>
    public IAxiosCacheStore Store => _store;

    /// <summary>同键并发是否只发一次。</summary>
    internal bool SingleFlight => _options.SingleFlight;

    /// <summary>是否发条件请求（旧条目由存储的 <see cref="IAxiosCacheRevalidationStore"/> 提供）。</summary>
    internal bool ConditionalRequests => _options.ConditionalRequests;

    /// <summary>清空全部条目（在飞的请求不受影响，它们完成后照常落库）。</summary>
    public ValueTask ClearAsync(CancellationToken cancellationToken = default) => _store.ClearAsync(cancellationToken);

    /// <summary>按 <paramref name="key"/> 摘掉一条；键可由 <see cref="KeyFor"/> 或 <see cref="CreateKey"/> 得到。</summary>
    public ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
        => _store.RemoveAsync(key, cancellationToken);

    /// <summary>本次请求的缓存键：<see cref="AxiosCacheOptions.KeySelector"/> 优先，否则用 <see cref="CreateKey"/>；返回空白视为配置错误。</summary>
    public string KeyFor(AxiosConfig config, Uri uri)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(uri);

        string? key;
        try
        {
            key = _options.KeySelector?.Invoke(config, uri) ?? CreateKey(config, uri);
        }
        catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
        {
            throw AxiosException.InvalidOption("缓存键选择器抛异常。", config, ex);
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            throw AxiosException.InvalidOption("缓存键是空白：每个可缓存的请求必须有一个非空键。", config);
        }

        return key;
    }

    /// <summary>默认键：方法 + 绝对 URL，带凭据时再拼一段凭据摘要（不同用户的同名接口不会串用同一条缓存）。</summary>
    /// <remarks>凭据只以摘要进键——键会一直留在存储里，原值不该跟着存活。用 Cookie 或自定义头区分身份时请给 <see cref="AxiosCacheOptions.KeySelector"/>。</remarks>
    public static string CreateKey(AxiosConfig config, Uri uri)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(uri);

        var identity = ResolveIdentity(config);
        var absoluteUri = uri.AbsoluteUri;
        var builder = new StringBuilder(config.Method.Method.Length + absoluteUri.Length + 24 + (identity is null ? 0 : 17));

        builder.Append(config.Method.Method).Append(' ').Append(absoluteUri);

        if (identity is not null)
        {
            builder.Append(' ').Append(Digest(identity));
        }

        return builder.ToString();
    }

    /// <summary>这个请求要不要走缓存（<see cref="AxiosCacheOptions.ShouldCache"/>，默认 <see cref="IsCacheable"/>）。</summary>
    internal bool CanHandle(AxiosConfig config)
    {
        try
        {
            return _options.ShouldCache(config);
        }
        catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
        {
            throw AxiosException.InvalidOption("缓存判定回调抛异常。", config, ex);
        }
    }

    /// <summary>默认的缓存准入口径：GET / HEAD、没有短路响应、不是流式、没有请求体、不是范围请求。</summary>
    /// <remarks>
    /// 键只有方法 + URL，扣不住 <c>Range</c>：范围请求若参与缓存，206 的分段正文会被后续普通 GET 拿去当整份内容。
    /// 需要按 Range 分片缓存时用 <see cref="AxiosCacheOptions.ShouldCache"/> 放行，并在 <see cref="AxiosCacheOptions.KeySelector"/> 里把范围算进键。
    /// </remarks>
    public static bool IsCacheable(AxiosConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.ShortCircuit is null
            && config.Data is null
            && config.ResponseType != ResponseType.Stream
            && (IsMethod(config.Method, "GET") || IsMethod(config.Method, "HEAD"))
            && !(config.HasHeaders && config.Headers.Has("Range"));
    }

    /// <summary>
    /// 读取本次<b>请求</b>的缓存指令。只有 <see cref="CacheFreshnessMode.Server"/> / <see cref="CacheFreshnessMode.Intersection"/>
    /// 才解析：<see cref="CacheFreshnessMode.Configured"/> 是「强制按配置执行」，请求头不参与取舍。
    /// </summary>
    internal CacheRequestDirectives ReadRequestDirectives(AxiosConfig config)
    {
        if (_options.FreshnessMode == CacheFreshnessMode.Configured || !config.HasHeaders)
        {
            return CacheRequestDirectives.None;
        }

        var cacheControl = CacheControlDirectives.ParseValue(config.Headers.Get("Cache-Control"));

        // 旧式 Pragma 只在没有 Cache-Control 时生效（RFC 9111 §5.4）。
        var pragmaNoCache = cacheControl.Raw is null
            && string.Equals(config.Headers.Get("Pragma")?.Trim(), "no-cache", StringComparison.OrdinalIgnoreCase);

        return new CacheRequestDirectives(
            cacheControl.NoStore,
            cacheControl.NoCache || pragmaNoCache,
            cacheControl.MaxAge);
    }

    private static bool IsMethod(HttpMethod method, string expected)
        => string.Equals(method.Method, expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 按响应头与请求指令算一条写入策略；返回 <c>null</c> 表示 HTTP 语义要求「不落库」（只有
    /// <see cref="CacheFreshnessMode.Server"/> / <see cref="CacheFreshnessMode.Intersection"/> 时才会发生）。
    /// <see cref="CacheFreshnessMode.Configured"/> 一个头都不解析：TTL 就是配置值，HTTP 信息留给
    /// <see cref="AxiosCacheEntryPolicy"/> 在被读取时按需解析。
    /// </summary>
    internal AxiosCacheEntryPolicy? CreateEntryPolicy(
        IReadOnlyDictionary<string, IEnumerable<string>> headers,
        DateTimeOffset receivedAt,
        CacheRequestDirectives request = default)
    {
        var mode = _options.FreshnessMode;

        if (mode == CacheFreshnessMode.Configured)
        {
            return new AxiosCacheEntryPolicy(
                _options.TimeToLive, _options.TimeToLive, mode, headers, receivedAt, http: null);
        }

        // 请求侧否决：no-store / max-age=0 不落库；no-cache 只跳过查询，仍可落库。
        if (!request.CanStore)
        {
            return null;
        }

        var http = ReadHttpCacheInfo(headers, receivedAt);
        var remaining = http.Remaining;

        // Vary: * 表示任何维度都可能影响内容，键覆盖不住，落库只会把别人的内容交付出去。
        if (ContainsVaryStar(http.Vary))
        {
            return null;
        }

        // 服务器否决优先：no-store / no-cache / 新鲜期已用尽，两种解析方式都不落库。
        if (http.CacheControl.NoStore || http.CacheControl.NoCache || remaining == TimeSpan.Zero)
        {
            return null;
        }

        TimeSpan timeToLive;
        if (mode == CacheFreshnessMode.Server)
        {
            // 只信服务器：没有声明新鲜期就不缓存；有则完全按它，配置 TTL 不设上限。
            if (remaining is not { } serverTtl)
            {
                return null;
            }

            timeToLive = serverTtl;
        }
        else
        {
            // 取交集：配置 TTL 是上限，HTTP 剩余新鲜期更短时以它为准；没有新鲜期则只有配置 TTL。
            timeToLive = remaining is { } available && available < _options.TimeToLive
                ? available
                : _options.TimeToLive;
        }

        // 请求的 max-age 是客户端声明「只接受这么新的内容」：落库 TTL 同样不能超过它。
        if (request.MaxAge is { } requested && requested < timeToLive)
        {
            timeToLive = requested;
        }

        return new AxiosCacheEntryPolicy(
            timeToLive, _options.TimeToLive, mode, headers: null, receivedAt, http);
    }

    private static bool ContainsVaryStar(IReadOnlyList<string> vary)
    {
        foreach (var field in vary)
        {
            if (field == "*")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>解析响应头里与缓存相关的全部信息；只有解析模式或按需读取策略的存储才会走到这里。</summary>
    internal static HttpCacheInfo ReadHttpCacheInfo(
        IReadOnlyDictionary<string, IEnumerable<string>> headers,
        DateTimeOffset receivedAt)
    {
        var cacheControl = CacheControlDirectives.Parse(GetValues(headers, "Cache-Control"));
        var date = ParseHttpDate(FirstValue(headers, "Date"));
        var expiresText = FirstValue(headers, "Expires");
        var expires = ParseHttpDate(expiresText);

        // RFC 9111 的 current_age：Age 头与「收到响应 - Date」取较大值，负数按零。
        var age = ParseSeconds(FirstValue(headers, "Age")) ?? TimeSpan.Zero;
        if (date is { } parsedDate && receivedAt > parsedDate)
        {
            var apparentAge = receivedAt - parsedDate;
            if (apparentAge > age)
            {
                age = apparentAge;
            }
        }

        TimeSpan? freshness = null;
        if (cacheControl.MaxAge is { } maxAge)
        {
            freshness = maxAge;
        }
        else if (expiresText is not null)
        {
            // 非法 Expires 按「已过期」处理（RFC 9111 §5.3）；合法值的新鲜期是 Expires - Date，缺 Date 时用收到响应的时刻。
            var reference = date ?? receivedAt;
            freshness = expires is { } parsedExpires && parsedExpires > reference
                ? parsedExpires - reference
                : TimeSpan.Zero;
        }

        return new HttpCacheInfo(freshness, age, cacheControl, date, expires, ParseVary(headers));
    }

    private static IEnumerable<string>? GetValues(
        IReadOnlyDictionary<string, IEnumerable<string>> headers,
        string name)
        => headers.TryGetValue(name, out var values) ? values : null;

    private static string? FirstValue(
        IReadOnlyDictionary<string, IEnumerable<string>> headers,
        string name)
        => GetValues(headers, name)?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static DateTimeOffset? ParseHttpDate(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AllowWhiteSpaces,
                out var parsed)
            ? parsed
            : null;

    private static TimeSpan? ParseSeconds(string? value)
    {
        if (value is null
            || !long.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds < 0)
        {
            return null;
        }

        // 秒数换算成 tick 可能超出 TimeSpan 上限：超出就按「无限长」处理。
        return seconds > long.MaxValue / TimeSpan.TicksPerSecond
            ? TimeSpan.MaxValue
            : TimeSpan.FromTicks(seconds * TimeSpan.TicksPerSecond);
    }

    private static IReadOnlyList<string> ParseVary(IReadOnlyDictionary<string, IEnumerable<string>> headers)
    {
        var values = GetValues(headers, "Vary");
        if (values is null)
        {
            return Array.Empty<string>();
        }

        List<string>? fields = null;
        foreach (var value in values)
        {
            foreach (var part in value.Split(','))
            {
                var field = part.Trim();
                if (field.Length > 0)
                {
                    (fields ??= new List<string>()).Add(field);
                }
            }
        }

        return fields ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    /// <summary>
    /// 读一条；存储报错按「没命中」处理（缓存不可用不该让请求失败），但调用方取消要照常抛统一错误。
    /// 读取时长受剩余总预算约束：存储挂起时不能让请求挂在查询上。
    /// </summary>
    internal ValueTask<AxiosShortCircuit?> TryGetAsync(string key, AxiosConfig config, long deadlineTimestamp)
        => TryGetCoreAsync(key, config, deadlineTimestamp, revalidation: false);

    /// <summary>
    /// 取一条可供条件请求复用的旧条目；存储不支持（没实现 <see cref="IAxiosCacheRevalidationStore"/>）时直接返回 <c>null</c>。
    /// 预算与取消口径与 <see cref="TryGetAsync"/> 一致。
    /// </summary>
    internal ValueTask<AxiosShortCircuit?> TryGetRevalidationAsync(string key, AxiosConfig config, long deadlineTimestamp)
        => _revalidationStore is not null
            ? TryGetCoreAsync(key, config, deadlineTimestamp, revalidation: true)
            : new ValueTask<AxiosShortCircuit?>((AxiosShortCircuit?)null);

    private async ValueTask<AxiosShortCircuit?> TryGetCoreAsync(
        string key,
        AxiosConfig config,
        long deadlineTimestamp,
        bool revalidation)
    {
        var cancellationToken = config.CancellationToken;
        var remaining = TotalTimeoutBudget.Remaining(deadlineTimestamp);

        // 总预算已经花完：没余量再等存储，按统一错误模型报出来（与「没能发出尝试」同一口径）。
        if (TotalTimeoutBudget.IsExhausted(remaining))
        {
            throw AxiosException.TotalTimeoutExceeded(
                config, request: null, attempt: 1, budget: config.TotalTimeout);
        }

        Task<AxiosShortCircuit?>? read = null;

        try
        {
            // 只有 revalidation 为 true 时才会走到 _revalidationStore（见 TryGetRevalidationAsync 的判定）。
            var pending = revalidation
                ? _revalidationStore!.GetRevalidationAsync(key, cancellationToken)
                : _store.GetAsync(key, cancellationToken);

            // 同步完成的存储（内置内存存储）不付出 Task 转换与计时器的成本。
            if (pending.IsCompleted)
            {
                return await pending.ConfigureAwait(false);
            }

            read = pending.AsTask();

            return remaining is { } left
                ? await read.WaitAsync(Timeouts.Clamp(left)).ConfigureAwait(false)
                : await read.ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            // 存储挂起超出剩余总预算：放弃这次读取，但要观察它的失败，免得 GC 报「未观察的任务异常」。
            Observe(read);
            throw AxiosException.TotalTimeoutExceeded(
                config, request: null, attempt: 1, budget: config.TotalTimeout, ex);
        }
        catch (OperationCanceledException ex)
        {
            // 调用方取消 → 统一错误模型（对外只暴露 ERR_CANCELED）。
            if (cancellationToken.IsCancellationRequested)
            {
                throw AxiosException.Canceled(config, null, ex);
            }

            // 存储自己取消（调用方没取消）：缓存不可用，按没命中处理。
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>观察被放弃的任务的异常（仅故障时续接），免得它拖到 GC 报「未观察的任务异常」。</summary>
    private static void Observe(Task? task)
        => task?.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    /// <summary>
    /// 写一条；失败只是这次不入缓存。写不跟随调用方的取消（结果已经拿到了，不该因为超时被丢掉），
    /// 但受 <see cref="AxiosCacheOptions.StoreTimeout"/> 与剩余总预算约束：存储挂起超过两者中较短的一个时按「这次没缓存」处理。
    /// </summary>
    /// <param name="key">缓存键。</param>
    /// <param name="reply">要写入的条目。</param>
    /// <param name="policy">已判定的新鲜期策略。</param>
    /// <param name="budgetRemaining">本次请求剩余的总预算（<see cref="AxiosConfig.TotalTimeout"/>）；<c>null</c> 表示没有总预算。</param>
    internal async ValueTask<bool> TrySetAsync(
        string key,
        AxiosShortCircuit reply,
        AxiosCacheEntryPolicy policy,
        TimeSpan? budgetRemaining = null)
    {
        try
        {
            var pending = _store.SetAsync(key, reply, policy, CancellationToken.None);

            // 同步完成的存储（内置内存存储）不付出 Task 转换与计时器的成本。
            if (pending.IsCompleted)
            {
                await pending.ConfigureAwait(false);
                return true;
            }

            var task = pending.AsTask();

            var timeout = _options.StoreTimeout is { } configured && configured > TimeSpan.Zero
                ? configured
                : (TimeSpan?)null;

            if (budgetRemaining is { } left)
            {
                // 预算已经花完：不再等存储，按「这次没缓存」处理。
                if (TotalTimeoutBudget.IsExhausted(left))
                {
                    Observe(task);
                    return false;
                }

                // 两者取短：写入既不能超过存储自己的预算，也不能超过本次请求的总预算。
                timeout = timeout is { } current && current < left ? current : left;
            }

            if (timeout is not { } wait)
            {
                await task.ConfigureAwait(false);
                return true;
            }

            try
            {
                await task.WaitAsync(Timeouts.Clamp(wait)).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                // 超时按「这次没缓存」处理；底层写入继续跑，但要观察它的失败，免得 GC 报「未观察的任务异常」。
                Observe(task);
                return false;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>登记一次发送：返回的实例要么是领导者（负责发送并落库），要么是等待同一结果的跟随者。</summary>
    internal AxiosCacheFlight Join(string key)
    {
        var leader = new AxiosCacheFlight();
        var existing = _flights.GetOrAdd(key, leader);

        return ReferenceEquals(existing, leader) ? leader : new AxiosCacheFlight(existing.Completion);
    }

    /// <summary>领导者成功：落库已完成（或这次不可缓存），放行跟随者。</summary>
    internal void CompleteFlight(string key, AxiosCacheFlight flight, AxiosShortCircuit? reply)
    {
        RemoveFlight(key, flight);
        flight.Succeed(reply);
    }

    /// <summary>领导者失败：跟随者原样拿到这个错误，不要挂在原地等。</summary>
    internal void FailFlight(string key, AxiosCacheFlight flight, Exception error)
    {
        RemoveFlight(key, flight);
        flight.Fail(error);
    }

    private void RemoveFlight(string key, AxiosCacheFlight flight)
    {
        // 先摘再放行：之后进来的请求要么新建一次发送，要么直接命中已落库的条目。
        if (_flights.TryGetValue(key, out var current) && ReferenceEquals(current, flight))
        {
            _flights.TryRemove(key, out _);
        }
    }

    /// <summary>身份来源：显式 <c>Authorization</c> 头优先，其次连接级 Basic 凭据；两者都只以摘要进键。</summary>
    private static string? ResolveIdentity(AxiosConfig config)
    {
        if (config.HasHeaders && config.Headers.Get("Authorization") is { } authorization)
        {
            return authorization;
        }

        return config.ConnectionOrNull?.Auth?.ToBasicHeaderValue();
    }

    /// <summary>凭据摘要：SHA-256 的前 8 字节转十六进制（16 个字符），只够区分身份、不足以还原原值。</summary>
    private static string Digest(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 8));
}

/// <summary>一次在飞发送的登记：领导者发送完成后把结果交给跟随者。</summary>
internal sealed class AxiosCacheFlight
{
    private readonly TaskCompletionSource<AxiosShortCircuit?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task<AxiosShortCircuit?>? _shared;

    /// <summary>领导者：结果由 <see cref="Succeed"/> / <see cref="Fail"/> 填写。</summary>
    internal AxiosCacheFlight()
    {
    }

    /// <summary>跟随者：等领导者那一份结果。</summary>
    internal AxiosCacheFlight(Task<AxiosShortCircuit?> shared) => _shared = shared;

    internal bool IsLeader => _shared is null;

    internal Task<AxiosShortCircuit?> Completion => _shared ?? _completion.Task;

    /// <summary>放行跟随者；<paramref name="reply"/> 为 <c>null</c> 表示这次没有可共享的字节，跟随者自己发。</summary>
    internal void Succeed(AxiosShortCircuit? reply) => _completion.TrySetResult(reply);

    internal void Fail(Exception error)
    {
        if (!_completion.TrySetException(error))
        {
            return;
        }

        // 没有跟随者时这个异常没人接：先观察一次，免得拖到 GC 报「未观察的任务异常」。
        _ = _completion.Task.Exception;
    }
}
