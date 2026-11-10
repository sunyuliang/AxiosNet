using AxiosNet.Cache;
using AxiosNet.Internal;
using AxiosNet.Pipeline;
using AxiosNet.Telemetry;

namespace AxiosNet;

// AxiosClient 的缓存阶段：命中直接交付、未命中参与单飞、成功后落库。
public sealed partial class AxiosClient
{
    /// <summary>
    /// 跟随者等领导者那一份结果：只让自己的取消生效（领导者的取消不牵连自己），等待时长受剩余总预算约束。
    /// 返回 <c>null</c> 表示这次没有可共享的字节，调用方自己发。
    /// </summary>
    private static async Task<AxiosShortCircuit?> AwaitFlightAsync(
        AxiosCacheFlight flight,
        AxiosConfig config,
        long deadlineTimestamp)
    {
        var remaining = TotalTimeoutBudget.Remaining(deadlineTimestamp);

        // 总预算已经花完：连等别人的结果都不必了，按统一错误模型报出来。
        if (TotalTimeoutBudget.IsExhausted(remaining))
        {
            throw AxiosException.TotalTimeoutExceeded(
                config, request: null, attempt: 1, budget: config.TotalTimeoutValue);
        }

        // 没有总预算：只让自己的令牌生效（不建计时器）。
        if (remaining is null)
        {
            return await WaitOnFlightAsync(flight, config, config.CancellationToken, bounded: false).ConfigureAwait(false);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(config.CancellationToken);

        // CancelAfter 上限约 49.7 天，越界会抛裸异常，故收敛到上限。
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

            // 领导者以未翻译的取消失败（异常已随单飞广播）：跟随者自己发，别当成本次请求超时。
            if (flight.Completion.IsCompleted)
            {
                return null;
            }

            // 令牌没取消、等待却有上限：是总预算到点了，别把跟随者挂在原地等领导者。
            if (bounded)
            {
                throw AxiosException.TotalTimeoutExceeded(
                    config, request: null, attempt: 1, budget: config.TotalTimeoutValue, ex);
            }

            // 否则是领导者被它自己的调用方取消，这次请求本身没问题。
            return null;
        }
        catch (AxiosException error) when (error.Code == AxiosException.ErrCanceled && !config.CancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// 落库并放行跟随者：只有 2xx、拿到了原始字节、且 <see cref="CacheFreshnessMode"/> 允许落库时才入缓存；写缓存失败不影响这次请求。
    /// 写入同样受剩余 <see cref="AxiosConfig.ResilienceSettings.TotalTimeout"/> 约束：预算已用完就不落库，跟随者各自补发。
    /// <paramref name="flight"/> 为 <c>null</c> 时表示这次请求不参与单飞（关掉了单飞，或是跟随者落空后自己补发），只落库。
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
            // 先挡掉不可能落库的响应（非 2xx / 部分内容 / 没拿到原始字节）：解析模式连响应头都不用解析。
            if (IsStorable(response))
            {
                // 总预算已经花完：不准备正文、也不等存储 —— 缓存是加速手段，不能反过来拖住返回。
                if (TotalTimeoutBudget.IsExhausted(TotalTimeoutBudget.Remaining(deadlineTimestamp)))
                {
                    AxiosTelemetry.CacheStoreFailed(config);
                    return;
                }

                // 再判 HTTP 语义、最后才动正文：no-store / no-cache 的响应没必要为落库复制字节。
                var receivedAt = DateTimeOffset.UtcNow;
                var policy = cache.CreateEntryPolicy(response.Headers, receivedAt, requestDirectives);

                if (policy is not null)
                {
                    reply = TryCreateCacheEntry(response, policy, receivedAt);

                    // 复制正文（需要时）也要花时间，剩余预算按此刻重算。
                    if (await cache.TrySetAsync(
                            key, reply, policy, TotalTimeoutBudget.Remaining(deadlineTimestamp)).ConfigureAwait(false))
                    {
                        AxiosTelemetry.CacheStored(config);
                    }
                    else
                    {
                        // 存储没接住：跟随者别去读一条不存在的条目，自己发。
                        AxiosTelemetry.CacheStoreFailed(config);
                        reply = null;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 缓存是加速手段，任何一步出错都只让这次不入缓存。
            AxiosTelemetry.CacheStoreFailed(config);
            reply = null;
        }
        finally
        {
            // 落库缓冲到此为止（条目已持有正文，可能与响应共用同一块内存）；对外的 RawBody 视图若开启仍随响应存活。
            response.RawBodyBuffer = null;

            if (flight is not null)
            {
                cache.CompleteFlight(key, flight, reply);
            }
        }
    }

    /// <summary>
    /// 只有 2xx 且拿到了原始字节的响应才可能落库（流式 / 响应拦截器换了对象都拿不到字节）。
    /// 空正文同样是可缓存的响应（HEAD / 204 本来就没有正文），因此只判 <c>null</c> 不判长度。
    /// </summary>
    /// <remarks>
    /// 206 / 带 <c>Content-Range</c> 的响应是某段区间的内容，键又只有方法 + URL：落库后普通 GET 会把它当整份正文交付。
    /// </remarks>
    private static bool IsStorable(AxiosResponse response)
        => response.Status is >= 200 and <= 299
           && response.Status != 206
           && !response.Headers.ContainsKey("Content-Range")
           && response.RawBodyBuffer is not null;

    /// <summary>从响应里做一条缓存条目；调用方先用 <see cref="IsStorable"/> 挡掉不可能落库的响应。</summary>
    /// <remarks>
    /// 头按<b>全部值</b>保存：同名多值（如多个 <c>WWW-Authenticate</c> challenge）折叠成首值会改变语义。
    /// <c>Set-Cookie</c> 是唯一例外（会话态不缓存）。<c>Age</c> 头不固定：命中时按这里的落库时刻与龄期重算。
    /// </remarks>
    private static AxiosShortCircuit TryCreateCacheEntry(
        AxiosResponse response,
        AxiosCacheEntryPolicy policy,
        DateTimeOffset receivedAt)
    {
        var body = response.RawBodyBuffer!;
        var headers = BuildResponseHeaderValues(response);

        return new AxiosShortCircuit
        {
            Status = response.Status,
            StatusText = response.StatusText,
            ProtocolVersion = response.ProtocolVersion,

            // 单值字典保留（第三方存储在按契约读它），多值字典供交付时原样回放全部值。
            Headers = headers.ToDictionary(
                entry => entry.Key,
                entry => entry.Value[0],
                StringComparer.OrdinalIgnoreCase),
            MultiValueHeaders = headers.ToDictionary(
                entry => entry.Key,
                entry => (IEnumerable<string>)entry.Value,
                StringComparer.OrdinalIgnoreCase),

            Body = CanShareBody(response) ? body : (byte[])body.Clone(),

            // 命中时的 Age = 落库龄期 + 在缓存里停留的时间，而不是原样回放入库时的数值。
            ReceivedAt = receivedAt,
            StoredAge = policy.Age
        };
    }

    /// <summary>
    /// 条目能否直接与响应共用正文：<c>Data</c> 不是这块数组，且没有 transformResponse（它可能把数组包进自己的对象）。
    /// </summary>
    private static bool CanShareBody(AxiosResponse response)
        => !ReferenceEquals(response.Data, response.RawBodyBuffer)
           && response.Config?.HasTransformResponse is not true;

    /// <summary>
    /// 把响应头收成「全部值」字典：跳过会话型 Cookie、丢弃非法值；缓存条目与非 2xx 共享都走这一条。
    /// </summary>
    private static Dictionary<string, List<string>> BuildResponseHeaderValues(AxiosResponse response)
    {
        var headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
        {
            // Set-Cookie 是发给「这一次连接」的会话态：命中 / 共享时回放等于把别人的会话塞进下一次请求。
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
                    // 命中时这些头会走短路响应的校验：先过一遍，坏头不进缓存（否则每次命中都抛）。
                    AxiosHeaders.ValidateHeaderValue(AxiosHeaders.ValidateHeaderName(header.Key), value);
                    values.Add(value);
                }
                catch (ArgumentException)
                {
                    // 单个值不合法就丢掉它，正文照旧可用。
                }
            }

            if (values.Count > 0)
            {
                headers[header.Key] = values;
            }
        }

        return headers;
    }

    /// <summary>
    /// 领导者拿到非 2xx 响应：把状态码 / 响应头 / 原始正文做成可共享的短路结果，交给跟随者各自判定。
    /// </summary>
    /// <remarks>
    /// 不可共享时返回 <c>null</c>，跟随者各自补发：
    /// 流式响应（<see cref="ResponseType.Stream"/>，只能读一次）；没拿到原始字节（未捕获正文 / 不落库的请求）；
    /// 正文超过 <see cref="AxiosCacheOptions.MaxSharedFailureBodyBytes"/>（默认 256 KB，<c>0</c> = 关闭共享）。
    /// 共享正文在这里复制一次，与领导者自己响应的 <c>Data</c> 脱钩（<c>byte[]</c> 形态可能直接引用原数组）；
    /// 跟随者各自经 <see cref="Internal.ResponseReader.ReadShortCircuit"/> 建立独立的响应头与 <c>Data</c>，不会共享可变对象。
    /// 该结果只用于本次单飞，不落 <see cref="Cache.IAxiosCacheStore"/>：HTTP 缓存契约不存非 2xx，存储实现者无需扩展。
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

        var headers = BuildResponseHeaderValues(response);

        return new AxiosShortCircuit
        {
            Status = response.Status,
            StatusText = response.StatusText,
            ProtocolVersion = response.ProtocolVersion,
            Headers = headers.ToDictionary(
                entry => entry.Key,
                entry => entry.Value[0],
                StringComparer.OrdinalIgnoreCase),
            MultiValueHeaders = headers.ToDictionary(
                entry => entry.Key,
                entry => (IEnumerable<string>)entry.Value,
                StringComparer.OrdinalIgnoreCase),

            // 复制一次而不是每个跟随者复制：领导者的 Data 可能是同一块数组，共享对象必须与它脱钩。
            Body = (byte[])body.Clone()
        };
    }

