
using AxiosNet.Pooling;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Attributes;
using AxiosNet.WebAPI.Metadata;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;

namespace AxiosNet.Tests.WebAPI;

/// <summary>
/// 声明式绑定：路由模板、Path/Query/Header/Body 归位规则、`Configure` 钩子。
/// </summary>
public sealed class ModelBindingTests
{
    // ===============================================================
    // Bug 3：查询绑定全为 null 时返回空字典，静默清空实例默认 Params
    // ===============================================================

    [Fact]
    public void QueryBindingsAllNull_DoesNotClearInstanceDefaultParams()
    {
        using var client = new ApiClient("http://127.0.0.1:1/", c =>
            c.Params = new { api_key = "secret" });

        // 有 [Query] 绑定但值为 null：Params 不应变成空字典把 api_key 清掉。
        var uri = client.GetUri(new AllNullQueryRequest());

        Assert.Contains("api_key=secret", uri);
    }

    [Fact]
    public void GetModel_JsonPropertyName_DoesNotRenameQueryParameter()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");

        var uri = client.GetUri(new JsonNamedQueryRequest { MemberName = "v" });

        // 查询名只认 [Query("名")] 与成员名：[JsonPropertyName] 是 JSON 正文的键名，不参与查询串。
        Assert.Contains("MemberName=v", uri, StringComparison.Ordinal);
        Assert.DoesNotContain("wire_name", uri, StringComparison.Ordinal);
    }

    [Fact]
    public void NoQueryBindings_DoesNotClearInstanceDefaultParams()
    {
        using var client = new ApiClient("http://127.0.0.1:1/", c =>
            c.Params = new { api_key = "secret" });

        // 没有任何查询绑定（对照组）：两种模型形态行为必须一致
        var uri = client.GetUri(new NoQueryBindingRequest());

        Assert.Contains("api_key=secret", uri);
    }

    // ===============================================================
    // 默认通道只看 HTTP 方法：POST 上的未标注成员一律进聚合正文（与类型无关）
    // ===============================================================

    [Fact]
    public async Task PostModel_SingleDateOnlyProperty_BindsBody()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new ApiClient(server.Url);

        var request = new DateOnlyBodyCandidateRequest { When = new DateOnly(2026, 10, 22) };

        await client.RequestFullAsync(request);

        var received = Assert.Single(server.Received);

        // 标量也按成员键聚合：{ "when": "2026-10-22" }。
        // 两张现代资产一致——net6.0 的框架 STJ 没有 DateOnly 转换器，由库在默认选项里补上。
        Assert.Null(received.Query);
        var json = JsonSerializer.Deserialize<JsonElement>(received.Body);
        Assert.Equal("2026-10-22", json.GetProperty("when").GetString());
    }

    [Fact]
    public async Task PostModel_SingleUriProperty_BindsBody()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestFullAsync(new UriBodyCandidateRequest { Callback = new Uri("http://example.com/hook") });

        var received = Assert.Single(server.Received);
        Assert.Null(received.Query);
        var json = JsonSerializer.Deserialize<JsonElement>(received.Body);
        Assert.Equal("http://example.com/hook", json.GetProperty("callback").GetString());
    }

    [Fact]
    public async Task PostModel_SingleDateOnlyPathProperty_IsAcceptedAsPath()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new ApiClient(server.Url);

        // DateOnly 应被当作 Path/Header 的标量处理。
        await client.RequestFullAsync(new DateOnlyPathRequest { Day = new DateOnly(2026, 10, 22) });

        var received = Assert.Single(server.Received);
        Assert.Equal("/reports/2026-10-22", received.Path);
    }

    [Fact]
    public async Task PostModel_BodyAttribute_AddsKeyAndCoexistsWithQuery()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestFullAsync(new BodyKeyRequest { Token = "t", Page = 2, Name = "n" });

        var received = Assert.Single(server.Received);

        // [Body] 是聚合正文里的一个键，不是原样正文；未标注成员照常并入同一个对象。
        var json = JsonSerializer.Deserialize<JsonElement>(received.Body);
        Assert.Equal("t", json.GetProperty("token").GetString());
        Assert.Equal(2, json.GetProperty("page").GetInt32());
        Assert.Equal("n", json.GetProperty("name").GetString());

        // 同一成员可同时进 Query（[Body] 与 [Query] 正交）。
        Assert.Contains("page=2", received.Query);
    }

    [Fact]
    public async Task GetModel_BodyAttribute_StillProducesBody()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestFullAsync(new GetWithBodyRequest { Token = "t", Page = 3 });

        var received = Assert.Single(server.Received);
        Assert.Equal("GET", received.Method);

        // 显式 [Body] 在任何方法上都进正文；其余未标注成员保持 Query。
        var json = JsonSerializer.Deserialize<JsonElement>(received.Body);
        Assert.Equal("t", json.GetProperty("token").GetString());
        Assert.Contains("Page=3", received.Query);
    }

    [Fact]
    public async Task PostModel_WholeBody_IsRawAndUnwrapped()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestFullAsync(new RawBodyRequest { Payload = new Person("n", 1) });

        var received = Assert.Single(server.Received);

        // 无参 [Body] 是整份正文：原样发送，不加键壳。
        var json = JsonSerializer.Deserialize<JsonElement>(received.Body);
        Assert.Equal("n", json.GetProperty("name").GetString());
        Assert.Equal(1, json.GetProperty("age").GetInt32());
        Assert.False(json.TryGetProperty("payload", out _));
        Assert.Null(received.Query);
    }

    [Fact]
    public async Task PostModel_WholeBodyWithExplicitQuery_IsAccepted()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestFullAsync(new BodyWithQueryRequest
        {
            Payload = new Person("n", 1),
            Tag = "x",
            Page = 4
        });

        var received = Assert.Single(server.Received);

        // 整份正文原样发送；显式 [Query] 的成员照旧进查询串。
        var json = JsonSerializer.Deserialize<JsonElement>(received.Body);
        Assert.Equal("n", json.GetProperty("name").GetString());
        Assert.Equal(1, json.GetProperty("age").GetInt32());
        Assert.Contains("tag=x", received.Query);
        Assert.Contains("page=4", received.Query);
    }

    [Fact]
    public async Task PostModel_WholeBodyWithUnannotatedMember_IsRejected()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new ApiClient(server.Url);

        // POST 上未标注成员会改道进正文，与整份正文冲突：必须显式换通道。
        var ex = await Assert.ThrowsAsync<AxiosException>(
            () => client.RequestFullAsync(new BodyWithUnannotatedRequest { Payload = new Person("n", 1) }));

        Assert.Contains("整份正文必须独占", ex.Message);
        Assert.Contains("[Query]", ex.Message);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task PostModel_WholeBodyWithKeyMember_IsRejected()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new ApiClient(server.Url);

        var ex = await Assert.ThrowsAsync<AxiosException>(
            () => client.RequestFullAsync(new WholeBodyAndKeyRequest
            {
                Payload = new Person("n", 1),
                Extra = new Person("m", 2)
            }));

        Assert.Contains("整份正文必须独占", ex.Message);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task PostModel_MultipleWholeBodies_AreRejected()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new ApiClient(server.Url);

        var ex = await Assert.ThrowsAsync<AxiosException>(
            () => client.RequestFullAsync(new TwoWholeBodyRequest()));

        Assert.Contains("只能有一个", ex.Message);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task PostModel_BodyMembers_KeepDeclarationOrder()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestFullAsync(new MixedOrderRequest());

        var received = Assert.Single(server.Received);

        // 正文键按声明顺序输出（显式键名与未标注成员混排也不重排）。
        Assert.Equal("{\"a\":1,\"b\":2,\"d\":4}", Encoding.UTF8.GetString(received.Body));
    }

    [Fact]
    public async Task PostModel_BodyName_OverridesJsonPropertyName_AndSkipsNamingPolicy()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestFullAsync(new BodyNameRequest());

        var received = Assert.Single(server.Received);

        // user_name 压过 [JsonPropertyName]；UserId 原样保留（显式名字不套 camelCase）。
        Assert.Equal("{\"user_name\":\"n\",\"UserId\":2}", Encoding.UTF8.GetString(received.Body));
    }

    [Fact]
    public async Task PostModel_BodyName_BlankFallsBackToJsonPropertyName()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestFullAsync(new BlankBodyNameRequest());

        var received = Assert.Single(server.Received);
        Assert.Equal("{\"jp\":1}", Encoding.UTF8.GetString(received.Body));
    }

    [Fact]
    public async Task PostModel_BodyName_DuplicateKey_IsRejected()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new ApiClient(server.Url);

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.RequestFullAsync(new DuplicateBodyNameRequest()));

        Assert.Contains("重复键", ex.Message);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task PostModel_BodyNamesDifferingOnlyInCase_AreRejected()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new ApiClient(server.Url);

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.RequestFullAsync(new CaseDifferingBodyNameRequest()));

        Assert.Contains("仅大小写不同", ex.Message);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task PostModel_FormBodyNamesDifferingOnlyInCase_AreSent()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new ApiClient(server.Url);

        // 表单键名的大小写由接收端解释：库只按同名判重，不再拦「仅大小写不同」。
        await client.RequestFullAsync(new CaseDifferingFormBodyNameRequest());

        var body = Encoding.UTF8.GetString(Assert.Single(server.Received).Body);
        Assert.Equal("a=1&A=2", body);
    }

    [Fact]
    public async Task Post_FormInterfaceBodyNamesDifferingOnlyInCase_AreSent()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new ApiClient(server.Url);

        // 接口路径与请求模型共用同一个构建器，通道判定必须同样生效。
        await client.Create<ICaseDifferingFormBodyApi>().SubmitAsync("1", "2");

        var body = Encoding.UTF8.GetString(Assert.Single(server.Received).Body);
        Assert.Equal("a=1&A=2", body);
    }

    [Fact]
    public async Task PostModel_EmptyJsonPropertyName_PartFallsBackToPropertyName()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestFullAsync(new EmptyJsonPropertyNamePartRequest());

        var body = Encoding.UTF8.GetString(Assert.Single(server.Received).Body);
        Assert.Contains("name=Value", body);
        Assert.DoesNotContain("name=\"\"", body);
    }

    [Fact]
    public async Task PostModel_ExplicitBody_WithMultipartContentType_MessageNamesExplicitKey()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new ApiClient(server.Url, defaults =>
            defaults.ContentType = "multipart/form-data; boundary=xyz");

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.RequestFullAsync(new SingleExplicitBodyRequest()));

        // 显式 [Body("x")] 不是「未标注成员」，报错不能这么说。
        Assert.Contains("按键聚合的正文", ex.Message);
        Assert.Contains("[Body(\"名\")]", ex.Message);
        Assert.DoesNotContain("默认 Body（未标注成员按键名聚合）", ex.Message);
    }

    [Fact]
    public void RequestModelBuilder_BlankFieldName_FallsBackToMemberName()
    {
        var builder = new RequestModelBuilder(typeof(BlankFieldNameSample));
        builder.Member("Value", typeof(string), static _ => null).FieldName("  ");

        var member = Assert.Single(builder.Declaration.Members);
        Assert.Equal("Value", member.FieldName);
        Assert.False(member.NameFromAttribute);
    }

    [Fact]
    public async Task PostModel_JsonPropertyName_SameAsPropertyName_IsNotRecased()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new ApiClient(server.Url);

        // 显式 [Body] 通路与未标注默认正文通路各一条：名字与属性名完全相同时也必须保留原样。
        await client.RequestFullAsync(new BodySameNameJsonPropertyRequest());
        await client.RequestFullAsync(new UnannotatedSameNameJsonPropertyRequest());

        Assert.Collection(
            server.Received,
            first => Assert.Equal("{\"Tag\":\"t\"}", Encoding.UTF8.GetString(first.Body)),
            second => Assert.Equal("{\"Tag\":\"t\"}", Encoding.UTF8.GetString(second.Body)));
    }

    [Fact]
    public async Task PostModel_AggregateBody_FormDuplicateKey_IsRejected()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new ApiClient(server.Url);

        // 键名由表单命名钩子统一映射成同一个名字：构建期按成员名判不出，运行期按线上形态拦下。
        client.Axios.Defaults.Serialization.FormFieldName = (_, _) => "same";

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.RequestFullAsync(
            new TwoKeyFormRequest(),
            config => config.ContentType = AxiosConfig.FormUrlEncodedContentType));

        Assert.Contains("重复键", ex.Message);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task PostModel_AggregateBody_SerializationFailure_NamesKeysAndDisposesOwnedStream()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new ApiClient(server.Url);

        // 声明类型是 object：构建期判不出是流，直到序列化才发现 → 报错文案要给键名而不是内部类型名，且流要回收
        var stream = new DisposeTrackingStream();
        var ex = await Assert.ThrowsAsync<AxiosException>(
            () => client.RequestFullAsync(new RuntimeStreamBodyRequest { Data = stream }));

        Assert.Contains("默认正文", ex.Message);
        Assert.DoesNotContain(nameof(AxiosNet.Internal.Content.AggregateBody), ex.Message);
        Assert.True(stream.Disposed);
        Assert.Empty(server.Received);
    }

    [Fact]
    public void PostModel_AggregateBody_CannotCarryFileParts_IsRejectedAtBuild()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");

        // 合并成对象后文件部件没有线上形态：构建期就拦下，不留到序列化时才炸。
        var stream = Assert.ThrowsAny<AxiosException>(() => client.GetUri(new BodyStreamRequest()));
        Assert.Contains("[Multipart]", stream.Message, StringComparison.Ordinal);
        Assert.Contains("[Body]", stream.Message, StringComparison.Ordinal);

        var file = Assert.ThrowsAny<AxiosException>(() => client.GetUri(new BodyFormFileRequest()));
        Assert.Contains("[Multipart]", file.Message, StringComparison.Ordinal);
    }

