namespace AxiosNet.WebAPI;

/// <summary>
/// 标记请求模型需要授权：客户端配了 <see cref="ApiClient.AccessTokenProvider"/> 时会调用它取令牌，
/// 并按 <see cref="Scheme"/> 拼出 <c>Authorization: {scheme} {token}</c>；随后
/// <see cref="ApiClient.Authorization"/> 仍会执行（可覆盖该头）。
/// <para>
/// 没有标注任何授权特性的模型沿用旧行为：只要客户端配了授权钩子就执行。
/// 本特性真正的差别在 <see cref="Scheme"/>——同一客户端要访问「Bearer 为主、个别端点 Basic」时用它切换。
/// </para>
/// <para>
/// 与 <see cref="AllowAnonymousAttribute"/> 同标一个类属二义性，构建期报错；派生类的声明优先于基类。
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class AuthorizeAttribute : Attribute
{
    /// <summary>声明需要授权，使用指定认证方案（默认 <c>Bearer</c>）。</summary>
    /// <param name="scheme">认证方案名，拼在令牌前（如 <c>Bearer</c> / <c>Basic</c>）；不能为空白。</param>
    public AuthorizeAttribute(string scheme = "Bearer")
    {
        if (string.IsNullOrWhiteSpace(scheme))
        {
            throw new ArgumentException("授权 scheme 不能为空白。", nameof(scheme));
        }

        Scheme = scheme.Trim();
    }

    /// <summary>认证方案名（拼在令牌前，如 <c>Bearer</c> / <c>Basic</c>）。</summary>
    public string Scheme { get; }
}

/// <summary>
/// 标记请求模型<b>不做授权</b>：跳过 <see cref="ApiClient.AccessTokenProvider"/> 与
/// <see cref="ApiClient.Authorization"/>（登录、健康检查等公开端点用）。
/// <para>只跳过授权钩子，模型上显式写的 <c>Authorization</c> 头（<c>[Header]</c> / <c>[Headers]</c>）照常发送。</para>
/// <para>与 <see cref="AuthorizeAttribute"/> 同标一个类属二义性，构建期报错；派生类的声明优先于基类。</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class AllowAnonymousAttribute : Attribute
{
}
