using System.Diagnostics.CodeAnalysis;

namespace AxiosNet.WebAPI;

/// <summary>
/// 声明式入口：<c>Axios.Create(url).Create&lt;IUserApi&gt;()</c> / <c>client.RequestAsync(request)</c>。
/// 一个客户端实例既是原始 HTTP 入口，也是声明式入口，不需要再单独构造门面。
/// 需要凭据提供方 / 强类型错误工厂时，仍用 <see cref="ApiClient"/>（本扩展每次调用按需包装同一个客户端）。
/// </summary>
public static class AxiosClientDeclarativeExtensions
{
    /// <summary>为接口创建代理实现，底层复用当前客户端（默认值、拦截器、连接池全部生效）。</summary>
    /// <remarks>语义与 <see cref="ApiClient.Create{T}"/> 完全一致。</remarks>
    public static T Create<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods
            | DynamicallyAccessedMemberTypes.PublicProperties
            | DynamicallyAccessedMemberTypes.PublicEvents
            | DynamicallyAccessedMemberTypes.Interfaces)] T>(this AxiosClient client)
        where T : class
        => Wrap(client).Create<T>();

    /// <summary>非泛型重载（供 DI 等运行时场景使用），语义与 <see cref="ApiClient.Create(Type)"/> 一致。</summary>
    [RequiresUnreferencedCode("未生成元数据时的回退路径通过反射读取接口方法、参数特性与类型；裁剪时需 root 接口及其参数 / 返回类型。")]
    public static object Create(
        this AxiosClient client,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods
            | DynamicallyAccessedMemberTypes.PublicProperties
            | DynamicallyAccessedMemberTypes.PublicEvents
            | DynamicallyAccessedMemberTypes.Interfaces)] Type interfaceType)
        => Wrap(client).Create(interfaceType);

    /// <summary>发送请求模型描述的请求；返回类型由请求模型的 <see cref="IApiRequest{TResponse}"/> 泛型确定。</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072",
        Justification = "请求模型的运行时类型无法静态标注：AOT 下由 AxiosNet 源生成器在编译期写入成员取值委托，注册表命中后不再反射；"
            + "未命中且无动态代码时抛 AxiosAotMetadataException，不会反射模型成员（README「运行时与 AOT」）。")]
    public static Task<AxiosResponse<TResponse>> RequestAsync<TResponse>(
        this AxiosClient client,
        IApiRequest<TResponse> request,
        CancellationToken cancellationToken = default)
        => Wrap(client).RequestAsync(request, cancellationToken);

    /// <summary>同 <see cref="RequestAsync{TResponse}(AxiosClient, IApiRequest{TResponse}, CancellationToken)"/>，另支持按请求微调配置。</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072",
        Justification = "请求模型的运行时类型无法静态标注：AOT 下由 AxiosNet 源生成器在编译期写入成员取值委托，注册表命中后不再反射；"
            + "未命中且无动态代码时抛 AxiosAotMetadataException，不会反射模型成员（README「运行时与 AOT」）。")]
    public static Task<AxiosResponse<TResponse>> RequestAsync<TResponse>(
        this AxiosClient client,
        IApiRequest<TResponse> request,
        Action<AxiosConfig>? configure,
        CancellationToken cancellationToken = default)
        => Wrap(client).RequestAsync(request, configure, cancellationToken);

    /// <summary>按请求模型拼出最终 URL，不发请求；语义与 <see cref="ApiClient.GetUri{TResponse}"/> 一致。</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072",
        Justification = "请求模型的运行时类型无法静态标注：AOT 下由 AxiosNet 源生成器在编译期写入成员取值委托，注册表命中后不再反射；"
            + "未命中且无动态代码时抛 AxiosAotMetadataException，不会反射模型成员（README「运行时与 AOT」）。")]
    public static string GetUri<TResponse>(this AxiosClient client, IApiRequest<TResponse> request)
        => Wrap(client).GetUri(request);

    /// <summary>以 NDJSON 读取流式响应；语义与 <see cref="ApiClient.RequestStreamAsync{TItem}(IApiRequest{TItem}, CancellationToken)"/> 一致。</summary>
    public static Task<AxiosResponse<IAsyncEnumerable<TItem>>> RequestStreamAsync<TItem>(
        this AxiosClient client,
        IApiRequest<TItem> request,
        CancellationToken cancellationToken = default)
        => Wrap(client).RequestStreamAsync(request, cancellationToken);

    /// <summary>以指定帧格式读取流式响应；语义与 <see cref="ApiClient.RequestStreamAsync{TItem}(IApiRequest{TItem}, AxiosStreamFormat, Action{AxiosConfig}?, CancellationToken)"/> 一致。</summary>
    public static Task<AxiosResponse<IAsyncEnumerable<TItem>>> RequestStreamAsync<TItem>(
        this AxiosClient client,
        IApiRequest<TItem> request,
        AxiosStreamFormat format,
        Action<AxiosConfig>? configure = null,
        CancellationToken cancellationToken = default)
        => Wrap(client).RequestStreamAsync(request, format, configure, cancellationToken);

    // 声明式门面只是同一客户端的一层包装，不拥有客户端，也不缓存任何状态。
    private static ApiClient Wrap(AxiosClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return new ApiClient(client, ownsAxiosClient: false);
    }
}
