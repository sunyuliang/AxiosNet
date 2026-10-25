using System.Net;
using System.Net.Http;
using System.Text;
using AxiosNet.WebAPI;

namespace AxiosNet.Tests.WebAPI;

/// <summary>
/// 流式单帧上限（<see cref="AxiosConfig.LimitSettings.MaxStreamFrameLength"/>）：
/// 默认 8 MB 不变、可收紧可放大、null 回到默认、越界在发送前 fail-fast。
/// <para>
/// 这里刻意用 <c>string</c> 作为帧类型：一个 JSON 字符串就是一个 token，能同时压到
/// 「读缓冲撑满」（单 token）与「溢写缓冲累计」（多 token）两条上限路径。
/// </para>
/// </summary>
public sealed class StreamFrameLimitTests
{
    private const int Mb = 1024 * 1024;
    private const string NdjsonMediaType = "application/x-ndjson";
    private const string SseMediaType = "text/event-stream";
    private const string JsonMediaType = "application/json";

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
        using var client = CreateClient(NdjsonFrame(9 * Mb), NdjsonMediaType);

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
        using var client = CreateClient(NdjsonFrame(9 * Mb), NdjsonMediaType, c => c.Limits.MaxStreamFrameLength = 12 * Mb);

        var items = await ReadAllAsync(client, AxiosStreamFormat.NewlineDelimitedJson);

        Assert.Equal(9 * Mb, Assert.Single(items).Length);
    }

    [Fact]
    public async Task ConfiguredLimit_AllowsSseFrameAboveDefault()
    {
        using var client = CreateClient(SseFrame(9 * Mb), SseMediaType, c => c.Limits.MaxStreamFrameLength = 12 * Mb);

        var items = await ReadAllAsync(client, AxiosStreamFormat.ServerSentEvents);

        Assert.Equal(9 * Mb, Assert.Single(items).Length);
    }

    [Fact]
    public async Task ConfiguredLimit_AllowsJsonArrayElementAboveDefault()
    {
        using var client = CreateClient(JsonArrayElement(9 * Mb), JsonMediaType, c => c.Limits.MaxStreamFrameLength = 12 * Mb);

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
        using var client = CreateClient(NdjsonFrame(2 * Mb), NdjsonMediaType, c => c.Limits.MaxStreamFrameLength = 1 * Mb);

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
        using var client = CreateClient(NdjsonFrame(9 * Mb), NdjsonMediaType, c => c.Limits.MaxStreamFrameLength = 1 * Mb);

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
        using var client = CreateClient(NdjsonFrame(2 * Mb), NdjsonMediaType, c => c.Limits.MaxStreamFrameLength = 12 * Mb);

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
        using var client = CreateClient(
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
        using var client = CreateClient(NdjsonFrame(9 * Mb), NdjsonMediaType, c => c.Limits.MaxStreamFrameLength = 12 * Mb);

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
        var handler = new ScriptedHandler(NdjsonFrame(16), NdjsonMediaType);
        using var client = CreateClient(handler, c => c.Limits.MaxStreamFrameLength = frameLength);

        var error = await Assert.ThrowsAsync<AxiosException>(
            () => ReadAllAsync(client, AxiosStreamFormat.NewlineDelimitedJson));

        Assert.Equal(AxiosException.ErrBadOptionValue, error.Code);
        Assert.Contains("MaxStreamFrameLength", error.Message);
        Assert.Equal(0, handler.Calls);
    }

    // ------------------------------------------------------------------
    // 辅助
    // ------------------------------------------------------------------

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

    private static ApiClient CreateClient(byte[][] chunks, string contentType, Action<AxiosConfig>? configure = null)
        => CreateClient(new ScriptedHandler(chunks, contentType), configure);

    private static ApiClient CreateClient(ScriptedHandler handler, Action<AxiosConfig>? configure = null)
    {
        var config = new AxiosConfig { BaseAddress = "http://stream.test/" };
        configure?.Invoke(config);
        return new ApiClient(new AxiosClient(config, handler: handler, disposeHandler: true), ownsAxiosClient: true);
    }

    private static async Task<List<string>> ReadAllAsync(
        ApiClient client,
        AxiosStreamFormat format,
        Action<AxiosConfig>? configure = null)
    {
        var response = await client.RequestStreamAsync(new AnyRequest(), format, configure);

        var items = new List<string>();
        await foreach (var item in response.Data!)
        {
            items.Add(item);
        }

        return items;
    }

    /// <summary>帧类型就是 JSON 字符串：一个元素一个 token，最省事地压到分帧上限。</summary>
    [Get("stream/any")]
    private sealed class AnyRequest : IApiRequest<string>
    {
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly byte[][] _chunks;
        private readonly string _contentType;

        public ScriptedHandler(byte[][] chunks, string contentType)
        {
            _chunks = chunks;
            _contentType = contentType;
        }

        /// <summary>到达传输层的次数：越界配置必须在为 0 时就被拦下。</summary>
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;

            var content = new StreamContent(new ScriptedStream(_chunks));
            content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(_contentType);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request
            });
        }
    }

    private sealed class ScriptedStream : Stream
    {
        private readonly byte[][] _chunks;
        private int _index;
        private int _offset;

        public ScriptedStream(byte[][] chunks) => _chunks = chunks;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_index >= _chunks.Length)
            {
                return ValueTask.FromResult(0);
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

            return ValueTask.FromResult(count);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
