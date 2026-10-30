using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using System.Reflection;

namespace AxiosNet.Tests.Errors;

/// <summary>
/// 错误码的枚举形式（<see cref="AxiosException.ErrorCode"/>）与 <see cref="ApiError{TError}"/> 的继承关系：
/// 内置常量必须全部有对应的枚举值，强类型错误必须能被 <c>catch (AxiosException)</c> 接住。
/// </summary>
public sealed class ErrorCodeTests
{
    [Fact]
    public void EveryBuiltInCode_MapsToDistinctNonUnknownValue()
    {
        var constants = typeof(AxiosException)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(constants);

        var mapped = constants.Select(AxiosErrorCodes.FromString).ToList();

        // 新增常量而忘了补枚举映射时，这里会红。
        Assert.DoesNotContain(AxiosErrorCode.Unknown, mapped);
        Assert.Equal(constants.Count, mapped.Distinct().Count());
    }

    [Fact]
    public void UnrecognizedCode_MapsToUnknown()
    {
        Assert.Equal(AxiosErrorCode.Unknown, AxiosErrorCodes.FromString(null));
        Assert.Equal(AxiosErrorCode.Unknown, AxiosErrorCodes.FromString("ERR_NOT_BUILT_IN"));
    }

    [Theory]
    [InlineData(400, AxiosErrorCode.BadRequest)]
    [InlineData(404, AxiosErrorCode.BadRequest)]
    [InlineData(500, AxiosErrorCode.BadResponse)]
    public async Task StatusErrors_ExposeTheEnumCode(int status, AxiosErrorCode expected)
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(status));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url));

        Assert.Equal(expected, ex.ErrorCode);
    }

    [Fact]
    public async Task Timeout_ExposesConnAbortedEnum()
    {
        await using var server = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(5_000);
            return TestJson.Response(new { ok = true });
        });

        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url, c => c.Timeout = TimeSpan.FromMilliseconds(100)));

        Assert.Equal(AxiosErrorCode.ConnAborted, ex.ErrorCode);
    }

    [Fact]
    public async Task ExternalCancellation_ExposesCanceledEnum()
    {
        await using var server = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(5_000);
            return TestJson.Response(new { ok = true });
        });

        using var client = new AxiosClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url, cancellationToken: cts.Token));

        Assert.Equal(AxiosErrorCode.Canceled, ex.ErrorCode);
    }

    [Fact]
    public async Task ApiError_IsAnAxiosException_AndKeepsTypedDetails()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(
            new { code = "E42", message = "field invalid" },
            statusCode: 400));

        using var client = new ApiClient(server.Url) { ErrorFactory = new ApiErrorFactory<Problem>() };

        // 关键点：只写 catch (AxiosException) 也能接住强类型错误。
        AxiosException? caught = null;
        try
        {
            await client.RequestAsync(new FailRequest());
        }
        catch (AxiosException ex)
        {
            caught = ex;
        }

        var typed = Assert.IsType<ApiError<Problem>>(caught);
        Assert.Equal(400, typed.Status);
        Assert.Equal("Bad Request", typed.StatusText);
        Assert.Equal(400, typed.Response.Status);
        Assert.Equal("field invalid", typed.Error?.Message);

        // 与原始异常同源：Code / ErrorCode / Config / Request 都从基类继承。
        Assert.Equal(AxiosException.ErrBadRequest, typed.Code);
        Assert.Equal(AxiosErrorCode.BadRequest, typed.ErrorCode);
        Assert.Same(typed.Original, typed.InnerException);
        Assert.Same(typed.Original.Config, typed.Config);
    }

    [Get("fail")]
    private sealed class FailRequest : IApiRequest<Problem>
    {
    }

    private sealed class Problem
    {
        public string? Code { get; set; }

        public string? Message { get; set; }
    }
}
