using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>16. P1 能力：下载/上传进度回调与 maxRate 限速。</summary>
public sealed class ProgressAndRateExample : IExample
{
    public int Number => 16;

    public string Title => "下载/上传进度回调 + maxRate 限速";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        var lastStep = 0;

        void Report(AxiosProgressEvent e)
        {
            // 每 25% 打印一次，避免刷屏
            var step = (int)Math.Floor((e.Progress ?? 0) * 4);
            if (step <= lastStep && (e.Progress ?? 0) < 1)
            {
                return;
            }

            lastStep = step;
            var rate = e.Rate is double r ? $"{r / 1024:0.#} KB/s" : "?";
            Console.WriteLine($"  [{(e.Upload ? "上传" : "下载")}] {e.Loaded}/{e.Total} bytes, {(e.Progress ?? 0) * 100:0}%, {rate}");
        }

        // 1) 下载 256 KB，限速 256 KB/s（约 1s，能看到多次进度）
        var download = await Axios.GetAsync<byte[]>(context.BaseUrl + "download?kb=256", c =>
        {
            c.Limits.MaxRate = 256 * 1024;
            c.Callbacks.OnDownloadProgress = Report;
        });
        Console.WriteLine($"  下载完成: {download.Data!.Length} bytes");

        // 2) 上传 512 KB 文本，限速 1 MB/s
        lastStep = 0;
        var payload = new string('x', 512 * 1024);
        await Axios.PostAsync<string>(context.BaseUrl + "echo", payload, c =>
        {
            c.ContentType = AxiosContentType.TextPlain;
            c.ResponseType = ResponseType.Text;
            c.Limits.MaxRate = 1024 * 1024;
            c.Callbacks.OnUploadProgress = Report;
        });
        Console.WriteLine("  上传完成");

        // 3) 原始进度事件里还有速率与预计剩余时间
        Console.WriteLine("  提示：AxiosProgressEvent 还提供 Rate（B/s）与 Estimated（剩余时间）");
    }
}
