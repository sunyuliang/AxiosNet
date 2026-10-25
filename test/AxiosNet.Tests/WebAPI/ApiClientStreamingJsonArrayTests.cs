using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AxiosNet.WebAPI;

namespace AxiosNet.Tests.WebAPI;

/// <summary>
/// 流式响应的 JSON 数组格式（<see cref="AxiosStreamFormat.JsonArray"/>）：
/// 用 <c>Utf8JsonReader</c> 在字节层增量定位顶层元素，逐元素交付。
/// </summary>
public sealed class ApiClientStreamingJsonArrayTests
{
    // ------------------------------------------------------------------
    // 基本形态
    // ------------------------------------------------------------------

    [Fact]
    public async Task JsonArray_ReadsAllElements_WhenChunksSplitTokensAndMultiByteCharacters()
    {
        var body = Encoding.UTF8.GetBytes("[{\"n\":1,\"text\":\"你好\"},{\"n\":2,\"text\":\"再见\"}]");

        // 在每个「不该切」的位置切块：逗号后、多字节字符中间、token 中间
        var cuts = new[] { 1, 6, 15, 17, 30, body.Length };
        using var client = CreateClient(new ScriptedHandler(Slice(body, cuts)));

        var response = await client.RequestStreamAsync(new ItemRequest(), AxiosStreamFormat.JsonArray);

        var items = await CollectAsync(response.Data!);
        Assert.Equal(200, response.Status);
        Assert.Equal(2, items.Count);
        Assert.Equal(1, items[0].N);
        Assert.Equal("你好", items[0].Text);
        Assert.Equal(2, items[1].N);
        Assert.Equal("再见", items[1].Text);
    }

    [Fact]
    public async Task JsonArray_ElementLargerThanReadBuffer_IsReassembled()
    {
        // 单个元素约 10KB（> 8KB 的读块）：必须走「已消费前缀溢写 + 收尾拼接」的慢路径
        var text = new string('中', 4_000);
        var body = Encoding.UTF8.GetBytes($"[{{\"n\":1,\"text\":\"{text}\"}},{{\"n\":2,\"text\":\"tail\"}}]");

        using var client = CreateClient(new ScriptedHandler(Slice(body, 3_000)));
        var response = await client.RequestStreamAsync(new ItemRequest(), AxiosStreamFormat.JsonArray);

        var items = await CollectAsync(response.Data!);
        Assert.Equal(2, items.Count);
        Assert.Equal(4_000, items[0].Text!.Length);
        Assert.Equal(text, items[0].Text);
        Assert.Equal("tail", items[1].Text);
    }

    [Fact]
    public async Task JsonArray_ElementSpanningManyReads_GrowsSpillBuffer()
    {
        // ~210KB 的单元素（> 8KB 读块的 26 倍）：spill 缓冲区要多次扩容，且跨越几十次压缩
        var text = new string('中', 70_000);
        var body = Encoding.UTF8.GetBytes($"[{{\"n\":1,\"text\":\"{text}\"}},{{\"n\":2}}]");

        using var client = CreateClient(new ScriptedHandler(Slice(body, 8 * 1024)));
        var response = await client.RequestStreamAsync(new ItemRequest(), AxiosStreamFormat.JsonArray);

        var items = await CollectAsync(response.Data!);
        Assert.Equal(2, items.Count);
        Assert.Equal(text, items[0].Text);
        Assert.Equal(2, items[1].N);
    }

    [Fact]
    public async Task JsonArray_OneBytePerRead_StillDeliversEveryElement()
    {
        // 极端分块（每次只给 1 字节）+ 4 字节 emoji + 标量 null/true：边界判定不能靠「块够大」
        var body = Encoding.UTF8.GetBytes("[{\"n\":1,\"text\":\"你好😀\"},null,true,{\"n\":2}]");
        var chunks = body.Select(b => new[] { b }).ToArray();

        using var client = CreateClient(new ScriptedHandler(chunks));
        var response = await client.RequestStreamAsync(new AnyRequest(), AxiosStreamFormat.JsonArray);

        var items = await CollectAsync(response.Data!);
        Assert.Equal(4, items.Count);
        Assert.Equal("你好😀", Assert.IsType<JsonElement>(items[0]).GetProperty("text").GetString());
        Assert.Null(items[1]);
        Assert.True(Assert.IsType<JsonElement>(items[2]).GetBoolean());
        Assert.Equal(2, Assert.IsType<JsonElement>(items[3]).GetProperty("n").GetInt32());
    }

