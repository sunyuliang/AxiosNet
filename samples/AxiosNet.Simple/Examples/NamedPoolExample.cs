using AxiosNet;
using AxiosNet.Pooling;

namespace AxiosNet.Simple.Examples;

/// <summary>30. 命名共享池：按名字取同一条进程级池，多个客户端复用同一套 Handler 与长连接。</summary>
public sealed class NamedPoolExample : IExample
{
    public int Number => 30;

    public string Title => "命名共享池：GetOrCreate 按名字复用一条池，客户端释放不影响它";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        var baseUrl = context.BaseUrl;
        var server = context.Server;

        // 池名由调用方指定：同名即同一条池。
        const string key = "demo.named-pool";

        // options 只在首次创建时生效：这里相当于启动时的一次性配置。
        var pool = HttpConnectionPool.GetOrCreate(key, new ConnectionPoolOptions
        {
            MaxConnectionsPerServer = 4,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            IdleHandlerLifetime = TimeSpan.FromMinutes(30)
        });

        try
        {
            Console.WriteLine($"  命名池 '{key}': 表里条目数 = {HttpConnectionPool.SharedCount}");

            // 两处互不相干的代码，只要用同一个名字就拿到同一条池（不传 options = 只取池）
            using var orderClient = Axios.Create(c => c.BaseAddress = baseUrl, pool: HttpConnectionPool.GetOrCreate(key));
            using var payClient = Axios.Create(c => c.BaseAddress = baseUrl, pool: HttpConnectionPool.GetOrCreate(key));

            Console.WriteLine($"  两处按名字取池: 同一条 = {ReferenceEquals(orderClient.Pool, payClient.Pool)}");

            server.ResetStats();
            for (var i = 0; i < 4; i++)
            {
                await orderClient.GetAsync(baseUrl);
                await payClient.GetAsync(baseUrl);
            }

            var stats = pool.GetStatistics();
            Console.WriteLine($"  8 个请求（两个客户端）: 服务器接受 TCP 连接数 = {server.TotalConnections}（期望 1，长连接跨客户端复用）");
            Console.WriteLine($"  池统计: origin 节点={stats.EndpointCount}, 累计租约={stats.TotalLeases}");

            // 命名池不归任何客户端所有：客户端 Dispose 之后池照样能用
            orderClient.Dispose();
            payClient.Dispose();
            using (HttpConnectionPool.GetOrCreate(key).Lease(new Uri(baseUrl)))
            {
            }

            Console.WriteLine($"  两个客户端都 Dispose 后: 命名池仍可用，节点数 = {pool.GetStatistics().EndpointCount}");

            // 同一名字传不同配置会报错
            try
            {
                HttpConnectionPool.GetOrCreate(key, new ConnectionPoolOptions { MaxConnectionsPerServer = 64 });
            }
            catch (AxiosException ex)
            {
                Console.WriteLine($"  同一名字传不同配置: {ex.Code}（第二次的配置不会被静默忽略）");
            }
        }
        finally
        {
            // Release = 摘除并优雅释放；此后同名再取会新建一条
            Console.WriteLine($"  Release('{key}') = {HttpConnectionPool.Release(key)}，剩余条目数 = {HttpConnectionPool.SharedCount}");
        }
    }
}
