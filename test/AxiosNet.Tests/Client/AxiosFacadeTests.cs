
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using System.Text;

namespace AxiosNet.Tests.Client;

/// <summary>
/// 静态门面 `Axios`：默认配置、拦截器、快捷方法、共享连接池。
/// </summary>
public sealed class AxiosFacadeTests
{
    [Fact]
    public async Task StaticAxios_GetAsync_WorksLikeAxiosGet()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("static", 1)));

        var response = await Axios.GetAsync<Person>(server.Url + "static-call");

        Assert.Equal("static", response.Data!.Name);
        Assert.Equal("/static-call", server.Received[0].Path);
    }

    [Fact]
    public void StaticAxios_GetUri_WorksLikeFacade()
    {
        var uri = Axios.GetUri(c =>
        {
            c.BaseAddress = "https://example.com/";
            c.Url = "a";
            c.Params = new { q = "x y" };
        });

        Assert.Equal("https://example.com/a?q=x%20y", uri);
    }

    // ------------------------------------------------------------------
    // L12：Axios.Create 支持共享池
    // ------------------------------------------------------------------

    [Fact]
    public async Task AxiosCreate_AcceptsSharedPool()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var pool = new HttpConnectionPool();

        using var client = Axios.Create(new AxiosConfig(), pool);

        Assert.Same(pool, client.Pool);
        var response = await client.GetAsync(server.Url + "x");
        Assert.Equal(200, response.Status);
        Assert.Equal(1, pool.GetStatistics().EndpointCount);
    }

    [Fact]
    public async Task AxiosCreate_WithConfigureAction_AcceptsSharedPool()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var pool = new HttpConnectionPool();

        using var client = Axios.Create(c => c.BaseAddress = server.Url, pool);

        Assert.Same(pool, client.Pool);
        var response = await client.GetAsync("x");
        Assert.Equal(200, response.Status);
    }

    // ------------------------------------------------------------------
    // 门面转发：每个重载都要真的落到默认实例上（否则「加了重载但没接上」不会被发现）
    // ------------------------------------------------------------------

    [Fact]
    public async Task Facade_ShortcutOverloads_ForwardVerbDataAndConfigure()
    {
        // 固定回一个合法 Person：这组用例只验证「重载有没有接到管线上」，正文形状交给响应类型用例
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        var text = await Axios.GetAsync(server.Url + "s1");
        var typed = await Axios.PostAsync<Person>(server.Url + "s2", new Person("bob", 2));
        var withData = await Axios.PutAsync(server.Url + "s3", new { n = 3 });
        var typedWithData = await Axios.PatchAsync<Person>(server.Url + "s4", new Person("ann", 4));
        var deleted = await Axios.DeleteAsync(server.Url + "s5");
        var deletedTyped = await Axios.DeleteAsync<Person>(server.Url + "s6", new Person("del", 6));
        var head = await Axios.HeadAsync(server.Url + "s7");
        var headTyped = await Axios.HeadAsync<Person>(server.Url + "s8", new Person("head", 8));
        var options = await Axios.OptionsAsync(server.Url + "s9");
        var optionsTyped = await Axios.OptionsAsync<Person>(server.Url + "s10", new Person("opt", 10));
        var configured = await Axios.GetAsync(server.Url + "s11", c => c.Headers.Set("X-Facade", "1"));
        var configuredTyped = await Axios.GetAsync<Person>(server.Url + "s12", c => c.Headers.Set("X-Facade", "2"));
        var dataAndConfigure = await Axios.GetAsync(server.Url + "s13", new { q = 13 }, c => c.Headers.Set("X-Facade", "3"));
        var dataAndConfigureTyped = await Axios.GetAsync<Person>(server.Url + "s14", new { q = 14 }, c => c.Headers.Set("X-Facade", "4"));

        Assert.All(
            new[] { text, typed, withData, typedWithData, deleted, deletedTyped, head, headTyped, options, optionsTyped,
                    configured, configuredTyped, dataAndConfigure, dataAndConfigureTyped },
            response => Assert.Equal(200, response.Status));

        var methods = server.Received.Select(r => r.Method).ToArray();
        Assert.Equal(
            new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "DELETE", "HEAD", "HEAD", "OPTIONS", "OPTIONS", "GET", "GET", "GET", "GET" },
            methods);
        Assert.Equal("3", Header(server.Received.Single(r => r.Path == "/s13"), "x-facade"));
        Assert.Equal("4", Header(server.Received.Single(r => r.Path == "/s14"), "x-facade"));
    }

    [Fact]
    public async Task Facade_FormShortcuts_DefaultToMultipart()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ok", 1)));

        var posted = await Axios.PostFormAsync(server.Url + "f1", new { a = 1 });
        var postedTyped = await Axios.PostFormAsync<Person>(server.Url + "f2", new { a = 2 });
        var put = await Axios.PutFormAsync(server.Url + "f3", new { a = 3 });
        var putTyped = await Axios.PutFormAsync<Person>(server.Url + "f4", new { a = 4 });
        var patched = await Axios.PatchFormAsync(server.Url + "f5", new { a = 5 });
        var patchedTyped = await Axios.PatchFormAsync<Person>(server.Url + "f6", new { a = 6 });

        Assert.All(new[] { posted, postedTyped, put, putTyped, patched, patchedTyped },
            response => Assert.Equal(200, response.Status));

        foreach (var request in server.Received)
        {
            Assert.StartsWith("multipart/form-data", Header(request, "content-type"));
        }
    }

    [Fact]
    public async Task Facade_TokenOverloads_ForwardTheToken()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // 每个令牌重载都必须把令牌传到管线里，否则取消会被静默忽略
        var calls = new Func<Task>[]
        {
            () => Axios.GetAsync(server.Url + "t1", cts.Token),
            () => Axios.GetAsync<Person>(server.Url + "t2", cts.Token),
            () => Axios.DeleteAsync(server.Url + "t3", cts.Token),
            () => Axios.DeleteAsync<Person>(server.Url + "t4", cts.Token),
            () => Axios.HeadAsync(server.Url + "t5", cts.Token),
            () => Axios.HeadAsync<Person>(server.Url + "t6", cts.Token),
            () => Axios.OptionsAsync(server.Url + "t7", cts.Token),
            () => Axios.OptionsAsync<Person>(server.Url + "t8", cts.Token),
            () => Axios.PostAsync(server.Url + "t9", cts.Token),
            () => Axios.PostAsync<Person>(server.Url + "t10", cts.Token),
            () => Axios.PutAsync(server.Url + "t11", cts.Token),
            () => Axios.PutAsync<Person>(server.Url + "t12", cts.Token),
            () => Axios.PatchAsync(server.Url + "t13", cts.Token),
            () => Axios.PatchAsync<Person>(server.Url + "t14", cts.Token),
            () => Axios.PostFormAsync(server.Url + "t15", cts.Token),
            () => Axios.PostFormAsync<Person>(server.Url + "t16", cts.Token),
            () => Axios.PutFormAsync(server.Url + "t17", cts.Token),
            () => Axios.PutFormAsync<Person>(server.Url + "t18", cts.Token),
            () => Axios.PatchFormAsync(server.Url + "t19", cts.Token),
            () => Axios.PatchFormAsync<Person>(server.Url + "t20", cts.Token),
        };

        foreach (var call in calls)
        {
            var error = await Assert.ThrowsAsync<AxiosException>(call);
            Assert.Equal(AxiosException.ErrCanceled, error.Code);
        }

        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Facade_RequestAsyncOverloads_ShareTheDefaultPipeline()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));

        var plain = await Axios.RequestAsync(new AxiosConfig { Url = server.Url + "r1" });
        var typed = await Axios.RequestAsync<Person>(new AxiosConfig
        {
            Url = server.Url + "r2",
            Method = HttpMethod.Post,
            Data = new Person("replay", 2)
        });
        var configured = await Axios.RequestAsync(c => c.Url = server.Url + "r3");
        var configuredTyped = await Axios.RequestAsync<Person>(c =>
        {
            c.Url = server.Url + "r4";
            c.Data = new Person("cfg", 4);
        });

        Assert.Equal(200, plain.Status);
        Assert.Equal("replay", typed.Data!.Name);
        Assert.Equal(200, configured.Status);
        Assert.Equal("cfg", configuredTyped.Data!.Name);
    }

    [Fact]
    public void Facade_ExposesDefaultsAndInterceptors()
    {
        Assert.Same(Axios.DefaultClient.Defaults, Axios.Defaults);
        Assert.Same(Axios.DefaultClient.Interceptors, Axios.Interceptors);
        Assert.Same(Axios.DefaultClient, Axios.DefaultClient);

        var requestId = Axios.Interceptors.Request.Use((AxiosConfig c) => c);
        var responseId = Axios.Interceptors.Response.Use((AxiosResponse r) => r);
        try
        {
            Assert.True(Axios.Interceptors.Request.Count >= 1);
            Assert.True(Axios.Interceptors.Response.Count >= 1);
            Assert.True(Axios.Interceptors.Request.Eject(requestId));
            Assert.True(Axios.Interceptors.Response.Eject(responseId));
        }
        finally
        {
            Axios.Interceptors.Request.Eject(requestId);
            Axios.Interceptors.Response.Eject(responseId);
        }
    }

    [Fact]
    public async Task Facade_Predicates_ClassifyErrorsLikeAxios()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(404, Body: Encoding.UTF8.GetBytes("{}")));

        var error = await Assert.ThrowsAsync<AxiosException>(() => Axios.GetAsync(server.Url + "missing"));

        Assert.True(Axios.IsAxiosError(error));
        Assert.False(Axios.IsAxiosError(new InvalidOperationException()));
        Assert.False(Axios.IsAxiosError(null));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var canceled = await Assert.ThrowsAsync<AxiosException>(() => Axios.GetAsync(server.Url + "x", cts.Token));
        Assert.True(Axios.IsCancel(canceled));
        Assert.True(Axios.IsCancel(new OperationCanceledException()));
        Assert.False(Axios.IsCancel(error));
    }

    [Fact]
    public void Facade_Create_GuardsNullConfigureAction()
    {
        Assert.Throws<ArgumentNullException>(() => Axios.Create((Action<AxiosConfig>)null!));

        using var fromDefaults = Axios.Create(new AxiosConfig { BaseAddress = "https://example.com/" });
        using var fromAction = Axios.Create(c => c.Timeout = TimeSpan.FromSeconds(3));

        Assert.Equal("https://example.com/", fromDefaults.Defaults.BaseAddress);
        Assert.Equal(TimeSpan.FromSeconds(3), fromAction.Defaults.Timeout);
    }

    private static string? Header(TestRequest request, string name)
        => request.Headers.TryGetValue(name, out var value) ? value : null;

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
}