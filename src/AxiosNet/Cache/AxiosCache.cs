using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

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
    /// <c>Cache-Control</c>（<c>max-age</c>）与 <c>Expires</c> / <c>Date</c> / <c>Age</c>，见枚举各项说明。
    /// <c>s-maxage</c>（库按私有缓存解释）、<c>Vary</c>、条件请求（ETag 复用）不在库的取舍范围，
    /// 但这些信息都会随 <see cref="AxiosCacheEntryPolicy"/> 交给自建存储，由存储决定要不要用。
    /// </remarks>
    public CacheFreshnessMode FreshnessMode { get; set; }

    /// <summary>同键并发只发一次（单飞），默认 <c>true</c>；关掉后每个请求各自发送，只共享已落库的条目。</summary>
    public bool SingleFlight { get; set; } = true;

    /// <summary>条目存储，<c>null</c> = 内置 <see cref="MemoryCacheStore"/>；接 Redis / 共享缓存时换成自己的实现。</summary>
    /// <remarks>只在构造 <see cref="AxiosCache"/> 时读取一次（换存储请新建一个缓存实例），故为 <c>init</c>。</remarks>
    public IAxiosCacheStore? Store { get; init; }

    /// <summary>内置内存存储的条目上限，默认 1024；自建 <see cref="Store"/> 时本项不生效（上限归存储自己管）。</summary>
    public int MaxEntries { get; set; } = 1024;

    /// <summary>键选择器（参数是本次生效配置与最终 URL），默认 <see cref="AxiosCache.CreateKey"/>。</summary>
    /// <remarks>需要按租户 / 用户 / 语言区分缓存时在这里拼进键；返回空白视为配置错误。</remarks>
    public Func<AxiosConfig, Uri, string>? KeySelector { get; set; }

    /// <summary>
    /// 哪些请求参与缓存，默认 <see cref="AxiosCache.IsCacheable"/>：GET / HEAD、无短路响应、非流式、无请求体。
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
/// 只存 2xx 且拿到原始字节的响应；流式响应、响应拦截器换了响应对象、存储读写失败都不影响这次请求。
/// 命中不发请求，因此不占并发闸门、不产生遥测 span 与在途计数，<see cref="AxiosResponse.Request"/> 为 <c>null</c>。
/// </remarks>
/// <example>
/// <code>
/// client.Defaults.Cache = new AxiosCache(new AxiosCacheOptions { TimeToLive = TimeSpan.FromSeconds(30) });
/// </code>
/// </example>
public sealed class AxiosCache
{
    private readonly ConcurrentDictionary<string, AxiosCacheFlight> _flights = new(StringComparer.Ordinal);
    private readonly AxiosCacheOptions _options;
    private readonly IAxiosCacheStore _store;

    /// <summary>构造一份缓存策略；<paramref name="options"/> 为 <c>null</c> 时全用默认值。</summary>
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

        // MaxEntries 只在用内置存储时才校验：自建存储的条目上限归它自己管。
        _store = _options.Store ?? new MemoryCacheStore(_options.MaxEntries);
    }

    /// <summary>这份缓存的策略。</summary>
    public AxiosCacheOptions Options => _options;

    /// <summary>条目存储。</summary>
    public IAxiosCacheStore Store => _store;

    /// <summary>同键并发是否只发一次。</summary>
    internal bool SingleFlight => _options.SingleFlight;

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

    /// <summary>默认的缓存准入口径：GET / HEAD、没有短路响应、不是流式、没有请求体。</summary>
    public static bool IsCacheable(AxiosConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.ShortCircuit is null
            && config.Data is null
            && config.ResponseType != ResponseType.Stream
            && (IsMethod(config.Method, "GET") || IsMethod(config.Method, "HEAD"));
    }

    private static bool IsMethod(HttpMethod method, string expected)
        => string.Equals(method.Method, expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 按响应头算一条写入策略；返回 <c>null</c> 表示 HTTP 语义要求「不落库」（只有
    /// <see cref="CacheFreshnessMode.Server"/> / <see cref="CacheFreshnessMode.Intersection"/> 时才会发生）。
    /// <see cref="CacheFreshnessMode.Configured"/> 一个头都不解析：TTL 就是配置值，HTTP 信息留给
    /// <see cref="AxiosCacheEntryPolicy"/> 在被读取时按需解析。
    /// </summary>
    internal AxiosCacheEntryPolicy? CreateEntryPolicy(
        IReadOnlyDictionary<string, IEnumerable<string>> headers,
        DateTimeOffset receivedAt)
    {
        var mode = _options.FreshnessMode;

        if (mode == CacheFreshnessMode.Configured)
        {
            return new AxiosCacheEntryPolicy(
                _options.TimeToLive, _options.TimeToLive, mode, headers, receivedAt, http: null);
        }

        var http = ReadHttpCacheInfo(headers, receivedAt);
        var remaining = http.Remaining;

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

        return new AxiosCacheEntryPolicy(
            timeToLive, _options.TimeToLive, mode, headers: null, receivedAt, http);
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

    /// <summary>读一条；存储报错按「没命中」处理（缓存不可用不该让请求失败），但调用方取消要照常抛。</summary>
    internal async ValueTask<AxiosShortCircuit?> TryGetAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            return await _store.GetAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>写一条；失败只是这次不入缓存。写不跟随调用方的取消（结果已经拿到了，不该因为超时被丢掉）。</summary>
    internal async ValueTask<bool> TrySetAsync(string key, AxiosShortCircuit reply, AxiosCacheEntryPolicy policy)
    {
        try
        {
            await _store.SetAsync(key, reply, policy, CancellationToken.None).ConfigureAwait(false);
            return true;
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

    private static string? ResolveIdentity(AxiosConfig config)
    {
        if (config.HasHeaders && config.Headers.Get("Authorization") is { } authorization)
        {
            return authorization;
        }

        return config.ConnectionOrNull?.Auth?.ToBasicHeaderValue();
    }

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
