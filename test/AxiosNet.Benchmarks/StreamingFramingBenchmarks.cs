using System.ComponentModel;
using AxiosNet.Benchmarks.Infrastructure;
using AxiosNet.WebAPI;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;

namespace AxiosNet.Benchmarks;

/// <summary>
/// 热点五：流式响应分帧（NDJSON / SSE / JSON 数组）。
/// <para>
/// 被测路径：<see cref="ApiClient.RequestStreamAsync{TItem}"/> 拿流 → 字节级找帧边界 → 每帧交给序列化器 →
/// <c>await foreach</c> 消费完。三种格式共用同一套「读块 / 压缩 / 上限 / 错误映射」，
/// 差别只在边界怎么找：NDJSON 找 <c>0x0A</c>、SSE 找空行并剥 <c>data:</c>、JSON 数组靠
/// <c>Utf8JsonReader</c> 增量定位顶层元素。
/// </para>
/// <para>
/// <b>读法</b>：一次调用 = 一个完整流（含取流、分帧、逐帧反序列化、释放）。
/// 三个 <c>Frames</c> 档位（10 / 100 / 1000）用来把成本拆成两部分：
/// <c>10 → 1000</c> 的<b>斜率</b>是每帧成本，<c>Frames = 10</c> 那一行里剩下的固定部分才是取流开销。
/// 单看一档数字无法区分「每帧变贵」和「取流变贵」，必须两档一起看。
/// </para>
/// <para>
/// <c>Ratio</c> 相对同 <c>Frames</c> 的 NDJSON（最省的分帧方式），读作「这种格式贵几倍」。
/// 三种格式的帧内容完全相同（同一份 <see cref="StreamFrame"/> JSON），因此比值只反映分帧路径本身。
/// </para>
/// </summary>
[Description("流式分帧：NDJSON（基线）/ SSE / JSON 数组，10 与 100 与 1000 帧三档")]
[BenchmarkGroup("纯 CPU（FakeHttpHandler，无网络）", "没有网络噪声，比值与 Allocated 都稳", Order = 2)]
[BenchmarkHint("比值 = 格式差；Frames 三档的斜率 = 每帧成本，小档位剩余才是取流开销")]
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[HideColumns("Job")]
// 不能 sealed：BDN 会派生基准类来拦截调用（0.15 起对 sealed 直接报校验错误）。
public class StreamingFramingBenchmarks
{
    /// <summary>单个流里的帧数：斜率 = 每帧成本，小档位剩余 = 取流固定开销。</summary>
    [Params(10, 100, 1000)]
    public int Frames { get; set; }

    private readonly StreamRequest _request = new();
    private ApiClient _ndjson = null!;
    private ApiClient _sse = null!;
    private ApiClient _array = null!;

    [GlobalSetup]
    public void Setup()
    {
        // 每档帧数各预编码一次：基准方法里不做字符串拼接 / 编码
        _ndjson = CreateClient(Payloads.Ndjson(Frames), Payloads.NdjsonContentType);
        _sse = CreateClient(Payloads.Sse(Frames), Payloads.SseContentType);
        _array = CreateClient(Payloads.JsonArray(Frames), Payloads.JsonContentType);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _ndjson.Dispose();
        _sse.Dispose();
        _array.Dispose();
    }

    /// <summary>基线：行分隔 JSON，边界就是 <c>\n</c>。</summary>
    [Benchmark(Baseline = true)]
    public Task<int> Ndjson() => CountFramesAsync(_ndjson, AxiosStreamFormat.NewlineDelimitedJson);

    /// <summary>SSE：空行结束一帧，且要剥 <c>data: </c> 前缀、跳过注释 / 其它字段。</summary>
    [Benchmark]
    public Task<int> Sse() => CountFramesAsync(_sse, AxiosStreamFormat.ServerSentEvents);

    /// <summary>JSON 数组：没有行分隔符，每个元素都要走一遍 <c>Utf8JsonReader</c> 定位边界。</summary>
    [Benchmark]
    public Task<int> JsonArray() => CountFramesAsync(_array, AxiosStreamFormat.JsonArray);

    private async Task<int> CountFramesAsync(ApiClient client, AxiosStreamFormat format)
    {
        var response = await client.RequestStreamAsync(_request, format).ConfigureAwait(false);

        var count = 0;
        await foreach (var frame in response.Data!.ConfigureAwait(false))
        {
            // 触摸一下帧内容，避免整个循环被当成死代码
            count += frame.Id >= 0 ? 1 : 0;
        }

        return count;
    }

    private static ApiClient CreateClient(byte[] body, string contentType)
    {
        var axios = new AxiosClient(
            new AxiosConfig { BaseAddress = "http://bench.local/" },
            handler: new FakeHttpHandler(body, contentType));

        return new ApiClient(axios, ownsAxiosClient: true);
    }
}
