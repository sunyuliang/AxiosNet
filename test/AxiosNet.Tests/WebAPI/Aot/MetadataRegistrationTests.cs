using System.Text.Json.Serialization;
using AxiosNet.Internal;
using AxiosNet.Metadata;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI.Internal;
using AxiosNet.WebAPI.Metadata;

namespace AxiosNet.Tests.WebAPI.Aot;

/// <summary>
/// 生成元数据注册表的新 API：形状 / 映射访问器 / 接口声明都能注册、查回，并保持「重名抛错、配置错误延迟到首次使用」。
/// 注册表是进程级状态，这里与其它改全局状态的用例同集合串行。
/// </summary>
[Collection(GlobalSerializationCollection.Name)]
public sealed class MetadataRegistrationTests
{
    static MetadataRegistrationTests()
    {
        // 反射兜底形状：[JsonPropertyName] 决定字段名，[JsonIgnore] 决定排除。
        AxiosMetadata.RegisterShape<ShapeSample>();

        // 生成形状（手工模拟源生成器写入的委托与字面量）。
        AxiosMetadata.RegisterShape(typeof(BakedShapeSample), static builder => builder
            .Member(nameof(BakedShapeSample.Id), typeof(int), static target => ((BakedShapeSample)target).Id)
                .FieldName("id")
            .Member(nameof(BakedShapeSample.Secret), typeof(string), static target => ((BakedShapeSample)target).Secret)
                .Ignore());

        // 配置期抛错的形状：注册不抛，错误随形状进入首次展平。
        AxiosMetadata.RegisterShape(typeof(BrokenShapeSample), static _ => throw new InvalidOperationException("形状声明损坏"));

        // 配置期抛错的请求模型：注册不抛，错误随声明进入首次构建。
        AxiosApiMetadata.RegisterRequestModel(typeof(BrokenRequestModelSample), static _ => throw new InvalidOperationException("请求模型声明损坏"));

        // 自定义只读字典的枚举访问器。
        AxiosMetadata.RegisterMap(typeof(ReadOnlyMapSample), static target => ((ReadOnlyMapSample)target).Enumerate());

        AxiosApiMetadata.RegisterInterface(
            typeof(IRegisteredApi),
            static _ => throw new NotSupportedException("代理工厂不属于本用例的测试范围"),
            static builder => builder
                .StaticHeaders("X-Api: v1")
                .Method(nameof(IRegisteredApi.GetAsync), HttpMethod.Get, "items/{id}")
                    .Parameter(0, "id", typeof(int))
                        .Path("id")
                    .ReturnsData(typeof(object)));

        // 方法声明缺路径绑定：注册不抛，Validate 时一次性暴露。
        AxiosApiMetadata.RegisterInterface(
            typeof(IBrokenApi),
            static _ => throw new NotSupportedException("代理工厂不属于本用例的测试范围"),
            static builder => builder
                .Method(nameof(IBrokenApi.GetAsync), HttpMethod.Get, "items/{id}")
                    .ReturnsData(typeof(object)));
    }

    [Fact]
    public void ReflectionShape_UsesJsonPropertyNameAndJsonIgnore()
    {
        Assert.True(ShapeRegistry.TryGetShape(typeof(ShapeSample), out var shape));
        Assert.NotNull(shape);
        Assert.Null(shape!.Error);
        Assert.Equal(["Name", "FullName", "Secret"], shape.Members.Select(static member => member.Name));
        Assert.Equal("full", shape.Members[0].FieldName);
        Assert.Equal("full_name", shape.Members[1].FieldName);
        Assert.True(shape.Members[2].Ignored);
    }

    [Fact]
    public void GeneratedShape_KeepsBakedFieldNamesAndIgnores()
    {
        Assert.True(ShapeRegistry.TryGetShape(typeof(BakedShapeSample), out var shape));
        Assert.NotNull(shape);
        Assert.Equal("id", shape!.Members[0].FieldName);
        Assert.Equal(42, shape.Members[0].GetValue(new BakedShapeSample { Id = 42 }));
        Assert.True(shape.Members[1].Ignored);
    }

    [Fact]
    public void ShapeConfigurationError_IsDeferredToRegistryLookup()
    {
        Assert.True(ShapeRegistry.TryGetShape(typeof(BrokenShapeSample), out var shape));
        Assert.Equal("形状声明损坏", shape!.Error);
        Assert.Empty(shape.Members);
    }

    [Fact]
    public void RequestModelConfigurationError_IsDeferredToFirstBuild()
    {
        var error = Assert.Throws<InvalidOperationException>(() => RequestModelMetadata.Get(typeof(BrokenRequestModelSample)));

        Assert.Equal("请求模型声明损坏", error.Message);
    }

    [Fact]
    public void DuplicateShapeRegistration_Throws()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => AxiosMetadata.RegisterShape<ShapeSample>());

        Assert.Contains("对象形状已注册", error.Message);
    }

    [Fact]
    public void MapAccessor_IsRegisteredAndEnumeratesPairs()
    {
        Assert.True(ShapeRegistry.TryGetMap(typeof(ReadOnlyMapSample), out var accessor));
        var pairs = accessor!.Enumerate(new ReadOnlyMapSample()).ToArray();

        Assert.Equal(["a", "b"], pairs.Select(static pair => pair.Key));
        Assert.Equal([1, 2], pairs.Select(static pair => pair.Value));
    }

    [Fact]
    public void DuplicateMapRegistration_Throws()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => AxiosMetadata.RegisterMap(typeof(ReadOnlyMapSample), static _ => []));

        Assert.Contains("映射访问器已注册", error.Message);
    }

    [Fact]
    public void RegisteredInterface_DeclarationsBuildWithoutProxyFactory()
    {
        Assert.True(GeneratedMetadataRegistry.TryGetInterface(typeof(IRegisteredApi), out var entry));
        Assert.NotNull(entry);

        var methods = entry!.GetMethods();
        Assert.Single(methods);
        methods[0].EnsureValid();
    }

    [Fact]
    public void BrokenInterfaceMethod_ErrorSurfacesOnValidate()
    {
        Assert.True(GeneratedMetadataRegistry.TryGetInterface(typeof(IBrokenApi), out var entry));
        Assert.NotNull(entry);

        var error = Assert.Throws<InvalidOperationException>(entry!.Validate);
        Assert.Contains("{id}", error.Message);
        Assert.Contains("没有", error.Message);
    }
}

public sealed class ShapeSample
{
    [JsonPropertyName("full")]
    public string Name { get; set; } = "";

    [JsonPropertyName("full_name")]
    public string? FullName { get; set; }

    [JsonIgnore]
    public string? Secret { get; set; }
}

public sealed class BakedShapeSample
{
    public int Id { get; set; }

    public string? Secret { get; set; }
}

public sealed class BrokenShapeSample
{
}

public sealed class BrokenRequestModelSample
{
}

public sealed class ReadOnlyMapSample
{
    public IEnumerable<KeyValuePair<string, object?>> Enumerate()
    {
        yield return new("a", 1);
        yield return new("b", 2);
    }
}

public interface IRegisteredApi
{
    Task<object> GetAsync(int id);
}

public interface IBrokenApi
{
    Task<object> GetAsync(int id);
}
