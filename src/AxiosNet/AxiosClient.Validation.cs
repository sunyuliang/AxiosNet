using AxiosNet.Circuit;
using AxiosNet.Internal;
using AxiosNet.Pooling;
using AxiosNet.Retry;

namespace AxiosNet;

// AxiosClient 的构造期与按请求校验：连接级选项、限制范围、并发闸门、HTTP/3。
public sealed partial class AxiosClient
{
    /// <summary>连接级选项无法按 defaults 生效时的构造期校验（注入 handler / 共享池共用判定，只是文案不同）。</summary>
    private static void ValidateConnectionLevelOptions(
        AxiosConfig? defaults,
        AxiosConfig config,
        ConnectionLevelMessages messages)
    {
        if (defaults is null)
        {
            return;
        }

        if (defaults.MaxRedirectsSet)
        {
            throw AxiosException.InvalidOption(messages.MaxRedirects, config);
        }

        if (defaults.WithCredentialsSet)
        {
            throw AxiosException.InvalidOption(messages.WithCredentials, config);
        }

        if (defaults.ConnectionOrNull?.Proxy is not null || defaults.ConnectionOrNull?.Credentials is not null)
        {
            throw AxiosException.InvalidOption(messages.ProxyCredentials, config);
        }
    }

    /// <summary>两种连接级场景的报错文案（判定相同、归属方不同）。</summary>
    private sealed record ConnectionLevelMessages(string MaxRedirects, string WithCredentials, string ProxyCredentials)
    {
        /// <summary>注入自定义 handler：连接级选项由 handler 决定。</summary>
        public static ConnectionLevelMessages Handler { get; } = new(
            "注入自定义 handler 时 MaxRedirects 由 handler 决定（重定向在连接层处理），请在 handler 上配置。",
            "注入自定义 handler 时 WithCredentials 由 handler 决定（Cookie 容器在连接层），请在 handler 上配置。",
            "注入自定义 handler 时 Proxy / Credentials 由 handler 决定，请在 handler 上配置。");

        /// <summary>共享连接池：连接级选项由池决定。</summary>
        public static ConnectionLevelMessages SharedPool { get; } = new(
            "共享连接池时 MaxRedirects 由池决定（重定向由各 origin 的 Handler 处理），"
            + "请在创建 ConnectionPoolOptions 时设置 AllowAutoRedirect / MaxAutomaticRedirections，"
            + "或不传 pool 让本实例自建池。",
            "共享连接池时 WithCredentials 由池决定（Cookie 容器在连接层），"
            + "请在创建 ConnectionPoolOptions 时设置 UseCookies / Credentials，"
            + "或不传 pool 让本实例自建池。",
            "共享连接池时 Proxy / Credentials 由池决定，"
            + "请在 ConnectionPoolOptions.Proxy / Credentials 上设置，或不传 pool 让本实例自建池。");
    }

    /// <summary>
    /// 「明确要求 HTTP/3」时的前置护栏：只拦 <c>RequestVersionExact</c> / <c>RequestVersionOrHigher</c>，
    /// <c>RequestVersionOrLower</c> 的降级是正常路径；判据是本机能力 <see cref="Http3Support"/>，做不到就报配置错误。
    /// 换端点换掉 scheme 后按同一套护栏重判，不静默降级。
    /// </summary>
    internal static void ValidateHttp3Request(Uri uri, Version version, HttpVersionPolicy policy, AxiosConfig config)
    {
        if (version.Major < 3 || policy == HttpVersionPolicy.RequestVersionOrLower)
        {
            return;
        }

        // 明文目标上不存在 h3（QUIC 自带 TLS 1.3）。
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw AxiosException.InvalidOption(
                $"HTTP/3 只能跑在 QUIC（TLS 1.3 over UDP）上，{uri.Scheme}:// 目标不存在 h3："
                + "请改用 https 目标；要在明文目标上自动落到 1.1，请把 VersionPolicy 设为 RequestVersionOrLower。",
                config);
        }

