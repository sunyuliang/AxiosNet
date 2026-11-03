using System.Globalization;
using System.Text;
using System.Text.Json;
using AxiosNet.Metadata;
using AxiosNet.Retry;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Attributes;

namespace AxiosNet.Tests.Regression;

/// <summary>回归：格式化修复后的对抗边界（自定义 ParamsSerializer、集合形态、JSON 树、嵌套声明、重试、并发）。</summary>
public sealed class FormatAdversarialTests
{
    // ---------------------------------------------------------------- P1 自定义 ParamsSerializer

    [Fact]
    public async Task CustomParamsSerializer_SeesFormattedStrings()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);
        client.Axios.Defaults.ValueFormatter = (value, _) => value is int number ? "v" + number.ToString(CultureInfo.InvariantCulture) : null;

        await client.RequestAsync(new P1Request { Id = 7 }, c =>
            c.Serialization.ParamsSerializer = raw =>
            {
                var dict = (Dictionary<string, object?>)raw!;
                return string.Join("&", dict.Select(entry => entry.Key + "=" + (string)entry.Value!));
            });

        var query = Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty);
        Assert.Equal("Id=v7&Month=2024-03", query);
    }

    [Get("p1")]
    private sealed class P1Request : IApiRequest<Person>
    {
        [Query] public int Id { get; set; }

        [Query, Format("yyyy-MM")] public DateTime Month { get; set; } = new(2024, 3, 4);
    }

    // ---------------------------------------------------------------- P2 CSV / Multi / Pipes

    [Fact]
    public async Task CsvCollection_MemberFormatWinsOverHook()
    {
        await ExpectCollection(AxiosCollectionFormat.Csv, "Dates=2024-03,2024-05");
    }

    [Fact]
    public async Task MultiCollection_MemberFormatWinsOverHook()
    {
        await ExpectCollection(AxiosCollectionFormat.Multi, "Dates=2024-03&Dates=2024-05");
    }

    [Fact]
    public async Task PipesCollection_MemberFormatWinsOverHook()
    {
        await ExpectCollection(AxiosCollectionFormat.Pipes, "Dates=2024-03|2024-05");
    }

    private static async Task ExpectCollection(AxiosCollectionFormat format, string expected)
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);
        client.Axios.Defaults.ValueFormatter = (value, _) => value is DateTime date ? "H" + date.ToString("yyyy", CultureInfo.InvariantCulture) : null;

        await client.RequestAsync(
            new P2Request { Dates = [new DateTime(2024, 3, 4, 0, 0, 0, DateTimeKind.Utc), new DateTime(2024, 5, 6, 0, 0, 0, DateTimeKind.Utc)] },
            c => c.Serialization.CollectionFormat = format);

        var query = Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty);
        Assert.Equal(expected, query);
    }

    [Get("p2")]
    private sealed class P2Request : IApiRequest<Person>
    {
        [Query, Format("yyyy-MM")] public List<DateTime> Dates { get; set; } = new();
    }

    // ---------------------------------------------------------------- P3 多层集合

    [Fact]
    public async Task NestedCollections_KeepShapeWithHook()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);
        client.Axios.Defaults.ValueFormatter = (value, _) => value is int number ? "v" + number.ToString(CultureInfo.InvariantCulture) : null;

        await client.RequestAsync(new P3Request { Matrix = [[1, 2], [3]] });

        var query = Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty);
        Assert.Equal("Matrix[][]=v1&Matrix[][]=v2&Matrix[][]=v3", query);
    }

    [Get("p3")]
    private sealed class P3Request : IApiRequest<Person>
    {
        [Query] public List<List<int>> Matrix { get; set; } = new();
    }

    // ---------------------------------------------------------------- P4 字典里的文件部件 + [Format]

    [Fact]
    public void DictionaryOfFilesWithFormat_IsRejected()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");

        var ex = Assert.Throws<InvalidOperationException>(() => client.GetUri(new P4Request()));
        Assert.Contains("文件部件", ex.Message);
    }

    [Post("p4")]
    [Multipart]
    private sealed class P4Request : IApiRequest<Person>
    {
        [Part, Format(typeof(AdversarialFormats), nameof(AdversarialFormats.Hex))]
        public Dictionary<string, byte[]> Files { get; set; } = new();
    }

    // ---------------------------------------------------------------- P5 multipart 字符串集合

    [Fact]
    public async Task MultipartStringCollection_MemberFormatWinsOverHook()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);
        client.Axios.Defaults.ValueFormatter = (value, _) => value is string text ? "H" + text : null;

        await client.RequestAsync(new P5Request { Tags = ["a", "b"] });

        var body = Encoding.UTF8.GetString(Assert.Single(server.Received).Body);
        Assert.Contains("Fb", body);
        Assert.DoesNotContain("Hb", body);
    }

    [Post("p5")]
    [Multipart]
    private sealed class P5Request : IApiRequest<Person>
    {
        [Part, Format(typeof(AdversarialFormats), nameof(AdversarialFormats.Prefixed))]
        public List<string> Tags { get; set; } = new();
    }

    // ---------------------------------------------------------------- P6 并发

    [Fact]
    public async Task ConcurrentRequests_NoCrosstalk()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);
        client.Axios.Defaults.ValueFormatter = (value, _) => value is int number ? "v" + number.ToString(CultureInfo.InvariantCulture) : null;

        await Task.WhenAll(Enumerable.Range(0, 32).Select(id =>
            client.RequestAsync(new P6Request { Id = id })));

        var queries = server.Received
            .Select(request => request.Query ?? string.Empty)
            .ToList();

        for (var id = 0; id < 32; id++)
        {
            Assert.Contains(queries, query => query == "Id=v" + id);
        }
    }

    [Get("p6")]
    private sealed class P6Request : IApiRequest<Person>
    {
        [Query] public int Id { get; set; }
    }

    // ---------------------------------------------------------------- P7 multipart 装配失败释放流

    [Fact]
    public async Task MultipartFormatterFailure_DisposesStream()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);
        var stream = new TrackingStream();

        var error = await Assert.ThrowsAsync<AxiosException>(
            () => client.RequestAsync(new P7Request
            {
                File = new FormFile("a.bin", stream),
                Text = "x"
            }));

        Assert.Equal(AxiosException.ErrBadRequest, error.Code);
        Assert.True(stream.Disposed, "装配失败后流必须释放");
        Assert.Empty(server.Received);
    }

    [Post("p7")]
    [Multipart]
    private sealed class P7Request : IApiRequest<Person>
    {
        [Part] public FormFile? File { get; set; }

        [Part, Format(typeof(AdversarialFormats), nameof(AdversarialFormats.Throw))] public string Text { get; set; } = string.Empty;
    }

    private sealed class TrackingStream : MemoryStream
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    // ---------------------------------------------------------------- P8/P9 JsonElement 树里的钩子

    [Fact]
    public async Task QueryAnonymousObject_HookAppliesToLeaves()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new AxiosClient
        {
        };

        await client.GetAsync(server.Url + "p8", c =>
        {
            c.Params = new { when = new DateTime(2024, 3, 4, 0, 0, 0, DateTimeKind.Utc) };
            c.ValueFormatter = (value, _) => value is DateTime date ? "H" + date.ToString("yyyy", CultureInfo.InvariantCulture) : null;
        });

        var query = Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty);
        Assert.Equal("when=H2024", query);
    }

    [Fact]
    public async Task QueryJsonElementRoot_HookAppliesToLeaves()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new AxiosClient
        {
        };

        using var document = JsonDocument.Parse("""{"when":"2024-03-04"}""");
        await client.GetAsync(server.Url + "p9", c =>
        {
            c.Params = document.RootElement;
            c.ValueFormatter = (value, _) => value is string text ? "H" + text : null;
        });

        var query = Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty);
        Assert.Equal("when=H2024-03-04", query);
    }

    // ---------------------------------------------------------------- P11/P12 嵌套声明的格式化失败

    [Fact]
    public async Task NestedObjectHookFailure_IsBadRequest()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);
        client.Axios.Defaults.ValueFormatter = (value, _) =>
        {
            if (value is DateTime)
            {
                throw new InvalidOperationException("hook failed");
            }

            return null;
        };

        var error = await Assert.ThrowsAsync<AxiosException>(
            () => client.RequestAsync(new P11Request { Item = new P11Inner { When = new DateTime(2024, 3, 4) } }));

        Assert.Equal(AxiosException.ErrBadRequest, error.Code);
        Assert.IsType<InvalidOperationException>(error.Cause);
        Assert.Empty(server.Received);
    }

    [Get("p11")]
    private sealed class P11Request : IApiRequest<Person>
    {
        [Query] public P11Inner? Item { get; set; }
    }

    private sealed class P11Inner
    {
        public DateTime When { get; set; }
    }

    [Fact]
    public async Task NestedMemberFormatFailure_IsBadRequest()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        var error = await Assert.ThrowsAsync<AxiosException>(
            () => client.RequestAsync(new P12Request { Item = new P12Inner { When = new DateTime(2024, 3, 4) } }));

        Assert.Equal(AxiosException.ErrBadRequest, error.Code);
        Assert.IsType<InvalidOperationException>(error.Cause);
        Assert.Empty(server.Received);
    }

    [Get("p12")]
    private sealed class P12Request : IApiRequest<Person>
    {
        [Query] public P12Inner? Item { get; set; }
    }

    private sealed class P12Inner
    {
        [Format(typeof(AdversarialFormats), nameof(AdversarialFormats.Throw))]
        public DateTime When { get; set; }
    }

    // ---------------------------------------------------------------- P13/P14 JSON 树的表单 / multipart

    [Fact]
    public async Task FormJsonElement_HookAppliesToLeaves()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new AxiosClient();
        using var document = JsonDocument.Parse("""{"when":"2024-03-04"}""");

        await client.PostAsync(server.Url + "p13", document.RootElement, c =>
        {
            c.ContentType = AxiosConfig.FormUrlEncodedContentType;
            c.ValueFormatter = (value, _) => value is string text ? "H" + text : null;
        });

        var body = Uri.UnescapeDataString(Encoding.UTF8.GetString(Assert.Single(server.Received).Body));
        Assert.Equal("when=H2024-03-04", body);
    }

    [Fact]
    public async Task MultipartJsonElement_HookAppliesToLeaves()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url, c => c.ValueFormatter = (value, _) => value is string text ? "H" + text : null);
        using var document = JsonDocument.Parse("""{"when":"2024-03-04"}""");
        var request = new P14Request { Fields = document.RootElement };

        await client.RequestAsync(request);

        var body = Encoding.UTF8.GetString(Assert.Single(server.Received).Body);
        Assert.Contains("H2024-03-04", body);
    }

    [Post("p14")]
    [Multipart]
    private sealed class P14Request : IApiRequest<Person>
    {
        [Part] public JsonElement Fields { get; set; }
    }

    // ---------------------------------------------------------------- P15 重试重建请求不二次包装

    [Fact]
    public async Task Retry_ReformatsQueryOnlyOnce()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ =>
        {
            calls++;
            return calls == 1
                ? new TestResponse(500)
                : TestJson.Response(new Person("ok", 1));
        });

        using var client = new ApiClient(server.Url);
        client.Axios.Defaults.ValueFormatter = (value, _) => value is int number ? "v" + number.ToString(CultureInfo.InvariantCulture) : null;

        await client.RequestAsync(new P6Request { Id = 7 }, c =>
            c.Retry.Policy = new RetryPolicy { MaxRetries = 1, Delay = TimeSpan.Zero });

        Assert.Equal(2, server.Received.Count);
        Assert.All(server.Received, received =>
            Assert.Equal("Id=v7", received.Query));
    }

    // ---------------------------------------------------------------- P16 闭合泛型接口参数

    [Fact]
    public async Task ClosedGenericInterfaceParameter_Formats()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);
        client.Axios.Defaults.ValueFormatter = (value, _) => value is int number ? "v" + number.ToString(CultureInfo.InvariantCulture) : null;

        var api = client.Create<IGenericQueryApi<int>>();
        await api.GetAsync(5);

        var query = Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty);
        Assert.Equal("value=v5", query);
    }

    public interface IGenericQueryApi<T>
    {
        [Get("p16")]
        Task<AxiosResponse<Person>> GetAsync([Query] T value, CancellationToken cancellationToken = default);
    }
}

internal static class AdversarialFormats
{
    public static string? Hex(object? value, ValueTextChannel _)
        => value is byte[] bytes ? Convert.ToHexString(bytes) : null;

    public static string? Prefixed(object? value, ValueTextChannel _) => value is string text ? "F" + text : null;

    public static string? Throw(object? value, ValueTextChannel _) => throw new InvalidOperationException("formatter failed");
}
