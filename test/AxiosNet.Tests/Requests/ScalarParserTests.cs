using AxiosNet.Internal;

namespace AxiosNet.Tests.Requests;

/// <summary>
/// 裸标量文本的兜底解析（<see cref="ScalarParser"/>）：响应体不是合法 JSON 但语义上是标量时的唯一入口。
/// 这里压的是「解析失败必须返回 false 而不是抛异常」这条契约——失败后由上层决定报错形态。
/// </summary>
public sealed class ScalarParserTests
{
    [Fact]
    public void String_ReturnsOriginalTextWithoutTrimming()
    {
        // 字符串分支按原样交付：调用方要的是正文本身，不是「看起来像数字的正文」
        Assert.True(ScalarParser.TryParse("  padded  ", typeof(string), out var value));
        Assert.Equal("  padded  ", value);
    }

    [Theory]
    [InlineData("maybe", typeof(bool))]
    [InlineData("not-a-date", typeof(DateTime))]
    [InlineData("not-a-date", typeof(DateTimeOffset))]
    [InlineData("not-a-span", typeof(TimeSpan))]
    [InlineData("2024-13-45", typeof(DateOnly))]
    [InlineData("99:99", typeof(TimeOnly))]
    [InlineData("not-a-guid", typeof(Guid))]
    public void UnparsableText_ReturnsFalseWithoutThrowing(string text, Type type)
    {
        Assert.False(ScalarParser.TryParse(text, type, out var value));
        Assert.Null(value);
    }

    [Fact]
    public void MalformedAbsoluteUri_ReturnsFalse()
    {
        // 方括号未闭合：Uri 构造失败，必须回 false 而不是抛出
        Assert.False(ScalarParser.TryParse("http://[::1", typeof(Uri), out var value));
        Assert.Null(value);
    }

    [Fact]
    public void NullableTarget_ParsesIntoUnderlyingType()
    {
        Assert.True(ScalarParser.TryParse("42", typeof(int?), out var value));
        Assert.Equal(42, value);
    }

    [Fact]
    public void EnumWithoutNumericMatch_ReturnsFalse()
    {
        Assert.False(ScalarParser.TryParse("999", typeof(AxiosStreamFormat), out _));
    }
}
