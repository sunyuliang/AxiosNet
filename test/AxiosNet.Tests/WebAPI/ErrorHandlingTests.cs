
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;

namespace AxiosNet.Tests.WebAPI;

/// <summary>
/// 声明式错误：`ErrorFactory` 映射、`ErrorContext` 原文保留、网络错误不进入工厂。
/// </summary>
public sealed class ErrorHandlingTests
{
    // ------------------------------------------------------------------
    // 错误工厂 / 强类型错误
    // ------------------------------------------------------------------

    [Fact]
    public async Task ErrorFactory_ProducesTypedApiError_FromErrorBody()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(
            new { code = "E42", message = "field invalid" },
            statusCode: 400));
        using var client = new ApiClient(server.Url)
        {
            ErrorFactory = new ApiErrorFactory<ApiProblem>()
        };

        var error = await Assert.ThrowsAsync<ApiError<ApiProblem>>(
            () => client.RequestAsync(new FailRequest()));

        Assert.Equal(400, error.Status);
        Assert.Equal("Bad Request", error.StatusText);
        Assert.Equal("E42", error.Error?.Code);
        Assert.Equal("field invalid", error.Error?.Message);
        Assert.Equal(AxiosException.ErrBadRequest, error.Code);
        Assert.IsType<AxiosException>(error.InnerException);
        Assert.Equal(400, error.Response.Status);
    }

    [Fact]
    public async Task ErrorFactory_ReturningNull_KeepsAxiosException()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new { code = "E1" }, statusCode: 500));
        using var client = new ApiClient(server.Url)
        {
            ErrorFactory = new NoopErrorFactory()
        };

        var error = await Assert.ThrowsAsync<AxiosException>(
            () => client.RequestAsync(new FailRequest()));

        Assert.Equal(AxiosException.ErrBadResponse, error.Code);
        Assert.Equal(500, error.Status);
    }

    [Fact]
    public async Task ErrorFactory_NotInvokedForNetworkErrors()
    {
        var factory = new RecordingErrorFactory();
        using var client = new ApiClient("http://127.0.0.1:1/") { ErrorFactory = factory };

        var error = await Assert.ThrowsAsync<AxiosException>(
            () => client.RequestAsync(new FailRequest()));

        Assert.Equal(AxiosException.ErrNetwork, error.Code);
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public async Task ErrorContext_Body_KeepsRawText_EvenWhenDeclaredTypeParses()
    {
        // 错误体恰好能被声明类型解析：RawText 仍然可用（Data 已是对象，原文不能被消费掉）
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("error-body", 400), statusCode: 400));
        using var client = new ApiClient(server.Url) { ErrorFactory = new CapturingErrorFactory() };

        await Assert.ThrowsAsync<ApiError<Person>>(
            () => client.RequestAsync(new FailPersonRequest()));

        Assert.NotNull(CapturingErrorFactory.LastBody);
        Assert.Contains("error-body", CapturingErrorFactory.LastBody!);
    }

    // ------------------------------------------------------------------
    // 测试模型
    // ------------------------------------------------------------------


    [Get("fail")]
    private sealed class FailRequest : IApiRequest<Person>
    {
    }

    [Get("fail")]
    private sealed class FailPersonRequest : IApiRequest<Person>
    {
    }


    private sealed class ApiProblem
    {
        public string? Code { get; set; }

        public string? Message { get; set; }
    }

    private sealed class NoopErrorFactory : IApiErrorFactory
    {
        public ValueTask<Exception?> CreateAsync(ApiErrorContext context, CancellationToken cancellationToken)
            => new((Exception?)null);
    }

    private sealed class RecordingErrorFactory : IApiErrorFactory
    {
        public int Calls { get; private set; }

        public ValueTask<Exception?> CreateAsync(ApiErrorContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return new ValueTask<Exception?>((Exception?)null);
        }
    }

    private sealed class CapturingErrorFactory : IApiErrorFactory
    {
        public static string? LastBody { get; private set; }

        public async ValueTask<Exception?> CreateAsync(ApiErrorContext context, CancellationToken cancellationToken)
        {
            LastBody = context.Body;
            var error = await context.TryReadAsync<Person>(cancellationToken);
            return new ApiError<Person>(error, context);
        }
    }
}