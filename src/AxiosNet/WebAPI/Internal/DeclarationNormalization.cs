using System.Net.Http;

namespace AxiosNet.WebAPI.Internal;

/// <summary>
/// 声明值的归一化与校验（反射路径与生成路径共用，保证两条路径的错误文本一致）。
/// 生成器在编译期已按同样规则写入字面量；这里再校验一次是最后一道闸门（幂等）。
/// </summary>
internal static class DeclarationNormalization
{
    /// <summary>HTTP 方法名归一化：去首尾空白、转大写；空白直接拒绝。</summary>
    internal static HttpMethod HttpMethodFrom(string? method)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            throw new ArgumentException("HTTP 方法不能为空白。", nameof(method));
        }

        return new HttpMethod(method.Trim().ToUpperInvariant());
    }

    /// <summary>路径模板归一化：去首尾空白；空白直接拒绝。</summary>
    internal static string Path(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("路径模板不能为空白。", nameof(path));
        }

        return path.Trim();
    }

    /// <summary>授权 scheme 归一化：去首尾空白；空串 / 内部空白直接拒绝（它会与令牌拼进同一个头值）。</summary>
    internal static string Scheme(string? scheme)
    {
        if (string.IsNullOrWhiteSpace(scheme))
        {
            throw new ArgumentException("授权 scheme 不能为空白。", nameof(scheme));
        }

        var trimmed = scheme.Trim();
        foreach (var c in trimmed)
        {
            if (char.IsWhiteSpace(c))
            {
                throw new ArgumentException(
                    $"授权 scheme 不能包含空白字符（当前 \"{trimmed}\"）：它会与令牌拼进同一个 Authorization 头值。",
                    nameof(scheme));
            }
        }

        return trimmed;
    }

    /// <summary>请求体媒体类型归一化：去首尾空白；空白直接拒绝，并复用请求头取值校验（阻止 CR/LF 注入）。</summary>
    internal static string ContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            throw new ArgumentException("请求体 Content-Type 不能为空白。", nameof(contentType));
        }

        var trimmed = contentType.Trim();
        AxiosHeaders.ValidateHeaderValue("Content-Type", trimmed);
        return trimmed;
    }

    /// <summary>[Part] 的可选字符串：省略（null）保持 null；显式空白视为构建错误。</summary>
    internal static string? OptionalPartValue(string? value, string memberDisplay, string attributeMember)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException(
                $"{memberDisplay} 的 [Part].{attributeMember} 不能为空白：要省略请直接不设置。");
        }

        return trimmed;
    }

    /// <summary>从声明解析授权策略；[Authorize] 与 [AllowAnonymous] 同标一层属二义性。</summary>
    internal static ApiAuthorization Authorization(bool allowAnonymous, string? authorizeScheme, string owner)
    {
        if (allowAnonymous && authorizeScheme is not null)
        {
            throw new InvalidOperationException($"{owner} 同时标注了 [Authorize] 与 [AllowAnonymous]：授权与匿名互斥。");
        }

        if (allowAnonymous)
        {
            return new ApiAuthorization(ApiAuthorizationMode.Anonymous, "Bearer");
        }

        return authorizeScheme is null
            ? ApiAuthorization.Inherit
            : new ApiAuthorization(ApiAuthorizationMode.Required, Scheme(authorizeScheme));
    }
}
