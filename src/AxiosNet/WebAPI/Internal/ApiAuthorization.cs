using AxiosNet.WebAPI.Attributes;

namespace AxiosNet.WebAPI.Internal;

/// <summary>声明式模型的授权策略（来自 <see cref="AuthorizeAttribute"/> / <see cref="AllowAnonymousAttribute"/>）。</summary>
internal enum ApiAuthorizationMode
{
    /// <summary>未标注：客户端配了凭据提供方就取凭据。</summary>
    Inherit = 0,

    /// <summary><see cref="AuthorizeAttribute"/>：需要授权，按 Scheme 拼 header。</summary>
    Required = 1,

    /// <summary><see cref="AllowAnonymousAttribute"/>：跳过整条授权链路。</summary>
    Anonymous = 2
}

/// <summary>授权策略 + 认证方案。</summary>
/// <param name="Mode">策略：未标注 / 要求授权 / 匿名。</param>
/// <param name="Scheme">拼 Authorization 头用的认证方案名（如 <c>Bearer</c>）。</param>
internal readonly record struct ApiAuthorization(ApiAuthorizationMode Mode, string Scheme)
{
    /// <summary>未标注时的默认值：<see cref="ApiAuthorizationMode.Inherit"/> 表示「不强制要求授权」，但客户端配了凭据提供方时照样取凭据并写头。</summary>
    public static readonly ApiAuthorization Inherit = new(ApiAuthorizationMode.Inherit, "Bearer");
}
