using System.ComponentModel;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using AxiosNet.Benchmarks.Infrastructure;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;

namespace AxiosNet.Benchmarks;

/// <summary>
/// 热点六：大正文上传（真实回环 socket）。
/// <para>
/// <b>回答的问题</b>：正文从 1MB 涨到 32MB 时，AxiosNet 有没有多一次拷贝、有没有把正文缓冲起来？
/// 上传链路在库内只做「选一个 <see cref="HttpContent"/>」（<c>byte[]</c> / <c>ReadOnlyMemory</c> 零拷贝持有），
/// 所以预期是：<c>Allocated</c> 只与内容对象同量级（百字节），不随 <c>Megabytes</c> 增长，耗时与裸
/// <see cref="HttpClient"/> 同量级。
/// </para>
/// <para>
/// <b>两种框架分开测</b>：<c>byte[]</c> / 可 seek <see cref="MemoryStream"/> 带 <c>Content-Length</c>；
/// 不可 seek 的流（<see cref="NonSeekableStream"/>）、<c>IAsyncEnumerable&lt;byte[]&gt;</c> 与
/// <c>IAsyncEnumerable&lt;ReadOnlyMemory&lt;byte&gt;&gt;</c> 没有长度，走 <c>Transfer-Encoding: chunked</c>。
/// 三种 chunked 形态走同一条传输路径，差别只在「每一块从哪来」：流是框架按 80KB 缓冲读，
/// 异步序列是调用方直接给块（不分片再拷贝）。
/// </para>
/// <para>
/// <b>读数方式</b>：<c>Ratio</c> 相对 <c>HttpClient_Upload_ByteArray</c>（同为时间比，吞吐 = 1 / Ratio）。
/// 上传侧没有「8MB 信任上限」这回事——那个阈值只作用于响应预分配；这里三档尺寸只是刻度。
/// 流式用例的验收线：<c>Allocated</c> 只随「块数」增长（64KB 一块约 3–4 KB 传输层开销，32MB ≈ 2.2MB），
/// 不是另存一份正文；时间列只下「同量级」的结论——同进程回环下 <c>RatioSD</c> 可到 0.1–0.6，
/// 复跑会让 1MB / 8MB 档的先后顺序翻转，谁更快不能当门槛。
/// </para>
/// </summary>
[Description("大正文上传（回环 socket）：1 / 8 / 32MB，byte[] / ReadOnlyMemory / Stream 与 IAsyncEnumerable（chunked）")]
[BenchmarkGroup("回环 socket（数字含 TCP 往返与线程池调度）", "绝对值随机器负载摆，能下结论的是比值", Order = 1)]
[BenchmarkHint("Allocated 只应与内容对象同量级、不随 Megabytes 增长；chunked 三类形态互相印证")]
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[HideColumns("Job")]
// 不能 sealed：BDN 会派生基准类来拦截调用。
public class LargeBodyUploadBenchmarks
{
    /// <summary>正文大小（MB）。三档只是刻度；上传侧没有预分配阈值。</summary>
    [Params(1, 8, 32)]
    public int Megabytes { get; set; }

    /// <summary>流式正文的块大小：与框架 StreamContent 的内部缓冲同量级，避免测到「块太小」的框架开销。</summary>
    private const int StreamChunkBytes = 64 * 1024;

    private byte[] _payload = null!;
    private byte[][] _payloadChunks = null!;
    private LoopbackHttpServer _server = null!;
    private AxiosClient _axios = null!;
    private HttpClient _http = null!;
    private string _url = null!;

