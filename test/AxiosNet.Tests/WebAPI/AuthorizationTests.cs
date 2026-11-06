using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Attributes;
using System.Net.Http;
using System.Net;

namespace AxiosNet.Tests.WebAPI;

/// <summary>声明式授权：托管凭据、自定义凭据、显式头与授权声明的优先级。</summary>
public sealed class AuthorizationTests
{
    [Fact]
    public async Task RequestScopedNullAuth_ClearsInstanceAuth()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new AxiosClient(new AxiosConfig
        {
            Transport = new()
            {
                BasicAuth = new AxiosAuth("user", "pass"),
            },
        });

        await client.RequestAsync(new AxiosConfig
        {
            Url = server.Url + "public",
            Transport = new()
            {
                BasicAuth = null,
            },
        });

        Assert.False(server.Received[0].Headers.ContainsKey("authorization"));
    }

    [Fact]
    public async Task FixedToken_SetsAuthorizationHeader()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = new ApiClient(server.Url)
        {
            CredentialProvider = CredentialProviders.Fixed("tok-1")
        };

        await client.RequestAsync(new GetUserRequest { Id = 1 });

        var received = Assert.Single(server.Received);
        Assert.Equal("Bearer tok-1", received.Headers["Authorization"]);
    }

    [Fact]
    public async Task HeaderCredential_IsSentAsIs()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = new ApiClient(server.Url)
        {
            CredentialProvider = CredentialProviders.Fixed(ApiCredential.Header("Basic dXNlcjpwYXNz"))
        };

        await client.RequestAsync(new GetUserRequest { Id = 1 });

        var received = Assert.Single(server.Received);
        Assert.Equal("Basic dXNlcjpwYXNz", received.Headers["Authorization"]);
    }

    [Fact]
    public async Task TokenWithSpace_StillGetsSchemePrefix()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = new ApiClient(server.Url)
        {
            // 令牌形态一律拼 scheme：想原样发送完整头值要用 ApiCredential.Header（否则 staleToken 无法从头上反解）。
            CredentialProvider = CredentialProviders.Fixed("Signature abc")
        };

        await client.RequestAsync(new GetUserRequest { Id = 1 });

        var received = Assert.Single(server.Received);
        Assert.Equal("Bearer Signature abc", received.Headers["Authorization"]);
    }

    [Fact]
    public void FixedBlankToken_ThrowsInsteadOfGoingAnonymous()
    {
        var ex = Assert.Throws<ArgumentException>(() => CredentialProviders.Fixed(" "));
        Assert.Contains("不能为空白", ex.Message);
    }

    [Fact]
    public async Task CreatedProvider_IsCalledPerRequest()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        var calls = 0;
        using var client = new ApiClient(server.Url)
        {
            CredentialProvider = CredentialProviders.Create((_, _) =>
                ValueTask.FromResult(ApiCredential.Token("tok-" + Interlocked.Increment(ref calls))))
        };

        await client.RequestAsync(new GetUserRequest { Id = 1 });
        await client.RequestAsync(new GetUserRequest { Id = 2 });

        Assert.Equal(2, calls);
        Assert.Equal("Bearer tok-1", server.Received[0].Headers["Authorization"]);
        Assert.Equal("Bearer tok-2", server.Received[1].Headers["Authorization"]);
    }

    [Fact]
    public async Task BlankCredential_WritesNoHeader()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = new ApiClient(server.Url)
        {
            CredentialProvider = CredentialProviders.Fixed(ApiCredential.Token(null))
        };

        await client.RequestAsync(new GetUserRequest { Id = 1 });

        Assert.False(Assert.Single(server.Received).Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task CustomCredential_CanSkipAuthByRequestType()
    {
        await using var server = TestHttpServer.Start(req =>
            req.Path == "/login"
                ? TestJson.Response("token123")
                : TestJson.Response(new Person("ok", 1)));

        using var client = new ApiClient(server.Url)
        {
            CredentialProvider = CredentialProviders.Create((ctx, _) =>
            {
                if (ctx.Request is not LoginRequest)
                {
                    ctx.Config.Headers.Set("Authorization", "Bearer tok");
                }

                return ValueTask.FromResult(ApiCredential.Custom());
            })
        };

        var login = await client.RequestAsync(new LoginRequest { Credentials = new Person("u", 1) });
        var guarded = await client.RequestAsync(new GetUserRequest { Id = 1 });

        Assert.Equal("\"token123\"", login.Data);
        Assert.False(server.Received[0].Headers.ContainsKey("Authorization"));
        Assert.Equal("Bearer tok", server.Received[1].Headers["Authorization"]);
    }

    [Fact]
    public async Task CustomCredential_SeesFinalConfig_WithModelBindingsApplied()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        string? seenTrace = null;
        using var client = new ApiClient(server.Url)
        {
            CredentialProvider = CredentialProviders.Create((ctx, _) =>
            {
                seenTrace = ctx.Config.Headers.Get("X-Trace");
                return ValueTask.FromResult(ApiCredential.Custom());
            })
        };

        await client.RequestAsync(new GetUserRequest { Id = 1, Trace = "trace-final" });

        Assert.Equal("trace-final", seenTrace);
    }

    [Fact]
    public async Task NoCredentialConfigured_NoAuthHeaderSent()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new GetUserRequest { Id = 1 });

        var received = Assert.Single(server.Received);
        Assert.False(received.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task TokenWithControlChars_FailsFastBeforeSend()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = new ApiClient(server.Url)
        {
            CredentialProvider = CredentialProviders.Fixed("tok\r\nX-Evil: injected")
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.RequestAsync(new GetUserRequest { Id = 1 }));

        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task ProviderThrows_NoRequestSent()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = new ApiClient(server.Url)
        {
            CredentialProvider = new ThrowingCredentialProvider()
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.RequestAsync(new GetUserRequest { Id = 1 }));

        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Authorize_UsesManagedCredential_WithBearerPrefix()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url)
        {
            CredentialProvider = new StaticCredentialProvider("tok-1")
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
            CredentialProvider = new StaticCredentialProvider("dXNlcjpwYXNz")
        };

        await client.RequestAsync(new BasicAuthorizedRequest());

        Assert.Equal("Basic dXNlcjpwYXNz", Assert.Single(server.Received).Headers["Authorization"]);
    }

    [Fact]
    public async Task AllowAnonymous_SkipsCredentialProvider()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        var recorder = new RecordingCredentialProvider();
        using var client = new ApiClient(server.Url) { CredentialProvider = recorder };

        await client.RequestAsync(new AnonymousRequest());

        Assert.False(Assert.Single(server.Received).Headers.ContainsKey("Authorization"));
        Assert.Equal(0, recorder.Calls);
    }

    [Fact]
    public async Task ExplicitAuthorizationHeader_IsNotOverwrittenByManagedCredential()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url)
        {
            CredentialProvider = new StaticCredentialProvider("tok-1")
        };

        await client.RequestAsync(new ExplicitAuthorizationRequest { Authorization = "Signature abc" });

        Assert.Equal("Signature abc", Assert.Single(server.Received).Headers["Authorization"]);
    }

    [Fact]
    public async Task ExplicitAuthorizationHeader_SkipsCredentialProvider()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        var recorder = new RecordingCredentialProvider();
        using var client = new ApiClient(server.Url) { CredentialProvider = recorder };

        await client.RequestAsync(new ExplicitAuthorizationRequest { Authorization = "Signature abc" });

        // 模型自带的头已经给出凭据：不进凭据链路，也就不会白取一次令牌。
        Assert.Equal(0, recorder.Calls);
        Assert.Equal("Signature abc", Assert.Single(server.Received).Headers["Authorization"]);
    }

    [Fact]
    public async Task ExplicitAuthorizationHeader_ProviderFailureDoesNotBlockRequest()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url) { CredentialProvider = new ThrowingCredentialProvider() };

        await client.RequestAsync(new ExplicitAuthorizationRequest { Authorization = "Signature abc" });

        Assert.Equal("Signature abc", Assert.Single(server.Received).Headers["Authorization"]);
    }

    [Fact]
    public async Task ExplicitAuthorizationInDefaults_SkipsCredentialProvider()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        var recorder = new RecordingCredentialProvider();
        using var client = new ApiClient(
            server.Url,
            defaults => defaults.Headers.Set("Authorization", "Bearer static"))
        {
            CredentialProvider = recorder
        };

        await client.RequestAsync(new AuthorizedRequest());

        Assert.Equal(0, recorder.Calls);
        Assert.Equal("Bearer static", Assert.Single(server.Received).Headers["Authorization"]);
    }

    [Fact]
    public async Task CustomCredential_WritesItsOwnHeader()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url)
        {
            CredentialProvider = CredentialProviders.Create((ctx, _) =>
            {
                ctx.Config.Headers.Set("Authorization", "Final xyz");
                return ValueTask.FromResult(ApiCredential.Custom());
            })
        };

        await client.RequestAsync(new AuthorizedRequest());

        Assert.Equal("Final xyz", Assert.Single(server.Received).Headers["Authorization"]);
    }

    [Fact]
    public async Task CustomCredential_DoesNotReplayOn401()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new { error = "unauthorized" }, statusCode: 401));

        var calls = 0;
        using var client = new ApiClient(server.Url)
        {
            CredentialProvider = CredentialProviders.Create((ctx, _) =>
            {
                Interlocked.Increment(ref calls);
                ctx.Config.Headers.Set("Authorization", "Signature abc");
                return ValueTask.FromResult(ApiCredential.Custom());
            })
        };

        await Assert.ThrowsAsync<AxiosException>(() => client.RequestAsync(new AuthorizedRequest()));

        Assert.Equal(1, calls);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task ExplicitAuthorizationInInstanceDefaults_IsNotOverwrittenByManagedCredential()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url, defaults => defaults.Headers.Set("Authorization", "Bearer static"))
        {
            CredentialProvider = new StaticCredentialProvider("tok-1")
        };

        await client.RequestAsync(new AuthorizedRequest());

        Assert.Equal("Bearer static", Assert.Single(server.Received).Headers["Authorization"]);
    }

    [Fact]
    public async Task NoneCredential_WritesNoHeader()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url)
        {
            CredentialProvider = new StaticCredentialProvider(null)
        };

        await client.RequestAsync(new AuthorizedRequest());

        Assert.False(Assert.Single(server.Received).Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task UnannotatedModel_StillRunsCredentialProvider()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        var recorder = new RecordingCredentialProvider();
        using var client = new ApiClient(server.Url) { CredentialProvider = recorder };

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

    /// <summary>分块写出、块间带 60ms 间隔的正文。</summary>
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

    private sealed class ThrowingCredentialProvider : IApiCredentialProvider
    {
        public ValueTask<ApiCredential> GetCredentialAsync(ApiAuthContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("凭据源不可用（模拟）。");
    }

    private sealed class StaticCredentialProvider : IApiCredentialProvider
    {
        private readonly string? _token;

        public StaticCredentialProvider(string? token) => _token = token;

        public ValueTask<ApiCredential> GetCredentialAsync(ApiAuthContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(ApiCredential.Token(_token));
    }

    private sealed class RecordingCredentialProvider : IApiCredentialProvider
    {
        public int Calls { get; private set; }

        public ValueTask<ApiCredential> GetCredentialAsync(ApiAuthContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(ApiCredential.None);
        }
    }

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
