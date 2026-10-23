using System.Net;
using System.Net.Http;
using System.Text;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;

namespace AxiosNet.Tests.WebAPI;

/// <summary>
/// 2026-10-23 审计 B1–B8 的回归用例（先红后绿）。
/// </summary>
public sealed class Audit20261023B1B8FixTests
{
    // ------------------------------------------------------------------
    // B1：未标注的平铺字典在 POST 上不能被默认 Body 吞掉
    // ------------------------------------------------------------------

    [Fact]
    public async Task B1_UnannotatedFlatMap_OnPost_StaysInQuery()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new FlatMapOnlyPostRequest
        {
            Filters = new Dictionary<string, string>
            {
                ["tag"] = "x",
                ["page"] = "2"
            }
        });

        var received = Assert.Single(server.Received);
        Assert.Equal("/search", received.Path);
        Assert.NotNull(received.Query);
        Assert.Contains("tag=x", received.Query);
        Assert.Contains("page=2", received.Query);
        Assert.Empty(received.Body);
    }

    // ------------------------------------------------------------------
    // B4：平铺字典不产生具名参数，不能占用「同名查询绑定」的唯一名额
    // ------------------------------------------------------------------

    [Fact]
    public void B4_FlatMap_DoesNotConflictWithNamedQueryBinding()
    {
        using var client = new ApiClient("http://api.test/");

        var uri = client.GetUri(new FlatMapWithNamedQueryRequest
        {
            Filters = new Dictionary<string, string> { ["tag"] = "x" },
            Label = "hi"
        });

        Assert.Contains("Filters=hi", uri);
        Assert.Contains("tag=x", uri);
    }

    // ------------------------------------------------------------------
    // B3：基类的「删除声明」不能删掉派生类同名静态头的值
    // ------------------------------------------------------------------

    [Fact]
    public async Task B3_DerivedStaticHeader_WinsOverBaseRemoval()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new DerivedStaticHeaderRequest());

        var received = Assert.Single(server.Received);
        Assert.True(received.Headers.TryGetValue("X-Trace", out var trace), "X-Trace 被基类的删除声明误删");
        Assert.Equal("derived", trace);
    }

    // ------------------------------------------------------------------
    // B7：流式请求的生效配置回放时不能泄漏 ResponseType.Stream
    // ------------------------------------------------------------------

    [Fact]
    public async Task B7_StreamResponseConfigReplay_DoesNotLeakStreamResponseType()
    {
        using var client = CreateStreamingClient(
            new ChunkedHandler(new[] { Encoding.UTF8.GetBytes("{\"n\":1}\n") }));

        var response = await client.RequestStreamAsync(new FrameRequest());
        Assert.Equal(ResponseType.Stream, response.Config!.ResponseType);
        Assert.Single(await CollectAsync(response.Data!));

        var replayed = await client.Axios.RequestAsync<FrameItem>(response.Config!);

        Assert.Equal(ResponseType.Json, replayed.Config!.ResponseType);
        var item = Assert.IsType<FrameItem>(replayed.Data);
        Assert.Equal(1, item.N);
    }

    // ------------------------------------------------------------------
    // B2：枚举令牌在挂起的读取中取消，也要翻译成 ERR_CANCELED
    // ------------------------------------------------------------------

    [Fact]
    public async Task B2_CancelDuringPendingRead_ReportsCanceled()
    {
        using var client = CreateStreamingClient(new ChunkedHandler(
            new[] { Encoding.UTF8.GetBytes("{\"n\":1}\n") },
            stallAfterChunks: true));

        var response = await client.RequestStreamAsync(new FrameRequest());

        using var cts = new CancellationTokenSource();
        await using var enumerator = response.Data!.GetAsyncEnumerator(cts.Token);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(1, enumerator.Current.N);

        var pending = enumerator.MoveNextAsync().AsTask();
        await Task.Delay(150);
        cts.Cancel();

        var error = await Assert.ThrowsAsync<AxiosException>(() => pending);
        Assert.Equal(AxiosException.ErrCanceled, error.Code);
    }

    // ------------------------------------------------------------------
    // B5：UTF-16 / UTF-32 charset 声明会破坏按字节分帧，必须在取流时报错
    // ------------------------------------------------------------------

    [Fact]
    public async Task B5_NonAsciiCompatibleCharset_ThrowsClearError()
    {
        using var client = CreateStreamingClient(new ChunkedHandler(
            new[] { Encoding.Unicode.GetBytes("{\"n\":1}\n") },
            contentType: "application/x-ndjson; charset=utf-16"));

        var error = await Assert.ThrowsAsync<AxiosException>(
            () => client.RequestStreamAsync(new FrameRequest()));

        Assert.Equal(AxiosException.ErrBadResponse, error.Code);
        Assert.Contains("charset", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // B6：反序列化结果类型不符时不能静默返回 default(T)
    // ------------------------------------------------------------------

    [Fact]
    public async Task B6_WrongDeserializedType_ReportsBadResponse()
    {
        AxiosContent.Register("application/x-audit-wrong-type", new WrongTypeSerializer());

        using var client = CreateStreamingClient(new ChunkedHandler(
            new[] { Encoding.UTF8.GetBytes("anything\n") },
            contentType: "application/x-audit-wrong-type"));

        var response = await client.RequestStreamAsync(new FrameRequest());

        var error = await Assert.ThrowsAsync<AxiosException>(() => CollectAsync(response.Data!));

        Assert.Equal(AxiosException.ErrBadResponse, error.Code);
        Assert.Contains(nameof(FrameItem), error.Message);
        Assert.NotNull(error.Cause);
        Assert.Contains("System.String", error.Cause!.Message);
    }

    // ------------------------------------------------------------------
    // B8：调用方在两次 MoveNext 之间停顿，不算「网络空闲」
    // ------------------------------------------------------------------

    [Fact]
    public async Task B8_ConsumerPauseBetweenFrames_DoesNotTripIdleTimeout()
    {
        using var client = CreateStreamingClient(
            new ChunkedHandler(
                new[]
                {
                    Encoding.UTF8.GetBytes("{\"n\":1}\n"),
                    Encoding.UTF8.GetBytes("{\"n\":2}\n")
                },
                chunkDelays: new[]
                {
                    TimeSpan.Zero,
                    TimeSpan.FromMilliseconds(40)
                },
                stallAfterChunks: true),
            idle: TimeSpan.FromMilliseconds(150));

        var response = await client.RequestStreamAsync(new FrameRequest());

        await using var enumerator = response.Data!.GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(1, enumerator.Current.N);

        // 消费者处理第一帧耗时 400ms（> idle 150ms）：计时器若在读取之外也走，这里就会误判空闲
        await Task.Delay(400);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(2, enumerator.Current.N);
    }

    // ------------------------------------------------------------------
    // 辅助
    // ------------------------------------------------------------------

    private static ApiClient CreateStreamingClient(HttpMessageHandler handler, TimeSpan? idle = null)
    {
        var config = new AxiosConfig { BaseAddress = "http://stream.test/" };
        var client = new ApiClient(new AxiosClient(config, handler: handler, disposeHandler: true), ownsAxiosClient: true);
        if (idle is { } idleValue)
        {
            client.StreamIdleTimeout = idleValue;
        }

        return client;
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        await foreach (var item in source.ConfigureAwait(false))
        {
            items.Add(item);
        }

        return items;
    }

    // ------------------------------------------------------------------
    // 测试模型
    // ------------------------------------------------------------------

    [Post("search")]
    private sealed class FlatMapOnlyPostRequest : IApiRequest<Person>
    {
        public Dictionary<string, string> Filters { get; set; } = new();
    }

    [Get("search")]
    private sealed class FlatMapWithNamedQueryRequest : IApiRequest<Person>
    {
        public Dictionary<string, string> Filters { get; set; } = new();

        [Query("Filters")]
        public string? Label { get; set; }
    }

    [Headers("X-Trace")]
    private abstract class BaseWithStaticRemoval : IApiRequest<Person>
    {
    }

    [Get("derived-header")]
    [Headers("X-Trace: derived")]
    private sealed class DerivedStaticHeaderRequest : BaseWithStaticRemoval
    {
    }

    [Get("stream")]
    private sealed class FrameRequest : IApiRequest<FrameItem>
    {
    }

    public sealed record FrameItem(int N);

    // ------------------------------------------------------------------
    // 测试替身
    // ------------------------------------------------------------------

    /// <summary>永远返回 string 的序列化器：用于制造「非 null 但类型不符」的帧值。</summary>
    private sealed class WrongTypeSerializer : IContentSerializer
    {
        public string Serialize(object? value, Type type, AxiosConfig config) => string.Empty;

        public object? Deserialize(string text, Type type, AxiosConfig config) => "wrong-type";
    }

    /// <summary>预置分块的响应流；可为每个分块指定读取前的延迟，读完后挂起。</summary>
    private sealed class PacedStream : Stream
    {
        private readonly byte[][] _chunks;
        private readonly TimeSpan[] _delays;
        private readonly bool _stallAfterChunks;
        private int _index;
        private int _offset;

        public PacedStream(byte[][] chunks, TimeSpan[] delays, bool stallAfterChunks)
        {
            _chunks = chunks;
            _delays = delays;
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

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_index >= _chunks.Length)
            {
                if (_stallAfterChunks)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                }

                return 0;
            }

            if (_offset == 0 && _index < _delays.Length && _delays[_index] > TimeSpan.Zero)
            {
                await Task.Delay(_delays[_index], cancellationToken).ConfigureAwait(false);
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

    private sealed class ChunkedHandler : HttpMessageHandler
    {
        private readonly byte[][] _chunks;
        private readonly TimeSpan[] _delays;
        private readonly bool _stallAfterChunks;
        private readonly string _contentType;

        public ChunkedHandler(
            byte[][] chunks,
            bool stallAfterChunks = false,
            TimeSpan[]? chunkDelays = null,
            string contentType = "application/x-ndjson")
        {
            _chunks = chunks;
            _stallAfterChunks = stallAfterChunks;
            _delays = chunkDelays ?? new TimeSpan[chunks.Length];
            _contentType = contentType;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StreamContent(new PacedStream(_chunks, _delays, _stallAfterChunks));
            content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(_contentType);

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request
            };

            return Task.FromResult(response);
        }
    }
}
