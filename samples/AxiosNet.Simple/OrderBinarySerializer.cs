using System.Buffers.Binary;
using System.Text;
using AxiosNet.Serialization;

namespace AxiosNet.Simple;

/// <summary>
/// 24 号示例用的极简二进制格式序列化器（真实项目里换成 Protobuf / MessagePack / CBOR）。
/// <para>
/// 线格式（小端）：<c>[int32 Id][uint8 NameLen][Name(UTF-8)][int32 Quantity][int32 AmountCents]</c>。
/// 这里不引第三方依赖，只为演示 <see cref="IContentSerializer"/> 的<b>字节入口</b>：
/// 响应 Content-Type 非文本时，库不做任何解码，原始字节直达 <see cref="Deserialize(ReadOnlyMemory{byte},Type,AxiosConfig)"/>。
/// </para>
/// </summary>
public sealed class OrderBinarySerializer : IContentSerializer
{
    /// <summary>请求方向的二进制体由调用方自己序列化成 byte[] 后作为 data 传入，本方法不会被调用。</summary>
    public string Serialize(object? value, Type type, AxiosConfig config)
        => throw new NotSupportedException("二进制请求体请调用方自行序列化成 byte[] 后作为 data 传入（库原样发送）。");

    /// <summary>二进制格式没有文本形态，文本入口不该被触达（Content-Type 声明非文本时库走字节入口）。</summary>
    public object? Deserialize(string text, Type type, AxiosConfig config)
        => throw new NotSupportedException("二进制格式不支持文本入口。");

    public object? Deserialize(ReadOnlyMemory<byte> data, Type type, AxiosConfig config)
        => Parse(data.Span);

    /// <summary>编码（服务端与「请求方向」示例共用）。</summary>
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
