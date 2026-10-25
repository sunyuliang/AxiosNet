using System.Net;
using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>
/// 26. HTTP/2：默认就是 2.0（h2 已是公网流量主力），服务端不支持时自动降级；
/// 需要老协议时一行退回 1.1。
/// <para>
/// 演示服务器只实现 HTTP/1.1（相当于现实里那些只认 1.x 的服务），正好用来观察默认值的两个面：
/// 客户端请求 2.0，服务端看到的请求行仍然是 HTTP/1.1，请求照常成功 —— 降级不需要额外配置，
/// 也不会多一次往返。真实的 https 目标支持 h2 时，同样的默认配置会直接谈成 h2。
/// </para>
/// </summary>
public sealed class Http2Example : IExample
{
    /// <summary>默认客户端：配置固定，首次运行创建后跨运行复用。</summary>
    private static AxiosClient? _client;

    public int Number => 26;

    public string Title => "HTTP/2：默认 2.0（不支持时自动降级）、一行退回 1.1";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        var http = _client ??= new AxiosClient(new AxiosConfig { BaseAddress = context.BaseUrl });

        Console.WriteLine("  服务端能力：本示例的演示服务器只实现 HTTP/1.1（相当于「不支持 h2 的老服务」）");
        Console.WriteLine();

        // 1) 默认：什么都不配，库按 2.0 发起，服务端不支持就降级
        var byDefault = await http.GetAsync<ProtocolProbe>("protocol");
        Console.WriteLine("  [1] 默认配置（不设置 HttpVersion）");
        Console.WriteLine($"      客户端配置 2.0（本示例是明文，没有 ALPN 可协商）-> 服务端收到 {byDefault.Data!.RequestVersion}，响应协商结果 {byDefault.ProtocolVersion}：按契约落 1.1，请求照常成功");

        // 2) 退回 1.1：老网关 / NTLM 这类连接级认证 / 必须用连接级头时
        var forced = await http.GetAsync<ProtocolProbe>("protocol", c => c.Connection.HttpVersion = HttpVersion.Version11);
        Console.WriteLine("  [2] 退回 1.1（HttpVersion = Version11，策略仍是默认的 RequestVersionOrLower）");
        Console.WriteLine($"      客户端请求 1.1 -> 服务端收到 {forced.Data!.RequestVersion}，响应协商结果 {forced.ProtocolVersion}：不再尝试协商");

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

        // 4) HTTP/3：本库没有 h3 专属实现（无 QUIC / Alt-Svc），但版本是原样透传的。
        //    h3 只存在于 QUIC 上，明文目标上 OrLower 会落到 1.1（Exact 则会直接失败）。
        Console.WriteLine("  [4] 要求 h3（HttpVersion = Version30，策略默认 OrLower）");
        var h3 = await http.GetAsync<ProtocolProbe>("protocol", c => c.Connection.HttpVersion = HttpVersion.Version30);
        Console.WriteLine($"      客户端请求 3.0 -> 服务端收到 {h3.Data!.RequestVersion}，响应协商结果 {h3.ProtocolVersion}：明文上不存在 h3，按 OrLower 落 1.1");

        Console.WriteLine();
        Console.WriteLine("  要点：");
        Console.WriteLine("    · 默认 2.0：h2 已是公网流量主力，默认停在 1.1 等于白白丢掉多路复用与头部压缩");
        Console.WriteLine("    · 降级是免费的：https 上走 TLS 的 ALPN，服务端只认 1.1 就在同一次握手里谈成 1.1，不多一次往返、不多一条连接");
        Console.WriteLine("    · 明文 http:// 没有 ALPN 可协商，一律走 1.1 —— 所以默认 2.0 只影响 https，http:// 的线上行为与之前完全一致");
        Console.WriteLine("    · 需要 1.1 时一行退回：Connection.HttpVersion = Version11（老网关、NTLM 认证、必须用连接级头）");
        Console.WriteLine("    · 降级是静默的：response.ProtocolVersion 是「实际用了哪个版本」，日志 / 断言靠它确认有没有真拿到 h2");
        Console.WriteLine("    · HTTP/3：版本原样透传，但真正跑 h3 需要 .NET 7+ 运行时 + QUIC（MsQuic）；本库没有 Alt-Svc 自动发现");
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
