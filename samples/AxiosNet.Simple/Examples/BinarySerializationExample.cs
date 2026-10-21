using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>
/// 24. 二进制格式：响应 Content-Type 非文本时，库<b>不解码</b>，原始字节直达序列化器的字节入口
/// （Protobuf / MessagePack / CBOR / Avro 同理）；请求方向的二进制体由调用方自己序列化。
/// </summary>
public sealed class BinarySerializationExample : IExample
{
    public int Number => 24;

    public string Title => "二进制格式：字节入口（protobuf / MessagePack 同理）";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        using var client = Axios.Create(c =>
        {
            c.BaseAddress = context.BaseUrl;
            c.ContentSerializer = new OrderBinarySerializer();
        });

        // 1) 强类型响应：application/octet-stream → 字节入口，全程不经过 UTF-8 解码
        var order = await client.GetAsync<BinaryOrder>("orders/1/binary");
        var data = order.Data!;
        Console.WriteLine($"  字节入口解析: Id={data.Id}, Name={data.Name}, Qty={data.Quantity}, Amount={data.AmountCents / 100m:0.00}");

        // 2) 同一响应，非泛型调用：框架自己判断形态 —— 二进制给原始字节，不会猜成文本
        var raw = await client.GetAsync("orders/1/binary");
        var bytes = (byte[])raw.Data!;
        Console.WriteLine($"  非泛型响应: {bytes.Length} 字节, 首字节 0x{bytes[0]:X2}, Content-Type={raw.GetHeader("Content-Type")}");

        // 3) 显式 byte[]：同样零解码（对比：文本响应才需要解码成字符串）
        var explicitBytes = await client.GetAsync<byte[]>("orders/1/binary");
        Console.WriteLine($"  显式 byte[]: {explicitBytes.Data!.Length} 字节");

        // 4) 请求方向：二进制体由调用方自己序列化，库只套媒体类型原样发送（/echo 原样回显）
        var payload = OrderBinarySerializer.Write(new BinaryOrder
        {
            Id = 9,
            Name = "bob",
            Quantity = 1,
            AmountCents = 500
        });

        var echoed = await client.PostAsync<byte[]>(
            "echo",
            payload,
            c => c.ContentType = AxiosContentType.OctetStream);

        Console.WriteLine($"  二进制请求体回显: {echoed.Data!.Length} 字节（与发送一致: {echoed.Data.AsSpan().SequenceEqual(payload)}）");

        Console.WriteLine("  提示：文本序列化器不必实现字节入口 —— 接口里默认按 UTF-8 解码转调文本入口，");
        Console.WriteLine("        所以把 JSON 错标成 octet-stream 的服务端也能照常工作。");
    }
}
