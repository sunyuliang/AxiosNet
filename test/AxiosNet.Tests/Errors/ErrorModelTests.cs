
using AxiosNet.Pooling;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net;
using System.Security.Authentication;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text;

namespace AxiosNet.Tests.Errors;

/// <summary>
/// 统一错误模型：稳定 `Code`、状态与响应快照、回调异常归一、配置错误携带 config。
/// </summary>
public sealed class ErrorModelTests
{
    [Fact]
    public async Task ErrorContext_Body_DecodesByteArrayWithDeclaredCharset()
    {
        // 0xE9 在 iso-8859-1 下是 é，不能按 UTF-8 解成替换字符。
        var body = new byte[] { (byte)'{', 0xE9, (byte)'}' };
        await using var server = TestHttpServer.Start(
            _ => new TestResponse(StatusCode: 500, Body: body, ContentType: "application/json; charset=iso-8859-1"));
        var factory = new CapturingErrorFactory();
        using var client = new ApiClient(server.Url) { ErrorFactory = factory };

        await Assert.ThrowsAsync<AxiosException>(() => client.RequestAsync(new ByteBodyErrorRequest()));

        Assert.NotNull(factory.Captured);
        Assert.Equal("{é}", factory.Captured!.Body);
    }

    [Fact]
    public async Task RequestBodySerializationFailure_IsWrappedAsBadRequest()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var node = new Node();
        node.Self = node;

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(server.Url + "x", node));

        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);
        Assert.IsType<JsonException>(ex.Cause);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task TransformRequestFailure_IsWrappedAsBadRequest()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(
            server.Url + "x",
            new { a = 1 },
            c => c.TransformRequest.Add((_, _) => throw new FormatException("bad transform"))));

        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);
        Assert.IsType<FormatException>(ex.Cause);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task ErrorStatus_WithUnparsableBody_KeepsStatusAndResponse()
    {
        const string html = "<html><body>Not Found</body></html>";
        await using var server = TestHttpServer.Start(_ => new TestResponse(404, "Not Found", Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8"));

        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<Person>(server.Url + "missing"));

        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);
        Assert.Equal(404, ex.Status);
        Assert.NotNull(ex.Response);
        Assert.Equal(html, ex.Response!.Data);
    }

    [Fact]
    public async Task OkStatus_WithUnparsableBody_StillThrowsBadResponse()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(200, "OK", Encoding.UTF8.GetBytes("<html>not json</html>"), "text/html; charset=utf-8"));

        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<Person>(server.Url + "x"));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
    }

    [Fact]
    public async Task InvalidJson_ForComplexType_ThrowsAxiosException()
    {
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(Body: Encoding.UTF8.GetBytes("not-json"), ContentType: "text/plain; charset=utf-8"));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<Person>(server.Url));

        Assert.Equal("ERR_BAD_RESPONSE", ex.Code);
    }

    // ------------------------------------------------------------------
    // validateStatus 只判定一次
    // ------------------------------------------------------------------

    [Fact]
    public async Task ValidateStatus_IsInvokedExactlyOncePerRequest()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var calls = 0;
        await client.GetAsync(server.Url, c => c.Callbacks.ValidateStatus = _ =>
        {
            calls++;
            return true;
        });

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ValidateStatus_IsInvokedOnce_ForErrorStatusToo()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = true }, statusCode: 500));
        using var client = new AxiosClient();

        var calls = 0;
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url, c =>
            c.Callbacks.ValidateStatus = _ =>
            {
                calls++;
                return false;
            }));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
        Assert.Equal(1, calls);
    }

    // ------------------------------------------------------------------
    // 池级错误也带 Config
    // ------------------------------------------------------------------

    [Fact]
    public async Task PoolCapacityError_FromRequest_CarriesConfig()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            MaxEndpointCount = 1,
            EvictionInterval = TimeSpan.Zero,
            IdleHandlerLifetime = TimeSpan.Zero
        });

        // 先把唯一的 origin 名额占住（另一个 origin 的租约，不发请求）
        using var busy = pool.Lease(new Uri("http://127.0.0.1:1/warmup"));

        using var client = new AxiosClient(pool: pool);
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url));

        Assert.Equal(AxiosException.ErrPoolExhausted, ex.Code);
        // 请求期从池里抛出的错误同样要指回本次请求的配置，而不是 Config == null
        Assert.NotNull(ex.Config);
        Assert.Empty(server.Received);
    }

    // ------------------------------------------------------------------
    // 超长 URL 的异常消息
    // ------------------------------------------------------------------

    [Fact]
    public void InvalidUrl_WithHugeQuery_DoesNotEmbedWholeUrlInMessage()
    {
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://example.com/" });
        var huge = new string('a', 200_000);

        // .NET 的 Uri 上限是 65519 字符，超长查询串只能报 ERR_INVALID_URL；
        // 但消息里不该塞进整条 URL（否则一条日志几十万字符）。
        var ex = Assert.Throws<AxiosException>(() => client.GetUri(c =>
        {
            c.Url = "p";
            c.Params = new { q = huge };
        }));

        Assert.Equal(AxiosException.ErrInvalidUrl, ex.Code);
        Assert.True(ex.Message.Length < 1024, $"异常消息长度 {ex.Message.Length}");
        Assert.Contains("truncated", ex.Message);
    }

    [Fact]
    public async Task Status404_ThrowsAxiosException_WithResponseAndBadRequestCode()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(404, Body: TestJson.Bytes(new { error = "missing" })));

        using var client = new AxiosClient();
        var config = new AxiosConfig { Url = server.Url + "missing" };

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.RequestAsync(config));

        Assert.True(ex.IsAxiosError);
        Assert.Equal("ERR_BAD_REQUEST", ex.Code);
        Assert.Equal(404, ex.Response!.Status);
        Assert.Equal("Not Found", ex.Response.StatusText);
        Assert.Equal("missing", ex.Response.Data is JsonElement el ? el.GetProperty("error").GetString() : null);
        Assert.NotNull(ex.Request);
        Assert.Equal(config.Url, ex.Config!.Url);
        Assert.Contains("404", ex.Message);
    }

    [Fact]
    public async Task Status500_UsesBadResponseCode()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(500));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url));

        Assert.Equal("ERR_BAD_RESPONSE", ex.Code);
        Assert.Equal(500, ex.Response!.Status);
    }

    [Fact]
    public async Task ValidateStatus_True_AcceptsAnyStatusCode()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(503));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url, c => c.Callbacks.ValidateStatus = _ => true);

        Assert.Equal(503, response.Status);
    }

    [Fact]
    public async Task ValidateStatus_CustomRule_IsRespected()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(404));
        using var client = new AxiosClient();

        // 只接受 404
        var response = await client.GetAsync(server.Url, c => c.Callbacks.ValidateStatus = status => status == 404);
        Assert.Equal(404, response.Status);

        await using var server2 = TestHttpServer.Start(_ => new TestResponse(400));
        await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server2.Url, c => c.Callbacks.ValidateStatus = status => status == 404));
    }

    [Fact]
    public async Task Timeout_ThrowsECONNABORTED_Quickly()
    {
        await using var server = TestHttpServer.Start(async _ =>
        {
            // 20 秒才回包，必须远大于下面的 5 秒预算，否则分不清「超时生效」与「刚好等到回包」。
            await Task.Delay(20_000);
            return TestJson.Response(new { ok = true });
        });

        using var client = new AxiosClient();
        var stopwatch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url, c => c.Timeout = TimeSpan.FromMilliseconds(300)));

        stopwatch.Stop();

        Assert.Equal("ECONNABORTED", ex.Code);
        Assert.Contains("timeout", ex.Message);

        // 5 秒：给调度抖动留足余量，又远小于「自己等完」的 20 秒。
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"实际耗时 {stopwatch.Elapsed}，超时未及时生效");
    }

    [Fact]
    public async Task ExternalCancellation_ThrowsERRCANCELED()
    {
        await using var server = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(2_000);
            return TestJson.Response(new { ok = true });
        });

        using var client = new AxiosClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url, cancellationToken: cts.Token));

        Assert.Equal("ERR_CANCELED", ex.Code);
    }

    [Fact]
    public async Task NetworkError_UsesERRNETWORKCode()
    {
        using var client = new AxiosClient();

        // 未被监听的端口，连接会被拒绝。
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("http://127.0.0.1:1/unreachable", c => c.Timeout = TimeSpan.FromSeconds(5)));

        Assert.Equal("ERR_NETWORK", ex.Code);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task InstanceDefaultToken_IsRespected_WhenNoPerRequestTokenIsGiven()
    {
        await using var server = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(2_000);
            return TestJson.Response(new { ok = true });
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            CancellationToken = cts.Token
        });

        // 不按请求传 token 时应沿用实例默认 token。
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("slow"));

        Assert.Equal("ERR_CANCELED", ex.Code);
    }

    [Fact]
    public async Task PerRequestToken_TakesPrecedenceOverInstanceDefault()
    {
        await using var server = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(2_000);
            return TestJson.Response(new { ok = true });
        });

        using var defaultCts = new CancellationTokenSource(); // 永不取消
        using var perRequestCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            CancellationToken = defaultCts.Token
        });

        // 实例默认 token 永不取消，这里应被按请求的 token 取消。
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("slow", cancellationToken: perRequestCts.Token));

        Assert.Equal("ERR_CANCELED", ex.Code);
    }

    [Fact]
    public async Task TimeoutMessage_OverridesDefaultText()
    {
        await using var server = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(600);
            return TestJson.Response(new { ok = true });
        });
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(
            server.Url + "slow",
            c =>
            {
                c.Timeout = TimeSpan.FromMilliseconds(100);
                c.Callbacks.TimeoutMessage = "上游响应超时，请稍后重试";
            }));

        Assert.Equal(AxiosException.ErrConnAborted, ex.Code);
        Assert.Equal("上游响应超时，请稍后重试", ex.Message);
    }

    [Fact]
    public async Task Timeout_WithoutMessage_UsesDefaultText()
    {
        await using var server = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(600);
            return TestJson.Response(new { ok = true });
        });
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "slow", c => c.Timeout = TimeSpan.FromMilliseconds(100)));

        Assert.Equal(AxiosException.ErrConnAborted, ex.Code);
        Assert.False(ex.IsTotalTimeoutExceeded);   // 发出之后才超时：不算「预算耗尽没发出」
        Assert.Contains("timeout of 100ms exceeded", ex.Message);
    }

    [Fact]
    public async Task AxiosException_ExposesStatusAndSerializableSnapshot()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { message = "nope" }, statusCode: 404));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "missing"));

        Assert.Equal(404, ex.Status);
        Assert.True(ex.IsAxiosError);
        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);

        var json = ex.ToJson();
        Assert.Equal("AxiosError", json["name"]);
        Assert.Equal("ERR_BAD_REQUEST", json["code"]);
        Assert.Equal(404, json["status"]);
        Assert.Equal("GET", json["method"]);
        Assert.EndsWith("/missing", (string)json["url"]!);
        Assert.Equal(true, json["isAxiosError"]);
    }

    [Fact]
    public void ErrorCodeConstants_MatchAxiosStrings()
    {
        Assert.Equal("ERR_BAD_REQUEST", AxiosException.ErrBadRequest);
        Assert.Equal("ERR_BAD_RESPONSE", AxiosException.ErrBadResponse);
        Assert.Equal("ECONNABORTED", AxiosException.ErrConnAborted);
        Assert.Equal("ERR_NETWORK", AxiosException.ErrNetwork);
        Assert.Equal("ERR_CANCELED", AxiosException.ErrCanceled);
        Assert.Equal("ERR_INVALID_URL", AxiosException.ErrInvalidUrl);
        Assert.Equal("ERR_BAD_OPTION_VALUE", AxiosException.ErrBadOptionValue);

        // 库里自定义的错误码（axios 没有对应项，命名沿用同一风格）
        Assert.Equal("ERR_QUEUE_TIMEOUT", AxiosException.ErrQueueTimeout);
        Assert.Equal("ERR_QUEUE_FULL", AxiosException.ErrQueueFull);
    }

    [Fact]
    public async Task StaticHelpers_DetectAxiosErrorAndCancellation()
    {
        using var client = new AxiosClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("http://127.0.0.1:1/never", cancellationToken: cts.Token));

        Assert.True(Axios.IsAxiosError(ex));
        Assert.True(Axios.IsCancel(ex));
        Assert.Equal(AxiosException.ErrCanceled, ex.Code);

        Assert.False(Axios.IsAxiosError(new InvalidOperationException()));
        Assert.False(Axios.IsCancel(new InvalidOperationException()));
        Assert.True(Axios.IsCancel(new OperationCanceledException()));
    }

    [Fact]
    public async Task GenericStreamRequest_WithTextResponseType_IsWrappedAsBadResponse()
    {
        // 泛型参数与显式 responseType 不匹配时，走统一错误模型而不是漏出 InvalidCastException。
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<Stream>(server.Url + "x", c => c.ResponseType = ResponseType.Text));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
        Assert.Contains("Stream", ex.Message);
    }

    [Fact]
    public async Task GenericStreamRequest_WithArrayBufferResponseType_IsWrappedAsBadResponse()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<Stream>(server.Url + "x", c => c.ResponseType = ResponseType.ArrayBuffer));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
        Assert.Contains("Stream", ex.Message);
    }

    [Fact]
    public async Task GenericStreamRequest_WithoutExplicitResponseType_StillWorks()
    {
        // 未显式配置 responseType 时，<Stream> 自动切流式。
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await using var stream = (await client.GetAsync<Stream>(server.Url + "x")).Data!;

        using var reader = new StreamReader(stream);
        Assert.Equal("{\"ok\":true}", await reader.ReadToEndAsync());
    }

    // ------------------------------------------------------------------
    // L11：ERR_INVALID_URL / ERR_BAD_OPTION_VALUE 携带 Config
    // ------------------------------------------------------------------

    [Fact]
    public async Task InvalidUrl_ErrorCarriesConfig()
    {
        using var client = new AxiosClient();
        var config = new AxiosConfig { Url = "/relative-without-base" };

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.RequestAsync(config));

        Assert.Equal(AxiosException.ErrInvalidUrl, ex.Code);
        // 挂上的是合并后的生效配置（与 axios error.config 一致）
        Assert.NotNull(ex.Config);
        Assert.Equal("/relative-without-base", ex.Config!.Url);
    }

    [Fact]
    public async Task InvalidOption_ErrorCarriesConfig()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url + "x", c => c.Timeout = TimeSpan.FromSeconds(-1)));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.NotNull(ex.Config);
    }

    [Fact]
    public void PoolOptionError_FromClientConstructor_CarriesDefaults()
    {
        var defaults = new AxiosConfig
        {
            Connection = new()
            {
                PoolOptions = new ConnectionPoolOptions { MaxConnectionsPerServer = 0 },
            },
        };

        var ex = Assert.Throws<AxiosException>(() => new AxiosClient(defaults));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Same(defaults, ex.Config);
    }

    // ------------------------------------------------------------------
    // 测试辅助
    // ------------------------------------------------------------------


    /// <summary>固定应答的离线 Handler（回放语义用例只需要一条稳定的响应）。</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_respond(request));
    }


    /// <summary>捕获错误上下文后放行（返回 null = 仍抛原始 AxiosException）。</summary>
    private sealed class CapturingErrorFactory : IApiErrorFactory
    {
        public ApiErrorContext? Captured { get; private set; }

        public ValueTask<Exception?> CreateAsync(ApiErrorContext context, CancellationToken cancellationToken)
        {
            Captured = context;
            return new ValueTask<Exception?>((Exception?)null);
        }
    }


    [Get("/err")]
    public sealed class ByteBodyErrorRequest : IApiRequest<byte[]>
    {
    }

    private sealed class Node
    {
        public Node? Self { get; set; }
    }

    /// <summary>把请求挡在 <see cref="TaskCompletionSource"/> 之后的测试 Handler。</summary>
    private sealed class GatedHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _gate;

        public GatedHandler(TaskCompletionSource gate) => _gate = gate;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await _gate.Task.ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json"),
                RequestMessage = request
            };
        }
    }
}