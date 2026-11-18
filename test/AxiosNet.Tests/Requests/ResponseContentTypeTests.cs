using System.Net.Http.Headers;
using System.Text;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Requests;

/// <summary>
/// 响应侧 <c>Content-Type</c> 的惰性解析（<see cref="ContentTypeHeader"/> + <see cref="AxiosResponseInfo"/>）：
/// 拆出来的媒体类型 / 字符集必须与 <see cref="MediaTypeHeaderValue"/> 逐字一致（这是快路径的立论），
/// 以及「没声明的就是 null」不能因为改成了惰性就变味。
/// </summary>
public sealed class ResponseContentTypeTests
{
    /// <summary>
    /// 差异护栏：快路径与 BCL 解析必须给出完全一样的结果——包括「解析不了就是两个 null」。
    /// 表里刻意混入引号、转义、多值、空参数、非法 token 等快路径**不接**的形态，
    /// 它们走 BCL 回落分支，结果仍然要一致。
    /// </summary>
    /// <param name="raw">原始头值。</param>
    [Theory]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("application/json; CHARSET=UTF-8")]
    [InlineData("application/json; Charset = Utf-8")]
    [InlineData("application/json;charset=UTF-8")]
    [InlineData("APPLICATION/JSON")]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    [InlineData("application/vnd.api+json;charset=utf-8")]
    [InlineData("text/plain ; charset = utf-8")]
    [InlineData("text/plain ;charset=utf-8")]
    [InlineData("application/json\t;\tcharset\t=\tutf-8")]
    [InlineData("application/json; charset = utf-8 ")]
    [InlineData("application/json; charset=utf-8 ;")]
    [InlineData("text/plain; charset=\"utf-8\"")]
    [InlineData("text/plain; charset=\"iso-8859-1\"; q=0.5")]
    [InlineData("application/json; boundary=--abc; charset=utf-8")]
    [InlineData("application/json; charset=gbk; charset=utf-8")]
    [InlineData("text/plain; q=0.5")]
    [InlineData("text/plain; charset=")]
    [InlineData("text/plain; charset")]
    [InlineData("application/json;")]
    [InlineData("application/json;;")]
    [InlineData("application/json; charset=utf-8, text/plain")]
    [InlineData("*/*")]
    [InlineData("json")]
    [InlineData("not a media type///")]
    [InlineData("text/plain; charset=\"utf-8\\\"x\"")]
    [InlineData("")]
    public void Split_MatchesBclParse(string raw)
    {
        var expected = MediaTypeHeaderValue.TryParse(raw, out var parsed)
            ? (parsed.MediaType, parsed.CharSet)
            : ((string?)null, (string?)null);

        Assert.Equal(expected, ContentTypeHeader.Split(raw));

        // 单值入口与整体拆分必须自洽（业务对象路径只用 MediaType，文本路径只用 CharSet）。
        Assert.Equal(expected.Item1, ContentTypeHeader.MediaTypeOf(raw));
        Assert.Equal(expected.Item2, ContentTypeHeader.CharSetOf(raw));
    }

    /// <summary>快路径真的走了「不构造 MediaTypeHeaderValue」那条：常见形态必须与 BCL 一致且不为 null。</summary>
    [Fact]
    public void Split_SimpleShape_KeepsMediaTypeAndCharSet()
    {
        Assert.Equal(
            ("application/json", "utf-8"),
            ContentTypeHeader.Split("application/json; charset=utf-8"));
    }

    /// <summary>惰性只解析一次：两次读到的必须是同一个字符串实例（否则等于每次都重新拆）。</summary>
    [Fact]
    public void ResponseInfo_ResolvesOnceAndCaches()
    {
        var info = new AxiosResponseInfo(200, "OK", new Dictionary<string, IEnumerable<string>>(), "text/plain; charset=utf-8");

        var mediaType = info.MediaType;
        var charSet = info.CharSet;

        Assert.Equal("text/plain", mediaType);
        Assert.Equal("utf-8", charSet);
        Assert.Same(mediaType, info.MediaType);
        Assert.Same(charSet, info.CharSet);
    }

    /// <summary>没有 Content-Type 时两者都是 null（惰性化不能把「没声明」变成「空串」）。</summary>
    [Fact]
    public void ResponseInfo_WithoutContentType_IsNull()
    {
        var info = new AxiosResponseInfo(204, "No Content", new Dictionary<string, IEnumerable<string>>(), rawContentType: null);

        Assert.Null(info.MediaType);
        Assert.Null(info.CharSet);
    }

    /// <summary>端到端：媒体类型与字符集从原始头值里拆出来，和以前一样挂在 <c>Config.Response</c> 上。</summary>
    [Fact]
    public async Task Response_ExposesMediaTypeAndCharSetThroughConfig()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes("""{"name":"lazy","age":1}"""),
            ContentType: "application/vnd.api+json; charset=utf-8"));

        using var client = new AxiosClient();

        var response = await client.GetAsync<Person>(server.Url + "typed");

        Assert.Equal("lazy", response.Data!.Name);
        Assert.Equal("application/vnd.api+json", response.Config!.Response!.MediaType);
        Assert.Equal("utf-8", response.Config.Response.CharSet);
    }

    /// <summary>
    /// 端到端：解析不出来的 <c>Content-Type</c> 与「没带」同一口径——按文本处理。
    /// 直接拿原始串做前缀匹配会把这种头判成二进制，JSON 正文就变成原始字节交出去。
    /// </summary>
    [Fact]
    public async Task Response_UnparsableContentType_IsStillTreatedAsText()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes("""{"name":"junk","age":3}"""),
            ContentType: "not a media type///"));

        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url + "junk");

        Assert.IsType<System.Text.Json.JsonElement>(response.Data);
        Assert.Null(response.Config!.Response!.MediaType);
    }

    /// <summary>端到端：声明的字符集真的参与解码（惰性只是把解析时机推后，不是把字符集丢掉）。</summary>
    [Fact]
    public async Task Response_DecodesBodyUsingDeclaredCharSet()
    {
        // 0xE9 是 ISO-8859-1 的 'é'；按 UTF-8 解会变成替换字符。
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: new byte[] { 0xE9 },
            ContentType: "text/plain; charset=iso-8859-1"));

        using var client = new AxiosClient();

        var response = await client.GetAsync<string>(server.Url + "latin1");

        Assert.Equal("é", response.Data);
    }

    /// <summary>短路响应没有内容头视图，只能从自建的头字典里取原始值：这条路径同样按惰性口径工作。</summary>
    [Fact]
    public async Task ShortCircuit_WithoutContentType_HasNoMediaType()
    {
        using var client = new AxiosClient();

        var response = await client.GetAsync<Person>(
            "http://shortcircuit.test/cached",
            config => config.ShortCircuit = AxiosShortCircuit.From(new Person("cached", 2)));

        Assert.Equal("cached", response.Data!.Name);
        Assert.Null(response.Config!.Response!.MediaType);
        Assert.Null(response.Config.Response.CharSet);
    }
}
