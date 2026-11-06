using AxiosNet.Internal;

namespace AxiosNet;

/// <summary>
/// 凭据来源的唯一槽位（<see cref="AxiosConfig.TransportSettings.Auth"/>）：静态 Basic、静态令牌、完整头值，
/// 或每请求调用一次的提供方。一个请求只能配一种形态。
/// </summary>
/// <remarks>
/// <para>对应 axios 的 <c>auth</c>。显式 <c>Headers.Authorization</c> 与本槽位同时设置视为配置冲突，发出前报
/// <c>ERR_BAD_OPTION_VALUE</c>；只设其一时按该来源落头。</para>
/// <para>凭据 → <c>Authorization</c> 头的映射只由内核实现；WebAPI 的声明式提供方仅作为适配器包成
/// <see cref="IAxiosCredentialProvider"/>，不自己拼头。</para>
/// </remarks>
public sealed class AxiosCredential
{
    private enum Form
    {
        Basic,
        Bearer,
        Header,
        Provider,
    }

    private readonly Form _form;
    private readonly AxiosAuth? _basic;
    private readonly string? _token;
    private readonly string? _scheme;
    private readonly string? _headerValue;
    private readonly IAxiosCredentialProvider? _provider;

    private AxiosCredential(Form form, AxiosAuth? basic = null, string? token = null, string? scheme = null, string? headerValue = null, IAxiosCredentialProvider? provider = null)
    {
        _form = form;
        _basic = basic;
        _token = token;
        _scheme = scheme;
        _headerValue = headerValue;
        _provider = provider;
    }

    /// <summary>静态 HTTP Basic（用户名 + 密码）。</summary>
    /// <param name="username">用户名，不能含冒号（RFC 7617）。</param>
    /// <param name="password">密码。</param>
    public static AxiosCredential Basic(string username, string password)
        => Basic(new AxiosAuth(username, password));

    /// <summary>静态 HTTP Basic（已有的 <see cref="AxiosAuth"/> 值）。</summary>
    /// <param name="auth">用户名与密码。</param>
    /// <exception cref="ArgumentNullException"><paramref name="auth"/> 为 <c>null</c>。</exception>
    public static AxiosCredential Basic(AxiosAuth auth)
    {
        ArgumentNullException.ThrowIfNull(auth);
        return new AxiosCredential(Form.Basic, basic: auth);
    }

