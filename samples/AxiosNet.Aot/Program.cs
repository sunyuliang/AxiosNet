using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxiosNet;
using AxiosNet.WebAPI;

// Native AOT 示例：本地回环服务 + 两种调用方式（axios 风格 / 声明式）。
// 发布：dotnet publish samples/AxiosNet.Aot -c Release -r win-x64
// AOT 前提：源生成器（随包分发的分析器）+ STJ 源生成上下文；不再需要 TrimmerRootAssembly。

using var server = new LoopbackServer();

// AOT 下 STJ 的反射回退默认关闭：必须由 TypeInfoResolver 提供源生成上下文。
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    TypeInfoResolver = ExchangeJsonContext.Default,
};

// 启动期体检：显式传入 typeof，避免 ValidateModels(Assembly) 的运行时类型枚举。
var modelErrors = ApiClient.ValidateModels(
    typeof(GetPersonRequest),
    typeof(CreatePersonRequest),
    typeof(SearchPersonRequest));
if (modelErrors.Count > 0)
{
    throw new InvalidOperationException(string.Join(Environment.NewLine, modelErrors));
}

// 1) axios 风格：POST JSON + 泛型回读
var http = new AxiosClient(new AxiosConfig
{
    BaseAddress = server.BaseAddress,
    Serialization = new() { JsonSerializerOptions = jsonOptions },
});

var payload = new Person("alice", 30, new Address("Shanghai", "200000"));
var posted = await http.PostAsync<Person>("/echo", payload);

// 2) 声明式：请求模型上的路由 / 参数特性由源生成器烘焙成元数据（JIT / AOT 同一路径）
var api = new ApiClient(server.BaseAddress, defaults =>
{
    defaults.Serialization.JsonSerializerOptions = jsonOptions;
});
api.AccessTokenProvider = new FixedTokenProvider();

var fetched = await api.RequestAsync(new GetPersonRequest
{
    Id = 7,
    Verbose = "true",
    TraceId = "trace-1",
});
Verify(
    server.TakeLast(),
    "GET /persons/7?verbose=true HTTP/1.1",
    ("X-Client", "axiom"),
    ("X-Static", "1"),
    ("X-Trace", "trace-1"),
    ("Authorization", "Bearer aot-token"));

