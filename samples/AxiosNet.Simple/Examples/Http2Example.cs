using System.Net;
using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>26. HTTP/2：默认就是 2.0，服务端不支持时自动降级；需要老协议时一行退回 1.1。</summary>
public sealed class Http2Example : IExample
{
    public int Number => 26;

    public string Title => "HTTP/2：默认 2.0（不支持时自动降级）、一行退回 1.1";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        var protocolUrl = context.BaseUrl + "protocol";

        Console.WriteLine("  服务端能力：本示例的演示服务器只实现 HTTP/1.1（相当于「不支持 h2 的老服务」）");
        Console.WriteLine();

        // 1) 默认：什么都不配，库按 2.0 发起，服务端不支持就降级
        var byDefault = await Axios.GetAsync<ProtocolProbe>(protocolUrl);
        Console.WriteLine("  [1] 默认配置（不设置 HttpVersion）");
        Console.WriteLine($"      客户端配置 2.0（本示例是明文，没有 ALPN 可协商）-> 服务端收到 {byDefault.Data!.RequestVersion}，响应协商结果 {byDefault.ProtocolVersion}：按契约落 1.1，请求照常成功");

        // 2) 退回 1.1：老网关 / NTLM 认证 / 必须用连接级头时
        var forced = await Axios.GetAsync<ProtocolProbe>(protocolUrl, c => c.Connection.HttpVersion = HttpVersion.Version11);
        Console.WriteLine("  [2] 退回 1.1（HttpVersion = Version11，策略仍是默认的 RequestVersionOrLower）");
        Console.WriteLine($"      客户端请求 1.1 -> 服务端收到 {forced.Data!.RequestVersion}，响应协商结果 {forced.ProtocolVersion}：不再尝试协商");

        // 3) 要求必须是 2.0：Exact 不降级，服务端不支持就失败
        Console.WriteLine("  [3] 要求必须是 h2（VersionPolicy = RequestVersionExact）");
        try
        {
            await Axios.GetAsync<ProtocolProbe>(protocolUrl, c =>
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

        // 4) HTTP/3：默认仍是 2.0，h3 要显式要求（Version30）
        Console.WriteLine("  [4] 要求 h3（HttpVersion = Version30，策略默认 OrLower）");
        Console.WriteLine($"      本机 HTTP/3 能力：{(Http3Support.IsSupported ? "可用" : "不可用 —— " + Http3Support.UnsupportedReason)}");
        var h3 = await Axios.GetAsync<ProtocolProbe>(protocolUrl, c => c.Connection.HttpVersion = HttpVersion.Version30);
        Console.WriteLine($"      客户端请求 3.0 -> 服务端收到 {h3.Data!.RequestVersion}，响应协商结果 {h3.ProtocolVersion}：明文上不存在 h3，按 OrLower 落 1.1");

        // 5) 「必须 h3」在明文目标上发请求前就报错
        Console.WriteLine("  [5] 要求必须 h3（VersionPolicy = RequestVersionExact）");
        try
        {
            await Axios.GetAsync<ProtocolProbe>(protocolUrl, c =>
            {
                c.Connection.HttpVersion = HttpVersion.Version30;
                c.Connection.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            });
            Console.WriteLine("      [意外] 明文目标竟然接受了 h3 请求");
        }
        catch (AxiosException ex)
        {
            Console.WriteLine($"      失败（预期）：code={ex.Code} —— 环境上不可能做到，发请求前就报配置错误");
        }

        Console.WriteLine();
        Console.WriteLine("  要点：");
        Console.WriteLine("    · 默认 2.0：h2 已是公网流量主力，默认停在 1.1 等于白白丢掉多路复用与头部压缩");
        Console.WriteLine("    · 降级是免费的：https 上走 TLS 的 ALPN，服务端只认 1.1 就在同一次握手里谈成 1.1，不多一次往返、不多一条连接");
        Console.WriteLine("    · 明文 http:// 没有 ALPN 可协商，一律走 1.1 —— 所以默认 2.0 只影响 https，http:// 的线上行为与之前完全一致");
        Console.WriteLine("    · 需要 1.1 时一行退回：Connection.HttpVersion = Version11（老网关、NTLM 认证、必须用连接级头）");
        Console.WriteLine("    · 降级是静默的：response.ProtocolVersion 是「实际用了哪个版本」，日志 / 断言靠它确认有没有真拿到 h2");
        Console.WriteLine("    · HTTP/3：默认仍是 2.0，写 Version30 才用 h3；本机能力查 Http3Support.IsSupported（不支持时 UnsupportedReason 说明缺什么）");
        Console.WriteLine("    · 「必须 h3」（Exact / OrHigher）在明文目标或没有 QUIC 的环境里会在发请求前报 ERR_BAD_OPTION_VALUE，不会白等超时；本库不做 Alt-Svc 自动发现");
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
