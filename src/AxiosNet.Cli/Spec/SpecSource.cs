using System.Text;

namespace AxiosNet.Cli.Spec;

/// <summary>一份已读入内存的文档及其可读来源名（本地文件名 / URL / stdin）。</summary>
internal sealed class SpecSource
{
    /// <summary>构造已读入的文档：displayName 只用于错误与警告展示。</summary>
    public SpecSource(string displayName, string content)
    {
        DisplayName = displayName;
        Content = content;
    }

    /// <summary>可读来源名（文件名 / URL / <c>&lt;stdin&gt;</c>），出现在错误信息里。</summary>
    public string DisplayName { get; }

    /// <summary>文档原文（尚未解析）。</summary>
    public string Content { get; }
}

/// <summary>本地路径 / http(s) URL / <c>-</c>（stdin）三种来源的统一读取入口。</summary>
internal static class SpecSourceLoader
{
    /// <summary>按 <see cref="CliOptions.Source"/> 读取文档：<c>-</c> 读 stdin，http(s) 下载，其余按本地文件读取。</summary>
    public static async Task<SpecSource> LoadAsync(CliOptions options)
    {
        var source = options.Source
            ?? throw new CliUsageException("缺少 OpenAPI 文档参数。");

        if (source == "-")
        {
            // 不能用 Console.In：它按控制台代码页解码（中文 Windows 上是 GBK），
            // 而管道里传入的 UTF-8 文档会被解成乱码。直接读标准输入字节并按 UTF-8 解码（可识别 BOM）。
            using var reader = new StreamReader(
                Console.OpenStandardInput(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                detectEncodingFromByteOrderMarks: true);
            var text = await reader.ReadToEndAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new GenerationException("stdin 没有内容。");
            }

            return new SpecSource("<stdin>", text);
        }

        if (source.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return await LoadFromUrlAsync(source, options).ConfigureAwait(false);
        }

        var fullPath = Path.GetFullPath(source);
        if (!File.Exists(fullPath))
        {
            throw new GenerationException($"找不到文档文件：{source}");
        }

        return new SpecSource(Path.GetFileName(fullPath), await File.ReadAllTextAsync(fullPath).ConfigureAwait(false));
    }

    /// <summary>通过 HTTP(S) 下载文档：附带自定义请求头与超时，非 2xx / 空内容 / 超时都转成生成失败。</summary>
    private static async Task<SpecSource> LoadFromUrlAsync(string url, CliOptions options)
    {
        using var client = new HttpClient { Timeout = options.Timeout };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var header in options.Headers)
        {
            // Content-* 之类的头只能挂在内容上，挂到请求头集合会被静默丢弃；
            // 返回值必须检查，否则用户以为自定义头生效了、实际下载悄悄少一个头。
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                throw new CliUsageException(
                    $"--header 无法附加到请求头：{header.Key}（Content-* 等头不能用于下载文档）。");
            }
        }

        try
        {
            using var response = await client.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new GenerationException(
                    $"下载 OpenAPI 文档失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}（{url}）。");
            }

            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new GenerationException($"下载到的 OpenAPI 文档为空：{url}。");
            }

            return new SpecSource(url, text);
        }
        catch (HttpRequestException ex)
        {
            throw new GenerationException($"下载 OpenAPI 文档失败：{ex.Message}（{url}）。", ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new GenerationException($"下载 OpenAPI 文档超时（{options.Timeout.TotalSeconds:0.#} 秒）：{url}。", ex);
        }
    }
}
