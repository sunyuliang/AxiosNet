using System.ComponentModel;
using System.Text.Json;
using AxiosNet.Benchmarks.Infrastructure;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;

namespace AxiosNet.Benchmarks;

/// <summary>
/// dynamic 反序列化：同一份字节分别落成强类型、<c>AxiosDynamic</c> 对象图与裸 <c>JsonElement</c>，<c>Ratio</c> 相对强类型。<c>Wide</c> 两行用 13 字段 / 3 层嵌套载荷看差距随图规模放大。
/// </summary>
[Description("dynamic 反序列化：强类型基线 vs AxiosDynamic 对象图 vs JsonElement vs 加成员访问")]
[BenchmarkGroup("纯 CPU（FakeHttpHandler，无网络）", "没有网络噪声，比值与 Allocated 都稳", Order = 2)]
[BenchmarkHint("比值 = dynamic 比强类型贵几倍；Wide 两行看每节点成本随图变大怎么放大")]
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[HideColumns("Job")]
// 不能 sealed：BDN 会派生基准类来拦截调用（0.15 起对 sealed 直接报校验错误）。
public class DynamicDeserializationBenchmarks
{
    // 小对象正文（5 字段 + 嵌套）。
    private AxiosClient _user = null!;
    // 宽对象图正文（13 标量 / 3 层嵌套 / 2 数组 / 1 null）。
    private AxiosClient _wide = null!;

    /// <summary>两个客户端各配一份正文：切载荷不用改路由，也不进测量区。</summary>
    [GlobalSetup]
    public void Setup()
    {
        _user = CreateClient(Payloads.UserJsonBytes);
        _wide = CreateClient(Payloads.WideJsonBytes);
    }

    /// <summary>释放两个客户端（正文由 FakeHttpHandler 持有，不涉及服务器）。</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _user.Dispose();
        _wide.Dispose();
    }

    /// <summary>基线：强类型反序列化。</summary>
    [Benchmark(Baseline = true)]
    public async Task<string> Typed()
    {
        var response = await _user.GetAsync<UserDto>("users/1").ConfigureAwait(false);
        return response.Data!.Name;
    }

    /// <summary>构建 dynamic 对象图（对象 → AxiosDynamic，数组 → List&lt;object?&gt;）。</summary>
    [Benchmark]
    public async Task<object?> Dynamic()
    {
        // 泛型实参写 object（= <dynamic> 的运行时形态）：判据是 dataType == typeof(object)，
        // 写 <dynamic> 只会在返回处多一次 DLR 转换，与图构建无关。
        var response = await _user.GetAsync<object>("users/1").ConfigureAwait(false);
        return response.Data;
    }

    /// <summary>只留 JsonElement，不建图。</summary>
    [Benchmark]
    public async Task<JsonElement> JsonElement()
    {
        var response = await _user.GetAsync("users/1").ConfigureAwait(false);
        return (JsonElement)response.Data!;
    }

    /// <summary>建图 + 5 次成员访问（<c>data.address.city</c> 这类跨节点访问）。</summary>
    [Benchmark]
    public async Task<string> Dynamic_WithAccess()
    {
        var response = await _user.GetAsync<dynamic>("users/1").ConfigureAwait(false);
        dynamic data = response.Data!;
        string name = data.name;
        long id = data.id;
        string city = data.address.city;
        string firstTag = data.tags[0];
        bool active = data.active;
        return $"{name}/{id}/{city}/{firstTag}/{active}";
    }

    /// <summary>宽图（13 标量 + 3 层嵌套 + 2 数组）：看每节点成本。</summary>
    [Benchmark]
    public async Task<object?> Dynamic_Wide()
    {
        var response = await _wide.GetAsync<object>("users/1").ConfigureAwait(false);
        return response.Data;
    }

    /// <summary>同一份宽图的 JsonElement 形态，作为 <c>Dynamic_Wide</c> 的对照。</summary>
    [Benchmark]
    public async Task<JsonElement> JsonElement_Wide()
    {
        var response = await _wide.GetAsync("users/1").ConfigureAwait(false);
        return (JsonElement)response.Data!;
    }

    /// <summary>用假 handler 造一个只回固定正文的客户端（基准里不碰网络）。</summary>
    private static AxiosClient CreateClient(byte[] body)
        => new(new AxiosConfig { BaseAddress = "http://bench.local/" }, handler: new FakeHttpHandler(body, Payloads.JsonContentType));
}
