using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Internal;
using AxiosNet.WebAPI.Metadata;

namespace AxiosNet.Tests.WebAPI.Aot;

/// <summary>
/// 接口级声明构建器：合法声明能构建全部方法契约（含 multipart / 流式 / 授权），
/// 非法声明在 Validate 时一次性暴露；代理工厂在此之前不会被调用。
/// </summary>
[Collection(GlobalSerializationCollection.Name)]
public sealed class InterfaceDeclarationTests
{
    static InterfaceDeclarationTests()
    {
        AxiosApiMetadata.RegisterInterface(typeof(IDeclaredBindingApi), CreateProxy, static builder => builder
            .StaticHeaders("X-Api: v1")
            .Method(nameof(IDeclaredBindingApi.GetAsync), HttpMethod.Get, "decl/items/{id}")
                .StaticHeaders("X-Method: yes")
                .Parameter(0, "id", typeof(int))
                    .Path("id")
                .Parameter(1, "q", typeof(string))
                    .Query()
                .Parameter(2, "headers", typeof(IDictionary<string, string>))
                    .HeaderCollection()
                .CancellationToken(3)
                .ReturnsResponse(typeof(Person))
            .Method(nameof(IDeclaredBindingApi.SaveAsync), "post", "decl/items")
                .Authorize("Basic")
                .Parameter(0, "payload", typeof(Person))
                    .Body()
                .ReturnsVoid()
            .Method(nameof(IDeclaredBindingApi.PingAsync), HttpMethod.Head, "decl/ping")
                .ReturnsData(typeof(object)));

        AxiosApiMetadata.RegisterInterface(typeof(IDeclaredMultipartApi), CreateProxy, static builder => builder
            .Method(nameof(IDeclaredMultipartApi.UploadAsync), HttpMethod.Post, "decl/upload/{id}")
                .Multipart()
                .Parameter(0, "id", typeof(int))
                    .Path("id")
                .Parameter(1, "file", typeof(Stream))
                    .Part("file", "a.bin", "application/octet-stream")
                .Parameter(2, "note", typeof(string))
                    .Unannotated()
                .ReturnsData(typeof(Person)));

        AxiosApiMetadata.RegisterInterface(typeof(IDeclaredStreamApi), CreateProxy, static builder => builder
            .Method(nameof(IDeclaredStreamApi.StreamAsync), HttpMethod.Get, "decl/stream")
                .Stream(AxiosStreamFormat.ServerSentEvents)
                .ReturnsResponse(typeof(IAsyncEnumerable<Person>)));

        // 接口级授权被方法级 [AllowAnonymous] 覆盖。
        AxiosApiMetadata.RegisterInterface(typeof(IDeclaredAuthorizeApi), CreateProxy, static builder => builder
            .Authorize("Basic")
            .Method(nameof(IDeclaredAuthorizeApi.GetAsync), HttpMethod.Get, "decl/auth")
                .Anonymous()
                .ReturnsVoid());

        // 取消令牌参数即使被统一声明并标注绑定特性也按类型跳过（与反射路径一致）。
        AxiosApiMetadata.RegisterInterface(typeof(IDeclaredCancellationTokenApi), CreateProxy, static builder => builder
            .Method(nameof(IDeclaredCancellationTokenApi.GetAsync), HttpMethod.Get, "decl/ct/{id}")
                .Parameter(0, "id", typeof(int))
                    .Path("id")
                .Parameter(1, "cancellationToken", typeof(CancellationToken))
                    .Path("id")
                .CancellationToken(1)
                .ReturnsVoid());

        // 非法声明：注册只记录，Validate 时抛。
        AxiosApiMetadata.RegisterInterface(typeof(IConfigureErrorApi), CreateProxy, static builder =>
        {
            builder.Method(nameof(IConfigureErrorApi.GetAsync), HttpMethod.Get, "decl/owner-error");
            throw new InvalidOperationException("接口声明损坏");
        });

        AxiosApiMetadata.RegisterInterface(typeof(IBlankMethodApi), CreateProxy, static builder => builder
            .Method(nameof(IBlankMethodApi.GetAsync), " ", "decl/blank")
                .ReturnsVoid());

        AxiosApiMetadata.RegisterInterface(typeof(IPartWithoutMultipartApi), CreateProxy, static builder => builder
            .Method(nameof(IPartWithoutMultipartApi.GetAsync), HttpMethod.Post, "decl/part")
                .Parameter(0, "file", typeof(Stream))
                    .Part()
                .ReturnsVoid());

        AxiosApiMetadata.RegisterInterface(typeof(IDuplicatePartApi), CreateProxy, static builder => builder
            .Method(nameof(IDuplicatePartApi.GetAsync), HttpMethod.Post, "decl/dup-part")
                .Multipart()
                .Parameter(0, "first", typeof(string))
                    .Part("same")
                .Parameter(1, "second", typeof(string))
                    .Part("same")
                .ReturnsVoid());

        AxiosApiMetadata.RegisterInterface(typeof(IMultipartBodyConflictApi), CreateProxy, static builder => builder
            .Method(nameof(IMultipartBodyConflictApi.GetAsync), HttpMethod.Post, "decl/multipart-body")
                .Multipart()
                .Parameter(0, "payload", typeof(Person))
                    .Body()
                .ReturnsVoid());

        AxiosApiMetadata.RegisterInterface(typeof(IUnsupportedReturnApi), CreateProxy, static builder => builder
            .Method(nameof(IUnsupportedReturnApi.GetAsync), HttpMethod.Get, "decl/unsupported"));

        AxiosApiMetadata.RegisterInterface(typeof(IStreamWithoutStreamingApi), CreateProxy, static builder => builder
            .Method(nameof(IStreamWithoutStreamingApi.GetAsync), HttpMethod.Get, "decl/stream-plain")
                .Stream()
                .ReturnsData(typeof(Person)));

        AxiosApiMetadata.RegisterInterface(typeof(IInvalidStreamFormatApi), CreateProxy, static builder => builder
            .Method(nameof(IInvalidStreamFormatApi.GetAsync), HttpMethod.Get, "decl/bad-format")
                .Stream((AxiosStreamFormat)99)
                .ReturnsResponse(typeof(IAsyncEnumerable<Person>)));

        AxiosApiMetadata.RegisterInterface(typeof(IBadStaticHeaderApi), CreateProxy, static builder => builder
            .StaticHeaders(" ")
            .Method(nameof(IBadStaticHeaderApi.GetAsync), HttpMethod.Get, "decl/bad-header")
                .ReturnsVoid());

        AxiosApiMetadata.RegisterInterface(typeof(IBadHeaderCollectionApi), CreateProxy, static builder => builder
            .Method(nameof(IBadHeaderCollectionApi.GetAsync), HttpMethod.Get, "decl/bad-collection")
                .Parameter(0, "count", typeof(int))
                    .HeaderCollection()
                .ReturnsVoid());

        AxiosApiMetadata.RegisterInterface(typeof(IDuplicateRegistrationApi), CreateProxy, static builder => builder
            .Method(nameof(IDuplicateRegistrationApi.GetAsync), HttpMethod.Get, "decl/duplicate")
                .ReturnsVoid());

        // 接口级错误但没有声明任何方法：Validate 仍要抛出（不能被空方法表吞掉）。
        AxiosApiMetadata.RegisterInterface(typeof(IConfigureErrorEmptyApi), CreateProxy, static _ =>
            throw new InvalidOperationException("接口声明损坏"));

        // 两个取消令牌参数：与反射路径一样按类型识别并拒绝。
        AxiosApiMetadata.RegisterInterface(typeof(IDuplicateCancellationTokenApi), CreateProxy, static builder => builder
            .Method(nameof(IDuplicateCancellationTokenApi.GetAsync), HttpMethod.Get, "decl/ct-dup")
                .Parameter(0, "first", typeof(CancellationToken))
                    .Unannotated()
                .Parameter(1, "second", typeof(CancellationToken))
                    .Unannotated()
                .ReturnsVoid());

        // 生成器可直接写声明字段：builder 不再拆开的分支（[ApiIgnore] 与绑定同标、方法级声明损坏）。
        var ignoreConflict = new InterfaceDeclarationData { InterfaceType = typeof(IIgnoreConflictApi) };
        var ignoreMethod = new InterfaceMethodDeclaration
        {
            Name = nameof(IIgnoreConflictApi.GetAsync),
            MethodName = "GET",
            Path = "decl/ignore",
            ReturnShape = InterfaceReturnShape.Void
        };

        ignoreMethod.Parameters.Add(new InterfaceParameterDeclaration
        {
            Index = 0,
            Name = "id",
            ValueType = typeof(int),
            Ignore = true,
            Path = true,
            PathName = "id"
        });

        ignoreConflict.Methods.Add(ignoreMethod);
        GeneratedMetadataRegistry.RegisterInterface(ignoreConflict, CreateProxy);

        var methodError = new InterfaceDeclarationData { InterfaceType = typeof(IMethodErrorApi) };
        methodError.Methods.Add(new InterfaceMethodDeclaration
        {
            Name = nameof(IMethodErrorApi.GetAsync),
            Error = "方法声明损坏"
        });

        GeneratedMetadataRegistry.RegisterInterface(methodError, CreateProxy);
    }

