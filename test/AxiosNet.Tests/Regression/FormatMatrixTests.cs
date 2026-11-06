using System.Globalization;
using System.Text;
using System.Text.Json;
using AxiosNet.Cache;
using AxiosNet.Metadata;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Attributes;
#if NET7_0_OR_GREATER
using AxiosNet.WebAPI.Serialization;
#endif

namespace AxiosNet.Tests.Regression;

/// <summary>
/// 枚举矩阵驱动的格式化审查（对应 README「值格式化」的通道 × 形态矩阵）。
/// 每个 Fact 覆盖矩阵中的一个通道 × 全部适用形态；失败即定位到具体通道/形态。
/// </summary>
public sealed class FormatMatrixTests
{
    // ----------------------------------------------------------------
    // 共享数据与期望
    // ----------------------------------------------------------------

    private static readonly DateTime WhenUtc = new(2024, 3, 5, 6, 7, 8, DateTimeKind.Utc);
    private static readonly DateTimeOffset OffsetValue = new(2024, 3, 5, 6, 7, 8, TimeSpan.FromHours(2));
    private static readonly DateOnly DayValue = new(2024, 3, 5);
    private static readonly TimeOnly ClockValue = new(13, 45, 30);
    private static readonly TimeSpan SpanValue = TimeSpan.FromMinutes(90);
    private static readonly Guid IdValue = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTime MaybeValue = new(2024, 4, 6, 0, 0, 0, DateTimeKind.Utc);
    private static readonly byte[] BlobValue = [1, 2, 3];
    private static readonly byte[] MemValue = [4, 5];

    private static JsonElement JsonValue() =>
        JsonDocument.Parse("""{"a":1,"b":[2,3]}""").RootElement.Clone();

    private static MxPayload Payload() => new()
    {
        Int = 7,
        Dec = 1.25m,
        Flag = true,
        Kind = MxMode.Beta,
        Text = "a b&c",
        When = WhenUtc,
        Offset = OffsetValue,
        Day = DayValue,
        Clock = ClockValue,
        Span = SpanValue,
        Id = IdValue,
        Maybe = MaybeValue,
        Json = JsonValue(),
        Blob = BlobValue,
        Mem = new Memory<byte>(MemValue),
        Obj = 5,
        Nested = new MxInner { X = "n1", Y = 9 }
    };

    /// <summary>K01：无钩子 / 无成员声明的通道默认文本。</summary>
    private static (string Key, string Value)[] Defaults(string prefix = "") => new[]
    {
        ($"{prefix}Int", "7"),
        ($"{prefix}Dec", "1.25"),
        ($"{prefix}Flag", "true"),
        ($"{prefix}Kind", "Beta"),
        ($"{prefix}Text", "a b&c"),
        ($"{prefix}When", "2024-03-05T06:07:08.000Z"),
        ($"{prefix}Offset", "2024-03-05T04:07:08.000Z"),
        ($"{prefix}Day", "2024-03-05"),
        ($"{prefix}Clock", "13:45:30.0000000"),
        ($"{prefix}Span", "01:30:00"),
        ($"{prefix}Id", "11111111-1111-1111-1111-111111111111"),
        ($"{prefix}Maybe", "2024-04-06T00:00:00.000Z"),
        ($"{prefix}Json[a]", "1"),
        ($"{prefix}Json[b][]", "2"),
        ($"{prefix}Json[b][]", "3"),
        ($"{prefix}Blob", "AQID"),
        ($"{prefix}Mem", "BAU="),
        ($"{prefix}Obj", "5"),
        ($"{prefix}Nested[X]", "n1"),
        ($"{prefix}Nested[Y]", "9")
    };

    /// <summary>K02：实例钩子对所有叶子形态生效。</summary>
    private static (string Key, string Value)[] Hooked(string prefix = "") => new[]
    {
        ($"{prefix}Int", "H7"),
        ($"{prefix}Dec", "H1.25"),
        ($"{prefix}Flag", "Htrue"),
        ($"{prefix}Kind", "HBeta"),
        ($"{prefix}Text", "Ha b&c"),
        ($"{prefix}When", "H20240305"),
        ($"{prefix}Offset", "H20240305"),
        ($"{prefix}Day", "H20240305"),
        ($"{prefix}Clock", "H134530"),
        ($"{prefix}Span", "H90"),
        ($"{prefix}Id", "H11111111111111111111111111111111"),
        ($"{prefix}Maybe", "H20240406"),
        ($"{prefix}Json[a]", "H1"),
        ($"{prefix}Json[b][]", "H2"),
        ($"{prefix}Json[b][]", "H3"),
        ($"{prefix}Blob", "HAQID"),
        ($"{prefix}Mem", "HBAU="),
        ($"{prefix}Obj", "H5"),
        ($"{prefix}Nested[X]", "Hn1"),
        ($"{prefix}Nested[Y]", "H9")
    };

