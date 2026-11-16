using AxiosNet.Cache;
using AxiosNet.Tests.Infrastructure;
using System.Text;

namespace AxiosNet.Tests.Caching;

/// <summary>
/// 自定义 <see cref="IAxiosCacheStore"/> 的重建入口：<see cref="AxiosShortCircuit.Restore"/> 必须把
/// 多值头 / 落库时刻 / 龄期三个包内字段一并装回，否则包外存储「照自己格式存、再照自己格式读回」会静默降级。
/// </summary>
/// <remarks>
/// 本程序集是 <c>AxiosNet</c> 的 friend（<c>InternalsVisibleTo</c>），因此既可以直接读内部字段做结构断言，
/// 也可以「直接用属性初始化」一份对照条目，验证两条构造路径的行为等价。
/// </remarks>
public sealed class CacheEntryRestoreTests
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"name":"cached","age":7}""");

    /// <summary>
    /// 三个包内字段必须被真正填上，且经过一次命中交付后体现在响应上：
    /// 多值头原样回放（优先于单值字典）、<c>Age</c> 按落库龄期 + 缓存停留时间推进。
    /// </summary>
    [Fact]
    public async Task Restore_RefillsMultiValueHeadersAndAge()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("server", 42)));
        using var client = new AxiosClient();

        // 整点秒：Age 的取整差一不会在两次调用之间抖动。
        var receivedAt = TruncateToSecond(DateTimeOffset.UtcNow.AddSeconds(-10));
        var multi = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Vary"] = new[] { "Accept", "Accept-Encoding" },
            ["Age"] = new[] { "7" },
            ["ETag"] = new[] { "\"v1\"" },
            ["X-Cache"] = new[] { "HIT" }
        };

        var entry = AxiosShortCircuit.Restore(
            body: Body,
            headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Vary"] = "Accept",          // 单值字典里的折叠值：多值头优先，不能被它盖掉
                ["X-Cache"] = "HIT"
            },
            multiValueHeaders: multi,
            receivedAt: receivedAt,
            storedAge: TimeSpan.FromSeconds(5),
            contentType: AxiosContentType.Json);

        // 结构断言：包外写不到的三项确实被工厂装进去了。
        Assert.NotNull(entry.MultiValueHeaders);
        Assert.Equal(new[] { "Accept", "Accept-Encoding" }, entry.MultiValueHeaders!["Vary"]);
        Assert.Equal(receivedAt, entry.ReceivedAt);
        Assert.Equal(TimeSpan.FromSeconds(5), entry.StoredAge);

        var response = await client.GetAsync<Person>(
            server.Url + "users/1",
            c => c.ShortCircuit = entry);

        // 命中不发请求，正文按本次类型重新解析。
        Assert.Empty(server.Received);
        Assert.Equal("cached", response.Data!.Name);

        // 多值头原样交付（两条 Vary 都在，而不是被单值字典折叠成一条）。
        Assert.Equal(new[] { "Accept", "Accept-Encoding" }, response.Headers["Vary"]);
        Assert.Equal("HIT", response.GetHeader("X-Cache"));

        // Age = 落库那一刻的 5s + 在缓存里停留的 10s。
        var age = int.Parse(response.GetHeader("Age")!);
        Assert.InRange(age, 15, 16);
    }

    /// <summary>
    /// <c>ETag</c> 的<b>优先</b>来源是多值头（<see cref="CacheValidators"/>）：经过
    /// <see cref="AxiosShortCircuit.Restore"/> 的条目必须仍能提取校验器，否则条件请求会静默退化成整条重取。
    /// 这里同时覆盖「只在多值头里」与「只在单值字典里」两种存储形态。
    /// </summary>
    [Fact]
    public void Restore_KeepsValidatorsExtractable()
    {
        var onlyMulti = AxiosShortCircuit.Restore(
            body: Body,
            multiValueHeaders: new Dictionary<string, IEnumerable<string>>
            {
                ["ETag"] = new[] { "\"multi-value\"" }
            });

        Assert.True(CacheValidators.HasAny(onlyMulti), "多值头里的 ETag 没有被提取到——条件请求会静默失效。");
        Assert.Equal("\"multi-value\"", CacheValidators.Find(onlyMulti, CacheValidators.ETag));

        // 多值优先：两处都给时取多值里的那条。
        var both = AxiosShortCircuit.Restore(
            body: Body,
            headers: new Dictionary<string, string> { ["ETag"] = "\"single\"" },
            multiValueHeaders: new Dictionary<string, IEnumerable<string>>
            {
                ["ETag"] = new[] { "\"multi\"" }
            });

        Assert.Equal("\"multi\"", CacheValidators.Find(both, CacheValidators.ETag));

        // 只有单值字典（更朴素的后端）：仍然可用，不算退化。
        var onlySingle = AxiosShortCircuit.Restore(
            body: Body,
            headers: new Dictionary<string, string> { ["Last-Modified"] = "Wed, 21 Oct 2015 07:28:00 GMT" });

        Assert.True(CacheValidators.HasAny(onlySingle));
        Assert.Equal("Wed, 21 Oct 2015 07:28:00 GMT", CacheValidators.Find(onlySingle, CacheValidators.LastModified));
    }

    /// <summary>
    /// 用 <see cref="AxiosShortCircuit.Restore"/> 重建的条目与「直接用属性初始化」（含包内字段）的条目行为等价：
    /// 交付出来的状态码 / 头 / Age / 正文必须逐项相同。
    /// </summary>
    [Fact]
    public async Task Restore_IsEquivalentToDirectInitialization()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("server", 42)));
        using var client = new AxiosClient();

        var receivedAt = TruncateToSecond(DateTimeOffset.UtcNow.AddSeconds(-10));
        var multi = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Vary"] = new[] { "Accept", "Accept-Encoding" },
            ["Age"] = new[] { "7" },
            ["ETag"] = new[] { "\"v1\"" }
        };
        var single = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Vary"] = "Accept",
            ["X-Cache"] = "HIT"
        };

        var restored = AxiosShortCircuit.Restore(
            body: Body,
            headers: single,
            multiValueHeaders: multi,
            receivedAt: receivedAt,
            storedAge: TimeSpan.FromSeconds(5),
            contentType: AxiosContentType.Json);

        // 对照：直接用属性初始化（测试程序集是 friend，能写三个内部字段）—— 这就是 Restore 内部的等价形态。
        var direct = new AxiosShortCircuit
        {
            Body = Body,
            Headers = single,
            MultiValueHeaders = multi,
            ReceivedAt = receivedAt,
            StoredAge = TimeSpan.FromSeconds(5),
            ContentType = AxiosContentType.Json
        };

        var fromRestore = await client.GetAsync<Person>(server.Url + "users/1", c => c.ShortCircuit = restored);
        var fromDirect = await client.GetAsync<Person>(server.Url + "users/1", c => c.ShortCircuit = direct);

        Assert.Empty(server.Received);
        Assert.Equal(fromDirect.Status, fromRestore.Status);
        Assert.Equal(fromDirect.StatusText, fromRestore.StatusText);
        Assert.Equal(fromDirect.Data!.Name, fromRestore.Data!.Name);

        // Age 按秒取整、两次短路调用又各自取一次 UtcNow：跨过秒边界就正好差 1。
        // 这里要证明的是「两条构造路径同一口径」，不是「同一次读数」，因此放 1 秒容差（严格相等在 CI 上会偶发假红）。
        Assert.InRange(
            Math.Abs(int.Parse(fromDirect.GetHeader("Age")!) - int.Parse(fromRestore.GetHeader("Age")!)),
            0,
            1);
        Assert.Equal(fromDirect.GetHeader("X-Cache"), fromRestore.GetHeader("X-Cache"));

        // 头表逐项比对（多值头也在这里一起比：键集合相同、每个键的值序列相同）；Age 已在上面按容差比过。
        Assert.Equal(
            fromDirect.Headers.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase),
            fromRestore.Headers.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
        foreach (var header in fromDirect.Headers)
        {
            if (string.Equals(header.Key, "Age", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Assert.Equal(header.Value, fromRestore.Headers[header.Key]);
        }
    }

    /// <summary>
    /// <see cref="AxiosShortCircuit.Restore"/> 收的是调用方字典，不保证每个键都有值：
    /// 「键在、值为 <c>null</c>」必须等同于「没有这个头」，并且在<b>工厂入口</b>就归一掉
    ///（下游有四处按「每个值非 null」读：交付装配、条件请求校验器、304 合并、导出视图）。
    /// </summary>
    [Fact]
    public async Task Restore_NullMultiValueEntry_IsDroppedAtTheFactory()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("server", 42)));
        using var client = new AxiosClient();

        var entry = AxiosShortCircuit.Restore(
            body: Body,
            headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Cache"] = "HIT" },
            multiValueHeaders: new Dictionary<string, IEnumerable<string>>
            {
                ["Vary"] = null!,        // 反序列化后端可能给出这种形态
                ["X-Cache"] = null!,     // 同上：值与单值字典同名，也不该被它盖成「空」
            });

        // 入口即归一：内部字段里不留 null 值条目（这是下游敢不判空的前提）。
        Assert.All(entry.MultiValueHeaders!, header => Assert.NotNull(header.Value));
        Assert.Empty(entry.MultiValueHeaderValues!);

        var response = await client.GetAsync<Person>(server.Url + "users/1", c => c.ShortCircuit = entry);

        Assert.Empty(server.Received);
        Assert.Equal("cached", response.Data!.Name);
        Assert.Equal("HIT", response.GetHeader("X-Cache"));
    }

    /// <summary>把时刻截到整秒：<c>Age</c> 按秒取整，取整边界会让「两次调用差 1 秒」偶发误报。</summary>
    private static DateTimeOffset TruncateToSecond(DateTimeOffset value)
        => new(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), value.Offset);
}
