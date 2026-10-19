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
            ParamsSerializer = p =>
            {
                var d = (Dictionary<string, object?>)p!;
                return "ids=" + string.Join(",", (int[])d["ids"]!);
            }
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
}
