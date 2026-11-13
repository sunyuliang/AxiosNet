using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AxiosNet;
using AxiosNet.Cache;
using AxiosNet.Metadata;
using AxiosNet.Retry;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Attributes;

namespace AxiosNet.Compat.Tests;

/// <summary>
/// 兼容资产的能力冒烟：用最小 TcpListener 服务器把「基本请求 / PATCH / 重试 / 缓存 / 流式 / multipart /
/// 声明式 API / <c>[Format]</c>（模块初始化器）」在真实运行时上各跑一遍。
/// 这些路径在 netstandard2.0 上走的是 polyfill / shim / 扩展方法转发，与 net6+ 的代码路径不同，
/// 只靠类型存在性断言覆盖不到。
/// </summary>
public sealed class CompatIntegrationTests
{
    [Fact]
    public async Task 基本请求与_JSON_反序列化()
    {
        using var server = MiniServer.Start((_, _, _, _) => (200, "application/json", Utf8.Text("{\"name\":\"ada\",\"age\":36}")));
        using var client = new AxiosClient();

        var response = await client.GetAsync<Person>(server.Url + "x");

        Assert.Equal("ada", response.Data!.Name);
        Assert.Equal(36, response.Data.Age);
        Assert.Equal("GET", server.Methods.First());
    }

    [Fact]
    public async Task PATCH_方法在兼容资产上正确发送()
    {
        using var server = MiniServer.Start((_, _, _, _) => (200, "application/json", Utf8.Text("{}")));
        using var client = new AxiosClient();

        await client.PatchAsync(server.Url + "x", new { a = 1 });

        Assert.Equal("PATCH", server.Methods.First());
    }

    [Fact]
    public async Task 重试在兼容资产上生效()
    {
        var calls = 0;
        using var server = MiniServer.Start((_, _, _, _) =>
            Interlocked.Increment(ref calls) == 1
                ? (503, "text/plain", Utf8.Text("busy"))
                : (200, "application/json", Utf8.Text("{\"name\":\"ok\",\"age\":1}")));

        using var client = new AxiosClient(new AxiosConfig
        {
            Resilience = { Retry = { Policy = RetryPolicy.Constant(2, TimeSpan.Zero) } }
        });

        var response = await client.GetAsync<Person>(server.Url + "x");

        Assert.Equal("ok", response.Data!.Name);
        Assert.Equal(2, server.Methods.Count);
    }

    [Fact]
    public async Task 响应缓存在兼容资产上命中时不发请求()
    {
        using var server = MiniServer.Start((_, _, _, _) => (200, "application/json", Utf8.Text("{\"name\":\"c\",\"age\":1}")));
        using var client = new AxiosClient(new AxiosConfig
        {
            Caching = new AxiosCache(new AxiosCacheOptions { TimeToLive = TimeSpan.FromMinutes(1) })
        });

        await client.GetAsync<Person>(server.Url + "x");
        await client.GetAsync<Person>(server.Url + "x");

        Assert.Single(server.Methods);
    }

    [Fact]
    public async Task 流式_SSE_在兼容资产上逐帧交付()
    {
        const string sse = "data: {\"name\":\"a\",\"age\":1}\n\ndata: {\"name\":\"b\",\"age\":2}\n\n";
        using var server = MiniServer.Start((_, _, _, _) => (200, "text/event-stream", Utf8.Text(sse)));

        using var client = new ApiClient(server.Url);

        var response = await client.RequestStreamAsync<Person>(
            new StreamRequest(),
            AxiosStreamFormat.ServerSentEvents);

        var names = new List<string>();
        await foreach (var item in response.Data!)
        {
            names.Add(item.Name);
        }

        Assert.Equal(new[] { "a", "b" }, names);
    }

    [Fact]
    public async Task multipart_表单在兼容资产上按_FormData_发送()
    {
        using var server = MiniServer.Start((_, _, _, _) => (200, "application/json", Utf8.Text("{}")));
        using var client = new AxiosClient();

        var form = new FormData { SourceName = "测试表单", ["field"] = "value" };
        await client.PostFormAsync(server.Url + "upload", form);

        Assert.Contains("multipart/form-data", server.ContentTypes.First());
        Assert.Contains("field", server.Bodies.First());
        Assert.Contains("value", server.Bodies.First());
    }

