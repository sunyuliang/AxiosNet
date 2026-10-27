namespace AxiosNet.Tests;

/// <summary>公共 API 的空参数守卫：空头部名与空 Headers 赋值都抛 <see cref="ArgumentNullException"/>。</summary>
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

        Assert.Equal("name", Assert.Throws<ArgumentNullException>(() => headers[null!]).ParamName);

        Assert.Equal("abc", headers.Get("X-Trace"));
    }

    [Fact]
    public void HeadersProperty_NullAssignment_ThrowsAtAssignmentPoint()
    {
        var config = new AxiosConfig();
        var original = config.Headers;

        var ex = Assert.Throws<ArgumentNullException>(() => config.Headers = null!);

        Assert.Equal("value", ex.ParamName);

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
