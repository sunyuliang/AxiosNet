using System.Net.Http;
using AxiosNet.Internal;

namespace AxiosNet.Compat.Tests;

/// <summary>
/// 兼容资产（netstandard2.0）上 <see cref="ContentLengthHeader"/> 的取值口径：没有 NonValidated 与 span 版 TryParse，
/// 走的是字符串分支，但「恰好一条、非负十进制、裁掉空白」的判定必须与现代资产一致。
/// </summary>
public sealed class ContentLengthParsingCompatTests
{
    [Fact]
    public void ReadRaw_ParsesSingleValueAndTrimsWhitespace()
    {
        Assert.Equal((long?)0, ReadRaw("0"));
        Assert.Equal((long?)23, ReadRaw("00023"));
        Assert.Equal((long?)42, ReadRaw("  42  "));
        Assert.Equal((long?)long.MaxValue, ReadRaw(long.MaxValue.ToString()));
    }

    [Fact]
    public void ReadRaw_RejectsAmbiguousAndInvalidValues()
    {
        Assert.Null(ReadRaw("abc"));
        Assert.Null(ReadRaw("-1"));
        Assert.Null(ReadRaw("+1"));
        Assert.Null(ReadRaw("1.5"));
        Assert.Null(ReadRaw("99999999999999999999"));
        Assert.Null(ContentLengthHeader.ReadRaw(headers: null));

        var twice = new ByteArrayContent(Array.Empty<byte>());
        twice.Headers.TryAddWithoutValidation("Content-Length", new[] { "23", "23" });
        Assert.Null(ContentLengthHeader.ReadRaw(twice.Headers));
    }

    /// <summary>造一份只带一个 <c>Content-Length</c> 取值的内容并读回。</summary>
    private static long? ReadRaw(string value)
    {
        var content = new ByteArrayContent(Array.Empty<byte>());
        content.Headers.TryAddWithoutValidation("Content-Length", value);
        return ContentLengthHeader.ReadRaw(content.Headers);
    }
}
