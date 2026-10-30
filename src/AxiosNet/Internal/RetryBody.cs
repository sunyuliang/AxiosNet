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
    /// 检查正文是否可重放并记下流的起点；返回 <c>null</c> 表示正文不需要回拨（对象 / 字节 / 字符串 / 白名单内容）。
    /// </summary>
    /// <exception cref="AxiosException">正文不可重放（<c>ERR_BAD_OPTION_VALUE</c>）。</exception>
    public static RetryBody? Prepare(AxiosConfig config)
    {
        var data = config.Data;

        switch (data)
        {
            case null:
                return null;

            // 调用方自备的 HttpContent 按白名单放行：其余形态（含一次性流包装）在发送前拒绝。
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
    /// 调用方自备的 <see cref="HttpContent"/>：只放行能确定可重放的标准形态，逐层下钻 multipart 部件。
    /// </summary>
    /// <remarks>
    /// 「长度已知」不等于「可重放」——一次性流包装成自定义内容后照样只发得出一次。
    /// 所以这里按白名单判断：内存缓冲形态与 JsonContent 放行，其余自定义内容一律在发送前拒绝。
    /// </remarks>
    private static void EnsureReplayableContent(HttpContent content, AxiosConfig config)
    {
        switch (content)
        {
            // StringContent / FormUrlEncodedContent / 调用方用 byte[] 构造的内容都在这一类里。
            case ByteArrayContent:
            // 每次序列化都从持有的值重新写。
            case JsonContent:
            // 只读内存的零拷贝正文。
            case ReadOnlyMemoryContent:
                return;

            case MultipartContent multipart:
                foreach (var part in multipart)
                {
                    EnsureReplayableContent(part, config);
                }

                return;

            case StreamContent:
                throw NotReplayable(config, "StreamContent");

            case AsyncEnumerableBodyContent:
                // 兜住「流式正文被别的 HttpContent 包了一层」的情形。
                throw NotReplayable(config, "流式请求体（IAsyncEnumerable）");

            default:
                throw NotReplayable(config, $"自定义 HttpContent（{content.GetType().Name}）");
        }
    }

    private static AxiosException NotReplayable(AxiosConfig config, string what)
        => AxiosException.InvalidOption(
            $"开启重试或备用端点要求请求体能重发，但当前 data 是{what}："
            + "重发时第二次发送发不出完整正文（只会发出空内容，或直接报「流已被消费」）。"
            + "请改用 byte[] / string / 业务对象 / FormFile，或先把内容读进可 seek 的 MemoryStream / FileStream；"
            + "HttpContent 形态只认可重放的标准内容（ByteArrayContent / StringContent / FormUrlEncodedContent /"
            + " ReadOnlyMemoryContent / JsonContent / 全部部件都可重放的 MultipartContent），"
            + "其余自定义内容库无法回拨，请换成上述可重放形态；"
            + "不需要重发时请关掉重试 / 备用端点（流式正文本身没有错）。", config);
}
