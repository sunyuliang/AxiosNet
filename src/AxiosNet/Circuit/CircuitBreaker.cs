using System.Collections.Concurrent;
using System.Diagnostics;

namespace AxiosNet.Circuit;

/// <summary>
/// <see cref="IAxiosCircuitPolicy"/> 的默认实现：按 host 维度的失败计数 + 半开探测 + 自动恢复。
/// 状态机 Closed →（失败达 <see cref="FailureThreshold"/>）→ Open →（冷却 <see cref="BreakDuration"/>）→ HalfOpen → 成功闭合 / 失败重开；维度默认 <c>uri.Authority</c>，可用 <see cref="KeySelector"/> 改写。
/// </summary>
/// <example>
/// <code>
/// // 5 次连续失败后熔断 30 秒；冷却结束只放 1 个探测请求
/// var breaker = new CircuitBreaker
/// {
///     FailureThreshold = 5,
///     BreakDuration = TimeSpan.FromSeconds(30),
///     HalfOpenAttempts = 1
/// };
///
/// breaker.StateChanged += change =&gt; logger.LogWarning(
///     "{Host} 熔断状态：{From} -&gt; {To}", change.Host, change.Previous, change.Current);
///
/// var client = Axios.Create(c =&gt; c.Circuit.Policy = breaker);
///
/// // 运维手工恢复：清掉所有维度的账（下次请求重新开始计数）
/// breaker.Reset();
/// </code>
/// </example>
public sealed class CircuitBreaker : IAxiosCircuitPolicy
{
    private readonly ConcurrentDictionary<string, HostState> _hosts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>默认配置：连续 5 次失败后熔断 30 秒，半开态允许 1 个探测请求。</summary>
    public CircuitBreaker()
    {
    }

    /// <summary>按阈值与冷却时长构造（其余取默认值）。</summary>
    /// <param name="failureThreshold">连续多少次下游故障后打开熔断，必须 &gt;= 1。</param>
    /// <param name="breakDuration">打开后多久才允许探测，必须 &gt; <see cref="TimeSpan.Zero"/>。</param>
    public CircuitBreaker(int failureThreshold, TimeSpan breakDuration)
    {
        FailureThreshold = failureThreshold;
        BreakDuration = breakDuration;
    }

    /// <summary>连续多少次下游故障后打开熔断，默认 5（中间任何一次成功都会归零）。</summary>
    public int FailureThreshold { get; set; } = 5;

    /// <summary>打开后多久转入半开探测，默认 30 秒（冷却期内请求直接失败，不发出）。</summary>
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>半开态允许同时进行的探测请求数，默认 1。</summary>
    public int HalfOpenAttempts { get; set; } = 1;

    /// <summary>最多跟踪多少个维度（host），默认 512；满员时只淘汰不影响判定的条目。</summary>
    public int MaxTrackedHosts { get; set; } = 512;

    /// <summary>
    /// 自定义计数维度；<c>null</c>（默认）用 <see cref="AxiosCircuitContext.Host"/>，
    /// 返回 <c>null</c> 或空串时同样回退到它。
    /// </summary>
    public Func<AxiosCircuitContext, string>? KeySelector { get; set; }

    /// <summary>状态迁移回调（只在真的发生迁移时触发；订阅方抛出的异常会被忽略）。</summary>
    public event Action<AxiosCircuitTransition>? StateChanged;

    /// <summary>当前跟踪的维度数（用于观测 / 断言上限生效）。</summary>
    public int TrackedHostCount => _hosts.Count;

    /// <summary>测试专用：淘汰候选选出后、按 Gate 复核前触发（参数为候选 key）。生产代码不要设置。</summary>
    internal Action<string>? EvictionCandidateChosen { get; set; }

    /// <summary>某个维度当前的状态；未跟踪过（= 从未失败）的维度返回 <see cref="AxiosCircuitState.Closed"/>。</summary>
    public AxiosCircuitState GetState(string host)
        => host is not null && _hosts.TryGetValue(host, out var state)
            ? state.State
            : AxiosCircuitState.Closed;

    /// <summary>清掉所有维度的状态（等价于立刻全部恢复）；打开 / 半开态会触发一次 <see cref="StateChanged"/>。</summary>
    public void Reset()
    {
        foreach (var pair in _hosts)
        {
            if (_hosts.TryRemove(pair.Key, out var state) && state.State != AxiosCircuitState.Closed)
            {
                Notify(new AxiosCircuitTransition(state.Key, state.State, AxiosCircuitState.Closed));
            }
        }
    }

