
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Text;

namespace AxiosNet.Tests.WebAPI;

/// <summary>
/// 声明式授权：`[Authorize]`、`[AllowAnonymous]`、token provider 与授权 provider 的覆盖顺序。
/// </summary>
public sealed class AuthorizationTests
{
    // ===============================================================
    // 8. 按请求显式写 Auth = null 必须能关掉实例级 Basic 认证（否则凭据会静默发到第三方端点）。
    // ===============================================================

    [Fact]
    public async Task RequestScopedNullAuth_ClearsInstanceAuth()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new AxiosClient(new AxiosConfig
{
            Connection = new()
            {
                Auth = new AxiosAuth("user", "pass"),
            },
        });

        await client.RequestAsync(new AxiosConfig
{
            Url = server.Url + "public",
            Connection = new()
            {
                Auth = null,
            },
        });

        Assert.False(server.Received[0].Headers.ContainsKey("authorization"));
    }

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

    // ------------------------------------------------------------------
    // 授权声明
    // ------------------------------------------------------------------

    [Fact]
    public async Task Authorize_UsesTokenProvider_WithBearerPrefix()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url)
        {
            AccessTokenProvider = new StaticTokenProvider("tok-1")
        };

        await client.RequestAsync(new AuthorizedRequest());

        Assert.Equal("Bearer tok-1", Assert.Single(server.Received).Headers["Authorization"]);
    }

    [Fact]
    public async Task Authorize_SchemeFromAttribute_IsUsed()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url)
        {
            AccessTokenProvider = new StaticTokenProvider("dXNlcjpwYXNz")
        };

        await client.RequestAsync(new BasicAuthorizedRequest());

        Assert.Equal("Basic dXNlcjpwYXNz", Assert.Single(server.Received).Headers["Authorization"]);
    }

    [Fact]
    public async Task AllowAnonymous_SkipsTokenAndAuthorizationProviders()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        var recorder = new RecordingAuthorizationProvider();
        using var client = new ApiClient(server.Url)
        {
            AccessTokenProvider = new StaticTokenProvider("tok-1"),
            Authorization = recorder
        };

        await client.RequestAsync(new AnonymousRequest());

        Assert.False(Assert.Single(server.Received).Headers.ContainsKey("Authorization"));
        Assert.Equal(0, recorder.Calls);
    }

    [Fact]
    public async Task ExplicitAuthorizationHeader_IsNotOverwrittenByTokenProvider()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url)
        {
            AccessTokenProvider = new StaticTokenProvider("tok-1")
        };

        await client.RequestAsync(new ExplicitAuthorizationRequest { Authorization = "Signature abc" });

        Assert.Equal("Signature abc", Assert.Single(server.Received).Headers["Authorization"]);
    }

    [Fact]
    public async Task AuthorizationProvider_RunsAfterTokenProvider_AndCanOverride()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url)
        {
            AccessTokenProvider = new StaticTokenProvider("tok-1"),
            Authorization = new OverridingAuthorizationProvider("Final xyz")
        };

        await client.RequestAsync(new AuthorizedRequest());

        Assert.Equal("Final xyz", Assert.Single(server.Received).Headers["Authorization"]);
    }

    [Fact]
    public async Task ExplicitAuthorizationInInstanceDefaults_IsNotOverwrittenByTokenProvider()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url, defaults => defaults.Headers.Set("Authorization", "Bearer static"))
        {
            AccessTokenProvider = new StaticTokenProvider("tok-1")
        };

        await client.RequestAsync(new AuthorizedRequest());

        Assert.Equal("Bearer static", Assert.Single(server.Received).Headers["Authorization"]);
    }

    [Fact]
    public async Task NullToken_WritesNoHeader()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url)
        {
            AccessTokenProvider = new StaticTokenProvider(null)
        };

        await client.RequestAsync(new AuthorizedRequest());

        Assert.False(Assert.Single(server.Received).Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task UnannotatedModel_StillRunsAuthorizationProvider()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        var recorder = new RecordingAuthorizationProvider();
        using var client = new ApiClient(server.Url) { Authorization = recorder };

        await client.RequestAsync(new UnannotatedRequest());

        Assert.Equal(1, recorder.Calls);
    }

    [Fact]
    public void AuthorizeAndAllowAnonymous_OnSameModel_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ApiClient("http://127.0.0.1:1/").GetUri(new ConflictingAuthorizationRequest()));

        Assert.Contains("[Authorize]", ex.Message);
        Assert.Contains("[AllowAnonymous]", ex.Message);
    }

    private static byte[] Body(TestRequest request) => request.Body;

    // ===============================================================
    // 测试夹具
    // ===============================================================


    /// <summary>
    /// 分块写出、块间带 60ms 间隔的正文：让「传满声明长度即锁死」的判定在重发轮次里
    /// 必然产生一次非 force 的上报（而不是靠最终 Complete 的强制上报掩盖问题）。
    /// </summary>
    private sealed class SlowChunkedContent : HttpContent
    {
        private readonly int _chunks;
        private readonly int _chunkSize;

        public SlowChunkedContent(int chunks, int chunkSize)
        {
            _chunks = chunks;
            _chunkSize = chunkSize;
            Headers.ContentLength = (long)chunks * chunkSize;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => await SerializeToStreamAsync(stream, context, CancellationToken.None).ConfigureAwait(false);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[_chunkSize];
            for (var i = 0; i < _chunks; i++)
            {
                await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                await Task.Delay(60, cancellationToken).ConfigureAwait(false);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = (long)_chunks * _chunkSize;
            return true;
        }
    }

    /// <summary>测试用的最小自定义授权：委托出一行实现（生产实现按 IAuthorizationProvider 接口写即可）。</summary>
    private sealed class DelegateAuthorizationProvider(
        Func<ApiAuthContext, ValueTask> apply) : IAuthorizationProvider
    {
        public ValueTask ApplyAsync(ApiAuthContext context, CancellationToken cancellationToken)
            => apply(context);
    }


    private sealed class ThrowingAuthorizationProvider : IAuthorizationProvider
    {
        public ValueTask ApplyAsync(ApiAuthContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("凭据源不可用（模拟）。");
    }

    // ------------------------------------------------------------------
    // 辅助
    // ------------------------------------------------------------------


    private sealed class StaticTokenProvider : IAccessTokenProvider
    {
        private readonly string? _token;

        public StaticTokenProvider(string? token) => _token = token;

        public ValueTask<string?> GetTokenAsync(ApiAuthContext context, CancellationToken cancellationToken)
            => new(_token);
    }

    private sealed class RecordingAuthorizationProvider : IAuthorizationProvider
    {
        public int Calls { get; private set; }

        public ValueTask ApplyAsync(ApiAuthContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return default;
        }
    }

    private sealed class OverridingAuthorizationProvider : IAuthorizationProvider
    {
        private readonly string _value;

        public OverridingAuthorizationProvider(string value) => _value = value;

        public ValueTask ApplyAsync(ApiAuthContext context, CancellationToken cancellationToken)
        {
            context.Config.Headers.Set("Authorization", _value);
            return default;
        }
    }


    // ------------------------------------------------------------------
    // 请求模型
    // ------------------------------------------------------------------

    [Get("secure")]
    [Authorize]
    private sealed class AuthorizedRequest : IApiRequest<Person>
    {
    }

    [Get("secure")]
    [Authorize("Basic")]
    private sealed class BasicAuthorizedRequest : IApiRequest<Person>
    {
    }

    [Post("login")]
    [AllowAnonymous]
    private sealed class AnonymousRequest : IApiRequest<Person>
    {
    }

    [Get("secure")]
    [Authorize]
    private sealed class ExplicitAuthorizationRequest : IApiRequest<Person>
    {
        [Header("Authorization")]
        public string? Authorization { get; set; }
    }

    [Get("plain")]
    private sealed class UnannotatedRequest : IApiRequest<Person>
    {
    }

    [Get("conflict")]
    [Authorize]
    [AllowAnonymous]
    private sealed class ConflictingAuthorizationRequest : IApiRequest<Person>
    {
    }

}