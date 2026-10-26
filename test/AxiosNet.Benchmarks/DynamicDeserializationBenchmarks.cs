using System.ComponentModel;
using System.Text.Json;
using AxiosNet.Benchmarks.Infrastructure;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;

namespace AxiosNet.Benchmarks;

/// <summary>
/// 热点四：dynamic 反序列化（响应 → <c>AxiosDynamic</c> 对象图）。
/// <para>
/// 三条路径解析的是<b>同一份字节</b>，差别只在落成什么：
/// <list type="bullet">
/// <item><c>Typed</c>（基线）—— <c>GetAsync&lt;UserDto&gt;</c>，<c>System.Text.Json</c> 直接映射到强类型；</item>
/// <item><c>Dynamic</c> —— <c>GetAsync&lt;dynamic&gt;</c>，先 <c>JsonDocument</c> 解析，再逐节点构造
/// <see cref="AxiosDynamic"/> / <c>List&lt;object?&gt;</c>（每个属性一次索引器写入）；</item>
/// <item><c>JsonElement</c> —— 非泛型 <c>GetAsync</c>，只留 <c>JsonElement</c> 不建对象图；</item>
/// <item><c>Dynamic_WithAccess</c> —— 在上面再加 5 次成员访问，量 <c>TryGetMember</c> 的成本
/// （对象图是<b>建好</b>的，访问应当很便宜；如果这条相对 <c>Dynamic</c> 明显变贵，说明访问路径退化了）。</item>
/// </list>
/// </para>
/// <para>
/// <c>Ratio</c> 相对 <c>Typed</c>：读作「dynamic 比强类型贵几倍」。<c>Wide</c> 两行换成 13 字段 / 3 层嵌套的
/// 载荷，用来看差距随对象图规模怎么放大（宽图下 <c>AxiosDynamic</c> 的每节点成本才是主要项）。
/// </para>
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
    private AxiosClient _user = null!;
    private AxiosClient _wide = null!;

    [GlobalSetup]
    public void Setup()
    {
        _user = CreateClient(Payloads.UserJsonBytes);
        _wide = CreateClient(Payloads.WideJsonBytes);
    }

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

    private static AxiosClient CreateClient(byte[] body)
        => new(new AxiosConfig { BaseAddress = "http://bench.local/" }, handler: new FakeHttpHandler(body, Payloads.JsonContentType));
}
