namespace AxiosNet.Tests;

/// <summary>
/// 公共 API 的空参数守卫（此前会漏出裸 <see cref="NullReferenceException"/>）。
/// <para>
/// 头部名常来自配置项或循环变量，写错时报出参数名才能定位；而在请求管线深处
/// （配置合并的 <c>perCall.Headers.Count</c>）抛空引用，堆栈指向的是库内部，
/// 与真正写错的那一行赋值相隔很远。
/// </para>
/// </summary>
public sealed class NullArgumentGuardTests
{
    [Fact]
    public void HeaderLookups_WithNullName_ThrowArgumentNullException()
    {
        var headers = new AxiosHeaders();
        headers.Set("X-Trace", "abc");

        Assert.Equal("name", Assert.Throws<ArgumentNullException>(() => headers.Get(null!)).ParamName);
        Assert.Equal("name", Assert.Throws<ArgumentNullException>(() => headers.Has(null!)).ParamName);
        Assert.Equal("name", Assert.Throws<ArgumentNullException>(() => headers.Delete(null!)).ParamName);

        // 索引器读走的是同一条 Get 路径
        Assert.Equal("name", Assert.Throws<ArgumentNullException>(() => headers[null!]).ParamName);

        // 拒绝非法参数不能让已有条目受影响
        Assert.Equal("abc", headers.Get("X-Trace"));
    }

    [Fact]
    public void HeadersProperty_NullAssignment_ThrowsAtAssignmentPoint()
    {
        var config = new AxiosConfig();
        var original = config.Headers;

        var ex = Assert.Throws<ArgumentNullException>(() => config.Headers = null!);

        Assert.Equal("value", ex.ParamName);

        // 赋值失败后配置仍然可用（不能把已有的 headers 置空）
        Assert.Same(original, config.Headers);
        config.Headers.Set("X-Trace", "abc");
        Assert.Equal("abc", config.Headers.Get("X-Trace"));
    }

    [Fact]
    public void Clone_AfterFailedNullAssignment_StillWorks()
    {
        var config = new AxiosConfig { BaseAddress = "http://a.com" };
        Assert.Throws<ArgumentNullException>(() => config.Headers = null!);

        var clone = config.Clone();

        Assert.NotNull(clone.Headers);
        Assert.Equal("http://a.com", clone.BaseAddress);
    }
}
