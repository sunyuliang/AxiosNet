namespace AxiosNet.Internal.Content;

/// <summary>可重放性的使用方式：顺序重试只要求「能再发一次」，对冲还要求两条分支能同时读同一份内容。</summary>
internal enum ReplayMode
{
    /// <summary>顺序重放：内核重试 / 换端点 / WebAPI 的 401 重放。</summary>
    Sequential,

    /// <summary>并行对冲：首发与延迟副本同时在途。</summary>
    Hedge,
}

/// <summary>正文形态分类：重试与跨调用重放共用同一次判定，名单只在 <see cref="RetryBody.Prepare"/> 里维护。</summary>
internal enum BodyReplayKind
{
    /// <summary>不可重放（流式请求体、不可 seek 的流、白名单外的 HttpContent 等）。</summary>
    None,

    /// <summary>由数据重新装配正文：null / <c>byte[]</c> / <c>string</c> / 业务对象 / 无流部件的 FormData。</summary>
    Rebuild,

    /// <summary>内存缓冲的 HttpContent：内核重试可直接复用；跨调用重放需要一个正文快照。</summary>
    Content,

    /// <summary>依赖流回拨（可 seek 的 Stream / 含流部件的 multipart）：只在内核同一次请求内成立。</summary>
    Streamed,
}

/// <summary>正文可重放性的唯一分类入口：结论由 <see cref="RetryBody.Prepare"/> 给出。</summary>
internal static class BodyReplayability
{
    /// <summary>四态分类；内核重试与 WebAPI 的跨调用重放都从这里取结论。</summary>
    public static BodyReplayKind Classify(AxiosConfig config)
    {
        try
        {
            // 需要流回拨的形态只有内核能在同一请求内重放。
            if (RetryBody.Prepare(config) is not null)
            {
                return BodyReplayKind.Streamed;
            }

            // 返回 null 时：HttpContent 是内存缓冲内容，其余是可重建的数据。
            return config.Body is HttpContent ? BodyReplayKind.Content : BodyReplayKind.Rebuild;
        }
        catch (AxiosException)
        {
            return BodyReplayKind.None;
        }
    }

    /// <summary>按使用方式判定可重放性：顺序重放接受白名单内的内存内容与可回拨的流；对冲只接受两条分支可同时读的形态。</summary>
    public static bool CanReplay(AxiosConfig config, ReplayMode mode)
    {
        return Classify(config) switch
        {
            BodyReplayKind.None => false,

            // 可回拨的流只能顺序重放。
            BodyReplayKind.Streamed => mode == ReplayMode.Sequential,

            // 数据正文由 <see cref="RequestBodyFactory"/> 装配成内存字节内容；multipart 除外。
            BodyReplayKind.Rebuild => config.Body is null
                || !MediaTypes.Is(config.ContentType, AxiosContentType.MultipartFormData),

            // 调用方自备的 HttpContent：对冲只接受内存字节内容。
            BodyReplayKind.Content => mode == ReplayMode.Sequential
                || config.Body is ByteArrayContent or ReadOnlyMemoryContent,

            _ => false,
        };
    }
}
