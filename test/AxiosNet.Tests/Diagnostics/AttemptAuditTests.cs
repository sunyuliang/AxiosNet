using System.Diagnostics;
using AxiosNet.Circuit;
using AxiosNet.Pipeline;
using AxiosNet.Retry;
using AxiosNet.Telemetry;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Diagnostics;

/// <summary>
/// 每次尝试的审计：<see cref="Attempt.Audit"/> 在终端前后计时，按尝试发 AttemptStop / AttemptFailed 事件。
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class AttemptAuditTests
{
    // ------------------------------------------------------------------
    // 每次尝试一条
    // ------------------------------------------------------------------

    [Fact]
    public async Task Audit_EmitsOneEventPerAttempt()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) <= 2
            ? TestJson.Response(new { error = "boom" }, statusCode: 503)
            : TestJson.Response(new { ok = true }));

        var path = UniquePath("per-attempt");
        using var probe = new TelemetryProbe(server.Port, path);

        // 3 次尝试 = 首次 + 2 次重试。
        using var client = new AxiosClient(
            new AxiosConfig { Retry = { Policy = new RetryPolicy(2, TimeSpan.Zero) } },
            pipeline: AxiosAttemptPipeline.Default.Append(Attempt.Audit));

        var response = await client.GetAsync(server.Url + path);

        Assert.Equal(200, response.Status);

        var entries = AttemptEntries(probe);
        Assert.Equal(3, entries.Length);
        Assert.Equal(
            new[]
            {
                AxiosTelemetry.AttemptFailedEventName,
                AxiosTelemetry.AttemptFailedEventName,
                AxiosTelemetry.AttemptStopEventName
            },
            entries.Select(e => e.Name));

        var payloads = Payloads(entries);
        Assert.Equal(new[] { 1, 2, 3 }, payloads.Select(p => p.AttemptNumber));
        Assert.Equal(new int?[] { 503, 503, 200 }, payloads.Select(p => p.Status));
    }

    // ------------------------------------------------------------------
    // 失败分支带状态码 / 错误码
    // ------------------------------------------------------------------

    [Fact]
    public async Task Audit_FailedAttemptCarriesStatusAndErrorCode()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = "boom" }, statusCode: 500));
        var path = UniquePath("failed");
        using var probe = new TelemetryProbe(server.Port, path);
        using var client = new AxiosClient(pipeline: AxiosAttemptPipeline.Default.Append(Attempt.Audit));

        var exception = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + path));

        var entry = Assert.Single(AttemptEntries(probe));
        Assert.Equal(AxiosTelemetry.AttemptFailedEventName, entry.Name);

        var payload = Assert.IsType<AxiosAttemptEvent>(entry.Payload);
        Assert.Equal(1, payload.AttemptNumber);
        Assert.Equal(500, payload.Status);
        Assert.Equal(AxiosException.ErrBadResponse, payload.ErrorCode);
        Assert.Same(exception, payload.Exception);
        Assert.True(payload.Elapsed > TimeSpan.Zero);
    }

    // ------------------------------------------------------------------
    // 单次尝试耗时不含退避
    // ------------------------------------------------------------------

    [Fact]
    public async Task Audit_ElapsedExcludesRetryDelay()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) == 1
            ? TestJson.Response(new { error = "boom" }, statusCode: 503)
            : TestJson.Response(new { ok = true }));

        var path = UniquePath("elapsed");
        using var probe = new TelemetryProbe(server.Port, path);

        // 2s 退避足够把「尝试耗时」和「整请求耗时」区分开；上界只兜数量级退化（本机往返不可能到 1s）。
        using var client = new AxiosClient(
            new AxiosConfig { Retry = { Policy = new RetryPolicy(1, TimeSpan.FromSeconds(2)) } },
            pipeline: AxiosAttemptPipeline.Default.Append(Attempt.Audit));

        var clock = Stopwatch.StartNew();
        await client.GetAsync(server.Url + path);
        clock.Stop();

        var payloads = Payloads(AttemptEntries(probe));
        Assert.Equal(2, payloads.Length);
        Assert.All(
            payloads,
            payload => Assert.True(
                payload.Elapsed < TimeSpan.FromSeconds(1),
                $"单次尝试耗时 {payload.Elapsed.TotalMilliseconds:0}ms 不应包含 2s 退避"));
        Assert.True(
            clock.Elapsed >= TimeSpan.FromMilliseconds(1800),
            $"整请求耗时 {clock.Elapsed.TotalMilliseconds:0}ms 应包含退避");
    }

    // ------------------------------------------------------------------
    // 流式响应只到响应头
    // ------------------------------------------------------------------

    [Fact]
    public async Task Audit_StreamingAttempt_StopsAtResponseHeaders()
    {
        // 正文要 5s 才到：如果审计/终端等正文，这里 2s 就会超时；只判断「响应头一到就返回」。
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }) with
        {
            BodyDelay = TimeSpan.FromSeconds(5)
        });

        var path = UniquePath("stream");
        using var probe = new TelemetryProbe(server.Port, path);
        using var client = new AxiosClient(pipeline: AxiosAttemptPipeline.Default.Append(Attempt.Audit));

        var response = await client
            .GetAsync(server.Url + path, c => c.ResponseType = ResponseType.Stream)
            .WaitAsync(TimeSpan.FromSeconds(2));

        var payload = Assert.Single(Payloads(AttemptEntries(probe)));
        Assert.Equal(200, payload.Status);
        Assert.True(
            payload.Elapsed < TimeSpan.FromSeconds(1),
            $"流式审计应停在响应头，实际 {payload.Elapsed.TotalMilliseconds:0}ms");

        // 审计不能接管或释放正文：调用方仍应拿到可读的流。
        await using var body = Assert.IsAssignableFrom<Stream>(response.Data);
        Assert.True(body.CanRead);
    }

    // ------------------------------------------------------------------
    // 覆盖范围：终端之前的拒绝不算一次尝试
    // ------------------------------------------------------------------

    [Fact]
    public async Task Audit_DoesNotCoverAttemptsRejectedBeforeTheTerminal()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        var path = UniquePath("circuit");
        using var probe = new TelemetryProbe(server.Port, path);

        using var client = new AxiosClient(
            new AxiosConfig { Circuit = { Policy = new AlwaysRejecting() } },
            pipeline: AxiosAttemptPipeline.Default.Append(Attempt.Audit));

        var exception = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + path));

        Assert.Equal(AxiosException.ErrCircuitOpen, exception.Code);
        Assert.Empty(AttemptEntries(probe));
        Assert.Empty(server.Received);
    }

    // ------------------------------------------------------------------
    // 遥测关闭时审计是纯直通
    // ------------------------------------------------------------------

    [Fact]
    public async Task Audit_TelemetryDisabled_DoesNotEmitAttempts()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        var path = UniquePath("disabled");
        using var probe = new TelemetryProbe(server.Port, path);

        using var client = new AxiosClient(
            new AxiosConfig { Telemetry = { Enabled = false } },
            pipeline: AxiosAttemptPipeline.Default.Append(Attempt.Audit));

        var response = await client.GetAsync(server.Url + path);

        Assert.Equal(200, response.Status);
        Assert.Empty(AttemptEntries(probe));
    }

    // ------------------------------------------------------------------
    // 对冲：败者被收掉不算失败，只有胜者一条事件
    // ------------------------------------------------------------------

    [Fact]
    public async Task Audit_HedgedRequest_DoesNotReportTheDiscardedBranchAsFailure()
    {
        var arrivals = 0;
        await using var server = TestHttpServer.Start(async _ =>
        {
            if (Interlocked.Increment(ref arrivals) == 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300));
            }

            return TestJson.Response(new { ok = true });
        });

        var path = UniquePath("hedge");
        using var probe = new TelemetryProbe(server.Port, path);
        using var client = new AxiosClient(
            new AxiosConfig { Hedging = { MaxHedges = 1, Delay = TimeSpan.Zero } },
            pipeline: AxiosAttemptPipeline.Default.Append(Attempt.Audit));

        var response = await client.GetAsync(server.Url + path);

        Assert.Equal(200, response.Status);

        // 败者是被胜出者取消的：那是清理，不是下游故障 —— 发出去会让健康的对冲请求恒有一半「尝试失败」。
        var payload = Assert.Single(Payloads(AttemptEntries(probe)));
        Assert.Equal(200, payload.Status);
        Assert.Equal(1, payload.AttemptNumber);
        Assert.Equal(0, payload.EndpointIndex);
    }

    // ------------------------------------------------------------------
    // 换端点：每个端点一条事件，尝试号相同、端点序号不同
    // ------------------------------------------------------------------

    [Fact]
    public async Task Audit_Failover_ReportsTheEndpointIndex()
    {
        await using var primary = TestHttpServer.Start(_ =>
            TestJson.Response(new { error = "boom" }, statusCode: 500));
        await using var mirror = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        var path = UniquePath("failover");
        using var probe = new TelemetryProbe(primary.Port, path);
        using var client = new AxiosClient(
            new AxiosConfig
            {
                BaseAddress = primary.Url,
                Failover = new() { Endpoints = new[] { new Uri(mirror.Url) } }
            },
            pipeline: AxiosAttemptPipeline.Default.Append(Attempt.Audit));

        var response = await client.GetAsync(path);

        Assert.Equal(200, response.Status);

        // 换端点不推进尝试号：两条事件同属一次尝试，靠端点序号区分主端点与第 1 个备用端点。
        var payloads = Payloads(AttemptEntries(probe));
        Assert.Equal(2, payloads.Length);
        Assert.All(payloads, payload => Assert.Equal(1, payload.AttemptNumber));
        Assert.Equal(new[] { 0, 1 }, payloads.Select(payload => payload.EndpointIndex));
        Assert.Equal(new int?[] { 500, 200 }, payloads.Select(payload => payload.Status));
    }

    private static TelemetryEvent[] AttemptEntries(TelemetryProbe probe)
        => probe.Events.Where(e => IsAttemptEvent(e.Name)).ToArray();

    private static AxiosAttemptEvent[] Payloads(TelemetryEvent[] entries)
        => entries.Select(e => Assert.IsType<AxiosAttemptEvent>(e.Payload)).ToArray();

    private static bool IsAttemptEvent(string name)
        => name is AxiosTelemetry.AttemptStopEventName or AxiosTelemetry.AttemptFailedEventName;

    private static string UniquePath(string name) => $"attempt-audit/{name}-{Guid.NewGuid():N}";

    /// <summary>永远拒绝的熔断策略：验证「拒绝发生在审计之外」。</summary>
    private sealed class AlwaysRejecting : IAxiosCircuitPolicy
    {
        public bool ShouldAllow(AxiosCircuitContext context) => false;

        public TimeSpan GetRetryAfter(AxiosCircuitContext context) => TimeSpan.Zero;

        public void Record(AxiosCircuitContext context)
        {
        }
    }
}
