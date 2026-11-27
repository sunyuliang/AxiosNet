using System.IO.Compression;

namespace AxiosNet.Internal.Content;

/// <summary>请求体压缩：只作用于本库按字节缓冲出来的正文（文本 / 二进制 / 表单），用 <see cref="CompressionLevel.Fastest"/>。</summary>
internal static class Compression
{
    /// <summary>是否开启了压缩。</summary>
    public static bool IsEnabled(AxiosConfig config)
        => config.Compression is { } compression && compression != AxiosCompression.None;

    /// <summary>按 <see cref="AxiosConfig.Compression"/> 压缩并补 <c>Content-Encoding</c>；未开启或正文为空时原样包装（不复制）。</summary>
    /// <remarks>空正文压缩只会得到一个几十字节的帧头，比不压还大，服务端还得多解一次；空正文因此不压缩、不写 <c>Content-Encoding</c>。</remarks>
    public static HttpContent WrapBytes(byte[] bytes, AxiosConfig config)
    {
        if (bytes.Length == 0
            || config.Compression is not { } compression
            || compression == AxiosCompression.None)
        {
            return new ByteArrayContent(bytes);
        }

        var content = CompressToContent(bytes, compression);

        // 头与正文由同一次判断得出，调用方不能再手写 Content-Encoding。
        content.Headers.ContentEncoding.Add(HeaderValue(compression));
        return content;
    }

    /// <summary>压缩成 <see cref="ByteArrayContent"/>：直接引用压缩缓冲区的有效段，不做 <c>ToArray()</c> 复制。</summary>
    private static ByteArrayContent CompressToContent(ReadOnlySpan<byte> bytes, AxiosCompression compression)
    {
        using var output = CompressToStream(bytes, compression);

        // Length 才是成品长度；取不到缓冲时退回复制。
        return output.TryGetBuffer(out ArraySegment<byte> segment)
            ? new ByteArrayContent(segment.Array!, segment.Offset, segment.Count)
            : new ByteArrayContent(output.ToArray());
    }

    /// <summary><see cref="AxiosCompression"/> 对应的 <c>Content-Encoding</c> 取值。</summary>
    public static string HeaderValue(AxiosCompression compression) => compression switch
    {
        AxiosCompression.Gzip => "gzip",
        AxiosCompression.Deflate => "deflate",
        AxiosCompression.Brotli => "br",
        _ => throw new ArgumentOutOfRangeException(nameof(compression), compression, "未定义的请求体压缩算法。")
    };

    /// <summary>按算法压缩一段字节。</summary>
    public static byte[] Compress(ReadOnlySpan<byte> bytes, AxiosCompression compression)
    {
        using var output = CompressToStream(bytes, compression);
        return output.ToArray();
    }

    /// <summary>压缩到内存流（调用方负责释放）。</summary>
    private static MemoryStream CompressToStream(ReadOnlySpan<byte> bytes, AxiosCompression compression)
    {
        var output = new MemoryStream(Math.Max(64, bytes.Length / 2));
        try
        {
            using (var compressor = CreateCompressor(output, compression))
            {
                compressor.Write(bytes);
            }

            return output;
        }
        catch
        {
            // 压缩器抛错时就地还掉缓冲区。
            output.Dispose();
            throw;
        }
    }

    // HTTP 的 deflate 是 zlib（RFC 1950）包装，不是裸 DEFLATE（RFC 1951）：RFC 7230 §4.2.2 指向 RFC 1950。
    private static Stream CreateCompressor(Stream output, AxiosCompression compression) => compression switch
    {
        AxiosCompression.Gzip => new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true),
#if NETSTANDARD2_0
        // netstandard2.0 没有 ZLibStream / BrotliStream，只提供 gzip；正常路径已在选项校验时拒绝。
        AxiosCompression.Deflate or AxiosCompression.Brotli => throw new PlatformNotSupportedException(
            "netstandard2.0 兼容资产只支持 gzip 请求体压缩：该运行时没有 ZLibStream / BrotliStream。"),
#else
        AxiosCompression.Deflate => new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true),
        AxiosCompression.Brotli => new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true),
#endif
        _ => throw new ArgumentOutOfRangeException(nameof(compression), compression, "未定义的请求体压缩算法。")
    };
}
