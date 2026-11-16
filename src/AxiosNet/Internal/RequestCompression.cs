using System.IO.Compression;

namespace AxiosNet.Internal;

/// <summary>
/// 请求体压缩：只作用于本库按字节缓冲出来的正文（文本 / 二进制 / 表单），速度优先于压缩率
/// （<see cref="CompressionLevel.Fastest"/>：HTTP 传输瓶颈通常在网络，省 CPU 更划算）。
/// </summary>
internal static class Compression
{
    /// <summary>是否开启了压缩。</summary>
    public static bool IsEnabled(AxiosConfig config)
        => config.Compression is { } compression && compression != AxiosCompression.None;

    /// <summary>按 <see cref="AxiosConfig.Compression"/> 压缩并补 <c>Content-Encoding</c>；未开启时原样包装（不复制）。</summary>
    public static HttpContent WrapBytes(byte[] bytes, AxiosConfig config)
    {
        if (config.Compression is not { } compression || compression == AxiosCompression.None)
        {
            return new ByteArrayContent(bytes);
        }

        var content = CompressToContent(bytes, compression);

        // 头与正文由同一次判断得出，调用方不能再手写 Content-Encoding（见 AxiosClient 的校验）。
        content.Headers.ContentEncoding.Add(HeaderValue(compression));
        return content;
    }

    /// <summary>
    /// 压缩成 <see cref="ByteArrayContent"/>：直接引用压缩缓冲区的有效段，不再 <c>ToArray()</c> 复制一份成品
    /// （峰值从「缓冲区 + 成品」两份降到一份）。长正文配压缩时这一份常常就是几十 MB。
    /// </summary>
    private static ByteArrayContent CompressToContent(ReadOnlySpan<byte> bytes, AxiosCompression compression)
    {
        using var output = CompressToStream(bytes, compression);

        // 容量是预留值，Length 才是成品长度；取不到缓冲（MemoryStream 允许）时退回复制。
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

    /// <summary>
    /// 压缩到内存流（调用方负责释放）。容量先给原长一半 —— 压缩后通常更小，小正文变大时由扩容兜底。
    /// </summary>
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
            // 压缩器抛错时缓冲区已经没人引用，就地还掉。
            output.Dispose();
            throw;
        }
    }

    // HTTP 的 deflate 是 zlib（RFC 1950）包装，不是裸 DEFLATE（RFC 1951）：RFC 7230 §4.2.2 明确指向 RFC 1950，
    // axios(JS) 侧也走 zlib.deflate。裸流少了 2 字节头与 Adler-32 尾，Java/Tomcat 与多数网关会直接解不出来。
    private static Stream CreateCompressor(Stream output, AxiosCompression compression) => compression switch
    {
        AxiosCompression.Gzip => new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true),
#if NETSTANDARD2_0
        // ZLibStream / BrotliStream 是 .NET 6+ 类型。不能用裸 DeflateStream 冒充 zlib 包装（见上方注释），
        // 因此兼容资产只提供 gzip。正常路径上 ValidateCompression 已用 ERR_BAD_OPTION_VALUE 提前拒绝，
        // 这里的 PNSE 只是绕过选项校验直接构造正文时的最后兜底。
        AxiosCompression.Deflate or AxiosCompression.Brotli => throw new PlatformNotSupportedException(
            "netstandard2.0 兼容资产只支持 gzip 请求体压缩：该运行时没有 ZLibStream / BrotliStream。"),
#else
        AxiosCompression.Deflate => new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true),
        AxiosCompression.Brotli => new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true),
#endif
        _ => throw new ArgumentOutOfRangeException(nameof(compression), compression, "未定义的请求体压缩算法。")
    };
}
