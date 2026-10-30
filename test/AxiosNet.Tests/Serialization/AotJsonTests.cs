using System.Text.Json;
using System.Text.Json.Serialization;
using AxiosNet.Serialization;

namespace AxiosNet.Tests.Serialization;

/// <summary>
/// System.Text.Json 的 AOT 路径（net8.0）：配置了 <c>TypeInfoResolver</c> 时显式走 <c>GetTypeInfo</c>，
/// 缺类型元数据时给出可读异常；未配置时保持旧的反射语义。
/// </summary>
public sealed class AotJsonTests
{
#if NET8_0_OR_GREATER
    private static AxiosConfig ContextConfig()
        => new()
        {
            Serialization = new()
            {
                JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    TypeInfoResolver = AotJsonContext.Default
                }
            }
        };

    [Fact]
    public void ContextResolver_SerializesAndDeserializes_ThroughTypeInfo()
    {
        var config = ContextConfig();
        var serializer = SystemTextJsonSerializer.Instance;
        var value = new AotPerson { Name = "n", Age = 7 };

        var json = serializer.Serialize(value, typeof(AotPerson), config);

        Assert.Equal("{\"name\":\"n\",\"age\":7}", json);
        Assert.Equal(value, serializer.Deserialize(json, typeof(AotPerson), config));
    }

    [Fact]
    public void ContextResolver_Utf8Entry_MatchesTextEntry()
    {
        var config = ContextConfig();
        var serializer = SystemTextJsonSerializer.Instance;
        var bytes = System.Text.Encoding.UTF8.GetBytes("{\"name\":\"n\",\"age\":7}");

        var data = Assert.IsType<AotPerson>(serializer.Deserialize(bytes, typeof(AotPerson), config));

        Assert.Equal(new AotPerson { Name = "n", Age = 7 }, data);
    }

    [Fact]
    public void MissingTypeInContext_ThrowsReadableMetadataError()
    {
        var config = ContextConfig();

        var error = Assert.Throws<NotSupportedException>(
            () => SystemTextJsonSerializer.Instance.Serialize(new AotUnregistered("x"), typeof(AotUnregistered), config));

        Assert.Contains(nameof(AotUnregistered), error.Message);
        Assert.Contains("JsonSerializable", error.Message);
    }

    [Fact]
    public void MissingTypeInContext_FallsBackToNearestRegisteredAncestor()
    {
        var config = ContextConfig();

        var json = SystemTextJsonSerializer.Instance.Serialize(
            new AotDerivedPerson { Name = "d", Age = 3, Extra = "ignored" },
            typeof(AotDerivedPerson),
            config);

        // 只有祖先类型的元数据：按祖先序列化，派生成员不出现。
        Assert.Equal("{\"name\":\"d\",\"age\":3}", json);
    }
#endif

    [Fact]
    public void NoResolver_StillUsesReflectionInJit()
    {
        var config = new AxiosConfig
        {
            Serialization = new()
            {
                JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            }
        };

        var json = SystemTextJsonSerializer.Instance.Serialize(
            new AotPerson { Name = "n", Age = 7 },
            typeof(AotPerson),
            config);

        Assert.Contains("\"name\":\"n\"", json);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AotPerson))]
[JsonSerializable(typeof(AotBasePerson))]
internal partial class AotJsonContext : JsonSerializerContext;

public sealed record AotPerson
{
    public string Name { get; set; } = string.Empty;

    public int Age { get; set; }
}

public sealed record AotUnregistered(string Value);

public class AotBasePerson
{
    public string? Name { get; set; }

    public int Age { get; set; }
}

public sealed class AotDerivedPerson : AotBasePerson
{
    public string? Extra { get; set; }
}
