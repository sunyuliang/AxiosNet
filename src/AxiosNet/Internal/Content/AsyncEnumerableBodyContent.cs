using System.Net;

namespace AxiosNet.Internal.Content;

/// <summary>流式请求正文：按调用方产出的块顺序写出，不缓冲整段；长度未知，按 chunked / DATA 帧发送。</summary>
/// <remarks>只能发送一次：开启重试 / 备用端点时由 <see cref="RetryBody"/> 在首次发送前拒绝。</remarks>
internal sealed class AsyncEnumerableBodyContent : HttpContent
{
    private readonly Func<CancellationToken, IAsyncEnumerator<ReadOnlyMemory<byte>>> _open;

    // 生产端失败时要带出去的生效配置（错误模型要求失败都读得到 Config）。
    private readonly AxiosConfig _config;

    private AsyncEnumerableBodyContent(Func<CancellationToken, IAsyncEnumerator<ReadOnlyMemory<byte>>> open, AxiosConfig config)
    {
        _open = open;
        _config = config;
    }

    /// <summary><c>IAsyncEnumerable&lt;byte[]&gt;</c>：直接按块转发（空块跳过）。</summary>
    public static AsyncEnumerableBodyContent FromByteChunks(IAsyncEnumerable<byte[]> chunks, AxiosConfig config)
        => new(ct => Forward(chunks, ct), config);

    /// <summary><c>IAsyncEnumerable&lt;ReadOnlyMemory&lt;byte&gt;&gt;</c>：直接按块转发。</summary>
    public static AsyncEnumerableBodyContent FromMemoryChunks(IAsyncEnumerable<ReadOnlyMemory<byte>> chunks, AxiosConfig config)
        => new(chunks.GetAsyncEnumerator, config);

    // 无令牌重载在 netstandard2.0 上是唯一入口：把写出逻辑抽成共用核心，两个重载只是令牌不同。
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => WriteChunksAsync(stream, CancellationToken.None);

#if !NETSTANDARD2_0
    /// <summary>写正文：把异步可枚举的块逐块写进流；长度不可知，只能分块传输。</summary>
    protected override Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken)
        => WriteChunksAsync(stream, cancellationToken);
#endif

    /// <summary>逐块写出：令牌在这个时刻才拿得到，交给调用方的迭代器（[EnumeratorCancellation] 会接住它）。</summary>
    /// <remarks>netstandard2.0 的 HttpContent 不提供带令牌的虚重载，那里固定传 <see cref="CancellationToken.None"/>。</remarks>
    private async Task WriteChunksAsync(Stream stream, CancellationToken cancellationToken)
    {
        var enumerator = _open(cancellationToken);
        await using (enumerator.ConfigureAwait(false))
        {
            while (await MoveNextAsync(enumerator).ConfigureAwait(false))
            {
                var chunk = enumerator.Current;
                if (chunk.IsEmpty)
                {
                    continue;
                }

                await stream.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>推进一格；生产端（调用方的迭代器）抛的异常归成 <c>ERR_BAD_REQUEST</c>，写出侧的 IO 失败照常上抛。</summary>
    private async ValueTask<bool> MoveNextAsync(IAsyncEnumerator<ReadOnlyMemory<byte>> enumerator)
    {
        try
        {
            return await enumerator.MoveNextAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
        {
            // 这里是「正文没造出来」，不是网络故障：原始异常进 Cause，调用方看得到业务原因。
            throw AxiosException.StreamingBodyFailed(_config, ex);
        }
    }

    /// <summary>长度未知：始终走分块传输，不猜。</summary>
    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    /// <summary>把 <c>byte[]</c> 序列转成 <c>ReadOnlyMemory&lt;byte&gt;</c> 序列，统一内部消费形态。</summary>
    private static async IAsyncEnumerator<ReadOnlyMemory<byte>> Forward(
        IAsyncEnumerable<byte[]> chunks,
        CancellationToken cancellationToken)
    {
        await using var enumerator = chunks.GetAsyncEnumerator(cancellationToken);
        while (await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            yield return enumerator.Current;
        }
    }
}
