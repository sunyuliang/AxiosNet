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
