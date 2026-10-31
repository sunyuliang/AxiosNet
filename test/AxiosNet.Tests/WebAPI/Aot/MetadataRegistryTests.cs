using AxiosNet.Internal;
using AxiosNet.Metadata;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI.Internal;
using AxiosNet.WebAPI.Metadata;
using AxiosNet.WebAPI;

namespace AxiosNet.Tests.WebAPI.Aot;

/// <summary>
/// 元数据注册表：生成器 / 手工注册的声明优先于反射；AOT（强制无动态代码）下未注册即显式失败。
/// 注册表与 <see cref="RuntimeSupport.ForceNoDynamicCode"/> 是进程级状态，这里放独占集合里跑。
/// </summary>
[Collection(GlobalSerializationCollection.Name)]
public sealed class MetadataRegistryTests
{
    static MetadataRegistryTests()
    {
        // 与 RegisteredGetRequest 同形（仅没有手工注册），用于生成 / 反射两条路径的对照。
        AxiosApiMetadata.RegisterRequestModel(typeof(RegisteredGetRequest), static builder => builder
            .Route(HttpMethod.Get, "orders/{id}")
            .StaticHeaders("X-Static: yes")
            .Member(nameof(RegisteredGetRequest.Id), typeof(int), static target => ((RegisteredGetRequest)target).Id)
                .Path("id")
            .Member(nameof(RegisteredGetRequest.Search), typeof(string), static target => ((RegisteredGetRequest)target).Search)
                .Query("q")
            .Member(nameof(RegisteredGetRequest.Token), typeof(string), static target => ((RegisteredGetRequest)target).Token)
                .Header("X-Token"));
    }

    [Fact]
    public async Task RegisteredModel_UsesDeclaration_WhenDynamicCodeIsDisabled()
    {
        RuntimeSupport.ForceNoDynamicCode = true;
        try
        {
            await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
            using var client = new ApiClient(server.Url);

            await client.RequestAsync(new RegisteredGetRequest { Id = 42, Search = "x", Token = "t" });

            var received = Assert.Single(server.Received);
            Assert.Equal("/orders/42", received.Path);
            Assert.Equal("q=x", received.Query);
            Assert.Equal("t", received.Headers["X-Token"]);
            Assert.Equal("yes", received.Headers["X-Static"]);
        }
        finally
        {
            RuntimeSupport.ForceNoDynamicCode = false;
        }
    }

    [Fact]
    public void RegisteredModel_AndReflectionModel_ProduceSameUri()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");

        var registered = client.GetUri(new RegisteredGetRequest { Id = 7, Search = "hello world", Token = "t" });
        var reflected = client.GetUri(new ReflectionGetRequest { Id = 7, Search = "hello world", Token = "t" });

        Assert.Equal(reflected, registered);
        Assert.Contains("/orders/7", registered);
        Assert.Contains("q=hello%20world", registered);
    }

    [Fact]
    public void UnregisteredModel_WhenDynamicCodeIsDisabled_ThrowsReadableAotException()
    {
        RuntimeSupport.ForceNoDynamicCode = true;
        try
        {
            using var client = new ApiClient("http://127.0.0.1:1/");

            var error = Assert.Throws<AxiosAotMetadataException>(
                () => client.GetUri(new UnregisteredGetRequest { Id = 1 }));

            Assert.Contains(nameof(UnregisteredGetRequest), error.Message);
            Assert.Contains("RegisterRequestModel", error.Message);
            Assert.Contains("Native AOT", error.Message);
        }
        finally
        {
            RuntimeSupport.ForceNoDynamicCode = false;
        }
    }

    [Fact]
    public void DuplicateRegistration_Throws_InsteadOfSilentlyOverwriting()
    {
        AxiosApiMetadata.RegisterRequestModel(
            typeof(DuplicateRegistrationRequest),
            static builder => builder.Route(HttpMethod.Get, "aot/duplicate"));

        var error = Assert.Throws<InvalidOperationException>(() => AxiosApiMetadata.RegisterRequestModel(
            typeof(DuplicateRegistrationRequest),
            static builder => builder.Route(HttpMethod.Get, "aot/duplicate")));

        Assert.Contains("不能同时声明同一类型", error.Message);
    }

    [Fact]
    public void RegistrationError_IsDeferredToFirstUse()
    {
        // 绑定名与路径模板对不上：注册本身不抛，首次构建绑定计划时报错（与反射路径的时序一致）。
        AxiosApiMetadata.RegisterRequestModel(typeof(BrokenRegistrationRequest), static builder => builder
            .Route(HttpMethod.Get, "aot/broken/{id}")
            .Member("Other", typeof(int), static target => ((BrokenRegistrationRequest)target).Other)
                .Query());

        using var client = new ApiClient("http://127.0.0.1:1/");
        var error = Assert.Throws<InvalidOperationException>(
            () => client.GetUri(new BrokenRegistrationRequest()));

        Assert.Contains("{id}", error.Message);
        Assert.Contains("没有", error.Message);
    }

    /// <summary>
    /// 能力切换必须丢掉「按能力构建并缓存」的结果：先在 JIT 下建好缓存，再切到 AOT，
    /// 同一个类型必须改判成「缺元数据」，而不是把 JIT 的旧结论继续用下去。
    /// </summary>
    [Fact]
    public void CapabilitySwitch_DropsRequestModelMetadataCachedUnderJit()
    {
        // 预热：反射路径构建并缓存。
        var jit = RequestModelMetadata.Get(typeof(ReflectionGetRequest));
        Assert.Equal("orders/3", jit.ResolveUrl(new ReflectionGetRequest { Id = 3 }, null));

        RuntimeSupport.ForceNoDynamicCode = true;
        try
        {
            // 缓存没被丢掉的话这里会原样返回上面那份 JIT 元数据，不抛错。
            var error = Assert.Throws<AxiosAotMetadataException>(
                () => RequestModelMetadata.Get(typeof(ReflectionGetRequest)));

            Assert.Contains(nameof(ReflectionGetRequest), error.Message);
        }
        finally
        {
            RuntimeSupport.ForceNoDynamicCode = false;
        }

        // 切回来同样要重新构建，而不是留着「缺元数据」的结论。
        Assert.NotNull(RequestModelMetadata.Get(typeof(ReflectionGetRequest)));
    }
}

[Get("orders/{id}")]
public sealed class RegisteredGetRequest : IApiRequest<Person>
{
    [Path]
    public int Id { get; set; }

    [Query("q")]
    public string? Search { get; set; }

    [Header("X-Token")]
    public string? Token { get; set; }
}

/// <summary>与 <see cref="RegisteredGetRequest"/> 同形，但只走反射路径。</summary>
[Get("orders/{id}")]
public sealed class ReflectionGetRequest : IApiRequest<Person>
{
    [Path]
    public int Id { get; set; }

    [Query("q")]
    public string? Search { get; set; }

    [Header("X-Token")]
    public string? Token { get; set; }
}

[Get("aot/unregistered")]
public sealed class UnregisteredGetRequest : IApiRequest<Person>
{
    [Path]
    public int Id { get; set; }
}

[Get("aot/duplicate")]
public sealed class DuplicateRegistrationRequest : IApiRequest<object>;

[Get("aot/broken/{id}")]
public sealed class BrokenRegistrationRequest : IApiRequest<object>
{
    [Query]
    public int Other { get; set; }
}
