using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AxiosNet.Benchmarks.Infrastructure;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;

namespace AxiosNet.Benchmarks;

/// <summary>
/// JSON 对象上送（真实回环 socket）：正文该付几份。基线是「自己序列化成 UTF-8 字节 + ByteArrayContent」，
/// 也就是 1× 载荷；<c>Allocated / 载荷</c> 明显高于 1.2× 就说明正文又被串行化成了文本再转字节。
/// </summary>
[Description("JSON 对象上送（回环 socket）：HttpClient(UTF-8 字节) 基线 vs AxiosNet 对象正文 / 显式 TextEncoding")]
[BenchmarkGroup("回环 socket（数字含 TCP 往返与线程池调度）", "绝对值随机器负载摆，能下结论的是比值", Order = 2)]
[BenchmarkHint("看 Allocated：对象正文 ≈ 1.2× 载荷是字节出口；≈ 3× 说明退回「先序列化成字符串再转字节」")]
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[HideColumns("Job")]
// 不能 sealed：BDN 会派生基准类来拦截调用（0.15 起对 sealed 直接报校验错误）。
public class JsonUploadBenchmarks
{
    /// <summary>正文元素数：两档只改载荷大小，不改形状。</summary>
    [Params(100, 2000)]
    public int Items { get; set; }

    // 上送的 JSON 对象数组，长度由 Items 档位决定。
    private JsonItemDto[] _payload = null!;
    // 同一载荷的 UTF-8 字节，基线直接交给 ByteArrayContent。
    private byte[] _payloadUtf8 = null!;
    // 回环 socket 服务器，三个变体共用。
    private LoopbackHttpServer _server = null!;
    // 默认序列化配置的客户端，对象正文走字节出口。
    private AxiosClient _axios = null!;
    // 显式 Serialization.TextEncoding 的客户端，对象正文走文本出口。
    private AxiosClient _axiosTextEncoding = null!;
    // 基线 HttpClient，与 AxiosNet 用同一套 handler 配置。
    private HttpClient _http = null!;
    // 上送端点地址。
    private string _url = null!;

    /// <summary>载荷与两个客户端都在测量区外准备好；响应固定是一个小 JSON，不让下载成本掺进来。</summary>
    [GlobalSetup]
    public void Setup()
    {
        _payload = new JsonItemDto[Items];
        for (var i = 0; i < _payload.Length; i++)
        {
            _payload[i] = new JsonItemDto { Id = i, Name = "item-" + i };
        }

        _payloadUtf8 = JsonSerializer.SerializeToUtf8Bytes(_payload);

        _server = LoopbackHttpServer.Start(Payloads.UserJsonBytes);
        _url = _server.Url + "upload";

        _axios = new AxiosClient();
        _axiosTextEncoding = new AxiosClient(new AxiosConfig
        {
            Serialization = { TextEncoding = Encoding.UTF8 }
        });
        _http = new HttpClient(ComparableHttpClientHandler.Create());
    }

    /// <summary>先释放客户端再停服务器。</summary>
    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        _axios.Dispose();
        _axiosTextEncoding.Dispose();
        _http.Dispose();
        await _server.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>基线：自己序列化成 UTF-8 字节，零拷贝交给 <see cref="ByteArrayContent"/>。</summary>
    [Benchmark(Baseline = true)]
    public async Task HttpClient_PostJsonUtf8()
    {
        using var content = new ByteArrayContent(_payloadUtf8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _http.PostAsync(_url, content).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>库侧默认路径：对象正文交给库序列化（应当与基线同量级）。</summary>
    [Benchmark]
    public async Task AxiosNet_PostJsonObject()
    {
        var response = await _axios.PostAsync<string>(_url, _payload).ConfigureAwait(false);
        _ = response.Data;
    }

    /// <summary>显式 <c>Serialization.TextEncoding</c>：按契约回落文本出口，用来对照「串→字节」那份成本。</summary>
    [Benchmark]
    public async Task AxiosNet_PostJsonObject_TextEncoding()
    {
        var response = await _axiosTextEncoding.PostAsync<string>(_url, _payload).ConfigureAwait(false);
        _ = response.Data;
    }
}
