using System.Text;
using System.Text.Json;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.WebAPI.Tests;

/// <summary>
/// 边界与特殊场景：大小写匹配变体、默认 Body 的空值/数组语义、原始响应形态、
/// Guid/record 模型、非法头值 fail-fast、连接复用与门面参数守卫。
/// </summary>
public sealed class ApiClientEdgeCaseTests
{
    private static ApiClient CreateClient(TestHttpServer server) => new(server.Url);

    // ---- 路径绑定 ----

    [Fact]
    public async Task Get_PathTokenMatchesBindingCaseInsensitively()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient(server);

        await client.RequestAsync(new CaseInsensitivePathRequest { Id = 7 });

        var received = Assert.Single(server.Received);
        // [Path("UID")] ↔ 模板 {uid}：大小写不敏感匹配，替换用模板原文
        Assert.Equal("/users/7", received.Path);
    }

    [Fact]
    public void Path_DuplicatePlaceholderDifferingOnlyByCase_IsRejected()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");

        var ex = Assert.Throws<InvalidOperationException>(
            () => client.GetUri(new DuplicateCaseVariantRequest()));

        // {id} 与 {ID} 视为同一占位符（IgnoreCase），不允许重复出现
        Assert.Contains("重复使用占位符", ex.Message);
    }

    [Fact]
    public async Task Get_GuidPathValue_UsesLowercaseDFormat()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient(server);
        var token = Guid.Parse("0cf7d4a5-1b2c-4d3e-8f90-a1b2c3d4e5f6");

        await client.RequestAsync(new GuidPathRequest { Token = token });

        var received = Assert.Single(server.Received);
        // Guid.ToString() 的 "d" 格式：小写 + 连字符
        Assert.Equal($"/tokens/{token}", received.Path);
    }

    [Fact]
    public async Task Get_RecordRequestModel_BindsPositionalProperty()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient(server);

        // 位置参数 record（init-only 属性）可直接做请求模型
        var response = await client.RequestAsync(new RecordRequest(3));

        Assert.Equal(200, response.Status);

        var received = Assert.Single(server.Received);
        Assert.Equal("/records/3", received.Path);
    }

    // ---- 默认 Body 边界 ----

    [Fact]
    public async Task Post_DefaultBodyNullValue_NoBodySent()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient(server);

        await client.RequestAsync(new DefaultBodyNullRequest { Payload = null });

        var received = Assert.Single(server.Received);
        Assert.Equal("POST", received.Method);
        Assert.Empty(received.Body); // 默认 Body 属性为 null：不发请求体
        Assert.Null(received.Query);
    }

    [Fact]
    public async Task Post_UnannotatedScalarArray_DefaultsToJsonArrayBody()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient(server);

        // int[] 非标量 → 默认整个数组作为 JSON 请求体（钉死该语义：要发查询参数请显式 [Query]）
        await client.RequestAsync(new ScalarArrayDefaultBodyRequest { Tags = [1, 2, 3] });

        var received = Assert.Single(server.Received);
        Assert.Equal("[1,2,3]", Encoding.UTF8.GetString(received.Body));
        Assert.Null(received.Query);
    }

    [Fact]
    public async Task Post_BodyContentTypeOverriddenViaConfigure_FormEncoded()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient(server);

        await client.RequestAsync(new FormBodyRequest { Payload = new UserPayload("carol", 25) });

        var received = Assert.Single(server.Received);
        // [Body] 的编码规则与核心包 data 一致：ContentType 改表单就走键值对编码
        Assert.Equal("application/x-www-form-urlencoded", received.Headers["Content-Type"]);
        Assert.Contains("Name=carol", Encoding.UTF8.GetString(received.Body));
    }

    // ---- fail-fast：非法值在发出前被拦下 ----

    [Fact]
    public async Task Header_ValueWithControlChars_FailsFastBeforeSend()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient(server);

        // CRLF 注入：AxiosHeaders 拦截（ArgumentException），请求一个字节都不出
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.RequestAsync(new EvilHeaderRequest { Evil = "a\r\nX-Evil: injected" }));

        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Bearer_TokenWithControlChars_FailsFastBeforeSend()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = new ApiClient(server.Url)
        {
            Authorization = new BearerAuthorizationProvider("tok\r\nX-Evil: injected")
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.RequestAsync(new GetUserRequest { Id = 1 }));

        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Auth_ProviderThrows_NoRequestSent()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = new ApiClient(server.Url)
        {
            Authorization = new ThrowingAuthorizationProvider()
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.RequestAsync(new GetUserRequest { Id = 1 }));

        Assert.Empty(server.Received); // 授权失败（含抛异常）不发出半个请求
    }

    private sealed class ThrowingAuthorizationProvider : IAuthorizationProvider
    {
        public ValueTask ApplyAsync(ApiAuthContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("凭据源不可用（模拟）。");
    }

    // ---- 原始响应形态 ----

    [Fact]
    public async Task Get_ByteArrayResponse_ReceivesRawBytes()
    {
        var payload = new byte[] { 0x00, 0xFF, 0x10, 0x7F, 0x80 };
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: payload,
            ContentType: "application/octet-stream"));

        using var client = CreateClient(server);

        var response = await client.RequestAsync(new DownloadBytesRequest());

        Assert.Equal(200, response.Status);
        Assert.Equal(payload, response.Data); // byte[] 目标：字节原样到达，不做解码
    }

    [Fact]
    public async Task Get_StreamResponse_AutoSwitchesAndDeliversBody()
    {
        var payload = Encoding.UTF8.GetBytes("stream-payload-0123456789");
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: payload,
            ContentType: "application/octet-stream"));

        using var client = CreateClient(server);

        var response = await client.RequestAsync(new DownloadStreamRequest());

        Assert.Equal(200, response.Status);

        // TResponse 为 Stream 时自动切流式响应；读完须释放以归还连接
        using var stream = response.Data!;
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        Assert.Equal(payload, ms.ToArray());
    }

    // ---- 连接复用与门面守卫 ----

    [Fact]
    public async Task TwoSequentialRequests_ReuseSingleConnection()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient(server);

        await client.RequestAsync(new GetUserRequest { Id = 1 });
        await client.RequestAsync(new GetUserRequest { Id = 2 });

        Assert.Equal(2, server.Received.Count);
        // 长持有的 ApiClient 复用连接池：两次请求只建一条 TCP 连接
        Assert.Equal(1, server.TotalConnections);
    }

    [Fact]
    public void ApiClient_GuardsBlankBaseAddress()
    {
        Assert.Throws<ArgumentException>(() => new ApiClient(""));
        Assert.Throws<ArgumentException>(() => new ApiClient("   "));
    }

    [Fact]
    public async Task Request_NullModel_ThrowsArgumentNull()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.RequestAsync<Person>(null!));
        Assert.Throws<ArgumentNullException>(() => client.GetUri<Person>(null!));
    }

    // ---- 自定义谓词：HttpApiAttribute 直接引用 HttpMethod ----

    [Fact]
    public async Task PropFind_CustomVerbViaHttpMethodInstance_PassesThrough()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient(server);

        var response = await client.RequestAsync(new PropFindRequest());

        Assert.Equal(200, response.Status);

        var received = Assert.Single(server.Received);
        Assert.Equal("PROPFIND", received.Method);
        Assert.Equal("/calendar/event", received.Path);
    }

    // ---- 直用 HttpApiAttribute（字符串方法名）----

    [Fact]
    public async Task DirectHttpApi_LowercaseMethod_NormalizedOnWire()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient(server);

        var response = await client.RequestAsync(new DirectHttpApiDeleteRequest { Id = 4 });

        Assert.Equal(200, response.Status);

        var received = Assert.Single(server.Received);
        // "delete" 归一化为规范 DELETE，请求行与内置 [Delete] 子类完全一致
        Assert.Equal("DELETE", received.Method);
        Assert.Equal("/api/users/4", received.Path);
    }

    [Fact]
    public async Task DirectHttpApi_LowercasePost_DefaultBodyRuleApplies()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient(server);

        await client.RequestAsync(new DirectHttpApiPostRequest { Payload = new Person("iris", 28) });

        var received = Assert.Single(server.Received);
        // 归一化后的 POST 参与默认 Body 判定：唯一未标注非标量属性进请求体
        Assert.Equal("POST", received.Method);
        Assert.Contains("iris", Encoding.UTF8.GetString(received.Body));
        Assert.Null(received.Query);
    }

    [Fact]
    public void DirectHttpApi_BlankMethodOrPath_IsRejected()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");

        var methodError = Assert.Throws<InvalidOperationException>(
            () => client.GetUri(new BlankMethodRequest()));
        Assert.Contains("HTTP 方法不能为空白", methodError.Message);

        var pathError = Assert.Throws<InvalidOperationException>(
            () => client.GetUri(new BlankPathRequest()));
        Assert.Contains("路径模板不能为空白", pathError.Message);
    }
}
