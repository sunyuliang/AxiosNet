using System.Net.Http.Json;

namespace AxiosNet.Internal;

/// <summary>
/// 重发时的请求体重放：记住正文里各个流的起点，每次重发前拨回去。
/// 不可 seek 的流在请求发出之前就被拒绝（<c>ERR_BAD_OPTION_VALUE</c>）。
/// </summary>
internal sealed class RetryBody
{
    private readonly List<(Stream Stream, long Position)> _streams;

    private RetryBody(List<(Stream Stream, long Position)> streams) => _streams = streams;

    /// <summary>
    /// 检查正文是否可重放并记下流的起点；返回 <c>null</c> 表示正文不需要回拨（对象 / 字节 / 字符串）。
    /// </summary>
    /// <exception cref="AxiosException">正文含不可 seek 的流（<c>ERR_BAD_OPTION_VALUE</c>）。</exception>
    public static RetryBody? Prepare(AxiosConfig config)
    {
        var data = config.Data;

        switch (data)
        {
            case null:
                return null;

            // 调用方自备的 HttpContent 只有「明确包着流」的形态会被拦下。
            case HttpContent content:
                EnsureReplayableContent(content, config);
                return null;

            case Stream stream:
                if (!stream.CanSeek)
                {
                    throw NotReplayable(config, "不可 seek 的 Stream");
                }

                return new RetryBody(new List<(Stream, long)> { (stream, stream.Position) });

            // 流式正文：序列被消费后无法回拨，与不可 seek 的流同一口径。
            case IAsyncEnumerable<byte[]>:
            case IAsyncEnumerable<ReadOnlyMemory<byte>>:
                throw NotReplayable(config, "流式请求体（IAsyncEnumerable）");
        }

        // 结构化数据里只有 multipart 会把流当文件部件发出去。
        if (!MediaTypes.Is(config.ContentType, AxiosContentType.MultipartFormData))
        {
            return null;
        }

        var streams = new List<(Stream, long)>();
        MultipartOwnership.VisitStreams(
            data,
            stream =>
            {
                if (!stream.CanSeek)
                {
                    throw NotReplayable(config, "multipart 文件部件里不可 seek 的流");
                }

                streams.Add((stream, stream.Position));
            },
            part =>
            {
                // 部件级 HttpContent 由调用方构造，库无法回拨：与根级 StreamContent 同一规则，一律拒绝。
                if (part is StreamContent)
                {
                    throw NotReplayable(config, "multipart 部件里的 StreamContent");
                }
            });

        return streams.Count == 0 ? null : new RetryBody(streams);
    }

    /// <summary>把所有流拨回记录下来的起点（第二次及以后的发送前调用）。</summary>
    /// <exception cref="AxiosException">流已关闭或不支持定位（<c>ERR_BAD_OPTION_VALUE</c>）。</exception>
    public void Rewind(AxiosConfig config)
    {
        foreach (var (stream, position) in _streams)
        {
            try
            {
                stream.Position = position;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or NotSupportedException or IOException)
            {
                // 归到统一错误模型，不漏裸异常。
                throw AxiosException.InvalidOption(
                    "重试 / 换端点前无法把请求正文的流拨回起点：流可能已被关闭，或不支持定位（CanSeek）。"
                    + "开启重试或备用端点要求正文可重放，请改用 byte[] / string / 业务对象，"
                    + "或确保流在整个请求（含重试 / 换端点）期间保持打开且可 seek。", config, ex);
            }
        }
    }

    /// <summary>
    /// 调用方自备的 <see cref="HttpContent"/>：逐层下钻 multipart 部件，任何一个包着流的部件都拒绝。
    /// </summary>
    private static void EnsureReplayableContent(HttpContent content, AxiosConfig config)
    {
        switch (content)
        {
            case StreamContent:
                throw NotReplayable(config, "StreamContent");

            case AsyncEnumerableBodyContent:
                // 兜住「流式正文被别的 HttpContent 包了一层」的情形。
                throw NotReplayable(config, "流式请求体（IAsyncEnumerable）");

            case MultipartContent multipart:
                foreach (var part in multipart)
                {
                    EnsureReplayableContent(part, config);
                }

                return;

            default:
                // JsonContent 长度未知但可重放（每次序列化都重新写）。
                if (content is JsonContent)
                {
                    return;
                }

                // 未知类型的自定义内容：长度未知时拒绝。
                if (!HasKnownLength(content))
                {
                    throw NotReplayable(config, "长度未知的自定义 HttpContent");
                }

                return;
        }
    }

    /// <summary>
    /// 内容长度是否可确定；计算过程抛异常的按「未知」处理。
    /// </summary>
    private static bool HasKnownLength(HttpContent content)
    {
        try
        {
            return content.Headers.ContentLength is not null;
        }
        catch
        {
            return false;
        }
    }

    private static AxiosException NotReplayable(AxiosConfig config, string what)
        => AxiosException.InvalidOption(
            $"开启重试或备用端点要求请求体能重发，但当前 data 是{what}："
            + "重发时第二次发送发不出完整正文（只会发出空内容，或直接报「流已被消费」）。"
            + "请改用 byte[] / string / 业务对象 / FormFile，或先把内容读进可 seek 的 MemoryStream / FileStream；"
            + "HttpContent 形态（尤其 StreamContent / 长度未知的自定义内容）库无法回拨，请换成上述可重放形态；"
            + "不需要重发时请关掉重试 / 备用端点（流式正文本身没有错）。", config);
}
