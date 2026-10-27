
using AxiosNet.Pooling;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net;
using System.Security.Authentication;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;

namespace AxiosNet.Tests.Requests;

/// <summary>
/// 查询参数：对象/字典/集合/已编码串、嵌套方括号、循环引用、序列化器覆盖。
/// </summary>
public sealed class ParamsTests
{
    [Fact]
    public async Task Params_SelfReferencingGraph_ThrowsAxiosException()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        var node = new Node { Name = "root" };
        node.Children.Add(node);

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "q", c => c.Params = node));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Params_SharedChildObject_IsNotTreatedAsCycle()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var shared = new Leaf { Value = "v" };
        var root = new Pair { Left = shared, Right = shared };

        await client.GetAsync("q", c => c.Params = root);

        // 键名的方括号不转义。
        Assert.Contains("Left[Value]=v", server.Received[0].RawUrl);
        Assert.Contains("Right[Value]=v", server.Received[0].RawUrl);
    }

    // ------------------------------------------------------------------
    // 5) 自定义 paramsSerializer 的错误模型
    // ------------------------------------------------------------------

    [Fact]
    public async Task CustomParamsSerializerThrows_ReportsOptionError()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync("x", c => c.Serialization.ParamsSerializer = _ => throw new InvalidOperationException("bad params")));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.IsType<InvalidOperationException>(ex.Cause);
        Assert.Empty(server.Received);
    }

    // ---------------------------------------------------------------
    // 1/2：params 收尾与嵌套键转义
    // ---------------------------------------------------------------

    [Fact]
    public async Task Params_NestedAndSpecialKeys_StayStableAcrossRequests()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        // 同一份 params 发两次：属性名后缀按属性缓存、字典键每请求转义，两条路径结果必须一致
        // （键名保留属性名原形；方括号按 axios 默认 encoder 的规则还原，空格转 %20）
        for (var i = 0; i < 2; i++)
        {
            await client.GetAsync("search", c => c.Params = new NestedParams
            {
                Filter = new Dictionary<string, object?> { ["a b"] = 1, ["c[d]"] = 2 },
                Page = 3,
            });
        }

        Assert.All(server.Received, r => Assert.Equal("Filter[a%20b]=1&Filter[c[d]]=2&Page=3", r.Query));
    }

    // ------------------------------------------------------------------
    // 标量文本形态：TimeOnly 秒 / DateTime(Unspecified) UTC
    // ------------------------------------------------------------------

    [Fact]
    public async Task Query_TimeOnly_KeepsSeconds_AndUnspecifiedDateTimeIsUtc()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new AxiosClient();

        // TimeOnly 走往返格式（与请求体路径同一份 "O"）：秒与小数位都保留。
        // 注意走 configure 写 Params：GetAsync(url, data) 重载会把匿名对象当请求体。
        await client.GetAsync(server.Url + "q", c => c.Params = new { t = new TimeOnly(16, 30, 15) });
        Assert.Contains("16%3A30%3A15.0000000", server.Received[0].Query);

        // DateTime(Unspecified) 按 UTC 解释：同一份数据在任何时区的机器上发出同样的字节
        //（按本机时区折算会在东八区发出 04:00:00.000Z）。
        await client.GetAsync(server.Url + "q", c => c.Params = new { d = new DateTime(2026, 10, 24, 12, 0, 0) });
        Assert.Contains("2026-10-24T12%3A00%3A00.000Z", server.Received[1].Query);
    }

    [Fact]
    public async Task ParamsWithJsonElementValues_ReachServerEvenWithFragmentUrl()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();
        using var document = JsonDocument.Parse("""{"q":"a b","tags":["x","y"],"skip":null}""");

        await client.GetAsync(server.Url + "search#top", c => c.Params = document.RootElement);

        var received = server.Received[0];
        Assert.Equal("/search", received.Path);
        Assert.Equal("q=a%20b&tags[]=x&tags[]=y", received.Query);
    }

    // ------------------------------------------------------------------
    // params 的根类型
    // ------------------------------------------------------------------

    [Fact]
    public async Task Params_String_IsTreatedAsAlreadySerializedQuery()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        // 之前落进反射分支，被序列化成 "Length=7"（string 的 CLR 属性）
        await client.GetAsync(server.Url + "search", c => c.Params = "a=1&b=2");

        Assert.Equal("a=1&b=2", server.Received[0].Query);
    }

    [Fact]
    public void Params_String_LeadingQuestionMark_AndAmpersand_AreNormalized()
    {
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://example.com/" });

        Assert.Equal("http://example.com/p?a=1&b=2", client.GetUri(c =>
        {
            c.Url = "p";
            c.Params = "?a=1&b=2";
        }));

        Assert.Equal("http://example.com/p?a=1", client.GetUri(c =>
        {
            c.Url = "p";
            c.Params = "  &a=1  ";
        }));
    }

    [Fact]
    public void Params_RootCollection_UsesIndexKeys_InsteadOfClrProperties()
    {
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://example.com/" });

        // 之前输出 "Length=2&LongLength=2&Rank=1&SyncRoot[]=1&...IsSynchronized=false"
        Assert.Equal(
            "http://example.com/p?[0]=1&[1]=2",
            client.GetUri(c =>
            {
                c.Url = "p";
                c.Params = new[] { 1, 2 };
            }));

        Assert.Equal(
            "http://example.com/p?[0]=x&[1]=y",
            client.GetUri(c =>
            {
                c.Url = "p";
                c.Params = new List<string> { "x", "y" };
            }));
    }

    [Fact]
    public void Params_EmptyRootCollection_ProducesNoQuery()
    {
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://example.com/" });

        Assert.Equal("http://example.com/p", client.GetUri(c =>
        {
            c.Url = "p";
            c.Params = Array.Empty<int>();
        }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(42)]
    [InlineData(3.5)]
    public void Params_RootScalar_ThrowsBadOptionValue_InsteadOfSilentlyDropping(object scalar)
    {
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://example.com/" });

        var ex = Assert.Throws<AxiosException>(() => client.GetUri(c => c.Params = scalar));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.NotNull(ex.Config);
    }

    [Fact]
    public void Params_JsonElementRootScalar_ThrowsBadOptionValue()
    {
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://example.com/" });
        using var document = JsonDocument.Parse("\"just-text\"");

        var ex = Assert.Throws<AxiosException>(() => client.GetUri(c => c.Params = document.RootElement));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    [Fact]
    public void Params_NullJsonElement_ProducesNoQuery()
    {
        // null 不是「无法表达键名的标量」，而是「没有参数」，必须继续安静跳过。
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://example.com/" });
        using var document = JsonDocument.Parse("null");

        Assert.Equal("http://example.com/p", client.GetUri(c =>
        {
            c.Url = "p";
            c.Params = document.RootElement;
        }));
    }

    // ------------------------------------------------------------------
    // url 结尾的 ? / &
    // ------------------------------------------------------------------

    [Fact]
    public void Params_UrlEndingWithSeparator_DoesNotIntroduceEmptySegment()
    {
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://example.com/" });

        // 之前是 "http://example.com/p?&a=1"，服务端会多解析出一个无名空参数
        Assert.Equal("http://example.com/p?a=1", client.GetUri(c =>
        {
            c.Url = "p?";
            c.Params = new { a = 1 };
        }));

        Assert.Equal("http://example.com/p?a=1&b=2", client.GetUri(c =>
        {
            c.Url = "p?a=1&";
            c.Params = new { b = 2 };
        }));
    }

    // params 不能落到 URL fragment 里

    [Theory]
    [InlineData("http://a.com/p#top", "http://a.com/p?q=1#top")]
    [InlineData("http://a.com/p?z=0#top", "http://a.com/p?z=0&q=1#top")]
    public void Params_AreAppendedBeforeFragment(string url, string expected)
    {
        using var client = new AxiosClient();

        var uri = client.GetUri(c =>
        {
            c.Url = url;
            c.Params = new { q = 1 };
        });

        Assert.Equal(expected, uri);
    }

    [Fact]
    public async Task Params_WithFragment_ReachTheServer()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.GetAsync(server.Url + "p#top", c => c.Params = new { q = 1 });

        Assert.Equal("/p", server.Received[0].Path);
        Assert.Equal("q=1", server.Received[0].Query);
    }

    // params 传 JsonElement 按 JSON 树展开

    [Fact]
    public void Params_WithJsonElement_AreFlattened()
    {
        using var client = new AxiosClient();
        using var document = JsonDocument.Parse("""{"a":1,"b":["x","y"],"c":{"d":true},"e":null}""");

        var uri = client.GetUri(c =>
        {
            c.Url = "http://a.com/p";
            c.Params = document.RootElement;
        });

        Assert.Equal("http://a.com/p?a=1&b[]=x&b[]=y&c[d]=true", uri);
    }

    // ------------------------------------------------------------------
    // L3：byte[] 查询参数 → base64。
    // ------------------------------------------------------------------

    [Fact]
    public void ByteArrayParams_SerializeAsReversibleBase64()
    {
        using var client = new AxiosClient();
        var payload = new byte[] { 1, 2, 3 };

        var uri = client.GetUri(c =>
        {
            c.Url = "http://host/search";
            c.Params = new { bin = payload };
        });

        Assert.Equal("http://host/search?bin=AQID", uri);
        Assert.Equal(payload, Convert.FromBase64String("AQID"));
    }

    [Fact]
    public async Task Params_Object_ArraysUseBrackets_NestedUseBracketNotation_NullSkipped()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.GetAsync(server.Url + "search", c =>
            c.Params = new
            {
                q = "a b",
                ids = new[] { 1, 2 },
                nested = new { page = 1 },
                missing = (string?)null
            });

        var query = server.Received[0].Query;
        Assert.Equal("q=a%20b&ids[]=1&ids[]=2&nested[page]=1", query);
    }

    [Fact]
    public async Task Params_Dictionary_IsSerializedTheSameWay()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.GetAsync(server.Url + "search", c =>
            c.Params = new Dictionary<string, object?>
            {
                ["tags"] = new[] { "x", "y" },
                ["filter"] = new Dictionary<string, object?> { ["kind"] = "book" }
            });

        var query = server.Received[0].Query;
        Assert.Equal("tags[]=x&tags[]=y&filter[kind]=book", query);
    }

    [Fact]
    public async Task Params_AppendedToUrlThatAlreadyHasQuery()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.GetAsync(server.Url + "search?x=1", c => c.Params = new { y = 2 });

        Assert.Equal("x=1&y=2", server.Received[0].Query);
    }

    // ---------------------------------------------------------------
    // P1：配置合并与序列化细节
    // ---------------------------------------------------------------

    [Fact]
    public void ParamsObjectWithIndexer_IsSkippedInsteadOfThrowing()
    {
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://127.0.0.1:1/" });

        var uri = client.GetUri(c =>
        {
            c.Url = "search";
            c.Params = new WithIndexer { Name = "n", Page = 2 };
        });

        Assert.Contains("Name=n", uri);
        Assert.Contains("Page=2", uri);
    }

    [Fact]
    public async Task ParamsSerializer_Custom_ReplacesBuiltInEncoding()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.GetAsync(server.Url + "search", c =>
        {
            c.Params = new Dictionary<string, object?> { ["ids"] = new[] { 1, 2, 3 }, ["q"] = "x y" };
            c.Serialization.ParamsSerializer = p =>
            {
                var dict = (Dictionary<string, object?>)p!;
                return $"ids={string.Join(",", (int[])dict["ids"]!)}&q={Uri.EscapeDataString((string)dict["q"]!)}";
            };
        });

        Assert.Equal("ids=1,2,3&q=x%20y", server.Received[0].Query);
    }

    private static string Body(TestRequest request) => Encoding.UTF8.GetString(request.Body);

    // 表单 / multipart / params 展平的环检测。
    private sealed class Node
    {
        public string? Name { get; set; }
        public List<Node> Children { get; } = new();
    }

    private sealed class Pair
    {
        public Leaf? Left { get; set; }
        public Leaf? Right { get; set; }
    }

    private sealed class Leaf
    {
        public string? Value { get; set; }
    }

    private sealed class BlockingDisposeHandler : HttpMessageHandler
    {
        private readonly ManualResetEventSlim _entered;
        private readonly ManualResetEventSlim _release;
        private int _disposed;

        public BlockingDisposeHandler(ManualResetEventSlim entered, ManualResetEventSlim release)
        {
            _entered = entered;
            _release = release;
        }

        public bool Disposed => Volatile.Read(ref _disposed) == 1;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _entered.Set();
                _release.Wait();
            }

            base.Dispose(disposing);
        }
    }

    // IReadOnlyDictionary 按键值对展平。


    // ---------------------------------------------------------------
    // 测试模型与序列化器夹具
    // ---------------------------------------------------------------

    private sealed class NestedParams
    {
        public Dictionary<string, object?>? Filter { get; set; }

        public int Page { get; set; }
    }


    // ------------------------------------------------------------------
    // 测试辅助
    // ------------------------------------------------------------------


    /// <summary>固定应答的离线 Handler（回放语义用例只需要一条稳定的响应）。</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_respond(request));
    }


    /// <summary>把请求挡在 <see cref="TaskCompletionSource"/> 之后的测试 Handler。</summary>
    private sealed class GatedHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _gate;

        public GatedHandler(TaskCompletionSource gate) => _gate = gate;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await _gate.Task.ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json"),
                RequestMessage = request
            };
        }
    }

    // ---------------------------------------------------------------
    // 辅助
    // ---------------------------------------------------------------

    /// <summary>
    /// 正文读取阶段的超时预算。必须同时满足两头：
    /// <list type="bullet">
    /// <item>远大于「已预热连接的一次本地往返」——否则预算会先被响应头阶段吃掉，用例根本进不到正文阶段；</item>
    /// <item>远小于正文延迟（<see cref="SlowBodyDelay"/>）——否则读会自己等到正文，区分不出「超时生效」。</item>
    /// </list>
    /// <para>与 <c>StreamCancellationTests</c> 同一口径：预算与自然完成时间差一个数量级。</para>
    /// </summary>
    private static readonly TimeSpan BodyReadTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 服务端「先发响应头、再延迟正文」的延迟：必须远大于 <see cref="BodyReadTimeout"/>，
    /// 这样「被超时打断」与「自己等到正文」才不会混淆，调度抖动晚到几秒也仍有充足余量。
    /// </summary>
    private static readonly TimeSpan SlowBodyDelay = TimeSpan.FromSeconds(20);


    private sealed class WithIndexer
    {
        public string Name { get; set; } = string.Empty;

        public int Page { get; set; }

        public string this[int index] => "value-" + index;
    }

}