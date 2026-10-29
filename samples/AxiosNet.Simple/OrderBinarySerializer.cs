using System.Buffers.Binary;
using System.Text;
using AxiosNet.Serialization;

namespace AxiosNet.Simple;

/// <summary>
/// 24 号示例用的极简二进制格式序列化器（真实项目里换成 Protobuf / MessagePack / CBOR）。
/// 线格式（小端）：<c>[int32 Id][uint8 NameLen][Name(UTF-8)][int32 Quantity][int32 AmountCents]</c>。
/// 请求方向覆写 <see cref="IContentSerializer.SerializeToBytes"/>（对象 → 字节正文 + 自报媒体类型），
/// 响应方向覆写字节入口，两个方向都不经过文本。
/// </summary>
public sealed class OrderBinarySerializer : IContentSerializer
{
    /// <summary>本格式的媒体类型：请求方向自报为 Content-Type（调用方显式设置过时以调用方为准）。</summary>
    public string? MediaType => AxiosContentType.OctetStream;

    /// <summary>二进制格式没有文本形态，文本入口不该被触达。</summary>
    public string Serialize(object? value, Type type, AxiosConfig config)
        => throw new NotSupportedException("二进制格式不支持文本序列化，请覆写 SerializeToBytes。");

    /// <summary>请求出口：结构化数据 → 线格式字节。返回 null 的类型回落文本入口（本实现里必然抛出）。</summary>
    public ReadOnlyMemory<byte>? SerializeToBytes(object? value, Type type, AxiosConfig config)
        => value is BinaryOrder order ? Write(order) : null;

    /// <summary>二进制格式没有文本形态，文本入口不该被触达（Content-Type 声明非文本时库走字节入口）。</summary>
    public object? Deserialize(string text, Type type, AxiosConfig config)
        => throw new NotSupportedException("二进制格式不支持文本入口。");

    public object? Deserialize(ReadOnlyMemory<byte> data, Type type, AxiosConfig config)
        => Parse(data.Span);

    /// <summary>编码（服务端与「请求方向」共用）。</summary>
    public static byte[] Write(BinaryOrder order)
    {
        var name = Encoding.UTF8.GetBytes(order.Name);
        if (name.Length > byte.MaxValue)
        {
            throw new ArgumentException("示例格式用 1 字节存名字长度。", nameof(order));
        }

        var buffer = new byte[5 + name.Length + 8];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, order.Id);
        buffer[4] = (byte)name.Length;
        name.CopyTo(buffer, 5);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(5 + name.Length), order.Quantity);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(9 + name.Length), order.AmountCents);
        return buffer;
    }

    /// <summary>解码。</summary>
    public static BinaryOrder Parse(ReadOnlySpan<byte> data)
    {
        var nameLength = data[4];
        return new BinaryOrder
        {
            Id = BinaryPrimitives.ReadInt32LittleEndian(data),
            Name = Encoding.UTF8.GetString(data.Slice(5, nameLength)),
            Quantity = BinaryPrimitives.ReadInt32LittleEndian(data[(5 + nameLength)..]),
            AmountCents = BinaryPrimitives.ReadInt32LittleEndian(data[(9 + nameLength)..])
        };
    }
}
