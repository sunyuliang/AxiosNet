using AxiosNet.Pooling;

namespace AxiosNet;

/// <summary>
/// 实例级选项：只在构造 <see cref="AxiosClient"/> 时生效，构造期固化，之后改不会重建任何资源。
/// </summary>
/// <remarks>
/// <para>
/// 这些选项决定的是<b>连接层</b>与<b>闸门容量</b>的形态：重定向 / Cookie / 代理 / 凭据由连接层 Handler 执行，
/// 在建池时按选项写进 Handler；在途上限决定并发闸门的容量，也在构造期定死。两者都无法在请求发出时改，
/// 所以它们不在 <see cref="AxiosConfig"/> 上 —— 想写在单次请求里没有入口，编译期就拒绝。
/// </para>
/// <para>
/// 需要在同一进程里用不同出口或不同并发上限，请建多个 <see cref="AxiosClient"/>：
/// 连接池可以共享（构造参数 <c>pool</c>），每个客户端各自带自己的闸门与选项。
/// </para>
/// <para>
/// 按请求可变的那一半仍在 <see cref="AxiosConfig"/> 上：<see cref="AxiosConfig.TransportSettings.Profile"/>（传输档）、
/// <see cref="AxiosConfig.ConcurrencySettings.QueueTimeout"/>（排队等待上限）、<see cref="AxiosConfig.Timeout"/> 等。
/// </para>
/// </remarks>
public sealed class AxiosClientOptions
{
    /// <summary>未显式设置 <see cref="MaxRedirects"/> 时的生效值（与 <see cref="ConnectionPoolOptions"/> 的默认一致）。</summary>
    public const int DefaultMaxRedirects = 5;

    /// <summary>代理设置；注入自定义 <see cref="HttpMessageHandler"/> 时不生效（由 Handler 决定）。</summary>
    /// <remarks>与 <see cref="Pool"/> 里的 <see cref="ConnectionPoolOptions.Proxy"/> 互斥：两处都写会在构造期报错。</remarks>
    public IWebProxy? Proxy { get; set; }

    /// <summary>显式凭据；注入自定义 <see cref="HttpMessageHandler"/> 时不生效（由 Handler 决定）。</summary>
    /// <remarks>与 <see cref="Pool"/> 里的 <see cref="ConnectionPoolOptions.Credentials"/> 互斥。</remarks>
    public ICredentials? Credentials { get; set; }

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
            _maxRedirects = value;
            MaxRedirectsSet = true;
        }
    }

    /// <summary>是否被显式赋值过。</summary>
    internal bool MaxRedirectsSet { get; set; }

    /// <summary>
    /// 自建连接池的选项；与构造参数 <c>pool</c> / <c>poolOptions</c> 互斥（同时给会报错），
    /// 与 <see cref="Proxy"/> / <see cref="Credentials"/> 同名项也互斥。注入自定义 Handler 时本项无效。
    /// </summary>
    public ConnectionPoolOptions? Pool { get; set; }

    /// <summary>
    /// 实例内在途请求上限（并发闸门容量），<c>null</c>（默认）不限；必须是正整数，0 / 负数在构造期报错。
    /// </summary>
    /// <remarks>
    /// 闸门在首个受限请求时创建，容量取<b>构造期</b>的本值并固定（构造之后再改它也不影响容量）。
    /// 配了对冲时容量要按 <c>1 + MaxHedges</c> 倍预留。
    /// 排队等待的时长不在这里（那个值按请求可变），见 <see cref="AxiosConfig.ResilienceSettings.Concurrency"/>。
    /// </remarks>
    public int? MaxInFlight { get; set; }

    /// <summary>等待槽位的请求条数上限，只在设置 <see cref="MaxInFlight"/> 时有意义；必须是正整数或 <c>null</c>。</summary>
    public int? MaxQueue { get; set; }

    /// <summary>浅拷贝（客户端持自己的一份快照，之后改调用方对象不影响已建资源）。</summary>
    internal AxiosClientOptions Clone() => (AxiosClientOptions)MemberwiseClone();

    /// <summary>
    /// 构造期校验：并发上限 / 排队上限必须是正整数。这两项与 Handler 无关（闸门是库自己的），所以无条件校验；
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
    }

    /// <summary>
    /// 把连接池<b>实际生效</b>的出口写回本对象，让 <see cref="AxiosClient.Options"/> 的读者看到真实行为
    /// （池可能被 <c>poolOptions</c> / 传输档 / 注入的 <c>pool</c> 改写）。
    /// </summary>
    /// <remarks>
    /// 回写不是「调用方显式设置」，因此顺手清掉两个 <c>*Set</c> 标记。
    /// 这里的值只在 <see cref="AxiosClient.Options"/> 快照上生效，不会反向影响已建好的池。
    /// </remarks>
    /// <param name="effective">池的生效选项。</param>
    internal void AbsorbPoolEffective(ConnectionPoolOptions effective)
    {
        Proxy = effective.Proxy;
        Credentials = effective.Credentials;
        MaxRedirects = effective.AllowAutoRedirect ? effective.MaxAutomaticRedirections : 0;
        WithCredentials = effective.UseCookies;

        MaxRedirectsSet = false;
        WithCredentialsSet = false;
    }
}
