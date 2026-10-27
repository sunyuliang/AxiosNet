namespace AxiosNet.WebAPI;

/// <summary>
/// 声明式请求模型契约：<typeparamref name="TResponse"/> 即本次请求的返回类型，调用处不必再写响应类型。
/// 路由与参数绑定见 <see cref="HttpApiAttribute"/> 与 <see cref="PathAttribute"/> / <see cref="QueryAttribute"/> / <see cref="HeaderAttribute"/> / <see cref="BodyAttribute"/>；
/// 未标注参数特性的公共可读属性按 <see cref="QueryAttribute"/> 处理。
/// </summary>
/// <typeparam name="TResponse">本次请求的返回模型类型（也可以是 string / byte[] / Stream 等原始形态）。</typeparam>
public interface IApiRequest<out TResponse>
{
}

/// <summary>需要按请求微调 <see cref="AxiosConfig"/> 时实现本接口，或直接继承 <see cref="ApiRequest{TResponse}"/> 覆写其 <c>Configure</c>。回调发生在参数绑定之后、授权之前。</summary>
public interface IApiRequestConfigurable
{
    /// <summary>按请求调整本次发送使用的 <see cref="AxiosConfig"/>。</summary>
    void Configure(AxiosConfig config);
}

/// <summary><see cref="IApiRequest{TResponse}"/> 的便捷基类：覆写 <see cref="Configure"/> 即可按请求微调配置。</summary>
public abstract class ApiRequest<TResponse> : IApiRequest<TResponse>, IApiRequestConfigurable
{
    /// <summary>按请求调整本次发送使用的 <see cref="AxiosConfig"/>，默认不做任何事。</summary>
    protected virtual void Configure(AxiosConfig config)
    {
    }

    void IApiRequestConfigurable.Configure(AxiosConfig config) => Configure(config);
}
