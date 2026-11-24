using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Attributes;
using AxiosNet.WebAPI.Metadata;
using System.Text;
using System.Text.Json;

namespace AxiosNet.Tests.WebAPI;

/// <summary>
/// 整份正文快路（<c>InterfaceMethodRuntime.SendRootBody*</c>）：四种返回形态、回退到通用路径与装配失败的错误码。
/// 声明用手写注册，形状与生成代理写出的注册一致。
/// </summary>
[Collection(GlobalSerializationCollection.Name)]
public sealed class RootBodyFastPathTests
{
    static RootBodyFastPathTests()
    {
        AxiosApiMetadata.RegisterInterface(typeof(IFastBodyApi), NoProxy, static builder => builder
            .Method(nameof(IFastBodyApi.PostAsync), HttpMethod.Post, "fast/body")
                .Parameter(0, "payload", typeof(Person))
                    .Body()
                .CancellationToken(1)
                .ReturnsResponse(typeof(Person))
            .Method(nameof(IFastBodyApi.DataAsync), HttpMethod.Post, "fast/data")
                .Parameter(0, "payload", typeof(Person))
                    .Body()
                .CancellationToken(1)
                .ReturnsData(typeof(Person))
            .Method(nameof(IFastBodyApi.RawAsync), HttpMethod.Post, "fast/raw")
                .Parameter(0, "payload", typeof(Person))
                    .Body()
                .CancellationToken(1)
                .ReturnsResponse()
            .Method(nameof(IFastBodyApi.VoidAsync), HttpMethod.Delete, "fast/void")
                .Parameter(0, "payload", typeof(Person))
                    .Body()
                .CancellationToken(1)
                .ReturnsVoid()
            .Method(nameof(IFastBodyApi.GetAsync), HttpMethod.Get, "fast/none")
                .CancellationToken(0)
                .ReturnsResponse(typeof(Person))
            .Method(nameof(IFastBodyApi.IgnoredAsync), HttpMethod.Post, "fast/ignored")
                .Parameter(0, "payload", typeof(Person))
                    .Body()
                .Parameter(1, "trace", typeof(string))
                    .Ignore()
                .CancellationToken(2)
                .ReturnsResponse(typeof(Person)));

        // 接口级静态头让整份正文快路失效：头只能由绑定计划写进配置。
        AxiosApiMetadata.RegisterInterface(typeof(IDeclaredHeaderBodyApi), NoProxy, static builder => builder
            .StaticHeaders("X-Declared: 1")
            .Method(nameof(IDeclaredHeaderBodyApi.PostAsync), HttpMethod.Post, "declared/body")
                .Parameter(0, "payload", typeof(Person))
                    .Body()
                .CancellationToken(1)
                .ReturnsResponse(typeof(Person)));

        // 还有别的绑定通道：快路调用无法还原实参，必须显式失败而不是静默丢参数。
        AxiosApiMetadata.RegisterInterface(typeof(IMismatchApi), NoProxy, static builder => builder
            .Method(nameof(IMismatchApi.PostAsync), HttpMethod.Post, "mismatch")
                .Parameter(0, "payload", typeof(Person))
                    .Body()
                .Parameter(1, "q", typeof(string))
                    .Query()
                .CancellationToken(2)
                .ReturnsResponse(typeof(Person)));
    }

    [Fact]
    public async Task RootBody_IsSentAsWholeBody()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        var response = await Runtime(0, nameof(IFastBodyApi.PostAsync))
            .SendRootBodyResponseAsync<Person>(client, new Person("alice", 18), CancellationToken.None);

        Assert.Equal(200, response.Status);
        Assert.Equal("ok", response.Data!.Name);

        var received = Assert.Single(server.Received);
        Assert.Equal("POST", received.Method);
        Assert.Equal("/fast/body", received.Path);
        Assert.Equal("application/json", received.Headers["Content-Type"]);

