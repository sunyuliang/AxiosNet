using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Internal;
using AxiosNet.WebAPI.Metadata;

namespace AxiosNet.Tests.WebAPI.Aot;

/// <summary>
/// 请求模型的声明构建器：生成器写出的每个分支（默认 Body / multipart 部件 / 授权 / 静态头）都要与反射路径同语义；
/// 非法声明只在首次使用时抛，且消息能定位到具体成员。
/// </summary>
[Collection(GlobalSerializationCollection.Name)]
public sealed class RequestModelDeclarationTests
{
    static RequestModelDeclarationTests()
    {
        AxiosMetadata.RegisterRequestModel(typeof(DeclaredMultipartRequest), static builder => builder
            .Route(HttpMethod.Post, "declared/multipart/{id}")
            .StaticHeaders("X-Model: yes")
            .Multipart()
            .Member(nameof(DeclaredMultipartRequest.Id), typeof(int), static target => ((DeclaredMultipartRequest)target).Id)
                .Path("id")
            .Member(nameof(DeclaredMultipartRequest.File), typeof(Stream), static target => ((DeclaredMultipartRequest)target).File)
                .Part("upload", "a.bin", "application/octet-stream")
            .Member(nameof(DeclaredMultipartRequest.Note), typeof(string), static target => ((DeclaredMultipartRequest)target).Note)
                .FieldName("note")
                .Unannotated());

        AxiosMetadata.RegisterRequestModel(typeof(DeclaredDefaultBodyRequest), static builder => builder
            .Route(HttpMethod.Post, "declared/orders")
            .ImplementsApiRequest()
            .Member(nameof(DeclaredDefaultBodyRequest.Payload), typeof(Person), static target => ((DeclaredDefaultBodyRequest)target).Payload)
                .Unannotated());

        AxiosMetadata.RegisterRequestModel(typeof(DeclaredAuthorizeRequest), static builder => builder
            .Route("get", "declared/auth")
            .Authorize("Basic"));

        AxiosMetadata.RegisterRequestModel(typeof(DeclaredAnonymousRequest), static builder => builder
            .Route(HttpMethod.Get, "declared/anonymous")
            .Anonymous());

        AxiosMetadata.RegisterRequestModel(typeof(DeclaredHeadersRequest), static builder => builder
            .Route(HttpMethod.Get, "declared/headers")
            .StaticHeaders("X-Static: yes", "X-Removed")
            .Member(nameof(DeclaredHeadersRequest.Token), typeof(string), static target => ((DeclaredHeadersRequest)target).Token)
                .Header("X-Static"));

        // 非法声明：注册只记录错误，首次构建元数据时才抛。
        AxiosMetadata.RegisterRequestModel(typeof(DeclaredPartWithoutMultipartRequest), static builder => builder
            .Route(HttpMethod.Post, "declared/part")
            .Member(nameof(DeclaredPartWithoutMultipartRequest.File), typeof(Stream), static target => ((DeclaredPartWithoutMultipartRequest)target).File)
                .Part());

        AxiosMetadata.RegisterRequestModel(typeof(DeclaredDuplicatePartRequest), static builder => builder
            .Route(HttpMethod.Post, "declared/dup-part")
            .Multipart()
            .Member("First", typeof(string), static _ => null)
                .Part("same")
            .Member("Second", typeof(string), static _ => null)
                .Part("same"));

        AxiosMetadata.RegisterRequestModel(typeof(DeclaredMultipartBodyRequest), static builder => builder
            .Route(HttpMethod.Post, "declared/multipart-body")
            .Multipart()
            .Member("Payload", typeof(Person), static _ => null)
                .Body());

        AxiosMetadata.RegisterRequestModel(typeof(DeclaredBlankRouteRequest), static builder => builder
            .Route(" ", " ")
            .StaticHeaders());

        AxiosMetadata.RegisterRequestModel(typeof(DeclaredBlankSchemeRequest), static builder => builder
            .Route(HttpMethod.Get, "declared/blank-scheme")
            .Authorize(" "));

        AxiosMetadata.RegisterRequestModel(typeof(DeclaredAuthorizationConflictRequest), static builder => builder
            .Route(HttpMethod.Get, "declared/auth-conflict")
            .Anonymous()
            .Authorize("Bearer"));

        AxiosMetadata.RegisterRequestModel(typeof(DeclaredBlankPartNameRequest), static builder => builder
            .Route(HttpMethod.Post, "declared/blank-part")
            .Multipart()
            .Member("File", typeof(Stream), static _ => null)
                .Part(" "));

        AxiosMetadata.RegisterRequestModel(typeof(DeclaredBlankPartContentTypeRequest), static builder => builder
            .Route(HttpMethod.Post, "declared/blank-part-content")
            .Multipart()
            .Member("File", typeof(Stream), static _ => null)
                .Part("file", "a.bin", " "));

        AxiosMetadata.RegisterRequestModel(typeof(DeclaredBlankStaticHeaderRequest), static builder => builder
            .Route(HttpMethod.Get, "declared/blank-header")
            .StaticHeaders(" "));

        AxiosMetadata.RegisterRequestModel(typeof(DeclaredBadStaticHeaderRequest), static builder => builder
            .Route(HttpMethod.Get, "declared/bad-header")
            .StaticHeaders(": value"));

        AxiosMetadata.RegisterRequestModel(typeof(DeclaredDuplicatePathRequest), static builder => builder
            .Route(HttpMethod.Get, "declared/dup/{id}")
            .Member("First", typeof(int), static _ => null)
                .Path("id")
            .Member("Second", typeof(int), static _ => null)
                .Path("id"));

        // 生成器可直接写声明字段，能表达 builder 不再拆开的分支（[ApiIgnore] 与绑定同标、取值委托缺失）。
        var ignoreConflict = new RequestModelDeclaration
        {
            ModelType = typeof(DeclaredIgnoreConflictRequest),
            MethodName = "GET",
            Path = "declared/ignore"
        };

        ignoreConflict.Members.Add(new RequestModelMemberDeclaration
        {
            Name = "Id",
            FieldName = "Id",
            ValueType = typeof(int),
            GetValue = static target => ((DeclaredIgnoreConflictRequest)target).Id,
            Ignore = true,
            Path = true,
            PathName = "id"
        });

        GeneratedMetadataRegistry.RegisterRequestModel(ignoreConflict);

        var missingGetter = new RequestModelDeclaration
        {
            ModelType = typeof(DeclaredMissingGetterRequest),
            MethodName = "GET",
            Path = "declared/missing"
        };

        missingGetter.Members.Add(new RequestModelMemberDeclaration
        {
            Name = "Value",
            FieldName = "Value",
            ValueType = typeof(int)
        });

        GeneratedMetadataRegistry.RegisterRequestModel(missingGetter);
    }