    /// <summary>清掉某一个维度的状态；返回该维度此前是否被跟踪过（打开 / 半开态同样会触发 <see cref="StateChanged"/>）。</summary>
    public bool Reset(string host)
    {
        if (host is null || !_hosts.TryRemove(host, out var state))
        {
            return false;
        }

        if (state.State != AxiosCircuitState.Closed)
        {
            Notify(new AxiosCircuitTransition(state.Key, state.State, AxiosCircuitState.Closed));
        }

        return true;
    }

    /// <inheritdoc />
    public bool ShouldAllow(AxiosCircuitContext context)
    {
        var host = GetOrCreate(KeyOf(context));
        var transition = default(AxiosCircuitTransition);
        bool allowed;

        lock (host.Gate)
        {
            switch (host.State)
            {
                case AxiosCircuitState.Closed:
                    allowed = true;
                    break;

                case AxiosCircuitState.Open:
                    if (!IsCooledDown(host))
                    {
                        // 冷却期未过：不放行。
                        allowed = false;
                        break;
                    }

                    // 冷却结束：转入半开，只放探测请求。
                    host.ProbesInFlight = 0;
                    transition = MoveTo(host, AxiosCircuitState.HalfOpen);
                    goto case AxiosCircuitState.HalfOpen;

                case AxiosCircuitState.HalfOpen:
                    allowed = host.ProbesInFlight < HalfOpenAttempts;
                    if (allowed)
                    {
                        // 名额在这里领走，随后那次 Record 负责归还。
                        host.ProbesInFlight++;
                    }

                    break;

                default:
                    allowed = true;
                    break;
            }
        }

        Notify(transition);
        return allowed;
    }

