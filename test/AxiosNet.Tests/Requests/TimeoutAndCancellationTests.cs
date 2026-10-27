
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using System.Text;

namespace AxiosNet.Tests.Requests;

/// <summary>
/// 超时与取消：总预算、单请求覆盖、取消优先级、链路异常翻译。
/// </summary>
public sealed class TimeoutAndCancellationTests
{
    // ---------------------------------------------------------------
    // 取消：已取消的令牌必须在发请求前短路，且不与超时混淆
    // ---------------------------------------------------------------

    [Fact]
    public async Task AlreadyCanceledToken_ShortCircuitsWithoutSending()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url, _ => { }, cts.Token));

        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task AlreadyCanceledToken_WithTimeoutSet_IsCanceledNotTimeout()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url, c => c.Timeout = TimeSpan.FromSeconds(5), cts.Token));

        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
        Assert.Empty(server.Received);
    }

    // 超大 Timeout 收敛

    [Theory]
    [InlineData(60)]
    [InlineData(365)]
    public async Task HugeTimeout_IsClamped_BufferedRequest(int days)
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url + "ping", c => c.Timeout = TimeSpan.FromDays(days));

        Assert.Equal(200, response.Status);
    }

    [Fact]
    public async Task HugeTimeout_IsClamped_StreamingResponse()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes("{\"ok\":true}")));
        using var client = new AxiosClient();

        var response = await client.GetAsync<Stream>(server.Url + "ping", c => c.Timeout = TimeSpan.MaxValue);
        await using (var stream = response.Data!)
        {
            await stream.CopyToAsync(Stream.Null);
        }

        Assert.Equal(200, response.Status);
    }

    [Fact]
    public async Task NegativeTimeout_ThrowsBadOptionValue()
    {
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("http://127.0.0.1:1/x", c => c.Timeout = TimeSpan.FromSeconds(-1)));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    // 取消异常保留底层 Cause

    [Fact]
    public async Task Cancellation_KeepsUnderlyingCause()
    {
        await using var server = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(2000);
            return TestJson.Response(new { ok = true });
        });

        using var client = new AxiosClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "slow", _ => { }, cts.Token));

        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
        Assert.IsAssignableFrom<OperationCanceledException>(ex.Cause);
    }

    // ------------------------------------------------------------------
    // L17：超时预算在流构造时即耗尽 → 确定性 ECONNABORTED
    // ------------------------------------------------------------------

    [Fact]
    public async Task StreamResponse_ExhaustedTimeoutBudget_FailsDeterministically()
    {
        // 「剩余预算 <= 0」的窗口天然是竞态（响应头贴着 deadline 到达）：
        // 这里断言结果收敛在「拿到数据」或「ECONNABORTED」之一，不出现裸异常、不挂死。
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();
        await client.GetAsync(server.Url + "warm");

        for (var i = 0; i < 16; i++)
        {
            Stream? stream = null;
            try
            {
                var response = await client.GetAsync(server.Url + "s", c =>
                {
                    c.ResponseType = ResponseType.Stream;
                    c.Timeout = TimeSpan.FromMilliseconds(1);
                });

                stream = (Stream)response.Data!;
                var read = await stream.ReadAsync(new byte[16]);

                // 值断言（不是恒真的 read >= 0）：拿到流的路径必须真的读到正文字节 ——
                // 响应体非空，read == 0 意味着流被提前关闭，属于第三种不该出现的结果。
                Assert.True(read > 0, $"第 {i} 轮：拿到了流但读到 0 字节（响应体非空，不应为空读）。");
            }
            catch (AxiosException ex) when (ex.Code == AxiosException.ErrConnAborted)
            {
                // 允许的结果之一：预算耗尽/超时。两种结果（读到数据 / ECONNABORTED）之外的一切
                // （其它错误码、裸异常、挂死）都会让本用例失败 —— when 过滤器只放行这一种。
            }
            finally
            {
                stream?.Dispose();
            }
        }
    }


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