namespace AxiosNet.Pooling;

/// <summary>代理凭据兜底：代理自身没有凭据时，回落到实例级 <c>Credentials</c>。</summary>
/// <remarks>
/// <c>SocketsHttpHandler.Credentials</c> / <c>HttpClientHandler.Credentials</c> 只用于<b>源站</b>认证；
/// 代理 407 只认 <see cref="IWebProxy.Credentials"/>。把「代理地址 + 凭据」并排配置是常见用法，
/// 不做这层回落就会静默吃 407。<b>包一层而不是改调用方传入的实例</b>，代理自己配了凭据时一律以它为准。
/// </remarks>
internal sealed class ProxyCredentialsFallback : IWebProxy
{
    // 调用方的代理实例，只转发不改写。
    private readonly IWebProxy _inner;

    // 代理自己没配凭据时使用的实例级凭据。
    private readonly ICredentials _fallback;

    /// <summary>包一层代理，并给它一个凭据兜底。</summary>
    /// <param name="inner">调用方的代理实例。</param>
    /// <param name="fallback">代理没有凭据时使用的实例级凭据。</param>
    public ProxyCredentialsFallback(IWebProxy inner, ICredentials fallback)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    /// <summary>代理自己的凭据优先，其次回落到实例级凭据；赋值原样转给被包的代理。</summary>
    public ICredentials? Credentials
    {
        get => _inner.Credentials ?? _fallback;
        set => _inner.Credentials = value;
    }

    /// <summary>转发：目标地址该走哪个代理。</summary>
    /// <param name="destination">目标地址。</param>
    /// <returns>被包代理给出的代理地址。</returns>
    public Uri? GetProxy(Uri destination) => _inner.GetProxy(destination);

    /// <summary>转发：目标地址是否绕过代理。</summary>
    /// <param name="host">目标地址。</param>
    /// <returns>被包代理给出的判定。</returns>
    public bool IsBypassed(Uri host) => _inner.IsBypassed(host);
}
