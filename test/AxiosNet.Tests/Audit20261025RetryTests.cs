using System.Diagnostics;
using System.Text;
using AxiosNet.Retry;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 弹性重试的第四轮审查：补齐此前没有覆盖的组合（流式响应 / multipart 部件 / 调用方自带正文 /
/// 流起点偏移 / 超时预算口径 / 最终错误），并锁定本轮修掉的两个缺陷。
/// <para>
/// 放进 <see cref="TimingCollection"/>：<c>TimeoutBudget_IsPerAttempt_NotForWholeRequest</c>
/// 靠服务端延迟的算术区分「每次尝试一份预算」与「整条请求一份预算」，与其它集合并行跑时
/// 调度抖动会吃掉余量（GitHub Actions 上曾因此假失败）。
/// </para>
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class Audit20261025RetryTests
{
    /// <summary>零等待策略：只验证重试语义，不让用例真的等。</summary>
    private static RetryPolicy Immediate(int maxRetries)
        => new(maxRetries, TimeSpan.Zero) { RetryNonIdempotentMethods = true };

    // ------------------------------------------------------------------
    // 本轮修复：multipart 部件里的 StreamContent 必须 fail-fast
    // ------------------------------------------------------------------

    [Fact]
    public async Task MultipartStreamContentPart_WithRetry_FailsFast()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = Immediate(2) } });

        // 调用方自带的 StreamContent 部件：库既拨不回它包着的流，也无从判断可 seek 与否。
        // 修复前：第二次发送由 BCL 抛「流已被消费」→ 翻译成 ERR_NETWORK → 还被当成网络错误
        // 反复重试（3 次全废），最终错误码与真实原因（正文不可重放）毫无关系。
        var part = new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes("part-payload")));

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(
            server.Url + "u",
            new Dictionary<string, object?> { ["f"] = part },
            c => c.ContentType = "multipart/form-data"));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task CallerProvidedMultipartContent_WithNestedStreamPart_FailsFast()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = Immediate(2) } });

        // 根级 data 本身是调用方构造的 MultipartFormDataContent：里面还嵌着 StreamContent。
        // 只判根级类型会漏掉这层嵌套，第二次发送就会发出空文件。
        using var multipart = new MultipartFormDataContent();
        multipart.Add(
            new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes("nested"))),
            "f",
            "a.txt");

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(server.Url + "u", multipart));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task MultipartStreamContentPart_WithoutRetry_IsSentAsIs()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var client = new AxiosClient();
        var part = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("part-payload")));

        await client.PostAsync(
            server.Url + "u",
            new Dictionary<string, object?> { ["f"] = part },
            c => c.ContentType = "multipart/form-data");

        // 检查只发生在「开了重试」的路径上：不重试时自建部件照常可用（正文只发一次）
        Assert.Single(server.Received);
        Assert.Contains("part-payload", Encoding.UTF8.GetString(server.Received[0].Body));
    }

    // ------------------------------------------------------------------
    // 本轮修复：等待时间的取值范围
    // ------------------------------------------------------------------

    [Fact]
    public async Task MaxDelayBeyondTimerLimit_IsRejectedBeforeSending()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 500));

        using var client = new AxiosClient(new AxiosConfig
        {
            // 等待走 Task.Delay（计时器队列上限约 49.7 天）：超限必须在发请求之前明确报配置错误，
            // 而不是等真的重试时漏出裸 ArgumentOutOfRangeException
            Retry = { Policy = new RetryPolicy(2, TimeSpan.Zero) { MaxDelay = TimeSpan.FromDays(60) } }
        });

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public void RetryStatusCodes_SetToNull_IsRejectedAtAssignment()
    {
        // 赋 null 后在 ShouldRetry 里 NRE 是在 catch 块内发生的：会把本次尝试的原始失败一起掩盖。
        // 非空引用属性一律在赋值处拒绝（与 AxiosConfig.ContentType / Headers 同一约定）。
        Assert.Throws<ArgumentNullException>(() => new RetryPolicy { RetryStatusCodes = null! });
    }

    // ------------------------------------------------------------------
    // 补齐的覆盖：重试与各种正文 / 响应形态的组合
    // ------------------------------------------------------------------

    [Fact]
    public async Task StreamResponse_IsRetried_AndBodyStaysReadable()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) == 1
            ? TestJson.Response(new { error = "boom" }, statusCode: 500)
            : TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = Immediate(1) } });

        var response = await client.GetAsync(server.Url + "x", c => c.ResponseType = ResponseType.Stream);
        using var reader = new StreamReader((Stream)response.Data!);
        var text = await reader.ReadToEndAsync();

        // 流式响应的重试只覆盖「响应头之前」的失败；成功那次交出的流必须仍然可读（租约没被前一次吞掉）
        Assert.Contains("ok", text);
        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task MultipartFormFile_IsResentOnRetry()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) == 1
            ? TestJson.Response(new { error = "boom" }, statusCode: 500)
            : TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = Immediate(2) } });

        await client.PostAsync(
            server.Url + "u",
            new { file = new FormFile("a.txt", Encoding.UTF8.GetBytes("alpha-payload"), "text/plain") },
            c => c.ContentType = "multipart/form-data");

        // 文件部件里的流由库装配（StreamContent），重试前要拨回起点，否则第二次只剩一个空文件
        Assert.Equal(2, server.Received.Count);
        Assert.Contains("alpha-payload", Encoding.UTF8.GetString(server.Received[1].Body));
    }

    [Fact]
    public async Task CallerProvidedStringContent_IsResent()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) == 1
            ? TestJson.Response(new { error = "boom" }, statusCode: 500)
            : TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = Immediate(2) } });

        // 调用方自带的 HttpContent（非 StreamContent）：按可重放对待，重试要原样再发一次
        await client.PostAsync(server.Url + "u", new StringContent("raw-payload", Encoding.UTF8, "text/plain"));

        Assert.Equal(2, server.Received.Count);
        Assert.Equal("raw-payload", Encoding.UTF8.GetString(server.Received[1].Body));
    }

    [Fact]
    public async Task StreamBodyStartingAtOffset_IsRewoundToOriginalOffset()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) == 1
            ? TestJson.Response(new { error = "boom" }, statusCode: 500)
            : TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = Immediate(2) } });
        using var body = new MemoryStream(Encoding.UTF8.GetBytes("XXXXpayload"));

        // 起点不是 0：拨回的必须是「构造时那个位置」，不是 0（否则会把调用方故意跳过的前缀也发出去）
        body.Position = 4;

        await client.PostAsync(server.Url + "u", body, c => c.ContentType = "text/plain");

        Assert.Equal(2, server.Received.Count);
        Assert.Equal("payload", Encoding.UTF8.GetString(server.Received[0].Body));
        Assert.Equal("payload", Encoding.UTF8.GetString(server.Received[1].Body));
    }

    /// <summary>
    /// Timeout 是「每次尝试」的预算，不是「整条请求（含重试）」的总预算。
    /// <para>
    /// 口径的算术必须让两种实现在时间轴上分叉，而不是靠「差一点点」：
    /// 8 次尝试 × 300ms 服务端延迟 ≈ 2.4s，而预算是 2s ——
    /// 独立预算时每次 300ms 远小于 2000ms（余量 1700ms）；共享预算时第 7 次尝试起跑
    /// 已经耗掉 1.8s，只剩 200ms 却要等 300ms，必然超时。
    /// </para>
    /// <para>
    /// 余量不能再收紧：CI 满载时 <c>Task.Delay</c> 与线程池注入可能晚到数百毫秒
    /// （实测记录见 <c>StreamCancellationTests</c> 的预算注释）。此前是「4 次 × 300ms + 1000ms 预算」，
    /// 单次余量只有 700ms，GitHub Actions 上曾把「实现正确但机器忙」判成
    /// 「超时预算被整条请求共享」。因此这里同时做两件事：把单次余量抬到 1700ms，
    /// 并把整类放进 <see cref="TimingCollection"/> 独占运行。
    /// </para>
    /// </summary>
    [Fact]
    public async Task TimeoutBudget_IsPerAttempt_NotForWholeRequest()
    {
        const int attempts = 8;
        const int attemptDelayMs = 300;
        var timeout = TimeSpan.FromMilliseconds(2000);

        var calls = 0;
        await using var server = TestHttpServer.Start(async _ =>
        {
            var call = Interlocked.Increment(ref calls);
            await Task.Delay(attemptDelayMs);
            return call < attempts
                ? TestJson.Response(new { error = "boom" }, statusCode: 500)
                : TestJson.Response(new { ok = true });
        });

        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = Immediate(attempts - 1) } });

        var clock = Stopwatch.StartNew();
        var response = await client.GetAsync(server.Url + "x", c => c.Timeout = timeout);
        clock.Stop();

        Assert.Equal(200, response.Status);
        Assert.Equal(attempts, server.Received.Count);

        // 用例的区分力靠这条不变式：总耗时（≈2.4s）必须真的超过 Timeout。
        // 若没超过（服务端延迟被吞掉 / 尝试次数不对），本用例就什么都没证明 ——
        // 那种「绿」比红更危险，所以显式断言出来。
        Assert.True(
            clock.Elapsed > timeout,
            $"总耗时 {clock.Elapsed} 未超过 Timeout {timeout}：本用例没有区分「每次尝试一份预算」与"
            + "「整条请求一份预算」的能力（检查服务端延迟是否生效、尝试次数是否正确）。");
    }

    [Fact]
    public async Task AllAttemptsFail_LastAttemptErrorIsSurfaced()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) == 1
            ? TestJson.Response(new { error = "first" }, statusCode: 500)
            : TestJson.Response(new { error = "second" }, statusCode: 503));

        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = Immediate(1) } });

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x"));

        // 重试用尽后抛的是「最后一次」的失败，不是第一次的（否则调用方会误判服务端当前状态）
        Assert.Equal(503, ex.Status);
        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task UploadProgress_AcrossRetry_StaysMonotonicAndPercentCapped()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) == 1
            ? new TestResponse(503)
            : TestJson.Response(new { ok = true }));

        var events = new List<AxiosProgressEvent>();
        using var client = new AxiosClient(new AxiosConfig { Retry = { Policy = Immediate(1) } });

        var payload = new byte[64 * 1024];
        var response = await client.PostAsync(server.Url + "u", payload, c =>
        {
            c.ContentType = AxiosContentType.OctetStream;
            c.Callbacks.OnUploadProgress = events.Add;
        });

        Assert.Equal(200, response.Status);
        Assert.Equal(2, calls);

        // 第二轮重发同一份正文：Loaded 是累计真实发送量（2×total，单调不回落），
        // 而 Progress 自己封顶 100%，调用方不会看到 >100% 的百分比。
        Assert.NotEmpty(events);
        Assert.Equal(payload.Length * 2, events[^1].Loaded);
        Assert.All(events, e => Assert.True(e.Progress is null || e.Progress <= 1d));
    }
}
