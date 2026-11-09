using System.Text;

namespace AxiosNet.Internal;

/// <summary>
/// <c>Authorization</c> 头的唯一映射实现：内核的 <see cref="AxiosConfig.TransportSettings.Auth"/> 与
/// WebAPI 的凭据提供方都经这里落头，避免两套拼接规则漂移。这里只做「凭据值 → 头值」，不关心凭据从哪来。
/// </summary>
internal static class AuthorizationHeader
{
    /// <summary>头名。</summary>
    internal const string Name = "Authorization";

    /// <summary>令牌形态：<c>{scheme} {token}</c>；scheme 为空时按 <c>Bearer</c>。</summary>
    internal static string FormatToken(string? scheme, string token)
        => (string.IsNullOrWhiteSpace(scheme) ? "Bearer" : scheme) + " " + token;

    /// <summary>完整头值形态：调用方已给出整段值，原样使用。</summary>
    internal static string FormatComplete(string value) => value;

    /// <summary>内核配置头集合落「令牌」形态。</summary>
    internal static void SetToken(AxiosHeaders headers, string? scheme, string token)
        => headers.Set(Name, FormatToken(scheme, token));

    /// <summary>内核配置头集合落「完整头值」形态。</summary>
    internal static void SetComplete(AxiosHeaders headers, string value)
        => headers.Set(Name, FormatComplete(value));

    /// <summary>Basic 形态：<c>Basic base64(user:pass)</c>，用户名 / 密码按 UTF-8 编码。</summary>
    internal static string FormatBasic(AxiosAuth auth)
        => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(auth.Username + ":" + auth.Password));
}
