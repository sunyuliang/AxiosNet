using System.ComponentModel;
using AxiosNet.WebAPI.Internal;

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
}
