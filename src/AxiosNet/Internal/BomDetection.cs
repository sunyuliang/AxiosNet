namespace AxiosNet.Internal;

/// <summary>UTF-8 BOM（<c>EF BB BF</c>）的唯一字节判定：各读取 / 分帧路径共用，避免同一 3 字节序列在多处手写。</summary>
internal static class BomDetection
{
    /// <summary><paramref name="bytes"/> 开头是否为 UTF-8 BOM；纯字节比较，不分配。</summary>
    public static bool HasUtf8Bom(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
}
