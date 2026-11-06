using System.Text;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Serialization;

/// <summary>
/// 阶段 B 对抗性护栏：序列化回退链固定为「实例精确登记 → 表内默认 → 进程默认 → 内置 JSON」，
/// 旧的 <c>Serialization.ContentSerializer</c> 不再拥有压过精确登记的旁路。
/// </summary>
public sealed class SerializationFallbackTests
{
    [Fact]
    public async Task LegacyContentSerializer_NoLongerOverridesMediaTypeRegistration()
    {
        var legacy = new MarkerSerializer("legacy", "application/x-legacy");
        var registered = new MarkerSerializer("registered", "application/json");

        using var client = new AxiosClient(new AxiosConfig
        {
            ContentType = "application/json",
            Serialization = { BodySerializers = { Default = legacy } }
        });
        client.Defaults.Serialization.BodySerializers.Register("application/json", registered);

        await using var server = TestHttpServer.Start(_ => new TestResponse(200));

        await client.PostAsync(server.Url + "x", new Person("ada", 36));

        // 新语义：精确登记的应用/json 胜过旧 ContentSerializer 默认项。
        Assert.Equal("registered", Encoding.UTF8.GetString(server.Received[0].Body));
        Assert.Equal(0, legacy.SerializeCalls);
        Assert.Equal(1, registered.SerializeCalls);
    }

    [Fact]
    public async Task RegistryDefault_IsUsedOnlyWhenNoMediaTypeMatches()
    {
        var fallback = new MarkerSerializer("fallback", "application/x-fallback");

        using var client = new AxiosClient(new AxiosConfig
        {
            ContentType = "application/x-unknown",
            Serialization = { BodySerializers = { Default = fallback } }
        });

        await using var server = TestHttpServer.Start(_ => new TestResponse(200));

        await client.PostAsync(server.Url + "x", new Person("ada", 36));

        Assert.Equal("fallback", Encoding.UTF8.GetString(server.Received[0].Body));
    }

    [Fact]
    public async Task TextEncoding_AppliesToRequestAndResponse()
    {
        static string DecodeLatin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

        await using var server = TestHttpServer.Start(request =>
            new TestResponse(200, Body: Encoding.Latin1.GetBytes("café"), ContentType: "text/plain; charset=utf-8"));

        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = { TextEncoding = Encoding.Latin1 }
        });

        var response = await client.PostAsync<string>(server.Url + "x", "café");

        // 请求方向：按 Latin1 写出（é = 0xE9），不是 UTF-8 的两字节。
        var sent = server.Received[0].Body;
        Assert.Equal(new byte[] { 0x63, 0x61, 0x66, 0xE9 }, sent);

        // 响应方向：charset 声明 utf-8，但显式 TextEncoding 应压过它。
        Assert.Equal("café", response.Data);
    }

    private sealed class MarkerSerializer(string marker, string mediaType) : IContentSerializer
    {
        public int SerializeCalls { get; private set; }

        public string? MediaType => mediaType;

        public string Serialize(object? value, Type type, AxiosConfig config)
        {
            SerializeCalls++;
            return marker;
        }

        public object? Deserialize(string text, Type type, AxiosConfig config) => default;
    }

    private sealed record Person(string Name, int Age);
}
