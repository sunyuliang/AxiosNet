using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Internal;
using AxiosNet.WebAPI.Internal.Reflection;

namespace AxiosNet.Tests.WebAPI;

/// <summary>
/// 接口级声明的启动期校验：`ApiClient.ValidateInterfaces` 的错误聚合、重复类型去重与程序集扫描。
/// 与 `Create{T}()` 共用同一份元数据，所以这里报出的错就是调用时会抛的错。
/// </summary>
public sealed class InterfaceValidationTests
{
    [Fact]
    public void ValidateInterfaces_ValidInterface_ReturnsNoErrors()
    {
        Assert.Empty(ApiClient.ValidateInterfaces(typeof(IWellFormedApi)));
    }

    [Fact]
    public void ValidateInterfaces_SameTypeRepeated_IsValidatedOnce()
    {
        var errors = ApiClient.ValidateInterfaces(typeof(IUnroutedApi), typeof(IUnroutedApi));

        Assert.Single(errors);
    }

    [Fact]
    public void ValidateInterfaces_NullArguments_AreRejectedOrReported()
    {
        Assert.Throws<ArgumentNullException>(() => ApiClient.ValidateInterfaces((Type[])null!));

        // 数组里的 null 元素不抛异常，而是作为一条错误回报：启动期扫描不该被一个坏条目打断
        var errors = ApiClient.ValidateInterfaces(typeof(IWellFormedApi), null!);

        Assert.Single(errors);
        Assert.Contains("null", errors[0]);
    }

    [Fact]
    public void ValidateInterfaces_NonInterfaceType_IsReported()
    {
        Assert.NotEmpty(ApiClient.ValidateInterfaces(typeof(Person)));
    }

    [Fact]
    public void ValidateInterfaces_MethodWithoutRoute_IsReported()
    {
        Assert.NotEmpty(ApiClient.ValidateInterfaces(typeof(IUnroutedApi)));
    }

    [Fact]
    public void ValidateInterfaces_PlaceholderWithoutBinding_IsReported()
    {
        Assert.NotEmpty(ApiClient.ValidateInterfaces(typeof(IUnboundPlaceholderApi)));
    }

    [Fact]
    public void ValidateInterfaces_DuplicatePlaceholder_IsReported()
    {
        Assert.NotEmpty(ApiClient.ValidateInterfaces(typeof(IDuplicatePlaceholderApi)));
    }

    [Fact]
    public void ValidateInterfaces_AssemblyScan_CollectsEveryBrokenInterface()
    {
        Assert.Throws<ArgumentNullException>(() => ApiClient.ValidateInterfaces((System.Reflection.Assembly)null!));

        var errors = ApiClient.ValidateInterfaces(typeof(InterfaceValidationTests).Assembly);

        // 合法接口一条不报，坏接口（无路由 / 占位符未绑定 / 占位符重复）各自有错
        Assert.DoesNotContain(errors, error => error.Contains(nameof(IWellFormedApi)));
        Assert.NotEmpty(errors);
    }

    // ------------------------------------------------------------------
    // 接口形状
    // ------------------------------------------------------------------

    [Fact]
    public void ValidateInterfaces_OpenGenericInterface_IsReportedBeforeAnythingElse()
    {
        var errors = ApiClient.ValidateInterfaces(typeof(IGenericRepo<>));

        Assert.Single(errors);
        Assert.Contains("开放泛型", errors[0]);
    }

    [Fact]
    public void ValidateInterfaces_NonPublicInterface_IsReported()
    {
        var errors = ApiClient.ValidateInterfaces(typeof(IInternalApi));

        Assert.Contains(errors, error => error.Contains("不是 public 接口"));
    }

    [Fact]
    public void ValidateInterfaces_InterfaceWithPropertyOrEvent_IsReported()
    {
        var errors = ApiClient.ValidateInterfaces(typeof(IMemberBearingApi));

        Assert.Contains(errors, error => error.Contains("不能声明属性或事件"));
    }

    [Fact]
    public void ValidateInterfaces_InterfaceWithoutMethods_IsReported()
    {
        var errors = ApiClient.ValidateInterfaces(typeof(IEmptyApi));

        Assert.Single(errors);
        Assert.Contains("没有可声明的方法", errors[0]);
    }

    [Fact]
    public void ValidateInterfaces_GenericMethod_IsReported()
    {
        var errors = ApiClient.ValidateInterfaces(typeof(IGenericMethodApi));

        Assert.Contains(errors, error => error.Contains("泛型方法"));
    }

    [Fact]
    public void ValidateInterfaces_MultipleRouteAttributes_AreReportedWithBothNames()
    {
        var errors = ApiClient.ValidateInterfaces(typeof(IMultiRouteApi));

        Assert.Single(errors);
        Assert.Contains("多个路由特性", errors[0]);
        Assert.Contains("[Get]", errors[0]);
        Assert.Contains("[Post]", errors[0]);
    }

    // ------------------------------------------------------------------
    // [Stream] 的三种误用
    // ------------------------------------------------------------------

