namespace AxiosNet.Interceptors;

/// <summary>
/// 对应 axios 实例上的 interceptors.request / interceptors.response。
/// </summary>
public sealed class AxiosInterceptors
{
    /// <summary>请求拦截器管理器。</summary>
    public RequestInterceptorManager Request { get; } = new();

    /// <summary>响应拦截器管理器。</summary>
    public ResponseInterceptorManager Response { get; } = new();
}
