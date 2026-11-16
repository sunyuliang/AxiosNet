using AxiosNet;
using AxiosNet.Pooling;

namespace AxiosNet.Simple.Examples;

/// <summary>41. 预热：静态首用 + 预建池节点，不建 TCP。</summary>
public sealed class WarmupExample : IExample
{
    public int Number => 41;

    public string Title => "预热：Warmup() 把静态首用与 origin 节点提前建好（不建连），首条请求只剩建连与往返";

    /// <summary>示例入口：预热：Warmup() 把静态首用与 origin 节点提前建好（不建连），首条请求只剩建连与往返。</summary>
    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        context.Server.ResetStats();

        using var client = new AxiosClient(new AxiosConfig { BaseAddress = context.BaseUrl });

        // 41.1 客户端预热：建节点、不建连。返回 true 表示本次真的做了预热。
        var warmed = client.Warmup();
        var again = client.Warmup();
        var stats = client.Pool!.GetStatistics();

        Console.WriteLine($"  第一次 Warmup() = {warmed}，第二次 = {again}（标记记在实例上，重复调用不再做）");
        Console.WriteLine($"  池统计: origin 节点={stats.EndpointCount}, 累计租约={stats.TotalLeases}"
            + "（节点已建，还没有租约）");
        Console.WriteLine($"  服务器接受的 TCP 连接数 = {context.Server.TotalConnections}（期望 0：预热不建连）");

        // 41.2 首条请求复用预热出来的节点：只多一条 TCP，不多一个节点。
        await client.GetAsync(context.BaseUrl);
        Console.WriteLine($"  首条请求之后: TCP 连接数 = {context.Server.TotalConnections},"
            + $" origin 节点={client.Pool!.GetStatistics().EndpointCount}"
            + $", 累计租约={client.Pool!.GetStatistics().TotalLeases}");

        // 41.3 池级预热：明确指定 origin（默认档 / 命名档各建一个节点）。
        using var pool = new HttpConnectionPool(
            new ConnectionPoolOptions().AddTransport("egress-demo", new ConnectionTransportOptions()));
        pool.Prewarm(new Uri(context.BaseUrl));
        pool.Prewarm(new Uri(context.BaseUrl), "egress-demo");
        Console.WriteLine($"  池级 Prewarm: 默认档 + egress-demo 各一个节点 -> 节点数 = {pool.GetStatistics().EndpointCount}");
    }
}
