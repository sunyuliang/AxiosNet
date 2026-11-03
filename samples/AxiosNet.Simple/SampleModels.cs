using System.Xml.Serialization;

namespace AxiosNet.Simple;

// 示例 DTO

/// <summary>地址 DTO：演示嵌套对象字段的往返。</summary>
/// <param name="City">城市名。</param>
/// <param name="Zip">邮政编码。</param>
public sealed record Address(string City, string Zip);

/// <summary>用户 DTO：覆盖整数、字符串、布尔、浮点、嵌套对象与数组字段。</summary>
/// <param name="Id">用户 ID。</param>
/// <param name="Name">用户名。</param>
/// <param name="Age">年龄。</param>
/// <param name="Active">是否启用。</param>
/// <param name="Score">评分（浮点，验证数字往返不退化成字符串）。</param>
/// <param name="Address">嵌套地址对象（可空）。</param>
/// <param name="Roles">角色数组（可空）。</param>
public sealed record User(long Id, string Name, int Age, bool Active, double Score, Address? Address, string[]? Roles);

/// <summary>PascalCase 契约的 DTO：用于演示自定义 JSON 序列化器（Newtonsoft 默认保留属性名）。</summary>
public sealed class ProfileDto
{
    /// <summary>用户名：PascalCase 属性名，用来验证自定义序列化器不改名也能对上契约。</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>年龄。</summary>
    public int Age { get; set; }

    /// <summary>嵌套地址对象，与 <see cref="Address"/> 同形。</summary>
    public Address? Address { get; set; }

    /// <summary>角色数组（可空），演示数组字段的往返。</summary>
    public string[]? Roles { get; set; }
}

/// <summary>
/// XML 契约的 DTO：XmlSerializer 要求 public 类型 + 无参构造 + 可读写属性；
/// 根元素名用 <see cref="XmlRootAttribute"/> 与服务的 &lt;User&gt; 对齐。
/// </summary>
[XmlRoot("User")]
public sealed class XmlUser
{
    /// <summary>用户 ID，对应 XML 里的 Id 元素。</summary>
    public long Id { get; set; }

    /// <summary>用户名，对应 XML 里的 Name 元素。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>年龄，对应 XML 里的 Age 元素。</summary>
    public int Age { get; set; }
}

/// <summary>
/// 二进制契约的 DTO（线格式见 <see cref="OrderBinarySerializer"/>）：
/// 真实项目里换成 protobuf / MessagePack 生成的类型即可，这里手写只为演示「字节入口」。
/// </summary>
public sealed class BinaryOrder
{
    /// <summary>订单 ID（二进制线格式的第一个 int）。</summary>
    public int Id { get; set; }

    /// <summary>商品名（长度前缀 + UTF-8 字节）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>数量。</summary>
    public int Quantity { get; set; }

    /// <summary>金额（分）：线上按整数传，避免浮点误差。</summary>
    public int AmountCents { get; set; }
}
