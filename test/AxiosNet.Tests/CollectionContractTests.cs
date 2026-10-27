namespace AxiosNet.Tests;

/// <summary><c>SynchronizedList&lt;T&gt;</c> 对 <see cref="IList{T}"/> 契约的遵守情况：同一非法操作必须与 <see cref="List{T}"/> 抛同一异常类型。</summary>
public sealed class CollectionContractTests
{
    // 与 List<T> 逐条对齐：同一操作抛同一异常类型。

    [Fact]
    public void OutOfRangeOperations_ThrowSameTypeAsList()
    {
        var cases = new (string Name, Action Ours, Action Baseline)[]
        {
            ("空表 RemoveAt(0)", Chain(l => l.RemoveAt(0)), () => new List<int>().RemoveAt(0)),
            ("空表 RemoveAt(-1)", Chain(l => l.RemoveAt(-1)), () => new List<int>().RemoveAt(-1)),
            ("1 项 RemoveAt(1)", Chain(l => l.RemoveAt(1), seed: true), () => new List<int> { 1 }.RemoveAt(1)),
            ("空表 this[0] 读", Chain(l => _ = l[0]), () => _ = new List<int>()[0]),
            ("空表 this[5] 读", Chain(l => _ = l[5]), () => _ = new List<int>()[5]),
            ("1 项 this[-1] 读", Chain(l => _ = l[-1], seed: true), () => _ = new List<int> { 1 }[-1]),
            ("1 项 this[9] 读", Chain(l => _ = l[9], seed: true), () => _ = new List<int> { 1 }[9]),
            ("1 项 this[9] 写", Chain(l => l[9] = One, seed: true), () =>
            {
                var baseline = new List<int> { 1 };
                baseline[9] = 0;
            }),
            ("空表 Insert(1, x)", Chain(l => l.Insert(1, One)), () => new List<int>().Insert(1, 0)),
            ("空表 Insert(-1, x)", Chain(l => l.Insert(-1, One)), () => new List<int>().Insert(-1, 0)),
            ("1 项 Insert(9, x)", Chain(l => l.Insert(9, One), seed: true), () => new List<int> { 1 }.Insert(9, 0)),
        };

        foreach (var (name, ours, baseline) in cases)
        {
            var expected = Record(baseline);
            var actual = Record(ours);

            Assert.True(expected == actual, $"{name}：本类型抛 {actual}，List<T> 抛 {expected} —— 契约不一致。");
            Assert.Equal(nameof(ArgumentOutOfRangeException), actual);
        }
    }

    // 参数名与取值。

    [Fact]
    public void RemoveAtOnEmpty_ReportsIndexParameterAndValue()
    {
        var list = new AxiosConfig().TransformRequest;

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => list.RemoveAt(0));

        Assert.Equal("index", ex.ParamName);
        Assert.Equal(0, ex.ActualValue);
    }

    [Fact]
    public void IndexerOutOfRange_ReportsIndexParameter()
    {
        var list = new AxiosConfig().TransformRequest;

        var read = Assert.Throws<ArgumentOutOfRangeException>(() => _ = list[3]);
        var write = Assert.Throws<ArgumentOutOfRangeException>(() => list[3] = One);

        Assert.Equal("index", read.ParamName);
        Assert.Equal("index", write.ParamName);
    }

    [Fact]
    public void RemoveHeaders_RemoveAtOutOfRange_ThrowsArgumentOutOfRange()
    {
        // 删头名单是另一个公开集合（ICollection<string>），走的是同一个 SynchronizedList。
        var headers = new AxiosConfig().RemoveHeaders;
        headers.Add("X-Trace");

        var list = (IList<string>)headers;

        Assert.Throws<ArgumentOutOfRangeException>(() => list.RemoveAt(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.RemoveAt(-1));
        Assert.Single(headers);
    }

    // 合法边界不能被误伤。

    [Fact]
    public void InsertAtCount_Appends()
    {
        var list = new AxiosConfig().TransformRequest;
        list.Add(One);

        list.Insert(list.Count, One); // index == Count 合法（追加）

        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void RemoveAtLastIndex_Succeeds()
    {
        var list = new AxiosConfig().TransformRequest;
        list.Add(One);
        list.Add(One);

        list.RemoveAt(1);

        Assert.Single(list);
    }

    [Fact]
    public void IndexerWithinRange_StillReadsAndWrites()
    {
        var list = new AxiosConfig().TransformRequest;
        list.Add(One);

        list[0] = One;

        Assert.Same(One, list[0]);
    }

    private static readonly Func<object?, AxiosConfig, object?> One = static (data, _) => data;

    /// <summary>在全新的 transform 链上执行操作（seed 为 true 时先塞一条，用于「有元素但越界」的用例）。</summary>
    private static Action Chain(Action<IList<Func<object?, AxiosConfig, object?>>> action, bool seed = false)
        => () =>
        {
            var list = new AxiosConfig().TransformRequest;
            if (seed)
            {
                list.Add(One);
            }

            action(list);
        };

    private static string Record(Action action)
    {
        try
        {
            action();
            return "<未抛异常>";
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }
    }
}
