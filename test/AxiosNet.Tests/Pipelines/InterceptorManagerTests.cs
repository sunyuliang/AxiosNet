using AxiosNet.Interceptors;

namespace AxiosNet.Tests.Pipelines;

/// <summary>
/// 拦截器管理器本体（不发请求）：同步 / 异步两种注册重载、id 弹出、清空与计数。
/// 与请求管线配合的行为见 <see cref="InterceptorTests"/>。
/// </summary>
public sealed class InterceptorManagerTests
{
    [Fact]
    public void Managers_TrackIdsAndCountInBothDirections()
    {
        var interceptors = new AxiosInterceptors();

        // 请求方向：同步 / 异步两个注册重载共用同一套 id 序列
        var firstRequest = interceptors.Request.Use(c => c);
        var secondRequest = interceptors.Request.Use((AxiosConfig c) => Task.FromResult(c));

        Assert.True(firstRequest > 0);
        Assert.True(secondRequest > firstRequest);
        Assert.Equal(2, interceptors.Request.Count);

        Assert.True(interceptors.Request.Eject(firstRequest));
        Assert.False(interceptors.Request.Eject(firstRequest));
        Assert.Equal(1, interceptors.Request.Count);

        interceptors.Request.Clear();
        Assert.Equal(0, interceptors.Request.Count);

        // 响应方向：同一套语义，Eject 弹第二个不会连带弹掉第一个
        var firstResponse = interceptors.Response.Use(r => r);
        var secondResponse = interceptors.Response.Use((AxiosResponse r) => Task.FromResult(r));

        Assert.Equal(2, interceptors.Response.Count);

        Assert.True(interceptors.Response.Eject(secondResponse));
        Assert.False(interceptors.Response.Eject(secondResponse));
        Assert.Equal(1, interceptors.Response.Count);

        interceptors.Response.Clear();
        Assert.Equal(0, interceptors.Response.Count);
    }

    [Fact]
    public void Managers_RejectNullHandlers()
    {
        var interceptors = new AxiosInterceptors();

        Assert.Throws<ArgumentNullException>(() => interceptors.Request.Use((Func<AxiosConfig, AxiosConfig>)null!));
        Assert.Throws<ArgumentNullException>(() => interceptors.Request.Use((Func<AxiosConfig, Task<AxiosConfig>>)null!));
        Assert.Throws<ArgumentNullException>(() => interceptors.Response.Use((Func<AxiosResponse, AxiosResponse>)null!));
        Assert.Throws<ArgumentNullException>(() => interceptors.Response.Use((Func<AxiosResponse, Task<AxiosResponse>>)null!));
    }

    [Fact]
    public void Clear_AfterReuse_KeepsIdsMonotonic()
    {
        var interceptors = new AxiosInterceptors();

        var first = interceptors.Request.Use(c => c);
        interceptors.Request.Clear();
        var second = interceptors.Request.Use(c => c);

        // id 单调递增：复用旧 id 会让「弹出自己」变成「弹出别人的拦截器」
        Assert.True(second > first);
        Assert.Equal(1, interceptors.Request.Count);
    }
}