// 2) 声明式头绑定的 fail-fast（异常类型沿用 AxiosHeaders.Set 的公开契约）

    [Fact]
    public async Task DeclarativeHeader_InvalidName_FailsFastBeforeSend()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());
        using var api = new ApiClient(server.Url);

        // 头名来自 [Header("...")]，非法名在配置构建期就抛：声明式装配统一 ERR_BAD_REQUEST，一个字节都不发
        var error = await Assert.ThrowsAnyAsync<AxiosException>(() =>
            api.RequestFullAsync(new ProbeBadHeaderNameRequest { Value = "1" }));

        Assert.Equal(AxiosException.ErrBadRequest, error.Code);
        Assert.IsType<ArgumentException>(error.Cause);
        Assert.Empty(server.Received);
    }

    // ===============================================================
    // 6. 派生类用 [ApiIgnore] 覆盖基类带 [Query] 的 virtual 属性：
    //    语义是「完全不绑定」，不应误判成「与参数特性同用的二义性」。
    // ===============================================================

    [Fact]
    public async Task DerivedApiIgnore_OverridesInheritedQueryBinding()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestFullAsync(new IgnoredInheritedPageRequest { Page = 7 });

        var received = Assert.Single(server.Received);
        Assert.Null(received.Query);
    }

    // ===============================================================
    // 7. [Path] / [Header] 的标量文本化必须与 [Query] 一致：bool 是 true/false（不是 True/False），
    //    日期时间是 ISO 8601（不是不变文化的短日期 + 空格）。
    // ===============================================================

    [Fact]
    public async Task PathAndHeader_ScalarText_MatchesQueryConvention()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("n", 1)));
        using var client = new ApiClient(server.Url);

        var when = new DateTime(2026, 10, 22, 8, 30, 0, DateTimeKind.Utc);

        await client.RequestFullAsync(new ScalarTextRequest { Flag = true, When = when, DryRun = false });

        var received = Assert.Single(server.Received);

        Assert.Equal("/flags/true", received.Path);
        Assert.Equal("false", received.Headers.GetValueOrDefault("x-dry-run"));

        // Query 侧既有约定：DateTime → UTC 毫秒 ISO 8601
        Assert.Contains("When=2026-10-22T08%3A30%3A00.000Z", received.Query);
    }

    // 路径绑定

    [Fact]
    public async Task Get_PathTokenMatchesBindingCaseInsensitively()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient(server);

        await client.RequestFullAsync(new CaseInsensitivePathRequest { Id = 7 });

        var received = Assert.Single(server.Received);
        // [Path("UID")] ↔ 模板 {uid}：大小写不敏感匹配，替换用模板原文
        Assert.Equal("/users/7", received.Path);
    }

    [Fact]
    public void Path_DuplicatePlaceholderDifferingOnlyByCase_IsRejected()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");

        var ex = Assert.Throws<AxiosException>(
            () => client.GetUri(new DuplicateCaseVariantRequest()));

        // {id} 与 {ID} 视为同一占位符（IgnoreCase），不允许重复出现
        Assert.Contains("重复使用占位符", ex.Message);
    }

    [Fact]
    public async Task Get_GuidPathValue_UsesLowercaseDFormat()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient(server);
        var token = Guid.Parse("0cf7d4a5-1b2c-4d3e-8f90-a1b2c3d4e5f6");

        await client.RequestFullAsync(new GuidPathRequest { Token = token });

        var received = Assert.Single(server.Received);
        // Guid.ToString() 的 "d" 格式：小写 + 连字符
        Assert.Equal($"/tokens/{token}", received.Path);
    }

    [Fact]
    public async Task Get_RecordRequestModel_BindsPositionalProperty()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient(server);

        // 位置参数 record（init-only 属性）可直接做请求模型
        var response = await client.RequestFullAsync(new RecordRequest(3));

        Assert.Equal(200, response.Status);

        var received = Assert.Single(server.Received);
        Assert.Equal("/records/3", received.Path);
    }

    // 默认正文边界：成员值为 null 也发送对象

    [Fact]
    public async Task Post_DefaultBodyNullValue_SendsNullMember()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient(server);

        await client.RequestFullAsync(new DefaultBodyNullRequest { Payload = null });

        var received = Assert.Single(server.Received);
        Assert.Equal("POST", received.Method);

        // 有未标注成员就发对象，值为 null 也保留键
        Assert.Equal("{\"payload\":null}", Encoding.UTF8.GetString(received.Body));
        Assert.Null(received.Query);
    }

    [Fact]
    public async Task Post_UnannotatedScalarArray_WrapsInObjectBody()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient(server);

        // 数组也按成员键聚合：{ "tags": [1, 2, 3] }（要发查询参数请显式 [Query]）
        await client.RequestFullAsync(new ScalarArrayDefaultBodyRequest { Tags = [1, 2, 3] });

        var received = Assert.Single(server.Received);
        Assert.Equal("{\"tags\":[1,2,3]}", Encoding.UTF8.GetString(received.Body));
        Assert.Null(received.Query);
    }

    [Fact]
    public async Task Post_BodyContentTypeOverriddenViaConfigure_FormEncoded()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient(server);

        await client.RequestFullAsync(new FormBodyRequest { Payload = new UserPayload("carol", 25) });

        var received = Assert.Single(server.Received);
        // [Body] 的编码规则与核心包 data 一致：ContentType 改表单就走键值对编码
        Assert.Equal("application/x-www-form-urlencoded", received.Headers["Content-Type"]);
        Assert.Contains("Name=carol", Encoding.UTF8.GetString(received.Body));
    }

    // fail-fast：非法值在发出前被拦下

    [Fact]
    public async Task Header_ValueWithControlChars_FailsFastBeforeSend()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient(server);

        // CRLF 注入：AxiosHeaders 拦截，声明式装配统一归 ERR_BAD_REQUEST（内层仍是 ArgumentException），请求一个字节都不出
        var error = await Assert.ThrowsAnyAsync<AxiosException>(
            () => client.RequestFullAsync(new EvilHeaderRequest { Evil = "a\r\nX-Evil: injected" }));

        Assert.Equal(AxiosException.ErrBadRequest, error.Code);
        Assert.IsType<ArgumentException>(error.Cause);
        Assert.Empty(server.Received);
    }

    // 原始响应形态

    [Fact]
    public async Task Get_ByteArrayResponse_ReceivesRawBytes()
    {
        var payload = new byte[] { 0x00, 0xFF, 0x10, 0x7F, 0x80 };
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: payload, ContentType: "application/octet-stream"));

        using var client = CreateClient(server);

        var response = await client.RequestFullAsync(new DownloadBytesRequest());

        Assert.Equal(200, response.Status);
        Assert.Equal(payload, response.Data); // byte[] 目标：字节原样到达，不做解码
    }

    [Fact]
    public async Task Get_StreamResponse_AutoSwitchesAndDeliversBody()
    {
        var payload = Encoding.UTF8.GetBytes("stream-payload-0123456789");
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: payload, ContentType: "application/octet-stream"));

        using var client = CreateClient(server);

        var response = await client.RequestFullAsync(new DownloadStreamRequest());

        Assert.Equal(200, response.Status);

        // TResponse 为 Stream 时自动切流式响应；读完须释放以归还连接
        using var stream = response.Data!;
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        Assert.Equal(payload, ms.ToArray());
    }

    // 连接复用与门面守卫

    [Fact]
    public async Task TwoSequentialRequests_ReuseSingleConnection()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient(server);

        await client.RequestFullAsync(new GetUserRequest { Id = 1 });
        await client.RequestFullAsync(new GetUserRequest { Id = 2 });

        Assert.Equal(2, server.Received.Count);
        // 长持有的 ApiClient 复用连接池：两次请求只建一条 TCP 连接
        Assert.Equal(1, server.TotalConnections);
    }

    // 自定义谓词：HttpApiAttribute 直接引用 HttpMethod

    [Fact]
    public async Task PropFind_CustomVerbViaHttpMethodInstance_PassesThrough()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient(server);

        var response = await client.RequestFullAsync(new PropFindRequest());

        Assert.Equal(200, response.Status);

        var received = Assert.Single(server.Received);
        Assert.Equal("PROPFIND", received.Method);
        Assert.Equal("/calendar/event", received.Path);
    }

    // 直用 HttpApiAttribute（字符串方法名）

    [Fact]
    public async Task DirectHttpApi_LowercaseMethod_NormalizedOnWire()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient(server);

        var response = await client.RequestFullAsync(new DirectHttpApiDeleteRequest { Id = 4 });

        Assert.Equal(200, response.Status);

        var received = Assert.Single(server.Received);
        // "delete" 归一化为规范 DELETE，请求行与内置 [Delete] 子类完全一致
        Assert.Equal("DELETE", received.Method);
        Assert.Equal("/api/users/4", received.Path);
    }

    [Fact]
    public async Task DirectHttpApi_LowercasePost_DefaultBodyRuleApplies()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient(server);

        await client.RequestFullAsync(new DirectHttpApiPostRequest { Payload = new Person("iris", 28) });

        var received = Assert.Single(server.Received);
        // 归一化后的 POST 参与默认正文：未标注属性按键名聚合进对象
        Assert.Equal("POST", received.Method);
        Assert.Contains("iris", Encoding.UTF8.GetString(received.Body));
        Assert.Null(received.Query);
    }

    [Fact]
    public void DirectHttpApi_BlankMethodOrPath_IsRejected()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");

        var methodError = Assert.Throws<AxiosException>(
            () => client.GetUri(new BlankMethodRequest()));
        Assert.Contains("HTTP 方法不能为空白", methodError.Message);

        var pathError = Assert.Throws<AxiosException>(
            () => client.GetUri(new BlankPathRequest()));
        Assert.Contains("路径模板不能为空白", pathError.Message);
    }

    [Fact]
    public async Task Get_PathQueryHeaderBound_ReturnsTypedResponse()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new Person("alice", 18)));

        using var client = CreateClient_ApiClientRequest(server);

        var response = await client.RequestFullAsync(new GetUserRequest
        {
            Id = 5,
            Verbose = "1",
            Trace = "trace-1"
        });

        Assert.Equal(200, response.Status);
        Assert.NotNull(response.Data);
        Assert.Equal("alice", response.Data!.Name);
        Assert.Equal(18, response.Data.Age);

        var received = Assert.Single(server.Received);
        Assert.Equal("GET", received.Method);
        Assert.Equal("/users/5", received.Path);
        Assert.Equal("verbose=1", received.Query);
        Assert.Equal("trace-1", received.Headers["X-Trace"]);
    }

    [Fact]
    public async Task Get_UnannotatedProperty_BindsAsQueryByKeyNamed()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new[] { new Person("alice", 18), new Person("bob", 20) }));

        using var client = CreateClient_ApiClientRequest(server);

        var response = await client.RequestFullAsync(new ListUsersRequest
        {
            Ids = [1, 2],
            Role = "admin"
        });

        Assert.Equal(200, response.Status);
        Assert.NotNull(response.Data);
        Assert.Equal(2, response.Data!.Length);

        var received = Assert.Single(server.Received);
        // 数组走 AxiosNet 的 a[]=v 规则（键名保留属性名大小写）；标量直接键=值
        Assert.Contains("Ids[]=1", Uri.UnescapeDataString(received.Query ?? ""));
        Assert.Contains("Ids[]=2", Uri.UnescapeDataString(received.Query ?? ""));
        Assert.Contains("Role=admin", Uri.UnescapeDataString(received.Query ?? ""));
    }

    [Fact]
    public async Task Post_BodySerializedAsJson_TypedResponse()
    {
        await using var server = TestHttpServer.Start(req =>
            TestJson.Response(new Person("created", 1), statusCode: 201));

        using var client = CreateClient_ApiClientRequest(server);

        var response = await client.RequestFullAsync(new CreateUserRequest
        {
            Person = new Person("alice", 18)
        });

        Assert.Equal(201, response.Status);
        Assert.Equal("created", response.Data!.Name);

        var received = Assert.Single(server.Received);
        Assert.Equal("POST", received.Method);
        Assert.Equal("/users", received.Path);
        Assert.Equal("application/json", received.Headers["Content-Type"]);

        var json = JsonSerializer.Deserialize<JsonElement>(received.Body);
        Assert.Equal("alice", json.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Post_NullBodyProperty_NoRequestBodySent()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient_ApiClientRequest(server);

        var response = await client.RequestFullAsync(new NullBodyRequest { Person = null });

        Assert.Equal(200, response.Status);

        var received = Assert.Single(server.Received);
        Assert.Equal("POST", received.Method);
        Assert.Empty(received.Body);
    }

    [Fact]
    public async Task Delete_Head_MethodComesFromRouteAttribute()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(Body: null));

        using var client = CreateClient_ApiClientRequest(server);

        var deleted = await client.RequestFullAsync(new DeleteUserRequest { Id = 7 });
        Assert.Equal(200, deleted.Status);

        var ping = await client.RequestFullAsync(new PingRequest());
        Assert.Equal(200, ping.Status);

        Assert.Equal("DELETE", server.Received[0].Method);
        Assert.Equal("/users/7", server.Received[0].Path);
        Assert.Equal("HEAD", server.Received[1].Method);
        Assert.Equal("/ping", server.Received[1].Path);
    }

    [Fact]
    public async Task Get_PathValueIsUrlEscaped()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new Person("alice", 18)));

        using var client = CreateClient_ApiClientRequest(server);

        var response = await client.RequestFullAsync(new EscapedPathRequest { Name = "John Doe/a b" });

        Assert.Equal(200, response.Status);

        var received = Assert.Single(server.Received);
        Assert.Equal("/users/John%20Doe%2Fa%20b/profile", received.Path);
    }

    [Fact]
    public async Task Get_AbsoluteUrlInTemplate_OverridesBaseAddress()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new Person("alice", 18)));

        // baseAddress 指向别处：模板里的绝对 URL 必须整体覆盖它
        using var client = new ApiClient("http://192.0.2.1:1/");

        var response = await client.RequestFullAsync(new AbsoluteUrlRequest { Port = server.Port });

        Assert.Equal(200, response.Status);

        var received = Assert.Single(server.Received);
        Assert.Equal("/abs", received.Path);
    }

    [Fact]
    public async Task Get_ModelConfigureHook_HeaderReachesServer()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new Person("alice", 18)));

        using var client = CreateClient_ApiClientRequest(server);

        var response = await client.RequestFullAsync(new ConfiguredGetRequest { Id = 1 });

        Assert.Equal(200, response.Status);

        var received = Assert.Single(server.Received);
        Assert.Equal("from-model", received.Headers["X-Configured"]);
    }

    [Fact]
    public async Task Get_CallSiteConfigureHook_RunsAfterModelBinding()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new Person("alice", 18)));

        using var client = CreateClient_ApiClientRequest(server);

        var response = await client.RequestFullAsync(
            new GetUserRequest { Id = 1, Trace = "trace-1" },
            configure: config => config.Headers.Set("X-Trace", "trace-override"));

        Assert.Equal(200, response.Status);

        var received = Assert.Single(server.Received);
        // 调用处 configure 在模型绑定之后执行：同名的头以 configure 为准
        Assert.Equal("trace-override", received.Headers["X-Trace"]);
    }

    [Fact]
    public async Task Get_InheritedHeaderBinding_RetrievedFromBaseClass()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new Person("alice", 18)));

        using var client = CreateClient_ApiClientRequest(server);

        var response = await client.RequestFullAsync(new InheritedHeaderRequest { Id = 1, Trace = "t-9" });

        Assert.Equal(200, response.Status);

        var received = Assert.Single(server.Received);
        Assert.Equal("t-9", received.Headers["X-Trace"]);
    }

    [Fact]
    public async Task GetUri_NoRequestSent_ResolvesMergedUrl()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");

        var uri = client.GetUri(new GetUserRequest { Id = 5, Verbose = "1" });

        Assert.Equal("http://127.0.0.1:1/users/5?verbose=1", uri);
    }

    [Fact]
    public async Task Request_CanceledToken_ThrowsAxiosException()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse());

        using var client = CreateClient_ApiClientRequest(server);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAsync<AxiosException>(
            () => client.RequestFullAsync(new GetUserRequest { Id = 1 }, cts.Token));

        Assert.Equal(AxiosException.ErrCanceled, ex.Code);
    }

    [Fact]
    public async Task Request_TimeoutFromConfigureHook_AppliesToPipeline()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse
        {
            Body = TestJson.Bytes(new Person("slow", 1)),
            BodyDelay = TimeSpan.FromSeconds(2)
        });

        using var client = CreateClient_ApiClientRequest(server);

        var ex = await Assert.ThrowsAsync<AxiosException>(
            () => client.RequestFullAsync(
                new ConfiguredGetRequest { Id = 1 },
                configure: config => config.Timeout = TimeSpan.FromMilliseconds(100)));

        Assert.Equal(AxiosException.ErrConnAborted, ex.Code);
    }

    [Fact]
    public async Task Request_NonSuccessStatus_ThrowsAxiosExceptionWithResponse()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            StatusCode: 404,
            ReasonPhrase: "Not Found",
            Body: Encoding.UTF8.GetBytes("{}")));

        using var client = CreateClient_ApiClientRequest(server);

        var ex = await Assert.ThrowsAsync<AxiosException>(
            () => client.RequestFullAsync(new GetUserRequest { Id = 1 }));

        Assert.Equal(AxiosException.ErrBadRequest, ex.Code);
        Assert.Equal(404, ex.Status);
    }

    // 默认正文：POST/PUT/PATCH 上全部未标注成员按键名聚合成一个对象

    [Fact]
    public async Task Post_SingleUnannotatedComplexProperty_DefaultsToBody()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));

        using var client = CreateClient_ApiClientRequest(server);

        var response = await client.RequestFullAsync(new DefaultBodyRequest
        {
            Payload = new Person("carol", 25)
        });

        Assert.Equal(200, response.Status);

        var received = Assert.Single(server.Received);
        Assert.Equal("application/json", received.Headers["Content-Type"].Split(';')[0]);
        var json = JsonSerializer.Deserialize<JsonElement>(received.Body);
        Assert.Equal("carol", json.GetProperty("payload").GetProperty("name").GetString());
        Assert.Null(received.Query); // 没有任何查询参数
    }

    [Fact]
    public async Task Put_SingleUnannotatedComplexProperty_DefaultsToBody()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));

        using var client = CreateClient_ApiClientRequest(server);

        var response = await client.RequestFullAsync(new PutDefaultBodyRequest
        {
            Id = 7,
            Payload = new Person("dave", 40)
        });

        Assert.Equal(200, response.Status);

        var received = Assert.Single(server.Received);
        Assert.Equal("PUT", received.Method);
        Assert.Equal("/items/7", received.Path);
        var json = JsonSerializer.Deserialize<JsonElement>(received.Body);
        Assert.Equal("dave", json.GetProperty("payload").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Post_SingleUnannotatedScalarProperty_BecomesBodyMember()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient_ApiClientRequest(server);

        await client.RequestFullAsync(new ScalarDefaultBodyRequest { Page = 3 });

        var received = Assert.Single(server.Received);
        // 1 个未标注成员也加壳：{ "page": 3 }
        Assert.Equal("{\"page\":3}", Encoding.UTF8.GetString(received.Body));
        Assert.Null(received.Query);
    }

    [Fact]
    public async Task Post_TwoUnannotatedProperties_AggregateIntoBody()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient_ApiClientRequest(server);

        await client.RequestFullAsync(new TwoUnannotatedRequest { Page = 2, Payload = new Person("eve", 31) });

        var received = Assert.Single(server.Received);
        // 多个未标注属性 → 同一个对象 body，键 = 属性名（camelCase）
        var json = JsonSerializer.Deserialize<JsonElement>(received.Body);
        Assert.Equal(2, json.GetProperty("page").GetInt32());
        Assert.Equal("eve", json.GetProperty("payload").GetProperty("name").GetString());
        Assert.Null(received.Query);
    }

    [Fact]
    public async Task Post_AggregateBody_WithFormContentType_FlattensMembers()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient_ApiClientRequest(server);

        await client.RequestFullAsync(
            new TwoUnannotatedRequest { Page = 2, Payload = new Person("eve", 31) },
            config => config.ContentType = AxiosConfig.FormUrlEncodedContentType);

        var received = Assert.Single(server.Received);
        Assert.Equal("application/x-www-form-urlencoded", received.Headers["Content-Type"]);

        // 表单形态的聚合正文：成员键作为根级字段，嵌套规则与普通对象一致。
        var form = Uri.UnescapeDataString(Encoding.UTF8.GetString(received.Body));
        Assert.Contains("Page=2", form);
        Assert.Contains("Payload[Name]=eve", form);
        Assert.Null(received.Query);
    }

    [Fact]
    public async Task Post_ExplicitQueryAttribute_DoesNotBlockDefaultBody()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));

        using var client = CreateClient_ApiClientRequest(server);

        var response = await client.RequestFullAsync(new ExplicitQueryThenDefaultBodyRequest
        {
            Page = 4,
            Payload = new Person("frank", 50)
        });

        Assert.Equal(200, response.Status);

        var received = Assert.Single(server.Received);
        // 显式 [Query] 不算「未标注」：唯一未标注属性仍进聚合正文
        Assert.Contains("page=4", received.Query);
        var json = JsonSerializer.Deserialize<JsonElement>(received.Body);
        Assert.Equal("frank", json.GetProperty("payload").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Delete_UnannotatedComplexProperty_StaysQuery()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient_ApiClientRequest(server);

        await client.RequestFullAsync(new DeleteDefaultStaysQueryRequest { Id = 9, Payload = new Person("gina", 22) });

        var received = Assert.Single(server.Received);
        Assert.Equal("DELETE", received.Method);
        Assert.Contains("Payload[", Uri.UnescapeDataString(received.Query ?? "")); // DELETE 不参与默认 Body
        Assert.Empty(received.Body);
    }

    // [ApiIgnore]：属性从绑定中完全排除

    [Fact]
    public async Task Get_IgnoredProperty_IsNotSentAnywhere()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient_ApiClientRequest(server);

        await client.RequestFullAsync(new IgnoredQueryRequest { Name = "alice", CacheKey = "secret-state" });

        var received = Assert.Single(server.Received);
        Assert.Equal("Name=alice", received.Query); // 只有正常绑定进了查询串
        Assert.DoesNotContain("CacheKey", received.Query);
    }

    [Fact]
    public async Task Post_IgnoredProperty_DoesNotBlockDefaultBody()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));

        using var client = CreateClient_ApiClientRequest(server);

        var response = await client.RequestFullAsync(new IgnoredDoesNotBlockDefaultBodyRequest
        {
            InternalState = "should-not-appear",
            Payload = new Person("hank", 33)
        });

        Assert.Equal(200, response.Status);

        var received = Assert.Single(server.Received);
        // 被忽略的属性不算「未标注」：Payload 仍进聚合正文，InternalState 任何地方都不出现
        var json = JsonSerializer.Deserialize<JsonElement>(received.Body);
        Assert.Equal("hank", json.GetProperty("payload").GetProperty("name").GetString());
        Assert.Null(received.Query);
        Assert.DoesNotContain("should-not-appear", Encoding.UTF8.GetString(received.Body));
        Assert.DoesNotContain("should-not-appear", received.Query ?? "");
    }

    [Fact]
    public void Ignore_CombinedWithParameterAttribute_IsRejected()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");

        var ex = Assert.Throws<AxiosException>(
            () => client.GetUri(new IgnoreWithQueryRequest()));

        Assert.Contains("[ApiIgnore]", ex.Message);
        Assert.Contains("二义性", ex.Message);
    }

    // 多特性并存：同一属性绑定到多处

    [Fact]
    public async Task Get_PropertyInPathAndHeader_BindsBoth()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient_ApiClientRequest(server);

        await client.RequestFullAsync(new PathHeaderRequest { Id = 5 });

        var received = Assert.Single(server.Received);
        Assert.Equal("/users/5/scores", received.Path);
        Assert.Equal("5", received.Headers["X-User-Id"]);
    }

    [Fact]
    public async Task Get_PropertyInQueryAndHeader_BindsBoth()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient_ApiClientRequest(server);

        await client.RequestFullAsync(new QueryHeaderRequest { Trace = "t-42" });

        var received = Assert.Single(server.Received);
        Assert.Equal("trace=t-42", received.Query);
        Assert.Equal("t-42", received.Headers["X-Trace"]);
    }

    [Fact]
    public async Task Post_ScalarPropertyInPathBodyAndHeader_BindsAllThree()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient_ApiClientRequest(server);

        var response = await client.RequestFullAsync(new PathBodyHeaderRequest { OrderId = 88 });

        Assert.Equal(200, response.Status);

        var received = Assert.Single(server.Received);
        Assert.Equal("/orders/88", received.Path);                          // 路径
        Assert.Equal("88", received.Headers["X-Order-Id"]);                 // 请求头
        Assert.Equal("88", Encoding.UTF8.GetString(received.Body));         // 请求体（标量按 AxiosNet 规则走原始文本）
    }

    [Fact]
    public async Task Post_ComplexPropertyInQueryAndBody_BindsBoth()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        using var client = CreateClient_ApiClientRequest(server);

        var response = await client.RequestFullAsync(new QueryBodyRequest
        {
            Report = new ReportPayload(66, "weekly")
        });

        Assert.Equal(200, response.Status);

        var received = Assert.Single(server.Received);
        Assert.Contains("reportId[ReportId]=66", Uri.UnescapeDataString(received.Query ?? "")); // 查询串（嵌套规则）
        var json = JsonSerializer.Deserialize<JsonElement>(received.Body);            // 请求体
        Assert.Equal("weekly", json.GetProperty("title").GetString());
    }

    [Fact]
    public void PathOnComplexProperty_IsRejected()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");

        var ex = Assert.Throws<AxiosException>(
            () => client.GetUri(new PathOnComplexRequest()));

        Assert.Contains("Report", ex.Message);
        Assert.Contains("不是标量", ex.Message);
    }

    [Fact]
    public void HeaderOnComplexProperty_IsRejected()
    {
        using var client = new ApiClient("http://127.0.0.1:1/");

        var ex = Assert.Throws<AxiosException>(
            () => client.GetUri(new HeaderOnComplexRequest()));

        Assert.Contains("不是标量", ex.Message);
    }

