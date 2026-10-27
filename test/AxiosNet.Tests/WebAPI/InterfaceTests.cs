
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using System.Text.Json;
using System.Text;
using static AxiosNet.Tests.WebAPI.StreamingTestKit;

namespace AxiosNet.Tests.WebAPI;

/// <summary>
/// 接口级声明：`ApiClient.Create<T>()` 代理、与类模型语义等价、并发共享元数据。
/// </summary>
public sealed class InterfaceTests
{
    [Fact]
    public async Task Get_PathQueryHeaderAndStaticHeader_Bound()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 18)));
        using var client = CreateClient(server);

        var response = await client.Create<IUserApi>().GetAsync(5, "1", "trace-1");

        Assert.Equal(200, response.Status);
        Assert.Equal("alice", response.Data!.Name);

        var received = Assert.Single(server.Received);
        Assert.Equal("GET", received.Method);
        Assert.Equal("/users/5", received.Path);
        Assert.Equal("verbose=1", received.Query);
        Assert.Equal("trace-1", received.Headers["X-Trace"]);
        Assert.Equal("application/json", received.Headers["Accept"]);
    }

    [Fact]
    public async Task Post_SingleUnannotatedPayload_DefaultsToBody()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("created", 1), statusCode: 201));
        using var client = CreateClient(server);

        var response = await client.Create<IUserApi>().CreateAsync(new Person("alice", 18));

        Assert.Equal(201, response.Status);

        var received = Assert.Single(server.Received);
        Assert.Equal("POST", received.Method);
        Assert.Equal("/users", received.Path);
        Assert.Equal("application/json", received.Headers["Content-Type"]);

        var json = JsonSerializer.Deserialize<JsonElement>(received.Body);
        Assert.Equal("alice", json.GetProperty("name").GetString());
        Assert.Equal(18, json.GetProperty("age").GetInt32());
    }

    [Fact]
    public async Task Post_ExplicitNullBody_SendsNoBody()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = CreateClient(server);

        await client.Create<IUserApi>().CreateNullAsync(null);

        var received = Assert.Single(server.Received);
        Assert.Equal("POST", received.Method);
        Assert.Empty(received.Body);
    }

    [Fact]
    public async Task Get_UnannotatedAndExplicitQuery_UseArrayRules()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(Array.Empty<Person>()));
        using var client = CreateClient(server);

        await client.Create<IUserApi>().ListAsync([1, 2], "admin");

        var received = Assert.Single(server.Received);
        var query = Uri.UnescapeDataString(received.Query ?? "");
        Assert.Contains("ids[]=1", query);
        Assert.Contains("ids[]=2", query);
        Assert.Contains("role=admin", query);
    }

    [Fact]
    public async Task Get_HeaderCollectionAndApiIgnore_Bound()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = CreateClient(server);

        var api = client.Create<IUserApi>();
        await api.HeaderCollectionAsync(new Dictionary<string, string> { ["X-A"] = "1", ["X-B"] = "2" });
        await api.IgnoredAsync("secret", "alice");

        Assert.Equal("1", server.Received[0].Headers["X-A"]);
        Assert.Equal("2", server.Received[0].Headers["X-B"]);
        Assert.Equal("name=alice", server.Received[1].Query);
    }

    [Fact]
    public async Task Inheritance_BaseMethod_UsesScopeStaticHeaders()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = CreateClient(server);

        await client.Create<IDerivedApi>().GetAsync(1);
        await client.Create<IBaseApi>().GetAsync(2);

        // 同一基接口方法经派生接口调用：派生接口的静态头生效；直接声明基接口时不带派生头
        Assert.Equal("base", server.Received[0].Headers["X-Base"]);
        Assert.Equal("derived", server.Received[0].Headers["X-Scope"]);
        Assert.Equal("base", server.Received[1].Headers["X-Base"]);
        Assert.False(server.Received[1].Headers.ContainsKey("X-Scope"));
    }

    [Fact]
    public async Task Authorize_OnInterface_SendsBearer()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url)
        {
            Authorization = new BearerAuthorizationProvider("tok-1")
        };

        await client.Create<IAuthorizedApi>().GetAsync();

        Assert.Equal("Bearer tok-1", Assert.Single(server.Received).Headers["Authorization"]);
    }

    [Fact]
    public async Task AllowAnonymous_OnInterface_SkipsAuthorization()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url)
        {
            Authorization = new BearerAuthorizationProvider("tok-1")
        };

        await client.Create<IAnonymousApi>().GetAsync();

        Assert.False(Assert.Single(server.Received).Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task PathNull_FaultsTheReturnedTask_NotTheCall()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = CreateClient(server);

        var task = client.Create<IPathNullApi>().GetAsync(null);

        // 调用本身不抛：异常进入返回的 Task，与异步方法语义一致
        Assert.NotNull(task);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Contains("为 null", ex.Message);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task CanceledToken_ProducesErrCanceled()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = CreateClient(server);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.Create<IUserApi>().CancelAsync(cts.Token));

        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task ErrorFactory_ReceivesInterfaceInvocation()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("err", 1), statusCode: 500));
        var factory = new CapturingErrorFactory();
        using var client = new ApiClient(server.Url) { ErrorFactory = factory };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.Create<IStatusApi>().GetAsync());

        Assert.Equal("captured", ex.Message);
        var invocation = Assert.IsType<ApiInterfaceInvocation>(factory.Last!.Request);
        Assert.Equal(typeof(IStatusApi), invocation.InterfaceType);
        Assert.Equal(nameof(IStatusApi.GetAsync), invocation.MethodName);
        Assert.Empty(invocation.Arguments);
    }

    [Fact]
    public void ValidateInterfaces_ReportsAllDeclarationErrors()
    {
        var errors = ApiClient.ValidateInterfaces(
            typeof(IMissingRouteApi),
            typeof(IMultipleBodyApi),
            typeof(IWrongReturnApi),
            typeof(IStreamWithoutMultipartApi),
            typeof(IMultipartWithBodyApi),
            typeof(IStreamOnPlainApi),
            typeof(IInvalidStreamFormatApi));

        Assert.Contains(errors, error => error.Contains("缺少路由特性"));
        Assert.Contains(errors, error => error.Contains("[Body]"));
        Assert.Contains(errors, error => error.Contains("Task<AxiosResponse<T>>"));
        Assert.Contains(errors, error => error.Contains("[Part]") && error.Contains("[Multipart]"));
        Assert.Contains(errors, error => error.Contains("互斥"));
        Assert.Contains(errors, error => error.Contains("[Stream]"));
        Assert.Contains(errors, error => error.Contains("99"));
        Assert.Empty(ApiClient.ValidateInterfaces(
            typeof(IUserApi),
            typeof(IUploadApi),
            typeof(IStreamApi),
            typeof(IStreamingApi)));
    }

    // ------------------------------------------------------------------
    // 阶段 2：接口级 multipart
    // ------------------------------------------------------------------

    [Fact]
    public async Task Multipart_PartsUseDeclaredNameFileAndContentType()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = CreateClient(server);

        await client.Create<IUploadApi>().UploadAsync("年报", [1, 2, 3], [4, 5]);

        var received = Assert.Single(server.Received);
        Assert.Equal("POST", received.Method);
        Assert.Equal("/upload", received.Path);
        Assert.StartsWith("multipart/form-data; boundary=", received.Headers["Content-Type"]);

        var body = Encoding.UTF8.GetString(received.Body);
        Assert.Contains("name=title", body);
        Assert.Contains("年报", body);
        Assert.Contains("name=blob; filename=blob", body);           // byte[] → 文件部件（默认 filename = 字段名）
        Assert.Contains("name=extra; filename=a.txt", body);         // [Part(FileName)] 覆盖文件名
        Assert.Contains("text/plain", body);                         // [Part(ContentType)] 覆盖类型
        Assert.True(received.Body.AsSpan().IndexOf(new byte[] { 1, 2, 3 }) >= 0);
        Assert.True(received.Body.AsSpan().IndexOf(new byte[] { 4, 5 }) >= 0);
    }

    [Fact]
    public async Task Multipart_UnannotatedParameters_UseParameterNames()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = CreateClient(server);

        await client.Create<IUploadApi>().UploadDefaultAsync("n1", [9]);

        var body = Encoding.UTF8.GetString(Assert.Single(server.Received).Body);
        Assert.Contains("name=note", body);
        Assert.Contains("n1", body);
        Assert.Contains("name=payload; filename=payload", body);
    }

    [Fact]
    public async Task Multipart_LazyIteratorFailure_IsBadRequest()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = CreateClient(server);

        var error = await Assert.ThrowsAsync<AxiosException>(
            () => client.Create<IBrokenMultipartApi>().UploadAsync(ThrowingSequence()));

        // 与请求模型的 ResolveBody 一致：装配失败归 ERR_BAD_REQUEST，且一个字节都没发出去。
        Assert.Equal(AxiosException.ErrBadRequest, error.Code);
        Assert.Empty(server.Received);
    }

    // ------------------------------------------------------------------
    // 阶段 2：接口级流式
    // ------------------------------------------------------------------

    [Fact]
    public async Task Stream_DefaultFormatIsNdjson()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes("{\"n\":1,\"text\":\"a\"}\n{\"n\":2,\"text\":\"b\"}\n"),
            ContentType: "application/x-ndjson"));
        using var client = CreateClient(server);

        var response = await client.Create<IStreamingApi>().GetAsync();

        Assert.Equal(200, response.Status);
        var frames = await CollectAsync(response.Data!);
        Assert.Equal(new[] { 1, 2 }, frames.Select(frame => frame.N));
        Assert.Equal(new[] { "a", "b" }, frames.Select(frame => frame.Text));
    }

    [Fact]
    public async Task Stream_ServerSentEvents_FormatIsDeclared()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes("data: {\"n\":1}\n\ndata: {\"n\":2}\n\n"),
            ContentType: "text/event-stream"));
        using var client = CreateClient(server);

        var response = await client.Create<IStreamApi>().SseAsync();

        Assert.Equal(new[] { 1, 2 }, (await CollectAsync(response.Data!)).Select(frame => frame.N));
    }

    [Fact]
    public async Task Stream_JsonArray_FormatIsDeclared()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes("[{\"n\":1},{\"n\":2}]"),
            ContentType: "application/json"));
        using var client = CreateClient(server);

        var response = await client.Create<IStreamApi>().JsonArrayAsync();

        Assert.Equal(new[] { 1, 2 }, (await CollectAsync(response.Data!)).Select(frame => frame.N));
    }

    [Fact]
    public void Create_OnClass_ThrowsArgumentException()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");
        Assert.Throws<ArgumentException>(() => client.Create<GetUserRequest>());
    }

    [Fact]
    public async Task InterfaceAndModel_ProduceIdenticalRequests()
    {
        await using var modelServer = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        await using var interfaceServer = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var modelClient = CreateClient(modelServer);
        using var interfaceClient = CreateClient(interfaceServer);

        var payload = new Person("alice", 18);
        await modelClient.RequestAsync(new CreateUserRequest { Person = payload });
        await interfaceClient.Create<IUserApi>().CreateAsync(payload);

        var model = Assert.Single(modelServer.Received);
        var api = Assert.Single(interfaceServer.Received);

        Assert.Equal(model.Method, api.Method);
        Assert.Equal(model.Path, api.Path);
        Assert.Equal(model.Query, api.Query);
        Assert.Equal(model.Headers["Content-Type"], api.Headers["Content-Type"]);
        Assert.Equal(model.Body, api.Body);
    }

    [Fact]
    public async Task ConcurrentCalls_ShareMetadataAndStayIndependent()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = CreateClient(server);
        var api = client.Create<IUserApi>();

        await Task.WhenAll(Enumerable.Range(1, 20).Select(id => api.GetAsync(id)));

        Assert.Equal(20, server.Received.Count);
        Assert.All(server.Received, request => Assert.StartsWith("/users/", request.Path));
    }

    private static ApiClient CreateClient(TestHttpServer server) => new(server.Url);


    private static IEnumerable<string> ThrowingSequence()
    {
        yield return "first";
        throw new InvalidOperationException("boom");
    }

    private sealed class CapturingErrorFactory : IApiErrorFactory
    {
        public ApiErrorContext? Last { get; private set; }

        public ValueTask<Exception?> CreateAsync(ApiErrorContext context, CancellationToken cancellationToken)
        {
            Last = context;
            return new ValueTask<Exception?>(new InvalidOperationException("captured"));
        }
    }
}