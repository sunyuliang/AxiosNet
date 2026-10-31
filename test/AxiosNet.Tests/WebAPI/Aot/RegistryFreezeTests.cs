using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Internal;
using AxiosNet.WebAPI.Metadata;

namespace AxiosNet.Tests.WebAPI.Aot;

/// <summary>
/// 注册表冻结：<see cref="AxiosMetadata.Freeze"/> 之后任何真正写入都抛错，等价重复注册仍是空操作。
/// 冻结是进程级状态，用例必须在 finally 里解冻，否则同进程后续用例全部注册失败。
/// </summary>
[Collection(GlobalSerializationCollection.Name)]
public sealed class RegistryFreezeTests
{
    [Fact]
    public void Freeze_BlocksRequestModelRegistration()
    {
        try
        {
            AxiosMetadata.Freeze();

            var error = Assert.Throws<InvalidOperationException>(
                () => AxiosMetadata.RegisterRequestModel(typeof(FreezeProbeRequest), static builder => builder.Route(HttpMethod.Get, "freeze")));

            Assert.Contains("冻结", error.Message, StringComparison.Ordinal);
            Assert.Contains(typeof(FreezeProbeRequest).FullName!, error.Message, StringComparison.Ordinal);
        }
        finally
        {
            GeneratedMetadataRegistry.UnfreezeForTests();
        }
    }

    [Fact]
    public void Freeze_BlocksEveryRegistrationKind()
    {
        try
        {
            AxiosMetadata.Freeze();

            // 四种注册各用独立类型：若与别的用例复用同一类型，重复注册会先命中「已注册」，
            // 断言仍然过，但测的就不是冻结了。
            Assert.Throws<InvalidOperationException>(
                () => AxiosMetadata.RegisterInterface(typeof(IFreezeProbeOtherApi), static _ => new object(), static _ => { }));
            Assert.Throws<InvalidOperationException>(
                () => AxiosMetadata.RegisterShape(typeof(FreezeProbeOtherShape), static _ => { }));
            Assert.Throws<InvalidOperationException>(
                () => AxiosMetadata.RegisterMap(typeof(FreezeProbeOtherShape), static _ => Array.Empty<KeyValuePair<string, object?>>()));
        }
        finally
        {
            GeneratedMetadataRegistry.UnfreezeForTests();
        }
    }

    [Fact]
    public void Freeze_AllowsEquivalentGeneratedDuplicate()
    {
        // 冻结只禁止写入：两个互不引用的程序集各自生成同一接口的代理时，第二份是空操作，不该炸。
        AxiosMetadata.RegisterGeneratedInterface(typeof(IFreezeProbeApi), static _ => new object(), static _ => { });

        try
        {
            AxiosMetadata.Freeze();
            AxiosMetadata.RegisterGeneratedInterface(typeof(IFreezeProbeApi), static _ => new object(), static _ => { });
        }
        finally
        {
            GeneratedMetadataRegistry.UnfreezeForTests();
        }
    }

    [Fact]
    public void Freeze_IsIdempotent_AndLeavesReadsUnchanged()
    {
        try
        {
            AxiosMetadata.Freeze();
            AxiosMetadata.Freeze();

            Assert.True(GeneratedMetadataRegistry.IsFrozen);
            Assert.False(GeneratedMetadataRegistry.TryGetRequestModel(typeof(FreezeProbeRequest), out _));
        }
        finally
        {
            GeneratedMetadataRegistry.UnfreezeForTests();
        }

        Assert.False(GeneratedMetadataRegistry.IsFrozen);
    }
}

public sealed class FreezeProbeRequest
{
    public int Id { get; set; }
}

public sealed class FreezeProbeShape
{
    public string Name { get; set; } = string.Empty;
}

public sealed class FreezeProbeOtherShape
{
    public string Name { get; set; } = string.Empty;
}

public interface IFreezeProbeApi
{
    [Get("freeze")]
    Task<AxiosResponse> PingAsync();
}

public interface IFreezeProbeOtherApi
{
    [Get("freeze-other")]
    Task<AxiosResponse> PingAsync();
}
