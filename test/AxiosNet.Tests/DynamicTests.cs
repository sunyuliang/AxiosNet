using System.Dynamic;
using System.Text;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>dynamic 类型支持：响应动态展开、dynamic 作为 data / params、GetAsync&lt;dynamic&gt; 调用形式。</summary>
public sealed class DynamicTests
{
    private const string UserJson =
        """
        {
          "name": "alice",
          "age": 18,
          "active": true,
          "score": 95.5,
          "address": { "city": "Shanghai", "zip": "200000" },
          "roles": ["admin", "user"],
          "tags": [ { "id": 1, "label": "vip" } ],
          "nickname": null
        }
        """;

    [Fact]
    public async Task GetAsync_OfDynamic_ExpandoGraph_SupportsMemberAndIndexAndArrayAccess()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes(UserJson)));
        using var client = new AxiosClient();

        var response = await client.GetAsync<dynamic>(server.Url + "users/1");

        Assert.Equal(200, response.Status);
        dynamic data = response.Data!;

        Assert.Equal("alice", (string)data.name);
        Assert.Equal(18L, (long)data.age);
        Assert.True((bool)data.active);
        Assert.Equal(95.5d, (double)data.score, 2);

        Assert.Equal("Shanghai", (string)data.address.city);
        Assert.Equal("200000", (string)data.address.zip);

        Assert.Equal("admin", (string)data.roles[0]);
        Assert.Equal("user", (string)data.roles[1]);
        Assert.Equal(1L, (long)data.tags[0].id);
        Assert.Equal("vip", (string)data.tags[0].label);

        Assert.Null(data.nickname);

        Assert.Equal("alice", (string)data["name"]);

        object? rawData = response.Data;
        var asDictionary = Assert.IsAssignableFrom<IDictionary<string, object?>>(rawData);
        Assert.Equal("alice", asDictionary["name"]);

        Assert.IsType<AxiosDynamic>(rawData);
        Assert.IsType<List<object?>>((object?)data.roles);
    }

    [Fact]
    public async Task GetAsync_OfDynamic_RootArray_WrapsNestedObjectsAsAxiosDynamic()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes("""[{"id":1},{"id":2}]""")));
        using var client = new AxiosClient();

        var response = await client.GetAsync<dynamic>(server.Url);
        dynamic list = response.Data!;

        Assert.IsType<List<object?>>((object?)response.Data);
        Assert.Equal(2, list.Count);
        Assert.Equal(1L, (long)list[0].id);
        Assert.Equal(2L, (long)list[1].id);
    }

    [Fact]
    public async Task Dynamic_PrimitivesAndNull_AreMappedToClrTypes()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes("""{"i":42,"f":1.25,"b":false,"s":"x","n":null}""")));
        using var client = new AxiosClient();

        dynamic d = (await client.GetAsync<dynamic>(server.Url)).Data!;

        Assert.Equal(42L, (long)d.i);
        Assert.IsType<long>(d.i);
        Assert.Equal(1.25d, (double)d.f, 2);
        Assert.False((bool)d.b);
        Assert.Equal("x", (string)d.s);
        Assert.Null(d.n);
    }

    [Fact]
    public async Task ResponseType_Dynamic_SetOnConfig_WorksWithNonGenericRequest()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes("""{"ok":true}""")));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url, c => c.ResponseType = ResponseType.Dynamic);

        dynamic d = response.Data!;
        Assert.True((bool)d.ok);
        Assert.IsType<AxiosDynamic>((object?)response.Data);
    }

    [Fact]
    public async Task GetAsync_OfDynamic_ExplicitTextResponseType_IsRespected()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes("""{"ok":true}"""), ContentType: "text/plain"));
        using var client = new AxiosClient();

        // <dynamic> 默认走动态展开，但显式 ResponseType.Text 优先，返回原始字符串。
        var response = await client.GetAsync<dynamic>(server.Url, c => c.ResponseType = ResponseType.Text);

        Assert.IsType<string>(response.Data);
        Assert.Equal("""{"ok":true}""", (string)response.Data!);
    }

    [Fact]
    public async Task Dynamic_RequestBody_ExpandoObject_IsSerializedAsJson()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        dynamic payload = new ExpandoObject();
        payload.name = "bob";
        payload.age = 30;
        dynamic address = new ExpandoObject();
        address.city = "Beijing";
        payload.address = address;
        payload.roles = new[] { "dev", "lead" };

        var response = await client.PostAsync<dynamic>(server.Url + "echo", payload);

        var sentRaw = Encoding.UTF8.GetString(server.Received[0].Body);
        Assert.StartsWith("application/json", server.Received[0].Headers.GetValueOrDefault("content-type"));
        Assert.Contains("\"name\":\"bob\"", sentRaw);
        Assert.Contains("\"address\":{\"city\":\"Beijing\"}", sentRaw);
        Assert.Contains("\"roles\":[\"dev\",\"lead\"]", sentRaw);

        dynamic data = response.Data!;
        Assert.Equal("bob", (string)data.name);
        Assert.Equal("Beijing", (string)data.address.city);
    }

    [Fact]
    public async Task Dynamic_Params_ExpandoObject_AreSerializedToQuery()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        dynamic query = new ExpandoObject();
        query.page = 2;
        query.keyword = "a b";
        dynamic filter = new ExpandoObject();
        filter.kind = "book";
        query.filter = filter;
        query.ids = new[] { 1, 2 };

        await client.GetAsync<dynamic>(server.Url + "search", c => c.Params = query);

        var actualQuery = server.Received[0].Query;
        Assert.Contains("page=2", actualQuery);
        Assert.Contains("keyword=a%20b", actualQuery);
        Assert.Contains("filter[kind]=book", actualQuery);
        Assert.Contains("ids[]=1", actualQuery);
        Assert.Contains("ids[]=2", actualQuery);
    }

    [Fact]
    public async Task StaticAxios_GetAsync_OfDynamic_WorksLikeFacade()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes("""{"hello":"world"}""")));

        dynamic data = (await Axios.GetAsync<dynamic>(server.Url)).Data!;

        Assert.Equal("world", (string)data.hello);
    }

    [Fact]
    public async Task GetAsync_OfDynamic_EmptyBody_ReturnsNullData()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(204, "No Content"));
        using var client = new AxiosClient();

        var response = await client.GetAsync<dynamic>(server.Url);

        Assert.Null(response.Data);
    }
}

