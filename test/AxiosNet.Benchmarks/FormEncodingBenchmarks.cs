using System.ComponentModel;
using AxiosNet.Benchmarks.Infrastructure;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;

namespace AxiosNet.Benchmarks;

/// <summary>
/// 表单编码：反射展平 → 逐键值百分号编码 → 拼正文，摘掉网络只量 CPU。三个变体是纯 ASCII、全转义与嵌套键（<c>[a][b]</c>），<c>Ratio</c> 相对同字段数的 <c>FlatAscii</c>。
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

    // 表单请求都走这个客户端（FakeHttpHandler，不出网）。
    private AxiosClient _client = null!;
    // 扁平 ASCII 表单：转义映射几乎不命中，是编码的最省路径。
    private Dictionary<string, string> _ascii = null!;
    // 全字段都要百分号转义的表单（空格 / 中文 / & / = / + / % / 斜杠）。
    private Dictionary<string, string> _unicode = null!;
    // 嵌套键名（user[profile][city]）加数组（tags[]）的表单。
    private Dictionary<string, object?> _nested = null!;

    /// <summary>按 Fields 档位预造三份表单：字典构造与字符串插值都在测量区外。</summary>
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

    /// <summary>释放客户端。</summary>
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
