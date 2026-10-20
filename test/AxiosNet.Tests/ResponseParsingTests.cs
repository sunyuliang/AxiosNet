using System.Text;
using System.Text.Json;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 响应解析层的边角覆盖：非 JSON 裸标量的兜底解析、文本解码的 BOM/charset 优先级、
/// 状态码文本映射、以及泛型参数与 responseType 不匹配时的统一错误模型。
/// </summary>
public sealed class ResponseParsingTests
{
    private static TestHttpServer StartTextServer(string text, string contentType = "text/plain")
        => TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes(text),
            ContentType: contentType));

    // ---------------------------------------------------------------
    // ScalarParser：非 JSON 文本按目标类型兜底
    // ---------------------------------------------------------------

    [Fact]
    public async Task BareText_Bool_IsParsedCaseInsensitively()
    {
        // JSON 的布尔是小写 true/false，"True" 不是合法 JSON，走裸标量兜底
        await using var server = StartTextServer("True");
        using var client = new AxiosClient();

        var response = await client.GetAsync<bool>(server.Url);

        Assert.True(response.Data);
    }

    [Fact]
    public async Task BareText_Char_IsParsedFromFirstCharacter()
    {
        await using var server = StartTextServer("A");
        using var client = new AxiosClient();

        var response = await client.GetAsync<char>(server.Url);

        Assert.Equal('A', response.Data);
    }

    [Fact]
    public async Task BareText_Guid_WithBraces_IsParsed()
    {
        var guid = Guid.Parse("{12345678-1234-1234-1234-123456789012}");
        await using var server = StartTextServer("{12345678-1234-1234-1234-123456789012}");
        using var client = new AxiosClient();

        var response = await client.GetAsync<Guid>(server.Url);

        Assert.Equal(guid, response.Data);
    }

    [Fact]
    public async Task BareText_Enum_IsParsedIgnoringCase()
    {
        await using var server = StartTextServer("monday");
        using var client = new AxiosClient();

        var response = await client.GetAsync<DayOfWeek>(server.Url);

        Assert.Equal(DayOfWeek.Monday, response.Data);
    }

    [Fact]
    public async Task BareText_NullableEnum_IsUnwrapped()
    {
        await using var server = StartTextServer("Friday");
        using var client = new AxiosClient();

        var response = await client.GetAsync<DayOfWeek?>(server.Url);

        Assert.Equal(DayOfWeek.Friday, response.Data);
    }

    [Fact]
    public async Task BareText_Decimal_AcceptsGroupSeparators()
    {
        await using var server = StartTextServer("1,234.5");
        using var client = new AxiosClient();

        var response = await client.GetAsync<decimal>(server.Url);

        Assert.Equal(1234.5m, response.Data);
    }

    [Fact]
    public async Task BareText_Long_AcceptsExplicitPlusSign()
    {
        // JSON 数字不允许前导 '+'，Convert.ToInt64 也不收千分位（与 ToDecimal 的语义差异是 BCL 行为）
        await using var server = StartTextServer("+1000");
        using var client = new AxiosClient();

        var response = await client.GetAsync<long>(server.Url);

        Assert.Equal(1000L, response.Data);
    }

    [Fact]
    public async Task BareText_DateTimeOffset_IsParsedInvariantly()
    {
        await using var server = StartTextServer("2024-01-02T03:04:05+08:00");
        using var client = new AxiosClient();

        var response = await client.GetAsync<DateTimeOffset>(server.Url);

        Assert.Equal(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.FromHours(8)), response.Data);
    }

    [Fact]
    public async Task BareText_DateOnly_And_TimeOnly_AreParsed()
    {
        await using var dateServer = StartTextServer("2024-01-02");
        await using var timeServer = StartTextServer("03:04:05");
        using var client = new AxiosClient();

        Assert.Equal(new DateOnly(2024, 1, 2), (await client.GetAsync<DateOnly>(dateServer.Url)).Data);
        Assert.Equal(new TimeOnly(3, 4, 5), (await client.GetAsync<TimeOnly>(timeServer.Url)).Data);
    }

    [Fact]
    public async Task BareText_TimeSpan_And_Uri_AreParsed()
    {
        await using var spanServer = StartTextServer("01:02:03");
        await using var uriServer = StartTextServer("http://example.com/a");
        using var client = new AxiosClient();

        Assert.Equal(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(3),
            (await client.GetAsync<TimeSpan>(spanServer.Url)).Data);
        Assert.Equal(new Uri("http://example.com/a"), (await client.GetAsync<Uri>(uriServer.Url)).Data);
    }

    [Fact]
    public async Task BareText_Unparseable_WrappedAsBadResponse()
    {
        await using var server = StartTextServer("not-a-guid");
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<Guid>(server.Url));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
        Assert.IsAssignableFrom<JsonException>(ex.Cause);
    }

    [Fact]
    public async Task BareText_UnparseableNumber_WrappedAsBadResponse()
    {
        await using var server = StartTextServer("abc");
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<int>(server.Url));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
    }

    // ---------------------------------------------------------------
    // ResponseTextDecoder：charset / BOM / 强制编码的优先级
    // ---------------------------------------------------------------

    [Fact]
    public async Task Response_UnknownCharset_FallsBackToUtf8()
    {
        var body = Encoding.UTF8.GetBytes("你好");
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: body,
            ContentType: "text/plain; charset=x-unknown-charset"));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url, c => c.ResponseType = ResponseType.Text);

        Assert.Equal("你好", response.Data);
    }

    [Theory]
    [InlineData("utf-16be")]
    [InlineData("utf-32le")]
    [InlineData("utf-32be")]
    public async Task Response_Bom_IsDetected_ForAllUnicodeVariants(string variant)
    {
        var encoding = variant switch
        {
            "utf-16be" => Encoding.BigEndianUnicode,
            "utf-32le" => new UTF32Encoding(bigEndian: false, byteOrderMark: true),
            _ => new UTF32Encoding(bigEndian: true, byteOrderMark: true)
        };

        var body = encoding.GetPreamble().Concat(encoding.GetBytes("你好")).ToArray();
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: body,
            ContentType: "text/plain"));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url, c => c.ResponseType = ResponseType.Text);

        Assert.Equal("你好", response.Data);
    }

    [Fact]
    public async Task Response_ForcedEncoding_BeatsDeclaredCharset()
    {
        // 服务端谎报 charset=utf-8，实际字节是无 BOM 的 UTF-16：强制编码必须压过声明
        var body = Encoding.Unicode.GetBytes("你好");
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: body,
            ContentType: "text/plain; charset=utf-8"));
        using var client = new AxiosClient();

        var auto = await client.GetAsync(server.Url, c => c.ResponseType = ResponseType.Text);
        Assert.NotEqual("你好", auto.Data);

        var forced = await client.GetAsync(server.Url, c =>
        {
            c.ResponseType = ResponseType.Text;
            c.ResponseEncoding = Encoding.Unicode;
        });
        Assert.Equal("你好", forced.Data);
    }

    // ---------------------------------------------------------------
    // StatusTextMap：reason phrase 由库维护，不走线上
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(201, "Created")]
    [InlineData(204, "No Content")]
    [InlineData(422, "Unprocessable Entity")]
    [InlineData(429, "Too Many Requests")]
    [InlineData(502, "Bad Gateway")]
    public async Task StatusText_ForMappedCodes_ComesFromMap(int statusCode, string expected)
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }, statusCode));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url, c => c.ValidateStatus = _ => true);

        Assert.Equal(statusCode, response.Status);
        Assert.Equal(expected, response.StatusText);
    }

    [Fact]
    public async Task StatusText_ForCodeOutsideMapAndEnum_IsEmpty()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }, 599));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url, c => c.ValidateStatus = _ => true);

        Assert.Equal(599, response.Status);
        Assert.Equal(string.Empty, response.StatusText);
    }

    // ---------------------------------------------------------------
    // DataCast：泛型参数与 responseType 不匹配 → ERR_BAD_RESPONSE
    // ---------------------------------------------------------------

    [Fact]
    public async Task TextResponse_WithScalarGeneric_IsWrappedAsBadResponse()
    {
        await using var server = StartTextServer("hello");
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<int>(
            server.Url,
            c => c.ResponseType = ResponseType.Text));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
        Assert.IsAssignableFrom<InvalidCastException>(ex.Cause);
        Assert.Contains("responseType", ex.Cause!.Message);
    }
}