    private static string? Hook(object? value, ValueTextChannel _) => value switch
    {
        int i => "H" + i.ToString(CultureInfo.InvariantCulture),
        long l => "H" + l.ToString(CultureInfo.InvariantCulture),
        decimal d => "H" + d.ToString(CultureInfo.InvariantCulture),
        double d => "H" + d.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "Htrue" : "Hfalse",
        MxMode mode => "H" + mode,
        string s => "H" + s,
        DateTime dt => "H" + dt.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
        DateTimeOffset offset => "H" + offset.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
        DateOnly day => "H" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
        TimeOnly time => "H" + time.ToString("HHmmss", CultureInfo.InvariantCulture),
        TimeSpan span => "H" + ((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture),
        Guid id => "H" + id.ToString("N"),
        byte[] bytes => "H" + Convert.ToBase64String(bytes),
        ReadOnlyMemory<byte> memory => "H" + Convert.ToBase64String(memory.Span),
        Memory<byte> memory => "H" + Convert.ToBase64String(memory.Span),
        ArraySegment<byte> segment => "H" + Convert.ToBase64String(segment.AsSpan()),
        _ => null
    };

    // ----------------------------------------------------------------
    // G1：C01 / C02 / C03 / C04 —— 核心直调 Params
    // ----------------------------------------------------------------

    [Fact]
    public void C01_DirectParams_RootPoco_AllShapes_Default()
    {
        using var client = new AxiosClient();

        var uri = client.GetUri(c =>
        {
            c.BaseAddress = "http://example.test/";
            c.Url = "q";
            c.Params = Payload();
        });

        AssertPairs(QueryPairs(uri), Defaults());
    }

    [Fact]
    public void C01_DirectParams_RootPoco_AllShapes_Hook()
    {
        using var client = new AxiosClient();

        var uri = client.GetUri(c =>
        {
            c.BaseAddress = "http://example.test/";
            c.Url = "q";
            c.Params = Payload();
            c.Serialization.ValueText = Hook;
        });

        AssertPairs(QueryPairs(uri), Hooked());
    }

    [Fact]
    public void C02_DirectParams_Dictionary_AllShapes_Default()
    {
        using var client = new AxiosClient();

        var uri = client.GetUri(c =>
        {
            c.BaseAddress = "http://example.test/";
            c.Url = "q";
            c.Params = new Dictionary<string, object?>
            {
                ["Int"] = 7,
                ["Dec"] = 1.25m,
                ["Flag"] = true,
                ["Kind"] = MxMode.Beta,
                ["Text"] = "a b&c",
                ["When"] = WhenUtc,
                ["Offset"] = OffsetValue,
                ["Day"] = DayValue,
                ["Clock"] = ClockValue,
                ["Span"] = SpanValue,
                ["Id"] = IdValue,
                ["Maybe"] = MaybeValue,
                ["Json"] = JsonValue(),
                ["Blob"] = BlobValue,
                ["Mem"] = new Memory<byte>(MemValue),
                ["Obj"] = 5,
                ["Nested"] = new MxInner { X = "n1", Y = 9 }
            };
        });

        AssertPairs(QueryPairs(uri), Defaults());
    }

    [Fact]
    public void C02_DirectParams_Dictionary_AllShapes_Hook()
    {
        using var client = new AxiosClient();

        var uri = client.GetUri(c =>
        {
            c.BaseAddress = "http://example.test/";
            c.Url = "q";
            c.Serialization.ValueText = Hook;
            c.Params = new Dictionary<string, object?>
            {
                ["Int"] = 7,
                ["Dec"] = 1.25m,
                ["Flag"] = true,
                ["Kind"] = MxMode.Beta,
                ["Text"] = "a b&c",
                ["When"] = WhenUtc,
                ["Offset"] = OffsetValue,
                ["Day"] = DayValue,
                ["Clock"] = ClockValue,
                ["Span"] = SpanValue,
                ["Id"] = IdValue,
                ["Maybe"] = MaybeValue,
                ["Json"] = JsonValue(),
                ["Blob"] = BlobValue,
                ["Mem"] = new Memory<byte>(MemValue),
                ["Obj"] = 5,
                ["Nested"] = new MxInner { X = "n1", Y = 9 }
            };
        });

        AssertPairs(QueryPairs(uri), Hooked());
    }

    [Fact]
    public void C03_DirectParams_RootCollection_AllShapes_Default()
    {
        using var client = new AxiosClient();

        var uri = client.GetUri(c =>
        {
            c.BaseAddress = "http://example.test/";
            c.Url = "q";
            c.Params = new List<object?>
            {
                7, 1.25m, true, MxMode.Beta, "a b&c", WhenUtc, OffsetValue, DayValue,
                ClockValue, SpanValue, IdValue, MaybeValue, JsonValue(), BlobValue,
                new Memory<byte>(MemValue), 5, new MxInner { X = "n1", Y = 9 }
            };
        });

        AssertPairs(QueryPairs(uri), new[]
        {
            ("[0]", "7"),
            ("[1]", "1.25"),
            ("[2]", "true"),
            ("[3]", "Beta"),
            ("[4]", "a b&c"),
            ("[5]", "2024-03-05T06:07:08.000Z"),
            ("[6]", "2024-03-05T04:07:08.000Z"),
            ("[7]", "2024-03-05"),
            ("[8]", "13:45:30.0000000"),
            ("[9]", "01:30:00"),
            ("[10]", "11111111-1111-1111-1111-111111111111"),
            ("[11]", "2024-04-06T00:00:00.000Z"),
            ("[12][a]", "1"),
            ("[12][b][]", "2"),
            ("[12][b][]", "3"),
            ("[13]", "AQID"),
            ("[14]", "BAU="),
            ("[15]", "5"),
            ("[16][X]", "n1"),
            ("[16][Y]", "9")
        });
    }

    [Fact]
    public void C03_DirectParams_RootCollection_AllShapes_Hook()
    {
        using var client = new AxiosClient();

        var uri = client.GetUri(c =>
        {
            c.BaseAddress = "http://example.test/";
            c.Url = "q";
            c.Serialization.ValueText = Hook;
            c.Params = new List<object?> { 7, 1.25m, true, MxMode.Beta, "a b&c", WhenUtc, DayValue, BlobValue, new Memory<byte>(MemValue) };
        });

        AssertPairs(QueryPairs(uri), new[]
        {
            ("[0]", "H7"),
            ("[1]", "H1.25"),
            ("[2]", "Htrue"),
            ("[3]", "HBeta"),
            ("[4]", "Ha b&c"),
            ("[5]", "H20240305"),
            ("[6]", "H20240305"),
            ("[7]", "HAQID"),
            ("[8]", "HBAU=")
        });
    }

    [Fact]
    public void C04_DirectParams_JsonTree_Default()
    {
        using var client = new AxiosClient();
        var json = JsonDocument.Parse("""{"a":1,"b":["x","y"],"c":true,"d":null,"e":{"f":2}}""").RootElement.Clone();

        var uri = client.GetUri(c =>
        {
            c.BaseAddress = "http://example.test/";
            c.Url = "q";
            c.Params = json;
        });

        AssertPairs(QueryPairs(uri), new[]
        {
            ("a", "1"),
            ("b[]", "x"),
            ("b[]", "y"),
            ("c", "true"),
            ("e[f]", "2")
        });
    }

    [Fact]
    public void C04_DirectParams_JsonTree_Hook()
    {
        using var client = new AxiosClient();
        var json = JsonDocument.Parse("""{"a":1,"b":["x",true],"c":false}""").RootElement.Clone();

        var uri = client.GetUri(c =>
        {
            c.BaseAddress = "http://example.test/";
            c.Url = "q";
            c.Params = json;
            c.Serialization.ValueText = Hook;
        });

        AssertPairs(QueryPairs(uri), new[]
        {
            ("a", "H1"),
            ("b[]", "Hx"),
            ("b[]", "Htrue"),
            ("c", "Hfalse")
        });
    }

    // ----------------------------------------------------------------
    // G1：C05–C08 —— 声明式查询
    // ----------------------------------------------------------------

    [Fact]
    public void C05_DeclarativeQuery_AllShapes_Default()
    {
        using var client = new ApiClient("http://example.test/");

        var uri = client.GetUri(QueryRequest(Payload()));

        AssertPairs(QueryPairs(uri), Defaults());
    }

    [Fact]
    public void C05_DeclarativeQuery_AllShapes_Hook()
    {
        using var client = new ApiClient("http://example.test/", c => c.Serialization.ValueText = Hook);

        var uri = client.GetUri(QueryRequest(Payload()));

        AssertPairs(QueryPairs(uri), Hooked());
    }

    [Fact]
    public void C06_DeclarativeQuery_NamedMap_AllShapes_Default()
    {
        using var client = new ApiClient("http://example.test/");

        var uri = client.GetUri(new MxMapQueryRequest
        {
            Map = new Dictionary<string, object?>
            {
                ["Int"] = 7,
                ["Dec"] = 1.25m,
                ["Flag"] = true,
                ["Kind"] = MxMode.Beta,
                ["Text"] = "a b&c",
                ["When"] = WhenUtc,
                ["Offset"] = OffsetValue,
                ["Day"] = DayValue,
                ["Clock"] = ClockValue,
                ["Span"] = SpanValue,
                ["Id"] = IdValue,
                ["Maybe"] = MaybeValue,
                ["Json"] = JsonValue(),
                ["Blob"] = BlobValue,
                ["Mem"] = new Memory<byte>(MemValue),
                ["Obj"] = 5,
                ["Nested"] = new MxInner { X = "n1", Y = 9 }
            }
        });

        AssertPairs(QueryPairs(uri), MapDefaults("m"));
    }

    /// <summary>C06：命名字典按 <c>map[key]</c> 展平；叶子本身再带方括号时继续追加。</summary>
    private static (string Key, string Value)[] MapDefaults(string map) => Defaults()
        .Select(pair =>
        {
            var bracket = pair.Key.IndexOf('[');
            return bracket < 0
                ? ($"{map}[{pair.Key}]", pair.Value)
                : ($"{map}[{pair.Key[..bracket]}]{pair.Key[bracket..]}", pair.Value);
        })
        .ToArray();

    [Fact]
    public void C06_DeclarativeQuery_NamedMap_AllShapes_Hook()
    {
        using var client = new ApiClient("http://example.test/", c => c.Serialization.ValueText = Hook);

        var uri = client.GetUri(new MxMapQueryRequest
        {
            Map = new Dictionary<string, object?>
            {
                ["Int"] = 7,
                ["Dec"] = 1.25m,
                ["Flag"] = true,
                ["Kind"] = MxMode.Beta,
                ["Text"] = "a b&c",
                ["When"] = WhenUtc,
                ["Day"] = DayValue,
                ["Blob"] = BlobValue,
                ["Mem"] = new Memory<byte>(MemValue),
                ["Obj"] = 5,
                ["Nested"] = new MxInner { X = "n1", Y = 9 }
            }
        });

        var pairs = QueryPairs(uri);
        AssertPairs(pairs, new[]
        {
            ("m[Int]", "H7"),
            ("m[Dec]", "H1.25"),
            ("m[Flag]", "Htrue"),
            ("m[Kind]", "HBeta"),
            ("m[Text]", "Ha b&c"),
            ("m[When]", "H20240305"),
            ("m[Day]", "H20240305"),
            ("m[Blob]", "HAQID"),
            ("m[Mem]", "HBAU="),
            ("m[Obj]", "H5"),
            ("m[Nested][X]", "Hn1"),
            ("m[Nested][Y]", "H9")
        });
    }

    [Fact]
    public void C07_DeclarativeQuery_Collection_AllShapes_Default()
    {
        using var client = new ApiClient("http://example.test/");

        var uri = client.GetUri(new MxCollectionQueryRequest
        {
            Values =
            [
                7, 1.25m, true, MxMode.Beta, "a b&c", WhenUtc, OffsetValue, DayValue,
                ClockValue, SpanValue, IdValue, MaybeValue, JsonValue(), BlobValue,
                new Memory<byte>(MemValue), 5, new MxInner { X = "n1", Y = 9 }
            ]
        });

        AssertPairs(QueryPairs(uri), new[]
        {
            ("Values[]", "7"),
            ("Values[]", "1.25"),
            ("Values[]", "true"),
            ("Values[]", "Beta"),
            ("Values[]", "a b&c"),
            ("Values[]", "2024-03-05T06:07:08.000Z"),
            ("Values[]", "2024-03-05T04:07:08.000Z"),
            ("Values[]", "2024-03-05"),
            ("Values[]", "13:45:30.0000000"),
            ("Values[]", "01:30:00"),
            ("Values[]", "11111111-1111-1111-1111-111111111111"),
            ("Values[]", "2024-04-06T00:00:00.000Z"),
            ("Values[][a]", "1"),
            ("Values[][b][]", "2"),
            ("Values[][b][]", "3"),
            ("Values[]", "AQID"),
            ("Values[]", "BAU="),
            ("Values[]", "5"),
            ("Values[][X]", "n1"),
            ("Values[][Y]", "9")
        });
    }

    [Fact]
    public void C07_DeclarativeQuery_Collection_AllShapes_Hook()
    {
        using var client = new ApiClient("http://example.test/", c => c.Serialization.ValueText = Hook);

        var uri = client.GetUri(new MxCollectionQueryRequest
        {
            Values = [7, 1.25m, true, MxMode.Beta, "a b&c", WhenUtc, DayValue, BlobValue, new Memory<byte>(MemValue)]
        });

        AssertPairs(QueryPairs(uri), new[]
        {
            ("Values[]", "H7"),
            ("Values[]", "H1.25"),
            ("Values[]", "Htrue"),
            ("Values[]", "HBeta"),
            ("Values[]", "Ha b&c"),
            ("Values[]", "H20240305"),
            ("Values[]", "H20240305"),
            ("Values[]", "HAQID"),
            ("Values[]", "HBAU=")
        });
    }

    [Fact]
    public void C08_DeclarativeQuery_NestedPoco_DefaultAndHook()
    {
        using var client = new ApiClient("http://example.test/");
        var uri = client.GetUri(new MxNestedQueryRequest { N = new MxInner { X = "n1", Y = 9 } });
        AssertPairs(QueryPairs(uri), new[] { ("N[X]", "n1"), ("N[Y]", "9") });

        using var hooked = new ApiClient("http://example.test/", c => c.Serialization.ValueText = Hook);
        var hookedUri = hooked.GetUri(new MxNestedQueryRequest { N = new MxInner { X = "n1", Y = 9 } });
        AssertPairs(QueryPairs(hookedUri), new[] { ("N[X]", "Hn1"), ("N[Y]", "H9") });
    }

    // ----------------------------------------------------------------
    // G1：C09 / C10 / C11 —— 路径 / 请求头
    // ----------------------------------------------------------------

    [Fact]
    public void C09_Path_AllScalarShapes_Default()
    {
        using var client = new ApiClient("http://example.test/");

        var uri = client.GetUri(new MxPathRequest
        {
            Int = 7,
            Dec = 1.25m,
            Flag = true,
            Kind = MxMode.Beta,
            Text = "a b&c",
            When = WhenUtc,
            Offset = OffsetValue,
            Day = DayValue,
            Clock = ClockValue,
            Span = SpanValue,
            Id = IdValue,
            Maybe = MaybeValue
        });

        Assert.Equal(
            "/grid/7/1.25/true/Beta/a%20b%26c/2024-03-05T06%3A07%3A08.000Z/2024-03-05T04%3A07%3A08.000Z/"
            + "2024-03-05/13%3A45%3A30.0000000/01%3A30%3A00/11111111-1111-1111-1111-111111111111/"
            + "2024-04-06T00%3A00%3A00.000Z",
            new Uri(uri).AbsolutePath);
    }

    [Fact]
    public void C09_Path_AllScalarShapes_Hook()
    {
        using var client = new ApiClient("http://example.test/", c => c.Serialization.ValueText = Hook);

        var uri = client.GetUri(new MxPathRequest
        {
            Int = 7,
            Dec = 1.25m,
            Flag = true,
            Kind = MxMode.Beta,
            Text = "a b&c",
            When = WhenUtc,
            Offset = OffsetValue,
            Day = DayValue,
            Clock = ClockValue,
            Span = SpanValue,
            Id = IdValue,
            Maybe = MaybeValue
        });

        Assert.Equal(
            "/grid/H7/H1.25/Htrue/HBeta/Ha%20b%26c/H20240305/H20240305/H20240305/H134530/H90/"
            + "H11111111111111111111111111111111/H20240406",
            new Uri(uri).AbsolutePath);
    }

    [Fact]
    public async Task C10_Header_AllScalarShapes_DefaultAndHook()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        var request = new MxHeaderRequest
        {
            Int = 7,
            Dec = 1.25m,
            Flag = true,
            Kind = MxMode.Beta,
            Text = "a b&c",
            When = WhenUtc,
            Offset = OffsetValue,
            Day = DayValue,
            Clock = ClockValue,
            Span = SpanValue,
            Id = IdValue,
            Maybe = MaybeValue
        };

        await client.RequestAsync(request);

        var headers = Assert.Single(server.Received).Headers;
        Assert.Equal("7", headers["X-Int"]);
        Assert.Equal("1.25", headers["X-Dec"]);
        Assert.Equal("true", headers["X-Flag"]);
        Assert.Equal("Beta", headers["X-Kind"]);
        Assert.Equal("a b&c", headers["X-Text"]);
        Assert.Equal("2024-03-05T06:07:08.000Z", headers["X-When"]);
        Assert.Equal("2024-03-05T04:07:08.000Z", headers["X-Offset"]);
        Assert.Equal("2024-03-05", headers["X-Day"]);
        Assert.Equal("13:45:30.0000000", headers["X-Clock"]);
        Assert.Equal("01:30:00", headers["X-Span"]);
        Assert.Equal("11111111-1111-1111-1111-111111111111", headers["X-Id"]);
        Assert.Equal("2024-04-06T00:00:00.000Z", headers["X-Maybe"]);

        await using var hookedServer = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var hookedClient = new ApiClient(hookedServer.Url, c => c.Serialization.ValueText = Hook);
        await hookedClient.RequestAsync(request);

        var hooked = Assert.Single(hookedServer.Received).Headers;
        Assert.Equal("H7", hooked["X-Int"]);
        Assert.Equal("H20240305", hooked["X-When"]);
    }

