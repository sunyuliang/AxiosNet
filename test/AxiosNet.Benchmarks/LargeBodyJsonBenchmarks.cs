using System.ComponentModel;
using System.Text.Json;
using AxiosNet.Benchmarks.Infrastructure;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;

namespace AxiosNet.Benchmarks;

/// <summary>
/// 大正文 JSON 响应（真实回环 socket）。
/// <para>
/// <b>回答的问题</b>：同样 1 / 8 / 32MB 的 JSON，三种交付形态各自付几次载荷的分配，
/// 库侧相对「裸 HttpClient + System.Text.Json」有没有多一次拷贝。字节路径已在
/// <see cref="LargeBodyDownloadBenchmarks"/> 里按 1× 钉住；这里补的是 JSON 路径：
/// <c>byte[]</c> 仍应是 1×，强类型是「1× 字节 + 对象图」，<c>JsonElement</c> 是「1× 字节 + 文档 + 克隆」。
/// </para>
/// <para>
/// <b>基线选择</b>：<c>HttpClient_GetBytes</c> 只读字节，是「零处理」参照；<c>HttpClient_Typed</c> /
/// <c>HttpClient_JsonElement</c> 用与库相同的 <see cref="JsonSerializerDefaults.Web"/> 和同样的
/// <c>JsonDocument.Parse + RootElement.Clone</c> 算法，用来判断库侧相对裸 STJ 有没有额外成本。
/// </para>
/// <para>
/// <b>读数方式</b>：主指标是 <c>Allocated / 载荷</c>（确定性量）。本机实测：<c>byte[]</c> = 1.00×；
/// 强类型 ≈ 3.4×（对象图 + 字符串，随记录数线性）；<c>JsonElement</c> ≈ 4.1×（原始字节 + 文档 +
/// 克隆 + 每 token 元数据，不是凭空的「3×」）。三种形态都随 <c>Megabytes</c> 线性增长才算正常——
/// 某一档突然跳一个载荷量级，说明多了一次整串复制或 UTF-16 往返。
/// </para>
/// </summary>
[Description("大正文 JSON 响应（回环 socket）：1 / 8 / 32MB，字节 / 强类型 / JsonElement")]
[BenchmarkGroup("回环 socket（数字含 TCP 往返与线程池调度）", "绝对值随机器负载摆，能下结论的是比值", Order = 1)]
[BenchmarkHint("Allocated / 载荷：byte[] ≈ 1×；强类型 ≈ 3.4×；JsonElement ≈ 4.1×（文档 + 克隆 + token 元数据）。三档线性 = 没有额外整串复制")]
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[HideColumns("Job")]
// 不能 sealed：BDN 会派生基准类来拦截调用。
public class LargeBodyJsonBenchmarks
{
    /// <summary>正文大小（MB）。与二进制下载组同刻度，方便两组对照。</summary>
    [Params(1, 8, 32)]
    public int Megabytes { get; set; }

    /// <summary>与库默认一致的 JSON 选项（Web：camelCase + 大小写不敏感），否则裸 STJ 对照不公平。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private LoopbackHttpServer _server = null!;
    private AxiosClient _axios = null!;
    private HttpClient _http = null!;
    private string _url = null!;

    [GlobalSetup]
    public void Setup()
    {
        _server = LoopbackHttpServer.Start(Payloads.LargeJson(Megabytes * 1024 * 1024), Payloads.JsonContentType);
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

    /// <summary>基线：裸 HttpClient 把正文读成 byte[]，不做任何解析。</summary>
    [Benchmark(Baseline = true)]
    public async Task<int> HttpClient_GetBytes()
    {
        var data = await _http.GetByteArrayAsync(_url).ConfigureAwait(false);
        return data.Length;
    }

    /// <summary>裸 HttpClient + STJ 强类型：库侧强类型路径的对照。</summary>
    [Benchmark]
    public async Task<int> HttpClient_Typed()
    {
        var data = await _http.GetByteArrayAsync(_url).ConfigureAwait(false);
        return JsonSerializer.Deserialize<List<JsonItemDto>>(data, JsonOptions)!.Count;
    }

    /// <summary>裸 HttpClient + <c>JsonDocument.Parse + Clone</c>：库侧 JsonElement 路径的对照。</summary>
    [Benchmark]
    public async Task<int> HttpClient_JsonElement()
    {
        var data = await _http.GetByteArrayAsync(_url).ConfigureAwait(false);
        using var document = JsonDocument.Parse(data);
        return document.RootElement.Clone().GetArrayLength();
    }

    /// <summary>库侧字节路径：应与二进制下载同口径，只付 1× 载荷。</summary>
    [Benchmark]
    public async Task<int> AxiosNet_Bytes()
    {
        var response = await _axios.GetAsync<byte[]>(_url).ConfigureAwait(false);
        return response.Data!.Length;
    }

    /// <summary>库侧强类型路径：1× 字节 + 对象图 + 字符串。</summary>
    [Benchmark]
    public async Task<int> AxiosNet_Typed()
    {
        var response = await _axios.GetAsync<List<JsonItemDto>>(_url).ConfigureAwait(false);
        return response.Data!.Count;
    }

    /// <summary>库侧非泛型路径：保留 <c>JsonElement</c>，不建业务对象图。</summary>
    [Benchmark]
    public async Task<int> AxiosNet_JsonElement()
    {
        var response = await _axios.GetAsync(_url).ConfigureAwait(false);
        return ((JsonElement)response.Data!).GetArrayLength();
    }
}
