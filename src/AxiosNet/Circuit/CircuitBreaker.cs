using System.Collections.Concurrent;
using System.Diagnostics;
using AxiosNet.Telemetry;

namespace AxiosNet.Circuit;

/// <summary>
/// <see cref="IAxiosCircuitPolicy"/> 的默认实现：按 origin 维度统计失败，滚动窗口失败率与连续失败两条规则取「或」，半开态慢启动恢复。
/// </summary>
/// <remarks>
/// Closed →（连续失败达 <see cref="FailureThreshold"/>，或窗口失败率达 <see cref="FailureRateThreshold"/>）→ Open
/// →（冷却 <see cref="BreakDuration"/>）→ HalfOpen →（连续 <see cref="RecoverySuccesses"/> 次成功）→ Closed；探测失败立刻回 Open。
/// 维度默认 <c>uri.Authority</c>（host:port），可用 <see cref="KeySelector"/> 改写。
/// </remarks>
/// <example>
/// <code>
/// // 5 次连续失败、或最近 20 次里失败率过半（至少 10 个样本）就熔断；
/// // 冷却 30 秒后进入半开，连续 3 次探测成功才全量放开
/// var breaker = new CircuitBreaker
/// {
///     FailureThreshold = 5,
///     WindowSize = 20,
///     MinimumSamples = 10,
///     FailureRateThreshold = 0.5,
///     BreakDuration = TimeSpan.FromSeconds(30),
///     HalfOpenAttempts = 1,
///     RecoverySuccesses = 3
/// };
///
/// breaker.StateChanged += change =&gt; logger.LogWarning(
///     "{Host} 熔断状态：{From} -&gt; {To}", change.Host, change.Previous, change.Current);
///
/// var client = Axios.Create(c =&gt; c.Resilience.Circuit.Policy = breaker);
///
/// // 运维手工恢复：清掉所有维度的账（下次请求重新开始计数）
/// breaker.Reset();
/// </code>
/// </example>
public sealed class CircuitBreaker : IAxiosCircuitPolicy
{
    private readonly ConcurrentDictionary<string, HostState> _hosts = new(StringComparer.OrdinalIgnoreCase);

    // 进程级状态计数，供 circuit.state 仪表读取。
    private static long _trackedHosts;
    private static long _openHosts;
    private static long _halfOpenHosts;

    /// <summary>所有熔断器实例当前跟踪的维度总数（进程级）。</summary>
    internal static long TrackedCount => Interlocked.Read(ref _trackedHosts);

    /// <summary>所有熔断器实例中处于打开态的维度数（进程级）。</summary>
    internal static long OpenCount => Interlocked.Read(ref _openHosts);

    /// <summary>所有熔断器实例中处于半开态的维度数（进程级）。</summary>
    internal static long HalfOpenCount => Interlocked.Read(ref _halfOpenHosts);

    /// <summary>默认配置：连续 5 次失败或窗口失败率过半（至少 10 样本）熔断，冷却 30 秒，连续 3 次成功恢复。</summary>
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
    /// <remarks>与窗口失败率取或；样本不足 <see cref="MinimumSamples"/> 时只有本规则能打开。</remarks>
    public int FailureThreshold { get; set; } = 5;

    /// <summary>滚动窗口样本数，默认 20；<c>0</c> 表示关闭失败率判定，只按连续失败熔断。</summary>
    public int WindowSize { get; set; } = 20;

    /// <summary>窗口里至少要有这么多个样本才判失败率，默认 10；样本不足时只走连续失败那条规则。</summary>
    public int MinimumSamples { get; set; } = 10;

    /// <summary>窗口失败率阈值，默认 <c>0.5</c>（须在 <c>(0, 1]</c>）；实际阈值 <c>ceil(样本数 × 本值)</c>，<see cref="WindowSize"/> 为 0 时不校验也不参与。</summary>
    public double FailureRateThreshold { get; set; } = 0.5;

    /// <summary>半开态连续成功多少次才全量恢复，默认 3；<c>0</c>/<c>1</c> 表示一次成功即闭合。恢复期并发上限为 <c>HalfOpenAttempts + 连续成功数</c>。</summary>
    public int RecoverySuccesses { get; set; } = 3;

    /// <summary>打开后多久转入半开探测，默认 30 秒；冷却期内请求直接失败。转入是惰性的（下一次准入判定时切换）。</summary>
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>半开态初始允许同时进行的探测请求数（也是慢启动的起点），默认 1。</summary>
    public int HalfOpenAttempts { get; set; } = 1;

