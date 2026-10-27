
using AxiosNet.Pooling;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using System.Diagnostics;
using System.Dynamic;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Net.Security;
using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using System.Text;

namespace AxiosNet.Tests.Requests;

/// <summary>
/// 请求体与 ContentType：编码只看 ContentType、标量/字节/流/HttpContent、长度头校验。
/// </summary>
public sealed class RequestBodyTests
{
    [Theory]
    [InlineData("application/json\r\nX-Injected: yes")]
    [InlineData("application/json\nX-Injected: yes")]
    [InlineData("application/json\rX-Injected: yes")]
    public void ContentType_WithCrlf_Throws(string contentType)
    {
        Assert.Throws<ArgumentException>(() => new AxiosConfig { ContentType = contentType });
    }

    [Fact]
    public async Task ContentType_WithCrlf_CannotReachTheWire()
    {
        await using var server = TestHttpServer.Start(_ => Json());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        await Assert.ThrowsAsync<ArgumentException>(async () => await client.PostAsync("x", new { a = 1 }, c => c.ContentType = "application/json\r\nX-Injected: yes"));

        Assert.Empty(server.Received);
    }

    // ===============================================================
    // Bug 12：ContentType 赋 null / 空白静默变成「显式 application/json」
    // ===============================================================

    [Fact]
    public void ContentType_NullAssignment_Throws()
    {
        var config = new AxiosConfig();

        Assert.Throws<ArgumentNullException>(() => config.ContentType = null!);
    }

    [Fact]
    public void ContentType_BlankAssignment_Throws()
    {
        var config = new AxiosConfig();

        // 不应静默重置为 application/json 并压掉序列化器自报的媒体类型。
        Assert.Throws<ArgumentException>(() => config.ContentType = "   ");
    }

    // ===============================================================
    // Bug 14：ContentType 声明的 charset 与 RequestEncoding 矛盾时静默产出错误字节
    // ===============================================================

