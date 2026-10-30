using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using System.Text;

namespace AxiosNet.Tests.Serialization;

/// <summary>
/// 每实例序列化注册表：登记只影响本客户端，不污染全局注册表，也不被其它实例共享。
/// 优先级：显式 <c>ContentSerializer</c> → 实例注册表 → 全局注入 → 全局注册表 → 内置 JSON。
/// </summary>
[Collection(GlobalSerializationCollection.Name)]
public sealed class ContentSerializerRegistryTests
{
    [Fact]
    public void NewClient_StartsWithEmptyRegistry()
    {
        using var client = new AxiosClient();

        Assert.NotNull(client.ContentSerializers);
        Assert.Equal(0, client.ContentSerializers.Count);

        // 两次读取拿到的是同一份表（登记后仍能看见）。
        client.ContentSerializers.Register("application/x-inst-empty", new MarkerSerializer("x"));
        Assert.Same(client.ContentSerializers, client.ContentSerializers);
        Assert.Equal(1, client.ContentSerializers.Count);
    }

    [Fact]
    public async Task InstanceRegistry_DrivesRequestAndResponse()
    {
        await using var server = TestHttpServer.Start(req => req.Path.EndsWith("/orders/1")
            ? new TestResponse(200, Body: Encoding.UTF8.GetBytes("<XmlOrder><Id>7</Id><Name>book</Name></XmlOrder>"), ContentType: "application/xml; charset=utf-8")
            : TestJson.Echo(req, contentType: "application/xml; charset=utf-8"));

        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        client.ContentSerializers.Register(AxiosContentType.Xml, XmlContentSerializer.Instance);

        await client.PostAsync("orders", new XmlOrder { Id = 1, Name = "pen" }, c => c.ContentType = AxiosContentType.Xml);

        Assert.Contains("<XmlOrder", Body(server.Received[0]));
        Assert.Contains("<Id>1</Id>", Body(server.Received[0]));

        var response = await client.GetAsync<XmlOrder>("orders/1");
        Assert.Equal(7, response.Data!.Id);
        Assert.Equal("book", response.Data.Name);
    }