    /// <inheritdoc />
    public TimeSpan GetRetryAfter(AxiosCircuitContext context)
    {
        if (!_hosts.TryGetValue(KeyOf(context), out var host))
        {
            return TimeSpan.Zero;
        }

        lock (host.Gate)
        {
            if (host.State != AxiosCircuitState.Open)
            {
                // 半开态：名额随时可能被前一个探测归还，说不准具体时刻。
                return TimeSpan.Zero;
            }

            var remaining = BreakDuration - Elapsed(host.OpenedAt);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <inheritdoc />
    public void Record(AxiosCircuitContext context)
    {
        if (!_hosts.TryGetValue(KeyOf(context), out var host))
        {
            // 没经过 ShouldAllow，或条目已被淘汰：没有可记的账。
            return;
        }

        var transition = default(AxiosCircuitTransition);

        lock (host.Gate)
        {
            if (context.IsSuccess)
            {
                if (host.State == AxiosCircuitState.HalfOpen)
                {
                    // 探测成功：恢复闭合，失败计数清零。
                    host.ProbesInFlight = 0;
                    host.Failures = 0;
                    transition = MoveTo(host, AxiosCircuitState.Closed);
                }
                else
                {
                    // 「连续失败」在成功处归零。
                    host.Failures = 0;
                }
            }
            else if (context.IsFailure)
            {
                host.ProbesInFlight = 0;

                if (host.State == AxiosCircuitState.HalfOpen)
                {
                    // 探测失败：立刻重新打开并重新计时。
                    host.Failures = 0;
                    host.OpenedAt = Stopwatch.GetTimestamp();
                    transition = MoveTo(host, AxiosCircuitState.Open);
                }
                else if (host.State == AxiosCircuitState.Closed)
                {
                    host.Failures++;

                    if (host.Failures >= FailureThreshold)
                    {
                        host.Failures = 0;
                        host.OpenedAt = Stopwatch.GetTimestamp();
                        transition = MoveTo(host, AxiosCircuitState.Open);
                    }
                }

                // 打开态：冷却期内还在途的请求报上来的失败不改账。
            }
            else if (host.State == AxiosCircuitState.HalfOpen && host.ProbesInFlight > 0)
            {
                // 取消 / 4xx：既不计数也不清零，但必须归还半开态的探测名额。
                host.ProbesInFlight--;
            }
        }

        Notify(transition);
    }

    /// <summary>计数维度：<see cref="KeySelector"/> 优先，回退到 <see cref="AxiosCircuitContext.Host"/>。</summary>
    private string KeyOf(AxiosCircuitContext context)
    {
        if (KeySelector is { } selector && selector(context) is { Length: > 0 } key)
        {
            return key;
        }

        return context.Host;
    }

    /// <summary>取（或新建）某个维度的状态；新建前先确认没有超出跟踪上限。</summary>
    private HostState GetOrCreate(string key)
    {
        if (_hosts.TryGetValue(key, out var existing))
        {
            return existing;
        }

        // 上限只在新建这条冷路径上检查：命中已有维度不受影响。
        if (_hosts.Count >= (MaxTrackedHosts > 0 ? MaxTrackedHosts : 1))
        {
            Evict();
        }

        return _hosts.GetOrAdd(key, static name => new HostState(name));
    }

    /// <summary>
    /// 淘汰一个维度给新维度腾位置：只淘汰闭合态与冷却早已过期的打开态，
    /// 正在熔断与半开探测中的条目一律不动（没有可淘汰的就不做）。
    /// </summary>
    private void Evict()
    {
        string? closedIdle = null;
        string? closed = null;
        string? staleOpen = null;
        var staleOpenedAt = long.MaxValue;

        foreach (var pair in _hosts)
        {
            var state = pair.Value;

            switch (state.State)
            {
                case AxiosCircuitState.Closed:
                    if (Volatile.Read(ref state.Failures) == 0)
                    {
                        closedIdle = pair.Key;
                    }
                    else
                    {
                        closed ??= pair.Key;
                    }

                    break;

                case AxiosCircuitState.Open when IsCooledDown(state) && Volatile.Read(ref state.ProbesInFlight) == 0:
                    var openedAt = Volatile.Read(ref state.OpenedAt);
                    if (openedAt < staleOpenedAt)
                    {
                        staleOpenedAt = openedAt;
                        staleOpen = pair.Key;
                    }

                    break;
            }

            if (closedIdle is not null)
            {
                // 最理想的目标已经找到，不必再看其余条目
                break;
            }
        }

        // 挑选是无锁快照，删除前需在各自 Gate 内复核。
        var candidate = closedIdle ?? closed ?? staleOpen;
        if (candidate is not null)
        {
            EvictionCandidateChosen?.Invoke(candidate);
        }

        if (TryEvict(closedIdle) || TryEvict(closed) || TryEvict(staleOpen))
        {
            return;
        }
    }

    /// <summary>在目标条目的 <see cref="HostState.Gate"/> 内复核「丢了不改变判定」并移除。</summary>
    private bool TryEvict(string? key)
    {
        if (key is null || !_hosts.TryGetValue(key, out var state))
        {
            return false;
        }

        lock (state.Gate)
        {
            var evictable = state.State switch
            {
                AxiosCircuitState.Closed => true,
                // 冷却过期的打开态：下次访问本来就会转半开探测，丢掉等价。
                AxiosCircuitState.Open => IsCooledDown(state) && state.ProbesInFlight == 0,
                _ => false
            };

            // 按「键 + 实例」移除，避免误删期间重建的条目。
            return evictable && _hosts.TryRemove(new KeyValuePair<string, HostState>(key, state));
        }
    }

    private bool IsCooledDown(HostState host) => Elapsed(host.OpenedAt) >= BreakDuration;

    private static AxiosCircuitTransition MoveTo(HostState host, AxiosCircuitState next)
    {
        var previous = host.State;
        host.StateValue = (int)next;
        return new AxiosCircuitTransition(host.Key, previous, next);
    }

    private void Notify(AxiosCircuitTransition transition)
    {
        if (transition.IsEmpty || StateChanged is not { } handler)
        {
            return;
        }

        try
        {
            handler(transition);
        }
        catch
        {
            // 观测代码抛异常不该改变请求结果。
        }
    }

    /// <summary>自 <paramref name="since"/> 起的时长（单调时钟，不受系统时间调整影响）。</summary>
    private static TimeSpan Elapsed(long since)
    {
        var seconds = (double)(Stopwatch.GetTimestamp() - since) / Stopwatch.Frequency;
        return seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
    }

    /// <summary>单个维度（host）的状态：一把锁保护，维度之间互不阻塞。</summary>
    private sealed class HostState
    {
        public HostState(string key) => Key = key;

        public string Key { get; }

        public object Gate { get; } = new();

        /// <summary>以 <c>int</c> 存放，便于 <see cref="GetState"/> 无锁读取。</summary>
        public int StateValue;

        public AxiosCircuitState State => (AxiosCircuitState)Volatile.Read(ref StateValue);

        /// <summary>闭合态下的连续失败数。</summary>
        public int Failures;

        /// <summary>半开态下已放行、尚未回报结果的探测数。</summary>
        public int ProbesInFlight;

        /// <summary>本次打开的时刻（<see cref="Stopwatch.GetTimestamp"/>）。</summary>
        public long OpenedAt;
    }
}