    /// <summary>是否为会话型 Cookie 头（<c>Set-Cookie</c> / <c>Set-Cookie2</c>）：落库时跳过，缓存条目不该回放过期 Cookie。</summary>
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

    /// <summary>
    /// 条件请求拿到 <c>304 Not Modified</c>：用 304 的响应头更新库里那条，再按库里的正文装配响应。
    /// </summary>
    /// <remarks>
    /// 状态码与正文都取库里的那一份（304 没有正文）；响应拦截器与 <c>validateStatus</c> 看到的都是合并后的响应。
    /// 正文不复制：<see cref="AxiosResponse.RawBodyBuffer"/> 与条目共用同一块内存（重新落库时由 <see cref="TryCreateCacheEntry"/> 按需复制）。
    /// </remarks>
    private static AxiosAttemptResult CompleteNotModified(
        HttpResponseMessage message,
        HttpRequestMessage request,
        in AxiosAttemptContext ctx)
    {
        var config = ctx.Config;
        var merged = MergeNotModified(config.RevalidationEntry!, message);

        var validateStatus = config.ValidateStatus ?? DefaultValidateStatus;
        var statusAccepted = validateStatus(merged.Status);

        var response = ResponseReader.ReadShortCircuit(merged, config, ctx.DataType, ctx.Target, lenient: !statusAccepted);

        // 这次确实发出了一条请求：与「命中不发请求」区分开。
        response.Request = request;
        response.RawBodyBuffer = config.CacheCaptureRawBody ? merged.Body : null;
        response.CacheRevalidated = true;

        if (!statusAccepted)
        {
            throw AxiosException.FromResponse(config, request, response);
        }

        return new AxiosAttemptResult(response);
    }

