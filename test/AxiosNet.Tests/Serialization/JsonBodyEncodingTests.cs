using System.Text;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Serialization;

/// <summary>
/// JSON 请求正文的编码契约：默认走 UTF-8 字节出口；显式声明的 charset 与 <c>Serialization.TextEncoding</c>
/// 仍然决定正文编码与 <c>Content-Type</c> 头，不会被字节出口悄悄改成 UTF-8。
/// </summary>
/// <remarks>
/// 载荷只用 ASCII 字段值：System.Text.Json 默认会把非 ASCII 转义成 <c>\uXXXX</c>，
/// 转义策略不构成本用例要守的契约，这里只钉「正文按哪个编码写出」。
/// </remarks>
public sealed class JsonBodyEncodingTests
{
    /// <summary>正文在 UTF-8 / UTF-16 下都应解出的文本（ASCII 载荷，不受转义策略影响）。</summary>
    private const string ExpectedJson = "{\"name\":\"ada\",\"age\":36}";

    [Fact]
    public async Task JsonBody_DefaultConfig_IsUtf8WithJsonContentType()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(200));

        using var client = new AxiosClient();
        var response = await client.PostAsync<string>(server.Url + "x", new Person("ada", 36));

        var request = Assert.Single(server.Received);
        Assert.Equal("application/json", request.Headers["Content-Type"]);
        Assert.Equal(ExpectedJson, Encoding.UTF8.GetString(request.Body));
        Assert.Equal(200, response.Status);
    }

    [Fact]
    public async Task JsonBody_DeclaredNonUtf8Charset_IsEncodedWithThatCharset()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(200));

        using var client = new AxiosClient();
        await client.PostAsync<string>(server.Url + "x", new Person("ada", 36), c =>
        {
            // 声明的 charset 优先于库默认的 UTF-8：正文必须按它编码，否则服务端解出乱码。
            c.ContentType = "application/json; charset=utf-16";
        });

        var request = Assert.Single(server.Received);
        Assert.Equal("application/json; charset=utf-16", request.Headers["Content-Type"]);
        Assert.Equal(ExpectedJson, Encoding.Unicode.GetString(request.Body));
    }

    [Fact]
    public async Task JsonBody_ExplicitTextEncoding_KeepsCharsetHeader()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(200));

        using var client = new AxiosClient();
        await client.PostAsync<string>(server.Url + "x", new Person("ada", 36), c =>
        {
            // 显式编码仍由文本路径负责：头里要能看出正文是按什么编码写的。
            c.Serialization.TextEncoding = Encoding.UTF8;
        });

        var request = Assert.Single(server.Received);
        Assert.Equal("application/json; charset=utf-8", request.Headers["Content-Type"]);
        Assert.Equal(ExpectedJson, Encoding.UTF8.GetString(request.Body));
    }

    /// <summary>请求正文的载荷形状：一个字符串 + 一个数字。</summary>
    private sealed record Person(string Name, int Age);
}
