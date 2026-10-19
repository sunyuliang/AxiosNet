using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>
/// 15. P0 能力：getUri 预览（不发请求）、maxContentLength 拦截超大响应、maxRedirects: 0 不跟随重定向。
/// </summary>
public sealed class GetUriAndLimitsExample : IExample
{
    public int Number => 15;

    public string Title => "getUri 预览 / maxContentLength / maxRedirects: 0";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        using var http = new AxiosClient(new AxiosConfig
        {
            BaseAddress = context.BaseUrl,
            MaxContentLength = 1024 * 1024 // 1 MB 上限
        });

        // 1) getUri：只拼 URL 不发请求（签名、日志、调试都能用）
        var uri = http.GetUri(c =>
        {
            c.Url = "users";
            c.Params = new { page = 2, ids = new[] { 1, 2 } };
        });
        Console.WriteLine($"  getUri: {uri}");

        // 2) maxContentLength：/big 返回 8 MB，超过 1 MB 上限，读正文前就被拒
        try
        {
            await http.GetAsync<byte[]>("big");
            Console.WriteLine("  [意外] 8 MB 响应没有被拦截");
        }
        catch (AxiosException ex)
        {
            Console.WriteLine($"  大响应被拦截: code={ex.Code}, message={ex.Message}");
        }

        // 3) maxRedirects = 0：不跟随重定向（实例级配置，构造时就固定）
        using var noRedirect = new AxiosClient(new AxiosConfig
        {
            BaseAddress = context.BaseUrl,
            MaxRedirects = 0
        });

        try
        {
            await noRedirect.GetAsync("redirect");
            Console.WriteLine("  [意外] 居然跟随了重定向");
        }
        catch (AxiosException ex)
        {
            Console.WriteLine(
                $"  未跟随重定向: status={ex.Status}, location={ex.Response?.GetHeader("Location")}, code={ex.Code}");
        }

        // 4) 对照：默认 maxRedirects = 5，会跟到 /users/1
        var followed = await http.GetAsync<string>("redirect", c => c.ResponseType = ResponseType.Text);
        Console.WriteLine($"  默认跟随重定向: status={followed.Status}");
    }
}
