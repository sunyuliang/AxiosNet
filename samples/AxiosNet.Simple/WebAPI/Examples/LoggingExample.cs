using AxiosNet;
using AxiosNet.WebAPI;

namespace AxiosNet.Simple.WebAPI.Examples;

/// <summary>
/// 6. 全局日志：client.Axios.Interceptors 请求/响应拦截器，一次挂载对整个客户端生效。
/// </summary>
public sealed class LoggingExample : IExample
{
    /// <summary>本示例专用客户端：拦截器只由本示例挂载，跨运行复用前先清空（不能用共享客户端，否则日志拦截器会漏进其它示例）。</summary>
    private static ApiClient? _client;

    public int Number => 6;

    public string Title => "全局日志：AxiosClient 拦截器（请求 / 响应 / 错误三个分支）";

    public async Task RunAsync(ExampleContext context)
    {
        ExampleConsole.Section(Number, Title);

        // 静态复用：首次运行创建；拦截器每次运行重挂，先清掉上一轮的，避免重复执行
        var client = _client ??= new ApiClient(context.BaseUrl);
        client.Axios.Interceptors.Request.Clear();
        client.Axios.Interceptors.Response.Clear();

        // 拦截器在授权之后执行：这里看到的 config 已是最终形态（含授权头）
        client.Axios.Interceptors.Request.Use(config =>
        {
            Console.WriteLine($"  --> {config.Method} {config.Url}");
            config.Headers.Set("X-Trace-Id", Guid.NewGuid().ToString("N")[..8]); // 顺手全局加头
            return config;
        });

        client.Axios.Interceptors.Response.Use(
            response =>
            {
                Console.WriteLine($"  <-- {response.Status}（Data = {ExampleConsole.Pretty(response.Data)}）");
                return response;
            },
            error =>
            {
                var ex = (AxiosException)error;
                Console.WriteLine($"  <-- 失败 {ex.Code} HTTP {ex.Status}：{ex.Message}");
                throw error; // 只记日志就原样抛出；也可返回兜底 AxiosResponse 做恢复
            });

        // 成功请求：请求 / 响应两个分支都走
        await client.RequestAsync(new GetUserRequest { Id = 1 });

        // 失败请求：走 onRejected 分支，异常原样向上抛
        try
        {
            await client.RequestAsync(new GetMissingUserRequest { Id = 999 });
        }
        catch (AxiosException)
        {
            Console.WriteLine("  （异常已按预期向上抛出）");
        }
    }
}
