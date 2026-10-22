using System.Text;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 2026-10-22 P2 修复：上传进度的跨次累计、transform 链的线程安全不依赖调用方传入的集合类型、
/// Basic 认证用户名的 RFC 7617 约束。
/// </summary>
public sealed class Audit20261022P2FixTests
{
    // ------------------------------------------------------------------
    // 上传进度：307 重定向会重发同一份正文，进度必须单调推进而不是掉回 0
    // ------------------------------------------------------------------

    [Fact]
    public async Task UploadProgress_AccumulatesAcrossRedirect()
    {
        await using var server = TestHttpServer.Start(request => request.Path == "/redirect"
            ? new TestResponse(
                307,
                "Temporary Redirect",
                Headers: new Dictionary<string, string> { ["Location"] = "/final" })
            : new TestResponse());

        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var payload = new byte[256 * 1024];
        var loaded = new List<long>();
        var progresses = new List<double?>();

        await client.PostAsync("redirect", payload, c =>
        {
            c.ContentType = AxiosContentType.OctetStream;
            c.OnUploadProgress = e =>
            {
                loaded.Add(e.Loaded);
                progresses.Add(e.Progress);
            };
        });

        Assert.Equal(2, server.Received.Count);

        Assert.NotEmpty(loaded);

        // 单调不减：旧实现每次重发都新建计量器，第二次会从 0 重新报
        for (var i = 1; i < loaded.Count; i++)
        {
            Assert.True(loaded[i] >= loaded[i - 1], $"进度回跳：{loaded[i - 1]} → {loaded[i]}");
        }

        // 跨次累计：两次发送的总字节都会计入（旧实现的上界是单份 payload）
        Assert.True(loaded[^1] > payload.Length, $"最后一次上报 {loaded[^1]} 未跨次累计");

        // 百分比封顶 100%：累计超过 total 时不能报出 >1 的进度
        Assert.All(progresses, p => Assert.True(p is null or <= 1d));
    }

    // ------------------------------------------------------------------
    // transform 链：setter 传任何 IList 都要保证线程安全（枚举走快照）
    // ------------------------------------------------------------------

    [Fact]
    public async Task TransformRequest_SetterWrapsPlainList()
    {
        var list = new List<Func<object?, AxiosConfig, object?>> { (data, _) => data };
        var config = new AxiosConfig { TransformRequest = list };

        // 存进去的不是调用方那个 List：否则并发枚举会抛 InvalidOperationException
        Assert.NotSame(list, config.TransformRequest);
        Assert.Single(config.TransformRequest);

        var failures = 0;
        using var stop = new CancellationTokenSource();

        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    foreach (var _ in config.TransformRequest)
                    {
                    }
                }
                catch
                {
                    Interlocked.Increment(ref failures);
                }
            }
        });

        for (var i = 0; i < 5000; i++)
        {
            config.TransformRequest.Add((data, _) => data);
            if (config.TransformRequest.Count > 3)
            {
                config.TransformRequest.RemoveAt(0);
            }
        }

        stop.Cancel();
        await reader;

        Assert.Equal(0, failures);
    }

    [Fact]
    public void TransformResponse_SetterWrapsPlainList()
    {
        var list = new List<Func<object?, AxiosConfig, object?>> { (data, _) => data };
        var config = new AxiosConfig { TransformResponse = list };

        Assert.NotSame(list, config.TransformResponse);
        Assert.Single(config.TransformResponse);

        // 已是包装类型时不重复包装
        var existing = config.TransformResponse;
        config.TransformResponse = existing;
        Assert.Same(existing, config.TransformResponse);
    }

    // ------------------------------------------------------------------
    // Basic 认证：user-id 不允许冒号（RFC 7617 用第一个冒号做分隔）
    // ------------------------------------------------------------------

    [Fact]
    public void AxiosAuth_UsernameWithColon_Throws()
    {
        Assert.Throws<ArgumentException>(() => new AxiosAuth("dom\\user:admin", "pwd"));

        var auth = new AxiosAuth();
        Assert.Throws<ArgumentException>(() => auth.Username = "a:b");
    }

    [Fact]
    public async Task AxiosAuth_PasswordMayContainColon_AndHeaderIsCorrect()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        // 用 "/" 显式请求 baseAddress 本身（空串 / null 现在按非法地址拒绝）
        await client.GetAsync("/", c => c.Auth = new AxiosAuth("user", "p@ss:word"));

        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:p@ss:word"));
        Assert.Equal(expected, server.Received[0].Headers["Authorization"]);
    }
}
