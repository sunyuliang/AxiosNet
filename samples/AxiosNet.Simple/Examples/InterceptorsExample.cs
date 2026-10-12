using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>
/// 10. 请求/响应拦截器（含错误恢复）。
/// </summary>
public sealed class InterceptorsExample : IExample
{
    public int Number => 10;

    public string Title => "拦截器：请求改写配置、响应改写数据、错误分支恢复";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        using var http = new AxiosClient(new AxiosConfig { BaseAddress = context.BaseUrl });

        http.Interceptors.Request.Use(config =>
        {
            config.Headers.Set("X-Trace-Id", Guid.NewGuid().ToString("N")[..8]);
            Console.WriteLine("  [请求拦截器] 已添加 X-Trace-Id");
            return config;
        });

        http.Interceptors.Response.Use(
            onFulfilled: response =>
            {
                // 把非泛型 JsonElement 响应统一包一层标记
                response.Data = $"[已拦截] status={response.Status}";
                return response;
            },
            onRejected: ex =>
            {
                var error = (AxiosException)ex;
                Console.WriteLine($"  [响应拦截器-错误恢复] {error.Code} / HTTP {error.Response?.Status}");
                return new AxiosResponse { Status = 200, StatusText = "Recovered", Data = "fallback-data" };
            });

        var ok = await http.GetAsync("/", c => c.ResponseType = ResponseType.Text);
        Console.WriteLine($"  正常请求结果: {ok.Data}");

        var recovered = await http.GetAsync("/not-found");
        Console.WriteLine($"  404 被拦截恢复: {recovered.Status} / data={recovered.Data}");
    }
}
