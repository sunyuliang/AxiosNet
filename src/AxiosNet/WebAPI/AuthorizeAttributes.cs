namespace AxiosNet.WebAPI;

/// <summary>
/// 标记请求模型需要授权：客户端配了 <see cref="ApiClient.AccessTokenProvider"/> 时按 <see cref="Scheme"/> 拼出
/// <c>Authorization: {scheme} {token}</c>，随后 <see cref="ApiClient.Authorization"/> 仍会执行。
/// 未标注授权特性的模型只要客户端配了授权钩子也会执行；与 <see cref="AllowAnonymousAttribute"/> 同标一个类属二义性。
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class AuthorizeAttribute : Attribute
{
    /// <summary>声明需要授权，使用指定认证方案（默认 <c>Bearer</c>）。</summary>
    /// <param name="scheme">认证方案名，拼在令牌前（如 <c>Bearer</c> / <c>Basic</c>）；不能为空白、不能含内部空白。</param>
    public AuthorizeAttribute(string scheme = "Bearer")
    {
        if (string.IsNullOrWhiteSpace(scheme))
        {
            throw new ArgumentException("授权 scheme 不能为空白。", nameof(scheme));
        }

        var trimmed = scheme.Trim();

        // scheme 与令牌拼进同一个头值，内部空白会让服务端按 RFC 7235 解析失败，构造期直接拒。
        foreach (var c in trimmed)
        {
            if (char.IsWhiteSpace(c))
            {
                throw new ArgumentException($"授权 scheme 不能包含空白字符（当前 \"{trimmed}\"）：它会与令牌拼进同一个 Authorization 头值。", nameof(scheme));
            }
        }

        Scheme = trimmed;
    }

    /// <summary>认证方案名（拼在令牌前，如 <c>Bearer</c> / <c>Basic</c>）。</summary>
    public string Scheme { get; }
}

/// <summary>
/// 标记请求模型不做授权：跳过 <see cref="ApiClient.AccessTokenProvider"/> 与 <see cref="ApiClient.Authorization"/>。
/// 模型上显式写的 <c>Authorization</c> 头照常发送；与 <see cref="AuthorizeAttribute"/> 同标一个类属二义性。
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class AllowAnonymousAttribute : Attribute
{
}