    /// <summary>把 304 的响应头合并到库里那条上：条目头在前，304 的头覆盖同名项；状态 / 正文保持条目里的。</summary>
    private static AxiosShortCircuit MergeNotModified(AxiosShortCircuit stored, HttpResponseMessage message)
    {
        // 多值字典：304 里的 Set-Cookie 可能不止一条，按逗号折叠会写出语法非法的头。
        var headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        if (stored.Headers is { Count: > 0 } storedHeaders)
        {
            foreach (var header in storedHeaders)
            {
                headers[header.Key] = new List<string>(1) { header.Value };
            }
        }

        // 缓存条目按全部值保存：304 合并前必须把多值也带进来，否则条件头 / Set-Cookie 会丢。
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

        return new AxiosShortCircuit
        {
            Status = stored.Status,
            StatusText = stored.StatusText,
            MultiValueHeaders = headers.ToDictionary(
                entry => entry.Key,
                entry => (IEnumerable<string>)entry.Value,
                StringComparer.OrdinalIgnoreCase),
            Body = stored.Body,

            // 协商结果取这次真正发出的那一次（降级是静默的）。
            ProtocolVersion = message.Version,

            // 304 之后的条目重新计时：Age 以合并后的头为准（304 通常不带 Age，按 0 起算）。
            ReceivedAt = DateTimeOffset.UtcNow,
            StoredAge = headers.TryGetValue("Age", out var ageValues) && ageValues.Count > 0
                ? AxiosCache.ParseSeconds(ageValues[0]) ?? TimeSpan.Zero
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