    /// <summary>半开态一次探测最长占用名额的时长，默认 30 秒；<c>null</c> 表示不设看门狗。</summary>
    /// <remarks>探测在时长内未回报视为卡死：整轮半开作废并重新打开，迟到结果凭过期票被丢弃。看门狗只在准入判定时检查，以最近一次放行探测的时刻计时。</remarks>
    public TimeSpan? ProbeTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>最多跟踪的计数维度数，默认 512（须 &gt;= 1）；满员且无可淘汰项时新维度按闭合态直通、不记账。</summary>
    /// <remarks>淘汰优先无失败样本的闭合维度，其次有失败台账的闭合维度与冷却已过期的打开维度；熔断中与半开探测中的不动。</remarks>
    public int MaxTrackedHosts { get; set; } = 512;

    /// <summary>自定义计数维度；<c>null</c>（默认）用 <see cref="AxiosCircuitContext.Host"/>，返回 <c>null</c> 或空串时也回退到它。</summary>
    public Func<AxiosCircuitContext, string>? KeySelector { get; set; }

    /// <summary>状态迁移回调（仅真实迁移时触发；订阅方异常被忽略）。</summary>
    public event Action<AxiosCircuitTransition>? StateChanged;

    /// <summary>当前跟踪的维度数；并发新建时可能短暂略超 <see cref="MaxTrackedHosts"/>。</summary>
    public int TrackedHostCount => _hosts.Count;

    /// <summary>测试专用：淘汰候选选出后、按 Gate 复核前触发（参数为候选 key）。生产代码不要设置。</summary>
    internal Action<string>? EvictionCandidateChosen { get; set; }

    /// <summary>某个维度当前的状态；没有登记（含跟踪表已满、按直通处理）的维度返回 <see cref="AxiosCircuitState.Closed"/>。</summary>
    public AxiosCircuitState GetState(string host)
        => host is not null && _hosts.TryGetValue(host, out var state)
            ? state.State
            : AxiosCircuitState.Closed;

    /// <summary>清掉所有维度的状态（等价于立刻全部恢复）；打开 / 半开态会触发一次 <see cref="StateChanged"/>。</summary>
    public void Reset()
    {
        foreach (var pair in _hosts)
        {
            // 按「键 + 实例」移除。
            if (_hosts.TryRemove(new KeyValuePair<string, HostState>(pair.Key, pair.Value)))
            {
                var removed = Unregister(pair.Value);

                if (removed != AxiosCircuitState.Closed)
                {
                    Notify(new AxiosCircuitTransition(pair.Value.Key, removed, AxiosCircuitState.Closed));
                }
            }
        }
    }

    /// <summary>清掉某一个维度的状态；返回该维度是否被跟踪过（打开 / 半开态同样触发 <see cref="StateChanged"/>）。</summary>
    public bool Reset(string host)
    {
        if (host is null || !_hosts.TryRemove(host, out var state))
        {
            return false;
        }

        var removed = Unregister(state);

        if (removed != AxiosCircuitState.Closed)
        {
            Notify(new AxiosCircuitTransition(state.Key, removed, AxiosCircuitState.Closed));
        }

        return true;
    }

    /// <inheritdoc />
    public bool ShouldAllow(AxiosCircuitContext context) => ShouldAllow(context, out _, out _);

    /// <summary>带准入票的准入判定，不取建议等待时间（只关心放行与否的调用方用）。</summary>
    /// <param name="context">本次尝试的上下文。</param>
    /// <param name="ticket">放行时的代次；不放行时为当前代次。</param>
    /// <returns>是否放行。</returns>
    internal bool ShouldAllow(AxiosCircuitContext context, out long ticket)
        => ShouldAllow(context, out ticket, out _);

    /// <summary>带准入票的准入判定（库内管线使用）：票号是放行那一刻的代次，回报时过期即视为上一轮的迟到结果。</summary>
    /// <param name="context">本次尝试的上下文。</param>
    /// <param name="ticket">放行时的代次；不放行时为当前代次。</param>
    /// <param name="retryAfter">拒绝时的建议等待时间（冷却剩余），放行或说不准时为零。</param>
    internal bool ShouldAllow(AxiosCircuitContext context, out long ticket, out TimeSpan retryAfter)
    {
        var host = GetOrCreate(KeyOf(context));

        retryAfter = TimeSpan.Zero;

        // 未登记（表满且无可淘汰）：按闭合态直通。
        if (host is null)
        {
            AxiosTelemetry.Dispatch.CircuitUntrackedHost();
            ticket = 0;
            return true;
        }

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
                        allowed = false;
                        retryAfter = RemainingBreak(host);
                        break;
                    }

