using System.Net.Http.Headers;
using System.Text;
using AxiosNet.Internal;

namespace AxiosNet.Tests.Requests;

/// <summary>
/// 请求侧 Content-Type 的解析细节（<see cref="RequestContentType"/>）：补 charset 的条件、读回 charset、
/// 以及从媒体类型参数里取 boundary。这些都是「写法不规范但语义清楚」的输入，解析必须与其一致。
/// </summary>
public sealed class ContentTypeParsingTests
{
    [Fact]
    public void Compose_TextualTypeWithoutCharset_AppendsRequestEncoding()
    {
        Assert.Equal("text/plain; charset=utf-16", RequestContentType.Compose("text/plain", Encoding.Unicode, appendCharset: true));
    }

    [Fact]
    public void Compose_DoesNotAppendWhenCallerSaysNo()
    {
        Assert.Equal("text/plain", RequestContentType.Compose("text/plain", Encoding.Unicode, appendCharset: false));
    }

    [Fact]
    public void Compose_NonTextualType_IsNeverAppended()
    {
        Assert.Equal("image/png", RequestContentType.Compose("image/png", Encoding.Unicode, appendCharset: true));
    }

    [Fact]
    public void Compose_ExistingCharsetWithSpaces_IsRespectedNotDuplicated()
    {
        // 标准解析能认出来（不是朴素字符串查找），因此不会再补一个 charset
        Assert.Equal(
            "text/plain; charset = utf-8",
            RequestContentType.Compose("text/plain; charset = utf-8", Encoding.Unicode, appendCharset: true));
    }

    [Fact]
    public void Compose_MediaTypeThatMerelyMentionsCharset_StillGetsCharset()
    {
        // 只有 "charset" 字样、不是参数：快速排除与朴素查找都应判为「没有 charset」
        Assert.Equal(
            "text/x-charset-notes; charset=utf-16",
            RequestContentType.Compose("text/x-charset-notes", Encoding.Unicode, appendCharset: true));
    }

    [Fact]
    public void GetCharsetEncoding_ReadsDeclaredCharset()
    {
        Assert.Equal(Encoding.UTF8, RequestContentType.GetCharsetEncoding("application/json; charset=utf-8"));
        Assert.Equal(Encoding.Latin1, RequestContentType.GetCharsetEncoding("text/plain; charset=\"iso-8859-1\""));
    }

    [Fact]
    public void GetCharsetEncoding_UndecidableInputs_ReturnNull()
    {
        Assert.Null(RequestContentType.GetCharsetEncoding("text/plain"));

        // 连媒体类型都解析不出来
        Assert.Null(RequestContentType.GetCharsetEncoding("not a media type///"));

        // charset 参数存在但值为空
        Assert.Null(RequestContentType.GetCharsetEncoding("text/plain; charset="));

        // 平台不认识的 charset 名：不在解析阶段判定
        Assert.Null(RequestContentType.GetCharsetEncoding("text/plain; charset=x-unknown-charset"));
    }

    [Fact]
    public void ExtractBoundary_HandlesQuotedAndParameterisedForms()
    {
        Assert.Null(RequestContentType.ExtractBoundary("multipart/form-data"));

        Assert.Equal("plain-boundary", RequestContentType.ExtractBoundary("multipart/form-data; boundary=plain-boundary"));

        // 带引号：引号本身不进 boundary
        Assert.Equal("abc", RequestContentType.ExtractBoundary("multipart/form-data; boundary=\"abc\""));

        // 引号未闭合：判定为没有 boundary，而不是切出半个值
        Assert.Null(RequestContentType.ExtractBoundary("multipart/form-data; boundary=\""));

        // boundary 之后还有别的参数
        Assert.Equal("abc", RequestContentType.ExtractBoundary("multipart/form-data; boundary=abc; charset=utf-8"));
    }

    [Fact]
    public void HasMultipartBoundary_RequiresMultipartMediaTypeAndBoundaryParameter()
    {
        var withBoundary = new MediaTypeHeaderValue("multipart/form-data") { Parameters = { new NameValueHeaderValue("boundary", "abc") } };
        var withoutBoundary = new MediaTypeHeaderValue("multipart/form-data");
        var jsonWithBoundary = new MediaTypeHeaderValue("application/json") { Parameters = { new NameValueHeaderValue("boundary", "abc") } };

        Assert.True(RequestContentType.HasMultipartBoundary(withBoundary));
        Assert.False(RequestContentType.HasMultipartBoundary(withoutBoundary));
        Assert.False(RequestContentType.HasMultipartBoundary(jsonWithBoundary));
        Assert.False(RequestContentType.HasMultipartBoundary(null));
    }
}
