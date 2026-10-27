using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Requests;

/// <summary>
/// 响应状态文本：.NET Core 不再提供 reason phrase，库用与 axios 一致的映射表补齐，
/// 表外状态码回退到 <see cref="System.Net.HttpStatusCode"/> 枚举名，再取不到就留空。
/// </summary>
public sealed class StatusTextTests
{
    [Theory]
    [InlineData(201, "Created")]
    [InlineData(202, "Accepted")]
    [InlineData(203, "Non-Authoritative Information")]
    [InlineData(204, "No Content")]
    [InlineData(205, "Reset Content")]
    [InlineData(206, "Partial Content")]
    [InlineData(301, "Moved Permanently")]
    [InlineData(302, "Found")]
    [InlineData(303, "See Other")]
    [InlineData(304, "Not Modified")]
    [InlineData(307, "Temporary Redirect")]
    [InlineData(308, "Permanent Redirect")]
    [InlineData(400, "Bad Request")]
    [InlineData(401, "Unauthorized")]
    [InlineData(403, "Forbidden")]
    [InlineData(404, "Not Found")]
    [InlineData(405, "Method Not Allowed")]
    [InlineData(406, "Not Acceptable")]
    [InlineData(407, "Proxy Authentication Required")]
    [InlineData(408, "Request Timeout")]
    [InlineData(409, "Conflict")]
    [InlineData(410, "Gone")]
    [InlineData(412, "Precondition Failed")]
    [InlineData(413, "Payload Too Large")]
    [InlineData(414, "URI Too Long")]
    [InlineData(415, "Unsupported Media Type")]
    [InlineData(422, "Unprocessable Entity")]
    [InlineData(429, "Too Many Requests")]
    [InlineData(500, "Internal Server Error")]
    [InlineData(501, "Not Implemented")]
    [InlineData(502, "Bad Gateway")]
    [InlineData(503, "Service Unavailable")]
    [InlineData(504, "Gateway Timeout")]
    public async Task StatusText_MappedCode_MatchesAxiosWording(int statusCode, string expected)
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(statusCode, Body: Array.Empty<byte>()));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url + "status", c => c.Callbacks.ValidateStatus = _ => true);

        Assert.Equal(statusCode, response.Status);
        Assert.Equal(expected, response.StatusText);
    }

    [Fact]
    public async Task StatusText_UnmappedButKnownCode_FallsBackToEnumName()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(411, Body: Array.Empty<byte>()));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url + "teapot", c => c.Callbacks.ValidateStatus = _ => true);

        Assert.Equal("LengthRequired", response.StatusText);
    }

    [Fact]
    public async Task StatusText_UnknownCode_IsEmpty()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(599, Body: Array.Empty<byte>()));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url + "unknown", c => c.Callbacks.ValidateStatus = _ => true);

        Assert.Equal(string.Empty, response.StatusText);
    }
}
