
using AxiosNet.Internal;
using AxiosNet.Pooling;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using System.Collections;
using System.Collections.Immutable;
using System.Dynamic;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Text;

namespace AxiosNet.Tests.Requests;

/// <summary>
/// 表单与文件上传：urlencoded 展平、multipart 组装、FormFile 所有权与字段名校验。
/// </summary>
public sealed class FormAndMultipartTests
{
    [Fact]
    public void FormFile_FromPath_InvalidFileName_DoesNotLeakFileHandle()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".txt");
        File.WriteAllText(path, "payload");

        try
        {
            // 双引号会破坏 Content-Disposition 的 quoted-string。
            Assert.Throws<ArgumentException>(() => FormFile.FromPath(path, "bad\"name.txt", "text/plain"));

            // 句柄已释放：能以独占方式打开。
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
            }

            // 也能删除。
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task TransformResponse_Throws_IsWrappedAsAxiosException()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes("{\"a\":1}"), ContentType: "application/json; charset=utf-8"));

        using var client = new AxiosClient();

        var error = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<object>(server.Url + "item", c => c.TransformResponse.Add((_, _) => throw new InvalidOperationException("boom"))));

        Assert.Equal(AxiosException.ErrBadResponse, error.Code);
        Assert.IsType<InvalidOperationException>(error.Cause);
    }

    [Fact]
    public async Task TransformResponse_SuccessPath_StillTransforms()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes("{\"a\":1}"), ContentType: "application/json; charset=utf-8"));

        using var client = new AxiosClient();

        var response = await client.GetAsync<string>(server.Url + "item", c => c.TransformResponse.Add((_, _) => "unwrapped"));

        Assert.Equal("unwrapped", response.Data);
    }

    [Fact]
    public async Task XmlSerializer_Declaration_MatchesRequestEncoding()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes("{}"), ContentType: "application/json; charset=utf-8"));

        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = new()
            {
                ContentSerializer = XmlContentSerializer.Instance,
            },
        });

        await client.PostAsync(server.Url + "xml", new XmlProbeDto { Name = "中文" }, c => c.Serialization.RequestEncoding = Encoding.Unicode);

        var request = server.Received[0];

        // 字节确实是 UTF-16LE（'<' 0x3C 后跟 0x00）。
        Assert.True(request.Body.Length > 1);
        Assert.Equal(0x3C, request.Body[0]);
        Assert.Equal(0x00, request.Body[1]);

        var declaration = Encoding.Unicode.GetString(request.Body, 0, Math.Min(request.Body.Length, 96));
        Assert.Contains("encoding=\"utf-16\"", declaration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("charset=utf-16", request.Headers["Content-Type"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void XmlSerializer_WithoutRequestEncoding_DeclaresUtf8()
    {
        var text = XmlContentSerializer.Instance.Serialize(new XmlProbeDto { Name = "abc" }, typeof(XmlProbeDto), new AxiosConfig());

        Assert.Contains("encoding=\"utf-8\"", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetUri_HostLikeRelativeUrl_IsCombinedWithBaseAddress()
    {
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://example.com/api" });

        // isAbsoluteURL 要求 scheme://，host:port 形态属相对路径。
        Assert.Equal("http://example.com/api/localhost:8080/items", client.GetUri(new AxiosConfig { Url = "localhost:8080/items" }));
    }

    [Fact]
    public void GetUri_AbsoluteUrl_StillIgnoresBaseAddress()
    {
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://example.com/api" });

        Assert.Equal("https://other.test/x?q=1", client.GetUri(new AxiosConfig { Url = "https://other.test/x?q=1" }));
    }

    [Fact]
    public void GetUri_FileScheme_IsStillRejected()
    {
        using var client = new AxiosClient();

        var error = Assert.Throws<AxiosException>(() => client.GetUri(new AxiosConfig { Url = "file:///etc/passwd" }));
        Assert.Equal(AxiosException.ErrInvalidUrl, error.Code);
    }

    [Fact]
    public void FormFile_FromPath_TwoArguments_KeepsContentTypeSemantics()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".txt");
        File.WriteAllText(path, "payload");

        try
        {
            // 两参的第二个参数是 Content-Type，文件名取路径原名。
            var file = FormFile.FromPath(path, "application/pdf");

            Assert.Equal(Path.GetFileName(path), file.FileName);
            Assert.Equal("application/pdf", file.ContentType);

            file.Content.Dispose();

            // 误用不会被拦：第二个字符串原样成为 Content-Type。
            var misused = FormFile.FromPath(path, "report.txt");
            Assert.Equal("report.txt", misused.ContentType);
            Assert.Equal(Path.GetFileName(path), misused.FileName);
            misused.Content.Dispose();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FormFile_FromPath_ThreeArguments_SetsFileName()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".txt");
        File.WriteAllText(path, "payload");

        try
        {
            var file = FormFile.FromPath(path, "report.txt", "text/plain");

            Assert.Equal("report.txt", file.FileName);
            Assert.Equal("text/plain", file.ContentType);

            file.Content.Dispose();
        }
        finally
        {
            File.Delete(path);
        }
    }

    // 这三个头由 BCL 归为内容头，必须送达（代价是附带一个空正文）。
    [Theory]
    [InlineData("Expires", "Wed, 21 Oct 2026 07:28:00 GMT")]
    [InlineData("Last-Modified", "Wed, 21 Oct 2026 07:28:00 GMT")]
    [InlineData("Allow", "GET, POST")]
    public async Task Get_WithContentModeledResponseHeader_DeliversHeader(string name, string value)
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes("{}"), ContentType: "application/json; charset=utf-8"));

        using var client = new AxiosClient();

        await client.GetAsync(server.Url + "x", c => c.Headers.Set(name, value));

        var request = server.Received[0];
        Assert.Equal(value, request.Headers[name]);
    }

    [Fact]
    public async Task Get_WithPlainCustomHeader_SendsNoBody()
    {
        // 普通自定义头走请求头，不会为它造出正文。
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes("{}"), ContentType: "application/json; charset=utf-8"));

        using var client = new AxiosClient();

        await client.GetAsync(server.Url + "x", c => c.Headers.Set("X-Trace", "1"));

        var request = server.Received[0];
        Assert.Equal("1", request.Headers["X-Trace"]);
        Assert.Empty(request.Body);
        Assert.False(request.Headers.ContainsKey("Content-Length"));
    }

    [Fact]
    public async Task Get_WithContentHeader_StillAllowedToCreateBody()
    {
        // 真正的 entity header 仍按原语义落到正文上。
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: Encoding.UTF8.GetBytes("{}"), ContentType: "application/json; charset=utf-8"));

        using var client = new AxiosClient();

        await client.GetAsync(server.Url + "x", c => c.Headers.Set("Content-Language", "zh-CN"));

        var request = server.Received[0];
        Assert.Equal("zh-CN", request.Headers["Content-Language"]);
    }

    [Fact]
    public async Task FormUrlEncoded_SelfReferencingGraph_ThrowsAxiosException()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        var node = new Node { Name = "root" };
        node.Children.Add(node);

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(server.Url + "up", node, c => c.ContentType = "application/x-www-form-urlencoded"));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("循环引用", ex.Message);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task FormUrlEncoded_MutualReference_ThrowsAxiosException()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        var a = new Node { Name = "a" };
        var b = new Node { Name = "b" };
        a.Children.Add(b);
        b.Children.Add(a);

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(server.Url + "up", a, c => c.ContentType = "application/x-www-form-urlencoded"));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Multipart_SelfReferencingGraph_ThrowsAxiosException()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        var node = new Node { Name = "root" };
        node.Children.Add(node);

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostFormAsync(server.Url + "up", node));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task FormUrlEncoded_SharedChildObject_IsNotTreatedAsCycle()
    {
        // 同一个子对象出现在两个分支（DAG 而非环）：路径式环检测放行，展平两次。
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        var shared = new Leaf { Value = "v" };
        var root = new Pair { Left = shared, Right = shared };

        await client.PostAsync(server.Url + "up", root, c => c.ContentType = "application/x-www-form-urlencoded");

        // 表单字段名用属性名原样。
        Assert.Equal("Left[Value]=v&Right[Value]=v", Body(server.Received[0]));
    }

    [Fact]
    public async Task FormUrlEncoded_DeeplyNestedGraph_StillFlattens()
    {
        // 合法深层嵌套（10 层）不受环检测影响。
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        var root = new Node { Name = "0" };
        var current = root;
        for (var i = 1; i <= 10; i++)
        {
            var next = new Node { Name = i.ToString() };
            current.Children.Add(next);
            current = next;
        }

        await client.PostAsync(server.Url + "up", root, c => c.ContentType = "application/x-www-form-urlencoded");

        // 嵌套数组用 a[] 形式。
        Assert.Contains("Children[][Name]=1", Body(server.Received[0]));
    }

    [Fact]
    public async Task ReadOnlyDictionary_FormUrlEncoded_FlattensAsKeyValuePairs()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        IReadOnlyDictionary<string, object> data = new ReadOnlyStringMap { { "a", 1 }, { "b", "x" } };

        await client.PostAsync(server.Url + "up", data, c => c.ContentType = "application/x-www-form-urlencoded");

        Assert.Equal("a=1&b=x", Body(server.Received[0]));
    }

    [Fact]
    public async Task ReadOnlyDictionary_Multipart_FlattensAsNamedFields()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        IReadOnlyDictionary<string, object> data = new ReadOnlyStringMap { { "a", 1 } };

        await client.PostFormAsync(server.Url + "up", data);

        // 简单 token 的 disposition 不带引号。
        Assert.Contains("name=a", Body(server.Received[0]));
        Assert.DoesNotContain("a[Key]", Body(server.Received[0]));
    }

    [Fact]
    public async Task ImmutableDictionary_FormUrlEncoded_StillHitsGenericDictionaryBranch()
    {
        // 同时实现 IDictionary<string,object> 与 IReadOnlyDictionary 时走通用字典分支。
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        IReadOnlyDictionary<string, object> data = ImmutableDictionary.CreateRange(
            new[] { new KeyValuePair<string, object>("a", 1) });

        await client.PostAsync(server.Url + "up", data, c => c.ContentType = "application/x-www-form-urlencoded");

        Assert.Equal("a=1", Body(server.Received[0]));
    }

    [Fact]
    public async Task FormUrlEncoded_ByteBuffers_AreBase64()
    {
        // urlencoded 没有二进制表示，四种字节容器都必须折成 base64（可逆），不能落到 ToString()
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        var data = new
        {
            plain = new byte[] { 1, 2, 3 },
            readOnly = new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3 }),
            writable = new Memory<byte>(new byte[] { 1, 2, 3 }),
            segment = new ArraySegment<byte>(new byte[] { 0, 1, 2, 3 }, 1, 3)
        };

        await client.PostAsync(server.Url + "up", data, c => c.ContentType = "application/x-www-form-urlencoded");

        Assert.Equal("plain=AQID&readOnly=AQID&writable=AQID&segment=AQID", Body(server.Received[0]));
    }

    [Fact]
    public async Task FormUrlEncoded_NonGenericDictionary_FlattensWithStringifiedKeys()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        var nested = new Hashtable { ["kind"] = "book", [42] = "number-key" };

        await client.PostAsync(server.Url + "up", nested, c => c.ContentType = "application/x-www-form-urlencoded");

        var body = Body(server.Received[0]);
        Assert.Contains("kind=book", body);
        Assert.Contains("42=number-key", body);
    }

    [Fact]
    public async Task FormUrlEncoded_JsonIgnoredProperty_IsSkipped()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "up",
            new FormWithIgnoredMember { Keep = "v", Drop = "hidden" },
            c => c.ContentType = "application/x-www-form-urlencoded");

        Assert.Equal("Keep=v", Body(server.Received[0]));
    }

    [Fact]
    public async Task FormUrlEncoded_JsonElementWithNoKey_IsIgnored()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        using var scalar = JsonDocument.Parse("5");
        using var nullValue = JsonDocument.Parse("""{"keep":"v","gone":null}""");

        // 根标量没有可表达的键名：不伪造 key；嵌套 null 直接跳过
        await client.PostAsync(server.Url + "up", scalar.RootElement.Clone(), c => c.ContentType = "application/x-www-form-urlencoded");
        Assert.Equal(string.Empty, Body(server.Received[0]));

        await client.PostAsync(server.Url + "up", nullValue.RootElement.Clone(), c => c.ContentType = "application/x-www-form-urlencoded");
        Assert.Equal("keep=v", Body(server.Received[1]));
    }

    [Fact]
    public async Task FormUrlEncoded_NonUtf8Encoding_EncodesBytesNotCharacters()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        // UTF-16 下空格仍是单字节 0x20 → '+'，其余字节逐个转义（百分号编码作用于字节流）
        await client.PostAsync(
            server.Url + "up",
            new { v = "a b" },
            c =>
            {
                c.ContentType = "application/x-www-form-urlencoded";
                c.Serialization.RequestEncoding = Encoding.Unicode;
            });

        // 键名与值都按字节流编码：UTF-16 下每个字符都带一个 0x00 字节
        Assert.Equal("v%00=a%00+%00b%00", Body(server.Received[0]));
    }

    [Fact]
    public async Task FormUrlEncoded_Utf8_SpecialCharacters_FollowEscapeDataStringRules()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        // 保留字符逐字节转义、空格转 '+'、~ 不转义（RFC 3986 unreserved）。
        await client.PostAsync(
            server.Url + "up",
            new { v = "a+b c&d=e/f" },
            c => c.ContentType = "application/x-www-form-urlencoded");
        Assert.Equal("v=a%2Bb+c%26d%3De%2Ff", Body(server.Received[0]));

        await client.PostAsync(
            server.Url + "up",
            new { v = "!*()'~" },
            c => c.ContentType = "application/x-www-form-urlencoded");
        Assert.Equal("v=%21%2A%28%29%27~", Body(server.Received[1]));

        // 键名里的方括号保留（axios 口径）。
        await client.PostAsync(
            server.Url + "up",
            new { k = new { inner = 1 } },
            c => c.ContentType = "application/x-www-form-urlencoded");
        Assert.Equal("k[inner]=1", Body(server.Received[2]));
    }

    [Fact]
    public void FormUrlEncoded_Utf8_MatchesEscapeDataString_ForEveryAsciiAndUnicodeSample()
    {
        // 新单趟编码器与旧实现（EscapeDataString + 空格替换）逐字符对拍：全部 ASCII 可打印字符 + 典型样本。
        var mismatches = new List<string>();

        void Check(string value)
        {
            var actual = EncodeForm(new { v = value }, Encoding.UTF8);
            var expected = "v=" + Uri.EscapeDataString(value).Replace("%20", "+");
            if (actual != expected)
            {
                mismatches.Add($"'{value}': '{actual}' vs '{expected}'");
            }
        }

        for (var c = ' '; c <= '~'; c++)
        {
            Check(c.ToString());
        }

        foreach (var sample in new[] { "", " ", "  ", "+", "a+b c", "你好", "😀", "\u00a0", "\u201c", "%25", "[]", "a[b]", "\t\r\n", "~!*'()" })
        {
            Check(sample);
        }

        Assert.Empty(mismatches);
    }

    [Fact]
    public void FormUrlEncoded_NonUtf8_MatchesLegacyByteWiseEncoding()
    {
        // 非 UTF-8 编码逐字节转义：与旧的「GetBytes 再逐字节判断」实现一致（键名保留方括号）。
        foreach (var encoding in new[] { Encoding.Latin1, Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.ASCII })
        {
            foreach (var value in new[] { "", "a b", "café", "你好", "~!*'()[]", "😀" })
            {
                var expected = LegacyFormKey(encoding) + "=" + LegacyFormValue(value, encoding);

                Assert.Equal(expected, EncodeForm(new { v = value }, encoding));
            }
        }
    }

    /// <summary>旧实现：键名编码后再把方括号换回字面量。</summary>
    private static string LegacyFormKey(Encoding encoding) => LegacyFormValue("v", encoding).Replace("%5B", "[").Replace("%5D", "]");

    private static string LegacyFormValue(string value, Encoding encoding)
    {
        var builder = new StringBuilder();
        foreach (var b in encoding.GetBytes(value))
        {
            if (b == (byte)' ')
            {
                builder.Append('+');
            }
            else if ((b >= (byte)'A' && b <= (byte)'Z')
                || (b >= (byte)'a' && b <= (byte)'z')
                || (b >= (byte)'0' && b <= (byte)'9')
                || b is (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~')
            {
                builder.Append((char)b);
            }
            else
            {
                builder.Append('%').Append((b >> 4).ToString("X")).Append((b & 0xF).ToString("X"));
            }
        }

        return builder.ToString();
    }

    private static string EncodeForm(object data, Encoding encoding)
    {
        using var content = FormBodyEncoder.Create(data, new AxiosConfig(), "application/x-www-form-urlencoded", encoding, appendCharset: false);
        return content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    private sealed class FormWithIgnoredMember
    {
        public string Keep { get; set; } = string.Empty;

        [JsonIgnore]
        public string Drop { get; set; } = string.Empty;
    }

    // ------------------------------------------------------------------
    // multipart：字段名校验失败时，调用方的流必须被释放
    // ------------------------------------------------------------------

    [Fact]
    public void FormFile_InvalidFieldName_ThrowsAtConstruction()
    {
        // FieldName 与 FileName 一样落在 quoted-string 里：赋值处就拦，不把它拖到组装期
        var ex = Assert.Throws<ArgumentException>(() =>
            new FormFile("a.txt", new MemoryStream()) { FieldName = "bad\"name" });

        Assert.Contains("字段名", ex.Message);
    }

    [Theory]
    [InlineData("bad\"name")]
    [InlineData("bad\\name")]
    [InlineData("bad\r\nname")]
    [InlineData("bad\0name")]
    public void FormFile_InvalidFieldName_RejectsEveryDispositionSeparator(string fieldName)
    {
        Assert.Throws<ArgumentException>(() =>
            new FormFile("a.txt", new MemoryStream()) { FieldName = fieldName });
    }

    [Fact]
    public async Task Multipart_InvalidNestedFieldName_ReleasesCallerStream()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var stream = new MemoryStream(Encoding.UTF8.GetBytes("payload"));
        var payload = new Dictionary<string, object?> { ["bad\"name"] = stream };

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.PostAsync("upload", payload, c => c.ContentType = AxiosContentType.MultipartFormData));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);

        // 关键断言：部件没能加入容器，容器的 Dispose 覆盖不到它 —— 必须由组装方就地释放
        Assert.False(stream.CanRead);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Multipart_RootFormFileWithoutFieldName_ReportsBadOptionAndReleasesStream()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var stream = new MemoryStream(Encoding.UTF8.GetBytes("payload"));
        var file = new FormFile("a.txt", stream);

        // 根节点没有字段名：这是调用方的模型装配错误（ERR_BAD_OPTION_VALUE），
        // 不是「请求体序列化失败」（ERR_BAD_REQUEST）
        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.PostAsync("upload", file, c => c.ContentType = AxiosContentType.MultipartFormData));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.False(stream.CanRead);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Multipart_SuccessPath_StillReleasesCallerStream()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var stream = new MemoryStream(Encoding.UTF8.GetBytes("payload"));
        var file = new FormFile("a.txt", stream);

        await client.PostAsync(
            "upload",
            new { file },
            c => c.ContentType = AxiosContentType.MultipartFormData);

        Assert.False(stream.CanRead);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task Multipart_InvalidFieldNameOnFormFile_DoesNotReachAssembler()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        // 非法 FieldName 在构造期就被拒：既不会发出请求，也不会走到「已取得流所有权」的组装路径
        Assert.Throws<ArgumentException>(() =>
            new FormFile("a.txt", new MemoryStream()) { FieldName = "a;b\"c" });

        Assert.Empty(server.Received);
    }

    // ------------------------------------------------------------------
    // XML：声明里的 encoding 必须与头、正文三者一致
    // ------------------------------------------------------------------

    [Fact]
    public async Task Xml_ContentTypeCharset_DrivesDeclarationAndBody()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        await client.PostAsync(
            "xml",
            new XmlPayload { Name = "hello" },
            c =>
            {
                c.Serialization.ContentSerializer = XmlContentSerializer.Instance;
                c.ContentType = AxiosContentType.Xml + "; charset=iso-8859-1";
            });

        var request = server.Received[0];
        var document = Encoding.Latin1.GetString(request.Body);

        // 头、XML 声明、正文编码三者统一由 ContentType 的 charset 决定
        Assert.Contains("charset=iso-8859-1", request.Headers["Content-Type"]);
        Assert.Contains("encoding=\"iso-8859-1\"", document);
        Assert.Contains("<Name>hello</Name>", document);
    }

    [Fact]
    public async Task Xml_RequestEncoding_DrivesDeclarationWhenNoCharset()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        await client.PostAsync(
            "xml",
            new XmlPayload { Name = "hello" },
            c =>
            {
                c.Serialization.ContentSerializer = XmlContentSerializer.Instance;
                c.ContentType = AxiosContentType.Xml;
                c.Serialization.RequestEncoding = Encoding.Latin1;
            });

        var document = Encoding.Latin1.GetString(server.Received[0].Body);
        Assert.Contains("encoding=\"iso-8859-1\"", document);
        Assert.Contains("charset=iso-8859-1", server.Received[0].Headers["Content-Type"]);
    }

    [Fact]
    public async Task Xml_DefaultEncoding_RemainsUtf8()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        await client.PostAsync(
            "xml",
            new XmlPayload { Name = "中文" },
            c => c.Serialization.ContentSerializer = XmlContentSerializer.Instance);

        var request = server.Received[0];
        var document = Encoding.UTF8.GetString(request.Body);

        // 序列化器自报 media type：头是 application/xml，未显式设 RequestEncoding 时不追加 charset
        Assert.Equal(AxiosContentType.Xml, request.Headers["Content-Type"]);
        Assert.Contains("encoding=\"utf-8\"", document);
        Assert.Contains("中文", document);
    }

    // ===============================================================
    // 10. 表单通道承载文件部件：属于调用方配置错误，错误码应为 ERR_BAD_OPTION_VALUE
    //     （与 multipart 侧一致），不是 ERR_BAD_REQUEST。
    // ===============================================================

    [Fact]
    public async Task FormUrlEncoded_FilePart_ReportsOptionError()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.PostAsync(
                server.Url + "upload",
                new { file = new MemoryStream(new byte[4]) },
                c => c.ContentType = AxiosConfig.FormUrlEncodedContentType));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    // ---------------------------------------------------------------
    // 15：boundary 错误分类
    // ---------------------------------------------------------------

    [Fact]
    public async Task Multipart_BoundaryTooLong_ReportsOptionError()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        // RFC 2046 的 boundary 上限 70 字符：BCL 抛 ArgumentException。这是调用方的选项错误，
        // 必须报 ERR_BAD_OPTION_VALUE（构造在 try 外时会被 AxiosClient 收成 ERR_BAD_REQUEST）。
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(
            "upload",
            new { name = "alice" },
            c => c.ContentType = "multipart/form-data; boundary=" + new string('x', 80)));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("boundary", ex.Message);
        Assert.Empty(server.Received);   // 发送前就拦下
    }

    [Fact]
    public async Task Multipart_ValidCustomBoundary_StillWorks()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        await client.PostAsync(
            "upload",
            new { name = "alice" },
            c => c.ContentType = "multipart/form-data; boundary=----axios-net");

        var received = Assert.Single(server.Received);
        Assert.Contains("----axios-net", received.Headers["Content-Type"]);
        Assert.Contains("----axios-net", BodyOf(received));
    }

    [Fact]
    public async Task Multipart_LaterPartFails_StillReleasesUntouchedStreams()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        var untouched = new MemoryStream("payload"u8.ToArray());

        // SortedDictionary 的枚举顺序确定：先装配非法字段名（抛错），此时后面那个裸流还没被
        // 包成 StreamContent，容器 Dispose 覆盖不到它 —— 装配失败的回收必须走进原始数据。
        var data = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["bad\"name"] = 1,
            ["zzz"] = untouched,
        };

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.PostAsync("upload", data, c => c.ContentType = "multipart/form-data"));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.False(untouched.CanRead);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Multipart_WithContentTypeHeader_KeepsGeneratedBoundary()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "upload",
            new { title = "t", file = FormFile.FromBytes("a.txt", new byte[] { 1, 2, 3 }) },
            c =>
            {
                c.ContentType = "multipart/form-data";
                c.Headers.Set("Content-Type", "multipart/form-data");
            });

        var request = server.Received[0];
        Assert.Contains("boundary=", request.Headers["Content-Type"]);
        Assert.Contains("t", Encoding.UTF8.GetString(request.Body));
    }

    [Fact]
    public async Task Multipart_WithContentTypeHeaderAndProgressWrapper_KeepsBoundary()
    {
        // OnUploadProgress 会把 multipart 包进 ProgressHttpContent：boundary 保护必须穿透包装层。
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        var events = 0;
        await client.PostAsync(
            server.Url + "upload",
            new { title = "t", file = FormFile.FromBytes("a.txt", new byte[] { 1, 2, 3 }) },
            c =>
            {
                c.ContentType = "multipart/form-data";
                c.Headers.Set("Content-Type", "multipart/form-data");
                c.Callbacks.OnUploadProgress = _ => events++;
            });

        Assert.Contains("boundary=", server.Received[0].Headers["Content-Type"]);
        Assert.Contains("t", Encoding.UTF8.GetString(server.Received[0].Body));
        Assert.True(events > 0);
    }

    // ---------------------------------------------------------------
    // multipart / form：JSON 树（JsonElement）作为数据源
    // ---------------------------------------------------------------

    [Fact]
    public async Task Multipart_RootDictionaryWithJsonElementValues_SendsFields()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        using var title = JsonDocument.Parse("\"年报\"");
        using var count = JsonDocument.Parse("42");
        var data = new Dictionary<string, object?>
        {
            ["title"] = title.RootElement,
            ["count"] = count.RootElement
        };

        await client.PostAsync(server.Url + "upload", data, c => c.ContentType = "multipart/form-data");

        var body = Encoding.UTF8.GetString(server.Received[0].Body);
        Assert.Contains("name=title", body);
        Assert.Contains("年报", body);
        Assert.Contains("name=count", body);
        Assert.Contains("42", body);
    }

    [Fact]
    public async Task Multipart_JsonElementRoot_FlattensNestedObjectsAndArrays()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        using var document = JsonDocument.Parse("""{"a":{"b":"c"},"list":[1,2],"gone":null}""");

        await client.PostAsync(server.Url + "upload", document.RootElement, c => c.ContentType = "multipart/form-data");

        var body = Encoding.UTF8.GetString(server.Received[0].Body);
        // 部件名含 [] 时 BCL 会加引号，纯 token 名不加
        Assert.Contains("name=\"a[b]\"", body);
        Assert.Contains("c", body);
        Assert.Contains("name=list", body);
        Assert.Contains("1", body);
        Assert.Contains("2", body);
        Assert.DoesNotContain("gone", body);
    }

    [Fact]
    public async Task FormUrlEncoded_RootArray_UsesIndexKeys()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        await client.PostAsync(server.Url + "form", new[] { 1, 2, 3 },
            c => c.ContentType = AxiosConfig.FormUrlEncodedContentType);

        Assert.Equal("[0]=1&[1]=2&[2]=3", Encoding.UTF8.GetString(server.Received[0].Body));
    }

    [Fact]
    public async Task FormUrlEncoded_JsonElementRoot_FlattensLikeObject()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        using var document = JsonDocument.Parse("""{"a":1,"b":["x","y"]}""");

        await client.PostAsync(server.Url + "form", document.RootElement,
            c => c.ContentType = AxiosConfig.FormUrlEncodedContentType);

        Assert.Equal("a=1&b[]=x&b[]=y", Encoding.UTF8.GetString(server.Received[0].Body));
    }

    [Fact]
    public async Task FormUrlEncoded_RootScalar_IsSkippedInsteadOfInventingAKey()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        await client.PostAsync(server.Url + "form", 42,
            c => c.ContentType = AxiosConfig.FormUrlEncodedContentType);

        Assert.Empty(server.Received[0].Body);
    }

    // ------------------------------------------------------------------
    // HttpContent + multipart
    // ------------------------------------------------------------------

    [Fact]
    public async Task HttpContentWithoutContentType_UnderMultipartConfig_ThrowsBadOptionValue()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();
        using var body = new ByteArrayContent(new byte[] { 1, 2, 3 });

        // 头部声明 multipart、正文却没有任何 boundary → 服务端必然解析失败。
        // 之前只是把 mediaType 补到内容头上就发出去了（换一个看不懂的 400）。
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(
            server.Url + "upload",
            body,
            c => c.ContentType = "multipart/form-data"));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("boundary", ex.Message);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task HttpContentWithOwnContentType_UnderMultipartConfig_IsUntouched()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();
        using var body = new StringContent("raw-text");

        // 内容自带 Content-Type 时库不覆盖：这里发出去的是 text/plain，不是 multipart，
        // 因此不该被上面那条 boundary 校验误伤。
        await client.PostAsync(server.Url + "raw", body, c => c.ContentType = "multipart/form-data");

        var sent = server.Received[0];
        Assert.StartsWith("text/plain", sent.Headers.GetValueOrDefault("content-type"));
        Assert.Equal("raw-text", Encoding.UTF8.GetString(sent.Body));
    }

    [Fact]
    public async Task FormFlattening_IgnoresCustomSerializer()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig
{
            Serialization = new()
            {
                ContentSerializer = SnakeCaseSerializer.Instance,
            },
        });

        await client.PostAsync(
            server.Url + "form",
            new User { UserName = "alice", Age = 20 },
            c => c.ContentType = AxiosConfig.FormUrlEncodedContentType);

        // 表单是独立编码通道：换 JSON 引擎（snake_case）不影响字段名，命名只看属性名 /
        // [JsonPropertyName] / FormFieldNameSelector。这样换 XML、YAML 序列化器也不会把表单带坏。
        var body = Body_JsonCustomization(server.Received[0]);
        Assert.Contains("UserName=alice", body);
        Assert.Contains("Age=20", body);
    }

    [Fact]
    public async Task MemoryByte_InMultipart_BecomesFilePart()
    {
        // Memory<byte> 不能掉进 JSON 序列化兜底（字段会变成 {}）。
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "upload",
            new { blob = new Memory<byte>(new byte[] { 9, 8, 7 }) },
            c => c.ContentType = "multipart/form-data");

        var body = Encoding.ASCII.GetString(server.Received[0].Body);
        Assert.Contains("name=blob", body);
        Assert.Contains("filename=blob", body);
        Assert.Contains("application/octet-stream", body);
    }

    // ------------------------------------------------------------------
    // L8：multipart Content-Type + 原始内容请求体（头部/正文自相矛盾）
    // ------------------------------------------------------------------

    [Fact]
    public async Task RawString_UnderMultipartContentType_ThrowsBadOptionValue()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.PostAsync(server.Url + "x", "hello", c => c.ContentType = "multipart/form-data"));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task RawBytes_UnderMultipartContentType_ThrowsBadOptionValue()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.PostAsync(server.Url + "x", new byte[] { 1, 2 }, c => c.ContentType = "multipart/form-data"));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task RawBytes_WithDeclaredBoundary_ArePassedThroughAsIs()
    {
        // 带上 boundary 即视为「预序列化的 multipart 正文」，按调用方断言原样发送
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();
        var payload = Encoding.UTF8.GetBytes("--xyz\r\nContent-Disposition: form-data; name=\"a\"\r\n\r\nb\r\n--xyz--\r\n");

        await client.PostAsync(server.Url + "x", payload, c => c.ContentType = "multipart/form-data; boundary=xyz");

        var sent = server.Received[0];
        Assert.Contains("boundary=xyz", sent.Headers.GetValueOrDefault("Content-Type"));
        Assert.Equal(payload, sent.Body);
    }

    // ------------------------------------------------------------------
    // L9：multipart 部件自带的 Content-Disposition（name/filename）
    // ------------------------------------------------------------------

    [Fact]
    public async Task Multipart_UserPartWithoutName_GetsFieldName()
    {
        // net 的 MultipartFormDataContent.Add 对已有 disposition 完全不碰：
        // 只带 filename 的部件也要拿到 name，否则服务端无法绑定字段。
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var part = new ByteArrayContent(new byte[] { 1, 2 });
        part.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { FileName = "report.pdf" };

        await client.PostAsync(server.Url + "upload", new { doc = part }, c => c.ContentType = "multipart/form-data");

        var body = Encoding.ASCII.GetString(server.Received[0].Body);
        Assert.Contains("name=doc", body);
        Assert.Contains("filename=report.pdf", body);
    }

    [Fact]
    public async Task Multipart_UserPartWithNameAndFileName_IsNotOverwritten()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var part = new ByteArrayContent(new byte[] { 1, 2 });
        part.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "custom",
            FileName = "report.pdf"
        };

        await client.PostAsync(server.Url + "upload", new { doc = part }, c => c.ContentType = "multipart/form-data");

        var body = Encoding.ASCII.GetString(server.Received[0].Body);
        Assert.Contains("name=custom", body);
        Assert.DoesNotContain("name=doc", body);
        Assert.Contains("filename=report.pdf", body);
    }

    [Fact]
    public async Task FormContentType_FlattensNestedObjectsAndArrays_AndSkipsNulls()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "form",
            new
            {
                name = "a b",
                tags = new[] { "x", "y" },
                filter = new { kind = "book" },
                skip = (string?)null
            },
            c => c.ContentType = AxiosConfig.FormUrlEncodedContentType);

        Assert.Equal("application/x-www-form-urlencoded", ContentType(server.Received[0]));
        Assert.Equal("name=a+b&tags[]=x&tags[]=y&filter[kind]=book", Body_RequestBody(server.Received[0]));
    }

    [Fact]
    public async Task Multipart_SendsTextFieldsAndMultipleFileParts()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "upload",
            new
            {
                title = "年报",
                meta = new { author = "bob" },
                files = new[]
                {
                    new FormFile("a.txt", Encoding.UTF8.GetBytes("alpha"), "text/plain"),
                    FormFile.FromBytes("b.txt", Encoding.UTF8.GetBytes("beta"))
                }
            },
            c => c.ContentType = "multipart/form-data");

        var sent = server.Received[0];
        var contentType = ContentType(sent)!;
        Assert.StartsWith("multipart/form-data", contentType);
        Assert.Contains("boundary=", contentType);

        var body = Body_RequestBody(sent);
        Assert.Contains("name=title", body);
        Assert.Contains("年报", body);
        Assert.Contains("meta[author]", body);
        Assert.Contains("bob", body);

        Assert.Equal(2, CountOccurrences(body, "filename="));
        Assert.Contains("a.txt", body);
        Assert.Contains("alpha", body);
        Assert.Contains("text/plain", body);
        Assert.Contains("b.txt", body);
        Assert.Contains("beta", body);
        Assert.Contains("application/octet-stream", body);
    }

    [Fact]
    public async Task Multipart_FromPath_StreamsFileContent()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".txt");
        await File.WriteAllTextAsync(path, "file-on-disk");

        try
        {
            await using var server = TestHttpServer.Start(EchoJson);
            using var client = new AxiosClient();

            await client.PostAsync(
                server.Url + "upload",
                new { file = FormFile.FromPath(path, "report.txt", "text/plain") },
                c => c.ContentType = "multipart/form-data");

            var body = Body_RequestBody(server.Received[0]);
            Assert.Contains("report.txt", body);
            Assert.Contains("file-on-disk", body);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Multipart_AcceptsRawStreamsAndByteArraysAsFileParts()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("stream-data"));

        await client.PostAsync(
            server.Url + "upload",
            new Dictionary<string, object?>
            {
                ["title"] = "hello",
                ["stream"] = stream,
                ["bytes"] = new byte[] { 9, 8, 7 }
            },
            c => c.ContentType = "multipart/form-data");

        var body = Body_RequestBody(server.Received[0]);
        Assert.Contains("name=title", body);
        Assert.Contains("stream-data", body);
        Assert.Contains("name=stream", body);
        Assert.Contains("name=bytes", body);
    }

    [Fact]
    public async Task Multipart_RootFileCollection_UsesFieldName()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "upload",
            new[]
            {
                new FormFile("x.txt", Encoding.UTF8.GetBytes("x-content")) { FieldName = "files" },
                new FormFile("y.txt", Encoding.UTF8.GetBytes("y-content")) { FieldName = "files" }
            },
            c => c.ContentType = "multipart/form-data");

        var body = Body_RequestBody(server.Received[0]);
        Assert.Equal(2, CountOccurrences(body, "name=files"));
        Assert.Contains("x-content", body);
        Assert.Contains("y-content", body);
    }

    [Fact]
    public async Task Multipart_HonorsExplicitBoundary()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "upload",
            new { a = 1 },
            c => c.ContentType = "multipart/form-data; boundary=----axios-net");

        var sent = server.Received[0];
        Assert.Contains("----axios-net", ContentType(sent));
        Assert.Contains("------axios-net", Body_RequestBody(sent));
    }

    [Fact]
    public async Task Multipart_RootScalarWithoutFieldName_Throws()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(
            server.Url + "upload",
            42,
            c => c.ContentType = "multipart/form-data"));

        // 缺字段名是调用方的模型装配错误：与「名字含非法字符」同属 ERR_BAD_OPTION_VALUE，
        // 不是「请求体序列化失败」（ERR_BAD_REQUEST）
        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.NotNull(ex.Cause);
        Assert.Contains("字段名", ex.Cause!.Message);
    }

    // ------------------------------------------------------------------
    // multipart 的字段名 / 文件名：Content-Disposition 是 quoted-string，
    // 引号与换行原本会让 HttpClient 抛一句看不出所以然的异常，反斜杠与控制字符更糟——
    // 不报错，但服务端解析回来是「带引号的原文」，字段静默对不上。统一在发出前拦下。
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("a\"b")]
    [InlineData("a\\b")]
    [InlineData("a\r\nb")]
    [InlineData("a\tb")]
    [InlineData("a\0b")]
    public async Task Multipart_FieldNameWithDispositionSeparator_ThrowsBadOptionValue(string fieldName)
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync(
            server.Url + "upload",
            new Dictionary<string, object?> { [fieldName] = "v" },
            c => c.ContentType = "multipart/form-data"));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains(fieldName, ex.Message);
        Assert.NotNull(ex.Cause);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Multipart_FieldNamesThatAreLegalInQuotedString_StillWork()
    {
        await using var server = TestHttpServer.Start(EchoJson);
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "upload",
            new Dictionary<string, object?>
            {
                ["a[b]"] = "1",
                ["c d"] = "2",
                ["a;b"] = "3",
                ["a=b"] = "4",
                ["中文名"] = "5"
            },
            c => c.ContentType = "multipart/form-data");

        var body = Body_RequestBody(server.Received[0]);
        Assert.Contains("name=\"a[b]\"", body);
        Assert.Contains("name=\"c d\"", body);
        Assert.Contains("name=\"a;b\"", body);
        Assert.Contains("name=\"a=b\"", body);
        // 非 ASCII 字段名由 HttpClient 编成 RFC 2047 encoded-word，服务端解析回「中文名」
        Assert.Contains("name=\"=?utf-8?B?5Lit5paH5ZCN?=\"", body);
    }

    [Theory]
    [InlineData("a\"b.txt")]
    [InlineData("a\\b.txt")]
    [InlineData("a\nb.txt")]
    [InlineData("a\rb.txt")]
    [InlineData("a\0b.txt")]
    public void FormFile_FileNameWithDispositionSeparator_IsRejectedUpfront(string fileName)
    {
        var ex = Assert.Throws<ArgumentException>(() => new FormFile(fileName, new byte[] { 1 }));

        Assert.Equal("fileName", ex.ParamName);
        Assert.Contains(fileName, ex.Message);
    }

    [Fact]
    public async Task CustomFormAndMultipart_CanBypassTheFramework_ViaTransformRequest()
    {
        await using var server = TestHttpServer.Start(EchoJson);

        var fileBytes = new byte[] { 0xFF, 0x00, 0xFE };
        var config = new AxiosConfig();

        // 逃生口在 transformRequest：返回 HttpContent 后库只负责原样发送（头也自己带）。
        // 也就是说内置的表单/multipart 组装不是唯一路径 —— 自己造 FormUrlEncodedContent /
        // MultipartFormDataContent（含自定义 boundary、流式文件部件）完全可行，不必改库。
        config.TransformRequest.Add((data, _) => data switch
        {
            "as-form" => new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["name"] = "alice",
                ["tags"] = "a,b"
            }),
            "as-multipart" => BuildMultipart(fileBytes),
            _ => data
        });

        using var client = new AxiosClient(config);

        await client.PostAsync(server.Url + "form", "as-form", c => c.ContentType = AxiosContentType.FormUrlEncoded);
        await client.PostAsync(server.Url + "upload", "as-multipart", c => c.ContentType = AxiosContentType.MultipartFormData);

        var form = Body_RequestBody(server.Received[0]);
        Assert.Contains("name=alice", form);
        Assert.Contains("tags=a%2Cb", form);   // 由 BCL 的 FormUrlEncodedContent 编码，不经框架展平

        // boundary 由 BCL 生成：断言结构 + 文件字节原样到达（没有被当文本重编码）
        Assert.StartsWith("multipart/form-data; boundary=", ContentType(server.Received[1]));
        Assert.Contains("name=file; filename=a.bin", Body_RequestBody(server.Received[1]));
        Assert.True(server.Received[1].Body.AsSpan().IndexOf(fileBytes) >= 0);
    }

    [Fact]
    public async Task FormFieldNameSelector_AppliesToFormUrlEncoded()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "form",
            new { name = "alice", filter = new { kind = "book" } },
            c =>
            {
                c.ContentType = AxiosConfig.FormUrlEncodedContentType;
                c.Serialization.FormFieldNameSelector = (prefix, key) => prefix is null ? key : $"{prefix}.{key}";
            });

        Assert.Equal("name=alice&filter.kind=book", Body_SerializationAndTransform(server.Received[0]));
    }

    [Fact]
    public async Task FormFieldNameSelector_AppliesToMultipart()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "upload",
            new { name = "alice", meta = new { author = "bob" } },
            c =>
            {
                c.ContentType = "multipart/form-data";
                c.Serialization.FormFieldNameSelector = (prefix, key) => prefix is null ? key : $"{prefix}_{key}";
            });

        var body = Body_SerializationAndTransform(server.Received[0]);
        Assert.Contains("name=meta_author", body);
        Assert.Contains("bob", body);
    }

    [Fact]
    public async Task PostForm_DefaultsToMultipart()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        await client.PostFormAsync(server.Url + "upload", new { name = "alice" });

        var sent = server.Received[0];
        Assert.StartsWith("multipart/form-data", sent.Headers.GetValueOrDefault("content-type"));
        Assert.Contains("name=name", Body_SerializationAndTransform(sent));
        Assert.Contains("alice", Body_SerializationAndTransform(sent));
    }

    [Fact]
    public async Task PostForm_RespectsExplicitContentType()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        await client.PostFormAsync(
            server.Url + "form",
            new { name = "alice" },
            c => c.ContentType = AxiosConfig.FormUrlEncodedContentType);

        Assert.Equal("application/x-www-form-urlencoded", server.Received[0].Headers.GetValueOrDefault("content-type"));
        Assert.Equal("name=alice", Body_SerializationAndTransform(server.Received[0]));
    }

    [Fact]
    public async Task PostForm_IgnoresNonFormInstanceContentType()
    {
        // 实例默认值为 JSON 接口设置过 ContentType：它是「默认值」，不是本次调用的意见，
        // 不能把 PostFormAsync 的 multipart 承诺压掉（否则字段被按 JSON 发出去，且没有任何提示）。
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();
        client.Defaults.ContentType = AxiosContentType.Json;

        await client.PostFormAsync(server.Url + "upload", new { name = "alice" });

        var sent = server.Received[0];
        Assert.StartsWith("multipart/form-data", sent.Headers.GetValueOrDefault("content-type"));
        Assert.Contains("name=name", Body_SerializationAndTransform(sent));
        Assert.Contains("alice", Body_SerializationAndTransform(sent));
    }

    [Fact]
    public async Task PostForm_KeepsInstanceFormUrlEncodedDefault()
    {
        // 实例默认值本身就是表单编码＝「这个客户端专发表单」的选择，方法默认值不抢它。
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();
        client.Defaults.ContentType = AxiosConfig.FormUrlEncodedContentType;

        await client.PostFormAsync(server.Url + "form", new { name = "alice" });

        Assert.Equal("application/x-www-form-urlencoded", server.Received[0].Headers.GetValueOrDefault("content-type"));
        Assert.Equal("name=alice", Body_SerializationAndTransform(server.Received[0]));
    }

    [Fact]
    public async Task PostForm_KeepsInstanceMultipartDefaultWithBoundary()
    {
        // 实例默认值里的 multipart 参数（固定 boundary）同样保留：覆盖会把调用方指定的边界换掉。
        const string boundary = "----axios-net-form";
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();
        client.Defaults.ContentType = AxiosContentType.MultipartFormData + "; boundary=" + boundary;

        await client.PostFormAsync(server.Url + "upload", new { name = "alice" });

        var sent = server.Received[0];
        var contentType = sent.Headers.GetValueOrDefault("content-type")!;
        Assert.StartsWith("multipart/form-data; boundary=", contentType);
        Assert.Contains(boundary, contentType);
        Assert.Contains("--" + boundary, Body_SerializationAndTransform(sent));
    }

    [Fact]
    public async Task PostForm_ConfigureStillBeatsInstanceContentType()
    {
        // configure 是本次调用的显式意见，优先级高于方法默认值与实例默认值。
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();
        client.Defaults.ContentType = AxiosConfig.FormUrlEncodedContentType;

        await client.PostFormAsync(
            server.Url + "json",
            new { name = "alice" },
            c => c.ContentType = AxiosContentType.Json);

        Assert.Equal("application/json", server.Received[0].Headers.GetValueOrDefault("content-type"));
        Assert.Equal("{\"name\":\"alice\"}", Body_SerializationAndTransform(server.Received[0]));
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task PutAndPatchForm_UseMultipartAndExpectedMethod(string method)
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        if (method == "PUT")
        {
            await client.PutFormAsync(server.Url + "upload", new { name = "alice" });
        }
        else
        {
            await client.PatchFormAsync(server.Url + "upload", new { name = "alice" });
        }

        Assert.Equal(method, server.Received[0].Method);
        Assert.StartsWith("multipart/form-data", server.Received[0].Headers.GetValueOrDefault("content-type"));
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
    private sealed class ReadOnlyStringMap : IReadOnlyDictionary<string, object>
    {
        private readonly Dictionary<string, object> _inner = new();

        public object this[string key] => _inner[key];
        public IEnumerable<string> Keys => _inner.Keys;
        public IEnumerable<object> Values => _inner.Values;
        public int Count => _inner.Count;
        public bool ContainsKey(string key) => _inner.ContainsKey(key);
        public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => _inner.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _inner.GetEnumerator();
        public bool TryGetValue(string key, out object value) => _inner.TryGetValue(key, out value!);
        public void Add(string key, object value) => _inner[key] = value;
    }

    public sealed class XmlPayload
    {
        public string? Name { get; set; }
    }


    // ===============================================================
    // 测试夹具
    // ===============================================================


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

    private static string BodyOf(TestRequest request) => Encoding.UTF8.GetString(request.Body);

    // ---------------------------------------------------------------
    // 测试模型与序列化器夹具
    // ---------------------------------------------------------------


    private sealed class Node_AuditRegression
    {
        public Node_AuditRegression? Self { get; set; }
    }

    private static string Body_JsonCustomization(TestRequest request) => Encoding.UTF8.GetString(request.Body);

    private sealed class User
    {
        public string UserName { get; set; } = string.Empty;

        public int Age { get; set; }
    }

    /// <summary>
    /// net8.0 内置 <c>JsonNamingPolicy.SnakeCaseLower</c>，net6.0 的 System.Text.Json 6 没有，
    /// 这里给出等价实现，保证两个 TFM 的用例语义一致。
    /// </summary>
    private static JsonNamingPolicy SnakeCase =>
#if NET8_0_OR_GREATER
        JsonNamingPolicy.SnakeCaseLower;

#else
        SnakeCaseNamingPolicy.Instance;

#endif

    private sealed class SnakeCaseNamingPolicy : JsonNamingPolicy
    {
        public static SnakeCaseNamingPolicy Instance { get; } = new();

        public override string ConvertName(string name)
        {
            var builder = new StringBuilder(name.Length + 8);
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (char.IsUpper(c))
                {
                    if (i > 0)
                    {
                        builder.Append('_');
                    }

                    builder.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }
    }

    /// <summary>用 snake_case 固定策略的 STJ 适配器，证明自定义序列化器会绕开 config 里的 options。</summary>
    private sealed class SnakeCaseSerializer : IContentSerializer
    {
        private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = SnakeCase
        };

        public static SnakeCaseSerializer Instance { get; } = new();

        public string Serialize(object? value, Type type, AxiosConfig config)
            => JsonSerializer.Serialize(value, type, Options);

        public object? Deserialize(string json, Type type, AxiosConfig config)
            => JsonSerializer.Deserialize(json, type, Options);
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

    private static string? ContentType(TestRequest request) => request.Headers.GetValueOrDefault("content-type");

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static HttpContent BuildMultipart(byte[] fileBytes)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent("bob"), "owner");
        form.Add(new ByteArrayContent(fileBytes), "file", "a.bin");
        return form;
    }

    private static string Body_SerializationAndTransform(TestRequest request) => Encoding.UTF8.GetString(request.Body);
}