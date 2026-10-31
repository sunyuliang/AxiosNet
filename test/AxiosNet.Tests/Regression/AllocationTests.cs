
using AxiosNet.Internal;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Internal;
using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AxiosNet.Tests.Regression;

/// <summary>
/// 分配护栏：克隆成本、流读取的 CTS 复用、声明长度预分配上限。
/// </summary>
[Collection(AllocationCollection.Name)]
public sealed class AllocationTests
{
    [Fact]
    public void Config_Clone_StaysFarBelowNewPlusAssignCost()
    {
        var config = new AxiosConfig { Timeout = TimeSpan.FromSeconds(3) };
        config.Headers.Set("Authorization", "Bearer token");

        // 带内容 Headers 的配置克隆约 700 B/次；阈值 1050 能抓住「重新 new + 逐属性赋值」的退化路径，
        // 同时给运行时差异留余量。
        var average = AverageBytes(20_000, () => config.Clone());
        Assert.True(
            average < 1050,
            $"AxiosConfig.Clone() 平均 {average} B/次，超出护栏阈值 1050 B——疑似退回了 new + 逐属性赋值路径。");
    }

    [Fact]
    public void Config_Clone_KeepsCollectionsIndependent()
    {
        var source = new AxiosConfig();
        source.Headers.Set("Accept", "application/json");
        source.RemoveHeaders.Add("X-Trace");
        source.TransformRequest.Add((data, config) => data);
        source.TransformResponse.Add((data, config) => data);

        var clone = source.Clone();

        // MemberwiseClone 是浅拷贝：集合成员漏替换就会与源共享，合并时互相污染
        Assert.NotSame(source.Headers, clone.Headers);
        Assert.NotSame(source.RemoveHeaders, clone.RemoveHeaders);
        Assert.NotSame(source.TransformRequest, clone.TransformRequest);
        Assert.NotSame(source.TransformResponse, clone.TransformResponse);

        clone.Headers.Set("Accept", "text/plain");
        clone.RemoveHeaders.Add("X-Other");
        clone.TransformRequest.Clear();

        Assert.Equal("application/json", source.Headers.Get("Accept"));
        Assert.Equal("X-Trace", Assert.Single(source.RemoveHeaders));
        Assert.Single(source.TransformRequest);
        Assert.Single(source.TransformResponse);
    }

    [Fact]
    public void Config_Clone_EmptyChain_IsUsableAndIsolated()
    {
        var source = new AxiosConfig();
        var clone = source.Clone();

        // 空链走无参构造（不加锁、不建快照 List），但语义必须与普通副本一致
        Assert.Empty(clone.TransformRequest);
        Assert.Empty(clone.TransformResponse);
        Assert.Empty(clone.RemoveHeaders);

        clone.TransformRequest.Add((data, config) => data);
        clone.RemoveHeaders.Add("X-Trace");

        Assert.Empty(source.TransformRequest);
        Assert.Empty(source.RemoveHeaders);
    }

    [Fact]
    public async Task StreamRead_WithRepeatedCallerToken_ReusesOneLinkedCts()
    {
        const int size = 8 * 1024 * 1024;
        // 注入 Handler + 自带记录流，而不是 TestHttpServer：真实 socket 读路径的令牌会随
        // 底层实现变化，这里要观察的只是「库交给底层那一次的令牌」。
        var content = new RecordingPayloadContent(size);
        using var client = new AxiosClient(handler: new RecordingPayloadHandler(content));
        using var cts = new CancellationTokenSource();
        var buffer = new byte[131072];

        var response = await client.GetAsync<Stream>("http://localhost/big");
        using var stream = response.Data!;

        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cts.Token);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        Assert.Equal(size, total);

        var tokens = content.ReadTokens;
        Assert.True(tokens.Count > 10, $"读取块数异常：{tokens.Count}。");

