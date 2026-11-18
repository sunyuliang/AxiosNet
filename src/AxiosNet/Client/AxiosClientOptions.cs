using AxiosNet.Pooling;

namespace AxiosNet;

/// <summary>
/// 实例级选项：决定连接层与闸门容量的形态，只在构造 <see cref="AxiosClient"/> 时生效。
/// </summary>
/// <remarks>
/// 构造时取一份副本并冻结（见 <see cref="AxiosClient.Options"/>），再改一律抛 <c>ERR_BAD_OPTION_VALUE</c>。
/// 按请求可变的项在 <see cref="AxiosConfig"/> 上：<see cref="AxiosConfig.TransportSettings.Profile"/>、
/// <see cref="AxiosConfig.ConcurrencySettings.QueueTimeout"/>、<see cref="AxiosConfig.Timeout"/>。
/// 同一进程要用不同出口或并发上限时建多个 <see cref="AxiosClient"/>，连接池可用构造参数共享。
/// </remarks>
public sealed class AxiosClientOptions
{
    /// <summary>未显式设置 <see cref="MaxRedirects"/> 时的生效值（与 <see cref="ConnectionPoolOptions"/> 的默认一致）。</summary>
    public const int DefaultMaxRedirects = 5;

    /// <summary>已冻结标记：客户端构造完之后置位，之后任何写入都报错。</summary>
    private bool _frozen;

    private IWebProxy? _proxy;

    /// <summary>代理设置；注入自定义 <see cref="HttpMessageHandler"/> 时不生效（由 Handler 决定）。</summary>
    /// <remarks>与 <see cref="Pool"/> 里的 <see cref="ConnectionPoolOptions.Proxy"/> 互斥：两处都写会在构造期报错。</remarks>
    public IWebProxy? Proxy
    {
        get => _proxy;
        set
        {
            EnsureMutable();
            _proxy = value;
        }
    }

    private ICredentials? _credentials;

    /// <summary>显式凭据；注入自定义 <see cref="HttpMessageHandler"/> 时不生效（由 Handler 决定）。</summary>
    /// <remarks>与 <see cref="Pool"/> 里的 <see cref="ConnectionPoolOptions.Credentials"/> 互斥。</remarks>
    public ICredentials? Credentials
    {
        get => _credentials;
        set
        {
            EnsureMutable();
            _credentials = value;
        }
    }

    private bool _withCredentials;

    /// <summary>
    /// 对应 axios withCredentials（使用系统默认凭据 / Cookie 容器），默认 <c>false</c>。
    /// 为 <c>true</c> 时等价于在池上设 <see cref="ConnectionPoolOptions.UseCookies"/> 并把
    /// <see cref="Credentials"/> 兜底为 <see cref="CredentialCache.DefaultCredentials"/>。
    /// </summary>
    public bool WithCredentials
    {
        get => _withCredentials;
        set
        {
            EnsureMutable();
            _withCredentials = value;
            WithCredentialsSet = true;
        }
    }

    /// <summary>是否被显式赋值过；与池上的 <c>UseCookies</c> 同时出现时用来判断是否重复配置。</summary>
    internal bool WithCredentialsSet { get; set; }

    private int _maxRedirects = DefaultMaxRedirects;

    /// <summary>对应 axios maxRedirects，默认 5，<c>0</c> 表示不跟随重定向；负数在构造期报错。</summary>
    public int MaxRedirects
    {
        get => _maxRedirects;
        set
        {
            EnsureMutable();
            _maxRedirects = value;
            MaxRedirectsSet = true;
        }
    }

    /// <summary>是否被显式赋值过。</summary>
    internal bool MaxRedirectsSet { get; set; }

    private ConnectionPoolOptions? _pool;

    /// <summary>
    /// 自建连接池的选项；与构造参数 <c>pool</c> / <c>poolOptions</c> 互斥（同时给会报错），
    /// 与 <see cref="Proxy"/> / <see cref="Credentials"/> 同名项也互斥。注入自定义 Handler 时本项无效。
    /// </summary>
    /// <remarks>
    /// <see cref="AxiosClient.Options"/> 上的这一项是<b>回填的生效快照</b>：即使构造时用的是 <c>poolOptions</c>
    /// 或共享池，这里也能读到那个池的真实选项（而不是 <c>null</c>）。
    /// </remarks>
    public ConnectionPoolOptions? Pool
    {
        get => _pool;
        set
        {
            EnsureMutable();
            _pool = value;
        }
    }

    private int? _maxInFlight;

    /// <summary>
    /// 实例内在途请求上限（并发闸门容量），<c>null</c>（默认）不限；必须是正整数，0 / 负数在构造期报错。
    /// </summary>
    /// <remarks>
    /// 闸门在首个受限请求时创建，容量取<b>构造期</b>的本值并固定（构造之后再改它也不影响容量）。
    /// 配了对冲时容量要按 <c>1 + MaxHedges</c> 倍预留。
    /// 排队等待的时长不在这里（那个值按请求可变），见 <see cref="AxiosConfig.ResilienceSettings.Concurrency"/>。
    /// </remarks>
    public int? MaxInFlight
    {
        get => _maxInFlight;
        set
        {
            EnsureMutable();
            _maxInFlight = value;
        }
    }

    private int? _maxQueue;

