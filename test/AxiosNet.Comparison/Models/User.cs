using System.Text.Json.Serialization;

namespace AxiosNet.Comparison.Models;

/// <summary>
/// 所有实现共用的模型：POST 时作为请求体被序列化，响应再反序列化回同一类型。
/// 属性上都标了 <see cref="JsonPropertyNameAttribute"/>，各库因此按同一组名字绑定，不会因为
/// 默认 JsonSerializerOptions 的大小写策略不同被当成解析失败的性能差异。
/// </summary>
internal sealed class User
{
    /// <summary>用户 ID，对应正文的 id 字段。</summary>
    [JsonPropertyName("id")]
    public long Id { get; set; }

    /// <summary>用户名，对应正文的 name 字段。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>年龄（整数），对应正文的 age 字段。</summary>
    [JsonPropertyName("age")]
    public int Age { get; set; }

    /// <summary>角色，对应正文的 role 字段。</summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;
}

/// <summary>回环服务器读写的固定正文，以及与 <see cref="User"/> 对应的请求体实例。</summary>
internal static class Payloads
{
    /// <summary>回环服务器返回的固定正文字节：所有实现收到同一串字节，解析成本的比较基准因此一致。</summary>
    public static ReadOnlySpan<byte> UserJson =>
        """{"id":1,"name":"axios-net","age":30,"role":"admin"}"""u8;

    /// <summary>
    /// POST 请求体：全部实现把同一个实例交给各自库序列化，正文与 <see cref="UserJson"/> 同字段
    /// （裸 socket 基线不给库序列化，直接发 <see cref="UserJson"/>）。共享单个实例，避免把「每请求构造模型」的分配算进测量。
    /// </summary>
    public static readonly User Request = new() { Id = 1, Name = "axios-net", Age = 30, Role = "admin" };

    public const string Route = "api/users/1";
}