        // 结构断言，而不是分配测量：反复传同一个调用方令牌时，整段循环只应建一次链接源，
        // 因此交给底层的令牌**每次都是同一个**（CancellationToken 按源比较）。
        // 每块新建 linked CTS 会让每块令牌的源各不相同，这里必然失败。
        //
        // 用令牌身份断言而不是分配差值：线程局部分配计数会被同线程无关分配污染，令牌身份不受运行时噪声影响。
        var first = tokens[0];
        Assert.True(first.CanBeCanceled, "交给底层的令牌应链接了调用方令牌（可取消）。");
        Assert.True(
            tokens.All(token => token == first),
            $"反复传同一个调用方令牌时，{tokens.Count} 块中出现了 {tokens.Distinct().Count()} 种不同的令牌"
            + "——疑似每块都在新建 linked CTS。");
    }

    [Fact]
    public async Task StreamRead_SwappedCallerToken_NewTokenTakesEffect()
    {
        // 换令牌必须让缓存失效：新令牌（这里预取消）要立刻生效，
        // 不能继续沿用上一个令牌对应的链接源。
        const int size = 256 * 1024;
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: new byte[size], ContentType: "application/octet-stream"));

        using var client = new AxiosClient();
        var response = await client.GetAsync<Stream>(server.Url + "file");
        await using var stream = response.Data!;

        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        var buffer = new byte[64];

        // 第一块用 first：内部缓存 first 对应的链接源
        Assert.Equal(64, await stream.ReadAsync(buffer, first.Token));

        // 换 second（已取消）：必须取消，而不是复用 first 的链接。
        // 传输层取消抛的是 TaskCanceledException（OperationCanceledException 的子类），
        // 与 StreamCancellationTests 的既有断言一致用 ThrowsAnyAsync。
        second.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await stream.ReadAsync(buffer, second.Token));

        // first 的链接源被替换时释放，但流本身不受影响：不传令牌仍走预建链接源正常读
        Assert.Equal(64, await stream.ReadAsync(buffer));
    }

    [Theory]
    [InlineData(1024 * 1024)]      // 8MB 预分配信任上限内：读满声明值直接交出那个数组
    [InlineData(32 * 1024 * 1024)] // 超过信任上限：段式累积，段从 ArrayPool 租、只装配一次
    public void DeclaredLengthResponse_PaysAtMostOneAndAHalfPayloads(int size)
    {
        // 内存响应（ByteArrayContent 自带 Content-Length）：同步完成，分配全部落在测量线程上；
        // 真实 socket 读路径每块有几百 B 且随分段波动，基线不可比。
        using var client = new AxiosClient(handler: new MemoryPayloadHandler(size));

        // warmup：JIT、静态缓存、HttpClient 首次管道与 ArrayPool 冷启动都不计入测量窗口
        ReadDeclaredPayload(client, size);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var data = ReadDeclaredPayload(client, size);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(size, data.Length);

        // 1× 是设计值：预分配路径直接交出数组，段式路径只付最终数组（段来自池，warmup 后零新分配）。
        // 两条退化路径都落在护栏之外：老 MemoryStream 倍增到 32MB 累计 ≈ 2× → ToArray 再一份 ≈ 3×；
        // 「预分配缓冲 + 重新装配」= 2×。阈值 1.5× 卡在两者之下，又给运行时噪声留余量。
        Assert.True(
            allocated < size * 3 / 2,
            $"声明长度 {size:N0} B 的响应读取分配了 {allocated:N0} B（{allocated / (double)size:0.00}× 载荷）"
            + "——超出 1.5× 护栏：要么预分配路径变成了「预分配 + 重新装配」，要么段式累积退回了 MemoryStream 倍增。");
    }

    [Fact]
    public void JsonTypedResponse_PaysObjectGraphNotAnExtraCopy()
    {
        // 现有 JSON 用例只验「正文转换入口不整串解码」；整条响应（原始字节 + 对象图）的分配量没有量尺，
        // 字节被多复制一份这种退化不会被发现。本机实测（net6 / net8 一致）：1MB 正文约 3.40×——
        // 1× 原始字节 + List / 对象 / 字符串；4× 的门槛卡在「再多一次整串复制」之下。
        const int size = 1024 * 1024;
        using var client = new AxiosClient(handler: new MemoryPayloadHandler(BuildJsonPayload(size), "application/json"));

        // 测量窗口内必须同步推进：await 会引入测试自己的分配。
#pragma warning disable xUnit1031
        void Read() => _ = client.GetAsync<List<JsonNode>>("http://localhost/json").GetAwaiter().GetResult();
        var sample = client.GetAsync<List<JsonNode>>("http://localhost/json").GetAwaiter().GetResult();
#pragma warning restore xUnit1031

        Assert.True(sample.Data!.Count > 0, "JSON 没有反序列化成对象图，这条用例测不到被测路径。");

        var average = AverageBytes(3, Read);

        Assert.True(
            average < size * 4,
            $"1MB JSON 强类型响应平均分配 {average:N0} B（{average / (double)size:0.00}× 载荷）——超出 4× 护栏："
            + "疑似整串解码成 UTF-16 再反序列化，或字节被额外复制了一份。");
    }

    [Fact]
    public void JsonElementResponse_PaysDocumentAndClone()
    {
        // 非泛型路径保留 JsonElement：实测 4.09× = 1× 原始字节 + Parse + RootElement.Clone() + 释放 3.09×。
        // 3.09× 与裸 JsonSerializer.Deserialize<JsonElement> 逐字节相同，是 STJ 交付 JsonElement 的地板。
        // 门槛 4.5× 专门卡住「省掉 Clone、让元素持有未释放的文档」这个看似划算的改法——实测更贵：
        // 不释放的文档每次都要重新租元数据数组，总量反而升到 5.02×（见 test/AxiosNet.Benchmarks/README.md）。
        const int size = 1024 * 1024;
        using var client = new AxiosClient(handler: new MemoryPayloadHandler(BuildJsonPayload(size), "application/json"));

#pragma warning disable xUnit1031
        void Read() => _ = client.GetAsync("http://localhost/json").GetAwaiter().GetResult();
        var sample = client.GetAsync("http://localhost/json").GetAwaiter().GetResult();
#pragma warning restore xUnit1031

        Assert.True(((JsonElement)sample.Data!).GetArrayLength() > 0, "JSON 没有解析成 JsonElement，这条用例测不到被测路径。");

        var average = AverageBytes(3, Read);

        Assert.True(
            average < size * 9 / 2,
            $"1MB JSON JsonElement 响应平均分配 {average:N0} B（{average / (double)size:0.00}× 载荷）——超出 4.5× 护栏："
            + "要么是文档之外又多了一次整串解码 / 复制，要么是省掉了 Clone、让未释放的文档每次重新租元数据（后者实测更贵）。");
    }

    [Fact]
    public void JsonTextResponse_DoesNotDecodeWholeBodyBeforeSerialization()
    {
        // 1 MB 的 JSON 文本正文 + 只记录入口的序列化器：正文不该先解码成整串 UTF-16（2 倍体量），
        // 再由 STJ 转码回 UTF-8——业务对象直接走字节入口。
        var body = new byte[1024 * 1024];
        Array.Fill(body, (byte)'a');

        var serializer = new EntryOnlySerializer();
        var config = new AxiosConfig();
        config.Serialization.ContentSerializer = serializer;

        object? Convert()
            => ResponseReader.ConvertBody(config, null, typeof(JsonItem), body, AxiosContentType.Json, "utf-8", lenient: false, out _);

        var average = AverageBytes(50, () => Convert());

        Assert.Equal(0, serializer.TextCalls);
        Assert.True(serializer.BytesCalls > 0, "业务对象的反序列化没有走字节入口。");
        Assert.True(
            average < 64 * 1024,
            $"1 MB JSON 文本响应平均分配 {average:N0} B——超出护栏：正文又被解码成了整串 UTF-16。");
    }

    [Fact]
    public async Task SseFrames_AreDeliveredWithoutPerLineDecoding()
    {
        // 每帧 data 行只按字节切片交给序列化器：帧开销不应随「每行解码 + 字段子串」线性增长。
        const int frameCount = 2000;

        // +1 帧供 AverageBytes 的测量前预热消费。
        var body = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("data: {}\n\n", frameCount + 1)));

        var serializer = new EntryOnlySerializer();
        var config = new AxiosConfig();
        config.Serialization.ContentSerializer = serializer;

        await using var stream = new MemoryStream(body);
        var frames = new JsonStreamEnumerable<JsonItem>(stream, config, null, AxiosStreamFormat.ServerSentEvents, charSet: null, "text/event-stream");
        await using var enumerator = frames.GetAsyncEnumerator();

        // 测量窗口内必须同步推进：await 会引入测试自己的分配。
