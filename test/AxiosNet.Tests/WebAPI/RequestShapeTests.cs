using System.Text;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Attributes;

namespace AxiosNet.Tests.WebAPI;

/// <summary>声明式的两种返回形态：<c>RequestAsync</c> 只交付正文，<c>RequestFullAsync</c> 交付完整响应。</summary>
public sealed class RequestShapeTests
{
    [Fact]
    public async Task RequestAsync_ReturnsDataWithoutUnwrapping()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 18)));
        using var client = new ApiClient(server.Url);

        var user = await client.RequestAsync(new GetPersonRequest { Id = 5 });

        Assert.Equal("alice", user.Name);
        Assert.Equal(18, user.Age);
        Assert.Equal("/persons/5", Assert.Single(server.Received).Path);
    }

    [Fact]
    public async Task RequestAsync_ConfigureOverload_AppliesToTheSameRequest()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 18)));
        using var client = new ApiClient(server.Url);

        var user = await client.RequestAsync(
            new GetPersonRequest { Id = 5 },
            config => config.Headers.Set("X-Trace", "shape"));

        Assert.Equal("alice", user.Name);
        Assert.Equal("shape", Assert.Single(server.Received).Headers["x-trace"]);
    }

    [Fact]
    public async Task RequestAsync_OnBareClient_ReusesInstanceDefaults()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 18)));
        using var http = Axios.Create(server.Url, config => config.Headers.Set("X-Default", "yes"));

        var user = await http.RequestAsync(new GetPersonRequest { Id = 5 });

        Assert.Equal("alice", user.Name);
        Assert.Equal("yes", Assert.Single(server.Received).Headers["x-default"]);
    }

    [Fact]
    public async Task RequestAsync_EmptyBody_ReturnsDefault()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(204, "No Content"));
        using var client = new ApiClient(server.Url);

        var user = await client.RequestAsync(new GetPersonRequest { Id = 5 });

        Assert.Null(user);
    }

    [Fact]
    public async Task RequestAsync_ValueTypeBody_IsDeliveredAsIs()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(200, "OK", Encoding.UTF8.GetBytes("7")));
        using var client = new ApiClient(server.Url);

        var count = await client.RequestAsync(new CountRequest());

        Assert.Equal(7, count);
    }

    [Fact]
    public async Task RequestFullAsync_ReturnsResponseMetadata()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(201, "Created", Encoding.UTF8.GetBytes("""{"name":"alice","age":18}""")));
        using var client = new ApiClient(server.Url);

        var response = await client.RequestFullAsync(new GetPersonRequest { Id = 5 });

        Assert.Equal(201, response.Status);
        Assert.Equal("alice", response.Data!.Name);
        Assert.Equal("/persons/5", response.Request!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task RequestAsync_BrokenModel_ThrowsAxiosException()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 18)));
        using var client = new ApiClient(server.Url);

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.RequestAsync(new UnboundPathRequest()));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Get("persons/{id}")]
    private sealed class GetPersonRequest : IApiRequest<Person>
    {
        [Path]
        public int Id { get; set; }
    }

    [Get("counts")]
    private sealed class CountRequest : IApiRequest<int>
    {
    }

    [Get("persons/{id}")]
    private sealed class UnboundPathRequest : IApiRequest<Person>
    {
    }
}
