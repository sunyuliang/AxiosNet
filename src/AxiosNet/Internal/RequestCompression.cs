using System.IO.Compression;

namespace AxiosNet.Internal;

/// <summary>
/// 请求体压缩：只作用于本库按字节缓冲出来的正文（文本 / 二进制 / 表单），速度优先于压缩率
/// （<see cref="CompressionLevel.Fastest"/>：HTTP 传输瓶颈通常在网络，省 CPU 更划算）。
/// </summary>
internal static class RequestCompression
{
    /// <summary>是否开启了压缩。</summary>
    public static bool IsEnabled(AxiosConfig config)
        => config.RequestCompression is { } compression && compression != AxiosCompression.None;

    /// <summary>按 <see cref="AxiosConfig.RequestCompression"/> 压缩并补 <c>Content-Encoding</c>；未开启时原样包装（不复制）。</summary>
    public static HttpContent WrapBytes(byte[] bytes, AxiosConfig config)
    {
        if (config.RequestCompression is not { } compression || compression == AxiosCompression.None)
        {
            return new ByteArrayContent(bytes);
        }

        var content = new ByteArrayContent(Compress(bytes, compression));

        // 头与正文由同一次判断得出，调用方不能再手写 Content-Encoding（见 AxiosClient 的校验）。
        content.Headers.ContentEncoding.Add(HeaderValue(compression));
        return content;
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
        // 压缩后通常更小，但小正文可能变大：容量给原长一半即可，超出由扩容兜底。
        using var output = new MemoryStream(Math.Max(64, bytes.Length / 2));

        using (var compressor = CreateCompressor(output, compression))
        {
            compressor.Write(bytes);
        }

        return output.ToArray();
    }

    private static Stream CreateCompressor(Stream output, AxiosCompression compression) => compression switch
    {
        AxiosCompression.Gzip => new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true),
        AxiosCompression.Deflate => new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true),
        AxiosCompression.Brotli => new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true),
        _ => throw new ArgumentOutOfRangeException(nameof(compression), compression, "未定义的请求体压缩算法。")
    };
}
