
using AxiosNet.Pooling;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text;

namespace AxiosNet.Tests.Requests;

/// <summary>
/// 响应解析：裸标量回退、字符集与 BOM 探测、状态文本映射、非 JSON 原文。
/// </summary>
public sealed class ResponseParsingTests
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

    // 回放不携带按请求 Auth 的契约由 Audit20261024P2FixTests 锁定（含「用目标实例默认」），

    // ------------------------------------------------------------------
    // ScalarParser：Enum 未定义值 / char 多字符
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_Enum_UndefinedNumericValue_FailsBadResponse()
    {
        // 内置 JSON 对「裸数字 → enum」不校验 IsDefined（STJ 既定行为），单靠它测不出分层：
        // 注入一个反序列化即抛的序列化器，让「ScalarParser 拒未定义值」变成可观测的差异 ——
        // (Perm)999 不是合法枚举值：应拒断并落到 ERR_BAD_RESPONSE。
        await using var server = TestHttpServer.Start(_ => BareText("999"));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<Perm>(
            $"http://127.0.0.1:{server.Port}/",
            c => c.Serialization.ContentSerializer = new ThrowingSerializer()));
        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
    }

    [Fact]
    public async Task GetAsync_Enum_DefinedAndFlagsCombination_AreAccepted()
    {
        await using var server = TestHttpServer.Start(_ => BareText("Read,Write"));
        using var client = new AxiosClient();

        var response = await client.GetAsync<Perm>($"http://127.0.0.1:{server.Port}/");
        Assert.Equal(Perm.Read | Perm.Write, response.Data);
    }

    [Fact]
    public async Task GetAsync_Char_MultiCharText_FailsBadResponse()
    {
        await using var server = TestHttpServer.Start(_ => BareText("abc"));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<char>($"http://127.0.0.1:{server.Port}/"));
        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);

        // 单字符仍然正常（不因收紧而误拒）
        await using var single = TestHttpServer.Start(_ => BareText("x"));
        using var client2 = new AxiosClient();
        var ok = await client2.GetAsync<char>($"http://127.0.0.1:{single.Port}/");
        Assert.Equal('x', ok.Data);
    }

    [Fact]
    public async Task GetAsync_Enum_UndefinedNumericValue_WithDefaultSerializer_FailsBadResponse()
    {
        // 同一条 application/json + "999" 的响应但不注入序列化器：STJ 会把 999 转成 (E)999，
        // 产物必须按同一标准复核。
        await using var server = TestHttpServer.Start(_ => BareText("999"));
        using var client = new AxiosClient();

        var fromPlain = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<Level>(
            $"http://127.0.0.1:{server.Port}/"));
        Assert.Equal(AxiosException.ErrBadResponse, fromPlain.Code);
        Assert.Contains("undefined", fromPlain.Message);

        var fromFlags = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<Perm>(
            $"http://127.0.0.1:{server.Port}/"));
        Assert.Equal(AxiosException.ErrBadResponse, fromFlags.Code);

        // 第三方序列化器直接产出 (Level)999（不依赖 STJ 的既定行为）：复核同样拒
        var fromThirdParty = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<Level>(
            $"http://127.0.0.1:{server.Port}/",
            c => c.Serialization.ContentSerializer = new FixedUndefinedEnumSerializer()));
        Assert.Equal(AxiosException.ErrBadResponse, fromThirdParty.Code);
    }

    [Fact]
    public async Task GetAsync_Enum_DefinedValues_WithDefaultSerializer_AreAccepted()
    {
        // 复核不能误拒合法值：已定义数字（第 8 步直解析）、第三方序列化器产出的已定义枚举
        // （复核就发生在这一层）、flags 组合数字都要照常通过。
        await using var number = TestHttpServer.Start(_ => BareText("1"));
        await using var name = TestHttpServer.Start(_ => BareText("\"Low\""));
        await using var flags = TestHttpServer.Start(_ => BareText("3"));

        using var numberClient = new AxiosClient();
        Assert.Equal(Level.Low, (await numberClient.GetAsync<Level>($"http://127.0.0.1:{number.Port}/")).Data);

        using var nameClient = new AxiosClient();
        Assert.Equal(Level.Low, (await nameClient.GetAsync<Level>(
            $"http://127.0.0.1:{name.Port}/",
            c => c.Serialization.ContentSerializer = new StringEnumSerializer())).Data);

        using var flagsClient = new AxiosClient();
        Assert.Equal(Perm.Read | Perm.Write, (await flagsClient.GetAsync<Perm>($"http://127.0.0.1:{flags.Port}/")).Data);
    }

    [Fact]
    public async Task GetAsync_EnumBaseType_WithSerializerReturningEnum_IsNotFalseRejected()
    {
        // 目标类型本身不是枚举（Enum 基类）时，序列化器产出具体枚举是合法的：
        // 复核只对「目标类型是枚举」生效 —— Enum.IsDefined 对非枚举入参抛 ArgumentException，
        // 若不设防会被外层 catch 收成错误的 ERR_BAD_RESPONSE（2026-10-25 自审发现）。
        var handler = new RecordingHandler(_ => JsonResponse());
        using var client = new AxiosClient(handler: handler);

        var response = await client.GetAsync<Enum>(
            "http://localhost/a",
            c => c.Serialization.ContentSerializer = new FixedDefinedEnumSerializer());

        Assert.Equal(Level.Low, response.Data);
    }

    [Fact]
    public async Task GetAsync_Enum_UndefinedValue_OnErrorStatus_KeepsStatusError()
    {
        // 状态码不合法时 lenient：正文问题不得盖住 HTTP 状态错误（与解析失败同口径回退原文）。
        await using var server = TestHttpServer.Start(
            _ => new TestResponse(StatusCode: 500, Body: Encoding.UTF8.GetBytes("999"), ContentType: "application/json"));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<Level>($"http://127.0.0.1:{server.Port}/"));
        Assert.Contains("status code 500", ex.Message);
    }

    [Fact]
    public async Task GetAsync_OfInt_BareNumberText_IsParsed()
    {
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(Body: Encoding.UTF8.GetBytes("42"), ContentType: "text/plain; charset=utf-8"));
        using var client = new AxiosClient();

        var response = await client.GetAsync<int>(server.Url);

        Assert.Equal(42, response.Data);
    }

    [Fact]
    public async Task GetAsync_OfDateTime_BareDateText_IsParsed()
    {
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(Body: Encoding.UTF8.GetBytes("2024-01-02"), ContentType: "text/plain"));
        using var client = new AxiosClient();

        var response = await client.GetAsync<DateTime>(server.Url);

        Assert.Equal(new DateTime(2024, 1, 2), response.Data);
    }

    [Fact]
    public async Task Response_CharsetInContentType_IsAutoDetected()
    {
        var body = Encoding.Unicode.GetBytes("你好"); // UTF-16LE
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(Body: body, ContentType: "text/plain; charset=utf-16"));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url, c => c.ResponseType = ResponseType.Text);

        Assert.Equal("你好", response.Data);
    }

    [Fact]
    public async Task Response_Bom_IsAutoDetected_WhenNoCharset()
    {
        var body = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("你好")).ToArray();
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(Body: body, ContentType: "text/plain"));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url, c => c.ResponseType = ResponseType.Text);

        Assert.Equal("你好", response.Data);
    }

    [Fact]
    public async Task ResponseEncoding_OverridesAutoDetection()
    {
        // 服务端未声明 charset、字节为 Latin1：默认按 UTF-8 解码会乱码，强制 Latin1 后正确
        var body = Encoding.Latin1.GetBytes("café");
        await using var server = TestHttpServer.Start(_ =>
            new TestResponse(Body: body, ContentType: "text/plain"));
        using var client = new AxiosClient();

        var auto = await client.GetAsync(server.Url, c => c.ResponseType = ResponseType.Text);
        Assert.NotEqual("café", auto.Data);

        var forced = await client.GetAsync(server.Url, c =>
        {
            c.ResponseType = ResponseType.Text;
            c.Serialization.ResponseEncoding = Encoding.Latin1;
        });
        Assert.Equal("café", forced.Data);
    }

    // ---------------------------------------------------------------
    // 响应解码
    // ---------------------------------------------------------------

    [Fact]
    public async Task ResponseCharset_Utf16WithoutBom_IsDecoded()
    {
        const string text = "hello 你好";
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.Unicode.GetBytes(text), ContentType: "text/plain; charset=utf-16"));

        using var client = new AxiosClient();

        // charset 直接给出 UTF-16（LE）时不应依赖 BOM 探测
        var response = await client.GetAsync<string>(server.Url + "text");

        Assert.Equal(text, response.Data);
    }

    [Fact]
    public async Task ResponseCharset_QuotedValue_IsAccepted()
    {
        const string text = "引号包裹的 charset";
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes(text), ContentType: "text/plain; charset=\"utf-8\""));

        using var client = new AxiosClient();

        var response = await client.GetAsync<string>(server.Url + "text");

        Assert.Equal(text, response.Data);
    }

    [Fact]
    public async Task ByteArrayResponse_IsNotDecodedAsText()
    {
        // 故意用非 UTF-8 的字节序列：任何一次「先解码成 string 再转回来」都会破坏它
        var payload = new byte[] { 0x00, 0xFF, 0xFE, 0x80, 0x41, 0xC3, 0x28 };
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: payload, ContentType: "application/octet-stream"));

        using var client = new AxiosClient();

        // 不显式指定 responseType，靠 T = byte[] 走「只要字节」的分支
        var response = await client.GetAsync<byte[]>(server.Url + "bin");

        Assert.Equal(payload, response.Data);
    }

    // ---------------------------------------------------------------
    // ScalarParser：非 JSON 文本按目标类型兜底
    // ---------------------------------------------------------------

    [Fact]
    public async Task BareText_Bool_IsParsedCaseInsensitively()
    {
        // JSON 的布尔是小写 true/false，"True" 不是合法 JSON，走裸标量兜底
        await using var server = StartTextServer("True");
        using var client = new AxiosClient();

        var response = await client.GetAsync<bool>(server.Url);

        Assert.True(response.Data);
    }

    [Fact]
    public async Task BareText_Char_IsParsedFromFirstCharacter()
    {
        await using var server = StartTextServer("A");
        using var client = new AxiosClient();

        var response = await client.GetAsync<char>(server.Url);

        Assert.Equal('A', response.Data);
    }

    [Fact]
    public async Task BareText_Guid_WithBraces_IsParsed()
    {
        var guid = Guid.Parse("{12345678-1234-1234-1234-123456789012}");
        await using var server = StartTextServer("{12345678-1234-1234-1234-123456789012}");
        using var client = new AxiosClient();

        var response = await client.GetAsync<Guid>(server.Url);

        Assert.Equal(guid, response.Data);
    }

    [Fact]
    public async Task BareText_Enum_IsParsedIgnoringCase()
    {
        await using var server = StartTextServer("monday");
        using var client = new AxiosClient();

        var response = await client.GetAsync<DayOfWeek>(server.Url);

        Assert.Equal(DayOfWeek.Monday, response.Data);
    }

    [Fact]
    public async Task BareText_NullableEnum_IsUnwrapped()
    {
        await using var server = StartTextServer("Friday");
        using var client = new AxiosClient();

        var response = await client.GetAsync<DayOfWeek?>(server.Url);

        Assert.Equal(DayOfWeek.Friday, response.Data);
    }

    [Fact]
    public async Task BareText_Decimal_AcceptsGroupSeparators()
    {
        await using var server = StartTextServer("1,234.5");
        using var client = new AxiosClient();

        var response = await client.GetAsync<decimal>(server.Url);

        Assert.Equal(1234.5m, response.Data);
    }

    [Fact]
    public async Task BareText_Long_AcceptsExplicitPlusSign()
    {
        // JSON 数字不允许前导 '+'，Convert.ToInt64 也不收千分位（与 ToDecimal 的语义差异是 BCL 行为）
        await using var server = StartTextServer("+1000");
        using var client = new AxiosClient();

        var response = await client.GetAsync<long>(server.Url);

        Assert.Equal(1000L, response.Data);
    }

    [Fact]
    public async Task BareText_DateTimeOffset_IsParsedInvariantly()
    {
        await using var server = StartTextServer("2024-01-02T03:04:05+08:00");
        using var client = new AxiosClient();

        var response = await client.GetAsync<DateTimeOffset>(server.Url);

        Assert.Equal(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.FromHours(8)), response.Data);
    }

    [Fact]
    public async Task BareText_DateOnly_And_TimeOnly_AreParsed()
    {
        await using var dateServer = StartTextServer("2024-01-02");
        await using var timeServer = StartTextServer("03:04:05");
        using var client = new AxiosClient();

        Assert.Equal(new DateOnly(2024, 1, 2), (await client.GetAsync<DateOnly>(dateServer.Url)).Data);
        Assert.Equal(new TimeOnly(3, 4, 5), (await client.GetAsync<TimeOnly>(timeServer.Url)).Data);
    }

    [Fact]
    public async Task BareText_TimeSpan_And_Uri_AreParsed()
    {
        await using var spanServer = StartTextServer("01:02:03");
        await using var uriServer = StartTextServer("http://example.com/a");
        using var client = new AxiosClient();

        Assert.Equal(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(3),
            (await client.GetAsync<TimeSpan>(spanServer.Url)).Data);
        Assert.Equal(new Uri("http://example.com/a"), (await client.GetAsync<Uri>(uriServer.Url)).Data);
    }

    [Fact]
    public async Task BareText_Unparseable_WrappedAsBadResponse()
    {
        await using var server = StartTextServer("not-a-guid");
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<Guid>(server.Url));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
        Assert.IsAssignableFrom<JsonException>(ex.Cause);
    }

    [Fact]
    public async Task BareText_UnparseableNumber_WrappedAsBadResponse()
    {
        await using var server = StartTextServer("abc");
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<int>(server.Url));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
    }

    // ---------------------------------------------------------------
    // ResponseTextDecoder：charset / BOM / 强制编码的优先级
    // ---------------------------------------------------------------

    [Fact]
    public async Task Response_UnknownCharset_FallsBackToUtf8()
    {
        var body = Encoding.UTF8.GetBytes("你好");
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: body, ContentType: "text/plain; charset=x-unknown-charset"));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url, c => c.ResponseType = ResponseType.Text);

        Assert.Equal("你好", response.Data);
    }

    [Theory]
    [InlineData("utf-16be")]
    [InlineData("utf-32le")]
    [InlineData("utf-32be")]
    public async Task Response_Bom_IsDetected_ForAllUnicodeVariants(string variant)
    {
        var encoding = variant switch
        {
            "utf-16be" => Encoding.BigEndianUnicode,
            "utf-32le" => new UTF32Encoding(bigEndian: false, byteOrderMark: true),
            _ => new UTF32Encoding(bigEndian: true, byteOrderMark: true)
        };

        var body = encoding.GetPreamble().Concat(encoding.GetBytes("你好")).ToArray();
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: body, ContentType: "text/plain"));
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url, c => c.ResponseType = ResponseType.Text);

        Assert.Equal("你好", response.Data);
    }

    [Fact]
    public async Task Response_ForcedEncoding_BeatsDeclaredCharset()
    {
        // 服务端谎报 charset=utf-8，实际字节是无 BOM 的 UTF-16：强制编码必须压过声明
        var body = Encoding.Unicode.GetBytes("你好");
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: body, ContentType: "text/plain; charset=utf-8"));
        using var client = new AxiosClient();

        var auto = await client.GetAsync(server.Url, c => c.ResponseType = ResponseType.Text);
        Assert.NotEqual("你好", auto.Data);

        var forced = await client.GetAsync(server.Url, c =>
        {
            c.ResponseType = ResponseType.Text;
            c.Serialization.ResponseEncoding = Encoding.Unicode;
        });
        Assert.Equal("你好", forced.Data);
    }

    // StatusTextMap（reason phrase 由库维护、不走线上）的整张表在 StatusTextTests 里逐码断言。

    // ---------------------------------------------------------------
    // 非 JSON 文本：不解析、不抛异常（首个非空白字符闸口）
    // ---------------------------------------------------------------

    [Fact]
    public async Task NonJsonText_IsReturnedVerbatim()
    {
        // HTML 错误页 / 纯文本端点没有「文档形态」：原样交给调用方，而不是「解析失败再回退」。
        const string html = "<html><body>502 Bad Gateway</body></html>";
        await using var server = StartTextServer(html, "text/html");
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url);

        Assert.Equal(html, response.Data);
    }

    [Theory]
    [InlineData("null", JsonValueKind.Null)]
    [InlineData("42", JsonValueKind.Number)]
    [InlineData("-1.5", JsonValueKind.Number)]
    [InlineData("true", JsonValueKind.True)]
    [InlineData("\"text\"", JsonValueKind.String)]
    [InlineData("[1,2]", JsonValueKind.Array)]
    [InlineData("{\"a\":1}", JsonValueKind.Object)]
    [InlineData("  \r\n{\"a\":1}", JsonValueKind.Object)]
    public async Task JsonDocument_IsParsedForEveryRootForm(string body, JsonValueKind expected)
    {
        // 闸口只看首个非空白字符：RFC 8259 允许的每一种根形态都必须仍然被解析成 JsonElement
        // （漏掉任何一种都会把合法响应降级成字符串，属于行为回归）。
        await using var server = StartTextServer(body, "application/json");
        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url);

        var element = Assert.IsType<JsonElement>(response.Data);
        Assert.Equal(expected, element.ValueKind);
    }

    [Fact]
    public async Task NonJsonText_DoesNotTakeTheExceptionPath()
    {
        // 「解析失败再吞掉」这条路每次请求要抛 3 次 JsonException，代价是分配与吞吐。
        // 闸口生效后一次都不该抛。计数按线程过滤：xunit 并行跑其它测试类，全局计数会被它们污染。
        var threadId = Environment.CurrentManagedThreadId;
        var jsonExceptions = 0;

        void OnFirstChance(object? sender, FirstChanceExceptionEventArgs e)
        {
            if (e.Exception is JsonException && Environment.CurrentManagedThreadId == threadId)
            {
                jsonExceptions++;
            }
        }

        using var client = new AxiosClient(
            handler: new FixedTextHandler("<html><body>nope</body></html>", "text/html"));

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        try
        {
            for (var i = 0; i < 50; i++)
            {
                var response = await client.GetAsync("http://stub.local/x");
                Assert.Equal("<html><body>nope</body></html>", response.Data);
            }
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
        }

        Assert.Equal(0, jsonExceptions);
    }

    [Fact]
    public async Task TextResponse_WithScalarGeneric_IsWrappedAsBadResponse()
    {
        await using var server = StartTextServer("hello");
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<int>(
            server.Url,
            c => c.ResponseType = ResponseType.Text));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
        Assert.IsAssignableFrom<InvalidCastException>(ex.Cause);
        Assert.Contains("responseType", ex.Cause!.Message);
    }

    // ------------------------------------------------------------------
    // 测试辅助
    // ------------------------------------------------------------------

    private static HttpResponseMessage JsonResponse()
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(TestJson.Bytes(new Person("alice", 1)))
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") }
            }
        };


    private static TestResponse BareText(string text)
        => new(Body: Encoding.UTF8.GetBytes(text), ContentType: "application/json");

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


    /// <summary>反序列化即抛的序列化器：用于观测「ScalarParser 已拒断、不会静默放行」的分层差异。</summary>
    private sealed class ThrowingSerializer : IContentSerializer
    {
        public string Serialize(object? value, Type type, AxiosConfig config) => "0";

        public object? Deserialize(string text, Type type, AxiosConfig config)
            => throw new InvalidOperationException("strict");
    }

    /// <summary>带字符串枚举转换器的第三方序列化器：把 <c>"Low"</c> 解成 <see cref="Level.Low"/>
    /// —— 产物复核不得误拒序列化器合法产出的已定义枚举值。</summary>
    private sealed class StringEnumSerializer : IContentSerializer
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            Converters = { new JsonStringEnumConverter() }
        };

        public string Serialize(object? value, Type type, AxiosConfig config)
            => JsonSerializer.Serialize(value, type, Options);

        public object? Deserialize(string text, Type type, AxiosConfig config)
            => JsonSerializer.Deserialize(text, type, Options);
    }

    /// <summary>无视正文、固定产出未定义枚举值的第三方序列化器：复核必须拒（不依赖 STJ 的既定行为）。</summary>
    private sealed class FixedUndefinedEnumSerializer : IContentSerializer
    {
        public string Serialize(object? value, Type type, AxiosConfig config) => "0";

        public object? Deserialize(string text, Type type, AxiosConfig config) => (Level)999;
    }

    /// <summary>固定产出已定义枚举值的第三方序列化器：目标类型不是枚举时复核必须放行。</summary>
    private sealed class FixedDefinedEnumSerializer : IContentSerializer
    {
        public string Serialize(object? value, Type type, AxiosConfig config) => "0";

        public object? Deserialize(string text, Type type, AxiosConfig config) => Level.Low;
    }


    [Flags]
    private enum Perm
    {
        None = 0,
        Read = 1,
        Write = 2,
        Execute = 4
    }

    /// <summary>非 flags 枚举（未定义值复核的两条路径都要覆盖）。</summary>
    private enum Level
    {
        Low = 1,
        High = 2
    }


    private static TestHttpServer StartTextServer(string text, string contentType = "text/plain")
        => TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes(text), ContentType: contentType));

    /// <summary>固定正文 + 固定 Content-Type 的 Handler：不碰网络，便于确定性地数异常次数。</summary>
    private sealed class FixedTextHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly string _contentType;

        public FixedTextHandler(string body, string contentType)
        {
            _body = Encoding.UTF8.GetBytes(body);
            _contentType = contentType;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(_body);
            content.Headers.ContentType = new(_contentType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}