using System.ComponentModel;
using AxiosNet.Benchmarks.Infrastructure;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;

namespace AxiosNet.Benchmarks;

/// <summary>
/// 热点三：<c>application/x-www-form-urlencoded</c> 的展平与百分号编码。
/// <para>
/// 被测路径：<see cref="AxiosClient.PostFormAsync"/> → 反射展平（嵌套对象 / 数组 / 字典）→
/// 键值逐个 <c>PercentEncode</c>（键还要做 <c>%5B</c> / <c>%5D</c> 回填）→ 一次成型拼正文 → 上送。
/// 这里用 <see cref="FakeHttpHandler"/> 摘掉网络：编码是纯 CPU，不需要 socket 掺进来。
/// </para>
/// <para>
/// <b>三个变体各自对应一种成本</b>：
/// <list type="bullet">
/// <item><c>FlatAscii</c>（基线）—— 转义映射几乎不命中，只有展平 + 拼接；</item>
/// <item><c>FlatUnicode</c> —— 空格 / 中文 / <c>&amp;</c> / <c>=</c> / <c>+</c> / <c>%</c> / <c>/</c> 全都要转义，
/// 每个字符都要判一次映射，并写 3 个字符；</item>
/// <item><c>Nested</c> —— 键名走 <c>[a][b]</c> 形式，命中 <c>EncodeKey</c> 的两次 <c>Replace</c>
/// （每次 Replace 都产出一个新字符串），是这条通道里最容易退化的地方。</item>
/// </list>
/// </para>
/// <para>
/// <c>Ratio</c> 相对同 <c>Fields</c> 的 <c>FlatAscii</c>，因此它读的是「编码成本 / 基础成本」的倍数，
/// 不受机器绝对性能影响。
/// </para>
/// </summary>
[Description("表单百分号编码：扁平 ASCII（基线）/ 全转义 / 嵌套键名，5 与 50 字段两档")]
[BenchmarkGroup("纯 CPU（FakeHttpHandler，无网络）", "没有网络噪声，比值与 Allocated 都稳", Order = 2)]
[BenchmarkHint("比值 = 编码成本 / 基础成本，看全转义与嵌套键名各贵多少")]
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[HideColumns("Job")]
// 不能 sealed：BDN 会派生基准类来拦截调用（0.15 起对 sealed 直接报校验错误）。
public class FormEncodingBenchmarks
{
    /// <summary>字段数：5 = 典型登录 / 查询表单，50 = 批量提交。</summary>
    [Params(5, 50)]
    public int Fields { get; set; }

    private AxiosClient _client = null!;
    private Dictionary<string, string> _ascii = null!;
    private Dictionary<string, string> _unicode = null!;
    private Dictionary<string, object?> _nested = null!;

    [GlobalSetup]
    public void Setup()
    {
        _client = new AxiosClient(
            new AxiosConfig { BaseAddress = "http://bench.local/" },
            handler: new FakeHttpHandler("""{"ok":true}"""));

        _ascii = Payloads.FormAscii(Fields);
        _unicode = Payloads.FormUnicode(Fields);
        _nested = Payloads.FormNested(Fields);
    }

    [GlobalCleanup]
    public void Cleanup() => _client.Dispose();

    /// <summary>基线：扁平 ASCII 字段，转义映射基本不命中。</summary>
    [Benchmark(Baseline = true)]
    public async Task<int> PostForm_FlatAscii()
    {
        var response = await _client.PostFormAsync("form", _ascii).ConfigureAwait(false);
        return response.Status;
    }

    /// <summary>每个字符都要查转义表并写出 3 个字符（UTF-8 逐字节 %XX）。</summary>
    [Benchmark]
    public async Task<int> PostForm_FlatUnicode()
    {
        var response = await _client.PostFormAsync("form", _unicode).ConfigureAwait(false);
        return response.Status;
    }

    /// <summary>嵌套键名：<c>user[profile][city]</c> 形态，额外走两次 <c>Replace</c>。</summary>
    [Benchmark]
    public async Task<int> PostForm_Nested()
    {
        var response = await _client.PostFormAsync("form", _nested).ConfigureAwait(false);
        return response.Status;
    }
}