var created = await api.RequestAsync(new CreatePersonRequest
{
    Person = payload,
    Notify = true,
});
var createRequest = server.TakeLast();
Verify(createRequest, "POST /persons?notify=true HTTP/1.1", ("Authorization", "Bearer aot-token"));
if (createRequest.GetHeader("Content-Type")?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
{
    throw new InvalidOperationException($"POST 的 Content-Type 不是 JSON：{createRequest.GetHeader("Content-Type")}");
}

if (!createRequest.Body.Contains("\"name\":\"alice\"", StringComparison.Ordinal))
{
    throw new InvalidOperationException($"POST 请求体没有按 STJ（camelCase）序列化：{createRequest.Body}");
}

var searched = await api.RequestAsync(new SearchPersonRequest
{
    Term = "alice",
    Page = 2,
});
var searchRequest = server.TakeLast();
Verify(searchRequest, "GET /persons?q=alice&page=2 HTTP/1.1");
if (searchRequest.HasHeader("Authorization"))
{
    throw new InvalidOperationException("[AllowAnonymous] 模型不应带 Authorization 头。");
}

Console.WriteLine($"POST /echo      -> {posted.Data?.Name} / {posted.Data?.Age} / {posted.Data?.Address?.City}");
Console.WriteLine($"GET persons/7   -> {fetched.Data?.Name} / query+header+auth 校验通过");
Console.WriteLine($"POST persons    -> {created.Data?.Name} / body+query+auth 校验通过");
Console.WriteLine($"GET persons     -> {searched.Data?.Name} / 匿名请求校验通过");

static void Verify(
    RequestSnapshot request,
    string expectedLine,
    params (string Name, string Value)[] expectedHeaders)
{
    if (request.RequestLine != expectedLine)
    {
        throw new InvalidOperationException($"请求行不符：期望 \"{expectedLine}\"，实际 \"{request.RequestLine}\"。");
    }

    foreach (var (name, value) in expectedHeaders)
    {
        var actual = request.GetHeader(name);
        if (actual != value)
        {
            throw new InvalidOperationException($"请求头 {name} 不符：期望 \"{value}\"，实际 \"{actual}\"。");
        }
    }
}

// ------------------------------------------------------------------
// 模型：AOT 下由源生成器（AxiosNet.SourceGen）直接烘焙元数据，
//       getter 是生成期写死的属性访问，不依赖反射，也不靠 TrimmerRootAssembly。
// ------------------------------------------------------------------

[Get("persons/{id}")]
[Headers("X-Client: axiom", "X-Static: 1")]
internal sealed class GetPersonRequest : IApiRequest<Person>
{
    [Path]
    public int Id { get; set; }

    [Query("verbose")]
    public string Verbose { get; set; } = string.Empty;

    [Header("X-Trace")]
    public string? TraceId { get; set; }
}

[Post("persons")]
[Authorize]
internal sealed class CreatePersonRequest : IApiRequest<Person>
{
    [Body]
    public Person? Person { get; set; }

    [Query("notify")]
    public bool Notify { get; set; }
}

[Get("persons")]
[AllowAnonymous]
internal sealed class SearchPersonRequest : IApiRequest<Person>
{
    [Query("q")]
    public string? Term { get; set; }

    [Query("page")]
    public int Page { get; set; }
}

public sealed record Person(string Name, int Age, Address? Address);

public sealed record Address(string City, string Zip);

internal sealed class FixedTokenProvider : IAccessTokenProvider
{
    public ValueTask<string?> GetTokenAsync(ApiAuthContext context, CancellationToken cancellationToken)
        => new ValueTask<string?>("aot-token");
}

// STJ 源生成上下文：覆盖请求/响应用到的全部类型。
[JsonSerializable(typeof(Person))]
internal partial class ExchangeJsonContext : JsonSerializerContext
{
}

// ------------------------------------------------------------------
// 本地回环 HTTP 服务：断网环境也能跑，并完整经过连接池与请求管线
// ------------------------------------------------------------------

internal sealed class LoopbackServer : IDisposable
{
    private const string PersonJson = """{"name":"alice","age":30,"address":{"city":"Shanghai","zip":"200000"}}""";

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Task _loop;
    private RequestSnapshot? _last;

    public LoopbackServer()
    {
        _listener.Start();
        BaseAddress = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _loop = Task.Run(AcceptLoopAsync);
    }

    public string BaseAddress { get; }

    /// <summary>取走最近一次请求（取走后清空，避免相邻场景互相覆盖）。</summary>
    public RequestSnapshot TakeLast()
    {
        lock (_gate)
        {
            var last = _last ?? throw new InvalidOperationException("服务端还没有收到请求。");
            _last = null;
            return last;
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var (head, body) = await ReadRequestAsync(stream);

            var snapshot = RequestSnapshot.Parse(head, body);
            lock (_gate)
            {
                _last = snapshot;
            }

            var payload = Encoding.UTF8.GetBytes(PersonJson);
            var responseHead = $"HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(responseHead));
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
        }
    }

    /// <summary>读完请求头与 Content-Length 指定的正文，再回响应，避免连接被提前关闭。</summary>
    private static async Task<(string Head, string Body)> ReadRequestAsync(Stream stream)
    {
        var received = new List<byte>(4096);
        var chunk = new byte[4096];
        var headerEnd = -1;

        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(chunk);
            if (read == 0)
            {
                return (Encoding.ASCII.GetString(received.ToArray()), string.Empty);
            }

            received.AddRange(chunk.AsSpan(0, read).ToArray());
            headerEnd = IndexOfHeaderEnd(received);
        }

        var head = Encoding.ASCII.GetString(received.ToArray(), 0, headerEnd);
        var contentLength = 0;
        foreach (var line in head.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                _ = int.TryParse(line[15..].Trim(), out contentLength);
            }
        }

        var bodyReceived = received.Count - (headerEnd + 4);
        while (bodyReceived < contentLength)
        {
            var read = await stream.ReadAsync(chunk);
            if (read == 0)
            {
                break;
            }

            received.AddRange(chunk.AsSpan(0, read).ToArray());
            bodyReceived += read;
        }

        var bodyLength = Math.Max(0, Math.Min(contentLength, received.Count - (headerEnd + 4)));
        var body = Encoding.UTF8.GetString(received.ToArray(), headerEnd + 4, bodyLength);
        return (head, body);
    }

    private static int IndexOfHeaderEnd(List<byte> buffer)
    {
        for (var i = 3; i < buffer.Count; i++)
        {
            if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' && buffer[i - 1] == '\r' && buffer[i] == '\n')
            {
                return i - 3;
            }
        }

        return -1;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();

        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 取消导致的异常，忽略
        }

        _cts.Dispose();
    }
}

internal sealed class RequestSnapshot
{
    private readonly Dictionary<string, string> _headers;

    private RequestSnapshot(string requestLine, Dictionary<string, string> headers, string body)
    {
        RequestLine = requestLine;
        _headers = headers;
        Body = body;
    }

    public string RequestLine { get; }

    public string Body { get; }

    public static RequestSnapshot Parse(string head, string body)
    {
        var lines = head.Split("\r\n");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var separator = lines[i].IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            headers[lines[i][..separator].Trim()] = lines[i][(separator + 1)..].Trim();
        }

        return new RequestSnapshot(lines.Length > 0 ? lines[0] : string.Empty, headers, body);
    }

    public string? GetHeader(string name) => _headers.TryGetValue(name, out var value) ? value : null;

    public bool HasHeader(string name) => _headers.ContainsKey(name);
}