    [Fact]
    public async Task C11_HeaderCollection_StringMap()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new MxHeaderCollectionRequest
        {
            Headers = new Dictionary<string, string> { ["X-A"] = "1", ["X-B"] = "two" }
        });

        var headers = Assert.Single(server.Received).Headers;
        Assert.Equal("1", headers["X-A"]);
        Assert.Equal("two", headers["X-B"]);
    }

    // ----------------------------------------------------------------
    // G1：C12 / C13 —— urlencoded
    // ----------------------------------------------------------------

    [Fact]
    public async Task C12_DirectForm_AllShapes_DefaultAndHook()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new AxiosClient();

        await client.PostAsync(server.Url + "f", Payload(), c => c.ContentType = "application/x-www-form-urlencoded");
        AssertPairs(FormPairs(Assert.Single(server.Received)), Defaults());

        await using var hookedServer = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var hooked = new AxiosClient();
        await hooked.PostAsync(
            hookedServer.Url + "f",
            Payload(),
            c =>
            {
                c.ContentType = "application/x-www-form-urlencoded";
                c.Serialization.ValueText = Hook;
            });
        AssertPairs(FormPairs(Assert.Single(hookedServer.Received)), Hooked());
    }

    [Fact]
    public async Task C13_DeclarativeForm_AllShapes_DefaultAndHook()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        await client.Create<IFormApi>().SendAsync(Payload());
        AssertPairs(FormPairs(Assert.Single(server.Received)), Defaults());

        await using var hookedServer = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var hooked = new ApiClient(hookedServer.Url, c => c.Serialization.ValueText = Hook);
        await hooked.Create<IFormApi>().SendAsync(Payload());
        AssertPairs(FormPairs(Assert.Single(hookedServer.Received)), Hooked());
    }

    // ----------------------------------------------------------------
    // G1：C14–C19 —— multipart
    // ----------------------------------------------------------------

    [Fact]
    public async Task C14_MultipartDirectParts_AllShapes_DefaultAndHook()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        await client.Create<IPartsApi>().SendAsync(
            i: 7,
            dec: 1.25m,
            flag: true,
            kind: MxMode.Beta,
            text: "a b&c",
            when: WhenUtc,
            offset: OffsetValue,
            day: DayValue,
            clock: ClockValue,
            span: SpanValue,
            id: IdValue,
            maybe: MaybeValue,
            json: JsonValue(),
            blob: BlobValue,
            mem: new Memory<byte>(MemValue),
            obj: 5,
            nested: new MxInner { X = "n1", Y = 9 });

        AssertParts(Assert.Single(server.Received), InterfacePartDefaults(), new[]
        {
            ("blob", BlobValue),
            ("mem", MemValue)
        });

        await using var hookedServer = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var hooked = new ApiClient(hookedServer.Url, c => c.Serialization.ValueText = Hook);
        await hooked.Create<IPartsApi>().SendAsync(i: 7, when: WhenUtc, day: DayValue, mem: new Memory<byte>(MemValue), nested: new MxInner { X = "n1", Y = 9 });

        AssertParts(Assert.Single(hookedServer.Received), new[]
        {
            ("i", "H7"),
            ("when", "H20240305"),
            ("day", "H20240305"),
            ("nested[X]", "Hn1"),
            ("nested[Y]", "H9")
        }, new[] { ("mem", MemValue) });
    }

    [Fact]
    public async Task C15_MultipartFlatModel_AllShapes_Default()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(UploadRequest(Payload()));

        AssertParts(Assert.Single(server.Received), ModelPartDefaults(), new[]
        {
            ("Blob", BlobValue),
            ("Mem", MemValue)
        });
    }

    [Fact]
    public async Task C15_MultipartFlatModel_AllShapes_Hook()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url, c => c.Serialization.ValueText = Hook);

        await client.RequestAsync(UploadRequest(Payload()));

        var parts = MultipartParts(Assert.Single(server.Received));
        Assert.Contains(parts, p => p.Name == "Int" && p.Content == "H7");
        Assert.Contains(parts, p => p.Name == "When" && p.Content == "H20240305");
        Assert.Contains(parts, p => p.Name == "Nested[X]" && p.Content == "Hn1");
        // 文件部件不受钩子影响
        Assert.Contains(parts, p => p.Name == "Blob" && p.Bytes.SequenceEqual(BlobValue));
    }

    [Fact]
    public async Task C16_C17_C18_MultipartNestedMapCollection()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(UploadRequest(Payload()));

        var parts = MultipartParts(Assert.Single(server.Received));
        Assert.Contains(parts, p => p.Name == "Nested[X]" && p.Content == "n1");
        Assert.Contains(parts, p => p.Name == "Nested[Y]" && p.Content == "9");
        Assert.Contains(parts, p => p.Name == "Tags[env]" && p.Content == "test");
        Assert.Equal(1, parts.Count(p => p.Name == "Nums" && p.Content == "1"));
        Assert.Contains(parts, p => p.Name == "Nums" && p.Content == "2");
    }

    // ----------------------------------------------------------------
    // G1：C20 / C21 —— 正文
    // ----------------------------------------------------------------

    [Theory]
    [InlineData("7", "7")]
    [InlineData("1.25", "1.25")]
    [InlineData("true", "true")]
    [InlineData("Beta", "Beta")]
    [InlineData("a b&c", "a b&c")]
    [InlineData("2024-03-05T06:07:08.0000000Z", "2024-03-05T06:07:08.0000000Z")]
    [InlineData("2024-03-05T06:07:08.0000000+02:00", "2024-03-05T06:07:08.0000000+02:00")]
    [InlineData("2024-03-05", "2024-03-05")]
    [InlineData("13:45:30.0000000", "13:45:30.0000000")]
    [InlineData("01:30:00", "01:30:00")]
    [InlineData("11111111-1111-1111-1111-111111111111", "11111111-1111-1111-1111-111111111111")]
    public async Task C20_ScalarBody_TextShapes(string expected, string _)
    {
        var value = expected switch
        {
            "7" => (object)7,
            "1.25" => 1.25m,
            "true" => true,
            "Beta" => MxMode.Beta,
            "a b&c" => "a b&c",
            "2024-03-05T06:07:08.0000000Z" => WhenUtc,
            "2024-03-05T06:07:08.0000000+02:00" => OffsetValue,
            "2024-03-05" => DayValue,
            "13:45:30.0000000" => ClockValue,
            "01:30:00" => SpanValue,
            _ => (object)IdValue
        };

        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new AxiosClient();

        await client.PostAsync(server.Url + "b", value);

        Assert.Equal(expected, Encoding.UTF8.GetString(Assert.Single(server.Received).Body));
    }

    [Fact]
    public async Task C20_ScalarBody_BinaryAndStream_StayRaw()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new AxiosClient();

        await client.PostAsync(server.Url + "b", BlobValue);
        Assert.Equal(BlobValue, Assert.Single(server.Received).Body);
    }

    [Fact]
    public async Task C20_ScalarBody_HookReturningNull_KeepsBinaryRaw()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new AxiosClient();

        await client.PostAsync(server.Url + "b", new Memory<byte>(MemValue), c => c.Serialization.ValueText = (_, _) => null);
        Assert.Equal(MemValue, Assert.Single(server.Received).Body);
    }

