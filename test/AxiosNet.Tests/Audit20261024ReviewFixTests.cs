using System.Net;
using System.Text;
using System.Text.Json;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;

namespace AxiosNet.Tests;

/// <summary>
/// 2026-10-24 全库审查的修复回归（按审查报告编号）：
/// <list type="bullet">
/// <item>回放 response.Config 不再把来源实例的取消令牌搬到目标实例（三元赋值置标记陷阱）；</item>
/// <item>Enum 未定义数值 / char 多字符不再静默通过（ScalarParser 复核）；</item>
/// <item>SSE 帧上限按字节判定（与 NDJSON 口径一致）；</item>
/// <item>请求拦截器替换 config 后，泛型 &lt;object&gt;/&lt;Stream&gt; 的 responseType 自动切换仍然生效；</item>
/// <item>transform 本次追加的重复注册不再被「继承段去重」误吞；</item>
/// <item>第三方序列化器自报的 MediaType 含控制字符落 ERR_BAD_OPTION_VALUE（不再是 ERR_BAD_REQUEST）；</item>
/// <item>错误体 byte[] 分支按 Content-Type charset 解码；</item>
/// <item>TimeOnly 保留秒、DateTime(Unspecified) 按 UTC 解释（跨机器确定）；</item>
/// <item>ValidateModels(Assembly) 跳过抽象 / 开放泛型请求基类。</item>
/// </list>
/// <para>
/// 审查中另有两项初判「缺陷」经既有测试核对后确认是确立中的契约，未改动、由既有测试把守：
/// 回放不携带按请求 Auth（Audit20261024P2FixTests）、回放不泄漏 Stream responseType
/// （Audit20261023B1B8FixTests.B7）。
/// </para>
/// </summary>
public sealed class Audit20261024ReviewFixTests
{
    // ------------------------------------------------------------------
    // 回放语义：CancellationToken 与 Auth
    // ------------------------------------------------------------------

    [Fact]
    public async Task Replay_ResponseConfig_DoesNotCarrySourceCancellationToken()
    {
        var handler = new RecordingHandler(_ => JsonResponse());
        using var clientA = new AxiosClient(handler: handler);
        using var cts = new CancellationTokenSource();

        var response = await clientA.GetAsync("http://localhost/a", c => c.CancellationToken = cts.Token);
        Assert.Equal(cts.Token, response.Config.CancellationToken);

        // 回放到同一实例与另一个实例：令牌描述的是「来源实例那一次调用」，不搬过去
        // （观测点是回放请求自己的生效配置，不是 handler 收到的内部 CTS 令牌）。
        var replaySame = await clientA.GetAsync("http://localhost/a", response.Config);
        Assert.False(replaySame.Config.CancellationToken.CanBeCanceled, "回放到同实例后仍带着来源令牌。");

        var handlerB = new RecordingHandler(_ => JsonResponse());
        using var clientB = new AxiosClient(handler: handlerB);
        var replayOther = await clientB.GetAsync("http://localhost/b", response.Config);
        Assert.False(replayOther.Config.CancellationToken.CanBeCanceled, "回放到另一实例时把来源令牌搬了过去。");
    }

    // 回放不携带按请求 Auth 的契约由 Audit20261024P2FixTests 锁定（含「用目标实例默认」），
    // 与本轮修复前行为一致，这里不重复。

