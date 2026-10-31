using System.Reflection;
using AxiosNet.WebAPI.Internal;

namespace AxiosNet.WebAPI.Reflection;

/// <summary>
/// 读一层的授权声明（<see cref="AllowAnonymousAttribute"/> / <see cref="AuthorizeAttribute"/>）。
/// 两个特性同标一层属二义性，由 <see cref="DeclarationNormalization.Authorization"/> 在构建期统一拒绝。
/// </summary>
internal static class AuthorizationDeclaration
{
    /// <summary>取一层的授权声明；两层都没标时返回 <c>false</c>，交由更外层决定。</summary>
    public static bool TryRead(MemberInfo member, out bool allowAnonymous, out string? scheme)
    {
        allowAnonymous = member.GetCustomAttribute<AllowAnonymousAttribute>(inherit: false) is not null;
        scheme = member.GetCustomAttribute<AuthorizeAttribute>(inherit: false)?.Scheme;
        return allowAnonymous || scheme is not null;
    }
}
