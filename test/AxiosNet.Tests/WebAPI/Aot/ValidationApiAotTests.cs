using System.Reflection;
using AxiosNet.Internal;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Internal;
using AxiosNet.WebAPI.Metadata;

namespace AxiosNet.Tests.WebAPI.Aot;

/// <summary>
/// AOT 下的校验入口：已注册的声明走注册表（无需反射）；未注册的类型给出可读的缺元数据错误；
/// 程序集扫描在 AOT 下显式失败并指向生成的 ValidateAll / 按类型重载。
/// 注册表是进程级状态，与其它改全局状态的用例同集合串行。
/// </summary>
[Collection(GlobalSerializationCollection.Name)]
public sealed class ValidationApiAotTests
{
    static ValidationApiAotTests()
    {
        AxiosApiMetadata.RegisterRequestModel(
            typeof(ValidatedRegisteredRequest),
            static builder => builder
                .Route(HttpMethod.Get, "items")
                .ImplementsApiRequest());

        AxiosApiMetadata.RegisterRequestModel(
            typeof(ValidatedBrokenRequest),
            static builder => builder.ImplementsApiRequest());

        // 有路由、也注册了声明，但没实现 IApiRequest<TResponse>：声明路径不能因为「注册过」就放过它。
        AxiosApiMetadata.RegisterRequestModel(
            typeof(ValidatedNonApiRequest),
            static builder => builder.Route(HttpMethod.Get, "items"));

        AxiosApiMetadata.RegisterInterface(
            typeof(IValidatedApi),
            static _ => throw new NotSupportedException("代理工厂不属于本用例的测试范围"),
            static builder => builder
                .Method(nameof(IValidatedApi.GetAsync), HttpMethod.Get, "items/{id}")
                    .Parameter(0, "id", typeof(int))
                        .Path("id")
                    .CancellationToken(1)
                .ReturnsResponse(typeof(object)));

        // 两个方法都缺 {id} 绑定：校验应聚合全部方法错误，而不是只报第一条。
        AxiosApiMetadata.RegisterInterface(
            typeof(IBrokenMethodsApi),
            static _ => throw new NotSupportedException("代理工厂不属于本用例的测试范围"),
            static builder => builder
                .Method(nameof(IBrokenMethodsApi.FirstAsync), HttpMethod.Get, "items/{id}")
                    .Parameter(0, "id", typeof(int))
                        .Unannotated()
                    .CancellationToken(1)
                .ReturnsData(typeof(object))
                .Method(nameof(IBrokenMethodsApi.SecondAsync), HttpMethod.Get, "items/{id}")
                    .Parameter(0, "id", typeof(int))
                        .Unannotated()
                    .CancellationToken(1)
                .ReturnsData(typeof(object)));
    }

    [Fact]
    public void ValidateModels_RegisteredModel_UnderAot_ReturnsNoErrors()
    {
        RuntimeSupport.ForceNoDynamicCode = true;
        try
        {
            Assert.Empty(ApiClient.ValidateModels(typeof(ValidatedRegisteredRequest)));
        }
        finally
        {
            RuntimeSupport.ForceNoDynamicCode = false;
        }
    }

    [Fact]
    public void ValidateModels_UnregisteredModel_UnderAot_ReportsMissingMetadata()
    {
        RuntimeSupport.ForceNoDynamicCode = true;
        try
        {
            var error = Assert.Single(ApiClient.ValidateModels(typeof(ValidatedUnregisteredRequest)));

            Assert.Contains(nameof(ValidatedUnregisteredRequest), error);
            Assert.Contains("缺少生成元数据", error);
            Assert.Contains("RegisterRequestModel", error);
        }
        finally
        {
            RuntimeSupport.ForceNoDynamicCode = false;
        }
    }

    [Fact]
    public void ValidateModels_RegisteredBrokenModel_ReportsBuildError()
    {
        RuntimeSupport.ForceNoDynamicCode = true;
        try
        {
            var error = Assert.Single(ApiClient.ValidateModels(typeof(ValidatedBrokenRequest)));

            Assert.Contains(nameof(ValidatedBrokenRequest), error);
            Assert.Contains("缺少路由特性", error);
        }
        finally
        {
            RuntimeSupport.ForceNoDynamicCode = false;
        }
    }

    [Fact]
    public void ValidateModels_JitFallback_StillReflectsUnregisteredModels()
    {
        // JIT 下未注册的模型照旧走反射：合法模型不需要注册也能通过。
        Assert.Empty(ApiClient.ValidateModels(typeof(ValidatedJitRequest)));
    }

    [Fact]
    public void ValidateModels_NonRequestType_ReportsNotImplemented()
    {
        var error = Assert.Single(ApiClient.ValidateModels(typeof(ValidatedPlainType)));

        Assert.Contains("未实现 IApiRequest<TResponse>", error);
    }

