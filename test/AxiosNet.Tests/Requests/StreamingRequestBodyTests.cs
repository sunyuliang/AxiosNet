using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using AxiosNet.Retry;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Requests;

/// <summary>
/// 流式请求体：<c>IAsyncEnumerable&lt;byte[]&gt;</c> / <c>IAsyncEnumerable&lt;ReadOnlyMemory&lt;byte&gt;&gt;</c> 作为 data。
/// 覆盖 chunked 发送、其他异步序列的错误指引、不可重放拒绝（重试 / 备用端点 / 自定义内容）与取消。
/// </summary>
public sealed class StreamingRequestBodyTests
{
    private const string Expected = "hello streaming body";

    private static readonly byte[] Hello = Encoding.UTF8.GetBytes("hello ");

    private static readonly byte[] Streaming = Encoding.UTF8.GetBytes("streaming ");

    private static readonly byte[] Body = Encoding.UTF8.GetBytes("body");

    [Fact]
    public async Task AsyncEnumerableBytes_IsSentAsChunkedBody()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "x",
            ByteChunks(Hello, Streaming, Body),
            c => c.ContentType = AxiosContentType.OctetStream);

        var request = Assert.Single(server.Received);
        Assert.Equal(Expected, Encoding.UTF8.GetString(request.Body));

        // 长度未知 → 分块传输，且不伪造 Content-Length
        Assert.Contains("chunked", request.Headers["Transfer-Encoding"], StringComparison.OrdinalIgnoreCase);
        Assert.False(request.Headers.ContainsKey("Content-Length"));
        Assert.Equal(AxiosContentType.OctetStream, request.Headers["Content-Type"]);
    }

    [Fact]
    public async Task AsyncEnumerableMemory_IsSentAsChunkedBody()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "x",
            MemoryChunks(Hello, Streaming, Body),
            c => c.ContentType = AxiosContentType.OctetStream);

        var request = Assert.Single(server.Received);
        Assert.Equal(Expected, Encoding.UTF8.GetString(request.Body));
        Assert.Contains("chunked", request.Headers["Transfer-Encoding"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmptySequence_SendsEmptyChunkedBody()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "x",
            EmptyChunks(),
            c => c.ContentType = AxiosContentType.OctetStream);

        var request = Assert.Single(server.Received);
        Assert.Empty(request.Body);
        Assert.Contains("chunked", request.Headers["Transfer-Encoding"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OtherAsyncEnumerables_AreRejectedWithGuidance()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(server.Url + "x", Numbers()));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("IAsyncEnumerable<byte[]>", ex.Message);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Multipart_WithStreamingBody_IsRejected()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(
            server.Url + "x",
            ByteChunks(Hello),
            c => c.ContentType = AxiosContentType.MultipartFormData));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Retry_WithStreamingBody_IsRejectedBeforeSend()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            Retry = { Policy = RetryPolicy.Constant(2, TimeSpan.Zero) }
        });

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(
            server.Url + "x",
            ByteChunks(Hello),
            c => c.ContentType = AxiosContentType.OctetStream));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("流式请求体", ex.Message);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Failover_WithStreamingBody_IsRejectedBeforeSend()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            Failover = new() { Endpoints = new[] { new Uri(server.Url) } }
        });

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(
            server.Url + "x",
            new ReadOnlyMemoryChunks(),
            c => c.ContentType = AxiosContentType.OctetStream));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Hedging_WithStreamingBody_IsSentOnce()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            Hedging = new() { MaxHedges = 1, Delay = TimeSpan.Zero, HedgeNonIdempotentMethods = true }
        });

        await client.PostAsync(
            server.Url + "x",
            ByteChunks(Hello),
            c => c.ContentType = AxiosContentType.OctetStream);

        // 对冲是延迟优化而不是语义保证：正文不能并发重放时静默降级为单次发送（与 StreamContent 同一口径），
        // 不像重试 / 备用端点那样报错 —— 报错会把「只想更快」的配置变成不可用。
        var request = Assert.Single(server.Received);
        Assert.Equal("hello ", Encoding.UTF8.GetString(request.Body));
    }

    [Fact]
    public async Task Retry_WithJsonContent_IsRetriedWithFullBody()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) == 1
            ? TestJson.Response(new { error = "boom" }, statusCode: 500)
            : TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            Retry =
            {
                // 这里验的是正文可重放，不是默认的幂等门槛：POST 必须显式打开才会重试。
                Policy = new RetryPolicy
                {
                    MaxRetries = 2,
                    Delay = TimeSpan.Zero,
                    RetryNonIdempotentMethods = true
                }
            }
        });

        // JsonContent 的 ContentLength 是 null（每次从持有的值重新序列化），但它是可重放的：
        // 「长度未知」这条收紧规则不能把它误伤成配置错误，重发时正文也必须完整。
        using var content = JsonContent.Create(new { name = "a" });
        await client.PostAsync(server.Url + "x", content);

        Assert.Equal(2, server.Received.Count);
        Assert.All(server.Received, request => Assert.Equal("{\"name\":\"a\"}", Encoding.UTF8.GetString(request.Body)));
    }

    [Fact]
    public async Task Redirect307_ReEnumeratesStreamingBody()
    {
        await using var server = TestHttpServer.Start(request =>
            request.Path == "/start"
                ? new TestResponse(
                    307,
                    "Temporary Redirect",
                    Headers: new Dictionary<string, string> { ["Location"] = "/final" })
                : TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var enumerations = 0;
        async IAsyncEnumerable<byte[]> Chunks([EnumeratorCancellation] CancellationToken token = default)
        {
            enumerations++;
            token.ThrowIfCancellationRequested();
            yield return Hello;
            await Task.Yield();
            yield return Body;
        }

        await client.PostAsync(
            server.Url + "start",
            Chunks(),
            c => c.ContentType = AxiosContentType.OctetStream);

        // 307 保留方法与正文：库重新枚举同一份序列再发一遍（生产者必须能重启）。
        Assert.Equal(2, server.Received.Count);
        Assert.All(server.Received, request => Assert.Equal("hello body", Encoding.UTF8.GetString(request.Body)));
        Assert.Equal(2, enumerations);
    }

    [Fact]
    public async Task Retry_WithUnknownLengthCustomContent_IsRejected()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            Retry = { Policy = RetryPolicy.Constant(2, TimeSpan.Zero) }
        });

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.PostAsync(server.Url + "x", new UnknownLengthContent(Hello)));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("自定义 HttpContent", ex.Message);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Retry_WithKnownLengthCustomContent_IsRejected()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            Retry = { Policy = RetryPolicy.Constant(2, TimeSpan.Zero) }
        });

        // 长度已知不代表可重放：一次性流包装成自定义内容后，重发只会发出空正文或直接报错。
        // 与其让第二次尝试报出误导性的网络错误，不如在发送前按配置错误拒绝。
        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.PostAsync(server.Url + "x", new FixedLengthContent(Hello)));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("自定义 HttpContent", ex.Message);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task MaxBodyLength_DoesNotApplyToUnknownLengthStreamingBody()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        // 文档口径：MaxBodyLength 只对能算出长度的正文生效。流式正文长度未知，
        // 既拿不到长度可拦，也不该在发送中途失败——收齐比拦下更重要（拦下的其实是「已知长度」那条路）。
        await client.PostAsync(
            server.Url + "x",
            ByteChunks(Hello, Streaming, Body),
            c =>
            {
                c.ContentType = AxiosContentType.OctetStream;
                c.Limits.MaxBodyLength = 4;
            });

        var request = Assert.Single(server.Received);
        Assert.Equal(Expected, Encoding.UTF8.GetString(request.Body));
    }

    [Fact]
    public async Task StreamingBody_HonorsCallerCancellation()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // 非泛型 TaskCompletionSource 是 .NET 8 才有的 API：测试要同时跑 net6.0，用泛型版本。
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<byte[]> Slow([EnumeratorCancellation] CancellationToken token = default)
        {
            started.TrySetResult(true);
            while (true)
            {
                await Task.Delay(20, token);
                yield return new byte[1024];
            }
        }

        var send = client.PostAsync(
            server.Url + "x",
            Slow(),
            c => c.ContentType = AxiosContentType.OctetStream,
            cts.Token);

        await started.Task;
        cts.Cancel();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => send);
        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
    }

    // ------------------------------------------------------------------
    // 辅助
    // ------------------------------------------------------------------

    private static async IAsyncEnumerable<byte[]> ByteChunks(params byte[][] chunks)
    {
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }

    private static async IAsyncEnumerable<byte[]> EmptyChunks()
    {
        await Task.Yield();
        yield break;
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> MemoryChunks(params byte[][] chunks)
    {
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }

    private static async IAsyncEnumerable<int> Numbers()
    {
        await Task.Yield();
        yield return 1;
    }

    /// <summary>可重放但带异步迭代器的形态：备用端点测试用（与 ByteChunks 等价的类实现）。</summary>
    private sealed class ReadOnlyMemoryChunks : IAsyncEnumerable<ReadOnlyMemory<byte>>
    {
        public async IAsyncEnumerator<ReadOnlyMemory<byte>> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return Hello;

            await Task.Yield();
            yield return Body;
        }
    }

    /// <summary>长度未知的自定义内容：模拟用户自己写的流式 <see cref="HttpContent"/>。</summary>
    private sealed class UnknownLengthContent(byte[] payload) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(payload, 0, payload.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>长度已知的一次性内容：长度不能说明可重放，重试时同样必须拒绝。</summary>
    private sealed class FixedLengthContent(byte[] payload) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(payload, 0, payload.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = payload.Length;
            return true;
        }
    }
}
