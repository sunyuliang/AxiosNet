using AxiosNet.Metadata;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Architecture;

/// <summary>
/// 内核中性扩展点的对抗性证明：格式化读取器由调用方注册、内核只读；预格式化文本由调用方标记、内核不再套钩子。
/// 这两条都不依赖 WebAPI——删掉 WebAPI 后内核仍能表达同样的能力。
/// </summary>
public sealed class CoreExtensionPointTests
{
    private sealed class ShapeProbe
    {
        public DateTime When { get; set; }
    }

    [Fact]
    public async Task ValueFormatReader_RegisteredReader_AppliesToKernelShape()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        // 用内核自有注册点临时叠加读取器：只对本探测类型的成员生效，其余成员委托给原读取器，
        // 避免并行运行的其他格式化测试看到被清空的注册点。没有涉及任何 WebAPI 类型。
        var previous = ValueFormatReader.Current;
        ValueFormatReader.Register(property =>
            property.DeclaringType == typeof(ShapeProbe) && property.Name == nameof(ShapeProbe.When)
                ? ValueFormat.FromFormat("yyyy-MM")
                : previous?.Invoke(property));

        try
        {
            await client.GetAsync("x", config => config.Params = new ShapeProbe { When = new DateTime(2024, 3, 4, 5, 6, 7) });

            var query = Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty);
            Assert.Equal("When=2024-03", query);
        }
        finally
        {
            // 恢复原读取器（模块初始化器注册的那一份），避免污染同进程的其他测试。
            ValueFormatReader.Register(previous);
        }
    }

    [Fact]
    public async Task PreformattedValue_BypassesFormattingHook_ForQueryAndScalarBody()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        await client.GetAsync("x", config =>
        {
            config.Params = new Dictionary<string, object?> { ["v"] = new PreformattedValue("raw") };
            // 钩子若被套到预格式化值上，查询会变成 hooked，测试立刻红。
            config.Serialization.ValueText = (_, _) => "hooked";
        });

        Assert.Equal("v=raw", Assert.Single(server.Received).Query);

        await client.PostAsync("y", new PreformattedValue("{\"raw\":true}"), config =>
            config.Serialization.ValueText = (_, _) => "hooked");

        var body = System.Text.Encoding.UTF8.GetString(server.Received[^1].Body);
        Assert.Contains("\"raw\":true", body);
    }
}