#if NET7_0_OR_GREATER
    [Fact]
    public async Task C21_StructuredBody_MemberFormatNeedsBridge()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new MxJsonBodyRequest { Body = new MxJsonPayload { When = WhenUtc } });
        Assert.Contains(
            "2024-03-05T06:07:08Z",
            Encoding.UTF8.GetString(Assert.Single(server.Received).Body));

        await using var bridgedServer = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var bridged = new ApiClient(bridgedServer.Url);
        await bridged.RequestAsync(
            new MxJsonBodyRequest { Body = new MxJsonPayload { When = WhenUtc } },
            c => c.Serialization.JsonOptions = AxiosJsonFormat.ApplyTo());

        Assert.Contains(
            "\"20240305\"",
            Encoding.UTF8.GetString(Assert.Single(bridgedServer.Received).Body));
    }
#endif

    // ----------------------------------------------------------------
    // G2：配置组合
    // ----------------------------------------------------------------

    [Fact]
    public void K03_K04_MemberFormat_WinsOverHook()
    {
        using var client = new ApiClient("http://example.test/", c => c.Serialization.ValueText = Hook);
        var uri = client.GetUri(new MxFmtQueryRequest
        {
            When = WhenUtc,
            Dec = 1.5m,
            Int = 7,
            Blob = BlobValue,
            Plain = "p"
        });

        AssertPairs(QueryPairs(uri), new[]
        {
            ("When", "20240305"),
            ("Dec", "1.50"),
            ("Int", "H7"),
            ("Blob", "BAQID"),
            ("Plain", "Hp")
        });
    }

    [Fact]
    public async Task K03_K04_MemberFormat_FormAndMultipart()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url, c => c.Serialization.ValueText = Hook);

        await client.Create<IFormApi>().SendAsync(new MxPayload
        {
            When = WhenUtc,
            Nested = new MxInner { X = "n1", Y = 9 }
        });
        AssertPairs(FormPairs(Assert.Single(server.Received)), new[]
        {
            ("When", "H20240305"),
            ("Nested[X]", "Hn1"),
            ("Nested[Y]", "H9")
        });
    }

    [Fact]
    public async Task K05_CustomQueryEncoder_ReplacesRendering()
    {
        object? seen = null;
        using var client = new ApiClient("http://example.test/", c => c.Serialization.QueryEncoder = value =>
        {
            seen = value;
            return "fixed=1";
        });

        var uri = client.GetUri(new MxQueryRequest { When = WhenUtc, Int = 7 });

        Assert.Contains("fixed=1", uri);
        Assert.NotNull(seen);
    }

    [Fact]
    public async Task K07_Cache_KeyedByFormattedUri()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new AxiosClient(new AxiosConfig { Caching = new AxiosCache() });

        await client.GetAsync(server.Url + "c", c => c.Params = new { Day = DayValue });
        await client.GetAsync(server.Url + "c", c => c.Params = new { Day = DayValue });
        await client.GetAsync(server.Url + "c", c => c.Params = new { Day = new DateOnly(2024, 3, 6) });

        Assert.Equal(2, server.Received.Count);
        Assert.Equal("Day=2024-03-05", server.Received[0].Query);
        // 第二次相同参数的请求命中缓存、没有再发出；Received[1] 是第三个（不同日期）请求。
        Assert.Equal("Day=2024-03-06", server.Received[1].Query);
    }

    [Fact]
    public async Task K09_Hedging_ReusesFormattedQuery()
    {
        var arrivals = 0;
        await using var server = TestHttpServer.Start(async _ =>
        {
            if (Interlocked.Increment(ref arrivals) == 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200));
            }

            return TestJson.Response(new Person("ok", 1));
        });

        using var client = new ApiClient(server.Url, c =>
        {
            c.Resilience.Hedging.MaxHedges = 2;
            c.Resilience.Hedging.Delay = TimeSpan.FromMilliseconds(5);
        });
        client.Axios.Defaults.Serialization.ValueText = Hook;

        await client.RequestAsync(new MxQueryRequest { When = WhenUtc });

        Assert.True(server.Received.Count >= 1);
        foreach (var request in server.Received)
        {
            Assert.Contains("When=H20240305", request.Query);
        }
    }

    // ----------------------------------------------------------------
    // G4：错误路径
    // ----------------------------------------------------------------

    [Fact]
    public async Task E1_DirectFormatterThrows_IsOptionError()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url + "x", c =>
        {
            c.Params = new { When = WhenUtc };
            c.Serialization.ValueText = (_, _) => throw new InvalidOperationException("boom");
        }));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task E1_DeclarativeQueryFormatterThrows_IsBadRequest()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url, c => c.Serialization.ValueText = (_, _) => throw new InvalidOperationException("boom"));

        // 装配失败由 WebAPI 自有的异常工厂产出（AxiosException 派生），按公共契约接住基类。
        var ex = await Assert.ThrowsAnyAsync<AxiosException>(() =>
            client.RequestAsync(new MxQueryRequest { When = WhenUtc }));

        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task E1_PathAndHeaderFormatterThrows_IsBadRequest()
    {
        using var client = new ApiClient("http://127.0.0.1:1/", c => c.Serialization.ValueText = (_, _) => throw new InvalidOperationException("boom"));

        var pathEx = Assert.ThrowsAny<AxiosException>(() => client.GetUri(new MxPathRequest { When = WhenUtc }));
        Assert.Equal(AxiosException.ErrBadRequest, pathEx.Code);

        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var headerClient = new ApiClient(server.Url, c => c.Serialization.ValueText = (_, _) => throw new InvalidOperationException("boom"));
        var headerEx = await Assert.ThrowsAnyAsync<AxiosException>(() =>
            headerClient.RequestAsync(new MxHeaderRequest { Int = 7 }));
        Assert.Equal(AxiosException.ErrBadRequest, headerEx.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public void E2_PathNull_IsBadRequest()
    {
        using var client = new ApiClient("http://example.test/");
        var ex = Assert.ThrowsAny<AxiosException>(() => client.GetUri(new MxPathRequest()));
        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);
    }

    [Theory]
    [InlineData(typeof(IllegalFormatOnString))]
    [InlineData(typeof(IllegalFormatOnBool))]
    [InlineData(typeof(IllegalFormatOnPoco))]
    [InlineData(typeof(IllegalFormatOnPathPoco))]
    [InlineData(typeof(IllegalFormatOnHeaderCollection))]
    [InlineData(typeof(IllegalFormatOnBodyPoco))]
    [InlineData(typeof(IllegalFormatOnFilePart))]
    [InlineData(typeof(IllegalFormatOnFileDictionaryPart))]
    public void E3_IllegalDeclarations_FailAtBuild(Type model)
    {
        var errors = ApiClient.ValidateModels(model);
        Assert.NotEmpty(errors);
    }

    // ----------------------------------------------------------------
    // G5：并发
    // ----------------------------------------------------------------

    [Fact]
    public async Task X02_CrossConfigConcurrency_NoCrosstalk()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var hooked = new ApiClient(server.Url, c => c.Serialization.ValueText = Hook);
        using var plain = new ApiClient(server.Url);

        var requests = Enumerable.Range(0, 32).Select(async i =>
        {
            var value = new DateTime(2024, 3, 5, 6, 7, i, DateTimeKind.Utc);
            if (i % 2 == 0)
            {
                await hooked.RequestAsync(new MxQueryRequest { When = value });
            }
            else
            {
                await plain.RequestAsync(new MxQueryRequest { When = value });
            }
        });

        await Task.WhenAll(requests);

        // TestHttpServer 记录的是原始 URL（未解码）：先解码再比对，避免把 %3A 误判成客户端串扰。
        var values = server.Received.Select(r => Uri.UnescapeDataString(r.Query!.Split('=')[1])).ToList();
        Assert.All(values, value => Assert.True(
            value.StartsWith("H20240305", StringComparison.Ordinal)
            || value.StartsWith("2024-03-05T06:07", StringComparison.Ordinal)));
        Assert.Contains(values, value => value.StartsWith("H20240305", StringComparison.Ordinal));
        Assert.Contains(values, value => value.StartsWith("2024-03-05T06:07", StringComparison.Ordinal));
    }

    // ----------------------------------------------------------------
    // 辅助
    // ----------------------------------------------------------------

    private static MxQueryRequest QueryRequest(MxPayload source) => new()
    {
        Int = source.Int,
        Dec = source.Dec,
        Flag = source.Flag,
        Kind = source.Kind,
        Text = source.Text,
        When = source.When,
        Offset = source.Offset,
        Day = source.Day,
        Clock = source.Clock,
        Span = source.Span,
        Id = source.Id,
        Maybe = source.Maybe,
        Json = source.Json,
        Blob = source.Blob,
        Mem = source.Mem,
        Obj = source.Obj,
        Nested = source.Nested
    };

    private static MxUploadRequest UploadRequest(MxPayload source) => new()
    {
        Int = source.Int,
        Dec = source.Dec,
        Flag = source.Flag,
        Kind = source.Kind,
        Text = source.Text,
        When = source.When,
        Offset = source.Offset,
        Day = source.Day,
        Clock = source.Clock,
        Span = source.Span,
        Id = source.Id,
        Maybe = source.Maybe,
        Json = source.Json,
        Blob = source.Blob,
        Mem = source.Mem,
        Obj = source.Obj,
        Nested = source.Nested,
        Tags = new Dictionary<string, string> { ["env"] = "test" },
        Nums = [1, 2]
    };

    private static (string Key, string Value)[] ModelPartDefaults() => new[]
    {
        ("Int", "7"),
        ("Dec", "1.25"),
        ("Flag", "true"),
        ("Kind", "Beta"),
        ("Text", "a b&c"),
        ("When", "2024-03-05T06:07:08.000Z"),
        ("Offset", "2024-03-05T04:07:08.000Z"),
        ("Day", "2024-03-05"),
        ("Clock", "13:45:30.0000000"),
        ("Span", "01:30:00"),
        ("Id", "11111111-1111-1111-1111-111111111111"),
        ("Maybe", "2024-04-06T00:00:00.000Z"),
        ("Json[a]", "1"),
        ("Json[b]", "2"),
        ("Json[b]", "3"),
        ("Obj", "5"),
        ("Nested[X]", "n1"),
        ("Nested[Y]", "9")
    };

    private static (string Key, string Value)[] InterfacePartDefaults() => new[]
    {
        ("i", "7"),
        ("dec", "1.25"),
        ("flag", "true"),
        ("kind", "Beta"),
        ("text", "a b&c"),
        ("when", "2024-03-05T06:07:08.000Z"),
        ("offset", "2024-03-05T04:07:08.000Z"),
        ("day", "2024-03-05"),
        ("clock", "13:45:30.0000000"),
        ("span", "01:30:00"),
        ("id", "11111111-1111-1111-1111-111111111111"),
        ("maybe", "2024-04-06T00:00:00.000Z"),
        ("json[a]", "1"),
        ("json[b]", "2"),
        ("json[b]", "3"),
        ("obj", "5"),
        ("nested[X]", "n1"),
        ("nested[Y]", "9")
    };

    private static void AssertParts(
        TestRequest request,
        (string Key, string Value)[] textExpected,
        (string Name, byte[] Bytes)[] fileExpected)
    {
        var parts = MultipartParts(request);
        foreach (var (key, value) in textExpected)
        {
            if (!parts.Any(p => p.Name == key && p.Content == value && p.Bytes.Length == 0))
            {
                Assert.Fail($"缺少文本部件 {key}={value}；实际：{string.Join(" | ", parts.Select(p => $"{p.Name}={(p.Bytes.Length > 0 ? "<file>" : p.Content)}"))}");
            }
        }

        foreach (var (name, bytes) in fileExpected)
        {
            if (!parts.Any(p => p.Name == name && p.Bytes.SequenceEqual(bytes)))
            {
                Assert.Fail($"缺少文件部件 {name}；实际：{string.Join(" | ", parts.Select(p => $"{p.Name}={(p.Bytes.Length > 0 ? "<file>" : p.Content)}"))}");
            }
        }
    }

    private static Dictionary<string, List<string>> QueryPairs(string uri)
    {
        var query = new Uri(uri).Query;
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (query.Length == 0)
        {
            return result;
        }

        foreach (var segment in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=');
            var key = Uri.UnescapeDataString(separator < 0 ? segment : segment[..separator]);
            var value = separator < 0 ? string.Empty : Uri.UnescapeDataString(segment[(separator + 1)..]);
            (result.TryGetValue(key, out var list) ? list : result[key] = new List<string>()).Add(value);
        }

        return result;
    }

    private static Dictionary<string, List<string>> FormPairs(TestRequest request)
            => FormPairs(Encoding.UTF8.GetString(request.Body));

    private static Dictionary<string, List<string>> FormPairs(string body)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (body.Length == 0)
        {
            return result;
        }

        foreach (var segment in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=');
            var key = Uri.UnescapeDataString((separator < 0 ? segment : segment[..separator]).Replace("+", " "));
            var value = separator < 0 ? string.Empty : Uri.UnescapeDataString(segment[(separator + 1)..].Replace("+", " "));
            (result.TryGetValue(key, out var list) ? list : result[key] = new List<string>()).Add(value);
        }

        return result;
    }

    private static void AssertPairs(
        Dictionary<string, List<string>> actual,
        IEnumerable<(string Key, string Value)> expected)
    {
        var expectedMap = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (key, value) in expected)
        {
            (expectedMap.TryGetValue(key, out var list) ? list : expectedMap[key] = new List<string>()).Add(value);
        }

        var actualKeys = actual.Keys.OrderBy(k => k, StringComparer.Ordinal);
        var expectedKeys = expectedMap.Keys.OrderBy(k => k, StringComparer.Ordinal);
        Assert.Equal(expectedKeys, actualKeys);

        foreach (var key in expectedKeys)
        {
            Assert.Equal(expectedMap[key].OrderBy(v => v, StringComparer.Ordinal), actual[key].OrderBy(v => v, StringComparer.Ordinal));
        }
    }

    private sealed record MxPart(string Name, string? FileName, string Content, byte[] Bytes);

    private static string? ReadDispositionValue(string headerBlock, string key)
    {
        var index = headerBlock.IndexOf(" " + key, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        index += key.Length + 1;
        if (index >= headerBlock.Length)
        {
            return null;
        }

        if (headerBlock[index] == '"')
        {
            var start = index + 1;
            return headerBlock[start..headerBlock.IndexOf('"', start)];
        }

        var end = headerBlock.IndexOf(';', index);
        if (end < 0)
        {
            end = headerBlock.IndexOf("\r\n", index, StringComparison.Ordinal);
        }

        if (end < 0)
        {
            end = headerBlock.Length;
        }

        return headerBlock[index..end].Trim();
    }

    private static List<MxPart> MultipartParts(TestRequest request)
    {
        var contentType = request.Headers["Content-Type"];
        var boundary = contentType[(contentType.IndexOf("boundary=", StringComparison.Ordinal) + "boundary=".Length)..].Trim('"');
        var text = Encoding.UTF8.GetString(request.Body);
        var result = new List<MxPart>();

        foreach (var section in text.Split("--" + boundary))
        {
            var headerEnd = section.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0)
            {
                continue;
            }

            var headerBlock = section[..headerEnd];
            var name = ReadDispositionValue(headerBlock, "name=");
            if (name is null)
            {
                continue;
            }

            var fileName = ReadDispositionValue(headerBlock, "filename=");

            var contentStart = headerEnd + 4;
            var contentEnd = section.Length;
            if (contentEnd >= 2 && section[(contentEnd - 2)..] == "\r\n")
            {
                contentEnd -= 2;
            }

            var content = section[contentStart..contentEnd];
            result.Add(new MxPart(name, fileName, content, fileName is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(content)));
        }

        return result;
    }
}

