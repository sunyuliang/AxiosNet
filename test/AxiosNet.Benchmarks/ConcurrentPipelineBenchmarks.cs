using System.ComponentModel;
using AxiosNet.Benchmarks.Infrastructure;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;

namespace AxiosNet.Benchmarks;

/// <summary>
/// 并发吞吐：把 <c>ConcurrencyPerformanceTests</c> 的「85% 门槛」换成可读的比值。
/// <para>
/// <b>为什么单独一个类</b>：<see cref="RequestPipelineBenchmarks"/> 测的是单请求延迟（一次一个），
/// 这里测的是「8 条在飞请求时每请求摊到多少」——两者不是一回事，前者看 CPU 增量，后者看吞吐上限。
/// BDN 的 <c>OperationsPerInvoke = 8</c> 让每次调用记为 8 个操作，于是 Mean 就是<b>每请求</b>成本，
/// <c>Ratio</c> 就是「每请求时间比」，<b>吞吐比 = 1 / Ratio</b>。
/// </para>
/// <para>判定看比值 ± 误差是否跨过 1.0；跨进程 / 跨机器不可比。</para>
/// <para>
/// 并发数取 8：与 <c>ConcurrencyPerformanceTests</c> 保持一致（该机器 P 核逻辑线程数，
/// in-flight 超过它之后两侧会被摊到不同性能的核上，比值反而失真）。
/// </para>
/// </summary>
[Description("并发吞吐（8 在飞）：HttpClient 基线 vs AxiosNet，Mean 为每请求成本")]
[BenchmarkGroup("回环 socket（数字含 TCP 往返与线程池调度）", "绝对值随机器负载摆，能下结论的是比值", Order = 1)]
[BenchmarkHint("Mean 已是每请求成本（OperationsPerInvoke = 8），吞吐比 = 1 / 比值")]
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[HideColumns("Job")]
// 不能 sealed：BDN 会派生基准类来拦截调用（0.15 起对 sealed 直接报校验错误）。
public class ConcurrentPipelineBenchmarks
{
    private const int Concurrency = 8;

    private LoopbackHttpServer _server = null!;
    private AxiosClient _axios = null!;
    private HttpClient _http = null!;

    [GlobalSetup]
    public void Setup()
    {
        _server = LoopbackHttpServer.Start(Payloads.UserJsonBytes);

        _axios = new AxiosClient(new AxiosConfig { BaseAddress = _server.Url });
        _http = new HttpClient(ComparableHttpClientHandler.Create()) { BaseAddress = new Uri(_server.Url) };
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        _axios.Dispose();
        _http.Dispose();
        await _server.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>基线：裸 HttpClient，8 条在飞，每请求成本。</summary>
    [Benchmark(Baseline = true, OperationsPerInvoke = Concurrency)]
    public async Task<string> HttpClient_Concurrent()
    {
        var pending = new Task<string>[Concurrency];
        for (var i = 0; i < pending.Length; i++)
        {
            pending[i] = _http.GetStringAsync("users/1");
        }

        await Task.WhenAll(pending).ConfigureAwait(false);
        return pending[0].Result;
    }

    /// <summary>与基线同一形态：8 条在飞，每请求成本（吞吐比 = 1 / Ratio）。</summary>
    [Benchmark(OperationsPerInvoke = Concurrency)]
    public async Task<string?> AxiosNet_Concurrent()
    {
        var pending = new Task<AxiosResponse<string>>[Concurrency];
        for (var i = 0; i < pending.Length; i++)
        {
            pending[i] = _axios.GetAsync<string>("users/1");
        }

        var responses = await Task.WhenAll(pending).ConfigureAwait(false);
        return responses[0].Data;
    }
}
