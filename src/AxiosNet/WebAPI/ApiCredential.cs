namespace AxiosNet.WebAPI;

/// <summary>凭据上下文：提供方在发送前拿到的全部信息。</summary>
public sealed class ApiAuthContext
{
    internal ApiAuthContext(object request, AxiosConfig config)
    {
        Request = request;
        Config = config;
    }

    /// <summary>本次请求模型实例（发送前，可读取其属性做条件授权）；接口式调用时是 <see cref="ApiInterfaceInvocation"/>。</summary>
    public object Request { get; }

    /// <summary>本次请求的「载荷」类型（即 <c>Request.GetType()</c>）：模型式是模型类型，接口式是 <see cref="ApiInterfaceInvocation"/>。</summary>
    public Type RequestType => Request.GetType();

    /// <summary>本次请求的生效配置（已合并模型绑定结果）：托管落点用 <see cref="ApiCredential"/> 表达，自定义落点直接写 <see cref="AxiosConfig.Headers"/> / <see cref="AxiosConfig.Params"/>。</summary>
    public AxiosConfig Config { get; }
}

/// <summary>凭据扩展点：每次请求发送前调用一次，返回的凭据描述决定库把值写到哪里、以及 401 后能否重放。</summary>
/// <remarks>
/// <para>托管落点（<see cref="ApiCredential.Token"/> / <see cref="ApiCredential.Header(string, string?)"/> / <see cref="ApiCredential.Query"/>）由库写入，401 后自动作废、重取、重放一次。</para>
/// <para>显式 <c>Authorization</c> 头与本接口（或已设置的 <c>Transport.Auth</c>）同时存在视为配置冲突并报错，不静默覆盖；只设其一时按该来源生效。</para>
/// <para>值依赖请求本身的场景（HMAC / SigV4 等签名、令牌放正文、一次写多个头）返回 <see cref="ApiCredential.Custom()"/>：自己写配置，库不接管也不重放。</para>
/// </remarks>
public interface IApiCredentialProvider
{
    /// <summary>取本次请求的凭据；也可直接写 <see cref="ApiAuthContext.Config"/> 后返回 <see cref="ApiCredential.Custom()"/>。</summary>
    /// <param name="context">本次请求的上下文（模型实例与已合并的配置）。</param>
    /// <param name="cancellationToken">调用方的取消令牌。</param>
    /// <returns>凭据描述；<see cref="ApiCredential.None"/> 表示本次不写凭据。</returns>
    ValueTask<ApiCredential> GetCredentialAsync(ApiAuthContext context, CancellationToken cancellationToken);

    /// <summary>作废当前凭据；401 之后由库调用，让下一次取凭据必然拿到新值。只有托管落点形态才会被调用。</summary>
    /// <param name="staleToken">刚才被拒的值（<see cref="ApiCredential.Value"/>），拿不到时为 <c>null</c>，表示无条件作废。</param>
    void Invalidate(string? staleToken = null)
    {
    }
}

/// <summary>凭据形态：决定库把值写到哪里、以及 401 后是否重放。</summary>
public enum ApiCredentialKind
{
    /// <summary>本次不带凭据：不写任何落点，也不重放。</summary>
    None = 0,

    /// <summary>令牌：库按 <c>[Authorize]</c> 的 scheme 拼 <c>Authorization: {scheme} {value}</c>，401 后刷新并重放。</summary>
    Token = 1,

    /// <summary>请求头：库写 <c>{Name}: {Value}</c>；落点名是 <c>Authorization</c> 时值原样发送（不拼 scheme）。401 后刷新并重放。</summary>
    Header = 2,

    /// <summary>查询参数：库写 <c>?{Name}={Value}</c>，401 后刷新并重放。</summary>
    Query = 3,

    /// <summary>提供方已自行写好配置：库不再写任何落点，也不重放。</summary>
    Custom = 4,
}

