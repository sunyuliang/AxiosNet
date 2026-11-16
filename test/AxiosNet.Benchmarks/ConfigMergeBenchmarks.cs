using System.ComponentModel;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;

namespace AxiosNet.Benchmarks;

/// <summary>
/// 配置合并（<c>MergeConfig</c>）与请求头合并的成本，经 <c>GetUri</c> 这个唯一出口暴露；<c>Ratio</c> 相对 <c>GetUri_NoMerge</c>，1.0x 表示合并在噪声里。两个参数分别拉实例默认头数与按请求头数。
/// </summary>
[Description("配置合并 / 头合并：GetUri 合并 vs 不合并（实例头数 × 按请求头数）")]
[BenchmarkGroup("纯 CPU（FakeHttpHandler，无网络）", "没有网络噪声，比值与 Allocated 都稳", Order = 2)]
[BenchmarkHint("比值 − 1 = 把按请求补丁合到默认值上的增量；每多一批按请求头就多复制一版条目数组")]
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[HideColumns("Job")]
// 不能 sealed：BDN 会派生基准类来拦截调用（0.15 起对 sealed 直接报校验错误）。
public class ConfigMergeBenchmarks
{
    /// <summary>实例默认配置上的头数（<c>Defaults.Headers</c>）。</summary>
    [Params(0, 5, 10)]
    public int InstanceHeaders { get; set; }

    /// <summary>按请求配置上的头数（<c>perCall.Headers</c>）：合并要逐条覆盖到副本上。</summary>
    [Params(0, 2)]
    public int PerCallHeaders { get; set; }

    // 按请求配置：复用同一个对象，构造成本不算进测量。
    private AxiosConfig _perCall = null!;
    // 持有实例默认配置的客户端，合并的右值就是它的 Defaults。
    private AxiosClient _client = null!;

    /// <summary>按参数组合造默认头与按请求头；一条 transform 用来覆盖「继承段长度 + 去重扫描」这段逻辑。</summary>
    [GlobalSetup]
    public void Setup()
    {
        var defaults = new AxiosConfig
        {
            BaseAddress = "http://bench.local/api/",
            Params = new { page = 1, size = 20 }
        };

        for (var i = 0; i < InstanceHeaders; i++)
        {
            defaults.Headers.Set($"X-Default-{i}", "value");
        }

        // 一条默认 transform：合并层要为它维护「继承段长度」并做去重扫描
        defaults.Serialization.TransformRequest.Add((data, config) => data);

        _client = new AxiosClient(defaults);

        _perCall = new AxiosConfig { Url = "users/1", Method = HttpMethod.Get };
        for (var i = 0; i < PerCallHeaders; i++)
        {
            _perCall.Headers.Set($"X-Call-{i}", "value");
        }
    }

    /// <summary>释放客户端（连同内部连接池）：参数组合之间不共用实例。</summary>
    [GlobalCleanup]
    public void Cleanup() => _client.Dispose();

    /// <summary>基线：不合并按请求项（<c>Defaults.Clone()</c> + URL 组装）。</summary>
    [Benchmark(Baseline = true)]
    public string GetUri_NoMerge() => _client.GetUri(config => config.Url = "users/1");

    /// <summary>合并路径：<c>MergeConfig</c> + URL 组装。</summary>
    [Benchmark]
    public string GetUri_Merge() => _client.GetUri(_perCall);
}
