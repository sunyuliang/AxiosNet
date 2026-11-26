using System.Runtime.CompilerServices;
using AxiosNet.Cache;
using AxiosNet.Pipeline;
using AxiosNet.Telemetry;

namespace AxiosNet;

// AxiosClient 的缓存阶段：命中直接交付、未命中参与单飞、成功后落库。
public sealed partial class AxiosClient
{
    /// <summary>
    /// 跟随者等领导者那一份结果：只让自己的取消生效，等待时长受剩余总预算约束；返回 <c>null</c> 表示没有可共享的字节，调用方自己发。
    /// </summary>
    private static async Task<AxiosShortCircuit?> AwaitFlightAsync(
        AxiosCacheFlight flight,
        AxiosConfig config,
        long deadlineTimestamp)
    {
        var remaining = TotalTimeoutBudget.Remaining(deadlineTimestamp);

        // 总预算已花完：按统一错误模型报出。
        if (TotalTimeoutBudget.IsExhausted(remaining))
        {
            throw AxiosException.TotalTimeoutExceeded(
                config, request: null, attempt: 1, budget: config.TotalTimeoutValue);
        }

        // 没有总预算：只让自己的令牌生效。
        if (remaining is null)
        {
            return await WaitOnFlightAsync(flight, config, config.CancellationToken, bounded: false).ConfigureAwait(false);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(config.CancellationToken);

        // CancelAfter 上限约 49.7 天，收敛到上限。
        linked.CancelAfter(Timeouts.Clamp(remaining.Value));

        return await WaitOnFlightAsync(flight, config, linked.Token, bounded: true).ConfigureAwait(false);
    }

    /// <summary>跟随同键的在途请求：等到它的结果；令牌被自己取消就抛，领导者失败或预算到点则返回 null 让调用方自行补发。</summary>
    private static async Task<AxiosShortCircuit?> WaitOnFlightAsync(
        AxiosCacheFlight flight,
        AxiosConfig config,
        CancellationToken token,
        bool bounded)
    {
        try
        {
            return await flight.Completion.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            // 自己的令牌取消 → 统一错误模型。
            if (config.CancellationToken.IsCancellationRequested)
            {
                throw AxiosException.Canceled(config, null, ex);
            }

            // 领导者失败（异常已随单飞广播）：跟随者自己发。
            if (flight.Completion.IsCompleted)
            {
                return null;
            }

            // 等待有上限且令牌未取消：总预算到点。
            if (bounded)
            {
                throw AxiosException.TotalTimeoutExceeded(
                    config, request: null, attempt: 1, budget: config.TotalTimeoutValue, ex);
            }

            // 领导者被自己的调用方取消。
            return null;
        }
        catch (AxiosException error) when (error.Code == AxiosException.ErrCanceled && !config.CancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>缓存阶段的冷路径：查命中、参与单飞、读请求指令、准备条件请求；命中时直接给出已成型的响应。</summary>
    /// <param name="cache">本次请求的缓存策略（准入已由 <see cref="AxiosCache.CanHandle"/> 判定）。</param>
    /// <param name="config">本次请求的生效配置；<c>CacheCaptureRawBody</c> 与 <c>RevalidationEntry</c> 由这里写回。</param>
    /// <param name="uri">最终 URL，用于建键。</param>
    /// <param name="deadlineTimestamp">总预算截止时间戳，读条目与等同伴都受它约束。</param>
    /// <param name="dataType">泛型入口的数据类型，命中交付时按它重新解析正文。</param>
    /// <param name="target">复用的响应对象（重放路径可能传入）。</param>
    // netstandard2.0 编译器不接受方法级 AsyncMethodBuilder（CS0592）：兼容资产上摘掉该特性。
#if !NETSTANDARD2_0
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<CacheAttempt> TryServeFromCacheAsync(
        AxiosCache cache,
        AxiosConfig config,
        Uri uri,
        long deadlineTimestamp,
        Type? dataType,
        AxiosResponse? target)
    {
        var key = cache.KeyFor(config, uri);

        // 请求侧 Cache-Control / Pragma：只有非 Configured 模式才解析。
        var directives = cache.ReadRequestDirectives(config);

        AxiosCacheFlight? flight = null;
        var cacheMissed = false;

        try
        {
            // 请求要求绕开缓存时不查、不参与单飞，但仍会尝试落库。
            if (!directives.SkipLookup)
            {
                if (await cache.TryGetAsync(key, config, deadlineTimestamp).ConfigureAwait(false) is { } cachedReply)
                {
                    AxiosTelemetry.Dispatch.CacheHit(config);
                    var hit = await CompleteShortCircuitAsync(cachedReply, config, dataType, target).ConfigureAwait(false);
                    return new CacheAttempt(hit, key, Flight: null, directives);
                }

                cacheMissed = true;

                if (cache.SingleFlightEnabled)
                {
                    flight = cache.SingleFlight.Join(key);

                    if (!flight.IsLeader)
                    {
                        try
                        {
                            var sharedReply = await AwaitFlightAsync(flight, config, deadlineTimestamp).ConfigureAwait(false);

                            if (sharedReply is not null)
                            {
                                AxiosTelemetry.Dispatch.CacheHit(config);
                                cacheMissed = false;
                                var shared = await CompleteShortCircuitAsync(sharedReply, config, dataType, target).ConfigureAwait(false);
                                return new CacheAttempt(shared, key, Flight: null, directives);
                            }
                        }
                        finally
                        {
                            // 跟随者不落库、不参与广播。
                            flight = null;
                        }
                    }
                }
            }

            // 查过没有且真要发出去：计一次未命中。
            if (cacheMissed)
            {
                AxiosTelemetry.Dispatch.CacheMiss(config);
            }

            // 请求侧否决落库时不必为缓存复制正文。
            config.CacheCaptureRawBody = directives.CanStore;

            // 条件请求：拿带校验器的旧条目，把校验器写成条件头。
            // no-store 不读缓存；调用方自己写了条件头时以调用方为准（不覆盖、也不接管 304）。
            if (cache.ConditionalRequests
                && !directives.NoStore
                && !HasCallerConditionalHeaders(config))
            {
                var revalidation = await cache.TryGetRevalidationAsync(key, config, deadlineTimestamp).ConfigureAwait(false);

                if (revalidation is not null && CacheValidators.HasAny(revalidation))
                {
                    config.RevalidationEntry = revalidation;
                }
            }

            return new CacheAttempt(Served: null, key, flight, directives);
        }
        catch (Exception ex) when (flight is not null)
        {
            // 领导者已加入单飞：抛出去之前必须把失败广播给跟随者，否则它们等的那个 Completion 永远不会完成，
            // 这条键的在途登记也不会被摘除（后续同键请求会一直挂在它上面）。
            BroadcastFlightFailure(cache, key, flight, config, ex);
            throw;
        }
    }

    /// <summary>本次请求在单飞上与缓存上要跨阶段保留的三项状态。</summary>
    /// <param name="Served">命中或跟随者拿到的响应；<c>null</c> 表示还要真正发送。</param>
    /// <param name="Key">本次请求的缓存键。</param>
    /// <param name="Flight">领导者的在途登记；跟随者与未参与单飞时为 <c>null</c>。</param>
    /// <param name="Directives">请求侧的缓存指令，落库时还要再看一次。</param>
    private readonly record struct CacheAttempt(
        AxiosResponse? Served,
        string? Key,
        AxiosCacheFlight? Flight,
        CacheRequestDirectives Directives);

    /// <summary>
    /// 领导者失败时把结果交给跟随者：HTTP 状态类失败共享状态 / 头 / 原始正文让各自判定，
    /// 传输 / 取消类失败按约定广播；没参与单飞或跟随者已脱离时什么都不做。
    /// </summary>
    /// <param name="cache">本次请求的缓存策略。</param>
    /// <param name="key">缓存键。</param>
    /// <param name="flight">领导者的在途登记。</param>
    /// <param name="config">本次请求的生效配置。</param>
    /// <param name="error">触发失败的异常。</param>
    private static void BroadcastFlightFailure(
        AxiosCache? cache,
        string? key,
        AxiosCacheFlight? flight,
        AxiosConfig? config,
        Exception error)
    {
        if (flight is null || cache is null || key is null)
        {
            return;
        }

        if (error is AxiosException { Response: not null } statusFailure && config is not null)
        {
            cache.SingleFlight.Complete(key, flight, TryCreateSharedFailureReply(statusFailure.Response, config, cache));
            return;
        }

        cache.SingleFlight.Fail(key, flight, error);
    }

    /// <summary>
    /// 落库并放行跟随者：只有 2xx、拿到了原始字节、且 <see cref="CacheFreshnessMode"/> 允许落库时才入缓存；写入受剩余总预算约束。
    /// <paramref name="flight"/> 为 <c>null</c> 时只落库，不参与单飞。
    /// </summary>
    private static async Task StoreAsync(
        AxiosCache cache,
        string key,
        AxiosCacheFlight? flight,
        AxiosConfig config,
        CacheRequestDirectives requestDirectives,
        AxiosResponse response,
        long deadlineTimestamp)
    {
        AxiosShortCircuit? reply = null;
        try
        {
            // 先挡掉不可能落库的响应。
            if (IsStorable(response))
            {
                // 总预算已花完：不准备正文、也不等存储。
                if (TotalTimeoutBudget.IsExhausted(TotalTimeoutBudget.Remaining(deadlineTimestamp)))
                {
                    AxiosTelemetry.Dispatch.CacheStoreFailed(config);
                    return;
                }

                // 再判 HTTP 语义、最后才动正文。
                var receivedAt = DateTimeOffset.UtcNow;
                var policy = cache.CreateEntryPolicy(response.Headers, receivedAt, requestDirectives);

                if (policy is not null)
                {
                    reply = TryCreateCacheEntry(response, policy, receivedAt);

                    // 复制正文也要花时间，剩余预算按此刻重算。
                    if (await cache.TrySetAsync(
                            key, reply, policy, TotalTimeoutBudget.Remaining(deadlineTimestamp)).ConfigureAwait(false))
                    {
                        AxiosTelemetry.Dispatch.CacheStored(config);
                    }
                    else
                    {
                        // 存储没接住：跟随者自己发。
                        AxiosTelemetry.Dispatch.CacheStoreFailed(config);
                        reply = null;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 任一环节出错都只让这次不入缓存。
            AxiosTelemetry.Dispatch.CacheStoreFailed(config);
            reply = null;
        }
        finally
        {
            // 落库缓冲到此为止（条目已持有正文）。
            response.RawBodyBuffer = null;

            if (flight is not null)
            {
                cache.SingleFlight.Complete(key, flight, reply);
            }
        }
    }

    /// <summary>
    /// 只有 2xx 且拿到了原始字节的响应才可能落库；空正文同样可缓存（HEAD / 204 本来就没有正文）。
    /// </summary>
    /// <remarks>206 / 带 <c>Content-Range</c> 的响应是某段区间的内容，键只有方法 + URL，落库后会被当整份正文交付。</remarks>
    private static bool IsStorable(AxiosResponse response)
        => response.Status is >= 200 and <= 299
           && response.Status != 206
           && !response.Headers.ContainsKey("Content-Range")
           && response.RawBodyBuffer is not null;

    /// <summary>从响应里做一条缓存条目；调用方先用 <see cref="IsStorable"/> 挡掉不可能落库的响应。</summary>
    /// <remarks>
    /// 头按<b>全部值</b>保存：同名多值折叠成首值会改变语义。<c>Set-Cookie</c> 是唯一例外；<c>Age</c> 命中时重算。
    /// </remarks>
    private static AxiosShortCircuit TryCreateCacheEntry(
        AxiosResponse response,
        AxiosCacheEntryPolicy policy,
        DateTimeOffset receivedAt)
    {
        var body = response.RawBodyBuffer!;
        var (single, multi) = ToHeaderViews(BuildResponseHeaderValues(response));

        return new AxiosShortCircuit
        {
            Status = response.Status,
            StatusText = response.StatusText,
            ProtocolVersion = response.ProtocolVersion,

            Headers = single,
            MultiValueHeaders = multi,

            Body = CanShareBody(response) ? body : (byte[])body.Clone(),

            // 命中时的 Age = 落库龄期 + 在缓存里停留的时间。
            ReceivedAt = receivedAt,
            StoredAge = policy.Age
        };
    }

    /// <summary>把「全部值」字典拆成契约要求的两个头视图：<see cref="AxiosShortCircuit.Headers"/> 存首值，多值视图供命中时回放全部值。</summary>
    /// <remarks>两个视图都按大小写不敏感建键；多值视图直接引用传入的 <see cref="List{T}"/>（不复制），单值视图取当时的第一条。</remarks>
    /// <param name="headers">每个头的全部值；每个头至少一条（空列表会让取首值抛出）。</param>
    /// <returns>单值视图与多值视图。</returns>
    private static (Dictionary<string, string> Single, Dictionary<string, IEnumerable<string>> Multi) ToHeaderViews(
        Dictionary<string, List<string>> headers)
        => (
            headers.ToDictionary(
                entry => entry.Key,
                entry => entry.Value[0],
                StringComparer.OrdinalIgnoreCase),
            headers.ToDictionary(
                entry => entry.Key,
                entry => (IEnumerable<string>)entry.Value,
                StringComparer.OrdinalIgnoreCase));

    /// <summary>条目能否直接与响应共用正文：<c>Data</c> 不是这块数组，且没有 transformResponse。</summary>
    private static bool CanShareBody(AxiosResponse response)
        => !ReferenceEquals(response.Data, response.RawBodyBuffer)
           && response.Config?.HasTransformResponse is not true;

    /// <summary>把响应头收成「全部值」字典：跳过会话型 Cookie、丢弃非法值。</summary>
    private static Dictionary<string, List<string>> BuildResponseHeaderValues(AxiosResponse response)
    {
        var headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
        {
            // Set-Cookie 是会话态，跳过。
            if (IsSessionCookieHeader(header.Key))
            {
                continue;
            }

            var values = new List<string>(2);
            foreach (var value in header.Value)
            {
                if (value is null)
                {
                    continue;
                }

                try
                {
                    // 先校验：坏头不进缓存。
                    AxiosHeaders.ValidateHeaderValue(AxiosHeaders.ValidateName(header.Key), value);
                    values.Add(value);
                }
                catch (ArgumentException)
                {
                    // 单个值不合法就丢掉它。
                }
            }

            if (values.Count > 0)
            {
                headers[header.Key] = values;
            }
        }

        return headers;
    }

    /// <summary>领导者拿到非 2xx 响应：把状态码 / 响应头 / 原始正文做成可共享的短路结果，交给跟随者各自判定。</summary>
    /// <remarks>
    /// 不可共享时返回 <c>null</c>，跟随者各自补发：流式响应、没拿到原始字节、正文超过
    /// <see cref="AxiosCacheOptions.MaxSharedFailureBodyBytes"/>（默认 256 KB，<c>0</c> = 关闭共享）。该结果不落存储。
    /// </remarks>
    private static AxiosShortCircuit? TryCreateSharedFailureReply(
        AxiosResponse response,
        AxiosConfig config,
        AxiosCache cache)
    {
        if (config.ResponseType == ResponseType.Stream)
        {
            return null;
        }

        var body = response.RawBodyBuffer;
        var limit = cache.Options.MaxSharedFailureBodyBytes;
        if (body is null || limit == 0 || body.Length > limit)
        {
            return null;
        }

        var (single, multi) = ToHeaderViews(BuildResponseHeaderValues(response));

        return new AxiosShortCircuit
        {
            Status = response.Status,
            StatusText = response.StatusText,
            ProtocolVersion = response.ProtocolVersion,
            Headers = single,
            MultiValueHeaders = multi,

            // 复制一次，与领导者的 Data 脱钩。
            Body = (byte[])body.Clone()
        };
    }

    /// <summary>是否为会话型 Cookie 头（<c>Set-Cookie</c> / <c>Set-Cookie2</c>）。</summary>
    private static bool IsSessionCookieHeader(string name)
        => string.Equals(name, "Set-Cookie", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "Set-Cookie2", StringComparison.OrdinalIgnoreCase);

    /// <summary>304 里描述「304 自己（没有正文）」的头：不能拿来覆盖缓存条目的同名头。</summary>
    private static readonly HashSet<string> NotModifiedSkippedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Length",
        "Transfer-Encoding",
        "Content-Range"
    };

    /// <summary>调用方自己写了条件头：以调用方为准，库不覆盖，也不接管这次 304。</summary>
    private static bool HasCallerConditionalHeaders(AxiosConfig config)
        => config.HasHeaders
           && (config.Headers.Has("If-None-Match") || config.Headers.Has("If-Modified-Since"));

    /// <summary>条件请求拿到 <c>304 Not Modified</c>：用 304 的响应头更新库里那条，再按库里的正文装配响应。</summary>
    /// <remarks>
    /// 状态码与正文都取库里的那一份；正文不复制，<see cref="AxiosResponse.RawBodyBuffer"/> 与条目共用同一块内存。
    /// </remarks>
    /// <param name="message">收到的 304 响应。</param>
    /// <param name="request">本次请求（写进 <see cref="AxiosResponse.Request"/>）。</param>
    /// <param name="ctx">本次尝试的上下文。</param>
    /// <param name="maxRedirects">实例生效的重定向上限，只用于把原因写进异常消息。</param>
    private static AxiosAttemptResult CompleteNotModified(
        HttpResponseMessage message,
        HttpRequestMessage request,
        in AxiosAttemptContext ctx,
        int maxRedirects)
    {
        var config = ctx.Config;
        var merged = MergeNotModified(config.RevalidationEntry!, message);

        var statusAccepted = IsStatusAccepted(config, merged.Status);

        var response = ResponseReader.ReadShortCircuit(merged, config, ctx.DataType, ctx.Target, lenient: !statusAccepted);

        // 这次确实发出了一条请求。
        response.Request = request;
        response.RawBodyBuffer = config.CacheCaptureRawBody ? merged.Body : null;
        response.CacheRevalidated = true;

        if (!statusAccepted)
        {
            throw AxiosException.FromResponse(config, request, response, maxRedirects, ctx.AttemptNumber);
        }

        return new AxiosAttemptResult(response);
    }

    /// <summary>把 304 的响应头合并到库里那条上：条目头在前，304 的头覆盖同名项；状态 / 正文保持条目里的。</summary>
    private static AxiosShortCircuit MergeNotModified(AxiosShortCircuit stored, HttpResponseMessage message)
    {
        // 多值字典：304 里的 Set-Cookie 可能不止一条。
        var headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        if (stored.Headers is { Count: > 0 } storedHeaders)
        {
            foreach (var header in storedHeaders)
            {
                headers[header.Key] = new List<string>(1) { header.Value };
            }
        }

        // 缓存条目按全部值保存，合并前必须把多值带进来。
        if (stored.MultiValueHeaders is { Count: > 0 } storedMulti)
        {
            foreach (var header in storedMulti)
            {
                var values = new List<string>(2);
                foreach (var value in header.Value)
                {
                    if (value is not null)
                    {
                        values.Add(value);
                    }
                }

                if (values.Count > 0)
                {
                    headers[header.Key] = values;
                }
            }
        }

        foreach (var header in message.Headers)
        {
            MergeNotModifiedHeader(headers, header.Key, header.Value);
        }

        // 内容头（Content-Type 等）在内容集合里；Content-Length 会被跳过。
        if (message.Content is { } content)
        {
            foreach (var header in content.Headers)
            {
                MergeNotModifiedHeader(headers, header.Key, header.Value);
            }
        }

        // 单值字典也要填：外部存储契约里 Headers 是每个头的首值。
        var (single, multi) = ToHeaderViews(headers);

        return new AxiosShortCircuit
        {
            Status = stored.Status,
            StatusText = stored.StatusText,
            Headers = single,
            MultiValueHeaders = multi,
            Body = stored.Body,

            // 协商结果取这次真正发出的那一次。
            ProtocolVersion = message.Version,

            // 304 之后的条目重新计时。
            ReceivedAt = DateTimeOffset.UtcNow,
            StoredAge = headers.TryGetValue("Age", out var ageValues) && ageValues.Count > 0
                ? HttpCacheSemantics.ParseSeconds(ageValues[0]) ?? TimeSpan.Zero
                : TimeSpan.Zero
        };
    }

    /// <summary>把 304 的头并进缓存条目：只描述 304 自身（没有正文）的头直接丢弃，同名值先清后写。</summary>
    private static void MergeNotModifiedHeader(
        Dictionary<string, List<string>> headers,
        string name,
        IEnumerable<string> values)
    {
        if (NotModifiedSkippedHeaders.Contains(name))
        {
            return;
        }

        var list = values.Where(value => value.Length > 0).ToList();
        if (list.Count > 0)
        {
            headers[name] = list;
        }
    }
}
