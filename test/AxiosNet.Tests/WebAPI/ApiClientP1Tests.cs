using System.Net;
using System.Net.Http;
using System.Text;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;

namespace AxiosNet.Tests.WebAPI;

/// <summary>
/// P1：[Authorize] / [AllowAnonymous] / IAccessTokenProvider，以及流式响应（NDJSON / SSE）。
/// </summary>
public sealed class ApiClientP1Tests
{
    // ------------------------------------------------------------------
    // 授权声明
    // ------------------------------------------------------------------

    [Fact]
    public async Task Authorize_UsesTokenProvider_WithBearerPrefix()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url)
        {
            AccessTokenProvider = new StaticTokenProvider("tok-1")
        };

        await client.RequestAsync(new AuthorizedRequest());

        Assert.Equal("Bearer tok-1", Assert.Single(server.Received).Headers["Authorization"]);
    }

    [Fact]
    public async Task Authorize_SchemeFromAttribute_IsUsed()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url)
        {
            AccessTokenProvider = new StaticTokenProvider("dXNlcjpwYXNz")
        };

        await client.RequestAsync(new BasicAuthorizedRequest());

        Assert.Equal("Basic dXNlcjpwYXNz", Assert.Single(server.Received).Headers["Authorization"]);
    }

    [Fact]
    public async Task AllowAnonymous_SkipsTokenAndAuthorizationProviders()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        var recorder = new RecordingAuthorizationProvider();
        using var client = new ApiClient(server.Url)
        {
            AccessTokenProvider = new StaticTokenProvider("tok-1"),
            Authorization = recorder
        };

        await client.RequestAsync(new AnonymousRequest());

        Assert.False(Assert.Single(server.Received).Headers.ContainsKey("Authorization"));
        Assert.Equal(0, recorder.Calls);
    }

    [Fact]
    public async Task ExplicitAuthorizationHeader_IsNotOverwrittenByTokenProvider()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url)
        {
            AccessTokenProvider = new StaticTokenProvider("tok-1")
        };

        await client.RequestAsync(new ExplicitAuthorizationRequest { Authorization = "Signature abc" });

        Assert.Equal("Signature abc", Assert.Single(server.Received).Headers["Authorization"]);
    }

    [Fact]
    public async Task AuthorizationProvider_RunsAfterTokenProvider_AndCanOverride()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url)
        {
            AccessTokenProvider = new StaticTokenProvider("tok-1"),
            Authorization = new OverridingAuthorizationProvider("Final xyz")
        };

        await client.RequestAsync(new AuthorizedRequest());

        Assert.Equal("Final xyz", Assert.Single(server.Received).Headers["Authorization"]);
    }

    [Fact]
    public async Task ExplicitAuthorizationInInstanceDefaults_IsNotOverwrittenByTokenProvider()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url, defaults => defaults.Headers.Set("Authorization", "Bearer static"))
        {
            AccessTokenProvider = new StaticTokenProvider("tok-1")
        };

        await client.RequestAsync(new AuthorizedRequest());

        Assert.Equal("Bearer static", Assert.Single(server.Received).Headers["Authorization"]);
    }

    [Fact]
    public async Task NullToken_WritesNoHeader()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url)
        {
            AccessTokenProvider = new StaticTokenProvider(null)
        };

        await client.RequestAsync(new AuthorizedRequest());

        Assert.False(Assert.Single(server.Received).Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task UnannotatedModel_StillRunsAuthorizationProvider()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        var recorder = new RecordingAuthorizationProvider();
        using var client = new ApiClient(server.Url) { Authorization = recorder };

        await client.RequestAsync(new UnannotatedRequest());

        Assert.Equal(1, recorder.Calls);
    }

    [Fact]
    public void AuthorizeAndAllowAnonymous_OnSameModel_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ApiClient("http://127.0.0.1:1/").GetUri(new ConflictingAuthorizationRequest()));

        Assert.Contains("[Authorize]", ex.Message);
        Assert.Contains("[AllowAnonymous]", ex.Message);
    }

    // ------------------------------------------------------------------
    // 流式：NDJSON
    // ------------------------------------------------------------------

    [Fact]
    public async Task Ndjson_ReadsAllFrames_IncludingSplitMultiByteCharacters()
    {
        var first = Encoding.UTF8.GetBytes("{\"n\":1,\"text\":\"你好\"}\n");
        // 在「好」的三个字节中间切开：分帧必须按字节做，不能先解码
        var second = Encoding.UTF8.GetBytes("{\"n\":2,\"text\":\"再见\"}\n");
        var split = first.Length - 5;

        using var client = CreateStreamingClient(
            new ChunkedHandler(new[]
            {
                first[..split],
                [.. first[split..], .. second[..3]],
                second[3..]
            }));

        var response = await client.RequestStreamAsync(new StreamRequest());

        var items = await CollectAsync(response.Data!);
        Assert.Equal(200, response.Status);
        Assert.Equal(2, items.Count);
        Assert.Equal(1, items[0].N);
        Assert.Equal("你好", items[0].Text);
        Assert.Equal(2, items[1].N);
        Assert.Equal("再见", items[1].Text);
    }

    [Fact]
    public async Task Ndjson_EmptyStream_YieldsNothing()
    {
        using var client = CreateStreamingClient(new ChunkedHandler(Array.Empty<byte[]>()));

        var response = await client.RequestStreamAsync(new StreamRequest());

        Assert.Empty(await CollectAsync(response.Data!));
    }

    [Fact]
    public async Task Ndjson_InvalidFrame_ReportsBadResponse()
    {
        using var client = CreateStreamingClient(
            new ChunkedHandler(new[] { Encoding.UTF8.GetBytes("{oops}\n") }));

        var response = await client.RequestStreamAsync(new StreamRequest());

        var error = await Assert.ThrowsAsync<AxiosException>(() => CollectAsync(response.Data!));
        Assert.Equal(AxiosException.ErrBadResponse, error.Code);
    }

    [Fact]
    public async Task Ndjson_MidStreamDisconnect_ReportsNetworkError()
    {
        var chunk = Encoding.UTF8.GetBytes("{\"n\":1}\n");
        using var client = CreateStreamingClient(new ChunkedHandler(new[] { chunk }, failAtIndex: 1));

        var response = await client.RequestStreamAsync(new StreamRequest());

        var error = await Assert.ThrowsAsync<AxiosException>(() => CollectAsync(response.Data!));
        Assert.Equal(AxiosException.ErrNetwork, error.Code);
    }

    [Fact]
    public async Task Stream_CanOnlyBeEnumeratedOnce()
    {
        using var client = CreateStreamingClient(
            new ChunkedHandler(new[] { Encoding.UTF8.GetBytes("{\"n\":1}\n") }));

        var response = await client.RequestStreamAsync(new StreamRequest());

        await CollectAsync(response.Data!);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CollectAsync(response.Data!));
        Assert.Contains("只能枚举一次", error.Message);
    }

    // ------------------------------------------------------------------
    // 流式：SSE
    // ------------------------------------------------------------------

    [Fact]
    public async Task Sse_ReadsDataFrames_SkipsComments_AndStopsAtDoneSentinel()
    {
        var body = string.Concat(
            ": keep-alive\n\n",
            "event: tick\ndata: {\"n\":1,\"text\":\"a\"}\n\n",
            "data: {\"n\":2,\n",
            "data: \"text\":\"b\"}\n\n",
            "data: [DONE]\n\n",
            "data: {\"n\":3}\n\n");

        using var client = CreateStreamingClient(
            new ChunkedHandler(new[] { Encoding.UTF8.GetBytes(body) }));

        var response = await client.RequestStreamAsync(new StreamRequest(), AxiosStreamFormat.ServerSentEvents);

        var items = await CollectAsync(response.Data!);
        Assert.Equal(2, items.Count);
        Assert.Equal(1, items[0].N);
        Assert.Equal("a", items[0].Text);
        Assert.Equal(2, items[1].N);
        Assert.Equal("b", items[1].Text);
    }

    [Fact]
    public async Task Sse_FrameWithoutTrailingBlankLine_IsStillDelivered()
    {
        using var client = CreateStreamingClient(
            new ChunkedHandler(new[] { Encoding.UTF8.GetBytes("data: {\"n\":7}\n") }));

        var response = await client.RequestStreamAsync(new StreamRequest(), AxiosStreamFormat.ServerSentEvents);

        var items = await CollectAsync(response.Data!);
        Assert.Equal(7, Assert.Single(items).N);
    }

    // ------------------------------------------------------------------
    // 流式：超时口径（D1-A）
    // ------------------------------------------------------------------

    [Fact]
    public async Task Stream_TimeoutOnlyCoversHeaders_BodyCanOutliveIt()
    {
        var chunks = new[]
        {
            Encoding.UTF8.GetBytes("{\"n\":1}\n"),
            Encoding.UTF8.GetBytes("{\"n\":2}\n")
        };

        using var client = CreateStreamingClient(
            new ChunkedHandler(chunks, initialDelay: TimeSpan.FromMilliseconds(400)),
            defaults: config => config.Timeout = TimeSpan.FromMilliseconds(150));

        var response = await client.RequestStreamAsync(new StreamRequest());

        var items = await CollectAsync(response.Data!);
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task StreamIdleTimeout_FiresWhenServerStalls()
    {
        using var client = CreateStreamingClient(
            new ChunkedHandler(new[] { Encoding.UTF8.GetBytes("{\"n\":1}\n") }, stallAfterChunks: true),
            timeout: TimeSpan.FromSeconds(10),
            idle: TimeSpan.FromMilliseconds(150));

        var response = await client.RequestStreamAsync(new StreamRequest());

        var error = await Assert.ThrowsAsync<AxiosException>(() => CollectAsync(response.Data!));
        Assert.Equal(AxiosException.ErrConnAborted, error.Code);
        Assert.Contains("idle", error.Message);
    }

    // ------------------------------------------------------------------
    // 流式：状态码错误与错误工厂
    // ------------------------------------------------------------------

    [Fact]
    public async Task Stream_HttpError_ThrowsBeforeEnumeration_AndHonorsErrorFactory()
    {
        using var client = CreateStreamingClient(
            new ChunkedHandler(Array.Empty<byte[]>(), statusCode: HttpStatusCode.BadRequest,
                body: Encoding.UTF8.GetBytes("{\"code\":\"E9\"}")));
        client.ErrorFactory = new ApiErrorFactory<StreamErrorBody>();

        var error = await Assert.ThrowsAsync<ApiError<StreamErrorBody>>(
            () => client.RequestStreamAsync(new StreamRequest()));

        Assert.Equal(400, error.Status);
        Assert.Equal("E9", error.Error?.Code);
    }

    // ------------------------------------------------------------------
    // 辅助
    // ------------------------------------------------------------------

    private static ApiClient CreateStreamingClient(
        HttpMessageHandler handler,
        Action<AxiosConfig>? defaults = null,
        TimeSpan? timeout = null,
        TimeSpan? idle = null)
    {
        var config = new AxiosConfig { BaseAddress = "http://stream.test/" };
        defaults?.Invoke(config);
        if (timeout is { } value)
        {
            config.Timeout = value;
        }

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

    public sealed record StreamItem(int N, string? Text);

    public sealed class StreamErrorBody
    {
        public string? Code { get; set; }
    }

    private sealed class StaticTokenProvider : IAccessTokenProvider
    {
        private readonly string? _token;

        public StaticTokenProvider(string? token) => _token = token;

        public ValueTask<string?> GetTokenAsync(ApiAuthContext context, CancellationToken cancellationToken)
            => new(_token);
    }

    private sealed class RecordingAuthorizationProvider : IAuthorizationProvider
    {
        public int Calls { get; private set; }

        public ValueTask ApplyAsync(ApiAuthContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return default;
        }
    }

    private sealed class OverridingAuthorizationProvider : IAuthorizationProvider
    {
        private readonly string _value;

        public OverridingAuthorizationProvider(string value) => _value = value;

        public ValueTask ApplyAsync(ApiAuthContext context, CancellationToken cancellationToken)
        {
            context.Config.Headers.Set("Authorization", _value);
            return default;
        }
    }

    /// <summary>用预置分块模拟流式响应；可在中途抛 IO 异常或读完挂起。</summary>
    private sealed class ScriptedStream : Stream
    {
        private readonly byte[][] _chunks;
        private readonly int _failAtIndex;
        private readonly bool _stallAfterChunks;
        private readonly TimeSpan _initialDelay;
        private int _index;
        private int _offset;

        public ScriptedStream(
            byte[][] chunks,
            int failAtIndex,
            bool stallAfterChunks,
            TimeSpan initialDelay)
        {
            _chunks = chunks;
            _failAtIndex = failAtIndex;
            _stallAfterChunks = stallAfterChunks;
            _initialDelay = initialDelay;
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

        public override int Read(Span<byte> buffer)
        {
            var temp = new byte[buffer.Length];
            var read = ReadAsync(temp.AsMemory(), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            temp.AsSpan(0, read).CopyTo(buffer);
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

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
        private readonly int _failAtIndex;
        private readonly bool _stallAfterChunks;
        private readonly TimeSpan _initialDelay;
        private readonly HttpStatusCode _statusCode;
        private readonly byte[]? _body;

        public ChunkedHandler(
            byte[][] chunks,
            int failAtIndex = -1,
            bool stallAfterChunks = false,
            TimeSpan initialDelay = default,
            HttpStatusCode statusCode = HttpStatusCode.OK,
            byte[]? body = null)
        {
            _chunks = chunks;
            _failAtIndex = failAtIndex;
            _stallAfterChunks = stallAfterChunks;
            _initialDelay = initialDelay;
            _statusCode = statusCode;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpContent content = _body is not null
                ? new ByteArrayContent(_body)
                : new StreamContent(new ScriptedStream(_chunks, _failAtIndex, _stallAfterChunks, _initialDelay));

            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-ndjson");

            var response = new HttpResponseMessage(_statusCode)
            {
                Content = content,
                RequestMessage = request
            };

            return Task.FromResult(response);
        }
    }

    // ------------------------------------------------------------------
    // 请求模型
    // ------------------------------------------------------------------

    [Get("secure")]
    [Authorize]
    private sealed class AuthorizedRequest : IApiRequest<Person>
    {
    }

    [Get("secure")]
    [Authorize("Basic")]
    private sealed class BasicAuthorizedRequest : IApiRequest<Person>
    {
    }

    [Post("login")]
    [AllowAnonymous]
    private sealed class AnonymousRequest : IApiRequest<Person>
    {
    }

    [Get("secure")]
    [Authorize]
    private sealed class ExplicitAuthorizationRequest : IApiRequest<Person>
    {
        [Header("Authorization")]
        public string? Authorization { get; set; }
    }

    [Get("plain")]
    private sealed class UnannotatedRequest : IApiRequest<Person>
    {
    }

    [Get("conflict")]
    [Authorize]
    [AllowAnonymous]
    private sealed class ConflictingAuthorizationRequest : IApiRequest<Person>
    {
    }

    [Get("stream")]
    private sealed class StreamRequest : IApiRequest<StreamItem>
    {
    }
}
