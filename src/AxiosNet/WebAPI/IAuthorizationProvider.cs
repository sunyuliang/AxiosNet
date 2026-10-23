namespace AxiosNet.WebAPI;

/// <summary>
/// 授权上下文：授权提供方在发送前拿到的全部信息。
/// <para>
/// 典型用法是往 <see cref="Config"/> 上写头（如 <c>Authorization</c>），
/// 也可以按 <see cref="Request"/> 的类型跳过特定端点（如登录本身）。
/// </para>
/// </summary>
public sealed class ApiAuthContext
{
    internal ApiAuthContext(object request, AxiosConfig config)
    {
        Request = request;
        Config = config;
    }

    /// <summary>本次请求模型实例（发送前，可读取其属性做条件授权）。</summary>
    public object Request { get; }

    /// <summary>本次请求的模型类型（即 <c>Request.GetType()</c> 的结果；请求模型实例非空，可直接取）。</summary>
    public Type RequestType => Request.GetType();

    /// <summary>
    /// 本次请求的生效配置（已合并模型绑定结果）：往 <see cref="AxiosConfig.Headers"/> 上写授权头即可。
    /// </summary>
    public AxiosConfig Config { get; }
}

/// <summary>
/// 授权扩展点：在每次请求发送前调用，由实现方决定怎么把凭据写进 <see cref="ApiAuthContext.Config"/>。
/// <para>内置实现见 <see cref="BearerAuthorizationProvider"/>；需要 OAuth2 刷新、签名、多凭据轮换时自行实现本接口。</para>
/// </summary>
public interface IAuthorizationProvider
{
    /// <summary>在请求发送前把凭据写进 <paramref name="context"/>.Config。</summary>
    ValueTask ApplyAsync(ApiAuthContext context, CancellationToken cancellationToken);
}

/// <summary>
/// 访问令牌扩展点（Refit 的 <c>AuthorizationHeaderValueGetter</c> 等价能力）：
/// 只负责<b>取令牌</b>，拼头由库来做（<c>Authorization: {scheme} {token}</c>），scheme 来自
/// <see cref="AuthorizeAttribute"/>，未标注时为 <c>Bearer</c>。
/// <para>
/// 返回 <c>null</c> 或空白表示本次不带令牌（不写头）；抛出的异常原样冒泡（与授权实现一致）。
/// 模型已显式写了 <c>Authorization</c> 头时不会被覆盖。
/// </para>
/// <para>适用于「刷新/缓存自己管、只想少写拼头代码」的场景；要完全控制头部（签名、多凭据）请用
/// <see cref="IAuthorizationProvider"/>。</para>
/// </summary>
public interface IAccessTokenProvider
{
    /// <summary>取本次请求的访问令牌；<c>null</c> / 空白表示不写 <c>Authorization</c> 头。</summary>
    ValueTask<string?> GetTokenAsync(ApiAuthContext context, CancellationToken cancellationToken);
}
