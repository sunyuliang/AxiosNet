namespace AxiosNet.WebAPI;

/// <summary>授权上下文：授权提供方在发送前拿到的全部信息，典型用法是往 <see cref="Config"/> 上写头。</summary>
public sealed class ApiAuthContext
{
    internal ApiAuthContext(object request, AxiosConfig config)
    {
        Request = request;
        Config = config;
    }

    /// <summary>本次请求模型实例（发送前，可读取其属性做条件授权）。</summary>
    public object Request { get; }

    /// <summary>本次请求的模型类型（即 <c>Request.GetType()</c>）。</summary>
    public Type RequestType => Request.GetType();

    /// <summary>本次请求的生效配置（已合并模型绑定结果）：往 <see cref="AxiosConfig.Headers"/> 上写授权头即可。</summary>
    public AxiosConfig Config { get; }
}

/// <summary>授权扩展点：在每次请求发送前调用，由实现方决定怎么把凭据写进 <see cref="ApiAuthContext.Config"/>。内置实现见 <see cref="BearerAuthorizationProvider"/>。</summary>
public interface IAuthorizationProvider
{
    /// <summary>在请求发送前把凭据写进 <paramref name="context"/>.Config。</summary>
    ValueTask ApplyAsync(ApiAuthContext context, CancellationToken cancellationToken);
}

/// <summary>
/// 访问令牌扩展点：只负责取令牌，拼头由库来做（<c>Authorization: {scheme} {token}</c>，scheme 来自 <see cref="AuthorizeAttribute"/>）。
/// 返回 <c>null</c> 或空白表示本次不带令牌；模型已显式写了 <c>Authorization</c> 头时不会被覆盖；要完全控制头部请用 <see cref="IAuthorizationProvider"/>。
/// </summary>
public interface IAccessTokenProvider
{
    /// <summary>取本次请求的访问令牌；<c>null</c> / 空白表示不写 <c>Authorization</c> 头。</summary>
    ValueTask<string?> GetTokenAsync(ApiAuthContext context, CancellationToken cancellationToken);
}

/// <summary>
/// 可作废令牌的提供方：401 之后由库调用，让下一次 <see cref="IAccessTokenProvider.GetTokenAsync"/> 必然拿到新令牌。
/// 不实现本接口的提供方也能用 401 重放，但只能靠「再取一次值是否变化」判断是否值得重放。内置实现见 <see cref="AccessTokenManager"/>。
/// </summary>
public interface IAccessTokenRefresher
{
    /// <summary>作废当前令牌；<paramref name="staleToken"/> 非 <c>null</c> 时只在它仍是当前令牌时作废（并发下不误伤刚刷新的新令牌）。</summary>
    void Invalidate(string? staleToken = null);
}
