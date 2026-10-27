using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;

namespace AxiosNet.Tests;

/// <summary>
/// 2026-10-24 全库审查的修复回归（按审查报告编号）：
/// <list type="bullet">
/// <item>回放 response.Config 不再把来源实例的取消令牌搬到目标实例（三元赋值置标记陷阱）；</item>
/// <item>Enum 未定义数值 / char 多字符不再静默通过（ScalarParser 第 8 步复核 + 第 9 步序列化器产物复核，内置 JSON 的 STJ 路径同样拦截；错误状态码下 lenient 回退原文）；</item>
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
        var config = response.Config!;
        Assert.Equal(cts.Token, config.CancellationToken);

        // 回放到同一实例与另一个实例：令牌描述的是「来源实例那一次调用」，不搬过去
        // （观测点是回放请求自己的生效配置，不是 handler 收到的内部 CTS 令牌）。
        var replaySame = await clientA.GetAsync("http://localhost/a", config);
        Assert.False(replaySame.Config!.CancellationToken.CanBeCanceled, "回放到同实例后仍带着来源令牌。");

        var handlerB = new RecordingHandler(_ => JsonResponse());
        using var clientB = new AxiosClient(handler: handlerB);
        var replayOther = await clientB.GetAsync("http://localhost/b", config);
        Assert.False(replayOther.Config!.CancellationToken.CanBeCanceled, "回放到另一实例时把来源令牌搬了过去。");
    }

    // 回放不携带按请求 Auth 的契约由 Audit20261024P2FixTests 锁定（含「用目标实例默认」），

    // ------------------------------------------------------------------
    // ScalarParser：Enum 未定义值 / char 多字符
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_Enum_UndefinedNumericValue_FailsBadResponse()
    {
        // 内置 JSON 对「裸数字 → enum」不校验 IsDefined（STJ 既定行为），单靠它测不出分层：
        // 注入一个反序列化即抛的序列化器，让「ScalarParser 拒未定义值」变成可观测的差异 ——
        // (Perm)999 不是合法枚举值：应拒断并落到 ERR_BAD_RESPONSE。
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

    [Fact]
    public async Task GetAsync_Enum_UndefinedNumericValue_WithDefaultSerializer_FailsBadResponse()
    {
        // 同一条 application/json + "999" 的响应但不注入序列化器：STJ 会把 999 转成 (E)999，
        // 产物必须按同一标准复核。
        await using var server = TestHttpServer.Start(_ => BareText("999"));
        using var client = new AxiosClient();

        var fromPlain = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<Level>(
            $"http://127.0.0.1:{server.Port}/"));
        Assert.Equal(AxiosException.ErrBadResponse, fromPlain.Code);
        Assert.Contains("undefined", fromPlain.Message);

        var fromFlags = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<Perm>(
            $"http://127.0.0.1:{server.Port}/"));
        Assert.Equal(AxiosException.ErrBadResponse, fromFlags.Code);

        // 第三方序列化器直接产出 (Level)999（不依赖 STJ 的既定行为）：复核同样拒
        var fromThirdParty = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<Level>(
            $"http://127.0.0.1:{server.Port}/",
            c => c.Serialization.ContentSerializer = new FixedUndefinedEnumSerializer()));
        Assert.Equal(AxiosException.ErrBadResponse, fromThirdParty.Code);
    }

    [Fact]
    public async Task GetAsync_Enum_DefinedValues_WithDefaultSerializer_AreAccepted()
    {
        // 复核不能误拒合法值：已定义数字（第 8 步直解析）、第三方序列化器产出的已定义枚举
        // （复核就发生在这一层）、flags 组合数字都要照常通过。
        await using var number = TestHttpServer.Start(_ => BareText("1"));
        await using var name = TestHttpServer.Start(_ => BareText("\"Low\""));
        await using var flags = TestHttpServer.Start(_ => BareText("3"));

        using var numberClient = new AxiosClient();
        Assert.Equal(Level.Low, (await numberClient.GetAsync<Level>($"http://127.0.0.1:{number.Port}/")).Data);

        using var nameClient = new AxiosClient();
        Assert.Equal(Level.Low, (await nameClient.GetAsync<Level>(
            $"http://127.0.0.1:{name.Port}/",
            c => c.Serialization.ContentSerializer = new StringEnumSerializer())).Data);

        using var flagsClient = new AxiosClient();
        Assert.Equal(Perm.Read | Perm.Write, (await flagsClient.GetAsync<Perm>($"http://127.0.0.1:{flags.Port}/")).Data);
    }

    [Fact]
    public async Task GetAsync_EnumBaseType_WithSerializerReturningEnum_IsNotFalseRejected()
    {
        // 目标类型本身不是枚举（Enum 基类）时，序列化器产出具体枚举是合法的：
        // 复核只对「目标类型是枚举」生效 —— Enum.IsDefined 对非枚举入参抛 ArgumentException，
        // 若不设防会被外层 catch 收成错误的 ERR_BAD_RESPONSE（2026-10-25 自审发现）。
        var handler = new RecordingHandler(_ => JsonResponse());
        using var client = new AxiosClient(handler: handler);

        var response = await client.GetAsync<Enum>(
            "http://localhost/a",
            c => c.Serialization.ContentSerializer = new FixedDefinedEnumSerializer());

        Assert.Equal(Level.Low, response.Data);
    }

    [Fact]
    public async Task GetAsync_Enum_UndefinedValue_OnErrorStatus_KeepsStatusError()
    {
        // 状态码不合法时 lenient：正文问题不得盖住 HTTP 状态错误（与解析失败同口径回退原文）。
        await using var server = TestHttpServer.Start(
            _ => new TestResponse(StatusCode: 500, Body: Encoding.UTF8.GetBytes("999"), ContentType: "application/json"));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<Level>($"http://127.0.0.1:{server.Port}/"));
        Assert.Contains("status code 500", ex.Message);
    }

    // ------------------------------------------------------------------
    // SSE 帧上限：字节口径
    // ------------------------------------------------------------------

    [Fact]
    public async Task RequestStreamAsync_SseFrameOverByteLimit_ThrowsFrameTooLarge()
    {
        // 3,000,000 个汉字 = 9,000,000 UTF-8 字节（> 8MB 上限），StringBuilder 侧只有 6,000,000 字符。
        // 上限按字节判：这里必然触发。
        var payload = new string('中', 3_000_000);
        var body = Encoding.UTF8.GetBytes("data: " + payload + "\n\n");
        await using var server = TestHttpServer.Start(
            _ => new TestResponse(Body: body, ContentType: "text/event-stream"));
        using var client = new ApiClient(server.Url);

        var response = await client.RequestStreamAsync(new SseBigFrameRequest(), AxiosStreamFormat.ServerSentEvents);

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
        await client.GetAsync("http://localhost/a", response.Config!);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Transform_ReplayOfObjectEntryResponse_DoesNotDuplicateDefaultChain()
    {
        // 对象入口（调用方自建配置、自身不带任何 transform）走 MergeConfig：
        // 产物的继承段必须仍记为「实例默认段」，否则回放时默认 transform 会再执行一遍。
        // 只在 perCall.HasTransform* 分支里写产物标记时，这条路径的标记恒为 0（回归点）。
        var handler = new RecordingHandler(_ => JsonResponse());
        using var client = new AxiosClient(handler: handler);
        var calls = 0;
        Func<object?, AxiosConfig, object?> log = (data, _) =>
        {
            calls++;
            return data;
        };

        client.Defaults.TransformRequest.Add(log);

        var response = await client.RequestAsync(new AxiosConfig
        {
            Url = "http://localhost/a",
            Data = new { x = 1 }
        });
        Assert.Equal(1, calls);

        await client.RequestAsync(response.Config!);
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

    // ------------------------------------------------------------------
    // 标量文本形态：TimeOnly 秒 / DateTime(Unspecified) UTC
    // ------------------------------------------------------------------

    [Fact]
    public async Task Query_TimeOnly_KeepsSeconds_AndUnspecifiedDateTimeIsUtc()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new AxiosClient();

        // TimeOnly 走往返格式（与请求体路径同一份 "O"）：秒与小数位都保留。
        // 注意走 configure 写 Params：GetAsync(url, data) 重载会把匿名对象当请求体。
        await client.GetAsync(server.Url + "q", c => c.Params = new { t = new TimeOnly(16, 30, 15) });
        Assert.Contains("16%3A30%3A15.0000000", server.Received[0].Query);

        // DateTime(Unspecified) 按 UTC 解释：同一份数据在任何时区的机器上发出同样的字节
        //（按本机时区折算会在东八区发出 04:00:00.000Z）。
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

    /// <summary>带字符串枚举转换器的第三方序列化器：把 <c>"Low"</c> 解成 <see cref="Level.Low"/>
    /// —— 产物复核不得误拒序列化器合法产出的已定义枚举值。</summary>
    private sealed class StringEnumSerializer : IContentSerializer
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            Converters = { new JsonStringEnumConverter() }
        };

        public string Serialize(object? value, Type type, AxiosConfig config)
            => JsonSerializer.Serialize(value, type, Options);

        public object? Deserialize(string text, Type type, AxiosConfig config)
            => JsonSerializer.Deserialize(text, type, Options);
    }

    /// <summary>无视正文、固定产出未定义枚举值的第三方序列化器：复核必须拒（不依赖 STJ 的既定行为）。</summary>
    private sealed class FixedUndefinedEnumSerializer : IContentSerializer
    {
        public string Serialize(object? value, Type type, AxiosConfig config) => "0";

        public object? Deserialize(string text, Type type, AxiosConfig config) => (Level)999;
    }

    /// <summary>固定产出已定义枚举值的第三方序列化器：目标类型不是枚举时复核必须放行。</summary>
    private sealed class FixedDefinedEnumSerializer : IContentSerializer
    {
        public string Serialize(object? value, Type type, AxiosConfig config) => "0";

        public object? Deserialize(string text, Type type, AxiosConfig config) => Level.Low;
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

    /// <summary>非 flags 枚举（未定义值复核的两条路径都要覆盖）。</summary>
    private enum Level
    {
        Low = 1,
        High = 2
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