    [GlobalSetup]
    public void Setup()
    {
        _payload = Payloads.Large(Megabytes * 1024 * 1024);

        // IAsyncEnumerable<byte[]> 的块必须是独立数组：切块成本放在测量区外（真实生产者也不会每请求重切）。
        var chunkCount = (_payload.Length + StreamChunkBytes - 1) / StreamChunkBytes;
        _payloadChunks = new byte[chunkCount][];
        for (var i = 0; i < chunkCount; i++)
        {
            var offset = i * StreamChunkBytes;
            var length = Math.Min(StreamChunkBytes, _payload.Length - offset);
            _payloadChunks[i] = new byte[length];
            Buffer.BlockCopy(_payload, offset, _payloadChunks[i], 0, length);
        }

        // 响应固定为一个小 JSON：上传用例不应顺带下载大正文
        _server = LoopbackHttpServer.Start(Payloads.UserJsonBytes);
        _url = _server.Url + "upload";

        _axios = new AxiosClient();

        // 两侧都用绝对 URL，避免把 BaseAddress 拼接成本算进任何一侧
        _http = new HttpClient(ComparableHttpClientHandler.Create());
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        _axios.Dispose();
        _http.Dispose();
        await _server.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>基线：裸 HttpClient + ByteArrayContent（零拷贝持有同一个数组）。</summary>
    [Benchmark(Baseline = true)]
    public async Task HttpClient_Upload_ByteArray()
    {
        using var content = new ByteArrayContent(_payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await _http.PostAsync(_url, content).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>库侧路径：<c>byte[]</c> → ByteArrayContent，同样零拷贝。</summary>
    [Benchmark]
    public async Task AxiosNet_Upload_ByteArray()
    {
        var response = await _axios
            .PostAsync<string>(_url, _payload, c => c.ContentType = AxiosContentType.OctetStream)
            .ConfigureAwait(false);
        _ = response.Data;
    }

    /// <summary>零拷贝 <see cref="ReadOnlyMemory{T}"/> 路径（多一次结构体装箱，几十字节量级）。</summary>
    [Benchmark]
    public async Task AxiosNet_Upload_ReadOnlyMemory()
    {
        var response = await _axios
            .PostAsync<string>(_url, (ReadOnlyMemory<byte>)_payload, c => c.ContentType = AxiosContentType.OctetStream)
            .ConfigureAwait(false);
        _ = response.Data;
    }

    /// <summary>可 seek 的流：StreamContent 按 <c>Length - Position</c> 写 Content-Length。</summary>
    [Benchmark]
    public async Task AxiosNet_Upload_StreamSeekable()
    {
        using var stream = new MemoryStream(_payload, writable: false);
        var response = await _axios
            .PostAsync<string>(_url, stream, c => c.ContentType = AxiosContentType.OctetStream)
            .ConfigureAwait(false);
        _ = response.Data;
    }

    /// <summary>基线：裸 HttpClient + 不可 seek 的流，走 chunked。</summary>
    [Benchmark]
    public async Task HttpClient_Upload_StreamChunked()
    {
        using var stream = new NonSeekableStream(new MemoryStream(_payload, writable: false));
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await _http.PostAsync(_url, content).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>不可 seek 的流：无长度，走 chunked。</summary>
    [Benchmark]
    public async Task AxiosNet_Upload_StreamChunked()
    {
        using var stream = new NonSeekableStream(new MemoryStream(_payload, writable: false));
        var response = await _axios
            .PostAsync<string>(_url, stream, c => c.ContentType = AxiosContentType.OctetStream)
            .ConfigureAwait(false);
        _ = response.Data;
    }

    /// <summary>流式正文（推荐形态）：<c>IAsyncEnumerable&lt;ReadOnlyMemory&lt;byte&gt;&gt;</c>，块零拷贝。</summary>
    [Benchmark]
    public async Task AxiosNet_Upload_AsyncEnumerableMemory()
    {
        var response = await _axios
            .PostAsync<string>(_url, MemoryChunks(_payload), c => c.ContentType = AxiosContentType.OctetStream)
            .ConfigureAwait(false);
        _ = response.Data;
    }

    /// <summary>流式正文：<c>IAsyncEnumerable&lt;byte[]&gt;</c>（块在测量区外预先切好）。</summary>
    [Benchmark]
    public async Task AxiosNet_Upload_AsyncEnumerableBytes()
    {
        var response = await _axios
            .PostAsync<string>(_url, ByteChunks(_payloadChunks), c => c.ContentType = AxiosContentType.OctetStream)
            .ConfigureAwait(false);
        _ = response.Data;
    }

    /// <summary>按 <c>StreamChunkBytes</c> 切片产出，不额外拷贝。</summary>
    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> MemoryChunks(
        byte[] payload,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        for (var offset = 0; offset < payload.Length; offset += StreamChunkBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return payload.AsMemory(offset, Math.Min(StreamChunkBytes, payload.Length - offset));
        }
    }

    private static async IAsyncEnumerable<byte[]> ByteChunks(
        byte[][] chunks,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return chunk;
        }
    }
}
