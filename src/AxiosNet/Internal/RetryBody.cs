namespace AxiosNet.Internal;

/// <summary>
/// 重试时的「请求体重放」：记住正文里各个流的起点，每次重试前把它们拨回去。
/// <para>
/// 重试复用<b>同一份</b> <see cref="HttpContent"/>（见 <c>AxiosClient.SendWithRetryAsync</c>）：
/// 上传进度计量器与限速状态都挂在正文上，换一份会让进度跳回 0，也要把业务对象重新序列化一遍。
/// 代价是「来自流的正文」第二次发送时流已经在末尾 —— 不拨回起点就会悄悄发出一个空文件 / 空正文，
/// 所以不可 seek 的流在请求发出<b>之前</b>就被拒绝（<c>ERR_BAD_OPTION_VALUE</c>），而不是等重试时才发现。
/// </para>
/// <para>
/// 检查范围覆盖<b>整条正文的对象图</b>：根级 <see cref="Stream"/> / <see cref="StreamContent"/>，
/// 以及 multipart 表单里的 <see cref="FormFile"/>、裸流与调用方自带的 <see cref="HttpContent"/> 部件。
/// 只查根级会漏掉「文件部件里的网络流」这类最常见的上传形态 —— 那时第二次发送的表现是
/// BCL 抛「流已被消费」→ 被翻译成 <c>ERR_NETWORK</c> → 还会被当成网络错误反复重试。
/// </para>
/// </summary>
internal sealed class RetryBody
{
    private readonly List<(Stream Stream, long Position)> _streams;

    private RetryBody(List<(Stream Stream, long Position)> streams) => _streams = streams;

    /// <summary>
    /// 检查正文是否可重放并记下流的起点；返回 <c>null</c> 表示正文不需要回拨（对象 / 字节 / 字符串）。
    /// </summary>
    /// <exception cref="AxiosException">
    /// 正文含不可 seek 的流（<c>ERR_BAD_OPTION_VALUE</c>）：开着重试也发不出第二份完整正文。
    /// </exception>
    public static RetryBody? Prepare(AxiosConfig config)
    {
        var data = config.Data;

        switch (data)
        {
            case null:
                return null;

            // 调用方自备的 HttpContent 由它自己决定能否重发；只有「明确包着流」的形态会被拦下，
            // 其余（StringContent / ByteArrayContent / 自实现）按可重放对待。
            case HttpContent content:
                if (content is StreamContent)
                {
                    throw NotReplayable(config, "StreamContent");
                }

                return null;

            case Stream stream:
                if (!stream.CanSeek)
                {
                    throw NotReplayable(config, "不可 seek 的 Stream");
                }

                return new RetryBody(new List<(Stream, long)> { (stream, stream.Position) });
        }

        // 结构化数据里只有 multipart 会把流当成文件部件发出去（JSON 根本序列化不了流）。
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
                // 部件级的 HttpContent 由调用方构造，库既拨不回它包着的流、也无从判断可 seek 与否：
                // 与根级 data 是 StreamContent 同一条规则（一律拒绝），否则第二次发送的表现是
                // BCL 抛「流已被消费」→ 被翻译成 ERR_NETWORK → 还会被当成网络错误反复重试，
                // 最终给调用方一个与真实原因（正文不可重放）毫无关系的错误码。
                if (part is StreamContent)
                {
                    throw NotReplayable(config, "multipart 部件里的 StreamContent");
                }
            });

        return streams.Count == 0 ? null : new RetryBody(streams);
    }

    /// <summary>把所有流拨回记录下来的起点（第二次及以后的重试前调用）。</summary>
    /// <exception cref="AxiosException">
    /// 流在请求期间被关闭 / 不再支持定位（<c>ERR_BAD_OPTION_VALUE</c>）：正文已无法重放，
    /// 与其发一个空正文出去，不如明确报「这次重试做不了」。
    /// </exception>
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
                // 不把裸 ObjectDisposedException / NotSupportedException 漏给调用方：
                // 库内所有失败都必须落在统一错误模型里（见 AxiosException）。
                throw AxiosException.InvalidOption(
                    "重试前无法把请求正文的流拨回起点：流可能已被关闭，或不支持定位（CanSeek）。"
                    + "开启重试要求正文可重放，请改用 byte[] / string / 业务对象，"
                    + "或确保流在整个请求（含重试）期间保持打开且可 seek。",
                    config,
                    ex);
            }
        }
    }

    private static AxiosException NotReplayable(AxiosConfig config, string what)
        => AxiosException.InvalidOption(
            $"开启重试要求请求体能重发，但当前 data 是{what}："
            + "重试时第二次发送发不出完整正文（只会发出空内容，或直接报「流已被消费」）。"
            + "请改用 byte[] / string / 业务对象 / FormFile，或先把内容读进可 seek 的 MemoryStream / FileStream；"
            + "HttpContent 形态（尤其 StreamContent）库无法回拨，请换成上述可重放形态。",
            config);
}
