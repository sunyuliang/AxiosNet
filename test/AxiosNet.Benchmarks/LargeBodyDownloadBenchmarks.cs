using System.ComponentModel;
using AxiosNet.Benchmarks.Infrastructure;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;

namespace AxiosNet.Benchmarks;

/// <summary>
/// 热点七：大正文下载（真实回环 socket）。
/// <para>
/// <b>回答的问题</b>：缓冲下载时库侧付出了几次拷贝、内存峰值多大；流式下载是否真的不落内存。
/// 响应读取的信任上限在 <c>ResponseReader</c> 里：声明长度 ≤ 8MB 时按声明值精确预分配、读满直接交出那个数组（1×）；
/// &gt; 8MB 走池化分段、装配时只付最终数组（仍是 1×）。所以 <c>Allocated</c> 的预期是：
/// 缓冲下载 ≈ 1× 载荷，流式下载只有读缓冲，与 <c>Megabytes</c> 无关。
/// </para>
/// <para>
/// <b>读数方式</b>：<c>Ratio</c> 相对 <c>HttpClient_Download_Buffered</c>（时间比，吞吐 = 1 / Ratio）。
/// 8MB 这一档是预分配路径的边界，和 1MB / 32MB 一起看能区分「预分配」与「段式累积」两条路径的成本。
/// </para>
/// </summary>
[Description("大正文下载（回环 socket）：1 / 8 / 32MB，缓冲成 byte[] vs 流式读完")]
[BenchmarkGroup("回环 socket（数字含 TCP 往返与线程池调度）", "绝对值随机器负载摆，能下结论的是比值", Order = 1)]
[BenchmarkHint("缓冲下载 Allocated ≈ 1× 载荷（两条路径都是），流式下载应远小于载荷；8MB 是响应预分配边界")]
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[HideColumns("Job")]
// 不能 sealed：BDN 会派生基准类来拦截调用。
public class LargeBodyDownloadBenchmarks
{
    /// <summary>正文大小（MB）。8MB 是 <c>ResponseReader</c> 预分配信任上限。</summary>
    [Params(1, 8, 32)]
    public int Megabytes { get; set; }

    private LoopbackHttpServer _server = null!;
    private AxiosClient _axios = null!;
    private HttpClient _http = null!;
    private string _url = null!;

    [GlobalSetup]
    public void Setup()
    {
        var payload = Payloads.Large(Megabytes * 1024 * 1024);
        _server = LoopbackHttpServer.Start(payload, "application/octet-stream");
        _url = _server.Url + "download";

        _axios = new AxiosClient();
        _http = new HttpClient(ComparableHttpClientHandler.Create());
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        _axios.Dispose();
        _http.Dispose();
        await _server.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>基线：裸 HttpClient 把正文读成 byte[]。</summary>
    [Benchmark(Baseline = true)]
    public async Task<int> HttpClient_Download_Buffered()
    {
        var data = await _http.GetByteArrayAsync(_url).ConfigureAwait(false);
        return data.Length;
    }

    /// <summary>库侧缓冲路径：预检上限 → 精确预分配 / 段式累积 → 交出 byte[]。</summary>
    [Benchmark]
    public async Task<int> AxiosNet_Download_Buffered()
    {
        var response = await _axios.GetAsync<byte[]>(_url).ConfigureAwait(false);
        return response.Data!.Length;
    }

    /// <summary>基线：裸 HttpClient 取响应流后直接拷进 <see cref="Stream.Null"/>，不落内存。</summary>
    [Benchmark]
    public async Task HttpClient_Download_Stream()
    {
        using var stream = await _http.GetStreamAsync(_url).ConfigureAwait(false);
        await stream.CopyToAsync(Stream.Null).ConfigureAwait(false);
    }

    /// <summary>库侧流式路径：<c>GetAsync&lt;Stream&gt;</c> 自动按流交付，只付读缓冲。</summary>
    [Benchmark]
    public async Task AxiosNet_Download_Stream()
    {
        var response = await _axios.GetAsync<Stream>(_url).ConfigureAwait(false);
        await using var stream = response.Data!;
        await stream.CopyToAsync(Stream.Null).ConfigureAwait(false);
    }
}
