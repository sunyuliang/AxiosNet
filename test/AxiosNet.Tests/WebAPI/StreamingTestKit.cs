using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Net;

namespace AxiosNet.Tests.WebAPI;

// ---- 流式用例公用替身 ----
//
// 分帧用例曾经每个阶段各带一套 Handler / 流 / 工厂（能力互相嵌套），这里收成一份并集：
// 需要「逐块延迟」「读一半断流」「读完挂起」「换 Content-Type」都只是同一个类换参数。

/// <summary>
/// 按脚本吐正文的 Handler。分块可逐块延迟、可在指定位置断流或读完挂起；
/// 也可退化成「一次给完整正文 + 任意 Content-Type」。
/// </summary>
internal sealed class ScriptedStreamHandler : HttpMessageHandler
{
    private readonly byte[][] _chunks;
    private readonly TimeSpan[] _chunkDelays;
    private readonly TimeSpan _initialDelay;
    private readonly int _failAtIndex;
    private readonly bool _stallAfterChunks;
    private readonly string _contentType;
    private readonly HttpStatusCode _statusCode;
    private readonly byte[]? _body;

    /// <summary>抵达传输层的次数：越界的配置必须在为 0 时就被拦下。</summary>
    public int Calls { get; private set; }

    public ScriptedStreamHandler(
        byte[][] chunks,
        int failAtIndex = -1,
        bool stallAfterChunks = false,
        TimeSpan initialDelay = default,
        TimeSpan[]? chunkDelays = null,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        byte[]? body = null,
        string contentType = "application/x-ndjson")
    {
        _chunks = chunks;
        _failAtIndex = failAtIndex;
        _stallAfterChunks = stallAfterChunks;
        _initialDelay = initialDelay;
        _chunkDelays = chunkDelays ?? new TimeSpan[chunks.Length];
        _statusCode = statusCode;
        _body = body;
        _contentType = contentType;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // 计数在方法入口：只要真的抵达传输层就记一次，前面构正文失败也算。
        Calls++;

        HttpContent content = _body is not null
            ? new ByteArrayContent(_body)
            : new StreamContent(
                new ScriptedReadStream(_chunks, _chunkDelays, _initialDelay, _failAtIndex, _stallAfterChunks));

        // Parse 才接受带参数的完整头值（例如 text/event-stream; charset=utf-16）
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(_contentType);

        var response = new HttpResponseMessage(_statusCode)
        {
            Content = content,
            RequestMessage = request
        };

        return Task.FromResult(response);
    }
}

/// <summary>按脚本分块返回的响应流；同步读也走异步实现，用来把 CRLF / 多字节字符切到两次读里。</summary>
internal sealed class ScriptedReadStream : Stream
{
    private readonly byte[][] _chunks;
    private readonly TimeSpan[] _chunkDelays;
    private readonly TimeSpan _initialDelay;
    private readonly int _failAtIndex;
    private readonly bool _stallAfterChunks;
    private int _index;
    private int _offset;

    public ScriptedReadStream(
        byte[][] chunks,
        TimeSpan[] chunkDelays,
        TimeSpan initialDelay,
        int failAtIndex,
        bool stallAfterChunks)
    {
        _chunks = chunks;
        _chunkDelays = chunkDelays;
        _initialDelay = initialDelay;
        _failAtIndex = failAtIndex;
        _stallAfterChunks = stallAfterChunks;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (_index == 0 && _offset == 0 && _initialDelay > TimeSpan.Zero)
        {
            await Task.Delay(_initialDelay, cancellationToken).ConfigureAwait(false);
        }

        if (_index >= _chunks.Length)
        {
            if (_stallAfterChunks)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            if (_failAtIndex >= 0 && _index >= _failAtIndex)
            {
                throw new IOException("connection reset by peer");
            }

            return 0;
        }

        if (_offset == 0 && _index < _chunkDelays.Length && _chunkDelays[_index] > TimeSpan.Zero)
        {
            await Task.Delay(_chunkDelays[_index], cancellationToken).ConfigureAwait(false);
        }

        var chunk = _chunks[_index];
        var count = Math.Min(buffer.Length, chunk.Length - _offset);
        chunk.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        if (_offset >= chunk.Length)
        {
            _index++;
            _offset = 0;
        }

        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(Span<byte> buffer)
    {
        var temp = new byte[buffer.Length];
        var read = ReadAsync(temp.AsMemory(), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        temp.AsSpan(0, read).CopyTo(buffer);
        return read;
    }

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>流式用例的客户端组装与帧收集（各测试类用 <c>using static</c> 引入）。</summary>
internal static class StreamingTestKit
{
    /// <summary>流式用例统一跑在这个基地址上：不解析域名，禁用真实网络。</summary>
    internal const string BaseAddress = "http://stream.test/";

    internal static ApiClient CreateClient(
        HttpMessageHandler handler,
        Action<AxiosConfig>? configure = null,
        TimeSpan? timeout = null,
        TimeSpan? idle = null)
    {
        var config = new AxiosConfig { BaseAddress = BaseAddress };
        configure?.Invoke(config);
        if (timeout is { } timeoutValue)
        {
            config.Timeout = timeoutValue;
        }

        var client = new ApiClient(
            new AxiosClient(config, handler: handler, disposeHandler: true),
            ownsAxiosClient: true);

        if (idle is { } idleValue)
        {
            client.StreamIdleTimeout = idleValue;
        }

        return client;
    }

    /// <summary>把一次性的帧序列抽干成列表（流只能枚举一次）。</summary>
    internal static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        await foreach (var item in source.ConfigureAwait(false))
        {
            items.Add(item);
        }

        return items;
    }
}