    [Fact]
    public void MultipartDeclaration_BindsExplicitAndDefaultParts()
    {
        var metadata = RequestModelMetadata.Get(typeof(DeclaredMultipartRequest));

        Assert.True(metadata.IsMultipart);
        Assert.Equal(["upload", "note"], metadata.PartBindings.Select(static part => part.Name));

        var upload = metadata.PartBindings[0];
        Assert.True(upload.HasExplicitName);
        Assert.Equal("a.bin", upload.FileName);
        Assert.Equal("application/octet-stream", upload.ContentType);

        Assert.False(metadata.PartBindings[1].HasExplicitName);
        Assert.Null(metadata.PartBindings[1].FileName);
        Assert.Null(metadata.PartBindings[1].ContentType);
    }

    [Fact]
    public void DefaultBodyDeclaration_MovesSolePayloadOutOfQuery()
    {
        var request = new DeclaredDefaultBodyRequest { Payload = new Person("n", 1) };
        var metadata = RequestModelMetadata.Get(typeof(DeclaredDefaultBodyRequest));

        Assert.Same(request.Payload, metadata.ResolveBody(request));
        Assert.Null(metadata.ResolveQuery(request, formatter: null));
    }

    [Fact]
    public void AuthorizationDeclarations_MapToRequiredAndAnonymous()
    {
        var required = RequestModelMetadata.Get(typeof(DeclaredAuthorizeRequest));
        Assert.Equal(ApiAuthorizationMode.Required, required.Authorization.Mode);
        Assert.Equal("Basic", required.Authorization.Scheme);
        Assert.Equal(HttpMethod.Get, required.Method);

        var anonymous = RequestModelMetadata.Get(typeof(DeclaredAnonymousRequest));
        Assert.Equal(ApiAuthorizationMode.Anonymous, anonymous.Authorization.Mode);
    }

