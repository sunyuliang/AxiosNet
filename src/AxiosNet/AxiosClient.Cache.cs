using AxiosNet.Cache;
using AxiosNet.Internal;
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
    /// <paramref name="flight"/> 为 <c>null</c> 时表示这次请求不参与单飞（关掉了单飞，或是跟随者落空后自己补发），只落库。
    /// </summary>
    private static async Task StoreAsync(
        AxiosCache cache,
        string key,
        AxiosCacheFlight? flight,
        AxiosConfig config,
        CacheRequestDirectives requestDirectives,
        AxiosResponse response)
    {
        AxiosShortCircuit? reply = null;
        try
        {
            // 先挡掉不可能落库的响应（非 2xx / 部分内容 / 没拿到原始字节）：解析模式连响应头都不用解析。
            if (IsStorable(response))
            {
                // 再判 HTTP 语义、最后才复制正文：no-store / no-cache 的响应没必要为落库克隆一份字节。
                var policy = cache.CreateEntryPolicy(response.Headers, DateTimeOffset.UtcNow, requestDirectives);

                if (policy is not null)
                {
                    reply = TryCreateCacheEntry(response);

                    if (await cache.TrySetAsync(key, reply, policy).ConfigureAwait(false))
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
}