    [Fact]
    public async Task JsonArray_RandomChunkBoundaries_DoNotLoseOrDuplicateElements()
    {
        // 分块边界落在任意位置（1~97 字节）：元素不能丢、不能重、不能截断
        var expected = Enumerable.Range(1, 100).ToList();
        var json = new StringBuilder("[");
        for (var i = 1; i <= 100; i++)
        {
            if (i > 1)
            {
                json.Append(',');
            }

            // 字符串里塞 ] , { } 与转义，外加嵌套数组：都不能影响元素边界
            json.Append($"{{\"n\":{i},\"t\":\"a]b,c{{d}}\\\"e\",\"arr\":[{i},{{\"z\":[1,2]}}]}}");
        }

        json.Append(']');
        var body = Encoding.UTF8.GetBytes(json.ToString());
        var random = new Random(20261025);

        for (var round = 0; round < 10; round++)
        {
            using var client = CreateClient(new ScriptedHandler(SliceRandom(body, random)));
            var response = await client.RequestStreamAsync(new ItemRequest(), AxiosStreamFormat.JsonArray);

            Assert.Equal(expected, (await CollectAsync(response.Data!)).Select(x => x.N));
        }
    }

    [Fact]
    public async Task JsonArray_NestedContainers_WithBracketsAndCommasInsideStrings_AreNotSplit()
    {
        // 字符串里的 ] , { 都不能被当成结构字符：分帧交给 Utf8JsonReader，不是找分隔符
        var body = "[{\"tags\":[\"a]b\",\"c,d\"],\"inner\":{\"x\":[1,2]}},[1,{\"y\":2}]]";

        using var client = CreateClient(new ScriptedHandler(new[] { Encoding.UTF8.GetBytes(body) }));
        var response = await client.RequestStreamAsync(new AnyRequest(), AxiosStreamFormat.JsonArray);

        var items = await CollectAsync(response.Data!);
        Assert.Equal(2, items.Count);

        var first = Assert.IsType<JsonElement>(items[0]);
        Assert.Equal("a]b", first.GetProperty("tags")[0].GetString());
        Assert.Equal("c,d", first.GetProperty("tags")[1].GetString());

        var second = Assert.IsType<JsonElement>(items[1]);
        Assert.Equal(1, second[0].GetInt32());
        Assert.Equal(2, second[1].GetProperty("y").GetInt32());
    }

    [Fact]
    public async Task JsonArray_ScalarElements_AreDeliveredOneByOne()
    {
        using var client = CreateClient(new ScriptedHandler(new[] { Encoding.UTF8.GetBytes("[1, 2 ,3]") }));

        var response = await client.RequestStreamAsync(new IntRequest(), AxiosStreamFormat.JsonArray);

        Assert.Equal(new[] { 1, 2, 3 }, await CollectAsync(response.Data!));
    }

    [Fact]
    public async Task JsonArray_WhitespaceBetweenElements_IsIgnored()
    {
        var body = "[\r\n  {\"n\":1}\r\n , \t{\"n\":2}\n]";

        using var client = CreateClient(new ScriptedHandler(new[] { Encoding.UTF8.GetBytes(body) }));
        var response = await client.RequestStreamAsync(new ItemRequest(), AxiosStreamFormat.JsonArray);

        Assert.Equal(new[] { 1, 2 }, (await CollectAsync(response.Data!)).Select(x => x.N));
    }

    [Fact]
    public async Task JsonArray_TrailingBytesAfterClosingBracket_AreNotRead()
    {
        // 收尾 ']' 之后立刻结束：不校验尾部、也不必等连接关闭（这里尾部就是垃圾）
        var body = "[{\"n\":1}]trailing-garbage";

        using var client = CreateClient(new ScriptedHandler(new[] { Encoding.UTF8.GetBytes(body) }));
        var response = await client.RequestStreamAsync(new ItemRequest(), AxiosStreamFormat.JsonArray);

        Assert.Equal(1, Assert.Single(await CollectAsync(response.Data!)).N);
    }

    // ------------------------------------------------------------------
    // 空流与编码
    // ------------------------------------------------------------------