        // 整份正文不能再像键名聚合那样包一层参数名。
        var json = JsonSerializer.Deserialize<JsonElement>(received.Body);
        Assert.Equal("alice", json.GetProperty("name").GetString());
        Assert.Equal(18, json.GetProperty("age").GetInt32());
        Assert.False(json.TryGetProperty("payload", out _));
    }

    [Fact]
    public async Task RootBody_DataShapeReturnsPayload()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("data", 2)));
        using var client = new ApiClient(server.Url);

        var data = await Runtime(1, nameof(IFastBodyApi.DataAsync))
            .SendRootBodyDataAsync<Person>(client, new Person("alice", 18), CancellationToken.None);

        Assert.Equal("data", data!.Name);

        var received = Assert.Single(server.Received);
        Assert.Equal("alice", JsonSerializer.Deserialize<JsonElement>(received.Body).GetProperty("name").GetString());
    }

    [Fact]
    public async Task RootBody_UntypedShapeKeepsJsonElement()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("raw", 3)));
        using var client = new ApiClient(server.Url);

        var response = await Runtime(2, nameof(IFastBodyApi.RawAsync))
            .SendRootBodyUntypedAsync(client, new Person("alice", 18), CancellationToken.None);

        Assert.Equal("raw", Assert.IsType<JsonElement>(response.Data).GetProperty("name").GetString());
        Assert.Equal("/fast/raw", Assert.Single(server.Received).Path);
    }

    [Fact]
    public async Task RootBody_VoidShapeSendsBodyAndDiscardsResponse()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(204));
        using var client = new ApiClient(server.Url);

        await Runtime(3, nameof(IFastBodyApi.VoidAsync))
            .SendRootBodyVoidAsync(client, new Person("alice", 18), CancellationToken.None);

        var received = Assert.Single(server.Received);
        Assert.Equal("DELETE", received.Method);
        Assert.Equal("alice", JsonSerializer.Deserialize<JsonElement>(received.Body).GetProperty("name").GetString());
    }

    [Fact]
    public async Task NoBody_LeavesRequestWithoutContent()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ping", 4)));
        using var client = new ApiClient(server.Url);

        var response = await Runtime(4, nameof(IFastBodyApi.GetAsync))
            .SendRootBodyResponseAsync<Person>(client, rootBody: null, CancellationToken.None);

        Assert.Equal("ping", response.Data!.Name);

        var received = Assert.Single(server.Received);
        Assert.Equal("GET", received.Method);
        Assert.Empty(received.Body);
    }

    /// <summary>
    /// 被忽略的参数不参与绑定，但它的实参仍按原值进入调用载荷（凭据提供方通过 <see cref="ApiInterfaceInvocation"/> 读取）。
    /// 快路只传正文与取消令牌，还原不出这个位置，因此这种形状走通用路径。
    /// </summary>
    [Fact]
    public async Task IgnoredParameter_StaysOutOfBodyButInPayload()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 5)));
        var provider = new CapturingTokenProvider();
        using var client = new ApiClient(server.Url) { CredentialProvider = provider };

        await Runtime(5, nameof(IFastBodyApi.IgnoredAsync))
            .SendResponseAsync<Person>(
                client,
                [new Person("alice", 18), "trace", CancellationToken.None],
                CancellationToken.None);

        var received = Assert.Single(server.Received);
        Assert.Equal("alice", JsonSerializer.Deserialize<JsonElement>(received.Body).GetProperty("name").GetString());
        Assert.False(received.Headers.ContainsKey("trace"));

        var invocation = Assert.IsType<ApiInterfaceInvocation>(provider.Invocation);
        Assert.Equal("trace", invocation.Arguments[1]);
    }

    /// <summary>接口级静态头让快路退回通用路径：头与正文都要发出去，正文仍是整份。</summary>
    [Fact]
    public async Task DeclaredStaticHeader_FallsBackAndStillSendsWholeBody()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 6)));
        using var client = new ApiClient(server.Url);

        await Runtime(0, nameof(IDeclaredHeaderBodyApi.PostAsync), typeof(IDeclaredHeaderBodyApi))
            .SendRootBodyResponseAsync<Person>(client, new Person("alice", 18), CancellationToken.None);

        var received = Assert.Single(server.Received);
        Assert.Equal("1", received.Headers["X-Declared"]);
        Assert.Equal("alice", JsonSerializer.Deserialize<JsonElement>(received.Body).GetProperty("name").GetString());
    }

    /// <summary>有凭据提供方时按实参建调用载荷，主体仍是整份正文。</summary>
    [Fact]
    public async Task CredentialProvider_KeepsPayloadAndBody()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 7)));
        using var client = new ApiClient(server.Url)
        {
            CredentialProvider = new FixedTokenProvider()
        };

        await Runtime(0, nameof(IFastBodyApi.PostAsync))
            .SendRootBodyResponseAsync<Person>(client, new Person("alice", 18), CancellationToken.None);

        var received = Assert.Single(server.Received);
        Assert.Equal("Bearer fixed", received.Headers["Authorization"]);
        Assert.Equal("alice", JsonSerializer.Deserialize<JsonElement>(received.Body).GetProperty("name").GetString());
    }

    /// <summary>实参无法还原时显式失败：静默少发一个查询参数比抛错更难发现。</summary>
    [Fact]
    public async Task UnrebuildableDeclaration_ThrowsInsteadOfDroppingArguments()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 8)));
        using var client = new ApiClient(server.Url);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Runtime(0, nameof(IMismatchApi.PostAsync), typeof(IMismatchApi))
                .SendRootBodyResponseAsync<Person>(client, new Person("alice", 18), CancellationToken.None));

        Assert.Contains("版本", error.Message, StringComparison.Ordinal);
        Assert.Empty(server.Received);
    }

    /// <summary>
    /// 快路判定落在声明元数据上：两条路径产出的请求完全一致，只能靠判定值区分「真走快路」与「静默退回通用路径」，
    /// 否则判定退化成恒 <c>false</c> 时上面所有用例照样绿。
    /// </summary>
    [Theory]
    [InlineData(0, nameof(IFastBodyApi.PostAsync))]
    [InlineData(1, nameof(IFastBodyApi.DataAsync))]
    [InlineData(2, nameof(IFastBodyApi.RawAsync))]
    [InlineData(3, nameof(IFastBodyApi.VoidAsync))]
    [InlineData(4, nameof(IFastBodyApi.GetAsync))]
    public void FastShapes_AreJudgedLiteral(int index, string methodName)
    {
        var metadata = Runtime(index, methodName).Metadata;

        Assert.True(metadata.IsLiteralRootBody);
        Assert.True(metadata.IsArgsRebuildable);
    }

    /// <summary>被忽略的参数让快路失效：它的实参要留在调用载荷里，快路还原不出那个位置。</summary>
    [Fact]
    public void IgnoredParameterShape_IsNeitherLiteralNorRebuildable()
    {
        var metadata = Runtime(5, nameof(IFastBodyApi.IgnoredAsync)).Metadata;

        Assert.False(metadata.IsLiteralRootBody);
        Assert.False(metadata.IsArgsRebuildable);
    }

    /// <summary>接口级静态头让快路失效，但实参仍可还原：退回通用路径，不是判据不一致。</summary>
    [Fact]
    public void StaticHeaderShape_IsRebuildableButNotLiteral()
    {
        var metadata = Runtime(0, nameof(IDeclaredHeaderBodyApi.PostAsync), typeof(IDeclaredHeaderBodyApi)).Metadata;

        Assert.False(metadata.IsLiteralRootBody);
        Assert.True(metadata.IsArgsRebuildable);
    }

    /// <summary>另有查询绑定：既不是快路形状，实参也无法只凭正文与令牌还原。</summary>
    [Fact]
    public void ExtraBindingShape_IsNeitherLiteralNorRebuildable()
    {
        var metadata = Runtime(0, nameof(IMismatchApi.PostAsync), typeof(IMismatchApi)).Metadata;

        Assert.False(metadata.IsLiteralRootBody);
        Assert.False(metadata.IsArgsRebuildable);
    }

    /// <summary>
    /// 快路与通用路径必须发出同一份报文（方法 / URL / 头 / 正文）：任何一项不同都说明「快路」是行为变更而不是等价替换。
    /// 头里排除 <c>host</c>（两台测试服务器的端口不同），其余逐个比对。
    /// </summary>
    [Theory]
    [InlineData(0, nameof(IFastBodyApi.PostAsync))]
    [InlineData(1, nameof(IFastBodyApi.DataAsync))]
    [InlineData(2, nameof(IFastBodyApi.RawAsync))]
    [InlineData(3, nameof(IFastBodyApi.VoidAsync))]
    [InlineData(4, nameof(IFastBodyApi.GetAsync))]
    public async Task FastPath_SendsSameRequestAsGeneralPath(int index, string methodName)
    {
        var fast = await Capture(index, methodName, useFastPath: true);
        var slow = await Capture(index, methodName, useFastPath: false);

        Assert.Equal(slow.Method, fast.Method);
        Assert.Equal(slow.RawUrl, fast.RawUrl);
        Assert.Equal(slow.Body, fast.Body);
        Assert.Equal(DescribeHeaders(slow), DescribeHeaders(fast));
    }

    private static async Task<TestRequest> Capture(int index, string methodName, bool useFastPath)
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);
        var runtime = Runtime(index, methodName);
        object? body = methodName == nameof(IFastBodyApi.GetAsync) ? null : new Person("alice", 18);

        if (useFastPath)
        {
            await runtime.SendRootBodyResponseAsync<Person>(client, body, CancellationToken.None);
        }
        else
        {
            // 生成代理在非快路形状下发的就是这个数组（正文 + 取消令牌位置）。
            await runtime.SendResponseAsync<Person>(client, [body, CancellationToken.None], CancellationToken.None);
        }

        return Assert.Single(server.Received);
    }

    private static string DescribeHeaders(TestRequest request)
        => string.Join(
            "\n",
            request.Headers
                .Where(header => !string.Equals(header.Key, "host", StringComparison.OrdinalIgnoreCase))
                .OrderBy(header => header.Key, StringComparer.Ordinal)
                .Select(header => header.Key + ": " + header.Value));

    private static readonly Func<ApiClient, object> NoProxy =
        static _ => throw new NotSupportedException("这些接口只在本类里直接驱动调用桥，不创建代理。");

    private static InterfaceMethodRuntime Runtime(int index, string methodName, Type? interfaceType = null)
        => AxiosApiMetadata.GetInterfaceMethod(interfaceType ?? typeof(IFastBodyApi), index, methodName);

    private sealed class FixedTokenProvider : IApiCredentialProvider
    {
        public ValueTask<ApiCredential> GetCredentialAsync(ApiAuthContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(ApiCredential.Token("fixed"));
    }

    /// <summary>把本次调用的载荷留下来，供用例检查实参数组。</summary>
    private sealed class CapturingTokenProvider : IApiCredentialProvider
    {
        /// <summary>最近一次请求的调用载荷；没走到凭据链路时为 <c>null</c>。</summary>
        public object? Invocation { get; private set; }

        public ValueTask<ApiCredential> GetCredentialAsync(ApiAuthContext context, CancellationToken cancellationToken)
        {
            Invocation = context.Request;
            return ValueTask.FromResult(ApiCredential.Token("captured"));
        }
    }
}

