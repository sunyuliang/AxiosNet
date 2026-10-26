using System.ComponentModel;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;

namespace AxiosNet.Benchmarks;

/// <summary>
/// 热点二：配置合并（<c>MergeConfig</c>）与请求头合并。
/// <para>
/// <b>为什么用 <see cref="AxiosClient.GetUri(AxiosConfig)"/> 测</b>：<c>MergeConfig</c> 是私有的，
/// 而 <c>GetUri(config)</c> 是它唯一对外的薄出口 —— 合并 + URL 组装，正是每个请求在发出去之前
/// 必付的那段 CPU。<c>GetUri(Action)</c> 走的是另一条路（<c>Defaults.Clone()</c> + 回调 + URL 组装，
/// <b>不合并按请求项</b>），两条路径的差值就是「把按请求补丁合到实例默认值上」的成本。
/// </para>
/// <para>
/// <b>怎么读</b>：<c>Ratio</c> 相对 <c>GetUri_NoMerge</c>（同参数组合）。两者都含 <c>Clone</c> 与
/// <c>Uri</c> 归一化，所以 <c>1.0x</c> 表示合并本身在噪声里；显著大于 1 才是合并的成本。
/// 两个 <c>[Params]</c> 分别拉「实例默认头数」与「按请求头数」，用来确认合并成本随头数线性增长
/// （<c>AxiosHeaders</c> 的写入是 O(N) 复制整版条目数组，这是它的设计前提）。
/// </para>
/// <para>
/// 合并依赖的两个原语（<c>AxiosConfig.Clone</c> / <c>AxiosHeaders</c>）单独在
/// <see cref="ConfigPrimitiveBenchmarks"/> 里测：它们与参数无关，留在这里会被 6 组参数重复测 6 遍。
/// </para>
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

    private AxiosConfig _perCall = null!;
    private AxiosClient _client = null!;

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
        defaults.TransformRequest.Add((data, config) => data);

        _client = new AxiosClient(defaults);

        _perCall = new AxiosConfig { Url = "users/1", Method = HttpMethod.Get };
        for (var i = 0; i < PerCallHeaders; i++)
        {
            _perCall.Headers.Set($"X-Call-{i}", "value");
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _client.Dispose();

    /// <summary>基线：不合并按请求项（<c>Defaults.Clone()</c> + URL 组装）。</summary>
    [Benchmark(Baseline = true)]
    public string GetUri_NoMerge() => _client.GetUri(config => config.Url = "users/1");

    /// <summary>合并路径：<c>MergeConfig</c> + URL 组装。</summary>
    [Benchmark]
    public string GetUri_Merge() => _client.GetUri(_perCall);
}
