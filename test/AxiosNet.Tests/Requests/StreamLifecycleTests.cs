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
}