    [Fact]
    public async Task JsonArray_EmptyArray_YieldsNothing()
    {
        using var client = CreateClient(new ScriptedHandler(new[] { Encoding.UTF8.GetBytes("[]") }));

        var response = await client.RequestStreamAsync(new ItemRequest(), AxiosStreamFormat.JsonArray);

        Assert.Empty(await CollectAsync(response.Data!));
    }

    [Fact]
    public async Task JsonArray_EmptyBody_YieldsNothing()
    {
        using var client = CreateClient(new ScriptedHandler(Array.Empty<byte[]>()));

        var response = await client.RequestStreamAsync(new ItemRequest(), AxiosStreamFormat.JsonArray);

        Assert.Empty(await CollectAsync(response.Data!));
    }

    [Fact]
    public async Task JsonArray_Utf8Bom_IsSkipped()
    {
        var payload = Encoding.UTF8.GetBytes("[{\"n\":5}]");
        var body = new byte[payload.Length + 3];
        body[0] = 0xEF;
        body[1] = 0xBB;
        body[2] = 0xBF;
        payload.CopyTo(body, 3);

        // BOM 被切成两半（首块只有 0xEF）：必须等够 3 个字节再判定，否则会被当成非法 JSON 起始
        using var client = CreateClient(new ScriptedHandler(new[] { body[..1], body[1..] }));
        var response = await client.RequestStreamAsync(new ItemRequest(), AxiosStreamFormat.JsonArray);

        Assert.Equal(5, Assert.Single(await CollectAsync(response.Data!)).N);
    }

    [Fact]
    public async Task JsonArray_Utf16Bom_ReportsBadResponse()
    {
        var body = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes("[{\"n\":1}]"))
            .ToArray();

        using var client = CreateClient(new ScriptedHandler(new[] { body }));

        var response = await client.RequestStreamAsync(new ItemRequest(), AxiosStreamFormat.JsonArray);

