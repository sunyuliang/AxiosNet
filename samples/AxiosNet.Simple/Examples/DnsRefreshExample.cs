using System.Net;
using System.Net.Sockets;
using AxiosNet;
using AxiosNet.Pooling;

namespace AxiosNet.Simple.Examples;

/// <summary>
/// 20. DNS 变更与连接过期：连接池如何收敛到新地址，以及如何强制立即刷新。
/// <para>
/// 用两个本地服务器模拟「同一个域名的两个 IP」，再用 <see cref="ConnectionPoolOptions.HandlerFactory"/> +
/// <see cref="SocketsHttpHandler.ConnectCallback"/> 接管解析过程，从而在几秒内复现真机上要等很久的场景。
/// </para>
/// </summary>
public sealed class DnsRefreshExample : IExample
{
    /// <summary>假域名：ConnectCallback 会把它指到当前「解析出来」的端口，不会真的查 DNS。</summary>
    private const string Url = "http://dns-demo.local/whoami";

    public int Number => 20;

    public string Title => "DNS 变更：连接生命周期轮换 vs 手动清池（连接池如何收敛到新 IP）";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        // 本示例的服务器、假 DNS 与客户端故意每次运行新建：两个「地址」是每轮新起的本地实例，
        // 池的收敛过程必须在全新连接上复现，静态复用会把上一轮的连接与解析结果带进来

        // 两个本地实例 = 同一个域名的旧 / 新两个地址
        using var oldServer = DemoServer.Start();
        using var newServer = DemoServer.Start();
        var dns = new FakeDns(oldServer.Port);

        Console.WriteLine($"  模拟 DNS：dns-demo.local -> 127.0.0.1:{oldServer.Port}（旧）");
        Console.WriteLine($"  备用地址：127.0.0.1:{newServer.Port}（新，切换后用）");

        // ---- 场景一：靠 PooledConnectionLifetime 自然收敛 ----
        Console.WriteLine();
        Console.WriteLine("  [1] PooledConnectionLifetime = 2s：过期后下一次请求换新连接并重新解析");

        using var pool = new HttpConnectionPool(CreateOptions(dns, TimeSpan.FromSeconds(2)));
        using var client = new AxiosClient(pool: pool);

        for (var i = 1; i <= 3; i++)
        {
            Console.WriteLine($"      第 {i} 次请求 -> 命中端口 {await RequestAsync(client)}");
        }

        Console.WriteLine($"      旧地址已接受 TCP 连接数 = {oldServer.TotalConnections}（3 个请求复用 1 条连接）");

        dns.PointTo(newServer.Port);
        Console.WriteLine($"      >>> DNS 已切换到新地址（127.0.0.1:{newServer.Port}）");

        Console.WriteLine($"      切换后立刻请求 -> 命中端口 {await RequestAsync(client)}（仍是旧地址：连接还健康，池不会因为 DNS 变了就主动断开）");

        await Task.Delay(TimeSpan.FromSeconds(2.2));

        Console.WriteLine($"      PooledConnectionLifetime 到期后再请求 -> 命中端口 {await RequestAsync(client)}（新连接重新解析 DNS）");
        Console.WriteLine($"      新地址已接受 TCP 连接数 = {newServer.TotalConnections}");

        // ---- 场景二：不想等生命周期，手动清池立即收敛 ----
        Console.WriteLine();
        Console.WriteLine("  [2] 长生命周期（10 分钟）下，用 pool.Clear() 立即失效");

        using var frozenPool = new HttpConnectionPool(CreateOptions(dns, TimeSpan.FromMinutes(10)));
        using var frozenClient = new AxiosClient(pool: frozenPool);

        Console.WriteLine($"      当前命中端口 {await RequestAsync(frozenClient)}（新地址）");
        dns.PointTo(oldServer.Port);
        Console.WriteLine($"      >>> DNS 切回旧地址（127.0.0.1:{oldServer.Port}）");
        Console.WriteLine($"      不等待再请求 -> 命中端口 {await RequestAsync(frozenClient)}（10 分钟生命周期内保持旧连接）");

        frozenPool.Clear();
        Console.WriteLine("      >>> pool.Clear()");
        Console.WriteLine($"      清池后请求 -> 命中端口 {await RequestAsync(frozenClient)}（立即收敛到当前 DNS）");

        // ---- 小结 ----
        Console.WriteLine();
        Console.WriteLine("  要点：");
        Console.WriteLine("    · PooledConnectionLifetime 控制「一条连接最多还能被复用多久」，到期后下一次取用时换新连接并重新解析 DNS");
        Console.WriteLine("    · 连接健康时池不会因为 DNS 变化主动断开，这正是 keep-alive 的收益，也意味着收敛有时间窗口");
        Console.WriteLine("    · 窗口上界 ≈ PooledConnectionLifetime（另有 PooledConnectionIdleTimeout 兜底），默认 10 分钟");
        Console.WriteLine("    · 需要立即失效只有 pool.Clear()，它会打断在途请求，适合运维手动触发或进程关闭");
        Console.WriteLine("    · 服务发现（Consul/Nacos/SRV）可像本例的 ConnectCallback 一样自己决定目标地址，每次建连都会重新查询");
    }

    private static ConnectionPoolOptions CreateOptions(FakeDns dns, TimeSpan lifetime) => new()
    {
        PooledConnectionLifetime = lifetime,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
        IdleHandlerLifetime = TimeSpan.FromMinutes(5),

        // 保留库默认 Handler（连接数上限、重定向、解压等），只接管「怎么连」这一步。
        HandlerFactory = context =>
        {
            var handler = context.CreateDefaultHandler();

            // ConnectCallback 每次建立新 TCP 连接时都会调用一次 —— 这就是本例的「DNS 解析器」。
            handler.ConnectCallback = async (connection, cancellationToken) =>
            {
                var port = dns.Port;
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new DnsEndPoint("127.0.0.1", port), cancellationToken)
                        .ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };

            return handler;
        }
    };

    private static async Task<int> RequestAsync(AxiosClient client)
    {
        var response = await client.GetAsync<WhoAmI>(Url).ConfigureAwait(false);
        return response.Data?.Port ?? throw new InvalidOperationException("响应里没有 port 字段");
    }

    /// <summary>可变的「DNS 记录」：切换端口等价于改 A 记录。</summary>
    private sealed class FakeDns
    {
        private int _port;

        public FakeDns(int port) => _port = port;

        public int Port => Volatile.Read(ref _port);

        public void PointTo(int port) => Volatile.Write(ref _port, port);
    }

    private sealed class WhoAmI
    {
        public int Port { get; set; }
    }
}