    [Fact]
    public async Task 声明式请求模型在兼容资产上走通完整管线()
    {
        using var server = MiniServer.Start((_, _, _, _) => (200, "application/json", Utf8.Text("{\"name\":\"decl\",\"age\":7}")));
        using var client = new ApiClient(server.Url);

        var response = await client.RequestAsync(new GetPersonRequest { Id = 42, Verbose = "1" });

        Assert.Equal("decl", response.Data!.Name);
        Assert.Equal("/people/42", server.Paths.First());
        Assert.Contains("verbose=1", server.Paths.First() + server.Queries.First());
    }

    [Fact]
    public async Task Format_特性在兼容资产上生效_模块初始化器已执行()
    {
        using var server = MiniServer.Start((_, _, _, _) => (200, "application/json", Utf8.Text("{}")));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new FormattedRequest { Since = new DateTime(2026, 11, 13) });

        // [Format("yyyy-MM-dd")] 只有模块初始化器把格式化读取器注册进内核才会生效；
        // 未注册时输出会退化成 ISO / 区域性默认格式。
        var query = Uri.UnescapeDataString(server.Queries.First());
        Assert.Contains("since=2026-11-13", query);
    }

    [Fact]
    public async Task 下载进度回调在兼容资产上按字节累计()
    {
        var body = Utf8.Text("{\"name\":\"metered\",\"age\":3}");
        using var server = MiniServer.Start((_, _, _, _) => (200, "application/json", body));

        var events = new List<AxiosProgressEvent>();
        using var client = new AxiosClient();

        await client.GetAsync<Person>(server.Url + "x", c => c.Observability.OnDownloadProgress = events.Add);

        // 下载侧计量挂在 ProgressReadStream 上，netstandard2.0 没有 Stream.ReadAsync(Memory) 虚方法，
        // 调用要靠扩展方法转回数组重载才会落到重写实现上；这里就是那条链路的端到端证据。
        Assert.NotEmpty(events);
        var last = events[events.Count - 1];
        Assert.False(last.Upload);
        Assert.Equal(body.Length, last.Loaded);
        Assert.Equal(body.Length, last.Total);
        Assert.Equal(1d, last.Progress!.Value);
    }

    [Fact]
    public async Task 上传进度回调在兼容资产上按字节累计()
    {
        using var server = MiniServer.Start((_, _, _, _) => (200, "application/json", Utf8.Text("{}")));

        var events = new List<AxiosProgressEvent>();
        using var client = new AxiosClient();

        await client.PostAsync(
            server.Url + "x",
            new { payload = new string('x', 4096) },
            c => c.Observability.OnUploadProgress = events.Add);

        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.True(e.Upload));

        var last = events[events.Count - 1];
        Assert.True(last.Loaded > 0);
        Assert.Equal(last.Total, last.Loaded);
    }

    [Fact]
    public async Task 原始响应字节回调在兼容资产上拿到已解压正文()
    {
        var body = Utf8.Text("{\"name\":\"raw\",\"age\":4}");
        using var server = MiniServer.Start((_, _, _, _) => (200, "application/json", body));

        var captured = Array.Empty<byte>();
        using var client = new AxiosClient();

        await client.GetAsync<Person>(
            server.Url + "x",
            c => c.Observability.OnRawResponse = (_, bytes) => captured = bytes.ToArray());

        Assert.Equal(body, captured);
    }

    [Fact]
    public async Task 请求与响应拦截器在兼容资产上按序生效()
    {
        using var server = MiniServer.Start((_, _, _, _) => (200, "application/json", Utf8.Text("{\"name\":\"chain\",\"age\":5}")));
        using var client = new AxiosClient();

        var order = new List<string>();

        client.Interceptors.Request.Use(c =>
        {
            order.Add("request");
            c.Headers.Set("X-Trace", "intercepted");
            return c;
        });

        client.Interceptors.Response.Use(r =>
        {
            order.Add("response");
            return r;
        });

        var response = await client.GetAsync<Person>(server.Url + "x");

        Assert.Equal(new[] { "request", "response" }, order);
        Assert.Equal("intercepted", server.Traces.First());
        Assert.Equal("chain", response.Data!.Name);
    }

    [Fact]
    public async Task 调用方取消在兼容资产上映射为_ERR_CANCELED()
    {
        using var server = MiniServer.Start((_, _, _, _) =>
        {
            Thread.Sleep(3000);
            return (200, "application/json", Utf8.Text("{}"));
        });

        using var client = new AxiosClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var error = await Record.ExceptionAsync(() => client.GetAsync(server.Url + "x", null, cts.Token));

        var axios = Assert.IsAssignableFrom<AxiosException>(error);
        Assert.Equal(AxiosException.ErrCanceled, axios.Code);
    }

    [Fact]
    public async Task 对冲在兼容资产上返回首个成功结果()
    {
        using var server = MiniServer.Start((_, _, _, _) =>
        {
            Thread.Sleep(120);
            return (200, "application/json", Utf8.Text("{\"name\":\"hedged\",\"age\":9}"));
        });

        using var client = new AxiosClient(new AxiosConfig
        {
            Resilience = { Hedging = { MaxHedges = 1, Delay = TimeSpan.Zero } }
        });

        var response = await client.GetAsync<Person>(server.Url + "x");

        Assert.Equal("hedged", response.Data!.Name);

        // 两条分支（首发 + 1 条对冲）都在延迟窗口内发出，因此服务端收到两次。
        Assert.Equal(2, server.Methods.Count);
    }

    [Fact]
    public async Task 授权_401_在兼容资产上作废旧凭据并重放一次()
    {
        var calls = 0;
        using var server = MiniServer.Start((_, _, _, _) =>
            Interlocked.Increment(ref calls) == 1
                ? (401, "text/plain", Utf8.Text("denied"))
                : (200, "application/json", Utf8.Text("{\"name\":\"authed\",\"age\":1}")));

        var provider = new RotatingTokenProvider();
        using var client = new ApiClient(server.Url) { CredentialProvider = provider };

        var response = await client.RequestAsync(new AuthorizedRequest());

        Assert.Equal("authed", response.Data!.Name);

        // 托管落点由库写 Authorization 头；401 后先作废旧值（netstandard2.0 没有默认接口实现，
        // Invalidate 是必须实现的成员），再用新值重放一次。
        Assert.Equal(new[] { "Bearer t1", "Bearer t2" }, server.Authorizations);
        Assert.Equal(1, provider.InvalidateCount);
    }

    [Fact]
    public async Task 响应体积上限在兼容资产上于流式读取中途中断()
    {
        // chunked（无 Content-Length）才能走到读取期的累计上限，而不是发请求前的长度预检。
        var body = Utf8.Text("{\"name\":\"" + new string('x', 8192) + "\",\"age\":1}");
        using var server = MiniServer.Start((_, _, _, _) => (200, "application/json", body), chunked: true);

        using var client = new AxiosClient(new AxiosConfig { Limits = { MaxResponseBytes = 1024 } });

        var error = await Record.ExceptionAsync(() => client.GetAsync(server.Url + "x"));

        var axios = Assert.IsAssignableFrom<AxiosException>(error);
        Assert.Equal(AxiosException.ErrBadResponse, axios.Code);
        Assert.Contains("MaxResponseBytes", axios.Message);
    }

    [Fact]
    public async Task 分块响应在兼容资产上完整解码()
    {
        var payload = new string('y', 7000);
        using var server = MiniServer.Start(
            (_, _, _, _) => (200, "application/json", Utf8.Text("{\"name\":\"" + payload + "\",\"age\":2}")),
            chunked: true);

        using var client = new AxiosClient();

        var response = await client.GetAsync<Person>(server.Url + "x");

        Assert.Equal(payload, response.Data!.Name);
    }

    [Fact]
    public async Task gzip_响应在兼容资产上自动解压()
    {
        var raw = Utf8.Text("{\"name\":\"zipped\",\"age\":5}");

        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(raw, 0, raw.Length);
        }

        using var server = MiniServer.Start((_, _, _, _) => (200, "application/json", compressed.ToArray()));
        server.ExtraResponseHeaders = "Content-Encoding: gzip\r\n";

        using var client = new AxiosClient();

        var response = await client.GetAsync<Person>(server.Url + "x");

        // 兼容资产把 AutomaticDecompression 映射到 HttpClientHandler，由它宣告 Accept-Encoding 并解压。
        Assert.Equal("zipped", response.Data!.Name);
        Assert.Contains("gzip", server.AcceptEncodings.First());
    }

    [Fact]
    public async Task 静态凭据在兼容资产上按形态落头()
    {
        using var server = MiniServer.Start((_, _, _, _) => (200, "application/json", Utf8.Text("{}")));
        using var client = new AxiosClient();

        await client.GetAsync(server.Url + "a", c => c.Transport.Auth = AxiosCredential.Basic("user", "pass"));
        await client.GetAsync(server.Url + "b", c => c.Transport.Auth = AxiosCredential.Bearer("tok"));
        await client.GetAsync(server.Url + "c", c => c.Transport.Auth = AxiosCredential.Header("Signature abc"));

        Assert.Equal(
            new[]
            {
                "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:pass")),
                "Bearer tok",
                "Signature abc",
            },
            server.Authorizations);
    }

    [Fact]
    public async Task 响应流在兼容资产上原样交付()
    {
        var body = Utf8.Text("streamed-bytes");
        using var server = MiniServer.Start((_, _, _, _) => (200, "application/octet-stream", body));

        using var client = new AxiosClient();

        var response = await client.GetAsync(server.Url + "x", c => c.ResponseType = ResponseType.Stream);

        using var stream = Assert.IsAssignableFrom<Stream>(response.Data);
        using var buffered = new MemoryStream();
        await stream.CopyToAsync(buffered);

        Assert.Equal(body, buffered.ToArray());
    }

    [Fact]
    public async Task 重定向上限在兼容资产上生效()
    {
        var hits = 0;
        using var server = MiniServer.Start((_, _, _, _) =>
        {
            Interlocked.Increment(ref hits);
            return (302, "text/plain", Array.Empty<byte>());
        });
        server.ExtraResponseHeaders = "Location: /next\r\n";

        // MaxRedirects 是实例级选项（建池时固化），只能写在实例默认值上。
        using var client = new AxiosClient(new AxiosConfig { Transport = { MaxRedirects = 2 } });

        var error = await Record.ExceptionAsync(() => client.GetAsync(server.Url + "loop"));

        var axios = Assert.IsAssignableFrom<AxiosException>(error);
        Assert.Equal(AxiosException.ErrTooManyRedirects, axios.Code);

        // 首发 + 2 次跟随，第 3 个 302 交给调用方。
        Assert.Equal(3, Volatile.Read(ref hits));
    }
}