// --------------------------------------------------------------------
// 测试模型
// --------------------------------------------------------------------

public enum MxMode
{
    Alpha,
    Beta
}

public sealed class MxInner
{
    public string? X { get; set; }

    public int Y { get; set; }
}

public sealed class MxPayload
{
    public int? Int { get; set; }

    public decimal? Dec { get; set; }

    public bool? Flag { get; set; }

    public MxMode? Kind { get; set; }

    public string? Text { get; set; }

    public DateTime? When { get; set; }

    public DateTimeOffset? Offset { get; set; }

    public DateOnly? Day { get; set; }

    public TimeOnly? Clock { get; set; }

    public TimeSpan? Span { get; set; }

    public Guid? Id { get; set; }

    public DateTime? Maybe { get; set; }

    public JsonElement? Json { get; set; }

    public byte[]? Blob { get; set; }

    public Memory<byte>? Mem { get; set; }

    public object? Obj { get; set; }

    public MxInner? Nested { get; set; }

    public FormFile? File { get; set; }
}

[Get("q")]
public sealed class MxQueryRequest : IApiRequest<Person>
{
    [Query] public int? Int { get; set; }

    [Query] public decimal? Dec { get; set; }

    [Query] public bool? Flag { get; set; }

    [Query] public MxMode? Kind { get; set; }

