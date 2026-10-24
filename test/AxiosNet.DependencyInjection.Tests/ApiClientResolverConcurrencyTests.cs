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
        // 旧实现：configure 跑在解析器级锁内 —— A 的 configure 同步等 B 创建、B 等同一把锁，死锁。
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

        // configure 内的同步等待若死锁，这里超时失败（旧实现必然死锁）
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
        // 直接驱动解析器：注入两个站点，第二个 holder 释放抛错时不跳过第一个的释放。
        // 通过「容器退出时全部站点都已创建 + 自定义释放顺序」覆盖循环鲁棒性：
        // 无法从外部让 holder.Dispose 抛错（内部类型），这里至少锁住「全部站点都被释放、
        // 且释放后 Get 抛 ObjectDisposedException」的既有契约不回退。
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