    /// <summary>
    /// 等待槽位的请求条数上限，<b>只在设置了 <see cref="MaxInFlight"/> 时有意义</b>；
    /// 必须是正整数或 <c>null</c>。
    /// </summary>
    /// <remarks>只设本项而不设在途上限会在构造期报错：没有闸门就没有队列，静默无效比报错更难查。</remarks>
    public int? MaxQueue
    {
        get => _maxQueue;
        set
        {
            EnsureMutable();
            _maxQueue = value;
        }
    }

    /// <summary>
    /// 复制一份选项（浅拷贝：<see cref="Pool"/> 等引用成员按引用共享）。
    /// </summary>
    /// <returns>未冻结的新副本，可继续改；用于「注册时先留一份、构造时再给客户端」。</returns>
    /// <remarks>
    /// <see cref="AxiosClient"/> 构造时会自己调一次：调用方随后改传进去的对象不影响已建资源。
    /// 容器 / 门面这类「先登记、后惰性创建」的入口应显式调用本方法，避免登记到创建之间被改。
    /// </remarks>
    public AxiosClientOptions Clone()
    {
        var copy = (AxiosClientOptions)MemberwiseClone();

        // 副本回到「构造期还在填」的状态：它可能来自一个已经冻结的 Options（例如拿旧实例的选项建新实例）。
        copy._frozen = false;
        return copy;
    }

    /// <summary>
    /// 冻结：之后的写入一律报错。
    /// </summary>
    /// <remarks>由 <see cref="AxiosClient"/> 在构造末尾调用，此前 <see cref="AbsorbPoolEffective"/> 已把生效值回填完。</remarks>
    internal void Freeze() => _frozen = true;

    /// <summary>
    /// 构造期校验：并发上限 / 排队上限必须是正整数，且排队深度必须挂在在途上限上。
    /// 这两项与 Handler 无关（闸门是库自己的），所以无条件校验；
    /// <see cref="MaxRedirects"/> 的取值范围由连接层分支各自把关（注入 Handler / 共享池时它压根不生效）。
    /// </summary>
    /// <param name="config">报错时带上实例配置，便于日志定位。</param>
    internal void ValidateConcurrency(AxiosConfig config)
    {
        if (MaxInFlight is int maxInFlight && maxInFlight < 1)
        {
            throw AxiosException.InvalidOption(
                $"AxiosClientOptions.MaxInFlight 必须是正整数（当前 {maxInFlight}）：不限制请设为 null，"
                + "要「不排队」请把 Resilience.Concurrency.QueueTimeout 设为 TimeSpan.Zero。",
                config);
        }

        if (MaxQueue is int maxQueue && maxQueue < 1)
        {
            throw AxiosException.InvalidOption(
                $"AxiosClientOptions.MaxQueue 必须是正整数（当前 {maxQueue}）：不限制排队深度请设为 null，"
                + "要「不排队」请把 Resilience.Concurrency.QueueTimeout 设为 TimeSpan.Zero。",
                config);
        }

        // 没有闸门就没有队列：只写排队深度等于什么都没配，报错而不是静默忽略。
        if (MaxQueue is not null && MaxInFlight is null)
        {
            throw AxiosException.InvalidOption(
                $"AxiosClientOptions.MaxQueue = {MaxQueue} 需要同时设置 MaxInFlight：排队深度只在有并发闸门时可言，"
                + "不限制在途条数时请把 MaxQueue 设为 null。",
                config);
        }
    }

    /// <summary>
    /// 把连接池<b>实际生效</b>的出口写回本对象，让 <see cref="AxiosClient.Options"/> 的读者看到真实行为
    /// （池可能被 <c>poolOptions</c> / 传输档 / 注入的 <c>pool</c> 改写）。
    /// </summary>
    /// <remarks>
    /// 回写不是「调用方显式设置」，因此直接写字段（绕过冻结）并清掉两个 <c>*Set</c> 标记。
    /// 这里的值只在 <see cref="AxiosClient.Options"/> 快照上生效，不会反向影响已建好的池。
    /// </remarks>
    /// <param name="effective">池的生效选项。</param>
    internal void AbsorbPoolEffective(ConnectionPoolOptions effective)
    {
        _proxy = effective.Proxy;
        _credentials = effective.Credentials;
        _maxRedirects = effective.AllowAutoRedirect ? effective.MaxAutomaticRedirections : 0;
        _withCredentials = effective.UseCookies;

        // 自建池 / 共享池 / 传输档都可能改变池的形态：快照里放生效的那份，
        // 否则读者看到 Pool = null，会以为这个实例没有池选项。
        _pool = effective.Clone();

        MaxRedirectsSet = false;
        WithCredentialsSet = false;
    }

    /// <summary>冻结后写入的统一出口：报错而不是静默无效。</summary>
    /// <exception cref="AxiosException">已冻结（<c>ERR_BAD_OPTION_VALUE</c>）。</exception>
    private void EnsureMutable()
    {
        if (!_frozen)
        {
            return;
        }

        throw AxiosException.InvalidOption(
            "AxiosClient.Options 是构造后的只读快照：这些选项决定连接层与并发闸门的形态，构造期已固化，"
            + "改它不会重建任何资源。要换出口或容量请新建 AxiosClient（在一份新的 AxiosClientOptions 上设好再构造）。");
    }
}