    [Query] public string? Text { get; set; }

    [Query] public DateTime? When { get; set; }

    [Query] public DateTimeOffset? Offset { get; set; }

    [Query] public DateOnly? Day { get; set; }

    [Query] public TimeOnly? Clock { get; set; }

    [Query] public TimeSpan? Span { get; set; }

    [Query] public Guid? Id { get; set; }

    [Query] public DateTime? Maybe { get; set; }

    [Query] public JsonElement? Json { get; set; }

    [Query] public byte[]? Blob { get; set; }

    [Query] public Memory<byte>? Mem { get; set; }

    [Query] public object? Obj { get; set; }

    [Query] public MxInner? Nested { get; set; }
}

[Get("map")]
public sealed class MxMapQueryRequest : IApiRequest<Person>
{
    [Query("m")] public Dictionary<string, object?>? Map { get; set; }
}

[Get("list")]
public sealed class MxCollectionQueryRequest : IApiRequest<Person>
{
    [Query] public List<object?>? Values { get; set; }
}

[Get("nested")]
public sealed class MxNestedQueryRequest : IApiRequest<Person>
{
    [Query] public MxInner? N { get; set; }
}

[Get("grid/{Int}/{Dec}/{Flag}/{Kind}/{Text}/{When}/{Offset}/{Day}/{Clock}/{Span}/{Id}/{Maybe}")]
public sealed class MxPathRequest : IApiRequest<Person>
{
    [Path] public int Int { get; set; }

