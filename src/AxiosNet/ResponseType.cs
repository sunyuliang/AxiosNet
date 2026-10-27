namespace AxiosNet;

/// <summary>
/// 对应 axios config.responseType：json 反序列化（默认）、text 转 string、arraybuffer 转 byte[]、
/// stream 转 Stream、dynamic 展开为 <see cref="AxiosDynamic"/> 图。
/// </summary>
public enum ResponseType
{
    Json,
    Text,
    ArrayBuffer,
    Stream,
    Dynamic
}
