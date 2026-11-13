using System.Net;

namespace AxiosNet.Internal;

/// <summary>流式请求正文：按调用方产出的块顺序写出，不缓冲整段；长度未知，按 chunked / DATA 帧发送。</summary>
/// <remarks>只能发送一次：开启重试 / 备用端点时由 <see cref="RetryBody"/> 在首次发送前拒绝。</remarks>
internal sealed class AsyncEnumerableBodyContent : HttpContent
{
    private readonly Func<CancellationToken, IAsyncEnumerator<ReadOnlyMemory<byte>>> _open;

    private AsyncEnumerableBodyContent(Func<CancellationToken, IAsyncEnumerator<ReadOnlyMemory<byte>>> open)
        => _open = open;

    /// <summary><c>IAsyncEnumerable&lt;byte[]&gt;</c>：直接按块转发（空块跳过）。</summary>
    public static AsyncEnumerableBodyContent FromByteChunks(IAsyncEnumerable<byte[]> chunks)
        => new(ct => Forward(chunks, ct));

    /// <summary><c>IAsyncEnumerable&lt;ReadOnlyMemory&lt;byte&gt;&gt;</c>：直接按块转发。</summary>
    public static AsyncEnumerableBodyContent FromMemoryChunks(IAsyncEnumerable<ReadOnlyMemory<byte>> chunks)
        => new(chunks.GetAsyncEnumerator);

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
            while (await enumerator.MoveNextAsync().ConfigureAwait(false))
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