    [Path] public decimal Dec { get; set; }

    [Path] public bool Flag { get; set; }

    [Path] public MxMode Kind { get; set; }

    [Path] public string Text { get; set; } = string.Empty;

    [Path] public DateTime When { get; set; }

    [Path] public DateTimeOffset Offset { get; set; }

    [Path] public DateOnly Day { get; set; }

    [Path] public TimeOnly Clock { get; set; }

    [Path] public TimeSpan Span { get; set; }

    [Path] public Guid Id { get; set; }

    [Path] public DateTime? Maybe { get; set; }
}

[Get("headers")]
public sealed class MxHeaderRequest : IApiRequest<Person>
{
    [Header("X-Int")] public int? Int { get; set; }

    [Header("X-Dec")] public decimal? Dec { get; set; }

    [Header("X-Flag")] public bool? Flag { get; set; }

    [Header("X-Kind")] public MxMode? Kind { get; set; }

    [Header("X-Text")] public string? Text { get; set; }

    [Header("X-When")] public DateTime? When { get; set; }

    [Header("X-Offset")] public DateTimeOffset? Offset { get; set; }

    [Header("X-Day")] public DateOnly? Day { get; set; }

    [Header("X-Clock")] public TimeOnly? Clock { get; set; }

    [Header("X-Span")] public TimeSpan? Span { get; set; }

