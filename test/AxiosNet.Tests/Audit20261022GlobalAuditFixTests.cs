using System.Net;
using System.Text;
using AxiosNet.Pooling;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;

namespace AxiosNet.Tests;

/// <summary>
/// 2026-10-22 全库审查（全局 bug 排查）确认缺陷的回归测试，每个发现都有动态复现支撑：
/// <list type="bullet">
/// <item>Bug 1：MaxRedirects 按请求校验拿属性默认值 5 做相等短路，池配了别的重定向策略时显式写的值被静默放行；</item>
/// <item>Bug 2：内置 JSON 序列化器字节入口不剥 UTF-8 BOM，带 BOM 的 octet-stream JSON 反序列化失败；</item>
/// <item>Bug 3：请求模型查询绑定全为 null 时返回空字典，静默清空实例默认 Params；</item>
/// <item>Bug 4：协议相对 URL（//host/path）被当相对地址与 baseURL 拼接，请求静默发往错误主机；</item>
/// <item>Bug 5：反序列化 catch 过滤器未排除 OperationCanceledException，自定义序列化器遵循取消被误报成解析失败；</item>
/// <item>Bug 6：非法 HTTP 方法 token 的 FormatException 逃出统一错误模型且不进错误缓存；</item>
/// <item>Bug 7：IsScalarType 与 ParamsSerializer.IsScalar 清单漂移，POST 模型的单个 DateOnly / TimeOnly / Uri 属性被静默当成请求体；</item>
/// <item>Bug 8：Defaults.RemoveHeaders 只在快捷路径生效，RequestAsync 入口不生效；</item>
/// <item>Bug 9：请求拦截器返回 null Task 时 await null 漏出裸 NullReferenceException；</item>
/// <item>Bug 10：GetStatistics 的 EndpointCount 与 Endpoints 快照来自两次独立读取，并发下自相矛盾；</item>
/// <item>Bug 11：容量驱逐声称 LRU 实际按字典枚举序（任意序）取第一个空闲节点；</item>
/// <item>Bug 12：ContentType 赋 null / 空白静默重置为 application/json 并标记为显式设置；</item>
/// <item>Bug 13：自建池分支对 poolOptions 与 defaults.Connection.PoolOptions / Proxy / Credentials 同时传入静默取一个；</item>
/// <item>Bug 14：ContentType 声明的 charset 与 RequestEncoding 矛盾时静默产出「头一个编码、正文另一个编码」的错误字节。</item>
/// </list>
/// </summary>
public sealed class Audit20261022GlobalAuditFixTests
{
    private static string Body(TestRequest request) => Encoding.UTF8.GetString(request.Body);

    private static string ContentType(TestRequest request)
        => request.Headers.GetValueOrDefault("content-type") ?? string.Empty;

    // ===============================================================
    // Bug 1：MaxRedirects 校验短路用属性默认值替代池生效值
    // ===============================================================

