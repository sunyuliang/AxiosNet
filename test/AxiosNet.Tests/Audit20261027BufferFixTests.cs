using System.Net;
using System.Text;
using System.Text.Json;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI.Internal;

namespace AxiosNet.Tests;

/// <summary>
/// 2026-10-27 缓冲区 / 分配审计：响应正文的「声明长度预分配」与「段式累积」两条路径的边界语义。
/// <list type="bullet">
/// <item><b>声明长度只是提示</b>：声明值比实际正文短时（代理改头、自制 <see cref="HttpContent"/>、
/// Handler 不按声明截断），读完声明值必须继续读到 EOF，否则静默丢掉尾部数据；
/// 丢数据的同时还会绕过 <see cref="AxiosConfig.LimitSettings.MaxContentLength"/> —— 上限校验按真实字节数走。</item>
/// <item><b>声明长度恰好命中</b>时仍要零拷贝交出预分配数组（1× 载荷），不能为了「探测是否还有更多」
/// 退化成「预分配 + 重新装配」的 2× 分配。</item>
/// <item><b>段式累积的缓冲区所有权唯一</b>：跨多个池化段装配后字节必须完全一致（回归护栏）。</item>
/// <item><b>流式分帧的缓冲不按历史峰值长驻</b>：一个超大元素 / 超大帧之后，整条流不该一直拖着那块内存
/// （JSON 数组的溢写缓冲、SSE 的帧缓冲是同一个坑的两处）。</item>
/// </list>
/// 分配测量用进程级计数，因此整类放进 <see cref="AllocationCollection"/> 独占运行。
/// </summary>
[Collection(AllocationCollection.Name)]
public sealed class Audit20261027BufferFixTests
{
    private const string BlobUrl = "http://fixed.local/blob";

    // ------------------------------------------------------------------
    // 1. 声明长度偏短：不得截断
    // ------------------------------------------------------------------

    [Fact]
    public async Task DeclaredLength_ShorterThanBody_IsNotTruncated()
    {
        var payload = Payload(200_000, seed: 3);
        using var client = new AxiosClient(handler: new BodyHandler(payload, declaredLength: 1024));

        var response = await client.GetAsync<byte[]>(BlobUrl, c => c.ResponseType = ResponseType.ArrayBuffer);

        // 缺陷版本只交回 1024 字节：读到声明值就 return，后面的数据被静默丢弃。
        Assert.Equal(payload.Length, response.Data!.Length);
        Assert.Equal(payload, response.Data);
        Assert.Equal(payload.Length, response.BodyBytes);
    }

    [Fact]
    public async Task DeclaredLength_ShorterThanBody_StillEnforcesMaxContentLength()
    {
        var payload = Payload(200_000, seed: 4);
        using var client = new AxiosClient(handler: new BodyHandler(payload, declaredLength: 1024));

        // 上限判的是真实字节数：声明 1 KB 不等于正文 1 KB。
        var error = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<byte[]>(
            BlobUrl,
            c =>
            {
                c.ResponseType = ResponseType.ArrayBuffer;
                c.Limits.MaxContentLength = 4096;
            }));

