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

    internal ApiCredentialProviderAdapter(IApiCredentialProvider inner, object request, string? scheme)
    {
        _inner = inner;
        _request = request;
        _scheme = scheme;
    }

    /// <summary>上一次提供方实际返回的形态（401 重放据此判断能不能重放，<see cref="ApiCredentialKind.Custom"/> / <see cref="ApiCredentialKind.None"/> 不重放）。</summary>
    internal ApiCredentialKind LastKind { get; private set; } = ApiCredentialKind.None;

    /// <summary>声明式提供方负责刷新策略，内核只记录“可刷新”这一事实。</summary>
    public bool CanRefresh => true;

    /// <inheritdoc />
    public async ValueTask<AxiosCredential?> GetAsync(AxiosCredentialContext context, CancellationToken cancellationToken)
    {
        // 上下文用内核合并后的生效配置：提供方返回 Custom 时写进的是本次真正发送的那份配置。
        var credential = await _inner
            .GetCredentialAsync(new ApiAuthContext(_request, context.Config), cancellationToken)
            .ConfigureAwait(false);

        LastKind = credential.Kind;

        if (string.IsNullOrWhiteSpace(credential.Value))
        {
            return null;
        }

        return credential.Kind switch
        {
            ApiCredentialKind.Token => AxiosCredential.Bearer(credential.Value!, _scheme),
            ApiCredentialKind.Header => AxiosCredential.Header(credential.Value!),
            _ => null,
        };
    }

    /// <inheritdoc />
    public void Invalidate(string? staleValue = null) => _inner.Invalidate(staleValue);
}
