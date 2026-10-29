using System.Net;

namespace AxiosNet.Internal;

/// <summary>
/// 流式请求正文（异步序列）：按调用方产出的块顺序写出，<b>不缓冲整段</b>。
/// <para>
/// 一次请求只能发送一次：序列被消费后无法回拨，因此开启重试 / 备用端点时由
/// <see cref="RetryBody"/> 在第一次发送之前拒绝（与不可 seek 的流同一口径）。
/// 长度始终未知，传输层按 chunked（HTTP/1.1）或 DATA 帧（HTTP/2）发送。
/// </para>
/// <para>
/// 只依赖基础框架里的 <see cref="IAsyncEnumerable{T}"/>：<c>PipeReader</c> 在 net6.0 / net8.0 上不在基础框架内，
/// 需要管道的地方请用 <c>pipe.Reader.AsStream()</c> 包装后按 <see cref="Stream"/> 传入（走 StreamContent 路径）。
/// </para>
/// </summary>
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

    // net6.0 上无令牌的 SerializeToStreamAsync(Stream, TransportContext) 仍是抽象成员，必须实现。
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken)
    {
        // 令牌在这个时刻才拿得到：交给调用方的迭代器（[EnumeratorCancellation] 会接住它）。
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
