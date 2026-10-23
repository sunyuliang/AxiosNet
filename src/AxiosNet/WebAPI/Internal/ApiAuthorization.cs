namespace AxiosNet.WebAPI.Internal;

/// <summary>声明式模型的授权策略（来自 <see cref="AuthorizeAttribute"/> / <see cref="AllowAnonymousAttribute"/>）。</summary>
internal enum ApiAuthorizationMode
{
    /// <summary>未标注：沿用旧行为（客户端配了授权钩子就执行）。</summary>
    Inherit = 0,

    /// <summary><see cref="AuthorizeAttribute"/>：需要授权，按 Scheme 拼 header。</summary>
    Required = 1,

    /// <summary><see cref="AllowAnonymousAttribute"/>：跳过整条授权链路。</summary>
    Anonymous = 2
}

/// <summary>授权策略 + 认证方案。</summary>
internal readonly record struct ApiAuthorization(ApiAuthorizationMode Mode, string Scheme)
{
    /// <summary>未标注时的默认值（Bearer 只用于兜底，Mode 为 Inherit 时不会拼头）。</summary>
    public static readonly ApiAuthorization Inherit = new(ApiAuthorizationMode.Inherit, "Bearer");
}
