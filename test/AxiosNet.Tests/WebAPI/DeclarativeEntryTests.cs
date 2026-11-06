using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Attributes;

namespace AxiosNet.Tests.WebAPI;

/// <summary>
/// 阶段 E 的单入口对抗：<c>Axios.Create(baseAddress)</c> 得到的实例同时是原始 HTTP 与声明式入口，
/// 声明式调用必须复用同一个客户端（默认值 / 拦截器 / 连接池），且不引入额外状态。
/// </summary>
public sealed class DeclarativeEntryTests
{
    // ===============================================================
    // 4 行默认路径：基地址实例
    // ===============================================================

    [Fact]
    public async Task CreateByBaseAddress_MergesRelativeUrlAndAppliesDefaults()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 18)));
        using var http = Axios.Create(server.Url, c => c.Timeout = TimeSpan.FromSeconds(5));

        var user = (await http.GetAsync<Person>("users/1")).Data!;

        Assert.Equal("alice", user.Name);
        var received = Assert.Single(server.Received);
        Assert.Equal("/users/1", received.Path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateByBaseAddress_RejectsBlank(string baseAddress)
    {
        var error = Assert.Throws<ArgumentException>(() => Axios.Create(baseAddress));
        Assert.Equal("baseAddress", error.ParamName);
    }

    [Fact]
    public void CreateByBaseAddress_RejectsNull()
    {
        Assert.Throws<ArgumentException>(() => Axios.Create((string)null!));
    }

    // ===============================================================
    // 声明式入口：同一个客户端
    // ===============================================================

    [Fact]
    public async Task DeclarativeProxy_ReusesClientDefaultsAndRequestInterceptor()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 18)));
        using var client = Axios.Create(server.Url, c => c.Headers.Set("X-Default", "yes"));
        client.Interceptors.Request.Use((Func<AxiosConfig, Task<AxiosConfig>>)(config =>
        {
            config.Headers.Set("X-Interceptor", "on");
            return Task.FromResult(config);
        }));

        var api = client.Create<IUserApi>();
        var response = await api.GetAsync(5);

        Assert.Equal("alice", response.Data!.Name);
        var received = Assert.Single(server.Received);
        Assert.Equal("/users/5", received.Path);
        Assert.Equal("yes", received.Headers["X-Default"]);
        Assert.Equal("on", received.Headers["X-Interceptor"]);
    }

    [Fact]
    public async Task DeclarativeProxy_ReadsDefaultsChangedAfterCreation()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 18)));
        using var client = Axios.Create(server.Url);

        var api = client.Create<IUserApi>();
        // 代理创建后改实例默认值：请求期才取值，不能被创建时快照固化。
        client.Defaults.Headers.Set("X-Late", "later");

        await api.GetAsync(5);

        Assert.Equal("later", Assert.Single(server.Received).Headers["X-Late"]);
    }

    [Fact]
    public async Task DeclarativeRequestAsync_AndGetUri_UseModelRoute()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("bob", 30)));
        using var client = Axios.Create(server.Url);

        var response = await client.RequestAsync(new EntryGetUserRequest { Id = 7, Verbose = true });

        Assert.Equal("bob", response.Data!.Name);
        var received = Assert.Single(server.Received);
        Assert.Equal("/users/7", received.Path);
        Assert.Equal("verbose=true", received.Query);
        Assert.Equal($"{server.Url}users/7?verbose=true", client.GetUri(new EntryGetUserRequest { Id = 7, Verbose = true }));
    }

    [Fact]
    public async Task DeclarativeRequestAsync_PreservesExplicitAuthorizationHeader()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 18)));
        using var client = Axios.Create(server.Url, c => c.Headers.Set("Authorization", "Bearer fixed"));

        await client.RequestAsync(new EntryAuthorizedRequest());

        Assert.Equal("Bearer fixed", Assert.Single(server.Received).Headers["Authorization"]);
    }

    [Fact]
    public async Task DeclarativeProxies_TwoClientsStayIsolated()
    {
        await using var first = TestHttpServer.Start(_ => TestJson.Response(new Person("first", 1)));
        await using var second = TestHttpServer.Start(_ => TestJson.Response(new Person("second", 2)));
        using var firstClient = Axios.Create(first.Url);
        using var secondClient = Axios.Create(second.Url);

        var firstApi = firstClient.Create<IUserApi>();
        var secondApi = secondClient.Create<IUserApi>();
        var firstResponse = await firstApi.GetAsync(1);
        var secondResponse = await secondApi.GetAsync(2);

        Assert.Equal("first", firstResponse.Data!.Name);
        Assert.Equal("second", secondResponse.Data!.Name);
        Assert.Equal("/users/1", Assert.Single(first.Received).Path);
        Assert.Equal("/users/2", Assert.Single(second.Received).Path);
    }

    [Fact]
    public async Task DeclarativeRequestAsync_OnDisposedClient_FailsInsteadOfResurrecting()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 18)));
        var client = Axios.Create(server.Url);
        client.Dispose();

        var error = await Assert.ThrowsAnyAsync<AxiosException>(
            () => client.RequestAsync(new EntryGetUserRequest { Id = 1 }));

        Assert.Equal(AxiosException.ErrCanceled, error.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task DeclarativeEntry_ReusesFacadeWithCredentialProvider()
    {
        await using var server = TestHttpServer.Start(request =>
            request.Headers.TryGetValue("authorization", out var authorization) && authorization == "Bearer reused"
                ? TestJson.Response(new Person("alice", 18))
                : TestJson.Response(new { error = "unauthorized" }, statusCode: 401));
        using var client = Axios.Create(server.Url);
        // 先构造门面并设置凭据提供方，再走扩展入口：扩展必须复用同一门面，不能静默变匿名。
        using var facade = new ApiClient(client);
        facade.CredentialProvider = new StaticTokenProvider("reused");

        var response = await client.Create<IAuthorizedApi>().GetAsync();

        Assert.Equal("alice", response.Data!.Name);
        Assert.Equal("Bearer reused", Assert.Single(server.Received).Headers["Authorization"]);
    }

    [Fact]
    public async Task DeclarativeEntry_ReusesFacadeWithErrorFactory()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(
            new { code = "E42", message = "bad request" },
            statusCode: 400));
        using var client = Axios.Create(server.Url);
        using var facade = new ApiClient(client) { ErrorFactory = new ApiErrorFactory<FacadeProblem>() };

        var error = await Assert.ThrowsAsync<ApiError<FacadeProblem>>(
            () => client.RequestAsync(new EntryGetUserRequest { Id = 1 }));

        Assert.Equal("E42", error.Error?.Code);
    }

    [Fact]
    public async Task DeclarativeEntry_TwoExtensionCallsShareFacade()
    {
        await using var server = TestHttpServer.Start(request =>
            request.Headers.TryGetValue("authorization", out var authorization) && authorization == "Bearer once"
                ? TestJson.Response(new Person("alice", 18))
                : TestJson.Response(new { error = "unauthorized" }, statusCode: 401));
        using var client = new ApiClient(server.Url);
        client.CredentialProvider = new StaticTokenProvider("once");

        await client.Axios.Create<IAuthorizedApi>().GetAsync();
        await client.Axios.RequestAsync(new EntryAuthorizedRequest());

        Assert.Equal(2, server.Received.Count);
        Assert.All(server.Received, request => Assert.Equal("Bearer once", request.Headers["Authorization"]));
    }

    [Get("users/{id}")]
    private sealed class EntryGetUserRequest : IApiRequest<Person>
    {
        [Path] public int Id { get; set; }

        [Query("verbose")] public bool Verbose { get; set; }
    }

    [Authorize]
    [Get("secure")]
    private sealed class EntryAuthorizedRequest : IApiRequest<Person>
    {
    }

    private sealed class FacadeProblem
    {
        public string? Code { get; set; }

        public string? Message { get; set; }
    }

    private sealed class StaticTokenProvider(string token) : IApiCredentialProvider
    {
        public ValueTask<ApiCredential> GetCredentialAsync(ApiAuthContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(ApiCredential.Token(token));
    }
}
