
namespace AxiosNet.WebAPI.Internal.Authorization;

/// <summary>
/// 把声明式层的 <see cref="IApiCredentialProvider"/> 包成内核中性的 <see cref="IAxiosCredentialProvider"/>：<c>Authorization</c> 落点交给内核映射，其余托管落点直接写进本次配置。
/// </summary>
internal sealed class ApiCredentialProviderAdapter : IAxiosCredentialProvider
{
    private readonly IApiCredentialProvider _inner;
    private readonly object _request;
    private readonly string? _scheme;
    private readonly ApiCredentialCache _cache;

    // 401 后重取到的凭据：下一次发送直接用这份，不再调提供方（落点那时才写进合并后的配置）。
    private ApiCredential? _snapshot;

    internal ApiCredentialProviderAdapter(
        IApiCredentialProvider inner,
        object request,
        string? scheme,
        ApiCredentialCache cache)
    {
        _inner = inner;
        _request = request;
        _scheme = scheme;
        _cache = cache;
    }

    /// <summary>上一次提供方实际返回的形态（401 重放据此判断能不能重放）。</summary>
    internal ApiCredentialKind LastKind { get; private set; } = ApiCredentialKind.None;

    /// <summary>上一次提供方实际返回的值：401 后作为 <see cref="IApiCredentialProvider.Invalidate"/> 的入参，也用于判断重取是否拿到新值。</summary>
    internal string? LastValue { get; private set; }

    /// <summary>上一次落点是完整 <c>Authorization</c> 头值：拿不到令牌本体，401 后按无条件作废。</summary>
    internal bool LastValueIsCompleteAuthorization { get; private set; }

    /// <summary>本次凭据是否由库托管（<see cref="ApiCredentialKind.None"/> 与 <see cref="ApiCredentialKind.Custom"/> 不托管，不重放）。</summary>
    internal bool IsManaged => LastKind is not (ApiCredentialKind.None or ApiCredentialKind.Custom);

    /// <summary>声明式提供方负责刷新策略，内核只记录“可刷新”这一事实。</summary>
    public bool CanRefresh => true;

    /// <inheritdoc />
    public ValueTask<AxiosCredential?> GetAsync(AxiosCredentialContext context, CancellationToken cancellationToken)
    {
        // 401 后重取过：直接用那份凭据，落点写进本次合并后的配置。
        if (_snapshot is { } snapshot)
        {
            _snapshot = null;
            return PlatformCompat.FromResult(Apply(snapshot, context.Config));
        }

        // 上下文用内核合并后的生效配置：托管落点写进的是本次真正发送的那份配置。
        var pending = _inner.GetCredentialAsync(new ApiAuthContext(_request, context.Config), cancellationToken);

        // 常见提供方同步完成（缓存 / 静态令牌）：走同步返回。
        return pending.IsCompletedSuccessfully
            ? PlatformCompat.FromResult(Apply(pending.Result, context.Config))
            : AwaitAsync(pending, context.Config);
    }

    /// <inheritdoc />
    public void Invalidate(string? staleValue = null) => _inner.Invalidate(staleValue);

    /// <summary>401 后重取一次凭据：只取值、不写落点，结果留给下一次 <see cref="GetAsync"/> 应用；<see cref="ApiCredentialKind.None"/> / <see cref="ApiCredentialKind.Custom"/> 返回 <c>null</c>（不可重放）。</summary>
    internal async ValueTask<ApiCredential?> RefreshAsync(AxiosConfig config, CancellationToken cancellationToken)
    {
        var credential = await _inner
            .GetCredentialAsync(new ApiAuthContext(_request, config), cancellationToken)
            .ConfigureAwait(false);

        if (credential.Kind is ApiCredentialKind.None or ApiCredentialKind.Custom)
        {
            return null;
        }

        // 不在这里写落点：此刻拿到的是请求原件，往上写查询参数会整体覆盖实例默认值。
        _snapshot = credential;
        return credential;
    }

    /// <summary>提供方异步返回时的慢路径：等值后再走同一套落点应用。</summary>
    private async ValueTask<AxiosCredential?> AwaitAsync(ValueTask<ApiCredential> pending, AxiosConfig config)
        => Apply(await pending.ConfigureAwait(false), config);

    /// <summary>应用托管落点：<c>Authorization</c> 两种形态交给内核映射，其余直接写进配置；非头落点返回 <c>null</c>。</summary>
    private AxiosCredential? Apply(ApiCredential credential, AxiosConfig config)
    {
        LastKind = credential.Kind;
        LastValue = credential.Value;

        // Authorization 头落点的值是完整头值（不是令牌本体）：401 后只能无条件作废。
        LastValueIsCompleteAuthorization = credential.Kind == ApiCredentialKind.Header
            && string.Equals(credential.Name, AuthorizationHeader.Name, StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(credential.Value))
        {
            return null;
        }

        switch (credential.Kind)
        {
            // Authorization 头交给内核槽位：值原样发送，且与显式头并存时能报冲突。
            case ApiCredentialKind.Header when LastValueIsCompleteAuthorization:
                return _cache.Header(credential.Value!);

            case ApiCredentialKind.Header:
                config.Headers.Set(credential.Name!, credential.Value!);
                config.ResolvedCredentialIdentity = credential.Value;
                return null;

            case ApiCredentialKind.Query:
                config.SetQuery(credential.Name!, credential.Value!);
                config.ResolvedCredentialIdentity = credential.Value;
                return null;

            case ApiCredentialKind.Token:
                return _cache.Bearer(credential.Value!, _scheme);

            default:
                return null;
        }
    }
}

/// <summary>按值复用不可变的凭据映射对象：形态 + 值 + scheme 完全不变时复用上一次的映射结果。</summary>
internal sealed class ApiCredentialCache
{
    /// <summary>上一次令牌形态的映射结果（<c>Bearer</c> / <c>Basic</c>），供 <see cref="Bearer"/> 按值复用。</summary>
    private AxiosCredential? _bearer;

    /// <summary>上一次完整头值形态的映射结果，供 <see cref="Header"/> 按值复用。</summary>
    private AxiosCredential? _header;

    /// <summary>取「令牌 + scheme」的凭据映射：与上一次完全相同时复用同一实例。</summary>
    internal AxiosCredential Bearer(string token, string? scheme)
    {
        var cached = Volatile.Read(ref _bearer);
        if (cached is not null && cached.IsBearerFor(token, scheme))
        {
            return cached;
        }

        var credential = AxiosCredential.Bearer(token, scheme);
        Volatile.Write(ref _bearer, credential);
        return credential;
    }

    /// <summary>取「完整 Authorization 头值」的凭据映射：同值复用同一实例。</summary>
    internal AxiosCredential Header(string value)
    {
        var cached = Volatile.Read(ref _header);
        if (cached is not null && cached.IsHeaderFor(value))
        {
            return cached;
        }

        var credential = AxiosCredential.Header(value);
        Volatile.Write(ref _header, credential);
        return credential;
    }
}
