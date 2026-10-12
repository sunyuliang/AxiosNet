using System.Diagnostics;
using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>
/// 11. 错误处理 / 超时 / 取消。
/// </summary>
public sealed class ErrorHandlingExample : IExample
{
    public int Number => 11;

    public string Title => "错误码语义、超时 ECONNABORTED、取消 ERR_CANCELED、validateStatus";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        using var http = new AxiosClient();

        // 4xx -> ERR_BAD_REQUEST，异常上带完整 response
        try
        {
            await http.GetAsync(context.BaseUrl + "not-found");
        }
        catch (AxiosException ex)
        {
            Console.WriteLine($"  404: code={ex.Code}, message=\"{ex.Message}\", http={ex.Response!.Status}");
        }

        // 超时
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await http.GetAsync(context.BaseUrl + "slow?ms=1000", c => c.Timeout = TimeSpan.FromMilliseconds(200));
        }
        catch (AxiosException ex)
        {
            stopwatch.Stop();
            Console.WriteLine($"  超时: code={ex.Code}，{stopwatch.ElapsedMilliseconds}ms 即返回（服务端实际要等 1000ms）");
        }

        // 外部取消
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        try
        {
            await http.GetAsync(context.BaseUrl + "slow?ms=1000", cancellationToken: cts.Token);
        }
        catch (AxiosException ex)
        {
            Console.WriteLine($"  取消: code={ex.Code}");
        }

        // validateStatus：自己定义合法状态码，404 也可以正常返回
        var accepted = await http.GetAsync(context.BaseUrl + "not-found", c => c.ValidateStatus = s => s is >= 200 and < 500);
        Console.WriteLine($"  validateStatus 放行 404: status={accepted.Status}");
    }
}
