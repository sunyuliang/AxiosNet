using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Attributes;

namespace AxiosNet.Tests.WebAPI;

/// <summary>
/// 审查存疑格的专项探针：每格一条用例，逐格定性（声明式请求模型、路由、参数绑定、授权、401 重放、错误映射、接口代理）。
/// </summary>
public sealed class ReviewProbeTests
{
    /// <summary>格 1：请求模型同时驱动 GetUri 与实际发送，两者给出的路径 / 查询 / 头一致。</summary>
    [Fact]
    public async Task Probe_RequestModel_GetUriAndSendAgree()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 18)));
        using var client = Axios.Create(server.Url);

        var model = new GetUserRequest { Id = 7, Verbose = "1", Trace = "trace-1" };
        var uri = new Uri(client.GetUri(model));
        Assert.Equal("/users/7", uri.AbsolutePath);
        Assert.Equal("?verbose=1", uri.Query);

        await client.RequestAsync(model);

        var received = Assert.Single(server.Received);
        Assert.Equal("/users/7", received.Path);
        Assert.Equal("verbose=1", received.Query);
        Assert.Equal("trace-1", received.Headers["X-Trace"]);
    }

    /// <summary>格 2：路由与带 query / fragment 的 BaseAddress 组合时按 RFC 3986 解析，不把 query 拼进新路径。</summary>
    [Fact]
    public async Task Probe_Routing_BaseQueryAndFragmentAreResolvedNotConcatenated()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = Axios.Create(server.Url + "?tenant=legacy#section");

        var model = new ProbeRouteRequest { Id = 9, Filter = "x" };
        var uri = new Uri(client.GetUri(model));
        Assert.Equal("/items/9", uri.AbsolutePath);
        Assert.Equal("?filter=x", uri.Query);
        Assert.Equal(string.Empty, uri.Fragment);

        await client.RequestAsync(model);

        var received = Assert.Single(server.Received);
        Assert.Equal("/items/9", received.Path);
        Assert.Equal("filter=x", received.Query);
    }

    /// <summary>格 3：参数绑定——null 查询值不发键，数组按默认规则重复键，模型键名取属性名原样。</summary>
    [Fact]
    public async Task Probe_Binding_NullQueryOmittedAndArrayRepeated()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(Array.Empty<Person>()));
        using var client = Axios.Create(server.Url);

        await client.RequestAsync(new ListUsersRequest { Ids = [1, 2], Role = null });

        var received = Assert.Single(server.Received);
        Assert.Equal("/users", received.Path);
        // 模型绑定用属性名原样做键（此处为 PascalCase）；接口参数才用参数名（小写）。
        Assert.Contains("Ids[]=1", received.Query);
        Assert.Contains("Ids[]=2", received.Query);
        Assert.DoesNotContain("Role", received.Query);
    }

    /// <summary>格 4：授权——<c>[Authorize]</c> 带上提供方令牌，<c>[AllowAnonymous]</c> 不写认证头。</summary>
    [Fact]
    public async Task Probe_Authorization_AuthorizeSendsToken_AllowAnonymousDoesNot()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url) { CredentialProvider = CredentialProviders.Fixed("tok") };

        await client.Create<IAuthorizedApi>().GetAsync();
        await client.Create<IAnonymousApi>().GetAsync();

        Assert.Equal(2, server.Received.Count);
        Assert.Equal("Bearer tok", server.Received[0].Headers["Authorization"]);
        Assert.False(server.Received[1].Headers.ContainsKey("Authorization"));
    }

    /// <summary>格 5：401 重放——刷新令牌后重发一次，且请求体在第二次发送时完整保留。</summary>
    [Fact]
    public async Task Probe_UnauthorizedReplay_ResendsOnceWithRetainedBody()
    {
        await using var server = TestHttpServer.Start(request =>
            request.Headers.TryGetValue("authorization", out var auth) && auth == "Bearer tok-2"
                ? TestJson.Response(new Person("ok", 1))
                : TestJson.Response(new { error = "unauthorized" }, statusCode: 401));

        var issued = 0;
        using var tokens = new AccessTokenManager(new AccessTokenOptions(_ =>
            ValueTask.FromResult(new AccessToken("tok-" + Interlocked.Increment(ref issued)))));
        using var client = new ApiClient(server.Url) { CredentialProvider = tokens };

        var response = await client.RequestAsync(new ProbeSecureBodyRequest
        {
            Payload = new Person("bob", 30)
        });

        Assert.Equal(200, response.Status);
        Assert.Equal(2, server.Received.Count);
        Assert.Equal("Bearer tok-1", server.Received[0].Headers["Authorization"]);
        Assert.Equal("Bearer tok-2", server.Received[1].Headers["Authorization"]);
        Assert.Contains("bob", System.Text.Encoding.UTF8.GetString(server.Received[0].Body));
        Assert.Contains("bob", System.Text.Encoding.UTF8.GetString(server.Received[1].Body));
    }

    /// <summary>格 6：错误映射——按请求 <c>ValidateStatus = _ =&gt; true</c> 时不抛 <see cref="AxiosException"/>，直接拿到错误响应。</summary>
    [Fact]
    public async Task Probe_ErrorMapping_ValidateStatusWinsOverDefaultThrow()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new { error = "boom" }, statusCode: 500));
        using var client = Axios.Create(server.Url);

        var response = await client.RequestAsync(
            new ProbeFailRequest(),
            configure: c => c.ValidateStatus = _ => true);

        Assert.Equal(500, response.Status);
        Assert.Single(server.Received);
    }

    /// <summary>格 7：接口代理——<c>Create&lt;T&gt;()</c> 返回可用代理，路径 / 查询 / 取消令牌按声明绑定。</summary>
    [Fact]
    public async Task Probe_InterfaceProxy_BindsRouteAndQuery()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 18)));
        using var client = Axios.Create(server.Url);

        var api = client.Create<IProbeApi>();
        var response = await api.GetAsync(3, "x", CancellationToken.None);

        Assert.Equal("alice", response.Data!.Name);
        var received = Assert.Single(server.Received);
        Assert.Equal("/proxy/3", received.Path);
        Assert.Equal("q=x", received.Query);
    }

    // ------------------------------------------------------------------
    // 探针专用模型
    // ------------------------------------------------------------------

    [Get("items/{id}")]
    public sealed class ProbeRouteRequest : IApiRequest<Person>
    {
        [Path]
        public int Id { get; set; }

        [Query("filter")]
        public string? Filter { get; set; }
    }

    [Put("secure-body")]
    public sealed class ProbeSecureBodyRequest : IApiRequest<Person>
    {
        [Body]
        public Person Payload { get; set; } = null!;
    }

    [Get("fail")]
    public sealed class ProbeFailRequest : IApiRequest<Person>
    {
    }

    public interface IProbeApi
    {
        [Get("proxy/{id}")]
        Task<AxiosResponse<Person>> GetAsync(
            [Path] int id,
            [Query] string? q = null,
            CancellationToken cancellationToken = default);
    }
}
