namespace AxiosNet.WebAPI;

/// <summary>
/// 内置 Bearer 授权：把令牌写进 <c>Authorization</c> 头。
/// <para>
/// 令牌已含空格（如 <c>Basic xxx</c> / <c>Bearer xxx</c>）视为带 scheme 原样发送，
/// 否则自动补 <c>Bearer </c> 前缀。
/// </para>
/// <para>
/// 需要令牌缓存 / 过期刷新 / 单飞（single-flight）时，请实现自定义
/// <see cref="IAuthorizationProvider"/> —— 那是为此准备的扩展点，本内置实现刻意保持无状态。
/// </para>
/// </summary>
public sealed class BearerAuthorizationProvider : IAuthorizationProvider
{
    private const string Scheme = "Bearer ";

    private readonly Func<CancellationToken, ValueTask<string?>> _tokenFactory;

    /// <summary>固定令牌：每次请求都写同一个值。</summary>
    public BearerAuthorizationProvider(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("令牌不能为空白。", nameof(token));
        }

        _tokenFactory = _ => ValueTask.FromResult<string?>(token);
    }

    /// <summary>
    /// 动态取令牌：每次请求调用一次（适合自带缓存的外部令牌源；
    /// 本类不做缓存，<paramref name="tokenFactory"/> 返回什么就发什么）。
    /// </summary>
    public BearerAuthorizationProvider(Func<CancellationToken, Task<string?>> tokenFactory)
    {
        ArgumentNullException.ThrowIfNull(tokenFactory);
        _tokenFactory = async ct => await tokenFactory(ct).ConfigureAwait(false);
    }

    /// <summary>取令牌并写入 <c>Authorization</c> 头。</summary>
    /// <exception cref="InvalidOperationException">令牌工厂返回 <c>null</c> 或空白（无凭据可发，与其静默发出 401，不如当场指出来）。</exception>
    public async ValueTask ApplyAsync(ApiAuthContext context, CancellationToken cancellationToken)
    {
        var token = await _tokenFactory(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "BearerAuthorizationProvider 拿到的令牌是 null 或空白，无法设置 Authorization 头。"
                + "未登录 / 无令牌属于正常业务流转时，请改用自定义 IAuthorizationProvider 按需跳过。");
        }

        // 已含空格视为「scheme + 参数」完整形态（Token68 本身不允许空格），原样发送
        context.Config.Headers.Set("Authorization", token.Contains(' ') ? token : Scheme + token);
    }
}
