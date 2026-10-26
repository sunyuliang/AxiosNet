using System.Collections.Concurrent;
using System.Diagnostics;

namespace AxiosNet.Circuit;

/// <summary>
/// <see cref="IAxiosCircuitPolicy"/> 的默认实现：<b>按 host 维度</b>的失败计数 + 半开探测 + 自动恢复。
/// <para>
/// 状态机：<see cref="AxiosCircuitState.Closed"/> →（连续失败达到 <see cref="FailureThreshold"/>）→
/// <see cref="AxiosCircuitState.Open"/> →（冷却 <see cref="BreakDuration"/>）→
/// <see cref="AxiosCircuitState.HalfOpen"/> →（探测成功）→ 闭合 / （探测失败）→ 重新打开。
/// </para>
/// <para>
/// 失败口径与重试门禁对齐（见 <see cref="AxiosCircuitContext.IsFailure"/>）：
/// 网络错误 / 超时 / 5xx 计入失败；取消与 4xx（含 408 / 429）既不计数也不清零 ——
/// 一次 404 既不能证明下游恢复，也不该被当成「它挂了」。
/// </para>
/// <para>
/// 计数维度默认是 <c>uri.Authority</c>（<c>host:port</c>），可用 <see cref="KeySelector"/> 改写。
/// 每个维度一把锁、互不阻塞；跟踪的维度数有软上限（<see cref="MaxTrackedHosts"/>）：
/// 满员时只淘汰「丢了不改变判定」的条目，正在熔断 / 半开探测的条目不会被丢，
/// 因此面向任意 host（多租户 / 动态域名）时不会无限增长，也不会悄悄放行已知不可用的下游。
/// </para>
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

    /// <summary>
    /// 连续多少次下游故障后打开熔断，默认 5。
    /// <para>
    /// 是<b>连续</b>计数：中间任何一次成功都会归零（见 <see cref="Record"/>）。
    /// 开着重试时每次尝试各记一笔 —— 3 次重试 + 首次失败就是 4 笔，这是有意的：
    /// 一个请求连撞 4 次 5xx，本来就该判定下游不可用。
    /// </para>
    /// </summary>
    public int FailureThreshold { get; set; } = 5;

    /// <summary>
    /// 打开后多久转入半开探测，默认 30 秒。
    /// <para>
    /// 冷却期内请求<b>不发出</b>（直接失败），这是熔断真正省下的那部分压力：
    /// 重试是「再试一次」，冷却期是「先别试」。
    /// </para>
    /// </summary>
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 半开态允许同时进行的探测请求数，默认 1。
    /// <para>
    /// 大于 1 会让恢复更快，但也意味着下游刚有起色时要同时承受多个探测；对「刚被打挂」的服务，
    /// 1 个探测 + 成功即恢复通常已经够快（探测本身就是一个正常请求）。
    /// </para>
    /// </summary>
    public int HalfOpenAttempts { get; set; } = 1;

    /// <summary>
    /// 最多跟踪多少个维度（host），默认 512。
    /// <para>
    /// 达到上限时只淘汰「丢了不改变判定」的条目：闭合态优先（最坏重新攒失败计数），
    /// 其次是冷却早已过期的打开态。正在熔断 / 半开探测中的条目不会被淘汰 ——
    /// 宁可短暂超出上限，也不让刚判定为不可用的下游被悄悄放行。
    /// </para>
    /// </summary>
    public int MaxTrackedHosts { get; set; } = 512;

    /// <summary>
    /// 自定义计数维度；<c>null</c>（默认）用 <see cref="AxiosCircuitContext.Host"/>（即 <c>host:port</c>）。
    /// <para>
    /// 典型用法：<c>KeySelector = ctx =&gt; ctx.Uri.Host</c>（同一 host 的多个端口算一个下游）、
    /// <c>ctx =&gt; ctx.Uri.Host + ctx.Uri.AbsolutePath.Split('/')[1]</c>（按租户 / 路由前缀隔离）。
    /// 返回 <c>null</c> 或空串时回退到 <see cref="AxiosCircuitContext.Host"/>。
    /// </para>
    /// </summary>
    public Func<AxiosCircuitContext, string>? KeySelector { get; set; }

    /// <summary>
    /// 状态迁移回调（<see cref="AxiosCircuitState.Closed"/> → 打开、打开 → 半开、半开 → 恢复）。
    /// <para>
    /// 只在<b>真的发生迁移</b>时触发，且在工作线程上同步执行。订阅方抛出的异常会被忽略 ——
    /// 观测代码不该把请求变成失败（与库内遥测的处理一致）。
    /// </para>
    /// </summary>
    public event Action<AxiosCircuitTransition>? StateChanged;

    /// <summary>当前跟踪的维度数（用于观测 / 断言上限生效）。</summary>
    public int TrackedHostCount => _hosts.Count;

    /// <summary>
    /// 某个维度当前的状态；未跟踪过（= 从未失败）的维度返回 <see cref="AxiosCircuitState.Closed"/>。
    /// </summary>
    public AxiosCircuitState GetState(string host)
        => host is not null && _hosts.TryGetValue(host, out var state)
            ? state.State
            : AxiosCircuitState.Closed;

    /// <summary>
    /// 清掉<b>所有</b>维度的状态（等价于「立刻全部恢复」）：运维手工放行、或测试用例之间重置。
    /// <para>
    /// 处于打开 / 半开态的维度会触发一次 <see cref="StateChanged"/>（Open|HalfOpen → Closed）：
    /// 状态确实变了，订阅方（告警、看板）该看到这次恢复。
    /// </para>
    /// </summary>
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

    /// <summary>清掉<b>某一个</b>维度的状态；返回该维度此前是否被跟踪过（打开 / 半开态同样会触发 <see cref="StateChanged"/>）。</summary>
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
                        // 冷却期未过：不放行。这一步省掉的是「建连 + 等待超时」的全部代价。
                        allowed = false;
                        break;
                    }

                    // 冷却结束：转入半开，只放探测请求。名额在下面统一领取。
                    host.ProbesInFlight = 0;
                    transition = MoveTo(host, AxiosCircuitState.HalfOpen);
                    goto case AxiosCircuitState.HalfOpen;

                case AxiosCircuitState.HalfOpen:
                    allowed = host.ProbesInFlight < HalfOpenAttempts;
                    if (allowed)
                    {
                        // 名额在这里领走：随后那次 Record 负责归还（成功/失败会重置整个状态）。
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
            // 没经过 ShouldAllow（自定义调用顺序），或条目已被淘汰：没有可记的账。
            return;
        }

        var transition = default(AxiosCircuitTransition);

        lock (host.Gate)
        {
            if (context.IsSuccess)
            {
                if (host.State == AxiosCircuitState.HalfOpen)
                {
                    // 探测成功：整条链路恢复，失败计数清零。
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
                    // 探测失败：立刻重新打开并重新计时，不必再攒够 FailureThreshold 次。
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

                // 打开态：冷却期内还在途的请求报上来的失败不改账 ——
                // 它描述的是「开闸之前那一刻」，让它续期只会把冷却期无限拉长。
            }
            else if (host.State == AxiosCircuitState.HalfOpen && host.ProbesInFlight > 0)
            {
                // 取消 / 4xx：既不计数也不清零（既不能证明恢复，也不能证明挂了），
                // 但半开态的探测名额必须归还 —— 否则一次被取消的探测会把熔断器永久卡在半开。
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

        // 上限只在「新建」这条冷路径上检查：命中已有维度不受影响。
        if (_hosts.Count >= (MaxTrackedHosts > 0 ? MaxTrackedHosts : 1))
        {
            Evict();
        }

        return _hosts.GetOrAdd(key, static name => new HostState(name));
    }

    /// <summary>
    /// 淘汰一个维度，给新维度腾位置。
    /// <para>
    /// 只淘汰「丢了不改变判定」的条目：<b>闭合态</b>（最坏是重新攒失败计数），以及
    /// <b>冷却早已过期的打开态</b>（它下次被访问本来就会转半开探测，丢掉等价）。
    /// </para>
    /// <para>
    /// <b>正在熔断中（冷却未过）与半开探测中的条目一律不淘汰</b> —— 抹掉它等于让刚判定为不可用的
    /// 下游悄悄恢复放行，这是熔断最不该发生的事。一个可淘汰的都没有时什么都不做：
    /// 上限退化成软上限，宁可短暂超出也不丢账。
    /// </para>
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
                // 最理想的目标已经找到（闭合且无账可记），不必再看其余条目
                break;
            }
        }

        var victim = closedIdle ?? closed ?? staleOpen;
        if (victim is not null)
        {
            _hosts.TryRemove(victim, out _);
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
            // 观测代码抛异常不该改变请求结果（与库内遥测回调同一约定）。
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