        Assert.Equal(AxiosException.ErrBadResponse, error.Code);
        Assert.Contains("maxContentLength", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeclaredLength_ShorterThanBody_TextResponseIsComplete()
    {
        var text = new string('a', 100_000);
        var payload = Encoding.UTF8.GetBytes(text);
        using var client = new AxiosClient(handler: new BodyHandler(payload, declaredLength: payload.Length / 2));

        var response = await client.GetAsync<string>(BlobUrl, c => c.ResponseType = ResponseType.Text);

        Assert.Equal(text, response.Data);
    }

    [Fact]
    public async Task DeclaredLength_SatisfiedThenStreamBreaks_StillSucceeds()
    {
        // 正文已按声明读满，随后连接就断了（尾部探测撞上断流）：请求本身不该失败。
        var payload = Payload(64 * 1024, seed: 7);
        using var client = new AxiosClient(handler: new BodyHandler(payload, declaredLength: payload.Length, breakAfterBody: true));

        var response = await client.GetAsync<byte[]>(BlobUrl, c => c.ResponseType = ResponseType.ArrayBuffer);

        Assert.Equal(payload, response.Data);
    }

    // ------------------------------------------------------------------
    // 2. 声明长度精确命中：仍然 1× 分配（不允许为「探测尾部」付 2×）
    // ------------------------------------------------------------------

    [Fact]
    public void DeclaredLength_ExactlyMatchesBody_PaysOnePayloadAllocation()
    {
        const int size = 1024 * 1024;
        var payload = Payload(size, seed: 5);
        using var client = new AxiosClient(handler: new BodyHandler(payload, declaredLength: size));

        Read(); // warmup：JIT 与静态缓存不计入

        var best = long.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            Read();
            best = Math.Min(best, GC.GetTotalAllocatedBytes(precise: true) - before);
        }

        // 预分配路径：直接交出那个数组（1×）。若改成「预分配 + 装配」会到 2×。
        Assert.True(
            best < size * 3 / 2,
            $"声明长度精确命中的响应分配了 {best:N0} B（{best / (double)size:0.00}× 载荷）——"
            + "疑似退化成了「预分配缓冲 + 重新装配」的双份分配。");

        byte[] Read() => client
            .GetAsync<byte[]>(BlobUrl, c => c.ResponseType = ResponseType.ArrayBuffer)
            .GetAwaiter()
            .GetResult()
            .Data!;
    }

    // ------------------------------------------------------------------
    // 3. 段式累积：跨多个池化段装配后字节一致
    // ------------------------------------------------------------------

    [Fact]
    public async Task UndeclaredLength_MultiSegment_AssemblesExactBytes()
    {
        // 300 KB、每块 16 KB、不声明长度：必然跨 81920 / 163840 / … 多个池化段。
        var payload = Payload(300 * 1024, seed: 6);
        using var client = new AxiosClient(handler: new BodyHandler(payload, declaredLength: null, chunk: 16 * 1024));

        var response = await client.GetAsync<byte[]>(BlobUrl, c => c.ResponseType = ResponseType.ArrayBuffer);

        Assert.Equal(payload.Length, response.Data!.Length);
        Assert.Equal(payload, response.Data);
    }

    // ------------------------------------------------------------------
    // 4. 流式分帧：溢写缓冲不按历史峰值长驻
    // ------------------------------------------------------------------

