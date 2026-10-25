using System.Globalization;

namespace AxiosNet.Retry;

/// <summary>
/// <see cref="IAxiosRetryPolicy"/> 的默认实现：按「次数 + 间隔」重试，可选指数退避、抖动与
/// <c>Retry-After</c> 优先。
/// <para>
/// 默认只重试<b>幂等方法</b>（GET / HEAD / OPTIONS / TRACE / PUT / DELETE）上的
/// <b>网络错误、超时、408、429 与 5xx</b>；POST / PATCH 默认不重试——服务端可能已经执行过，
/// 重发会产生重复副作用，需要时显式打开 <see cref="RetryNonIdempotentMethods"/>。
/// </para>
/// <para>取消（<c>ERR_CANCELED</c>）、4xx（除 408 / 429）、URL / 配置类错误一律不重试。</para>
/// </summary>
/// <example>
/// <code>
/// // 固定间隔重试 3 次（每次间隔 1 秒，不翻倍、不加抖动）
/// config.Retry.Policy = RetryPolicy.Constant(3, TimeSpan.FromSeconds(1));
///
/// // 指数退避：200ms / 400ms / 800ms，单次最长 5 秒
/// config.Retry.Policy = RetryPolicy.Exponential(3, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5));
///
/// // 细粒度控制
/// config.Retry.Policy = new RetryPolicy
/// {
///     MaxRetries = 5,
///     Delay = TimeSpan.FromMilliseconds(100),
///     BackoffFactor = 1.5,
///     MaxDelay = TimeSpan.FromSeconds(10),
///     Jitter = true,
///     RetryNonIdempotentMethods = true,
///     RetryStatusCodes = status =&gt; status is 429 or &gt;= 500,
///     RetryWhen = ctx =&gt; ctx.ErrorCode != AxiosException.ErrBadOptionValue,
/// };
/// </code>
/// </example>
public sealed class RetryPolicy : IAxiosRetryPolicy
{
    /// <summary>默认策略：3 次、200ms 起、每次翻倍、带抖动、只重试幂等方法。</summary>
    public RetryPolicy()
    {
    }

    /// <summary>
    /// 按次数与固定间隔构造：给了 <paramref name="delay"/> 就<b>不翻倍、不加抖动</b>（等价于 <see cref="Constant"/>），
    /// 不给则沿用默认的指数退避（200ms 起、每次翻倍、带抖动）。
    /// </summary>
    /// <param name="maxRetries">最大重试次数（不含首次请求），0 表示不重试。</param>
    /// <param name="delay">每次重试前的固定等待时间；<c>null</c> 用默认值 200ms 并按默认退避翻倍。</param>
    public RetryPolicy(int maxRetries, TimeSpan? delay = null)
    {
        MaxRetries = maxRetries;

        if (delay is { } value)
        {
            Delay = value;
            BackoffFactor = 1d;
            Jitter = false;
        }
    }

    /// <summary>最大重试次数（不含首次请求），默认 3。</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>首次重试前的等待时间，默认 200ms。</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// 退避倍数：第 n 次重试的等待时间是 <c>Delay * BackoffFactor^(n-1)</c>，默认 2（翻倍）。
    /// <para><c>1</c> 表示固定间隔；小于 1 表示逐次缩短；<c>0</c> 或负值按 1 处理（不缩也不放）。</para>
    /// </summary>
    public double BackoffFactor { get; set; } = 2d;

    /// <summary>
    /// 单次等待的上限，默认 30 秒（指数退避跑久了的封顶）。
    /// <para>
    /// 必须落在计时器可表示的范围内（约 49.7 天）：等待用 <c>Task.Delay</c> 实现，超出会在请求发出前
    /// 报 <c>ERR_BAD_OPTION_VALUE</c>，而不是等到真的重试时才抛计时器异常。
    /// </para>
    /// </summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 是否给等待时间加随机抖动（在 50%–100% 之间取值），默认 <c>true</c>：
    /// 避免大量客户端在同一时刻一起重试（重试风暴）。
    /// </summary>
    public bool Jitter { get; set; } = true;

    /// <summary>
    /// 是否优先采用服务端返回的 <c>Retry-After</c> 头（秒数或 HTTP 日期），默认 <c>true</c>。
    /// <para>服务端明确说了「多久之后再来」时以它为准（仍受 <see cref="MaxDelay"/> 封顶）；解析失败则回到本地退避。</para>
    /// </summary>
    public bool HonorRetryAfter { get; set; } = true;

    /// <summary>
    /// 是否也重试 POST / PATCH 等非幂等方法，默认 <c>false</c>。
    /// <para>
    /// 打开前请确认接口幂等（或有服务端去重）：请求可能已经到达服务端并被执行，
    /// 重发会产生重复下单 / 重复扣款这类副作用。
    /// </para>
    /// </summary>
    public bool RetryNonIdempotentMethods { get; set; }

    private Func<int, bool> _retryStatusCodes = DefaultRetryStatus;

