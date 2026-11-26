namespace AxiosNet.Cache;

/// <summary>响应缓存：挂在 <see cref="AxiosConfig.Caching"/> 上，命中时不发请求，直接交付上次的响应（与短路响应同一条路径）。</summary>
/// <remarks>
/// 一个实例就是一份策略；条目由 <see cref="IAxiosCacheStore"/> 存取，默认 <see cref="MemoryCacheStore"/>。缓存原始正文字节，
/// 命中时按本次 responseType 与泛型参数重新解析；只存 2xx（206 / <c>Content-Range</c> 除外）。命中不占并发闸门、不产生 span，
/// <see cref="AxiosResponse.Request"/> 为 <c>null</c>。
/// </remarks>
/// <example>
/// <code>
/// client.Defaults.Caching = new AxiosCache(new AxiosCacheOptions { TimeToLive = TimeSpan.FromSeconds(30) });
/// </code>
/// </example>
public sealed class AxiosCache
{
    private readonly AxiosCacheOptions _options;
    private readonly IAxiosCacheStore _store;
    private readonly IAxiosCacheRevalidationStore? _revalidationStore;

    /// <summary>构造一份缓存策略；<paramref name="options"/> 为 <c>null</c> 时全用默认值。</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="AxiosCacheOptions.TimeToLive"/> 不大于零、<see cref="AxiosCacheOptions.StoreTimeout"/> 为负、<see cref="AxiosCacheOptions.MaxSharedFailureBodyBytes"/> 为负，
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

        if (_options.MaxSharedFailureBodyBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AxiosCacheOptions.MaxSharedFailureBodyBytes),
                _options.MaxSharedFailureBodyBytes,
                "MaxSharedFailureBodyBytes 不能为负：关闭共享请设为 0。");
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
    internal bool SingleFlightEnabled => _options.SingleFlight;

    /// <summary>在途发送登记簿：领导者负责发送与落库，跟随者等它那一份结果。</summary>
    internal SingleFlightCoordinator SingleFlight { get; } = new();

    /// <summary>是否发条件请求（旧条目由存储的 <see cref="IAxiosCacheRevalidationStore"/> 提供）。</summary>
    internal bool ConditionalRequests => _options.ConditionalRequests;

    /// <summary>清空全部条目（在途请求不受影响，它们完成后照常落库）。</summary>
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
    /// <remarks>凭据只以摘要进键——键会一直留在存储里，原值不该跟着存活。用 Cookie 或自定义头区分身份时请给 <see cref="AxiosCacheOptions.KeySelector"/>。查询串里的令牌会原样以明文进键（敏感令牌请勿放查询串，或用 KeySelector 只取摘要）；<c>OnSendingRequest</c> 在发送前才执行，晚于建键，其中写的头进不了键。</remarks>
    public static string CreateKey(AxiosConfig config, Uri uri) => CacheKeyFactory.Create(config, uri);

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
    /// <remarks>键只有方法 + URL，扣不住 <c>Range</c>；要按 Range 分片缓存请用 <see cref="AxiosCacheOptions.ShouldCache"/> 放行并在 <see cref="AxiosCacheOptions.KeySelector"/> 里把范围算进键。</remarks>
    public static bool IsCacheable(AxiosConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.ShortCircuit is null
            && config.Body is null
            && config.ResponseType != ResponseType.Stream
            && (IsMethod(config.Method, "GET") || IsMethod(config.Method, "HEAD"))
            && !(config.HasHeaders && config.Headers.Has("Range"));
    }

    /// <summary>读取本次<b>请求</b>的缓存指令；只有 <see cref="CacheFreshnessMode.Server"/> / <see cref="CacheFreshnessMode.Intersection"/> 才解析。</summary>
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

    // 按方法名比较，自定义 HttpMethod 也能匹配。
    private static bool IsMethod(HttpMethod method, string expected)
        => string.Equals(method.Method, expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>按响应头与请求指令算一条写入策略；返回 <c>null</c> 表示 HTTP 语义要求「不落库」（仅 Server / Intersection 模式）。</summary>
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

        var http = HttpCacheSemantics.ReadHttpCacheInfo(headers, receivedAt);
        var remaining = http.Remaining;

        // Vary: * 表示键覆盖不住，不落库。
        if (HttpCacheSemantics.ContainsVaryStar(http.Vary))
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
            if (remaining is not { } serverTtl)
            {
                return null;
            }

            timeToLive = serverTtl;
        }
        else
        {
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

    /// <summary>读一条；存储报错按「没命中」处理，调用方取消照常抛统一错误；读取时长受剩余总预算约束。</summary>
    internal ValueTask<AxiosShortCircuit?> TryGetAsync(string key, AxiosConfig config, long deadlineTimestamp)
        => TryGetCoreAsync(key, config, deadlineTimestamp, revalidation: false);

    /// <summary>取一条可供条件请求复用的旧条目；存储不支持 <see cref="IAxiosCacheRevalidationStore"/> 时返回 <c>null</c>。</summary>
    internal ValueTask<AxiosShortCircuit?> TryGetRevalidationAsync(string key, AxiosConfig config, long deadlineTimestamp)
        => _revalidationStore is not null
            ? TryGetCoreAsync(key, config, deadlineTimestamp, revalidation: true)
            : new ValueTask<AxiosShortCircuit?>((AxiosShortCircuit?)null);

    // 命中判定核心：取条目 → 交给上层做新鲜度 / 条件请求判定。
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
                config, request: null, attempt: 1, budget: config.TotalTimeoutValue);
        }

        Task<AxiosShortCircuit?>? read = null;

        try
        {
            // 只有 revalidation 为 true 时才会走到 _revalidationStore（见 TryGetRevalidationAsync 的判定）。
            var pending = revalidation
                ? _revalidationStore!.GetRevalidationAsync(key, cancellationToken)
                : _store.GetAsync(key, cancellationToken);

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
            // 存储挂起超预算：放弃读取，但要观察它的失败。
            Observe(read);
            throw AxiosException.TotalTimeoutExceeded(
                config, request: null, attempt: 1, budget: config.TotalTimeoutValue, ex);
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

    /// <summary>观察被放弃任务的异常（仅故障时续接）。</summary>
    private static void Observe(Task? task)
        => task?.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    /// <summary>写一条；失败只是这次不入缓存。写不跟随调用方取消，但受 <see cref="AxiosCacheOptions.StoreTimeout"/> 与剩余总预算约束（取较短）。</summary>
    /// <param name="key">缓存键。</param>
    /// <param name="reply">要写入的条目。</param>
    /// <param name="policy">已判定的新鲜期策略。</param>
    /// <param name="budgetRemaining">本次请求剩余的总预算（<see cref="AxiosConfig.ResilienceSettings.TotalTimeout"/>）；<c>null</c> 表示没有总预算。</param>
    internal async ValueTask<bool> TrySetAsync(
        string key,
        AxiosShortCircuit reply,
        AxiosCacheEntryPolicy policy,
        TimeSpan? budgetRemaining = null)
    {
        try
        {
            var pending = _store.SetAsync(key, reply, policy, CancellationToken.None);

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
                // 超时按「这次没缓存」处理；底层写入继续跑，但要观察它的失败。
                Observe(task);
                return false;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }
}
