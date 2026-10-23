using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 配置合并与请求体契约的高风险区（此前无覆盖）：
/// <list type="bullet">
/// <item><c>*Set</c> 语义：按请求显式 <c>null</c> 必须能清掉实例默认上限，
/// 不能被「null 视为未设置」吞掉；</item>
/// <item>transform 链：实例默认在前、本次追加在后，且不得污染实例 <c>Defaults</c>（否则跨请求累积）；</item>
/// <item>进度包装（<see cref="AxiosNet.Internal.ProgressHttpContent"/>）必须保留内层内容头，
/// 否则 <c>MaxBodyLength</c> 会静默失效、multipart 边界会丢；</item>
/// <item>实例级 <c>ContentType</c> 的「显式设置」标记要能穿过 <c>Clone</c>，
/// 否则无自带类型头的 <see cref="HttpContent"/> 拿不到配置的类型；</item>
/// <item>响应解码：charset 带引号、UTF-16 无 BOM、纯二进制不参与解码。</item>
/// </list>
/// </summary>
public sealed class ConfigMergeContractTests
{
    private static string BodyOf(TestRequest request) => Encoding.UTF8.GetString(request.Body);

    // ---------------------------------------------------------------
    // *Set 语义：显式 null 覆盖实例默认上限
    // ---------------------------------------------------------------

