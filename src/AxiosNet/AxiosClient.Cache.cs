using AxiosNet.Cache;

namespace AxiosNet;

// AxiosClient 的缓存阶段：命中直接交付、未命中参与单飞、成功后落库。
public sealed partial class AxiosClient
{
    /// <summary>
    /// 跟随者等领导者那一份结果：只让自己的取消生效（领导者的取消不牵连自己）。
    /// 返回 <c>null</c> 表示这次没有可共享的字节，调用方自己发。
    /// </summary>
    private static async Task<AxiosShortCircuit?> AwaitFlightAsync(AxiosCacheFlight flight, AxiosConfig config)
    {
        try
        {
            return await flight.Completion.WaitAsync(config.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            // 自己的令牌取消 → 统一错误模型；否则是领导者被它自己的调用方取消，这次请求本身没问题。
            if (config.CancellationToken.IsCancellationRequested)
            {
                throw AxiosException.Canceled(config, null, ex);
            }

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
    private static async Task StoreAsync(AxiosCache cache, string key, AxiosCacheFlight? flight, AxiosResponse response)
    {
        AxiosShortCircuit? reply = null;
        try
        {
            // 先挡掉不可能落库的响应（非 2xx / 没拿到原始字节）：解析模式连响应头都不用解析。
            if (IsStorable(response))
            {
                // 再判 HTTP 语义、最后才复制正文：no-store / no-cache 的响应没必要为落库克隆一份字节。
                var policy = cache.CreateEntryPolicy(response.Headers, DateTimeOffset.UtcNow);

                if (policy is not null)
                {
                    reply = TryCreateCacheEntry(response);

                    if (!await cache.TrySetAsync(key, reply, policy).ConfigureAwait(false))
                    {
                        // 存储没接住：跟随者别去读一条不存在的条目，自己发。
                        reply = null;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 缓存是加速手段，任何一步出错都只让这次不入缓存。
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
    private static bool IsStorable(AxiosResponse response)
        => response.Status is >= 200 and <= 299 && response.RawBody is not null;

    /// <summary>从响应里做一条缓存条目；调用方先用 <see cref="IsStorable"/> 挡掉不可能落库的响应。</summary>
    private static AxiosShortCircuit TryCreateCacheEntry(AxiosResponse response)
    {
        var body = response.RawBody!;

        // 头按首值存：短路响应的头字典是单值的（多值的 Set-Cookie 只留第一条）。
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
        {
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
}
