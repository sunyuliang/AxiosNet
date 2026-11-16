using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Reflection;
using System.Text;

namespace AxiosNet.Compat.Tests;

/// <summary>
/// 兼容资产里那些<b>用扩展方法补 BCL 实例方法</b>的成员，逐个钉住它们在 netstandard2.0 资产上的实际语义。
/// </summary>
/// <remarks>
/// 用例必须显式调用 <c>NetStandard20Extensions.方法名(...)</c>：测试工程按 net8.0 / net472 编译，写成 <c>stream.ReadAsync(memory)</c> 会直接绑到 BCL 上，等于什么都没测。
/// 分配数看 <see cref="ShimEvidenceTests"/>，<c>WaitAsync</c> 与 HttpContent 令牌边界看 <see cref="CompatBehaviorTests"/>，本文件负责其余语义面。
/// </remarks>
public sealed class PolyfillSemanticsTests
{
    [Fact]
    public async Task ReadAsync_shim_返回实际读到的字节数_空区间不消耗流()
    {
        var stream = new MemoryStream(Encoding.ASCII.GetBytes("abcdef"));
        var buffer = new byte[2];

        Assert.Equal(2, await NetStandard20Extensions.ReadAsync(stream, buffer.AsMemory(), CancellationToken.None));
        Assert.Equal("ab", Encoding.ASCII.GetString(buffer));

        Assert.Equal(2, await NetStandard20Extensions.ReadAsync(stream, buffer.AsMemory(), CancellationToken.None));
        Assert.Equal("cd", Encoding.ASCII.GetString(buffer));

        // 空区间必须直接返回 0，且不动流位置：调用方的「没数据可读」不能变成「吃掉一字节」。
        var position = stream.Position;
        Assert.Equal(0, await NetStandard20Extensions.ReadAsync(stream, Memory<byte>.Empty, CancellationToken.None));
        Assert.Equal(position, stream.Position);
    }

    [Fact]
    public async Task ReadAsync_shim_对非数组支撑的_Memory_也把字节写进目标区间()
    {
        // ArrayPool 数组切出的 Memory 走零分配转发；只有「不是数组段支撑」的 Memory 才走租借缓冲 + 复制。
        // 这条路径一旦复制方向或长度算错，表现是「读到 3 字节、内容却是垃圾」——静默错数据，必须钉住。
        var target = new byte[4];
        using var manager = new WrappedMemoryManager(target);
        var stream = new MemoryStream(Encoding.ASCII.GetBytes("xyz"));

        var read = await NetStandard20Extensions.ReadAsync(stream, manager.Memory, CancellationToken.None);

        Assert.Equal(3, read);
        Assert.Equal("xyz\0", Encoding.ASCII.GetString(target));
    }

    [Fact]
    public async Task WriteAsync_shim_在数组与租借缓冲两条路径上都写出全部字节()
    {
        var payload = Encoding.ASCII.GetBytes("hello shim");

        var arrayBacked = new MemoryStream();
        await NetStandard20Extensions.WriteAsync(arrayBacked, payload.AsMemory(), CancellationToken.None);

        var rented = new MemoryStream();
        using var manager = new WrappedMemoryManager(payload);
        await NetStandard20Extensions.WriteAsync(rented, manager.Memory, CancellationToken.None);

        // 空区间直接返回，不碰底层流。
        var emptyTarget = new CountingStream();
        await NetStandard20Extensions.WriteAsync(emptyTarget, Memory<byte>.Empty, CancellationToken.None);

        Assert.Equal("hello shim", Encoding.ASCII.GetString(arrayBacked.ToArray()));
        Assert.Equal("hello shim", Encoding.ASCII.GetString(rented.ToArray()));
        Assert.Equal(0, emptyTarget.WriteCount);
    }

    [Fact]
    public void Write_shim_同步重载写出全部字节_空区间不调用底层()
    {
        var target = new MemoryStream();
        NetStandard20Extensions.Write(target, Encoding.ASCII.GetBytes("sync").AsSpan());

        // 空区间：底层一次都不该被调用（否则会往目标流里塞一个 0 字节写入）。
        var counting = new CountingStream();
        NetStandard20Extensions.Write(counting, ReadOnlySpan<byte>.Empty);

        Assert.Equal("sync", Encoding.ASCII.GetString(target.ToArray()));
        Assert.Equal(0, counting.WriteCount);
    }

    [Fact]
    public void DisposeAsync_shim_退化为同步释放_并立即完成()
    {
        var stream = new CountingStream();

        var pending = NetStandard20Extensions.DisposeAsync(stream);

        // netstandard2.0 的 Stream 没有 IAsyncDisposable 契约：这里就是同步 Dispose()，
        // 因此 await 之前必须已经释放完毕。若哪天改成「真的异步」，这条用例会失败并提醒复核 await using 的语义。
        Assert.True(pending.IsCompleted);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task HttpContent_shim_的读取与复制_在进入边界就尊重已取消的令牌()
    {
        using var content = new StringContent("hello", Encoding.UTF8, "text/plain");

        // 正常路径：ReadAsStreamAsync / CopyToAsync 与 BCL 的返回内容一致。
        // 这里刻意用普通 using：net472 宿主的 Stream 没有 IAsyncDisposable 契约，`await using` 会退化成
        // 本文件正在测的那个 shim，把「测 A」和「依赖 A」搅在一起。
        using (var stream = await NetStandard20Extensions.ReadAsStreamAsync(content, CancellationToken.None))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            Assert.Equal("hello", await reader.ReadToEndAsync());
        }

        using (var sink = new MemoryStream())
        {
            await NetStandard20Extensions.CopyToAsync(content, sink, CancellationToken.None);
            Assert.Equal("hello", Encoding.UTF8.GetString(sink.ToArray()));
        }

        // 取消路径：令牌在进入边界生效（内部缓冲由 HttpContent 自己完成，无法逐块中断）。
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NetStandard20Extensions.ReadAsStreamAsync(content, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NetStandard20Extensions.CopyToAsync(content, new MemoryStream(), canceled.Token));
    }