    [Fact]
    public async Task PerRequestMaxContentLengthNull_ClearsInstanceDefault()
    {
        var payload = new byte[4096];
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: payload,
            ContentType: "application/octet-stream"));

        using var client = new AxiosClient(new AxiosConfig
        {
            Limits = new()
            {
                MaxContentLength = 16,
            },
        });

        // 实例默认上限对 4KB 响应生效（声明了长度 → 读正文前就拒绝）
        var blocked = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<byte[]>(server.Url + "file"));
        Assert.Equal(AxiosException.ErrBadResponse, blocked.Code);

        // 按请求显式设 null 必须清掉它：这条路径完全依赖 MaxContentLengthSet，
        // 一旦合并退化成「null 即未设置」，这里会继续被实例默认拦住。
        var response = await client.GetAsync<byte[]>(server.Url + "file", c => c.Limits.MaxContentLength = null);

        Assert.Equal(payload.Length, response.Data!.Length);
    }

    [Fact]
    public async Task PerRequestMaxBodyLengthNull_ClearsInstanceDefault()
    {
        var payload = new byte[1024];
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));

        using var client = new AxiosClient(new AxiosConfig
        {
            Limits = new()
            {
                MaxBodyLength = 16,
            },
        });

        var blocked = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(server.Url + "up", payload));
        Assert.Equal(AxiosException.ErrBadRequest, blocked.Code);
        Assert.Empty(server.Received);

        var response = await client.PostAsync(server.Url + "up", payload, c => c.Limits.MaxBodyLength = null);

        Assert.Equal(200, response.Status);
        Assert.Equal(payload.Length, server.Received[^1].Body.Length);
    }

    [Fact]
    public async Task PerRequestMaxRateNull_ClearsInstanceDefault()
    {
        const int size = 4096;
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: new byte[size],
            ContentType: "application/octet-stream"));

        // 实例默认 32 B/s：4KB 需要 128 秒，3 秒预算下必然超时。
        // 清不掉实例限速时本用例会以 ECONNABORTED 失败，而不是变慢。
        using var client = new AxiosClient(new AxiosConfig
        {
            Limits = new()
            {
                MaxRate = 32,
            },
            Timeout = TimeSpan.FromSeconds(3),
        });

        var stopwatch = Stopwatch.StartNew();
        var response = await client.GetAsync<byte[]>(server.Url + "file", c => c.Limits.MaxRate = null);
        stopwatch.Stop();

        Assert.Equal(size, response.Data!.Length);
        Assert.True(stopwatch.ElapsedMilliseconds < 2000, $"限速未被清除：{stopwatch.ElapsedMilliseconds}ms");
    }

    // ---------------------------------------------------------------
    // transform 链：顺序固定为「实例默认 → 本次追加」，且不写回实例
    // ---------------------------------------------------------------

    [Fact]
    public async Task RequestTransformChain_RunsInstanceThenPerRequest_AndDoesNotAccumulate()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        client.Defaults.TransformRequest.Add((data, _) => "[" + data + "]");

        var perRequestCalls = 0;
        for (var i = 0; i < 3; i++)
        {
            await client.PostAsync("echo", "x", c => c.TransformRequest.Add((data, _) =>
            {
                perRequestCalls++;
                return data + "P";
            }));
        }

        // 顺序：实例默认的包裹在前，本次追加的拼接在后
        Assert.Equal("[x]P", BodyOf(server.Received[0]));
        Assert.Equal("[x]P", BodyOf(server.Received[2]));

        // 每次请求只跑一次本次追加的 transform（若合并写回了 Defaults，第 2、3 次会重复叠加）
        Assert.Equal(3, perRequestCalls);
        Assert.Single(client.Defaults.TransformRequest);
    }

    [Fact]
    public async Task ResponseTransformChain_RunsInstanceThenPerRequest_InOrder()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { v = 1 }));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        client.Defaults.TransformResponse.Add((data, _) => "I:" + data);

        var response = await client.GetAsync<string>("data", c =>
            c.TransformResponse.Add((data, _) => data + ":P"));

        // 非泛型链上返回的新实例也会带上处理过的 Data
        Assert.Equal("I:{\"v\":1}:P", response.Data);

        // 实例默认链没有被本次追加污染
        Assert.Single(client.Defaults.TransformResponse);
    }

    // ---------------------------------------------------------------
    // 进度包装不得丢内容头
    // ---------------------------------------------------------------

    [Fact]
    public async Task MaxBodyLength_WithUploadProgressWrapper_IsStillEnforced()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        var payload = new byte[1024];

        // 进度包装（ProgressHttpContent）必须复制内层 Content-Length：
        // 一旦复制漏了，content.Headers.ContentLength 为 null，这个上限就静默失效了。
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(server.Url + "up", payload, c =>
        {
            c.Limits.MaxBodyLength = 16;
            c.Callbacks.OnUploadProgress = _ => { };
        }));

        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);

        // 超限在发送前判定：服务端一个请求都不该看到
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task ExplicitInstanceContentType_IsAppliedToRawHttpContent()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));

        // ContentType 显式设置 → ContentTypeSet 必须穿过 Defaults.Clone() 活到请求期，
        // 否则「无自带类型头的 HttpContent」会落到「不补头部」的分支。
        using var client = new AxiosClient(new AxiosConfig { ContentType = "application/xml" });
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("<a/>"));

        await client.PostAsync(server.Url + "up", content);

        Assert.Equal("application/xml", server.Received[0].Headers["content-type"]);
    }

    // ---------------------------------------------------------------
    // 进度事件：服务端未声明长度
    // ---------------------------------------------------------------

    [Fact]
    public async Task DownloadProgress_WithoutDeclaredLength_ReportsNullTotalAndProgress()
    {
        const int size = 256 * 1024;
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: new byte[size],
            ContentType: "application/octet-stream",
            Chunked: true));

        using var client = new AxiosClient();

        var events = new List<AxiosProgressEvent>();
        await client.GetAsync<byte[]>(server.Url + "chunked", c => c.Callbacks.OnDownloadProgress = events.Add);

        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.Null(e.Total));
        Assert.All(events, e => Assert.Null(e.Progress));

        // 总量未知时仍要报出准确的已传输字节，且最终事件只报一次满载值
        Assert.Equal(size, events[^1].Loaded);
        Assert.Single(events, e => e.Loaded == size);
    }

    // ---------------------------------------------------------------
    // 响应解码
    // ---------------------------------------------------------------

    [Fact]
    public async Task ResponseCharset_Utf16WithoutBom_IsDecoded()
    {
        const string text = "hello 你好";
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.Unicode.GetBytes(text),
            ContentType: "text/plain; charset=utf-16"));

        using var client = new AxiosClient();

        // charset 直接给出 UTF-16（LE）时不应依赖 BOM 探测
        var response = await client.GetAsync<string>(server.Url + "text");

        Assert.Equal(text, response.Data);
    }

    [Fact]
    public async Task ResponseCharset_QuotedValue_IsAccepted()
    {
        const string text = "引号包裹的 charset";
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes(text),
            ContentType: "text/plain; charset=\"utf-8\""));

        using var client = new AxiosClient();

        var response = await client.GetAsync<string>(server.Url + "text");

        Assert.Equal(text, response.Data);
    }

    [Fact]
    public async Task ByteArrayResponse_IsNotDecodedAsText()
    {
        // 故意用非 UTF-8 的字节序列：任何一次「先解码成 string 再转回来」都会破坏它
        var payload = new byte[] { 0x00, 0xFF, 0xFE, 0x80, 0x41, 0xC3, 0x28 };
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: payload,
            ContentType: "application/octet-stream"));

        using var client = new AxiosClient();

        // 不显式指定 responseType，靠 T = byte[] 走「只要字节」的分支
        var response = await client.GetAsync<byte[]>(server.Url + "bin");

        Assert.Equal(payload, response.Data);
    }

    // ---------------------------------------------------------------
    // baseURL 与 url 的斜杠合并
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("http://a.com/api/", "/users", "http://a.com/api/users")]
    [InlineData("http://a.com/api", "/users", "http://a.com/api/users")]
    [InlineData("http://a.com/api/", "users", "http://a.com/api/users")]
    [InlineData("http://a.com/api", "users", "http://a.com/api/users")]
    [InlineData("http://a.com/", "/users", "http://a.com/users")]
    [InlineData("http://a.com", "/users", "http://a.com/users")]
    [InlineData("http://a.com/api/", "https://b.com/x", "https://b.com/x")]
    public void GetUri_CollapsesSlashesBetweenBaseAndUrl(string baseAddress, string url, string expected)
    {
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = baseAddress });

        Assert.Equal(expected, client.GetUri(c => c.Url = url));
    }

    [Fact]
    public void GetUri_BaseUrlWithoutUrl_UsesBaseAsIs()
    {
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://a.com/api/" });

        Assert.Equal("http://a.com/api", client.GetUri(new AxiosConfig()));
    }

    [Fact]
    public void GetUri_ParamsDontLeakIntoFragment()
    {
        using var client = new AxiosClient();

        // fragment 之前的查询串才发得出去；拼在 # 之后等于整串都没了
        var uri = client.GetUri(c =>
        {
            c.Url = "http://a.com/p?existing=1#top";
            c.Params = new { q = "x" };
        });

        Assert.Equal("http://a.com/p?existing=1&q=x#top", uri);
    }

    [Fact]
    public async Task ParamsWithJsonElementValues_ReachServerEvenWithFragmentUrl()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();
        using var document = JsonDocument.Parse("""{"q":"a b","tags":["x","y"],"skip":null}""");

        await client.GetAsync(server.Url + "search#top", c => c.Params = document.RootElement);

        var received = server.Received[0];
        Assert.Equal("/search", received.Path);
        Assert.Equal("q=a%20b&tags[]=x&tags[]=y", received.Query);
    }

    // ---------------------------------------------------------------
    // 快捷方法直接在 Defaults 的副本上填写（见 AxiosClient.Shortcut）：
    // 必须保证「请求之间」「请求与实例默认值之间」没有共享可变状态。
    // ---------------------------------------------------------------

    [Fact]
    public async Task ShortcutConfig_SequentialRequests_DoNotLeakIntoDefaultsOrEachOther()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        client.Defaults.Headers.Set("X-Default", "base");
        client.Defaults.Headers.Set("X-Second", "2");

        // 第一次：加一个只属于本次的头，并摘掉一个实例默认头
        await client.GetAsync("first", c =>
        {
            c.Headers.Set("X-Only", "1");
            c.RemoveHeaders.Add("X-Second");
        });

        // 第二次：什么都不写，必须回到「只有实例默认头」的状态
        await client.GetAsync("second");

        var first = server.Received[0];
        var second = server.Received[1];

        // 本次生效
        Assert.Equal("1", first.Headers["X-Only"]);
        Assert.Equal("base", first.Headers["X-Default"]);
        Assert.False(first.Headers.ContainsKey("X-Second"));

        // 下一次不受影响：写的头不残留、删的头也没被写回实例默认值
        Assert.False(second.Headers.ContainsKey("X-Only"));
        Assert.Equal("2", second.Headers["X-Second"]);

        // 实例默认值原样
        Assert.Equal(2, client.Defaults.Headers.Count);
        Assert.Equal("base", client.Defaults.Headers.Get("X-Default"));
        Assert.Equal("2", client.Defaults.Headers.Get("X-Second"));
        Assert.Empty(client.Defaults.RemoveHeaders);
    }

    [Fact]
    public async Task ShortcutConfig_ConcurrentRequests_KeepTheirOwnHeaderValues()
    {
        // 服务端把收到的 X-Req 原样回显：任何一次请求的配置被别的请求覆盖，断言当场就会失败
        await using var server = TestHttpServer.Start(req => new TestResponse(
            Body: Encoding.UTF8.GetBytes(req.Headers["X-Req"]),
            ContentType: "text/plain; charset=utf-8"));

        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        client.Defaults.Headers.Set("X-Default", "base");

        var calls = Enumerable.Range(0, 64).Select(async i =>
        {
            var tag = i.ToString(CultureInfo.InvariantCulture);
            var response = await client.GetAsync<string>("echo", c => c.Headers.Set("X-Req", tag));
            Assert.Equal(tag, response.Data);
        });

        await Task.WhenAll(calls);

        // 并发写完，实例默认值里也不该多出这次的头
        Assert.False(client.Defaults.Headers.Has("X-Req"));
        Assert.Equal("base", client.Defaults.Headers.Get("X-Default"));
    }

    /// <summary>
    /// 回放 <see cref="AxiosResponse.Config"/>：它本身是「上一次的生效配置」，但交回来时必须
    /// 被当成<b>按请求项</b>重新合并到本实例默认值上。
    /// <para>
    /// 合并标记只在管线内部有效（<see cref="AxiosConfig.Clone"/> 不继承它）。此前它随
    /// <c>WithUrl</c> 的克隆一起被带出来，回放到另一个实例会跳过该实例的默认值 —— 头部丢失，
    /// 而且实例级的 <c>PoolOptions</c> 会被误判成「按请求设置」直接报错（调用方什么都没设）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReplayedEffectiveConfig_IsMergedIntoTheNewClientDefaults()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var first = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Connection = new()
            {
                PoolOptions = new ConnectionPoolOptions(),
            },
        });
        first.Defaults.Headers.Set("X-First", "1");
        var response = await first.GetAsync("one");

        using var second = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        second.Defaults.Headers.Set("X-Second", "2");

        // 不抛 ERR_BAD_OPTION（PoolOptions 是第一个实例的默认值，不是本次按请求写的）
        await second.PostAsync("two", response.Config!);

        var received = server.Received[1];

        // 新实例的默认头必须生效
        Assert.Equal("2", received.Headers["X-Second"]);

        // 旧实例的生效头仍在：合并后的配置里「实例默认」与「按请求项」已经不可区分，
        // 回放只能做到「带着旧值再合并一次」，这里把这条语义固定下来。
        Assert.Equal("1", received.Headers["X-First"]);
    }
}
