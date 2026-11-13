using AxiosNet.Circuit;
using AxiosNet.Internal;
using AxiosNet.Pipeline;
using AxiosNet.Pooling;
using AxiosNet.Retry;

namespace AxiosNet;

// AxiosClient 的构造期与按请求校验：连接级选项、限制范围、并发闸门、HTTP/3。
public sealed partial class AxiosClient
{
    /// <summary>连接级选项无法按 defaults 生效时的构造期校验（注入 handler / 共享池共用）。</summary>
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

        if (defaults.TransportOrNull?.Proxy is not null || defaults.TransportOrNull?.Credentials is not null)
        {
            throw AxiosException.InvalidOption(messages.ProxyCredentials, config);
        }
    }

    /// <summary>两种连接级场景的报错文案。</summary>
    /// <param name="MaxRedirects">重定向次数不被接受时的文案。</param>
    /// <param name="WithCredentials">凭证 / Cookie 不被接受时的文案。</param>
    /// <param name="ProxyCredentials">代理及其凭证不被接受时的文案。</param>
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
    /// <c>RequestVersionOrLower</c> 的降级是正常路径；判据是本机能力 <see cref="Http3Support"/>。
    /// netstandard2.0 兼容资产没有 h2 / h3 能力：只要 <c>version.Major &gt; 1</c> 就无条件拒绝，不看 policy。
    /// </summary>
    internal static void ValidateHttp3Request(Uri uri, Version version, HttpVersionPolicy policy, AxiosConfig config)
    {
#if NETSTANDARD2_0
        // 兼容资产只有 HttpClientHandler：HTTP/2 / HTTP/3 都发不出去。在这里明确拒绝，
        // 而不是让 Framework 静默按 1.1 发出、让调用方以为自己用了高版本协议。
        if (version.Major > 1)
        {
            throw AxiosException.InvalidOption(
                $"netstandard2.0 兼容资产只支持 HTTP/1.1，无法按 HTTP/{version} 发送请求："
                + "请让消费方运行 .NET 6+ 以取到 net6.0 / net8.0 资产（NuGet 自动选择），"
                + "或把 Transport.HttpVersion 设为 1.1。",
                config);
        }

        _ = uri;
        _ = policy;
        return;
#else
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
#endif
    }

    /// <summary>限制类配置的取值范围校验：负值一律报错；<c>Timeout=0</c> 不超时，<c>MaxRate=0</c> 不限速。</summary>
    private static void ValidateRangeConfig(AxiosConfig config)
    {
        if (config.Timeout < TimeSpan.Zero)
        {
            throw AxiosException.InvalidOption($"Timeout 不能为负（当前 {config.Timeout}）：要关闭超时请显式设为 TimeSpan.Zero。", config);
        }

        // 总预算与 Timeout 同一口径：0 与 null 都表示不设。
        if (config.TotalTimeoutValue is { } totalTimeout && totalTimeout < TimeSpan.Zero)
        {
            throw AxiosException.InvalidOption(
                $"TotalTimeout 不能为负（当前 {totalTimeout}）：不设总预算请设为 null（或 TimeSpan.Zero），"
                + "要限制整条请求（含重试 / 端点 / 对冲）请给一个正值。",
                config);
        }

        // 请求体压缩：枚举必须已定义；压缩与手写 Content-Encoding 只能二选一。
        if (config.Compression is { } compression)
        {
            if (!Enum.IsDefined(typeof(AxiosCompression), compression))
            {
                throw AxiosException.InvalidOption(
                    $"Compression 取值 {(int)compression} 未定义：可选 AxiosCompression.None / Gzip / Deflate / Brotli。", config);
            }

#if NETSTANDARD2_0
            // 兼容资产没有 ZLibStream / BrotliStream，只能用 gzip 压缩器。在选项校验阶段就用统一错误码拒绝，
            // 而不是等请求体构建到一半再抛 PlatformNotSupportedException 绕过 AxiosException 体系。
            if (compression is AxiosCompression.Deflate or AxiosCompression.Brotli)
            {
                throw AxiosException.InvalidOption(
                    "netstandard2.0 兼容资产只支持 gzip 请求体压缩：该运行时没有 ZLibStream / BrotliStream，"
                    + "请改用 AxiosCompression.Gzip 或关闭 Compression。", config);
            }
#endif

            if (compression != AxiosCompression.None && config.HasHeaders && config.Headers.Has("Content-Encoding"))
            {
                throw AxiosException.InvalidOption(
                    "已开启 Compression 时不能再手写 Content-Encoding 头：压缩算法会按 Compression 写入该头，"
                    + "两者同时存在会互相覆盖。自行压缩正文请去掉 Compression，并传入预压缩的字节 / 流。", config);
            }
        }

        var limits = config.LimitsOrNull;
        var maxRate = config.TransportOrNull?.MaxRate;

        if (maxRate is < 0)
        {
            throw AxiosException.InvalidOption($"Transport.MaxRate 不能为负（当前 {maxRate}）：要不限速请设为 null（0 等价于不限速）。", config);
        }

        if (limits?.MaxResponseBytes is < 0)
        {
            throw AxiosException.InvalidOption($"Limits.MaxResponseBytes 不能为负（当前 {limits.MaxResponseBytes}）：不限制请设为 null。", config);
        }

        if (limits?.MaxRequestBytes is < 0)
        {
            throw AxiosException.InvalidOption($"Limits.MaxRequestBytes 不能为负（当前 {limits.MaxRequestBytes}）：不限制请设为 null。", config);
        }

        // 并发上限：0 与负数都没有语义，一律报错。
        if (config.ResilienceOrNull?.Concurrency.MaxInFlight is int maxConcurrency && maxConcurrency < 1)
        {
            throw AxiosException.InvalidOption(
                $"Resilience.Concurrency.MaxInFlight 必须是正整数（当前 {maxConcurrency}）：不限制请设为 null，"
                + "要「不排队」请把 QueueTimeout 设为 TimeSpan.Zero。", config);
        }

        // 排队深度上限：0 与负数都没有语义（「不排队」由 QueueTimeout = Zero 表达）。
        if (config.ResilienceOrNull?.Concurrency.MaxQueue is int maxQueue && maxQueue < 1)
        {
            throw AxiosException.InvalidOption(
                $"Resilience.Concurrency.MaxQueue 必须是正整数（当前 {maxQueue}）：不限制排队深度请设为 null，"
                + "要「不排队」请把 QueueTimeout 设为 TimeSpan.Zero。", config);
        }

        if (config.ResilienceOrNull?.Concurrency.QueueTimeout is { } queueTimeout && queueTimeout < TimeSpan.Zero)
        {
            throw AxiosException.InvalidOption($"Resilience.Concurrency.QueueTimeout 不能为负（当前 {queueTimeout}）：要不排队请设为 TimeSpan.Zero，要一直等请设为 null。", config);
        }

        // 分帧上限：0 没有「不限制」语义，越界在发送前就报。
        if (limits?.MaxStreamFrameBytes is int frameLength
            && (frameLength < AxiosConfig.MinStreamFrameBytes
                || frameLength > AxiosConfig.MaxStreamFrameBytesLimit))
        {
            throw AxiosException.InvalidOption(
                $"Limits.MaxStreamFrameBytes 超出范围（当前 {frameLength} 字节）：合法区间 "
                + $"{AxiosConfig.MinStreamFrameBytes}–{AxiosConfig.MaxStreamFrameBytesLimit} 字节（1–64 MB），"
                + $"默认 {AxiosConfig.DefaultStreamFrameBytes} 字节（8 MB）。"
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

                // MaxDelay 超过计时器范围直接报错，不截断。
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

        // 对冲：分支数与起跑延迟的取值范围。
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

            // 第 n 条分支起跑于 n × Delay，越过计时器上限的分支等于永不发出，必须提前报错。
            if (hedging.MaxHedges > 0 && hedging.Delay.Ticks > Timeouts.Max.Ticks / hedging.MaxHedges)
            {
                throw AxiosException.InvalidOption(
                    $"对冲起跑延迟 Delay × MaxHedges 超出计时器上限（当前 {hedging.Delay} × {hedging.MaxHedges}，上限约 49.7 天）："
                    + "第 n 条分支起跑于 n × Delay，超出的部分会被收敛到上限，等于这条分支永不发出。"
                    + "请减小 Delay 或 MaxHedges。", config);
            }
        }

        // 备用端点：必须是绝对 http(s) 地址，且不带 query / fragment。
        if (config.FailoverOrNull?.Endpoints is { } endpoints)
        {
            for (var i = 0; i < endpoints.Count; i++)
            {
                if (endpoints[i] is not { IsAbsoluteUri: true } endpoint
                    || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
                {
                    throw AxiosException.InvalidOption(
                        $"备用端点 Failover.Endpoints[{i}] 必须是绝对的 http / https 地址（当前 {endpoints[i]?.ToString() ?? "null"}）："
                        + "它提供 origin 与路径前缀，请求的 path / query 会原样接在后面。", config);
                }

                if (!string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
                {
                    throw AxiosException.InvalidOption(
                        $"备用端点 Failover.Endpoints[{i}] 不能带 query 或 fragment（当前 {endpoint}）："
                        + "端点提供 origin 与路径前缀，请求自己的 path / query 会原样接在后面；"
                        + "要让某个参数只出现在备用端点上，请改用请求拦截器按 ctx 拼 URL。", config);
                }

                // 重复端点会让同一份请求对同一地址重发。比较 origin + 路径前缀（去尾斜杠），
                // scheme / host 已由 Uri 规范化为小写，路径按大小写敏感比较。
                var baseUrl = i == 0 ? null : endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/');
                for (var j = 0; j < i; j++)
                {
                    var otherBase = endpoints[j].GetLeftPart(UriPartial.Path).TrimEnd('/');
                    if (string.Equals(otherBase, baseUrl, StringComparison.Ordinal))
                    {
                        throw AxiosException.InvalidOption(
                            $"备用端点 Failover.Endpoints[{i}] 与 Endpoints[{j}] 重复（当前 {endpoint}）："
                            + "同一个地址会被重发两次，只会放大下游压力。", config);
                    }
                }
            }
        }

        // 备用端点健康记忆的取值范围。
        if (config.FailoverOrNull is { } failoverHealth)
        {
            if (failoverHealth.FailureThreshold < 0)
            {
                throw AxiosException.InvalidOption(
                    $"备用端点健康阈值 Failover.FailureThreshold 不能为负（当前 {failoverHealth.FailureThreshold}）："
                    + "0 表示关闭健康记忆，不记忆请设为 0 或把 Cooldown 设为 TimeSpan.Zero。", config);
            }

            if (failoverHealth.Cooldown < TimeSpan.Zero || failoverHealth.MaxCooldown < TimeSpan.Zero)
            {
                throw AxiosException.InvalidOption(
                    $"备用端点冷却时长不能为负（Cooldown = {failoverHealth.Cooldown}，MaxCooldown = {failoverHealth.MaxCooldown}）："
                    + "关闭健康记忆请把 Cooldown 设为 TimeSpan.Zero。", config);
            }
        }

        // 熔断策略的取值范围。
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

            if (breaker.ProbeTimeout is { } probeTimeout && probeTimeout <= TimeSpan.Zero)
            {
                throw AxiosException.InvalidOption(
                    $"半开探测超时 ProbeTimeout 必须 > 0（当前 {probeTimeout}）："
                    + "不想要看门狗请设为 null，否则探测刚放行就会被判定为卡死。",
                    config);
            }

            if (breaker.WindowSize < 0)
            {
                throw AxiosException.InvalidOption(
                    $"熔断窗口容量 WindowSize 不能为负（当前 {breaker.WindowSize}）："
                    + "关掉失败率判定、只按连续失败熔断请设为 0。",
                    config);
            }

            if (breaker.WindowSize > 0)
            {
                // 下限大于窗口时这条规则永远不触发，必须报错。
                if (breaker.MinimumSamples < 1 || breaker.MinimumSamples > breaker.WindowSize)
                {
                    throw AxiosException.InvalidOption(
                        $"熔断窗口样本下限 MinimumSamples 必须落在 1..WindowSize 之间"
                        + $"（当前 MinimumSamples = {breaker.MinimumSamples}，WindowSize = {breaker.WindowSize}）："
                        + "样本永远攒不够的话，失败率这条规则等于没开。",
                        config);
                }

                // NaN 由区间判断一并挡掉。
                if (!(breaker.FailureRateThreshold > 0 && breaker.FailureRateThreshold <= 1))
                {
                    throw AxiosException.InvalidOption(
                        $"熔断失败率阈值 FailureRateThreshold 必须落在 (0, 1]（当前 {breaker.FailureRateThreshold}）："
                        + "0.5 表示窗口内一半失败即打开；不想要这条规则请把 WindowSize 设为 0。",
                        config);
                }
            }

            if (breaker.RecoverySuccesses < 0)
            {
                throw AxiosException.InvalidOption(
                    $"慢启动恢复次数 RecoverySuccesses 不能为负（当前 {breaker.RecoverySuccesses}）："
                    + "不要慢启动（探测一次成功即闭合）请设为 0 或 1。",
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

    /// <summary>
    /// 解析本实例的备用端点健康表：只有「配有端点 + 健康记忆开启」时才需要，
    /// 首次用到时创建（CAS 保证并发下只有一份，避免各请求各记一套）。
    /// </summary>
    private FailoverHealth? ResolveFailoverHealth(AxiosConfig config)
    {
        if (config.FailoverOrNull is not { Endpoints.Count: > 0 } failover
            || failover.FailureThreshold <= 0
            || failover.Cooldown <= TimeSpan.Zero)
        {
            return null;
        }

        var existing = Volatile.Read(ref _failoverHealth);
        if (existing is not null)
        {
            return existing;
        }

        var created = new FailoverHealth();
        return Interlocked.CompareExchange(ref _failoverHealth, created, null) ?? created;
    }

    /// <summary>解析（必要时懒创建）本实例的并发闸门：容量在创建时固定，本次生效上限与已建闸门不一致就报错；本次没要求上限时沿用已有闸门（没有则不建），排队上限只有显式写过（*Set）才参与比较。</summary>
    private ConcurrencyGate? ResolveConcurrencyGate(AxiosConfig config)
    {
        var concurrency = config.ResilienceOrNull?.Concurrency;
        var requested = concurrency?.MaxInFlight;
        var requestedQueue = concurrency?.MaxQueue;

        // 排队上限按 *Set 判断，不看值。
        var queueStated = concurrency?.MaxQueueSet ?? false;
        var existing = Volatile.Read(ref _gate);

        if (existing is not null)
        {
            if (requested is int value && value != existing.MaxConcurrency)
            {
                throw ConcurrencyMismatch(config, value, existing.MaxConcurrency);
            }

            if (queueStated && requestedQueue != existing.MaxQueue)
            {
                throw QueueMismatch(config, requestedQueue, existing.MaxQueue);
            }

            return existing;
        }

        if (requested is not int limit)
        {
            return null;
        }

        var created = new ConcurrencyGate(limit, requestedQueue);

        // 并发首请求：输的一方用赢家的闸门，容量不同则报错。
        var winner = Interlocked.CompareExchange(ref _gate, created, null);
        if (winner is null)
        {
            return created;
        }

        if (winner.MaxConcurrency != limit)
        {
            throw ConcurrencyMismatch(config, limit, winner.MaxConcurrency);
        }

        if (queueStated && winner.MaxQueue != requestedQueue)
        {
            throw QueueMismatch(config, requestedQueue, winner.MaxQueue);
        }

        return winner;
    }

    /// <summary>并发上限与已建闸门不一致时的报错：讲清这是实例级配置、按请求写不会生效。</summary>
    private static AxiosException ConcurrencyMismatch(AxiosConfig config, int requested, int effective)
    {
        return AxiosException.InvalidOption(
            $"Resilience.Concurrency.MaxInFlight 是实例级配置（并发闸门在首个受限请求时创建并固定为 {effective}），"
            + $"本次生效配置要求 {requested}。按请求写不同的值不会生效，构造之后改 Defaults 也不会重建闸门；"
            + "要改变上限请新建 AxiosClient 并在 defaults 上设置。",
            config);
    }

    /// <summary>排队上限与已建闸门不一致时的报错（null 在消息里显示为「不限」）。</summary>
    private static AxiosException QueueMismatch(AxiosConfig config, int? requested, int? effective)
    {
        static string Text(int? value) => value is int v ? v.ToString() : "null（不限）";

        return AxiosException.InvalidOption(
            $"Resilience.Concurrency.MaxQueue 是实例级配置（排队上限与并发闸门一起在首个受限请求时创建并固定为 {Text(effective)}），"
            + $"本次生效配置要求 {Text(requested)}。按请求写不同的值不会生效，构造之后改 Defaults 也不会重建闸门；"
            + "要改变排队上限请新建 AxiosClient 并在 defaults 上设置。",
            config);
    }

    /// <summary>实例级选项（Proxy / Credentials / PoolOptions / WithCredentials / MaxRedirects）在构造池时已固定：按请求复述实例的等价设置放行，写入别的值报错（WithCredentials / MaxRedirects 按值比，其余按引用比）。</summary>
    private void ValidatePerRequestOptions(AxiosConfig config)
    {
        var requested = config.TransportOrNull;
        var defaultsTransport = Defaults.TransportOrNull;

        if (requested?.Proxy is not null && !ReferenceEquals(requested.Proxy, defaultsTransport?.Proxy))
        {
            throw AxiosException.InvalidOption(
                "Proxy 是实例级配置（连接池在构造 AxiosClient 时创建），请改用 new AxiosClient(new AxiosConfig { Transport = new() { Proxy = ... } }) "
                + "或 ConnectionPoolOptions.Proxy。", config);
        }

        if (requested?.Credentials is not null && !ReferenceEquals(requested.Credentials, defaultsTransport?.Credentials))
        {
            throw AxiosException.InvalidOption("Credentials 是实例级配置，请在 AxiosClient 的 defaults 或 ConnectionPoolOptions 上设置。", config);
        }

        // 池在构造时已建好，按请求改它一律报错。
        if (requested?.Pool is not null && !ReferenceEquals(requested.Pool, defaultsTransport?.Pool))
        {
            throw AxiosException.InvalidOption(
                "Pool 是实例级配置（连接池在构造 AxiosClient 时创建），"
                + "请通过 AxiosClient 构造参数或 defaults.Transport.Pool 设置。", config);
        }

        ValidateWithCredentials(config);
        ValidateMaxRedirects(config);
        ValidateProfileTransport(config);
        ValidateCredentialConflict(config);
    }

    /// <summary>凭据来源与显式 <c>Authorization</c> 头同时设置属于配置冲突：凭据只有一个来源，不做二选一静默压制。</summary>
    private static void ValidateCredentialConflict(AxiosConfig config)
    {
        var hasSource = config.TransportOrNull?.Auth is not null || config.InternalCredentialProvider is not null;
        if (!hasSource || !HasAuthorizationHeader(config))
        {
            return;
        }

        throw AxiosException.InvalidOption(
            "显式 Authorization 头与凭据来源（Transport.Auth / 凭据提供方）同时设置：凭据只能有一个来源。"
            + "保留其中一个：要自己写头就去掉凭据来源，要用凭据来源就删掉显式 Authorization 头。",
            config);
    }

    private static bool HasAuthorizationHeader(AxiosConfig config)
        => config.HasHeaders && config.Headers.Has(AuthorizationHeader.Name);

    /// <summary>
    /// 命名档与默认档的出口设置互斥：选中命名档后实际 Handler 只取该档的 Proxy / Credentials，
    /// 默认档上的同类设置不可能生效，故直接报错而不是静默忽略。注入自定义 Handler 时没有默认档，交给发送终端报错。
    /// </summary>
    private void ValidateProfileTransport(AxiosConfig config)
    {
        if (_customClient is not null)
        {
            return;
        }

        var profile = config.ProfileOrNull;
        if (string.IsNullOrEmpty(profile))
        {
            return;
        }

        var options = _pool!.EffectiveOptions;
        if (options.Proxy is null && options.Credentials is null)
        {
            return;
        }

        throw AxiosException.InvalidOption(
            $"选中命名传输档 '{profile}' 后，实际连接只使用该档的出口设置，默认档的 Proxy / Credentials 不会生效。"
            + "请把出口设置配置到该档（ConnectionPoolOptions.AddTransport），或去掉 Profile 使用默认档。", config);
    }

    /// <summary>
    /// <c>WithCredentials</c> 只允许复述实例生效值；注入自定义 Handler 时只要写就报错。
    /// 生效值取池的 <see cref="ConnectionPoolOptions.UseCookies"/>。
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

        // 本次没写这一项直接放行。
        if (!config.WithCredentialsSet)
        {
            return;
        }

        var requestedValue = config.TransportOrNull?.WithCredentials ?? false;

        // 生效值按本次请求选中的传输档判：每个档各有自己的 Cookie 容器（命名档不继承默认档的 WithCredentials）。
        var effective = _pool!.GetCookies(config.ProfileOrNull) is not null;

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

        // 未显式写过直接放行。
        if (!config.MaxRedirectsSet)
        {
            return;
        }

        var options = _pool!.EffectiveOptions;
        var effective = options.AllowAutoRedirect ? options.MaxAutomaticRedirections : 0;

        var requestedMaxRedirects = config.TransportOrNull?.MaxRedirects ?? AxiosConfig.DefaultMaxRedirects;

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
