using System.Text.Json.Serialization;

namespace AxiosNet.Comparison.Models;

/// <summary>
/// 所有实现共用的返回模型。显式标注 <see cref="JsonPropertyNameAttribute"/>：
/// 各库默认的 System.Text.Json 选项不完全一致（Web 默认大小写不敏感、裸 JsonSerializer 默认敏感），
/// 统一按属性名绑定，避免「某个库解析失败」被当成性能差异。
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

/// <summary>回环服务器返回的固定正文：与 <see cref="User"/> 逐字段对应。</summary>
internal static class Payloads
{
    /// <summary>预编码好的正文字节：所有实现发同一串字节，序列化差异不会被算进网络与解析成本。</summary>
    public static ReadOnlySpan<byte> UserJson =>
        """{"id":1,"name":"axios-net","age":30,"role":"admin"}"""u8;

    public const string Route = "api/users/1";
}
