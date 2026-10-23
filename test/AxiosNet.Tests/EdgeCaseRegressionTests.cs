using System.Globalization;
using System.Text;
using System.Text.Json;
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests;

/// <summary>
/// 边界值 / 特殊场景回归：这一批用例对应「params 根类型走反射」「拦截器返回 null」
/// 「无正文时内容头被静默丢弃」「validateStatus 被调用两次」「url 以 ? 结尾再拼查询串」
/// 「超长 URL 把整条串写进异常消息」等只在非常规输入下才暴露的缺陷。
/// </summary>
public sealed class EdgeCaseRegressionTests
{
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

    // ------------------------------------------------------------------
    // 拦截器返回 null
    // ------------------------------------------------------------------

    [Fact]
    public async Task RequestInterceptor_ReturningNull_ThrowsBadOptionValue_NotNullReference()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        Func<AxiosConfig, Task<AxiosConfig>> returnsNull = _ => Task.FromResult<AxiosConfig>(null!);
        client.Interceptors.Request.Use(returnsNull);

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("请求拦截器", ex.Message);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task RequestInterceptorRejected_ReturningNull_AlsoThrowsBadOptionValue()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        Func<AxiosConfig, Task<AxiosConfig>> boom = _ => throw new InvalidOperationException("boom");
        Func<Exception, Task<AxiosConfig>> returnsNull = _ => Task.FromResult<AxiosConfig>(null!);
        client.Interceptors.Request.Use(boom, returnsNull);

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task ResponseInterceptor_ReturningNull_ThrowsBadOptionValue()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        Func<AxiosResponse, Task<AxiosResponse>> returnsNull = _ => Task.FromResult<AxiosResponse>(null!);
        client.Interceptors.Response.Use(returnsNull);

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("响应拦截器", ex.Message);
    }

    [Fact]
    public async Task ResponseInterceptorRejected_ReturningNull_ThrowsBadOptionValue()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = true }, statusCode: 500));
        using var client = new AxiosClient();

        Func<AxiosResponse, Task<AxiosResponse>> identity = response => Task.FromResult(response);
        Func<Exception, Task<AxiosResponse>> returnsNull = _ => Task.FromResult<AxiosResponse>(null!);
        client.Interceptors.Response.Use(identity, returnsNull);

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    [Fact]
    public async Task TypedRequest_WithNullReturningResponseInterceptor_DoesNotThrowNullReference()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        Func<AxiosResponse, Task<AxiosResponse>> returnsNull = _ => Task.FromResult<AxiosResponse>(null!);
        client.Interceptors.Response.Use(returnsNull);

        // 之前 GetAsync<T> 会在 AsTyped 上抛裸 NullReferenceException
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<JsonElement>(server.Url));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
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

        Assert.Equal(
            "attachment; filename=a.txt",
            server.Received[0].Headers.GetValueOrDefault("content-disposition"));
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
    // validateStatus 只判定一次
    // ------------------------------------------------------------------

    [Fact]
    public async Task ValidateStatus_IsInvokedExactlyOncePerRequest()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        var calls = 0;
        await client.GetAsync(server.Url, c => c.Callbacks.ValidateStatus = _ =>
        {
            calls++;
            return true;
        });

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ValidateStatus_IsInvokedOnce_ForErrorStatusToo()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { error = true }, statusCode: 500));
        using var client = new AxiosClient();

        var calls = 0;
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url, c =>
            c.Callbacks.ValidateStatus = _ =>
            {
                calls++;
                return false;
            }));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);
        Assert.Equal(1, calls);
    }

    // ------------------------------------------------------------------
    // 泛型参数与 responseType 不匹配时的流式正文
    // ------------------------------------------------------------------

    [Fact]
    public async Task TypedRequest_WithMismatchedStreamResponse_ReleasesLease()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        // responseType: stream 的正文是 Stream(<Stream>)，GetAsync<byte[]> 的转换必然失败。
        // 此时响应交不到调用方手里，正文必须由库释放 —— 否则租约永不归还（之前 ActiveLeases 停在 1）。
        var ex = await Assert.ThrowsAsync<AxiosException>(
            () => client.GetAsync<byte[]>(server.Url, c => c.ResponseType = ResponseType.Stream));

        Assert.Equal(AxiosException.ErrBadResponse, ex.Code);

        var endpoints = client.Pool!.GetStatistics().Endpoints;
        Assert.All(endpoints, endpoint => Assert.Equal(0, endpoint.ActiveLeases));
    }

    [Fact]
    public async Task TypedRequest_AfterMismatchedStreamResponse_PoolStillUsable()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await Assert.ThrowsAsync<AxiosException>(
            () => client.GetAsync<byte[]>(server.Url, c => c.ResponseType = ResponseType.Stream));

        var response = await client.GetAsync(server.Url);
        Assert.Equal(200, response.Status);
        Assert.All(
            client.Pool!.GetStatistics().Endpoints,
            endpoint => Assert.Equal(0, endpoint.ActiveLeases));
    }

    // ------------------------------------------------------------------
    // 池选项：Timeout.InfiniteTimeSpan 表示「不限」
    // ------------------------------------------------------------------

    [Fact]
    public async Task PoolOptions_InfiniteTimeSpan_IsAccepted_AndRequestSucceeds()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            IdleHandlerLifetime = Timeout.InfiniteTimeSpan,
            ConnectTimeout = Timeout.InfiniteTimeSpan
        });

        // 之前这三项用 -1ms（InfiniteTimeSpan）会被判成「负数」直接抛错，
        // 而报错文案本身写的就是「不限请用 Timeout.InfiniteTimeSpan」——照文档改只会再炸一次。
        using var client = new AxiosClient(pool: pool);
        var response = await client.GetAsync(server.Url);

        Assert.Equal(200, response.Status);
        Assert.Equal(Timeout.InfiniteTimeSpan, pool.Options.PooledConnectionLifetime);
    }

    [Fact]
    public void PoolOptions_NegativeButNotInfinite_StillThrows()
    {
        var ex = Assert.Throws<AxiosException>(() => new HttpConnectionPool(new ConnectionPoolOptions
        {
            PooledConnectionLifetime = TimeSpan.FromSeconds(-5)
        }));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
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
        Assert.Equal(
            payload.Length.ToString(CultureInfo.InvariantCulture),
            sent.Headers.GetValueOrDefault("Content-Length"));
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

    // ------------------------------------------------------------------
    // 池级错误也带 Config
    // ------------------------------------------------------------------

    [Fact]
    public async Task PoolCapacityError_FromRequest_CarriesConfig()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            MaxEndpointCount = 1,
            EvictionInterval = TimeSpan.Zero,
            IdleHandlerLifetime = TimeSpan.Zero
        });

        // 先把唯一的 origin 名额占住（另一个 origin 的租约，不发请求）
        using var busy = pool.Lease(new Uri("http://127.0.0.1:1/warmup"));

        using var client = new AxiosClient(pool: pool);
        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync(server.Url));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        // 请求期从池里抛出的错误同样要指回本次请求的配置，而不是 Config == null
        Assert.NotNull(ex.Config);
        Assert.Empty(server.Received);
    }

    // ------------------------------------------------------------------
    // 超长 URL 的异常消息
    // ------------------------------------------------------------------

    [Fact]
    public void InvalidUrl_WithHugeQuery_DoesNotEmbedWholeUrlInMessage()
    {
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://example.com/" });
        var huge = new string('a', 200_000);

        // .NET 的 Uri 上限是 65519 字符，超长查询串只能报 ERR_INVALID_URL；
        // 但消息里不该塞进整条 URL（否则一条日志几十万字符）。
        var ex = Assert.Throws<AxiosException>(() => client.GetUri(c =>
        {
            c.Url = "p";
            c.Params = new { q = huge };
        }));

        Assert.Equal(AxiosException.ErrInvalidUrl, ex.Code);
        Assert.True(ex.Message.Length < 1024, $"异常消息长度 {ex.Message.Length}");
        Assert.Contains("truncated", ex.Message);
    }

    // ------------------------------------------------------------------
    // 实例级选项：按请求写「相同的值」放行，写「不同的值」才报错
    // ------------------------------------------------------------------

    [Fact]
    public async Task PerRequestMaxRedirects_EqualToPoolOptionsValue_IsAccepted()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        // defaults 上没写 MaxRedirects（属性值仍是 5），真正生效的是池上的 3：
        // 判定必须读池的实际策略，否则这里会被误判成「与生效值 5 不同」而报错。
        using var client = new AxiosClient(
            poolOptions: new ConnectionPoolOptions { MaxAutomaticRedirections = 3 });

        var response = await client.GetAsync(server.Url + "x", c => c.Connection.MaxRedirects = 3);

        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task PerRequestMaxRedirects_Zero_MatchesDisabledRedirectPool_IsAccepted()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        // AllowAutoRedirect = false 时生效值就是 0（不跟随），按请求写 0 属于等价写法
        using var client = new AxiosClient(
            poolOptions: new ConnectionPoolOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync(server.Url + "x", c => c.Connection.MaxRedirects = 0);

        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task PerRequestMaxRedirects_DiffersFromEffectiveValue_Throws()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            Connection = new()
            {
                MaxRedirects = 5,
            },
        });

        // 4 与生效值 5 不同：不会生效，必须响亮报错，而不是静默丢弃
        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url + "x", c => c.Connection.MaxRedirects = 4));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task PerRequestWithCredentials_EqualToInstanceDefault_IsAccepted()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        // 实例没开 WithCredentials（false），按请求写 false 是等价写法
        var response = await client.GetAsync(server.Url + "x", c => c.Connection.WithCredentials = false);

        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task PerRequestWithCredentials_DiffersFromInstance_Throws()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            Connection = new()
            {
                WithCredentials = true,
            },
        });

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync(server.Url + "x", c => c.Connection.WithCredentials = false));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task InjectedHandler_PerRequestWithCredentials_AlwaysThrows()
    {
        // 注入 Handler 时无从得知它的 Cookie 策略，无法判断「是否等价」，即使写 false 也报错
        using var client = new AxiosClient(handler: new HttpClientHandler());

        var ex = await Assert.ThrowsAsync<AxiosException>(() =>
            client.GetAsync("http://127.0.0.1:1/x", c => c.Connection.WithCredentials = false));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
    }

    // ------------------------------------------------------------------
    // 上面的判定依赖「按请求到底写没写」的 *Set 标记：
    // perCall 只含按请求项，缺省值是 WithCredentials=false / MaxRedirects=5，
    // 若改成纯值比较，下面这两条「按请求根本没写」的正常请求会被误判成写了不同的值。
    // ------------------------------------------------------------------

    [Fact]
    public async Task PerRequestOptions_Omitted_WithCredentialsTrue_Instance_StillWorks()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            Connection = new()
            {
                WithCredentials = true,
            },
        });

        var response = await client.GetAsync(server.Url + "x");

        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task PerRequestOptions_Omitted_PoolRedirectLimit3_StillWorks()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(
            poolOptions: new ConnectionPoolOptions { MaxAutomaticRedirections = 3 });

        var response = await client.GetAsync(server.Url + "x");

        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);
    }
}

/// <summary>
/// 只读、非 seekable 的流：模拟网络流 / 管道流（<see cref="Stream.Length"/> 不可知），
/// 用来覆盖「内容长度未知时手写 Content-Length」的逃生口。
/// </summary>
internal sealed class NonSeekableStream : Stream
{
    private readonly MemoryStream _inner;

    public NonSeekableStream(byte[] payload) => _inner = new MemoryStream(payload, writable: false);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
