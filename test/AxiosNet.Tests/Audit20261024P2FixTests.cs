using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 性能轮回归测试（按审核报告编号）：
/// <list type="bullet">
/// <item><b>15</b>：multipart 非法 boundary 统一报 <c>ERR_BAD_OPTION_VALUE</c>；</item>
/// <item><b>16</b>：序列化器自报 / 写入的带 charset Content-Type 与正文实际编码矛盾时拦下
/// （收口点挪到正文产出之后，兑现 <see cref="IContentSerializer"/> 上的文档承诺）；</item>
/// <item><b>1/2</b>：params 收尾与嵌套键转义缓存的正确性（跨请求稳定、特殊字符只转义一次）；</item>
/// <item><b>5/6</b>：transform 链写时复制——并发请求各自分离、不污染实例默认链；</item>
/// <item><b>9</b>：回放生效配置免二次深拷，且不带上一次的按请求 Auth（沿用目标实例的默认凭据）。</item>
/// <item><b>review#4</b>：multipart 装配在中间部件失败时，后面还没轮到装配的裸 <c>Stream</c> 也要释放
/// （容器 Dispose 只覆盖已加入的部件）。</item>
/// </list>
/// </summary>
public sealed class Audit20261024P2FixTests
{
    private static string BodyOf(TestRequest request) => Encoding.UTF8.GetString(request.Body);

    // ---------------------------------------------------------------
    // 15：boundary 错误分类
    // ---------------------------------------------------------------