        if (!Http3Support.IsSupported)
        {
            throw AxiosException.InvalidOption(
                $"当前环境跑不了 HTTP/3，无法满足 HttpVersion = 3.0 + VersionPolicy = {policy}："
                + $"{Http3Support.UnsupportedReason}。"
                + "想要「有 h3 就用 h3、没有就自动降级」，请把 VersionPolicy 设为 RequestVersionOrLower。",
                config);
        }
    }

    /// <summary>限制类配置的取值范围校验：负值一律报错，不静默当作「不限制」；<c>Timeout=0</c> 不超时，<c>MaxRate=0</c> 不限速。</summary>
    private static void ValidateRangeConfig(AxiosConfig config)
    {
        if (config.Timeout < TimeSpan.Zero)
        {
            throw AxiosException.InvalidOption($"Timeout 不能为负（当前 {config.Timeout}）：要关闭超时请显式设为 TimeSpan.Zero。", config);
        }

        var limits = config.LimitsOrNull;

        if (limits?.StreamIdleTimeout is { } idleTimeout && idleTimeout < TimeSpan.Zero)
        {
            throw AxiosException.InvalidOption($"StreamIdleTimeout 不能为负（当前 {idleTimeout}）：要不设空闲超时请显式设为 TimeSpan.Zero。", config);
        }

        if (limits?.MaxRate is < 0)
        {
            throw AxiosException.InvalidOption($"MaxRate 不能为负（当前 {limits.MaxRate}）：要不限速请设为 null（0 等价于不限速）。", config);
        }

        if (limits?.MaxContentLength is < 0)
        {
            throw AxiosException.InvalidOption($"MaxContentLength 不能为负（当前 {limits.MaxContentLength}）：不限制请设为 null。", config);
        }

        if (limits?.MaxBodyLength is < 0)
        {
            throw AxiosException.InvalidOption($"MaxBodyLength 不能为负（当前 {limits.MaxBodyLength}）：不限制请设为 null。", config);
        }

        // 并发上限：0 与负数都没有语义，静默当成「不限制」会让调用方以为限流生效了。
        if (limits?.MaxConcurrentRequests is int maxConcurrency && maxConcurrency < 1)
        {
            throw AxiosException.InvalidOption(
                $"MaxConcurrentRequests 必须是正整数（当前 {maxConcurrency}）：不限制请设为 null，"
                + "要「不排队」请把 QueueTimeout 设为 TimeSpan.Zero。", config);
        }

        if (limits?.QueueTimeout is { } queueTimeout && queueTimeout < TimeSpan.Zero)
        {
            throw AxiosException.InvalidOption($"QueueTimeout 不能为负（当前 {queueTimeout}）：要不排队请设为 TimeSpan.Zero，要一直等请设为 null。", config);
        }

        // 分帧上限：0 没有「不限制」语义，也不能溢出 int，越界在发送前就报。
        if (limits?.MaxStreamFrameLength is int frameLength
            && (frameLength < AxiosConfig.LimitSettings.MinStreamFrameLength
                || frameLength > AxiosConfig.LimitSettings.MaxStreamFrameLengthLimit))
        {
            throw AxiosException.InvalidOption(
                $"MaxStreamFrameLength 超出范围（当前 {frameLength} 字节）：合法区间 "
                + $"{AxiosConfig.LimitSettings.MinStreamFrameLength}–{AxiosConfig.LimitSettings.MaxStreamFrameLengthLimit} 字节（1–64 MB），"
                + $"默认 {AxiosConfig.LimitSettings.DefaultStreamFrameLength} 字节（8 MB）。"
                + "分帧器必须把整帧读进内存才能交给序列化器，因此没有「不限制」语义；更大的正文请用 ResponseType.Stream 自行分块处理。",
                config);
        }

        // 重试策略的取值范围：自定义策略只能校验 MaxRetries，内置实现连等待时间一起校验。
        if (config.RetryOrNull?.Policy is { } retry)
        {
            if (retry.MaxRetries < 0)
            {
                throw AxiosException.InvalidOption($"重试次数 MaxRetries 不能为负（当前 {retry.MaxRetries}）：不重试请设为 0 或不要设置 Retry.Policy。", config);
            }

            if (retry is RetryPolicy policy)
            {
                if (policy.Delay < TimeSpan.Zero)
                {
                    throw AxiosException.InvalidOption($"重试间隔 Delay 不能为负（当前 {policy.Delay}）：要立即重试请设为 TimeSpan.Zero。", config);
                }

                if (policy.MaxDelay < TimeSpan.Zero)
                {
                    throw AxiosException.InvalidOption($"重试间隔上限 MaxDelay 不能为负（当前 {policy.MaxDelay}）：不封顶请设为一个较大的值（上限约 49.7 天）。", config);
                }

                // MaxDelay 是显式配置的上限，超过计时器范围直接报错而不截断。
                if (policy.MaxDelay > Timeouts.Max)
                {
                    throw AxiosException.InvalidOption(
                        $"重试间隔上限 MaxDelay 超出计时器可表示的范围（当前 {policy.MaxDelay}，上限约 49.7 天）："
                        + "重试等待用 Task.Delay 实现，更长的间隔无法表达。请调小 MaxDelay，"
                        + "长时间退避请减少次数并用 CancellationToken 控制退出。",
                        config);
                }
            }
        }

        // 对冲：分支数与起跑延迟的取值范围。上限存在的理由是不让一次请求把下游打满。
        if (config.HedgingOrNull is { } hedging)
        {
            if (hedging.MaxHedges < 0)
            {
                throw AxiosException.InvalidOption(
                    $"对冲分支数 MaxHedges 不能为负（当前 {hedging.MaxHedges}）：不对冲请设为 0。", config);
            }

            if (hedging.MaxHedges > AxiosConfig.HedgingSettings.MaxHedgesLimit)
            {
                throw AxiosException.InvalidOption(
                    $"对冲分支数 MaxHedges 超出上限（当前 {hedging.MaxHedges}，上限 {AxiosConfig.HedgingSettings.MaxHedgesLimit}）："
                    + "再多分支追不回延迟，只会把下游打满。", config);
            }

            if (hedging.Delay < TimeSpan.Zero)
            {
                throw AxiosException.InvalidOption(
                    $"对冲起跑延迟 Delay 不能为负（当前 {hedging.Delay}）：要同时起跑请设为 TimeSpan.Zero。", config);
            }

            if (hedging.Delay > Timeouts.Max)
            {
                throw AxiosException.InvalidOption(
                    $"对冲起跑延迟 Delay 超出计时器可表示的范围（当前 {hedging.Delay}，上限约 49.7 天）："
                    + "它按 Task.Delay 实现，更长的延迟无法表达。", config);
            }
        }

        // 备用端点：必须是绝对 http(s) 地址，且不带 query / fragment —— 否则端点里的这部分会被静默丢掉。
        if (config.FailoverOrNull?.Endpoints is { } endpoints)
        {
            for (var i = 0; i < endpoints.Count; i++)
            {
                if (endpoints[i] is not { IsAbsoluteUri: true } endpoint
                    || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
                {
                    throw AxiosException.InvalidOption(
                        $"备用端点 Failover.Endpoints[{i}] 必须是绝对的 http / https 地址（当前 {endpoints[i]?.ToString() ?? "null"}）："
                        + "它只替换 origin 与路径前缀，请求的 path / query 会原样接上去。", config);
                }

                if (!string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
                {
                    throw AxiosException.InvalidOption(
                        $"备用端点 Failover.Endpoints[{i}] 不能带 query 或 fragment（当前 {endpoint}）："
                        + "端点只替换 origin 与路径前缀，请求自己的 path / query 会原样接上去；"
                        + "要让某个参数只出现在备用端点上，请改用请求拦截器按 ctx 拼 URL。", config);
                }
            }
        }

        // 熔断策略的取值范围：这些值都没有可执行含义，静默取默认值会让人以为配置生效了。
        if (config.CircuitOrNull?.Policy is CircuitBreaker breaker)
        {
            if (breaker.FailureThreshold < 1)
            {
                throw AxiosException.InvalidOption(
                    $"熔断阈值 FailureThreshold 必须 >= 1（当前 {breaker.FailureThreshold}）："
                    + "它表示「连续多少次下游故障后打开熔断」，不熔断请把 Circuit.Policy 设为 null。",
                    config);
            }

            if (breaker.HalfOpenAttempts < 1)
            {
                throw AxiosException.InvalidOption(
                    $"半开探测名额 HalfOpenAttempts 必须 >= 1（当前 {breaker.HalfOpenAttempts}）："
                    + "一个探测都不放行的熔断器永远无法恢复。",
                    config);
            }

            if (breaker.MaxTrackedHosts < 1)
            {
                throw AxiosException.InvalidOption(
                    $"熔断跟踪维度上限 MaxTrackedHosts 必须 >= 1（当前 {breaker.MaxTrackedHosts}）："
                    + "上限为 0 会让每个请求都新建一条状态、记不住任何失败。",
                    config);
            }

            if (breaker.BreakDuration <= TimeSpan.Zero)
            {
                throw AxiosException.InvalidOption(
                    $"熔断冷却时长 BreakDuration 必须 > 0（当前 {breaker.BreakDuration}）："
                    + "零冷却等价于「一打开就立刻探测」，熔断形同虚设；不熔断请把 Circuit.Policy 设为 null。",
                    config);
            }
        }
    }

    /// <summary>解析（必要时懒创建）本实例的并发闸门。信号量容量创建时固定、无法重建，所以「本次生效上限 ≠ 闸门容量」一律报错。未设置时不建闸门；实例已有闸门时按闸门走（<c>null</c> 视为「本次没表态」）。</summary>
    private ConcurrencyGate? ResolveConcurrencyGate(AxiosConfig config)
    {
        var requested = config.LimitsOrNull?.MaxConcurrentRequests;
        var existing = Volatile.Read(ref _gate);

        if (existing is not null)
        {
            if (requested is int value && value != existing.MaxConcurrency)
            {
                throw ConcurrencyMismatch(config, value, existing.MaxConcurrency);
            }

            return existing;
        }

        if (requested is not int limit)
        {
            return null;
        }

        var created = new ConcurrencyGate(limit);

        // 并发首请求：输的一方用赢家的闸门，容量不同则报错（两个线程带着不同上限同时首次请求）
        var winner = Interlocked.CompareExchange(ref _gate, created, null);
        if (winner is null)
        {
            return created;
        }

        if (winner.MaxConcurrency != limit)
        {
            throw ConcurrencyMismatch(config, limit, winner.MaxConcurrency);
        }

        return winner;
    }

    private static AxiosException ConcurrencyMismatch(AxiosConfig config, int requested, int effective)
    {
        return AxiosException.InvalidOption(
            $"MaxConcurrentRequests 是实例级配置（并发闸门在首个受限请求时创建并固定为 {effective}），"
            + $"本次生效配置要求 {requested}。按请求写不同的值不会生效，构造之后改 Defaults 也不会重建闸门；"
            + "要改变上限请新建 AxiosClient 并在 defaults 上设置。",
            config);
    }

    /// <summary>实例级选项（Proxy / Credentials / PoolOptions / WithCredentials / MaxRedirects）在构造池时已固定：按请求复述实例的等价设置放行，写入别的值报错（WithCredentials / MaxRedirects 按值比，其余按引用比）。</summary>
    private void ValidatePerRequestOptions(AxiosConfig config)
    {
        var requested = config.ConnectionOrNull;
        var defaultsConnection = Defaults.ConnectionOrNull;

        if (requested?.Proxy is not null && !ReferenceEquals(requested.Proxy, defaultsConnection?.Proxy))
        {
            throw AxiosException.InvalidOption(
                "Proxy 是实例级配置（连接池在构造 AxiosClient 时创建），请改用 new AxiosClient(new AxiosConfig { Connection = new() { Proxy = ... } }) "
                + "或 ConnectionPoolOptions.Proxy。", config);
        }

        if (requested?.Credentials is not null && !ReferenceEquals(requested.Credentials, defaultsConnection?.Credentials))
        {
            throw AxiosException.InvalidOption("Credentials 是实例级配置，请在 AxiosClient 的 defaults 或 ConnectionPoolOptions 上设置。", config);
        }

        // 池在构造时已经建好，按请求改它只会被静默丢弃，故与 Proxy / MaxRedirects 一致地报错。
        if (requested?.PoolOptions is not null && !ReferenceEquals(requested.PoolOptions, defaultsConnection?.PoolOptions))
        {
            throw AxiosException.InvalidOption(
                "PoolOptions 是实例级配置（连接池在构造 AxiosClient 时创建），"
                + "请通过 AxiosClient 构造参数或 defaults.PoolOptions 设置。", config);
        }

        ValidateWithCredentials(config);
        ValidateMaxRedirects(config);
    }

    /// <summary>
    /// <c>WithCredentials</c> 只允许复述实例生效值；注入自定义 Handler 时只要写就报错。
    /// 生效值取池的 <see cref="ConnectionPoolOptions.UseCookies"/>，并先按 <see cref="AxiosConfig.WithCredentialsSet"/> 早退。
    /// </summary>
    private void ValidateWithCredentials(AxiosConfig config)
    {
        if (_customClient is not null)
        {
            if (config.WithCredentialsSet)
            {
                throw AxiosException.InvalidOption(
                    "注入自定义 handler 时 WithCredentials 完全由 handler 决定（Cookie 容器在连接层），"
                    + "本库无法判断按请求写的值与实际行为是否等价，请在 handler 上配置。", config);
            }

            return;
        }

        // 本次没写这一项直接放行：池已按构造期设置固化。
        if (!config.WithCredentialsSet)
        {
            return;
        }

        var requestedValue = config.ConnectionOrNull?.WithCredentials ?? false;
        var effective = _pool!.EffectiveOptions.UseCookies;

        if (requestedValue == effective)
        {
            return;
        }

        throw AxiosException.InvalidOption(
            $"WithCredentials 是实例级配置（影响 Cookie 容器与系统凭据），按请求只能写与实例相同的值；"
            + $"当前按请求为 {requestedValue}，实例生效值为 {effective}。"
            + "要改变它请新建 AxiosClient 并在 defaults 上设置。", config);
    }

    /// <summary><c>MaxRedirects</c> 只允许写与实例生效值相同的值；生效值取池的实际策略（<c>AllowAutoRedirect = false</c> 时视为 0）。</summary>
    private void ValidateMaxRedirects(AxiosConfig config)
    {
        if (_customClient is not null)
        {
            if (config.MaxRedirectsSet)
            {
                throw AxiosException.InvalidOption(
                    "注入自定义 handler 时 MaxRedirects 由 handler 决定（重定向在连接层处理），"
                    + "本库无法判断按请求写的值与实际行为是否等价，请在 handler 上配置。", config);
            }

            return;
        }

        // 未显式写过直接放行；不能拿属性默认值 5 做相等短路，它与池生效值可能背离。
        if (!config.MaxRedirectsSet)
        {
            return;
        }

        var options = _pool!.EffectiveOptions;
        var effective = options.AllowAutoRedirect ? options.MaxAutomaticRedirections : 0;

        var requestedMaxRedirects = config.ConnectionOrNull?.MaxRedirects ?? 5;

        if (requestedMaxRedirects == effective)
        {
            return;
        }

        throw AxiosException.InvalidOption(
            $"MaxRedirects 是实例级配置（重定向由连接池 Handler 处理），按请求只能写与实例相同的值；"
            + $"当前按请求为 {requestedMaxRedirects}，实例生效值为 {effective}（0 表示不跟随重定向）。"
            + "要改变它请新建 AxiosClient 并在 defaults 或 ConnectionPoolOptions 上设置。", config);
    }
}
