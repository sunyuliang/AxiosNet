using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using System.Text;
using System.Text.Json;

namespace AxiosNet.Tests.WebAPI;

/// <summary>
/// 接口级声明的返回形态：`Task&lt;AxiosResponse&lt;T&gt;&gt;` / `Task&lt;AxiosResponse&gt;` / `Task&lt;T&gt;` / `Task`。
/// </summary>
public sealed class InterfaceReturnShapeTests
{
    private static ApiClient CreateClient(TestHttpServer server) => new(server.Url);

    [Fact]
    public async Task DataShape_ReturnsBodyDirectly()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 18)));
        using var client = CreateClient(server);

        var person = await client.Create<IReturnShapeApi>().GetPersonAsync(5);

        Assert.Equal("alice", person!.Name);
        Assert.Equal(18, person.Age);
        Assert.Equal("/people/5", Assert.Single(server.Received).Path);
    }

    [Fact]
    public async Task DataShape_ErrorStatus_ThrowsAxiosException()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "nope" }, statusCode: 400));
        using var client = CreateClient(server);

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.Create<IReturnShapeApi>().GetPersonAsync(5));

        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);
        Assert.Equal(400, ex.Status);
    }

    [Fact]
    public async Task DataShape_ErrorFactory_StillApplies()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(
            new { code = "E42", message = "broken" },
            statusCode: 400));
        using var client = CreateClient(server);
        client.ErrorFactory = new ApiErrorFactory<ShapeProblem>();

        var error = await Assert.ThrowsAsync<ApiError<ShapeProblem>>(
            () => client.Create<IReturnShapeApi>().GetPersonAsync(5));

        Assert.Equal("E42", error.Error?.Code);
    }

    [Fact]
    public async Task DataShape_TextPayload_IsReturnedAsString()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes("pong"), ContentType: "text/plain"));
        using var client = CreateClient(server);

        Assert.Equal("pong", await client.Create<IReturnShapeApi>().PingTextAsync());
    }

    [Fact]
    public async Task UnannotatedAxiosResponse_ReturnsRawElement()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 18)));
        using var client = CreateClient(server);

        var response = await client.Create<IReturnShapeApi>().GetRawAsync(5);

        Assert.Equal(200, response.Status);
        var body = Assert.IsType<JsonElement>(response.Data);
        Assert.Equal("alice", body.GetProperty("name").GetString());
    }

    [Fact]
    public async Task VoidShape_SendsRequestAndDiscardsBody()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(204));
        using var client = CreateClient(server);

        await client.Create<IReturnShapeApi>().DeleteAsync(7);

        var received = Assert.Single(server.Received);
        Assert.Equal("DELETE", received.Method);
        Assert.Equal("/people/7", received.Path);
    }

    [Fact]
    public async Task VoidShape_IgnoresUnparsableBody()
    {
        // Task 方法不装配正文：服务端回什么都不会变成反序列化错误。
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes("<html>ok</html>"), ContentType: "text/html"));
        using var client = CreateClient(server);

        await client.Create<IReturnShapeApi>().DeleteAsync(7);
    }

    [Fact]
    public async Task VoidShape_ErrorStatus_ThrowsAxiosException()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "nope" }, statusCode: 500));
        using var client = CreateClient(server);

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.Create<IReturnShapeApi>().DeleteAsync(7));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
        Assert.Equal(500, ex.Status);
    }

    [Fact]
    public async Task VoidShape_ErrorFactory_StillSeesResponse()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(
            new { code = "E42", message = "broken" },
            statusCode: 400));
        using var client = CreateClient(server);
        client.ErrorFactory = new ApiErrorFactory<ShapeProblem>();

        // Task 形态只是不交出正文，错误仍是带响应的 ApiError<TError>。
        var error = await Assert.ThrowsAsync<ApiError<ShapeProblem>>(() => client.Create<IReturnShapeApi>().DeleteAsync(7));

        Assert.NotNull(error.Response);
        Assert.Equal(400, error.Response.Status);
        Assert.Equal("E42", error.Error?.Code);
    }

    [Fact]
    public async Task FaultedShape_BindingFailureSurfacesAsFaultedTask()
    {
        // 绑定阶段抛错：Task<T> / Task 都必须以「已失败的 Task」交出，而不是同步抛。
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = CreateClient(server);
        var api = client.Create<IReturnShapeApi>();

        // 方法本身不抛（拿到的是 Task 而不是同步异常），失败落在 Task 上。
        var dataTask = api.GetPersonBySlugAsync(null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => dataTask);

        await Assert.ThrowsAsync<InvalidOperationException>(() => api.DeleteBySlugAsync(null));

        Assert.Empty(server.Received);
    }

    // ------------------------------------------------------------------
    // 返回形态的启动期校验
    // ------------------------------------------------------------------

    [Fact]
    public void Validation_RejectsSyncAndValueTask_WithReadableTypeName()
    {
        var sync = ApiClient.ValidateInterfaces(typeof(ISyncReturnApi));
        var valueTask = ApiClient.ValidateInterfaces(typeof(IValueTaskReturnApi));

        Assert.Contains(sync, error => error.Contains("不支持同步返回 / ValueTask"));
        Assert.Contains(valueTask, error => error.Contains("ValueTask<AxiosResponse<Person>>"));
    }

    [Fact]
    public void Validation_RejectsBareAsyncEnumerable_BeforeRuntime()
    {
        var errors = ApiClient.ValidateInterfaces(typeof(IBareEnumerableApi));

        Assert.Single(errors);
        Assert.Contains("Task<AxiosResponse<IAsyncEnumerable<T>>>", errors[0]);
    }

    [Fact]
    public void Validation_AcceptsEverySupportedShape()
    {
        Assert.Empty(ApiClient.ValidateInterfaces(typeof(IReturnShapeApi)));
    }
}

/// <summary>强类型错误的载荷。</summary>
public sealed class ShapeProblem
{
    public string? Code { get; set; }

    public string? Message { get; set; }
}

/// <summary>四种支持的返回形态各一个方法。</summary>
public interface IReturnShapeApi
{
    [Get("people/{id}")]
    Task<Person> GetPersonAsync([Path] int id);

    [Get("people/{slug}")]
    Task<Person> GetPersonBySlugAsync([Path] string? slug);

    [Get("raw/{id}")]
    Task<AxiosResponse> GetRawAsync([Path] int id);

    [Get("ping")]
    Task<string> PingTextAsync();

    [Delete("people/{id}")]
    Task DeleteAsync([Path] int id);

    [Delete("people/{slug}")]
    Task DeleteBySlugAsync([Path] string? slug);
}

/// <summary>同步返回：不支持。</summary>
public interface ISyncReturnApi
{
    [Get("people/{id}")]
    Person GetPerson([Path] int id);
}

/// <summary>ValueTask：不支持（接口代理统一走 Task）。</summary>
public interface IValueTaskReturnApi
{
    [Get("people/{id}")]
    ValueTask<AxiosResponse<Person>> GetPersonAsync([Path] int id);
}

/// <summary>裸 IAsyncEnumerable 缺少响应元数据：应声明 Task&lt;AxiosResponse&lt;IAsyncEnumerable&lt;T&gt;&gt;&gt;。</summary>
public interface IBareEnumerableApi
{
    [Get("stream")]
    Task<IAsyncEnumerable<Person>> StreamAsync();
}
