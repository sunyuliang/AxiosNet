using System.Text;
using AxiosNet.Internal.Response;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Requests;

/// <summary>
/// 流式正文的生命周期边界：释放后再读必须按 <see cref="Stream"/> 约定失败（<see cref="ObjectDisposedException"/>），
/// 不能把内部 <see cref="CancellationTokenSource"/> 的实现细节抛给调用方。
/// </summary>
public sealed class StreamLifecycleTests
{
    /// <summary>释放后读取：抛 <see cref="ObjectDisposedException"/>，消息不泄露内部同步原语。</summary>
    [Fact]
    public async Task ReadingAfterDispose_ThrowsObjectDisposed()
    {
        var payload = Encoding.UTF8.GetBytes(new string('a', 4096));
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            200, "OK", payload, "application/octet-stream"));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url + "x", c => c.ResponseType = ResponseType.Stream);
        var stream = (Stream)response.Data!;

        using (var reader = new StreamReader(stream, leaveOpen: true))
        {
            Assert.Equal(new string('a', 4096), await reader.ReadToEndAsync());
        }

        await stream.DisposeAsync();

        var ex = await Assert.ThrowsAsync<ObjectDisposedException>(() => stream.ReadAsync(new byte[16]).AsTask());

        Assert.DoesNotContain("CancellationTokenSource", ex.Message);
    }

    /// <summary>
    /// 关键路径：先用可取消的调用方令牌读一次（内部缓存了链接源），释放流，再用<b>同一个令牌</b>读。
    /// 命中缓存分支会直接取已释放链接源的令牌，必须在入口拦下。
    /// </summary>
    [Fact]
    public async Task ReadingAfterDispose_WithSameCallerToken_ThrowsObjectDisposed()
    {
        var payload = Encoding.UTF8.GetBytes(new string('a', 4096));
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            200, "OK", payload, "application/octet-stream"));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url + "x", c => c.ResponseType = ResponseType.Stream);
        var stream = (Stream)response.Data!;

        using var cts = new CancellationTokenSource();
        var buffer = new byte[512];

        // 第一次读：建立链接源缓存。
        Assert.True(await stream.ReadAsync(buffer, cts.Token) > 0);

        await stream.DisposeAsync();

        var ex = await Assert.ThrowsAsync<ObjectDisposedException>(
            () => stream.ReadAsync(buffer, cts.Token).AsTask());

        Assert.DoesNotContain("CancellationTokenSource", ex.Message);
    }

    /// <summary>
    /// 预算在构造时即耗尽（<c>remaining</c> 为零）的流：释放后同样是「流已关闭」，
    /// 不能因为预算分支先跑而报成超时。
    /// </summary>
    [Fact]
    public async Task ReadingAfterDispose_BudgetExhausted_ThrowsObjectDisposed()
    {
        var stream = new TimeoutStream(
            new MemoryStream(new byte[16]),
            TimeSpan.Zero,
            CancellationToken.None,
            new AxiosConfig(),
            new HttpRequestMessage(HttpMethod.Get, "http://example.invalid/x"));

        await stream.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => stream.ReadAsync(new byte[4]).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => stream.WriteAsync(new byte[4]).AsTask());
        Assert.Throws<ObjectDisposedException>(() => stream.Flush());
    }

    /// <summary>
    /// 调用方拿到流式正文后既不读也不释放、直接把引用丢掉：对象会被回收，但连接租约与并发槽位
    /// 只在流的 <c>Dispose</c> 里归还，计数不会自己归零——终结器必须兜底归还，否则该端点永久不可驱逐。
    /// </summary>
    [Fact]
    public async Task AbandonedResponseStream_ReleasesLeaseOnFinalize()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            200, "OK", Encoding.UTF8.GetBytes("ok"), "text/plain"));
        using var client = new AxiosClient();

        var weak = AcquireAndAbandon(client, server.Url + "x");

        // 终结器不在 GC.Collect() 内同步跑完：先收集、再等终结队列清空，循环几次确保排到。
        for (var i = 0; i < 5 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(weak.IsAlive, "流对象应已被回收（这条用例观察的是回收后的租约归还，不是强引用）。");
        Assert.All(client.Pool!.GetStatistics().Endpoints, endpoint => Assert.Equal(0, endpoint.ActiveLeases));
    }

    /// <summary>在独立方法里取得流并立刻丢弃引用，避免调用方栈帧把局部变量当根保活。</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference AcquireAndAbandon(AxiosClient client, string url)
    {
        var response = client.GetAsync<Stream>(url).GetAwaiter().GetResult();
        return new WeakReference(response.Data);
    }
}
