using System.Net;
using AxiosNet.Internal.Support;

namespace AxiosNet.Pooling;

/// <summary>按选项分组校验 <see cref="ConnectionPoolOptions"/> 的取值范围与兼容资产能力。</summary>
internal static class ConnectionPoolOptionsValidator
{
    /// <summary>
    /// 构造期校验：这些值非法时统一收敛到 <c>ERR_BAD_OPTION_VALUE</c>。
    /// </summary>
    internal static void Validate(ConnectionPoolOptions options, AxiosConfig? config = null)
    {
        if (options.MaxConnectionsPerServer <= 0)
        {
            throw AxiosException.InvalidOption($"MaxConnectionsPerServer 必须大于 0（当前 {options.MaxConnectionsPerServer}）。", config);
        }

#if NETSTANDARD2_0
        // 兼容资产的默认 Handler 是 HttpClientHandler：下列连接级选项没有映射位置，非默认值在构造期拒绝。
        // 只有全局 HandlerFactory 能接管默认档；命名档工厂不能给默认档豁免。
        if (options.HandlerFactory is null)
        {
            if (options.PooledConnectionLifetime != ConnectionPoolOptions.DefaultPooledConnectionLifetime)
            {
                throw UnmappedOnCompatibilityAsset("PooledConnectionLifetime", config);
            }

            if (options.PooledConnectionIdleTimeout != ConnectionPoolOptions.DefaultPooledConnectionIdleTimeout)
            {
                throw UnmappedOnCompatibilityAsset("PooledConnectionIdleTimeout", config);
            }

            if (options.ConnectTimeout != ConnectionPoolOptions.DefaultConnectTimeout)
            {
                throw UnmappedOnCompatibilityAsset("ConnectTimeout", config);
            }

            if (!options.EnableMultipleHttp2Connections)
            {
                throw UnmappedOnCompatibilityAsset("EnableMultipleHttp2Connections", config);
            }

            if (options.KeepAlivePingDelay != Timeout.InfiniteTimeSpan
                || options.KeepAlivePingTimeout != ConnectionPoolOptions.DefaultKeepAlivePingTimeout)
            {
                throw UnmappedOnCompatibilityAsset("KeepAlivePingDelay / KeepAlivePingTimeout", config);
            }
        }
#endif

        if (IsNegativeLimit(options.PooledConnectionLifetime))
        {
            throw AxiosException.InvalidOption("PooledConnectionLifetime 不能为负（0 表示连接不复用；不限生命周期请用 Timeout.InfiniteTimeSpan）。", config);
        }

        if (IsNegativeLimit(options.PooledConnectionIdleTimeout))
        {
            throw AxiosException.InvalidOption("PooledConnectionIdleTimeout 不能为负（0 表示不保留空闲连接；不限请用 Timeout.InfiniteTimeSpan）。", config);
        }

        if (options.ConnectTimeout <= TimeSpan.Zero && options.ConnectTimeout != Timeout.InfiniteTimeSpan)
        {
            throw AxiosException.InvalidOption("ConnectTimeout 必须大于 0；不限请用 Timeout.InfiniteTimeSpan。", config);
        }

        // 上限是 int.MaxValue 毫秒（约 24.85 天）。
        if (options.ConnectTimeout > TimeSpan.FromMilliseconds(int.MaxValue))
        {
            throw AxiosException.InvalidOption(
                $"ConnectTimeout（{options.ConnectTimeout}）超出底层 Handler 上限（约 24.85 天）：请调小，"
                + "或不限请用 Timeout.InfiniteTimeSpan。",
                config);
        }

        // SocketsHttpHandler 只接受正数。
        if (options.MaxResponseHeadersLength <= 0)
        {
            throw AxiosException.InvalidOption(
                $"MaxResponseHeadersLength 必须大于 0（当前 {options.MaxResponseHeadersLength} KB）："
                + "它是响应头的绝对上限；需要更大余量请设成更大的 KB 值。", config);
        }

        if (options.MaxAutomaticRedirections <= 0)
        {
            throw AxiosException.InvalidOption("MaxAutomaticRedirections 必须大于 0；不跟随重定向请设 AllowAutoRedirect = false。", config);
        }

        // Cookie 容量三项只在真的建容器时校验。
        if (options.UseCookies)
        {
            if (options.CookieCapacity <= 0)
            {
                throw AxiosException.InvalidOption($"CookieCapacity 必须大于 0（当前 {options.CookieCapacity}）。", config);
            }

            if (options.CookiePerDomainCapacity <= 0 || options.CookiePerDomainCapacity > options.CookieCapacity)
            {
                throw AxiosException.InvalidOption(
                    $"CookiePerDomainCapacity 必须在 1 到 CookieCapacity（{options.CookieCapacity}）之间（当前 {options.CookiePerDomainCapacity}）："
                    + "只调小总容量时请一并调小单域上限。", config);
            }

            if (options.CookieMaxCookieSize <= 0)
            {
                throw AxiosException.InvalidOption($"CookieMaxCookieSize 必须大于 0（当前 {options.CookieMaxCookieSize}）。", config);
            }
        }

        // DecompressionMethods.All 在 BCL 里是 -1（每一位都亮），属合法「全开」；超出已知算法位按错误拦下。
#if NETSTANDARD2_0
        // 兼容资产没有 Brotli / All：合法位只有 gzip / deflate，直接按位判定。
        const DecompressionMethods knownDecompression =
            DecompressionMethods.GZip | DecompressionMethods.Deflate;
        const string allowedDecompression = "GZip / Deflate 的任意组合";
        var requestedDecompression = options.Decompression;
#else
        const DecompressionMethods knownDecompression =
            DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli;
        const string allowedDecompression = "GZip / Deflate / Brotli 的组合或 All";
        var requestedDecompression = options.Decompression == DecompressionMethods.All
            ? knownDecompression
            : options.Decompression;
#endif
        if ((requestedDecompression & ~knownDecompression) != 0)
        {
            throw AxiosException.InvalidOption(
                $"Decompression 含不支持的算法（当前 {options.Decompression}）：只允许 {allowedDecompression}，"
                + "一个都不解压请设为 None（或把 AutomaticDecompression 设为 false）。", config);
        }

        if (IsNegativeLimit(options.IdleHandlerLifetime))
        {
            throw AxiosException.InvalidOption("IdleHandlerLifetime 不能为负（0 表示任何空闲节点立即可被驱逐；不限请用 Timeout.InfiniteTimeSpan）。", config);
        }

        if (options.EvictionInterval < TimeSpan.Zero)
        {
            throw AxiosException.InvalidOption("EvictionInterval 不能为负（0 表示不启用后台驱逐定时器；要永不驱逐请设为 0 并手动调用 EvictIdleEndpoints）。", config);
        }

        // 与 CancelAfter 同一口径的计时器上限（约 49.7 天）。
        if (options.EvictionInterval > Timeouts.Max)
        {
            throw AxiosException.InvalidOption($"EvictionInterval（{options.EvictionInterval}）超出后台计时器上限（约 49.7 天）：请调小，或设为 0 改用手动驱逐。", config);
        }

        // 0 不是「不 ping」而是「立刻 ping」。
        if (options.KeepAlivePingDelay <= TimeSpan.Zero && options.KeepAlivePingDelay != Timeout.InfiniteTimeSpan)
        {
            throw AxiosException.InvalidOption("KeepAlivePingDelay 必须大于 0；不发 keep-alive ping 请设为 Timeout.InfiniteTimeSpan。", config);
        }

        if (options.KeepAlivePingTimeout <= TimeSpan.Zero && options.KeepAlivePingTimeout != Timeout.InfiniteTimeSpan)
        {
            throw AxiosException.InvalidOption("KeepAlivePingTimeout 必须大于 0；不因 ping 超时断连请设为 Timeout.InfiniteTimeSpan。", config);
        }

        // 两者都由底层计时器表达，上限同 Timeouts.Max。
        var pingLimit = Timeouts.Max;
        if (options.KeepAlivePingDelay > pingLimit || options.KeepAlivePingTimeout > pingLimit)
        {
            throw AxiosException.InvalidOption(
                $"KeepAlivePingDelay / KeepAlivePingTimeout 超出计时器上限（约 49.7 天）：请调小，"
                + "或不发 ping 请设为 Timeout.InfiniteTimeSpan。",
                config);
        }

#if !NETSTANDARD2_0
        // 枚举只认两个值，其它值底层会当成「不 ping」。
        if (options.KeepAlivePingPolicy is not (HttpKeepAlivePingPolicy.WithActiveRequests or HttpKeepAlivePingPolicy.Always))
        {
            throw AxiosException.InvalidOption(
                $"KeepAlivePingPolicy 取值非法（当前 {(int)options.KeepAlivePingPolicy}）："
                + "只支持 WithActiveRequests（只在有在途请求时 ping）与 Always。",
                config);
        }
#endif
    }

