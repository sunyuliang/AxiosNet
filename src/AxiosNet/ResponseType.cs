namespace AxiosNet;

/// <summary>
/// 对应 axios config.responseType。
/// json -&gt; 反序列化(默认)，text -&gt; string，arraybuffer -&gt; byte[]，stream -&gt; Stream。
/// dynamic -&gt; 展开为支持动态成员访问的 ExpandoObject 图（类似 JS 中直接访问 response.data.xxx）。
/// </summary>
public enum ResponseType
{
    Json,
    Text,
    ArrayBuffer,
    Stream,
    Dynamic
}
