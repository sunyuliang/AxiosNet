using System.ComponentModel;
using AxiosNet.Benchmarks.Infrastructure;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;

namespace AxiosNet.Benchmarks;

/// <summary>
/// 并发吞吐：8 条在飞请求下每请求摊到的成本（<c>OperationsPerInvoke = 8</c>），吞吐比 = 1 / Ratio。判定看比值 ± 误差是否跨过 1.0；跨进程不可比。
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
    // 在飞请求数固定 8：与 ConcurrencyPerformanceTests 同一口径（该机器 P 核逻辑线程数）。
    private const int Concurrency = 8;

    // 回环服务器：两侧打同一个端口、同一份正文。
    private LoopbackHttpServer _server = null!;
    // 被测实现，配置保持默认，只改 BaseAddress。
    private AxiosClient _axios = null!;
    // 基线实现：handler 与 AxiosNet 默认连接池逐项对齐。
    private HttpClient _http = null!;

    /// <summary>起回环服务器并建好两个客户端：连接与连接池在测量区外完成。</summary>
    [GlobalSetup]
    public void Setup()
    {
        _server = LoopbackHttpServer.Start(Payloads.UserJsonBytes);

        _axios = new AxiosClient(new AxiosConfig { BaseAddress = _server.Url });
        _http = new HttpClient(ComparableHttpClientHandler.Create()) { BaseAddress = new Uri(_server.Url) };
    }

    /// <summary>先释放客户端再停服务器：客户端还持有连接时先停会多出异常关闭的噪声。</summary>
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