    [Fact]
    public async Task Multipart_BoundaryTooLong_ReportsOptionError()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        // RFC 2046 的 boundary 上限 70 字符：BCL 抛 ArgumentException。这是调用方的选项错误，
        // 必须报 ERR_BAD_OPTION_VALUE（构造在 try 外时会被 AxiosClient 收成 ERR_BAD_REQUEST）。
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(
            "upload",
            new { name = "alice" },
            c => c.ContentType = "multipart/form-data; boundary=" + new string('x', 80)));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("boundary", ex.Message);
        Assert.Empty(server.Received);   // 发送前就拦下
    }

    [Fact]
    public async Task Multipart_ValidCustomBoundary_StillWorks()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        await client.PostAsync(
            "upload",
            new { name = "alice" },
            c => c.ContentType = "multipart/form-data; boundary=----axios-net");

        var received = Assert.Single(server.Received);
        Assert.Contains("----axios-net", received.Headers["Content-Type"]);
        Assert.Contains("----axios-net", BodyOf(received));
    }

    [Fact]
    public async Task Multipart_LaterPartFails_StillReleasesUntouchedStreams()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var untouched = new MemoryStream("payload"u8.ToArray());

        // SortedDictionary 的枚举顺序确定：先装配非法字段名（抛错），此时后面那个裸流还没被
        // 包成 StreamContent，容器 Dispose 覆盖不到它 —— 装配失败的回收必须走进原始数据。
        var data = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["bad\"name"] = 1,
            ["zzz"] = untouched,
        };

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.PostAsync("upload", data, c => c.ContentType = "multipart/form-data"));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.False(untouched.CanRead);
        Assert.Empty(server.Received);
    }

    // ---------------------------------------------------------------
    // 16：charset 收口（序列化器自报 / 写头都覆盖）
    // ---------------------------------------------------------------

    [Fact]
    public async Task SerializerDeclaredCharset_ContradictingBodyEncoding_ReportsOptionError()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        // 自报的 MediaType 带 charset=iso-8859-1，而正文按 UTF-8 编码：入口预校验看不到
        // 序列化器的自报值（它发生在正文产出阶段），曾静默发出「头 latin1、正文 utf-8」。
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Serialization = new() { ContentSerializer = new CharsetDeclaringSerializer() },
        });

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.PostAsync("upload", new { a = 1 }));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("charset=iso-8859-1", ex.Message);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task SerializerWrittenCharsetHeader_ContradictingBodyEncoding_ReportsOptionError()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        // 序列化器在 Serialize 里写请求头：同样在正文产出之后才可见
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Serialization = new() { ContentSerializer = new HeaderWritingCharsetSerializer() },
        });

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.PostAsync("upload", new { a = 1 }));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("charset=iso-8859-1", ex.Message);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task SerializerDeclaredCharset_MatchingBodyEncoding_IsAccepted()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        // 自报与正文一致的 charset：放行，且头里保留该声明
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Serialization = new() { ContentSerializer = new Utf8CharsetDeclaringSerializer() },
        });

        await client.PostAsync("upload", new { a = 1 });

        Assert.Contains("charset=utf-8", Assert.Single(server.Received).Headers["Content-Type"]);
    }

    // ---------------------------------------------------------------
    // 1/2：params 收尾与嵌套键转义
    // ---------------------------------------------------------------

    [Fact]
    public async Task Params_NestedAndSpecialKeys_StayStableAcrossRequests()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        // 同一份 params 发两次：属性名后缀按属性缓存、字典键每请求转义，两条路径结果必须一致
        // （键名保留属性名原形；方括号按 axios 默认 encoder 的规则还原，空格转 %20）
        for (var i = 0; i < 2; i++)
        {
            await client.GetAsync("search", c => c.Params = new NestedParams
            {
                Filter = new Dictionary<string, object?> { ["a b"] = 1, ["c[d]"] = 2 },
                Page = 3,
            });
        }

        Assert.All(server.Received, r => Assert.Equal("Filter[a%20b]=1&Filter[c[d]]=2&Page=3", r.Query));
    }

    // ---------------------------------------------------------------
    // 5/6：transform 链写时复制
    // ---------------------------------------------------------------

    [Fact]
    public async Task ConcurrentRequests_PerRequestTransforms_KeepChainsIsolated()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        // 实例默认链 + 每请求追加：写时复制下并发请求各自分离，互不串味也不污染 Defaults
        client.Defaults.TransformRequest.Add((data, _) => "[" + data + "]");

        var requests = Enumerable.Range(0, 32).Select(i => client.PostAsync(
            "echo",
            "x",
            c => c.TransformRequest.Add((data, _) => data + i.ToString(CultureInfo.InvariantCulture))));

        await Task.WhenAll(requests);

        Assert.All(server.Received, r => Assert.Matches(@"^\[x\]\d+$", BodyOf(r)));
        Assert.Single(client.Defaults.TransformRequest);   // 实例链不被按请求追加污染
    }

    [Fact]
    public async Task GetUri_ReplayedConfigs_UseTargetInstanceBaseAddress_LikeRequestAsync()
    {
        await using var first = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        await using var second = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var source = new AxiosClient(new AxiosConfig { BaseAddress = first.Url });

        // 两种来源的配置：快捷方法（生效配置）与对象入口（合并结果）
        var fromShortcut = (await source.GetAsync("shortcut")).Config!;
        var fromConfig = (await source.RequestAsync(new AxiosConfig { Url = "object-form" })).Config!;

        using var target = new AxiosClient(new AxiosConfig { BaseAddress = second.Url });

        // GetUri 与 RequestAsync 必须给出同一个地址：签名 / 日志按 GetUri 算、
        // 请求按 RequestAsync 解析，两者错位就是静默的安全隐患。
        Assert.Equal(second.Url + "shortcut", target.GetUri(fromShortcut));
        Assert.Equal(second.Url + "object-form", target.GetUri(fromConfig));

        await target.RequestAsync(fromShortcut);
        await target.RequestAsync(fromConfig);

        Assert.Equal("/shortcut", second.Received[0].Path);
        Assert.Equal("/object-form", second.Received[1].Path);
        Assert.Equal(2, first.Received.Count);   // 源服务端只收到最初那两次
    }

    [Fact]
    public void ConfigClone_ListIsolation_HoldsInBothDirections()
    {
        var source = new AxiosConfig();
        source.TransformRequest.Add((data, _) => data);
        source.RemoveHeaders.Add("X-A");

        // 副本写：写时复制分离，源不受影响
        var clone = source.Clone();
        clone.TransformRequest.Add((data, _) => data);
        clone.RemoveHeaders.Add("X-B");

        Assert.Single(source.TransformRequest);
        Assert.Single(source.RemoveHeaders);
        Assert.Equal(2, clone.TransformRequest.Count);
        Assert.Equal(2, clone.RemoveHeaders.Count);

        // 源写：同样分离 —— 已克隆出去的副本看不到后续写入
        // （与旧「每请求整份拷贝」语义一致：隔离不靠副本，靠写入前分离）
        var snapshot = source.Clone();
        source.TransformRequest.Add((data, _) => data);
        source.RemoveHeaders.Add("X-C");

        Assert.Single(snapshot.TransformRequest);
        Assert.Single(snapshot.RemoveHeaders);
        Assert.Equal(2, source.TransformRequest.Count);
        Assert.Equal(2, source.RemoveHeaders.Count);
    }

    // ---------------------------------------------------------------
    // 9：回放不带上一次的按请求 Auth
    // ---------------------------------------------------------------

    [Fact]
    public async Task ReplayedEffectiveConfig_DoesNotCarryPerRequestAuth()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var source = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        var response = await source.GetAsync("x", c => c.Connection.Auth = new AxiosAuth("user", "pass"));

        Assert.Equal(TestJson.BasicAuth("user", "pass"), server.Received[0].Headers["Authorization"]);

        using var target = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        // Auth 是那一次请求的意图，不是「这份配置的内容」：回放不能把它带上
        await target.RequestAsync(response.Config!);

        Assert.False(server.Received[1].Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task ReplayedEffectiveConfig_UsesTargetInstanceDefaultAuth()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var source = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });
        var response = await source.GetAsync("x", c => c.Connection.Auth = new AxiosAuth("user", "pass"));

        using var target = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Connection = new() { Auth = new AxiosAuth("target", "tpw") },
        });

        await target.RequestAsync(response.Config!);

        // 目标实例的默认凭据生效（既不是源实例的按请求凭据，也不是空）
        Assert.Equal(TestJson.BasicAuth("target", "tpw"), server.Received[1].Headers["Authorization"]);
    }

    // ---------------------------------------------------------------
    // 测试模型与序列化器夹具
    // ---------------------------------------------------------------

    private sealed class NestedParams
    {
        public Dictionary<string, object?>? Filter { get; set; }

        public int Page { get; set; }
    }

    /// <summary>自报带 charset 的媒体类型（与正文实际编码矛盾）。</summary>
    private sealed class CharsetDeclaringSerializer : IContentSerializer
    {
        public string? MediaType => "application/json; charset=iso-8859-1";

        public string Serialize(object? value, Type type, AxiosConfig config) => """{"a":1}""";

        public object? Deserialize(string text, Type type, AxiosConfig config)
            => throw new InvalidOperationException("本夹具不参与响应解析。");
    }

    /// <summary>自报与正文一致的 charset（utf-8）。</summary>
    private sealed class Utf8CharsetDeclaringSerializer : IContentSerializer
    {
        public string? MediaType => "application/json; charset=utf-8";

        public string Serialize(object? value, Type type, AxiosConfig config) => """{"a":1}""";

        public object? Deserialize(string text, Type type, AxiosConfig config)
            => throw new InvalidOperationException("本夹具不参与响应解析。");
    }

    /// <summary>在 Serialize 里写带 charset 的 Content-Type 请求头。</summary>
    private sealed class HeaderWritingCharsetSerializer : IContentSerializer
    {
        public string? MediaType => null;

        public string Serialize(object? value, Type type, AxiosConfig config)
        {
            config.Headers.Set("Content-Type", "application/json; charset=iso-8859-1");
            return """{"a":1}""";
        }

        public object? Deserialize(string text, Type type, AxiosConfig config)
            => throw new InvalidOperationException("本夹具不参与响应解析。");
    }
}
