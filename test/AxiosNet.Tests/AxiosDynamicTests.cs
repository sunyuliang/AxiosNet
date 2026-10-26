using System.Text;
using System.Text.Json;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// <see cref="AxiosDynamic"/>（dynamic 响应的对象节点）自身的契约：
/// 成员/索引一致、缺省成员返回 null、运行期增删、完整的 IDictionary 表面、
/// JSON 正反序列化、以及当作 params/请求体再发出去。
/// </summary>
public sealed class AxiosDynamicTests
{
    /// <summary>同一个源码位置的成员读取（模拟用户代码里固定的 call site）。</summary>
    private static object? ReadName(dynamic value) => value.name;

    /// <summary>同一个源码位置的索引读取。</summary>
    private static object? ReadIndex(dynamic value, string key) => value[key];

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

    /// <summary>同一个源码位置的另一次成员读取（与 <see cref="ReadName"/> 不是一个 call site）。</summary>
    private static object? ReadExtra(dynamic value) => value.extra;

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
}
