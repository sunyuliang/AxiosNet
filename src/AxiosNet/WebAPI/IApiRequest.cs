namespace AxiosNet.WebAPI;

/// <summary>
/// 声明式请求模型契约：<typeparamref name="TResponse"/> 即本次请求的返回类型。
/// <para>
/// <see cref="ApiClient.RequestAsync{TResponse}(AxiosNet.WebAPI.IApiRequest{TResponse}, System.Threading.CancellationToken)"/>
/// 据此自动确定返回的泛型，调用处不需要（也无法）再写一遍响应类型：
/// <code>
/// public sealed class GetUserRequest : IApiRequest&lt;GetUserResponse&gt;
/// {
///     [Get("users/{id}")]
///     ...
/// }
/// var response = await client.RequestAsync(new GetUserRequest { Id = 5 });
/// response.Data.Name  // 已是 GetUserResponse
/// </code>
/// </para>
/// <para>
/// 路由与参数绑定见 <see cref="HttpApiAttribute"/>（或 <see cref="GetAttribute"/> 等子类）与
/// <see cref="PathAttribute"/> / <see cref="QueryAttribute"/> / <see cref="HeaderAttribute"/> /
/// <see cref="BodyAttribute"/>；未标注任何参数特性的公共可读属性一律按 <see cref="QueryAttribute"/> 处理。
/// </para>
/// </summary>
/// <typeparam name="TResponse">本次请求的返回模型类型（也可以是 string / byte[] / Stream 等原始形态）。</typeparam>
public interface IApiRequest<out TResponse>
{
}

/// <summary>
/// 需要按请求微调 <see cref="AxiosConfig"/>（超时、Content-Type、responseType、序列化器等）时实现本接口，
/// 或直接继承 <see cref="ApiRequest{TResponse}"/> 覆写其 <c>Configure</c>。
/// <para>回调发生在参数绑定之后、授权之前，可以读取并修改最终配置。</para>
/// </summary>
public interface IApiRequestConfigurable
{
    /// <summary>按请求调整本次发送使用的 <see cref="AxiosConfig"/>。</summary>
    void Configure(AxiosConfig config);
}

/// <summary>
/// <see cref="IApiRequest{TResponse}"/> 的便捷基类：覆写 <see cref="Configure"/> 即可按请求微调配置，
/// 不必额外实现 <see cref="IApiRequestConfigurable"/>。
/// </summary>
public abstract class ApiRequest<TResponse> : IApiRequest<TResponse>, IApiRequestConfigurable
{
    /// <summary>按请求调整本次发送使用的 <see cref="AxiosConfig"/>，默认不做任何事。</summary>
    protected virtual void Configure(AxiosConfig config)
    {
    }

    void IApiRequestConfigurable.Configure(AxiosConfig config) => Configure(config);
}
