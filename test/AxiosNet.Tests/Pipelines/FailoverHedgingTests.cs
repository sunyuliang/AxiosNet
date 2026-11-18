using System.Text.Json;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Pipelines;

/// <summary>
/// 换端点与对冲叠加：备用端点在主端点那组分支全部失败后才接手，接手时自己再展开一组分支。
/// </summary>
/// <remarks>
/// 断言的是「两条分支都在对手返回之前发出去」，属调度时序：与其它集合并行跑时会被抢走 CPU 与线程池线程，
/// 先成功的那条会在对手还没上路时就取消它，正确实现也会测不到，故整类进 <see cref="TimingCollection"/>。
/// </remarks>
[Collection(TimingCollection.Name)]
public sealed class FailoverHedgingTests
{
    [Fact]
    public async Task Failover_WithHedging_EachEndpointGetsItsOwnBranches()
    {
        // 两个端点都压一点延迟：两条分支必须真的都发出去，否则先成功的那条会在对手还在路上时就把它取消，
        // 「每个端点各得一组分支」就测不到了。
        await using var primary = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            return new TestResponse(503, Body: Array.Empty<byte>());
        });
        await using var mirror = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            return TestJson.Response(new { mirror = true });
        });

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = primary.Url,
            Resilience =
            {
                Failover = new() { Endpoints = new[] { new Uri(mirror.Url) } },
                Hedging = new() { MaxHedges = 1, Delay = TimeSpan.Zero }
            }
        });

        var response = await client.GetAsync("x");

        // 备用端点在对冲之外：主端点先跑完自己那组分支（2 条），全失败才换端点，镜像再拿一组。
        Assert.Equal(200, response.Status);
        Assert.True(Assert.IsType<JsonElement>(response.Data).GetProperty("mirror").GetBoolean());
        Assert.Equal(2, primary.Received.Count);
        Assert.Equal(2, mirror.Received.Count);
    }
}