/// <summary>凭据描述：托管落点由库写入并在 401 后重放，自定义凭据由提供方自行写配置。</summary>
public readonly record struct ApiCredential
{
    private ApiCredential(ApiCredentialKind kind, string? name, string? value)
    {
        Kind = kind;
        Name = name;
        Value = value;
    }

    /// <summary>凭据形态。</summary>
    public ApiCredentialKind Kind { get; }

    /// <summary>落点名：<see cref="ApiCredentialKind.Header"/> 是请求头名，<see cref="ApiCredentialKind.Query"/> 是查询参数名，其余为 <c>null</c>。</summary>
    public string? Name { get; }

    /// <summary>凭据值：<see cref="ApiCredentialKind.Token"/> 是令牌本身，其余托管形态是落点值，<see cref="ApiCredentialKind.Custom"/> / <see cref="ApiCredentialKind.None"/> 为 <c>null</c>。</summary>
    public string? Value { get; }

    /// <summary>本次不带凭据。</summary>
    public static ApiCredential None => default;

    /// <summary>令牌：库按声明的 scheme 拼 <c>Authorization</c> 头。空白值视为 <see cref="None"/>。</summary>
    /// <param name="token">令牌本身（不含 scheme）；<c>null</c> / 空白表示本次不带凭据。</param>
    public static ApiCredential Token(string? token)
        => string.IsNullOrWhiteSpace(token) ? None : new ApiCredential(ApiCredentialKind.Token, null, token);

    /// <summary>完整的 <c>Authorization</c> 头值：等价于 <c>Header("Authorization", value)</c>。空白值视为 <see cref="None"/>。</summary>
    /// <param name="value">形如 <c>Basic dXNlcjpwYXNz</c> 的完整头值；<c>null</c> / 空白表示本次不带凭据。</param>
    public static ApiCredential Header(string? value)
        => Header("Authorization", value);

    /// <summary>请求头：库写 <c>{name}: {value}</c>（覆盖同名头）。空白值视为 <see cref="None"/>。</summary>
    /// <remarks>
    /// <paramref name="name"/> 是 <c>Authorization</c>（忽略大小写）时值原样发送、不拼 scheme，并走内核凭据槽位：
    /// 与显式 <c>Authorization</c> 头同时存在时报冲突。
    /// </remarks>
    /// <param name="name">请求头名，如 <c>X-Api-Key</c>。</param>
    /// <param name="value">落点值；<c>null</c> / 空白表示本次不带凭据。</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> 是 <c>null</c> 或空白。</exception>
    public static ApiCredential Header(string name, string? value)
        => string.IsNullOrWhiteSpace(value) ? None : new ApiCredential(ApiCredentialKind.Header, ValidateName(name), value);

    /// <summary>查询参数：库写 <c>?{name}={value}</c>（覆盖同名参数）。空白值视为 <see cref="None"/>。</summary>
    /// <param name="name">查询参数名，如 <c>access_token</c>。</param>
    /// <param name="value">落点值；<c>null</c> / 空白表示本次不带凭据。</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> 是 <c>null</c> 或空白。</exception>
    public static ApiCredential Query(string name, string? value)
        => string.IsNullOrWhiteSpace(value) ? None : new ApiCredential(ApiCredentialKind.Query, ValidateName(name), value);

    /// <summary>提供方已自行写好配置（签名、正文令牌、多个头），库不写落点也不重放。</summary>
    public static ApiCredential Custom() => new(ApiCredentialKind.Custom, null, null);

    /// <summary>落点名不能为空：空白名会让库写到无意义的位置，比启动期报错更难查。</summary>
    /// <param name="name">待校验的落点名。</param>
    private static string ValidateName(string name)
        => string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("落点名不能为空白。", nameof(name))
            : name;
}

/// <summary>凭据提供方的常用构造。</summary>
public static class CredentialProviders
{
    /// <summary>固定令牌：等价于 <c>Fixed(ApiCredential.Token(token))</c>，额外把空白令牌判为配置错误。</summary>
    /// <param name="token">令牌本身（不含 scheme），不能是空白。</param>
    /// <exception cref="ArgumentException"><paramref name="token"/> 是 <c>null</c> 或空白 —— 静默退化成匿名请求比启动期报错更难查。</exception>
    public static IApiCredentialProvider Fixed(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("令牌不能为空白：要表达「本次不带凭据」请用 CredentialProviders.Fixed(ApiCredential.None)。", nameof(token));
        }

        return new DelegateCredentialProvider((_, _) => ValueTask.FromResult(ApiCredential.Token(token)));
    }

    /// <summary>固定凭据。</summary>
    /// <param name="credential">每次请求都返回的凭据描述。</param>
    public static IApiCredentialProvider Fixed(ApiCredential credential)
        => new DelegateCredentialProvider((_, _) => ValueTask.FromResult(credential));

    /// <summary>按委托取凭据。</summary>
    /// <param name="getAsync">每次请求调用一次的取凭据委托。</param>
    /// <param name="invalidate">401 后的作废回调；不给则重放只能靠「再取一次值是否变化」判定。</param>
    public static IApiCredentialProvider Create(
        Func<ApiAuthContext, CancellationToken, ValueTask<ApiCredential>> getAsync,
        Action<string?>? invalidate = null)
        => new DelegateCredentialProvider(getAsync, invalidate);

    private sealed class DelegateCredentialProvider(
        Func<ApiAuthContext, CancellationToken, ValueTask<ApiCredential>> getAsync,
        Action<string?>? invalidate = null) : IApiCredentialProvider
    {
        /// <summary>取本次请求的凭据。</summary>
        /// <param name="context">本次请求的上下文。</param>
        /// <param name="cancellationToken">调用方的取消令牌。</param>
        /// <returns>凭据描述。</returns>
        public ValueTask<ApiCredential> GetCredentialAsync(ApiAuthContext context, CancellationToken cancellationToken)
            => getAsync(context, cancellationToken);

        /// <summary>作废当前凭据（转给构造时给的委托；没给就什么都不做）。</summary>
        /// <param name="staleToken">刚才被拒的值。</param>
        public void Invalidate(string? staleToken = null) => invalidate?.Invoke(staleToken);
    }
}
