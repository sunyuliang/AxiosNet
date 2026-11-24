using System.ComponentModel;

namespace AxiosNet.WebAPI.Metadata;

/// <summary>
/// 生成代理的调用桥：把生成期烘焙的接口方法声明交给共享绑定 / 发送实现。
/// 只在源生成代码里使用；每次调用都复用同一个 <see cref="InterfaceMethodMetadata"/>，绑定计划只构建一次。
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class InterfaceMethodRuntime
{
    private readonly InterfaceMethodMetadata _metadata;

    internal InterfaceMethodRuntime(InterfaceMethodMetadata metadata)
        => _metadata = metadata;

    /// <summary>底层契约（生成代理不读它；诊断与快路判定断言用）。</summary>
    internal InterfaceMethodMetadata Metadata => _metadata;

    /// <summary><c>Task&lt;AxiosResponse&gt;</c>：交付完整响应，正文按 JsonElement 原样给。</summary>
    public Task<AxiosResponse> SendUntypedAsync(ApiClient client, object?[] args, CancellationToken cancellationToken)
        => _metadata.SendUntypedAsync(client, args, cancellationToken);

    /// <summary><c>Task&lt;AxiosResponse&lt;T&gt;&gt;</c>：交付完整响应。</summary>
    public Task<AxiosResponse<T>> SendResponseAsync<T>(ApiClient client, object?[] args, CancellationToken cancellationToken)
        => _metadata.SendResponseAsync<T>(client, args, cancellationToken);

    /// <summary><c>Task&lt;T&gt;</c>：只交付正文。</summary>
    public Task<T?> SendDataAsync<T>(ApiClient client, object?[] args, CancellationToken cancellationToken)
        => _metadata.SendDataAsync<T>(client, args, cancellationToken);

    /// <summary><c>Task</c>：只完成请求，正文丢弃。</summary>
    public Task SendVoidAsync(ApiClient client, object?[] args, CancellationToken cancellationToken)
        => _metadata.SendVoidAsync(client, args, cancellationToken);

    /// <summary><c>Task&lt;AxiosResponse&lt;IAsyncEnumerable&lt;T&gt;&gt;&gt;</c>：按 <paramref name="format"/> 交付帧。</summary>
    public Task<AxiosResponse<IAsyncEnumerable<TItem>>> SendStreamAsync<TItem>(
        ApiClient client,
        object?[] args,
        CancellationToken cancellationToken,
        AxiosStreamFormat format)
        => _metadata.SendStreamAsync<TItem>(client, args, cancellationToken, format);

    // 下面四个成员只给「字面量路由 + 单整份正文（或无正文）」的生成代理用：没有参数数组，也没有绑定计划遍历；
    // 声明不满足该形状时运行期自动退回上面的通用路径。名字与通用路径分开，避免正文本身就是数组时重载歧义。

    /// <summary><c>Task&lt;AxiosResponse&lt;T&gt;&gt;</c>：整份正文（<c>null</c> 表示不发正文）直接交给内核。</summary>
    public Task<AxiosResponse<T>> SendRootBodyResponseAsync<T>(ApiClient client, object? rootBody, CancellationToken cancellationToken)
        => _metadata.SendRootBodyResponseAsync<T>(client, rootBody, cancellationToken);

    /// <summary><c>Task&lt;AxiosResponse&gt;</c>：整份正文，正文按 JsonElement 原样给。</summary>
    public Task<AxiosResponse> SendRootBodyUntypedAsync(ApiClient client, object? rootBody, CancellationToken cancellationToken)
        => _metadata.SendRootBodyUntypedAsync(client, rootBody, cancellationToken);

    /// <summary><c>Task&lt;T&gt;</c>：整份正文，只交付正文。</summary>
    public Task<T?> SendRootBodyDataAsync<T>(ApiClient client, object? rootBody, CancellationToken cancellationToken)
        => _metadata.SendRootBodyDataAsync<T>(client, rootBody, cancellationToken);

    /// <summary><c>Task</c>：整份正文，只完成请求。</summary>
    public Task SendRootBodyVoidAsync(ApiClient client, object? rootBody, CancellationToken cancellationToken)
        => _metadata.SendRootBodyVoidAsync(client, rootBody, cancellationToken);
}
