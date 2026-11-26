using AxiosNet.Cache;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Caching;

/// <summary>
/// <c>Cache-Control</c> 指令解析的边界输入：引号、逗号与畸形头值的取舍。
/// 解析结果决定「能不能落库」，吞掉 <c>no-store</c> 等于把不该缓存的响应缓存下来。
/// </summary>
public sealed class CacheControlDirectivesEdgeTests
{
    /// <summary>直接压解析器本身（内部成员对测试项目可见）。</summary>
    private static CacheControlDirectives Parse(string value) => CacheControlDirectives.ParseValue(value);

    /// <summary>合法头值：指令与新鲜期照常识别。</summary>
    [Theory]
    [InlineData("max-age=60", 60, false)]
    [InlineData("public, max-age=60", 60, false)]
    [InlineData("no-store", null, true)]
    [InlineData("no-cache, must-revalidate", null, false)]
    public void WellFormed_KeepsDirectives(string value, int? maxAgeSeconds, bool noStore)
    {
        var parsed = Parse(value);

        Assert.Equal(noStore, parsed.NoStore);
        Assert.Equal(maxAgeSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null, parsed.MaxAge);
        Assert.Equal(value, parsed.Raw);
    }

    /// <summary>
    /// 引号不成对：后续指令不能被当成引号内容吞掉；未闭合引号之后（含其内）的新鲜期不采信，
    /// 但落在引号之前的指令本身是可信的，照常生效。
    /// </summary>
    [Theory]
    [InlineData("x=\"a,b, no-store", true, null)]
    [InlineData("max-age=60, x=\"a", false, 60)]
    [InlineData("x=\"a, max-age=3600", false, null)]
    [InlineData("max-age=60, x=\"a,b\"", false, 60)]
    public void MalformedQuotes_NeitherSwallowNorExtendFreshness(string value, bool noStore, int? maxAgeSeconds)
    {
        var parsed = Parse(value);

        Assert.Equal(noStore, parsed.NoStore);
        Assert.Equal(maxAgeSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null, parsed.MaxAge);
    }

    /// <summary>合法引号内的逗号不拆指令，扩展值原样保留（含 <c>quoted-pair</c> 解转义）。</summary>
    [Fact]
    public void WellFormedQuotedValue_KeepsCommaInExtension()
    {
        var parsed = Parse("x=\"a,b\", max-age=60");

        Assert.Equal("a,b", parsed.Extensions["x"]);
        Assert.Equal(TimeSpan.FromSeconds(60), parsed.MaxAge);
    }

    /// <summary>
    /// 端到端：未闭合引号后面的 <c>no-store</c> 必须仍然生效。若被吞掉，不该缓存的响应会被缓存。
    /// </summary>
    /// <remarks>只有 Server / Intersection 模式才解析响应头（默认 Configured 只信 TimeToLive），所以要显式切模式。</remarks>
    [Fact]
    public async Task UnclosedQuote_DoesNotDefeatNoStore()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ =>
        {
            Interlocked.Increment(ref calls);
            return new TestResponse(200, "OK", System.Text.Encoding.UTF8.GetBytes("{\"v\":1}"), "application/json",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    // max-age 在前（本来就会被解析到），畸形引号在后，no-store 被夹在引号内。
                    ["Cache-Control"] = "max-age=60, x=\"a,b, no-store"
                });
        });

        using var client = new AxiosClient(new AxiosConfig
        {
            Caching = new AxiosCache(new AxiosCacheOptions { FreshnessMode = CacheFreshnessMode.Server })
        });

        await client.GetAsync(server.Url + "x");
        await client.GetAsync(server.Url + "x");

        Assert.Equal(2, calls);
    }

    /// <summary>对照：同样在 Server 模式下，合法引号 + 无 no-store 的响应要能被缓存。</summary>
    [Fact]
    public async Task WellFormedQuotedExtension_StillCaches()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ =>
        {
            Interlocked.Increment(ref calls);
            return new TestResponse(200, "OK", System.Text.Encoding.UTF8.GetBytes("{\"v\":1}"), "application/json",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Cache-Control"] = "x=\"a,b\", max-age=60"
                });
        });

        using var client = new AxiosClient(new AxiosConfig
        {
            Caching = new AxiosCache(new AxiosCacheOptions { FreshnessMode = CacheFreshnessMode.Server })
        });

        await client.GetAsync(server.Url + "x");
        await client.GetAsync(server.Url + "x");

        Assert.Equal(1, calls);
    }
}