// B1：未标注的平铺字典在 POST 上按成员键聚合进默认 Body

    [Fact]
    public async Task B1_UnannotatedFlatMap_OnPost_BecomesBodyMember()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestFullAsync(new FlatMapOnlyPostRequest
        {
            Filters = new Dictionary<string, string>
            {
                ["tag"] = "x",
                ["page"] = "2"
            }
        });

        var received = Assert.Single(server.Received);
        Assert.Equal("/search", received.Path);
        // 平铺字典在 POST 上不再展开进查询串，而是作为名为 filters 的成员：
        // { "filters": { "tag": "x", "page": "2" } }
        Assert.Null(received.Query);
        var json = JsonSerializer.Deserialize<JsonElement>(received.Body);
        Assert.Equal("x", json.GetProperty("filters").GetProperty("tag").GetString());
        Assert.Equal("2", json.GetProperty("filters").GetProperty("page").GetString());
    }

// B4：平铺字典不产生具名参数，不能占用「同名查询绑定」的唯一名额

    [Fact]
    public void B4_FlatMap_DoesNotConflictWithNamedQueryBinding()
    {
        using var client = new ApiClient("http://api.test/");

        var uri = client.GetUri(new FlatMapWithNamedQueryRequest
        {
            Filters = new Dictionary<string, string> { ["tag"] = "x" },
            Label = "hi"
        });

        Assert.Contains("Filters=hi", uri);
        Assert.Contains("tag=x", uri);
    }

    [Fact]
    public async Task Bindings_ValueTypeProperties_WorkThroughCachedGetters()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));
        using var client = new ApiClient(server.Url);

        // 取值走构建期缓存的委托：int / bool 等值类型属性（装箱返回）照旧绑定正确
        var response = await client.RequestFullAsync(new BoundRequest { Id = 42, Name = "n", Flag = true });

        var received = Assert.Single(server.Received);
        Assert.Equal("/items/42", received.Path);
        Assert.Equal("Name=n&Flag=true", received.Query);   // 键名用属性名；bool 文本 true/false
        Assert.Equal(200, response.Status);
    }

    private static string Body(TestRequest request) => Encoding.UTF8.GetString(request.Body);

    private static string ContentType(TestRequest request)
        => request.Headers.GetValueOrDefault("content-type") ?? string.Empty;

    // ===============================================================
    // 测试用请求模型 / 序列化器
    // ===============================================================


    [Get("probe")]
    public sealed class ProbeBadHeaderNameRequest : IApiRequest<string>
    {
        [Header("X Bad")]
        public string? Value { get; set; }
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

    private static ApiClient CreateClient(TestHttpServer server) => new(server.Url);


    private static ApiClient CreateClient_ApiClientRequest(TestHttpServer server)
        => new(server.Url);

// 测试模型

    [Post("search")]
    private sealed class FlatMapOnlyPostRequest : IApiRequest<Person>
    {
        public Dictionary<string, string> Filters { get; set; } = new();
    }

    [Get("search")]
    private sealed class FlatMapWithNamedQueryRequest : IApiRequest<Person>
    {
        public Dictionary<string, string> Filters { get; set; } = new();

        [Query("Filters")]
        public string? Label { get; set; }
    }

    [Post("items")]
    private sealed class BodyKeyRequest : IApiRequest<Person>
    {
        [Body("")]
        public string Token { get; set; } = string.Empty;

        [Body(""), Query("page")]
        public int Page { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    [Get("items")]
    private sealed class GetWithBodyRequest : IApiRequest<Person>
    {
        [Body("")]
        public string Token { get; set; } = string.Empty;

        public int Page { get; set; }
    }

    [Post("items")]
    private sealed class BodyWithQueryRequest : IApiRequest<Person>
    {
        [Body]
        public Person Payload { get; set; } = null!;

        [Query("tag")]
        public string? Tag { get; set; }

        [Query("page")]
        public int Page { get; set; }
    }

    [Post("items")]
    private sealed class BodyWithUnannotatedRequest : IApiRequest<Person>
    {
        [Body]
        public Person Payload { get; set; } = null!;

        public int Page { get; set; }
    }

    [Post("items")]
    private sealed class RawBodyRequest : IApiRequest<Person>
    {
        [Body]
        public Person Payload { get; set; } = null!;
    }

    [Post("items")]
    private sealed class WholeBodyAndKeyRequest : IApiRequest<Person>
    {
        [Body("payload")]
        public Person Payload { get; set; } = null!;

        [Body]
        public Person Extra { get; set; } = null!;
    }

    [Post("items")]
    private sealed class TwoWholeBodyRequest : IApiRequest<Person>
    {
        [Body]
        public int A { get; set; } = 1;

        [Body]
        public MergeSlice Slice { get; set; } = new();

        [Body]
        public int C { get; set; } = 2;
    }

    private sealed class MergeSlice
    {
        [JsonPropertyName("u1")]
        public int U1 { get; set; } = 1;

        [JsonPropertyName("u2")]
        public int U2 { get; set; } = 2;
    }

    [Post("items")]
    private sealed class MixedOrderRequest : IApiRequest<Person>
    {
        [Body("a")]
        public int First { get; set; } = 1;

        [Body("b")]
        public int Second { get; set; } = 2;

        [JsonPropertyName("d")]
        public int Fourth { get; set; } = 4;
    }

    [Post("items")]
    private sealed class BodyNameRequest : IApiRequest<Person>
    {
        [Body("user_name")]
        [JsonPropertyName("ignored")]
        public string UserName { get; set; } = "n";

        [Body("UserId")]
        public int Id { get; set; } = 2;
    }

    [Post("items")]
    private sealed class BlankBodyNameRequest : IApiRequest<Person>
    {
        [Body("")]
        [JsonPropertyName("jp")]
        public int Value { get; set; } = 1;
    }

    [Post("items")]
    private sealed class DuplicateBodyNameRequest : IApiRequest<Person>
    {
        [Body("same")]
        public int First { get; set; } = 1;

        [Body("same")]
        public int Second { get; set; } = 2;
    }

    [Post("form")]
    [BodyContentType("application/x-www-form-urlencoded")]
    private sealed class CaseDifferingFormBodyNameRequest : IApiRequest<Person>
    {
        [Body("a")]
        public string Lower { get; set; } = "1";

        [Body("A")]
        public string Upper { get; set; } = "2";
    }

    [Multipart]
    [Post("upload")]
    private sealed class EmptyJsonPropertyNamePartRequest : IApiRequest<Person>
    {
        [JsonPropertyName("")]
        public string Value { get; set; } = "x";
    }

    [Post("items")]
    private sealed class SingleExplicitBodyRequest : IApiRequest<Person>
    {
        [Body("x")]
        public string Value { get; set; } = "v";
    }

    private sealed class BlankFieldNameSample
    {
    }

    public interface ICaseDifferingFormBodyApi
    {
        [Post("form")]
        [BodyContentType("application/x-www-form-urlencoded")]
        Task<AxiosResponse<Person>> SubmitAsync([Body("a")] string lower, [Body("A")] string upper);
    }

    [Post("items")]
    private sealed class CaseDifferingBodyNameRequest : IApiRequest<Person>
    {
        [Body("Tag")]
        public string First { get; set; } = "a";

        [Body("tag")]
        public string Second { get; set; } = "b";
    }

    [Post("items")]
    private sealed class BodySameNameJsonPropertyRequest : IApiRequest<Person>
    {
        [Body("")]
        [JsonPropertyName("Tag")]
        public string Tag { get; set; } = "t";
    }

    [Post("items")]
    private sealed class UnannotatedSameNameJsonPropertyRequest : IApiRequest<Person>
    {
        [JsonPropertyName("Tag")]
        public string Tag { get; set; } = "t";
    }

    [Post("items")]
    private sealed class TwoKeyFormRequest : IApiRequest<Person>
    {
        [Body("first")]
        public int First { get; set; } = 1;

        [Body("second")]
        public int Second { get; set; } = 2;
    }

    [Post("items")]
    private sealed class RuntimeStreamBodyRequest : IApiRequest<Person>
    {
        [Body("")]
        public object? Data { get; set; }
    }

    [Post("items")]
    private sealed class BodyStreamRequest : IApiRequest<Person>
    {
        [Body("")]
        public Stream? Data { get; set; }
    }

    [Post("items")]
    private sealed class BodyFormFileRequest : IApiRequest<Person>
    {
        [Body("")]
        public FormFile? File { get; set; }
    }

    /// <summary>记录是否被释放的流：验证正文产出失败时成员里的流会按 FormFile 契约回收。</summary>
    private sealed class DisposeTrackingStream : MemoryStream
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }


// 测试替身


    /// <summary>预置分块的响应流；可为每个分块指定读取前的延迟，读完后挂起。</summary>
    private sealed class PacedStream : Stream
    {
        private readonly byte[][] _chunks;
        private readonly TimeSpan[] _delays;
        private readonly bool _stallAfterChunks;
        private int _index;
        private int _offset;

        public PacedStream(byte[][] chunks, TimeSpan[] delays, bool stallAfterChunks)
        {
            _chunks = chunks;
            _delays = delays;
            _stallAfterChunks = stallAfterChunks;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_index >= _chunks.Length)
            {
                if (_stallAfterChunks)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                }

                return 0;
            }

            if (_offset == 0 && _index < _delays.Length && _delays[_index] > TimeSpan.Zero)
            {
                await Task.Delay(_delays[_index], cancellationToken).ConfigureAwait(false);
            }

            var chunk = _chunks[_index];
            var count = Math.Min(buffer.Length, chunk.Length - _offset);
            chunk.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            if (_offset >= chunk.Length)
            {
                _index++;
                _offset = 0;
            }

            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(Span<byte> buffer)
        {
            var temp = new byte[buffer.Length];
            var read = ReadAsync(temp.AsMemory(), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            temp.AsSpan(0, read).CopyTo(buffer);
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }


// 辅助


    /// <summary>预置正文的流式响应，Content-Type 可配置（验证非 UTF-8 charset 的逐行解码）。</summary>
    private sealed class CharsetAwareHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly string _contentType;

        public CharsetAwareHandler(string contentType, byte[] body)
        {
            _contentType = contentType;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StreamContent(new MemoryStream(_body));
            // Parse 才接受带参数的完整头值；MediaTypeHeaderValue 的构造函数只收裸媒体类型
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(_contentType);

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request
            };

            return Task.FromResult(response);
        }
    }


    [Get("items/{id}")]
    private sealed class BoundRequest : IApiRequest<Person>
    {
        [Path]
        public int Id { get; set; }

        [Query]
        public string? Name { get; set; }

        [Query]
        public bool Flag { get; set; }
    }
}
