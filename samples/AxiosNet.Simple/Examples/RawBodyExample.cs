using System.Text;
using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>39. 响应对象上的正文：错误体原文 RawText 与 opt-in 的原始字节 RawBody。</summary>
public sealed class RawBodyExample : IExample
{
    public int Number => 39;

    public string Title => "响应正文原文 RawText / 原始字节 RawBody（记日志与排查）";

    /// <summary>示例入口：响应正文原文 RawText / 原始字节 RawBody（记日志与排查）。</summary>
    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        // 1) 错误体原文：状态码被 validateStatus 拒绝时自动保留，不需要任何配置。
        try
        {
            await Axios.GetAsync(context.BaseUrl + "not-found");
        }
        catch (AxiosException ex)
        {
            Console.WriteLine($"  404 原文: {ex.Response!.RawText}");
            Console.WriteLine($"  404 原始字节: {(ex.Response.RawBody is null ? "null（未开启 CaptureRawResponseBody）" : $"{ex.Response.RawBody.Value.Length} bytes")}");
        }

        // 2) 成功响应默认不保留正文：要留就显式打开 CaptureRawResponseBody（只对非流式响应生效）。
        var response = await Axios.GetAsync(context.BaseUrl + "users/1", c =>
        {
            c.Observability.CaptureRawResponseBody = true;
            c.Observability.OnRawResponse = (info, bytes) =>
                Console.WriteLine($"  OnRawResponse: {info.Status} {info.MediaType} {bytes.Length} bytes");
        });

        var raw = response.RawBody!.Value;
        var preview = Encoding.UTF8.GetString(raw.Span[..Math.Min(48, raw.Length)]);
        Console.WriteLine($"  RawBody: {raw.Length} bytes，开头: {preview}");
        Console.WriteLine($"  成功响应不保留 RawText: {response.RawText ?? "null"}");

        // 3) 2xx 但正文与声明类型不符：Data 为 null，RawText 仍保留原文（排查坏正文不用提前开开关）。
        try
        {
            await Axios.GetAsync<int>(context.BaseUrl + "users/1");
        }
        catch (AxiosException ex)
        {
            Console.WriteLine($"  解析失败 Data: {ex.Response!.Data ?? "null"}");
            Console.WriteLine($"  解析失败原文: {ex.Response.RawText}");
        }

        Console.WriteLine("  提示：RawBody 是只读视图；长期留档请在 OnRawResponse 里自行复制");
    }
}
