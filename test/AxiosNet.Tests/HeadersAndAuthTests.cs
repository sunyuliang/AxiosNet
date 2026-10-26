using System.Text;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// AxiosHeaders 公共 API 与 AxiosAuth 边角的直接覆盖（原审计中这两块只有「值/名校验」与
/// 「happy path 上送」有用例，集合语义与 Basic 编造的边界没有）。
/// </summary>
public sealed class HeadersAndAuthTests
{
    // ---------------------------------------------------------------
    // AxiosHeaders：集合语义
    // ---------------------------------------------------------------

    [Fact]
    public void Set_SameNameDifferentCase_Overwrites()
    {
        var headers = new AxiosHeaders();

        headers.Set("X-Trace", "first");
        headers.Set("x-trace", "second");

        Assert.Equal("second", headers.Get("X-Trace"));
        Assert.Equal(1, headers.Count);
    }

    [Fact]
    public void Set_NullValue_DeletesEntry()
    {
        var headers = new AxiosHeaders();
        headers.Set("X-Keep", "1");

        headers.Set("X-Keep", null);

        Assert.False(headers.Has("x-keep"));
        Assert.Null(headers.Get("X-Keep"));
    }

    [Fact]
    public void Set_Batch_NullValueDeletes_AndNonNullOverwrites()
    {
        var headers = new AxiosHeaders();
        headers.Set(new[]
        {
            new KeyValuePair<string, string?>("X-A", "1"),
            new KeyValuePair<string, string?>("X-B", "2")
        });

        headers.Set(new[]
        {
            new KeyValuePair<string, string?>("X-A", (string?)null),
            new KeyValuePair<string, string?>("X-B", "22")
        });

        Assert.False(headers.Has("X-A"));
        Assert.Equal("22", headers.Get("X-B"));
    }

    [Fact]
    public void Set_Batch_RejectsControlCharactersInValue()
    {
        var headers = new AxiosHeaders();

        // 批量路径与单条 Set 必须同一闸口：第 k 条非法时前 k-1 条已生效。
        var ex = Assert.Throws<ArgumentException>(() => headers.Set(new[]
        {
            new KeyValuePair<string, string?>("X-Ok", "1"),
            new KeyValuePair<string, string?>("X-Bad", "a\r\nInjected: 1")
        }));

        Assert.Equal("value", ex.ParamName);
        Assert.Equal("1", headers.Get("X-Ok"));
        Assert.False(headers.Has("X-Bad"));
    }

    [Fact]
    public void Indexer_ReadsAndWrites_IncludingNullDelete()
    {
        var headers = new AxiosHeaders();

        headers["X-A"] = "1";
        Assert.Equal("1", headers["x-a"]);

        headers["X-A"] = null;
        Assert.Null(headers["X-A"]);
    }

    [Fact]
    public void Has_And_Delete_ReportPresenceCaseInsensitively()
    {
        var headers = new AxiosHeaders();
        headers.Set("X-Trace", "on");

        Assert.True(headers.Has("x-trace"));
        Assert.True(headers.Delete("X-TRACE"));
        Assert.False(headers.Has("X-Trace"));
        Assert.False(headers.Delete("X-Trace"));
    }

    [Fact]
    public void Get_TrimsLookupName_ButNotStoredValue()
    {
        var headers = new AxiosHeaders();
        headers.Set("X-A", " padded ");

        Assert.Equal(" padded ", headers.Get("X-A"));
        Assert.Equal(" padded ", headers.Get("  X-A  "));
    }

    [Fact]
    public void Clear_RemovesEverything()
    {
        var headers = new AxiosHeaders();
        headers.Set("X-A", "1");
        headers.Set("X-B", "2");

        headers.Clear();

        Assert.Equal(0, headers.Count);
        Assert.Empty(headers);
    }

    [Fact]
    public void Set_ReturnsSameInstance_ForChaining()
    {
        var headers = new AxiosHeaders();

        Assert.Same(headers, headers.Set("X-A", "1"));
    }

    [Fact]
    public void Clone_IsIndependent_OfSource()
    {
        var original = new AxiosHeaders();
        original.Set("X-A", "1");

        var clone = original.Clone();
        clone.Set("X-A", "2");
        clone.Set("X-B", "new");

        Assert.Equal("1", original.Get("X-A"));
        Assert.False(original.Has("X-B"));
    }

    [Fact]
    public void Constructor_FromPairs_StoresAllEntries_TrimmingNames()
    {
        var headers = new AxiosHeaders(new[]
        {
            new KeyValuePair<string, string>("  X-A  ", "1"),
            new KeyValuePair<string, string>("X-B", "2")
        });

        Assert.Equal(2, headers.Count);
        Assert.Equal("1", headers.Get("X-A"));
    }

    [Fact]
    public void Enumeration_IsASnapshot_NotALiveView()
    {
        var headers = new AxiosHeaders();
        headers.Set("X-A", "1");

        var seen = new List<string>();
        foreach (var pair in headers)
        {
            seen.Add(pair.Key);
            headers.Set("X-B", "2"); // 迭代中修改：快照语义下不应抛异常
        }

        Assert.Equal(new[] { "X-A" }, seen);
    }

    // ---------------------------------------------------------------
    // AxiosAuth：Basic 编造边界
    // ---------------------------------------------------------------

    [Fact]
    public async Task Auth_PasswordWithColon_IsNotSplit()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.GetAsync(server.Url, c => c.Connection.Auth = new AxiosAuth("user", "pass:word"));

        Assert.Equal(TestJson.BasicAuth("user", "pass:word"), server.Received[0].Headers["authorization"]);
    }

    [Fact]
    public async Task Auth_UnicodeCredentials_RoundTripThroughUtf8()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.GetAsync(server.Url, c => c.Connection.Auth = new AxiosAuth("用户名", "密码"));

        var header = server.Received[0].Headers["authorization"];
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("用户名:密码")), header);
        Assert.Equal("用户名:密码", Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..])));
    }

    [Fact]
    public async Task Auth_EmptyPassword_StillSendsHeader()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.GetAsync(server.Url, c => c.Connection.Auth = new AxiosAuth("user", string.Empty));

        Assert.Equal(TestJson.BasicAuth("user", string.Empty), server.Received[0].Headers["authorization"]);
    }

    [Fact]
    public async Task Auth_PerRequest_OverridesInstanceDefault()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            Connection = new()
            {
                Auth = new AxiosAuth("instance", "secret"),
            },
        });

        await client.GetAsync(server.Url, c => c.Connection.Auth = new AxiosAuth("percall", "other"));

        Assert.Equal(TestJson.BasicAuth("percall", "other"), server.Received[0].Headers["authorization"]);
    }

    [Fact]
    public async Task Auth_ExplicitAuthorizationHeader_WinsOverAuth()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.GetAsync(server.Url, c =>
        {
            c.Connection.Auth = new AxiosAuth("user", "secret");
            c.Headers.Set("Authorization", "Bearer token");
        });

        Assert.Equal("Bearer token", server.Received[0].Headers["authorization"]);
    }
}