    private static object CreateProxy(ApiClient client)
        => throw new NotSupportedException("代理工厂不属于声明契约的测试范围");

    [Fact]
    public void ValidDeclarations_BuildEveryMethodContractAndCache()
    {
        Assert.True(GeneratedMetadataRegistry.TryGetInterface(typeof(IDeclaredBindingApi), out var entry));
        Assert.NotNull(entry);

        var methods = entry!.GetMethods();
        Assert.Equal(3, methods.Length);
        Assert.All(methods, static method => method.EnsureValid());
        Assert.Same(methods, entry.GetMethods());
    }

    [Fact]
    public void MultipartDeclaration_BuildsPartBindings()
    {
        Assert.True(GeneratedMetadataRegistry.TryGetInterface(typeof(IDeclaredMultipartApi), out var entry));
        entry!.GetMethods()[0].EnsureValid();
    }

    [Fact]
    public void StreamAndAuthorizeDeclarations_Build()
    {
        Assert.True(GeneratedMetadataRegistry.TryGetInterface(typeof(IDeclaredStreamApi), out var stream));
        stream!.GetMethods()[0].EnsureValid();

        Assert.True(GeneratedMetadataRegistry.TryGetInterface(typeof(IDeclaredAuthorizeApi), out var authorize));
        authorize!.GetMethods()[0].EnsureValid();
    }