    [Fact]
    public void ValidateModels_RegisteredWithoutApiRequest_ReportsNotImplemented()
    {
        // 声明路径与反射路径同判据：注册了声明不等于能当请求模型。
        // 两种运行时都要报错，否则 AOT 下会放过 JIT 下会被拒绝的类型。
        var jitError = Assert.Single(ApiClient.ValidateModels(typeof(ValidatedNonApiRequest)));
        Assert.Contains("未实现 IApiRequest<TResponse>", jitError);

        RuntimeSupport.ForceNoDynamicCode = true;
        try
        {
            var error = Assert.Single(ApiClient.ValidateModels(typeof(ValidatedNonApiRequest)));
            Assert.Contains("未实现 IApiRequest<TResponse>", error);
        }
        finally
        {
            RuntimeSupport.ForceNoDynamicCode = false;
        }
    }

    [Fact]
    public void ValidateInterfaces_NonInterfaceType_ReportsNotAnInterface()
    {
        var error = Assert.Single(ApiClient.ValidateInterfaces(typeof(ValidatedPlainType)));

        Assert.Contains("不是接口", error);
    }

    [Fact]
    public void ValidateInterfaces_OpenGenericDefinition_ReportsClosedTypeRequired()
    {
        var error = Assert.Single(ApiClient.ValidateInterfaces(typeof(IValidatedOpenApi<>)));

        Assert.Contains("开放泛型", error);
    }

    [Fact]
    public void ValidateInterfaces_RegisteredInterface_UnderAot_ReturnsNoErrors()
    {
        RuntimeSupport.ForceNoDynamicCode = true;
        try
        {
            Assert.Empty(ApiClient.ValidateInterfaces(typeof(IValidatedApi)));
        }
        finally
        {
            RuntimeSupport.ForceNoDynamicCode = false;
        }
    }

    [Fact]
    public void ValidateInterfaces_UnregisteredInterface_UnderAot_ReportsMissingMetadata()
    {
        RuntimeSupport.ForceNoDynamicCode = true;
        try
        {
            var error = Assert.Single(ApiClient.ValidateInterfaces(typeof(IValidatedUnregisteredApi)));

            Assert.Contains(nameof(IValidatedUnregisteredApi), error);
            Assert.Contains("缺少生成元数据", error);
            Assert.Contains("RegisterInterface", error);
        }
        finally
        {
            RuntimeSupport.ForceNoDynamicCode = false;
        }
    }

    [Fact]
    public void ValidateInterfaces_RegisteredBrokenInterface_AggregatesEveryMethodError()
    {
        RuntimeSupport.ForceNoDynamicCode = true;
        try
        {
            var errors = ApiClient.ValidateInterfaces(typeof(IBrokenMethodsApi));

            Assert.Equal(2, errors.Count);
            Assert.Contains(errors, static error => error.Contains(nameof(IBrokenMethodsApi.FirstAsync)));
            Assert.Contains(errors, static error => error.Contains(nameof(IBrokenMethodsApi.SecondAsync)));
        }
        finally
        {
            RuntimeSupport.ForceNoDynamicCode = false;
        }
    }

    [Fact]
    public void AssemblyScan_UnderAot_ThrowsPlatformNotSupported()
    {
        RuntimeSupport.ForceNoDynamicCode = true;
        try
        {
            var modelError = Assert.Throws<PlatformNotSupportedException>(
                () => ApiClient.ValidateModels(typeof(ValidationApiAotTests).Assembly));
            Assert.Contains("Native AOT", modelError.Message);
            Assert.Contains("ValidateAll", modelError.Message);

            var interfaceError = Assert.Throws<PlatformNotSupportedException>(
                () => ApiClient.ValidateInterfaces(typeof(ValidationApiAotTests).Assembly));
            Assert.Contains("Native AOT", interfaceError.Message);
            Assert.Contains("ValidateInterfaces(Type[])", interfaceError.Message);
        }
        finally
        {
            RuntimeSupport.ForceNoDynamicCode = false;
        }
    }
}

/// <summary>手工注册的合法请求模型：AOT 下校验不反射成员。</summary>
[Get("items")]
public sealed class ValidatedRegisteredRequest : IApiRequest<object>;

/// <summary>注册了声明但没给路由：首次构建报「缺少路由特性」。</summary>
public sealed class ValidatedBrokenRequest : IApiRequest<object>;

/// <summary>未注册的请求模型：AOT 下必须报缺元数据。</summary>
[Get("items")]
public sealed class ValidatedUnregisteredRequest : IApiRequest<object>;

/// <summary>注册了声明但没实现 IApiRequest：校验必须与反射路径同判据。</summary>
[Get("items")]
public sealed class ValidatedNonApiRequest;

/// <summary>JIT 回退用：合法且未注册。</summary>
[Get("items")]
public sealed class ValidatedJitRequest : IApiRequest<object>
{
    [Query]
    public int Page { get; set; }
}

public sealed class ValidatedPlainType;

public interface IValidatedApi
{
    Task<AxiosResponse<object>> GetAsync(int id, CancellationToken cancellationToken);
}

public interface IBrokenMethodsApi
{
    Task<object> FirstAsync(int id, CancellationToken cancellationToken);

    Task<object> SecondAsync(int id, CancellationToken cancellationToken);
}

public interface IValidatedUnregisteredApi
{
    [Get("items")]
    Task<object> GetAsync(CancellationToken cancellationToken);
}

/// <summary>开放泛型接口：校验必须要求闭合类型。</summary>
public interface IValidatedOpenApi<T>
{
    [Get("items")]
    Task<T> GetAsync(CancellationToken cancellationToken);
}
