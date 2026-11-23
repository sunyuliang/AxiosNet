using System.Text;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Requests;

/// <summary>
/// 快捷方法的参数位护栏：<c>object? query</c> 能吞下一切，所以「把配置 / 令牌放到查询参数位置」
/// 必须在发请求之前被拦下并指路，而不是被当成请求正文序列化出去。
/// </summary>
public sealed class ShortcutArgumentGuardTests
{
    /// <summary>表单动词没有 <c>(url, config)</c> 形态：配置落到 <c>data</c> 上必须报错，不能当正文发。</summary>
    [Fact]
    public async Task FormVerb_ConfigInDataPosition_IsRejectedBeforeSending()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(
            () => client.PostFormAsync(server.Url + "x", new AxiosConfig()));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("RequestAsync(config)", ex.Message);
        Assert.Empty(server.Received); // 一个字都没发出去
    }

    /// <summary>
    /// 静态门面从来没有 <c>(url, config)</c> 形态：同一行代码在实例上走配置、在门面上原本会被当成正文，
    /// 这是「同一语法两种语义」的来源，必须报错。
    /// </summary>
    [Fact]
    public async Task Facade_ConfigInDataPosition_IsRejected()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        var ex = await Assert.ThrowsAsync<AxiosException>(
            () => Axios.GetAsync(server.Url + "x", new AxiosConfig()));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    /// <summary>令牌落到查询参数位置同样报错（与配置同一个口径，指路 <c>cancellationToken</c>）。</summary>
    [Fact]
    public async Task TokenInDataPosition_IsRejected()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();
        using var cts = new CancellationTokenSource();

        var ex = await Assert.ThrowsAsync<AxiosException>(
            () => client.GetAsync(server.Url + "x", query: cts.Token));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("cancellationToken", ex.Message);
        Assert.Empty(server.Received);
    }

    /// <summary>正文位置不受影响：对象、字典、字符串照旧当正文发。</summary>
    [Fact]
    public async Task NormalData_IsStillSentAsBody()
    {
        await using var server = TestHttpServer.Start(request => TestJson.Echo(request));
        using var client = new AxiosClient();

        var response = await client.PostAsync(server.Url + "x", new { name = "bob" });

        Assert.Equal(200, response.Status);
        Assert.Contains("\"name\":\"bob\"", Encoding.UTF8.GetString(Assert.Single(server.Received).Body));
    }
}