    // ------------------------------------------------------------------
    // ScalarParser：Enum 未定义值 / char 多字符
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_Enum_UndefinedNumericValue_FailsBadResponse()
    {
        // 内置 JSON 对「裸数字 → enum」不校验 IsDefined（STJ 既定行为），单靠它测不出分层：
        // 注入一个反序列化即抛的序列化器，让「ScalarParser 拒未定义值」变成可观测的差异 ——
        // 修复前 ScalarParser 放行 (Perm)999 直接返回，修复后拒断并落到 ERR_BAD_RESPONSE。
        await using var server = TestHttpServer.Start(_ => BareText("999"));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<Perm>(
            $"http://127.0.0.1:{server.Port}/",
            c => c.Serialization.ContentSerializer = new ThrowingSerializer()));
        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
    }

    [Fact]
    public async Task GetAsync_Enum_DefinedAndFlagsCombination_AreAccepted()
    {
        await using var server = TestHttpServer.Start(_ => BareText("Read,Write"));
        using var client = new AxiosClient();

        var response = await client.GetAsync<Perm>($"http://127.0.0.1:{server.Port}/");
        Assert.Equal(Perm.Read | Perm.Write, response.Data);
    }

    [Fact]
    public async Task GetAsync_Char_MultiCharText_FailsBadResponse()
    {
        await using var server = TestHttpServer.Start(_ => BareText("abc"));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<char>($"http://127.0.0.1:{server.Port}/"));
        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);

        // 单字符仍然正常（不因收紧而误拒）
        await using var single = TestHttpServer.Start(_ => BareText("x"));
        using var client2 = new AxiosClient();
        var ok = await client2.GetAsync<char>($"http://127.0.0.1:{single.Port}/");
        Assert.Equal('x', ok.Data);
    }

    // ------------------------------------------------------------------
    // SSE 帧上限：字节口径
    // ------------------------------------------------------------------

    [Fact]
    public async Task RequestStreamAsync_SseFrameOverByteLimit_ThrowsFrameTooLarge()
    {
        // 3,000,000 个汉字 = 9,000,000 UTF-8 字节（> 8MB 上限），StringBuilder 侧只有 6,000,000 字符。
        // 按字符判上限的旧实现要 ~13.3M 字符才触发，按字节则这里必然触发。
        var payload = new string('中', 3_000_000);
        var body = Encoding.UTF8.GetBytes("data: " + payload + "\n\n");
        await using var server = TestHttpServer.Start(
            _ => new TestResponse(Body: body, ContentType: "text/event-stream"));
        using var client = new ApiClient(server.Url);

        var response = await client.RequestStreamAsync(
            new SseBigFrameRequest(),
            AxiosStreamFormat.ServerSentEvents);

        var ex = await Assert.ThrowsAsync<AxiosException>(async () =>
        {
            await foreach (var _ in response.Data!)
            {
            }
        });
        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
    }

    // ------------------------------------------------------------------
    // 自动切换后移 + transform 去重语义
    // ------------------------------------------------------------------

    [Fact]
    public async Task Interceptor_ReplacingConfig_KeepsAutoStreamSwitch()
    {
        var handler = new RecordingHandler(_ => StreamResponse());
        using var client = new AxiosClient(handler: handler);

        // 拦截器整体替换 config：新 config 的 responseType 是默认 Json 且未显式设置过，
        // 泛型 <Stream> 的自动切换必须仍然生效（切换点已在拦截器之后）。
        client.Interceptors.Request.Use(_ => Task.FromResult(new AxiosConfig { Url = "http://localhost/stream" }));

        var response = await client.GetAsync<Stream>("http://localhost/other");
        Assert.IsAssignableFrom<Stream>(response.Data);
    }

    [Fact]
    public async Task Transform_DuplicateRegistrationInSameConfig_IsNotSwallowed()
    {
        var handler = new RecordingHandler(_ => JsonResponse());
        using var client = new AxiosClient(handler: handler);
        var calls = 0;
        Func<object?, AxiosConfig, object?> log = (data, _) =>
        {
            calls++;
            return data;
        };

        client.Defaults.TransformRequest.Add(log);

        // 调用方 new 的 config（非库产出）：两次追加与默认链同引用的委托是「有意重复」，
        // 合并后应执行 3 次（默认 1 + 本次 2）—— 去重只覆盖 perCall 登记的继承段。
        var config = new AxiosConfig { Data = new { x = 1 } };
        config.TransformRequest.Add(log);
        config.TransformRequest.Add(log);
        await client.PostAsync("http://localhost/a", config);

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Transform_Replay_DoesNotDuplicateDefaultChain()
    {
        var handler = new RecordingHandler(_ => JsonResponse());
        using var client = new AxiosClient(handler: handler);
        var calls = 0;
        Func<object?, AxiosConfig, object?> log = (data, _) =>
        {
            calls++;
            return data;
        };

        client.Defaults.TransformRequest.Add(log);

        var response = await client.GetAsync("http://localhost/a");
        Assert.Equal(1, calls);

        // 回放生效配置：默认链不重复执行（继承段去重）
        await client.GetAsync("http://localhost/a", response.Config);
        Assert.Equal(2, calls);
    }

    // ------------------------------------------------------------------
    // 第三方 MediaType 的错误分类 + 错误体 charset
    // ------------------------------------------------------------------

    [Fact]
    public async Task ThirdPartySerializer_MediaTypeWithControlChars_FailsBadOptionValue()
    {
        var handler = new RecordingHandler(_ => JsonResponse());
        using var client = new AxiosClient(handler: handler);

        // 第三方序列化器自报的 MediaType 是不可信输入：CR/LF 属于「声明写错了」，
        // 落 ERR_BAD_OPTION_VALUE，不能被收成 ERR_BAD_REQUEST「请求体序列化失败」。
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(
            "http://localhost/a",
            new { x = 1 },
            c => c.Serialization.ContentSerializer = new HostileMediaTypeSerializer()));
        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    [Fact]
    public async Task ErrorContext_Body_DecodesByteArrayWithDeclaredCharset()
    {
        // 0xE9 在 iso-8859-1 下是 é；按 UTF-8 解则是替换字符（修复前的行为）。
        var body = new byte[] { (byte)'{', 0xE9, (byte)'}' };
        await using var server = TestHttpServer.Start(
            _ => new TestResponse(StatusCode: 500, Body: body, ContentType: "application/json; charset=iso-8859-1"));
        var factory = new CapturingErrorFactory();
        using var client = new ApiClient(server.Url) { ErrorFactory = factory };

        await Assert.ThrowsAsync<AxiosException>(() => client.RequestAsync(new ByteBodyErrorRequest()));

        Assert.NotNull(factory.Captured);
        Assert.Equal("{é}", factory.Captured!.Body);
    }

    // ------------------------------------------------------------------
    // 标量文本形态：TimeOnly 秒 / DateTime(Unspecified) UTC
    // ------------------------------------------------------------------

    [Fact]
    public async Task Query_TimeOnly_KeepsSeconds_AndUnspecifiedDateTimeIsUtc()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new AxiosClient();

        // TimeOnly 走往返格式（保留秒）：与请求体路径的 "O" 一致，不再截成 "16:30"。
        // 注意走 configure 写 Params：GetAsync(url, data) 重载会把匿名对象当请求体。
        await client.GetAsync(server.Url + "q", c => c.Params = new { t = new TimeOnly(16, 30, 15) });
        Assert.Contains("16%3A30%3A15", server.Received[0].Query);

        // DateTime(Unspecified) 按 UTC 解释：同一份数据在任何时区的机器上发出同样的字节
        //（按本机时区折的旧实现在东八区会发出 04:00:00.000Z）。
        await client.GetAsync(server.Url + "q", c => c.Params = new { d = new DateTime(2026, 10, 24, 12, 0, 0) });
        Assert.Contains("2026-10-24T12%3A00%3A00.000Z", server.Received[1].Query);
    }

    // ------------------------------------------------------------------
    // ValidateModels：抽象 / 开放泛型基类
    // ------------------------------------------------------------------

    [Fact]
    public void ValidateModels_AssemblyScan_SkipsAbstractAndOpenGenericBases()
    {
        var errors = ApiClient.ValidateModels(typeof(Audit20261024ReviewFixTests).Assembly);

        // 抽象基类（含开放泛型定义）是合法模式：路由特性不继承，派生类各自声明路由。
        Assert.DoesNotContain(errors, error => error.Contains("PagedRequest"));
    }

    [Fact]
    public void ValidateModels_ExplicitAbstractType_StillReports()
    {
        var errors = ApiClient.ValidateModels(typeof(PagedRequest<Person>));

        var error = Assert.Single(errors);
        Assert.Contains("可实例化", error);
    }

    // ------------------------------------------------------------------
    // 测试辅助
    // ------------------------------------------------------------------

    private static HttpResponseMessage JsonResponse()
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(TestJson.Bytes(new Person("alice", 1)))
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") }
            }
        };

    private static HttpResponseMessage StreamResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new StreamContent(new MemoryStream(TestJson.Bytes(new Person("alice", 1))));
        return response;
    }

    private static TestResponse BareText(string text)
        => new(Body: Encoding.UTF8.GetBytes(text), ContentType: "application/json");

    /// <summary>记录最后一个请求的 URI / Authorization / 取消令牌的离线 Handler。</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public Uri? LastUri { get; private set; }

        public string? LastAuthorization { get; private set; }

        public CancellationToken LastToken { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastAuthorization = request.Headers.TryGetValues("Authorization", out var values)
                ? values.FirstOrDefault()
                : null;
            LastToken = cancellationToken;
            return Task.FromResult(_respond(request));
        }
    }

    /// <summary>自报带 CR/LF 的 MediaType 的第三方序列化器（不可信输入样例）。</summary>
    private sealed class HostileMediaTypeSerializer : IContentSerializer
    {
        // MediaType 是默认接口实现：类里重新实现即可（不能用 override）。
        public string? MediaType => "application/json\r\nX-Injected: evil";

        public string Serialize(object? value, Type type, AxiosConfig config) => "{}";

        public object? Deserialize(string text, Type type, AxiosConfig config) => null;
    }

    /// <summary>反序列化即抛的序列化器：用于观测「ScalarParser 已拒断、不会静默放行」的分层差异。</summary>
    private sealed class ThrowingSerializer : IContentSerializer
    {
        public string Serialize(object? value, Type type, AxiosConfig config) => "0";

        public object? Deserialize(string text, Type type, AxiosConfig config)
            => throw new InvalidOperationException("strict");
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

    [Flags]
    private enum Perm
    {
        None = 0,
        Read = 1,
        Write = 2,
        Execute = 4
    }

    /// <summary>抽象请求基类（开放泛型）：程序集扫描必须跳过它。</summary>
    public abstract class PagedRequest<TResponse> : IApiRequest<TResponse>
    {
        public int Page { get; set; }
    }

    [Get("/users/paged")]
    public sealed class PagedUserRequest : PagedRequest<Person>
    {
        public int Size { get; set; }
    }

    [Get("/sse")]
    public sealed class SseBigFrameRequest : IApiRequest<IAsyncEnumerable<string>>
    {
    }

    [Get("/err")]
    public sealed class ByteBodyErrorRequest : IApiRequest<byte[]>
    {
    }
}