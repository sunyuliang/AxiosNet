using System.Buffers;
using System.Diagnostics;
using System.Net;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 热路径分配护栏（2026-10-21 P1 三项优化的固化）：
/// <list type="bullet">
/// <item><see cref="AxiosConfig.Clone"/> 不得回到「new + 逐属性赋值」路径——
/// 构造期白分配 <c>_headers</c> 与三个列表，每请求一次纯浪费；</item>
/// <item>空 transform 链的 Clone 不得触发 <c>SynchronizedList</c> 的锁内快照构造；</item>
/// <item>流式响应反复用同一个调用方令牌读取时，不得每块新建 linked CTS
/// （用「交给底层的令牌每次相同」做结构断言，不做分配测量，见该用例注释）。</item>
/// </list>
/// 阈值均留一倍以上余量：这些是用例「数量级不退回到修复前」的护栏，不是精确基准，
/// 换运行时 / 换 GC 模式不应让它们误报。
/// </summary>
public sealed class AllocationRegressionTests
{
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

    [Fact]
    public void Config_Clone_StaysFarBelowNewPlusAssignCost()
    {
        var config = new AxiosConfig { Timeout = TimeSpan.FromSeconds(3) };
        config.Headers.Set("Authorization", "Bearer token");

        // 修复前：new AxiosConfig（含 4 个集合 + _headers 初始化）≈ 704 B，
        // 加上属性逐个赋值与被立刻覆盖的白分配，实测 1168 B/次；
        // MemberwiseClone 之后实测 960 B/次（4 个空集合各拷一份）；
        // 集合改为延迟创建后实测 ≈ 700 B/次（只剩这份有内容的 Headers 副本 + 对象本身）。
        // 阈值 1050：仍能抓住「退回到 new + 逐属性赋值」的 1168 B 路径，同时给运行时差异留余量。
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
        // 原先用「带令牌 / 不带令牌两条路径的分配差值 < 2048 B」做护栏，但线程局部分配计数
        // 在 CI 的并行负载下会被同线程上的无关分配污染（实测差值可达 7 KB，而单次 linked CTS
        // 只有几百 B），导致误报；令牌身份不受运行时噪声影响。
        var first = tokens[0];
        Assert.True(first.CanBeCanceled, "交给底层的令牌应链接了调用方令牌（可取消）。");
        Assert.True(
            tokens.All(token => token == first),
            $"反复传同一个调用方令牌时，{tokens.Count} 块中出现了 {tokens.Distinct().Count()} 种不同的令牌"
            + "——疑似每块都在新建 linked CTS。");
    }

    /// <summary>返回内存响应的 Handler：读取同步完成，用于确定的分配测量。</summary>
    private sealed class MemoryPayloadHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;

        public MemoryPayloadHandler(int size) => _payload = new byte[size];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new ByteArrayContent(_payload);
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
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

    [Fact]
    public async Task StreamRead_SwappedCallerToken_NewTokenTakesEffect()
    {
        // 换令牌必须让缓存失效：新令牌（这里预取消）要立刻生效，
        // 不能继续沿用上一个令牌对应的链接源。
        const int size = 256 * 1024;
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: new byte[size],
            ContentType: "application/octet-stream"));

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

    [Fact]
    public void DeclaredLengthResponse_BeyondPreallocTrustBound_AllocatesCloseToOneTimes()
    {
        // 32MB：超过 8MB 预分配信任上限，走段式累积（pipeline 的池化分段思想）。
        // ByteArrayContent 自带 Content-Length；注入 Handler 而非 TestHttpServer：内存响应同步完成，
        // 分配全部落在测量线程上（真实 socket 读路径每块有几百 B 且随分段波动，基线不可比）。
        const int size = 32 * 1024 * 1024;
        using var client = new AxiosClient(handler: new MemoryPayloadHandler(size));

        // warmup：JIT、静态缓存、HttpClient 首次管道与 ArrayPool 冷启动都不计入测量窗口
        ReadDeclaredPayload(client, size);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var data = ReadDeclaredPayload(client, size);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(size, data.Length);

        // MemoryStream 老路（>8MB 时的唯一路径）：从 256B 倍增到 32MB（累计 ≈ 2×），
        // 扩容必过冲 → TryGetBuffer 快路径不命中 → 再 ToArray 一次，合计 ≈ 3× 载荷。
        // 段式累积：段从 ArrayPool 租用（warmup 后全部命中、零新分配），只付 1× 最终数组。
        // 阈值 1.5×：足以抓住退回 MemoryStream 倍增的路径，又给运行时噪声留一倍以内余量。
        Assert.True(
            allocated < size * 3 / 2,
            $"声明长度 {size:N0} B 的响应读取分配了 {allocated:N0} B（{allocated / (double)size:0.0}× 载荷）"
            + "——超出 1.5× 护栏，疑似段式累积被替换回 MemoryStream 倍增路径。");
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
}
