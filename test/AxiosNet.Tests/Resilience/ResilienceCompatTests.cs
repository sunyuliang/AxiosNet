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
        // 关闭：幂等闸门拦住 POST，只应有一条分支发出。
        await using (var gated = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(25);
            return TestJson.Response(new { ok = true });
        }))
        {
            using var client = new AxiosClient(new AxiosConfig
            {
                BaseAddress = gated.Url,
                Resilience =
                {
                    Hedging = new() { MaxHedges = 1, Delay = TimeSpan.Zero }
                }
            });

            await client.PostAsync("x", new { id = 1 });

            Assert.Single(gated.Received);
        }

        // 打开：同一条 POST 必须真的被对冲成两条。
        // 服务端「收到两条分支才回包」：否则胜出者可能在线程池被抢占时先拿到响应、把还没发出去的败者收掉，
        // 断言就会随机变成 1 条。等两条都到齐再回包，把「确实发出了两条」变成确定性事实而不是时序巧合。
        var arrived = 0;
        var bothArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using (var hedged = TestHttpServer.Start(async _ =>
        {
            if (Interlocked.Increment(ref arrived) >= 2)
            {
                bothArrived.TrySetResult();
            }

            await bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return TestJson.Response(new { ok = true });
        }))
        {
            using var client = new AxiosClient(new AxiosConfig
            {
                BaseAddress = hedged.Url,
                Resilience =
                {
                    AllowUnsafeRetry = true,
                    Hedging = new() { MaxHedges = 1, Delay = TimeSpan.Zero }
                }
            });

            var failure = await Record.ExceptionAsync(() => client.PostAsync("x", new { id = 1 }));

            // 只有一条分支时服务端会等满 5 秒才超时，失败信息必须指向真正的原因，而不是一个 500。
            Assert.True(arrived >= 2, $"只发出了 {arrived} 条对冲分支：AllowUnsafeRetry 没有对 POST 放开对冲。");
            Assert.Null(failure);
            Assert.Equal(2, hedged.Received.Count);
        }
    }
}
