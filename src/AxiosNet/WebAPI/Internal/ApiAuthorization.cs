namespace AxiosNet.WebAPI.Internal;

/// <summary>声明式模型的授权策略（来自 <see cref="AuthorizeAttribute"/> / <see cref="AllowAnonymousAttribute"/>）。</summary>
internal enum ApiAuthorizationMode
{
    /// <summary>未标注：客户端配了授权钩子就执行。</summary>
    Inherit = 0,

    /// <summary><see cref="AuthorizeAttribute"/>：需要授权，按 Scheme 拼 header。</summary>
    Required = 1,

    /// <summary><see cref="AllowAnonymousAttribute"/>：跳过整条授权链路。</summary>
    Anonymous = 2
}

/// <summary>授权策略 + 认证方案。</summary>
internal readonly record struct ApiAuthorization(ApiAuthorizationMode Mode, string Scheme)
{
    /// <summary>未标注时的默认值：<see cref="ApiAuthorizationMode.Inherit"/> 表示「不强制要求授权」，但客户端配了令牌提供方或自定义授权时照样写头。</summary>
    public static readonly ApiAuthorization Inherit = new(ApiAuthorizationMode.Inherit, "Bearer");
}
