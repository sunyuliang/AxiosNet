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

    /// <summary>领导者落库并放行跟随者：只有 2xx 且拿到了原始字节才入缓存，写缓存失败不影响这次请求。</summary>
    private static async Task CompleteCacheFlightAsync(AxiosCache cache, string key, AxiosCacheFlight flight, AxiosResponse response)
    {
        AxiosShortCircuit? reply = null;
        try
        {
            reply = TryCreateCacheEntry(response);

            if (reply is not null && !await cache.TrySetAsync(key, reply).ConfigureAwait(false))
            {
                // 存储没接住：跟随者别去读一条不存在的条目，自己发。
                reply = null;
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
            cache.CompleteFlight(key, flight, reply);
        }
    }

    /// <summary>从响应里做一条缓存条目：非 2xx、没有原始字节（流式 / 响应拦截器换了对象）都返回 <c>null</c>。</summary>
    private static AxiosShortCircuit? TryCreateCacheEntry(AxiosResponse response)
    {
        if (response.Status is < 200 or > 299 || response.RawBody is not { Length: > 0 } body)
        {
            return null;
        }

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
