using System.Globalization;
using System.Text;
using System.Text.Json;
using AxiosNet.Metadata;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Attributes;
using AxiosNet.WebAPI.Reflection;
#if NET8_0_OR_GREATER
using AxiosNet.WebAPI.Serialization;
#endif

namespace AxiosNet.Tests.WebAPI;

/// <summary>
/// 成员级 <c>[Format]</c> 与实例级 <c>ValueFormatter</c>：值转线上文本只有一条路径，
/// 优先级是「成员声明 &gt; 实例钩子 &gt; 通道默认」，不适用的组合在构建期报错而不是静默回落。
/// </summary>
public sealed class FormatAttributeTests
{
    // ------------------------------------------------------------------
    // 值文本：格式串 / 格式化函数 / 优先级
    // ------------------------------------------------------------------

    [Fact]
    public async Task FormatString_AppliesToPathQueryAndHeader()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new FormattedScalarRequest
        {
            Id = 7,
            When = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Trace = 42
        });

        var received = Assert.Single(server.Received);
        Assert.Equal("/fmt/007", received.Path);
        Assert.Equal("When=2024-01-02", Uri.UnescapeDataString(received.Query ?? string.Empty));
        Assert.Equal("T42", received.Headers["X-Trace"]);
    }

    /// <summary>
    /// 实例钩子收到的通道必须与实际位置一致：同一个值换个通道就要能给出不同文本。
    /// 覆盖路径段 / 查询串 / 请求头 / 表单字段 / 裸标量正文五个入口。
    /// </summary>
    [Fact]
    public async Task ValueFormatter_ReceivesChannelPerLocation()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));

        var seen = new List<ValueTextChannel>();
        Func<object?, ValueTextChannel, string?> hook = (_, channel) =>
        {
            seen.Add(channel);
            return "ch" + ((int)channel).ToString(CultureInfo.InvariantCulture);
        };

        using var client = new ApiClient(server.Url, c => c.Serialization.ValueText = hook);

        // 声明式四通道：路径段 / 查询串 / 裸标量正文 / 请求头。
        await client.RequestAsync(new ChannelProbeRequest
        {
            Id = 7,
            When = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Count = 42,
            Trace = 7
        });

        // 表单字段：正文在管线内组装，直接调用要在按请求回调里给钩子。
        await client.Axios.PostAsync(
            server.Url + "probe-form",
            new Dictionary<string, object?> { ["amount"] = 1.5m },
            c =>
            {
                c.ContentType = "application/x-www-form-urlencoded";
                c.Serialization.ValueText = hook;
            });

        Assert.True(seen.Count == 5, "实际通道：" + string.Join(",", seen));
        Assert.Equal(
            new[]
            {
                ValueTextChannel.Path,
                ValueTextChannel.Query,
                ValueTextChannel.Header,
                ValueTextChannel.Form,
                ValueTextChannel.ScalarBody
            },
            seen.OrderBy(channel => channel).ToArray());

        var declarative = server.Received[0];
        Assert.Equal("/probe/ch0", declarative.Path);
        Assert.Equal("When=ch1", Uri.UnescapeDataString(declarative.Query ?? string.Empty));
        Assert.Equal("ch2", declarative.Headers["X-Probe"]);
        Assert.Equal("ch5", Encoding.UTF8.GetString(declarative.Body));

        Assert.Equal("amount=ch3", Encoding.UTF8.GetString(server.Received[1].Body));
    }

    /// <summary>
    /// 反射解析路径只认二参数签名（AOT 下由源生成器烘焙，这里是 JIT 反射那条）。
    /// 只写一参数的旧形态必须显式报错——否则会出现「生成器拦下、反射放行」两条路径口径不一致。
    /// </summary>
    [Fact]
    public void FormatSpecReader_RequiresChannelSignature()
    {
        var withChannel = FormatSpecReader.Read(
            new FormatAttribute(typeof(FormatHelpers), nameof(FormatHelpers.PerChannel)),
            "probe",
            "Page");

        Assert.Equal("q3", withChannel.TryFormat(3, ValueTextChannel.Query));
        Assert.Equal("h3", withChannel.TryFormat(3, ValueTextChannel.Header));

        var legacy = Assert.Throws<InvalidOperationException>(() => FormatSpecReader.Read(
            new FormatAttribute(typeof(LegacyFormats), nameof(LegacyFormats.Plain)),
            "probe",
            "Id"));

        Assert.Contains("ValueTextChannel", legacy.Message);
    }

    /// <summary>
    /// 成员级格式化函数也能声明第二个通道参数：同一个函数挂在路径 / 查询 / 请求头上时，
    /// 收到的通道必须与实际位置一致。
    /// </summary>
    [Fact]
    public async Task MemberFormatter_ReceivesChannelPerLocation()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new MemberChannelRequest { Id = 7, Page = 3, Trace = 5 });

        var received = Assert.Single(server.Received);
        Assert.Equal("/ch/p7", received.Path);
        Assert.Equal("Page=q3", Uri.UnescapeDataString(received.Query ?? string.Empty));
        Assert.Equal("h5", received.Headers["X-Page"]);
    }

    [Fact]
    public async Task MemberFormat_WinsOverValueFormatter()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url);
        client.Axios.Defaults.Serialization.ValueText = (value, _) => value switch
        {
            int id => "hook" + id.ToString(CultureInfo.InvariantCulture),
            DateTime date => "hook" + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            _ => null
        };

        await client.RequestAsync(new MixedFormatRequest
        {
            Id = 7,
            When = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc)
        });

        // Id 没有成员声明 → 实例钩子生效；When 有成员声明 → 成员声明压过钩子。
        Assert.Equal("Id=hook7&When=2024-01-02", Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty));
    }

    [Fact]
    public async Task FormatterReturningNull_FallsBackToDefaultRule()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new NullableFormatRequest { Value = 5 });
        await client.RequestAsync(new NullableFormatRequest { Value = 0 });

        Assert.Equal("Value=v5", Uri.UnescapeDataString(server.Received[0].Query ?? string.Empty));
        Assert.Equal("Value=0", Uri.UnescapeDataString(server.Received[1].Query ?? string.Empty));
    }

    [Fact]
    public async Task Format_OnCollection_AppliesPerElement()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new FormattedCollectionRequest { Codes = [11, 255] });

        Assert.Equal("Codes[]=0B&Codes[]=FF", Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty));
    }

    [Fact]
    public async Task Format_OnDictionary_AppliesPerValue()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new FormattedMapRequest
        {
            Filters = new Dictionary<string, DateTime>
            {
                ["from"] = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc)
            }
        });

        Assert.Equal("filters[from]=2024-01", Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty));
    }

    [Fact]
    public async Task Format_OnScalarBody_KeepsScalarBodyShape()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new FormattedScalarBodyRequest
        {
            At = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc)
        });

        var received = Assert.Single(server.Received);
        Assert.Equal("2024-01-02 03:04", Encoding.UTF8.GetString(received.Body));
        Assert.Contains("application/json", received.Headers["Content-Type"]);
    }

    [Fact]
    public async Task Format_OnScalarByteArrayBody_UsesFormatter()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new FormattedBytesBodyRequest { Data = [0x0A, 0xFF] });

        // byte[] 是合法的裸标量正文：格式化函数可以换成 base64 以外的编码（这里是 hex）。
        Assert.Equal("0AFF", Encoding.UTF8.GetString(Assert.Single(server.Received).Body));
    }

    [Fact]
    public async Task MemberFormat_AppliesToRootParamsObject()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url);

        // 普通调用把 POCO 当 params：根对象的成员声明与嵌套对象同一条规则，不能被静默忽略。
        await client.Axios.GetAsync(
            "root",
            configure: config => config.Params = new RootParams { Since = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc) });

        Assert.Equal("Since=2024-01-02", Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty));
    }

    [Fact]
    public async Task ValueFormatter_AppliesInsideNestedQueryObject()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url);
        client.Axios.Defaults.Serialization.ValueText = (value, _) => value is DateTime date
            ? "hook" + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            : null;

        await client.RequestAsync(new NestedQueryRequest
        {
            Filter = new NestedFilter
            {
                From = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                Month = new DateTime(2024, 3, 4, 0, 0, 0, DateTimeKind.Utc)
            }
        });

        // 嵌套对象的叶子也吃实例钩子（From），成员声明仍然压过钩子（Month）。
        Assert.Equal(
            "Filter[From]=hook20240102&Filter[Month]=2024-03",
            Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty));
    }

    [Fact]
    public async Task ValueFormatter_AppliesToHeaderAndScalarBody()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);
        client.Axios.Defaults.Serialization.ValueText = (value, _) => value switch
        {
            int number => "p" + number.ToString(CultureInfo.InvariantCulture),
            DateTime date => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            _ => null
        };

        await client.RequestAsync(new HookedScalarBodyRequest
        {
            Trace = 7,
            At = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc)
        });

        var received = Assert.Single(server.Received);
        Assert.Equal("p7", received.Headers["X-Trace"]);
        Assert.Equal("20240102", Encoding.UTF8.GetString(received.Body));
    }

    /// <summary>
    /// 格式化函数只有一个合法签名：<c>public static string? M(object?, ValueTextChannel)</c>。
    /// 可见性 / 第二参类型 / 返回类型任一处不符都要显式报错，不能退化成「找不到方法」或运行期静默回落。
    /// </summary>
    [Fact]
    public void FormatSpecReader_RejectsInvalidFormatterShapes()
    {
        var hidden = Assert.Throws<InvalidOperationException>(() => FormatSpecReader.Read(
            new FormatAttribute(typeof(InvalidFormats), "Hidden"),
            "probe",
            "Page"));
        Assert.Contains("ValueTextChannel", hidden.Message);

        var wrongChannel = Assert.Throws<InvalidOperationException>(() => FormatSpecReader.Read(
            new FormatAttribute(typeof(InvalidFormats), nameof(InvalidFormats.WrongChannel)),
            "probe",
            "Page"));
        Assert.Contains("ValueTextChannel", wrongChannel.Message);

        var wrongReturn = Assert.Throws<InvalidOperationException>(() => FormatSpecReader.Read(
            new FormatAttribute(typeof(InvalidFormats), nameof(InvalidFormats.ReturnsInt)),
            "probe",
            "Page"));
        Assert.Contains("返回", wrongReturn.Message);
    }

    /// <summary>钩子产出的文本按通道各自收尾：路径 / 查询继续转义，请求头原样写入。</summary>
    [Fact]
    public async Task ValueFormatter_OutputIsEscapedPerChannel()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url, c => c.Serialization.ValueText = (_, _) => "a&b c");

        await client.RequestAsync(new ChannelProbeRequest { Id = 1, Count = 2, Trace = 3 });

        var received = Assert.Single(server.Received);
        Assert.Equal("/probe/a%26b%20c", received.Path);
        Assert.Equal("When=a%26b%20c", received.Query);
        Assert.Equal("a&b c", received.Headers["X-Probe"]);
    }

    /// <summary>成员声明返回 null（本次不处理）时交给实例钩子，通道仍是成员所在的位置。</summary>
    [Fact]
    public async Task MemberFormatReturningNull_HandsOverToHook()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        var seen = new List<ValueTextChannel>();
        using var client = new ApiClient(server.Url, c => c.Serialization.ValueText = (_, channel) =>
        {
            seen.Add(channel);
            return "hook";
        });

        await client.RequestAsync(new NullableFormatRequest { Value = 0 });

        Assert.Equal("Value=hook", Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty));
        Assert.Equal([ValueTextChannel.Query], seen);
    }

    /// <summary>集合逐元素、嵌套对象逐叶子都会走到钩子，通道统一是 Query，且每个值只走一次。</summary>
    [Fact]
    public async Task ValueFormatter_ReachesEveryCollectionElementAndNestedLeaf()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        var seen = new List<ValueTextChannel>();
        using var client = new ApiClient(server.Url, c => c.Serialization.ValueText = (_, channel) =>
        {
            seen.Add(channel);
            return "e";
        });

        await client.RequestAsync(new HookedCollectionRequest { Codes = [1, 2, 3] });
        await client.RequestAsync(new HookedNestedRequest { Inner = new HookedInner { X = 1, Y = 2 } });

        var collection = Uri.UnescapeDataString(server.Received[0].Query ?? string.Empty);
        var nested = Uri.UnescapeDataString(server.Received[1].Query ?? string.Empty);
        Assert.Equal("Codes[]=e&Codes[]=e&Codes[]=e", collection);
        Assert.Equal("Inner[X]=e&Inner[Y]=e", nested);
        Assert.Equal(5, seen.Count);
        Assert.All(seen, static channel => Assert.Equal(ValueTextChannel.Query, channel));
    }

    // ------------------------------------------------------------------
    // 表单 / multipart：实例钩子与成员声明的分工
    // ------------------------------------------------------------------

    [Fact]
    public async Task ValueFormatter_AppliesToFormFields_MemberFormatStillWins()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);
        client.Axios.Defaults.Serialization.ValueText = (value, _) => value is DateTime date
            ? "hook" + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            : null;

        await client.Create<IFormatFormApi>().SubmitAsync(new FormPayload
        {
            When = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            Month = new DateTime(2024, 3, 4, 0, 0, 0, DateTimeKind.Utc),
            Note = "n"
        });

        var body = Uri.UnescapeDataString(Encoding.UTF8.GetString(Assert.Single(server.Received).Body));
        Assert.Contains("When=hook20240102", body);
        Assert.Contains("Month=2024-03", body);
        Assert.Contains("Note=n", body);
    }

    [Fact]
    public async Task ValueFormatter_AppliesToMultipartTextParts()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);
        client.Axios.Defaults.Serialization.ValueText = (value, _) => value is int number ? "p" + number.ToString(CultureInfo.InvariantCulture) : null;

        await client.Create<IFormatMultipartApi>().UploadAsync(7, new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        var body = Encoding.UTF8.GetString(Assert.Single(server.Received).Body);
        Assert.Contains("p7", body);
        Assert.Contains("2024-01-02", body);
    }

    // ------------------------------------------------------------------
    // 构建期报错：不适用的组合不留到运行期
    // ------------------------------------------------------------------

    [Fact]
    public void Format_WithStructuredBody_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ApiClient("http://127.0.0.1:1/").GetUri(new FormatWithStructuredBodyRequest()));

        Assert.Contains("结构化正文", ex.Message);
    }

    [Fact]
    public void Format_WithHeaderCollection_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ApiClient("http://127.0.0.1:1/").GetUri(new FormatWithHeaderCollectionRequest()));

        Assert.Contains("逐项格式化没有意义", ex.Message);
    }

    [Fact]
    public void Format_OnFilePart_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ApiClient("http://127.0.0.1:1/").GetUri(new FormatOnFilePartRequest()));

        Assert.Contains("文件按字节发送", ex.Message);
    }

    [Fact]
    public void FormatString_OnNonFormattable_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ApiClient("http://127.0.0.1:1/").GetUri(new FormatStringOnBoolRequest()));

        Assert.Contains("没有实现 IFormattable", ex.Message);
    }

    [Fact]
    public void FormatString_OnByteArray_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ApiClient("http://127.0.0.1:1/").GetUri(new FormatStringOnBytesRequest()));

        Assert.Contains("二进制没有格式串语义", ex.Message);
    }

    [Fact]
    public void Format_OnNonScalarQuery_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ApiClient("http://127.0.0.1:1/").GetUri(new FormatOnObjectQueryRequest()));

        Assert.Contains("不是标量", ex.Message);
    }

    [Fact]
    public void FormatAttribute_BlankFormatString_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new FormatAttribute("  "));
        Assert.Throws<ArgumentException>(() => new FormatAttribute(typeof(FormatHelpers), " "));
    }

    // ------------------------------------------------------------------
    // 运行期装配失败：声明式绑定与正文同口径（统一 ERR_BAD_REQUEST）
    // ------------------------------------------------------------------

    [Fact]
    public async Task ModelPathFormatterFailure_IsBadRequest()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        var error = await Assert.ThrowsAnyAsync<AxiosException>(
            () => client.RequestAsync(new ThrowingPathRequest { Id = 7 }));

        Assert.Equal(AxiosException.ErrBadRequest, error.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task ModelQueryFormatterFailure_IsBadRequest()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        var error = await Assert.ThrowsAnyAsync<AxiosException>(
            () => client.RequestAsync(new ThrowingQueryRequest { Name = "abc" }));

        Assert.Equal(AxiosException.ErrBadRequest, error.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public void ModelHeaderFormatterFailure_GetUri_IsBadRequest()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");

        var error = Assert.ThrowsAny<AxiosException>(
            () => client.GetUri(new ThrowingHeaderRequest { Code = "abc" }));

        Assert.Equal(AxiosException.ErrBadRequest, error.Code);
    }

    /// <summary>头集合的字典值也走钩子：钩子抛错同样是装配失败（ERR_BAD_REQUEST），请求不会发出。</summary>
    [Fact]
    public async Task HeaderCollectionHookFailure_IsBadRequest()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url, c => c.Serialization.ValueText = (_, _) =>
            throw new InvalidOperationException("boom"));

        var error = await Assert.ThrowsAnyAsync<AxiosException>(
            () => client.RequestAsync(new HookedHeaderCollectionRequest
            {
                Extra = new Dictionary<string, string> { ["X-K"] = "v" }
            }));

        Assert.Equal(AxiosException.ErrBadRequest, error.Code);
        Assert.Empty(server.Received);
    }

    /// <summary>
    /// 钩子产出的头值同样要在写头时过控制字符校验：头集合是本轮才接上钩子的入口，
    /// 不能让它成为绕过 <see cref="AxiosHeaders"/> 校验的通道（CR/LF 等于追加任意头）。
    /// </summary>
    [Fact]
    public async Task HeaderCollectionHookText_WithControlChars_IsRejected()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url, c => c.Serialization.ValueText = (_, _) => "v\r\nX-Evil: 1");

        var error = await Assert.ThrowsAnyAsync<AxiosException>(
            () => client.RequestAsync(new HookedHeaderCollectionRequest
            {
                Extra = new Dictionary<string, string> { ["X-K"] = "raw" }
            }));

        Assert.Equal(AxiosException.ErrBadRequest, error.Code);
        Assert.Contains("控制字符", error.Message);
        Assert.Empty(server.Received);
    }

    /// <summary>
    /// 钩子返回 <c>null</c>（本次不处理）时，字典值退回通道默认文本，
    /// 不能被当成「这个头不要了」——否则任一钩子写了 <c>is string</c> 判断就会静默丢头。
    /// </summary>
    [Fact]
    public async Task HeaderCollectionHookDeclining_KeepsDictionaryValue()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url, c => c.Serialization.ValueText = (_, _) => null);

        await client.RequestAsync(new HookedHeaderCollectionRequest
        {
            Extra = new Dictionary<string, string> { ["X-K"] = "raw" }
        });

        Assert.Equal("raw", Assert.Single(server.Received).Headers["X-K"]);
    }

    /// <summary>
    /// 钩子产出空串是「值就是空的」，不是「没有值」：头写成空值、查询保留 <c>key=</c>。
    /// 这两处的心智模型是「值 → 文本」，空串是合法文本，与 <c>null</c>（不处理）必须分开。
    /// </summary>
    [Fact]
    public async Task EmptyHookText_KeepsKeyWithEmptyValue()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url, c => c.Serialization.ValueText = (_, _) => string.Empty);

        await client.RequestAsync(new EmptyTextRequest { Id = 1, Trace = 2 });

        var received = Assert.Single(server.Received);
        Assert.Equal("Id=", received.Query);
        Assert.Equal(string.Empty, received.Headers["X-Empty"]);
    }

    // ------------------------------------------------------------------
    // 接口式：参数上的声明走同一套规则
    // ------------------------------------------------------------------

    [Fact]
    public async Task InterfaceParameter_Format_AppliesToPathAndQuery()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url);

        await client.Create<IFormatApi>().GetAsync(7, new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        var received = Assert.Single(server.Received);
        Assert.Equal("/fmt/007", received.Path);
        Assert.Equal("when=2024-01-02", Uri.UnescapeDataString(received.Query ?? string.Empty));
    }

    [Fact]
    public async Task InterfaceParameter_BodyFormatterFailure_IsBadRequest()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        var error = await Assert.ThrowsAnyAsync<AxiosException>(
            () => client.Create<IThrowingBodyFormatApi>().SubmitAsync("payload"));

        Assert.Equal("ERR_BAD_REQUEST", error.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task InterfaceParameter_PathFormatterFailure_IsBadRequest()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        var error = await Assert.ThrowsAnyAsync<AxiosException>(
            () => client.Create<IThrowingPathFormatApi>().GetAsync(7));

        Assert.Equal(AxiosException.ErrBadRequest, error.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task InterfaceParameter_QueryFormatterFailure_IsBadRequest()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        var error = await Assert.ThrowsAnyAsync<AxiosException>(
            () => client.Create<IThrowingQueryFormatApi>().GetAsync("abc"));

        Assert.Equal(AxiosException.ErrBadRequest, error.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public void InterfaceParameter_FormatWithStructuredBody_IsReported()
    {
        var errors = ApiClient.ValidateInterfaces(typeof(IBadFormatBodyApi));

        Assert.Contains(errors, static error => error.Contains("结构化正文"));
    }

#if NET8_0_OR_GREATER
    // ------------------------------------------------------------------
    // JSON 桥：结构化正文内部也按 [Format] 输出（默认不挂）
    // ------------------------------------------------------------------

    [Fact]
    public void JsonBridge_WritesFormattedText_AndRoundTrips()
    {
        var options = AxiosJsonFormat.ApplyTo(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var payload = new JsonFormatPayload
        {
            When = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            Amount = 12.5m
        };

        var json = JsonSerializer.Serialize(payload, options);
        Assert.Equal("""{"when":"2024-01-02","amount":"12.50"}""", json);

        var back = JsonSerializer.Deserialize<JsonFormatPayload>(json, options)!;
        Assert.Equal(payload.When, back.When);
        Assert.Equal(payload.Amount, back.Amount);
    }

    [Fact]
    public void JsonBridge_WritesAndReadsNullForNullableMember()
    {
        var options = AxiosJsonFormat.ApplyTo(new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var json = JsonSerializer.Serialize(new JsonNullableFormatPayload(), options);
        Assert.Equal("""{"maybe":null}""", json);

        // 可空成员的 null 必须能读回来：转换器不能把 null 当成「期望字符串」的错误。
        var back = JsonSerializer.Deserialize<JsonNullableFormatPayload>(json, options)!;
        Assert.Null(back.Maybe);
    }

    [Fact]
    public void JsonBridge_DoesNotAffectPlainOptions()
    {
        // 不挂桥时行为完全由序列化器决定：桥是可选层，不是默认行为。
        var json = JsonSerializer.Serialize(
            new JsonFormatPayload { When = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), Amount = 12.5m },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"2024-01-02T00:00:00Z\"", json);
        Assert.Contains("12.5", json);
    }
#endif

    // ------------------------------------------------------------------
    // 测试模型：请求模型
    // ------------------------------------------------------------------

    [Get("ch/{id}")]
    private sealed class MemberChannelRequest : IApiRequest<Person>
    {
        [Path, Format(typeof(FormatHelpers), nameof(FormatHelpers.PerChannel))]
        public int Id { get; set; }

        [Query, Format(typeof(FormatHelpers), nameof(FormatHelpers.PerChannel))]
        public int Page { get; set; }

        [Header("X-Page"), Format(typeof(FormatHelpers), nameof(FormatHelpers.PerChannel))]
        public int Trace { get; set; }
    }

    [Post("probe/{id}")]
    private sealed class ChannelProbeRequest : IApiRequest<Person>
    {
        [Path]
        public int Id { get; set; }

        [Query]
        public DateTime When { get; set; }

        [Body]
        public int Count { get; set; }

        [Header("X-Probe")]
        public int Trace { get; set; }
    }

    [Get("fmt/{id}")]
    private sealed class FormattedScalarRequest : IApiRequest<Person>
    {
        [Path, Format("000")]
        public int Id { get; set; }

        [Query, Format("yyyy-MM-dd")]
        public DateTime When { get; set; }

        [Header("X-Trace"), Format(typeof(FormatHelpers), nameof(FormatHelpers.Prefixed))]
        public int Trace { get; set; }
    }

    [Get("mixed")]
    private sealed class MixedFormatRequest : IApiRequest<Person>
    {
        [Query]
        public int Id { get; set; }

        [Query, Format("yyyy-MM-dd")]
        public DateTime When { get; set; }
    }

    [Get("nullable")]
    private sealed class NullableFormatRequest : IApiRequest<Person>
    {
        [Query, Format(typeof(FormatHelpers), nameof(FormatHelpers.NullWhenZero))]
        public int Value { get; set; }
    }

    [Get("codes")]
    private sealed class FormattedCollectionRequest : IApiRequest<Person>
    {
        [Query, Format("X2")]
        public int[] Codes { get; set; } = [];
    }

    [Get("map")]
    private sealed class FormattedMapRequest : IApiRequest<Person>
    {
        [Query("filters"), Format("yyyy-MM")]
        public Dictionary<string, DateTime> Filters { get; set; } = new();
    }

    [Post("raw")]
    private sealed class FormattedScalarBodyRequest : IApiRequest<Person>
    {
        [Body, Format("yyyy-MM-dd HH:mm")]
        public DateTime At { get; set; }
    }

    [Post("raw")]
    private sealed class HookedScalarBodyRequest : IApiRequest<Person>
    {
        [Header("X-Trace")]
        public int Trace { get; set; }

        [Body]
        public DateTime At { get; set; }
    }

    [Post("raw-bytes")]
    private sealed class FormattedBytesBodyRequest : IApiRequest<Person>
    {
        // byte[] 不是「标量」，但它是合法的裸标量正文形态（默认走 base64，可换成任意编码）。
        [Body, Format(typeof(FormatHelpers), nameof(FormatHelpers.Hex))]
        public byte[] Data { get; set; } = [];
    }

    [Post("body")]
    private sealed class FormatWithStructuredBodyRequest : IApiRequest<Person>
    {
        [Body, Format("yyyy-MM-dd")]
        public Person Payload { get; set; } = new("x", 1);
    }

    [Get("headers")]
    private sealed class FormatWithHeaderCollectionRequest : IApiRequest<Person>
    {
        [HeaderCollection, Format(typeof(FormatHelpers), nameof(FormatHelpers.Prefixed))]
        public Dictionary<string, string?> Extra { get; set; } = new();
    }

    [Post("upload")]
    [Multipart]
    private sealed class FormatOnFilePartRequest : IApiRequest<Person>
    {
        [Part, Format(typeof(FormatHelpers), nameof(FormatHelpers.Prefixed))]
        public Stream File { get; set; } = Stream.Null;
    }

    [Get("bool")]
    private sealed class FormatStringOnBoolRequest : IApiRequest<Person>
    {
        // bool 没有 IFormattable：格式串无处可用，构建期就该拦下。
        [Query, Format("X")]
        public bool Flag { get; set; }
    }

    [Get("bytes")]
    private sealed class FormatStringOnBytesRequest : IApiRequest<Person>
    {
        [Query, Format("N2")]
        public byte[] Payload { get; set; } = [];
    }

    [Get("object")]
    private sealed class FormatOnObjectQueryRequest : IApiRequest<Person>
    {
        [Query, Format("yyyy-MM-dd")]
        public Person Payload { get; set; } = new("x", 1);
    }

    [Get("throw/{id}")]
    private sealed class ThrowingPathRequest : IApiRequest<Person>
    {
        [Path, Format(typeof(FormatHelpers), nameof(FormatHelpers.Throwing))]
        public int Id { get; set; }
    }

    [Get("throw")]
    private sealed class ThrowingQueryRequest : IApiRequest<Person>
    {
        [Query, Format(typeof(FormatHelpers), nameof(FormatHelpers.Throwing))]
        public string? Name { get; set; }
    }

    [Get("throw-header")]
    private sealed class ThrowingHeaderRequest : IApiRequest<Person>
    {
        [Header("X-Code"), Format(typeof(FormatHelpers), nameof(FormatHelpers.Throwing))]
        public string? Code { get; set; }
    }

    [Get("nested")]
    private sealed class NestedQueryRequest : IApiRequest<Person>
    {
        [Query]
        public NestedFilter Filter { get; set; } = new();
    }

    [Get("hook-codes")]
    private sealed class HookedCollectionRequest : IApiRequest<Person>
    {
        [Query]
        public int[] Codes { get; set; } = [];
    }

    [Get("hook-nested")]
    private sealed class HookedNestedRequest : IApiRequest<Person>
    {
        [Query]
        public HookedInner Inner { get; set; } = new();
    }

    /// <summary>嵌套对象：没有任何成员声明，叶子全部交给实例钩子。</summary>
    public sealed class HookedInner
    {
        public int X { get; set; }

        public int Y { get; set; }
    }

    [Get("hc")]
    private sealed class HookedHeaderCollectionRequest : IApiRequest<Person>
    {
        [HeaderCollection]
        public Dictionary<string, string> Extra { get; set; } = new();
    }

    [Get("empty-text")]
    private sealed class EmptyTextRequest : IApiRequest<Person>
    {
        [Query]
        public int Id { get; set; }

        [Header("X-Empty")]
        public int Trace { get; set; }
    }

    /// <summary>非法签名样本：private / 第二参类型不对 / 返回类型不对。</summary>
    internal static class InvalidFormats
    {
        private static string? Hidden(object? value, ValueTextChannel channel) => null;

        public static string? WrongChannel(object? value, DayOfWeek channel) => null;

        public static int ReturnsInt(object? value, ValueTextChannel channel) => 0;
    }

    /// <summary>嵌套查询对象：一个叶子靠实例钩子、一个叶子靠成员声明。</summary>
    public sealed class NestedFilter
    {
        public DateTime From { get; set; }

        [Format("yyyy-MM")]
        public DateTime Month { get; set; }
    }

    /// <summary>普通调用的 params 根对象：成员声明同样要生效。</summary>
    public sealed class RootParams
    {
        [Format("yyyy-MM-dd")]
        public DateTime Since { get; set; }
    }

    // ------------------------------------------------------------------
    // 测试模型：接口式
    // ------------------------------------------------------------------

    public interface IFormatApi
    {
        [Get("fmt/{id}")]
        Task<AxiosResponse<Person>> GetAsync(
            [Path, Format("000")] int id,
            [Query, Format("yyyy-MM-dd")] DateTime when,
            CancellationToken cancellationToken = default);
    }

    public interface IFormatFormApi
    {
        [Post("form")]
        [BodyContentType(AxiosConfig.FormUrlEncodedContentType)]
        Task<AxiosResponse<Person>> SubmitAsync([Body] FormPayload payload, CancellationToken cancellationToken = default);
    }

    public interface IFormatMultipartApi
    {
        [Post("upload")]
        [Multipart]
        Task<AxiosResponse<Person>> UploadAsync(
            [Part] int id,
            [Part, Format("yyyy-MM-dd")] DateTime when,
            CancellationToken cancellationToken = default);
    }

    public interface IBadFormatBodyApi
    {
        [Post("body")]
        Task<AxiosResponse<Person>> SubmitAsync(
            [Body, Format("yyyy-MM-dd")] Person payload,
            CancellationToken cancellationToken = default);
    }

    public interface IThrowingBodyFormatApi
    {
        [Post("body")]
        Task<AxiosResponse<Person>> SubmitAsync(
            [Body, Format(typeof(FormatHelpers), nameof(FormatHelpers.Throwing))] string payload,
            CancellationToken cancellationToken = default);
    }

    public interface IThrowingPathFormatApi
    {
        [Get("throw/{id}")]
        Task<AxiosResponse<Person>> GetAsync(
            [Path, Format(typeof(FormatHelpers), nameof(FormatHelpers.Throwing))] int id,
            CancellationToken cancellationToken = default);
    }

    public interface IThrowingQueryFormatApi
    {
        [Get("throw")]
        Task<AxiosResponse<Person>> GetAsync(
            [Query, Format(typeof(FormatHelpers), nameof(FormatHelpers.Throwing))] string name,
            CancellationToken cancellationToken = default);
    }

    /// <summary>表单正文的载荷：一个成员靠实例钩子、一个成员靠成员声明。</summary>
    public sealed class FormPayload
    {
        public DateTime When { get; set; }

        [Format("yyyy-MM")]
        public DateTime Month { get; set; }

        public string Note { get; set; } = string.Empty;
    }

    /// <summary>JSON 桥的载荷。</summary>
    public sealed class JsonFormatPayload
    {
        [Format("yyyy-MM-dd")]
        public DateTime When { get; set; }

        [Format(typeof(FormatHelpers), nameof(FormatHelpers.Money))]
        public decimal Amount { get; set; }
    }

    /// <summary>JSON 桥的可空载荷：null 必须原样往返，不能被桥当成「期望字符串」。</summary>
    public sealed class JsonNullableFormatPayload
    {
        [Format("yyyy-MM-dd")]
        public DateTime? Maybe { get; set; }
    }
}

/// <summary>测试用的格式化函数集合：签名必须是 <c>public static string? M(object? value, ValueTextChannel channel)</c>。</summary>
internal static class FormatHelpers
{
    public static string? Prefixed(object? value, ValueTextChannel _)
        => value is int number ? "T" + number.ToString(CultureInfo.InvariantCulture) : null;

    /// <summary>按通道给出不同文本：验证成员级格式化函数的第二个参数确实是值所在通道。</summary>
    public static string? PerChannel(object? value, ValueTextChannel channel)
        => value is int number
            ? channel switch
            {
                ValueTextChannel.Path => "p" + number.ToString(CultureInfo.InvariantCulture),
                ValueTextChannel.Query => "q" + number.ToString(CultureInfo.InvariantCulture),
                ValueTextChannel.Header => "h" + number.ToString(CultureInfo.InvariantCulture),
                _ => "x" + number.ToString(CultureInfo.InvariantCulture)
            }
            : null;

    public static string? NullWhenZero(object? value, ValueTextChannel _)
        => value is int number and not 0 ? "v" + number.ToString(CultureInfo.InvariantCulture) : null;

    public static string? Money(object? value, ValueTextChannel _)
        => value is decimal amount ? amount.ToString("0.00", CultureInfo.InvariantCulture) : null;

    public static string? Throwing(object? value, ValueTextChannel _) => throw new InvalidOperationException("formatter failed");

    public static string? Hex(object? value, ValueTextChannel _)
        => value is byte[] bytes ? Convert.ToHexString(bytes) : null;
}

/// <summary>只写了一参数的旧形态：反射路径必须拒绝，与源生成器的 AXA015 口径一致。</summary>
internal static class LegacyFormats
{
    public static string? Plain(object? value) => value?.ToString();
}