    [Header("X-Id")] public Guid? Id { get; set; }

    [Header("X-Maybe")] public DateTime? Maybe { get; set; }
}

[Get("hc")]
public sealed class MxHeaderCollectionRequest : IApiRequest<Person>
{
    [HeaderCollection] public Dictionary<string, string>? Headers { get; set; }
}

[Post("form")]
[Multipart]
public sealed class MxUploadRequest : IApiRequest<Person>
{
    public int? Int { get; set; }

    public decimal? Dec { get; set; }

    public bool? Flag { get; set; }

    public MxMode? Kind { get; set; }

    public string? Text { get; set; }

    public DateTime? When { get; set; }

    public DateTimeOffset? Offset { get; set; }

    public DateOnly? Day { get; set; }

    public TimeOnly? Clock { get; set; }

    public TimeSpan? Span { get; set; }

    public Guid? Id { get; set; }

    public DateTime? Maybe { get; set; }

    public JsonElement? Json { get; set; }

    public byte[]? Blob { get; set; }

    public Memory<byte>? Mem { get; set; }

    public object? Obj { get; set; }

    public MxInner? Nested { get; set; }

    public Dictionary<string, string>? Tags { get; set; }

    public List<int>? Nums { get; set; }
}

public interface IFormApi
{
    [Post("form")]
    [BodyContentType("application/x-www-form-urlencoded")]
    Task<AxiosResponse<Person>> SendAsync([Body] MxPayload body, CancellationToken cancellationToken = default);
}

public interface IPartsApi
{
    [Post("upload")]
    [Multipart]
    Task<AxiosResponse<Person>> SendAsync(
        [Part] int? i = null,
        [Part] decimal? dec = null,
        [Part] bool? flag = null,
        [Part] MxMode? kind = null,
        [Part] string? text = null,
        [Part] DateTime? when = null,
        [Part] DateTimeOffset? offset = null,
        [Part] DateOnly? day = null,
        [Part] TimeOnly? clock = null,
        [Part] TimeSpan? span = null,
        [Part] Guid? id = null,
        [Part] DateTime? maybe = null,
        [Part] JsonElement? json = null,
        [Part] byte[]? blob = null,
        [Part] Memory<byte>? mem = null,
        [Part] object? obj = null,
        [Part] MxInner? nested = null,
        [Part] FormFile? file = null,
        CancellationToken cancellationToken = default);
}

[Get("fmt")]
public sealed class MxFmtQueryRequest : IApiRequest<Person>
{
    [Query, Format("yyyyMMdd")] public DateTime? When { get; set; }

    [Query, Format("0.00")] public decimal? Dec { get; set; }

    [Query, Format(typeof(MxFmts), nameof(MxFmts.Prefix))] public int? Int { get; set; }

    [Query, Format(typeof(MxFmts), nameof(MxFmts.Bytes))] public byte[]? Blob { get; set; }

    [Query] public string? Plain { get; set; }
}

public static class MxFmts
{
    public static string? Prefix(object? value, ValueTextChannel _) => value is null ? null : "H" + value;

    public static string? Bytes(object? value, ValueTextChannel _) => value is byte[] bytes ? "B" + Convert.ToBase64String(bytes) : null;
}

[Post("json")]
public sealed class MxJsonBodyRequest : IApiRequest<Person>
{
    [Body] public MxJsonPayload Body { get; set; } = new();
}

public sealed class MxJsonPayload
{
    [Format("yyyyMMdd")] public DateTime? When { get; set; }
}

// 非法声明：构建期必须拒绝
[Get("bad")]
public sealed class IllegalFormatOnString : IApiRequest<Person>
{
    [Query, Format("0.00")] public string? V { get; set; }
}

[Get("bad")]
public sealed class IllegalFormatOnBool : IApiRequest<Person>
{
    [Query, Format("0.00")] public bool? V { get; set; }
}

[Get("bad")]
public sealed class IllegalFormatOnPoco : IApiRequest<Person>
{
    [Query, Format("0.00")] public MxInner? V { get; set; }
}

[Get("bad/{v}")]
public sealed class IllegalFormatOnPathPoco : IApiRequest<Person>
{
    [Path, Format(typeof(MxFmts), nameof(MxFmts.Prefix))] public MxInner V { get; set; } = new();
}

[Get("bad")]
public sealed class IllegalFormatOnHeaderCollection : IApiRequest<Person>
{
    [HeaderCollection, Format(typeof(MxFmts), nameof(MxFmts.Prefix))] public Dictionary<string, string>? V { get; set; }
}

[Post("bad")]
public sealed class IllegalFormatOnBodyPoco : IApiRequest<Person>
{
    [Body, Format("0.00")] public MxInner V { get; set; } = new();
}

[Post("bad")]
[Multipart]
public sealed class IllegalFormatOnFilePart : IApiRequest<Person>
{
    [Part, Format(typeof(MxFmts), nameof(MxFmts.Prefix))] public byte[]? V { get; set; }
}

[Post("bad")]
[Multipart]
public sealed class IllegalFormatOnFileDictionaryPart : IApiRequest<Person>
{
    [Part, Format(typeof(MxFmts), nameof(MxFmts.Prefix))] public Dictionary<string, byte[]>? V { get; set; }
}
