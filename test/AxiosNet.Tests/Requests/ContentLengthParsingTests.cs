using System.Net.Http.Headers;
using AxiosNet.Internal;

namespace AxiosNet.Tests.Requests;

/// <summary>
/// <c>Content-Length</c> 原始值读取（<see cref="ContentLengthHeader"/>）：只认「恰好一条、非负十进制」，
/// 其余形态一律按长度未知处理；读取本身不解析类型化头、不分配。
/// </summary>
public sealed class ContentLengthParsingTests
{
    [Fact]
    public void ReadRaw_SingleValue_ParsesBytes()
    {
        Assert.Equal((long?)0, ReadRaw("0"));
        Assert.Equal((long?)23, ReadRaw("23"));
        Assert.Equal((long?)23, ReadRaw("00023"));
        Assert.Equal((long?)long.MaxValue, ReadRaw(long.MaxValue.ToString()));
    }

    [Fact]
    public void ReadRaw_SurroundingWhitespace_IsTrimmed()
    {
        Assert.Equal((long?)42, ReadRaw("  42  "));
        Assert.Equal((long?)42, ReadRaw("\t42\t"));
    }

    [Fact]
    public void ReadRaw_AbsentHeader_ReturnsNull_EvenWhenLengthIsComputable()
    {
        // 内存正文能算出长度（23 字节），但头里没有它就返回 null：这个读取器只报「声明值」，不替调用方算。
        var content = new ByteArrayContent(new byte[23]);

        Assert.Null(ContentLengthHeader.ReadRaw(content.Headers));
        Assert.Null(ContentLengthHeader.ReadRaw(headers: null));
    }

    [Fact]
    public void ReadRaw_MultipleValues_ReturnNull()
    {
        var content = new ByteArrayContent(new byte[23]);
        content.Headers.TryAddWithoutValidation("Content-Length", new[] { "23", "23" });

        // 口径不唯一（含被合并成「23, 23」的形态）：按未知处理，不猜一个数出来。
        Assert.Null(ContentLengthHeader.ReadRaw(content.Headers));
    }

    [Fact]
    public void ReadRaw_InvalidValues_ReturnNull()
    {
        Assert.Null(ReadRaw("abc"));
        Assert.Null(ReadRaw("-1"));
        Assert.Null(ReadRaw("+1"));
        Assert.Null(ReadRaw("1.5"));
        Assert.Null(ReadRaw("1e3"));
        Assert.Null(ReadRaw("12 34"));

        // 超出 long 范围：不截断、不饱和。
        Assert.Null(ReadRaw("99999999999999999999"));
    }

    [Fact]
    public void ReadRaw_RepeatedReads_DoNotAllocate()
    {
        // 每次响应只读一次：按「一批新内容、每份只读一次」量，类型化读取这里会付 56 B 的解析缓存。
        const int count = 2_000;
        var contents = new HttpContent[count];
        for (var i = 0; i < count; i++)
        {
            var content = new ByteArrayContent(new byte[23]);
            content.Headers.TryAddWithoutValidation("Content-Length", "23");
            contents[i] = content;
        }

        Assert.Equal((long?)23, ContentLengthHeader.ReadRaw(contents[0].Headers)); // 预热

        var before = GC.GetAllocatedBytesForCurrentThread();
        long total = 0;
        for (var i = 0; i < count; i++)
        {
            total += ContentLengthHeader.ReadRaw(contents[i].Headers) ?? 0;
        }

        var average = (GC.GetAllocatedBytesForCurrentThread() - before) / count;
        Assert.Equal(23 * count, total);
        Assert.True(average == 0, $"Content-Length 原始读取平均 {average} B/次，超出 0 B 护栏——疑似又走回了类型化头解析。");
    }

    /// <summary>造一份只带一个 <c>Content-Length</c> 取值的内容并读回。</summary>
    private static long? ReadRaw(string value)
    {
        var content = new ByteArrayContent(Array.Empty<byte>());
        content.Headers.TryAddWithoutValidation("Content-Length", value);
        return ContentLengthHeader.ReadRaw(content.Headers);
    }
}