#pragma warning disable xUnit1031
        bool MoveNext() => enumerator.MoveNextAsync().GetAwaiter().GetResult();
#pragma warning restore xUnit1031

        var average = AverageBytes(frameCount, () =>
        {
            if (!MoveNext())
            {
                throw new InvalidOperationException("帧数不足：枚举提前结束。");
            }
        });

        Assert.Equal(0, serializer.TextCalls);
        Assert.Equal(frameCount + 1, serializer.BytesCalls);

        // Debug 下 async 状态机以类分配（Release 实测 0 B/帧），分配量没有参考价值：只验路由，分配断言留给优化构建。
#if DEBUG
        GC.KeepAlive(average);
#else
        Assert.True(
            average < 64,
            $"SSE 每帧平均分配 {average} B——超出护栏：每行又在解码成字符串、字段名与值又各切了一次子串。");
#endif
    }

    private static long AverageBytes(int iterations, Action action)
    {
        action(); // warmup：JIT 与静态缓存不计入
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            action();
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
    }

    /// <summary>固定形状的 JSON 数组（元素 = 整数 / 10 字符字符串 / 浮点），让两条 JSON 护栏的量尺可比。</summary>
    private static byte[] BuildJsonPayload(int size)
    {
        const string item = """{"id":123,"name":"abcdefghij","value":1234.5}""";
        var count = (size / item.Length) + 1;
        var builder = new StringBuilder(2 + (count * (item.Length + 1)));
        builder.Append('[');
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(item);
        }

        builder.Append(']');
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private sealed class JsonNode
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public double Value { get; set; }
    }

    /// <summary>返回内存响应的 Handler：读取同步完成，用于确定的分配测量。</summary>
    private sealed class MemoryPayloadHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;
        private readonly string _mediaType;

        public MemoryPayloadHandler(int size)
            : this(new byte[size], "application/octet-stream")
        {
        }

        public MemoryPayloadHandler(byte[] payload, string mediaType)
        {
            _payload = payload;
            _mediaType = mediaType;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new ByteArrayContent(_payload);
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue(_mediaType);
            return Task.FromResult(response);
        }
    }

    /// <summary>返回自带记录流的响应：记录库每次交给底层读的令牌。</summary>
    private sealed class RecordingPayloadHandler : HttpMessageHandler
    {
        private readonly RecordingPayloadContent _content;

        public RecordingPayloadHandler(RecordingPayloadContent content) => _content = content;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = _content;
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            return Task.FromResult(response);
        }
    }

    private sealed class RecordingPayloadContent : HttpContent
    {
        private readonly RecordingStream _stream;

        public RecordingPayloadContent(int size) => _stream = new RecordingStream(size);

        public IReadOnlyList<CancellationToken> ReadTokens => _stream.ReadTokens;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(_stream);

        private sealed class RecordingStream : Stream
        {
            private readonly byte[] _payload;
            private int _position;

            public RecordingStream(int size) => _payload = new byte[size];

            public List<CancellationToken> ReadTokens { get; } = new();

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count)
                => ReadInto(buffer.AsSpan(offset, count));

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                ReadTokens.Add(cancellationToken);
                return new ValueTask<int>(ReadInto(buffer.Span));
            }

            // 私有方法不能 override 虚成员：与 Stream.Read(Span<byte>) 同签名会触发 CS0114
            //（对外部调用方而言私有方法不可见，行为上无影响，但警告即噪音，改名消除）。
            private int ReadInto(Span<byte> buffer)
            {
                var take = Math.Min(buffer.Length, _payload.Length - _position);
                _payload.AsSpan(_position, take).CopyTo(buffer);
                _position += take;
                return take;
            }
        }
    }

    /// <summary>以 arraybuffer 同步读整个响应并校验长度；同步阻塞等待避免 await 基础设施噪声。</summary>
    private static byte[] ReadDeclaredPayload(AxiosClient client, int expected)
    {
        var response = client.GetAsync<byte[]>(
            "http://localhost/big",
            c => c.ResponseType = ResponseType.ArrayBuffer).GetAwaiter().GetResult();
        Assert.Equal(expected, response.Data!.Length);
        return response.Data;
    }

    /// <summary>只记录入口、返回共享实例的序列化器：把「正文转换」本身的分配与反序列化分配隔离开。</summary>
    private sealed class EntryOnlySerializer : IContentSerializer
    {
        public static readonly JsonItem Value = new();

        public int TextCalls { get; private set; }

        public int BytesCalls { get; private set; }

        public string Serialize(object? value, Type type, AxiosConfig config) => throw new NotSupportedException();

        public object? Deserialize(string text, Type type, AxiosConfig config)
        {
            TextCalls++;
            return Value;
        }

        public object? Deserialize(ReadOnlyMemory<byte> data, Type type, AxiosConfig config)
        {
            BytesCalls++;
            return Value;
        }
    }

    private sealed class JsonItem
    {
    }

    /// <summary>
    /// 接口调用的实参视图必须按需创建：每次接口调用都会 new 一个 <see cref="ApiInterfaceInvocation"/>，
    /// 若在构造里就做 <c>Array.AsReadOnly</c>，等于每次请求白送一个只读包装（绝大多数请求没人读 Arguments）。
    /// </summary>
    [Fact]
    public void InterfaceInvocation_CreatesArgumentsViewOnlyWhenRead()
    {
        var args = new object?[] { "alice", 30 };

        // 只构造、不读 Arguments：应当只有一个对象本身（x64 上 4 个引用字段 + 对象头 = 48 B）。
        var withoutView = AverageBytes(20_000, () => _ = new ApiInterfaceInvocation(typeof(AllocationTests), "Call", args));
        var withView = AverageBytes(20_000, () => _ = new ApiInterfaceInvocation(typeof(AllocationTests), "Call", args).Arguments);

        Assert.True(
            withoutView < 64,
            $"构造 ApiInterfaceInvocation 平均分配 {withoutView} B——只该有一个对象，读视图不应跟着分配。");

        Assert.True(
            withView > withoutView,
            $"读 Arguments 应额外分配只读包装：不读 {withoutView} B / 读 {withView} B——差值消失说明视图又在构造里做了。");
    }

    [Fact]
    public void InterfaceInvocation_ArgumentsViewIsCachedAndStable()
    {
        var args = new object?[] { "alice", 30 };
        var invocation = new ApiInterfaceInvocation(typeof(AllocationTests), "Call", args);

        Assert.Same(invocation.Arguments, invocation.Arguments);
        Assert.Equal(2, invocation.Arguments.Count);
        Assert.Same(args[0], invocation.Arguments[0]);
        Assert.Equal(30, invocation.Arguments[1]);
    }
}
