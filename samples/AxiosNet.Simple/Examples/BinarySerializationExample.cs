using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>
/// 24. 二进制格式：请求与响应全程不经过文本（Protobuf / MessagePack / CBOR / Avro 同理）——
/// 请求对象 → <see cref="IContentSerializer.SerializeToBytes"/> + 自报 Content-Type；
/// 响应 Content-Type 非文本 → 原始字节直达 <see cref="IContentSerializer.Deserialize(ReadOnlyMemory{byte},Type,AxiosConfig)"/>。
/// </summary>
public sealed class BinarySerializationExample : IExample
{
    public int Number => 24;

    public string Title => "二进制格式：字节通道双向（protobuf / MessagePack 同理）";

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

        // 3) 请求方向：对象 → 字节正文，Content-Type 由序列化器自报（无需手动设置），/echo 原样回显
        var echoed = await client.PostAsync<BinaryOrder>("echo", new BinaryOrder
        {
            Id = 9,
            Name = "bob",
            Quantity = 1,
            AmountCents = 500
        });
        Console.WriteLine($"  二进制往返: Id={echoed.Data!.Id}, Name={echoed.Data.Name}, Qty={echoed.Data.Quantity}, Amount={echoed.Data.AmountCents / 100m:0.00}");

        // 4) 调用方显式设置过 Content-Type 时以调用方为准：序列化器照常产出字节正文
        var forced = await client.PostAsync<byte[]>(
            "echo",
            new BinaryOrder { Id = 10, Name = "cat", Quantity = 2, AmountCents = 900 },
            c => c.ContentType = "application/x-custom-binary");
        Console.WriteLine($"  显式 Content-Type 覆盖自报: {forced.Data!.Length} 字节, 回显头={forced.GetHeader("Content-Type")}");

        Console.WriteLine("  提示：文本序列化器两个字节入口都不必实现 —— 请求出口默认 null（走文本），");
        Console.WriteLine("        响应入口默认按响应字符集解码转调文本入口，错标成 octet-stream 的 JSON 服务端也能照常工作。");
    }
}
