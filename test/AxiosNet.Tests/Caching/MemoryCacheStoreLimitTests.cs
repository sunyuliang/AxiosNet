using AxiosNet.Cache;

namespace AxiosNet.Tests.Caching;

/// <summary>
/// <see cref="MemoryCacheStore"/> 的容量约束。<c>MaxEntries</c> / <c>MaxBytes</c> 是对外承诺，
/// 覆盖已存在的键不能成为绕过通道，否则占用会被推到上限以上、后续新键被静默拒收。
/// </summary>
public sealed class MemoryCacheStoreLimitTests
{
    private static AxiosShortCircuit Body(int size) => new()
    {
        Status = 200,
        StatusText = "OK",
        Body = new byte[size]
    };

    /// <summary>覆盖同一个键：新正文远大于 <c>MaxBytes</c> 时不能把占用推过上限，旧条目原样保留。</summary>
    [Fact]
    public async Task Overwrite_DoesNotExceedMaxBytes()
    {
        var store = new MemoryCacheStore(maxEntries: 16, maxBytes: 1000);

        await store.SetAsync("k", Body(100), TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(100, store.ApproximateBytes);

        await store.SetAsync("k", Body(10_000), TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.True(store.ApproximateBytes <= 1000, $"覆盖已有键后占用 {store.ApproximateBytes} 字节，超过 MaxBytes=1000");

        // 放不下就不接收：旧条目仍在（不清空、也不半途替换成超大条目）。
        var kept = await store.GetAsync("k", CancellationToken.None);
        Assert.NotNull(kept);
        Assert.Equal(100, kept!.Body!.Length);
    }

    /// <summary>单条就超过上限：不写入。</summary>
    [Fact]
    public async Task OversizedEntry_IsNotStored()
    {
        var store = new MemoryCacheStore(maxEntries: 16, maxBytes: 1000);

        await store.SetAsync("k", Body(10_000), TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Null(await store.GetAsync("k", CancellationToken.None));
        Assert.Equal(0, store.ApproximateBytes);
    }

    /// <summary>正常路径：覆盖成更小的正文要成功，并按差值退还字节。</summary>
    [Fact]
    public async Task Overwrite_Smaller_RefundsOldSize()
    {
        var store = new MemoryCacheStore(maxEntries: 16, maxBytes: 1000);

        await store.SetAsync("k", Body(500), TimeSpan.FromMinutes(1), CancellationToken.None);
        await store.SetAsync("k", Body(100), TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Equal(100, store.ApproximateBytes);
        Assert.Equal(1, store.Count);
        Assert.Equal(100, (await store.GetAsync("k", CancellationToken.None))!.Body!.Length);
    }

    /// <summary>条目数上限：超出的新键不写入，已有键仍可覆盖。</summary>
    [Fact]
    public async Task MaxEntries_IsRespected()
    {
        var store = new MemoryCacheStore(maxEntries: 2, maxBytes: 0);

        await store.SetAsync("a", Body(1), TimeSpan.FromMinutes(1), CancellationToken.None);
        await store.SetAsync("b", Body(1), TimeSpan.FromMinutes(1), CancellationToken.None);
        await store.SetAsync("c", Body(1), TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Equal(2, store.Count);
        Assert.Null(await store.GetAsync("c", CancellationToken.None));

        // 覆盖已有键不占新槽位。
        await store.SetAsync("a", Body(1), TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.NotNull(await store.GetAsync("a", CancellationToken.None));
        Assert.Equal(2, store.Count);
    }

    /// <summary>容量压力下过期条目要让位并退还字节，不能永久占住配额。</summary>
    [Fact]
    public async Task ExpiredEntries_AreReclaimedUnderPressure()
    {
        var store = new MemoryCacheStore(maxEntries: 16, maxBytes: 1000);

        // 6 × 150 = 900：写满到只剩 100 字节余量。
        for (var i = 0; i < 6; i++)
        {
            await store.SetAsync($"e{i}", Body(150), TimeSpan.FromMilliseconds(1), CancellationToken.None);
        }

        Assert.Equal(900, store.ApproximateBytes);

        await Task.Delay(50);

        // 新条目放不下：必须先清过期条目再判容量。
        await store.SetAsync("fresh", Body(150), TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.NotNull(await store.GetAsync("fresh", CancellationToken.None));
        Assert.Equal(150, store.ApproximateBytes);
        Assert.Equal(1, store.Count);
    }
}
