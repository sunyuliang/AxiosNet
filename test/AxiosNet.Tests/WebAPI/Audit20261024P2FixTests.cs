using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;

namespace AxiosNet.Tests.WebAPI;

/// <summary>
/// 2026-10-24 P2 性能轮的声明式侧回归测试（按审核报告编号）：
/// <list type="bullet">
/// <item><b>13</b>：流式逐行解码复用取流时解析的编码（非 UTF-8 charset 声明也解得对）；</item>
/// <item><b>10</b>：帧序列化器取流时解析一次，流开始后改注册表不影响已创建的枚举器；</item>
/// <item><b>12</b>：属性取值走缓存的委托——值类型属性与结构体模型照旧绑定正确。</item>
/// </list>
/// </summary>
public sealed class Audit20261024P2FixTests
{
    [Fact]
    public async Task Ndjson_NonUtf8DeclaredCharset_DecodesFrames()
    {
        // 响应声明 iso-8859-1：逐行解码用取流时解析的编码（此前每行重跑 charset 表查找）
        var body = Encoding.Latin1.GetBytes("{\"n\":1,\"text\":\"café\"}\n{\"n\":2,\"text\":\"naïve\"}\n");

        using var client = CreateStreamingClient(
            new CharsetAwareHandler("application/x-ndjson; charset=iso-8859-1", body));

        var response = await client.RequestStreamAsync(new FrameRequest());

        var items = await CollectAsync(response.Data!);
        Assert.Equal(2, items.Count);
        Assert.Equal("café", items[0].Text);
        Assert.Equal("naïve", items[1].Text);
    }

    [Fact]
    public async Task Streaming_SerializerResolvedOnceAtStreamStart()
    {
        // 取流之后才注册的序列化器不影响已创建的枚举器：帧序列化器按流固定，取流时解析一次
        // （用独有的媒体类型键，避免污染并行执行的其他流式用例）
        const string mediaType = "application/x-ndjson-p2fix";
        var body = Encoding.UTF8.GetBytes("{\"n\":1}\n");

        using var client = CreateStreamingClient(new CharsetAwareHandler(mediaType, body));

        var response = await client.RequestStreamAsync(new FrameRequest());

        AxiosContent.Register(mediaType, new ThrowingFrameSerializer());
        try
        {
            var items = await CollectAsync(response.Data!);
            Assert.Equal(1, Assert.Single(items).N);
        }
        finally
        {
            AxiosContent.Unregister(mediaType);
        }
    }

    [Fact]
    public async Task Bindings_ValueTypeProperties_WorkThroughCachedGetters()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        // 取值走构建期缓存的委托：int / bool 等值类型属性（装箱返回）照旧绑定正确
        var response = await client.RequestAsync(new BoundRequest { Id = 42, Name = "n", Flag = true });

        var received = Assert.Single(server.Received);
        Assert.Equal("/items/42", received.Path);
        Assert.Equal("Name=n&Flag=true", received.Query);   // 键名用属性名；bool 文本 true/false
        Assert.Equal(200, response.Status);
    }

    [Fact]
    public async Task InterceptorReplacingStreamWithNull_ThrowsAxiosException()
    {
        using var client = CreateStreamingClient(
            new CharsetAwareHandler("application/x-ndjson", Encoding.UTF8.GetBytes("{\"n\":1}\n")));

        // 响应拦截器把 Data 换成 null：不能漏出裸 InvalidOperationException（破坏统一错误模型），
        // 且必须报 ERR_BAD_RESPONSE，让调用方按 c.Code 分类处理。
        client.Axios.Interceptors.Response.Use(
            response =>
            {
                response.Data = null;
                return response;
            });

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.RequestStreamAsync(new FrameRequest()));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
    }

    // ---------------------------------------------------------------
    // 辅助
    // ---------------------------------------------------------------

    private static ApiClient CreateStreamingClient(HttpMessageHandler handler)
        => new(
            new AxiosClient(
                new AxiosConfig { BaseAddress = "http://stream.test/" },
                handler: handler,
                disposeHandler: true),
            ownsAxiosClient: true);

    private static async Task<List<FrameItem>> CollectAsync(IAsyncEnumerable<FrameItem> source)
    {
        var items = new List<FrameItem>();
        await foreach (var item in source.ConfigureAwait(false))
        {
            items.Add(item);
        }

        return items;
    }

    public sealed record FrameItem(int N, string? Text);

    [Get("stream")]
    private sealed class FrameRequest : IApiRequest<FrameItem>
    {
    }

    /// <summary>预置正文的流式响应，Content-Type 可配置（验证非 UTF-8 charset 的逐行解码）。</summary>
    private sealed class CharsetAwareHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly string _contentType;

        public CharsetAwareHandler(string contentType, byte[] body)
        {
            _contentType = contentType;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StreamContent(new MemoryStream(_body));
            // Parse 才接受带参数的完整头值；MediaTypeHeaderValue 的构造函数只收裸媒体类型
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(_contentType);

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request
            };

            return Task.FromResult(response);
        }
    }

    /// <summary>一旦参与帧反序列化就失败：用来证明序列化器是取流时解析的。</summary>
    private sealed class ThrowingFrameSerializer : IContentSerializer
    {
        public string? MediaType => null;

        public string Serialize(object? value, Type type, AxiosConfig config) => "{}";

        public object? Deserialize(string text, Type type, AxiosConfig config)
            => throw new InvalidOperationException("帧序列化器应在取流时解析，流开始后注册不该生效。");
    }

    [Get("items/{id}")]
    private sealed class BoundRequest : IApiRequest<Person>
    {
        [Path]
        public int Id { get; set; }

        [Query]
        public string? Name { get; set; }

        [Query]
        public bool Flag { get; set; }
    }
}