    [Fact]
    public void Encoding_shim_的三个_Span_重载与数组版结果一致()
    {
        const string text = "中文 abc 😀 end";
        var utf8 = Encoding.UTF8;

        // GetBytes(chars, bytes)：返回写入字节数，且内容与数组版逐字节相同。
        var expectedBytes = utf8.GetBytes(text);
        var actualBytes = new byte[expectedBytes.Length];
        var written = NetStandard20Extensions.GetBytes(utf8, text.AsSpan(), actualBytes);

        Assert.Equal(expectedBytes.Length, written);
        Assert.Equal(expectedBytes, actualBytes);

        // GetString(bytes)：与数组版解码结果相同（含 4 字节代理对）。
        Assert.Equal(text, NetStandard20Extensions.GetString(utf8, expectedBytes.AsSpan()));
        Assert.Equal(string.Empty, NetStandard20Extensions.GetString(utf8, ReadOnlySpan<byte>.Empty));

        // GetCharCount(bytes)：与数组版一致。
        Assert.Equal(text.Length, NetStandard20Extensions.GetCharCount(utf8, expectedBytes.AsSpan()));
        Assert.Equal(0, NetStandard20Extensions.GetCharCount(utf8, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void StartsWith_与_Contains_的_char_重载与_string_版判定一致()
    {
        Assert.True(NetStandard20Extensions.StartsWith("abc", 'a'));
        Assert.False(NetStandard20Extensions.StartsWith("abc", 'b'));

        // 空串没有首字符：string.StartsWith(char) 的语义是 false，不能退化成「空串匹配一切」。
        Assert.False(NetStandard20Extensions.StartsWith(string.Empty, 'a'));

        Assert.True(NetStandard20Extensions.Contains("abc", 'b'));
        Assert.False(NetStandard20Extensions.Contains("abc", 'z'));

        Assert.True(NetStandard20Extensions.StartsWith("abc".AsSpan(), 'a'));
        Assert.False(NetStandard20Extensions.StartsWith(ReadOnlySpan<char>.Empty, 'a'));
        Assert.True(NetStandard20Extensions.Contains("abc".AsSpan(), 'c'));
        Assert.False(NetStandard20Extensions.Contains("abc".AsSpan(), 'z'));
    }

    [Fact]
    public void TryAdd_只在键不存在时写入_TryRemove_只在值匹配时移除()
    {
        IDictionary<string, int> dictionary = new Dictionary<string, int> { ["a"] = 1 };

        Assert.False(NetStandard20Extensions.TryAdd(dictionary, "a", 99));
        Assert.Equal(1, dictionary["a"]);

        Assert.True(NetStandard20Extensions.TryAdd(dictionary, "b", 2));
        Assert.Equal(2, dictionary["b"]);

        var concurrent = new ConcurrentDictionary<string, int>();
        concurrent["k"] = 1;

        // 值不匹配不能删：TryRemove(KeyValuePair) 的语义是「键值都相等才移除」。
        Assert.False(NetStandard20Extensions.TryRemove(concurrent, new KeyValuePair<string, int>("k", 2)));
        Assert.True(concurrent.ContainsKey("k"));

        Assert.True(NetStandard20Extensions.TryRemove(concurrent, new KeyValuePair<string, int>("k", 1)));
        Assert.False(concurrent.ContainsKey("k"));
    }

    [Fact]
    public void CreateDelegate_泛型重载创建的委托可直接调用()
    {
        var method = typeof(PolyfillSemanticsTests).GetMethod(
            nameof(Double),
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var fn = NetStandard20Extensions.CreateDelegate<Func<int, int>>(method);

        Assert.Equal(42, fn(21));
    }

    /// <summary>被测的静态方法：交给 <c>CreateDelegate</c> 造委托。</summary>
    private static int Double(int value) => value * 2;

    /// <summary>把一整块数组包成 <see cref="Memory{T}"/>：<c>MemoryMarshal.TryGetArray</c> 对它返回 false，可逼出 shim 的租借缓冲路径。</summary>
    private sealed class WrappedMemoryManager : MemoryManager<byte>
    {
        private readonly byte[] _array;

        public WrappedMemoryManager(byte[] array) => _array = array;

        /// <inheritdoc />
        public override Span<byte> GetSpan() => _array;

        /// <inheritdoc />
        public override MemoryHandle Pin(int elementIndex = 0) => default;

        /// <inheritdoc />
        public override void Unpin()
        {
        }

        /// <summary>空实现：包装层不持有非托管资源。</summary>
        protected override void Dispose(bool disposing)
        {
        }
    }

    /// <summary>数底层被写了几次、并记录是否已释放的流。</summary>
    private sealed class CountingStream : Stream
    {
        public int WriteCount { get; private set; }

        public bool Disposed { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override void Flush()
        {
        }

        /// <summary>读取路径不支持（本类只关心写入）。</summary>
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <summary>不支持定位。</summary>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <summary>不支持改变长度。</summary>
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <summary>只累加写入次数，不保存字节。</summary>
        public override void Write(byte[] buffer, int offset, int count) => WriteCount++;

        /// <summary>置 <c>Disposed</c> 后交基类释放。</summary>
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