    [Fact]
    public async Task PerRequestMaxRedirects_EqualToDefaultsButDiffersFromPool_Throws()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));

        // defaults 上没写 MaxRedirects（属性值仍是默认 5），池实际策略是 3
        using var client = new AxiosClient(
            new AxiosConfig { BaseAddress = server.Url },
            poolOptions: new ConnectionPoolOptions { MaxAutomaticRedirections = 3 });

        // 按请求显式写 5：此前 5 == Defaults.MaxRedirects(5) 直接放行，实际只会跟随 3 次
        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync("x", c => c.Connection.MaxRedirects = 5));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("MaxRedirects", ex.Message);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task PerRequestMaxRedirects_WhenPoolDisablesRedirect_Throws()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));

        // AllowAutoRedirect = false 时生效值是 0：按请求写 5 与此前一样被静默放行
        using var client = new AxiosClient(
            new AxiosConfig { BaseAddress = server.Url },
            poolOptions: new ConnectionPoolOptions { AllowAutoRedirect = false });

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync("x", c => c.Connection.MaxRedirects = 5));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    [Fact]
    public async Task PerRequestMaxRedirects_Unset_DoesNotThrowEvenWhenPoolDiffersFromDefault()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));

        // 未显式写过的请求携带的是 Defaults 的属性值 5：不能因为池配了 3 就误抛
        using var client = new AxiosClient(
            new AxiosConfig { BaseAddress = server.Url },
            poolOptions: new ConnectionPoolOptions { MaxAutomaticRedirections = 3 });

        var response = await client.GetAsync("x");
        Assert.Equal(200, response.Status);
    }

    // ===============================================================
    // Bug 2：内置 JSON 序列化器字节入口不剥 UTF-8 BOM
    // ===============================================================

    [Fact]
    public async Task BuiltInJsonSerializer_ByteEntry_StripsUtf8Bom()
    {
        // 服务端把 JSON 标成 application/octet-stream（非文本 → 走字节入口）且正文带 UTF-8 BOM
        var payload = Encoding.UTF8.GetBytes("{\"name\":\"bom\",\"age\":7}");
        var body = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(payload).ToArray();

        await using var server = TestHttpServer.Start(_ => new TestResponse(
            ContentType: "application/octet-stream",
            Body: body));

        using var client = new AxiosClient();

        var response = await client.RequestAsync<Person>(new AxiosConfig { Url = server.Url + "x" });

        Assert.Equal("bom", response.Data!.Name);
        Assert.Equal(7, response.Data.Age);
    }

    [Fact]
    public async Task BuiltInJsonSerializer_TextEntry_StillDecodesBomAsBefore()
    {
        // 文本路径（对照组）：解码时剥 BOM 的行为不变
        var payload = Encoding.UTF8.GetBytes("{\"name\":\"bom\",\"age\":7}");
        var body = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(payload).ToArray();

        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: body));

        using var client = new AxiosClient();

        var response = await client.RequestAsync<Person>(new AxiosConfig { Url = server.Url + "x" });

        Assert.Equal("bom", response.Data!.Name);
    }

    // ===============================================================
    // Bug 3：查询绑定全为 null 时返回空字典，静默清空实例默认 Params
    // ===============================================================

    [Fact]
    public void QueryBindingsAllNull_DoesNotClearInstanceDefaultParams()
    {
        using var client = new ApiClient("http://127.0.0.1:1/", c =>
            c.Params = new { api_key = "secret" });

        // 有 [Query] 绑定但值为 null：此前 Params = 空字典，合并层整体替换把 api_key 清掉
        var uri = client.GetUri(new AllNullQueryRequest());

        Assert.Contains("api_key=secret", uri);
    }

    [Fact]
    public void NoQueryBindings_DoesNotClearInstanceDefaultParams()
    {
        using var client = new ApiClient("http://127.0.0.1:1/", c =>
            c.Params = new { api_key = "secret" });

        // 没有任何查询绑定（对照组）：两种模型形态行为必须一致
        var uri = client.GetUri(new NoQueryBindingRequest());

        Assert.Contains("api_key=secret", uri);
    }

    // ===============================================================
    // Bug 4：协议相对 URL 被当相对地址与 baseURL 拼接
    // ===============================================================

    [Fact]
    public void ProtocolRelativeUrl_ResolvesAgainstBaseAddressScheme()
    {
        using var client = new AxiosClient();

        // axios 的 isAbsoluteURL 把 //host/path 算绝对地址；库按 baseAddress 的 scheme 补全
        var uri = client.GetUri(c =>
        {
            c.BaseAddress = "https://api.example.com";
            c.Url = "//cdn.example.com/app.js";
        });

        Assert.Equal("https://cdn.example.com/app.js", uri);
    }

    [Fact]
    public void ProtocolRelativeUrl_WithoutBaseAddress_ThrowsInvalidUrl()
    {
        using var client = new AxiosClient();

        // 没有 scheme 可继承：给出明确的 URL 错误，而不是静默按相对地址处理
        var ex = Assert.Throws<AxiosException>(() => client.GetUri(c => c.Url = "//cdn.example.com/app.js"));

        Assert.Equal(AxiosException.ErrInvalidUrl, ex.Code);
    }

    // ===============================================================
    // Bug 5：自定义序列化器抛 OperationCanceledException 被误报成解析失败
    // ===============================================================

    [Fact]
    public async Task CustomSerializer_TextEntry_OperationCanceled_PropagatesUnwrapped()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Serialization = new()
            {
                ContentSerializer = new CancelingSerializer(),
            },
        });

        // 取消语义必须归一为 ERR_CANCELED（外层管线对 OCE 的统一包装），
        // 不能在下层被包装成 ERR_BAD_RESPONSE —— 那会把「取消」误报成「解析失败」
        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.RequestAsync<Person>(new AxiosConfig { Url = "x" }));

        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
        Assert.IsType<OperationCanceledException>(ex.Cause);
    }

    [Fact]
    public async Task CustomSerializer_ByteEntry_OperationCanceled_PropagatesUnwrapped()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            ContentType: "application/octet-stream",
            Body: Encoding.UTF8.GetBytes("{}")));
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Serialization = new()
            {
                ContentSerializer = new CancelingSerializer(),
            },
        });

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.RequestAsync<Person>(new AxiosConfig { Url = "x" }));

        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
        Assert.IsType<OperationCanceledException>(ex.Cause);
    }

    // ===============================================================
    // Bug 6：非法 HTTP 方法 token 的 FormatException 逃出统一错误模型
    // ===============================================================

    [Fact]
    public void InvalidHttpMethodToken_ThrowsInvalidOperationException_AndCachesSameMessage()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");

        // 模型定义错误统一归一为 InvalidOperationException（ApiClient 的文档承诺），
        // 且进缓存：每次调用抛同一份消息，不重新执行特性构造
        var first = Assert.Throws<InvalidOperationException>(() => client.GetUri(new BadMethodRequest()));
        var second = Assert.Throws<InvalidOperationException>(() => client.GetUri(new BadMethodRequest()));

        Assert.Equal(first.Message, second.Message);
    }

    // ===============================================================
    // Bug 7：IsScalarType 清单漂移，DateOnly / Uri 被静默当成请求体
    // ===============================================================

    [Fact]
    public async Task PostModel_SingleDateOnlyProperty_BindsQueryNotBody()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new DateOnlyBodyCandidateRequest { When = new DateOnly(2026, 10, 22) });

        var received = Assert.Single(server.Received);

        // DateOnly 走 IFormattable 的不变文化短日期（10/22/2026，斜杠被百分号编码）；
        // 本测试的要点是路由去向：查询参数而非请求体（此前被默认 Body 收走）
        Assert.Equal("When=10%2F22%2F2026", received.Query);
        Assert.Empty(received.Body);
    }

    [Fact]
    public async Task PostModel_SingleUriProperty_BindsQueryNotBody()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new UriBodyCandidateRequest { Callback = new Uri("http://example.com/hook") });

        var received = Assert.Single(server.Received);
        Assert.Contains("Callback=", received.Query);
        Assert.Empty(received.Body);
    }

    [Fact]
    public async Task PostModel_SingleDateOnlyPathProperty_IsAcceptedAsPath()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new ApiClient(server.Url);

        // DateOnly 此前不在 Path/Header 的标量清单里，构建期直接报错
        await client.RequestAsync(new DateOnlyPathRequest { Day = new DateOnly(2026, 10, 22) });

        var received = Assert.Single(server.Received);
        Assert.Equal("/reports/10%2F22%2F2026", received.Path);
    }

    // ===============================================================
    // Bug 8：Defaults.RemoveHeaders 只在快捷路径生效
    // ===============================================================

    [Fact]
    public async Task DefaultsRemoveHeaders_AppliesOnRequestAsyncPathToo()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Headers = new AxiosHeaders(new Dictionary<string, string> { ["X-Trace"] = "on" })
        });
        client.Defaults.RemoveHeaders.Add("X-Trace");

        await client.GetAsync("x");
        await client.RequestAsync(new AxiosConfig { Url = "x" });

        // 两条入口对「实例默认要摘除的头」行为一致
        Assert.Equal(2, server.Received.Count);
        Assert.All(server.Received, r => Assert.False(r.Headers.ContainsKey("X-Trace")));
    }

    // ===============================================================
    // Bug 9：请求拦截器返回 null Task 漏出裸 NRE
    // ===============================================================

    [Fact]
    public async Task RequestInterceptor_ReturningNullTask_ThrowsAxiosException()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        client.Interceptors.Request.Use((Func<AxiosConfig, Task<AxiosConfig>>)(_ => null!));

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("x"));

        Assert.Contains("null Task", ex.Message);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task RequestInterceptor_OnRejectedReturningNullTask_ThrowsAxiosException()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        // OnFulfilled 先抛，再让 OnRejected 返回 null：两条路径都要给出可定位的配置错误而不是裸 NRE
        client.Interceptors.Request.Use(
            (Func<AxiosConfig, Task<AxiosConfig>>)(_ => throw new InvalidOperationException("boom")),
            (Func<Exception, Task<AxiosConfig>>)(_ => null!));

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("x"));

        Assert.Contains("null Task", ex.Message);
        Assert.Empty(server.Received);
    }

    // ===============================================================
    // Bug 10：GetStatistics 的 EndpointCount 与 Endpoints 快照不一致
    // ===============================================================

    [Fact]
    public void GetStatistics_EndpointCount_EqualsEndpointsArrayLength()
    {
        using var pool = new HttpConnectionPool(new ConnectionPoolOptions());

        using var lease1 = pool.Lease(new Uri("http://127.0.0.1:9101/x"));
        using var lease2 = pool.Lease(new Uri("http://127.0.0.1:9102/x"));

        var stats = pool.GetStatistics();

        // 两者必须来自同一次快照（此前分两次读，并发新建 / 驱逐下计数与数组长度会背离）
        Assert.Equal(stats.Endpoints.Count, stats.EndpointCount);
        Assert.Equal(2, stats.EndpointCount);
    }

    // ===============================================================
    // Bug 11：容量驱逐声称 LRU 实际按任意序
    // ===============================================================

    [Fact]
    public void EnsureCapacity_EvictsLeastRecentlyUsedIdleEndpoint()
    {
        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            MaxEndpointCount = 2,
            IdleHandlerLifetime = TimeSpan.FromMinutes(30)
        });

        // A 先、B 后，再复用一次 A：B 成为最久未使用的空闲节点
        using (var a = pool.Lease(new Uri("http://127.0.0.1:9201/x")))
        {
        }

        Thread.Sleep(50);

        using (var b = pool.Lease(new Uri("http://127.0.0.1:9202/x")))
        {
        }

        Thread.Sleep(50);

        using (var aAgain = pool.Lease(new Uri("http://127.0.0.1:9201/x")))
        {
        }

        // 第三个 origin 触发容量检查：应驱逐 B 而不是任意一个
        using var c = pool.Lease(new Uri("http://127.0.0.1:9203/x"));

        var stats = pool.GetStatistics();
        Assert.Equal(2, stats.EndpointCount);
        Assert.Contains(stats.Endpoints, e => e.Origin.EndsWith(":9201"));
        Assert.Contains(stats.Endpoints, e => e.Origin.EndsWith(":9203"));
        Assert.DoesNotContain(stats.Endpoints, e => e.Origin.EndsWith(":9202"));
    }

    // ===============================================================
    // Bug 12：ContentType 赋 null / 空白静默变成「显式 application/json」
    // ===============================================================

    [Fact]
    public void ContentType_NullAssignment_Throws()
    {
        var config = new AxiosConfig();

        Assert.Throws<ArgumentNullException>(() => config.ContentType = null!);
    }

    [Fact]
    public void ContentType_BlankAssignment_Throws()
    {
        var config = new AxiosConfig();

        // 此前静默重置为 application/json 并置 ContentTypeSet，压掉序列化器自报的媒体类型
        Assert.Throws<ArgumentException>(() => config.ContentType = "   ");
    }

    // ===============================================================
    // Bug 14：ContentType 声明的 charset 与 RequestEncoding 矛盾时静默产出错误字节
    // ===============================================================

    [Fact]
    public async Task ContentTypeCharset_ConflictsWithRequestEncoding_Throws()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        // 头声明 utf-8、正文按 Latin1 编码：服务端按头解出乱码且没有任何报错
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(
            server.Url + "x",
            "café",
            c =>
            {
                c.ContentType = "text/plain; charset=utf-8";
                c.Serialization.RequestEncoding = Encoding.Latin1;
            }));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task ContentTypeCharset_WithoutRequestEncoding_EncodesBodyWithDeclaredCharset()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        // 声明 charset=iso-8859-1 但没设 RequestEncoding：此前正文按 UTF-8 编码、头声明 iso-8859-1，
        // 服务端按头解出乱码；现在以声明的 charset 编码正文，头与正文一致
        await client.PostAsync(
            server.Url + "x",
            "café",
            c => c.ContentType = "text/plain; charset=iso-8859-1");

        var sent = server.Received[0];
        Assert.Equal("text/plain; charset=iso-8859-1", ContentType(sent));
        Assert.Equal(new byte[] { (byte)'c', (byte)'a', (byte)'f', 0xE9 }, sent.Body);
    }

    // ===============================================================
    // Bug 13：自建池分支对重复传入的连接级选项静默取一个
    // ===============================================================

    [Fact]
    public void Constructor_PoolOptionsAndDefaultsPoolOptions_Throws()
    {
        var ex = Assert.Throws<AxiosException>(() => new AxiosClient(
            new AxiosConfig
            {
                Connection = new()
                {
                    PoolOptions = new ConnectionPoolOptions(),
                },
            },
            poolOptions: new ConnectionPoolOptions()));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("poolOptions", ex.Message);
    }

    [Fact]
    public void Constructor_ProxyOnBothPoolOptionsAndDefaults_Throws()
    {
        var ex = Assert.Throws<AxiosException>(() => new AxiosClient(
            new AxiosConfig
            {
                Connection = new()
                {
                    Proxy = new WebProxy("http://127.0.0.1:9"),
                },
            },
            poolOptions: new ConnectionPoolOptions { Proxy = new WebProxy("http://127.0.0.1:10") }));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    // ===============================================================
    // 测试用请求模型 / 序列化器
    // ===============================================================

    /// <summary>Deserialize 一律抛 OperationCanceledException 的序列化器（遵循取消的实现）。</summary>
    private sealed class CancelingSerializer : IContentSerializer
    {
        public string? MediaType => null;

        public string Serialize(object? value, Type type, AxiosConfig config) => "{}";

        public object? Deserialize(string text, Type type, AxiosConfig config)
            => throw new OperationCanceledException();

        public object? Deserialize(ReadOnlyMemory<byte> data, Type type, AxiosConfig config)
            => throw new OperationCanceledException();
    }
}

[Get("items")]
public sealed class AllNullQueryRequest : IApiRequest<Person>
{
    [Query]
    public string? Filter { get; set; }
}

[Get("items")]
public sealed class NoQueryBindingRequest : IApiRequest<Person>
{
    [ApiIgnore]
    public string? Hidden { get; set; }
}

[Post("report")]
public sealed class DateOnlyBodyCandidateRequest : IApiRequest<Person>
{
    public DateOnly When { get; set; }
}

[Post("upload")]
public sealed class UriBodyCandidateRequest : IApiRequest<Person>
{
    public Uri Callback { get; set; } = null!;
}

[Get("reports/{day}")]
public sealed class DateOnlyPathRequest : IApiRequest<Person>
{
    [Path]
    public DateOnly Day { get; set; }
}

[HttpApi("GE(T", "x")]
public sealed class BadMethodRequest : IApiRequest<Person>
{
}
