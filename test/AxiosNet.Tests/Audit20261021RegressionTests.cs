using System.Text;
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>空白 body 与空 body 等价、<c>IdleHandlerLifetime</c> 的驱逐语义、typed JSON 对非 UTF-8 字节的解码回退、空配置 <c>Clone()</c> 不重建集合。</summary>
public sealed class Audit20261021RegressionTests
{
    // 空白 body 与空 body 等价。
    [Fact]
    public async Task WhitespaceOnlyBody_TypedJson_IsTreatedAsEmpty()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes("  \r\n"), ContentType: "application/json; charset=utf-8"));

        using var client = new AxiosClient();

        var response = await client.GetAsync<Person>(server.Url + "blank");

        // 与空 body 一致：进不到反序列化。
        Assert.Null(response.Data);
        Assert.Equal(200, response.Status);
    }

    [Fact]
    public async Task WhitespaceOnlyBody_Dynamic_IsTreatedAsEmpty()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes("   \t\r\n"), ContentType: "application/json; charset=utf-8"));

        using var client = new AxiosClient();

        var response = await client.GetAsync<dynamic>(server.Url + "blank");

        Assert.Null((object?)response.Data);
    }

    [Fact]
    public async Task WhitespaceOnlyBody_NonGeneric_IsTreatedAsEmpty()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes("\r\n"), ContentType: "application/json; charset=utf-8"));

        using var client = new AxiosClient();

        var response = await client.RequestAsync(new AxiosConfig { Url = server.Url + "blank" });

        Assert.Null(response.Data);
    }

    [Fact]
    public async Task NonAsciiWhitespaceOnlyBody_TypedJson_IsTreatedAsEmpty()
    {
        // NBSP（U+00A0）在 UTF-8 里是 0xC2 0xA0：字节级判空认不出，靠解码后 Trim 兜底。
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes("\u00a0"), ContentType: "application/json; charset=utf-8"));

        using var client = new AxiosClient();

        var response = await client.GetAsync<Person>(server.Url + "nbsp");

        Assert.Null(response.Data);
    }

    [Fact]
    public async Task TextResponseType_KeepsWhitespaceVerbatim()
    {
        // 空白判定只作用于按 JSON 解析的分支。
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes("  \r\n"), ContentType: "text/plain; charset=utf-8"));

        using var client = new AxiosClient();

        var response = await client.GetAsync<string>(server.Url + "blank");

        Assert.Equal("  \r\n", response.Data);
    }

    [Fact]
    public async Task NonJsonBody_Dynamic_StillThrowsBadResponse()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes("<html>ok</html>"), ContentType: "text/html; charset=utf-8"));

        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<dynamic>(server.Url + "html"));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
    }

    // 空闲驱逐语义。
    [Fact]
    public async Task Pool_InfiniteIdleLifetime_ManualEvict_KeepsIdleEndpoint()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 18)));

        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            IdleHandlerLifetime = Timeout.InfiniteTimeSpan,
            EvictionInterval = TimeSpan.Zero
        });

        using (var client = new AxiosClient(pool: pool))
        {
            await client.GetAsync<Person>(server.Url + "x");
        }

        Assert.Equal(1, pool.GetStatistics().EndpointCount);

        var evicted = pool.EvictIdleEndpoints();

        Assert.Equal(0, evicted);
        Assert.Equal(1, pool.GetStatistics().EndpointCount);
    }

    [Fact]
    public async Task Pool_ZeroIdleLifetime_ManualEvict_StillEvictsIdleEndpoint()
    {
        // 0 表示立即可驱逐。
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 18)));

        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            IdleHandlerLifetime = TimeSpan.Zero,
            EvictionInterval = TimeSpan.Zero
        });

        using (var client = new AxiosClient(pool: pool))
        {
            await client.GetAsync<Person>(server.Url + "x");
        }

        Assert.Equal(1, pool.EvictIdleEndpoints());
        Assert.Equal(0, pool.GetStatistics().EndpointCount);
    }

    // UTF-8 直读的边界。
    [Fact]
    public async Task TypedJson_WithUtf8Bom_StillDeserializes()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var body = TestJson.Bytes(new Person("bom", 7));

        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: [.. bom, .. body], ContentType: "application/json"));

        using var client = new AxiosClient();

        var response = await client.GetAsync<Person>(server.Url + "bom");

        Assert.Equal(new Person("bom", 7), response.Data);
    }

    [Fact]
    public async Task TypedJson_WithNonUtf8Charset_StillDeserializes()
    {
        // 非 UTF-8 编码必须回退解码路径，否则 UTF-16 字节会被当成 UTF-8 直读。
        const string json = """{"name":"宽字符","age":42}""";

        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.Unicode.GetBytes(json), ContentType: "application/json; charset=utf-16"));

        using var client = new AxiosClient();

        var response = await client.GetAsync<Person>(server.Url + "utf16");

        Assert.Equal(new Person("宽字符", 42), response.Data);
    }

    [Fact]
    public async Task TypedJson_BareScalar_StillFallsBackToScalarParser()
    {
        // UTF-8 直读失败后走与文本路径相同的兜底（裸标量文本）。
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes("2024-01-02"), ContentType: "application/json"));

        using var client = new AxiosClient();

        var response = await client.GetAsync<DateTime>(server.Url + "date");

        Assert.Equal(new DateTime(2024, 1, 2), response.Data);
    }

    // 配置分配护栏。
    [Fact]
    public void Config_Clone_EmptyInstance_DoesNotMaterializeCollections()
    {
        var source = new AxiosConfig { Timeout = TimeSpan.FromSeconds(3) };

        // 空集合保持 null，Clone 只做 MemberwiseClone；阈值 400 留一倍余量给对象头差异。
        var clone = source.Clone(); // 预热
        GC.KeepAlive(clone);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20_000; i++)
        {
            GC.KeepAlive(source.Clone());
        }

        var average = (GC.GetAllocatedBytesForCurrentThread() - before) / 20_000;
        Assert.True(average < 400, $"空配置的 AxiosConfig.Clone() 平均 {average} B/次，超出 400 B 护栏——疑似又为空集合创建了副本。");
    }

    [Fact]
    public void Config_LazyCollections_AreStillIsolatedAfterClone()
    {
        // 延迟创建：两边都在首次写入时才真正建集合。
        var source = new AxiosConfig();

        Assert.Equal(0, source.Headers.Count);
        Assert.Empty(source.RemoveHeaders);
        Assert.Empty(source.TransformRequest);
        Assert.Empty(source.TransformResponse);

        var clone = source.Clone();
        clone.Headers.Set("X-Trace", "1");
        clone.RemoveHeaders.Add("X-Default");
        clone.TransformRequest.Add((data, _) => data);
        clone.TransformResponse.Add((data, _) => data);

        Assert.Null(source.Headers.Get("X-Trace"));
        Assert.Empty(source.RemoveHeaders);
        Assert.Empty(source.TransformRequest);
        Assert.Empty(source.TransformResponse);
    }
}
