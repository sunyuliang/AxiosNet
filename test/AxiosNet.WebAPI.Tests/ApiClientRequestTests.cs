using System.Text;
using System.Text.Json;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.WebAPI.Tests;

/// <summary>
/// 声明式绑定与端到端：路由特性、Path / Query / Header / Body 绑定、泛型返回、Configure 钩子、取消。
/// </summary>
public sealed class ApiClientRequestTests
{
    private static ApiClient CreateClient(TestHttpServer server)
        => new(server.Url);

    [Fact]
    public async Task Get_PathQueryHeaderBound_ReturnsTypedResponse()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new Person("alice", 18)));

        using var client = CreateClient(server);

        var response = await client.RequestAsync(new GetUserRequest
        {
            Id = 5,
            Verbose = "1",
            Trace = "trace-1"
        });

        Assert.Equal(200, response.Status);
        Assert.NotNull(response.Data);
        Assert.Equal("alice", response.Data!.Name);
        Assert.Equal(18, response.Data.Age);

        var received = Assert.Single(server.Received);
        Assert.Equal("GET", received.Method);
        Assert.Equal("/users/5", received.Path);
        Assert.Equal("verbose=1", received.Query);
        Assert.Equal("trace-1", received.Headers["X-Trace"]);
    }

    [Fact]
    public async Task Get_UnannotatedProperty_BindsAsQueryByKeyNamed()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new[] { new Person("alice", 18), new Person("bob", 20) }));

        using var client = CreateClient(server);

        var response = await client.RequestAsync(new ListUsersRequest
        {
            Ids = [1, 2],
            Role = "admin"
        });

        Assert.Equal(200, response.Status);
        Assert.NotNull(response.Data);
        Assert.Equal(2, response.Data!.Length);

        var received = Assert.Single(server.Received);
        // 数组走 AxiosNet 的 a[]=v 规则（键名保留属性名大小写）；标量直接键=值
        Assert.Contains("Ids[]=1", Uri.UnescapeDataString(received.Query ?? ""));
        Assert.Contains("Ids[]=2", Uri.UnescapeDataString(received.Query ?? ""));
        Assert.Contains("Role=admin", Uri.UnescapeDataString(received.Query ?? ""));
    }

    [Fact]
    public async Task Post_BodySerializedAsJson_TypedResponse()
    {
        await using var server = TestHttpServer.Start(req =>
            TestJson.Response(new Person("created", 1), statusCode: 201));

        using var client = CreateClient(server);

        var response = await client.RequestAsync(new CreateUserRequest
        {
            Person = new Person("alice", 18)
        });

        Assert.Equal(201, response.Status);
        Assert.Equal("created", response.Data!.Name);

        var received = Assert.Single(server.Received);
        Assert.Equal("POST", received.Method);
        Assert.Equal("/users", received.Path);
        Assert.Equal("application/json", received.Headers["Content-Type"]);

        var json = JsonSerializer.Deserialize<JsonElement>(received.Body);
        Assert.Equal("alice", json.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Post_NullBodyProperty_NoRequestBodySent()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient(server);

        var response = await client.RequestAsync(new NullBodyRequest { Person = null });

        Assert.Equal(200, response.Status);

        var received = Assert.Single(server.Received);
        Assert.Equal("POST", received.Method);
        Assert.Empty(received.Body);
    }

    [Fact]
    public async Task Delete_Head_MethodComesFromRouteAttribute()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: null));

        using var client = CreateClient(server);

        var deleted = await client.RequestAsync(new DeleteUserRequest { Id = 7 });
        Assert.Equal(200, deleted.Status);

        var ping = await client.RequestAsync(new PingRequest());
        Assert.Equal(200, ping.Status);

        Assert.Equal("DELETE", server.Received[0].Method);
        Assert.Equal("/users/7", server.Received[0].Path);
        Assert.Equal("HEAD", server.Received[1].Method);
        Assert.Equal("/ping", server.Received[1].Path);
    }

    [Fact]
    public async Task Get_PathValueIsUrlEscaped()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new Person("alice", 18)));

        using var client = CreateClient(server);

        var response = await client.RequestAsync(new EscapedPathRequest { Name = "John Doe/a b" });

        Assert.Equal(200, response.Status);

        var received = Assert.Single(server.Received);
        Assert.Equal("/users/John%20Doe%2Fa%20b/profile", received.Path);
    }

    [Fact]
    public async Task Get_AbsoluteUrlInTemplate_OverridesBaseAddress()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new Person("alice", 18)));

        // baseAddress 指向别处：模板里的绝对 URL 必须整体覆盖它
        using var client = new ApiClient("http://192.0.2.1:1/");

        var response = await client.RequestAsync(new AbsoluteUrlRequest { Port = server.Port });

        Assert.Equal(200, response.Status);

        var received = Assert.Single(server.Received);
        Assert.Equal("/abs", received.Path);
    }

    [Fact]
    public async Task Get_ModelConfigureHook_HeaderReachesServer()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new Person("alice", 18)));

        using var client = CreateClient(server);

        var response = await client.RequestAsync(new ConfiguredGetRequest { Id = 1 });

        Assert.Equal(200, response.Status);

        var received = Assert.Single(server.Received);
        Assert.Equal("from-model", received.Headers["X-Configured"]);
    }

    [Fact]
    public async Task Get_CallSiteConfigureHook_RunsAfterModelBinding()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new Person("alice", 18)));

        using var client = CreateClient(server);

        var response = await client.RequestAsync(
            new GetUserRequest { Id = 1, Trace = "trace-1" },
            configure: config => config.Headers.Set("X-Trace", "trace-override"));

        Assert.Equal(200, response.Status);

        var received = Assert.Single(server.Received);
        // 调用处 configure 在模型绑定之后执行：同名的头以 configure 为准
        Assert.Equal("trace-override", received.Headers["X-Trace"]);
    }

    [Fact]
    public async Task Get_InheritedHeaderBinding_RetrievedFromBaseClass()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new Person("alice", 18)));

        using var client = CreateClient(server);

        var response = await client.RequestAsync(new InheritedHeaderRequest { Id = 1, Trace = "t-9" });

        Assert.Equal(200, response.Status);

        var received = Assert.Single(server.Received);
        Assert.Equal("t-9", received.Headers["X-Trace"]);
    }

    [Fact]
    public async Task GetUri_NoRequestSent_ResolvesMergedUrl()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");

        var uri = client.GetUri(new GetUserRequest { Id = 5, Verbose = "1" });

        Assert.Equal("http://127.0.0.1:1/users/5?verbose=1", uri);
    }

    [Fact]
    public async Task Request_CanceledToken_ThrowsAxiosException()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());

        using var client = CreateClient(server);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAsync<AxiosException>(
            () => client.RequestAsync(new GetUserRequest { Id = 1 }, cts.Token));

        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
    }

    [Fact]
    public async Task Request_TimeoutFromConfigureHook_AppliesToPipeline()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse
        {
            Body = TestJson.Bytes(new Person("slow", 1)),
            BodyDelay = TimeSpan.FromSeconds(2)
        });

        using var client = CreateClient(server);

        var ex = await Assert.ThrowsAsync<AxiosException>(
            () => client.RequestAsync(
                new ConfiguredGetRequest { Id = 1 },
                configure: config => config.Timeout = TimeSpan.FromMilliseconds(100)));

        Assert.Equal(AxiosException.ErrConnAborted, ex.Code);
    }

    [Fact]
    public async Task Request_NonSuccessStatus_ThrowsAxiosExceptionWithResponse()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            StatusCode: 404,
            ReasonPhrase: "Not Found",
            Body: Encoding.UTF8.GetBytes("{}")));

        using var client = CreateClient(server);

        var ex = await Assert.ThrowsAsync<AxiosException>(
            () => client.RequestAsync(new GetUserRequest { Id = 1 }));

        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);
        Assert.Equal(404, ex.Status);
    }

    // ---- 默认 Body：POST/PUT/PATCH + 恰好一个未标注的非标量属性 ----

    [Fact]
    public async Task Post_SingleUnannotatedComplexProperty_DefaultsToBody()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));

        using var client = CreateClient(server);

        var response = await client.RequestAsync(new DefaultBodyRequest
        {
            Payload = new Person("carol", 25)
        });

        Assert.Equal(200, response.Status);

        var received = Assert.Single(server.Received);
        Assert.Equal("application/json", received.Headers["Content-Type"].Split(';')[0]);
        var json = JsonSerializer.Deserialize<JsonElement>(received.Body);
        Assert.Equal("carol", json.GetProperty("name").GetString());
        Assert.Null(received.Query); // 没有任何查询参数
    }

    [Fact]
    public async Task Put_SingleUnannotatedComplexProperty_DefaultsToBody()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));

        using var client = CreateClient(server);

        var response = await client.RequestAsync(new PutDefaultBodyRequest
        {
            Id = 7,
            Payload = new Person("dave", 40)
        });

        Assert.Equal(200, response.Status);

        var received = Assert.Single(server.Received);
        Assert.Equal("PUT", received.Method);
        Assert.Equal("/items/7", received.Path);
        var json = JsonSerializer.Deserialize<JsonElement>(received.Body);
        Assert.Equal("dave", json.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Post_SingleUnannotatedScalarProperty_StaysQuery()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient(server);

        await client.RequestAsync(new ScalarDefaultStaysQueryRequest { Page = 3 });

        var received = Assert.Single(server.Received);
        Assert.Equal("Page=3", received.Query); // 标量护栏：不静默变请求体
        Assert.Empty(received.Body);
    }

    [Fact]
    public async Task Post_TwoUnannotatedProperties_BothStayQuery()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient(server);

        await client.RequestAsync(new TwoUnannotatedRequest { Page = 2, Payload = new Person("eve", 31) });

        var received = Assert.Single(server.Received);
        // 多个未标注属性时歧义，全部维持 Query（复杂对象走 a[b]=v 嵌套规则）
        Assert.Contains("Page=2", received.Query);
        Assert.Contains("Payload[Name]=eve", Uri.UnescapeDataString(received.Query ?? ""));
        Assert.Empty(received.Body);
    }

    [Fact]
    public async Task Post_ExplicitQueryAttribute_DoesNotBlockDefaultBody()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));

        using var client = CreateClient(server);

        var response = await client.RequestAsync(new ExplicitQueryThenDefaultBodyRequest
        {
            Page = 4,
            Payload = new Person("frank", 50)
        });

        Assert.Equal(200, response.Status);

        var received = Assert.Single(server.Received);
        // 显式 [Query] 不算「未标注」：唯一未标注属性仍享受默认 Body
        Assert.Contains("page=4", received.Query);
        var json = JsonSerializer.Deserialize<JsonElement>(received.Body);
        Assert.Equal("frank", json.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Delete_UnannotatedComplexProperty_StaysQuery()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient(server);

        await client.RequestAsync(new DeleteDefaultStaysQueryRequest { Id = 9, Payload = new Person("gina", 22) });

        var received = Assert.Single(server.Received);
        Assert.Equal("DELETE", received.Method);
        Assert.Contains("Payload[", Uri.UnescapeDataString(received.Query ?? "")); // DELETE 不参与默认 Body
        Assert.Empty(received.Body);
    }

    // ---- [ApiIgnore]：属性从绑定中完全排除 ----

    [Fact]
    public async Task Get_IgnoredProperty_IsNotSentAnywhere()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient(server);

        await client.RequestAsync(new IgnoredQueryRequest { Name = "alice", CacheKey = "secret-state" });

        var received = Assert.Single(server.Received);
        Assert.Equal("Name=alice", received.Query); // 只有正常绑定进了查询串
        Assert.DoesNotContain("CacheKey", received.Query);
    }

    [Fact]
    public async Task Post_IgnoredProperty_DoesNotBlockDefaultBody()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));

        using var client = CreateClient(server);

        var response = await client.RequestAsync(new IgnoredDoesNotBlockDefaultBodyRequest
        {
            InternalState = "should-not-appear",
            Payload = new Person("hank", 33)
        });

        Assert.Equal(200, response.Status);

        var received = Assert.Single(server.Received);
        // 被忽略的属性不算「未标注」：Payload 仍享受默认 Body，InternalState 任何地方都不出现
        var json = JsonSerializer.Deserialize<JsonElement>(received.Body);
        Assert.Equal("hank", json.GetProperty("name").GetString());
        Assert.Null(received.Query);
        Assert.DoesNotContain("should-not-appear", Encoding.UTF8.GetString(received.Body));
        Assert.DoesNotContain("should-not-appear", received.Query ?? "");
    }

    [Fact]
    public void Ignore_CombinedWithParameterAttribute_IsRejected()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");

        var ex = Assert.Throws<InvalidOperationException>(
            () => client.GetUri(new IgnoreWithQueryRequest()));

        Assert.Contains("[ApiIgnore]", ex.Message);
        Assert.Contains("二义性", ex.Message);
    }

    // ---- 多特性并存：同一属性绑定到多处 ----

    [Fact]
    public async Task Get_PropertyInPathAndHeader_BindsBoth()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient(server);

        await client.RequestAsync(new PathHeaderRequest { Id = 5 });

        var received = Assert.Single(server.Received);
        Assert.Equal("/users/5/scores", received.Path);
        Assert.Equal("5", received.Headers["X-User-Id"]);
    }

    [Fact]
    public async Task Get_PropertyInQueryAndHeader_BindsBoth()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient(server);

        await client.RequestAsync(new QueryHeaderRequest { Trace = "t-42" });

        var received = Assert.Single(server.Received);
        Assert.Equal("trace=t-42", received.Query);
        Assert.Equal("t-42", received.Headers["X-Trace"]);
    }

    [Fact]
    public async Task Post_ScalarPropertyInPathBodyAndHeader_BindsAllThree()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient(server);

        var response = await client.RequestAsync(new PathBodyHeaderRequest { OrderId = 88 });

        Assert.Equal(200, response.Status);

        var received = Assert.Single(server.Received);
        Assert.Equal("/orders/88", received.Path);                          // 路径
        Assert.Equal("88", received.Headers["X-Order-Id"]);                 // 请求头
        Assert.Equal("88", Encoding.UTF8.GetString(received.Body));         // 请求体（标量按 AxiosNet 规则走原始文本）
    }

    [Fact]
    public async Task Post_ComplexPropertyInQueryAndBody_BindsBoth()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient(server);

        var response = await client.RequestAsync(new QueryBodyRequest
        {
            Report = new ReportPayload(66, "weekly")
        });

        Assert.Equal(200, response.Status);

        var received = Assert.Single(server.Received);
        Assert.Contains("reportId[ReportId]=66", Uri.UnescapeDataString(received.Query ?? "")); // 查询串（嵌套规则）
        var json = JsonSerializer.Deserialize<JsonElement>(received.Body);            // 请求体
        Assert.Equal("weekly", json.GetProperty("title").GetString());
    }

    [Fact]
    public void PathOnComplexProperty_IsRejected()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");

        var ex = Assert.Throws<InvalidOperationException>(
            () => client.GetUri(new PathOnComplexRequest()));

        Assert.Contains("Report", ex.Message);
        Assert.Contains("不是标量", ex.Message);
    }

    [Fact]
    public void HeaderOnComplexProperty_IsRejected()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");

        var ex = Assert.Throws<InvalidOperationException>(
            () => client.GetUri(new HeaderOnComplexRequest()));

        Assert.Contains("不是标量", ex.Message);
    }
}
