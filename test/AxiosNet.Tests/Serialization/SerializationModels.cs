using System.Xml.Serialization;

namespace AxiosNet.Tests.Serialization;

/// <summary>XML 序列化用的公开类型（<see cref="XmlSerializer"/> 要求 public + 无参构造）。</summary>
public sealed class XmlOrder
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>显式声明根命名空间的类型：内置 XML 序列化器不能把命名空间吞掉。</summary>
[XmlRoot("Order", Namespace = "urn:ax")]
public sealed class NamespacedOrder
{
    public int Id { get; set; }
}

/// <summary>二进制序列化用的公开类型（字段语义见二进制用例中的 payload 布局）。</summary>
public sealed class BinaryOrder
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public byte Tail { get; set; }
}