    [Fact]
    public async Task InstanceRegistry_IsInvisibleToOtherClients()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));

        using var registered = new AxiosClient();
        using var untouched = new AxiosClient();
        registered.ContentSerializers.Register("application/x-inst-isolated", new MarkerSerializer("instance"));

        await registered.PostAsync(
            server.Url + "x",
            new XmlOrder { Id = 1, Name = "pen" },
            c => c.ContentType = "application/x-inst-isolated");

        // 另一个实例没有这条登记：按内置 JSON 序列化，实例之间互不影响。
        await untouched.PostAsync(
            server.Url + "y",
            new XmlOrder { Id = 1, Name = "pen" },
            c => c.ContentType = "application/x-inst-isolated");

        Assert.Equal("instance", Body(server.Received[0]));
        Assert.Equal("""{"id":1,"name":"pen"}""", Body(server.Received[1]));
        Assert.Equal(1, registered.ContentSerializers.Count);
        Assert.Equal(0, untouched.ContentSerializers.Count);
    }

    [Fact]
    public async Task InstanceRegistry_BeatsGlobalSerializerAndGlobalRegistry()
    {
        var previous = AxiosContent.Serializer;
        AxiosContent.Register("application/x-inst-priority", new MarkerSerializer("global-registry"));
        AxiosContent.Serializer = XmlContentSerializer.Instance;
        try
        {
            await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
            using var client = new AxiosClient();
            client.ContentSerializers.Register("application/x-inst-priority", new MarkerSerializer("instance"));

            await client.PostAsync(
                server.Url + "x",
                new XmlOrder { Id = 1 },
                c => c.ContentType = "application/x-inst-priority");

            // 全局兜底序列化器与全局注册表都更粗；实例显式登记的格式更具体，优先。
            Assert.Equal("instance", Body(server.Received[0]));
        }
        finally
        {
            AxiosContent.Serializer = previous;
            AxiosContent.Unregister("application/x-inst-priority");
        }
    }

    [Fact]
    public async Task ExplicitContentSerializer_StillBeatsInstanceRegistry()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        client.ContentSerializers.Register("application/x-inst-explicit", new MarkerSerializer("instance"));

        await client.PostAsync(
            "x",
            new XmlOrder { Id = 1 },
            c =>
            {
                c.ContentType = "application/x-inst-explicit";
                c.Serialization.ContentSerializer = new MarkerSerializer("explicit");
            });

        Assert.Equal("explicit", Body(server.Received[0]));
    }

    [Fact]
    public async Task PerRequestRegistry_ReplacesInstanceRegistry()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();
        client.ContentSerializers.Register("application/x-inst-swap", new MarkerSerializer("instance"));

        await client.PostAsync(
            server.Url + "x",
            new XmlOrder { Id = 1 },
            c =>
            {
                c.ContentType = "application/x-inst-swap";

                // 按请求换一整份表：本次只用请求自己的登记。
                c.Serialization.ContentSerializers = new ContentSerializerRegistry();
                c.Serialization.ContentSerializers.Register("application/x-inst-swap", new MarkerSerializer("request"));
            });

        Assert.Equal("request", Body(server.Received[0]));

        // 请求级替换不回流到实例。
        Assert.Equal(1, client.ContentSerializers.Count);
    }

    [Fact]
    public async Task ReplayedConfig_DoesNotCarrySourceInstanceRegistry()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));

        using var source = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        using var target = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        source.ContentSerializers.Register("application/x-inst-replay", new MarkerSerializer("source"));

        var first = await source.PostAsync(
            "x",
            new XmlOrder { Id = 1, Name = "pen" },
            c => c.ContentType = "application/x-inst-replay");

        // 回放到没有这条登记的实例：注册表属实例级，沿用目标实例（空表）→ 内置 JSON。
        await target.RequestAsync(first.Config!);

        Assert.Equal("source", Body(server.Received[0]));
        Assert.Equal("""{"id":1,"name":"pen"}""", Body(server.Received[1]));
    }

    [Fact]
    public async Task InstanceRegistry_NormalizesAliasesAndStructuredSuffix()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();
        client.ContentSerializers.Register(AxiosContentType.Xml, XmlContentSerializer.Instance);

        await client.PostAsync(server.Url + "a", new XmlOrder { Id = 1 }, c => c.ContentType = "text/xml");
        await client.PostAsync(server.Url + "b", new XmlOrder { Id = 1 }, c => c.ContentType = "application/vnd.axios+xml");

        Assert.Contains("<XmlOrder", Body(server.Received[0]));
        Assert.Contains("<XmlOrder", Body(server.Received[1]));
    }

    [Fact]
    public void Register_ValidatesArgumentsAndTracksCount()
    {
        var registry = new ContentSerializerRegistry();

        Assert.Throws<ArgumentNullException>(() => registry.Register("application/x-inst-null", null!));
        Assert.Throws<ArgumentException>(() => registry.Register("   ", new MarkerSerializer("x")));
        Assert.Throws<ArgumentException>(() => registry.Register("; charset=utf-8", new MarkerSerializer("x")));

        registry.Register("APPLICATION/X-Inst-Count; charset=utf-8", new MarkerSerializer("first"));
        Assert.Equal(1, registry.Count);

        // 重复登记（大小写与参数不同）只覆盖，不新增。
        registry.Register("application/x-inst-count", new MarkerSerializer("second"));
        Assert.Equal(1, registry.Count);

        Assert.True(registry.Unregister("application/x-inst-count"));
        Assert.False(registry.Unregister("application/x-inst-count"));
        Assert.Equal(0, registry.Count);

        registry.Register("application/x-inst-count", new MarkerSerializer("again"));
        registry.Clear();
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void PreconfiguredRegistry_IsReusedByClient()
    {
        var registry = new ContentSerializerRegistry();
        using var client = new AxiosClient(new AxiosConfig { Serialization = new() { ContentSerializers = registry } });

        // 构造时就给了表：实例沿用它本身，不再另建一份。
        Assert.Same(registry, client.ContentSerializers);
    }

    [Fact]
    public async Task PerRequestRegister_MutatesTheInstanceTable()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        await client.PostAsync(
            "x",
            new XmlOrder { Id = 1 },
            c =>
            {
                c.ContentType = "application/x-inst-mutate";
                c.Serialization.ContentSerializers!.Register("application/x-inst-mutate", new MarkerSerializer("mutated"));
            });

        // 请求里拿到的是实例那张表本身：直接 Register 会留在实例上；要只在本次生效请 new 一张再赋值。
        Assert.Equal(1, client.ContentSerializers.Count);
    }

    [Fact]
    public void SharedDefaultsAndClones_ShareTheSameTable()
    {
        var defaults = new AxiosConfig();
        using var first = new AxiosClient(defaults);
        using var second = new AxiosClient(defaults);

        Assert.Same(first.ContentSerializers, second.ContentSerializers);

        // Clone() 复制的是同一张表（不是深拷贝）：往克隆出来的配置上登记，原实例也看得见。
        defaults.Clone().Serialization.ContentSerializers!.Register("application/x-inst-shared", new MarkerSerializer("shared"));

        Assert.Equal(1, first.ContentSerializers.Count);
    }

    // ------------------------------------------------------------------
    // 测试辅助
    // ------------------------------------------------------------------

    private static string Body(TestRequest request) => Encoding.UTF8.GetString(request.Body);

    /// <summary>只回一个标记串：用来判断「到底是哪个序列化器被调用了」。</summary>
    private sealed class MarkerSerializer : IContentSerializer
    {
        private readonly string _marker;

        public MarkerSerializer(string marker) => _marker = marker;

        public string Serialize(object? value, Type type, AxiosConfig config) => _marker;

        public object? Deserialize(string text, Type type, AxiosConfig config) => _marker;
    }
}
