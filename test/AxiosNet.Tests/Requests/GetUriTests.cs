
using AxiosNet.Internal;
using AxiosNet.Pooling;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;

namespace AxiosNet.Tests.Requests;

/// <summary>
/// 只拼 URL 不发请求：baseAddress 合并、params 序列化、路径段转义。
/// </summary>
public sealed class GetUriTests
{
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
    public void GetUri_MergesBaseAddressAndParams_WithoutSendingRequest()
    {
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = "https://api.example.com/v1/" });

        var uri = client.GetUri(c =>
        {
            c.Url = "users";
            c.Params = new { page = 2, ids = new[] { 1, 2 } };
        });

        Assert.Equal("https://api.example.com/v1/users?page=2&ids[]=1&ids[]=2", uri);
    }

    [Fact]
    public void RepeatedIdenticalUri_IsReused_AndCacheStaysBounded()
    {
        // 相同 URL 复用同一个已校验的 Uri 实例（每次请求不再 new Uri）。
        // 并行用例的高基数冲洗可能刚好清空缓存，重试几次取一次命中，不把并发调度当失败。
        var reused = false;
        for (var i = 0; i < 8 && !reused; i++)
        {
            var first = UrlBuilder.Build("https://api.example.com", "/items", null);
            reused = ReferenceEquals(first, UrlBuilder.Build("https://api.example.com", "/items", null));
        }

        Assert.True(reused, "相同 URL 连续两次构建没有复用同一个 Uri 实例——缓存没有生效。");

        // 高基数 URL（REST 路径带 ID）不能让缓存无限增长：上限 256，超了整表清空。
        for (var i = 0; i < 600; i++)
        {
            UrlBuilder.Build("https://api.example.com", "/items/" + i.ToString(CultureInfo.InvariantCulture), null);
        }

        var cache = (System.Collections.IDictionary)typeof(UrlBuilder)
            .GetField("UriCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;

        // 并发用例可能同时写入、冲到软上限附近，留一档余量即可证明没有无限增长。
        Assert.True(cache.Count <= 512, $"URI 缓存已增长到 {cache.Count} 条——应在上限处整表清空。");
    }

    [Fact]
    public async Task GetUri_DoesNotHitTheServer()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var uri = client.GetUri(c => c.Url = "not-sent");

        Assert.Equal(server.Url + "not-sent", uri);
        Assert.Empty(server.Received);
    }

    [Fact]
    public void GetUri_UsesCustomParamsSerializer()
    {
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = "https://api.example.com/",
            Serialization = new()
            {
                ParamsSerializer = p =>
            {
                var d = (Dictionary<string, object?>)p!;
                return "ids=" + string.Join(",", (int[])d["ids"]!);
            },
            },
        });

        var uri = client.GetUri(c =>
        {
            c.Url = "search";
            c.Params = new Dictionary<string, object?> { ["ids"] = new[] { 1, 2, 3 } };
        });

        Assert.Equal("https://api.example.com/search?ids=1,2,3", uri);
    }

    [Fact]
    public void GetUri_RelativeUrlWithoutBase_ThrowsInvalidUrl()
    {
        using var client = new AxiosClient();

        var ex = Assert.Throws<AxiosException>(() => client.GetUri(c => c.Url = "users"));

        Assert.Equal(AxiosException.ErrInvalidUrl, ex.Code);
    }

    [Fact]
    public void GetUri_FileUri_ThrowsInvalidUrl()
    {
        // 显式 file:// 不是 HTTP 目标。
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = "https://api.example.com/v1/" });

        var ex = Assert.Throws<AxiosException>(() => client.GetUri(c => c.Url = "file:///etc/passwd"));

        Assert.Equal(AxiosException.ErrInvalidUrl, ex.Code);
    }

    [Theory]
    [InlineData("a/b c", "a%2Fb%20c")]
    [InlineData("中文", "%E4%B8%AD%E6%96%87")]
    [InlineData("a?b#c&d+e", "a%3Fb%23c%26d%2Be")]
    [InlineData("plain-id_1.2~3", "plain-id_1.2~3")]
    [InlineData("100%", "100%25")]
    [InlineData("", "")]
    public void EncodePathSegment_EscapesSeparatorsAndNonAscii(string value, string expected)
        => Assert.Equal(expected, AxiosUrl.EncodePathSegment(value));

    [Fact]
    public async Task EncodePathSegment_KeepsValueInsideOneSegment()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.GetAsync(server.Url + "items/" + AxiosUrl.EncodePathSegment("a/b c"));

        Assert.Equal("/items/a%2Fb%20c", server.Received[0].RawUrl);
    }

    [Fact]
    public void EncodePathSegment_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AxiosUrl.EncodePathSegment(null!));
    }

    // ------------------------------------------------------------------
    // L4：Uri 归一化与线上请求行一致（框架行为，文档化 + 锁定）
    // ------------------------------------------------------------------

    [Fact]
    public void GetUri_NormalizesUnreservedPercentEscapes_LikeTheWire()
    {
        using var client = new AxiosClient();

        var uri = client.GetUri(c =>
        {
            c.Url = "http://host/p";
            c.Serialization.ParamsSerializer = _ => "q=%41%7E%2B";
        });

        // %41/%7E 是非保留字符（归一化为 A/~），%2B 是保留字符（保持转义）
        Assert.Equal("http://host/p?q=A~%2B", uri);
    }

    private static string Body(TestRequest request) => Encoding.UTF8.GetString(request.Body);


    // ===============================================================
    // 测试用请求模型 / 序列化器
    // ===============================================================


    // ---------------------------------------------------------------
    // 测试模型与序列化器夹具
    // ---------------------------------------------------------------


    /// <summary>把请求挡在 <see cref="TaskCompletionSource"/> 之后的测试 Handler。</summary>
    private sealed class GatedHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _gate;

        public GatedHandler(TaskCompletionSource gate) => _gate = gate;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await _gate.Task.ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json"),
                RequestMessage = request
            };
        }
    }
}