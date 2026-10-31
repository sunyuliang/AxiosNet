using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxiosNet.Internal;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI.Internal;

namespace AxiosNet.Tests.Serialization;

/// <summary>
/// System.Text.Json 的 AOT 路径（net8.0）：配置了 <c>TypeInfoResolver</c> 时显式走 <c>GetTypeInfo</c>，
/// 缺类型元数据时给出可读异常；未配置时保持旧的反射语义。
/// </summary>
[Collection(GlobalSerializationCollection.Name)]
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

        // 上下文里也注册了 object：祖先回退必须在 object 前停住，否则错误会埋进 STJ（按未注册的运行时类型报错）。
        var error = Assert.Throws<NotSupportedException>(
            () => SystemTextJsonSerializer.Instance.Serialize(new AotUnregistered("x"), typeof(AotUnregistered), config));

        Assert.Contains(nameof(AotUnregistered), error.Message);
        Assert.Contains("JsonSerializable", error.Message);

        // object 自身仍正常：显式声明 object 的序列化不受影响。
        Assert.Equal("null", SystemTextJsonSerializer.Instance.Serialize(null, typeof(object), config));
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

    [Fact]
    public void MissingTypeInContext_DeserializesThroughNearestRegisteredAncestor()
    {
        var config = ContextConfig();

        var data = Assert.IsType<AotBasePerson>(
            SystemTextJsonSerializer.Instance.Deserialize("{\"name\":\"d\",\"age\":3}", typeof(AotDerivedPerson), config));

        Assert.Equal("d", data.Name);
        Assert.Equal(3, data.Age);
    }

    [Fact]
    public void AnonymousType_ReportsNamedTypeHint()
    {
        var config = ContextConfig();
        var anonymous = new { Name = "n", Age = 1 };

        var error = Assert.Throws<NotSupportedException>(
            () => SystemTextJsonSerializer.Instance.Serialize(anonymous, anonymous.GetType(), config));

        // 匿名类型无法写入 [JsonSerializable]：错误消息要直接给出替代方向。
        Assert.Contains("匿名类型", error.Message);
        Assert.Contains("命名类型", error.Message);
    }

    [Fact]
    public void NullBody_SerializesAsJsonNull_WithoutTypeMetadata()
    {
        var config = ContextConfig();

        // 空请求体与类型无关：缺元数据的类型也该得到 "null"，而不是「缺元数据」异常。
        var json = SystemTextJsonSerializer.Instance.Serialize(null, typeof(AotUnregistered), config);

        Assert.Equal("null", json);
    }

    [Fact]
    public void NullBody_ForNonNullableValueType_StillFails()
    {
        var config = ContextConfig();

        // 快路径不能掩盖非法调用：null 传给非可空值类型由库统一判定（两个目标框架同一种异常）。
        Assert.Throws<ArgumentNullException>(
            () => SystemTextJsonSerializer.Instance.Serialize(null, typeof(int), config));
    }

    [Fact]
    public async Task NdjsonStreaming_DeserializesThroughSourceGeneratedMetadata()
    {
        var frames = new JsonStreamEnumerable<AotPerson>(
            new MemoryStream(Encoding.UTF8.GetBytes("{\"name\":\"a\",\"age\":1}\n{\"name\":\"b\",\"age\":2}\n")),
            ContextConfig(),
            request: null,
            AxiosStreamFormat.NewlineDelimitedJson,
            charSet: null,
            mediaType: "application/x-ndjson");

        RuntimeSupport.ForceNoDynamicCode = true;
        try
        {
            var names = new List<string>();
            await foreach (var person in frames)
            {
                names.Add(person.Name);
            }

            Assert.Equal(["a", "b"], names);
        }
        finally
        {
            RuntimeSupport.ForceNoDynamicCode = false;
        }
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
[JsonSerializable(typeof(object))]
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