    [Fact]
    public async Task JsonArrayStream_ReleasesOversizedSpillAfterElement()
    {
        // 一个 ~1 MB 的对象元素（跨很多次 8 KB 读，走 _elementSpill），后面跟两个小元素。
        var big = BuildBigObject(1024 * 1024);
        var json = "[" + big + ",{\"id\":1},{\"id\":2}]";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var frames = new JsonStreamEnumerable<JsonElement>(
            stream,
            new AxiosConfig(),
            request: null,
            format: AxiosStreamFormat.JsonArray,
            charSet: "utf-8",
            mediaType: "application/json");

        var spill = typeof(JsonStreamEnumerable<JsonElement>).GetField(
            "_elementSpill",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        await using var enumerator = frames.GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(JsonValueKind.Object, enumerator.Current.ValueKind);
        Assert.True(enumerator.Current.GetRawText().Length > 512 * 1024, "首个元素应当足够大，才能压到溢写缓冲。");

        // 该元素收尾后，峰值缓冲不该继续挂在枚举器上（后面还有整条流要读）。
        var retained = (byte[]?)spill.GetValue(frames);
        var withinBound = retained is null || retained.Length <= 64 * 1024;
        Assert.True(
            withinBound,
            $"超大元素读完后仍持有 {retained?.Length ?? 0:N0} B 的溢写缓冲——整条流会一直拖着这块内存。");

        // 其余元素照常可读（释放缓冲不能影响后续分帧）。
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(1, enumerator.Current.GetProperty("id").GetInt32());
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(2, enumerator.Current.GetProperty("id").GetInt32());
        Assert.False(await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task SseFrame_FrameBufferCapacityDoesNotFollowPeak()
    {
        // 一帧 512 KB 的 SSE data（内容是 JSON 字符串），后面跟一帧小的：帧缓冲的容量不该跟着峰值长驻。
        var big = new string('a', 512 * 1024);
        var sse = "data: \"" + big + "\"\n\n" + "data: \"small\"\n\n";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        var frames = new JsonStreamEnumerable<string>(
            stream,
            new AxiosConfig(),
            request: null,
            format: AxiosStreamFormat.ServerSentEvents,
            charSet: "utf-8",
            mediaType: "text/event-stream");

        var frameData = typeof(JsonStreamEnumerable<string>).GetField(
            "_frameData",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        await using var enumerator = frames.GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(big.Length, enumerator.Current.Length);

        var builder = (StringBuilder)frameData.GetValue(frames)!;
        Assert.True(
            builder.Capacity <= 64 * 1024,
            $"超大帧读完后帧缓冲仍保留 {builder.Capacity:N0} 字符容量——整条流会一直拖着它。");

        // 后续帧照常可读。
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("small", enumerator.Current);
        Assert.False(await enumerator.MoveNextAsync());
    }

    // ------------------------------------------------------------------
    // 助手
    // ------------------------------------------------------------------

    private static byte[] Payload(int size, int seed)
    {
        var payload = new byte[size];
        new Random(seed).NextBytes(payload);
        return payload;
    }

    private static string BuildBigObject(int approxBytes)
    {
        var builder = new StringBuilder(approxBytes + 64).Append("{\"items\":[");
        var index = 0;
        while (builder.Length < approxBytes)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append("{\"id\":").Append(index).Append(",\"name\":\"abcdefghij\"}");
            index++;
        }

        return builder.Append("]}").ToString();
    }

    /// <summary>按块返回正文的 Handler：<paramref name="declaredLength"/> 为 <c>null</c> 时不声明长度（等价 chunked）。</summary>
    private sealed class BodyHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly long? _declared;
        private readonly int _chunk;
        private readonly bool _breakAfterBody;

        public BodyHandler(byte[] body, long? declaredLength, int chunk = 8 * 1024, bool breakAfterBody = false)
        {
            _body = body;
            _declared = declaredLength;
            _chunk = chunk;
            _breakAfterBody = breakAfterBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ChunkedContent(_body, _chunk, _declared, _breakAfterBody);
            if (_declared is long declared)
            {
                content.Headers.ContentLength = declared;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>每次 <c>Read</c> 最多给 <c>chunk</c> 字节的响应内容；声明长度与真实正文可以不一致。</summary>
    private sealed class ChunkedContent : HttpContent
    {
        private readonly byte[] _body;
        private readonly int _chunk;
        private readonly long? _declared;
        private readonly bool _breakAfterBody;

        public ChunkedContent(byte[] body, int chunk, long? declared, bool breakAfterBody)
        {
            _body = body;
            _chunk = chunk;
            _declared = declared;
            _breakAfterBody = breakAfterBody;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = _declared ?? 0;
            return _declared is not null;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new ChunkStream(_body, _chunk, _breakAfterBody));

        private sealed class ChunkStream : Stream
        {
            private readonly byte[] _body;
            private readonly int _chunk;
            private readonly bool _breakAfterBody;
            private int _position;

            public ChunkStream(byte[] body, int chunk, bool breakAfterBody)
            {
                _body = body;
                _chunk = chunk;
                _breakAfterBody = breakAfterBody;
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count) => Fill(buffer.AsSpan(offset, count));

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
                => new(Fill(buffer.Span));

            private int Fill(Span<byte> buffer)
            {
                if (_position >= _body.Length)
                {
                    if (_breakAfterBody)
                    {
                        throw new IOException("连接在正文之后被重置");
                    }

                    return 0;
                }

                var take = Math.Min(Math.Min(buffer.Length, _chunk), _body.Length - _position);
                _body.AsSpan(_position, take).CopyTo(buffer);
                _position += take;
                return take;
            }
        }
    }
}