    /// <summary>是否为非法的负时长（<see cref="Timeout.InfiniteTimeSpan"/> 表示「不限」，放行）。</summary>
    private static bool IsNegativeLimit(TimeSpan value)
        => value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan;

#if NETSTANDARD2_0
    /// <summary>兼容资产上「没有对应配置入口」的池选项统一报错。</summary>
    /// <param name="optionName">被拒绝的选项名（用于消息定位）。</param>
    /// <param name="config">当前配置（可为 <c>null</c>，用于异常上下文）。</param>
    /// <returns>可直接抛出的配置错误。</returns>
    private static AxiosException UnmappedOnCompatibilityAsset(string optionName, AxiosConfig? config)
        => AxiosException.InvalidOption(
            $"{optionName} 在 netstandard2.0 兼容资产上无法生效：该资产由 HttpClientHandler 承载，"
            + "没有连接生命周期 / 建连超时 / h2 ping 的配置入口。请让消费方运行 .NET 6+ 以取到 net6.0 / net8.0 资产，"
            + "或用 ConnectionPoolOptions.HandlerFactory 接管默认档 Handler（命名传输档的工厂只作用于该档）；"
            + "空闲节点驱逐可改用 IdleHandlerLifetime（该选项在兼容资产上有效）。",
            config);
#endif
}
