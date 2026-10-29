
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;
using System.Dynamic;
using System.Text.Json;
using System.Text;

namespace AxiosNet.Tests.Requests;

/// <summary>
/// `dynamic` 响应：成员/索引/缺省字段、混合 shape、数组与嵌套图、回填为 params 与请求体。
/// </summary>
public sealed class DynamicTests
{
    [Fact]
    public void Member_Index_AndMissingKey_Agree()
    {
        dynamic data = new AxiosDynamic();
        data.name = "alice";
        data.age = 18L;

        Assert.Equal("alice", (string)data.name);
        Assert.Equal("alice", (string)data["name"]);
        Assert.Equal("alice", (string)ReadName(data));
        Assert.Equal("alice", (string)ReadIndex(data, "name")!);

        // 缺省成员：成员访问与索引访问都返回 null，不抛
        Assert.Null(data.missing);
        Assert.Null(data["missing"]);
        Assert.Null(ReadName(new AxiosDynamic()));
    }

    [Fact]
    public void MissingMember_ChainedAccess_Throws()
    {
        dynamic data = new AxiosDynamic();
        data.address = new AxiosDynamic();

        // 只有第一层是 null 安全的：null 上继续取成员仍是运行时绑定错误
        Assert.Null(data.address.city);
        Assert.Throws<Microsoft.CSharp.RuntimeBinder.RuntimeBinderException>(() => data.notThere.city);
    }

    [Fact]
    public void Keys_IsSnapshot_MutationDoesNotLeak()
    {
        dynamic firstDynamic = new AxiosDynamic();
        firstDynamic.a = 1;
        firstDynamic.b = 2;

        dynamic secondDynamic = new AxiosDynamic();
        secondDynamic.a = 1;
        secondDynamic.b = 2;

        var first = (AxiosDynamic)firstDynamic;
        var second = (AxiosDynamic)secondDynamic;

        // 键集合是快照（定长数组）：改不动，也不会波及共享形状（否则同形状的实例会一起少字段）
        var keys = first.Keys;
        Assert.Throws<NotSupportedException>(() => keys.Remove("a"));

        Assert.Equal(new[] { "a", "b" }, first.Keys);
        Assert.Equal(new[] { "a", "b" }, second.Keys);
    }

    [Fact]
    public void AddPropertyAfterRead_IsVisibleOnNextRead()
    {
        dynamic data = new AxiosDynamic();
        data.name = "alice";

        Assert.Equal("alice", (string)ReadName(data));
        Assert.Equal("alice", (string)ReadName(data)); // 同 call site 再读一次：命中缓存规则

        data.extra = 42L; // 形状变化 → 该 call site 为「新形状」再绑一次，值必须仍然正确
        Assert.Equal("alice", (string)ReadName(data));
        Assert.Equal(42L, (long)data.extra);
        Assert.Equal(42L, (long)ReadExtra(data));
    }

    [Fact]
    public void DifferentShapes_ReadThroughSameCallSite_AreIsolated()
    {
        dynamic withExtra = new AxiosDynamic();
        withExtra.name = "a";
        withExtra.extra = 1L;

        dynamic withoutExtra = new AxiosDynamic();
        withoutExtra.name = "b";

        // 同一 call site 读两种形状：各自都要拿到自己的值（槽位不能串）
        Assert.Equal("a", (string)ReadName(withExtra));
        Assert.Equal("b", (string)ReadName(withoutExtra));
        Assert.Equal("a", (string)ReadName(withExtra));
        Assert.Equal(1L, (long)withExtra["extra"]);
        Assert.Null(withoutExtra["extra"]);
    }

