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
                config, request: null, attempt: 1, budget: config.TotalTimeout);
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
                    config, request: null, attempt: 1, budget: config.TotalTimeout, ex);
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
    /// 写入同样受剩余 <see cref="AxiosConfig.TotalTimeout"/> 约束：预算已用完就不落库，跟随者各自补发。
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
                // 总预算已经花完：不克隆正文、也不等存储 —— 缓存是加速手段，不能反过来拖住返回。
                if (TotalTimeoutBudget.IsExhausted(TotalTimeoutBudget.Remaining(deadlineTimestamp)))
                {
                    AxiosTelemetry.CacheStoreFailed(config);
                    return;
                }

                // 再判 HTTP 语义、最后才复制正文：no-store / no-cache 的响应没必要为落库克隆一份字节。
                var policy = cache.CreateEntryPolicy(response.Headers, DateTimeOffset.UtcNow, requestDirectives);

                if (policy is not null)
                {
                    reply = TryCreateCacheEntry(response);

                    // 复制正文也要花时间，剩余预算按此刻重算。
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
            // 条目已持有自己那一份，响应不必再拉着原始字节。
            response.RawBody = null;

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
           && response.RawBody is not null;

    /// <summary>从响应里做一条缓存条目；调用方先用 <see cref="IsStorable"/> 挡掉不可能落库的响应。</summary>
    private static AxiosShortCircuit TryCreateCacheEntry(AxiosResponse response)
    {
        var body = response.RawBody!;

        // 头按首值存：短路响应的头字典是单值的。
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
        {
            // Set-Cookie 是发给「这一次连接」的会话态：命中时回放等于把别人的会话塞进下一次请求。
            if (IsSessionCookieHeader(header.Key))
            {
                continue;
            }

            if (header.Value.FirstOrDefault() is not { } value)
            {
                continue;
            }

            try
            {
                // 命中时这些头会走短路响应的校验：先过一遍，坏头不进缓存（否则每次命中都抛）。
                AxiosHeaders.ValidateHeaderValue(AxiosHeaders.ValidateHeaderName(header.Key), value);
                headers[header.Key] = value;
            }
            catch (ArgumentException)
            {
                // 单个头不合法就丢掉它，正文照旧可用。
            }
        }

        return new AxiosShortCircuit
        {
            Status = response.Status,
            StatusText = response.StatusText,
            ProtocolVersion = response.ProtocolVersion,
            Headers = headers,

            // 缓存自己持有一份：调用方拿到的 byte[] 可能就是这块内存，不能被改写污染。
            Body = (byte[])body.Clone()
        };
    }

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
    /// 正文不复制：<see cref="AxiosResponse.RawBody"/> 与条目共用同一块内存（落库时 <see cref="TryCreateCacheEntry"/> 会克隆）。
    /// </remarks>
    private static AxiosAttemptResult CompleteNotModified(
        HttpResponseMessage message,
        HttpRequestMessage request,
        in AxiosAttemptContext ctx)
    {
        var config = ctx.Config;
        var merged = MergeNotModified(config.RevalidationEntry!, message);

        var validateStatus = config.CallbacksOrNull?.ValidateStatus ?? DefaultValidateStatus;
        var statusAccepted = validateStatus(merged.Status);

        var response = ResponseReader.ReadShortCircuit(merged, config, ctx.DataType, ctx.Target, lenient: !statusAccepted);

        // 这次确实发出了一条请求：与「命中不发请求」区分开。
        response.Request = request;
        response.RawBody = config.CacheCaptureRawBody ? merged.Body : null;
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
            ProtocolVersion = message.Version
        };
    }

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