    [Fact]
    public void ValidateInterfaces_StreamOnDataReturn_IsReported()
    {
        // Task<string> 本身合法（只交付正文），但 [Stream] 需要 Task<AxiosResponse<IAsyncEnumerable<T>>>。
        var errors = ApiClient.ValidateInterfaces(typeof(IStreamWithoutResponseApi));

        Assert.Contains(errors, error => error.Contains("Task<AxiosResponse<IAsyncEnumerable<T>>>"));
    }

    [Fact]
    public void ValidateInterfaces_StreamOnNonEnumerablePayload_IsReported()
    {
        var errors = ApiClient.ValidateInterfaces(typeof(IStreamOnPlainPayloadApi));

        Assert.Contains(errors, error => error.Contains("IAsyncEnumerable"));
    }

    [Fact]
    public void ValidateInterfaces_UndefinedStreamFormat_IsReported()
    {
        var errors = ApiClient.ValidateInterfaces(typeof(IUndefinedStreamFormatApi));

        Assert.Single(errors);
        Assert.Contains("帧格式值 999 无效", errors[0]);
    }

    [Fact]
    public void ValidateInterfaces_StreamOnStreamingPayload_IsAccepted()
    {
        Assert.Empty(ApiClient.ValidateInterfaces(typeof(IStreamFormatApi)));
    }

    // ------------------------------------------------------------------
    // 参数形状
    // ------------------------------------------------------------------

    [Fact]
    public void ValidateInterfaces_ByRefParameter_IsReported()
    {
        var errors = ApiClient.ValidateInterfaces(typeof(IByRefParameterApi));

        Assert.Contains(errors, error => error.Contains("ref / out / in"));
    }

    [Fact]
    public void ValidateInterfaces_RefStructParameter_IsReported()
    {
        var errors = ApiClient.ValidateInterfaces(typeof(IRefStructParameterApi));

        Assert.Contains(errors, error => error.Contains("ref struct"));
    }

    [Fact]
    public void ValidateInterfaces_CancellationTokenWithBinding_IsReported()
    {
        var errors = ApiClient.ValidateInterfaces(typeof(IBoundCancellationTokenApi));

        Assert.Contains(errors, error => error.Contains("不能同时标注"));
    }

    [Fact]
    public void ValidateInterfaces_MultipleCancellationTokens_AreReported()
    {
        var errors = ApiClient.ValidateInterfaces(typeof(IDoubleCancellationTokenApi));

        Assert.Contains(errors, error => error.Contains("最多一个"));
    }

    [Fact]
    public void ValidateInterfaces_ApiIgnoreWithBinding_IsReportedAsAmbiguous()
    {
        var errors = ApiClient.ValidateInterfaces(typeof(IIgnoredBoundParameterApi));

        Assert.Contains(errors, error => error.Contains("二义性"));
    }

    [Fact]
    public void ValidateInterfaces_ApiIgnoreWithoutBinding_IsAccepted()
    {
        Assert.Empty(ApiClient.ValidateInterfaces(typeof(IIgnoredParameterApi)));
    }

    // ------------------------------------------------------------------
    // 元数据解析：DispatchProxy 交回的方法可能不是接口方法
    // ------------------------------------------------------------------

    [Fact]
    public void ResolveMethod_FallsBackToInterfaceMethod_WhenProxyHandsBackAnImplementationMethod()
    {
        var implementationMethod = typeof(WellFormedApiImplementation).GetMethod(
            nameof(WellFormedApiImplementation.GetAsync),
            new[] { typeof(int), typeof(string), typeof(CancellationToken) })!;

        var resolved = InterfaceDeclaration.ResolveMethod(typeof(IWellFormedApi), implementationMethod);

        Assert.True(resolved.DeclaringType!.IsInterface);
        Assert.Equal(nameof(IWellFormedApi.GetAsync), resolved.Name);
    }

    [Fact]
    public void ResolveMethod_SignatureDiffers_ReturnsOriginalMethod()
    {
        // 同名、参数个数也相同，只有参数类型不同：不能误认成接口方法
        var overload = typeof(WellFormedApiImplementation).GetMethod(
            nameof(WellFormedApiImplementation.GetAsync),
            new[] { typeof(long), typeof(string), typeof(CancellationToken) })!;

        var resolved = InterfaceDeclaration.ResolveMethod(typeof(IWellFormedApi), overload);

        Assert.Same(overload, resolved);
    }

    [Fact]
    public void ResolveMethod_UnknownMethod_ReturnsOriginalMethod()
    {
        var unknown = typeof(WellFormedApiImplementation).GetMethod(nameof(WellFormedApiImplementation.Unrelated))!;

        Assert.Same(unknown, InterfaceDeclaration.ResolveMethod(typeof(IWellFormedApi), unknown));
    }
}

/// <summary>合法接口：每个占位符都有绑定，返回类型是 <see cref="AxiosResponse{T}"/> 家族。</summary>
public interface IWellFormedApi
{
    [Get("items/{id}")]
    Task<AxiosResponse<Person>> GetAsync([Path] int id, [Query] string? keyword = null, CancellationToken cancellationToken = default);
}

