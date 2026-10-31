using System.Net;
using AxiosNet;
using AxiosNet.Pooling;

namespace AxiosNet.Simple.Examples;

/// <summary>
/// 37. 命名传输档：同一个池里按请求选出口（代理 / 凭据 / 客户端证书 / Cookie 会话），各档独立建连与隔离会话。
/// </summary>
public sealed class TransportProfileExample : IExample
{
    public int Number => 37;

    public string Title => "传输档：一个池多条出口，按请求切档、各档独立连接与 Cookie 会话";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        var baseUrl = context.BaseUrl;

        // 两个档：默认档直连；命名档各带自己的出口设置（这里用 Cookie 会话演示隔离，
        // 生产上把 Proxy / 客户端证书换成各自的即可）。
        var options = new ConnectionPoolOptions()
            .AddTransport("tenant-a", new ConnectionTransportOptions { UseCookies = true })
            .AddTransport("tenant-b", new ConnectionTransportOptions { UseCookies = true });

        // 池与客户端随本次运行新建：Cookie 会话与租约统计需要从干净状态开始
        using var pool = new HttpConnectionPool(options);
        using var client = new AxiosClient(pool: pool);

        // 37.1 同一个 origin、两个档 = 两个节点，各自的 Handler 与连接互不干扰
        await client.GetAsync(baseUrl, c => c.Connection.Transport = "tenant-a");
        await client.GetAsync(baseUrl, c => c.Connection.Transport = "tenant-b");

        var stats = pool.GetStatistics();
        Console.WriteLine($"  两个档各发一次: 节点数 = {stats.EndpointCount}（期望 2）");
        foreach (var endpoint in stats.Endpoints)
        {
            Console.WriteLine($"    {endpoint.Origin}  档='{endpoint.Transport}'  活跃租约={endpoint.ActiveLeases}");
        }

        // 37.2 Cookie 会话按档隔离：给 tenant-a 预置会话，tenant-b 看不到
        var uri = new Uri(baseUrl);
        pool.GetCookies("tenant-a")!.Add(uri, new Cookie("sid", "tenant-a-session"));

        var cookieA = pool.GetCookies("tenant-a")!.GetCookieHeader(uri);
        var cookieB = pool.GetCookies("tenant-b")!.GetCookieHeader(uri);
        Console.WriteLine($"  tenant-a 的 Cookie: '{cookieA}'");
        Console.WriteLine($"  tenant-b 的 Cookie: '{cookieB}'（期望为空：会话不串档）");

        // 37.3 未登记的档不会被静默忽略：请求发出前就报错
        try
        {
            await client.GetAsync(baseUrl, c => c.Connection.Transport = "tenant-c");
        }
        catch (AxiosException ex)
        {
            Console.WriteLine($"  请求未登记档: {ex.Code} — {ex.Message}");
        }

        // 37.4 档也可以在实例默认值上固定：之后每个请求都走它，单次请求仍可改回默认档
        using var tenantClient = new AxiosClient(
            new AxiosConfig { BaseAddress = baseUrl, Connection = { Transport = "tenant-a" } },
            pool: pool);

        await tenantClient.GetAsync("/");
        await tenantClient.GetAsync("/", c => c.Connection.Transport = null);
        Console.WriteLine($"  实例默认档 + 单次回到默认档: 累计租约 = {pool.GetStatistics().TotalLeases}");
    }
}