/// <summary>测试用响应模型。</summary>
public sealed class Person
{
    public string Name { get; set; } = string.Empty;

    public int Age { get; set; }
}

/// <summary>带路径 / 查询 / 头的声明式请求模型。</summary>
[Get("people/{id}")]
public sealed class GetPersonRequest : IApiRequest<Person>
{
    [Path]
    public int Id { get; set; }

    [Query("verbose")]
    public string? Verbose { get; set; }
}

/// <summary>流式请求模型。</summary>
[Get("stream")]
[Stream(AxiosStreamFormat.ServerSentEvents)]
public sealed class StreamRequest : IApiRequest<Person>
{
}

/// <summary>带格式串的请求模型：验证 <c>[Format]</c> 的模块初始化注册。</summary>
[Get("formatted")]
public sealed class FormattedRequest : IApiRequest<Person>
{
    [Query("since")]
    [Format("yyyy-MM-dd")]
    public DateTime Since { get; set; }
}

/// <summary>声明式授权的请求模型：凭据由 <see cref="IApiCredentialProvider"/> 提供。</summary>
[Get("secured")]
[Authorize]
public sealed class AuthorizedRequest : IApiRequest<Person>
{
}

/// <summary>每次取凭据都换一个新值，并记录被作废的次数。</summary>
internal sealed class RotatingTokenProvider : IApiCredentialProvider
{
    private int _issued;

