using AxiosNet.Retry;
using AxiosNet.Tests.Infrastructure;
using System.Security.Cryptography;
using System.Text;

namespace AxiosNet.Tests.Requests;

/// <summary>
/// 发送前钩子（<see cref="AxiosConfig.CallbackSettings.OnSendingRequest"/>）：
/// 能看到什么（最终 URL / 请求头 / 已序列化正文）、每次尝试各触发一次、改写只作用于本次发送，以及失败时的错误归属。
/// </summary>
public sealed class SendingRequestHookTests
{
    [Fact]
    public async Task FiresWithFinalUrlHeadersAndSerializedBody()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        Uri? seenUri = null;
        byte[]? seenBody = null;

        await client.PostAsync("items", new { name = "张三" }, c =>
        {
            c.Params = new Dictionary<string, object?> { ["page"] = 2 };
            c.Callbacks.OnSendingRequest = async (request, _, token) =>
            {
                seenUri = request.RequestUri;
                seenBody = await request.Content!.ReadAsByteArrayAsync(token);

                // 用「实际要发出的字节」签一份请求头，服务端据此校验。
                request.Headers.TryAddWithoutValidation("X-Signature", Sha256(seenBody));
            };
        });

        var received = Assert.Single(server.Received);

        // URL 已合并 BaseAddress 与查询参数，正文已是序列化后的 JSON，不是原始对象。
        Assert.Equal("/items?page=2", received.RawUrl);
        Assert.NotNull(seenUri);
        Assert.EndsWith("/items?page=2", seenUri!.PathAndQuery);

        Assert.NotNull(seenBody);
        Assert.Equal(seenBody, received.Body);
        Assert.Contains("\"name\":", Encoding.UTF8.GetString(received.Body));

        // 钩子写的头随本次发送一起到达服务端。
        Assert.Equal(Sha256(received.Body), received.Headers["X-Signature"]);
    }

    [Fact]
    public async Task FiresOncePerAttempt_AndRewritesDoNotLeakIntoTheNextAttempt()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) == 1
            ? TestJson.Response(new { error = "boom" }, statusCode: 500)
            : TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Retry = { Policy = new RetryPolicy(1, TimeSpan.Zero) }
        });

        var attempt = 0;
        var response = await client.GetAsync("retry", c => c.Callbacks.OnSendingRequest = (request, _, _) =>
        {
            var number = Interlocked.Increment(ref attempt);
            request.Headers.TryAddWithoutValidation("X-Attempt", number.ToString());
            request.Headers.TryAddWithoutValidation("X-Nonce", Guid.NewGuid().ToString("N"));
            return default;
        });

        Assert.Equal(200, response.Status);

        var requests = server.Received;
        Assert.Equal(2, requests.Count);

        // 每次尝试各一次，且改写落在各自的副本上：第二次尝试只有一个值，没有把第一次签名叠加进来。
        Assert.Equal("1", requests[0].Headers["X-Attempt"]);
        Assert.Equal("2", requests[1].Headers["X-Attempt"]);
        Assert.NotEqual(requests[0].Headers["X-Nonce"], requests[1].Headers["X-Nonce"]);
    }

    [Fact]
    public async Task CanReplaceTheBody_AndEveryAttemptStartsFromTheOriginal()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) == 1
            ? TestJson.Response(new { error = "boom" }, statusCode: 500)
            : TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Retry = { Policy = new RetryPolicy(1, TimeSpan.Zero) { RetryNonIdempotentMethods = true } }
        });

        await client.PostAsync("retry", new { original = true }, c =>
            c.Callbacks.OnSendingRequest = (request, _, _) =>
            {
                request.Content = new StringContent("replaced", Encoding.UTF8, "text/plain");
                return default;
            });

        // 每次尝试都从模板的原始正文出发，钩子各自替换：两次发出去的都是同一份替换后的正文。
        var requests = server.Received;
        Assert.Equal(2, requests.Count);
        foreach (var request in requests)
        {
            Assert.Equal("replaced", Encoding.UTF8.GetString(request.Body));
        }
    }

    [Fact]
    public async Task CallbackThrows_ReportsBadRequest_AndNothingIsSent()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("x", c =>
            c.Callbacks.OnSendingRequest = (_, _, _) => throw new InvalidOperationException("missing secret")));

        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);
        Assert.IsType<InvalidOperationException>(ex.Cause);
        Assert.Contains("OnSendingRequest", ex.Message);

        // 钩子在取连接之前执行，失败时请求没有发出。
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task CallbackCancels_ReportsCanceled()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        using var cts = new CancellationTokenSource();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("x", c =>
        {
            c.CancellationToken = cts.Token;
            c.Callbacks.OnSendingRequest = (_, _, token) =>
            {
                cts.Cancel();
                token.ThrowIfCancellationRequested();
                return default;
            };
        }));

        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task PerRequestHook_ReplacesInstanceDefault()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        var defaultCalls = 0;
        var perCallCalls = 0;

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Callbacks = new AxiosConfig.CallbackSettings
            {
                OnSendingRequest = (_, _, _) =>
                {
                    defaultCalls++;
                    return default;
                }
            }
        });

        await client.GetAsync("a");
        Assert.Equal(1, defaultCalls);

        await client.GetAsync("b", c => c.Callbacks.OnSendingRequest = (_, _, _) =>
        {
            perCallCalls++;
            return default;
        });

        Assert.Equal(1, defaultCalls);
        Assert.Equal(1, perCallCalls);
    }

    [Fact]
    public async Task SeesAuthHeaderInjectedFromConnectionOptions()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Connection = { Auth = new AxiosAuth("alice", "secret") }
        });

        string? seen = null;
        await client.GetAsync("x", c => c.Callbacks.OnSendingRequest = (request, _, _) =>
        {
            seen = request.Headers.Authorization?.ToString();
            return default;
        });

        // 钩子在连接选项（Auth）已落成请求头之后才跑：能看见，也能拿去签名。
        Assert.NotNull(seen);
        Assert.StartsWith("Basic ", seen);
        Assert.Equal(seen, server.Received[0].Headers["Authorization"]);
    }

    [Fact]
    public async Task BodyReplacement_StillObeysMaxBodyLength()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        // 模板正文 {"a":1} 只有 7 字节，10 的上限能放行；钩子换成 100 字节后必须重新校验。
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync("x", new { a = 1 }, c =>
        {
            c.Limits.MaxBodyLength = 10;
            c.Callbacks.OnSendingRequest = (request, _, _) =>
            {
                request.Content = new ByteArrayContent(new byte[100]);
                return default;
            };
        }));

        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);
        Assert.Empty(server.Received);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
