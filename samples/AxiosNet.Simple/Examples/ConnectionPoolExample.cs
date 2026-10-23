using AxiosNet;
using AxiosNet.Pooling;

namespace AxiosNet.Simple.Examples;

/// <summary>
/// 12. 网络连接池：TCP 连接复用、并发上限、跨实例共享。
/// </summary>
public sealed class ConnectionPoolExample : IExample
{
    public int Number => 12;

    public string Title => "连接池：顺序请求复用单 TCP、并发受 MaxConnectionsPerServer 约束、多客户端共享";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        // 本示例的客户端与池故意每次运行新建：三个场景都要用「全新池」数连接数与峰值并发，
        // 静态复用会让第二次运行的统计从上一轮的热连接开始，期望值不再成立

        var baseUrl = context.BaseUrl;
        var server = context.Server;

        ConnectionPoolOptions CreateOptions() => new()
        {
            MaxConnectionsPerServer = 2,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            IdleHandlerLifetime = TimeSpan.FromMinutes(30)
        };

        // 12.1 顺序请求：独立新池，5 个请求只建立 1 条 TCP 连接
        using (var pool = new HttpConnectionPool(CreateOptions()))
        using (var pooled = new AxiosClient(pool: pool))
        {
            server.ResetStats();
            for (var i = 0; i < 5; i++)
            {
                await pooled.GetAsync(baseUrl);
            }

            var stats = pool.GetStatistics();
            Console.WriteLine($"  顺序 5 请求: 服务器接受 TCP 连接数 = {server.TotalConnections}（期望 1，连接被复用）");
            Console.WriteLine($"  池统计: origin 节点={stats.EndpointCount}, 累计租约={stats.TotalLeases}");
        }

        // 12.2 并发上限：独立新池，6 个慢请求 + MaxConnectionsPerServer=2 -> 总连接 2、峰值并发 2
        using (var pool = new HttpConnectionPool(CreateOptions()))
        using (var pooled = new AxiosClient(pool: pool))
        {
            server.ResetStats();
            var tasks = Enumerable.Range(0, 6)
                .Select(_ => pooled.GetAsync(baseUrl + "slow?ms=300"))
                .ToArray();
            await Task.WhenAll(tasks);

            Console.WriteLine($"  并发 6 慢请求: TCP 连接数 = {server.TotalConnections}，服务器峰值并发 = {server.PeakConcurrentRequests}（期望均为 2）");
        }

        // 12.3 多个 AxiosClient 共享同一个独立新池：顺序 2 请求仍只有 1 条 TCP
        using (var sharedPool = new HttpConnectionPool(CreateOptions()))
        using (var client1 = new AxiosClient(pool: sharedPool))
        using (var client2 = new AxiosClient(pool: sharedPool))
        {
            server.ResetStats();
            await client1.GetAsync(baseUrl);
            await client2.GetAsync(baseUrl);
            Console.WriteLine($"  两个客户端共享池: TCP 连接数 = {server.TotalConnections}（期望 1）");
        }
    }
}