    /// <summary>静态令牌：落 <c>{scheme} {token}</c> 头，scheme 为空时按 <c>Bearer</c>。</summary>
    /// <param name="token">令牌本体，不能是空白。</param>
    /// <param name="scheme">认证方案名，默认 <c>Bearer</c>（如 <c>[Authorize("Token")]</c> 就用 <c>"Token"</c>）。</param>
    /// <exception cref="ArgumentException"><paramref name="token"/> 为空白。</exception>
    public static AxiosCredential Bearer(string token, string? scheme = null)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("令牌不能为空白：要表达「本次不带凭据」请把 Transport.Auth 设为 null。", nameof(token));
        }

        return new AxiosCredential(Form.Bearer, token: token, scheme: scheme);
    }

    /// <summary>完整 <c>Authorization</c> 头值：原样发送，不做 scheme 拼接。</summary>
    /// <param name="value">形如 <c>Basic dXNlcjpwYXNz</c> 的完整头值，不能是空白。</param>
    /// <exception cref="ArgumentException"><paramref name="value"/> 为空白。</exception>
    public static AxiosCredential Header(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Authorization 头值不能为空白：要表达「本次不带凭据」请把 Transport.Auth 设为 null。", nameof(value));
        }

        return new AxiosCredential(Form.Header, headerValue: value);
    }

    /// <summary>每请求调用一次的提供方：内核每次发送前取一次凭据并落头。</summary>
    /// <param name="provider">内核中性提供方（WebAPI 的声明式凭据提供方由库包成它）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> 为 <c>null</c>。</exception>
    public static AxiosCredential Provider(IAxiosCredentialProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new AxiosCredential(Form.Provider, provider: provider);
    }

    /// <summary>该来源是否支持 401 后刷新重放（只有提供方形态且提供方声明可刷新时为 <c>true</c>）。</summary>
    public bool IsRefreshable
        => _form == Form.Provider && _provider!.CanRefresh;

    /// <summary>槽位是否是提供方形态：内核据此把 401 的重试交给上层（不需要认识声明式层的提供方类型）。</summary>
    internal bool IsProvider => _form == Form.Provider;

    /// <summary>
    /// 静态形态的认证方案名（Basic 固定 <c>Basic</c>，令牌用显式 scheme 或默认 <c>Bearer</c>，
    /// 完整头值取第一段）；提供方形态返回 <c>null</c>，由提供方在解析后决定。
    /// </summary>
    internal string? Scheme
        => _form switch
        {
            Form.Basic => "Basic",
            Form.Bearer => _scheme ?? "Bearer",
            Form.Header => HeaderScheme(_headerValue!),
            _ => null,
        };

    private static string? HeaderScheme(string value)
    {
        var separator = value.IndexOf(' ');
        return separator > 0 ? value[..separator] : null;
    }

    /// <summary>取本次请求实际生效的凭据：静态形态原样返回，提供方形态调用一次提供方。</summary>
    /// <param name="context">本次请求的配置上下文。</param>
    /// <param name="cancellationToken">调用方的取消令牌。</param>
    internal ValueTask<AxiosCredential?> ResolveAsync(AxiosCredentialContext context, CancellationToken cancellationToken)
        => _form == Form.Provider
            ? _provider!.GetAsync(context, cancellationToken)
            : ValueTask.FromResult<AxiosCredential?>(this);

    /// <summary>凭据 → <c>Authorization</c> 头值；提供方形态由 <see cref="ResolveAsync"/> 先解成静态形态。</summary>
    internal string? ToHeaderValue()
    {
        var value = _form switch
        {
            Form.Basic => AuthorizationHeader.FormatBasic(_basic!),
            Form.Bearer => AuthorizationHeader.FormatToken(_scheme, _token!),
            Form.Header => AuthorizationHeader.FormatComplete(_headerValue!),
            _ => null,
        };

        if (value is not null)
        {
            // 统一入口拦 CR/LF 等控制字符：显式头走 AxiosHeaders.Set 的校验，凭据槽位不能绕过。
            AxiosHeaders.ValidateHeaderValue(AuthorizationHeader.Name, value);
        }

        return value;
    }

    /// <summary>401 后作废提供方当前凭据（静态形态无事可做）。</summary>
    /// <param name="staleValue">刚才被拒的令牌值，<c>null</c> 表示无条件作废。</param>
    internal void Invalidate(string? staleValue) => _provider?.Invalidate(staleValue);
}

/// <summary>内核中性凭据提供方：不依赖声明式层，每请求调用一次。</summary>
public interface IAxiosCredentialProvider
{
    /// <summary>取本次请求的凭据；返回 <c>null</c> 表示本次不写头。</summary>
    /// <param name="context">本次请求的配置上下文。</param>
    /// <param name="cancellationToken">调用方的取消令牌。</param>
    ValueTask<AxiosCredential?> GetAsync(AxiosCredentialContext context, CancellationToken cancellationToken);

    /// <summary>该来源是否支持刷新（401 重放仍由调用方按策略决定）。</summary>
    bool CanRefresh { get; }

    /// <summary>作废当前凭据，让下一次取凭据拿到新值；默认空实现。</summary>
    /// <param name="staleValue">刚才被拒的值，<c>null</c> 表示无条件作废。</param>
    void Invalidate(string? staleValue = null)
    {
    }
}

/// <summary>凭据提供方拿到的内核上下文（本次请求的生效配置）。</summary>
public sealed class AxiosCredentialContext
{
    internal AxiosCredentialContext(AxiosConfig config)
    {
        Config = config;
    }

    /// <summary>本次请求的生效配置（已合并，可直接读写头）。</summary>
    public AxiosConfig Config { get; }
}
