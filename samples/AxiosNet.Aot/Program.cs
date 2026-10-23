using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxiosNet;
using AxiosNet.WebAPI;

// Native AOT 示例：本地回环服务 + 两种调用方式（axios 风格 / 声明式）。
// 发布：dotnet publish samples/AxiosNet.Aot -c Release -r win-x64
// AOT 前提：root 应用程序集（见 csproj）+ STJ 源生成上下文（见下文）。

using var server = new LoopbackServer();

// AOT 下 STJ 的反射回退默认关闭：必须由 TypeInfoResolver 提供源生成上下文。
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    TypeInfoResolver = ExchangeJsonContext.Default,
};

// 启动期体检：显式传入 typeof，避免 ValidateModels(Assembly) 的运行时类型枚举。
var modelErrors = ApiClient.ValidateModels(typeof(GetPersonRequest));
if (modelErrors.Count > 0)
{
    throw new InvalidOperationException(string.Join(Environment.NewLine, modelErrors));
}

// 1) axios 风格：POST JSON + 泛型回读
var http = new AxiosClient(new AxiosConfig
{
    BaseAddress = server.BaseAddress,
    JsonSerializerOptions = jsonOptions,
});

var payload = new Person("alice", 30, new Address("Shanghai", "200000"));
var posted = await http.PostAsync<Person>("/echo", payload);

// 2) 声明式：请求模型上的路由 / 参数特性由反射绑定
var api = new ApiClient(server.BaseAddress, defaults =>
{
    defaults.JsonSerializerOptions = jsonOptions;
});

var fetched = await api.RequestAsync(new GetPersonRequest { Id = 7 });

Console.WriteLine($"POST /echo   -> {posted.Data?.Name} / {posted.Data?.Age} / {posted.Data?.Address?.City}");
Console.WriteLine($"GET persons/7 -> {fetched.Data?.Name} / {fetched.Data?.Age} / {fetched.Data?.Address?.City}");

// ------------------------------------------------------------------
// 模型：AOT 下由 csproj 的 TrimmerRootAssembly 保留 getter 与特性
// ------------------------------------------------------------------

[Get("persons/{id}")]
internal sealed class GetPersonRequest : IApiRequest<Person>
{
    [Path]
    public int Id { get; set; }
}

public sealed record Person(string Name, int Age, Address? Address);

public sealed record Address(string City, string Zip);

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
    private readonly Task _loop;

    public LoopbackServer()
    {
        _listener.Start();
        BaseAddress = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _loop = Task.Run(AcceptLoopAsync);
    }

    public string BaseAddress { get; }

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

    private static async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            await DrainRequestAsync(stream);

            var body = Encoding.UTF8.GetBytes(PersonJson);
            var head = $"HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            await stream.WriteAsync(body);
            await stream.FlushAsync();
        }
    }

    /// <summary>读完请求头与 Content-Length 指定的正文，再回响应，避免连接被提前关闭。</summary>
    private static async Task DrainRequestAsync(Stream stream)
    {
        var received = new List<byte>(2048);
        var chunk = new byte[1024];
        var headerEnd = -1;

        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(chunk);
            if (read == 0)
            {
                return;
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
                return;
            }

            bodyReceived += read;
        }
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
