namespace AxiosNet;

/// <summary>
/// 对应 axios config.responseType。
/// json -&gt; 反序列化(默认)，text -&gt; string，arraybuffer -&gt; byte[]，stream -&gt; Stream。
/// dynamic -&gt; 展开为 <see cref="AxiosDynamic"/> 图（支持成员/索引访问、缺省成员返回 null、运行期增删属性，
/// 对齐 JS 中直接访问 response.data.xxx）。
/// </summary>
public enum ResponseType
{
    Json,
    Text,
    ArrayBuffer,
    Stream,
    Dynamic
}