        var error = await Assert.ThrowsAsync<AxiosException>(() => CollectAsync(response.Data!));
        Assert.Equal(AxiosException.ErrBadResponse, error.Code);
        Assert.Contains("UTF-8", error.Message);
    }

    [Fact]
    public async Task JsonArray_NonUtf8Charset_FailsAtStreamAcquisition()
    {
        // iso-8859-1 是 ASCII 兼容的，按行分帧的 NDJSON 能接受；但 JSON 数组分帧走 Utf8JsonReader，
        // 只认 UTF-8 字节 —— 必须在取流时就报错，而不是让解析器逐元素炸 JsonException。
        using var client = CreateClient(new ScriptedHandler(
            new[] { Encoding.UTF8.GetBytes("[{\"n\":1}]") },
            contentType: "application/json; charset=iso-8859-1"));

        var error = await Assert.ThrowsAsync<AxiosException>(
            () => client.RequestStreamAsync(new ItemRequest(), AxiosStreamFormat.JsonArray));

        Assert.Equal(AxiosException.ErrBadResponse, error.Code);
        Assert.Contains("charset", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // 协议错误
    // ------------------------------------------------------------------

    [Fact]
    public async Task JsonArray_RootIsNotArray_ReportsBadResponse()
    {
        // 最常见的踩坑形态：分页响应外面还包了一层 { "items": [...] }
        var body = "{\"items\":[{\"n\":1}]}";

        using var client = CreateClient(new ScriptedHandler(new[] { Encoding.UTF8.GetBytes(body) }));
        var response = await client.RequestStreamAsync(new ItemRequest(), AxiosStreamFormat.JsonArray);

        var error = await Assert.ThrowsAsync<AxiosException>(() => CollectAsync(response.Data!));
        Assert.Equal(AxiosException.ErrBadResponse, error.Code);
        Assert.Contains("top-level '['", error.Message);
    }

    [Fact]
    public async Task JsonArray_InvalidJson_ReportsBadResponse()
    {
        var body = "[{\"n\":1},{oops}]";

        using var client = CreateClient(new ScriptedHandler(new[] { Encoding.UTF8.GetBytes(body) }));
        var response = await client.RequestStreamAsync(new ItemRequest(), AxiosStreamFormat.JsonArray);

        var error = await Assert.ThrowsAsync<AxiosException>(() => CollectAsync(response.Data!));
        Assert.Equal(AxiosException.ErrBadResponse, error.Code);
        Assert.NotNull(error.Cause);
    }

    [Fact]
    public async Task JsonArray_TruncatedArray_ReportsBadResponse()
    {
        // 服务端把数组写了一半就断流：已交付的元素保留，但枚举最终要报错
        var body = "[{\"n\":1},{\"n\":2}";

        using var client = CreateClient(new ScriptedHandler(new[] { Encoding.UTF8.GetBytes(body) }));
        var response = await client.RequestStreamAsync(new ItemRequest(), AxiosStreamFormat.JsonArray);

        var delivered = new List<int>();
        var error = await Assert.ThrowsAsync<AxiosException>(async () =>
        {
            await foreach (var item in response.Data!)
            {
                delivered.Add(item.N);
            }
        });

        Assert.Equal(AxiosException.ErrBadResponse, error.Code);
        Assert.Equal(new[] { 1, 2 }, delivered);

        // 「被截断」与「不是合法 JSON」要分开说：前者查服务端 / 网络，后者查数据格式
        Assert.Contains("closing ']'", error.Message);
    }

    [Fact]
    public async Task JsonArray_TruncatedAfterTrailingWhitespace_ReportsBadResponse()
    {
        // 断在元素边界之后的空白上（不是正好切在 token 末尾）：同样要点明缺 ']'
        var body = "[{\"n\":1}  \r\n";

        using var client = CreateClient(new ScriptedHandler(new[] { Encoding.UTF8.GetBytes(body) }));
        var response = await client.RequestStreamAsync(new ItemRequest(), AxiosStreamFormat.JsonArray);

        var delivered = new List<int>();
        var error = await Assert.ThrowsAsync<AxiosException>(async () =>
        {
            await foreach (var item in response.Data!)
            {
                delivered.Add(item.N);
            }
        });

        Assert.Equal(AxiosException.ErrBadResponse, error.Code);
        Assert.Equal(new[] { 1 }, delivered);
        Assert.Contains("closing ']'", error.Message);
    }

    [Fact]
    public async Task JsonArray_TruncatedInsideLastElement_ReportsBadResponse()
    {
        // 最后一段连 token 都不完整（元素边界都没到）：仍归 ERR_BAD_RESPONSE，且已交付的元素不丢
        var body = "[{\"n\":1},{\"n\":2";

        using var client = CreateClient(new ScriptedHandler(new[] { Encoding.UTF8.GetBytes(body) }));
        var response = await client.RequestStreamAsync(new ItemRequest(), AxiosStreamFormat.JsonArray);

        var delivered = new List<int>();
        var error = await Assert.ThrowsAsync<AxiosException>(async () =>
        {
            await foreach (var item in response.Data!)
            {
                delivered.Add(item.N);
            }
        });

        Assert.Equal(AxiosException.ErrBadResponse, error.Code);
        Assert.Equal(new[] { 1 }, delivered);
        Assert.NotNull(error.Cause);
    }

    [Fact]
    public async Task JsonArray_MidStreamDisconnect_ReportsNetworkError()
    {
        var body = Encoding.UTF8.GetBytes("[{\"n\":1},");

        using var client = CreateClient(new ScriptedHandler(new[] { body }, failAtIndex: 1));
        var response = await client.RequestStreamAsync(new ItemRequest(), AxiosStreamFormat.JsonArray);

        var error = await Assert.ThrowsAsync<AxiosException>(() => CollectAsync(response.Data!));
        Assert.Equal(AxiosException.ErrNetwork, error.Code);
    }

    [Fact]
    public async Task JsonArray_ElementOverFrameLimit_ReportsBadResponse()
    {
        // 两条上限都要覆盖：
        //  ① 元素由很多小 token 组成 → 溢写缓冲累计超限（AppendSpill）
        //  ② 元素是单个超大 token（一个 9MB 的字符串）→ 读缓冲撑到上限（FillAsync）
        foreach (var body in new[] { ManyTokenElement(), SingleTokenElement() })
        {
            using var client = CreateClient(new ScriptedHandler(Slice(body, 64 * 1024)));
            var response = await client.RequestStreamAsync(new AnyRequest(), AxiosStreamFormat.JsonArray);

            var error = await Assert.ThrowsAsync<AxiosException>(() => CollectAsync(response.Data!));
            Assert.Equal(AxiosException.ErrBadResponse, error.Code);

            // 必须是「元素超限」而不是「缺少分帧符」：数组里的分隔符是 ','，措辞要指向正确方向
            Assert.Contains("element exceeded", error.Message);
            Assert.Contains("byte limit", error.Message);
        }
    }

    /// <summary>单个元素由 900 万个 <c>1,</c> 组成（约 9MB）：走溢写缓冲的累计上限。</summary>
    private static byte[] ManyTokenElement()
    {
        var payload = new StringBuilder(9 * 1024 * 1024);
        payload.Append("[{\"a\":[");
        while (payload.Length < 9 * 1024 * 1024)
        {
            payload.Append("1,");
        }

        payload.Append("1]}]");
        return Encoding.UTF8.GetBytes(payload.ToString());
    }

    /// <summary>单个元素就是一个 9MB 的字符串 token：走读缓冲的上限（token 不闭合，溢写帮不上忙）。</summary>
    private static byte[] SingleTokenElement()
        => Encoding.UTF8.GetBytes("[\"" + new string('中', 3_000_000) + "\"]");

    // ------------------------------------------------------------------
    // 与既有契约的一致性：空闲超时、单次枚举、方言跟随序列化选项
    // ------------------------------------------------------------------

    [Fact]
    public async Task JsonArray_StreamIdleTimeout_FiresWhenServerStalls()
    {
        using var client = CreateClient(
            new ScriptedHandler(new[] { Encoding.UTF8.GetBytes("[{\"n\":1},") }, stallAfterChunks: true),
            idle: TimeSpan.FromMilliseconds(150));

        var response = await client.RequestStreamAsync(new ItemRequest(), AxiosStreamFormat.JsonArray);

        var error = await Assert.ThrowsAsync<AxiosException>(() => CollectAsync(response.Data!));
        Assert.Equal(AxiosException.ErrConnAborted, error.Code);
        Assert.Contains("idle", error.Message);
    }

    [Fact]
    public async Task JsonArray_CanOnlyBeEnumeratedOnce()
    {
        using var client = CreateClient(new ScriptedHandler(new[] { Encoding.UTF8.GetBytes("[{\"n\":1}]") }));

        var response = await client.RequestStreamAsync(new ItemRequest(), AxiosStreamFormat.JsonArray);
        await CollectAsync(response.Data!);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CollectAsync(response.Data!));
        Assert.Contains("只能枚举一次", error.Message);
    }

    [Fact]
    public async Task JsonArray_TrailingCommaAndComments_FollowSerializerOptions()
    {
        // 外层数组的方言必须与元素最终被解析时的选项一致：调用方打开 AllowTrailingCommas / 跳过注释后，
        // 分帧不能先按默认方言拒绝（否则「改 options 也无效」）
        var body = "[{\"n\":1},/*c*/{\"n\":2},]";

        using var client = CreateClient(new ScriptedHandler(new[] { Encoding.UTF8.GetBytes(body) }));

        var strict = await client.RequestStreamAsync(new ItemRequest(), AxiosStreamFormat.JsonArray);
        var strictError = await Assert.ThrowsAsync<AxiosException>(() => CollectAsync(strict.Data!));
        Assert.Equal(AxiosException.ErrBadResponse, strictError.Code);

        var lenient = await client.RequestStreamAsync(
            new ItemRequest(),
            AxiosStreamFormat.JsonArray,
            c => c.Serialization.JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            });

        Assert.Equal(new[] { 1, 2 }, (await CollectAsync(lenient.Data!)).Select(x => x.N));
    }

    [Fact]
    public async Task JsonArray_MaxDepth_DoesNotCountTheOuterArrayAgainstTheElement()
    {
        // 元素两层嵌套：按 MaxDepth=2 序列化器能接受，但 reader 眼里它深 3（外层数组占一层）。
        // 分帧的上限必须跟着 +1，否则「序列化器能读、分帧先拒」—— 调用方会以为 options 不生效。
        var body = "[{\"a\":{\"b\":1}}]";

        using var client = CreateClient(new ScriptedHandler(new[] { Encoding.UTF8.GetBytes(body) }));

        var response = await client.RequestStreamAsync(
            new AnyRequest(),
            AxiosStreamFormat.JsonArray,
            c => c.Serialization.JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                MaxDepth = 2
            });

        var item = Assert.IsType<JsonElement>(Assert.Single(await CollectAsync(response.Data!)));
        Assert.Equal(1, item.GetProperty("a").GetProperty("b").GetInt32());
    }

    // ------------------------------------------------------------------
    // 辅助
    // ------------------------------------------------------------------

    private static ApiClient CreateClient(HttpMessageHandler handler, TimeSpan? idle = null)
    {
        var config = new AxiosConfig { BaseAddress = "http://stream.test/" };
        var client = new ApiClient(new AxiosClient(config, handler: handler, disposeHandler: true), ownsAxiosClient: true);
        if (idle is { } idleValue)
        {
            client.StreamIdleTimeout = idleValue;
        }

        return client;
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        await foreach (var item in source.ConfigureAwait(false))
        {
            items.Add(item);
        }

        return items;
    }

    /// <summary>按给定边界切块（最后一个边界即结尾）；用于制造跨块 token 与跨块多字节字符。</summary>
    private static byte[][] Slice(byte[] body, params int[] cuts)
    {
        var chunks = new List<byte[]>();
        var from = 0;
        foreach (var cut in cuts)
        {
            if (cut <= from || cut > body.Length)
            {
                continue;
            }

            chunks.Add(body[from..cut]);
            from = cut;
        }

        if (from < body.Length)
        {
            chunks.Add(body[from..]);
        }

        return chunks.ToArray();
    }

    private static byte[][] Slice(byte[] body, int chunkSize)
    {
        var chunks = new List<byte[]>();
        for (var offset = 0; offset < body.Length; offset += chunkSize)
        {
            chunks.Add(body[offset..Math.Min(offset + chunkSize, body.Length)]);
        }

        return chunks.ToArray();
    }

    /// <summary>按随机长度切块：让边界落在 token / 多字节字符的任意位置。</summary>
    private static byte[][] SliceRandom(byte[] body, Random random)
    {
        var chunks = new List<byte[]>();
        var offset = 0;
        while (offset < body.Length)
        {
            var end = Math.Min(offset + random.Next(1, 98), body.Length);
            chunks.Add(body[offset..end]);
            offset = end;
        }

        return chunks.ToArray();
    }

    // ------------------------------------------------------------------
    // 测试模型
    // ------------------------------------------------------------------

    [Get("stream/items")]
    private sealed class ItemRequest : IApiRequest<StreamItem>
    {
    }

    [Get("stream/any")]
    private sealed class AnyRequest : IApiRequest<object>
    {
    }

    [Get("stream/int")]
    private sealed class IntRequest : IApiRequest<int>
    {
    }

    public sealed record StreamItem(int N, string? Text);

    // ------------------------------------------------------------------
    // 测试替身
    // ------------------------------------------------------------------

    /// <summary>预置分块的响应流；可在中途抛 IO 异常或读完后挂起。</summary>
    private sealed class ScriptedStream : Stream
    {
        private readonly byte[][] _chunks;
        private readonly int _failAtIndex;
        private readonly bool _stallAfterChunks;
        private int _index;
        private int _offset;

        public ScriptedStream(byte[][] chunks, int failAtIndex, bool stallAfterChunks)
        {
            _chunks = chunks;
            _failAtIndex = failAtIndex;
            _stallAfterChunks = stallAfterChunks;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_index == _failAtIndex)
            {
                throw new IOException("simulated mid-stream disconnect");
            }

            if (_index >= _chunks.Length)
            {
                if (_stallAfterChunks)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                }

                return 0;
            }

            var chunk = _chunks[_index];
            var count = Math.Min(buffer.Length, chunk.Length - _offset);
            chunk.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            if (_offset >= chunk.Length)
            {
                _index++;
                _offset = 0;
            }

            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(Span<byte> buffer)
        {
            var temp = new byte[buffer.Length];
            var read = ReadAsync(temp.AsMemory(), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            temp.AsSpan(0, read).CopyTo(buffer);
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly byte[][] _chunks;
        private readonly int _failAtIndex;
        private readonly bool _stallAfterChunks;
        private readonly string _contentType;

        public ScriptedHandler(
            byte[][] chunks,
            int failAtIndex = int.MaxValue,
            bool stallAfterChunks = false,
            string contentType = "application/json")
        {
            _chunks = chunks;
            _failAtIndex = failAtIndex;
            _stallAfterChunks = stallAfterChunks;
            _contentType = contentType;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StreamContent(new ScriptedStream(_chunks, _failAtIndex, _stallAfterChunks));
            content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(_contentType);

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request
            };

            return Task.FromResult(response);
        }
    }
}
