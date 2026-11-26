namespace AxiosNet.Internal.Serialization;

/// <summary>一帧的载荷：通常是原始字节，JSON 数组声明非 UTF-8 时是文本。</summary>
internal readonly struct FramePayload
{
    private readonly string? _text;
    private readonly ReadOnlyMemory<byte> _bytes;

    private FramePayload(string? text, ReadOnlyMemory<byte> bytes)
    {
        _text = text;
        _bytes = bytes;
    }

    /// <summary>文本入口：仅用于声明非 UTF-8 的 JSON 数组。</summary>
    /// <param name="text">已解码的正文。</param>
    public static FramePayload FromText(string text) => new(text, default);

    /// <summary>字节入口：默认路径。</summary>
    /// <param name="bytes">原始字节区间。</param>
    public static FramePayload FromBytes(ReadOnlyMemory<byte> bytes) => new(null, bytes);

    /// <summary>文本形态的正文；字节形态时为 <c>null</c>。</summary>
    public string? Text => _text;

    /// <summary>字节形态的正文；文本形态时为空区间。</summary>
    public ReadOnlyMemory<byte> Bytes => _bytes;

    /// <summary>正文长度（文本按字符数，字节按字节数）。</summary>
    public int Length => _text?.Length ?? _bytes.Length;
}
