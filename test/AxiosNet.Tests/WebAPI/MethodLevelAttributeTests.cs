using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;

namespace AxiosNet.Tests.WebAPI;

/// <summary>方法级 `[Headers]` / `[Authorize]` / `[AllowAnonymous]`：压过接口级声明，同层冲突报错。</summary>
public sealed class MethodLevelAttributeTests
{
    [Fact]
    public async Task MethodHeaders_OverrideInterfaceHeader_AndAppendOwn()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = CreateClient(server);

        await client.Create<IMethodLevelApi>().HeadersAsync();

        var received = Assert.Single(server.Received);
        Assert.Equal("method", received.Headers["X-Scope"]);
        Assert.Equal("yes", received.Headers["X-Method"]);

        // 接口级其它头照常生效。
        Assert.Equal("application/json", received.Headers["Accept"]);
    }

    [Fact]
    public async Task MethodHeaders_WithoutOwnHeader_KeepsInterfaceHeader()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = CreateClient(server);

        await client.Create<IMethodLevelApi>().PlainAsync();

        var received = Assert.Single(server.Received);
        Assert.Equal("interface", received.Headers["X-Scope"]);
    }

    [Fact]
    public async Task MethodHeaders_CanRemoveInterfaceHeader()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = CreateClient(server);

        await client.Create<IMethodLevelApi>().RemoveHeaderAsync();

        var received = Assert.Single(server.Received);
        Assert.False(received.Headers.ContainsKey("X-Scope"));
    }

    [Fact]
    public async Task MethodAllowAnonymous_BeatsInterfaceAuthorize()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = CreateClient(server);
        client.AccessTokenProvider = new StaticTokenProvider("tok");

        await client.Create<IMethodLevelApi>().AnonymousAsync();

        var received = Assert.Single(server.Received);
        Assert.False(received.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task MethodAuthorize_BeatsInterfaceAllowAnonymous()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = CreateClient(server);
        client.AccessTokenProvider = new StaticTokenProvider("tok");

        await client.Create<IAnonymousBaseApi>().RequiredAsync();

        var received = Assert.Single(server.Received);
        Assert.Equal("Bearer tok", received.Headers["Authorization"]);
    }

    [Fact]
    public async Task MethodAuthorizeScheme_OverridesInterfaceScheme()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = CreateClient(server);
        client.AccessTokenProvider = new StaticTokenProvider("tok");

        await client.Create<IMethodLevelApi>().BasicAsync();

        var received = Assert.Single(server.Received);
        Assert.Equal("Basic tok", received.Headers["Authorization"]);
    }

    [Fact]
    public async Task MethodAuthorize_WithoutProvider_SendsNoHeader()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = CreateClient(server);

        await client.Create<IMethodLevelApi>().PlainAsync();

        var received = Assert.Single(server.Received);
        Assert.False(received.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task MethodHeaders_ValueRevivesHeaderRemovedByInterface()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = CreateClient(server);

        await client.Create<IRemovingBaseApi>().ReviveAsync();

        var received = Assert.Single(server.Received);
        Assert.Equal("revived", received.Headers["X-Scope"]);
    }

    [Fact]
    public void ConflictingMethodAttributes_ReportedByValidation()
    {
        var errors = ApiClient.ValidateInterfaces(typeof(IConflictingMethodApi));

        var error = Assert.Single(errors);
        Assert.Contains("[Authorize]", error);
        Assert.Contains("[AllowAnonymous]", error);
    }

    private static ApiClient CreateClient(TestHttpServer server) => new(server.Url);

    private sealed class StaticTokenProvider(string token) : IAccessTokenProvider
    {
        public ValueTask<string?> GetTokenAsync(ApiAuthContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult<string?>(token);
    }
}

[Authorize]
[Headers("Accept: application/json", "X-Scope: interface")]
public interface IMethodLevelApi
{
    [Get("method-headers")]
    [Headers("X-Scope: method", "X-Method: yes")]
    Task<AxiosResponse<Person>> HeadersAsync();

    [Get("plain")]
    Task<AxiosResponse<Person>> PlainAsync();

    [Get("remove-header")]
    [Headers("X-Scope")]
    Task<AxiosResponse<Person>> RemoveHeaderAsync();

    [Get("anonymous")]
    [AllowAnonymous]
    Task<AxiosResponse<Person>> AnonymousAsync();

    [Get("basic")]
    [Authorize("Basic")]
    Task<AxiosResponse<Person>> BasicAsync();
}

[AllowAnonymous]
public interface IAnonymousBaseApi
{
    [Get("required")]
    [Authorize]
    Task<AxiosResponse<Person>> RequiredAsync();
}

[Headers("X-Scope")]
public interface IRemovingBaseApi
{
    [Get("revive")]
    [Headers("X-Scope: revived")]
    Task<AxiosResponse<Person>> ReviveAsync();
}

public interface IConflictingMethodApi
{
    [Get("conflict")]
    [Authorize]
    [AllowAnonymous]
    Task<AxiosResponse<Person>> GetAsync();
}