    [Fact]
    public void HeterogeneousShapes_AlternatingAtOneCallSite_StayCorrect()
    {
        var values = new List<object>();
        for (var i = 0; i < 64; i++)
        {
            dynamic item = new AxiosDynamic();
            item.name = $"n{i}";
            if (i % 2 == 0)
            {
                item.extra = i;
            }

            values.Add((object)item);
        }

        // 交替形状（异构数组）反复读同一 call site：既要正确，也不能退化成一层层嵌套的重绑
        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < values.Count; i++)
            {
                Assert.Equal($"n{i}", (string)ReadName((dynamic)values[i])!);
            }
        }
    }

    [Fact]
    public void DictionarySurface_BehavesLikeDictionary()
    {
        IDictionary<string, object?> data = new AxiosDynamic();
        data.Add("name", "alice");
        data["age"] = 18L;

        Assert.Equal(2, data.Count);
        Assert.Equal(new[] { "name", "age" }, data.Keys.ToArray());
        Assert.Equal(new object?[] { "alice", 18L }, data.Values.ToArray());
        Assert.False(data.IsReadOnly);
        Assert.True(data.ContainsKey("name"));
        Assert.True(data.TryGetValue("age", out var age));
        Assert.Equal(18L, age);
        Assert.True(data.Contains(new KeyValuePair<string, object?>("name", "alice")));
        Assert.False(data.Contains(new KeyValuePair<string, object?>("name", "bob")));

        Assert.Throws<ArgumentException>(() => data.Add("name", "bob"));

        // 枚举顺序 = 插入顺序
        Assert.Equal(new[] { "name=alice", "age=18" }, data.Select(pair => $"{pair.Key}={pair.Value}").ToArray());

        Assert.True(data.Remove("name"));
        Assert.False(data.Remove("name"));
        Assert.Single(data);
        Assert.Null(data["name"]);

        data.Clear();
        Assert.Empty(data);
    }

    [Fact]
    public void RemovedThenReAddedProperty_UsesFreshSlot()
    {
        dynamic data = new AxiosDynamic();
        data.a = 1L;
        data.b = 2L;

        Assert.Equal(2L, (long)ReadIndex(data, "b"));
        Assert.True(((IDictionary<string, object?>)data).Remove("b"));
        Assert.Null(ReadIndex(data, "b"));

        data.b = 3L;
        Assert.Equal(3L, (long)ReadIndex(data, "b"));
        Assert.Equal(1L, (long)ReadIndex(data, "a"));
    }

    /// <summary>
    /// 枚举中途删除：<b>键集合是快照，值不是</b>。
    /// <para>
    /// 这是刻意的取舍而不是缺陷：shape 走写时复制，但值数组必须原地可写（DLR 把「名字 → 槽位」
    /// 固化成常量下标，数组不能每次写都换新的），所以删除时会把槽位置 <c>null</c> 断开引用
    /// —— 否则被删掉的大对象会被「永不回收的槽位」长期持有。
    /// 于是「已捕获旧 shape 的枚举器」仍会枚举到这个键，但值为 <c>null</c>。
    /// 需要键和值一起定格，请先 <see cref="AxiosDynamic.CopyTo"/>。
    /// </para>
    /// </summary>
    [Fact]
    public void Enumeration_KeyRemovedMidway_KeepsKeyButYieldsNull()
    {
        var node = new AxiosDynamic();
        node["a"] = 1;
        node["b"] = 2;

        using var walker = node.GetEnumerator();
        Assert.True(walker.MoveNext()); // 这一次 MoveNext 捕获 shape / values
        Assert.Equal("a", walker.Current.Key);

        Assert.True(node.Remove("b"));

        Assert.True(walker.MoveNext());
        Assert.Equal("b", walker.Current.Key); // 键来自快照：仍然会被枚举到
        Assert.Null(walker.Current.Value);     // 值不是快照：引用已被断开
        Assert.False(walker.MoveNext());

        // 新一次枚举看到的是新 shape：b 不在了
        Assert.Single(node);
        Assert.Null(node["b"]);
    }

    /// <summary>
    /// 枚举中途清空：<b>换数组的写入对在途枚举器不可见</b>（与「删键可见」相对的另一半）。
    /// <para>
    /// 枚举器在首次 <c>MoveNext</c> 时把 shape 与值数组<b>引用</b>一起捕获，
    /// 而 <see cref="AxiosDynamic.Clear"/> 是「换成一个空数组」而不是「逐个槽位置空」，
    /// 因此它换不掉枚举器手里的那份引用 —— 在途枚举器读到的仍是完整的旧内容，不是一堆 <c>null</c>。
    /// </para>
    /// <para>
    /// 这条语义值得钉住：它决定了「枚举期间清空」不会让调用方拿到半截数据，
    /// 也说明「值不是快照」只对<b>原地写入</b>（改值 / 删除断引用）成立，对换数组不成立。
    /// </para>
    /// </summary>
    [Fact]
    public void Enumeration_ClearMidway_KeepsWholeOldContent()
    {
        var node = new AxiosDynamic();
        node["a"] = 1;
        node["b"] = 2;

        var seen = new List<string>();
        foreach (var pair in node)
        {
            seen.Add($"{pair.Key}={pair.Value}");
            if (seen.Count == 1)
            {
                node.Clear();
            }
        }

        Assert.Equal(new[] { "a=1", "b=2" }, seen); // 键和值都是旧的
        Assert.Empty(node);                          // 新一次枚举看到空 shape
    }

    [Fact]
    public void CountMemberAccess_IsShadowedByDynamicLookup()
    {
        dynamic data = new AxiosDynamic();
        data.name = "alice";

        // 动态成员优先于 CLR 成员：json 里没有 "Count" 字段 → null（要字典语义必须显式转型）
        Assert.Null(data.Count);
        Assert.Single((IDictionary<string, object?>)data);
    }

    [Fact]
    public void Serialization_ProducesPlainJsonObject_AndRoundTrips()
    {
        dynamic data = new AxiosDynamic();
        data.name = "alice";
        data.age = 18L;
        data.address = new AxiosDynamic();
        data.address.city = "Shanghai";
        data.roles = new List<object?> { "admin", "user" };
        data.nickname = null;

        string json = JsonSerializer.Serialize<object>((object)data);

        // 关键：必须是对象字面量，而不是 [{"Key":..,"Value":..}]
        Assert.Equal(
            """{"name":"alice","age":18,"address":{"city":"Shanghai"},"roles":["admin","user"],"nickname":null}""",
            json);

        Dictionary<string, object?> roundTripped = JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!;
        Assert.Equal(new[] { "name", "age", "address", "roles", "nickname" }, roundTripped.Keys.ToArray());
    }

    [Fact]
    public void ConstructorFromDictionary_CopiesEntries()
    {
        var source = new Dictionary<string, object?> { ["name"] = "alice", ["age"] = 18L };
        dynamic copy = new AxiosDynamic(source);

        copy.name = "bob";

        Assert.Equal("bob", (string)copy.name);
        Assert.Equal("alice", (string?)source["name"]); // 只拷贝条目，不与源字典共享实例
    }

    [Fact]
    public async Task ResponseData_CanBeReusedAsParams_AndAsRequestBody()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        dynamic original = new AxiosDynamic();
        original.page = 2L;
        original.keyword = "a b";
        original.filter = new AxiosDynamic();
        original.filter.kind = "book";

        // 1) 当 params：嵌套对象仍按 filter[kind] 展开（依赖 IDictionary<string,object>）
        await client.GetAsync<dynamic>(server.Url + "search", c => c.Params = original);
        var query = server.Received[0].Query;
        Assert.Contains("page=2", query);
        Assert.Contains("keyword=a%20b", query);
        Assert.Contains("filter[kind]=book", query);

        // 2) 当请求体：序列化成 JSON 对象
        // 注意 (object) 强制静态绑定：直接传 dynamic 会让整条 await 表达式也变成动态调用。
        var echoed = await client.PostAsync<object>(server.Url + "echo", (object)original);
        var sent = Encoding.UTF8.GetString(server.Received[1].Body);
        Assert.StartsWith("application/json", server.Received[1].Headers.GetValueOrDefault("content-type"));
        Assert.Contains("\"keyword\":\"a b\"", sent);
        Assert.Contains("\"filter\":{\"kind\":\"book\"}", sent);

        dynamic echoedData = echoed.Data!;
        Assert.Equal("a b", (string)echoedData.keyword);
    }

    [Fact]
    public async Task ResponseGraph_NestedObjects_AreAxiosDynamic_AndArraysAreLists()
    {
        const string json = """{"user":{"name":"alice"},"items":[{"id":1},{"id":2}]}""";
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes(json)));
        using var client = new AxiosClient();

        var response = await client.GetAsync<dynamic>(server.Url);
        object? raw = response.Data;

        var root = Assert.IsType<AxiosDynamic>(raw);
        Assert.IsType<AxiosDynamic>(root["user"]);
        var items = Assert.IsType<List<object?>>(root["items"]);
        Assert.All(items, item => Assert.IsType<AxiosDynamic>(item));

        dynamic data = response.Data!;
        Assert.Equal("alice", (string)data.user.name);
        Assert.Equal(2L, (long)data.items[1].id);
        Assert.Equal(2L, (long)data.items[1]["id"]);
    }

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
    public async Task Dynamic_NonJsonBody_OnErrorStatus_ExposesRawTextInResponse()
    {
        // 错误状态走 lenient：正文解析失败不得盖住 HTTP 状态错误，原文仍挂在 Response.Data 上。
        const string html = "<html>boom</html>";
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(StatusCode: 500, Body: Encoding.UTF8.GetBytes(html), ContentType: "text/html"));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<dynamic>(server.Url));

        Assert.Equal(500, ex.Response!.Status);
        Assert.Equal(html, ex.Response.Data);
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

    [Fact]
    public async Task DynamicResponse_NonJsonBody_IsWrappedAsBadResponse()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes("<html>gateway error</html>"), ContentType: "text/html"));

        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<dynamic>(server.Url + "html"));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(ex.Cause);
    }

    /// <summary>同一个源码位置的成员读取（模拟用户代码里固定的 call site）。</summary>
    private static object? ReadName(dynamic value) => value.name;

    /// <summary>同一个源码位置的索引读取。</summary>
    private static object? ReadIndex(dynamic value, string key) => value[key];

    /// <summary>同一个源码位置的另一次成员读取（与 <see cref="ReadName"/> 不是一个 call site）。</summary>
    private static object? ReadExtra(dynamic value) => value.extra;

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

    // ---------------------------------------------------------------
    // 辅助
    // ---------------------------------------------------------------

    /// <summary>
    /// 正文读取阶段的超时预算。必须同时满足两头：
    /// <list type="bullet">
    /// <item>远大于「已预热连接的一次本地往返」——否则预算会先被响应头阶段吃掉，用例根本进不到正文阶段；</item>
    /// <item>远小于正文延迟（<see cref="SlowBodyDelay"/>）——否则读会自己等到正文，区分不出「超时生效」。</item>
    /// </list>
    /// <para>与 <c>StreamCancellationTests</c> 同一口径：预算与自然完成时间差一个数量级。</para>
    /// </summary>
    private static readonly TimeSpan BodyReadTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 服务端「先发响应头、再延迟正文」的延迟：必须远大于 <see cref="BodyReadTimeout"/>，
    /// 这样「被超时打断」与「自己等到正文」才不会混淆，调度抖动晚到几秒也仍有充足余量。
    /// </summary>
    private static readonly TimeSpan SlowBodyDelay = TimeSpan.FromSeconds(20);


}