    /// <summary>触发重试的状态码判定，默认 408 / 429 / 5xx。</summary>
    /// <exception cref="ArgumentNullException">赋值为 <c>null</c>：判定是必需项，赋空只会在重试判定处炸成裸空引用。</exception>
    public Func<int, bool> RetryStatusCodes
    {
        get => _retryStatusCodes;
        set
        {
            // 非 null 引用类型：赋 null 是编程错误。等到 ShouldRetry 里再 NRE 更糟 ——
            // 那是在 catch 块内，会把本次尝试的原始失败一起掩盖掉。
            ArgumentNullException.ThrowIfNull(value);
            _retryStatusCodes = value;
        }
    }

    /// <summary>
    /// 附加条件：返回 <c>false</c> 时不重试（在内置判定之上再做一层收窄，比如「只在特定错误码上重试」）。
    /// </summary>
    public Func<AxiosRetryContext, bool>? RetryWhen { get; set; }

    /// <summary>默认的状态码判定：408（请求超时）、429（限流）与全部 5xx。</summary>
    public static Func<int, bool> DefaultRetryStatus { get; } = static status => status is 408 or 429 or (>= 500 and < 600);

    /// <summary>固定间隔策略：每次重试都等 <paramref name="interval"/>，不翻倍、不加抖动。</summary>
    public static RetryPolicy Constant(int maxRetries, TimeSpan interval)
        => new(maxRetries, interval);

    /// <summary>指数退避策略：<paramref name="baseDelay"/> 起每次翻倍，单次不超过 <paramref name="maxDelay"/>。</summary>
    public static RetryPolicy Exponential(int maxRetries, TimeSpan baseDelay, TimeSpan? maxDelay = null)
    {
        var policy = new RetryPolicy(maxRetries, baseDelay)
        {
            BackoffFactor = 2d,
            Jitter = true
        };

        if (maxDelay is { } cap)
        {
            policy.MaxDelay = cap;
        }

        return policy;
    }

    /// <inheritdoc />
    public bool ShouldRetry(AxiosRetryContext context)
    {
        if (context.Attempt > MaxRetries)
        {
            return false;
        }

        if (RetryWhen is { } extra && !extra(context))
        {
            return false;
        }

        if (!RetryNonIdempotentMethods
            && context.Config is { } config
            && !IsIdempotent(config.Method))
        {
            return false;
        }

        // 有响应：按状态码判定（408 / 429 / 5xx）。有响应但状态码不在范围内（4xx）说明是请求本身的问题，
        // 重发同样的请求只会再错一次。
        if (context.StatusCode is int status)
        {
            return RetryStatusCodes(status);
        }

        // 没有响应：网络层失败或超时，重发有意义
        return context.Exception switch
        {
            AxiosException { Code: AxiosException.ErrNetwork or AxiosException.ErrConnAborted } => true,
            HttpRequestException => true,
            IOException => true,
            _ => false
        };
    }

    /// <inheritdoc />
    public TimeSpan GetDelay(AxiosRetryContext context)
    {
        if (HonorRetryAfter && TryGetRetryAfter(context.Response, out var serverDelay))
        {
            return Clamp(serverDelay);
        }

        var factor = BackoffFactor > 0 ? Math.Pow(BackoffFactor, context.Attempt - 1) : 1d;
        var milliseconds = Delay.TotalMilliseconds * factor;

        if (!double.IsFinite(milliseconds))
        {
            return Clamp(MaxDelay);
        }

        if (Jitter && milliseconds > 0)
        {
            // 50%–100% 抖动：保留退避的单调趋势，同时打散同时重试的客户端
            milliseconds *= 0.5d + (Random.Shared.NextDouble() * 0.5d);
        }

        return Clamp(TimeSpan.FromMilliseconds(milliseconds));
    }

    /// <summary>把等待时间收敛到 <c>[0, MaxDelay]</c>。</summary>
    private TimeSpan Clamp(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return value > MaxDelay ? MaxDelay : value;
    }

    /// <summary>
    /// 服务端 <c>Retry-After</c>：既可能是秒数（<c>120</c>），也可能是 HTTP 日期（<c>Wed, 21 Oct 2015 07:28:00 GMT</c>）。
    /// </summary>
    private static bool TryGetRetryAfter(AxiosResponse? response, out TimeSpan delay)
    {
        delay = default;

        var raw = response?.GetHeader("Retry-After");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        raw = raw.Trim();

        if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            delay = TimeSpan.FromSeconds(seconds);
            return true;
        }

        if (DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var when))
        {
            delay = when - DateTimeOffset.UtcNow;
            if (delay < TimeSpan.Zero)
            {
                delay = TimeSpan.Zero;
            }

            return true;
        }

        return false;
    }

    /// <summary>幂等方法：重发不会改变服务端状态（或结果与发一次相同）。</summary>
    private static bool IsIdempotent(HttpMethod method)
        => method == HttpMethod.Get
            || method == HttpMethod.Head
            || method == HttpMethod.Options
            || method == HttpMethod.Trace
            || method == HttpMethod.Put
            || method == HttpMethod.Delete;
}