    [Fact]
    public void CancellationTokenParameter_IsSkippedLikeReflectionPath()
    {
        Assert.True(GeneratedMetadataRegistry.TryGetInterface(typeof(IDeclaredCancellationTokenApi), out var entry));
        entry!.Validate();
    }

    [Fact]
    public void DuplicateInterfaceRegistration_Throws()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => AxiosApiMetadata.RegisterInterface(typeof(IDuplicateRegistrationApi), CreateProxy, static _ => { }));

        Assert.Contains("接口", error.Message);
        Assert.Contains("元数据已注册", error.Message);
    }

    [Theory]
    [InlineData(typeof(IConfigureErrorApi), "接口声明损坏")]
    [InlineData(typeof(IConfigureErrorEmptyApi), "接口声明损坏")]
    [InlineData(typeof(IDuplicateCancellationTokenApi), "多个 CancellationToken")]
    [InlineData(typeof(IBlankMethodApi), "必须且只能标注一个路由特性")]
    [InlineData(typeof(IMethodErrorApi), "方法声明损坏")]
    [InlineData(typeof(IIgnoreConflictApi), "[ApiIgnore]")]
    [InlineData(typeof(IPartWithoutMultipartApi), "没有 [Multipart]")]
    [InlineData(typeof(IDuplicatePartApi), "同名")]
    [InlineData(typeof(IMultipartBodyConflictApi), "互斥")]
    [InlineData(typeof(IUnsupportedReturnApi), "返回类型不受支持")]
    [InlineData(typeof(IStreamWithoutStreamingApi), "标注了 [Stream]")]
    [InlineData(typeof(IInvalidStreamFormatApi), "帧格式值")]
    [InlineData(typeof(IBadStaticHeaderApi), "含空白声明")]
    [InlineData(typeof(IBadHeaderCollectionApi), "不是字符串键值字典")]
    public void BrokenDeclaration_ErrorSurfacesOnValidate(Type interfaceType, string expected)
    {
        Assert.True(GeneratedMetadataRegistry.TryGetInterface(interfaceType, out var entry));

        var error = Assert.Throws<InvalidOperationException>(entry!.Validate);

        Assert.Contains(expected, error.Message);
    }
}

public interface IDeclaredBindingApi
{
    Task<AxiosResponse<Person>> GetAsync(int id, string q, IDictionary<string, string> headers, CancellationToken cancellationToken);

    Task SaveAsync(Person payload);

    Task PingAsync();
}

public interface IDeclaredMultipartApi
{
    Task<Person> UploadAsync(int id, Stream file, string note);
}

public interface IDeclaredStreamApi
{
    Task<AxiosResponse<IAsyncEnumerable<Person>>> StreamAsync();
}

public interface IDeclaredAuthorizeApi
{
    Task GetAsync();
}

public interface IDeclaredCancellationTokenApi
{
    Task GetAsync(int id, CancellationToken cancellationToken);
}

public interface IConfigureErrorApi
{
    Task GetAsync();
}

public interface IConfigureErrorEmptyApi
{
    Task GetAsync();
}

public interface IDuplicateCancellationTokenApi
{
    Task GetAsync(CancellationToken first, CancellationToken second);
}

public interface IBlankMethodApi
{
    Task GetAsync();
}

public interface IIgnoreConflictApi
{
    Task GetAsync(int id);
}

public interface IMethodErrorApi
{
    Task GetAsync();
}

public interface IPartWithoutMultipartApi
{
    Task GetAsync(Stream file);
}

public interface IDuplicatePartApi
{
    Task GetAsync(string first, string second);
}

public interface IMultipartBodyConflictApi
{
    Task GetAsync(Person payload);
}

public interface IUnsupportedReturnApi
{
    void GetAsync();
}

public interface IStreamWithoutStreamingApi
{
    Task<Person> GetAsync();
}

public interface IInvalidStreamFormatApi
{
    Task<AxiosResponse<IAsyncEnumerable<Person>>> GetAsync();
}

public interface IBadStaticHeaderApi
{
    Task GetAsync();
}

public interface IBadHeaderCollectionApi
{
    Task GetAsync(int count);
}

public interface IDuplicateRegistrationApi
{
    Task GetAsync();
}
