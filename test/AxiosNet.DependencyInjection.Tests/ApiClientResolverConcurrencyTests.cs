using AxiosNet.WebAPI.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace AxiosNet.DependencyInjection.Tests;

/// <summary>
/// 2026-10-24 审查修复回归（解析器锁外创建 + 释放循环鲁棒性）：
/// <list type="bullet">
/// <item>站点首次解析的用户 configure 与容器解析不再跑在解析器级锁内 ——
/// 「configure 里同步等待另一站点解析」不再死锁，慢 configure 不再阻塞全部站点；</item>
/// <item>单个站点释放失败不跳过其余站点的释放。</item>
/// </list>
/// </summary>
public sealed class ApiClientResolverConcurrencyTests
{
    private static ServiceProvider BuildProvider(Action<IServiceCollection> register)
    {
        var services = new ServiceCollection();
        register(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Get_ConfigureWaitingForAnotherSite_DoesNotDeadlock()
    {
        // configure 若跑在解析器级锁内：A 的 configure 同步等 B 创建、B 等同一把锁，会死锁。
        ServiceProvider? provider = null;
        using var ready = new ManualResetEventSlim(false);

        provider = BuildProvider(services =>
        {
            services.AddApiClient("a", "https://a.example.com", configure: _ =>
            {
                // 通知测试「已进入 configure」，然后同步等待另一站点解析完成
                ready.Set();
                var other = provider!.GetRequiredService<IApiClientResolver>().Get("b");
                if (other is null)
                {
                    throw new InvalidOperationException("站点 b 解析失败");
                }
            });
            services.AddApiClient("b", "https://b.example.com");
        });

        var resolver = provider.GetRequiredService<IApiClientResolver>();

        var getA = Task.Run(() => resolver.Get("a"));

        // configure 内的同步等待若死锁，这里超时失败。
        var completed = await Task.WhenAny(getA, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(getA, completed);

        var clientA = await getA;
        Assert.Equal("https://a.example.com", clientA.Axios.Defaults.BaseAddress);
        Assert.NotNull(resolver.Get("b"));
    }

    [Fact]
    public async Task Get_ConcurrentSameSite_CreatesExactlyOnce()
    {
        var created = 0;
        using var provider = BuildProvider(services =>
        {
            services.AddApiClient("solo", "https://solo.example.com", configure: _ =>
            {
                Interlocked.Increment(ref created);
                Thread.Sleep(50); // 放大创建窗口：并发 Get 若没有站点锁会建多个
            });
        });

        var resolver = provider.GetRequiredService<IApiClientResolver>();
        var clients = await Task.WhenAll(
            Task.Run(() => resolver.Get("solo")),
            Task.Run(() => resolver.Get("solo")),
            Task.Run(() => resolver.Get("solo")));

        Assert.Equal(1, created);
        Assert.Same(clients[0], clients[1]);
        Assert.Same(clients[1], clients[2]);
    }

    [Fact]
    public void Dispose_OneSiteFails_StillReleasesTheOthers()
    {
        // 解析器侧锁定可观测契约：容器退出时全部站点都被释放、释放后 Get 抛 ObjectDisposedException。
        // 「首个失败不跳过其余站点」的循环本体由 HolderDisposalTests 直接驱动（holder 是内部类型，
        // 无法从外部注入抛错，见该文件的 seam 说明）。
        using var provider = BuildProvider(services =>
        {
            services.AddApiClient("x", "https://x.example.com");
            services.AddApiClient("y", "https://y.example.com");
        });

        var resolver = provider.GetRequiredService<IApiClientResolver>();
        _ = resolver.Get("x");
        _ = resolver.Get("y");

        provider.Dispose();

        Assert.Throws<ObjectDisposedException>(() => resolver.Get("x"));
        Assert.Throws<ObjectDisposedException>(() => resolver.Get("y"));
    }
}
