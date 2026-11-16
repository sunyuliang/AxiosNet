namespace AxiosNet.Tests.Client;

/// <summary>
/// <see cref="AxiosHeaders.MergeFrom"/> 的合并语义：同名覆盖在原位置、新名按对方顺序追加到尾部。
/// </summary>
/// <remarks>
/// <c>Merge</c> 从「<see cref="List{T}"/> + <c>ToArray</c>」改成一次分配到位（与 <c>ApplyOps</c> 同一模式），
/// 这里锁住改造前后必须逐项一致的可见行为：顺序、覆盖、大小写不敏感。
/// </remarks>
public sealed class HeadersMergeTests
{
    /// <summary>
    /// 覆盖 + 追加的顺序契约：<c>current</c> 的条目保持原位（取值被对方覆盖），
    /// 新名按 <c>incoming</c> 的枚举顺序接在尾部。
    /// </summary>
    [Fact]
    public void MergeFrom_OverridesInPlaceAndAppendsNewNamesInOrder()
    {
        var target = new AxiosHeaders();
        target.Set("Accept", "application/json");
        target.Set("X-Trace", "1");

        var incoming = new AxiosHeaders();
        incoming.Set("x-trace", "2");                    // 大小写不同 = 同名：覆盖，位置不变
        incoming.Set("X-New", "a");
        incoming.Set("accept-encoding", "gzip");
        incoming.Set("X-New2", "b");

        target.MergeFrom(incoming);

        // 顺序：原有两条在前（含被覆盖的那条），新增的三条按 incoming 的顺序接在后面。
        Assert.Equal(
            new[] { "Accept", "X-Trace", "X-New", "Accept-Encoding", "X-New2" },
            target.Select(header => header.Key).ToArray(),
            StringComparer.OrdinalIgnoreCase);

        // 覆盖语义：同名取 incoming 的值，未涉及的同名保持原值。
        Assert.Equal("application/json", target.Get("Accept"));
        Assert.Equal("2", target.Get("X-Trace"));
        Assert.Equal("a", target.Get("X-New"));
        Assert.Equal("gzip", target.Get("accept-encoding"));
        Assert.Equal("b", target.Get("X-New2"));
    }

    /// <summary>
    /// 只覆盖、不新增时不改变长度；重复合并同一个对象是幂等的（值相同 → 不发布新版本）。
    /// </summary>
    [Fact]
    public void MergeFrom_RepeatedMergeIsIdempotent()
    {
        var target = new AxiosHeaders();
        target.Set("Accept", "application/json");

        var incoming = new AxiosHeaders();
        incoming.Set("X-Added", "1");

        target.MergeFrom(incoming);
        var afterFirst = target.Select(header => header.Key).ToArray();

        target.MergeFrom(incoming);

        Assert.Equal(afterFirst, target.Select(header => header.Key).ToArray());
        Assert.Equal("1", target.Get("X-Added"));
        Assert.Equal("application/json", target.Get("Accept"));
    }

    /// <summary>
    /// 对方的空集合 / <c>null</c> 不产生任何变化（调用点可以无脑合并，不必先判空）。
    /// </summary>
    [Fact]
    public void MergeFrom_EmptyOrNullIsNoOp()
    {
        var target = new AxiosHeaders();
        target.Set("Accept", "application/json");

        target.MergeFrom(null);
        target.MergeFrom(new AxiosHeaders());

        Assert.Equal(new[] { "Accept" }, target.Select(header => header.Key).ToArray());
        Assert.Equal("application/json", target.Get("Accept"));
    }
}
