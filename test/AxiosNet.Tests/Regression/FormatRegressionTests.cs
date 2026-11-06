using System.Globalization;
using System.Reflection;
using System.Text;
using AxiosNet.Metadata;
using AxiosNet.Retry;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.Tests.WebAPI;
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
        using var client = new ApiClient(server.Url);
        // 只处理字符串的钩子：它只应看到原始 int / DateTime，不应看到已定型的文本
        client.Axios.Defaults.Serialization.ValueText = (value, _) => value is string text ? "X" + text : null;

        await client.RequestAsync(new HookQuery { Id = 7, Month = new DateTime(2024, 3, 4, 0, 0, 0, DateTimeKind.Utc) });

        var query = Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty);
        Assert.Equal("Id=7&Month=2024-03", query);
    }

    [Fact]
    public async Task Query_MapAndCsvCollection_AreNotHookedTwice()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);
        client.Axios.Defaults.Serialization.ValueText = (value, _) => value is string text ? "X" + text : null;

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

        using (var hooked = new ApiClient(server.Url, c => c.Serialization.ValueText = (_, _) => null))
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

        using var hooked = new ApiClient(server.Url);

        // 只处理 int 的钩子：对 byte[] 返回 null 时必须保持原始字节，而不是退化成 base64 文本正文
        hooked.Axios.Defaults.Serialization.ValueText =
            (value, _) => value is int number ? "p" + number.ToString(CultureInfo.InvariantCulture) : null;

        await hooked.RequestAsync(new BinaryBody { Payload = payload });

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
        using var client = new ApiClient(server.Url);
        client.Axios.Defaults.Serialization.ValueText = (value, _) => value is DateTime when ? when.ToString("yyyy-MM", CultureInfo.InvariantCulture) : null;

        var when = new DateTime(2024, 3, 4, 0, 0, 0, DateTimeKind.Utc);
        await client.RequestAsync(new PathHeaderRequest { When = when, HeaderWhen = when });

        var received = Assert.Single(server.Received);
        Assert.Equal("/probe/path/2024-03", received.Path);
        Assert.Equal("2024-03", received.Headers["X-When"]);
    }

    // ------------------------------------------------------------------
    // 请求头集合：字典值与单值头同一口径，逐项经过钩子且只经过一次
    // ------------------------------------------------------------------

    [Fact]
    public async Task HeaderCollection_ValuesGoThroughHookOnce()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        var seen = new List<object?>();
        using var client = new ApiClient(server.Url);
        // 只处理字符串的钩子：单值头与字典头都该命中它，且字典值只命中一次
        client.Axios.Defaults.Serialization.ValueText = (value, _) =>
        {
            seen.Add(value);
            return value is string text ? "H" + text : null;
        };

        await client.RequestAsync(new MixedHeaderRequest
        {
            One = "v",
            Extra = new Dictionary<string, string> { ["X-K"] = "raw" }
        });

        var received = Assert.Single(server.Received);
        Assert.Equal("Hv", received.Headers["X-One"]);
        Assert.Equal("Hraw", received.Headers["X-K"]);

        // 单值头一次、字典值一次：绑定期定型后不会再被正文工厂之类的位置套第二遍
        Assert.Equal(new object?[] { "v", "raw" }, seen);
    }

    // ------------------------------------------------------------------
    // 裸标量正文：直接调用与声明式同一口径，钩子看到原值一次
    // ------------------------------------------------------------------

    [Fact]
    public async Task DirectScalarBody_GoesThroughHook()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new AxiosClient();

        await client.PostAsync(server.Url + "b", 42, c =>
        {
            c.ContentType = "text/plain";
            c.Serialization.ValueText = (value, _) => value is int number ? "v" + number.ToString(CultureInfo.InvariantCulture) : null;
        });

        Assert.Equal("v42", Encoding.UTF8.GetString(Assert.Single(server.Received).Body));
    }

    [Fact]
    public async Task DeclarativeScalarBody_HookCalledOnce()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        var seen = new List<object?>();
        using var client = new ApiClient(server.Url);
        // 返回 null 的钩子最容易被问两遍：绑定期一次、正文工厂一次
        client.Axios.Defaults.Serialization.ValueText = (value, _) =>
        {
            seen.Add(value);
            return null;
        };

        await client.RequestAsync(new ScalarBodyRequest { Payload = new DateTime(2024, 3, 5, 6, 7, 8, DateTimeKind.Utc) });

        Assert.Equal("2024-03-05T06:07:08.0000000Z", Encoding.UTF8.GetString(Assert.Single(server.Received).Body));
        Assert.Single(seen);
    }

    // ------------------------------------------------------------------
    // 钩子只有一处（AxiosConfig.ValueFormatter）：两条入口共用，按请求可覆盖
    // ------------------------------------------------------------------

    /// <summary>接口式代理读的也是实例默认值上的钩子（与请求模型走同一个来源）。</summary>
    [Fact]
    public async Task InterfaceProxy_UsesInstanceHook()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url, c => c.Serialization.ValueText = (value, _) =>
            value is int number ? "H" + number.ToString(CultureInfo.InvariantCulture) : null);

        await client.Create<IUserApi>().GetAsync(7);

        Assert.Equal("/users/H7", Assert.Single(server.Received).Path);
    }

    /// <summary>
    /// 字符串正文的两条口径：声明式把它当裸标量正文（值 → 文本，过钩子），
    /// 直接调用的 <c>string</c> 是「原样正文」，没有这一步。
    /// </summary>
    [Fact]
    public async Task StringBody_HookedOnlyOnTheDeclarativePath()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url, c => c.Serialization.ValueText = (_, _) => "HOOKED");

        await client.RequestAsync(new StringBodyRequest { Text = "raw" });
        await client.Axios.PostAsync(server.Url + "raw-str", "raw", c => c.ContentType = "text/plain");

        Assert.Equal(2, server.Received.Count);
        Assert.Equal("HOOKED", Encoding.UTF8.GetString(server.Received[0].Body));
        Assert.Equal("raw", Encoding.UTF8.GetString(server.Received[1].Body));
    }

    /// <summary>模型自己的 <c>Configure</c> 里换钩子，同样算「绑定期之后」，要被护栏拦下。</summary>
    [Fact]
    public async Task ModelConfigureSettingHook_IsRejected()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        var error = await Assert.ThrowsAsync<AxiosException>(() => client.RequestAsync(new SelfConfiguringRequest()));

        Assert.Equal(AxiosException.ErrBadOptionValue, error.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task InstanceHook_CoversDeclarativeAndDirectCalls()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url, c =>
            c.Serialization.ValueText = (value, _) => value is DateTime date
                ? "d" + date.ToString("yyyy", CultureInfo.InvariantCulture)
                : null);

        // 声明式：路径段与请求头都走同一个钩子
        var when = new DateTime(2024, 3, 4, 0, 0, 0, DateTimeKind.Utc);
        await client.RequestAsync(new PathHeaderRequest { When = when, HeaderWhen = when });

        // 直接调用：裸标量正文也走同一个钩子（实例默认值里的钩子不能被按请求配置冲掉）
        await client.Axios.PostAsync(server.Url + "raw", new DateTime(2024, 5, 6, 0, 0, 0, DateTimeKind.Utc), c =>
            c.ContentType = "text/plain");

        Assert.Equal(2, server.Received.Count);
        Assert.Equal("/probe/path/d2024", server.Received[0].Path);
        Assert.Equal("d2024", server.Received[0].Headers["X-When"]);
        Assert.Equal("d2024", Encoding.UTF8.GetString(server.Received[1].Body));
    }

    [Fact]
    public async Task PerCallHook_OverridesInstanceHook()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url, c => c.Serialization.ValueText = (_, _) => "D");

        await client.Axios.PostAsync(server.Url + "raw", 42);
        await client.Axios.PostAsync(server.Url + "raw", 42, c => c.Serialization.ValueText = (_, _) => "P");

        Assert.Equal(2, server.Received.Count);
        Assert.Equal("D", Encoding.UTF8.GetString(server.Received[0].Body));
        Assert.Equal("P", Encoding.UTF8.GetString(server.Received[1].Body));
    }

    [Fact]
    public async Task DeclarativeRequest_CannotRetargetHookAfterBinding()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url, c => c.Serialization.ValueText = (_, _) => "D");

        var when = new DateTime(2024, 3, 4, 0, 0, 0, DateTimeKind.Utc);
        var error = await Assert.ThrowsAsync<AxiosException>(() =>
            client.RequestAsync(
                new PathHeaderRequest { When = when, HeaderWhen = when },
                c => c.Serialization.ValueText = (_, _) => "P"));

        // 绑定期已经用旧钩子写完 URL / 头 / 正文，再换只会半生效：直接拒绝，不留静默行为
        Assert.Equal(AxiosException.ErrBadOptionValue, error.Code);
        Assert.Empty(server.Received);
    }

    // ------------------------------------------------------------------
    // 直接调用（AxiosClient）的钩子覆盖面：查询 / 表单 / 裸标量正文；路径与头没有这一步
    // ------------------------------------------------------------------

    [Fact]
    public async Task DirectCall_HookCoversQueryFormAndScalarBody()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        var seen = new List<ValueTextChannel>();
        using var client = new AxiosClient();
        client.Defaults.Serialization.ValueText = (value, channel) =>
        {
            seen.Add(channel);
            return value is DateTime date ? "d" + date.ToString("yyyy", CultureInfo.InvariantCulture) : null;
        };

        var when = new DateTime(2024, 3, 4, 0, 0, 0, DateTimeKind.Utc);

        await client.GetAsync(server.Url + "q", c => c.Params = new { when });
        await client.PostAsync(server.Url + "f", new { when }, c => c.ContentType = AxiosConfig.FormUrlEncodedContentType);
        await client.PostAsync(server.Url + "b", when, c => c.ContentType = "text/plain");
        await client.GetAsync(server.Url + "h", c => c.Headers.Set("X-Raw", "raw"));

        Assert.Equal(4, server.Received.Count);
        Assert.Equal("when=d2024", server.Received[0].Query);
        Assert.Equal("when=d2024", Uri.UnescapeDataString(Encoding.UTF8.GetString(server.Received[1].Body)));
        Assert.Equal("d2024", Encoding.UTF8.GetString(server.Received[2].Body));

        // 头就是最终文本，没有「值 → 文本」这一步：钩子一次都不该被它触发
        Assert.Equal("raw", server.Received[3].Headers["X-Raw"]);
        Assert.Equal(
            new[] { ValueTextChannel.Query, ValueTextChannel.Form, ValueTextChannel.ScalarBody },
            seen);
    }

    /// <summary>
    /// 钩子在请求构造期只跑一次：重试是重发同一份已定型的请求，
    /// 不该再问一遍钩子（否则带副作用的钩子会随重试次数累加）。
    /// </summary>
    [Fact]
    public async Task Retry_DoesNotRerunHook()
    {
        var attempts = 0;
        await using var server = TestHttpServer.Start(_ =>
            Interlocked.Increment(ref attempts) == 1 ? new TestResponse(500) : TestJson.Response(new Person("ok", 1)));

        var calls = 0;
        using var client = new AxiosClient();

        await client.GetAsync(server.Url + "q", c =>
        {
            c.Params = new { when = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc) };
            c.Serialization.ValueText = (value, _) =>
            {
                calls++;
                return value is DateTime ? "d2024" : null;
            };
            c.Resilience.Retry.Policy = new RetryPolicy { MaxRetries = 1, Delay = TimeSpan.Zero };
        });

        Assert.Equal(2, server.Received.Count);
        Assert.Equal(1, calls);
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

    [Get("probe/mixed-header")]
    private sealed class MixedHeaderRequest : IApiRequest<Person>
    {
        [Header("X-One")]
        public string One { get; set; } = string.Empty;

        [HeaderCollection]
        public Dictionary<string, string> Extra { get; set; } = new();
    }

    [Get("probe/scalar-body")]
    private sealed class ScalarBodyRequest : IApiRequest<Person>
    {
        [Body]
        public DateTime Payload { get; set; }
    }

    [Post("probe/string-body")]
    private sealed class StringBodyRequest : IApiRequest<Person>
    {
        [Body]
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>在自己的 <c>Configure</c> 里换钩子：绑定期已过，会被护栏拒绝。</summary>
    [Get("probe/self-configuring")]
    private sealed class SelfConfiguringRequest : ApiRequest<Person>
    {
        /// <summary>按请求调整配置：这里故意改钩子。</summary>
        protected override void Configure(AxiosConfig config) => config.Serialization.ValueText = (_, _) => "bad";
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
    public static string? PrefixedString(object? value, ValueTextChannel _) => value is string text ? "F" + text : null;

    public static string? Hex(object? value, ValueTextChannel _) => value is ReadOnlyMemory<byte> memory ? Convert.ToHexString(memory.Span) : null;
}