                    host.ProbesInFlight = 0;
                    host.ProbeSuccesses = 0;
                    transition = MoveTo(host, AxiosCircuitState.HalfOpen);
                    goto case AxiosCircuitState.HalfOpen;

                case AxiosCircuitState.HalfOpen:
                    // 探测卡死：整轮作废并重新打开。
                    if (IsProbeExpired(host))
                    {
                        host.ProbesInFlight = 0;
                        host.ProbeSuccesses = 0;
                        transition = Reopen(host);
                        allowed = false;
                        retryAfter = RemainingBreak(host);
                        break;
                    }

                    allowed = host.ProbesInFlight < HalfOpenAttempts + host.ProbeSuccesses;
                    if (allowed)
                    {
                        host.ProbesInFlight++;
                        host.ProbeStartedAt = Stopwatch.GetTimestamp();
                    }

                    break;

                default:
                    allowed = true;
                    break;
            }

            ticket = host.Generation;
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
                return TimeSpan.Zero;
            }

            return RemainingBreak(host);
        }
    }

    /// <inheritdoc />
    public void Record(AxiosCircuitContext context) => Record(context, ticket: 0);

    /// <summary>带准入票的结果回报：票号与当前代次不一致时丢弃（迟到结果不污染新一轮）。</summary>
    /// <param name="context">结果上下文。</param>
    /// <param name="ticket">准入票；<c>0</c> 表示没有票（直接调用公共 API），照旧记账。</param>
    internal void Record(AxiosCircuitContext context, long ticket)
    {
        if (!_hosts.TryGetValue(KeyOf(context), out var host))
        {
            return;
        }

        AxiosCircuitTransition transition;

        lock (host.Gate)
        {
            // 迟到结果：不属于本轮，丢弃。
            if (ticket != 0 && ticket != host.Generation)
            {
                return;
            }

            transition = context.IsSuccess ? RecordSuccess(host)
                : context.IsFailure ? RecordFailure(host)
                : RecordIgnored(host);
        }

        Notify(transition);
    }

    /// <summary>成功：连续失败归零；闭合态记窗口并按失败率判定，半开态推进慢启动进度。</summary>
    private AxiosCircuitTransition RecordSuccess(HostState host)
    {
        host.Failures = 0;

        switch (host.State)
        {
            case AxiosCircuitState.HalfOpen:
                return AdvanceRecovery(host);

            case AxiosCircuitState.Closed:
                host.PushWindow(success: true, WindowSize);
                if (host.WindowTrips(MinimumSamples, FailureRateThreshold))
                {
                    return Reopen(host);
                }

                return default;

            default:
                return default;
        }
    }

    /// <summary>下游故障：半开态立刻重开，闭合态按两条规则累计，打开态不动账。</summary>
    private AxiosCircuitTransition RecordFailure(HostState host)
    {
        host.ProbesInFlight = 0;
        host.ProbeSuccesses = 0;

        switch (host.State)
        {
            case AxiosCircuitState.HalfOpen:
                host.Failures = 0;
                return Reopen(host);

            case AxiosCircuitState.Closed:
                host.Failures++;
                host.PushWindow(success: false, WindowSize);

                if (host.Failures >= FailureThreshold
                    || host.WindowTrips(MinimumSamples, FailureRateThreshold))
                {
                    host.Failures = 0;
                    return Reopen(host);
                }

                return default;

            default:
                return default;
        }
    }

    /// <summary>既不成功也非故障（取消 / 4xx / 请求从未发出）：不计数，只归还半开态的探测名额。</summary>
    private static AxiosCircuitTransition RecordIgnored(HostState host)
    {
        if (host.State == AxiosCircuitState.HalfOpen && host.ProbesInFlight > 0)
        {
            host.ProbesInFlight--;
        }

        return default;
    }

    /// <summary>半开态的一次探测成功：归还名额、推进慢启动进度，凑够 <see cref="RecoverySuccesses"/> 才闭合。</summary>
    private AxiosCircuitTransition AdvanceRecovery(HostState host)
    {
        if (host.ProbesInFlight == 0)
        {
            return default;
        }

        host.ProbesInFlight--;

        // 0/1 均表示一次成功即闭合。
        var target = RecoverySuccesses > 1 ? RecoverySuccesses : 1;
        if (++host.ProbeSuccesses < target)
        {
            return default;
        }

        host.ProbesInFlight = 0;
        host.ProbeSuccesses = 0;
        host.ResetWindow();
        return MoveTo(host, AxiosCircuitState.Closed);
    }

    /// <summary>打开并重新计时：清空窗口。</summary>
    private static AxiosCircuitTransition Reopen(HostState host)
    {
        host.OpenedAt = Stopwatch.GetTimestamp();
        host.ResetWindow();
        return MoveTo(host, AxiosCircuitState.Open);
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
    /// <returns>跟踪表已满且没有可淘汰条目时返回 <c>null</c>（该维度本次不跟踪）。</returns>
    private HostState? GetOrCreate(string key)
    {
        if (_hosts.TryGetValue(key, out var existing))
        {
            return existing;
        }

        // 上限只在新建时检查。
        var max = MaxTrackedHosts > 0 ? MaxTrackedHosts : 1;
        if (_hosts.Count >= max)
        {
            Evict();

            // 仍满：不登记新维度。
            if (_hosts.Count >= max)
            {
                return null;
            }
        }

        var candidate = new HostState(key);
        while (true)
        {
            if (_hosts.TryAdd(key, candidate))
            {
                Interlocked.Increment(ref _trackedHosts);
                return candidate;
            }

            // 竞态落选：返回表里那一个。
            if (_hosts.TryGetValue(key, out var raced))
            {
                return raced;
            }

            // 被驱逐：重试。
        }
    }

    /// <summary>淘汰一个维度给新维度腾位置：只淘汰闭合态与冷却已过期的打开态，熔断中与半开探测中的不动。</summary>
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
                    // 有失败台账的闭合维度优先度更低。
                    if (state.HasRecentFailures)
                    {
                        closed ??= pair.Key;
                    }
                    else
                    {
                        closedIdle = pair.Key;
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
                // 已找到最优目标。
                break;
            }
        }

        // 无锁挑选，删除前在 Gate 内复核。
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

    /// <summary>在目标条目的 <see cref="HostState.Gate"/> 内复核仍可淘汰（闭合态，或冷却已过期且无在途探测的打开态）并移除。</summary>
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
                // 冷却过期且无在途探测才可淘汰。
                AxiosCircuitState.Open => IsCooledDown(state) && state.ProbesInFlight == 0,
                _ => false
            };

            // 按「键 + 实例」移除。
            if (evictable && _hosts.TryRemove(new KeyValuePair<string, HostState>(key, state)))
            {
                Unregister(state);
                return true;
            }

            return false;
        }
    }

    /// <summary>条目离开跟踪表：维度计数 -1 并减掉它当前状态的计数，返回原状态供调用方发迁移通知。</summary>
    /// <remarks>须在 <c>host.Gate</c> 内调用。</remarks>
    private static AxiosCircuitState Unregister(HostState state)
    {
        lock (state.Gate)
        {
            state.Tracked = false;
            Interlocked.Decrement(ref _trackedHosts);

            var removed = state.State;
            AdjustStateCount(removed, -1);
            return removed;
        }
    }

    /// <summary>状态计数增减：只有打开 / 半开需要单独记（闭合态由「总数 - 两者」推出）。</summary>
    private static void AdjustStateCount(AxiosCircuitState state, int delta)
    {
        switch (state)
        {
            case AxiosCircuitState.Open:
                Interlocked.Add(ref _openHosts, delta);
                break;

            case AxiosCircuitState.HalfOpen:
                Interlocked.Add(ref _halfOpenHosts, delta);
                break;
        }
    }

    /// <summary>熔断已持续满 <see cref="BreakDuration"/>：可以进半开尝试探测。</summary>
    private bool IsCooledDown(HostState host) => Elapsed(host.OpenedAt) >= BreakDuration;

    /// <summary>打开态还差多久进半开（已到期则为零）。调用方须持有该状态的锁。</summary>
    private TimeSpan RemainingBreak(HostState host)
    {
        var remaining = BreakDuration - Elapsed(host.OpenedAt);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>半开态在途探测是否已超出 <see cref="ProbeTimeout"/>（没有在途探测时不算过期）。</summary>
    private bool IsProbeExpired(HostState host)
        => ProbeTimeout is { } timeout
           && host.ProbesInFlight > 0
           && Elapsed(host.ProbeStartedAt) >= timeout;

    /// <summary>全局单调代次源：每次状态迁移取一个从未出现过的值。</summary>
    private static long _generationEpoch;

    /// <summary>取下一个全局唯一代次（原子自增）。</summary>
    /// <returns>从未被使用过的代次值。</returns>
    private static long NextGenerationEpoch() => Interlocked.Increment(ref _generationEpoch);

    /// <summary>切换状态：换一个全局唯一的代次（旧请求的回报按迟到处理）、跟着调整各维度计数，返回迁移记录。</summary>
    private static AxiosCircuitTransition MoveTo(HostState host, AxiosCircuitState next)
    {
        var previous = host.State;
        host.StateValue = (int)next;

        // 每次迁移换一代；序号全局自增。
        host.Generation = NextGenerationEpoch();

        // 计数随迁移成对调整；已离开跟踪表的不再动。
        if (host.Tracked)
        {
            AdjustStateCount(previous, -1);
            AdjustStateCount(next, +1);
        }

        return new AxiosCircuitTransition(host.Key, previous, next);
    }

    /// <summary>通知状态迁移监听者；观测代码抛异常不影响请求结果。</summary>
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
            // 观测异常不影响请求。
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

        /// <summary>最近一次半开探测放行的时刻（<see cref="Stopwatch.GetTimestamp"/>），供 <see cref="ProbeTimeout"/> 判过期。</summary>
        public long ProbeStartedAt;

        /// <summary>当前代次：实例创建与每次迁移都取全局唯一值；<c>0</c> 专指「没有票」。</summary>
        public long Generation = NextGenerationEpoch();

        /// <summary>本次打开的时刻（<see cref="Stopwatch.GetTimestamp"/>）。</summary>
        public long OpenedAt;

        /// <summary>半开态已连续成功次数（慢启动进度）；探测失败 / 超时 / 闭合 / 转入半开时清零。</summary>
        public int ProbeSuccesses;

        /// <summary>滚动窗口：<c>0</c> = 成功，<c>1</c> = 失败。惰性分配，<c>WindowSize</c> 改了就重建。</summary>
        private byte[]? _window;

        // 窗口游标 / 有效样本 / 失败样本（读写都在 Gate 内）。
        private int _windowIndex;

        private int _windowCount;

        private int _windowFailures;

        /// <summary>是否还有「近期失败」的账（连续失败数或窗口内失败样本），淘汰优先级用，无锁读。</summary>
        public bool HasRecentFailures => Volatile.Read(ref Failures) > 0 || Volatile.Read(ref _windowFailures) > 0;

        /// <summary>把一次结果记进滚动窗口；<paramref name="windowSize"/> <c>&lt;= 0</c> 表示关掉失败率判定。</summary>
        public void PushWindow(bool success, int windowSize)
        {
            if (windowSize <= 0)
            {
                return;
            }

            if (_window is null || _window.Length != windowSize)
            {
                // WindowSize 变化时重建窗口。
                _window = new byte[windowSize];
                _windowIndex = 0;
                _windowCount = 0;
                _windowFailures = 0;
            }

            if (_windowCount == _window.Length)
            {
                // 覆盖最老一条，若是失败先扣计数。
                if (_window[_windowIndex] != 0)
                {
                    _windowFailures--;
                }
            }
            else
            {
                _windowCount++;
            }

            _window[_windowIndex] = success ? (byte)0 : (byte)1;

            if (!success)
            {
                _windowFailures++;
            }

            _windowIndex = (_windowIndex + 1) % _window.Length;
        }

        /// <summary>窗口内失败率是否达到阈值；样本不足 <paramref name="minimumSamples"/> 时返回 <c>false</c>。</summary>
        public bool WindowTrips(int minimumSamples, double threshold)
        {
            if (minimumSamples < 1 || _windowCount < minimumSamples)
            {
                return false;
            }

            // 阈值 = ceil(样本数 × 比例)。
            var needed = (int)Math.Ceiling(_windowCount * threshold);
            return _windowFailures >= needed;
        }

        /// <summary>清空窗口（数组保留复用）。</summary>
        public void ResetWindow()
        {
            _windowCount = 0;
            _windowFailures = 0;
            _windowIndex = 0;
        }

        /// <summary>是否仍在跟踪表里；决定状态迁移要不要动全局计数，仅在 <see cref="Gate"/> 内读写。</summary>
        public bool Tracked = true;
    }
}
