using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;

namespace AxiosNet.Tests.WebAPI;

/// <summary>
/// 授权链路：内置 Bearer（固定 / 工厂 / 带 scheme）、自定义提供方（按请求类型跳过）、调用时机与异常语义。
/// </summary>
public sealed class ApiClientAuthTests
{
    [Fact]
    public async Task Bearer_StaticToken_SetsAuthorizationHeader()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = new ApiClient(server.Url)
        {
            Authorization = new BearerAuthorizationProvider("tok-1")
        };

        await client.RequestAsync(new GetUserRequest { Id = 1 });

        var received = Assert.Single(server.Received);
        Assert.Equal("Bearer tok-1", received.Headers["Authorization"]);
    }

    [Fact]
    public async Task Bearer_TokenWithScheme_IsSentAsIs()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = new ApiClient(server.Url)
        {
            Authorization = new BearerAuthorizationProvider("Basic dXNlcjpwYXNz")
        };

        await client.RequestAsync(new GetUserRequest { Id = 1 });

        var received = Assert.Single(server.Received);
        Assert.Equal("Basic dXNlcjpwYXNz", received.Headers["Authorization"]);
    }

    [Fact]
    public async Task Bearer_TokenFactory_CalledPerRequest()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        var calls = 0;

        using var client = new ApiClient(server.Url)
        {
            Authorization = new BearerAuthorizationProvider(_ =>
            {
                calls++;
                return Task.FromResult<string?>("tok-" + calls);
            })
        };

        await client.RequestAsync(new GetUserRequest { Id = 1 });
        await client.RequestAsync(new GetUserRequest { Id = 2 });

        Assert.Equal(2, calls);
        Assert.Equal("Bearer tok-1", server.Received[0].Headers["Authorization"]);
        Assert.Equal("Bearer tok-2", server.Received[1].Headers["Authorization"]);
    }

    [Fact]
    public async Task Bearer_FactoryReturnsBlank_ThrowsInvalidOperationException()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = new ApiClient(server.Url)
        {
            Authorization = new BearerAuthorizationProvider(_ => Task.FromResult<string?>(null))
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.RequestAsync(new GetUserRequest { Id = 1 }));

        Assert.Contains("null 或空白", ex.Message);
        Assert.Empty(server.Received); // 授权失败不该发出半个请求
    }

    [Fact]
    public async Task Bearer_ConstructorGuardsBlankToken()
    {
        Assert.Throws<ArgumentException>(() => new BearerAuthorizationProvider(" "));
    }

    [Fact]
    public async Task CustomProvider_CanSkipAuthByRequestType()
    {
        await using var server = TestHttpServer.Start(req =>
            req.Path == "/login"
                ? TestJson.Response("token123")
                : TestJson.Response(new Person("ok", 1)));

        using var client = new ApiClient(server.Url)
        {
            // 登录本身不带凭据；其余请求带
            Authorization = new DelegateAuthorizationProvider(ctx =>
            {
                if (ctx.Request is not LoginRequest)
                {
                    ctx.Config.Headers.Set("Authorization", "Bearer tok");
                }

                return ValueTask.CompletedTask;
            })
        };

        var login = await client.RequestAsync(new LoginRequest { Credentials = new Person("u", 1) });
        var guarded = await client.RequestAsync(new GetUserRequest { Id = 1 });

        // AxiosNet 语义：TResponse 为 string 时 Data 是响应原文（JSON 字符串带引号原样给出）
        Assert.Equal("\"token123\"", login.Data);
        Assert.False(server.Received[0].Headers.ContainsKey("Authorization"));
        Assert.Equal("Bearer tok", server.Received[1].Headers["Authorization"]);
    }

    [Fact]
    public async Task CustomProvider_SeesFinalConfig_WithModelBindingsApplied()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        string? seenTrace = null;

        using var client = new ApiClient(server.Url)
        {
            Authorization = new DelegateAuthorizationProvider(ctx =>
            {
                seenTrace = ctx.Config.Headers.Get("X-Trace");
                return ValueTask.CompletedTask;
            })
        };

        await client.RequestAsync(new GetUserRequest { Id = 1, Trace = "trace-final" });

        // 授权在参数绑定之后执行：模型里的头对签名逻辑可见
        Assert.Equal("trace-final", seenTrace);
    }

    [Fact]
    public async Task NoAuthorizationConfigured_NoAuthHeaderSent()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new GetUserRequest { Id = 1 });

        var received = Assert.Single(server.Received);
        Assert.False(received.Headers.ContainsKey("Authorization"));
    }

    /// <summary>测试用的最小自定义授权：委托出一行实现（生产实现按 IAuthorizationProvider 接口写即可）。</summary>
    private sealed class DelegateAuthorizationProvider(
        Func<ApiAuthContext, ValueTask> apply) : IAuthorizationProvider
    {
        public ValueTask ApplyAsync(ApiAuthContext context, CancellationToken cancellationToken)
            => apply(context);
    }
}