/// <summary>方法上没有路由特性。</summary>
public interface IUnroutedApi
{
    Task<AxiosResponse<Person>> GetAsync([Query] int id, CancellationToken cancellationToken = default);
}

/// <summary>模板里有 {id}，但没有任何参数绑定到它。</summary>
public interface IUnboundPlaceholderApi
{
    [Get("items/{id}")]
    Task<AxiosResponse<Person>> GetAsync([Query] int page, CancellationToken cancellationToken = default);
}

/// <summary>同一个占位符出现两次。</summary>
public interface IDuplicatePlaceholderApi
{
    [Get("items/{id}/{id}")]
    Task<AxiosResponse<Person>> GetAsync([Path] int id, CancellationToken cancellationToken = default);
}

// ----------------------------------------------------------------------
// 形状负例：每一条都对应 InterfaceDeclaration 里的一条规则
// ----------------------------------------------------------------------

/// <summary>开放泛型接口：只能使用闭合类型。</summary>
public interface IGenericRepo<T>
{
    [Get("items")]
    Task<AxiosResponse<T>> GetAsync([Query] int page);
}

/// <summary>非 public 接口：DispatchProxy 无法为它生成实现。</summary>
internal interface IInternalApi
{
    [Get("items")]
    Task<AxiosResponse<Person>> GetAsync();
}

/// <summary>声明了属性：接口级声明只支持方法。</summary>
public interface IMemberBearingApi
{
    string? Name { get; }

    [Get("items")]
    Task<AxiosResponse<Person>> GetAsync();
}

/// <summary>没有任何方法。</summary>
public interface IEmptyApi
{
}

/// <summary>泛型方法：接口级声明暂不支持。</summary>
public interface IGenericMethodApi
{
    [Get("items")]
    Task<AxiosResponse<T>> GetAsync<T>();
}

/// <summary>一个方法标了两个路由特性。</summary>
public interface IMultiRouteApi
{
    [Get("items")]
    [Post("items")]
    Task<AxiosResponse<Person>> GetAsync();
}

/// <summary>[Stream] 标在非 Task&lt;AxiosResponse&lt;T&gt;&gt; 的返回类型上。</summary>
public interface IStreamWithoutResponseApi
{
    [Get("stream")]
    [Stream]
    Task<string> ReadAsync();
}

/// <summary>[Stream] 标在非 IAsyncEnumerable 的载荷上。</summary>
public interface IStreamOnPlainPayloadApi
{
    [Get("stream")]
    [Stream]
    Task<AxiosResponse<Person>> ReadAsync();
}

/// <summary>[Stream] 的帧格式落在枚举之外。</summary>
public interface IUndefinedStreamFormatApi
{
    [Get("stream")]
    [Stream((AxiosStreamFormat)999)]
    Task<AxiosResponse<IAsyncEnumerable<Person>>> ReadAsync();
}

/// <summary>合法流式接口（对照上述三种误用）。</summary>
public interface IStreamFormatApi
{
    [Get("stream")]
    [Stream(AxiosStreamFormat.ServerSentEvents)]
    Task<AxiosResponse<IAsyncEnumerable<Person>>> ReadAsync();
}

/// <summary>out 参数：接口级声明不支持按引用传递。</summary>
public interface IByRefParameterApi
{
    [Get("items")]
    Task<AxiosResponse<Person>> GetAsync([Query] out int page);
}

/// <summary>ref struct 参数：无法跨越接口调用绑定。</summary>
public interface IRefStructParameterApi
{
    [Get("items")]
    Task<AxiosResponse<Person>> GetAsync(Span<int> buffer);
}

/// <summary>CancellationToken 同时标了绑定特性。</summary>
public interface IBoundCancellationTokenApi
{
    [Get("items")]
    Task<AxiosResponse<Person>> GetAsync([Query] CancellationToken cancellationToken);
}

/// <summary>两个 CancellationToken 参数。</summary>
public interface IDoubleCancellationTokenApi
{
    [Get("items")]
    Task<AxiosResponse<Person>> GetAsync(CancellationToken first, CancellationToken second);
}

/// <summary>[ApiIgnore] 与参数特性同用：二义性。</summary>
public interface IIgnoredBoundParameterApi
{
    [Get("items")]
    Task<AxiosResponse<Person>> GetAsync([ApiIgnore, Query] int page);
}

/// <summary>只标 [ApiIgnore]：合法（参数被跳过）。</summary>
public interface IIgnoredParameterApi
{
    [Get("items")]
    Task<AxiosResponse<Person>> GetAsync([ApiIgnore] int page);
}

/// <summary>实现类：给 <c>ResolveMethod</c> 提供「同名不同签名」的方法。</summary>
public sealed class WellFormedApiImplementation : IWellFormedApi
{
    public Task<AxiosResponse<Person>> GetAsync(int id, string? keyword = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<AxiosResponse<Person>> GetAsync(long id, string? keyword = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public void Unrelated()
    {
    }
}
