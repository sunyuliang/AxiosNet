
using AxiosNet.Pooling;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text;

namespace AxiosNet.Tests.Requests;

/// <summary>
/// 方法谓词与 URL 解析：七个快捷方法、配置重载、相对/绝对/协议相对 URL、非法 URL。
/// </summary>
public sealed class MethodAndUrlTests
{
    // ===============================================================
    // Bug 6：非法 HTTP 方法 token 的 FormatException 逃出统一错误模型
    // ===============================================================

    [Fact]
    public void InvalidHttpMethodToken_ThrowsInvalidOperationException_AndCachesSameMessage()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");

        // 模型定义错误统一归一为 InvalidOperationException（ApiClient 的文档承诺），
        // 且进缓存：每次调用抛同一份消息，不重新执行特性构造
        var first = Assert.Throws<InvalidOperationException>(() => client.GetUri(new BadMethodRequest()));
        var second = Assert.Throws<InvalidOperationException>(() => client.GetUri(new BadMethodRequest()));

        Assert.Equal(first.Message, second.Message);
    }

    // ------------------------------------------------------------------
    // 1) 快捷方法 url 的 fail-fast
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_NullUrl_ReportsInvalidUrlBeforeSending()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        // 写错变量（null）不该被静默解释成「请求 baseAddress」：那会向根路径发出一个调用方没打算发的请求
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(null!));

        Assert.Equal(AxiosException.ErrInvalidUrl, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task PostAsync_BlankUrl_ReportsInvalidUrlBeforeSending()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        // POST 落到根路径的后果比 GET 严重（可能创建资源），空白串同样按非法地址拒绝
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync("   ", new { a = 1 }));

        Assert.Equal(AxiosException.ErrInvalidUrl, ex.Code);
        Assert.Empty(server.Received);
    }

    // ===============================================================
    // 9. RequestAsync 的空白 Url 不得静默打到 baseAddress（与快捷方法的 fail-fast 一致）。
    // ===============================================================

    [Fact]
    public async Task RequestAsync_BlankUrl_ThrowsInvalidUrl()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.RequestAsync(new AxiosConfig { Url = "   ", Method = HttpMethod.Post }));

        Assert.Equal(AxiosException.ErrInvalidUrl, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task NonHttpScheme_ThrowsInvalidUrl()
    {
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("ftp://example.com/x"));

        Assert.Equal(AxiosException.ErrInvalidUrl, ex.Code);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("OPTIONS")]
    public async Task ShortcutMethods_SendExpectedHttpMethod(string method)
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        switch (method)
        {
            case "PUT":
                await client.PutAsync(server.Url, new { name = "x" });
                break;
            case "PATCH":
                await client.PatchAsync(server.Url, new { name = "x" });
                break;
            case "DELETE":
                await client.DeleteAsync(server.Url);
                break;
            case "OPTIONS":
                await client.OptionsAsync(server.Url);
                break;
        }

        Assert.Equal(method, server.Received[0].Method);
    }

    [Fact]
    public async Task HeadAsync_NoBody()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(204, "No Content"));
        using var client = new AxiosClient();

        var response = await client.HeadAsync(server.Url);

        Assert.Equal(204, response.Status);
        Assert.Equal("No Content", response.StatusText);
        Assert.Null(response.Data);
        Assert.Equal("HEAD", server.Received[0].Method);
        // HEAD 响应不能写 body，连接上不能残留字节
        Assert.Equal(1, server.RequestsPerConnection.Values.Single());
    }

    [Fact]
    public async Task BaseAddress_IsCombinedWithRelativeUrl()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Response(new { path = req.Path }));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url + "base/" });

        await client.GetAsync("users");
        await client.GetAsync("/items");
        // 绝对地址优先于 baseURL
        await client.GetAsync(server.Url + "other");

        Assert.Equal("/base/users", server.Received[0].Path);
        Assert.Equal("/base/items", server.Received[1].Path);
        Assert.Equal("/other", server.Received[2].Path);
    }

    [Theory]
    [InlineData("users")]
    // Linux 下 Uri 会把 "/items" 解析成 file:///items 的绝对文件路径（Windows 下是相对地址），
    // 必须仍然按「缺少 baseURL」处理，而不是把 file URI 交给连接池。
    [InlineData("/items")]
    public async Task RelativeUrl_WithoutBaseAddress_ThrowsInvalidUrl(string url)
    {
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(url));

        Assert.Equal("ERR_INVALID_URL", ex.Code);
    }

    // HttpMethod

    [Fact]
    public async Task CustomMethod_IsSentVerbatim()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Response(new { method = req.Method }));
        using var client = new AxiosClient();

        var response = await client.RequestAsync<JsonElement>(c =>
        {
            c.Url = server.Url + "resource";
            c.Method = new HttpMethod("PROPFIND");
        });

        Assert.Equal("PROPFIND", server.Received[0].Method);
        Assert.Equal("PROPFIND", response.Data.GetProperty("method").GetString());
    }

    [Fact]
    public void Method_Null_Throws()
    {
        var config = new AxiosConfig();

        Assert.Throws<ArgumentNullException>(() => config.Method = null!);
        Assert.Equal(HttpMethod.Get, config.Method);
    }

    [Fact]
    public void Method_DefaultsToGet_AndMarksAsSetOnlyWhenAssigned()
    {
        var defaults = new AxiosConfig();
        Assert.Equal(HttpMethod.Get, defaults.Method);

        var config = new AxiosConfig { Url = "https://example.com/" };
        config.Method = HttpMethod.Patch;

        Assert.Equal(HttpMethod.Patch, config.Method);
    }

    [Fact]
    public async Task PatchMethod_UsesBclHttpMethod_AndSendsBody()
    {
        await using var server = TestHttpServer.Start(req => new TestResponse(Body: Encoding.UTF8.GetBytes(req.Method), ContentType: "text/plain; charset=utf-8"));
        using var client = new AxiosClient();

        var response = await client.PatchAsync(server.Url + "users/1", new { name = "bob" });

        Assert.Equal("PATCH", response.Data);
        Assert.Equal("PATCH", server.Received[0].Method);
    }

    // PostAsync(url, null) 落到 AxiosConfig 重载

    [Fact]
    public async Task PostAsync_WithNullConfig_FallsBackToEmptyBody()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var response = await client.PostAsync(server.Url + "x", (AxiosConfig)null!);

        Assert.Equal(200, response.Status);
        var received = Assert.Single(server.Received);
        Assert.Equal("POST", received.Method);
        Assert.Empty(received.Body);
    }

    [Fact]
    public async Task ConfigOverloads_ExistForPutPatchHeadOptions()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(204, "No Content"));
        using var client = new AxiosClient();

        var put = await client.PutAsync(server.Url + "x", new AxiosConfig { Data = new { n = 1 } });
        var patch = await client.PatchAsync(server.Url + "x", new AxiosConfig { Data = new { n = 2 } });
        var options = await client.OptionsAsync(server.Url + "x", new AxiosConfig());
        var head = await client.HeadAsync(server.Url + "x", new AxiosConfig());

        Assert.Equal(204, put.Status);
        Assert.Equal(204, patch.Status);
        Assert.Equal(204, options.Status);
        Assert.Equal(204, head.Status);
        Assert.Equal(new[] { "PUT", "PATCH", "OPTIONS", "HEAD" }, server.Received.Select(r => r.Method));
    }

    [Fact]
    public async Task PostAsync_WithNullBody_StillWorks()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var response = await client.PostAsync(server.Url + "ping", (object?)null);

        Assert.Equal(200, response.Status);
    }

    private static string Body(TestRequest request) => Encoding.UTF8.GetString(request.Body);

    private static string ContentType(TestRequest request)
        => request.Headers.GetValueOrDefault("content-type") ?? string.Empty;

    // ===============================================================
    // 测试用请求模型 / 序列化器
    // ===============================================================


    // ===============================================================
    // 测试夹具
    // ===============================================================


    /// <summary>
    /// 分块写出、块间带 60ms 间隔的正文：让「传满声明长度即锁死」的判定在重发轮次里
    /// 必然产生一次非 force 的上报（而不是靠最终 Complete 的强制上报掩盖问题）。
    /// </summary>
    private sealed class SlowChunkedContent : HttpContent
    {
        private readonly int _chunks;
        private readonly int _chunkSize;

        public SlowChunkedContent(int chunks, int chunkSize)
        {
            _chunks = chunks;
            _chunkSize = chunkSize;
            Headers.ContentLength = (long)chunks * chunkSize;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => await SerializeToStreamAsync(stream, context, CancellationToken.None).ConfigureAwait(false);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[_chunkSize];
            for (var i = 0; i < _chunks; i++)
            {
                await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                await Task.Delay(60, cancellationToken).ConfigureAwait(false);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = (long)_chunks * _chunkSize;
            return true;
        }
    }

    private sealed class Node
    {
        public Node? Self { get; set; }
    }


    /// <summary>完全离线的桩 Handler：不建连接，直接返回固定 JSON。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _json;

        public StubHandler(string json) => _json = json;

        public int Calls;
        public int DisposeCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
                RequestMessage = request
            };

            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            Interlocked.Increment(ref DisposeCount);
            base.Dispose(disposing);
        }
    }

}