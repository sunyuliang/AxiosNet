namespace AxiosNet.Internal;

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

/// <summary>
/// 正文可重放性的唯一分类入口：直接调用 <see cref="RetryBody.Prepare"/>，不再维护第二份类型名单。
/// </summary>
internal static class BodyReplayability
{
    /// <summary>四态分类；内核重试与 WebAPI 的跨调用重放都从这里取结论。</summary>
    /// <param name="config">本次请求的生效配置。</param>
    public static BodyReplayKind Classify(AxiosConfig config)
    {
        try
        {
            // 需要流回拨的形态（可 seek 的 Stream / 含流部件的 multipart）只有内核在同一次请求内能重放。
            if (RetryBody.Prepare(config) is not null)
            {
                return BodyReplayKind.Streamed;
            }

            // Prepare 返回 null 时，HttpContent 是内存缓冲内容（白名单外会在上面抛出），其余都是可重建的数据。
            return config.Body is HttpContent ? BodyReplayKind.Content : BodyReplayKind.Rebuild;
        }
        catch (AxiosException)
        {
            return BodyReplayKind.None;
        }
    }

    /// <summary>
    /// 按使用方式判定可重放性：顺序重放接受白名单内的内存内容与可回拨的流；
    /// 对冲只接受「两条分支同时读也安全」的形态——内存字节内容，或多路复用后落在内存字节内容上的数据正文。
    /// </summary>
    /// <param name="config">本次请求的生效配置。</param>
    /// <param name="mode">顺序重放或并行对冲。</param>
    public static bool CanReplay(AxiosConfig config, ReplayMode mode)
    {
        return Classify(config) switch
        {
            BodyReplayKind.None => false,
            BodyReplayKind.Streamed => false,

            // 数据正文由 <see cref="RequestBodyFactory"/> 装配成内存字节内容；multipart 除外（部件带共享可变状态）。
            // 没有正文时与 multipart 媒体类型无关。
            BodyReplayKind.Rebuild => config.Body is null
                || !MediaTypes.Is(config.ContentType, AxiosContentType.MultipartFormData),

            // 调用方自备的 HttpContent：只有内存字节内容能被两条分支同时读；JsonContent 等顺序可重放但不并行。
            BodyReplayKind.Content => config.Body is ByteArrayContent or ReadOnlyMemoryContent,

            _ => false,
        };
    }
}
