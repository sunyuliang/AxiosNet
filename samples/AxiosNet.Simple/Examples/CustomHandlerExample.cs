using System.Text.Json;
using AxiosNet;
using AxiosNet.Pooling;

namespace AxiosNet.Simple.Examples;

/// <summary>17. HttpMethod 直用 + 自定义 Handler 注入：单 Handler 模式与连接池 HandlerFactory。</summary>
public sealed class CustomHandlerExample : IExample
{
    /// <summary>本示例专用客户端（PROPFIND 演示）：配置固定，首次运行创建后跨运行复用。</summary>
    private static AxiosClient? _http;

    public int Number => 17;

    public string Title => "HttpMethod 直用 + 自定义 Handler 注入";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        // 静态复用：首次运行创建，之后复用同一实例与连接池
        var http = _http ??= new AxiosClient(new AxiosConfig { BaseAddress = context.BaseUrl });

        // 1) Method 就是 System.Net.Http.HttpMethod：内置谓词与自定义谓词一视同仁
        var propfind = await http.RequestAsync<JsonElement>(c =>
        {
            c.Url = "users/1";
            c.Method = new HttpMethod("PROPFIND");
        });

        Console.WriteLine($"  custom method: HttpMethod(\"PROPFIND\") -> status={propfind.Status}, data={propfind.Data.GetRawText()}");

        // 2) 直接注入 Handler：整个实例共用这一个 Handler，不创建连接池
        var singleTrace = new TraceHandler("single");
        singleTrace.InnerHandler = new SocketsHttpHandler();

        using (var injected = new AxiosClient(defaults: new AxiosConfig { BaseAddress = context.BaseUrl }, handler: singleTrace))
        {
            var headers = await injected.GetAsync<JsonElement>("headers");

            Console.WriteLine($"  注入 Handler（无池）: 拦截 {singleTrace.Count} 次, 服务端看到 x-trace-id={headers.Data.GetProperty("x-trace-id").GetString()}, usesPool={injected.UsesConnectionPool}");
        }

        // 3) 连接池 + HandlerFactory：既保留"按 origin 一个 Handler + 连接复用"，又能插入自定义 Handler
        var factoryCalls = 0;
        var intercepted = 0;

        using (var pooled = new AxiosClient(poolOptions: new ConnectionPoolOptions
        {
            MaxConnectionsPerServer = 8,
            HandlerFactory = ctx =>
            {
                factoryCalls++;
                Console.WriteLine(
                    $"  [HandlerFactory] origin={ctx.Origin}, base={ctx.BaseAddress}, maxConnections={ctx.Options.MaxConnectionsPerServer}");

                // 每次调用返回独立的 Handler：返回值按 origin 长期持有
                var trace = new TraceHandler("pool", () => Interlocked.Increment(ref intercepted));

                // 用池选项配好的 SocketsHttpHandler 作为内层
                trace.InnerHandler = ctx.CreateDefaultHandler();
                return trace;
            }
        }))
        {
            for (var i = 0; i < 3; i++)
            {
                await pooled.GetAsync(context.BaseUrl + "users/1");
            }

            var stats = pooled.Pool!.GetStatistics();
            Console.WriteLine($"  HandlerFactory: 工厂调用 {factoryCalls} 次（3 个请求同 origin）, 拦截 {intercepted} 次, 池节点 {stats.EndpointCount}");
        }
    }

    /// <summary>演示用 DelegatingHandler：给每个请求打一个 X-Trace-Id 并计数。</summary>
    private sealed class TraceHandler : DelegatingHandler
    {
        private readonly string _name;
        private readonly Action? _onSend;
        private int _count;

        public TraceHandler(string name, Action? onSend = null)
        {
            _name = name;
            _onSend = onSend;
        }

        public int Count => Volatile.Read(ref _count);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var seq = Interlocked.Increment(ref _count);
            request.Headers.TryAddWithoutValidation("X-Trace-Id", $"{_name}-{seq}");
            _onSend?.Invoke();
            return base.SendAsync(request, cancellationToken);
        }
    }
}
