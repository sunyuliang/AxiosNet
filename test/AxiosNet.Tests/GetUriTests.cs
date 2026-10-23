using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 对应 axios.getUri：只组装最终 URL（baseURL + params），不发请求。
/// </summary>
public sealed class GetUriTests
{
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
        // 显式 file:// 不是 HTTP 目标：不能拼到 baseURL 后面，也不能交给连接池。
        // 这条同时覆盖了 Unix 下 Uri 把 "/items" 解析成 file:///items 的兜底分支。
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = "https://api.example.com/v1/" });

        var ex = Assert.Throws<AxiosException>(() => client.GetUri(c => c.Url = "file:///etc/passwd"));

        Assert.Equal(AxiosException.ErrInvalidUrl, ex.Code);
    }

    [Fact]
    public void StaticAxios_GetUri_WorksLikeFacade()
    {
        var uri = Axios.GetUri(c =>
        {
            c.BaseAddress = "https://example.com/";
            c.Url = "a";
            c.Params = new { q = "x y" };
        });

        Assert.Equal("https://example.com/a?q=x%20y", uri);
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

        // 不转义时 "a/b c" 会被拆成两段：items/a/b%20c
        await client.GetAsync(server.Url + "items/" + AxiosUrl.EncodePathSegment("a/b c"));

        Assert.Equal("/items/a%2Fb%20c", server.Received[0].RawUrl);
    }

    [Fact]
    public void EncodePathSegment_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AxiosUrl.EncodePathSegment(null!));
    }
}
