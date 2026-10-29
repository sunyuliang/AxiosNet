using System.Collections.Concurrent;
using System.Diagnostics;

namespace AxiosNet.Circuit;

/// <summary>
/// <see cref="IAxiosCircuitPolicy"/> 的默认实现：按 host 维度的滚动窗口失败率 + 连续失败快速路径 + 半开慢启动恢复。
/// 状态机 Closed →（连续失败达 <see cref="FailureThreshold"/>，或窗口内失败率达 <see cref="FailureRateThreshold"/>）→ Open
/// →（冷却 <see cref="BreakDuration"/>）→ HalfOpen →（连续 <see cref="RecoverySuccesses"/> 次探测成功）→ Closed；
/// 期间任何探测失败立刻回 Open。维度默认 <c>uri.Authority</c>，可用 <see cref="KeySelector"/> 改写。
/// </summary>
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
/// var client = Axios.Create(c =&gt; c.Circuit.Policy = breaker);
///
/// // 运维手工恢复：清掉所有维度的账（下次请求重新开始计数）
/// breaker.Reset();
/// </code>
/// </example>
public sealed class CircuitBreaker : IAxiosCircuitPolicy
{
    private readonly ConcurrentDictionary<string, HostState> _hosts = new(StringComparer.OrdinalIgnoreCase);

    // 进程级状态计数，只在登记 / 移除 / 状态迁移时改动，供 axiosnet.client.circuit.state 仪表读取。
    private static long _trackedHosts;
    private static long _openHosts;
    private static long _halfOpenHosts;

    /// <summary>所有熔断器实例当前跟踪的维度总数（进程级）。</summary>
    internal static long TrackedCount => Interlocked.Read(ref _trackedHosts);

    /// <summary>所有熔断器实例中处于打开态的维度数（进程级）。</summary>
    internal static long OpenCount => Interlocked.Read(ref _openHosts);

    /// <summary>所有熔断器实例中处于半开态的维度数（进程级）。</summary>
    internal static long HalfOpenCount => Interlocked.Read(ref _halfOpenHosts);

    /// <summary>
    /// 默认配置：连续 5 次失败、或最近 20 次里失败率过半（至少 10 个样本）就熔断；
    /// 冷却 30 秒后进入半开，连续 3 次探测成功才放开。
    /// </summary>
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
    /// <remarks>样本不足 <see cref="WindowSize"/> 时的唯一判定规则。</remarks>
    public int FailureThreshold { get; set; } = 5;

    /// <summary>
    /// 滚动窗口的样本数，默认 20：按「最近 N 次结果」算失败率，与时间无关。
    /// <c>0</c> 表示关闭失败率判定，只按 <see cref="FailureThreshold"/> 的连续失败熔断。
    /// </summary>
    public int WindowSize { get; set; } = 20;

    /// <summary>窗口里至少要有这么多个样本才判失败率，默认 10；样本不足时只走连续失败那条规则。</summary>
    public int MinimumSamples { get; set; } = 10;

    /// <summary>
    /// 窗口内失败率达到多少就打开熔断，默认 <c>0.5</c>（必须落在 <c>(0, 1]</c>）。
    /// 实际阈值是 <c>ceil(样本数 × 本值)</c>：20 个样本、0.5 → 10 次失败即打开。
    /// </summary>
    public double FailureRateThreshold { get; set; } = 0.5;

    /// <summary>
    /// 慢启动：半开态需要连续成功多少次才全量恢复，默认 3；<c>0</c>/<c>1</c> 表示探测一次成功就闭合。
    /// 恢复期的探测并发上限为 <c>HalfOpenAttempts + 已连续成功次数</c>。
    /// </summary>
    public int RecoverySuccesses { get; set; } = 3;

    /// <summary>打开后多久转入半开探测，默认 30 秒（冷却期内请求直接失败，不发出）。</summary>
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>半开态初始允许同时进行的探测请求数（也是慢启动的起点），默认 1。</summary>
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
            // 按「键 + 实例」移除：期间重建的条目不属于本次重置。
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

    /// <summary>清掉某一个维度的状态；返回该维度此前是否被跟踪过（打开 / 半开态同样会触发 <see cref="StateChanged"/>）。</summary>
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
    public bool ShouldAllow(AxiosCircuitContext context) => ShouldAllow(context, out _);

    /// <summary>
    /// 带准入票的准入判定（库内管线使用）：票号是放行那一刻的代次，回报时票号过期即视为上一轮的迟到结果。
    /// </summary>
    internal bool ShouldAllow(AxiosCircuitContext context, out long ticket)
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

