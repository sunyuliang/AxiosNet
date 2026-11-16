using System.ComponentModel;
using AxiosNet.Benchmarks.Infrastructure;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;

namespace AxiosNet.Benchmarks;

/// <summary>
/// 请求全链路（真实回环 socket）：一条完整请求上比裸 <c>HttpClient</c> 贵多少，给的是带误差条的单请求延迟与分配。<c>Ratio</c> 是时间比，吞吐 = 1 / Ratio；判定退化时看 <c>RatioSD</c>。
/// </summary>
[Description("请求全链路（回环 socket）：HttpClient 基线 vs AxiosNet GET 字符串 / GET 强类型 / POST JSON")]
[BenchmarkGroup("回环 socket（数字含 TCP 往返与线程池调度）", "绝对值随机器负载摆，能下结论的是比值", Order = 1)]
[BenchmarkHint("比值 = 单请求时间比，吞吐比 = 1 / 比值；绝对值跨轮能差 30%，别当门槛")]
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[HideColumns("Job")]
// 不能 sealed：BDN 会派生基准类来拦截调用（0.15 起对 sealed 直接报校验错误）。
public class RequestPipelineBenchmarks
{
    // 回环服务器：固定一份小正文，只测客户端侧成本。
    private LoopbackHttpServer _server = null!;
    // 被测实现，配置保持默认。
    private AxiosClient _axios = null!;
    // 基线实现，连接池参数与库默认值逐项对齐。
    private HttpClient _http = null!;

    /// <summary>起回环服务器并建两个客户端；两侧都用「基地址 + 相对路径」，字符串拼装成本不进任何一侧。</summary>
    [GlobalSetup]
    public void Setup()
    {
        _server = LoopbackHttpServer.Start(Payloads.UserJsonBytes);

        _axios = new AxiosClient(new AxiosConfig { BaseAddress = _server.Url });

        // 两侧都用「基地址 + 相对路径」，避免把字符串拼装成本算进任何一侧
        _http = new HttpClient(ComparableHttpClientHandler.Create()) { BaseAddress = new Uri(_server.Url) };
    }

    /// <summary>先释放客户端再停服务器。</summary>
    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        _axios.Dispose();
        _http.Dispose();
        await _server.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>基线：裸 HttpClient 把正文读成字符串（不反序列化）。</summary>
    [Benchmark(Baseline = true)]
    public async Task<string> HttpClient_GetString()
        => await _http.GetStringAsync("users/1").ConfigureAwait(false);

    /// <summary>与基线工作量对等：AxiosNet 的合并 / 组装 / 拦截器链 / 正文解码全在这条路径上。</summary>
    [Benchmark]
    public async Task<string?> AxiosNet_GetString()
    {
        var response = await _axios.GetAsync<string>("users/1").ConfigureAwait(false);
        return response.Data;
    }

    /// <summary>加一段 JSON 反序列化（<c>System.Text.Json</c> 源生成以外的最常见路径）。</summary>
    [Benchmark]
    public async Task<int> AxiosNet_GetTyped()
    {
        var response = await _axios.GetAsync<UserDto>("users/1").ConfigureAwait(false);
        return response.Data!.Id;
    }

    /// <summary>带正文的请求：序列化 + Content-Length + 正文上送（服务器按 Content-Length 跳读）。</summary>
    [Benchmark]
    public async Task<int> AxiosNet_PostJson()
    {
        var response = await _axios
            .PostAsync<UserDto>("users", new { id = 1, name = "alice", tags = new[] { "admin", "user" } })
            .ConfigureAwait(false);
        return response.Data!.Id;
    }
}