    [Fact]
    public async Task ContentTypeCharset_ConflictsWithRequestEncoding_Throws()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        // 头声明 utf-8、正文按 Latin1 编码：服务端按头解出乱码且没有任何报错
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(
            server.Url + "x",
            "café",
            c =>
            {
                c.ContentType = "text/plain; charset=utf-8";
                c.Serialization.RequestEncoding = Encoding.Latin1;
            }));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task ContentTypeCharset_WithoutRequestEncoding_EncodesBodyWithDeclaredCharset()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        // 声明 charset=iso-8859-1 但没设 RequestEncoding：正文必须按声明的 charset 编码，与头保持一致。
        await client.PostAsync(
            server.Url + "x",
            "café",
            c => c.ContentType = "text/plain; charset=iso-8859-1");

        var sent = server.Received[0];
        Assert.Equal("text/plain; charset=iso-8859-1", ContentType(sent));
        Assert.Equal(new byte[] { (byte)'c', (byte)'a', (byte)'f', 0xE9 }, sent.Body);
    }

    // ===============================================================
    // 2. 上传正文写失败（网络 I/O）被 ProgressHttpContent 误译成
    //    「OnUploadProgress 回调抛出异常」+ ERR_BAD_REQUEST；期望 ERR_NETWORK。
    // ===============================================================

    [Fact]
    public async Task UploadBodyWriteFailure_MapsToNetworkError()
    {
        using var client = new AxiosClient(handler: new FailingBodyWriteHandler(), disposeHandler: false);

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.PostAsync("http://localhost/upload", new { a = 1 }, c => c.Callbacks.OnUploadProgress = _ => { }));

        Assert.Equal(AxiosException.ErrNetwork, ex.Code);
    }

    [Fact]
    public async Task UploadBodyWriteFailure_WithoutProgressCallback_StillMapsToNetworkError()
    {
        using var client = new AxiosClient(handler: new FailingBodyWriteHandler(), disposeHandler: false);

        // 只有 MaxRate 没有回调：不应报「OnUploadProgress 回调抛出异常」。
        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.PostAsync("http://localhost/upload", new { a = 1 }, c => c.Limits.MaxRate = 1_000_000));

        Assert.Equal(AxiosException.ErrNetwork, ex.Code);
        Assert.DoesNotContain("OnUploadProgress", ex.Message);
    }

    // ===============================================================
    // （已排除）表单非 UTF-8 百分号编码：逐字节直出可打印 ASCII 是字节级可逆的
    // （big5 的 "三" = A4 54 编码成 %A4T，解码回来仍是 A4 54），不是缺陷。
    // 强行"按字符"改写会让 UTF-16 等编码的 ASCII 字符丢掉尾字节（k%00 → k），反而损坏键名。
    // ===============================================================

    // ===============================================================
    // 5. Content-Length 校验不得依赖头的插入顺序（无正文时应一律跳过）。
    // ===============================================================

    [Fact]
    public async Task ExplicitContentLength_WithoutBody_IsIgnoredRegardlessOfHeaderOrder()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        // 先写 Content-Type：不应先造一个 0 字节正文导致 Content-Length 被判成不一致。
        await client.PostAsync(server.Url + "bodyless", (object?)null, c =>
        {
            c.Headers.Set("Content-Type", "application/json");
            c.Headers.Set("Content-Length", "5");
        });

        Assert.Equal("0", server.Received[0].Headers.GetValueOrDefault("content-length"));
    }

    // ===============================================================
    // M3：Headers 里声明的 Content-Type charset 与实际正文编码矛盾时不能静默发出
    // ===============================================================

    [Fact]
    public async Task M3_HeadersContentTypeCharset_ConflictsWithBodyEncoding_Throws()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        // 正文按默认 UTF-8 编码，头却声明 iso-8859-1：服务端按头解出乱码且没有任何报错
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(
            "x",
            "héllo",
            c => c.Headers.Set("Content-Type", "text/plain; charset=iso-8859-1")));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task M3_HeadersContentTypeCharset_MatchingBodyEncoding_IsAllowed()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        await client.PostAsync(
            "x",
            "héllo",
            c => c.Headers.Set("Content-Type", "text/plain; charset=utf-8"));

        Assert.Equal("héllo", Encoding.UTF8.GetString(Assert.Single(server.Received).Body));
    }

    // ===============================================================
    // L13：ContentType 的 charset 判重要容忍 "charset = utf-8" 这种空格写法
    // ===============================================================

    [Fact]
    public async Task L13_ContentTypeCharsetWithSpaces_DoesNotDuplicateCharset()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        await client.PostAsync("x", "hi", c =>
        {
            c.ContentType = "text/plain; charset = utf-8";
            c.Serialization.RequestEncoding = Encoding.UTF8;
        });

        var contentType = Assert.Single(server.Received).Headers["content-type"];
        Assert.Equal(1, CountOccurrences(contentType, "charset"));
    }

    [Fact]
    public async Task CancellationTokenAsSecondArgument_IsNotSerializedAsBody()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();
        using var cts = new CancellationTokenSource();

        await client.GetAsync(server.Url + "x", cts.Token);
        await client.PostAsync(server.Url + "x", cts.Token);

        Assert.Equal(2, server.Received.Count);
        Assert.All(server.Received, r => Assert.Empty(r.Body));
        Assert.Equal("GET", server.Received[0].Method);
        Assert.Equal("POST", server.Received[1].Method);
    }

    [Fact]
    public async Task CancellationTokenInsideObject_IsRejected()
    {
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.PostAsync("http://127.0.0.1:1/x", (object)new CancellationTokenSource().Token));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    [Fact]
    public async Task HttpContentWithoutContentType_IsNotGivenJsonHeader()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        using var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("hello")));
        await client.PostAsync(server.Url + "x", content);

        Assert.False(server.Received[0].Headers.ContainsKey("Content-Type"));
        Assert.Equal("hello", Encoding.UTF8.GetString(server.Received[0].Body));
    }

    // ---------------------------------------------------------------
    // 进度包装不得丢内容头
    // ---------------------------------------------------------------

    [Fact]
    public async Task MaxBodyLength_WithUploadProgressWrapper_IsStillEnforced()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        var payload = new byte[1024];

        // 进度包装（ProgressHttpContent）必须复制内层 Content-Length：
        // 一旦复制漏了，content.Headers.ContentLength 为 null，这个上限就静默失效了。
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(server.Url + "up", payload, c =>
        {
            c.Limits.MaxBodyLength = 16;
            c.Callbacks.OnUploadProgress = _ => { };
        }));

        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);

        // 超限在发送前判定：服务端一个请求都不该看到
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task ExplicitInstanceContentType_IsAppliedToRawHttpContent()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));

        // ContentType 显式设置 → ContentTypeSet 必须穿过 Defaults.Clone() 活到请求期，
        // 否则「无自带类型头的 HttpContent」会落到「不补头部」的分支。
        using var client = new AxiosClient(new AxiosConfig { ContentType = "application/xml" });
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("<a/>"));

        await client.PostAsync(server.Url + "up", content);

        Assert.Equal("application/xml", server.Received[0].Headers["content-type"]);
    }

    // ---------------------------------------------------------------
    // 上限边界值：恰好等于上限必须放行，超一字节必须拒绝
    // ---------------------------------------------------------------

    [Fact]
    public async Task MaxBodyLength_ExactlyAtLimit_SendsBody()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        const string body = """{"name":"bob"}""";
        var limit = Encoding.UTF8.GetByteCount(body);

        await client.PostAsync(server.Url + "echo", new { name = "bob" }, c => c.Limits.MaxBodyLength = limit);

        Assert.Equal(body, Encoding.UTF8.GetString(server.Received[0].Body));
    }

    [Fact]
    public async Task MaxBodyLength_OneByteOverLimit_ThrowsBeforeSending()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        const string body = """{"name":"bob"}""";
        var limit = Encoding.UTF8.GetByteCount(body) - 1;

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.PostAsync(server.Url + "echo", new { name = "bob" }, c => c.Limits.MaxBodyLength = limit));

        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);
        Assert.Empty(server.Received);
    }

    // ------------------------------------------------------------------
    // 无正文时的 Content-* 头
    // ------------------------------------------------------------------

    [Fact]
    public async Task ContentTypeHeader_WithoutBody_StillReachesTheServer()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        // 之前 Content-Type 落不进请求头（HttpRequestHeaders 直接拒收），被静默丢掉
        await client.PostAsync(
            server.Url + "bodyless",
            (object?)null,
            c => c.Headers.Set("Content-Type", "application/json"));

        Assert.Equal("application/json", server.Received[0].Headers.GetValueOrDefault("content-type"));
        Assert.Equal("0", server.Received[0].Headers.GetValueOrDefault("content-length"));
    }

    [Fact]
    public async Task ContentDispositionHeader_WithoutBody_StillReachesTheServer()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.PutAsync(
            server.Url + "empty",
            (object?)null,
            c => c.Headers.Set("Content-Disposition", "attachment; filename=a.txt"));

        Assert.Equal("attachment; filename=a.txt", server.Received[0].Headers.GetValueOrDefault("content-disposition"));
    }

    [Fact]
    public async Task ExplicitContentLength_WithoutBody_IsNotApplied()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        // 没有正文却写死 Content-Length: 5，会让连接一直等一个永远不来的正文；
        // 这里保持「无正文时不接受该头」，请求必须正常完成。
        await client.PostAsync(
            server.Url + "bodyless",
            (object?)null,
            c => c.Headers.Set("Content-Length", "5"));

        Assert.Equal("0", server.Received[0].Headers.GetValueOrDefault("content-length"));
    }

    [Fact]
    public async Task GetWithBody_ContentTypeHeader_IsStillOverridable()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        await client.GetAsync(
            server.Url + "with-body",
            "payload",
            c => c.Headers.Set("Content-Type", "text/plain; charset=utf-8"));

        var request = server.Received[0];
        Assert.Equal("text/plain; charset=utf-8", request.Headers.GetValueOrDefault("content-type"));
        Assert.Equal("payload", Encoding.UTF8.GetString(request.Body));
    }

    // ------------------------------------------------------------------
    // 手写 Content-Length
    // ------------------------------------------------------------------

    [Fact]
    public async Task ContentLengthHeader_MismatchingBody_ThrowsBadOptionValue()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        // 之前会一路发出去：服务端等 999 字节、只收到 5 字节，调用方最后只拿到一句 ERR_NETWORK
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(
            server.Url + "echo",
            "hello",
            c => c.Headers.Set("Content-Length", "999")));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("Content-Length", ex.Message);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task ContentLengthHeader_MatchingBody_IsAccepted()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        // 与真实长度一致时不是错误（等价于没写），不能因为「多写了一行」就拒绝
        var response = await client.PostAsync(
            server.Url + "echo",
            "hello",
            c => c.Headers.Set("Content-Length", "5"));

        Assert.Equal(200, response.Status);
        Assert.Equal("hello", Encoding.UTF8.GetString(server.Received[0].Body));
    }

    [Fact]
    public async Task ContentLengthHeader_ForNonSeekableStream_IsHonored()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();
        var payload = Encoding.UTF8.GetBytes("streamed-body");

        // 非 seekable 流的内容长度无法预知：手写的 Content-Length 是「已知长度但要关掉 chunked」的
        // 逃生口，必须原样保留（也正因为如此，长度已知时的校验不能用同一条规则一刀切）。
        await client.PostAsync(server.Url + "stream", new NonSeekableStream(payload), c =>
        {
            c.ContentType = "application/octet-stream";
            c.Headers.Set("Content-Length", payload.Length.ToString(CultureInfo.InvariantCulture));
        });

        var sent = server.Received[0];
        Assert.Equal(payload, sent.Body);
        Assert.Equal(payload.Length.ToString(CultureInfo.InvariantCulture), sent.Headers.GetValueOrDefault("Content-Length"));
    }

    [Fact]
    public async Task MaxBodyLength_ExceedsLimit_Throws()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(
            server.Url + "echo",
            new { name = new string('x', 4096) },
            c => c.Limits.MaxBodyLength = 512));

        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);
        Assert.Contains("maxBodyLength", ex.Message);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task MaxBodyLength_WithinLimit_SendsBody()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        await client.PostAsync(server.Url + "echo", new { name = "bob" }, c => c.Limits.MaxBodyLength = 4096);

        Assert.Equal("""{"name":"bob"}""", Encoding.UTF8.GetString(server.Received[0].Body));
    }

    // ------------------------------------------------------------------
    // L7：Memory<byte> / ArraySegment<byte> / ReadOnlyMemory<byte> 请求体
    // ------------------------------------------------------------------

    [Fact]
    public async Task MemoryByte_RequestBody_IsSentAsRawBytes()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();
        var payload = new byte[] { 0, 1, 2, 250, 255 };

        await client.PostAsync(server.Url + "bin", new Memory<byte>(payload), c => c.ContentType = "application/octet-stream");

        var sent = server.Received[0];
        Assert.Equal("application/octet-stream", sent.Headers.GetValueOrDefault("Content-Type"));
        Assert.Equal("5", sent.Headers.GetValueOrDefault("Content-Length"));
        Assert.Equal(payload, sent.Body);
    }

    [Fact]
    public async Task ArraySegment_RequestBody_IsSentAsRawBytes()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "bin",
            new ArraySegment<byte>(new byte[] { 9, 8, 7, 6 }, 1, 2),
            c => c.ContentType = "application/octet-stream");

        Assert.Equal(new byte[] { 8, 7 }, server.Received[0].Body);
    }

    [Fact]
    public async Task ReadOnlyMemory_RequestBody_IsSentAsRawBytes()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();
        var payload = new byte[] { 4, 5, 6 };

        await client.PostAsync(
            server.Url + "bin",
            new ReadOnlyMemory<byte>(payload),
            c => c.ContentType = "application/octet-stream");

        Assert.Equal(payload, server.Received[0].Body);
    }

    [Fact]
    public async Task Post_Object_SendsJsonAndReceivesJsonRoundTrip()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        var person = new Person("bob", 30);
        var response = await client.PostAsync<Person>(server.Url + "echo", person);

        Assert.Equal(person, response.Data);
        var sent = server.Received[0];
        Assert.StartsWith("application/json", sent.Headers.GetValueOrDefault("content-type"));
        Assert.Contains("\"name\":\"bob\"", Encoding.UTF8.GetString(sent.Body));
    }

    [Fact]
    public async Task Post_Dictionary_WithFormContentType_SendsFormUrlEncoded()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req, contentType: "application/x-www-form-urlencoded"));
        using var client = new AxiosClient();

        var form = new Dictionary<string, object?>
        {
            ["name"] = "alice",
            ["role"] = "admin"
        };

        // 表单编码由 config.ContentType 显式指定，不再由「字典类型」推断
        await client.PostAsync(
            server.Url + "form",
            form,
            c => c.ContentType = AxiosConfig.FormUrlEncodedContentType);

        var sent = server.Received[0];
        Assert.StartsWith("application/x-www-form-urlencoded", sent.Headers.GetValueOrDefault("content-type"));
        Assert.Equal("name=alice&role=admin", Encoding.UTF8.GetString(sent.Body));
    }

    [Fact]
    public async Task Post_String_ContentTypeCanBeOverridden()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req, contentType: "text/plain; charset=utf-8"));
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "raw",
            "raw-text",
            c => c.Headers.Set("Content-Type", "text/plain; charset=utf-8"));

        var sent = server.Received[0];
        Assert.Equal("text/plain; charset=utf-8", sent.Headers.GetValueOrDefault("content-type"));
        Assert.Equal("raw-text", Encoding.UTF8.GetString(sent.Body));
    }

    [Fact]
    public async Task Post_String_WithRequestEncoding_UsesEncodingAndCharsetHeader()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req, contentType: "text/plain"));
        using var client = new AxiosClient();

        await client.PostAsync(server.Url + "raw", "你好", c => c.Serialization.RequestEncoding = Encoding.Unicode);

        var sent = server.Received[0];
        Assert.Equal("你好", Encoding.Unicode.GetString(sent.Body));
        Assert.Contains("charset=utf-16", sent.Headers.GetValueOrDefault("content-type"));
    }

    [Fact]
    public async Task Post_Dictionary_WithRequestEncoding_PercentEncodesWithThatEncoding()
    {
        await using var server = TestHttpServer.Start(req
            => TestJson.Echo(req, contentType: "application/x-www-form-urlencoded"));
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "form",
            new Dictionary<string, object?> { ["name"] = "é" },
            c =>
            {
                c.ContentType = AxiosConfig.FormUrlEncodedContentType;
                c.Serialization.RequestEncoding = Encoding.Latin1;
            });

        var sent = server.Received[0];
        // Latin1 下 'é' 为单字节 0xE9 -> %E9（默认 UTF-8 会是 %C3%A9）
        Assert.Equal("name=%E9", Encoding.ASCII.GetString(sent.Body));
        Assert.Contains("charset=iso-8859-1", sent.Headers.GetValueOrDefault("content-type"));
    }

    [Fact]
    public async Task Post_Dictionary_WithNonUtf8RequestEncoding_WritesAsciiEscapedBytes()
    {
        await using var server = TestHttpServer.Start(req
            => TestJson.Echo(req, contentType: "application/x-www-form-urlencoded"));
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "form",
            new Dictionary<string, object?> { ["k"] = "é" },
            c =>
            {
                c.ContentType = AxiosConfig.FormUrlEncodedContentType;
                c.Serialization.RequestEncoding = Encoding.Unicode;
            });

        var sent = server.Received[0];
        // UTF-16LE 下 'k' -> 6B 00、'é' -> E9 00，故转义为 "k%00=%E9%00"（服务端按 charset 反转义可还原）。
        // 关键点：转义结果恒为 ASCII，wire 上应是「每字符 1 字节」；
        // 不能被再按 UTF-16 编成双字节流（22 字节），那会破坏 form-urlencoded 格式。
        Assert.Equal("k%00=%E9%00", Encoding.ASCII.GetString(sent.Body));
        Assert.Equal(11, sent.Body.Length);
        Assert.Contains("charset=utf-16", sent.Headers.GetValueOrDefault("content-type"));
    }

    [Fact]
    public async Task Post_MultipartFormDataContent_PassesThroughUntouched()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req, contentType: req.Headers.GetValueOrDefault("content-type") ?? "application/json"));
        using var client = new AxiosClient();

        using var multipart = new MultipartFormDataContent("axios-net-boundary");
        multipart.Add(new StringContent("alice"), "name");
        multipart.Add(new ByteArrayContent(new byte[] { 9, 8, 7 }), "file", "data.bin");

        await client.PostAsync(server.Url + "upload", multipart, c => c.ResponseType = ResponseType.Text);

        var sent = server.Received[0];
        Assert.StartsWith("multipart/form-data", sent.Headers.GetValueOrDefault("content-type"));
        var rawMultipart = Encoding.UTF8.GetString(sent.Body);
        Assert.Contains("form-data", rawMultipart);
        Assert.Contains("name=name", rawMultipart);
        Assert.Contains("alice", rawMultipart);
        Assert.Contains("data.bin", rawMultipart);
    }

    [Fact]
    public async Task DefaultContentType_IsJson_ForEveryStructuredShape()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        object dynamicPayload = new ExpandoObject();
        ((IDictionary<string, object?>)dynamicPayload)["name"] = "bob";

        await client.PostAsync(server.Url + "object", new { name = "bob" });
        await client.PostAsync(server.Url + "dictionary", new Dictionary<string, object?> { ["name"] = "bob" });
        await client.PostAsync(server.Url + "dynamic", dynamicPayload);
        await client.PostAsync(server.Url + "list", new[] { new { name = "bob" } });

        Assert.All(server.Received, request => Assert.Equal("application/json", ContentType_RequestBody(request)));
        Assert.Equal("""{"name":"bob"}""", Body_RequestBody(server.Received[0]));
        Assert.Equal("""{"name":"bob"}""", Body_RequestBody(server.Received[1]));
        Assert.Equal("""{"name":"bob"}""", Body_RequestBody(server.Received[2]));
        Assert.Equal("""[{"name":"bob"}]""", Body_RequestBody(server.Received[3]));
    }

    [Fact]
    public async Task DefaultContentType_IsJson_EvenForDictionary_NoImplicitFormEncoding()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        await client.PostAsync(server.Url + "form", new Dictionary<string, object?>
        {
            ["name"] = "alice",
            ["role"] = "admin"
        });

        var sent = server.Received[0];
        Assert.Equal("application/json", ContentType_RequestBody(sent));
        Assert.Equal("""{"name":"alice","role":"admin"}""", Body_RequestBody(sent));
    }

    [Fact]
    public async Task ContentType_InstanceDefault_Applies_AndPerRequestOverrideWins()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            ContentType = AxiosConfig.FormUrlEncodedContentType
        });

        var form = new Dictionary<string, object?> { ["name"] = "alice" };

        // 实例默认：表单
        await client.PostAsync("default", form);
        // 按请求覆盖：JSON
        await client.PostAsync("override", form, c => c.ContentType = AxiosConfig.JsonContentType);

        Assert.Equal("application/x-www-form-urlencoded", ContentType_RequestBody(server.Received[0]));
        Assert.Equal("name=alice", Body_RequestBody(server.Received[0]));

        Assert.Equal("application/json", ContentType_RequestBody(server.Received[1]));
        Assert.Equal("""{"name":"alice"}""", Body_RequestBody(server.Received[1]));
    }

    [Fact]
    public async Task ContentType_WithExplicitCharset_IsNotRewritten()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "form",
            new Dictionary<string, object?> { ["name"] = "é" },
            c =>
            {
                c.ContentType = "application/x-www-form-urlencoded; charset=iso-8859-1";
                c.Serialization.RequestEncoding = Encoding.Latin1;
            });

        var sent = server.Received[0];
        Assert.Equal("application/x-www-form-urlencoded; charset=iso-8859-1", ContentType_RequestBody(sent));
        Assert.Equal("name=%E9", Encoding.ASCII.GetString(sent.Body));
    }

    [Fact]
    public async Task NonJsonTextContentType_KeepsStructuredBodyAsJsonText()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        await client.PostAsync(server.Url + "xml", new { name = "bob" }, c => c.ContentType = "text/plain");

        var sent = server.Received[0];
        Assert.Equal("text/plain", ContentType_RequestBody(sent));
        Assert.Equal("""{"name":"bob"}""", Body_RequestBody(sent));
    }

    [Fact]
    public async Task BinaryBody_UsesConfiguredContentType_NotTheJsonDefault()
    {
        var payload = new byte[] { 0, 1, 2, 250, 255 };
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        await client.PostAsync(server.Url + "bin", payload, c => c.ContentType = "application/octet-stream");

        var sent = server.Received[0];
        Assert.Equal("application/octet-stream", ContentType_RequestBody(sent));
        Assert.Equal(payload, sent.Body);
    }

    [Fact]
    public async Task HttpContentPassthrough_KeepsItsOwnContentType()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        using var raw = new StringContent("hello", Encoding.UTF8, "text/plain");
        await client.PostAsync(server.Url + "raw", raw, c => c.ContentType = AxiosConfig.JsonContentType);

        var sent = server.Received[0];
        Assert.Equal("text/plain; charset=utf-8", ContentType_RequestBody(sent));
        Assert.Equal("hello", Body_RequestBody(sent));
    }

    [Fact]
    public async Task StringBody_IsNotParsed_AheadOfConfiguredContentType()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        await client.PostAsync(server.Url + "raw", """{"already":"json"}""");

        var sent = server.Received[0];
        Assert.Equal("application/json", ContentType_RequestBody(sent));
        Assert.Equal("""{"already":"json"}""", Body_RequestBody(sent));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    [InlineData("OPTIONS")]
    [InlineData("HEAD")]
    public async Task EveryMethod_AcceptsBodyOfAnyShape(string method)
    {
        await using var server = TestHttpServer.Start(req => req.Method == "HEAD"
            ? new TestResponse(204, "No Content")
            : TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        switch (method)
        {
            case "GET":
                await client.GetAsync(server.Url + "x", new { name = "bob" });
                await client.GetAsync<JsonElement>(server.Url + "x", new Dictionary<string, object?> { ["name"] = "bob" });
                break;
            case "DELETE":
                await client.DeleteAsync(server.Url + "x", new { name = "bob" });
                await client.DeleteAsync(server.Url + "x", "raw-text");
                break;
            case "OPTIONS":
                await client.OptionsAsync(server.Url + "x", new { name = "bob" });
                await client.OptionsAsync(server.Url + "x", new byte[] { 1, 2, 3 });
                break;
            case "HEAD":
                await client.HeadAsync(server.Url + "x", new { name = "bob" });
                break;
        }

        Assert.All(server.Received, request => Assert.Equal(method, request.Method));
        Assert.All(server.Received, request => Assert.True(request.Body.Length > 0));

        var first = server.Received[0];
        Assert.Equal("application/json", ContentType_RequestBody(first));
        Assert.Equal("""{"name":"bob"}""", Body_RequestBody(first));
    }

    [Fact]
    public async Task GetAsync_WithStreamBody_IsSupported()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("stream-body"));
        await client.GetAsync(server.Url + "x", stream);

        Assert.Equal("GET", server.Received[0].Method);
        Assert.Equal("stream-body", Body_RequestBody(server.Received[0]));
    }

    // ------------------------------------------------------------------
    // Content-Type 预置常量：目标框架是 net6.0，BCL 的 MediaTypeNames 在那里
    // 还没有 multipart / form-urlencoded（.NET 8 才补齐），所以库自带一套。
    //
    // 刻意把「常量的值」和「常量的行为」分开验：
    // - 值用字面量断言（唯一允许引用常量自身以外写法的地方），常量写错时立刻红；
    // - 行为用「字面量 vs 常量」成对比较，证明两者等价。
    // 本文件其余用例继续用字面量 —— 断言必须独立于被测常量，否则常量写错时
    // 测试会跟着一起绿（同源断言）。
    // ------------------------------------------------------------------

    [Fact]
    public void ContentTypeConstants_AreWireValues_AndBackwardCompatible()
    {
        Assert.Equal("application/json", AxiosContentType.Json);
        Assert.Equal("application/x-www-form-urlencoded", AxiosContentType.FormUrlEncoded);
        Assert.Equal("multipart/form-data", AxiosContentType.MultipartFormData);
        Assert.Equal("application/octet-stream", AxiosContentType.OctetStream);
        Assert.Equal("application/xml", AxiosContentType.Xml);
        Assert.Equal("text/plain", AxiosContentType.TextPlain);
        Assert.Equal("application/problem+json", AxiosContentType.ProblemJson);

        // 旧常量必须与新类同值：已有用户代码直接引用了它们
        Assert.Equal(AxiosContentType.Json, AxiosConfig.JsonContentType);
        Assert.Equal(AxiosContentType.FormUrlEncoded, AxiosConfig.FormUrlEncodedContentType);
    }

    [Fact]
    public async Task ContentTypeConstants_BehaveExactlyLikeTheirLiteralForms()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        var payload = new { a = "1 2" };

        // 同一份数据，分别用字面量与常量发送：正文与头部必须逐字节一致
        await client.PostAsync(server.Url + "f1", payload, c => c.ContentType = "application/x-www-form-urlencoded");
        await client.PostAsync(server.Url + "f2", payload, c => c.ContentType = AxiosContentType.FormUrlEncoded);
        Assert.Equal(Body_RequestBody(server.Received[0]), Body_RequestBody(server.Received[1]));
        Assert.Equal(ContentType_RequestBody(server.Received[0]), ContentType_RequestBody(server.Received[1]));
        Assert.Equal("a=1+2", Body_RequestBody(server.Received[1]));

        await client.PostAsync(server.Url + "j1", payload, c => c.ContentType = "application/json");
        await client.PostAsync(server.Url + "j2", payload, c => c.ContentType = AxiosContentType.Json);
        Assert.Equal(Body_RequestBody(server.Received[2]), Body_RequestBody(server.Received[3]));
        Assert.Equal(ContentType_RequestBody(server.Received[2]), ContentType_RequestBody(server.Received[3]));

        // multipart：boundary 随机，固定住才能逐字节比较
        await client.PostAsync(server.Url + "m1", payload, c => c.ContentType = "multipart/form-data; boundary=----fixed");
        await client.PostAsync(server.Url + "m2", payload, c => c.ContentType = AxiosContentType.MultipartFormData + "; boundary=----fixed");
        Assert.Equal(Body_RequestBody(server.Received[4]), Body_RequestBody(server.Received[5]));
        Assert.Equal(ContentType_RequestBody(server.Received[4]), ContentType_RequestBody(server.Received[5]));
        // 纯 token 的字段名不加引号：Content-Disposition: form-data; name=a
        Assert.Contains("form-data; name=a", Body_RequestBody(server.Received[5]));

        await client.PostAsync(server.Url + "b1", new byte[] { 1, 2 }, c => c.ContentType = "application/octet-stream");
        await client.PostAsync(server.Url + "b2", new byte[] { 1, 2 }, c => c.ContentType = AxiosContentType.OctetStream);
        Assert.Equal(server.Received[6].Body, server.Received[7].Body);
        Assert.Equal(ContentType_RequestBody(server.Received[6]), ContentType_RequestBody(server.Received[7]));
    }

    private static TestResponse Json(string body = "{\"ok\":true}") => new(Body: Encoding.UTF8.GetBytes(body), ContentType: "application/json; charset=utf-8");

    private static string Body(TestRequest request) => Encoding.UTF8.GetString(request.Body);

    private static string ContentType(TestRequest request)
        => request.Headers.GetValueOrDefault("content-type") ?? string.Empty;

    // ===============================================================
    // 测试用请求模型 / 序列化器
    // ===============================================================


    // ===============================================================
    // 测试夹具
    // ===============================================================

    /// <summary>读到正文时写入一个「写就失败」的流，等价于连接在发送正文途中被断开。</summary>
    private sealed class FailingBodyWriteHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                await request.Content.CopyToAsync(new FailingWriteStream(), cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class FailingWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new IOException("发送正文时连接被断开（模拟）");

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromException(new IOException("发送正文时连接被断开（模拟）"));

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException(new IOException("发送正文时连接被断开（模拟）"));
    }


    /// <summary>
    /// 分块写出、块间带 60ms 间隔的正文：让「传满声明长度即锁死」的判定在重发轮次里
    /// 必然产生一次非 force 的上报（而不是靠最终 Complete 的强制上报掩盖问题）。
    /// </summary>
    private sealed class SlowChunkedContent : HttpContent
    {
        private readonly int _chunks;
        private readonly int _chunkSize;

        public SlowChunkedContent(int chunks, int chunkSize)
        {
            _chunks = chunks;
            _chunkSize = chunkSize;
            Headers.ContentLength = (long)chunks * chunkSize;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => await SerializeToStreamAsync(stream, context, CancellationToken.None).ConfigureAwait(false);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[_chunkSize];
            for (var i = 0; i < _chunks; i++)
            {
                await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                await Task.Delay(60, cancellationToken).ConfigureAwait(false);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = (long)_chunks * _chunkSize;
            return true;
        }
    }

    // ------------------------------------------------------------------
    // 辅助
    // ------------------------------------------------------------------


    private static async Task DrainAsync<T>(IAsyncEnumerable<T>? source)
    {
        if (source is null)
        {
            return;
        }

        await foreach (var _ in source.ConfigureAwait(false))
        {
        }
    }

    private static int ActiveLeases(HttpConnectionPool pool)
        => pool.GetStatistics().Endpoints.Sum(e => e.ActiveLeases);

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(token, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    /// <summary>每次都返回同一段 NDJSON 正文的处理器。</summary>
    private sealed class FixedBodyHandler : HttpMessageHandler
    {
        private readonly byte[] _body;

        public FixedBodyHandler(byte[] body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(_body))
            };
            response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/x-ndjson");
            return Task.FromResult(response);
        }
    }

    private sealed class Node
    {
        public Node? Self { get; set; }
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

    private static TestResponse EchoJson(TestRequest req) => TestJson.Echo(req);

    private static string Body_RequestBody(TestRequest request) => Encoding.UTF8.GetString(request.Body);

    private static string? ContentType_RequestBody(TestRequest request) => request.Headers.GetValueOrDefault("content-type");


}