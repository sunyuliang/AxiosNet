using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Internal;

namespace AxiosNet.Tests.WebAPI;

/// <summary>
/// 元数据构建层（<see cref="InterfaceMethodMetadata"/>）的二次校验。
/// <para>
/// 同一批规则在 <see cref="InterfaceDeclaration"/> 里已经报过一次；这里是「万一声明校验被绕过」的兜底，
/// 所以只能直接驱动构建入口才能压到——走 <c>Create&lt;T&gt;()</c> 永远先在声明校验那层被拦下。
/// </para>
/// </summary>
public sealed class InterfaceMetadataTests
{
    [Fact]
    public void Get_MultipleRoutes_ReportsContractError()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => GetMetadata<IMultiRouteApi>());

        Assert.Contains("必须且只能标注一个路由特性", exception.Message);
    }

    [Fact]
    public void Get_RepeatedContractError_IsCachedAndReportedAgain()
    {
        // 同一条失败元数据会被缓存：第二次调用报同样的错，不会重新构建
        Assert.Throws<InvalidOperationException>(() => GetMetadata<IMultiRouteApi>());
        Assert.Throws<InvalidOperationException>(() => GetMetadata<IMultiRouteApi>());
    }

    [Fact]
    public void Get_MultipleCancellationTokens_ReportsContractError()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => GetMetadata<IDoubleCancellationTokenApi>());

        Assert.Contains("多个 CancellationToken", exception.Message);
    }

    [Fact]
    public void Get_ApiIgnoreWithBinding_ReportsAmbiguity()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => GetMetadata<IIgnoredBoundParameterApi>());

        Assert.Contains("[ApiIgnore]", exception.Message);
        Assert.Contains("二义性", exception.Message);
    }

    [Fact]
    public void Get_NonResponseReturnType_ReportsContractError()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => GetMetadata<IWrongReturnApi>());

        Assert.Contains("必须返回 Task<AxiosResponse<T>>", exception.Message);
    }

    [Fact]
    public void Get_StreamOnPlainPayload_ReportsContractError()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => GetMetadata<IStreamOnPlainPayloadApi>());

        Assert.Contains("IAsyncEnumerable", exception.Message);
    }

    [Fact]
    public void Get_UndefinedStreamFormat_ReportsContractError()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => GetMetadata<IUndefinedStreamFormatApi>());

        Assert.Contains("帧格式值 999 无效", exception.Message);
    }

    [Fact]
    public void Validate_DiamondInterfaceChain_IsDeduplicated()
    {
        // 菱形继承：基接口会从两条路径各被压栈一次，链路必须去重
        Assert.Empty(ApiClient.ValidateInterfaces(typeof(IDiamondApi)));
    }

    private static InterfaceMethodMetadata GetMetadata<T>()
    {
        var method = typeof(T).GetMethods().Single();

        return InterfaceMethodMetadata.Get(typeof(T), method);
    }
}

/// <summary>菱形继承的最底层接口。</summary>
public interface IDiamondRootApi
{
}

/// <summary>左分支。</summary>
public interface IDiamondLeftApi : IDiamondRootApi
{
}

/// <summary>右分支。</summary>
public interface IDiamondRightApi : IDiamondRootApi
{
}

/// <summary>同时继承左右两个分支：基接口在链路上会重复出现。</summary>
public interface IDiamondApi : IDiamondLeftApi, IDiamondRightApi
{
    [Get("items")]
    Task<AxiosResponse<Person>> GetAsync([Query] int page);
}
