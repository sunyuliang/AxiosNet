using System.Net;
using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>
/// 26. HTTP/2：默认仍是 1.1（与 <c>HttpClient</c> 一致，升级不改变线上协议），一行即可开启 h2；
/// 开启后服务端不支持会自动降级。
/// <para>
/// 演示服务器只实现 HTTP/1.1（相当于现实里的老服务），正好用来观察降级：客户端请求 2.0，
/// 服务端看到的请求行仍然是 HTTP/1.1，请求照常成功。
/// </para>
/// </summary>
public sealed class Http2Example : IExample
{
    /// <summary>默认客户端：配置固定，首次运行创建后跨运行复用。</summary>
    private static AxiosClient? _client;

    public int Number => 26;

    public string Title => "HTTP/2：默认 1.1、一行开启（服务端不支持时自动降级）";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        var http = _client ??= new AxiosClient(new AxiosConfig { BaseAddress = context.BaseUrl });

        Console.WriteLine("  服务端能力：本示例的演示服务器只实现 HTTP/1.1（相当于「不支持 h2 的老服务」）");
        Console.WriteLine();

        // 1) 默认：什么都不配，与 HttpClient 的默认一致
        var byDefault = await http.GetAsync<ProtocolProbe>("protocol");
        Console.WriteLine("  [1] 默认配置（不设置 HttpVersion）");
        Console.WriteLine($"      客户端请求 1.1 -> 服务端收到 {byDefault.Data!.RequestVersion}：与升级前完全一致");

        // 2) 开启 h2：只需要一行，策略默认 OrLower（服务端不支持就降级）
        var fallback = await http.GetAsync<ProtocolProbe>("protocol", c => c.Connection.HttpVersion = HttpVersion.Version20);
        Console.WriteLine("  [2] 开启 h2（HttpVersion = Version20，策略默认 RequestVersionOrLower）");
        Console.WriteLine($"      客户端请求 2.0 -> 服务端收到 {fallback.Data!.RequestVersion}：协商不上就降级，请求照常成功");

        // 3) 要求必须是 2.0：Exact 不降级，服务端不支持就失败（也用于明文 h2c 先验知识）
        Console.WriteLine("  [3] 要求必须是 h2（VersionPolicy = RequestVersionExact）");
        try
        {
            await http.GetAsync<ProtocolProbe>("protocol", c =>
            {
                c.Connection.HttpVersion = HttpVersion.Version20;
                c.Connection.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            });
            Console.WriteLine("      [意外] 只支持 1.1 的服务端竟然接受了 h2 请求");
        }
        catch (AxiosException ex)
        {
            Console.WriteLine($"      失败（预期）：code={ex.Code} —— Exact 的语义就是「不支持就报错，不降级」");
        }

        Console.WriteLine();
        Console.WriteLine("  要点：");
        Console.WriteLine("    · 默认仍是 1.1：升级本库不改变线上协议；需要多路复用 / 头部压缩时一行开启 h2");
        Console.WriteLine("    · 开启后策略默认 RequestVersionOrLower：https 上走 ALPN 协商，服务端只支持 1.1 时自动退回，老服务不受影响");
        Console.WriteLine("    · 明文 http:// 没有 ALPN 可协商，开了 h2 也仍是 1.1；h2c 必须先验知识（VersionPolicy = RequestVersionExact）");
        Console.WriteLine("    · HTTP/2 禁止 Connection / Keep-Alive / Transfer-Encoding 这类连接级头，需要它们（或 NTLM 认证）时保持 1.1");
        Console.WriteLine("    · 单条 h2 连接的并发上限由服务端 MAX_CONCURRENT_STREAMS 决定，池默认允许按 MaxConnectionsPerServer 加连接");
        Console.WriteLine("    · 本机示例没有证书，演示的是明文路径；生产环境（https://）走 TLS + ALPN，配置完全一样");
    }

    /// <summary>服务端回显的协议版本。</summary>
    private sealed class ProtocolProbe
    {
        public string? RequestVersion { get; set; }

        public string? ServerVersion { get; set; }
    }
}
