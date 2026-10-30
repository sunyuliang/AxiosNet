using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace AxiosNet;

/// <summary><see cref="AxiosCache"/> 的策略选项。</summary>
public sealed class AxiosCacheOptions
{
    /// <summary>条目存活时长，默认 30 秒；必须大于零（零会让条目刚存进去就过期）。</summary>
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>同键并发只发一次（单飞），默认 <c>true</c>；关掉后每个请求各自发送，只共享已落库的条目。</summary>
    public bool SingleFlight { get; set; } = true;

    /// <summary>条目数上限，默认 1024；满了先清过期条目，仍满则不再接收新条目。</summary>
    public int MaxEntries { get; set; } = 1024;

    /// <summary>键选择器（参数是本次生效配置与最终 URL），默认 <see cref="AxiosCache.CreateKey"/>。</summary>
    /// <remarks>需要按租户 / 用户 / 语言区分缓存时在这里拼进键；返回空白视为配置错误。</remarks>
    public Func<AxiosConfig, Uri, string>? KeySelector { get; set; }

    /// <summary>哪些请求参与缓存，默认 GET / HEAD、无短路响应、非流式、无请求体。</summary>
    public Func<AxiosConfig, bool>? ShouldCache { get; set; }
}

/// <summary>
/// 响应缓存：挂在 <see cref="AxiosConfig.Cache"/> 上，命中时不发请求，直接把上次的响应交付（走与短路响应同一条路径）。
/// 同一个键的并发请求只发一次，其余等这一次的结果。
/// </summary>
/// <remarks>
/// 一个实例就是一份策略：存活时长、键规则、条目上限都挂在实例上，要按请求区分就建多个实例。
/// 缓存的是<b>原始正文字节</b>，命中时按本次调用的 responseType 与泛型参数重新解析，所以 <c>GetAsync&lt;User&gt;</c> 与
/// <c>GetAsync&lt;string&gt;</c> 命中同一条目各自得到该有的形态。
/// 只存 2xx 且拿到原始字节的响应；流式响应、响应拦截器换了响应对象、写缓存失败都不入缓存，也不影响这次请求。
/// 命中不发请求，因此不占并发闸门、不产生遥测 span 与在途计数，<see cref="AxiosResponse.Request"/> 为 <c>null</c>。
/// </remarks>
/// <example>
/// <code>
/// client.Defaults.Cache = new AxiosCache(new AxiosCacheOptions { TimeToLive = TimeSpan.FromSeconds(30) });
/// </code>
/// </example>
public sealed class AxiosCache
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AxiosCacheFlight> _flights = new(StringComparer.Ordinal);
    private readonly AxiosCacheOptions _options;

    public AxiosCache(AxiosCacheOptions? options = null)
    {
        _options = options ?? new AxiosCacheOptions();

        if (_options.TimeToLive <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), _options.TimeToLive, "TimeToLive 必须大于零：零会让每个条目刚存进去就过期。");
        }

        if (_options.MaxEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), _options.MaxEntries, "MaxEntries 必须大于零。");
        }
    }

    /// <summary>这份缓存的策略。</summary>
    public AxiosCacheOptions Options => _options;

    /// <summary>同键并发是否只发一次。</summary>
    internal bool SingleFlight => _options.SingleFlight;

    /// <summary>当前条目数（可能含已过期但还没被清掉的条目）。</summary>
    public int Count => _entries.Count;

    /// <summary>清空全部条目（在飞的请求不受影响，它们完成后照常落库）。</summary>
    public void Clear() => _entries.Clear();

    /// <summary>按 <paramref name="key"/> 摘掉一条；键可由 <see cref="KeyFor"/> 或 <see cref="CreateKey"/> 得到。</summary>
    public bool Remove(string key) => _entries.TryRemove(key, out _);

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
    /// <remarks>凭据只以摘要进键——键会一直留在字典里，原值不该跟着存活。用 Cookie 或自定义头区分身份时请给 <see cref="AxiosCacheOptions.KeySelector"/>。</remarks>
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

    /// <summary>这个请求要不要走缓存：<see cref="AxiosCacheOptions.ShouldCache"/> 优先，否则按默认口径判定。</summary>
    internal bool CanHandle(AxiosConfig config)
    {
        if (_options.ShouldCache is not { } shouldCache)
        {
            return IsCacheable(config);
        }

        try
        {
            return shouldCache(config);
        }
        catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
        {
            throw AxiosException.InvalidOption("缓存判定回调抛异常。", config, ex);
        }
    }

    /// <summary>默认口径：GET / HEAD、没有短路响应、不是流式、没有请求体。</summary>
    private static bool IsCacheable(AxiosConfig config)
        => config.ShortCircuit is null
            && config.Data is null
            && config.ResponseType != ResponseType.Stream
            && (IsMethod(config.Method, "GET") || IsMethod(config.Method, "HEAD"));

    private static bool IsMethod(HttpMethod method, string expected)
        => string.Equals(method.Method, expected, StringComparison.OrdinalIgnoreCase);

    internal bool TryGet(string key, [NotNullWhen(true)] out AxiosShortCircuit? reply)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAt > Environment.TickCount64)
            {
                reply = entry.Reply;
                return true;
            }

            // 过期顺手摘掉，不必等淘汰。
            _entries.TryRemove(key, out _);
        }

        reply = null;
        return false;
    }

    internal void Set(string key, AxiosShortCircuit reply)
    {
        if (_entries.Count >= _options.MaxEntries)
        {
            PurgeExpired();

            if (_entries.Count >= _options.MaxEntries)
            {
                // 满了就不收：让已有条目自然过期，不做随机淘汰，命中率不会因此抖动。
                return;
            }
        }

        // 每次落库都读一次策略：构造后改 Options 也能生效。亚毫秒按 1 毫秒算（计时器分辨率）。
        var ttlMs = Math.Max(1, (long)Math.Ceiling(_options.TimeToLive.TotalMilliseconds));
        _entries[key] = new Entry(reply, Environment.TickCount64 + ttlMs);
    }

    private void PurgeExpired()
    {
        var now = Environment.TickCount64;
        foreach (var pair in _entries)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                _entries.TryRemove(pair.Key, out _);
            }
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

    private readonly struct Entry
    {
        internal Entry(AxiosShortCircuit reply, long expiresAt)
        {
            Reply = reply;
            ExpiresAt = expiresAt;
        }

        internal AxiosShortCircuit Reply { get; }

        /// <summary>到期时刻（<see cref="Environment.TickCount64"/> 口径，单调递增）。</summary>
        internal long ExpiresAt { get; }
    }
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
