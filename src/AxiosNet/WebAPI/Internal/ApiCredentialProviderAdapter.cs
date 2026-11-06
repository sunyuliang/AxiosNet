namespace AxiosNet.WebAPI.Internal;

/// <summary>
/// 把声明式层的 <see cref="IApiCredentialProvider"/> 包成内核中性的 <see cref="IAxiosCredentialProvider"/>：
/// 只负责取凭据值与刷新策略，凭据 → <c>Authorization</c> 头由内核统一映射。
/// </summary>
internal sealed class ApiCredentialProviderAdapter : IAxiosCredentialProvider
{
    private readonly IApiCredentialProvider _inner;
    private readonly object _request;
    private readonly string? _scheme;
    private readonly ApiCredentialCache _cache;

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

    /// <summary>上一次提供方实际返回的形态（401 重放据此判断能不能重放，<see cref="ApiCredentialKind.Custom"/> / <see cref="ApiCredentialKind.None"/> 不重放）。</summary>
    internal ApiCredentialKind LastKind { get; private set; } = ApiCredentialKind.None;

    /// <summary>声明式提供方负责刷新策略，内核只记录“可刷新”这一事实。</summary>
    public bool CanRefresh => true;

    /// <inheritdoc />
    public ValueTask<AxiosCredential?> GetAsync(AxiosCredentialContext context, CancellationToken cancellationToken)
    {
        // 上下文用内核合并后的生效配置：提供方返回 Custom 时写进的是本次真正发送的那份配置。
        var pending = _inner.GetCredentialAsync(new ApiAuthContext(_request, context.Config), cancellationToken);

        // 常见提供方同步完成（缓存令牌 / 静态令牌）：走同步返回，避免 async ValueTask 状态机的开销。
        return pending.IsCompletedSuccessfully
            ? ValueTask.FromResult(Map(pending.Result))
            : AwaitAsync(pending);
    }

    /// <inheritdoc />
    public void Invalidate(string? staleValue = null) => _inner.Invalidate(staleValue);

    private async ValueTask<AxiosCredential?> AwaitAsync(ValueTask<ApiCredential> pending)
        => Map(await pending.ConfigureAwait(false));

    private AxiosCredential? Map(ApiCredential credential)
    {
        LastKind = credential.Kind;

        if (string.IsNullOrWhiteSpace(credential.Value))
        {
            return null;
        }

        return credential.Kind switch
        {
            ApiCredentialKind.Token => _cache.Bearer(credential.Value!, _scheme),
            ApiCredentialKind.Header => _cache.Header(credential.Value!),
            _ => null,
        };
    }
}

/// <summary>
/// 按值复用不可变的凭据映射对象：提供方每请求仍会被调用一次，只有「形态 + 值 + scheme」完全不变时才复用上一次的映射结果。
/// 不做池化、不复用可变对象：命中的对象本身不可变，值变化时直接换新实例。
/// </summary>
internal sealed class ApiCredentialCache
{
    private AxiosCredential? _bearer;
    private AxiosCredential? _header;

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
