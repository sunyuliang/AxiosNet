namespace AxiosNet;

/// <summary>
/// 对应 axios config.responseType：json 反序列化（默认）、text 转 string、arraybuffer 转 byte[]、
/// stream 转 Stream、dynamic 展开为 <see cref="AxiosDynamic"/> 图。
/// </summary>
public enum ResponseType
{
    /// <summary>JSON 反序列化（默认）：非泛型调用得到 <c>JsonElement</c>，泛型调用按 T 反序列化。</summary>
    Json,

    /// <summary>正文按文本解码（编码见 <see cref="AxiosConfig.SerializationSettings.ResponseEncoding"/>），Data 为 <c>string</c>。</summary>
    Text,

    /// <summary>正文读成字节数组，Data 为 <c>byte[]</c>（下载 / 二进制载荷用）。</summary>
    ArrayBuffer,

    /// <summary>正文不缓冲、以 <see cref="Stream"/> 交出；正文读取延续本次尝试的 <see cref="AxiosConfig.Timeout"/> 预算。</summary>
    Stream,

    /// <summary>正文展开为动态对象图，Data 为 <see cref="AxiosDynamic"/>（节点）或 <c>List&lt;object?&gt;</c>（数组）。</summary>
    Dynamic
}
