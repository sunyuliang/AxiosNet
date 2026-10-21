using System.Xml.Serialization;

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

/// <summary>
/// XML 契约的 DTO：XmlSerializer 要求 public 类型 + 无参构造 + 可读写属性；
/// 根元素名用 <see cref="XmlRootAttribute"/> 与服务的 &lt;User&gt; 对齐。
/// </summary>
[XmlRoot("User")]
public sealed class XmlUser
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Age { get; set; }
}

/// <summary>
/// 二进制契约的 DTO（线格式见 <see cref="OrderBinarySerializer"/>）：
/// 真实项目里换成 protobuf / MessagePack 生成的类型即可，这里手写只为演示「字节入口」。
/// </summary>
public sealed class BinaryOrder
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Quantity { get; set; }

    public int AmountCents { get; set; }
}
