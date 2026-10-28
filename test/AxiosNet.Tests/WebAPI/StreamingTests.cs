
using AxiosNet.Pooling;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Internal;
using static AxiosNet.Tests.WebAPI.StreamingTestKit;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Net;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text;

namespace AxiosNet.Tests.WebAPI;

/// <summary>
/// 声明式流式响应：NDJSON / SSE / JSON 数组分帧、帧上限、正文预算、单次枚举，
/// 以及分帧缓冲的存留口径（超大元素 / 超大帧收尾后不得按历史峰值长驻）。
/// </summary>
public sealed class StreamingTests
{
    // ------------------------------------------------------------------
    // Bug 2：SSE 裸 CR 行尾
    // ------------------------------------------------------------------

    [Fact]
    public async Task Sse_BareCarriageReturnLineEndings_AreFramed()
    {
        var body = Encoding.UTF8.GetBytes("data: {\"n\":1}\r\rdata: {\"n\":2}\r\r");
        using var client = CreateClient(
            new ScriptedStreamHandler(new[] { body }, contentType: "text/event-stream"));

        var response = await client.RequestStreamAsync(new StreamRequest(), AxiosStreamFormat.ServerSentEvents);
        var items = await CollectAsync(response.Data!);

        Assert.Equal(new[] { 1, 2 }, items.Select(i => i.N));
    }

    [Fact]
    public async Task Sse_CrLfSplitAcrossReads_KeepsMultiLineFrameTogether()
    {
        // CR 与 LF 落到两次 ReadAsync：若把边界上的 CR 直接当行尾，
        // LF 会变成空行，多行 data 会被提前交付成非法 JSON。
        var bytes = Encoding.UTF8.GetBytes("data: {\"n\":1,\r\ndata: \"t\":\"ok\"}\r\n\r\n");
        var split = Array.IndexOf(bytes, (byte)'\r') + 1;

        using var client = CreateClient(
            new ScriptedStreamHandler(
                new[] { bytes[..split], bytes[split..] },
                contentType: "text/event-stream"));

        var response = await client.RequestStreamAsync(new StreamRequest(), AxiosStreamFormat.ServerSentEvents);
        var items = await CollectAsync(response.Data!);

        var item = Assert.Single(items);
        Assert.Equal(1, item.N);
        Assert.Equal("ok", item.T);
    }

    // ------------------------------------------------------------------
    // Bug 3：流式分帧未校验强制 ResponseEncoding
    // ------------------------------------------------------------------

