#pragma warning disable CS0618 // 本文件刻意同时使用 1.x 旧分组入口验证兼容性

using AxiosNet.Retry;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Resilience;

/// <summary>
/// 阶段 A 对抗性护栏：治理分组收敛后，旧写法与新写法必须指向同一份设置；
/// 非幂等放行只保留 <c>Resilience.AllowUnsafeRetry</c> 一个开关，重试与对冲共用；克隆不得共享 facade 所有权。
/// </summary>
public sealed class ResilienceCompatTests
{
    [Fact]
    public void LegacyAndCanonicalGroups_ShareTheSameSettingsObjects()
    {
        var config = new AxiosConfig();

        // 从旧入口写，新入口必须读到同一个策略。
        var policy = RetryPolicy.Constant(2, TimeSpan.Zero);
        config.Resilience.Retry.Policy = policy;
        Assert.Same(policy, config.Resilience.Retry.Policy);

        // 从新入口写，旧入口同样读到；且子对象不复制第二份状态。
        config.Resilience.Hedging.MaxHedges = 3;
        Assert.Equal(3, config.Resilience.Hedging.MaxHedges);
    }

    [Fact]
    public void ConcurrencyFacade_ReadsAndWritesTheSameLimitFields()
    {
        var config = new AxiosConfig();

        config.Resilience.Concurrency.MaxInFlight = 4;
        Assert.Equal(4, config.Resilience.Concurrency.MaxInFlight);

        config.Resilience.Concurrency.MaxQueue = 16;
        Assert.Equal(16, config.Resilience.Concurrency.MaxQueue);
    }

    [Fact]
    public void Clone_DoesNotRouteFacadeWritesBackToSource()
    {
        var source = new AxiosConfig
        {
            Resilience = { AllowUnsafeRetry = true }
        };

        var clone = source.Clone();
        clone.Resilience.Retry.Policy = RetryPolicy.Constant(1, TimeSpan.Zero);
        clone.Resilience.Hedging.MaxHedges = 1;

        // facade 持有 owner 引用：克隆侧必须重建，否则写入会落到 source 上。
        Assert.Null(source.Resilience.Retry.Policy);
        Assert.Equal(0, source.Resilience.Hedging.MaxHedges);
        Assert.NotNull(clone.Resilience.Retry.Policy);
    }

    [Fact]
    public async Task NonIdempotentPost_IsRetriedOnlyWhenAllowUnsafeRetryIsOn()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new { error = "boom" }, statusCode: 503));

        // 关闭：503 会抛出；重试层观察到异常，但幂等闸门拦住 POST，所以只发一次。
        using (var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Resilience =
            {
                Retry = { Policy = RetryPolicy.Constant(2, TimeSpan.Zero) }
            }
            }))
        {
            await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync("x", new { id = 1 }));
        }

        Assert.Single(server.Received);

        // 打开：同一个策略在 POST 上开始重试（首发 + 2 次重试）。
        using (var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Resilience =
            {
                AllowUnsafeRetry = true,
                Retry = { Policy = RetryPolicy.Constant(2, TimeSpan.Zero) }
            }
            }))
        {
            await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync("x", new { id = 1 }));
        }

        Assert.Equal(4, server.Received.Count);
    }

    [Fact]
    public async Task Hedging_OnNonIdempotentPost_IsGatedByTheSameSwitch()
    {
        await using var server = TestHttpServer.Start(async request =>
        {
            await Task.Delay(25);
            return TestJson.Response(new { ok = true });
        });

        // 关闭：POST 只发一条。
        using (var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Resilience =
            {
                Hedging = new() { MaxHedges = 1, Delay = TimeSpan.Zero }
            }
            }))
        {
            await client.PostAsync("x", new { id = 1 });
        }

        Assert.Equal(1, server.Received.Count);

        // 打开：同一条 POST 被对冲成两条。
        using (var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Resilience =
            {
                AllowUnsafeRetry = true,
                Hedging = new() { MaxHedges = 1, Delay = TimeSpan.Zero }
            }
            }))
        {
            await client.PostAsync("x", new { id = 1 });
        }

        Assert.Equal(3, server.Received.Count);
    }
}
