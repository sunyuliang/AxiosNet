using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;

namespace AxiosNet.Benchmarks;

/// <summary>
/// 响应 <c>Content-Type</c> 的解析成本：每条响应都构造类型化值（Eager）vs 只留原始头值、按需拆分（Lazy）。每条都新建响应并把头按原始字符串塞进容器，否则头容器缓存会让两边都量到 0。
/// </summary>
[Description("响应 Content-Type 解析：类型化解析（旧）vs 原始值 + 按需拆分（新）")]
[BenchmarkGroup("纯 CPU（FakeHttpHandler，无网络）", "Allocated 是确定性量，看它就够；旧路径每条要付整个媒体类型对象图", Order = 2)]
[BenchmarkHint("Lazy_RawOnly 相对 Eager 少的那些字节，就是流式 / 字节响应白付的解析成本")]
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[HideColumns("Job")]
public class ResponseContentTypeBenchmarks
{
    // 真实响应里最常见的形态：媒体类型 + 字符集。
    private const string RawContentType = "application/json; charset=utf-8";

    /// <summary>端到端参照用的客户端（构造一次，避免把池与 Handler 的成本算进每次请求）。</summary>
    private AxiosClient _client = null!;

    /// <summary>建好端到端那条基准要用的客户端。</summary>
    [GlobalSetup]
    public void Setup() => _client = new AxiosClient(handler: new RawContentTypeHandler());

    /// <summary>释放客户端与它的池。</summary>
    [GlobalCleanup]
    public void Cleanup() => _client.Dispose();

    /// <summary>旧实现：BCL 类型化解析（构造 <see cref="MediaTypeHeaderValue"/> 与参数对象）。</summary>
    /// <returns>媒体类型与字符集。</returns>
    [Benchmark(Baseline = true)]
    public (string? MediaType, string? CharSet) Eager_MediaTypeHeaderValue()
    {
        using var response = NewResponse();
        var parsed = response.Content.Headers.ContentType;
        return (parsed?.MediaType, parsed?.CharSet);
    }

    /// <summary>新实现的最省路径：只把原始头值取出来，不解析（流式 / 字节 / 不读响应上下文）。</summary>
    /// <returns>原始头值。</returns>
    [Benchmark]
    public string? Lazy_RawOnly()
    {
        using var response = NewResponse();
        return ContentTypeHeader.ReadRaw(response.Content.Headers);
    }

    /// <summary>要读正文的路径：媒体类型与字符集都要（文本 / 标量 / 强类型业务对象都走这一档）。</summary>
    /// <returns>媒体类型与字符集。</returns>
    [Benchmark]
    public (string? MediaType, string? CharSet) Lazy_Both()
    {
        using var response = NewResponse();
        return ContentTypeHeader.Split(ContentTypeHeader.ReadRaw(response.Content.Headers));
    }

    /// <summary>端到端参照：一次强类型 GET 的总分配（含上面的 Content-Type 处理），用来看这一项占整体多少。</summary>
    /// <returns>反序列化后的对象。</returns>
    [Benchmark]
    public async Task<BenchmarkPerson> EndToEnd_TypedJsonGet()
    {
        var response = await _client.GetAsync<BenchmarkPerson>("http://bench.local/person").ConfigureAwait(false);
        return response.Data!;
    }

    /// <summary>造一条「刚从网络拿到」的响应：Content-Type 是原始字符串，类型化值尚未解析。</summary>
    private static HttpResponseMessage NewResponse()
    {
        var content = new ByteArrayContent(Array.Empty<byte>());
        content.Headers.TryAddWithoutValidation("Content-Type", RawContentType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>基准用的最小响应体（与 <see cref="BenchmarkPerson"/> 对应）。</summary>
    private static readonly byte[] PersonJson = Encoding.UTF8.GetBytes("""{"name":"bench","age":42}""");

    /// <summary>返回固定 JSON、且 Content-Type 以**原始字符串**种下的 handler（贴近网络路径）。</summary>
    private sealed class RawContentTypeHandler : HttpMessageHandler
    {
        /// <summary>每次调用都新建响应，避免 BCL 的解析缓存把成本抹平。</summary>
        /// <param name="request">请求（本基准不关心）。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>固定 JSON 响应。</returns>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(PersonJson);
            content.Headers.TryAddWithoutValidation("Content-Type", RawContentType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }
    }
}

/// <summary>基准用的最小 DTO（反序列化目标）。</summary>
/// <param name="Name">名字。</param>
/// <param name="Age">年龄。</param>
public sealed record BenchmarkPerson(string Name, int Age);
