using System.Diagnostics;

namespace AxiosNet.Retry;

/// <summary>
/// 全局重试预算：给任意 <see cref="IAxiosRetryPolicy"/> 套一层共享的令牌桶，限制**整个客户端实例**的重试速率。
/// </summary>
/// <remarks>
/// <para>
/// 解决的场景：熔断没配时，下游故障 / 429 风暴会让每条独立请求各自重试，「重试放大」不受任何约束；
/// 本策略让全实例共享一只桶，桶空了就放弃重试。
/// </para>
/// <para>
/// 只拦「重试」不拦首发：内层策略说重试时才扣令牌，扣不到就放弃重试，把这次失败<b>原样</b>抛给调用方
/// （错误码不变，不会变成「预算耗尽」这类调用方需要额外认识的新错误）。
/// </para>
/// <para>
/// 令牌桶按策略对象共享：挂在 <c>Defaults.Retry.Policy</c> 上，本实例（含声明式客户端）的所有请求共用一只桶；
/// 按请求替换 <c>Retry.Policy</c> 会换成另一只桶，想全实例生效就不要在按请求上覆盖它。
/// </para>
/// <para>
/// 线程安全，每次判定只在锁内做一次时间戳换算、不分配；<see cref="RefusedRetries"/> 与
/// <see cref="OnBudgetExhausted"/> 用于观测（库内不另发指标）。
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // 瞬时最多放行 20 次重试，之后每秒补 5 次；内层策略仍是默认的「3 次、指数退避」
/// client.Defaults.Retry.Policy = new RetryBudgetPolicy(capacity: 20, refillPerSecond: 5);
///
/// // 也可以包住自定义策略：预算只管「放不放行」，可重试判定 / 次数 / 等待时间仍由内层决定
/// client.Defaults.Retry.Policy = new RetryBudgetPolicy(
///     RetryPolicy.Exponential(3, TimeSpan.FromMilliseconds(200)),
///     capacity: 50,
///     refillPerSecond: 10);
/// </code>
/// </example>
public sealed class RetryBudgetPolicy : IAxiosRetryPolicy
{
    private readonly object _sync = new();
    private readonly IAxiosRetryPolicy _inner;
    private readonly int _capacity;
    private readonly double _refillPerSecond;
    private double _tokens;
    private long _lastRefillTimestamp;
    private long _refusedRetries;

    /// <summary>包住默认策略（<see cref="RetryPolicy"/>）：3 次、指数退避，再加一层实例级重试预算。</summary>
    /// <param name="capacity">瞬时容量：桶满时最多连续放行这么多次重试，必须 <c>&gt;= 1</c>。</param>
    /// <param name="refillPerSecond">令牌补充速度（次/秒），必须 <c>&gt;= 0</c>；<c>0</c> 表示不补，桶用完后本实例不再重试。</param>
    public RetryBudgetPolicy(int capacity, double refillPerSecond)
        : this(new RetryPolicy(), capacity, refillPerSecond)
    {
    }

    /// <summary>包住指定策略：可重试判定、次数上限与等待时间都由 <paramref name="inner"/> 决定。</summary>
    /// <param name="inner">内层策略，通常是 <see cref="RetryPolicy"/> 或自定义 <see cref="IAxiosRetryPolicy"/>。</param>
    /// <param name="capacity">瞬时容量：桶满时最多连续放行这么多次重试，必须 <c>&gt;= 1</c>。</param>
    /// <param name="refillPerSecond">令牌补充速度（次/秒），必须 <c>&gt;= 0</c>；<c>0</c> 表示不补，桶用完后本实例不再重试。</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> 为 <c>null</c>。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> 小于 1，或 <paramref name="refillPerSecond"/> 为负 / 非有限值。</exception>
    public RetryBudgetPolicy(IAxiosRetryPolicy inner, int capacity, double refillPerSecond)
    {
        ArgumentNullException.ThrowIfNull(inner);

        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                capacity,
                "重试预算容量必须 >= 1：一次重试都不放行请直接不设置 Retry.Policy（或把 MaxRetries 设为 0）。");
        }

        if (!double.IsFinite(refillPerSecond) || refillPerSecond < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(refillPerSecond),
                refillPerSecond,
                "令牌补充速度必须 >= 0 的有限值：不补充请设为 0。");
        }

        _inner = inner;
        _capacity = capacity;
        _refillPerSecond = refillPerSecond;

        // 桶一开始是满的：全实例第一波重试优先放行，用完之后按补充速度恢复。
        _tokens = capacity;
        _lastRefillTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>内层策略：次数上限、可重试判定与等待时间都由它决定。</summary>
    public IAxiosRetryPolicy Inner => _inner;

    /// <summary>瞬时容量：桶满时最多连续放行这么多次重试。</summary>
    public int Capacity => _capacity;

    /// <summary>令牌补充速度（次/秒）：<c>0</c> 表示桶用完后不再补充。</summary>
    public double RefillPerSecond => _refillPerSecond;

    /// <summary>因预算不足被拒掉的重试次数（累计，线程安全）——用来接指标或告警；库内不另发指标。</summary>
    public long RefusedRetries => Interlocked.Read(ref _refusedRetries);

    /// <summary>当前可用令牌数（读取时按经过时间补足；只用于诊断与测试，不要据此做业务判断）。</summary>
    public double AvailableTokens
    {
        get
        {
            lock (_sync)
            {
                Refill(Stopwatch.GetTimestamp());
                return _tokens;
            }
        }
    }

    /// <summary>预算不足、某次重试被拒时触发：入参是那次失败的尝试上下文（与 <c>Retry.OnRetry</c> 同形）。</summary>
    /// <remarks>
    /// 在请求线程上同步执行且不在锁内，抛异常会中断本次请求（该错误原样冒给调用方）——耗时或可能抛异常的逻辑请自行包住。
    /// </remarks>
    public Action<AxiosRetryContext>? OnBudgetExhausted { get; set; }

    /// <inheritdoc />
    public int MaxRetries => _inner.MaxRetries;

    /// <inheritdoc />
    public bool ShouldRetry(AxiosRetryContext context)
    {
        // 先问内层：不可重试的失败（4xx、非幂等方法等）不该消耗预算。
        if (!_inner.ShouldRetry(context))
        {
            return false;
        }

        if (TryTake())
        {
            return true;
        }

        Interlocked.Increment(ref _refusedRetries);

        // 锁外回调：调用方的代码不该在内部锁里跑。
        OnBudgetExhausted?.Invoke(context);
        return false;
    }

    /// <inheritdoc />
    public TimeSpan GetDelay(AxiosRetryContext context) => _inner.GetDelay(context);

    private bool TryTake()
    {
        lock (_sync)
        {
            Refill(Stopwatch.GetTimestamp());

            if (_tokens < 1d)
            {
                return false;
            }

            _tokens -= 1d;
            return true;
        }
    }

    /// <summary>按距上次结算的时间补令牌，封顶 <see cref="Capacity"/>（调用方持有 <see cref="_sync"/>）。</summary>
    private void Refill(long now)
    {
        var elapsedSeconds = (now - _lastRefillTimestamp) / (double)Stopwatch.Frequency;
        _lastRefillTimestamp = now;

        if (elapsedSeconds <= 0 || _refillPerSecond <= 0)
        {
            return;
        }

        var tokens = _tokens + (elapsedSeconds * _refillPerSecond);
        _tokens = tokens > _capacity ? _capacity : tokens;
    }
}
