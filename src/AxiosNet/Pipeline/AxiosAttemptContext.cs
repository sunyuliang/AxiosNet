
namespace AxiosNet.Pipeline;

/// <summary>一次尝试的上下文：<b>不可变结构体，按值传递</b>；中间件只能用 <c>WithXxx</c> 派生副本。</summary>
/// <remarks>用结构体是因为并发类中间件要按分支拷贝上下文，且 async 状态机无法捕获 <c>in</c> 参数。</remarks>
public readonly struct AxiosAttemptContext
{
    private AxiosAttemptContext(
        AxiosConfig config,
        Uri uri,
        int attemptNumber,
        int endpointIndex,
        CancellationToken attemptToken,
        CancellationToken externalToken,
        Type? dataType,
        AxiosResponse? target,
        bool streamResponse,
        AxiosConfig.RetrySettings? retry,
        AxiosConfig.CircuitSettings? circuit,
        AxiosConfig.HedgingSettings? hedging,
        AxiosConfig.FailoverSettings? failover,
        ConcurrencyGate? gate,
        IDisposable? lease,
        long deadlineTimestamp,
        FailoverHealth? failoverHealth)
    {
        Config = config;
        Uri = uri;
        AttemptNumber = attemptNumber;
        EndpointIndex = endpointIndex;
        AttemptToken = attemptToken;
        ExternalToken = externalToken;
        DataType = dataType;
        Target = target;
        StreamResponse = streamResponse;
        Retry = retry;
        Circuit = circuit;
        Hedging = hedging;
        Failover = failover;
        _gate = gate;
        Lease = lease;
        _deadlineTimestamp = deadlineTimestamp;
        _failoverHealth = failoverHealth;
    }

    private readonly ConcurrencyGate? _gate;

    /// <summary>备用端点健康表（客户端级共享状态；注入自定义 Handler 时同样存在，只是没有连接池可换）。</summary>
    private readonly FailoverHealth? _failoverHealth;

    /// <summary>总预算的绝对时间戳（<see cref="TotalTimeoutBudget.None"/> = 没设），一次请求只算一次。</summary>
    private readonly long _deadlineTimestamp;

    /// <summary>唯一创建入口：流式标记与各子配置在这里一次算清。</summary>
    internal static AxiosAttemptContext From(
        AxiosConfig config,
        Uri uri,
        Type? dataType,
        AxiosResponse? target,
        ConcurrencyGate? gate,
        long deadlineTimestamp,
        FailoverHealth? failoverHealth)
    {
        var retry = config.RetryOrNull;
        var externalToken = config.CancellationToken;

        return new AxiosAttemptContext(
            config,
            uri,
            1,
            0,
            externalToken,
            externalToken,
            dataType,
            target,
            config.ResponseType == ResponseType.Stream,
            retry,
            config.CircuitOrNull,
            config.HedgingOrNull,
            config.FailoverOrNull,
            gate,
            null,
            deadlineTimestamp,
            failoverHealth);
    }

    /// <summary>本次请求的生效配置（合并 + 拦截器之后的最终形态）。</summary>
    public AxiosConfig Config { get; }

    /// <summary>最终 URL：错误上下文、熔断计数维度与遥测标签都用它。</summary>
    public Uri Uri { get; }

    /// <summary>第几次尝试，从 <c>1</c> 开始；只有重试中间件会推进它。</summary>
    public int AttemptNumber { get; }

    /// <summary>第几个端点，从 <c>0</c> 开始：<c>0</c> = 请求自己的地址，<c>n</c> = 第 n 个备用端点；只有备用端点中间件会推进它。</summary>
    public int EndpointIndex { get; }

    /// <summary>本次尝试的令牌：默认同 <see cref="ExternalToken"/>，对冲给每条分支各换一份。</summary>
    public CancellationToken AttemptToken { get; }

    /// <summary>调用方令牌：贯穿所有尝试与退避等待。</summary>
    public CancellationToken ExternalToken { get; }

    /// <summary>泛型入口的目标类型（非泛型入口为 <c>null</c>），决定反序列化成什么。</summary>
    public Type? DataType { get; }

    /// <summary>调用方预造的响应对象：管线就地填充，省掉一次类型转换与再分配。</summary>
    public AxiosResponse? Target { get; }

    /// <summary>是否流式响应（<c>ResponseType == Stream</c>）。</summary>
    public bool StreamResponse { get; }

    /// <summary>重试配置：<c>null</c> = 不重试（默认）；设了但 <see cref="AxiosConfig.RetrySettings.Policy"/> 为 <c>null</c> 或 <c>MaxRetries &lt;= 0</c> 时同样不重试。</summary>
    public AxiosConfig.RetrySettings? Retry { get; }

    /// <summary>熔断配置：<c>null</c> = 不熔断（默认）；设了但 <see cref="AxiosConfig.CircuitSettings.Policy"/> 为 <c>null</c> 时同样不熔断。</summary>
    public AxiosConfig.CircuitSettings? Circuit { get; }

    /// <summary>对冲配置：<c>null</c> = 未配置（默认）；<see cref="AxiosConfig.HedgingSettings.MaxHedges"/> 为 0 时同样不对冲。</summary>
    public AxiosConfig.HedgingSettings? Hedging { get; }

    /// <summary>备用端点配置：<c>null</c> = 未配置（默认）；端点表为空时同样不换端点。</summary>
    public AxiosConfig.FailoverSettings? Failover { get; }

    /// <summary>并发闸门：<c>null</c> = 本次不限并发。</summary>
    internal ConcurrencyGate? Gate => _gate;

    /// <summary>备用端点的健康表（客户端级共享，跨请求记忆；<c>null</c> 表示不记忆）。</summary>
    internal FailoverHealth? FailoverHealth => _failoverHealth;

    /// <summary>本次尝试登记的跨终端租约：由中间件在调用 <c>next</c> 前登记，终端负责处置；<c>null</c> = 没有。</summary>
    public IDisposable? Lease { get; }

    /// <summary>距总预算耗尽还剩多久；<c>null</c> = 没设总预算，<see cref="TimeSpan.Zero"/> = 已耗尽。</summary>
    internal TimeSpan? RemainingTotalTimeout => TotalTimeoutBudget.Remaining(_deadlineTimestamp);

    /// <summary>区分两个「整数型」派生字段的私有标记：拷贝构造的重载只能靠参数类型区分。</summary>
    private enum IntField
    {
        /// <summary>派生时覆写尝试序号。</summary>
        AttemptNumber,

        /// <summary>派生时覆写端点序号。</summary>
        EndpointIndex
    }

    /// <summary>拷贝构造：不可变结构体的「派生」= 整份复制后再覆写一个字段。</summary>
    /// <param name="source">来源上下文。</param>
    private AxiosAttemptContext(in AxiosAttemptContext source) => this = source;

    /// <summary>派生：换一个整数字段（尝试序号 / 端点序号）。</summary>
    /// <param name="source">来源上下文。</param>
    /// <param name="field">要改的字段。</param>
    /// <param name="value">新值。</param>
    private AxiosAttemptContext(in AxiosAttemptContext source, IntField field, int value) : this(source)
    {
        if (field == IntField.AttemptNumber)
        {
            AttemptNumber = value;
        }
        else
        {
            EndpointIndex = value;
        }
    }

    /// <summary>派生：换本次尝试的令牌。</summary>
    /// <param name="source">来源上下文。</param>
    /// <param name="attemptToken">新的尝试令牌。</param>
    private AxiosAttemptContext(in AxiosAttemptContext source, CancellationToken attemptToken) : this(source)
        => AttemptToken = attemptToken;

    /// <summary>派生：换生效配置，重试 / 熔断 / 对冲 / 端点子设置一并从新配置取。</summary>
    /// <param name="source">来源上下文。</param>
    /// <param name="config">新的生效配置。</param>
    private AxiosAttemptContext(in AxiosAttemptContext source, AxiosConfig config) : this(source)
    {
        Config = config;
        Retry = config.RetryOrNull;
        Circuit = config.CircuitOrNull;
        Hedging = config.HedgingOrNull;
        Failover = config.FailoverOrNull;
    }

    /// <summary>派生：换（或摘掉）调用方预造的响应对象。</summary>
    /// <param name="source">来源上下文。</param>
    /// <param name="target">新的预造响应；<c>null</c> 表示摘掉。</param>
    private AxiosAttemptContext(in AxiosAttemptContext source, AxiosResponse? target) : this(source)
        => Target = target;

    /// <summary>派生：换目标 URL。</summary>
    /// <param name="source">来源上下文。</param>
    /// <param name="uri">新的目标 URL。</param>
    private AxiosAttemptContext(in AxiosAttemptContext source, Uri uri) : this(source)
        => Uri = uri;

    /// <summary>派生：换本次尝试登记的跨终端租约。</summary>
    /// <param name="source">来源上下文。</param>
    /// <param name="lease">新的租约；<c>null</c> 表示没有。</param>
    private AxiosAttemptContext(in AxiosAttemptContext source, IDisposable? lease) : this(source)
        => Lease = lease;

    /// <summary>派生：换一个尝试序号（重试中间件推进尝试时用）。</summary>
    /// <param name="attemptNumber">新的尝试序号（从 <c>1</c> 开始）。</param>
    /// <returns>派生出的副本。</returns>
    public AxiosAttemptContext WithAttemptNumber(int attemptNumber)
        => new(in this, IntField.AttemptNumber, attemptNumber);

    /// <summary>派生：换本次尝试的令牌（对冲给每条分支单独设令牌时用）。</summary>
    /// <param name="attemptToken">新的尝试令牌。</param>
    /// <returns>派生出的副本。</returns>
    public AxiosAttemptContext WithAttemptToken(CancellationToken attemptToken)
        => new(in this, attemptToken);

    /// <summary>派生：换本次尝试的生效配置，子设置一并从新配置取。</summary>
    /// <param name="config">新的生效配置。</param>
    /// <returns>派生出的副本。</returns>
    internal AxiosAttemptContext WithConfig(AxiosConfig config) => new(in this, config);

    /// <summary>派生：摘掉调用方预造的响应对象。</summary>
    /// <returns>派生出的副本（<see cref="Target"/> 为 <c>null</c>）。</returns>
    internal AxiosAttemptContext WithoutTarget() => new(in this, (AxiosResponse?)null);

    /// <summary>派生：换目标 URL，同时换掉连接租约、熔断维度与遥测标签用的地址。</summary>
    /// <param name="uri">新的目标 URL。</param>
    /// <returns>派生出的副本。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> 为 <c>null</c>。</exception>
    public AxiosAttemptContext WithUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return new(in this, uri);
    }

    /// <summary>派生：换端点序号（备用端点中间件切到第 n 个备用端点时用，配合 <see cref="WithUri"/>）。</summary>
    /// <param name="endpointIndex">新的端点序号（从 <c>0</c> 开始）。</param>
    /// <returns>派生出的副本。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="endpointIndex"/> 为负。</exception>
    public AxiosAttemptContext WithEndpointIndex(int endpointIndex)
    {
        if (endpointIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(endpointIndex), endpointIndex, "端点序号不能为负。");
        }

        return new(in this, IntField.EndpointIndex, endpointIndex);
    }

    /// <summary>派生：登记一个跨终端租约；已登记过则追加（按登记逆序释放），不覆盖先登记的。</summary>
    /// <remarks>登记后所有权归终端：失败路径它处置、流式成功路径随正文转交；但终端只在被调用时兜底，登记方自己抛异常或决定不调 <c>next</c> 时必须自行处置。</remarks>
    /// <param name="lease">要登记的租约（不要传 <c>null</c>）。</param>
    /// <returns>派生出的副本。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="lease"/> 为 <c>null</c>。</exception>
    public AxiosAttemptContext WithLease(IDisposable lease)
    {
        ArgumentNullException.ThrowIfNull(lease);

        // 第一个裸放（零分配）。
        if (Lease is null)
        {
            return new(in this, lease);
        }

        // 第二个起合成链表：最新在前，Dispose 即逆序。
        var earlier = Lease as AxiosAttemptLeases ?? new AxiosAttemptLeases(Lease, null);
        return new(in this, new AxiosAttemptLeases(lease, earlier));
    }
}
