using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;

namespace AxiosNet.Tests.WebAPI;

/// <summary>
/// 方法级 `[BodyContentType]` / `[QueryCollectionFormat]`：覆盖正文编码与查询集合格式；
/// 非法取值在契约构建期报错（带接口 / 方法上下文），multipart 冲突由校验拦截，声明不跨调用泄漏、继承接口同样生效。
/// </summary>
public sealed class MethodLevelEncodingTests
{
    [Fact]
    public async Task BodyContentType_FormUrlEncoded_EncodesBodyAndHeader()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = CreateClient(server);

        await client.Create<IEncodingApi>().SubmitFormAsync(new FormPayload { Name = "alice", Count = 2 });

        var received = Assert.Single(server.Received);
        Assert.Equal("application/x-www-form-urlencoded", received.Headers["Content-Type"]);
        Assert.Equal("Name=alice&Count=2", System.Text.Encoding.UTF8.GetString(received.Body));
    }

    [Fact]
    public async Task BodyContentType_WithCharset_IsSentVerbatim()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = CreateClient(server);

        await client.Create<IEncodingApi>().SubmitFormUtf8Async(new FormPayload { Name = "alice", Count = 2 });

        var received = Assert.Single(server.Received);
        Assert.Equal("application/x-www-form-urlencoded; charset=utf-8", received.Headers["Content-Type"]);
        Assert.Equal("Name=alice&Count=2", System.Text.Encoding.UTF8.GetString(received.Body));
    }

    [Fact]
    public async Task QueryCollectionFormat_Multi_RepeatsKey()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = CreateClient(server);

        await client.Create<IEncodingApi>().SearchMultiAsync(["a", "b"]);

        var query = Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty);
        Assert.Equal("tags=a&tags=b", query);
    }

    [Fact]
    public async Task QueryCollectionFormat_OverridesInstanceDefault()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url, defaults => defaults.Serialization.CollectionFormat = AxiosCollectionFormat.Csv);

        await client.Create<IEncodingApi>().SearchMultiAsync(["a", "b"]);

        var query = Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty);
        Assert.Equal("tags=a&tags=b", query);
    }

    [Fact]
    public async Task QueryCollectionFormat_WithoutAttribute_KeepsBrackets()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = CreateClient(server);

        await client.Create<IEncodingApi>().SearchDefaultAsync(["a", "b"]);

        var query = Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty);
        Assert.Equal("tags[]=a&tags[]=b", query);
    }

    [Fact]
    public void MultipartWithBodyContentType_ReportedByValidation()
    {
        var errors = ApiClient.ValidateInterfaces(typeof(IMultipartContentTypeConflictApi));

        var error = Assert.Single(errors);
        Assert.Contains("[Multipart]", error);
        Assert.Contains("[BodyContentType]", error);
    }

    [Fact]
    public void InvalidBodyContentType_ReportedWithMethodContext()
    {
        // 取值在契约构建期校验：错误必须带接口 / 方法名，而不是属性构造期抛出的无上下文异常。
        var errors = ApiClient.ValidateInterfaces(typeof(IInvalidBodyContentTypeApi));

        Assert.Contains(errors, static error
            => error.Contains("[BodyContentType]") && error.Contains("IInvalidBodyContentTypeApi") && error.Contains("GetAsync"));
    }

    [Fact]
    public void UndefinedQueryCollectionFormat_ReportedWithMethodContext()
    {
        var errors = ApiClient.ValidateInterfaces(typeof(IInvalidCollectionFormatApi));

        Assert.Contains(errors, static error
            => error.Contains("[QueryCollectionFormat]") && error.Contains("集合格式值 99") && error.Contains("IInvalidCollectionFormatApi"));
    }

    [Fact]
    public async Task DeclaredEncoding_DoesNotLeakBetweenCalls()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);
        var api = client.Create<IEncodingLeakApi>();

        await api.SubmitFormAsync(new FormPayload { Name = "alice", Count = 2 });
        await api.SubmitJsonAsync(new FormPayload { Name = "alice", Count = 2 });

        Assert.Equal("application/x-www-form-urlencoded", server.Received[0].Headers["Content-Type"]);
        Assert.StartsWith("application/json", server.Received[1].Headers["Content-Type"]);
    }

    [Fact]
    public async Task InheritedInterfaceMethod_KeepsEncodingAttributes()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);
        var api = client.Create<IDerivedEncodingApi>();

        await api.BaseSearchAsync(["a", "b"]);
        await api.BaseSubmitAsync(new FormPayload { Name = "a", Count = 1 });

        Assert.Equal("tags=a&tags=b", Uri.UnescapeDataString(server.Received[0].Query!));
        Assert.Equal("application/x-www-form-urlencoded", server.Received[1].Headers["Content-Type"]);
    }

    private static ApiClient CreateClient(TestHttpServer server) => new(server.Url);
}

public sealed class FormPayload
{
    public string Name { get; set; } = string.Empty;

    public int Count { get; set; }
}

public interface IEncodingApi
{
    [Post("form")]
    [BodyContentType("application/x-www-form-urlencoded")]
    Task<AxiosResponse<Person>> SubmitFormAsync([Body] FormPayload payload);

    [Post("form-utf8")]
    [BodyContentType("application/x-www-form-urlencoded; charset=utf-8")]
    Task<AxiosResponse<Person>> SubmitFormUtf8Async([Body] FormPayload payload);

    [Get("search-multi")]
    [QueryCollectionFormat(AxiosCollectionFormat.Multi)]
    Task<AxiosResponse<Person>> SearchMultiAsync([Query] string[] tags);

    [Get("search-default")]
    Task<AxiosResponse<Person>> SearchDefaultAsync([Query] string[] tags);
}

public interface IMultipartContentTypeConflictApi
{
    [Post("upload")]
    [Multipart]
    [BodyContentType("application/x-www-form-urlencoded")]
    Task<AxiosResponse<Person>> UploadAsync(string name);
}

public interface IInvalidBodyContentTypeApi
{
    [Get("x")]
    [BodyContentType(" ")]
    Task<AxiosResponse<Person>> GetAsync();
}

public interface IInvalidCollectionFormatApi
{
    [Get("x")]
    [QueryCollectionFormat((AxiosCollectionFormat)99)]
    Task<AxiosResponse<Person>> GetAsync([Query] string[] tags);
}

public interface IEncodingLeakApi
{
    [Post("form")]
    [BodyContentType("application/x-www-form-urlencoded")]
    Task<AxiosResponse<Person>> SubmitFormAsync([Body] FormPayload payload);

    [Post("json")]
    Task<AxiosResponse<Person>> SubmitJsonAsync([Body] FormPayload payload);
}

public interface IBaseEncodingApi
{
    [Get("base-search")]
    [QueryCollectionFormat(AxiosCollectionFormat.Multi)]
    Task<AxiosResponse<Person>> BaseSearchAsync([Query] string[] tags);

    [Post("base-form")]
    [BodyContentType("application/x-www-form-urlencoded")]
    Task<AxiosResponse<Person>> BaseSubmitAsync([Body] FormPayload payload);
}

public interface IDerivedEncodingApi : IBaseEncodingApi
{
}