/// <summary>四种返回形态 + 无正文 + 被忽略参数的接口。</summary>
public interface IFastBodyApi
{
    [Post("fast/body")]
    Task<AxiosResponse<Person>> PostAsync([Body] Person payload, CancellationToken cancellationToken = default);

    [Post("fast/data")]
    Task<Person?> DataAsync([Body] Person payload, CancellationToken cancellationToken = default);

    [Post("fast/raw")]
    Task<AxiosResponse> RawAsync([Body] Person payload, CancellationToken cancellationToken = default);

    [Delete("fast/void")]
    Task VoidAsync([Body] Person payload, CancellationToken cancellationToken = default);

    [Get("fast/none")]
    Task<AxiosResponse<Person>> GetAsync(CancellationToken cancellationToken = default);

    [Post("fast/ignored")]
    Task<AxiosResponse<Person>> IgnoredAsync([Body] Person payload, [ApiIgnore] string? trace, CancellationToken cancellationToken = default);
}

/// <summary>接口级静态头的接口：头与整份正文都要发出去。</summary>
[Headers("X-Declared: 1")]
public interface IDeclaredHeaderBodyApi
{
    [Post("declared/body")]
    Task<AxiosResponse<Person>> PostAsync([Body] Person payload, CancellationToken cancellationToken = default);
}

/// <summary>除整份正文外还有一个查询参数的接口。</summary>
public interface IMismatchApi
{
    [Post("mismatch")]
    Task<AxiosResponse<Person>> PostAsync([Body] Person payload, [Query("q")] string? q, CancellationToken cancellationToken = default);
}
