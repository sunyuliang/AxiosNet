using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Attributes;
using System.Text;

namespace AxiosNet.Tests.WebAPI;

/// <summary>
/// 请求模型的类 / 属性级编码声明：<c>[BodyContentType]</c> / <c>[QueryCollectionFormat]</c> / <c>[Stream]</c>。
/// 属性和类同时显式声明且取值不同 → <c>ERR_BAD_OPTION_VALUE</c>，不静默取一个。
/// </summary>
public sealed class ModelEncodingDeclarationTests
{
    [Fact]
    public async Task ClassLevelBodyContentType_Applies()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new ClassBodyModel { Payload = "hi" });

        var received = Assert.Single(server.Received);
        Assert.Equal("text/plain", received.Headers["Content-Type"]);
    }

    [Fact]
    public async Task ClassLevelQueryCollectionFormat_Applies()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new ClassQueryModel { Tags = ["a", "b"] });

        Assert.Equal("Tags=a&Tags=b", Assert.Single(server.Received).Query);
    }

    [Fact]
    public async Task ClassLevelStreamFormat_Applies()
    {
        await using var server = TestHttpServer.Start(
            _ => new TestResponse
            {
                Body = Encoding.UTF8.GetBytes("data: \"a\"\n\ndata: \"b\"\n\n"),
                ContentType = "text/event-stream",
            });
        using var client = new ApiClient(server.Url);

        var response = await client.RequestStreamAsync(new ClassStreamModel());
        Assert.NotNull(response.Data);
        var frames = new List<string>();
        await foreach (var frame in response.Data)
        {
            frames.Add(frame);
        }

        Assert.Equal(["a", "b"], frames);
    }

    [Fact]
    public async Task PropertyLevelDeclarations_ApplyWithoutClassLevel()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new PropertyBodyModel { Payload = "hi" });
        await client.RequestAsync(new PropertyQueryModel { Tags = ["a", "b"] });

        Assert.Equal("application/json", server.Received[0].Headers["Content-Type"]);
        Assert.Equal("Tags=a&Tags=b", server.Received[1].Query);
    }

    [Fact]
    public async Task PropertyLevelStreamDeclaration_AppliesWithoutClassLevel()
    {
        await using var server = TestHttpServer.Start(
            _ => new TestResponse
            {
                Body = Encoding.UTF8.GetBytes("[\"a\",\"b\"]"),
                ContentType = "application/json",
            });
        using var client = new ApiClient(server.Url);

        var response = await client.RequestStreamAsync(new PropertyStreamModel());
        Assert.NotNull(response.Data);
        var frames = new List<string>();
        await foreach (var frame in response.Data)
        {
            frames.Add(frame);
        }

        Assert.Equal(["a", "b"], frames);
    }

    [Fact]
    public async Task ClassAndPropertyWithSameValue_AreAccepted()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new SameValueModel { Tags = ["a", "b"] });

        Assert.Equal("Tags=a&Tags=b", Assert.Single(server.Received).Query);
    }

    [Fact]
    public async Task BodyContentType_ClassAndPropertyConflict_ThrowsBeforeSend()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new ApiClient(server.Url);

        var error = await Assert.ThrowsAsync<AxiosException>(() =>
            client.RequestAsync(new ConflictingBodyModel { Payload = "hi" }));

        Assert.Equal(AxiosException.ErrBadOptionValue, error.Code);
        Assert.Contains("[BodyContentType]", error.Message);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task QueryCollectionFormat_ClassAndPropertyConflict_ThrowsBeforeSend()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new ApiClient(server.Url);

        var error = await Assert.ThrowsAsync<AxiosException>(() =>
            client.RequestAsync(new ConflictingQueryModel { Tags = ["a", "b"] }));

        Assert.Equal(AxiosException.ErrBadOptionValue, error.Code);
        Assert.Contains("[QueryCollectionFormat]", error.Message);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Stream_ClassAndPropertyConflict_ThrowsBeforeSend()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new ApiClient(server.Url);

        var error = await Assert.ThrowsAsync<AxiosException>(() =>
            client.RequestAsync(new ConflictingStreamModel()));

        Assert.Equal(AxiosException.ErrBadOptionValue, error.Code);
        Assert.Contains("[Stream]", error.Message);
        Assert.Empty(server.Received);
    }

    // ---- 模型 ------------------------------------------------------------------

    [Post("class-body")]
    [BodyContentType("text/plain")]
    private sealed class ClassBodyModel : IApiRequest<string>
    {
        [Body]
        public string? Payload { get; set; }
    }

    [Get("class-query")]
    [QueryCollectionFormat(AxiosCollectionFormat.Multi)]
    private sealed class ClassQueryModel : IApiRequest<string>
    {
        [Query]
        public string[]? Tags { get; set; }
    }

    [Get("class-stream")]
    [Stream(AxiosStreamFormat.ServerSentEvents)]
    private sealed class ClassStreamModel : IApiRequest<string>
    {
    }

    [Post("property-body")]
    private sealed class PropertyBodyModel : IApiRequest<string>
    {
        [Body]
        [BodyContentType("application/json")]
        public string? Payload { get; set; }
    }

    [Get("property-query")]
    private sealed class PropertyQueryModel : IApiRequest<string>
    {
        [Query]
        [QueryCollectionFormat(AxiosCollectionFormat.Multi)]
        public string[]? Tags { get; set; }
    }

    [Get("property-stream")]
    private sealed class PropertyStreamModel : IApiRequest<string>
    {
        [Query]
        [Stream(AxiosStreamFormat.JsonArray)]
        public string? Note { get; set; }
    }

    [Get("same-value")]
    [QueryCollectionFormat(AxiosCollectionFormat.Multi)]
    private sealed class SameValueModel : IApiRequest<string>
    {
        [Query]
        [QueryCollectionFormat(AxiosCollectionFormat.Multi)]
        public string[]? Tags { get; set; }
    }

    [Post("conflict-body")]
    [BodyContentType("application/json")]
    private sealed class ConflictingBodyModel : IApiRequest<string>
    {
        [Body]
        [BodyContentType("text/plain")]
        public string? Payload { get; set; }
    }

    [Get("conflict-query")]
    [QueryCollectionFormat(AxiosCollectionFormat.Brackets)]
    private sealed class ConflictingQueryModel : IApiRequest<string>
    {
        [Query]
        [QueryCollectionFormat(AxiosCollectionFormat.Multi)]
        public string[]? Tags { get; set; }
    }

    [Get("conflict-stream")]
    [Stream(AxiosStreamFormat.NewlineDelimitedJson)]
    private sealed class ConflictingStreamModel : IApiRequest<string>
    {
        [Query]
        [Stream(AxiosStreamFormat.ServerSentEvents)]
        public string? Note { get; set; }
    }
}
