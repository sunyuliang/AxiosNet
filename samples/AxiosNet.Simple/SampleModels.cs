namespace AxiosNet.Simple;

// 示例 DTO

public sealed record Address(string City, string Zip);

public sealed record User(long Id, string Name, int Age, bool Active, double Score, Address? Address, string[]? Roles);

/// <summary>PascalCase 契约的 DTO：用于演示自定义 JSON 序列化器（Newtonsoft 默认保留属性名）。</summary>
public sealed class ProfileDto
{
    public string UserName { get; set; } = string.Empty;

    public int Age { get; set; }

    public Address? Address { get; set; }

    public string[]? Roles { get; set; }
}