    [Fact]
    public void StaticHeaders_ApplyBeforeMemberHeaders_AndRemoveByName()
    {
        var config = new AxiosConfig();
        config.Headers.Set("X-Removed", "old");

        var metadata = RequestModelMetadata.Get(typeof(DeclaredHeadersRequest));
        metadata.ApplyStaticHeaders(config);

        Assert.Equal("yes", config.Headers["X-Static"]);
        Assert.Contains("X-Removed", config.RemoveHeaders);

        metadata.ApplyHeaders(new DeclaredHeadersRequest { Token = "t" }, config);
        Assert.Equal("t", config.Headers["X-Static"]);
    }

    [Theory]
    [InlineData(typeof(DeclaredIgnoreConflictRequest), "[ApiIgnore]")]
    [InlineData(typeof(DeclaredMissingGetterRequest), "缺少取值实现")]
    [InlineData(typeof(DeclaredPartWithoutMultipartRequest), "没有 [Multipart]")]
    [InlineData(typeof(DeclaredDuplicatePartRequest), "同名")]
    [InlineData(typeof(DeclaredMultipartBodyRequest), "互斥")]
    [InlineData(typeof(DeclaredBlankRouteRequest), "缺少路由特性")]
    [InlineData(typeof(DeclaredBlankSchemeRequest), "授权 scheme 不能为空白")]
    [InlineData(typeof(DeclaredAuthorizationConflictRequest), "授权与匿名互斥")]
    [InlineData(typeof(DeclaredBlankPartNameRequest), "[Part].Name 不能为空白")]
    [InlineData(typeof(DeclaredBlankPartContentTypeRequest), "[Part].ContentType 不能为空白")]
    [InlineData(typeof(DeclaredBlankStaticHeaderRequest), "含空白声明")]
    [InlineData(typeof(DeclaredBadStaticHeaderRequest), "缺少头名")]
    [InlineData(typeof(DeclaredDuplicatePathRequest), "有多个 Path 绑定")]
    public void BrokenDeclaration_ReportsOnFirstUse(Type requestType, string expected)
    {
        var error = Assert.Throws<InvalidOperationException>(() => RequestModelMetadata.Get(requestType));

        Assert.Contains(expected, error.Message);
    }
}

public sealed class DeclaredMultipartRequest
{
    public int Id { get; set; }

    public Stream? File { get; set; }

    public string? Note { get; set; }
}

public sealed class DeclaredDefaultBodyRequest
{
    public Person Payload { get; set; } = null!;
}

public sealed class DeclaredAuthorizeRequest;

public sealed class DeclaredAnonymousRequest;

public sealed class DeclaredHeadersRequest
{
    public string? Token { get; set; }
}

public sealed class DeclaredPartWithoutMultipartRequest
{
    public Stream? File { get; set; }
}

public sealed class DeclaredDuplicatePartRequest;

public sealed class DeclaredMultipartBodyRequest;

public sealed class DeclaredBlankRouteRequest;

public sealed class DeclaredBlankSchemeRequest;

public sealed class DeclaredAuthorizationConflictRequest;

public sealed class DeclaredBlankPartNameRequest;

public sealed class DeclaredBlankPartContentTypeRequest;

public sealed class DeclaredBlankStaticHeaderRequest;

public sealed class DeclaredBadStaticHeaderRequest;

public sealed class DeclaredDuplicatePathRequest;

public sealed class DeclaredIgnoreConflictRequest
{
    public int Id { get; set; }
}

public sealed class DeclaredMissingGetterRequest;