    public int InvalidateCount { get; private set; }

    public ValueTask<ApiCredential> GetCredentialAsync(ApiAuthContext context, CancellationToken cancellationToken)
        => new(ApiCredential.Token("t" + Interlocked.Increment(ref _issued)));

    // netstandard2.0 没有默认接口实现：这个成员在兼容资产上是必须实现的，不实现就编译不过。
    public void Invalidate(string? staleToken = null) => InvalidateCount++;
}

/// <summary>
/// 最小 HTTP/1.1 服务器：每连接一个请求，读完 <c>\r\n\r\n</c> 与 <c>Content-Length</c> 指定的正文后回包。
/// 不用 <c>record</c>（net472 宿主编译 record 需要额外的 IsExternalInit），也不支持 Keep-Alive。
/// </summary>
internal sealed class MiniServer : IDisposable
{
    /// <summary>分块正文每块后面的 CRLF。</summary>
    private static readonly byte[] Trailer = { (byte)'\r', (byte)'\n' };

    /// <summary>分块正文的结束块。</summary>
    private static readonly byte[] LastChunk = { (byte)'0', (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' };

    private readonly TcpListener _listener;
    private readonly Func<string, string, string, string, (int Status, string ContentType, byte[] Body)> _handler;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<string> _methods = new();
    private readonly ConcurrentQueue<string> _paths = new();
    private readonly ConcurrentQueue<string> _queries = new();
    private readonly ConcurrentQueue<string> _contentTypes = new();
    private readonly ConcurrentQueue<string> _bodies = new();
    private readonly ConcurrentQueue<string> _traces = new();
    private readonly ConcurrentQueue<string> _authorizations = new();
    private readonly ConcurrentQueue<string> _acceptEncodings = new();
    private readonly bool _chunked;

    private MiniServer(TcpListener listener, Func<string, string, string, string, (int, string, byte[])> handler, bool chunked)
    {
        _listener = listener;
        _handler = handler;
        _chunked = chunked;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public string Url => "http://127.0.0.1:" + Port + "/";

    public IReadOnlyList<string> Methods => _methods.ToArray();

    public IReadOnlyList<string> Paths => _paths.ToArray();

    public IReadOnlyList<string> Queries => _queries.ToArray();

    public IReadOnlyList<string> ContentTypes => _contentTypes.ToArray();

    public IReadOnlyList<string> Bodies => _bodies.ToArray();

    /// <summary>请求头 <c>X-Trace</c> 的取值（没带该头时为空串）。</summary>
    public IReadOnlyList<string> Traces => _traces.ToArray();

    /// <summary>请求头 <c>Authorization</c> 的取值（没带该头时为空串）。</summary>
    public IReadOnlyList<string> Authorizations => _authorizations.ToArray();

    /// <summary>请求头 <c>Accept-Encoding</c> 的取值（没带该头时为空串）。</summary>
    public IReadOnlyList<string> AcceptEncodings => _acceptEncodings.ToArray();

    /// <summary>附加到每个响应头的原始行（需自带 CRLF），用于构造 <c>Content-Encoding</c> / <c>Location</c> 之类的响应。</summary>
    public string ExtraResponseHeaders { get; set; } = string.Empty;

    public static MiniServer Start(Func<string, string, string, string, (int, string, byte[])> handler, bool chunked = false)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new MiniServer(listener, handler, chunked);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var head = await ReadHeadAsync(stream).ConfigureAwait(false);
                if (head is null)
                {
                    return;
                }

                var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                var requestLine = lines[0].Split(' ');
                var method = requestLine[0];
                var target = requestLine.Length > 1 ? requestLine[1] : "/";

                var queryIndex = target.IndexOf('?');
                var path = queryIndex >= 0 ? target.Substring(0, queryIndex) : target;
                var query = queryIndex >= 0 ? target.Substring(queryIndex + 1) : string.Empty;

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 1; i < lines.Length; i++)
                {
                    var colon = lines[i].IndexOf(':');
                    if (colon > 0)
                    {
                        headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
                    }
                }

                var length = headers.TryGetValue("Content-Length", out var rawLength) && int.TryParse(rawLength, out var parsed)
                    ? parsed
                    : 0;

                var bodyBytes = new byte[length];
                var read = 0;
                while (read < length)
                {
                    var n = await stream.ReadAsync(bodyBytes, read, length - read).ConfigureAwait(false);
                    if (n == 0)
                    {
                        break;
                    }

                    read += n;
                }

                var body = Encoding.UTF8.GetString(bodyBytes, 0, read);
                var contentType = headers.TryGetValue("Content-Type", out var ct) ? ct : string.Empty;

                _methods.Enqueue(method);
                _paths.Enqueue(path);
                _queries.Enqueue(query);
                _contentTypes.Enqueue(contentType);
                _bodies.Enqueue(body);
                _traces.Enqueue(headers.TryGetValue("X-Trace", out var trace) ? trace : string.Empty);
                _authorizations.Enqueue(headers.TryGetValue("Authorization", out var authorization) ? authorization : string.Empty);
                _acceptEncodings.Enqueue(headers.TryGetValue("Accept-Encoding", out var acceptEncoding) ? acceptEncoding : string.Empty);

                var (status, responseContentType, payload) = _handler(method, path, query, body);

                if (_chunked)
                {
                    // 不带 Content-Length，正文按 512 字节分块写：客户端拿到的 ContentLength 为 null。
                    var chunkedHead = "HTTP/1.1 " + status + " " + (status == 200 ? "OK" : "ERR") + "\r\n"
                        + "Content-Type: " + responseContentType + "\r\n"
                        + ExtraResponseHeaders
                        + "Transfer-Encoding: chunked\r\n"
                        + "Connection: close\r\n\r\n";

                    var headBytes = Encoding.ASCII.GetBytes(chunkedHead);
                    await stream.WriteAsync(headBytes, 0, headBytes.Length).ConfigureAwait(false);

                    var offset = 0;
                    while (offset < payload.Length)
                    {
                        var size = Math.Min(512, payload.Length - offset);
                        var prefix = Encoding.ASCII.GetBytes(size.ToString("X") + "\r\n");
                        await stream.WriteAsync(prefix, 0, prefix.Length).ConfigureAwait(false);
                        await stream.WriteAsync(payload, offset, size).ConfigureAwait(false);
                        await stream.WriteAsync(Trailer, 0, Trailer.Length).ConfigureAwait(false);
                        offset += size;
                    }

                    await stream.WriteAsync(LastChunk, 0, LastChunk.Length).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                    return;
                }

                var headerText = "HTTP/1.1 " + status + " " + (status == 200 ? "OK" : "ERR") + "\r\n"
                    + "Content-Type: " + responseContentType + "\r\n"
                    + ExtraResponseHeaders
                    + "Content-Length: " + payload.Length + "\r\n"
                    + "Connection: close\r\n\r\n";

                var headerBytes = Encoding.ASCII.GetBytes(headerText);
                await stream.WriteAsync(headerBytes, 0, headerBytes.Length).ConfigureAwait(false);
                if (payload.Length > 0)
                {
                    await stream.WriteAsync(payload, 0, payload.Length).ConfigureAwait(false);
                }

                await stream.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // 测试服务器不关心单连接失败：客户端可能提前关闭。
        }
    }

    private static async Task<string?> ReadHeadAsync(NetworkStream stream)
    {
        var buffer = new List<byte>(1024);
        var one = new byte[1];

        while (true)
        {
            var n = await stream.ReadAsync(one, 0, 1).ConfigureAwait(false);
            if (n == 0)
            {
                return null;
            }

            buffer.Add(one[0]);

            var count = buffer.Count;
            if (count >= 4
                && buffer[count - 4] == (byte)'\r'
                && buffer[count - 3] == (byte)'\n'
                && buffer[count - 2] == (byte)'\r'
                && buffer[count - 1] == (byte)'\n')
            {
                return Encoding.ASCII.GetString(buffer.ToArray());
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
}

internal static class Utf8
{
    public static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);
}