                    // 冷却结束：转入半开，慢启动从头开始（连续成功数清零）。
                    host.ProbesInFlight = 0;
                    host.ProbeSuccesses = 0;
                    transition = MoveTo(host, AxiosCircuitState.HalfOpen);
                    goto case AxiosCircuitState.HalfOpen;

                case AxiosCircuitState.HalfOpen:
                    // 慢启动：允许的在途探测数随连续成功递增，凑满 RecoverySuccesses 次成功转闭合。
                    allowed = host.ProbesInFlight < HalfOpenAttempts + host.ProbeSuccesses;
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

            // 票号在状态迁移之后取：本次放行属于迁移后的新代次。
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
                // 半开态：名额随时可能被前一个探测归还，说不准具体时刻。
                return TimeSpan.Zero;
            }

            var remaining = BreakDuration - Elapsed(host.OpenedAt);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <inheritdoc />
    public void Record(AxiosCircuitContext context) => Record(context, ticket: 0);

    /// <summary>
    /// 带准入票的结果回报：票号与当前代次不一致时直接丢弃。打开 / 半开 / 恢复都会让代次前进，
    /// 因此熔断打开前放行的请求迟到回报（超时长于冷却期时很常见）不会污染新一轮的探测名额与判定。
    /// </summary>
    /// <param name="context">结果上下文。</param>
    /// <param name="ticket">准入票；<c>0</c> 表示没有票（直接调用公共 API），照旧记账。</param>
    internal void Record(AxiosCircuitContext context, long ticket)
    {
        if (!_hosts.TryGetValue(KeyOf(context), out var host))
        {
            // 没经过 ShouldAllow，或条目已被淘汰：没有可记的账。
            return;
        }

        AxiosCircuitTransition transition;

        lock (host.Gate)
        {
            // 迟到结果：它属于上一轮，这一轮的名额与台账都不该被它改动。
            if (ticket != 0 && ticket != host.Generation)
            {
                return;
            }

            // 三条路径对探测名额的处理各不相同（推进 / 作废 / 只归还）。
            transition = context.IsSuccess ? RecordSuccess(host)
                : context.IsFailure ? RecordFailure(host)
                : RecordIgnored(host);
        }

        Notify(transition);
    }

    /// <summary>成功：连续失败归零；闭合态记进窗口，半开态推进慢启动进度。</summary>
    private AxiosCircuitTransition RecordSuccess(HostState host)
    {
        // 「连续失败」在成功处归零（窗口不清零：滚动窗口靠覆盖自然衰减）。
        host.Failures = 0;

        switch (host.State)
        {
            case AxiosCircuitState.HalfOpen:
                return AdvanceRecovery(host);

            case AxiosCircuitState.Closed:
                host.PushWindow(success: true, WindowSize);
                return default;

            default:
                // 打开态：冷却期尾巴上那条在途请求的结果不改账（那一轮台账打开时就结清了）。
                return default;
        }
    }

    /// <summary>下游故障：半开态立刻重开，闭合态按两条规则累计，打开态不动账。</summary>
    private AxiosCircuitTransition RecordFailure(HostState host)
    {
        // 故障一出，慢启动进度与在途名额一起作废（名额随状态迁移失效，不必逐个归还）。
        host.ProbesInFlight = 0;
        host.ProbeSuccesses = 0;

        switch (host.State)
        {
            case AxiosCircuitState.HalfOpen:
                // 探测失败：立刻重新打开并重新计时。
                host.Failures = 0;
                return Reopen(host);

            case AxiosCircuitState.Closed:
                host.Failures++;
                host.PushWindow(success: false, WindowSize);

                // 两条规则取或：样本够了看失败率（治「半死不活」），样本不够靠连续失败（治低流量）。
                if (host.Failures >= FailureThreshold
                    || host.WindowTrips(MinimumSamples, FailureRateThreshold))
                {
                    host.Failures = 0;
                    return Reopen(host);
                }

                return default;

            default:
                // 打开态：冷却期内还在途的请求报上来的失败不改账。
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
        // 只有真的领到名额的探测才推进度：冷却期之前就在途的请求不属于这一轮慢启动，
        // 让它算一次成功会把「连续 N 次探测成功」提前凑满。
        if (host.ProbesInFlight == 0)
        {
            return default;
        }

        host.ProbesInFlight--;

        // 0 与 1 都表示「不要慢启动」：探测一次成功就闭合。
        var target = RecoverySuccesses > 1 ? RecoverySuccesses : 1;
        if (++host.ProbeSuccesses < target)
        {
            return default;
        }

        // 慢启动走完：全量恢复，窗口与探测计数一起清掉，恢复后从干净台账起算。
        host.ProbesInFlight = 0;
        host.ProbeSuccesses = 0;
        host.ResetWindow();
        return MoveTo(host, AxiosCircuitState.Closed);
    }

    /// <summary>打开并重新计时：窗口清空，恢复后从干净台账重新判，避免旧失败把刚恢复的维度立刻打回去。</summary>
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

        // TryAdd 而不是 GetOrAdd：要在「真的登记成功」时才 +1，竞态落选不能重复计数。
        var candidate = new HostState(key);
        while (true)
        {
            if (_hosts.TryAdd(key, candidate))
            {
                Interlocked.Increment(ref _trackedHosts);
                return candidate;
            }

            // 竞态落选：必须返回表里那一个，否则它的状态迁移会照常调整全局计数。
            if (_hosts.TryGetValue(key, out var raced))
            {
                return raced;
            }

            // 落选的那一条在这两步之间被驱逐了：重来一轮。
        }
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
                    // 窗口里还留着一笔失败就不算「空闲」：连续失败数可能刚被一次成功清零，
                    // 但它的失败率台账仍然有效，丢掉等于把这条维度的判定重置成全新维度。
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
            if (evictable && _hosts.TryRemove(new KeyValuePair<string, HostState>(key, state)))
            {
                Unregister(state);
                return true;
            }

            return false;
        }
    }

    /// <summary>条目离开跟踪表：维度计数 -1 并减掉它当前状态的计数，返回该状态供调用方发迁移通知。</summary>
    /// <remarks>必须在 <c>host.Gate</c> 内：<see cref="MoveTo"/> 的状态迁移同样在锁内成对调整计数。</remarks>
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

    private bool IsCooledDown(HostState host) => Elapsed(host.OpenedAt) >= BreakDuration;

    private static AxiosCircuitTransition MoveTo(HostState host, AxiosCircuitState next)
    {
        var previous = host.State;
        host.StateValue = (int)next;

        // 每次迁移都换一代：上一轮放行的请求持旧票，之后回报的结果一律按迟到处理。
        host.Generation++;

        // 计数跟着迁移走：调用方都在 host.Gate 内，所以这两步对同一维度是原子的。
        // 已经离开跟踪表的维度不再动计数：驱逐与迁移可能交错（Evict 拿到表项之后、本次 Report 才进锁），
        // 照常计数的话 open / half_open 会永久多算一个、由「总数 - 两者」推出的 closed 又少算，
        // 且再也回不来。
        if (host.Tracked)
        {
            AdjustStateCount(previous, -1);
            AdjustStateCount(next, +1);
        }

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

        /// <summary>
        /// 当前代次：每次状态迁移 +1。放行时把代次作为准入票发给调用方，迟到的结果凭旧票被丢弃。
        /// 从 1 开始，<c>0</c> 专指「没有票」（直接调用公共 API）。
        /// </summary>
        public long Generation = 1;

        /// <summary>本次打开的时刻（<see cref="Stopwatch.GetTimestamp"/>）。</summary>
        public long OpenedAt;

        /// <summary>
        /// 半开态已连续成功多少次（慢启动的进度）。清零时机只有两处：探测失败，以及下次从
        /// <see cref="AxiosCircuitState.Open"/> 转入半开（进度不跨轮沿用）；被忽略的结果不动它。
        /// </summary>
        public int ProbeSuccesses;

        /// <summary>滚动窗口：<c>0</c> = 成功，<c>1</c> = 失败。惰性分配，<c>WindowSize</c> 改了就重建。</summary>
        private byte[]? _window;

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
                // 运行中改了 WindowSize：按新容量重建，旧样本作废（冷路径，且在 Gate 内）。
                _window = new byte[windowSize];
                _windowIndex = 0;
                _windowCount = 0;
                _windowFailures = 0;
            }

            if (_windowCount == _window.Length)
            {
                // 覆盖最老的一条：它若是一笔失败，先从计数里扣掉。
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

            // 阈值口径：ceil(样本数 × 比例)。20 个样本、0.5 → 10 次失败即打开。
            var needed = (int)Math.Ceiling(_windowCount * threshold);
            return _windowFailures >= needed;
        }

        /// <summary>清空窗口（打开 / 恢复时重新记账）；数组保留复用，不在热路径上放内存。</summary>
        public void ResetWindow()
        {
            _windowCount = 0;
            _windowFailures = 0;
            _windowIndex = 0;
        }

        /// <summary>
        /// 是否仍在跟踪表里（<see cref="CircuitBreaker.Unregister"/> 之后为 <c>false</c>）。
        /// 只用来决定「这次状态迁移要不要动全局计数」，故只在 <see cref="Gate"/> 内读写。
        /// </summary>
        public bool Tracked = true;
    }
}