    [Fact]
    public async Task Sse_ForcedNonAsciiCompatibleEncoding_IsRejected()
    {
        // 响应未声明 charset，但调用方强制 UTF-16：分帧按字节找 CR / LF 必然切错，
        // 应在取流时直接报 ERR_BAD_RESPONSE，而不是静默产出乱码帧。
        var body = Encoding.Unicode.GetBytes("data: {\"n\":1}\r\n\r\n");
        using var client = CreateClient(new ScriptedStreamHandler(new[] { body }, contentType: "text/event-stream"));

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.RequestStreamAsync(
                new StreamRequest(),
                AxiosStreamFormat.ServerSentEvents,
                config => config.Serialization.ResponseEncoding = Encoding.Unicode));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
    }

    // ------------------------------------------------------------------
    // SSE 帧上限：字节口径
    // ------------------------------------------------------------------

    [Fact]
    public async Task RequestStreamAsync_SseFrameOverByteLimit_ThrowsFrameTooLarge()
    {
        // 3,000,000 个汉字 = 9,000,000 UTF-8 字节（> 8MB 上限），StringBuilder 侧只有 6,000,000 字符。
        // 上限按字节判：这里必然触发。
        var payload = new string('中', 3_000_000);
        var body = Encoding.UTF8.GetBytes("data: " + payload + "\n\n");
        await using var server = TestHttpServer.Start(
            _ => new TestResponse(Body: body, ContentType: "text/event-stream"));
        using var client = new ApiClient(server.Url);

        var response = await client.RequestStreamAsync(new SseBigFrameRequest(), AxiosStreamFormat.ServerSentEvents);

        var ex = await Assert.ThrowsAsync<AxiosException>(async () =>
        {
            await foreach (var _ in response.Data!)
            {
            }
        });
        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
    }

    // ------------------------------------------------------------------
    // 流式：NDJSON
    // ------------------------------------------------------------------

    [Fact]
    public async Task Ndjson_ReadsAllFrames_IncludingSplitMultiByteCharacters()
    {
        var first = Encoding.UTF8.GetBytes("{\"n\":1,\"text\":\"你好\"}\n");
        // 在「好」的三个字节中间切开：分帧必须按字节做，不能先解码
        var second = Encoding.UTF8.GetBytes("{\"n\":2,\"text\":\"再见\"}\n");
        var split = first.Length - 5;

        using var client = CreateClient(
            new ScriptedStreamHandler(new[]
            {
                first[..split],
                [.. first[split..], .. second[..3]],
                second[3..]
            }));

        var response = await client.RequestStreamAsync(new StreamFrameRequest());

        var items = await CollectAsync(response.Data!);
        Assert.Equal(200, response.Status);
        Assert.Equal(2, items.Count);
        Assert.Equal(1, items[0].N);
        Assert.Equal("你好", items[0].Text);
        Assert.Equal(2, items[1].N);
        Assert.Equal("再见", items[1].Text);
    }

    [Fact]
    public async Task Ndjson_EmptyStream_YieldsNothing()
    {
        using var client = CreateClient(new ScriptedStreamHandler(Array.Empty<byte[]>()));

        var response = await client.RequestStreamAsync(new StreamFrameRequest());

        Assert.Empty(await CollectAsync(response.Data!));
    }

    [Fact]
    public async Task Ndjson_InvalidFrame_ReportsBadResponse()
    {
        using var client = CreateClient(
            new ScriptedStreamHandler(new[] { Encoding.UTF8.GetBytes("{oops}\n") }));

        var response = await client.RequestStreamAsync(new StreamFrameRequest());

        var error = await Assert.ThrowsAsync<AxiosException>(() => CollectAsync(response.Data!));
        Assert.Equal(AxiosException.ErrBadResponse, error.Code);
    }

    [Fact]
    public async Task Ndjson_MidStreamDisconnect_ReportsNetworkError()
    {
        var chunk = Encoding.UTF8.GetBytes("{\"n\":1}\n");
        using var client = CreateClient(new ScriptedStreamHandler(new[] { chunk }, failAtIndex: 1));

        var response = await client.RequestStreamAsync(new StreamFrameRequest());

        var error = await Assert.ThrowsAsync<AxiosException>(() => CollectAsync(response.Data!));
        Assert.Equal(AxiosException.ErrNetwork, error.Code);
    }

    [Fact]
    public async Task Stream_CanOnlyBeEnumeratedOnce()
    {
        using var client = CreateClient(
            new ScriptedStreamHandler(new[] { Encoding.UTF8.GetBytes("{\"n\":1}\n") }));

        var response = await client.RequestStreamAsync(new StreamFrameRequest());

        await CollectAsync(response.Data!);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CollectAsync(response.Data!));
        Assert.Contains("只能枚举一次", error.Message);
    }

    // ------------------------------------------------------------------
    // 流式：SSE
    // ------------------------------------------------------------------

    [Fact]
    public async Task Sse_ReadsDataFrames_SkipsComments_AndStopsAtDoneSentinel()
    {
        var body = string.Concat(
            ": keep-alive\n\n",
            "event: tick\ndata: {\"n\":1,\"text\":\"a\"}\n\n",
            "data: {\"n\":2,\n",
            "data: \"text\":\"b\"}\n\n",
            "data: [DONE]\n\n",
            "data: {\"n\":3}\n\n");

        using var client = CreateClient(
            new ScriptedStreamHandler(new[] { Encoding.UTF8.GetBytes(body) }));

        var response = await client.RequestStreamAsync(new StreamFrameRequest(), AxiosStreamFormat.ServerSentEvents);

        var items = await CollectAsync(response.Data!);
        Assert.Equal(2, items.Count);
        Assert.Equal(1, items[0].N);
        Assert.Equal("a", items[0].Text);
        Assert.Equal(2, items[1].N);
        Assert.Equal("b", items[1].Text);
    }

    [Fact]
    public async Task Sse_FrameWithoutTrailingBlankLine_IsStillDelivered()
    {
        using var client = CreateClient(
            new ScriptedStreamHandler(new[] { Encoding.UTF8.GetBytes("data: {\"n\":7}\n") }));

        var response = await client.RequestStreamAsync(new StreamFrameRequest(), AxiosStreamFormat.ServerSentEvents);

        var items = await CollectAsync(response.Data!);
        Assert.Equal(7, Assert.Single(items).N);
    }

    // ------------------------------------------------------------------
    // 流式：正文与响应头共用同一份预算（不设超时时正文不限时）
    // ------------------------------------------------------------------

    [Fact]
    public async Task Stream_NoTimeoutConfigured_BodyIsNotBounded()
    {
        var chunks = new[]
        {
            Encoding.UTF8.GetBytes("{\"n\":1}\n"),
            Encoding.UTF8.GetBytes("{\"n\":2}\n")
        };

        // 正文首块延迟 400ms，但没设 Timeout：正文阶段不该有任何计时器。
        using var client = CreateClient(new ScriptedStreamHandler(chunks, initialDelay: TimeSpan.FromMilliseconds(400)));

        var response = await client.RequestStreamAsync(new StreamFrameRequest());

        var items = await CollectAsync(response.Data!);
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task Stream_BodySharesTheAttemptBudget()
    {
        // 预算 150ms，服务端吐完一帧就挂起：正文阶段到点必须被打断，错误码与超时一致。
        using var client = CreateClient(
            new ScriptedStreamHandler(new[] { Encoding.UTF8.GetBytes("{\"n\":1}\n") }, stallAfterChunks: true),
            timeout: TimeSpan.FromMilliseconds(150));

        var response = await client.RequestStreamAsync(new StreamFrameRequest());

        var error = await Assert.ThrowsAsync<AxiosException>(() => CollectAsync(response.Data!));
        Assert.Equal(AxiosException.ErrConnAborted, error.Code);
        Assert.Contains("timeout of", error.Message);
    }

    [Fact]
    public async Task Stream_BodyIsBoundedByDeadline_NotJustTimeout()
    {
        // 只设 Deadline、不设 Timeout：正文阶段同样在截止点被打断，
        // 而且消息要指明是「预算压的」，不能写成「timeout of 0ms exceeded」（统一口径后的关键点）。
        using var client = CreateClient(
            new ScriptedStreamHandler(new[] { Encoding.UTF8.GetBytes("{\"n\":1}\n") }, stallAfterChunks: true),
            configure: config => config.Deadline = TimeSpan.FromMilliseconds(200));

        var response = await client.RequestStreamAsync(new StreamFrameRequest());

        var error = await Assert.ThrowsAsync<AxiosException>(() => CollectAsync(response.Data!));
        Assert.Equal(AxiosException.ErrConnAborted, error.Code);
        Assert.Contains("remaining Deadline budget", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 流式：状态码错误与错误工厂
    // ------------------------------------------------------------------

    [Fact]
    public async Task Stream_HttpError_ThrowsBeforeEnumeration_AndHonorsErrorFactory()
    {
        using var client = CreateClient(
            new ScriptedStreamHandler(Array.Empty<byte[]>(), statusCode: HttpStatusCode.BadRequest,
                body: Encoding.UTF8.GetBytes("{\"code\":\"E9\"}")));
        client.ErrorFactory = new ApiErrorFactory<StreamErrorBody>();

        var error = await Assert.ThrowsAsync<ApiError<StreamErrorBody>>(
            () => client.RequestStreamAsync(new StreamFrameRequest()));

        Assert.Equal(400, error.Status);
        Assert.Equal("E9", error.Error?.Code);
    }

    // ------------------------------------------------------------------
    // 基本形态
    // ------------------------------------------------------------------

    [Fact]
    public async Task JsonArray_ReadsAllElements_WhenChunksSplitTokensAndMultiByteCharacters()
    {
        var body = Encoding.UTF8.GetBytes("[{\"n\":1,\"text\":\"你好\"},{\"n\":2,\"text\":\"再见\"}]");

        // 在每个「不该切」的位置切块：逗号后、多字节字符中间、token 中间
        var cuts = new[] { 1, 6, 15, 17, 30, body.Length };
        using var client = CreateClient(JsonArrayHandler(Slice_ApiClientStreamingJsonArray(body, cuts)));

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

        using var client = CreateClient(JsonArrayHandler(Slice_ApiClientStreamingJsonArray(body, 3_000)));
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

        using var client = CreateClient(JsonArrayHandler(Slice_ApiClientStreamingJsonArray(body, 8 * 1024)));
        var response = await client.RequestStreamAsync(new ItemRequest(), AxiosStreamFormat.JsonArray);

        var items = await CollectAsync(response.Data!);
        Assert.Equal(2, items.Count);
        Assert.Equal(text, items[0].Text);
        Assert.Equal(2, items[1].N);
    }

    [Fact]
    public async Task JsonArray_ReleasesOversizedSpillAfterElement()
    {
        // 一个 ~1 MB 的对象元素（跨几十次 8 KB 读，走 _elementSpill），后面跟两个小元素。
        var big = BuildBigObject(1024 * 1024);
        var body = Encoding.UTF8.GetBytes("[" + big + ",{\"id\":1},{\"id\":2}]");

        using var client = CreateClient(JsonArrayHandler(Slice_ApiClientStreamingJsonArray(body, 8 * 1024)));
        var response = await client.RequestStreamAsync(new AnyRequest(), AxiosStreamFormat.JsonArray);

        // Data 的真身就是分帧器本身，溢写缓冲的存留只能从这里看（枚举器由它自己创建）。
        var frames = response.Data!;
        var spill = typeof(JsonStreamEnumerable<object>).GetField(
            "_elementSpill",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        await using var enumerator = frames.GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        var first = Assert.IsType<JsonElement>(enumerator.Current);
        Assert.True(first.GetRawText().Length > 512 * 1024, "首个元素应当足够大，才能压到溢写缓冲。");

        // 该元素收尾后，峰值缓冲不该继续挂在分帧器上（后面还有整条流要读）。
        var retained = (byte[]?)spill.GetValue(frames);
        var withinBound = retained is null || retained.Length <= 64 * 1024;
        Assert.True(
            withinBound,
            $"超大元素读完后仍持有 {retained?.Length ?? 0:N0} B 的溢写缓冲（元素的 1 MB 已被交付）——整条流会一直拖着这块内存。");

        // 其余元素照常可读（释放缓冲不能影响后续分帧）。
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(1, Assert.IsType<JsonElement>(enumerator.Current).GetProperty("id").GetInt32());
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(2, Assert.IsType<JsonElement>(enumerator.Current).GetProperty("id").GetInt32());
        Assert.False(await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task Sse_FrameBufferCapacityDoesNotFollowPeak()
    {
        // 一帧 512 KB 的 SSE data（内容是 JSON 字符串），后面跟一帧小的：帧缓冲的容量不该跟着峰值长驻。
        var big = new string('a', 512 * 1024);
        var body = Encoding.UTF8.GetBytes("data: \"" + big + "\"\n\n" + "data: \"small\"\n\n");

        using var client = CreateClient(new ScriptedStreamHandler(
            Slice_ApiClientStreamingJsonArray(body, 8 * 1024),
            contentType: "text/event-stream"));
        var response = await client.RequestStreamAsync(new AnyRequest_StreamFrameLimit(), AxiosStreamFormat.ServerSentEvents);

        var frames = response.Data!;
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

    [Fact]
    public async Task JsonArray_OneBytePerRead_StillDeliversEveryElement()
    {
        // 极端分块（每次只给 1 字节）+ 4 字节 emoji + 标量 null/true：边界判定不能靠「块够大」
        var body = Encoding.UTF8.GetBytes("[{\"n\":1,\"text\":\"你好😀\"},null,true,{\"n\":2}]");
        var chunks = body.Select(b => new[] { b }).ToArray();

        using var client = CreateClient(JsonArrayHandler(chunks));
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
            using var client = CreateClient(JsonArrayHandler(SliceRandom(body, random)));
            var response = await client.RequestStreamAsync(new ItemRequest(), AxiosStreamFormat.JsonArray);

            Assert.Equal(expected, (await CollectAsync(response.Data!)).Select(x => x.N));
        }
    }

    [Fact]
    public async Task JsonArray_NestedContainers_WithBracketsAndCommasInsideStrings_AreNotSplit()
    {
        // 字符串里的 ] , { 都不能被当成结构字符：分帧交给 Utf8JsonReader，不是找分隔符
        var body = "[{\"tags\":[\"a]b\",\"c,d\"],\"inner\":{\"x\":[1,2]}},[1,{\"y\":2}]]";

        using var client = CreateClient(JsonArrayHandler(new[] { Encoding.UTF8.GetBytes(body) }));
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
        using var client = CreateClient(JsonArrayHandler(new[] { Encoding.UTF8.GetBytes("[1, 2 ,3]") }));

        var response = await client.RequestStreamAsync(new IntRequest(), AxiosStreamFormat.JsonArray);

        Assert.Equal(new[] { 1, 2, 3 }, await CollectAsync(response.Data!));
    }

    [Fact]
    public async Task JsonArray_WhitespaceBetweenElements_IsIgnored()
    {
        var body = "[\r\n  {\"n\":1}\r\n , \t{\"n\":2}\n]";

        using var client = CreateClient(JsonArrayHandler(new[] { Encoding.UTF8.GetBytes(body) }));
        var response = await client.RequestStreamAsync(new ItemRequest(), AxiosStreamFormat.JsonArray);

        Assert.Equal(new[] { 1, 2 }, (await CollectAsync(response.Data!)).Select(x => x.N));
    }

    [Fact]
    public async Task JsonArray_TrailingBytesAfterClosingBracket_AreNotRead()
    {
        // 收尾 ']' 之后立刻结束：不校验尾部、也不必等连接关闭（这里尾部就是垃圾）
        var body = "[{\"n\":1}]trailing-garbage";

        using var client = CreateClient(JsonArrayHandler(new[] { Encoding.UTF8.GetBytes(body) }));
        var response = await client.RequestStreamAsync(new ItemRequest(), AxiosStreamFormat.JsonArray);

        Assert.Equal(1, Assert.Single(await CollectAsync(response.Data!)).N);
    }

    // ------------------------------------------------------------------
    // 空流与编码
    // ------------------------------------------------------------------

    [Fact]
    public async Task JsonArray_EmptyArray_YieldsNothing()
    {
        using var client = CreateClient(JsonArrayHandler(new[] { Encoding.UTF8.GetBytes("[]") }));

        var response = await client.RequestStreamAsync(new ItemRequest(), AxiosStreamFormat.JsonArray);

        Assert.Empty(await CollectAsync(response.Data!));
    }

    [Fact]
    public async Task JsonArray_EmptyBody_YieldsNothing()
    {
        using var client = CreateClient(JsonArrayHandler(Array.Empty<byte[]>()));

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
        using var client = CreateClient(JsonArrayHandler(new[] { body[..1], body[1..] }));
        var response = await client.RequestStreamAsync(new ItemRequest(), AxiosStreamFormat.JsonArray);

        Assert.Equal(5, Assert.Single(await CollectAsync(response.Data!)).N);
    }

    [Fact]
    public async Task JsonArray_Utf16Bom_ReportsBadResponse()
    {
        var body = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes("[{\"n\":1}]"))
            .ToArray();

        using var client = CreateClient(JsonArrayHandler(new[] { body }));

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
        using var client = CreateClient(JsonArrayHandler(
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

        using var client = CreateClient(JsonArrayHandler(new[] { Encoding.UTF8.GetBytes(body) }));
        var response = await client.RequestStreamAsync(new ItemRequest(), AxiosStreamFormat.JsonArray);

        var error = await Assert.ThrowsAsync<AxiosException>(() => CollectAsync(response.Data!));
        Assert.Equal(AxiosException.ErrBadResponse, error.Code);
        Assert.Contains("top-level '['", error.Message);
    }

    [Fact]
    public async Task JsonArray_InvalidJson_ReportsBadResponse()
    {
        var body = "[{\"n\":1},{oops}]";

        using var client = CreateClient(JsonArrayHandler(new[] { Encoding.UTF8.GetBytes(body) }));
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

        using var client = CreateClient(JsonArrayHandler(new[] { Encoding.UTF8.GetBytes(body) }));
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

        using var client = CreateClient(JsonArrayHandler(new[] { Encoding.UTF8.GetBytes(body) }));
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

        using var client = CreateClient(JsonArrayHandler(new[] { Encoding.UTF8.GetBytes(body) }));
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

        using var client = CreateClient(JsonArrayHandler(new[] { body }, failAtIndex: 1));
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
            using var client = CreateClient(JsonArrayHandler(Slice_ApiClientStreamingJsonArray(body, 64 * 1024)));
            var response = await client.RequestStreamAsync(new AnyRequest(), AxiosStreamFormat.JsonArray);

            var error = await Assert.ThrowsAsync<AxiosException>(() => CollectAsync(response.Data!));
            Assert.Equal(AxiosException.ErrBadResponse, error.Code);

            // 必须是「元素超限」而不是「缺少分帧符」：数组里的分隔符是 ','，措辞要指向正确方向
            Assert.Contains("element exceeded", error.Message);
            Assert.Contains("byte limit", error.Message);
        }
    }

    // ------------------------------------------------------------------
    // 与既有契约的一致性：正文预算、单次枚举、方言跟随序列化选项
    // ------------------------------------------------------------------

    [Fact]
    public async Task JsonArray_BodySharesTheAttemptBudget()
    {
        using var client = CreateClient(
            JsonArrayHandler(new[] { Encoding.UTF8.GetBytes("[{\"n\":1},") }, stallAfterChunks: true),
            timeout: TimeSpan.FromMilliseconds(150));

        var response = await client.RequestStreamAsync(new ItemRequest(), AxiosStreamFormat.JsonArray);

        var error = await Assert.ThrowsAsync<AxiosException>(() => CollectAsync(response.Data!));
        Assert.Equal(AxiosException.ErrConnAborted, error.Code);
        Assert.Contains("timeout of", error.Message);
    }

    [Fact]
    public async Task JsonArray_CanOnlyBeEnumeratedOnce()
    {
        using var client = CreateClient(JsonArrayHandler(new[] { Encoding.UTF8.GetBytes("[{\"n\":1}]") }));

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

        using var client = CreateClient(JsonArrayHandler(new[] { Encoding.UTF8.GetBytes(body) }));

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

        using var client = CreateClient(JsonArrayHandler(new[] { Encoding.UTF8.GetBytes(body) }));

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
    // B7：流式请求的生效配置回放时不能泄漏 ResponseType.Stream
    // ------------------------------------------------------------------

    [Fact]
    public async Task B7_StreamResponseConfigReplay_DoesNotLeakStreamResponseType()
    {
        using var client = CreateClient(
            new ScriptedStreamHandler(new[] { Encoding.UTF8.GetBytes("{\"n\":1}\n") }));

        var response = await client.RequestStreamAsync(new StreamFrameRequest());
        Assert.Equal(ResponseType.Stream, response.Config!.ResponseType);
        Assert.Single(await CollectAsync(response.Data!));

        var replayed = await client.Axios.RequestAsync<StreamFrame>(response.Config!);

        Assert.Equal(ResponseType.Json, replayed.Config!.ResponseType);
        var item = Assert.IsType<StreamFrame>(replayed.Data);
        Assert.Equal(1, item.N);
    }

    // ------------------------------------------------------------------
    // B2：枚举令牌在挂起的读取中取消，也要翻译成 ERR_CANCELED
    // ------------------------------------------------------------------

    [Fact]
    public async Task B2_CancelDuringPendingRead_ReportsCanceled()
    {
        using var client = CreateClient(new ScriptedStreamHandler(
            new[] { Encoding.UTF8.GetBytes("{\"n\":1}\n") },
            stallAfterChunks: true));

        var response = await client.RequestStreamAsync(new StreamFrameRequest());

        using var cts = new CancellationTokenSource();
        await using var enumerator = response.Data!.GetAsyncEnumerator(cts.Token);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(1, enumerator.Current.N);

        var pending = enumerator.MoveNextAsync().AsTask();
        await Task.Delay(150);
        cts.Cancel();

        var error = await Assert.ThrowsAsync<AxiosException>(() => pending);
        Assert.Equal(AxiosException.ErrCanceled, error.Code);
    }

    // ------------------------------------------------------------------
    // B5：UTF-16 / UTF-32 charset 声明会破坏按字节分帧，必须在取流时报错
    // ------------------------------------------------------------------

    [Fact]
    public async Task B5_NonAsciiCompatibleCharset_ThrowsClearError()
    {
        using var client = CreateClient(new ScriptedStreamHandler(
            new[] { Encoding.Unicode.GetBytes("{\"n\":1}\n") },
            contentType: "application/x-ndjson; charset=utf-16"));

        var error = await Assert.ThrowsAsync<AxiosException>(
            () => client.RequestStreamAsync(new StreamFrameRequest()));

        Assert.Equal(AxiosException.ErrBadResponse, error.Code);
        Assert.Contains("charset", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // B6：反序列化结果类型不符时不能静默返回 default(T)
    // ------------------------------------------------------------------

    [Fact]
    public async Task B6_WrongDeserializedType_ReportsBadResponse()
    {
        AxiosContent.Register("application/x-audit-wrong-type", new WrongTypeSerializer());

        using var client = CreateClient(new ScriptedStreamHandler(
            new[] { Encoding.UTF8.GetBytes("anything\n") },
            contentType: "application/x-audit-wrong-type"));

        var response = await client.RequestStreamAsync(new StreamFrameRequest());

        var error = await Assert.ThrowsAsync<AxiosException>(() => CollectAsync(response.Data!));

        Assert.Equal(AxiosException.ErrBadResponse, error.Code);
        Assert.Contains(nameof(StreamFrame), error.Message);
        Assert.NotNull(error.Cause);
        Assert.Contains("System.String", error.Cause!.Message);
    }

    // ------------------------------------------------------------------
    // B8：调用方在两次 MoveNext 之间停顿，不算「网络空闲」
    // ------------------------------------------------------------------

    [Fact]
    public async Task B8_ConsumerPauseBetweenFrames_IsHarmlessWithoutBudget()
    {
        using var client = CreateClient(
            new ScriptedStreamHandler(
                new[]
                {
                    Encoding.UTF8.GetBytes("{\"n\":1}\n"),
                    Encoding.UTF8.GetBytes("{\"n\":2}\n")
                },
                stallAfterChunks: true));

        var response = await client.RequestStreamAsync(new StreamFrameRequest());

        await using var enumerator = response.Data!.GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(1, enumerator.Current.N);

        // 消费者处理第一帧耗时 400ms：没设 Timeout / Deadline 时正文没有任何计时器，
        // 停顿多久都不该失败（要中途退出只能靠取消令牌）。
        await Task.Delay(400);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(2, enumerator.Current.N);
    }

    [Fact]
    public async Task Ndjson_NonUtf8DeclaredCharset_DecodesFrames()
    {
        // 响应声明 iso-8859-1：逐行解码用取流时解析的编码。
        var body = Encoding.Latin1.GetBytes("{\"n\":1,\"text\":\"café\"}\n{\"n\":2,\"text\":\"naïve\"}\n");

        using var client = CreateClient(
            new ScriptedStreamHandler(
                Array.Empty<byte[]>(),
                body: body,
                contentType: "application/x-ndjson; charset=iso-8859-1"));

        var response = await client.RequestStreamAsync(new StreamFrameRequest());

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

        using var client = CreateClient(new ScriptedStreamHandler(Array.Empty<byte[]>(), body: body, contentType: mediaType));

        var response = await client.RequestStreamAsync(new StreamFrameRequest());

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
    public async Task InterceptorReplacingStreamWithNull_ThrowsAxiosException()
    {
        using var client = CreateClient(
            new ScriptedStreamHandler(
                Array.Empty<byte[]>(),
                body: Encoding.UTF8.GetBytes("{\"n\":1}\n"),
                contentType: "application/x-ndjson"));

        // 响应拦截器把 Data 换成 null：不能漏出裸 InvalidOperationException（破坏统一错误模型），
        // 且必须报 ERR_BAD_RESPONSE，让调用方按 c.Code 分类处理。
        client.Axios.Interceptors.Response.Use(
            response =>
            {
                response.Data = null;
                return response;
            });

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.RequestStreamAsync(new StreamFrameRequest()));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
    }

    // ------------------------------------------------------------------
    // 默认值
    // ------------------------------------------------------------------

    [Fact]
    public void DefaultStreamFrameLength_IsEightMegabytes()
        => Assert.Equal(8 * Mb, AxiosConfig.LimitSettings.DefaultStreamFrameLength);

    [Fact]
    public void FrameLengthBounds_AreConsistent()
    {
        // 三个边界值是一组（默认 / 下界 / 上界），公开给调用方引用后就必须自洽：
        // 默认值落在区间内、下界小于上界。改其中一个而忘了另外两个，这里先红。
        var min = AxiosConfig.LimitSettings.MinStreamFrameLength;
        var max = AxiosConfig.LimitSettings.MaxStreamFrameLengthLimit;
        var @default = AxiosConfig.LimitSettings.DefaultStreamFrameLength;

        Assert.True(min < max, $"下界 {min} 必须小于上界 {max}");
        Assert.InRange(@default, min, max);
    }

    [Fact]
    public async Task Default_RejectsNdjsonFrameAboveEightMegabytes()
    {
        using var client = CreateFrameLimitClient(NdjsonFrame(9 * Mb), NdjsonMediaType);

        var error = await Assert.ThrowsAsync<AxiosException>(
            () => ReadAllAsync(client, AxiosStreamFormat.NewlineDelimitedJson));

        Assert.Equal(AxiosException.ErrBadResponse, error.Code);
        Assert.Contains("8388608", error.Message);
    }

    // ------------------------------------------------------------------
    // 放大：超过默认上限的帧在配置后可以读
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConfiguredLimit_AllowsNdjsonFrameAboveDefault()
    {
        using var client = CreateFrameLimitClient(NdjsonFrame(9 * Mb), NdjsonMediaType, c => c.Limits.MaxStreamFrameLength = 12 * Mb);

        var items = await ReadAllAsync(client, AxiosStreamFormat.NewlineDelimitedJson);

        Assert.Equal(9 * Mb, Assert.Single(items).Length);
    }

    [Fact]
    public async Task ConfiguredLimit_AllowsSseFrameAboveDefault()
    {
        using var client = CreateFrameLimitClient(SseFrame(9 * Mb), SseMediaType, c => c.Limits.MaxStreamFrameLength = 12 * Mb);

        var items = await ReadAllAsync(client, AxiosStreamFormat.ServerSentEvents);

        Assert.Equal(9 * Mb, Assert.Single(items).Length);
    }

    [Fact]
    public async Task ConfiguredLimit_AllowsJsonArrayElementAboveDefault()
    {
        using var client = CreateFrameLimitClient(JsonArrayElement(9 * Mb), JsonMediaType, c => c.Limits.MaxStreamFrameLength = 12 * Mb);

        var items = await ReadAllAsync(client, AxiosStreamFormat.JsonArray);

        Assert.Equal(9 * Mb, Assert.Single(items).Length);
    }

    // ------------------------------------------------------------------
    // 收紧：比默认更小的上限同样生效
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConfiguredLimit_RejectsFrameThatDefaultWouldAllow()
    {
        // 2 MB 的帧在默认 8 MB 下完全合法；把上限收到 1 MB 后必须失败 ——
        // 否则「配了但没生效」和「服务端发太大」会混在一起。
        using var client = CreateFrameLimitClient(NdjsonFrame(2 * Mb), NdjsonMediaType, c => c.Limits.MaxStreamFrameLength = 1 * Mb);

        var error = await Assert.ThrowsAsync<AxiosException>(
            () => ReadAllAsync(client, AxiosStreamFormat.NewlineDelimitedJson));

        Assert.Equal(AxiosException.ErrBadResponse, error.Code);
        Assert.Contains("1048576", error.Message);
    }

    // ------------------------------------------------------------------
    // 合并语义：按请求覆盖、显式 null 回到默认
    // ------------------------------------------------------------------

    [Fact]
    public async Task PerRequestValue_OverridesInstanceDefault()
    {
        using var client = CreateFrameLimitClient(NdjsonFrame(9 * Mb), NdjsonMediaType, c => c.Limits.MaxStreamFrameLength = 1 * Mb);

        var items = await ReadAllAsync(
            client,
            AxiosStreamFormat.NewlineDelimitedJson,
            c => c.Limits.MaxStreamFrameLength = 12 * Mb);

        Assert.Equal(9 * Mb, Assert.Single(items).Length);
    }

    [Fact]
    public async Task PerRequestValue_CanTightenInstanceDefault()
    {
        // 反方向同样要生效：只验证「放大」会漏掉「按请求收紧被实例默认盖住」这类合并错误。
        using var client = CreateFrameLimitClient(NdjsonFrame(2 * Mb), NdjsonMediaType, c => c.Limits.MaxStreamFrameLength = 12 * Mb);

        var error = await Assert.ThrowsAsync<AxiosException>(
            () => ReadAllAsync(client, AxiosStreamFormat.NewlineDelimitedJson, c => c.Limits.MaxStreamFrameLength = 1 * Mb));

        Assert.Equal(AxiosException.ErrBadResponse, error.Code);
        Assert.Contains("1048576", error.Message);
    }

    [Fact]
    public async Task OtherLimitsSet_FrameLengthStillUsesDefault()
    {
        // Limits 分组被创建（配了别的项）不等于分帧上限被「设置过」：
        // 合并层看的是 *Set 标记，这里防的是「组一存在就把未设置的项也搬过去」。
        using var client = CreateFrameLimitClient(
            NdjsonFrame(9 * Mb),
            NdjsonMediaType,
            c => c.Limits.MaxContentLength = 64 * Mb);

        var error = await Assert.ThrowsAsync<AxiosException>(
            () => ReadAllAsync(client, AxiosStreamFormat.NewlineDelimitedJson));

        Assert.Equal(AxiosException.ErrBadResponse, error.Code);
        Assert.Contains("8388608", error.Message);
    }

    [Fact]
    public async Task PerRequestExplicitNull_ClearsInstanceDefault()
    {
        // 实例默认放到 12 MB，按请求显式设 null 表示「回到默认 8 MB」：
        // 合并层必须看 *Set 标记，否则「未设置」与「显式 null」会被混为一谈。
        using var client = CreateFrameLimitClient(NdjsonFrame(9 * Mb), NdjsonMediaType, c => c.Limits.MaxStreamFrameLength = 12 * Mb);

        var error = await Assert.ThrowsAsync<AxiosException>(
            () => ReadAllAsync(client, AxiosStreamFormat.NewlineDelimitedJson, c => c.Limits.MaxStreamFrameLength = null));

        Assert.Equal(AxiosException.ErrBadResponse, error.Code);
        Assert.Contains("8388608", error.Message);
    }

    [Fact]
    public void Clone_PreservesConfiguredValue()
    {
        var config = new AxiosConfig { Limits = new() { MaxStreamFrameLength = 12 * Mb } };

        Assert.Equal(12 * Mb, config.Clone().Limits.MaxStreamFrameLength);
    }

    // ------------------------------------------------------------------
    // 越界：发送前 fail-fast，不发请求
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(512 * 1024)]
    [InlineData(64 * 1024 * 1024 + 1)]
    public async Task OutOfRangeValue_FailsBeforeSending(int frameLength)
    {
        var handler = new ScriptedStreamHandler(NdjsonFrame(16), contentType: NdjsonMediaType);
        using var client = CreateClient(handler, c => c.Limits.MaxStreamFrameLength = frameLength);

        var error = await Assert.ThrowsAsync<AxiosException>(
            () => ReadAllAsync(client, AxiosStreamFormat.NewlineDelimitedJson));

        Assert.Equal(AxiosException.ErrBadOptionValue, error.Code);
        Assert.Contains("MaxStreamFrameLength", error.Message);
        Assert.Equal(0, handler.Calls);
    }


    [Get("stream")]
    private sealed class IdleStreamRequest : IApiRequest<FrameItem>
    {
    }

    private sealed record FrameItem(int N);

    private static ApiClient CreateStreamApiClient(AxiosConfig defaults)
    {
        var handler = new FixedBodyHandler(Encoding.UTF8.GetBytes("{\"n\":1}\n"));
        return new ApiClient(new AxiosClient(defaults, handler: handler, disposeHandler: true), ownsAxiosClient: true);
    }

    private static async Task DrainAsync<T>(IAsyncEnumerable<T>? source)
    {
        if (source is null)
        {
            return;
        }

        await foreach (var _ in source.ConfigureAwait(false))
        {
        }
    }

    private static int ActiveLeases(HttpConnectionPool pool)
        => pool.GetStatistics().Endpoints.Sum(e => e.ActiveLeases);


    /// <summary>每次都返回同一段 NDJSON 正文的处理器。</summary>
    private sealed class FixedBodyHandler : HttpMessageHandler
    {
        private readonly byte[] _body;

        public FixedBodyHandler(byte[] body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(_body))
            };
            response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/x-ndjson");
            return Task.FromResult(response);
        }
    }


    public sealed record Frame(int N, string? T = null);

    [Get("stream")]
    private sealed class StreamRequest : IApiRequest<Frame>
    {
    }


    /// <summary>固定应答的离线 Handler（回放语义用例只需要一条稳定的响应）。</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_respond(request));
    }


    [Get("/sse")]
    public sealed class SseBigFrameRequest : IApiRequest<IAsyncEnumerable<string>>
    {
    }


    public sealed class StreamErrorBody
    {
        public string? Code { get; set; }
    }

    private sealed class StaticTokenProvider : IAccessTokenProvider
    {
        private readonly string? _token;

        public StaticTokenProvider(string? token) => _token = token;

        public ValueTask<string?> GetTokenAsync(ApiAuthContext context, CancellationToken cancellationToken)
            => new(_token);
    }


    private sealed class OverridingAuthorizationProvider : IAuthorizationProvider
    {
        private readonly string _value;

        public OverridingAuthorizationProvider(string value) => _value = value;

        public ValueTask ApplyAsync(ApiAuthContext context, CancellationToken cancellationToken)
        {
            context.Config.Headers.Set("Authorization", _value);
            return default;
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

    /// <summary>拼一个约 <paramref name="approxBytes"/> 字节的对象：用于压溢写缓冲。</summary>
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


    /// <summary>按给定边界切块（最后一个边界即结尾）；用于制造跨块 token 与跨块多字节字符。</summary>
    private static byte[][] Slice_ApiClientStreamingJsonArray(byte[] body, params int[] cuts)
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

    private static byte[][] Slice_ApiClientStreamingJsonArray(byte[] body, int chunkSize)
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


    [Get("stream/items")]
    private sealed class ItemRequest : IApiRequest<StreamFrame>
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


    [Get("stream")]
    private sealed class StreamFrameRequest : IApiRequest<StreamFrame>
    {
    }


    /// <summary>永远返回 string 的序列化器：用于制造「非 null 但类型不符」的帧值。</summary>
    private sealed class WrongTypeSerializer : IContentSerializer
    {
        public string Serialize(object? value, Type type, AxiosConfig config) => string.Empty;

        public object? Deserialize(string text, Type type, AxiosConfig config) => "wrong-type";
    }


    /// <summary>一旦参与帧反序列化就失败：用来证明序列化器是取流时解析的。</summary>
    private sealed class ThrowingFrameSerializer : IContentSerializer
    {
        public string? MediaType => null;

        public string Serialize(object? value, Type type, AxiosConfig config) => "{}";

        public object? Deserialize(string text, Type type, AxiosConfig config)
            => throw new InvalidOperationException("帧序列化器应在取流时解析，流开始后注册不该生效。");
    }


    private const int Mb = 1024 * 1024;

    private const string NdjsonMediaType = "application/x-ndjson";

    private const string SseMediaType = "text/event-stream";

    private const string JsonMediaType = "application/json";


    /// <summary>9 MB 级的单帧：一个 JSON 字符串 token（引号内全是 ASCII，字符数 = 字节数）。</summary>
    private static byte[][] NdjsonFrame(int payloadChars)
        => Chunk(Encoding.UTF8.GetBytes("\"" + new string('a', payloadChars) + "\"\n"));

    private static byte[][] SseFrame(int payloadChars)
        => Chunk(Encoding.UTF8.GetBytes("data: \"" + new string('a', payloadChars) + "\"\n\n"));

    private static byte[][] JsonArrayElement(int payloadChars)
        => Chunk(Encoding.UTF8.GetBytes("[\"" + new string('a', payloadChars) + "\"]"));

    /// <summary>切成 64 KB 块：真实网络不会一次把 9 MB 交给读取方。</summary>
    private static byte[][] Chunk(byte[] body)
    {
        const int chunkSize = 64 * 1024;
        var chunks = new List<byte[]>((body.Length / chunkSize) + 1);
        for (var offset = 0; offset < body.Length; offset += chunkSize)
        {
            chunks.Add(body[offset..Math.Min(offset + chunkSize, body.Length)]);
        }

        return chunks.ToArray();
    }

    /// <summary>
    /// JSON 数组分帧用例的正文 Handler：Content-Type 必须是 application/json，
    /// 其余能力（断流位置 / 读完挂起 / 换 charset）都走共享实现。
    /// </summary>
    private static ScriptedStreamHandler JsonArrayHandler(
        byte[][] chunks,
        int failAtIndex = -1,
        bool stallAfterChunks = false,
        string contentType = "application/json")
        => new(chunks, failAtIndex: failAtIndex, stallAfterChunks: stallAfterChunks, contentType: contentType);

    /// <summary>帧上限用例：正文按块给、Content-Type 逐例指定，配置经 <paramref name="configure"/> 注入。</summary>
    private static ApiClient CreateFrameLimitClient(
        byte[][] chunks, string contentType, Action<AxiosConfig>? configure = null)
        => CreateClient(new ScriptedStreamHandler(chunks, contentType: contentType), configure);


    private static async Task<List<string>> ReadAllAsync(
        ApiClient client,
        AxiosStreamFormat format,
        Action<AxiosConfig>? configure = null)
    {
        var response = await client.RequestStreamAsync(new AnyRequest_StreamFrameLimit(), format, configure);

        var items = new List<string>();
        await foreach (var item in response.Data!)
        {
            items.Add(item);
        }

        return items;
    }

    /// <summary>帧类型就是 JSON 字符串：一个元素一个 token，最省事地压到分帧上限。</summary>
    [Get("stream/any")]
    private sealed class AnyRequest_StreamFrameLimit : IApiRequest<string>
    {
    }


}