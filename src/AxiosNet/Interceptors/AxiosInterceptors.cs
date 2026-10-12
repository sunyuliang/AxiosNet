namespace AxiosNet.Interceptors;

/// <summary>
/// 对应 axios 实例上的 interceptors.request / interceptors.response。
/// </summary>
public sealed class AxiosInterceptors
{
    public RequestInterceptorManager Request { get; } = new();

    public ResponseInterceptorManager Response { get; } = new();
}
