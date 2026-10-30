namespace AxiosNet;

/// <summary>
/// 请求体压缩算法（对应请求头的 <c>Content-Encoding</c>），见 <see cref="AxiosConfig.RequestCompression"/>。
/// </summary>
public enum AxiosCompression
{
    /// <summary>不压缩（用于按请求覆盖实例默认的压缩设置）。</summary>
    None = 0,

    /// <summary>gzip。</summary>
    Gzip,

    /// <summary>deflate（zlib）。</summary>
    Deflate,

    /// <summary>br（Brotli）。</summary>
    Brotli
}
