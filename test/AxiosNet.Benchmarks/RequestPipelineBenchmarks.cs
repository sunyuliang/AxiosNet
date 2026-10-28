using System.ComponentModel;
using AxiosNet.Benchmarks.Infrastructure;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;

namespace AxiosNet.Benchmarks;

/// <summary>
/// 热点一：请求全链路（真实回环 socket）。
/// <para>
/// <b>这一组回答的问题</b>：AxiosNet 在一条完整请求上比裸 <see cref="HttpClient"/> 贵多少？
/// 与 <c>ConcurrencyPerformanceTests</c> 的 85% 能力线是同一条问题，但这里给的是带误差条的
/// 单请求延迟与分配，而不是一个「多轮最好一轮是否过线」的布尔值。
/// </para>
/// <para>
/// <b>读数方式</b>：BDN 的 <c>Ratio</c> 是<b>时间</b>比（相对 <c>HttpClient_GetString</c>），
/// 所以「吞吐比 = 1 / Ratio」。注意它是<b>逐行顺序测</b>的：每个变体一个独立进程、独立时段，
/// 消掉的是「换机器 / 换模型」的差异，消不掉「跑 A 时机器比跑 B 时热」—— 判定退化时看 <c>RatioSD</c>，
/// 必要时跑两遍看比值稳不稳。
/// </para>
/// <para>
/// <b>对照口径</b>：两侧 Handler 功能逐项对齐（见 <see cref="ComparableHttpClientHandler"/>），
/// 服务器是回环直连的最小实现（见 <see cref="LoopbackHttpServer"/>），两边工作量对等：
/// <c>HttpClient_GetString</c> 与 <c>AxiosNet_GetString</c> 都只把正文读成字符串，不做反序列化。
/// </para>
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
    private LoopbackHttpServer _server = null!;
    private AxiosClient _axios = null!;
    private HttpClient _http = null!;

    [GlobalSetup]
    public void Setup()
    {
        _server = LoopbackHttpServer.Start(Payloads.UserJsonBytes);

        _axios = new AxiosClient(new AxiosConfig { BaseAddress = _server.Url });

        // 两侧都用「基地址 + 相对路径」，避免把字符串拼装成本算进任何一侧
        _http = new HttpClient(ComparableHttpClientHandler.Create()) { BaseAddress = new Uri(_server.Url) };
    }

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
