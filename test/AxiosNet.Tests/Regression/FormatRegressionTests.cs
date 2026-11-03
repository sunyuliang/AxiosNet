using System.Globalization;
using System.Reflection;
using System.Text;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Attributes;

namespace AxiosNet.Tests.Regression;

/// <summary>
/// 回归：成员级 [Format] / 实例钩子的对抗性边界（查询通道二次格式化、结构化集合、multipart 字符串、
/// 文件部件校验、反射与源生成口径、DateOnly 线上形态）。
/// </summary>
public sealed class FormatRegressionTests
{
    // ------------------------------------------------------------------
    // 查询通道：绑定层格式化过的文本不能被实例钩子再套一遍
    // ------------------------------------------------------------------

    [Fact]
    public async Task Query_ScalarBinding_IsNotHookedTwice()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url)
        {
            // 只处理字符串的钩子：它只应看到原始 int / DateTime，不应看到已定型的文本
            ValueFormatter = value => value is string text ? "X" + text : null
        };

        await client.RequestAsync(new HookQuery { Id = 7, Month = new DateTime(2024, 3, 4, 0, 0, 0, DateTimeKind.Utc) });

        var query = Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty);
        Assert.Equal("Id=7&Month=2024-03", query);
    }

    [Fact]
    public async Task Query_MapAndCsvCollection_AreNotHookedTwice()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url)
        {
            ValueFormatter = value => value is string text ? "X" + text : null
        };

        await client.RequestAsync(
            new MapAndCsvQuery
            {
                When = new Dictionary<string, DateTime> { ["a"] = new DateTime(2024, 3, 4, 0, 0, 0, DateTimeKind.Utc) },
                Codes = new List<int> { 1, 2 }
            },
            c => c.Serialization.CollectionFormat = AxiosCollectionFormat.Csv);

        var query = Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty);
        Assert.Equal("Codes=1,2&a=2024-03-04T00:00:00.000Z", query);
    }

    // ------------------------------------------------------------------
    // 查询通道：设置钩子不能把对象集合压成 ToString
    // ------------------------------------------------------------------

    [Fact]
    public async Task Query_ListOfObjects_KeepsStructure_WithHook()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using (var plain = new ApiClient(server.Url))
        {
            await plain.RequestAsync(new ListQuery { Items = { new Item { Name = "a", Age = 1 } } });
        }

        using (var hooked = new ApiClient(server.Url) { ValueFormatter = _ => null })
        {
            await hooked.RequestAsync(new ListQuery { Items = { new Item { Name = "a", Age = 1 } } });
        }

        Assert.Equal(2, server.Received.Count);
        Assert.Equal(server.Received[0].Query, server.Received[1].Query);
    }

    // ------------------------------------------------------------------
    // 表单 / multipart：同一份声明在两条装配路径上口径一致
    // ------------------------------------------------------------------

    [Fact]
    public async Task Multipart_StringPart_AppliesMemberFormat()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new MultipartString());

        var body = Encoding.UTF8.GetString(Assert.Single(server.Received).Body);
        Assert.Contains("Fabc", body);
    }

    [Fact]
    public async Task Form_StringField_AppliesMemberFormat()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "form",
            new StringPayload(),
            c => c.ContentType = AxiosConfig.FormUrlEncodedContentType);

        var body = Uri.UnescapeDataString(Encoding.UTF8.GetString(Assert.Single(server.Received).Body));
        Assert.Contains("Code=Fabc", body);
    }

    // ------------------------------------------------------------------
    // multipart：Memory 形态的二进制同样是文件部件，不能「接受后静默忽略」
    // ------------------------------------------------------------------

    [Fact]
    public void MemoryBytePart_IsRejectedLikeByteArray()
    {
        Assert.Throws<InvalidOperationException>(
            () => new ApiClient("http://127.0.0.1:1/").GetUri(new MemoryPart()));
    }

    [Fact]
    public void BinaryCollectionPart_IsRejectedLikeScalarPart()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ApiClient("http://127.0.0.1:1/").GetUri(new BinaryCollectionPart()));

        Assert.Contains("文件部件", ex.Message);
    }

    // ------------------------------------------------------------------
    // 查询通道：只有成员声明、没有实例钩子时同样只能格式化一次
    // ------------------------------------------------------------------

    [Fact]
    public async Task Query_StringDelegateFormat_AppliesOnce_WithoutHook()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new QueryStringFormat { Code = "abc" });

        var query = Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty);
        Assert.Equal("Code=Fabc", query);
    }

    [Fact]
    public async Task Query_ListOfStringDelegateFormat_AppliesOnce_WithoutHook()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new QueryStringListFormat { Tags = { "a", "b" } });

        var query = Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty);
        Assert.Equal("Tags[]=Fa&Tags[]=Fb", query);
    }

    // ------------------------------------------------------------------
    // 裸标量正文：钩子「本次不处理」（返回 null）不能改变二进制正文的默认形态
    // ------------------------------------------------------------------

    [Fact]
    public async Task BinaryBody_WithDecliningHook_KeepsRawBytes()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        var payload = new byte[] { 1, 2, 3, 0xFF };

        using (var plain = new ApiClient(server.Url))
        {
            await plain.RequestAsync(new BinaryBody { Payload = payload });
        }

        using (var hooked = new ApiClient(server.Url)
        {
            // 只处理 int 的钩子：对 byte[] 返回 null 时必须保持原始字节，而不是退化成 base64 文本正文
            ValueFormatter = value => value is int number ? "p" + number.ToString(CultureInfo.InvariantCulture) : null
        })
        {
            await hooked.RequestAsync(new BinaryBody { Payload = payload });
        }

        Assert.Equal(2, server.Received.Count);
        Assert.Equal(payload, server.Received[0].Body);
        Assert.Equal(payload, server.Received[1].Body);
    }

    // ------------------------------------------------------------------
    // 反射与源生成：基类声明 + override 的 [Format] 必须同口径
    // ------------------------------------------------------------------

    [Fact]
    public void Reflection_InheritsFormatFromBaseOverride()
    {
        var property = typeof(DerivedFormat).GetProperty(nameof(DerivedFormat.When))!;
        Assert.NotNull(property.GetCustomAttribute<FormatAttribute>(inherit: true));
    }

    [Fact]
    public async Task Generated_OverrideFormat_MatchesReflectionParams()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var generated = new ApiClient(server.Url);
        await generated.RequestAsync(new DerivedRequest { When = new DateTime(2024, 3, 4, 0, 0, 0, DateTimeKind.Utc) });

        using var reflection = new AxiosClient();
        await reflection.GetAsync(server.Url + "q", c => c.Params = new DerivedFormat
        {
            When = new DateTime(2024, 3, 4, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.Equal(2, server.Received.Count);
        var generatedQuery = Uri.UnescapeDataString(server.Received[0].Query ?? string.Empty);
        var reflectionQuery = Uri.UnescapeDataString(server.Received[1].Query ?? string.Empty);
        Assert.Equal(generatedQuery, reflectionQuery);
    }

    // ------------------------------------------------------------------
    // 定值通道：Path / Header 在绑定期定型，钩子必须只作用一次
    // ------------------------------------------------------------------

    [Fact]
    public async Task PathAndHeader_ApplyHookOnceAtBindTime()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url)
        {
            ValueFormatter = value => value is DateTime when ? when.ToString("yyyy-MM", CultureInfo.InvariantCulture) : null
        };

        var when = new DateTime(2024, 3, 4, 0, 0, 0, DateTimeKind.Utc);
        await client.RequestAsync(new PathHeaderRequest { When = when, HeaderWhen = when });

        var received = Assert.Single(server.Received);
        Assert.Equal("/probe/path/2024-03", received.Path);
        Assert.Equal("2024-03", received.Headers["X-When"]);
    }

    // ------------------------------------------------------------------
    // 可空标量：拆包后走同一条格式化链
    // ------------------------------------------------------------------

    [Fact]
    public async Task NullableDateOnly_AppliesMemberFormat()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);
        await client.RequestAsync(new NullableRequest { Day = new DateOnly(2024, 3, 4) });

        var query = Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty);
        Assert.Equal("Day=2024-03-04", query);
    }

    // ------------------------------------------------------------------
    // DateOnly 线上形态：ISO 日期；且与旧规则（不变文化的短日期）不同
    // ------------------------------------------------------------------

    [Fact]
    public async Task DateOnly_Query_UseIsoDate()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new AxiosClient();

        var oldRule = new DateOnly(2024, 1, 2).ToString(null, CultureInfo.InvariantCulture);

        await client.GetAsync(server.Url + "q", c => c.Params = new { d = new DateOnly(2024, 1, 2) });

        Assert.Equal("d=2024-01-02", server.Received[0].Query);

        // 旧规则（IFormattable + InvariantCulture）与新形态不同：这是可观察的行为变更。
        Assert.NotEqual("2024-01-02", oldRule);
    }

    // ------------------------------------------------------------------
    // 测试模型
    // ------------------------------------------------------------------

    [Get("probe/hook")]
    private sealed class HookQuery : IApiRequest<Person>
    {
        [Query]
        public int Id { get; set; }

        [Query, Format("yyyy-MM")]
        public DateTime Month { get; set; }
    }

    public sealed class Item
    {
        public string Name { get; set; } = string.Empty;

        public int Age { get; set; }
    }

    [Get("probe/list")]
    private sealed class ListQuery : IApiRequest<Person>
    {
        [Query]
        public List<Item> Items { get; set; } = new();
    }

    [Get("probe/map-csv")]
    private sealed class MapAndCsvQuery : IApiRequest<Person>
    {
        [Query]
        public Dictionary<string, DateTime> When { get; set; } = new();

        [Query]
        public List<int> Codes { get; set; } = new();
    }

    [Post("probe/mp")]
    [Multipart]
    private sealed class MultipartString : IApiRequest<Person>
    {
        [Part, Format(typeof(Formats), nameof(Formats.PrefixedString))]
        public string Code { get; set; } = "abc";
    }

    public sealed class StringPayload
    {
        [Format(typeof(Formats), nameof(Formats.PrefixedString))]
        public string Code { get; set; } = "abc";
    }

    [Post("probe/mp-memory")]
    [Multipart]
    private sealed class MemoryPart : IApiRequest<Person>
    {
        [Part, Format(typeof(Formats), nameof(Formats.Hex))]
        public ReadOnlyMemory<byte> Data { get; set; }
    }

    [Post("probe/mp-binary-collection")]
    [Multipart]
    private sealed class BinaryCollectionPart : IApiRequest<Person>
    {
        [Part, Format(typeof(Formats), nameof(Formats.Hex))]
        public List<ReadOnlyMemory<byte>> Data { get; set; } = new();
    }

    [Post("probe/binary-body")]
    private sealed class BinaryBody : IApiRequest<Person>
    {
        [Body]
        public byte[] Payload { get; set; } = Array.Empty<byte>();
    }

    [Get("probe/query-string-format")]
    private sealed class QueryStringFormat : IApiRequest<Person>
    {
        [Query, Format(typeof(Formats), nameof(Formats.PrefixedString))]
        public string Code { get; set; } = "abc";
    }

    [Get("probe/query-string-list-format")]
    private sealed class QueryStringListFormat : IApiRequest<Person>
    {
        [Query, Format(typeof(Formats), nameof(Formats.PrefixedString))]
        public List<string> Tags { get; set; } = new();
    }

    public class BaseFormat
    {
        [Format("yyyy-MM")]
        public virtual DateTime When { get; set; }
    }

    public sealed class DerivedFormat : BaseFormat
    {
        public override DateTime When { get; set; }
    }

    [Get("probe/path/{when}")]
    private sealed class PathHeaderRequest : IApiRequest<Person>
    {
        [Path]
        public DateTime When { get; set; }

        [Header("X-When")]
        public DateTime HeaderWhen { get; set; }
    }

    [Get("probe/optional")]
    private sealed class NullableRequest : IApiRequest<Person>
    {
        [Query, Format("yyyy-MM-dd")]
        public DateOnly? Day { get; set; }
    }

    [Get("probe/derived")]
    private sealed class DerivedRequest : BaseFormat, IApiRequest<Person>
    {
        public override DateTime When { get; set; }
    }
}

internal static class Formats
{
    public static string? PrefixedString(object? value) => value is string text ? "F" + text : null;

    public static string? Hex(object? value) => value is ReadOnlyMemory<byte> memory ? Convert.ToHexString(memory.Span) : null;
}
