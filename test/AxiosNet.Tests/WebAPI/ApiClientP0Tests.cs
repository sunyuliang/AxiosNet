using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;

namespace AxiosNet.Tests.WebAPI;

/// <summary>
/// P0 对标 Refit 的能力：类级静态头 / 字典头 / 查询映射平铺 / CollectionFormat /
/// URL 参数格式化 / 错误工厂 / 模型启动期校验。
/// </summary>
public sealed class ApiClientP0Tests
{
    // ------------------------------------------------------------------
    // 类级静态头 + 字典头
    // ------------------------------------------------------------------

    [Fact]
    public async Task StaticHeaders_AreSent_PropertyHeaderOverridesSameName()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new StaticHeaderRequest { Trace = "dynamic" });

        var received = Assert.Single(server.Received);
        Assert.Equal("application/json", received.Headers["Accept"]);
        Assert.Equal("2", received.Headers["X-Api-Version"]);
        // 属性上的 [Header] 覆盖类级同名静态头
        Assert.Equal("dynamic", received.Headers["X-Trace"]);
    }

    [Fact]
    public async Task StaticHeaders_NameOnly_RemovesDefaultHeader()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url, defaults => defaults.Headers.Set("X-Remove", "old"));

        await client.RequestAsync(new RemoveHeaderRequest());

        var received = Assert.Single(server.Received);
        Assert.False(received.Headers.ContainsKey("X-Remove"));
    }

    [Fact]
    public async Task HeaderCollection_WritesEachEntry_NullValueSkipped()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new HeaderCollectionRequest
        {
            Extra = new Dictionary<string, string?>
            {
                ["X-A"] = "1",
                ["X-B"] = null
            }
        });

        var received = Assert.Single(server.Received);
        Assert.Equal("1", received.Headers["X-A"]);
        Assert.False(received.Headers.ContainsKey("X-B"));
    }

    [Fact]
    public void HeaderCollection_NonStringDictionary_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ApiClient("http://127.0.0.1:1/").GetUri(new BadHeaderCollectionRequest()));

        Assert.Contains("[HeaderCollection]", ex.Message);
        Assert.Contains("IDictionary<string, string>", ex.Message);
    }

    [Fact]
    public void HeaderAndHeaderCollection_OnSameProperty_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ApiClient("http://127.0.0.1:1/").GetUri(new ConflictingHeaderRequest()));

        Assert.Contains("二义性", ex.Message);
    }

    // ------------------------------------------------------------------
    // 查询映射平铺 + CollectionFormat
    // ------------------------------------------------------------------

    [Fact]
    public async Task QueryMap_FlattensDictionaryEntries_IntoTopLevelKeys()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new SearchMapRequest
        {
            Filters = new Dictionary<string, object?> { ["role"] = "admin", ["page"] = 2 }
        });

        var query = Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty);
        Assert.Contains("role=admin", query);
        Assert.Contains("page=2", query);
        Assert.DoesNotContain("Filters", query);
    }

    [Fact]
    public async Task QueryMap_AllNullValues_SendsNoQueryString()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new SearchMapRequest
        {
            Filters = new Dictionary<string, object?> { ["role"] = null }
        });

        Assert.Null(Assert.Single(server.Received).Query);
    }

    [Fact]
    public async Task QueryMap_ExplicitName_KeepsNestedForm()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new NamedMapRequest
        {
            Filters = new Dictionary<string, object?> { ["role"] = "admin" }
        });

        var query = Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty);
        Assert.Contains("filters[role]=admin", query);
    }

    [Fact]
    public async Task CollectionFormat_PerRequest_JoinsScalars()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new[] { new Person("alice", 1) }));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(
            new ListUsersRequest { Ids = [1, 2], Role = null },
            config => config.Serialization.CollectionFormat = AxiosCollectionFormat.Csv);

        var query = Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty);
        Assert.Contains("Ids=1,2", query);
    }

    [Fact]
    public async Task CollectionFormat_FromInstanceDefaults_RepeatsKey()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new[] { new Person("alice", 1) }));
        using var client = new ApiClient(server.Url,
            defaults => defaults.Serialization.CollectionFormat = AxiosCollectionFormat.Multi);

        await client.RequestAsync(new ListUsersRequest { Ids = [1, 2], Role = null });

        var query = Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty);
        Assert.Contains("Ids=1", query);
        Assert.Contains("Ids=2", query);
        Assert.DoesNotContain("Ids[]", query);
    }

    [Fact]
    public async Task CollectionFormat_CoreParams_HonorsPipesAndSsv()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        await client.GetAsync<Person>("items", config =>
        {
            config.Params = new { tags = new[] { "a", "b" } };
            config.Serialization.CollectionFormat = AxiosCollectionFormat.Pipes;
        });

        Assert.Equal("tags=a|b", Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty));

        await client.GetAsync<Person>("items", config =>
        {
            config.Params = new { tags = new[] { "a", "b" } };
            config.Serialization.CollectionFormat = AxiosCollectionFormat.Ssv;
        });

        Assert.Equal("tags=a b", Uri.UnescapeDataString(server.Received[1].Query ?? string.Empty));
    }

    // ------------------------------------------------------------------
    // URL 参数格式化钩子
    // ------------------------------------------------------------------

    [Fact]
    public async Task UrlParameterFormatter_AppliesToPathAndQuery()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url)
        {
            UrlParameterFormatter = value => value switch
            {
                int id => "p" + id,
                DateTime date => date.ToString("yyyyMMdd"),
                _ => null // 其余值走默认规则
            }
        };

        await client.RequestAsync(new FormattedRequest
        {
            Id = 7,
            When = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc)
        });

        var received = Assert.Single(server.Received);
        Assert.Equal("/fmt/p7", received.Path);
        Assert.Equal("When=20240102", Uri.UnescapeDataString(received.Query ?? string.Empty));
    }

    // ------------------------------------------------------------------
    // 错误工厂 / 强类型错误
    // ------------------------------------------------------------------

    [Fact]
    public async Task ErrorFactory_ProducesTypedApiError_FromErrorBody()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(
            new { code = "E42", message = "field invalid" },
            statusCode: 400));
        using var client = new ApiClient(server.Url)
        {
            ErrorFactory = new ApiErrorFactory<ApiProblem>()
        };

        var error = await Assert.ThrowsAsync<ApiError<ApiProblem>>(
            () => client.RequestAsync(new FailRequest()));

        Assert.Equal(400, error.Status);
        Assert.Equal("Bad Request", error.StatusText);
        Assert.Equal("E42", error.Error?.Code);
        Assert.Equal("field invalid", error.Error?.Message);
        Assert.Equal(AxiosException.ErrBadRequest, error.Code);
        Assert.IsType<AxiosException>(error.InnerException);
        Assert.Equal(400, error.Response.Status);
    }

    [Fact]
    public async Task ErrorFactory_ReturningNull_KeepsAxiosException()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new { code = "E1" }, statusCode: 500));
        using var client = new ApiClient(server.Url)
        {
            ErrorFactory = new NoopErrorFactory()
        };

        var error = await Assert.ThrowsAsync<AxiosException>(
            () => client.RequestAsync(new FailRequest()));

        Assert.Equal(AxiosException.ErrBadResponse, error.Code);
        Assert.Equal(500, error.Status);
    }

    [Fact]
    public async Task ErrorFactory_NotInvokedForNetworkErrors()
    {
        var factory = new RecordingErrorFactory();
        using var client = new ApiClient("http://127.0.0.1:1/") { ErrorFactory = factory };

        var error = await Assert.ThrowsAsync<AxiosException>(
            () => client.RequestAsync(new FailRequest()));

        Assert.Equal(AxiosException.ErrNetwork, error.Code);
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public async Task ErrorContext_Body_KeepsRawText_EvenWhenDeclaredTypeParses()
    {
        // 错误体恰好能被声明类型解析：RawText 仍然可用（Data 已是对象，原文不能被消费掉）
        await using var server = TestHttpServer.Start(_ => TestJson.Response(
            new Person("error-body", 400),
            statusCode: 400));
        using var client = new ApiClient(server.Url) { ErrorFactory = new CapturingErrorFactory() };

        await Assert.ThrowsAsync<ApiError<Person>>(
            () => client.RequestAsync(new FailPersonRequest()));

        Assert.NotNull(CapturingErrorFactory.LastBody);
        Assert.Contains("error-body", CapturingErrorFactory.LastBody!);
    }

    // ------------------------------------------------------------------
    // 模型启动期校验
    // ------------------------------------------------------------------

    [Fact]
    public void ValidateModels_ValidModel_ReturnsEmpty()
    {
        Assert.Empty(ApiClient.ValidateModels(typeof(GetUserRequest)));
    }

    [Fact]
    public void ValidateModels_CollectsAllErrors_WithoutThrowing()
    {
        var errors = ApiClient.ValidateModels(
            typeof(GetUserRequest),
            typeof(NoRouteModel),
            typeof(HeaderCollectionRequest),
            typeof(object));

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, message => message.Contains("NoRouteModel") && message.Contains("路由特性"));
        Assert.Contains(errors, message => message.Contains("System.Object"));
    }

    [Fact]
    public void ValidateModels_AssemblyScan_IncludesNestedModels()
    {
        var errors = ApiClient.ValidateModels(typeof(ApiClientP0Tests).Assembly);

        Assert.Contains(errors, message => message.Contains(nameof(NoRouteModel)));
    }

    // ------------------------------------------------------------------
    // 测试模型
    // ------------------------------------------------------------------

    [Get("static")]
    [Headers("Accept: application/json", "X-Api-Version: 2", "X-Trace: static")]
    private sealed class StaticHeaderRequest : IApiRequest<Person>
    {
        [Header("X-Trace")]
        public string? Trace { get; set; }
    }

    [Get("static")]
    [Headers("X-Remove")]
    private sealed class RemoveHeaderRequest : IApiRequest<Person>
    {
    }

    [Get("headers")]
    private sealed class HeaderCollectionRequest : IApiRequest<Person>
    {
        [HeaderCollection]
        public Dictionary<string, string?> Extra { get; set; } = new();
    }

    [Get("headers")]
    private sealed class BadHeaderCollectionRequest : IApiRequest<Person>
    {
        [HeaderCollection]
        public Dictionary<string, int> Extra { get; set; } = new();
    }

    [Get("headers")]
    private sealed class ConflictingHeaderRequest : IApiRequest<Person>
    {
        [Header("X-A")]
        [HeaderCollection]
        public Dictionary<string, string?> Extra { get; set; } = new();
    }

    [Get("search")]
    private sealed class SearchMapRequest : IApiRequest<Person>
    {
        [Query]
        public Dictionary<string, object?> Filters { get; set; } = new();
    }

    [Get("search")]
    private sealed class NamedMapRequest : IApiRequest<Person>
    {
        [Query("filters")]
        public Dictionary<string, object?> Filters { get; set; } = new();
    }

    [Get("fmt/{id}")]
    private sealed class FormattedRequest : IApiRequest<Person>
    {
        [Path]
        public int Id { get; set; }

        [Query]
        public DateTime When { get; set; }
    }

    [Get("fail")]
    private sealed class FailRequest : IApiRequest<Person>
    {
    }

    [Get("fail")]
    private sealed class FailPersonRequest : IApiRequest<Person>
    {
    }

    private sealed class NoRouteModel : IApiRequest<object>
    {
        public string? Name { get; set; }
    }

    private sealed class ApiProblem
    {
        public string? Code { get; set; }

        public string? Message { get; set; }
    }

    private sealed class NoopErrorFactory : IApiErrorFactory
    {
        public ValueTask<Exception?> CreateAsync(ApiErrorContext context, CancellationToken cancellationToken)
            => new((Exception?)null);
    }

    private sealed class RecordingErrorFactory : IApiErrorFactory
    {
        public int Calls { get; private set; }

        public ValueTask<Exception?> CreateAsync(ApiErrorContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return new ValueTask<Exception?>((Exception?)null);
        }
    }

    private sealed class CapturingErrorFactory : IApiErrorFactory
    {
        public static string? LastBody { get; private set; }

        public async ValueTask<Exception?> CreateAsync(ApiErrorContext context, CancellationToken cancellationToken)
        {
            LastBody = context.Body;
            var error = await context.TryReadAsync<Person>(cancellationToken);
            return new ApiError<Person>(error, context);
        }
    }
}
